// CapturePipeline.cs
// 唯一渲染入口 — 所有截图模式共用
// Single / AnimSequence / ParticleSequence 都走 CaptureFrame()
//
// 思路：
//   RGB：原始相机 → CaptureRGB → endCameraRendering 回调抓取（颜色 = Game View）
//   Alpha（透明模式）：克隆相机 B/W 双背景对比
//   不透明模式：CaptureRGB 直接出图

// System.Func used explicitly via System.Func<> to avoid Object ambiguity
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2019_3_OR_NEWER
using UnityEngine.Rendering.Universal;
#endif

public static class CapturePipeline
{
    // ================================================================
    //  endCameraRendering 回调：URP 渲染完成后直接抓 RGB
    // ================================================================

    private static Camera        s_capCam;
    private static RenderTexture s_capRT;
    private static Texture2D     s_capResult;

    static CapturePipeline()
    {
        RenderPipelineManager.endCameraRendering += (ctx, cam) =>
        {
            if (cam != s_capCam || s_capRT == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = s_capRT;
            s_capResult = new Texture2D(s_capRT.width, s_capRT.height,
                TextureFormat.RGBA32, false, false);
            s_capResult.ReadPixels(new Rect(0, 0, s_capRT.width, s_capRT.height), 0, 0, false);
            s_capResult.Apply();
            RenderTexture.active = prev;
        };
    }

    // ================================================================
    //  公开入口
    // ================================================================

    /// <summary>
    /// 渲染一帧。所有截图模式统一入口。
    /// </summary>
    public static Texture2D CaptureFrame(Camera cam, int outW, int outH,
        int superSize, bool transparent,
        bool shadowCatcher = false,
        List<GameObject> shadowCasters = null,
        List<GameObject> shadowReceivers = null,
        float shadowIntensity = 1f)
    {
        if (shadowCatcher)
            return CaptureFrame_ShadowCatcher(cam, outW, outH, superSize,
                shadowCasters, shadowReceivers, shadowIntensity);

        int rw = outW * superSize;
        int rh = outH * superSize;

        // 不透明：endCameraRendering 回调直接抓
        if (!transparent)
        {
            var result = CaptureRGB(cam, outW, outH);
            if (result != null) return result;
            return RenderAndDownsample(cam, rw, rh, false, false, false, default, outW, outH, superSize);
        }

        // 透明：CaptureRGB 抓 RGB + B/W 双背景算 Alpha
        return RenderWithGameView(cam, rw, rh, outW, outH, superSize);
    }

    /// <summary>抓取原始相机渲染结果（sRGB，含后处理）。Particle 遮挡模式需要。</summary>
    internal static Texture2D CaptureRGB(Camera cam, int w, int h)
    {
        var prevTarget = cam.targetTexture;
        var rtFormat = cam.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        int msaaSamples = cam.allowMSAA ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;
        var rt = RenderTexture.GetTemporary(w, h, 24, rtFormat, RenderTextureReadWrite.sRGB, msaaSamples);

        s_capCam = cam;
        s_capRT = rt;
        s_capResult = null;

        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = prevTarget;

        var result = s_capResult;
        s_capCam = null;
        s_capRT = null;
        s_capResult = null;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    /// <summary>双背景算法：B/W pass 差异算 Alpha，和 rgbTex 合成。rgbTex 应为线性空间。</summary>
    internal static Texture2D DualBackgroundAlpha(Texture2D rgbTex,
        Texture2D blackTex, Texture2D whiteTex)
    {
        if (rgbTex.width != blackTex.width || rgbTex.height != blackTex.height ||
            rgbTex.width != whiteTex.width || rgbTex.height != whiteTex.height)
        {
            Debug.LogError("[CapturePipeline] Pass size mismatch.");
            return rgbTex;
        }

        int w = rgbTex.width, h = rgbTex.height;
        var rgb = rgbTex.GetPixels32();
        var b   = blackTex.GetPixels32();
        var wt  = whiteTex.GetPixels32();

        const int SOLID_EPS = 4;
        const int CLEAR_EPS = 2;

        for (int i = 0; i < rgb.Length; i++)
        {
            int dr = wt[i].r - b[i].r;
            int dg = wt[i].g - b[i].g;
            int db = wt[i].b - b[i].b;
            if (dr < 0) dr = 0; if (dg < 0) dg = 0; if (db < 0) db = 0;
            int diff = (dr + dg + db) / 3;

            byte alpha;
            if (diff <= SOLID_EPS)         alpha = 255;
            else if (diff >= 255 - CLEAR_EPS) alpha = 0;
            else                            alpha = (byte)(255 - diff);

            rgb[i] = new Color32(rgb[i].r, rgb[i].g, rgb[i].b, alpha);
        }

        var result = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        result.SetPixels32(rgb);
        result.Apply();

        // Alpha 1px 膨胀：消除辉光溢出边缘的白边
        DilateAlpha(result);

        return result;
    }

    // Alpha 边缘修正：RGB 亮度梯度引导 alpha 收缩
    // B/W alpha 边界比 RGB 柔和边界多出一圈 → 造成白边
    // 用 RGB 梯度定位视觉边缘，只在该缩的地方缩 alpha
    private static void DilateAlpha(Texture2D tex)
    {
        int w = tex.width, h = tex.height;
        var px = tex.GetPixels32();

        // 计算 luminance
        var lum = new float[px.Length];
        for (int i = 0; i < px.Length; i++)
            lum[i] = 0.2126f * px[i].r + 0.7152f * px[i].g + 0.0722f * px[i].b;

        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                if (px[i].a < 64) continue; // alpha 太低，不处理

                // 四邻最大亮度
                float maxLum = lum[i];
                if (lum[i - 1] > maxLum) maxLum = lum[i - 1];
                if (lum[i + 1] > maxLum) maxLum = lum[i + 1];
                if (lum[i - w] > maxLum) maxLum = lum[i - w];
                if (lum[i + w] > maxLum) maxLum = lum[i + w];

                // 只在 alpha 边界处理（邻居有透明像素），避免内部纹理被误伤
                bool isBoundary = px[i - 1].a < 5 || px[i + 1].a < 5
                               || px[i - w].a < 5 || px[i + w].a < 5;
                if (!isBoundary) continue;

                // 边界像素亮度远低于邻居 → alpha 多出的一圈，缩减
                if (maxLum > 10 && lum[i] < maxLum * 0.65f)
                {
                    float ratio = lum[i] / maxLum;
                    px[i].a = (byte)(px[i].a * ratio);
                }
            }
        }

        tex.SetPixels32(px);
        tex.Apply();
    }

    /// <summary>序列帧 Sheet 拼接器。renderFrame(i) 返回第 i 帧。</summary>
    internal static bool BuildSheet(System.Func<int, Texture2D> renderFrame,
        int frameCount, int cols, int rows, int outW, int outH,
        string folderAbs, string sheetFileName, string framesDirName,
        bool exportIndividual, out string outSheetPath)
    {
        outSheetPath = null;

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
            cpuSheet = new Texture2D(cols * outW, rows * outH, TextureFormat.RGBA32, false, false);
            cpuSheet.SetPixels32(new Color32[cpuSheet.width * cpuSheet.height]);
        }

        string framesDir = null;
        if (exportIndividual && !string.IsNullOrEmpty(framesDirName))
        {
            framesDir = Path.Combine(folderAbs, framesDirName).Replace('\\', '/');
            Directory.CreateDirectory(framesDir);
        }

        bool canceled = false;
        try
        {
            for (int i = 0; i < frameCount; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                    "Capturing Sequence", $"Frame {i + 1}/{frameCount}",
                    (i + 1) / (float)frameCount))
                {
                    canceled = true;
                    break;
                }

                var frameTex = renderFrame(i);
                if (frameTex == null) continue;

                try
                {
                    int col = i % cols;
                    int row = i / cols;
                    int dstX = col * outW;
                    int dstY = (rows - 1 - row) * outH;
                    if (gpuCopy)
                        Graphics.CopyTexture(frameTex, 0, 0, 0, 0, outW, outH, sheetRT, 0, 0, dstX, dstY);
                    else
                        cpuSheet.SetPixels32(dstX, dstY, outW, outH, frameTex.GetPixels32());

                    if (exportIndividual && framesDir != null)
                    {
                        var frameBytes = frameTex.EncodeToPNG();
                        var fpath = Path.Combine(framesDir, $"frame_{i:D4}.png").Replace('\\', '/');
                        File.WriteAllBytes(fpath, frameBytes);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(frameTex);
                }
            }

            if (canceled) return false;

            Texture2D sheetTex;
            if (gpuCopy)
            {
                var prevActive = RenderTexture.active;
                RenderTexture.active = sheetRT;
                sheetTex = new Texture2D(sheetRT.width, sheetRT.height, TextureFormat.RGBA32, false, false);
                sheetTex.ReadPixels(new Rect(0, 0, sheetRT.width, sheetRT.height), 0, 0, false);
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
            UnityEngine.Object.DestroyImmediate(sheetTex);

            outSheetPath = Path.Combine(folderAbs, sheetFileName).Replace('\\', '/');
            File.WriteAllBytes(outSheetPath, bytes);
            return true;
        }
        finally
        {
            RenderTexture.ReleaseTemporary(sheetRT);
            if (cpuSheet != null) UnityEngine.Object.DestroyImmediate(cpuSheet);
            EditorUtility.ClearProgressBar();
        }
    }

    /// <summary>强制所有 SkinnedMeshRenderer 重新蒙皮。AnimSequence 用。</summary>
    internal static void ForceSkinnedRefresh()
    {
        var all = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>();
        if (all == null || all.Length == 0) return;

        var dummy = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            for (int i = 0; i < all.Length; i++)
            {
                var smr = all[i];
                if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
                smr.forceMatrixRecalculationPerRender = true;
                try { smr.BakeMesh(dummy); }
                catch { /* ignore */ }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(dummy);
        }
    }

    // ================================================================
    //  透明 + 后处理渲染
    // ================================================================

    private static Texture2D RenderWithGameView(Camera cam, int rw, int rh,
        int outW, int outH, int superSize)
    {
        var rgbTex = CaptureRGB(cam, rw, rh);
        if (rgbTex != null)
        {
            SrgbToLinear(rgbTex);

            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var finalTex = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            UnityEngine.Object.DestroyImmediate(rgbTex);
            UnityEngine.Object.DestroyImmediate(blackTex);
            UnityEngine.Object.DestroyImmediate(whiteTex);

            if (superSize > 1)
            {
                var resized = Downsample(finalTex, outW, outH);
                UnityEngine.Object.DestroyImmediate(finalTex);
                finalTex = resized;
            }
            LinearToGamma(finalTex);
            return finalTex;
        }

        // Fallback
        var fbRgbTex   = RenderOnce(cam, rw, rh, true,  false, false, default);
        var fbBlackTex = RenderOnce(cam, rw, rh, false, true,  true,  Color.black);
        var fbWhiteTex = RenderOnce(cam, rw, rh, false, true,  true,  Color.white);
        var fbTex = DualBackgroundAlpha(fbRgbTex, fbBlackTex, fbWhiteTex);
        UnityEngine.Object.DestroyImmediate(fbRgbTex);
        UnityEngine.Object.DestroyImmediate(fbBlackTex);
        UnityEngine.Object.DestroyImmediate(fbWhiteTex);
        if (superSize > 1)
        {
            var resized = Downsample(fbTex, outW, outH);
            UnityEngine.Object.DestroyImmediate(fbTex);
            fbTex = resized;
        }
        LinearToGamma(fbTex);
        return fbTex;
    }

    internal static Texture2D RenderAndDownsample(Camera cam, int rw, int rh,
        bool transparent, bool disablePost, bool overrideClear, Color clearColor,
        int outW, int outH, int superSize)
    {
        var tex = RenderOnce(cam, rw, rh, transparent, disablePost, overrideClear, clearColor);
        if (superSize > 1)
        {
            var resized = Downsample(tex, outW, outH);
            UnityEngine.Object.DestroyImmediate(tex);
            tex = resized;
        }
        LinearToGamma(tex);
        return tex;
    }

    // ================================================================
    //  基础渲染：单 pass
    // ================================================================

    private static Texture2D RenderOnce(Camera cam, int rw, int rh,
        bool transparent, bool disablePost,
        bool overrideClear, Color clearColor,
        int cullingMask = -1)
    {
        var prevClearFlags = cam.clearFlags;
        var prevBg         = cam.backgroundColor;
        var prevTarget     = cam.targetTexture;
        var prevCulling    = cam.cullingMask;

#if UNITY_2019_3_OR_NEWER
        UniversalAdditionalCameraData urpData = null;
        bool prevPost = false;
        cam.TryGetComponent(out urpData);
        if (urpData != null)
        {
            prevPost = urpData.renderPostProcessing;
            if (disablePost) urpData.renderPostProcessing = false;
        }
#endif

        var rtFormat = cam.allowHDR
            ? RenderTextureFormat.ARGBHalf
            : RenderTextureFormat.ARGB32;
        int msaaSamples = cam.allowMSAA ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;
        var rt = RenderTexture.GetTemporary(rw, rh, 24, rtFormat,
            RenderTextureReadWrite.Linear, msaaSamples);

        try
        {
            if (cullingMask >= 0) cam.cullingMask = cullingMask;
            if (transparent)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            else if (overrideClear)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = clearColor;
            }

            cam.targetTexture = rt;
            cam.Render();

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            return tex;
        }
        finally
        {
            cam.targetTexture = prevTarget;
            cam.clearFlags    = prevClearFlags;
            cam.backgroundColor = prevBg;
            cam.cullingMask   = prevCulling;
#if UNITY_2019_3_OR_NEWER
            if (urpData != null) urpData.renderPostProcessing = prevPost;
#endif
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ================================================================
    //  工具
    // ================================================================

    internal static Texture2D ReadRTToTexture2D(RenderTexture rt, int w, int h)
    {
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
        tex.Apply();
        RenderTexture.active = prevActive;
        return tex;
    }

    internal static Texture2D Downsample(Texture2D src, int dstW, int dstH)
    {
        var tmp = RenderTexture.GetTemporary(dstW, dstH, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 1);
        tmp.filterMode = FilterMode.Bilinear;
        var prev = RenderTexture.active;
        Graphics.Blit(src, tmp);
        RenderTexture.active = tmp;
        var result = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false, false);
        result.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0, false);
        result.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);
        return result;
    }

    internal static void LinearToGamma(Texture2D tex)
    {
        var px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = px[i].gamma;
        tex.SetPixels(px);
        tex.Apply();
    }

    internal static void SrgbToLinear(Texture2D tex)
    {
        var px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = px[i].linear;
        tex.SetPixels(px);
        tex.Apply();
    }

    internal static void SetPostProcessing(Camera cam, bool enabled)
    {
#if UNITY_2019_3_OR_NEWER
        if (cam.TryGetComponent<UniversalAdditionalCameraData>(out var urpData))
            urpData.renderPostProcessing = enabled;
#endif
    }

    // ================================================================
    //  Shadow Catcher
    // ================================================================

    private static Texture2D CaptureFrame_ShadowCatcher(Camera cam,
        int outW, int outH, int superSize,
        List<GameObject> casterRoots, List<GameObject> receiverRoots,
        float shadowIntensity)
    {
        int rw = outW * superSize;
        int rh = outH * superSize;

        var casterRenderers  = GetRenderersFromList(casterRoots);
        var receiverRenderers = GetRenderersFromList(receiverRoots);

        var casterEnabled  = new bool[casterRenderers.Length];
        var receiverEnabled = new bool[receiverRenderers.Length];
        var receiverRecvShadow = new bool[receiverRenderers.Length];
        for (int i = 0; i < casterRenderers.Length; i++)
            casterEnabled[i] = casterRenderers[i].enabled;
        for (int i = 0; i < receiverRenderers.Length; i++)
        {
            receiverEnabled[i] = receiverRenderers[i].enabled;
            receiverRecvShadow[i] = receiverRenderers[i].receiveShadows;
        }

        try
        {
            var normalTex = RenderNormalTransparentLinear(cam, rw, rh);

            SetRenderersEnabled(receiverRenderers, false);
            var objectTex = RenderOnce(cam, rw, rh, true, true, false, default);
            SetRenderersEnabled(receiverRenderers, true);

            var fullTex = RenderOnce(cam, rw, rh, true, true, false, default);

            SetRenderersEnabled(casterRenderers, false);
            SetReceiveShadows(receiverRenderers, false);
            var groundNoShadowTex = RenderOnce(cam, rw, rh, true, true, false, default);
            SetReceiveShadows(receiverRenderers, true);
            SetRenderersEnabled(casterRenderers, true);

            var finalTex = CompositeShadowWithNormalColors(
                objectTex, fullTex, groundNoShadowTex, normalTex,
                shadowIntensity);

            UnityEngine.Object.DestroyImmediate(objectTex);
            UnityEngine.Object.DestroyImmediate(fullTex);
            UnityEngine.Object.DestroyImmediate(groundNoShadowTex);
            UnityEngine.Object.DestroyImmediate(normalTex);

            if (superSize > 1)
            {
                var resized = Downsample(finalTex, outW, outH);
                UnityEngine.Object.DestroyImmediate(finalTex);
                finalTex = resized;
            }
            LinearToGamma(finalTex);
            return finalTex;
        }
        finally
        {
            for (int i = 0; i < casterRenderers.Length; i++)
                casterRenderers[i].enabled = casterEnabled[i];
            for (int i = 0; i < receiverRenderers.Length; i++)
            {
                receiverRenderers[i].enabled = receiverEnabled[i];
                receiverRenderers[i].receiveShadows = receiverRecvShadow[i];
            }
        }
    }

    private static Texture2D RenderNormalTransparentLinear(Camera cam, int rw, int rh)
    {
        var gameViewTex = CaptureGameViewRGB(cam);
        if (gameViewTex != null)
        {
            SrgbToLinear(gameViewTex);

            Texture2D rgbTex;
            if (gameViewTex.width == rw && gameViewTex.height == rh)
            {
                rgbTex = gameViewTex;
            }
            else
            {
                var tmp = RenderTexture.GetTemporary(rw, rh, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1);
                tmp.filterMode = FilterMode.Bilinear;
                Graphics.Blit(gameViewTex, tmp);
                UnityEngine.Object.DestroyImmediate(gameViewTex);
                var prevActive = RenderTexture.active;
                RenderTexture.active = tmp;
                rgbTex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, true);
                rgbTex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
                rgbTex.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tmp);
            }

            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var result = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            UnityEngine.Object.DestroyImmediate(rgbTex);
            UnityEngine.Object.DestroyImmediate(blackTex);
            UnityEngine.Object.DestroyImmediate(whiteTex);
            return result;
        }
        else
        {
            var rgbTex  = RenderOnce(cam, rw, rh, true, false, false, default);
            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var result = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            UnityEngine.Object.DestroyImmediate(rgbTex);
            UnityEngine.Object.DestroyImmediate(blackTex);
            UnityEngine.Object.DestroyImmediate(whiteTex);
            return result;
        }
    }

    private static Texture2D CaptureGameViewRGB(Camera cam)
    {
        var gameViewRT = GetGameViewRT();
        if (gameViewRT == null) return null;

        var prevTarget = cam.targetTexture;
        cam.targetTexture = gameViewRT;
        cam.Render();
        cam.targetTexture = prevTarget;

        bool rtIsHDR = gameViewRT.format == RenderTextureFormat.ARGBHalf
                    || gameViewRT.format == RenderTextureFormat.ARGBFloat
                    || gameViewRT.format == RenderTextureFormat.RGB111110Float;

        Texture2D tex;
        if (rtIsHDR)
        {
            var ldrRT = RenderTexture.GetTemporary(gameViewRT.width, gameViewRT.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(gameViewRT, ldrRT);
            var prevActive = RenderTexture.active;
            RenderTexture.active = ldrRT;
            tex = new Texture2D(ldrRT.width, ldrRT.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, ldrRT.width, ldrRT.height), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(ldrRT);
        }
        else
        {
            var prevActive = RenderTexture.active;
            RenderTexture.active = gameViewRT;
            tex = new Texture2D(gameViewRT.width, gameViewRT.height, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, gameViewRT.width, gameViewRT.height), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
        }
        return tex;
    }

    private static RenderTexture GetGameViewRT()
    {
        var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (gameViewType == null) return null;

        var getMainMethod = gameViewType.GetMethod("GetMainGameView",
            BindingFlags.NonPublic | BindingFlags.Static);
        EditorWindow gameView = null;
        if (getMainMethod != null)
            gameView = getMainMethod.Invoke(null, null) as EditorWindow;
        if (gameView == null)
            gameView = EditorWindow.GetWindow(gameViewType);
        if (gameView == null) return null;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;

        var baseType = gameViewType.BaseType;
        if (baseType != null)
        {
            var field = baseType.GetField("m_TargetTexture", flags);
            if (field != null)
            {
                var rt = field.GetValue(gameView) as RenderTexture;
                if (rt != null && rt.IsCreated() && rt.width > 0)
                    return rt;
            }
        }

        foreach (var name in new[] { "m_RenderTexture", "m_TargetTexture", "m_Target" })
        {
            var field = gameViewType.GetField(name, flags);
            if (field != null)
            {
                var rt = field.GetValue(gameView) as RenderTexture;
                if (rt != null && rt.IsCreated() && rt.width > 0)
                    return rt;
            }
        }

        return null;
    }

    private static Texture2D CompositeShadowWithNormalColors(
        Texture2D objectTex, Texture2D fullTex, Texture2D groundNoShadowTex,
        Texture2D normalTex, float shadowIntensity)
    {
        int w = objectTex.width, h = objectTex.height;
        var objPx   = objectTex.GetPixels32();
        var fullPx  = fullTex.GetPixels32();
        var gndPx   = groundNoShadowTex.GetPixels32();
        var normalPx  = normalTex.GetPixels32();

        const int OBJ_ALPHA_THRESH = 8;
        const int GND_ALPHA_THRESH = 8;

        for (int i = 0; i < objPx.Length; i++)
        {
            if (objPx[i].a >= OBJ_ALPHA_THRESH)
            {
                // 物体像素：用正常透明截图颜色（与 Game View 一致）
                normalPx[i].a = 255;
            }
            else if (gndPx[i].a >= GND_ALPHA_THRESH)
            {
                // 地面像素：从亮度差异计算影子 alpha
                int lumC = (gndPx[i].r + gndPx[i].g + gndPx[i].b) / 3;
                int lumB = (fullPx[i].r + fullPx[i].g + fullPx[i].b) / 3;
                int diff = lumC - lumB;
                if (diff < 0) diff = 0;
                float ratio = lumC > 0 ? (float)diff / lumC : 0f;
                int a = Mathf.RoundToInt(Mathf.Clamp01(ratio * shadowIntensity) * 255);
                if (a > 255) a = 255;
                normalPx[i] = new Color32(0, 0, 0, (byte)a);
            }
            else
            {
                // 背景：全透明
                normalPx[i] = new Color32(0, 0, 0, 0);
            }
        }

        var result = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        result.SetPixels32(normalPx);
        result.Apply();
        return result;
    }

    private static Renderer[] GetRenderersFromList(List<GameObject> roots)
    {
        if (roots == null || roots.Count == 0) return new Renderer[0];
        var list = new List<Renderer>();
        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] == null) continue;
            var r = roots[i].GetComponentsInChildren<Renderer>(true);
            if (r != null && r.Length > 0) list.AddRange(r);
        }
        return list.ToArray();
    }

    private static void SetRenderersEnabled(Renderer[] renderers, bool enabled)
    {
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = enabled;
    }

    private static void SetReceiveShadows(Renderer[] renderers, bool enabled)
    {
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].receiveShadows = enabled;
    }
}
