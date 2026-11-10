using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using LabelTool;
using ProcGen;
using PrefabTools;
using ShadowBakerTool.Editor;
using SelfShadowBakerTool.Editor;
using FogOfWarTool.Editor;
using DayChangeTransitionTool.Editor;
using SLGTerrainBakerTool.Editor;
using SLGMapTileCutter.Editor;
using ZXXTools.FBXMeshEditor;
using ZXXTools.CardGroundBaker;
using ZXXTools.SLGCityEditor;
using RockTools;

public class ZXXToolBox : EditorWindow
{
    // ── Tool registry ──
    private static readonly (string id, string defaultDesc, string category, Action action)[] _registry =
    {
        ("FBX Mesh Editor",      "在 Unity 场景中直接选择并移动静态模型顶点，支持 Undo；当前为 V1 第一阶段", "资源制作", FBXMeshEditorWindow.Open),
        ("Rock Generator",       "组合内置或自定义石头 Mesh，生成并烘焙岩石模型",       "资源制作", RockGeneratorWindow.Open),
        ("SLG Map Tile Cutter", "根据线框在模型表面生成可编辑的独立 SLG 地块 Mesh", "场景搭建", SLGMapTileCutterWindow.Open),
        ("SLG City Editor",     "绘制主城网格区域，提取边界并自动生成模块化城墙",       "场景搭建", SLGCityEditorWindow.Open),
        ("ProcGen",              "在模型表面按规则散布预制体",                       "场景搭建",   ProcGenWindow.Open),
        ("Path Builder",         "沿路径生成墙体、山脉散布和可变宽河流",              "场景搭建",   PathBuilderWindow.ShowWindow),
        ("Building Card Prefab", "SLG 建筑 PNG：自动原点、面片和点击 BoxCollider",    "资源制作",   BuildingCardTool.ShowWindow),
        ("Box Collider Generator","批量为预制体生成 BoxCollider",                     "资源制作",   BoxColliderGenerator.ShowWindow),
        ("Shadow Baker",         "建筑顶光影子烘培 + 地面面片生成",                  "视觉烘焙",   ShadowBakerWindow.Open),
        ("Card Ground Baker",    "将摄像机朝向面片沿视线拍平到世界平面或 Collider 地表", "视觉烘焙", CardGroundBakerWindow.Open),
        ("Self Shadow Baker",    "建筑自投影阴影烘焙：uv2 展开 + PCF 软边 + 自动赋 _BakedMap", "视觉烘焙", SelfShadowBakerWindow.Open),
        ("SLG Terrain Baker",    "SLG 地貌制作材质：烘焙自定义 Mipmap + 生成手机运行材质", "视觉烘焙", SLGTerrainBakeWindow.Open),
        ("Fog Of War Tool",      "战争迷雾 RGBA Mask：R 探索 / G 选中 / B 溶解边缘", "场景搭建",   FogOfWarToolWindow.Open),
        ("Day Change Transition","SLG 跨天短夜晚过渡：一键创建 Night Volume + 主光过渡控制", "场景搭建",   DayChangeTransitionToolWindow.Open),
        ("Outline",              "Mesh 外描边：平滑法线烘焙 + Outline Shader",    "视觉烘焙",   OutlineToolWindow.Open),
        ("Toon Material Converter", "ToonOutline 普通/打包 Shader 双向转换，自动输出 BC 与 N_M_R 贴图", "资源制作", ToonMaterialConverterWindow.Open),
        ("Generated Assets Manager", "按工具、生命周期和资源类型整理、审计并安全清理 Generated 文件", "检查与输出", OpenGeneratedAssetsManager),
        ("Light Cookie Scroller","灯光 Cookie 纹理滚动动画：云影/焦散/斑驳光影流动",  "视觉烘焙",   AddLightCookieScrollerToSelected),
        ("Batch Replacer",                "在 Hierarchy 中批量替换场景物体",         "批量操作",   PrefabBatchReplacer.ShowWindow),
        ("Random Material",               "从自定义材质列表随机赋予场景对象或 Project Prefab", "批量操作", RandomMaterialWindow.Open),
        ("Overlap Spreader",              "将选中的重叠场景物体沿各组中心向外散开，直到地面投影不再重叠", "批量操作", OverlapSpreaderWindow.Open),
        ("Transparent Queue Sorter",       "在树状面板中手动排序半透明物体，并烘培指定范围内的独立队列材质", "批量操作", TransparentQueueSorterWindow.Open),
        ("Batch Rename Tool",             "批量重命名工具",                         "批量操作",   BatchRenameTool.Open),
        ("Sync Names",                    "同步物体名称到 Prefab（有选中→仅选中 / 无选中→整个场景）", "批量操作", MakeSelectedOrAll("Sync Names", PrefabNameSyncTool.Execute)),
        ("Cast Shadows OFF",              "关闭 Cast Shadows（有选中→仅选中 / 无选中→整个场景）", "批量操作", MakeSelectedOrAll("Cast Shadows OFF", ShadowCastOffTool.Execute)),
        ("Convert Variant To Normal Prefab","Prefab Variant 转换为普通 Prefab",      "资源制作", ConvertVariantToNormalPrefab.ConvertSelectedVariant),
        ("Prefab Extractor",              "从场景提取子物体为独立 Prefab",           "资源制作", PrefabExtractorTool.Open),
        ("Normalize Prefab Root",         "规范化根节点 Transform，重置为 identity  Ctrl+Shift+N", "资源制作", PrefabRootNormalizerWindow.ShowWindow),
        ("Transparent Screenshot",        "截取透明背景的 Game 视图截图  Ctrl+Shift+X","检查与输出", TransparentScreenshot.OpenWindow),
        ("Shading Mode",                  "切换选中模型的软硬边法线显示（非破坏性预览）", "视觉烘焙",   ShadingModeToggleWindow.Open),
        ("Label Tool",                    "场景预制体名称标签显示工具",               "检查与输出", LabelManagerWindow.Open),
        ("Measure Tool",                  "三维测量：包围盒尺寸 / 距离 / 标尺",          "检查与输出", MeasureTool.Open),
        ("Git Path Navigator",          "粘贴 Git 路径自动定位到 Project 窗口  Ctrl+G",  "检查与输出", GitPathNavigator.ShowWindow),
    };

    // ── Selected / All helper ──

    /// <summary>返回一个 Action：有选中物体→传选中 roots；无选中→弹窗确认后传场景 roots。</summary>
    private static Action MakeSelectedOrAll(string toolId, Action<GameObject[]> process)
    {
        return () =>
        {
            if (Selection.gameObjects != null && Selection.gameObjects.Length > 0)
            {
                process(Selection.gameObjects);
            }
            else
            {
                bool confirm = EditorPrefs.GetBool(ConfirmKey(toolId), true);
                if (!confirm || EditorUtility.DisplayDialog("确认操作",
                    $"当前【未选中任何物体】，将对【整个场景】执行「{toolId}」。\n\n确定继续？",
                    "确定", "取消"))
                {
                    var scene = SceneManager.GetActiveScene();
                    process(scene.GetRootGameObjects());
                }
            }
        };
    }

    private static string ConfirmKey(string toolId) => $"ZXXToolBox_Confirm_{toolId}";

    /// <summary>该工具是否支持「有选中→选中 / 无选中→全局」模式</summary>
    private static bool IsSelectedAllTool(string toolId)
    {
        return toolId == "Sync Names" || toolId == "Cast Shadows OFF";
    }

    private static readonly string[] _toolCatNames = { "场景搭建", "资源制作", "视觉烘焙", "批量操作", "检查与输出" };
    private static readonly string[] _catNames = { "场景搭建", "资源制作", "视觉烘焙", "批量操作", "检查与输出", "工作流", "使用统计" };
    private const int WorkflowTabIndex = 5;
    private const int UsageTabIndex = 6;
    private int _usageSortMode;

    private static Type FindLightCookieScrollerType()
    {
        return TypeCache.GetTypesDerivedFrom<MonoBehaviour>()
            .FirstOrDefault(type => type.Name == "LightCookieScroller");
    }

    private static void OpenGeneratedAssetsManager()
    {
        Type type = TypeCache.GetTypesDerivedFrom<EditorWindow>()
            .FirstOrDefault(candidate => candidate.FullName
                == "ZXXTools.GeneratedAssets.GeneratedAssetsManagerWindow");
        MethodInfo openMethod = type?.GetMethod(
            "Open", BindingFlags.Public | BindingFlags.Static);
        if (openMethod != null)
        {
            openMethod.Invoke(null, null);
            return;
        }
        EditorUtility.DisplayDialog(
            "Generated Assets Manager",
            "管理器脚本尚未完成编译，请等待 Unity 编译结束后重试。",
            "确定");
    }

    /// <summary>
    /// ArtCode 是独立运行时程序集，Unity.Editor 没有直接引用它。
    /// 通过类型缓存调用其编辑器方法，避免工具箱依赖一个可见的顶部菜单字符串。
    /// </summary>
    private static void AddLightCookieScrollerToSelected()
    {
        var componentType = FindLightCookieScrollerType();
        var addMethod = componentType?.GetMethod(
            "AddToSelectedLights",
            BindingFlags.Public | BindingFlags.Static);

        if (addMethod == null)
        {
            EditorUtility.DisplayDialog(
                "Light Cookie Scroller",
                "未找到 LightCookieScroller.AddToSelectedLights，请确认 ArtCode 程序集已成功编译。",
                "确定");
            return;
        }

        try
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            var targets = Selection.gameObjects.Where(go => go.GetComponent<Light>() != null
                && go.GetComponent(componentType) == null).ToArray();
            addMethod.Invoke(null, null);
            ZXXToolUsageStats.Complete("Light Cookie Scroller",
                targets.Count(go => go != null && go.GetComponent(componentType) != null), usageTimer);
        }
        catch (TargetInvocationException exception)
        {
            Debug.LogException(exception.InnerException ?? exception);
        }
    }

    // ── Runtime data (loaded from EditorPrefs) ──
    private List<ToolEntry> _tools;
    private bool _editMode;

    private class ToolEntry
    {
        public string id;
        public string desc;
        public string category;
        public Action action;
        public bool visible = true;
        public bool confirmOnAll = true; // 无选中时操作前是否弹窗确认
    }

    private Vector2 _scroll;
    private Vector2 _wfScroll;
    private int _selectedCat;

    // Workflow state
    private List<WorkflowDef> _workflows = new List<WorkflowDef>();
    private int _editingWfIdx = -1;
    private bool _wfCreating;
    private string _wfNewName = "";

    // ════════════════ Dynamic Menu ════════════════

    private const string MenuBasePath = "ArtTools/";
    private static string _currentMenuPath = null;

    /// <summary>注册或刷新 Unity 顶部菜单项，使菜单名称跟随显示名称。</summary>
    private static void RefreshMenu(string newDisplayName)
    {
        try
        {
            var menuType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Menu");
            if (menuType == null) return;

            var addMethod = menuType.GetMethod("AddMenuItem",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new Type[] { typeof(string), typeof(string), typeof(bool), typeof(int), typeof(Action), typeof(Func<bool>) },
                null);
            var removeMethod = menuType.GetMethod("RemoveMenuItem",
                BindingFlags.Static | BindingFlags.NonPublic, null,
                new Type[] { typeof(string) }, null);

            if (addMethod == null || removeMethod == null) return;

            // Remove old menu item
            if (_currentMenuPath != null)
            {
                removeMethod.Invoke(null, new object[] { _currentMenuPath });
            }

            // Add new menu item with current display name
            string newPath = MenuBasePath + newDisplayName;
            addMethod.Invoke(null, new object[] { newPath, "", false, 200, (Action)OpenFromMenu, (Func<bool>)null });
            _currentMenuPath = newPath;
        }
        catch
        {
            // Internal API may differ between Unity versions; fallback silently
        }
    }

    private static void OpenFromMenu()
    {
        var config = LoadOrCreateConfig();
        var w = GetWindow<ZXXToolBox>(config.displayName);
        w.minSize = Vector2.zero;
    }

    [InitializeOnLoadMethod]
    private static void InitMenu()
    {
        EditorApplication.delayCall += () =>
        {
            var config = LoadOrCreateConfig();
            RefreshMenu(config.displayName);
        };
    }

    public static void Open()
    {
        OpenFromMenu();
    }

    private void OnEnable()
    {
        wantsMouseMove = true; // 高频鼠标跟踪，悬停高亮即时响应
        _displayName = LoadOrCreateConfig().displayName;
        LoadSettings();
        LoadWorkflows();
        // 延迟到 Unity 布局系统初始化完成后再取消最小尺寸
        EditorApplication.delayCall += RemoveMinSize;
    }

    private void RemoveMinSize()
    {
        minSize = new Vector2(0, 0);
    }

    // ════════════════ Persistence ════════════════

    private const string PrefKey = "ZXXToolBox_Settings";
    private const string ConfigDefaultName = "ZXX ToolBox";
    private const int PrefVersion = 2; // bump when defaults change

    private string _displayName;

    /// <summary>加载配置文件（按类型查找，不写死路径）；不存在则自动创建在脚本同目录。</summary>
    private static ZXXToolBoxConfig LoadOrCreateConfig()
    {
        // 按类型查找，任意位置都能找到
        var guids = AssetDatabase.FindAssets("t:ZXXToolBoxConfig");
        if (guids.Length > 0)
            return AssetDatabase.LoadAssetAtPath<ZXXToolBoxConfig>(AssetDatabase.GUIDToAssetPath(guids[0]));

        // 不存在 → 在脚本同目录创建
        var temp = ScriptableObject.CreateInstance<ZXXToolBoxConfig>();
        var script = MonoScript.FromScriptableObject(temp);
        var scriptDir = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(script)).Replace('\\', '/');
        DestroyImmediate(temp);

        var config = ScriptableObject.CreateInstance<ZXXToolBoxConfig>();
        config.displayName = ConfigDefaultName;
        AssetDatabase.CreateAsset(config, scriptDir + "/ZXXToolBoxConfig.asset");
        AssetDatabase.SaveAssets();
        return config;
    }

    private void LoadSettings()
    {
        _tools = _registry.Select((r, idx) => new ToolEntry
        {
            id = r.id,
            desc = r.defaultDesc,
            category = r.category,
            action = r.action,
        }).ToList();

        var config = LoadOrCreateConfig();

        // ── 共享设置：从配置文件加载分类 / 描述 / 可见性 ──
        // 首次自动迁移：config 为空但 EditorPrefs 有旧数据 → 同步到 config
        string json = EditorPrefs.GetString(PrefKey, "");
        if (config.toolOverrides.Count == 0 && !string.IsNullOrEmpty(json))
        {
            try
            {
                var legacy = JsonUtility.FromJson<ToolSettings>(json);
                if (legacy?.entries != null)
                {
                    foreach (var se in legacy.entries)
                        config.toolOverrides.Add(new ToolOverride { id = se.id, desc = se.desc, category = se.category, visible = se.visible });
                    EditorUtility.SetDirty(config);
                }
            }
            catch { }
        }

        // 应用配置覆盖
        foreach (var ov in config.toolOverrides)
        {
            var match = _tools.Find(t => t.id == ov.id);
            if (match == null) continue;
            if (!string.IsNullOrEmpty(ov.desc))     match.desc     = ov.desc;
            if (!string.IsNullOrEmpty(ov.category)) match.category = ov.category;
            match.visible = ov.visible;
        }

        // ── 个人设置：从 EditorPrefs 加载排序 / 确认弹窗 ──
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                var saved = JsonUtility.FromJson<ToolSettings>(json);
                if (saved?.entries != null && saved.version == PrefVersion)
                {
                    var reordered = new List<ToolEntry>();
                    foreach (var se in saved.entries)
                    {
                        var match = _tools.Find(t => t.id == se.id);
                        if (match != null) { match.confirmOnAll = se.confirmOnAll; reordered.Add(match); }
                    }
                    foreach (var t in _tools)
                        if (!reordered.Exists(r => r.id == t.id))
                            reordered.Add(t);
                    _tools = reordered;
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        // 共享设置 → 配置文件
        var config = LoadOrCreateConfig();
        config.displayName = _displayName;
        config.toolOverrides.Clear();
        foreach (var t in _tools)
            config.toolOverrides.Add(new ToolOverride { id = t.id, desc = t.desc, category = t.category, visible = t.visible });
        EditorUtility.SetDirty(config);

        // 个人设置 → EditorPrefs
        var s = new ToolSettings
        {
            version = PrefVersion,
            entries = _tools.Select(t => new SavedEntry
            {
                id = t.id,
                confirmOnAll = t.confirmOnAll,
            }).ToList()
        };
        EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));

        // 这两个开关会在实际执行批处理时读取；只在确认保存编辑时写入，取消编辑不会留下副作用。
        foreach (var t in _tools)
        {
            if (IsSelectedAllTool(t.id))
                EditorPrefs.SetBool(ConfirmKey(t.id), t.confirmOnAll);
        }
    }

    [Serializable]
    private class ToolSettings { public int version; public List<SavedEntry> entries; }
    [Serializable]
    private class SavedEntry
    {
        public string id;
        public string desc;
        public string category;
        public bool visible = true;
        public bool confirmOnAll = true;
    }

    // ── Workflow migration helper (旧 EditorPrefs 数据迁到配置文件后删除) ──
    [Serializable]
    private class WorkflowListData { public List<WorkflowDef> workflows; }

    // ════════════════ GUI ════════════════

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    private void OnGUI()
    {
        // 防护：OnEnable 可能还没跑完就被 Unity 调用 OnGUI
        if (_tools == null) LoadSettings();
        if (string.IsNullOrEmpty(_displayName)) _displayName = LoadOrCreateConfig().displayName;

        // 鼠标移动时立即重绘，让悬停高亮跟手
        if (Event.current.type == EventType.MouseMove) Repaint();

        // Keyboard shortcuts
        Event e = Event.current;
        if (e != null && e.type == EventType.KeyDown && e.control)
        {
            if (e.shift && e.keyCode == KeyCode.N)
            {
                PrefabRootNormalizer.WrapSelected();
                e.Use();
                return;
            }
            if (e.shift && e.keyCode == KeyCode.X)
            {
                TransparentScreenshot.OpenWindow();
                e.Use();
                return;
            }
        }

        // Height mode check
        if (_editMode && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            CancelEdit();
            e.Use();
        }

        if (_editMode)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("工具箱名称", GUILayout.Width(70));
            var newName = EditorGUILayout.TextField(_displayName);
            if (newName != _displayName)
            {
                _displayName = newName;
                titleContent = new GUIContent(_displayName);
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(4);

        // ── 标签栏 + 设置 ──
        EditorGUILayout.BeginHorizontal();
        _selectedCat = GUILayout.Toolbar(_selectedCat, _catNames, GUILayout.Height(20));
        GUI.backgroundColor = _editMode ? new Color(1f, 0.9f, 0.6f) : Color.white;
        var gearIcon = EditorGUIUtility.IconContent("d_Settings");
        gearIcon.tooltip = _editMode ? "退出编辑模式" : "编辑模式：排序、描述、分类、可见性";
        var gearStyle = new GUIStyle(GUI.skin.button) { padding = new RectOffset(1, 1, 1, 1) };
        if (GUILayout.Button(gearIcon, gearStyle, GUILayout.Width(24), GUILayout.Height(20)))
            ToggleEditMode();
        GUI.backgroundColor = Color.white;
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(6);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        if (_selectedCat == UsageTabIndex)
        {
            ZXXToolUsageStats.Draw(_registry.Select(tool => tool.id).ToArray(), ref _usageSortMode);
        }
        else if (_selectedCat == WorkflowTabIndex)
        {
            DrawWorkflowTab();
        }
        else
        {
            var catTools = _tools.Where(t => t.category == _catNames[_selectedCat]).ToList();
            for (int i = 0; i < catTools.Count; i++)
            {
                var tool = catTools[i];
                if (!_editMode && !tool.visible) continue;

                EditorGUILayout.BeginVertical("box");

                if (_editMode)
                {
                    int idx = _tools.IndexOf(tool);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(tool.id, EditorStyles.boldLabel, GUILayout.Width(200));
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(idx == 0))
                        if (GUILayout.Button(new GUIContent("▲", "上移"), GUILayout.Width(25), GUILayout.Height(18)))
                            SwapTools(idx, idx - 1);
                    using (new EditorGUI.DisabledScope(idx == _tools.Count - 1))
                        if (GUILayout.Button(new GUIContent("▼", "下移"), GUILayout.Width(25), GUILayout.Height(18)))
                            SwapTools(idx, idx + 1);
                    tool.visible = GUILayout.Toggle(tool.visible, new GUIContent(tool.visible ? "可见" : "隐藏", "在工具箱中显示/隐藏此工具"), "Button", GUILayout.Width(50), GUILayout.Height(18));
                    EditorGUILayout.EndHorizontal();

                    tool.desc = EditorGUILayout.TextField(tool.desc);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("分类", GUILayout.Width(30));
                    int catIdx = Array.IndexOf(_toolCatNames, tool.category);
                    if (catIdx < 0) catIdx = 0;
                    catIdx = EditorGUILayout.Popup(catIdx, _toolCatNames, GUILayout.Width(100));
                    if (catIdx >= 0 && catIdx < _toolCatNames.Length && _toolCatNames[catIdx] != tool.category)
                        tool.category = _toolCatNames[catIdx];
                    EditorGUILayout.EndHorizontal();

                    // 选中/全局模式工具：弹窗确认开关
                    if (IsSelectedAllTool(tool.id))
                    {
                        EditorGUILayout.BeginHorizontal();
                        tool.confirmOnAll = EditorGUILayout.ToggleLeft("无选中时弹窗确认", tool.confirmOnAll);
                        EditorGUILayout.EndHorizontal();
                    }

                }
                else
                {
                    // ── 整行可点击 ──
                    var bgColor = GUI.color;
                    float rowW = EditorGUIUtility.currentViewWidth - 22;
                    float idH2 = EditorStyles.boldLabel.CalcHeight(new GUIContent(tool.id), rowW);
                    float descH2 = EditorStyles.wordWrappedMiniLabel.CalcHeight(new GUIContent(tool.desc), rowW);
                    var boxRect = GUILayoutUtility.GetRect(new GUIContent(""), GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.Height(idH2 + descH2 + 12));

                    // 悬停检测
                    bool hover = boxRect.Contains(Event.current.mousePosition);
                    EditorGUIUtility.AddCursorRect(boxRect, MouseCursor.Link);

                    // 悬停 tooltip
                    if (hover) GUI.tooltip = tool.desc;

                    if (hover && Event.current.type == EventType.Repaint)
                        GUI.color = new Color(0.78f, 0.87f, 1f, 1f);  // 悬停浅蓝
                    GUI.Box(boxRect, "", GUI.skin.box);
                    GUI.color = bgColor;

                    // 内容
                    var inner = new Rect(boxRect.x + 6, boxRect.y + 3, boxRect.width - 12, boxRect.height - 6);
                    EditorGUI.LabelField(new Rect(inner.x, inner.y, inner.width, idH2), tool.id, EditorStyles.boldLabel);
                    EditorGUI.LabelField(new Rect(inner.x, inner.y + idH2 + 2, inner.width, descH2), tool.desc, EditorStyles.wordWrappedMiniLabel);

                    // 点击触发
                    if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && hover)
                    {
                        InvokeTool(tool.id, tool.action);
                        Event.current.Use();
                    }
                }

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.EndScrollView();

        if (_selectedCat != WorkflowTabIndex && _selectedCat != UsageTabIndex && _editMode)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("保存并退出", "保存当前编辑并退出编辑模式"), GUILayout.Height(28)))
                ToggleEditMode();
            if (GUILayout.Button(new GUIContent("取消", "放弃修改并退出编辑模式"), GUILayout.Height(28)))
                CancelEdit();
            if (GUILayout.Button(new GUIContent("恢复默认", "恢复所有工具到默认排序和描述"), GUILayout.Height(28)))
                ResetToDefaults();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
            if (GUILayout.Button("生成说明书截图", GUILayout.Height(26)))
                CaptureHelper.Run();
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("编辑模式：拖动 ▲▼ 排序，修改描述，切换可见/隐藏，调整分类。\nEsc 取消修改。", MessageType.Info);
        }

        // ── 署名（低调灰字，右对齐） ──
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    private static void InvokeTool(string id, Action action)
    {
        ZXXToolUsageStats.RecordCall(id);
        action();
    }

    private void ToggleEditMode()
    {
        if (_editMode)
        {
            SaveSettings();
            titleContent = new GUIContent(_displayName);
            RefreshMenu(_displayName);
            EditorApplication.delayCall += () => AssetDatabase.SaveAssets();
        }
        _editMode = !_editMode;
        if (!_editMode) Repaint();
    }

    private void CancelEdit()
    {
        _displayName = LoadOrCreateConfig().displayName;
        titleContent = new GUIContent(_displayName);
        RefreshMenu(_displayName);
        LoadSettings();
        _editMode = false;
    }

    private void SwapTools(int a, int b)
    {
        var tmp = _tools[a];
        _tools[a] = _tools[b];
        _tools[b] = tmp;
    }

    private void ResetToDefaults()
    {
        EditorPrefs.DeleteKey(PrefKey);
        foreach (var (id, _, _, _) in _registry)
        {
            if (IsSelectedAllTool(id))
                EditorPrefs.DeleteKey(ConfirmKey(id));
        }
        var config = LoadOrCreateConfig();
        config.displayName = ConfigDefaultName;
        config.toolOverrides.Clear();
        EditorUtility.SetDirty(config);
        EditorApplication.delayCall += () => AssetDatabase.SaveAssets();
        _displayName = ConfigDefaultName;
        titleContent = new GUIContent(_displayName);
        RefreshMenu(_displayName);
        LoadSettings();
    }

    // ════════════════ Workflow ════════════════

    private void LoadWorkflows()
    {
        var config = LoadOrCreateConfig();

        // 一次性迁移：旧 EditorPrefs 数据 → 配置文件
        if (config.workflows.Count == 0)
        {
            string json = EditorPrefs.GetString("ZXXToolBox_Workflows", "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var data = JsonUtility.FromJson<WorkflowListData>(json);
                    if (data?.workflows != null && data.workflows.Count > 0)
                    {
                        config.workflows = data.workflows;
                        EditorUtility.SetDirty(config);
                        EditorPrefs.DeleteKey("ZXXToolBox_Workflows");
                    }
                }
                catch { }
            }
        }

        _workflows = config.workflows;
    }

    private void SaveWorkflows()
    {
        EditorUtility.SetDirty(LoadOrCreateConfig());
        EditorApplication.delayCall += () => AssetDatabase.SaveAssets();
    }

    private void DrawWorkflowTab()
    {
        EditorGUILayout.LabelField("自定义工作流", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("将多个工具串联为工作流，按步骤引导执行。", MessageType.None);
        EditorGUILayout.Space(4);

        // ── Create new ──
        if (_wfCreating)
        {
            EditorGUILayout.BeginHorizontal();
            _wfNewName = EditorGUILayout.TextField("工作流名称", _wfNewName);
            if (GUILayout.Button(new GUIContent("确认创建", "创建新的工作流"), GUILayout.Width(80)) && !string.IsNullOrWhiteSpace(_wfNewName))
            {
                _workflows.Add(new WorkflowDef { name = _wfNewName.Trim() });
                _editingWfIdx = _workflows.Count - 1;
                _wfCreating = false;
                _wfNewName = "";
                SaveWorkflows();
            }
            if (GUILayout.Button(new GUIContent("取消", "取消创建工作流"), GUILayout.Width(50)))
            {
                _wfCreating = false;
                _wfNewName = "";
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }
        else
        {
            if (GUILayout.Button(new GUIContent("+ 新建工作流", "创建一个新的自定义工作流"), GUILayout.Height(26)))
                _wfCreating = true;
            EditorGUILayout.Space(4);
        }

        if (_workflows.Count == 0)
        {
            EditorGUILayout.HelpBox("还没有工作流，点击上方按钮创建一个。", MessageType.Info);
            return;
        }

        _wfScroll = EditorGUILayout.BeginScrollView(_wfScroll, GUILayout.ExpandHeight(true));

        for (int w = 0; w < _workflows.Count; w++)
        {
            var wf = _workflows[w];
            bool expanded = _editingWfIdx == w;

            EditorGUILayout.BeginVertical("box");

            // Header
            EditorGUILayout.BeginHorizontal();
            expanded = EditorGUILayout.Foldout(expanded, wf.name, true);
            if (expanded != (_editingWfIdx == w))
                _editingWfIdx = expanded ? w : -1;
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(new GUIContent("运行", "按步骤引导执行此工作流"), GUILayout.Width(50), GUILayout.Height(20)))
                WorkflowRunner.Open(wf);
            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button(new GUIContent("删除", "删除此工作流"), GUILayout.Width(40), GUILayout.Height(20)))
            {
                if (EditorUtility.DisplayDialog("删除工作流", $"确定删除「{wf.name}」？", "删除", "取消"))
                {
                    _workflows.RemoveAt(w);
                    _editingWfIdx = -1;
                    SaveWorkflows();
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // Step list (when expanded)
            if (expanded)
            {
                EditorGUILayout.Space(2);

                // Rename
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("名称", GUILayout.Width(30));
                wf.name = EditorGUILayout.TextField(wf.name);
                if (GUILayout.Button("保存", GUILayout.Width(50)))
                    SaveWorkflows();
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4);

                // Steps
                if (wf.steps.Count == 0)
                    EditorGUILayout.HelpBox("还没有步骤，点击下方按钮添加。", MessageType.None);

                int removeStep = -1;
                for (int s = 0; s < wf.steps.Count; s++)
                {
                    var step = wf.steps[s];
                    EditorGUILayout.BeginHorizontal();

                    string icon = step.stepType == "manual" ? "✋" : "⚙";
                    string label = step.stepType == "manual" ? step.label : step.toolId;
                    EditorGUILayout.LabelField($"{icon} {s + 1}. {label}", GUILayout.ExpandWidth(true));

                    using (new EditorGUI.DisabledScope(s == 0))
                        if (GUILayout.Button("▲", GUILayout.Width(25), GUILayout.Height(18)))
                        {
                            var tmp = wf.steps[s];
                            wf.steps[s] = wf.steps[s - 1];
                            wf.steps[s - 1] = tmp;
                            SaveWorkflows();
                        }
                    using (new EditorGUI.DisabledScope(s == wf.steps.Count - 1))
                        if (GUILayout.Button("▼", GUILayout.Width(25), GUILayout.Height(18)))
                        {
                            var tmp = wf.steps[s];
                            wf.steps[s] = wf.steps[s + 1];
                            wf.steps[s + 1] = tmp;
                            SaveWorkflows();
                        }
                    if (GUILayout.Button("✕", GUILayout.Width(25), GUILayout.Height(18)))
                        removeStep = s;

                    EditorGUILayout.EndHorizontal();
                }
                if (removeStep >= 0)
                {
                    wf.steps.RemoveAt(removeStep);
                    SaveWorkflows();
                }

                EditorGUILayout.Space(4);

                // Add step
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("+ 添加手动步骤", "添加一个手动操作步骤（显示提示文字）")))
                {
                    wf.steps.Add(new WorkflowStepData { stepType = "manual", label = "新步骤" });
                    SaveWorkflows();
                }
                if (GUILayout.Button(new GUIContent("+ 添加工具步骤", "添加一个打开工具窗口并配置参数的步骤")))
                {
                    wf.steps.Add(new WorkflowStepData { stepType = "tool", toolId = _registry.Length > 0 ? _registry[0].id : "" });
                    SaveWorkflows();
                }
                EditorGUILayout.EndHorizontal();

                // Edit manual step labels
                for (int s = 0; s < wf.steps.Count; s++)
                {
                    var step = wf.steps[s];
                    if (step.stepType == "manual")
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"  步骤{s + 1}文字", GUILayout.Width(70));
                        string newLabel = EditorGUILayout.TextField(step.label);
                        if (newLabel != step.label)
                        {
                            step.label = newLabel;
                            SaveWorkflows();
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    else
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"  步骤{s + 1}工具", GUILayout.Width(70));
                        int curIdx = -1;
                        string[] ids = new string[_registry.Length];
                        for (int r = 0; r < _registry.Length; r++)
                        {
                            ids[r] = _registry[r].id;
                            if (_registry[r].id == step.toolId) curIdx = r;
                        }
                        int newIdx = EditorGUILayout.Popup(curIdx, ids);
                        if (newIdx != curIdx && newIdx >= 0)
                        {
                            step.toolId = ids[newIdx];
                            SaveWorkflows();
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }

    // ════════════════ Workflow Runner ════════════════

    private class WorkflowRunner : EditorWindow
    {
        private WorkflowDef _wf;
        private int _stepIdx;
        private string _backupJson; // global EditorPrefs value before entering current tool step

        // Tool ID → EditorPrefs key for settings snapshot
        private static readonly Dictionary<string, string> _toolPrefKeys = new Dictionary<string, string>
        {
            { "Shadow Baker", "ShadowBaker.Settings" },
            { "Box Collider Generator", "BoxColliderGenerator.Settings" },
        };

        public static void Open(WorkflowDef wf)
        {
            var win = GetWindow<WorkflowRunner>($"工作流: {wf.name}");
            win.minSize = new Vector2(360, 260);
            win._wf = wf;
            win._stepIdx = 0;
            win._backupJson = null;
            win.ApplyStepSettings(0);
            win.Show();
        }

        private void OnDisable()
        {
            RestoreBackup();
        }

        private void OnGUI()
        {
            if (_wf == null) { Close(); return; }

            EditorGUILayout.LabelField($"工作流: {_wf.name}", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);

            if (_wf.steps.Count == 0)
            {
                EditorGUILayout.HelpBox("工作流没有步骤。", MessageType.Info);
                return;
            }

            // Guard against step index out of range (user may have edited workflow)
            if (_stepIdx >= _wf.steps.Count)
                _stepIdx = _wf.steps.Count - 1;

            float progress = (float)(_stepIdx + 1) / _wf.steps.Count;
            var rect = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(rect, progress, $"步骤 {_stepIdx + 1} / {_wf.steps.Count}");

            EditorGUILayout.Space(8);

            var step = _wf.steps[_stepIdx];

            EditorGUILayout.BeginVertical("box");
            if (step.stepType == "manual")
            {
                EditorGUILayout.LabelField("✋ 手动步骤", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(step.label, MessageType.None);
                EditorGUILayout.HelpBox("完成以上操作后，点击下方「下一步 ▶」继续。", MessageType.Warning);
            }
            else
            {
                var toolDesc = "";
                foreach (var (id, desc, _, _) in _registry)
                    if (id == step.toolId) { toolDesc = desc; break; }
                EditorGUILayout.LabelField($"⚙ 工具步骤: {step.toolId}", EditorStyles.boldLabel);
                if (!string.IsNullOrEmpty(toolDesc))
                    EditorGUILayout.LabelField(toolDesc, EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(4);
                var action = FindToolAction(step.toolId);
                using (new EditorGUI.DisabledScope(action == null))
                {
                    if (GUILayout.Button(new GUIContent("打开工具窗口", "打开对应工具窗口进行参数配置"), GUILayout.Height(30)))
                        InvokeTool(step.toolId, action);
                }
                string hasSaved = string.IsNullOrEmpty(step.settingsJson)
                    ? "尚未保存过设置。"
                    : "已有保存的设置，打开工具时会自动加载。";
                EditorGUILayout.HelpBox($"在打开的工具窗口中配置参数并完成操作，然后回到此窗口点击「下一步 ▶」。\n{hasSaved}", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space(8);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_stepIdx == 0))
                if (GUILayout.Button(new GUIContent("◀ 上一步", "返回上一个步骤"), GUILayout.Height(28)))
                    GoToStep(_stepIdx - 1);
            using (new EditorGUI.DisabledScope(_stepIdx >= _wf.steps.Count - 1))
                if (GUILayout.Button(new GUIContent("下一步 ▶", "进入下一个步骤"), GUILayout.Height(28)))
                    GoToStep(_stepIdx + 1);
            EditorGUILayout.EndHorizontal();

            if (_stepIdx >= _wf.steps.Count - 1)
            {
                EditorGUILayout.Space(4);
                GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
                if (GUILayout.Button(new GUIContent("完成工作流", "结束工作流并关闭此窗口"), GUILayout.Height(30)))
                {
                    CaptureStepSettings(_stepIdx);
                    RestoreBackup();
                    Close();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        // ── Navigation with settings snapshot ──
        private void GoToStep(int newIdx)
        {
            CaptureStepSettings(_stepIdx);
            _stepIdx = newIdx;
            ApplyStepSettings(_stepIdx);
        }

        private void ApplyStepSettings(int idx)
        {
            var step = _wf.steps[idx];
            if (step.stepType != "tool") return;

            string prefKey = GetPrefKey(step.toolId);
            if (prefKey == null) return;

            // Restore any previous leaked backup first
            RestoreBackup();

            // Backup current global value
            _backupJson = EditorPrefs.GetString(prefKey, "");

            // Apply workflow step's saved settings
            if (!string.IsNullOrEmpty(step.settingsJson))
                EditorPrefs.SetString(prefKey, step.settingsJson);
        }

        private void CaptureStepSettings(int idx)
        {
            var step = _wf.steps[idx];
            if (step.stepType != "tool") return;

            string prefKey = GetPrefKey(step.toolId);
            if (prefKey == null) return;

            // Flush tool window's in-memory state to EditorPrefs
            CallSaveSettings(step.toolId);

            // Capture current settings to the step
            step.settingsJson = EditorPrefs.GetString(prefKey, "");

            // Restore original global value
            RestoreBackup();
        }

        private void RestoreBackup()
        {
            if (_backupJson == null || _wf == null) return;

            if (_stepIdx < _wf.steps.Count)
            {
                var step = _wf.steps[_stepIdx];
                string prefKey = GetPrefKey(step.toolId);
                if (prefKey != null)
                    EditorPrefs.SetString(prefKey, _backupJson);
            }
            _backupJson = null;
        }

        private static string GetPrefKey(string toolId)
        {
            _toolPrefKeys.TryGetValue(toolId, out var key);
            return key;
        }

        private static void CallSaveSettings(string toolId)
        {
            switch (toolId)
            {
                case "Shadow Baker":
                    if (HasOpenInstances<ShadowBakerWindow>())
                        GetWindow<ShadowBakerWindow>().SaveSettings();
                    break;
                case "Box Collider Generator":
                    if (HasOpenInstances<BoxColliderGenerator>())
                        GetWindow<BoxColliderGenerator>().SaveSettings();
                    break;
            }
        }

        private static Action FindToolAction(string toolId)
        {
            foreach (var (id, _, _, action) in _registry)
                if (id == toolId) return action;
            return null;
        }
    }
}
