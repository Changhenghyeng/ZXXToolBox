using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Compile as a .NET Framework Windows executable. Embed the distribution ZIP as
// AssetsPayload.zip and its manifest as AssetsManifest.json. Runtime data belongs
// to the outer executable's directory; this cache contains only bundled files.
internal static class CachedLauncher
{
    private const string CompleteFile = ".素材搜图运行环境完整";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public sealed class PayloadFile
    {
        public string path { get; set; }
        public long size { get; set; }
    }

    public sealed class PayloadManifest
    {
        public string payload_hash { get; set; }
        public string main_exe { get; set; }
        public List<PayloadFile> files { get; set; }
    }

    private sealed class StartupProgress : Form
    {
        private readonly Label message;

        public StartupProgress()
        {
            Text = "素材搜图";
            ClientSize = new Size(360, 105);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            ControlBox = false;
            ShowIcon = false;
            Padding = new Padding(16);
            message = new Label();
            message.Dock = DockStyle.Fill;
            message.Text = "正在准备首次启动…\r\n后续启动会直接使用已保存的运行环境。";
            message.TextAlign = ContentAlignment.MiddleLeft;
            ProgressBar progress = new ProgressBar();
            progress.Dock = DockStyle.Bottom;
            progress.Height = 14;
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 25;
            Controls.Add(message);
            Controls.Add(progress);
            FormClosing += delegate(object sender, FormClosingEventArgs args) { args.Cancel = true; };
        }

        public void UpdateMessage(string value)
        {
            message.Text = value + "\r\n后续启动会直接使用已保存的运行环境。";
            Application.DoEvents();
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Stopwatch watch = Stopwatch.StartNew();
        Dictionary<string, object> report = new Dictionary<string, object>();
        try
        {
            report["phase"] = "manifest_read";
            WriteReport(report);
            PayloadManifest manifest = ReadManifest();
            string outerDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string configuredCache = Environment.GetEnvironmentVariable("ASSET_SEARCH_RUNTIME_CACHE");
            string cacheRoot = Path.GetFullPath(String.IsNullOrWhiteSpace(configuredCache)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "素材搜图", "运行环境")
                : configuredCache);
            string cachePath = Path.Combine(cacheRoot, manifest.payload_hash.Substring(0, 20));
            report["cache_path"] = cachePath;
            Directory.CreateDirectory(cacheRoot);

            // The mutex is specific to the resolved cache location. A process
            // abandoned during extraction does not leave a permanent lock.
            string mutexName = "Local\\AssetSearchRuntime_" + HashText(cachePath.ToUpperInvariant());
            Process child;
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool ownsMutex = false;
                try
                {
                    report["phase"] = "mutex_wait";
                    WriteReport(report);
                    try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(60));
                        if (!ownsMutex) throw new IOException("运行环境准备超时，请稍后重试。"); }
                    catch (AbandonedMutexException) { ownsMutex = true; }
                    bool cacheHit = IsComplete(cachePath, manifest);
                    if (!cacheHit)
                    {
                        foreach (string candidate in Directory.GetDirectories(cacheRoot,
                            manifest.payload_hash.Substring(0, 20) + ".r*"))
                        {
                            if (IsComplete(candidate, manifest))
                            {
                                cachePath = candidate;
                                cacheHit = true;
                                break;
                            }
                        }
                    }
                    // Publish repaired files under a fresh name. This also
                    // avoids replacing DLL paths still mapped by another run.
                    if (!cacheHit && (Directory.Exists(cachePath) || File.Exists(cachePath)))
                        cachePath = Path.Combine(cacheRoot, manifest.payload_hash.Substring(0, 20)
                            + ".r" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    report["cache_path"] = cachePath;
                    if (!cacheHit)
                    {
                        report["phase"] = "cache_prepare";
                        WriteReport(report);
                        if (Environment.GetEnvironmentVariable("ASSET_SEARCH_LAUNCHER_HEADLESS") == "1")
                            PrepareCache(cacheRoot, cachePath, manifest, delegate(string message) { });
                        else
                        using (StartupProgress progress = new StartupProgress())
                        {
                            progress.Show();
                            Application.DoEvents();
                            PrepareCache(cacheRoot, cachePath, manifest, progress.UpdateMessage);
                        }
                    }
                    report["cache_hit"] = cacheHit;
                    report["prepare_ms"] = watch.Elapsed.TotalMilliseconds;

                    ProcessStartInfo start = new ProcessStartInfo();
                    start.FileName = SafeChildPath(cachePath, manifest.main_exe);
                    start.Arguments = JoinArguments(args);
                    start.WorkingDirectory = outerDirectory;
                    start.UseShellExecute = false;
                    start.EnvironmentVariables["ASSET_SEARCH_BASE_DIR"] = outerDirectory;
                    report["phase"] = "child_start";
                    WriteReport(report);
                    child = Process.Start(start);
                    if (child == null) throw new IOException("无法启动素材搜图程序。");
                    report["child_pid"] = child.Id;
                    WriteReport(report);
                }
                finally
                {
                    if (ownsMutex) mutex.ReleaseMutex();
                }
            }
            using (child)
            {
                child.WaitForExit();
                report["child_exit_code"] = child.ExitCode;
                WriteReport(report);
                return child.ExitCode;
            }
        }
        catch (Exception error)
        {
            report["error"] = error.ToString();
            report["prepare_ms"] = watch.Elapsed.TotalMilliseconds;
            WriteReport(report);
            if (Environment.GetEnvironmentVariable("ASSET_SEARCH_LAUNCHER_HEADLESS") != "1")
                MessageBox.Show("启动失败：\r\n" + error.Message, "素材搜图",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static PayloadManifest ReadManifest()
    {
        PayloadManifest manifest;
        using (Stream resource = OpenResource("AssetsManifest.json"))
        using (StreamReader reader = new StreamReader(resource, Encoding.UTF8, true))
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = 16 * 1024 * 1024;
            manifest = serializer.Deserialize<PayloadManifest>(reader.ReadToEnd());
        }
        if (manifest == null || manifest.files == null || manifest.files.Count == 0)
            throw new InvalidDataException("内置运行环境清单无效。");
        if (manifest.payload_hash == null || manifest.payload_hash.Length != 64)
            throw new InvalidDataException("内置运行环境版本标识无效。");
        foreach (char character in manifest.payload_hash)
            if (!Uri.IsHexDigit(character))
                throw new InvalidDataException("内置运行环境版本标识无效。");
        manifest.payload_hash = manifest.payload_hash.ToLowerInvariant();
        manifest.main_exe = NormalizeRelativePath(String.IsNullOrWhiteSpace(manifest.main_exe)
            ? "素材搜图.exe" : manifest.main_exe);
        HashSet<string> seen = new HashSet<string>(PathComparer);
        foreach (PayloadFile file in manifest.files)
        {
            if (file == null || file.size < 0)
                throw new InvalidDataException("内置文件清单无效。");
            file.path = NormalizeRelativePath(file.path);
            if (PathComparer.Equals(file.path, CompleteFile) || !seen.Add(file.path))
                throw new InvalidDataException("内置文件清单包含重复路径。");
        }
        if (!seen.Contains(manifest.main_exe))
            throw new InvalidDataException("内置文件清单缺少主程序。");
        return manifest;
    }

    private static Stream OpenResource(string name)
    {
        Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (resource == null) throw new InvalidDataException("缺少内置运行文件：" + name);
        return resource;
    }

    private static bool IsComplete(string directory, PayloadManifest manifest)
    {
        try
        {
            if (!Directory.Exists(directory) || IsReparsePoint(directory)) return false;
            string marker = Path.Combine(directory, CompleteFile);
            if (!File.Exists(marker) || IsReparsePoint(marker)
                || File.ReadAllText(marker, Encoding.UTF8).Trim() != manifest.payload_hash)
                return false;
            HashSet<string> checkedDirectories = new HashSet<string>(PathComparer);
            checkedDirectories.Add(directory);
            foreach (PayloadFile file in manifest.files)
            {
                string fullPath = SafeChildPath(directory, file.path);
                FileInfo info = new FileInfo(fullPath);
                if (!info.Exists || info.Length != file.size
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                string parent = info.DirectoryName;
                while (!checkedDirectories.Contains(parent))
                {
                    if (!Directory.Exists(parent) || IsReparsePoint(parent)) return false;
                    checkedDirectories.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    private static void PrepareCache(string cacheRoot, string cachePath, PayloadManifest manifest,
        Action<string> updateProgress)
    {
        string staging = Path.Combine(cacheRoot, ".准备." + Guid.NewGuid().ToString("N").Substring(0, 12));
        string backup = null;
        Directory.CreateDirectory(staging);
        try
        {
            using (Stream payload = OpenResource("AssetsPayload.zip"))
            using (SHA256 hash = SHA256.Create())
            {
                if (ToHex(hash.ComputeHash(payload)) != manifest.payload_hash)
                    throw new InvalidDataException("内置运行环境校验失败，请重新获取完整程序。");
            }
            Dictionary<string, long> expected = new Dictionary<string, long>(PathComparer);
            foreach (PayloadFile file in manifest.files) expected.Add(file.path, file.size);
            HashSet<string> extracted = new HashSet<string>(PathComparer);
            updateProgress("正在展开运行环境…");
            using (Stream payload = OpenResource("AssetsPayload.zip"))
            using (ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    // Neither Unix symlinks nor Windows reparse entries are part
                    // of a distribution. Never extract either into the cache.
                    int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("内置压缩包包含不支持的链接文件。");
                    bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
                    string relative = NormalizeRelativePath(isDirectory
                        ? entry.FullName.TrimEnd('/', '\\') : entry.FullName);
                    string destination = SafeChildPath(staging, relative);
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    long expectedSize;
                    if (!expected.TryGetValue(relative, out expectedSize)
                        || entry.Length != expectedSize || !extracted.Add(relative))
                        throw new InvalidDataException("内置压缩包与文件清单不一致。");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    if (new FileInfo(destination).Length != expectedSize)
                        throw new InvalidDataException("解压后的运行文件大小不正确。");
                    if (extracted.Count % 16 == 0 || extracted.Count == expected.Count)
                        updateProgress("正在准备运行环境（" + extracted.Count + "/" + expected.Count + "）…");
                }
            }
            if (extracted.Count != expected.Count)
                throw new InvalidDataException("内置压缩包缺少运行文件。");
            File.WriteAllText(Path.Combine(staging, CompleteFile), manifest.payload_hash, new UTF8Encoding(false));
            if (!IsComplete(staging, manifest)) throw new IOException("运行环境完整性检查失败。");

            // Preserve an existing incomplete cache instead of deleting files
            // that another application or user might have placed there.
            if (File.Exists(cachePath)) throw new IOException("运行缓存目录存在同名文件：" + cachePath);
            if (Directory.Exists(cachePath))
            {
                if (IsReparsePoint(cachePath)) throw new IOException("运行缓存目录不能是目录链接：" + cachePath);
                backup = Path.Combine(cacheRoot, ".备份." + Guid.NewGuid().ToString("N").Substring(0, 12));
                Directory.Move(cachePath, backup);
            }
            try
            {
                Directory.Move(staging, cachePath);
                staging = null;
            }
            catch
            {
                if (backup != null && !Directory.Exists(cachePath))
                {
                    try { Directory.Move(backup, cachePath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
        }
        finally
        {
            // Only this invocation's newly created, verified in-root staging
            // directory is eligible for cleanup. Existing caches are retained.
            if (staging != null && Directory.Exists(staging))
            {
                string prefix = Path.GetFullPath(cacheRoot).TrimEnd(Path.DirectorySeparatorChar)
                    + Path.DirectorySeparatorChar + ".准备.";
                if (Path.GetFullPath(staging).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(staging, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string NormalizeRelativePath(string path)
    {
        if (String.IsNullOrEmpty(path)) throw new InvalidDataException("内置文件路径为空。");
        string relative = path.Replace('/', '\\');
        if (Path.IsPathRooted(relative)) throw new InvalidDataException("内置压缩包包含绝对路径。");
        foreach (string segment in relative.Split('\\'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".."
                || segment.EndsWith(".", StringComparison.Ordinal)
                || segment.EndsWith(" ", StringComparison.Ordinal)
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("内置压缩包包含无效文件路径。");
            string device = segment.Split('.')[0].ToUpperInvariant();
            if (device == "CON" || device == "PRN" || device == "AUX" || device == "NUL"
                || (device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal)
                    || device.StartsWith("LPT", StringComparison.Ordinal))
                    && device[3] >= '1' && device[3] <= '9'))
                throw new InvalidDataException("内置压缩包包含保留文件名。");
        }
        return relative;
    }

    private static string SafeChildPath(string parent, string relative)
    {
        string prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内置文件路径超出运行缓存目录。");
        return result;
    }

    private static string HashText(string value)
    {
        using (SHA256 hash = SHA256.Create()) return ToHex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static string ToHex(byte[] bytes)
    {
        StringBuilder result = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return result.ToString();
    }

    private static string JoinArguments(string[] arguments)
    {
        StringBuilder result = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (result.Length > 0) result.Append(' ');
            result.Append('"');
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') result.Append('\\', slashes * 2 + 1);
                else result.Append('\\', slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
        }
        return result.ToString();
    }

    private static void WriteReport(Dictionary<string, object> report)
    {
        string path = Environment.GetEnvironmentVariable("ASSET_SEARCH_LAUNCHER_REPORT");
        if (String.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (System.Security.SecurityException) { }
    }
}
