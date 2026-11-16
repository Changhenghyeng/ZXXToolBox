using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppNetworkControl;

internal sealed class HistoryEntry
{
    public string RootPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsBlocked { get; set; }
    public bool MonitorEnabled { get; set; }
    public DateTimeOffset LastActionAt { get; set; }
    public List<string> TargetPaths { get; set; } = [];
    [JsonIgnore] public string CurrentState { get; set; } = "未检查";

    public override string ToString()
    {
        return $"[{CurrentState}] {DisplayName}  ·  最后操作 {LastActionAt.LocalDateTime:yyyy-MM-dd HH:mm}  ·  {RootPath}";
    }
}

internal static class HistoryStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "应用联网控制");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "history.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch { return []; }
    }

    public static void RecordBlocked(string rootPath, IEnumerable<string> targetPaths)
    {
        var entries = Load();
        var entry = FindOrCreate(entries, rootPath);
        entry.IsBlocked = true;
        entry.LastActionAt = DateTimeOffset.Now;
        entry.TargetPaths = targetPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        Save(entries);
    }

    public static void RecordRestored(string rootPath)
    {
        var entries = Load();
        var entry = FindOrCreate(entries, rootPath);
        entry.IsBlocked = false;
        entry.MonitorEnabled = false;
        entry.LastActionAt = DateTimeOffset.Now;
        Save(entries);
    }

    public static void SetMonitorEnabled(string rootPath, bool enabled)
    {
        var entries = Load();
        var entry = FindOrCreate(entries, rootPath);
        foreach (var item in entries) item.MonitorEnabled = false;
        entry.MonitorEnabled = enabled;
        Save(entries);
    }

    public static void ImportBlocked(IReadOnlyDictionary<string, List<string>> groups)
    {
        if (groups.Count == 0) return;
        var entries = Load();
        bool changed = false;
        foreach (var group in groups)
        {
            var entry = entries.FirstOrDefault(x => string.Equals(x.RootPath, group.Key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = FindOrCreate(entries, group.Key);
                entry.LastActionAt = DateTimeOffset.Now;
                changed = true;
            }
            if (!entry.IsBlocked || !entry.TargetPaths.SequenceEqual(group.Value, StringComparer.OrdinalIgnoreCase)) changed = true;
            entry.IsBlocked = true;
            entry.TargetPaths = group.Value.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        if (changed) Save(entries);
    }

    public static void Remove(string rootPath)
    {
        var entries = Load();
        entries.RemoveAll(x => string.Equals(x.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        Save(entries);
    }

    private static HistoryEntry FindOrCreate(List<HistoryEntry> entries, string rootPath)
    {
        rootPath = Path.GetFullPath(rootPath);
        var entry = entries.FirstOrDefault(x => string.Equals(x.RootPath, rootPath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry;
        entry = new HistoryEntry
        {
            RootPath = rootPath,
            DisplayName = Path.GetFileName(rootPath),
            LastActionAt = DateTimeOffset.Now
        };
        entries.Add(entry);
        return entry;
    }

    private static void Save(List<HistoryEntry> entries)
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporaryPath = FilePath + ".tmp";
        string json = JsonSerializer.Serialize(entries.OrderByDescending(x => x.LastActionAt).ToList(), JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, FilePath, true);
    }
}
