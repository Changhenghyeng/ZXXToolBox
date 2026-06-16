// ParticleSequenceRender.cs
// URP 粒子特效序列帧截图 —— 3-pass 共享克隆相机
// 放到 Editor 文件夹下
//
// 用法：
//   由 TransparentScreenshot.cs 面板调用，不直接使用。
//
// 思路：
//   克隆用户指定相机 → 统一跑 3 pass：
//     PASS A: 黑底 关后处理 → 用于算 alpha（避免 Bloom 辉光污染）
//     PASS B: 白底 关后处理 → 用于算 alpha
//     PASS C: 透明底 开后处理 → 拿带辉光/色调映射的正确 RGB
//   合成:  final.RGB   = PassC.RGB
//          final.Alpha = 1 - mean(PassB - PassA)

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2019_3_OR_NEWER
using UnityEngine.Rendering.Universal;
#endif

public static class ParticleSequenceRender
{
    // ---- 粒子种子状态备份 ----
    public class ParticleState
    {
        public ParticleSystem system;
        public bool autoRandomSeed;
        public uint randomSeed;
    }

    // ---- 扫描信息（供外部 UI 展示） ----
    public class ScanInfo
    {
        public int systemCount;
        public string[] names;        // 简称（相对路径）
        public float[] durations;
        public bool[] isLooping;
        public float maxDuration;
        public float minDuration;
        public bool allLooping;
    }

    // ================================================================
    //  Camera 管理
    // ================================================================

    /// <summary>克隆相机：复制完整 GameObject，移除 AudioListener，禁用并隐藏。</summary>
    private static Camera CloneCamera(Camera source)
    {
        if (source == null)
        {
            Debug.LogError("[ParticleSequence] CloneCamera: source is null");
            return null;
        }
        var go = Object.Instantiate(source.gameObject);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.name = "[ParticleSequence Clone]";

        var al = go.GetComponent<AudioListener>();
        if (al != null) Object.DestroyImmediate(al);

        var cam = go.GetComponent<Camera>();
        cam.enabled = false;
        return cam;
    }

    /// <summary>销毁克隆相机及其 GameObject。</summary>
    private static void DestroyClone(Camera clone)
    {
        if (clone != null && clone.gameObject != null)
            Object.DestroyImmediate(clone.gameObject);
    }

    // ================================================================
    //  ParticleSystem 管理
    // ================================================================

    /// <summary>收集 GameObject 及其所有子节点下的 ParticleSystem（包括未激活的）。</summary>
    public static ParticleSystem[] GetAllSystems(GameObject root)
    {
        if (root == null) return new ParticleSystem[0];
        return root.GetComponentsInChildren<ParticleSystem>(true);
    }

    /// <summary>固定所有粒子系统的随机种子，返回备份状态。
    /// 种子基于每个 PS 的 instance ID，保证同一次会话中多次调用结果一致。</summary>
    public static List<ParticleState> FixRandomSeeds(ParticleSystem[] systems)
    {
        var states = new List<ParticleState>(systems.Length);
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            states.Add(new ParticleState
            {
                system = ps,
                autoRandomSeed = ps.useAutoRandomSeed,
                randomSeed = ps.randomSeed
            });
            ps.useAutoRandomSeed = false;
            uint seed = unchecked((uint)ps.GetInstanceID());
            if (seed == 0) seed = 42u;
            ps.randomSeed = seed;
        }
        return states;
    }

    /// <summary>恢复粒子系统的随机种子设置。</summary>
    public static void RestoreRandomSeeds(List<ParticleState> states)
    {
        if (states == null) return;
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (s.system == null) continue;
            s.system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            s.system.useAutoRandomSeed = s.autoRandomSeed;
            s.system.randomSeed = s.randomSeed;
        }
    }

    /// <summary>将所有粒子系统推进到指定时刻 t（含 Stop + Simulate + Pause）。
    /// withChildren=false：因为我们已经手动收集了全部 PS，各自独立控制。</summary>
    public static void SimulateAll(ParticleSystem[] systems, float t)
    {
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Simulate(t, false, true);
            ps.Pause(false);
        }
    }

    /// <summary>清除所有粒子系统（Stop + Clear），恢复静止状态。</summary>
    public static void ClearAll(ParticleSystem[] systems)
    {
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    // ================================================================
    //  扫描信息（供 UI 辅助用户判断时长）
    // ================================================================

    public static ScanInfo BuildScanInfo(GameObject root)
    {
        var info = new ScanInfo();
        if (root == null)
        {
            info.systemCount = 0;
            return info;
        }

        var systems = GetAllSystems(root);
        info.systemCount = systems.Length;
        info.names = new string[info.systemCount];
        info.durations = new float[info.systemCount];
        info.isLooping = new bool[info.systemCount];
        info.maxDuration = 0f;
        info.minDuration = float.MaxValue;
        info.allLooping = true;

        string rootPath = GetGameObjectPath(root);

        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            float dur = ps.main.duration;
            bool loop = ps.main.loop;

            // 显示相对路径
            string fullPath = GetGameObjectPath(ps.gameObject);
            string relPath;
            if (fullPath != null && rootPath != null
                && fullPath.StartsWith(rootPath + "/"))
                relPath = fullPath.Substring(rootPath.Length + 1);
            else if (ps.gameObject == root)
                relPath = root.name;
            else
                relPath = fullPath ?? ps.gameObject.name;

            info.names[i] = relPath;
            info.durations[i] = dur;
            info.isLooping[i] = loop;
            if (dur > info.maxDuration) info.maxDuration = dur;
            if (dur < info.minDuration) info.minDuration = dur;
            if (!loop) info.allLooping = false;
        }

        if (info.systemCount == 0)
        {
            info.maxDuration = 0f;
            info.minDuration = 0f;
        }
        if (info.minDuration > info.maxDuration)
            info.minDuration = info.maxDuration;

        return info;
    }

    // ================================================================
    //  3-Pass 渲染：单帧
    // ================================================================

    /// <summary>渲染一帧（已含 supersize 下采样），返回带 alpha 的线性 Texture2D。</summary>
    /// <param name="occluderRenderers">遮挡物 Renderer（可为空）</param>
    /// <param name="particleRenderers">粒子目标下所有 Renderer（可为空）</param>
    /// <param name="rgbTex">预抓取的 RGB（sRGB 空间，含后处理）。来自 TransparentScreenshot.CaptureRGB(原始相机)。</param>
    private static Texture2D RenderFrame(Camera cloneCam, int outW, int outH,
        int superSize, Texture2D rgbTex,
        Renderer[] occluderRenderers, Renderer[] particleRenderers)
    {
        int rw = outW * superSize;
        int rh = outH * superSize;

        // rgbTex 是原始相机 + CaptureRGB 抓的 sRGB，转为 linear 用于合成
        CapturePipeline.SrgbToLinear(rgbTex);

        int msaaSamples = cloneCam.allowMSAA
            ? Mathf.Max(1, QualitySettings.antiAliasing)
            : 1;
        var rtFormat = cloneCam.allowHDR
            ? RenderTextureFormat.ARGBHalf
            : RenderTextureFormat.ARGB32;

        var rt = RenderTexture.GetTemporary(rw, rh, 24, rtFormat,
            RenderTextureReadWrite.Linear, msaaSamples);
        Texture2D finalTex = null;

        bool hasOcclusion = occluderRenderers != null
            && occluderRenderers.Length > 0
            && particleRenderers != null
            && particleRenderers.Length > 0;

        try
        {
            if (hasOcclusion) SetRenderersEnabled(occluderRenderers, false);

            // Pass A: 黑底，关 PP（Alpha 计算用）
            CapturePipeline.SetPostProcessing(cloneCam, false);
            cloneCam.clearFlags = CameraClearFlags.SolidColor;
            cloneCam.backgroundColor = Color.black;
            cloneCam.targetTexture = rt;
            cloneCam.Render();
            var blackTex = CapturePipeline.ReadRTToTexture2D(rt, rw, rh);

            // Pass B: 白底，关 PP（Alpha 计算用）
            cloneCam.backgroundColor = Color.white;
            cloneCam.Render();
            var whiteTex = CapturePipeline.ReadRTToTexture2D(rt, rw, rh);

            // RGB 来自外部 CaptureRGB(原始相机)，已是 linear，直接合成
            finalTex = CapturePipeline.DualBackgroundAlpha(rgbTex, blackTex, whiteTex);

            // --- Pass D / E：遮挡物颜色对比 ---
            if (hasOcclusion)
            {
                CapturePipeline.SetPostProcessing(cloneCam, false);
                cloneCam.backgroundColor = Color.clear;

                SetRenderersEnabled(occluderRenderers, true);
                SetRenderersEnabled(particleRenderers, false);
                cloneCam.Render();
                var occluderTex = CapturePipeline.ReadRTToTexture2D(rt, rw, rh);

                SetRenderersEnabled(particleRenderers, true);
                cloneCam.Render();
                var combinedTex = CapturePipeline.ReadRTToTexture2D(rt, rw, rh);

                ApplyOcclusionMask(finalTex, occluderTex, combinedTex);

                Object.DestroyImmediate(occluderTex);
                Object.DestroyImmediate(combinedTex);
                SetRenderersEnabled(occluderRenderers, false);
                SetRenderersEnabled(particleRenderers, true);
            }

            Object.DestroyImmediate(blackTex);
            Object.DestroyImmediate(whiteTex);
            Object.DestroyImmediate(rgbTex);

            // --- 下采样 ---
            if (superSize > 1)
            {
                var resized = CapturePipeline.Downsample(finalTex, outW, outH);
                Object.DestroyImmediate(finalTex);
                finalTex = resized;
            }

            // Linear → Gamma
            CapturePipeline.LinearToGamma(finalTex);
            return finalTex;
        }
        finally
        {
            cloneCam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            if (hasOcclusion)
            {
                SetRenderersEnabled(occluderRenderers, true);
                SetRenderersEnabled(particleRenderers, true);
            }
        }
    }

    /// <summary>从 RenderTexture 读取像素到 Texture2D（线性空间）。</summary>

    /// <summary>
    /// 合成：RGB 来自 rgbTex（透明底+PP），Alpha 来自 black/white（双背景算法）。
    /// 额外对 Bloom 区域做 alpha 补偿：PP 辉光扩散到粒子外的区域，
    /// 在 black/white 对比中 alpha=0 但在 rgbTex 中有可见辉光颜色，
    /// 用 rgbTex 亮度推导 alpha，使辉光扩散区半透明可见。
    /// </summary>

    /// <summary>颜色对比判断前后 + 软过渡。</summary>
    private static void ApplyOcclusionMask(Texture2D finalTex,
        Texture2D occluderTex, Texture2D combinedTex)
    {
        int w = finalTex.width, h = finalTex.height;
        if (occluderTex.width != w || occluderTex.height != h
            || combinedTex.width != w || combinedTex.height != h) return;

        var finalPx = finalTex.GetPixels32();
        var occPx = occluderTex.GetPixels32();
        var combPx = combinedTex.GetPixels32();

        const int DIFF_HIDE = 20;
        const int DIFF_SHOW = 50;

        for (int i = 0; i < finalPx.Length; i++)
        {
            float occW = occPx[i].a / 255f;
            if (occW < 0.02f) continue;

            int dr = Mathf.Abs(combPx[i].r - occPx[i].r);
            int dg = Mathf.Abs(combPx[i].g - occPx[i].g);
            int db = Mathf.Abs(combPx[i].b - occPx[i].b);
            int diff = dr + dg + db;

            float hide;
            if (diff <= DIFF_HIDE)      hide = 1f;
            else if (diff >= DIFF_SHOW) hide = 0f;
            else                        hide = 1f - (diff - DIFF_HIDE) / (float)(DIFF_SHOW - DIFF_HIDE);

            float vis = 1f - (occW * hide);
            finalPx[i].a = (byte)(finalPx[i].a * vis);
        }

        finalTex.SetPixels32(finalPx);
        finalTex.Apply();
    }

    /// <summary>批量开关 Renderer。</summary>
    private static void SetRenderersEnabled(Renderer[] renderers, bool enabled)
    {
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = enabled;
    }

    /// <summary>开关后处理。</summary>

    /// <summary>线性 → sRGB Gamma 校正。</summary>

    /// <summary>双线性下采样。</summary>

    // ================================================================
    //  序列帧主入口
    // ================================================================

    /// <summary>执行粒子序列帧截图，返回是否成功。</summary>
    /// <param name="occluderRoots">遮挡物根节点列表（可为空），用于深度遮挡判断</param>
    public static bool CaptureParticleSheet(
        Camera sourceCam,          // 要克隆的相机（仅用于 Alpha/遮挡计算）
        GameObject particleTarget, // 粒子特效根节点
        float duration,            // 总时长（秒）
        int frameCount,            // 帧数
        int columns,               // Sheet 列数
        int outW, int outH,        // 单帧输出分辨率
        int superSize,             // 超采样倍数
        bool useEndpoints,         // true=含端点采样, false=中心采样
        bool exportIndividual,     // 是否导出单帧
        string outputFolderAbs,    // 绝对路径输出目录
        string sheetFileName,      // Sheet 文件名（不含路径）
        string framesDirName,      // 单帧子目录名（可为 null）
        List<GameObject> occluderRoots)   // 遮挡物根节点（可为 null / 空）
    {
        if (sourceCam == null || particleTarget == null)
        {
            Debug.LogError("[ParticleSequence] Camera or particle target is null.");
            return false;
        }

        var allPS = GetAllSystems(particleTarget);
        if (allPS.Length == 0)
        {
            Debug.LogError("[ParticleSequence] No ParticleSystem found on target.");
            return false;
        }

        int cols = Mathf.Max(1, columns);
        int rows = Mathf.CeilToInt(frameCount / (float)cols);

        // GPU 纹理尺寸检查
        long sheetPixels = (long)cols * outW * rows * outH;
        if (sheetPixels > 16384L * 16384L)
        {
            Debug.LogError("[ParticleSequence] Sheet size too large, abort.");
            return false;
        }

        Directory.CreateDirectory(outputFolderAbs);

        string framesDir = null;
        if (exportIndividual && !string.IsNullOrEmpty(framesDirName))
        {
            framesDir = Path.Combine(outputFolderAbs, framesDirName).Replace('\\', '/');
            Directory.CreateDirectory(framesDir);
        }

        // 克隆相机
        Camera cloneCam = null;
        List<ParticleState> savedStates = null;
        bool started = false;
        bool canceled = false;
        string sheetPath = null;

        // Sheet RT
        bool gpuCopy = (SystemInfo.copyTextureSupport & CopyTextureSupport.TextureToRT) != 0;
        var sheetRT = RenderTexture.GetTemporary(cols * outW, rows * outH, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 1);
        sheetRT.filterMode = FilterMode.Bilinear;
        {
            var prevActive = RenderTexture.active;
            RenderTexture.active = sheetRT;
            GL.Clear(true, true, new Color(0, 0, 0, 0));
            RenderTexture.active = prevActive;
        }

        Texture2D cpuSheet = null;
        if (!gpuCopy)
        {
            cpuSheet = new Texture2D(cols * outW, rows * outH,
                TextureFormat.RGBA32, false, false);
            var clear = new Color32[cpuSheet.width * cpuSheet.height];
            cpuSheet.SetPixels32(clear);
        }

        // 收集遮挡物和粒子的所有 Renderer
        Renderer[] occluderRenderers = null;
        Renderer[] particleRenderers = null;
        if (occluderRoots != null && occluderRoots.Count > 0)
        {
            var occList = new List<Renderer>();
            for (int i = 0; i < occluderRoots.Count; i++)
            {
                if (occluderRoots[i] != null)
                {
                    var r = occluderRoots[i].GetComponentsInChildren<Renderer>(true);
                    if (r != null && r.Length > 0) occList.AddRange(r);
                }
            }
            occluderRenderers = occList.ToArray();
        }
        var pList = new List<Renderer>();
        {
            var r = particleTarget.GetComponentsInChildren<Renderer>(true);
            if (r != null && r.Length > 0) pList.AddRange(r);
        }
        particleRenderers = pList.ToArray();

        try
        {
            // 固定种子
            savedStates = FixRandomSeeds(allPS);

            // 克隆相机
            cloneCam = CloneCamera(sourceCam);
            if (cloneCam == null)
            {
                Debug.LogError("[ParticleSequence] Failed to clone camera.");
                return false;
            }

            for (int i = 0; i < frameCount; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                    "Capturing Particle Sequence",
                    $"Frame {i + 1}/{frameCount}",
                    (i + 1) / (float)frameCount))
                {
                    canceled = true;
                    break;
                }

                // 计算时间
                float t;
                if (useEndpoints && frameCount > 1)
                    t = i / (float)(frameCount - 1) * duration;
                else
                    t = (i + 0.5f) / frameCount * duration;

                // 推进粒子到时刻 t
                SimulateAll(allPS, t);

                // RGB：直接从原始相机抓（和 Single/Anim 一致的 CaptureRGB）
                int rw = outW * superSize;
                int rh = outH * superSize;
                var capturedRGB = CapturePipeline.CaptureRGB(sourceCam, rw, rh);

                // Alpha + 遮挡：克隆相机做 B/W 和遮挡判断
                var frameTex = RenderFrame(cloneCam, outW, outH,
                    superSize, capturedRGB,
                    occluderRenderers, particleRenderers);

                try
                {
                    int col = i % cols;
                    int row = i / cols;
                    int dstX = col * outW;
                    int dstY = (rows - 1 - row) * outH;

                    if (gpuCopy)
                    {
                        Graphics.CopyTexture(frameTex, 0, 0, 0, 0,
                            outW, outH, sheetRT, 0, 0, dstX, dstY);
                    }
                    else
                    {
                        var px = frameTex.GetPixels32();
                        cpuSheet.SetPixels32(dstX, dstY, outW, outH, px);
                    }

                    if (exportIndividual && framesDir != null)
                    {
                        var fbytes = frameTex.EncodeToPNG();
                        var fname = $"frame_{i:D4}.png";
                        var fpath = Path.Combine(framesDir, fname)
                            .Replace('\\', '/');
                        File.WriteAllBytes(fpath, fbytes);
                    }
                }
                finally
                {
                    Object.DestroyImmediate(frameTex);
                }
            }

            if (!canceled)
            {
                Texture2D sheetTex;
                if (gpuCopy)
                {
                    var prevActive = RenderTexture.active;
                    RenderTexture.active = sheetRT;
                    sheetTex = new Texture2D(sheetRT.width, sheetRT.height,
                        TextureFormat.RGBA32, false, false);
                    sheetTex.ReadPixels(
                        new Rect(0, 0, sheetRT.width, sheetRT.height),
                        0, 0, false);
                    sheetTex.Apply();
                    RenderTexture.active = prevActive;
                }
                else
                {
                    cpuSheet.Apply();
                    sheetTex = cpuSheet;
                    cpuSheet = null;
                }

                var bytes = sheetTex.EncodeToPNG();
                Object.DestroyImmediate(sheetTex);

                sheetPath = Path.Combine(outputFolderAbs, sheetFileName)
                    .Replace('\\', '/');
                File.WriteAllBytes(sheetPath, bytes);

                Debug.Log(
                    $"<color=#7fcf6f>[ParticleSequence]</color> "
                    + $"Sheet saved: {sheetPath} "
                    + $"(layout {cols}x{rows}, frame {outW}x{outH})");
            }
            else
            {
                Debug.Log("[ParticleSequence] Canceled.");
            }

            return !canceled;
        }
        finally
        {
            // 恢复粒子
            if (savedStates != null)
                RestoreRandomSeeds(savedStates);
            ClearAll(allPS);

            // 销毁资源
            DestroyClone(cloneCam);
            RenderTexture.ReleaseTemporary(sheetRT);
            if (cpuSheet != null) Object.DestroyImmediate(cpuSheet);
            EditorUtility.ClearProgressBar();
        }
    }

    // ================================================================
    //  工具方法
    // ================================================================

    private static string GetGameObjectPath(GameObject go)
    {
        if (go == null) return "";
        var path = "/" + go.name;
        var t = go.transform;
        while (t.parent != null)
        {
            t = t.parent;
            path = "/" + t.name + path;
        }
        return path;
    }
}
