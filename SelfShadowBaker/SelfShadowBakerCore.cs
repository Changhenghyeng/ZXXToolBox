using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace SelfShadowBakerTool.Editor
{
    /// <summary>
    /// 将自阴影遮罩(R)和/或环境遮蔽(G)烘焙到模型 uv2 图集，配合 Lit_BakedOcclusion 着色器。
    /// R/G 可独立烘焙，互不覆盖。
    /// </summary>
    public class SelfShadowBakeSettings
    {
        public List<MeshRenderer> Renderers = new List<MeshRenderer>();
        // Additional world-space occluders. Only Renderers are unwrapped into this atlas.
        public List<MeshRenderer> Occluders = new List<MeshRenderer>();

        /// <summary>世界空间光照方向（太阳照向地面，Y 一般为负）。</summary>
        public Vector3 LightDirection = new Vector3(0.3f, -1f, 0.2f);

        // what to bake this run (the other channel is preserved from the existing texture)
        public bool BakeShadow = true;  // R
        public bool BakeAO = false;     // G

        public int Resolution = 1024;          // baked atlas (uv2) resolution

        // --- self-shadow (R) ---
        public int ShadowMapResolution = 2048; // light-space depth resolution
        public float PcfRadius = 1.5f;          // soft-edge radius, in shadow-map texels
        public int PcfTaps = 5;                 // 1 / 3 / 5 / 7 / 9 (NxN)
        public bool InvertOutput = false;       // false: 1 = lit ; true: 1 = shadow

        // --- ambient occlusion (G) ---
        public int AoSamples = 64;              // hemisphere directions (over the sphere)
        public int AoDepthResolution = 1024;    // per-direction depth resolution
        public float AoMaxDistance = 0f;        // world units; 0 = auto (~0.5 * bounds extents)
        public float AoLightBias = 0f;          // 0 = uniform ambient, 1 = biased toward the sun
        public float AoPower = 1.0f;            // contrast: AO = pow(AO, power)

        // --- shared anti-acne (in light-space texels) ---
        public float DepthBias = 3.0f;
        public float NormalBias = 3.0f;

        public int DilatePixels = 4;        // grow covered texels into the gutter (seam fix)
        public bool FlipV = false;          // atlas orientation escape hatch (normally off)

        public string OutputFolder = "Assets/BakedSelfShadows";
        public string AssetName = "Building";

        // --- fill / reflected light (B) ---
        // Baked visibility from a direction OPPOSITE the sun (+ manual offset). It lifts the dark
        // side so it reads as bounced/reflected light instead of dead black. The runtime shader
        // does: fillColor * saturate(dot(N, fillDir)) * B  — orientation is computed live, this
        // channel only carries "can the bounce reach this texel" (self-occlusion of the fill).
        public bool BakeFill = false;
        public Vector3 FillOffset = Vector3.zero; // added to -sun, then normalized, to aim the fill
    }

    public class SelfShadowBakeResult
    {
        public Texture2D Texture;
        public string TexturePath;
        public int MeshCount;
        public List<string> Skipped = new List<string>();
        public float ShadowedPercent; // % of covered texels shadowed (R bakes)
        public float AoPercent;       // average occlusion % (G bakes)
    }

    public static class SelfShadowBakerCore
    {
        const string BakeShaderName = "Hidden/SelfShadowBaker";
        const int PassDepth = 0, PassShadow = 1, PassAo = 2;

        public static SelfShadowBakeResult Bake(SelfShadowBakeSettings s, out string error)
        {
            error = null;
            if (s == null || s.Renderers == null || s.Renderers.Count == 0) { error = "没有可烘焙的 Renderer。"; return null; }
            if (!s.BakeShadow && !s.BakeAO && !s.BakeFill) { error = "没有选择要烘焙的通道（R 自阴影 / G AO / B 反光）。"; return null; }

            var shader = Shader.Find(BakeShaderName);
            if (shader == null) { error = $"找不到着色器 {BakeShaderName}（确认 shader 在工程里且无编译错误）。"; return null; }

            // ---- collect meshes (uv2 required) + world bounds ----
            var draws = new List<MeshDraw>();
            var skipped = new List<string>();
            Bounds wb = default; bool hasBounds = false;
            foreach (var r in s.Renderers)
            {
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) { skipped.Add($"{r.name} (无 Mesh)"); continue; }
                if (mesh.uv2 == null || mesh.uv2.Length == 0) { skipped.Add($"{r.name} (无 uv2)"); continue; }
                draws.Add(new MeshDraw { mesh = mesh, l2w = r.transform.localToWorldMatrix });
                if (!hasBounds) { wb = r.bounds; hasBounds = true; } else wb.Encapsulate(r.bounds);
            }
            if (draws.Count == 0) { error = "没有带 uv2 (lightmap UV) 的网格。被跳过: " + string.Join(", ", skipped); return null; }

            Vector3 sun = s.LightDirection.sqrMagnitude < 1e-6f ? Vector3.down : s.LightDirection.normalized;
            float halfDiag = Mathf.Max(wb.extents.magnitude, 1e-3f);
            float aoMaxDistance = s.AoMaxDistance > 1e-4f ? s.AoMaxDistance : halfDiag * 0.5f;
            var occluderDraws = new List<MeshDraw>();
            var seen = new HashSet<MeshRenderer>();
            // Depth rendering requires geometry, but does not require lightmap UVs.
            var occluders = new List<MeshRenderer>(s.Renderers);
            if (s.Occluders != null) occluders.AddRange(s.Occluders);
            foreach (var r in occluders)
            {
                if (r == null || !seen.Add(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                occluderDraws.Add(new MeshDraw { mesh = mf.sharedMesh, l2w = r.transform.localToWorldMatrix });
                wb.Encapsulate(r.bounds);
            }
            halfDiag = Mathf.Max(wb.extents.magnitude, 1e-3f);
            int n = Mathf.Max(8, s.Resolution);

            var camGO = new GameObject("~SelfShadowCam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGO.AddComponent<Camera>();
            cam.orthographic = true; cam.enabled = false; cam.allowHDR = false; cam.allowMSAA = false;
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var prevActive = RenderTexture.active;

            try
            {
                float[] shadow = null, ao = null, fill = null;
                bool[] cov = null;
                float shadowedPct = 0f, aoPct = 0f;

                if (s.BakeShadow) shadow = ComputeShadow(cam, mat, draws, occluderDraws, wb, halfDiag, sun, s, n, out cov, out shadowedPct);
                if (s.BakeAO) ao = ComputeAO(cam, mat, draws, occluderDraws, wb, halfDiag, aoMaxDistance, sun, s, n, out cov, out aoPct);
                if (s.BakeFill)
                {
                    // Fill direction = opposite the sun (+ manual offset). Bake its visibility:
                    // 1 = the bounce reaches this texel, 0 = occluded by the model's own geometry.
                    Vector3 fillDir = -sun + s.FillOffset;
                    if (fillDir.sqrMagnitude < 1e-6f) fillDir = -sun;
                    fillDir.Normalize();
                    fill = ComputeShadow(cam, mat, draws, occluderDraws, wb, halfDiag, fillDir, s, n, out cov, out _);
                }

                RenderTexture.active = prevActive;

                // ---- output path (stable, so R/G bakes share one file) ----
                string folder = string.IsNullOrEmpty(s.OutputFolder) ? "Assets/BakedSelfShadows" : s.OutputFolder.TrimEnd('/');
                EnsureFolder(folder);
                string safeName = string.IsNullOrEmpty(s.AssetName) ? "Building" : SanitizeFileName(s.AssetName);
                string pngPath = $"{folder}/{safeName}_BakedOcclusion.png";

                // ---- preserve the channel we did not bake ----
                Color[] existing = LoadExistingPixels(pngPath, n);
                var outPx = new Color[n * n];
                for (int i = 0; i < outPx.Length; i++)
                {
                    float r = existing != null ? existing[i].r : 1f;
                    float g = existing != null ? existing[i].g : 1f;
                    float b = existing != null ? existing[i].b : 1f;
                    bool covered = cov != null && cov[i];

                    if (shadow != null)
                    {
                        if (covered) { float v = Mathf.Clamp01(shadow[i]); r = s.InvertOutput ? 1f - v : v; }
                        else r = existing != null ? existing[i].r : 1f;
                    }
                    if (ao != null)
                        g = covered ? Mathf.Clamp01(ao[i]) : (existing != null ? existing[i].g : 1f);
                    if (fill != null)
                        b = covered ? Mathf.Clamp01(fill[i]) : (existing != null ? existing[i].b : 1f);

                    outPx[i] = new Color(r, g, b, 1f); // R = self-shadow, G = AO, B = fill (reflected-light visibility), A = 1
                }

                var outTex = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
                outTex.SetPixels(outPx);
                outTex.Apply(false);
                File.WriteAllBytes(pngPath, outTex.EncodeToPNG());
                Object.DestroyImmediate(outTex);

                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                ConfigureImporter(pngPath);
                var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);

                return new SelfShadowBakeResult
                {
                    Texture = asset, TexturePath = pngPath, MeshCount = draws.Count,
                    Skipped = skipped, ShadowedPercent = shadowedPct, AoPercent = aoPct
                };
            }
            finally
            {
                RenderTexture.active = prevActive;
                EditorUtility.ClearProgressBar();
                if (mat != null) Object.DestroyImmediate(mat);
                if (camGO != null) Object.DestroyImmediate(camGO);
            }
        }

        // =====================================================================
        // Self-shadow (single sun direction, PCF)
        // =====================================================================
        static float[] ComputeShadow(Camera cam, Material mat, List<MeshDraw> draws, List<MeshDraw> occluders, Bounds wb,
            float halfDiag, Vector3 sun, SelfShadowBakeSettings s, int n, out bool[] cov, out float shadowedPct)
        {
            var fit = FitOrtho(cam, wb, sun, halfDiag, s.ShadowMapResolution);
            RenderTexture depthRT = NewDepthRT(s.ShadowMapResolution);
            RenderTexture bakeRT = NewBakeRT(n);
            try
            {
                RenderDepth(mat, fit, occluders, depthRT);

                mat.SetTexture("_LightDepthTex", depthRT);
                mat.SetMatrix("_LightView", fit.view);
                mat.SetFloat("_HalfW", fit.halfW);
                mat.SetFloat("_HalfH", fit.halfH);
                mat.SetFloat("_TexelWorld", fit.texelWorld);
                mat.SetFloat("_DepthBias", Mathf.Max(0f, s.DepthBias));
                mat.SetFloat("_NormalBias", Mathf.Max(0f, s.NormalBias));
                mat.SetFloat("_PcfRadius", Mathf.Max(0f, s.PcfRadius));
                mat.SetInt("_PcfTaps", Mathf.Clamp(s.PcfTaps | 1, 1, 9));
                mat.SetFloat("_FlipV", s.FlipV ? 1f : 0f);

                var cmd = new CommandBuffer { name = "SelfShadow.Unwrap" };
                cmd.SetRenderTarget(bakeRT);
                cmd.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                DrawAll(cmd, draws, mat, PassShadow);
                Graphics.ExecuteCommandBuffer(cmd);
                cmd.Release();

                var raw = ReadBack(bakeRT, n);
                var vis = new float[raw.Length];
                cov = new bool[raw.Length];
                for (int i = 0; i < raw.Length; i++) { vis[i] = raw[i].r; cov[i] = raw[i].a > 0.5f; }

                Dilate(vis, cov, n, Mathf.Max(0, s.DilatePixels));

                double acc = 0; int c = 0;
                for (int i = 0; i < vis.Length; i++) if (cov[i]) { acc += 1f - Mathf.Clamp01(vis[i]); c++; }
                shadowedPct = c > 0 ? (float)(acc / c) * 100f : 0f;
                return vis;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(depthRT);
                RenderTexture.ReleaseTemporary(bakeRT);
            }
        }

        // =====================================================================
        // Ambient occlusion (many directions, additive accumulation)
        // =====================================================================
        static float[] ComputeAO(Camera cam, Material mat, List<MeshDraw> draws, List<MeshDraw> occluders, Bounds wb,
            float halfDiag, float maxDist, Vector3 sun, SelfShadowBakeSettings s, int n, out bool[] cov, out float aoPct)
        {
            int samples = Mathf.Clamp(s.AoSamples, 8, 512);
            Vector3 towardLight = (-sun).normalized;

            RenderTexture depthRT = NewDepthRT(s.AoDepthResolution);
            RenderTexture accumRT = NewBakeRT(n);
            try
            {
                // clear the accumulation buffer once, then add each direction's contribution
                var clr = new CommandBuffer { name = "AO.Clear" };
                clr.SetRenderTarget(accumRT);
                clr.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                Graphics.ExecuteCommandBuffer(clr);
                clr.Release();

                mat.SetFloat("_DepthBias", Mathf.Max(0f, s.DepthBias));
                mat.SetFloat("_NormalBias", Mathf.Max(0f, s.NormalBias));
                mat.SetFloat("_FlipV", s.FlipV ? 1f : 0f);
                mat.SetVector("_LightVec", towardLight);
                mat.SetFloat("_LightBias", Mathf.Clamp01(s.AoLightBias));
                mat.SetFloat("_AoMaxDist", maxDist);

                for (int k = 0; k < samples; k++)
                {
                    if ((k & 7) == 0)
                        EditorUtility.DisplayProgressBar("Self Shadow Baker", $"AO 采样 {k + 1}/{samples}", (float)k / samples);

                    Vector3 dir = FibSphere(k, samples);     // outward sample direction
                    var fit = FitOrtho(cam, wb, -dir, halfDiag, s.AoDepthResolution); // light comes from dir
                    RenderDepth(mat, fit, occluders, depthRT);

                    mat.SetTexture("_LightDepthTex", depthRT);
                    mat.SetMatrix("_LightView", fit.view);
                    mat.SetFloat("_HalfW", fit.halfW);
                    mat.SetFloat("_HalfH", fit.halfH);
                    mat.SetFloat("_TexelWorld", fit.texelWorld);
                    mat.SetVector("_AoDir", dir);

                    var cmd = new CommandBuffer { name = "AO.Accumulate" };
                    cmd.SetRenderTarget(accumRT);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    DrawAll(cmd, draws, mat, PassAo);
                    Graphics.ExecuteCommandBuffer(cmd);
                    cmd.Release();
                }

                var raw = ReadBack(accumRT, n);
                var ao = new float[raw.Length];
                cov = new bool[raw.Length];
                float power = Mathf.Max(0.05f, s.AoPower);
                for (int i = 0; i < raw.Length; i++)
                {
                    float sumW = raw[i].g;
                    float open = sumW > 1e-5f ? raw[i].r / sumW : 1f; // cosine-weighted average openness
                    ao[i] = Mathf.Pow(Mathf.Clamp01(open), power);
                    cov[i] = raw[i].a > 0.5f;
                }

                Dilate(ao, cov, n, Mathf.Max(0, s.DilatePixels));

                double acc = 0; int c = 0;
                for (int i = 0; i < ao.Length; i++) if (cov[i]) { acc += 1f - Mathf.Clamp01(ao[i]); c++; }
                aoPct = c > 0 ? (float)(acc / c) * 100f : 0f;
                return ao;
            }
            finally
            {
                RenderTexture.ReleaseTemporary(depthRT);
                RenderTexture.ReleaseTemporary(accumRT);
            }
        }

        // =====================================================================
        // Shared helpers
        // =====================================================================
        struct MeshDraw { public Mesh mesh; public Matrix4x4 l2w; }

        struct OrthoFit { public Matrix4x4 view, proj; public float halfW, halfH, texelWorld, farSentinel; }

        static OrthoFit FitOrtho(Camera cam, Bounds wb, Vector3 dir, float halfDiag, int depthRes)
        {
            Vector3 d = dir.sqrMagnitude < 1e-6f ? Vector3.down : dir.normalized;
            float camDist = halfDiag * 3f;
            cam.transform.SetPositionAndRotation(wb.center - d * camDist, Quaternion.LookRotation(d));

            Matrix4x4 view = cam.worldToCameraMatrix;
            Vector3 vMin = Vector3.positiveInfinity, vMax = Vector3.negativeInfinity;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3(
                    (c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                Vector3 v = view.MultiplyPoint3x4(corner);
                vMin = Vector3.Min(vMin, v); vMax = Vector3.Max(vMax, v);
            }
            float pad = halfDiag * 0.05f;
            float halfH = Mathf.Max(Mathf.Abs(vMin.y), Mathf.Abs(vMax.y)) + pad;
            float halfW = Mathf.Max(Mathf.Abs(vMin.x), Mathf.Abs(vMax.x)) + pad;
            cam.orthographicSize = halfH;
            cam.aspect = halfW / Mathf.Max(halfH, 1e-4f);
            cam.nearClipPlane = Mathf.Max(0.001f, -vMax.z - pad);
            cam.farClipPlane = -vMin.z + pad;

            return new OrthoFit
            {
                view = cam.worldToCameraMatrix,
                proj = cam.projectionMatrix,
                halfW = halfW, halfH = halfH,
                texelWorld = (2f * Mathf.Max(halfW, halfH)) / Mathf.Max(depthRes, 1),
                farSentinel = cam.farClipPlane + 10f
            };
        }

        static RenderTexture NewDepthRT(int res)
        {
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(
                Mathf.Max(8, res), Mathf.Max(8, res), RenderTextureFormat.RFloat, 24) { sRGB = false });
            rt.filterMode = FilterMode.Point;
            rt.wrapMode = TextureWrapMode.Clamp;
            return rt;
        }

        static RenderTexture NewBakeRT(int res)
        {
            var rt = RenderTexture.GetTemporary(new RenderTextureDescriptor(
                res, res, RenderTextureFormat.ARGBFloat, 0) { sRGB = false });
            rt.filterMode = FilterMode.Point;
            rt.wrapMode = TextureWrapMode.Clamp;
            return rt;
        }

        static void RenderDepth(Material mat, OrthoFit fit, List<MeshDraw> draws, RenderTexture depthRT)
        {
            mat.SetMatrix("_LightView", fit.view);
            var cmd = new CommandBuffer { name = "SelfShadow.Depth" };
            cmd.SetRenderTarget(depthRT);
            cmd.ClearRenderTarget(true, true, new Color(fit.farSentinel, 0, 0, 0));
            cmd.SetViewProjectionMatrices(fit.view, fit.proj);
            DrawAll(cmd, draws, mat, PassDepth);
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Release();
        }

        static void DrawAll(CommandBuffer cmd, List<MeshDraw> draws, Material mat, int pass)
        {
            foreach (var d in draws)
                for (int sm = 0; sm < d.mesh.subMeshCount; sm++)
                    cmd.DrawMesh(d.mesh, d.l2w, mat, sm, pass);
        }

        static Color[] ReadBack(RenderTexture rt, int n)
        {
            RenderTexture.active = rt;
            var t = new Texture2D(n, n, TextureFormat.RGBAFloat, false, true);
            t.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
            t.Apply(false);
            var px = t.GetPixels();
            Object.DestroyImmediate(t);
            return px;
        }

        // Fibonacci-sphere unit direction (uniform over the sphere); per-texel cosine weight
        // restricts each direction to that texel's hemisphere.
        static Vector3 FibSphere(int i, int n)
        {
            float ga = Mathf.PI * (3f - Mathf.Sqrt(5f)); // golden angle
            float y = 1f - (i + 0.5f) / n * 2f;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float t = ga * i;
            return new Vector3(Mathf.Cos(t) * r, y, Mathf.Sin(t) * r);
        }

        // Read pixels of an existing (possibly non-readable) texture via a GPU blit.
        static Color[] LoadExistingPixels(string path, int n)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null || tex.width != n || tex.height != n) return null;
            var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var tmp = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
            tmp.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
            tmp.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            var px = tmp.GetPixels();
            Object.DestroyImmediate(tmp);
            return px;
        }

        /// <summary>将被覆盖像素值向外扩张到 gutter，避免 UV 岛边缘双线性/mip 采样漏出底色造成黑边。</summary>
        static void Dilate(float[] val, bool[] cov, int n, int iterations)
        {
            if (iterations <= 0) return;
            var nv = new float[val.Length];
            var nc = new bool[cov.Length];
            for (int it = 0; it < iterations; it++)
            {
                System.Array.Copy(val, nv, val.Length);
                System.Array.Copy(cov, nc, cov.Length);
                for (int y = 0; y < n; y++)
                {
                    int row = y * n;
                    for (int x = 0; x < n; x++)
                    {
                        int idx = row + x;
                        if (cov[idx]) continue;
                        float sum = 0f; int cnt = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= n) continue;
                            int nrow = ny * n;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = x + dx;
                                if (nx < 0 || nx >= n) continue;
                                int nidx = nrow + nx;
                                if (!cov[nidx]) continue;
                                sum += val[nidx]; cnt++;
                            }
                        }
                        if (cnt > 0) { nv[idx] = sum / cnt; nc[idx] = true; }
                    }
                }
                System.Array.Copy(nv, val, val.Length);
                System.Array.Copy(nc, cov, cov.Length);
            }
        }

        static void EnsureFolder(string p)
        {
            if (AssetDatabase.IsValidFolder(p)) return;
            string parent = Path.GetDirectoryName(p)?.Replace('\\', '/');
            string leaf = Path.GetFileName(p);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        internal static string SanitizeFileName(string name)
        {
            foreach (var ch in Path.GetInvalidFileNameChars())
                name = name.Replace(ch, '_');
            return name;
        }

        static void ConfigureImporter(string p)
        {
            var imp = AssetImporter.GetAtPath(p) as TextureImporter;
            if (imp == null) return;
            imp.textureType = TextureImporterType.Default;
            imp.sRGBTexture = false; // linear data (R = shadow, G = AO)
            imp.alphaSource = TextureImporterAlphaSource.None;
            imp.mipmapEnabled = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.filterMode = FilterMode.Bilinear;
            imp.SaveAndReimport();
        }
    }
}
