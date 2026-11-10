using System;
using System.IO;
using UnityEditor;
using System.Threading.Tasks;
using System.Collections.Generic;

using UnityEngine;

// Editor-only capture; does not modify scene cameras or require the Recorder package.
[InitializeOnLoad]
internal static class GameViewVideoRecorder
{
    private static GameViewVideoEncoder encoder;
    private static GameViewVideoRunner runner;
    private static GameObject captureObject;
    private static string outputPath;
    private static string temporaryOutputPath;
    private static string failureReason;
    private static bool reveal;
    private static string encoderExecutable;
    private static float previousCaptureDeltaTime;
    private static int fps;
    private static int frameWidth, frameHeight;
    private static int outputWidth, outputHeight, sourceWidth, sourceHeight;
    private static GameViewVideoFrameBuffer frameBuffer;
    private static string recordedOverlayPath;
    private static bool recordedOverlayStretch;
    private static readonly Queue<Task> pendingFrames = new Queue<Task>();
    private static Task<string> preparing;
    private static string selectedCodec;
    private static int quality;
    private static Task finishing;
    private static bool pausedForEncoder;
    private static string finishingPath;
    private static string finishingTemporaryPath;
    private static string finishingReason;
    private static double finishingDuration;
    private static int finishingFrames;
    private static bool finishingReveal;
    public static bool IsFinalizing => finishing != null || (preparing != null && !IsRecording);
    public static bool IsRecording { get; private set; }
    public static int FrameCount { get; private set; }
    public static double Duration => fps > 0 ? (double)FrameCount / fps : 0;
    public static string Status { get; private set; }

    static GameViewVideoRecorder()
    {
        EditorApplication.update += UpdateEncoding;
        AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
        EditorApplication.quitting += Shutdown;
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.ExitingPlayMode) Stop(false);
        };
    }

    private static void ReleaseEncoderPause()
    {
        if (!pausedForEncoder) return;
        pausedForEncoder = false;
        if (EditorApplication.isPlaying) EditorApplication.isPaused = false;
    }

    private static void UpdateEncoding()
    {
        if (preparing != null && preparing.IsCompleted)
        {
            var ready = preparing;
            preparing = null;
            try { selectedCodec = ready.GetAwaiter().GetResult(); if (IsRecording) StartCaptureRunner(); }
            catch (Exception ex) { failureReason = ex.Message; Stop(false); Status = "准备录制失败：" + ex.Message; }
        }
        while (pendingFrames.Count > 0 && pendingFrames.Peek().IsCompleted)
        {
            var completed = pendingFrames.Dequeue();
            try { completed.GetAwaiter().GetResult(); FrameCount++; }
            catch (Exception ex) { failureReason = ex.Message; Stop(false); Debug.LogError("视频编码失败：" + ex.Message); break; }
        }
        if (preparing == null && (encoder == null || pendingFrames.Count < encoder.Capacity)) ReleaseEncoderPause();
        if (finishing == null || !finishing.IsCompleted) return;
        var done = finishing;
        finishing = null;
        try
        {
            done.GetAwaiter().GetResult();
            if (finishingFrames > 0) File.Move(finishingTemporaryPath, finishingPath);
            else if (File.Exists(finishingTemporaryPath)) File.Delete(finishingTemporaryPath);
            Status = finishingFrames > 0 ? $"已保存 {finishingDuration:F1} 秒视频：\n{finishingPath}" : "没有捕获到视频帧。";
            if (!string.IsNullOrEmpty(finishingReason)) Status = "录制提前结束：" + finishingReason + "\n" + Status;
            Debug.Log(Status);
            if (finishingReveal && finishingFrames > 0) EditorUtility.RevealInFinder(finishingPath);
        }
        catch (Exception ex) { Status = "视频收尾失败，临时文件可能不完整：" + ex.Message + "\n" + finishingTemporaryPath; Debug.LogError(Status); }
    }

    private static void Shutdown()
    {
        Stop(false);
        try { preparing?.GetAwaiter().GetResult(); } catch (Exception ex) { Debug.LogError(ex.Message); }
        preparing = null;
        // Domain reload/quit must not abandon a process using this domain's buffers.
        try { finishing?.GetAwaiter().GetResult(); } catch (Exception ex) { Debug.LogError(ex.Message); }
        finishing = null;
    }

    public static void Begin(string folder, int frameRate, bool openFolder,
        string overlayPath = null, bool stretchOverlay = false, int customWidth = 0, int customHeight = 0, string ffmpegPath = null, int qualityLevel = 18)
    {
        if (IsRecording || IsFinalizing || preparing != null) return;
        if (!EditorApplication.isPlaying || EditorApplication.isPaused)
        {
            Status = "请先进入 Play 模式并取消暂停，再开始录制。";
            return;
        }
        try
        {
            if ((customWidth != 0 || customHeight != 0) &&
                (customWidth < 2 || customHeight < 2 || customWidth > 4096 || customHeight > 4096
                || customWidth % 2 != 0 || customHeight % 2 != 0))
                throw new ArgumentException("自定义视频宽高必须为 2～4096 的偶数。");
            encoderExecutable = string.IsNullOrWhiteSpace(ffmpegPath) ? GameViewVideoEncoder.FindExecutable() : ffmpegPath;
            quality = qualityLevel == 16 || qualityLevel == 22 ? qualityLevel : 18;
            outputWidth = customWidth;
            outputHeight = customHeight;
            // Decode the source file directly: texture import compression, tint and lighting
            // must not change the artist's UI colours.
            frameBuffer = new GameViewVideoFrameBuffer(overlayPath, stretchOverlay);
            recordedOverlayPath = overlayPath;
            recordedOverlayStretch = stretchOverlay;
            Directory.CreateDirectory(folder);
            outputPath = Path.Combine(folder, "Game_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
            temporaryOutputPath = Path.ChangeExtension(outputPath, ".recording.mp4");
            failureReason = null;
            fps = frameRate == 30 || frameRate == 120 ? frameRate : 60;
            reveal = openFolder;
            FrameCount = 0;
            pendingFrames.Clear();
            previousCaptureDeltaTime = Time.captureDeltaTime;
            IsRecording = true;
            var gameSize = Handles.GetMainGameViewSize();
            int probeWidth = outputWidth > 0 ? outputWidth : Mathf.Max(2, (int)gameSize.x);
            int probeHeight = outputHeight > 0 ? outputHeight : Mathf.Max(2, (int)gameSize.y);
            preparing = Task.Run(() => GameViewVideoEncoder.SelectHardware(encoderExecutable, probeWidth, probeHeight, fps, quality));
            pausedForEncoder = true;
            EditorApplication.isPaused = true;
            Status = "正在检测可用的硬件编码器…";
        }
        catch (Exception ex)
        {
            Stop(false);
            frameBuffer?.Dispose();
            frameBuffer = null;
            Status = "无法开始录制：" + ex.Message;
            Debug.LogException(ex);
        }
    }

    private static void StartCaptureRunner()
    {
        Time.captureFramerate = fps;
        captureObject = new GameObject("Game View Video Capture") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(captureObject);
        runner = captureObject.AddComponent<GameViewVideoRunner>();
        if (runner == null) throw new InvalidOperationException("无法创建逐帧录制组件。");
        runner.StartCapture(CaptureFrame, RunnerDestroyed);
        Status = "正在等待 Game 画面。";
    }
    internal static void CaptureFrame()
    {
        UpdateEncoding();
        if (!IsRecording || EditorApplication.isPaused || (encoder != null && pendingFrames.Count >= encoder.Capacity)) return;
        try
        {
            if (encoder == null) { sourceWidth = Screen.width; sourceHeight = Screen.height; }
            if (Screen.width != sourceWidth || Screen.height != sourceHeight)
                throw new InvalidOperationException("Game 分辨率发生变化，录制已结束；已录制的帧会保留。");
            if (encoder == null)
            {
                frameWidth = outputWidth > 0 ? outputWidth : sourceWidth;
                frameHeight = outputHeight > 0 ? outputHeight : sourceHeight;
                if (frameWidth < 2 || frameHeight < 2 || frameWidth % 2 != 0 || frameHeight % 2 != 0)
                    throw new InvalidOperationException("视频输出需要偶数宽高，请将 Game 分辨率设为 1920 × 1080 等固定尺寸后重试。");
                encoder = new GameViewVideoEncoder(encoderExecutable, temporaryOutputPath, frameWidth, frameHeight, fps, selectedCodec, quality);
            }
            var frame = frameBuffer.Capture(frameWidth, frameHeight,
                GameViewVideoPreview.IsInCapturedFrame(recordedOverlayPath, recordedOverlayStretch));
            pendingFrames.Enqueue(encoder.AddFrame(frame));
            // Only throttle when the bounded queue is actually full, not after every frame.
            if (pendingFrames.Count >= encoder.Capacity && !pendingFrames.Peek().IsCompleted)
            {
                pausedForEncoder = true;
                EditorApplication.isPaused = true;
            }
            Status = $"{encoder.Description} / {frameWidth} × {frameHeight} / {fps} FPS\n{(pausedForEncoder ? "编码缓冲已满，正在等待…" : "正在录制")}\n{outputPath}";
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            Stop(false);
            Status = "录制中止：" + ex.Message + "\n" + Status;
            Debug.LogError(Status);
        }
    }

    internal static void RunnerDestroyed(GameViewVideoRunner destroyedRunner)
    {
        if (ReferenceEquals(runner, destroyedRunner)) Stop(false);
    }

    public static void Stop(bool allowReveal = true)
    {
        if (!IsRecording) return;
        IsRecording = false;
        ReleaseEncoderPause();
        frameBuffer?.Dispose();
        frameBuffer = null;
        GameViewVideoPreview.Clear();
        Time.captureDeltaTime = previousCaptureDeltaTime;
        var activeEncoder = encoder;
        encoder = null;
        var activeRunner = runner;
        runner = null;
        var activeObject = captureObject;
        captureObject = null;
        if (activeRunner != null) activeRunner.StopCapture();
        // Destroy on the next editor tick, not from inside the runner's coroutine/OnDestroy.
        if (activeObject != null)
            EditorApplication.delayCall += () =>
            {
                if (activeObject != null) UnityEngine.Object.DestroyImmediate(activeObject);
            };
        finishingPath = outputPath;
        finishingTemporaryPath = temporaryOutputPath;
        finishingReason = failureReason;
        finishingFrames = FrameCount + pendingFrames.Count;
        finishingDuration = (double)finishingFrames / fps;
        finishingReveal = allowReveal && reveal;
        pendingFrames.Clear();
        Status = "正在保存 MP4 视频…";
        try { finishing = activeEncoder != null ? activeEncoder.Finish() : Task.CompletedTask; }
        catch (Exception ex) { finishing = Task.FromException(ex); }
    }
}
