using System;
using System.IO;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
internal static class GameViewVideoPreview
{
    private static GameViewUIOverlayPreview preview;
    private static Texture2D image;
    private static string loadedPath;
    private static bool stretch;
    public static string Error { get; private set; }

    static GameViewVideoPreview()
    {
        AssemblyReloadEvents.beforeAssemblyReload += Clear;
        EditorApplication.quitting += Clear;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                Clear();
        };
    }

    public static void Sync(bool enabled, string path, bool stretchImage, int outputWidth = 0, int outputHeight = 0)
    {
        // Freeze the image and layout for the duration of the recording.
        if (GameViewVideoRecorder.IsRecording) return;
        if (!enabled || string.IsNullOrWhiteSpace(path)) { Clear(); return; }
        if (preview != null && loadedPath == path && stretch == stretchImage)
        {
            bool changed = preview.OutputWidth != outputWidth || preview.OutputHeight != outputHeight;
            preview.OutputWidth = outputWidth;
            preview.OutputHeight = outputHeight;
            if (changed) UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return;
        }
        Clear();
        try
        {
            image = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                { hideFlags = HideFlags.HideAndDontSave };
            if (!image.LoadImage(File.ReadAllBytes(path), false))
                throw new IOException("无法解码 UI 图片。");
            image.wrapMode = TextureWrapMode.Clamp;
            var go = new GameObject("Screenshot UI Preview") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                preview = go.AddComponent<GameViewUIOverlayPreview>();
                if (preview == null) throw new InvalidOperationException("无法创建 UI 预览组件。");
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
            }
            catch { UnityEngine.Object.DestroyImmediate(go); throw; }
            preview.Image = image;
            preview.Stretch = stretchImage;
            preview.OutputWidth = outputWidth;
            preview.OutputHeight = outputHeight;
            loadedPath = path;
            stretch = stretchImage;
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
        catch (Exception ex)
        {
            Clear();
            Error = "UI 预览失败：" + ex.Message;
        }
    }

    public static bool IsInCapturedFrame(string path, bool stretchImage)
    {
        return preview != null && preview.isActiveAndEnabled && image != null
            && loadedPath == path && stretch == stretchImage
            && preview.LastDrawnFrame == Time.frameCount;
    }

    public static void Clear()
    {
        if (preview != null) UnityEngine.Object.DestroyImmediate(preview.gameObject);
        if (image != null) UnityEngine.Object.DestroyImmediate(image);
        preview = null;
        image = null;
        loadedPath = null;
        Error = null;
    }
}
