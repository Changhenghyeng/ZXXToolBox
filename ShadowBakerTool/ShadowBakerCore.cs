using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace ShadowBakerTool.Editor
{
    public enum SoftnessMode
    {
        DistanceField = 0,  // Distance-from-edge falloff. Crisp at the building, fades outward.
        Gaussian = 1,  // Plain uniform Gaussian blur. Simpler look.
    }

    /// <summary>
    /// Parameters describing one shadow bake. Built from the editor UI.
    /// </summary>
    public class BakeSettings
    {
        // Source
        public List<MeshRenderer> Renderers = new List<MeshRenderer>();
        // Light direction in WORLD space (points the way light travels, from sun toward ground).
        public Vector3 LightDirection = new Vector3(0.3f, -1f, 0.2f).normalized;
        // Light intensity & color → modulate the eventual baked shadow darkness.
        public float LightIntensity = 1.0f;
        public Color LightColor = Color.white;
        // Resolution (base resolution, will scale to match aspect ratio).
        public int Resolution = 512;
        // Softness — based on a distance field from the building's silhouette edge.
        public SoftnessMode Softness = SoftnessMode.DistanceField;
        public float FalloffPx = 32f;    // how many pixels the soft halo extends
        public float MaxAlpha = 0.85f;  // darkness right at the building (contact)
        public float MinAlpha = 0.0f;   // darkness at the far edge of the halo
        public float FalloffPow = 1.5f;   // curve shape (>1 = solid core, <1 = soft core)
        public int Samples = 32;     // radial samples per pixel for distance estimate
        // Padding around the building's footprint (FIXED WORLD UNITS, not percentage)
        public float FootprintPadding = 0.2f;
        // Whether to skip semi-transparent materials in the shadow silhouette
        public bool SkipTransparentMaterials;
        // Gradient overlay on the output shadow texture
        public bool EnableGradient = false;
        public float GradientAngle = 0f;     // degrees, 0=top→bottom, 90=left→right
        public float GradientPosition = 0.5f; // 0-1, center of the gradient transition zone
        public float GradientWidth = 1f;     // 0-1, width of the transition zone
        public Color GradientColor = new Color(1, 1, 1, 1); // target color at the far end of the gradient
        // Output
        public string OutputFolder = "Assets/BakedShadows";
        public string AssetName = "Building";
    }

    /// <summary>
    /// Information needed by the mesh-generation step after a bake.
    /// </summary>
    public class BakeResult
    {
        public Texture2D Texture;       // The saved & imported PNG asset
        public string TexturePath;   // Asset path of the PNG
        public Bounds FootprintWS;   // World-space XZ bounds (Y is the projection plane Y)
        public Vector2 PaddedSizeXZ;  // World units, including padding — matches the texture extents
        public Vector3 FootprintCenterWS;
    }

    public static class ShadowBakerCore
    {
        // Shader names — must match the .shader files in Runtime/Shaders.
        private const string SilhouetteShaderName = "Hidden/ShadowBaker/Silhouette";
        private const string BlurShaderName = "Hidden/ShadowBaker/Blur";

        /// <summary>
        /// Perform a bake. Returns null on failure (and logs the reason).
        /// </summary>
        public static BakeResult Bake(BakeSettings s)
        {
            if (s == null || s.Renderers == null || s.Renderers.Count == 0)
            {
                Debug.LogError("[ShadowBaker] No renderers specified.");
                return null;
            }

            var silhouetteShader = Shader.Find(SilhouetteShaderName);
            var blurShader = Shader.Find(BlurShaderName);
            if (silhouetteShader == null || blurShader == null)
            {
                Debug.LogError("[ShadowBaker] Required shaders not found. Make sure the Runtime/Shaders folder is in the project.");
                return null;
            }

            // 1. Compute the building's world-space bounds (only from the selected renderers).
            Bounds wsBounds = ComputeWorldBounds(s.Renderers);
            if (wsBounds.size == Vector3.zero)
            {
                Debug.LogError("[ShadowBaker] Renderers have zero bounds.");
                return null;
            }

            // 2. The ground plane is at the building's lowest Y.
            float groundY = wsBounds.min.y;

            // 3. Compute where the building's bounding box projects to on the ground when
            //    sheared along the light direction. For each of the 8 corners of the bounding
            //    box, solve: corner + t*lightDir lands on Y=groundY.
            //    The result is an 8-point projected polygon — we just need its XZ AABB.
            Vector3 lightDir = s.LightDirection;
            if (lightDir.sqrMagnitude < 1e-6f) lightDir = new Vector3(0, -1, 0);
            lightDir.Normalize();

            // Light must point downward; if user passes an upward vector by mistake, flip it.
            if (lightDir.y > -0.05f)
            {
                Debug.LogWarning("[ShadowBaker] Light direction Y is not negative; flipping or clamping. Make sure the sun points downward.");
                if (lightDir.y > 0f) lightDir = -lightDir;
                // Clamp so we never divide by ~0 (shadow at horizon goes to infinity).
                if (lightDir.y > -0.1f)
                {
                    lightDir.y = -0.1f;
                    lightDir.Normalize();
                }
            }

            // Project each mesh vertex onto the ground (NOT just the 8 AABB corners).
            //
            // Why: an AABB-corner projection assumes the model fills its bounding box,
            // which is rarely true. For elongated/diagonal models (e.g. a catapult with
            // a long arm, a leaning pole, a thin rooftop antenna), the AABB is much
            // larger than the actual geometry, and projecting just the 8 corners gives
            // a "shadow AABB" that is wildly larger than the real shadow. The decal
            // quad ends up oversized and visually mismatched — building looks small in
            // the middle of a big empty padding region.
            //
            // Iterating real vertices gives a tight footprint that hugs the actual
            // shadow shape. Cost: O(vertex count) per bake; trivial for an editor tool.
            float shadowMinX = float.PositiveInfinity, shadowMaxX = float.NegativeInfinity;
            float shadowMinZ = float.PositiveInfinity, shadowMaxZ = float.NegativeInfinity;
            int totalVertsProjected = 0;
            var perRendererLog = new System.Text.StringBuilder();
            foreach (var r in s.Renderers)
            {
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                var verts = mesh.vertices;            // object-space
                var l2w = r.transform.localToWorldMatrix;

                // Per-renderer diagnostic snapshot
                float meshMinX = float.PositiveInfinity, meshMaxX = float.NegativeInfinity;
                float meshMinY = float.PositiveInfinity, meshMaxY = float.NegativeInfinity;
                float meshMinZ = float.PositiveInfinity, meshMaxZ = float.NegativeInfinity;

                for (int i = 0; i < verts.Length; i++)
                {
                    Vector3 c = l2w.MultiplyPoint3x4(verts[i]);   // world-space vertex
                    if (c.x < meshMinX) meshMinX = c.x;
                    if (c.x > meshMaxX) meshMaxX = c.x;
                    if (c.y < meshMinY) meshMinY = c.y;
                    if (c.y > meshMaxY) meshMaxY = c.y;
                    if (c.z < meshMinZ) meshMinZ = c.z;
                    if (c.z > meshMaxZ) meshMaxZ = c.z;

                    float t = (groundY - c.y) / lightDir.y;       // lightDir.y guaranteed < 0
                    Vector3 p = c + lightDir * t;
                    if (p.x < shadowMinX) shadowMinX = p.x;
                    if (p.x > shadowMaxX) shadowMaxX = p.x;
                    if (p.z < shadowMinZ) shadowMinZ = p.z;
                    if (p.z > shadowMaxZ) shadowMaxZ = p.z;
                }
                totalVertsProjected += verts.Length;

                perRendererLog.AppendLine(
                    $"  Renderer '{r.name}': verts={verts.Length}\n" +
                    $"    transform.localScale = {r.transform.localScale}\n" +
                    $"    transform.lossyScale = {r.transform.lossyScale}\n" +
                    $"    r.bounds (worldspace AABB) = center {r.bounds.center} size {r.bounds.size}\n" +
                    $"    mesh.bounds (localspace AABB) = center {mesh.bounds.center} size {mesh.bounds.size}\n" +
                    $"    Computed world-vert AABB: X[{meshMinX},{meshMaxX}] Y[{meshMinY},{meshMaxY}] Z[{meshMinZ},{meshMaxZ}]\n" +
                    $"    Computed world-vert size: ({meshMaxX-meshMinX}, {meshMaxY-meshMinY}, {meshMaxZ-meshMinZ})");
            }
            if (totalVertsProjected == 0)
            {
                Debug.LogError("[ShadowBaker] No projectable vertices found on any renderer.");
                return null;
            }

            // 4. Adaptive padded viewport (no more forced square!)
            float shadowSizeX = shadowMaxX - shadowMinX;
            float shadowSizeZ = shadowMaxZ - shadowMinZ;
            // Fixed world-unit padding (replaces percentage padding, avoids over-padding)
            float paddedSizeX = shadowSizeX + s.FootprintPadding * 2f;
            float paddedSizeZ = shadowSizeZ + s.FootprintPadding * 2f;

            // === DEBUG: print everything that determines decal size ===
            Debug.Log(
                $"[ShadowBaker DEBUG]\n" +
                $"  wsBounds.min  = {wsBounds.min}\n" +
                $"  wsBounds.max  = {wsBounds.max}\n" +
                $"  wsBounds.size = {wsBounds.size}\n" +
                $"  lightDir      = {lightDir}\n" +
                $"  groundY       = {groundY}\n" +
                $"  shadow X      = [{shadowMinX}, {shadowMaxX}]  size={shadowSizeX}\n" +
                $"  shadow Z      = [{shadowMinZ}, {shadowMaxZ}]  size={shadowSizeZ}\n" +
                $"  padding       = {s.FootprintPadding}\n" +
                $"  paddedSize    = ({paddedSizeX}, {paddedSizeZ})\n" +
                $"  vertsProjected = {totalVertsProjected}\n" +
                $"  Renderers.Count = {s.Renderers.Count}\n" +
                $"--- PER-RENDERER ---\n{perRendererLog}");

            // Center the viewport on the shadow's XZ center (not the building's center)
            Vector3 centerXZ = new Vector3(
                (shadowMinX + shadowMaxX) * 0.5f,
                groundY,
                (shadowMinZ + shadowMaxZ) * 0.5f);

            // 5. Set up the temporary camera high above the building, looking straight down.
            var camGO = new GameObject("~ShadowBakerCam") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var cam = camGO.AddComponent<Camera>();
                float camHeight = wsBounds.size.y + 10f;
                camGO.transform.position = centerXZ + Vector3.up * (camHeight + 1f);
                camGO.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                cam.orthographic = true;

                // The camera in our top-down setup is rotated so its local UP axis points
                // along world +Z. So orthographicSize (the half-HEIGHT of the view) maps to
                // half of paddedSizeZ, and the view WIDTH maps to paddedSizeX.
                // We DO NOT use cam.rect — it only crops the output viewport on the RT, it
                // does NOT shrink the world frustum, so using it produces a squashed image.
                // Instead, we make the RT itself rectangular and the projection matrix
                // rectangular, so world ↔ RT pixels stay 1:1 in both axes.
                cam.orthographicSize = paddedSizeZ * 0.5f;

                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = camHeight + wsBounds.size.y + 5f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.enabled = false;
                cam.cullingMask = 0;

                // 6. Rectangular RenderTexture: long edge = Resolution, short edge follows
                //    the world aspect ratio exactly. No power-of-two snap — modern Unity /
                //    URP handle NPOT RTs fine, and snapping would break the aspect ratio
                //    we just carefully matched to the world.
                float aspectRatio = Mathf.Max(paddedSizeX, 0.01f) / Mathf.Max(paddedSizeZ, 0.01f);
                int rtWidth, rtHeight;
                if (aspectRatio >= 1f)
                {
                    rtWidth = s.Resolution;
                    rtHeight = Mathf.Max(1, Mathf.RoundToInt(s.Resolution / aspectRatio));
                }
                else
                {
                    rtWidth = Mathf.Max(1, Mathf.RoundToInt(s.Resolution * aspectRatio));
                    rtHeight = s.Resolution;
                }

                var descriptor = new RenderTextureDescriptor(rtWidth, rtHeight, RenderTextureFormat.ARGB32, 24)
                {
                    sRGB = false,
                    useMipMap = false,
                    autoGenerateMips = false
                };

                var rtRaw = RenderTexture.GetTemporary(descriptor);
                rtRaw.filterMode = FilterMode.Bilinear;
                rtRaw.wrapMode = TextureWrapMode.Clamp;
                cam.targetTexture = rtRaw;

                // 7. Render silhouette via CommandBuffer
                var silhouetteMat = new Material(silhouetteShader) { hideFlags = HideFlags.HideAndDontSave };
                silhouetteMat.SetVector("_LightDir", new Vector4(lightDir.x, lightDir.y, lightDir.z, 0f));
                silhouetteMat.SetFloat("_GroundY", groundY);

                Matrix4x4 viewMat = cam.worldToCameraMatrix;
                // Rectangular ortho projection — must match the rectangular RT and the
                // rectangular world footprint. Using orthographicSize for both axes (as
                // before) produced a square frustum and squashed the long axis.
                float halfX = paddedSizeX * 0.5f;
                float halfZ = paddedSizeZ * 0.5f;
                Matrix4x4 projMat = Matrix4x4.Ortho(
                    -halfX, halfX,
                    -halfZ, halfZ,
                    cam.nearClipPlane, cam.farClipPlane);
                projMat = GL.GetGPUProjectionMatrix(projMat, renderIntoTexture: true);

                var cmd = new CommandBuffer { name = "ShadowBaker.Silhouette" };
                cmd.SetRenderTarget(rtRaw);
                cmd.ClearRenderTarget(clearDepth: true, clearColor: true, backgroundColor: Color.black);
                cmd.SetViewProjectionMatrices(viewMat, projMat);

                int meshCount = 0;
                foreach (var r in s.Renderers)
                {
                    if (r == null) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mesh = mf.sharedMesh;
                    var mats = r.sharedMaterials;
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    {
                        if (s.SkipTransparentMaterials && sm < mats.Length && mats[sm] != null)
                        {
                            var subMat = mats[sm];
                            // Only treat the submesh as transparent when the material actually
                            // exposes these properties. Calling GetColor/GetFloat on a shader that
                            // lacks them (e.g. Custom/BakedLightMap has no _BaseColor) logs a Unity
                            // error AND returns 0 — which would wrongly skip OPAQUE geometry and
                            // leave the building out of the silhouette (blank/white shadow).
                            if (subMat.HasProperty("_BaseColor") && subMat.GetColor("_BaseColor").a < 0.99f)
                                continue;
                            if (subMat.HasProperty("_Surface") && subMat.GetFloat("_Surface") > 0.5f)
                                continue;
                        }
                        cmd.DrawMesh(mesh, r.transform.localToWorldMatrix, silhouetteMat, sm, 0);
                    }
                    meshCount++;
                }

                if (meshCount == 0)
                {
                    Debug.LogWarning("[ShadowBaker] No drawable meshes found.");
                }

                try
                {
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                finally
                {
                    cmd.Release();
                    Object.DestroyImmediate(silhouetteMat);
                }

                // 8. Apply softness post-process
                var rtFinal = ApplySoftness(rtRaw, s, blurShader);

                // 9. Read final RT into a Texture2D
                Texture2D tex = ReadRT(rtFinal, s);

                // 10. Save as PNG asset
                string fullFolder = s.OutputFolder.TrimEnd('/');
                EnsureFolderExists(fullFolder);
                string fileName = string.IsNullOrEmpty(s.AssetName) ? "Shadow" : s.AssetName;
                string pngPath = AssetDatabase.GenerateUniqueAssetPath($"{fullFolder}/{fileName}_Shadow.png");
                File.WriteAllBytes(pngPath, tex.EncodeToPNG());

                // Cleanup
                Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(rtRaw);
                RenderTexture.ReleaseTemporary(rtFinal);

                AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
                ConfigureImporter(pngPath);
                var importedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);

                return new BakeResult
                {
                    Texture = importedTex,
                    TexturePath = pngPath,
                    FootprintWS = wsBounds,
                    PaddedSizeXZ = new Vector2(paddedSizeX, paddedSizeZ),
                    FootprintCenterWS = centerXZ
                };
            }
            finally
            {
                Object.DestroyImmediate(camGO);
            }
        }

        private static RenderTexture ApplySoftness(RenderTexture src, BakeSettings s, Shader blurShader)
        {
            var mat = new Material(blurShader) { hideFlags = HideFlags.HideAndDontSave };
            var descriptor = src.descriptor;

            mat.SetFloat("_FalloffPx", s.FalloffPx);
            mat.SetFloat("_MinAlpha", s.MinAlpha);
            mat.SetFloat("_MaxAlpha", s.MaxAlpha);
            mat.SetFloat("_FalloffPow", s.FalloffPow);
            mat.SetFloat("_Samples", s.Samples);

            var rtFinal = RenderTexture.GetTemporary(descriptor);
            rtFinal.filterMode = FilterMode.Bilinear;
            rtFinal.wrapMode = TextureWrapMode.Clamp;

            int passIdx = (s.Softness == SoftnessMode.Gaussian) ? 1 : 0;
            Graphics.Blit(src, rtFinal, mat, passIdx);

            Object.DestroyImmediate(mat);
            return rtFinal;
        }

        /// <summary>
        /// Read the RT to a Texture2D, applying light color tint and intensity.
        /// Performs a VERTICAL FLIP here so the output PNG matches world orientation.
        /// </summary>
        private static Texture2D ReadRT(RenderTexture rt, BakeSettings s)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = prev;

            float intensityFactor = Mathf.Clamp01(s.LightIntensity);
            Color[] pixels = tex.GetPixels();
            Color[] flippedPixels = new Color[pixels.Length];
            int w = tex.width;
            int h = tex.height;

            for (int y = 0; y < h; y++)
            {
                int srcRowStart = y * w;
                int dstRowStart = (h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                {
                    Color p = pixels[srcRowStart + x];
                    float mask = (1f - p.r) * intensityFactor;
                    float v = 1f - mask;
                    flippedPixels[dstRowStart + x] = new Color(v, v, v, 1f);
                }
            }

            // Apply gradient overlay if enabled
            if (s.EnableGradient)
            {
                float angleRad = s.GradientAngle * Mathf.Deg2Rad;
                float dirU = Mathf.Sin(angleRad);
                float dirV = Mathf.Cos(angleRad);

                // Compute the projection range of the [0,1]×[0,1] UV square onto this direction
                float p1 = 0f, p2 = dirU, p3 = dirV, p4 = dirU + dirV;
                float minProj = Mathf.Min(Mathf.Min(p1, p2), Mathf.Min(p3, p4));
                float maxProj = Mathf.Max(Mathf.Max(p1, p2), Mathf.Max(p3, p4));
                float projRange = maxProj - minProj;

                // Map Position/Width to a [0,1] blend range
                float halfW = Mathf.Clamp01(s.GradientWidth) * 0.5f;
                float center = Mathf.Clamp01(s.GradientPosition);
                float blendStart = Mathf.Max(0f, center - halfW);
                float blendEnd = Mathf.Min(1f, center + halfW);
                float blendRange = blendEnd - blendStart;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float u = (float)x / Mathf.Max(w - 1, 1);
                        float v = (float)y / Mathf.Max(h - 1, 1);
                        float proj = u * dirU + v * dirV;
                        float rawT = projRange > 0.0001f ? (proj - minProj) / projRange : 0f;

                        // Remap: rawT in [blendStart, blendEnd] → factor in [0, 1]
                        float t;
                        if (rawT <= blendStart)
                            t = 0f;
                        else if (rawT >= blendEnd)
                            t = 1f;
                        else
                            t = blendRange > 0.0001f ? (rawT - blendStart) / blendRange : 0f;

                        int idx = y * w + x;
                        float strength = s.GradientColor.a;
                        float blend = t * strength;
                        flippedPixels[idx] = Color.Lerp(flippedPixels[idx], s.GradientColor, blend);
                        flippedPixels[idx].a = 1f; // keep texture opaque, alpha drives blend strength
                    }
                }
            }

            tex.SetPixels(flippedPixels);
            tex.Apply();
            return tex;
        }

        private static Bounds ComputeWorldBounds(List<MeshRenderer> renderers)
        {
            bool initialized = false;
            Bounds b = new Bounds();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!initialized) { b = r.bounds; initialized = true; }
                else { b.Encapsulate(r.bounds); }
            }
            return b;
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(parent)) return;
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolderExists(parent);
            }
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void ConfigureImporter(string pngPath)
        {
            var importer = AssetImporter.GetAtPath(pngPath) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }
    }
}