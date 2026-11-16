using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AppNetworkControl;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool startHidden = args.Any(x => string.Equals(x, "--startup", StringComparison.OrdinalIgnoreCase));
        using var singleInstance = new Mutex(true, "Local\\AppNetworkControlV2", out bool first);
        if (!first) { MessageBox.Show("应用联网控制已在运行，请从系统托盘打开。", "应用联网控制"); return; }
        try { Application.Run(new MainForm(startHidden)); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法启动应用联网控制", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal static class ExecutableResolver
{
    public static string Resolve(string selectedPath)
    {
        selectedPath = Path.GetFullPath(selectedPath);
        string extension = Path.GetExtension(selectedPath);
        if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) return ValidateExecutable(selectedPath);
        if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择 .exe 程序或 .lnk Windows 快捷方式。");

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("当前系统无法读取 Windows 快捷方式。");
            shellObject = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("无法打开 Windows 快捷方式服务。");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(selectedPath);
            dynamic shortcut = shortcutObject;
            string targetPath = Environment.ExpandEnvironmentVariables((string?)shortcut.TargetPath ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(targetPath))
                throw new InvalidOperationException("这个快捷方式没有普通 EXE 目标，可能是商店应用或特殊系统快捷方式。");
            return ValidateExecutable(targetPath);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("读取快捷方式失败：" + ex.Message, ex); }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject)) Marshal.FinalReleaseComObject(shortcutObject);
            if (shellObject is not null && Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    private static string ValidateExecutable(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"快捷方式指向的不是 EXE 程序：\n{path}");
        if (!File.Exists(path)) throw new InvalidOperationException($"快捷方式指向的程序不存在：\n{path}");
        return path;
    }
}

internal sealed record ProcessChoice(string Path, string DisplayName, int[] Pids, bool IsRoot)
{
    public override string ToString()
    {
        string kind = IsRoot ? "主程序" : "联网子进程";
        string pids = Pids.Length == 0 ? "未运行或未检测到连接" : $"PID {string.Join(", ", Pids)}";
        return $"[{kind}] {DisplayName}  —  {pids}  —  {Path}";
    }
}

internal static class NetworkProcessScanner
{
    public static Dictionary<string, List<ProcessChoice>> FindMany(IEnumerable<string> rootPaths)
    {
        string[] roots = rootPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var output = new Dictionary<string, List<ProcessChoice>>(StringComparer.OrdinalIgnoreCase);
        if (roots.Length == 0) return output;
        var processes = NativeNetwork.ReadProcessTree();
        var networkPids = NativeNetwork.GetNetworkOwnerPids();
        var children = processes.ToLookup(x => x.ParentPid);
        var byPid = processes.ToDictionary(x => x.Pid);
        var pathCache = new Dictionary<int, string?>();
        string? ProcessPath(int pid)
        {
            if (!pathCache.TryGetValue(pid, out var path)) pathCache[pid] = path = TryGetProcessPath(pid);
            return path;
        }
        foreach (string rootPath in roots)
        {
            var rootPids = processes.Where(x => string.Equals(x.ExeName, Path.GetFileName(rootPath), StringComparison.OrdinalIgnoreCase))
                .Where(x => string.Equals(ProcessPath(x.Pid), rootPath, StringComparison.OrdinalIgnoreCase)).Select(x => x.Pid).ToHashSet();
            var related = new HashSet<int>(rootPids);
            var queue = new Queue<int>(rootPids);
            while (queue.TryDequeue(out int parent))
                foreach (var child in children[parent]) if (related.Add(child.Pid)) queue.Enqueue(child.Pid);
            var found = new List<ProcessChoice> { new(rootPath, Path.GetFileName(rootPath), rootPids.Order().ToArray(), true) };
            found.AddRange(related.Where(x => !rootPids.Contains(x) && networkPids.Contains(x))
                .Select(pid => (Pid: pid, Path: ProcessPath(pid)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Path) && !string.Equals(x.Path, rootPath, StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Path!, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessChoice(g.Key, byPid[g.First().Pid].ExeName, g.Select(x => x.Pid).Order().ToArray(), false))
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            output[rootPath] = found;
        }
        return output;
    }

    private static string? TryGetProcessPath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch { return null; }
    }
}

internal static class NativeNetwork
{
    private const uint Th32csSnapProcess = 0x00000002;
    private const int AfInet = 2, AfInet6 = 23;

    public static HashSet<int> GetNetworkOwnerPids()
    {
        var result = new HashSet<int>();
        ReadTable(GetExtendedTcpTable, AfInet, 5, 24, 20, result);
        ReadTable(GetExtendedTcpTable, AfInet6, 5, 56, 52, result);
        ReadTable(GetExtendedUdpTable, AfInet, 1, 12, 8, result);
        ReadTable(GetExtendedUdpTable, AfInet6, 1, 28, 24, result);
        return result;
    }

    public static List<(int Pid, int ParentPid, string ExeName)> ReadProcessTree()
    {
        var result = new List<(int, int, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do { result.Add(((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile)); }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    private delegate uint TableReader(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    private static void ReadTable(TableReader reader, int family, int tableClass, int rowSize, int pidOffset, HashSet<int> output)
    {
        int size = 0;
        reader(IntPtr.Zero, ref size, false, family, tableClass, 0);
        if (size <= 4) return;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (reader(buffer, ref size, false, family, tableClass, 0) != 0) return;
            int count = Marshal.ReadInt32(buffer);
            long start = buffer.ToInt64() + 4;
            for (int i = 0; i < count; i++)
            {
                int pid = Marshal.ReadInt32(new IntPtr(start + (long)i * rowSize + pidOffset));
                if (pid > 0) output.Add(pid);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}

internal static class FirewallRules
{
    private const string Prefix = "应用联网控制";
    private const int DirectionIn = 1, DirectionOut = 2, ActionBlock = 0, AllProfiles = int.MaxValue;

    // Match both the old per-root rules and the new per-path rules.
    // Never touch rules from other tools or Windows itself.
    public static void SetPathBlocked(string path, bool blocked)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        try { ApplyPathRules((object)policy, path, blocked, () => CreateCom("HNetCfg.FWRule")); }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

    internal static void ApplyPathRules(dynamic policy, string path, bool blocked, Func<object> createRule)
    {
        var names = new List<string>();
        foreach (dynamic rule in policy.Rules)
            if (((string)rule.Name).StartsWith(Prefix + " [", StringComparison.Ordinal) &&
                string.Equals((string?)rule.ApplicationName, path, StringComparison.OrdinalIgnoreCase))
                names.Add((string)rule.Name);
        foreach (string name in names) policy.Rules.Remove(name);
        if (blocked)
        {
            AddRule(policy, path, path, "入站", DirectionIn, createRule);
            AddRule(policy, path, path, "出站", DirectionOut, createRule);
        }
    }

    public static Dictionary<string, PathRuleState> ReadPathStates()
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        try { return ReadPathStatesFrom(policy); }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

    internal static Dictionary<string, PathRuleState> ReadPathStatesFrom(dynamic policy)
    {
        var builders = new Dictionary<string, ManagedGroupBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic rule in policy.Rules)
        {
            if (!((string)rule.Name).StartsWith(Prefix + " [", StringComparison.Ordinal)) continue;
            string? path = (string?)rule.ApplicationName;
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!builders.TryGetValue(path, out var builder)) builders[path] = builder = new ManagedGroupBuilder();
            bool valid = (bool)rule.Enabled && (int)rule.Action == ActionBlock && (int)rule.Profiles == AllProfiles;
            builder.AllRulesValid &= valid;
            if (!builder.Directions.TryGetValue(path, out var directions)) builder.Directions[path] = directions = [];
            if (valid) directions.Add((int)rule.Direction);
        }
        bool enabled = IsCurrentFirewallEnabled(policy);
        return builders.ToDictionary(x => x.Key, x => new PathRuleState(enabled && x.Value.AllRulesValid &&
            x.Value.Directions[x.Key].Contains(DirectionIn) && x.Value.Directions[x.Key].Contains(DirectionOut)), StringComparer.OrdinalIgnoreCase);
    }

    public static void SetBlocked(string rootPath, IReadOnlyCollection<string> paths)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        RemoveGroup(policy, rootPath);
        foreach (string path in paths)
        {
            AddRule(policy, rootPath, path, "入站", DirectionIn);
            AddRule(policy, rootPath, path, "出站", DirectionOut);
        }
    }

    public static void EnsureBlocked(string rootPath, IEnumerable<string> paths)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        string marker = GroupMarker(rootPath);
        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (dynamic rule in policy.Rules)
        {
            string name = (string)rule.Name;
            if (name.StartsWith(marker, StringComparison.Ordinal)) existingNames.Add(name);
        }

        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string inboundName = RuleName(rootPath, path, "入站");
            string outboundName = RuleName(rootPath, path, "出站");
            if (!existingNames.Contains(inboundName)) AddRule(policy, rootPath, path, "入站", DirectionIn);
            if (!existingNames.Contains(outboundName)) AddRule(policy, rootPath, path, "出站", DirectionOut);
        }
    }

    public static int RemoveGroup(string rootPath) => RemoveGroup(CreateCom("HNetCfg.FwPolicy2"), rootPath);

    public static int CountGroupRules(string rootPath)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        string marker = GroupMarker(rootPath);
        int count = 0;
        foreach (dynamic rule in policy.Rules)
            if (((string)rule.Name).StartsWith(marker, StringComparison.Ordinal)) count++;
        return count;
    }

    public static FirewallVerification VerifyGroup(string rootPath)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        string marker = GroupMarker(rootPath);
        var directions = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        bool allRulesValid = true;

        foreach (dynamic rule in policy.Rules)
        {
            string name = (string)rule.Name;
            if (!name.StartsWith(marker, StringComparison.Ordinal)) continue;
            string? path = (string?)rule.ApplicationName;
            bool valid = !string.IsNullOrWhiteSpace(path) && (bool)rule.Enabled && (int)rule.Action == ActionBlock;
            allRulesValid &= valid;
            if (!valid || path is null) continue;
            if (!directions.TryGetValue(path, out var set)) directions[path] = set = [];
            set.Add((int)rule.Direction);
        }

        bool completePairs = directions.Count > 0 && directions.Values.All(x => x.Contains(DirectionIn) && x.Contains(DirectionOut));
        int currentProfiles = (int)policy.CurrentProfileTypes;
        bool firewallEnabled = currentProfiles != 0;
        foreach (int profile in new[] { 1, 2, 4 })
            if ((currentProfiles & profile) != 0) firewallEnabled &= (bool)policy.FirewallEnabled[profile];

        var problems = new List<string>();
        if (directions.Count == 0) problems.Add("没有找到该组规则");
        if (!allRulesValid) problems.Add("存在未启用或非阻止规则");
        if (!completePairs) problems.Add("部分程序缺少入站或出站规则");
        if (!firewallEnabled) problems.Add("当前 Windows 防火墙配置未启用");
        return new FirewallVerification(problems.Count == 0, directions.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList(), string.Join("；", problems));
    }

    public static IReadOnlyDictionary<string, ManagedGroupInfo> GetManagedGroups()
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2");
        var builders = new Dictionary<string, ManagedGroupBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic rule in policy.Rules)
        {
            string name = (string)rule.Name;
            if (!name.StartsWith(Prefix + " [", StringComparison.Ordinal)) continue;
            string description = (string?)rule.Description ?? "";
            const string rootToken = "主程序：";
            const string targetToken = "；目标：";
            int rootStart = description.IndexOf(rootToken, StringComparison.Ordinal);
            int targetStart = description.IndexOf(targetToken, StringComparison.Ordinal);
            if (rootStart < 0 || targetStart <= rootStart) continue;
            string rootPath = description[(rootStart + rootToken.Length)..targetStart];
            string targetPath = description[(targetStart + targetToken.Length)..];
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(targetPath)) continue;

            if (!builders.TryGetValue(rootPath, out var builder)) builders[rootPath] = builder = new ManagedGroupBuilder();
            bool valid = (bool)rule.Enabled && (int)rule.Action == ActionBlock;
            builder.AllRulesValid &= valid;
            if (!builder.Directions.TryGetValue(targetPath, out var set)) builder.Directions[targetPath] = set = [];
            if (valid) set.Add((int)rule.Direction);
        }

        bool firewallEnabled = IsCurrentFirewallEnabled(policy);
        return builders.ToDictionary(
            x => x.Key,
            x => new ManagedGroupInfo(
                x.Key,
                x.Value.Directions.Keys.Order(StringComparer.CurrentCultureIgnoreCase).ToList(),
                firewallEnabled && x.Value.AllRulesValid && x.Value.Directions.Count > 0 &&
                    x.Value.Directions.Values.All(d => d.Contains(DirectionIn) && d.Contains(DirectionOut))),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCurrentFirewallEnabled(dynamic policy)
    {
        int currentProfiles = (int)policy.CurrentProfileTypes;
        bool enabled = currentProfiles != 0;
        foreach (int profile in new[] { 1, 2, 4 })
            if ((currentProfiles & profile) != 0) enabled &= (bool)policy.FirewallEnabled[profile];
        return enabled;
    }

    private static int RemoveGroup(dynamic policy, string rootPath)
    {
        string marker = GroupMarker(rootPath);
        var names = new List<string>();
        foreach (dynamic rule in policy.Rules)
        {
            string name = (string)rule.Name;
            if (name.StartsWith(marker, StringComparison.Ordinal)) names.Add(name);
        }
        foreach (string name in names) policy.Rules.Remove(name);
        return names.Count;
    }

    private static void AddRule(dynamic policy, string rootPath, string path, string directionText, int direction, Func<object>? createRule = null)
    {
        dynamic rule = createRule is null ? CreateCom("HNetCfg.FWRule") : createRule();
        try
        {
        rule.Name = RuleName(rootPath, path, directionText);
        rule.Description = $"由“{Prefix}”创建。主程序：{rootPath}；目标：{path}";
        rule.ApplicationName = path;
        rule.Direction = direction;
        rule.Action = ActionBlock;
        rule.Enabled = true;
        rule.Profiles = AllProfiles;
        rule.InterfaceTypes = "All";
        policy.Rules.Add(rule);
        }
        finally { if (Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule); }
    }

    private static string GroupMarker(string rootPath) => $"{Prefix} [{Hash(rootPath)}] ";
    private static string RuleName(string rootPath, string path, string directionText) => $"{GroupMarker(rootPath)}[{Hash(path)}] {directionText}";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..12];
    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId) ?? throw new PlatformNotSupportedException("当前系统不支持 Windows Defender Firewall 接口。");
        return Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法连接 Windows Defender Firewall。");
    }

    private sealed class ManagedGroupBuilder
    {
        public bool AllRulesValid { get; set; } = true;
        public Dictionary<string, HashSet<int>> Directions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record FirewallVerification(bool Success, IReadOnlyList<string> TargetPaths, string Details);
internal sealed record ManagedGroupInfo(string RootPath, IReadOnlyList<string> TargetPaths, bool IsEffective);

internal sealed record PathRuleState(bool Effective);
