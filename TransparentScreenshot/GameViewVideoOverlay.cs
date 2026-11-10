using System;
using System.IO;
using UnityEngine;

// Blend decoded display-space pixels after scene rendering, without material/colour-space conversion.
internal sealed class GameViewVideoOverlay
{
    private readonly Color32[] source;
    private readonly int sourceWidth, sourceHeight;
    private readonly bool stretch;
    private Color32[] fitted;
    private int width, height, left, bottom;

    public GameViewVideoOverlay(string path, bool stretch)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new IOException("请选择有效的 UI 图片文件。");
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            throw new IOException("UI 图片仅支持 PNG / JPG，透明 UI 请使用 PNG。");
        this.stretch = stretch;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            if (!texture.LoadImage(File.ReadAllBytes(path), false))
                throw new IOException("无法解码 UI 图片。");
            sourceWidth = texture.width;
            sourceHeight = texture.height;
            source = texture.GetPixels32();
        }
        finally { UnityEngine.Object.DestroyImmediate(texture); }
    }

    private Color Premultiplied(int x, int y)
    {
        Color c = source[Mathf.Clamp(y, 0, sourceHeight - 1) * sourceWidth
            + Mathf.Clamp(x, 0, sourceWidth - 1)];
        c.r *= c.a;
        c.g *= c.a;
        c.b *= c.a;
        return c;
    }

    private void Fit(int frameWidth, int frameHeight)
    {
        float scale = Mathf.Min((float)frameWidth / sourceWidth, (float)frameHeight / sourceHeight);
        width = stretch ? frameWidth : Mathf.Clamp(Mathf.RoundToInt(sourceWidth * scale), 1, frameWidth);
        height = stretch ? frameHeight : Mathf.Clamp(Mathf.RoundToInt(sourceHeight * scale), 1, frameHeight);
        left = (frameWidth - width) / 2;
        bottom = (frameHeight - height) / 2;
        fitted = new Color32[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            // Premultiplied bilinear filtering avoids dark fringes around transparent UI.
            float sx = (x + 0.5f) * sourceWidth / width - 0.5f;
            float sy = (y + 0.5f) * sourceHeight / height - 0.5f;
            int ix = Mathf.FloorToInt(sx), iy = Mathf.FloorToInt(sy);
            Color c = Color.Lerp(Color.Lerp(Premultiplied(ix, iy), Premultiplied(ix + 1, iy), sx - ix),
                Color.Lerp(Premultiplied(ix, iy + 1), Premultiplied(ix + 1, iy + 1), sx - ix), sy - iy);
            if (c.a > 0) { c.r /= c.a; c.g /= c.a; c.b /= c.a; }
            fitted[y * width + x] = c;
        }
    }

    public void Composite(Texture2D frame, bool preserveAlpha = false)
    {
        if (fitted == null) Fit(frame.width, frame.height);
        var pixels = frame.GetPixels32();
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            Color32 ui = fitted[y * width + x];
            if (ui.a == 0) continue;
            int index = (y + bottom) * frame.width + x + left;
            Color32 bg = pixels[index];
            int a = ui.a, inverse = 255 - a;
            if (preserveAlpha)
            {
                // Straight-alpha source-over: transparent screenshots must not become opaque.
                int alphaNumerator = a * 255 + bg.a * inverse;
                pixels[index] = new Color32(
                    (byte)((ui.r * a * 255 + bg.r * bg.a * inverse + alphaNumerator / 2) / alphaNumerator),
                    (byte)((ui.g * a * 255 + bg.g * bg.a * inverse + alphaNumerator / 2) / alphaNumerator),
                    (byte)((ui.b * a * 255 + bg.b * bg.a * inverse + alphaNumerator / 2) / alphaNumerator),
                    (byte)((alphaNumerator + 127) / 255));
                continue;
            }
            pixels[index] = new Color32(
                (byte)((ui.r * a + bg.r * inverse + 127) / 255),
                (byte)((ui.g * a + bg.g * inverse + 127) / 255),
                (byte)((ui.b * a + bg.b * inverse + 127) / 255), 255);
        }
        frame.SetPixels32(pixels);
        frame.Apply(false, false);
    }
}
