using System;
using System.IO;
using UnityEngine;

// Persistent buffers: no full-frame managed arrays, CPU resize loops or GPU uploads per frame.
internal sealed class GameViewVideoFrameBuffer : IDisposable
{
    private RenderTexture capture, output;
    private Texture2D pixels, ui;
    private Material blit;
    private readonly bool stretch;

    public GameViewVideoFrameBuffer(string overlayPath, bool stretchOverlay)
    {
        stretch = stretchOverlay;
        try
        {
            var shader = Shader.Find("Hidden/ZXX/GameViewVideoBlit");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("视频合成 Shader 不可用，请等待 Unity 导入完成。");
            blit = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (overlayPath != null)
            {
                if (string.IsNullOrWhiteSpace(overlayPath)) throw new IOException("请选择 UI 图片。");
                string ext = Path.GetExtension(overlayPath).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") throw new IOException("UI 图片仅支持 PNG / JPG。");
                // Raw display-space bytes: do not gamma-decode the source UI.
                ui = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                    { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
                if (!ui.LoadImage(File.ReadAllBytes(overlayPath), true)) throw new IOException("无法解码 UI 图片。");
                blit.SetTexture("_Overlay", ui);
            }
        }
        catch { Dispose(); throw; }
    }

    private static RenderTexture CreateTarget(int width, int height)
    {
        var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        if (!rt.Create()) { UnityEngine.Object.DestroyImmediate(rt); throw new IOException("无法分配视频缓冲，请降低分辨率。"); }
        return rt;
    }

    public Texture2D Capture(int width, int height, bool previewIncluded)
    {
        if (capture == null)
        {
            capture = CreateTarget(Screen.width, Screen.height);
            output = CreateTarget(width, height);
            pixels = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                { hideFlags = HideFlags.HideAndDontSave };
        }
        var previous = RenderTexture.active;
        bool previousSRGB = GL.sRGBWrite;
        try
        {
            ScreenCapture.CaptureScreenshotIntoRenderTexture(capture);
            Vector4 rect = new Vector4(0, 0, 1, 1);
            if (ui != null && !stretch)
            {
                float scale = Mathf.Min((float)width / ui.width, (float)height / ui.height);
                float w = Mathf.Clamp(Mathf.Round(ui.width * scale), 1, width);
                float h = Mathf.Clamp(Mathf.Round(ui.height * scale), 1, height);
                rect = new Vector4(Mathf.Floor((width - w) / 2) / width,
                    Mathf.Floor((height - h) / 2) / height, w / width, h / height);
            }
            blit.SetVector("_OverlayRect", rect);
            blit.SetFloat("_UseOverlay", ui != null && !previewIncluded ? 1 : 0);
            GL.sRGBWrite = false;
            Graphics.Blit(capture, output, blit);
            RenderTexture.active = output;
            pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            // Encoder reads CPU data. Apply() would unnecessarily upload 32 MB back to GPU at 4K.
            return pixels;
        }
        finally { RenderTexture.active = previous; GL.sRGBWrite = previousSRGB; }
    }

    public void Dispose()
    {
        if (capture != null) { capture.Release(); UnityEngine.Object.DestroyImmediate(capture); }
        if (output != null) { output.Release(); UnityEngine.Object.DestroyImmediate(output); }
        if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
        if (ui != null) UnityEngine.Object.DestroyImmediate(ui);
        if (blit != null) UnityEngine.Object.DestroyImmediate(blit);
        capture = output = null;
        pixels = ui = null;
        blit = null;
    }
}
