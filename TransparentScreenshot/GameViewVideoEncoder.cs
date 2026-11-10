using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using UnityEditor.Media;
using UnityEditor;
using UnityEngine;

// Three reusable frame buffers and ordered writes; never build an unbounded frame queue.
internal sealed class GameViewVideoEncoder : IDisposable
{
    private Process process;
    private readonly Queue<byte[]> buffers = new Queue<byte[]>();
    private readonly object bufferLock = new object();
    private MediaEncoder unityEncoder;
    private int width, height;
    public int Capacity => unityEncoder != null ? 1 : 3;
    public string Description { get; private set; }
    private readonly StringBuilder errors = new StringBuilder();
    private bool failed;
    private Task pendingWrite;

    public static string FindExecutable()
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            string candidate = Path.Combine(directory.Trim('"'), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        // Reuse an existing installation without copying or bundling third-party binaries.
        foreach (string root in new[] { @"D:\Tools\JianyingPro",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JianyingPro", "Apps") })
        {
            if (!Directory.Exists(root)) continue;
            var versions = Directory.GetDirectories(root);
            Array.Sort(versions, StringComparer.OrdinalIgnoreCase);
            for (int i = versions.Length - 1; i >= 0; i--)
            {
                string candidate = Path.Combine(versions[i], "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return "";
    }

    private static string CodecOptions(string codec, int quality)
    {
        switch (codec)
        {
            case "h264_nvenc": return $"-c:v h264_nvenc -preset p4 -rc constqp -qp {quality} -profile:v high";
            case "h264_amf": return $"-c:v h264_amf -quality balanced -rc cqp -qp_i {quality} -qp_p {quality} -profile:v high";
            case "h264_qsv": return $"-c:v h264_qsv -preset veryfast -global_quality {quality} -look_ahead 0 -profile:v high";
            default: throw new ArgumentException("Unsupported encoder");
        }
    }

    // Probe an actual frame, not just an encoder listing: drivers/session limits can fail at runtime.
    // Called on a worker before Play-mode capture starts. Null selects Unity's built-in encoder.
    public static string SelectHardware(string executable, int width, int height, int fps, int quality)
    {
        if (!File.Exists(executable)) return null;
        foreach (string codec in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
        {
            try
            {
                using (var probe = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"-hide_banner -loglevel error -nostdin -f lavfi -i color=size={width}x{height}:rate={fps} -frames:v 1 {CodecOptions(codec, quality)} -pix_fmt yuv420p -f null -",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                }})
                {
                    probe.Start();
                    probe.BeginErrorReadLine();
                    if (!probe.WaitForExit(5000)) { probe.Kill(); probe.WaitForExit(1000); continue; }
                    if (probe.ExitCode == 0) return codec;
                }
            }
            catch { /* Another backend, then built-in fallback. */ }
        }
        return null;
    }

    public GameViewVideoEncoder(string executable, string output, int width, int height, int fps,
        string codec, int quality)
    {
        this.width = width;
        this.height = height;
        if (codec == null)
        {
            unityEncoder = new MediaEncoder(output, new VideoTrackAttributes
            {
                frameRate = new MediaRational(fps), width = (uint)width, height = (uint)height,
                includeAlpha = false, bitRateMode = VideoBitrateMode.High
            });
            Description = "Unity H.264（兼容模式）";
            return;
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("视频编码程序不可用。", executable);
        if (output.IndexOf('"') >= 0) throw new ArgumentException("输出路径包含无效字符。");
        for (int i = 0; i < 3; i++) buffers.Enqueue(new byte[checked(width * height * 4)]);
        Description = codec == "h264_nvenc" ? "NVIDIA 硬件编码" : codec == "h264_amf" ? "AMD 硬件编码" : "Intel 硬件编码";
        process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            // Raw CPU rows are bottom-up. Encode a standard H.264/yuv420p MP4 for common players.
            Arguments = $"-hide_banner -loglevel error -nostdin -n -f rawvideo -pixel_format rgba -video_size {width}x{height} -framerate {fps} -i pipe:0 -an -vf vflip,scale=in_range=full:out_range=tv:out_color_matrix=bt709 {CodecOptions(codec, quality)} -g {fps * 2} -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv -movflags +faststart -f mp4 \"{output}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardError = true
        }};
        process.ErrorDataReceived += (sender, args) =>
        {
            if (args.Data == null) return;
            lock (errors) { if (errors.Length < 8192) errors.AppendLine(args.Data); }
        };
        try { process.Start(); process.BeginErrorReadLine(); }
        catch { process.Dispose(); process = null; throw; }
    }

    public Task AddFrame(Texture2D frame)
    {
        if (unityEncoder != null)
        {
            if (!unityEncoder.AddFrame(width, height, width * 4, TextureFormat.RGBA32, frame.GetRawTextureData<byte>()))
                throw new IOException("Unity 编码器拒绝当前帧。");
            return Task.CompletedTask;
        }
        if (process.HasExited) throw new IOException("视频编码器已退出：" + ErrorText());
        byte[] buffer;
        lock (bufferLock)
        {
            if (buffers.Count == 0) throw new InvalidOperationException("编码缓冲已满。");
            buffer = buffers.Dequeue();
        }
        try
        {
            var data = frame.GetRawTextureData<byte>();
            if (data.Length != buffer.Length) throw new IOException("视频帧尺寸不一致。");
            data.CopyTo(buffer);
        }
        catch { lock (bufferLock) buffers.Enqueue(buffer); throw; }
        Task previous = pendingWrite;
        pendingWrite = Task.Run(async () =>
        {
            try
            {
                if (previous != null) await previous.ConfigureAwait(false);
                var write = process.StandardInput.BaseStream.WriteAsync(buffer, 0, buffer.Length);
                if (await Task.WhenAny(write, Task.Delay(10000)).ConfigureAwait(false) != write)
                {
                    process.Kill();
                    throw new IOException("编码器超过 10 秒未响应，已停止。");
                }
                await write.ConfigureAwait(false);
            }
            catch { failed = true; throw; }
            finally { lock (bufferLock) buffers.Enqueue(buffer); }
        });
        return pendingWrite;
    }

    private string ErrorText() { lock (errors) return errors.ToString(); }

    // Unity native encoding must be finalized on the editor thread; external encoding can finish in background.
    public Task Finish()
    {
        if (unityEncoder != null) { Dispose(); return Task.CompletedTask; }
        return Task.Run(() => Dispose());
    }

    public void Dispose()
    {
        if (unityEncoder != null) { unityEncoder.Dispose(); unityEncoder = null; return; }
        if (process == null) return;
        try
        {
            pendingWrite?.GetAwaiter().GetResult();
            process.StandardInput.Close();
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new IOException("视频收尾超时，文件可能不完整。");
            }
            if (failed || process.ExitCode != 0) throw new IOException("视频编码失败：" + ErrorText());
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose(); process = null;
            lock (bufferLock) buffers.Clear();
        }
    }
}
