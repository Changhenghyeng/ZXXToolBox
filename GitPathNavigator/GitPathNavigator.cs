using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// 粘贴 Git 路径，自动提取 Assets/ 或 Packages/ 之后的部分，在 Project 窗口中定位。
/// </summary>
public class GitPathNavigator : EditorWindow
{
    private const string ControlName = "GitPathField";
    private const string HistoryKey  = "GitPathNavigator_History";
    private const int    MaxHistory  = 50;

    private enum DateFilter { All, Today, Last7Days, Last30Days }

    private string            _inputPath       = "";
    private string            _statusMsg       = "";
    private MessageType       _statusType      = MessageType.None;
    private string            _lastAssetPath   = "";
    private List<HistoryEntry> _history        = new List<HistoryEntry>();
    private bool              _showHistory     = true;
    private string            _searchFilter    = "";
    private DateFilter        _dateFilter      = DateFilter.All;

    // 模糊搜索建议
    private List<string>  _suggestions     = new List<string>();
    private List<string>  _assetPathCache;
    private bool          _cacheDirty      = true;
    private Vector2       _suggestionScroll;
    private string        _prevInput       = "";

    public static void ShowWindow()
    {
        var win = GetWindow<GitPathNavigator>(false, "Git Path Navigator", true);
        win.minSize = new Vector2(500, 250);
    }

    [Shortcut("ZXX Tools/Open Git Path Navigator", null, KeyCode.G, ShortcutModifiers.Control)]
    private static void OpenFromShortcut()
    {
        ShowWindow();
    }

    private void OnEnable()
    {
        LoadHistory();
        _cacheDirty = true;
        if (string.IsNullOrEmpty(_statusMsg))
        {
            _statusMsg  = "Paste a Git path (e.g., from git diff) and click Locate to find the asset in the Project window";
            _statusType = MessageType.Info;
        }
    }

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    // ════════════════ GUI ════════════════

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Paste a Git path to locate it in the Project window", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4);

        // ── 输入行 ──
        EditorGUILayout.BeginHorizontal();
        GUI.SetNextControlName(ControlName);
        _inputPath = EditorGUILayout.TextField(_inputPath, GUILayout.Height(22));
        if (GUILayout.Button(Content("Clear", "清空输入框和状态"), GUILayout.Width(50), GUILayout.Height(22)))
        {
            _inputPath     = "";
            _statusMsg     = "";
            _lastAssetPath = "";
            _suggestions.Clear();
            GUI.FocusControl(null);
            Repaint();
        }
        EditorGUILayout.EndHorizontal();

        // ── 模糊搜索建议 ──
        if (_inputPath != _prevInput)
        {
            _prevInput = _inputPath;
            UpdateSuggestions(_inputPath);
        }

        bool isInputFocused = GUI.GetNameOfFocusedControl() == ControlName;
        if (_suggestions.Count > 0 && isInputFocused && !string.IsNullOrWhiteSpace(_inputPath))
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            _suggestionScroll = EditorGUILayout.BeginScrollView(
                _suggestionScroll, GUILayout.MaxHeight(180));
            for (int i = 0; i < _suggestions.Count; i++)
            {
                string suggestion = _suggestions[i];
                if (GUILayout.Button(suggestion, EditorStyles.linkLabel))
                {
                    _inputPath = suggestion;
                    Navigate(suggestion);
                    GUI.FocusControl(null);
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(4);

        // ── 定位 + 打开文件夹 ──
        bool pressEnter = Event.current.type == EventType.KeyDown
                       && Event.current.keyCode == KeyCode.Return
                       && GUI.GetNameOfFocusedControl() == ControlName;

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(Content("Locate", "解析路径并在 Project 窗口中定位到对应资源"), GUILayout.Height(24)) || pressEnter)
        {
            if (!Navigate(_inputPath) && _suggestions.Count > 0)
            {
                Navigate(_suggestions[0]);
                _inputPath = _suggestions[0];
            }
            if (pressEnter) Event.current.Use();
        }
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_lastAssetPath)))
        {
            if (GUILayout.Button(Content("Reveal in Explorer", "在系统资源管理器中打开该文件所在目录"), GUILayout.Height(24)))
                EditorUtility.RevealInFinder(_lastAssetPath);
        }
        EditorGUILayout.EndHorizontal();

        // ── 历史记录 ──
        EditorGUILayout.Space(4);
        _showHistory = EditorGUILayout.Foldout(_showHistory, $"History ({_history.Count})");

        if (_showHistory)
        {
            if (_history.Count == 0)
            {
                EditorGUILayout.HelpBox("暂无历史记录，粘贴 Git 路径后使用「定位」按钮即可记录", MessageType.Info);
            }
            else
            {
                // 筛选工具条
                EditorGUILayout.BeginHorizontal();
                _dateFilter = (DateFilter)EditorGUILayout.EnumPopup(_dateFilter, GUILayout.Width(100), GUILayout.Height(22));
                var searchStyle = new GUIStyle(EditorStyles.toolbarSearchField) { fixedHeight = 18, fixedWidth = 0, stretchWidth = true };
                _searchFilter = EditorGUILayout.TextField(_searchFilter, searchStyle);
                GUILayout.FlexibleSpace();
                bool hasFilter = IsFilterActive();
                string clearLabel = hasFilter ? "Clear Filtered" : "Clear All";
                if (GUILayout.Button(Content(clearLabel, hasFilter ? "清除当前筛选条件下的记录" : "清除全部历史记录"), EditorStyles.miniButton, GUILayout.Width(80), GUILayout.Height(22)))
                {
                    if (hasFilter)
                        DeleteFilteredEntries();
                    else
                        ClearHistory();
                }
                EditorGUILayout.EndHorizontal();

                var filtered = GetFilteredHistory().ToList();
                if (filtered.Count == 0)
                {
                    EditorGUILayout.HelpBox("当前筛选条件下无记录", MessageType.Info);
                }
                else
                {
                    EditorGUI.indentLevel++;
                    for (int i = 0; i < filtered.Count; i++)
                    {
                        var entry     = filtered[i];
                        int realIndex = _history.IndexOf(entry);

                        EditorGUILayout.BeginHorizontal();

                        // 时间戳
                        var timeStyle = new GUIStyle(EditorStyles.label)
                        {
                            alignment = TextAnchor.MiddleLeft,
                            normal    = { textColor = new Color(0.55f, 0.55f, 0.55f, 0.85f) }
                        };
                        string displayTime = entry.time ?? "";
                        EditorGUILayout.LabelField(displayTime, timeStyle, GUILayout.Width(145));

                        // 路径（可点击）
                        if (GUILayout.Button(entry.path, EditorStyles.linkLabel))
                        {
                            _inputPath = entry.path;
                            Navigate(entry.path);
                        }

                        // 单条删除
                        if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                            DeleteHistoryEntry(realIndex);

                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }

        // ── 状态提示 ──
        if (!string.IsNullOrEmpty(_statusMsg))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(_statusMsg, _statusType);
        }

        // ── 署名 ──
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal    = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    // ════════════════ 核心逻辑 ════════════════

    private bool Navigate(string rawPath)
    {
        _lastAssetPath = "";

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            SetStatus("Please paste a path first", MessageType.Warning);
            return false;
        }

        string normalized = rawPath.Replace('\\', '/').Trim().TrimEnd('/');

        string assetPath = ExtractPath(normalized, "Assets") ?? ExtractPath(normalized, "Packages");
        if (assetPath == null)
        {
            SetStatus($"Cannot find 'Assets' or 'Packages' in path:\n{rawPath}", MessageType.Error);
            return false;
        }

        var obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath);

        if (obj == null)
        {
            SetStatus($"Asset not found in Project:\n{assetPath}", MessageType.Error);
            return false;
        }

        EditorUtility.FocusProjectWindow();
        Selection.activeObject = obj;
        EditorGUIUtility.PingObject(obj);
        _lastAssetPath = assetPath;
        AddToHistory(rawPath);
        SetStatus($"Located: {assetPath}", MessageType.Info);
        return true;
    }

    private static string ExtractPath(string normalized, string root)
    {
        string slashRoot = "/" + root;

        int idx = normalized.IndexOf(slashRoot + "/", StringComparison.Ordinal);
        if (idx >= 0) return normalized.Substring(idx + 1);

        if (normalized.StartsWith(root + "/", StringComparison.Ordinal)) return normalized;

        if (normalized.EndsWith(slashRoot, StringComparison.Ordinal) ||
            string.Equals(normalized, root, StringComparison.Ordinal))
            return root;

        return null;
    }

    // ════════════════ 模糊搜索 ════════════════

    private void UpdateSuggestions(string input)
    {
        _suggestions.Clear();
        if (string.IsNullOrWhiteSpace(input) || input.Length < 2) return;

        // 刷新全路径缓存
        if (_cacheDirty || _assetPathCache == null)
        {
            _assetPathCache = new List<string>(AssetDatabase.GetAllAssetPaths());
            _cacheDirty = false;
        }

        // 剥掉 Git 仓库前缀，提取纯资产路径
        string normalized = input.Trim().Replace('\\', '/');
        string assetPart  = ExtractPath(normalized, "Assets")
                         ?? ExtractPath(normalized, "Packages")
                         ?? normalized;

        var candidates = new HashSet<string>();

        // 第一轮：取文件名用 Unity 内置索引快速搜索
        string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPart);
        if (!string.IsNullOrEmpty(fileName) && fileName.Length >= 1)
        {
            foreach (var guid in AssetDatabase.FindAssets(fileName + "*"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    candidates.Add(path);
            }
        }

        // 第二轮：全路径包含匹配（覆盖文件夹匹配、拼写偏移等情况）
        if (candidates.Count < 15)
        {
            string lowerAsset = assetPart.ToLowerInvariant();
            foreach (var path in _assetPathCache)
            {
                if (candidates.Count >= 200) break;
                if (path.ToLowerInvariant().Contains(lowerAsset))
                    candidates.Add(path);
            }
        }

        // 模糊评分排序，取前 15
        _suggestions = candidates
            .Select(p => new { path = p, score = FuzzyScore(assetPart, p) })
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(15)
            .Select(x => x.path)
            .ToList();
    }

    private static int FuzzyScore(string query, string target)
    {
        int score = 0;
        int qIdx = 0;
        int consecutive = 0;
        int prevMatch = -1;

        for (int i = 0; i < target.Length && qIdx < query.Length; i++)
        {
            if (char.ToLowerInvariant(target[i]) == char.ToLowerInvariant(query[qIdx]))
            {
                score += 1;

                if (prevMatch >= 0 && i == prevMatch + 1)
                {
                    consecutive++;
                    score += consecutive * 3;
                }
                else
                {
                    consecutive = 0;
                }

                // 首选匹配：路径段开头、下划线后、大写字母
                if (i == 0 || target[i - 1] == '/' || target[i - 1] == '_')
                    score += 5;
                else if (char.IsUpper(target[i]))
                    score += 2;

                prevMatch = i;
                qIdx++;
            }
        }

        return qIdx == query.Length ? score : 0;
    }

    // ════════════════ 历史记录 ════════════════

    private void LoadHistory()
    {
        _history.Clear();
        string json = EditorPrefs.GetString(HistoryKey, "");
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var data = JsonUtility.FromJson<HistoryData>(json);
            // v2 格式（含时间戳）优先
            if (data?.entries != null && data.entries.Length > 0)
            {
                _history.AddRange(data.entries);
            }
            // 兼容旧格式：纯字符串数组，自动迁移
            else if (data?.items != null && data.items.Length > 0)
            {
                foreach (var item in data.items)
                {
                    if (!string.IsNullOrEmpty(item))
                        _history.Add(new HistoryEntry { path = item, time = "" });
                }
                SaveHistory(); // 迁移为新格式
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        var data = new HistoryData { entries = _history.ToArray() };
        EditorPrefs.SetString(HistoryKey, JsonUtility.ToJson(data));
    }

    private void AddToHistory(string path)
    {
        // 移除同路径旧记录，避免重复
        _history.RemoveAll(e => e.path == path);
        _history.Insert(0, new HistoryEntry
        {
            path = path,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        });
        if (_history.Count > MaxHistory)
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        SaveHistory();
    }

    private void DeleteHistoryEntry(int index)
    {
        if (index < 0 || index >= _history.Count) return;
        _history.RemoveAt(index);
        SaveHistory();
        Repaint();
    }

    private bool IsFilterActive()
    {
        return !string.IsNullOrWhiteSpace(_searchFilter) || _dateFilter != DateFilter.All;
    }

    private void DeleteFilteredEntries()
    {
        var filtered = GetFilteredHistory().ToList();
        if (filtered.Count == 0) return;
        foreach (var entry in filtered)
            _history.Remove(entry);
        SaveHistory();
        Repaint();
    }

    private void ClearHistory()
    {
        _history.Clear();
        EditorPrefs.DeleteKey(HistoryKey);
        _showHistory = false;
        Repaint();
    }

    private IEnumerable<HistoryEntry> GetFilteredHistory()
    {
        IEnumerable<HistoryEntry> query = _history;

        // 模糊搜索（不区分大小写）
        if (!string.IsNullOrWhiteSpace(_searchFilter))
        {
            string keyword = _searchFilter.Trim();
            query = query.Where(e =>
                e.path.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        switch (_dateFilter)
        {
            case DateFilter.Today:
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                query = _history.Where(e =>
                    !string.IsNullOrEmpty(e.time) && e.time.StartsWith(today));
                break;
            }
            case DateFilter.Last7Days:
            {
                var sevenDaysAgo = DateTime.Now.Date.AddDays(-7);
                query = _history.Where(e =>
                    TryParseDate(e.time, out var dt) && dt >= sevenDaysAgo);
                break;
            }
            case DateFilter.Last30Days:
            {
                var thirtyDaysAgo = DateTime.Now.Date.AddDays(-30);
                query = _history.Where(e =>
                    TryParseDate(e.time, out var dt) && dt >= thirtyDaysAgo);
                break;
            }
        }

        return query;
    }

    private static bool TryParseDate(string time, out DateTime result)
    {
        if (string.IsNullOrEmpty(time)) { result = default; return false; }
        return DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss",
                   null, DateTimeStyles.None, out result);
    }

    [Serializable]
    private class HistoryEntry
    {
        public string path;
        public string time;
    }

    [Serializable]
    private class HistoryData
    {
        public string[]       items;   // legacy — 旧版本兼容
        public HistoryEntry[] entries; // v2 — 含时间戳
    }

    // ════════════════ 辅助 ════════════════

    private void SetStatus(string msg, MessageType type)
    {
        _statusMsg  = msg;
        _statusType = type;
        Repaint();
    }
}
