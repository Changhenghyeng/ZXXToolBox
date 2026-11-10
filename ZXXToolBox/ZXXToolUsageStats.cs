using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>本机、当前项目的工具使用统计；不写入共享配置或资源。</summary>
public static class ZXXToolUsageStats
{
    [Serializable]
    public class Entry
    {
        public string id;
        public long calls;
        public long completed;
        public long items;
        public double seconds;
        public string lastUtc;
    }

    [Serializable]
    private class Data
    {
        public string sinceUtc;
        public List<Entry> entries = new List<Entry>();
    }

    private static readonly HashSet<string> OperationTools = new HashSet<string>
    {
        "Building Card Prefab", "Shadow Baker", "Self Shadow Baker", "Card Ground Baker",
        "ProcGen", "Random Material", "Overlap Spreader", "Transparent Queue Sorter",
        "FBX Mesh Editor", "Rock Generator", "SLG Map Tile Cutter", "SLG City Editor",
        "Path Builder", "Box Collider Generator", "SLG Terrain Baker", "Fog Of War Tool",
        "Day Change Transition", "Outline", "Toon Material Converter", "Generated Assets Manager",
        "Light Cookie Scroller", "Batch Replacer", "Batch Rename Tool", "Sync Names",
        "Cast Shadows OFF", "Convert Variant To Normal Prefab", "Prefab Extractor",
        "Normalize Prefab Root", "Transparent Screenshot", "Shading Mode", "Label Tool",
        "Measure Tool", "Git Path Navigator"
    };
    private static string Key => "ZXXToolBox_Usage_v1_" + Hash128.Compute(Application.dataPath.ToLowerInvariant());
    private static Data _data;
    private static bool _warned;

    public static Stopwatch Begin() => Stopwatch.StartNew();
    public static bool HasOperationTracking(string id) => OperationTools.Contains(id);

    // A call counts an invocation from the toolbox/workflow, not a successful bake.
    public static void RecordCall(string id)
    {
        Update(id, entry => entry.calls++);
    }

    // Count one completed batch and its successful outputs; zero-output/failed runs do not count.
    public static void Complete(string id, int items, Stopwatch timer)
    {
        timer.Stop();
        RecordCompleted(id, items, timer.Elapsed.TotalSeconds);
    }

    // Editor tools in separate assemblies call this via reflection to avoid circular references.
    public static void RecordCompleted(string id, int items, double seconds)
    {
        if (items <= 0) return;
        Update(id, entry =>
        {
            entry.completed++;
            entry.items += items;
            entry.seconds += Math.Max(0.0, seconds);
        });
    }

    private static Data Load()
    {
        if (_data != null) return _data;
        string json = EditorPrefs.GetString(Key, "");
        Data data = string.IsNullOrEmpty(json) ? new Data() : JsonUtility.FromJson<Data>(json);
        if (data == null || data.entries == null)
            throw new InvalidDataException("工具统计数据无法读取，原记录已保留。");
        if (string.IsNullOrEmpty(data.sinceUtc)) data.sinceUtc = DateTime.UtcNow.ToString("O");
        _data = data;
        return _data;
    }

    private static void Update(string id, Action<Entry> update)
    {
        // Statistics must never interrupt the actual tool operation.
        try
        {
            Data data = Load();
            Entry entry = data.entries.Find(item => item.id == id);
            if (entry == null) { entry = new Entry { id = id }; data.entries.Add(entry); }
            update(entry);
            entry.lastUtc = DateTime.UtcNow.ToString("O");
            EditorPrefs.SetString(Key, JsonUtility.ToJson(data));
        }
        catch (Exception exception)
        {
            if (_warned) return;
            _warned = true;
            UnityEngine.Debug.LogWarning("[ZXXToolBox] 使用统计保存失败：" + exception.Message);
        }
    }

    public static string LocalTime(string utc)
    {
        return DateTime.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    }

    public static void Draw(IReadOnlyList<string> ids, ref int sortMode)
    {
        Data data;
        try { data = Load(); }
        catch (Exception exception) { EditorGUILayout.HelpBox(exception.Message, MessageType.Error); return; }

        EditorGUILayout.HelpBox("从添加统计后开始累计，仅保存在本机，按项目区分。调用次数统计工具箱和工作流入口；执行批次统计各工具的实际操作，取消和失败项不计，部分成功按成功项累计。纯预览工具统计主动应用，检查工具统计检查或定位。处理项随工具表示对象、资源或报告；耗时不代表节省的人工时间。", MessageType.Info);
        EditorGUILayout.LabelField("开始统计：" + LocalTime(data.sinceUtc));
        using (new EditorGUILayout.HorizontalScope())
        {
            sortMode = EditorGUILayout.Popup("排序", sortMode, new[] { "工具箱顺序", "调用次数", "完成批次" });
            if (GUILayout.Button("导出 CSV", GUILayout.Width(95))) Export(ids);
            if (GUILayout.Button("清空统计", GUILayout.Width(95)) && EditorUtility.DisplayDialog(
                    "清空本项目使用统计", "将清空本机当前项目的所有累计记录，无法撤销。建议先导出 CSV。", "清空", "取消"))
            {
                _data = new Data { sinceUtc = DateTime.UtcNow.ToString("O") };
                EditorPrefs.SetString(Key, JsonUtility.ToJson(_data));
                GUIUtility.ExitGUI();
            }
        }
        var rows = ids.Select(id => data.entries.Find(entry => entry.id == id) ?? new Entry { id = id });
        if (sortMode == 1) rows = rows.OrderByDescending(entry => entry.calls);
        if (sortMode == 2) rows = rows.OrderByDescending(entry => entry.completed);
        EditorGUILayout.LabelField("累计调用 " + data.entries.Sum(entry => entry.calls) + " 次；完成 "
            + data.entries.Sum(entry => entry.completed) + " 批；运行 "
            + (data.entries.Sum(entry => entry.seconds) / 60.0).ToString("F1") + " 分钟");
        foreach (Entry entry in rows)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(entry.id, EditorStyles.boldLabel);
                string operations = HasOperationTracking(entry.id)
                    ? $"完成 {entry.completed} 批  |  处理 {entry.items} 项  |  耗时 {entry.seconds:F1} 秒"
                    : "实际执行统计：尚未接入";
                EditorGUILayout.LabelField($"调用 {entry.calls} 次  |  {operations}", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("最近使用：" + LocalTime(entry.lastUtc), EditorStyles.miniLabel);
            }
        }
    }

    private static void Export(IReadOnlyList<string> ids)
    {
        string path = EditorUtility.SaveFilePanel("导出工具使用统计", "", "ZXX工具使用统计.csv", "csv");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Data data = Load();
            var csv = new StringBuilder("工具,调用次数,完成批次,处理项数,运行秒数,最近使用,开始统计\r\n");
            foreach (string id in ids)
            {
                Entry entry = data.entries.Find(item => item.id == id) ?? new Entry { id = id };
                string operations = HasOperationTracking(id)
                    ? $"{entry.completed},{entry.items},{entry.seconds.ToString("F3", CultureInfo.InvariantCulture)}"
                    : ",,";
                csv.Append('"').Append(id.Replace("\"", "\"\"")).Append("\",").Append(entry.calls)
                    .Append(',').Append(operations).Append(',').Append(LocalTime(entry.lastUtc))
                    .Append(',').Append(LocalTime(data.sinceUtc)).Append("\r\n");
            }
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            EditorUtility.RevealInFinder(path);
        }
        catch (Exception exception) { EditorUtility.DisplayDialog("导出失败", exception.Message, "确定"); }
    }
}
