using System.Text.Json;

namespace AppNetworkControl;

internal sealed class ApplicationGroup
{
    public string RootPath { get; set; } = "";
    public List<string> Paths { get; set; } = [];
}

internal sealed class ControlState
{
    public int Version { get; set; } = 2;
    public List<ApplicationGroup> Groups { get; set; } = [];
    public List<string> BlockedPaths { get; set; } = [];

    public bool IsBlocked(string path) => BlockedPaths.Contains(path, StringComparer.OrdinalIgnoreCase);
    public void SetBlocked(string path, bool blocked)
    {
        BlockedPaths.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
        if (blocked) BlockedPaths.Add(path);
    }

    public bool Merge(string rootPath, IEnumerable<string> paths)
    {
        var group = Groups.FirstOrDefault(x => string.Equals(x.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (group is null) return false;
        var known = group.Paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        foreach (var path in paths)
            if (known.Add(path)) { group.Paths.Add(path); changed = true; }
        return changed;
    }
}

internal static class ControlStore
{
    internal static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "应用联网控制", "groups-v2.json");
    public static ControlState Load()
    {
        if (File.Exists(FilePath))
        {
            var state = JsonSerializer.Deserialize<ControlState>(File.ReadAllText(FilePath))
                ?? throw new InvalidOperationException("分组配置为空，请检查配置文件。");
            if (state.Version != 2 || state.Groups is null || state.BlockedPaths is null || state.BlockedPaths.Any(string.IsNullOrWhiteSpace) ||
                state.Groups.Any(x => x is null || string.IsNullOrWhiteSpace(x.RootPath) || x.Paths is null || x.Paths.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidOperationException("分组配置格式不正确，原文件未被覆盖。");
            foreach (var group in state.Groups)
                group.Paths = group.Paths.Prepend(group.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return state;
        }

        // Read old data without rewriting it, so the old release remains usable.
        var result = new ControlState();
        foreach (var entry in HistoryStore.Load())
            result.Groups.Add(new ApplicationGroup { RootPath = entry.RootPath, Paths = entry.TargetPaths.Prepend(entry.RootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        var managed = FirewallRules.GetManagedGroups();
        foreach (var pair in managed)
        {
            if (!result.Groups.Any(x => string.Equals(x.RootPath, pair.Key, StringComparison.OrdinalIgnoreCase)))
                result.Groups.Add(new ApplicationGroup { RootPath = pair.Key, Paths = [pair.Key] });
            result.Merge(pair.Key, pair.Value.TargetPaths);
            foreach (string path in pair.Value.TargetPaths) result.SetBlocked(path, true);
        }
        Save(result);
        return result;
    }

    public static void Save(ControlState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
