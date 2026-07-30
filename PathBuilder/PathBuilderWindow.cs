using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using ZXXTools.PathBuilder;

public class PathBuilderWindow : EditorWindow
{
    private enum BuilderTab { Wall, Mountain, River }
    private enum BuildMode { Enclosure, LShape, Corridor, Line }

    [SerializeField] private BuilderTab activeTab;

    // ── Prefab ──
    private GameObject wallPrefab;
    private GameObject firstPrefab;
    private GameObject cornerPrefab;
    private bool useDifferentFirst;

    // ── Mode ──
    private BuildMode buildMode = BuildMode.Enclosure;

    // ── Wall: size & segments ──
    private float wallLength = 10f;
    private float wallDepth  = 8f;
    private float segLength = 2f;
    private int segsLong;
    private int segsShort;
    private bool autoSegment = true;
    private bool faceInward;
    private bool useCorners;

    // ── Line: queue ──
    private int lineCount = 5;
    private float lineSpacing = 2f;
    private int lineAxis;       // 0=X, 1=Y, 2=Z
    private bool linePositive = true;

    // ── Common ──
    private Vector3 objectScale = Vector3.one;
    private Vector3 lineRotation = Vector3.zero;
    private Vector3 centerPos = Vector3.zero;
    private string rootName = "WallEnclosure";

    private Vector2 scroll;

    public static void ShowWindow()
    {
        var w = GetWindow<PathBuilderWindow>("Path Builder");
        w.minSize = new Vector2(420, 560);
        w.Show();
    }

    public static PathBuilderWindow Open(PathBuildMode mode)
    {
        PathBuilderWindow window = GetWindow<PathBuilderWindow>("Path Builder");
        window.minSize = new Vector2(420, 560);
        window.SelectSplineMode(mode);
        window.Show();
        return window;
    }

    private void OnEnable()
    {
        RecalcWallSegments();
        EnableSplineUI();
    }

    private void OnDisable()
    {
        DisableSplineUI();
    }

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    // ════════════════ GUI ════════════════

    private void OnGUI()
    {
        DrawMainTabs();

        if (activeTab == BuilderTab.Wall)
            DrawWallWorkspace();
        else
            DrawSplineWorkspace();
    }

    private void DrawMainTabs()
    {
        int selected = GUILayout.Toolbar(
            (int)activeTab,
            PathBuilderSplineUI.MainTabs,
            GUILayout.Height(28f));
        BuilderTab next = (BuilderTab)selected;
        if (next == activeTab)
            return;

        if (next == BuilderTab.Wall)
        {
            activeTab = next;
            Repaint();
            return;
        }

        SelectSplineMode(
            next == BuilderTab.Mountain
                ? PathBuildMode.Mountain
                : PathBuildMode.River);
    }

    private void DrawWallWorkspace()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        bool isLine = buildMode == BuildMode.Line;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Wall", EditorStyles.boldLabel);
        buildMode = (BuildMode)EditorGUILayout.EnumPopup(Content("Build Mode", "生成模式"), buildMode);
        EditorGUILayout.Space(4);

        DrawPrefabSection(isLine);
        EditorGUILayout.Space(4);

        if (isLine) DrawLineSettings();
        else        DrawWallSettings();
        EditorGUILayout.Space(4);

        DrawCommonOptions(isLine);

        EditorGUILayout.EndScrollView();

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            DrawBuildSection(isLine);
        DrawAuthorFooter();
    }

    private static void DrawAuthorFooter()
    {
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    // ── Prefab ──

    private void DrawPrefabSection(bool isLine)
    {
        EditorGUILayout.LabelField(isLine ? Content("Object", "物体") : Content("Wall Segments", "墙段"), EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (isLine)
            {
                useDifferentFirst = EditorGUILayout.Toggle(Content("Use Different First Prefab", "首项使用不同 Prefab"), useDifferentFirst);
                if (useDifferentFirst)
                {
                    firstPrefab = (GameObject)EditorGUILayout.ObjectField(Content("First Prefab", "首个 Prefab"), firstPrefab, typeof(GameObject), true);
                    wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Rest Prefab", "其余 Prefab"), wallPrefab, typeof(GameObject), true);
                }
                else
                {
                    wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Object Prefab", "物体 Prefab"), wallPrefab, typeof(GameObject), true);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Wall Segment Prefab", "墙段 Prefab"), wallPrefab, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) RecalcWallSegments();

                if (buildMode == BuildMode.Enclosure)
                    cornerPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Corner Prefab (Optional)", "转角 Prefab（可选）"), cornerPrefab, typeof(GameObject), true);
            }
        }
    }

    // ── Line Settings ──

    private void DrawLineSettings()
    {
        EditorGUILayout.LabelField(Content("Queue Settings", "队列设置"), EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Count", "数量"), GUILayout.Width(70));
            lineCount = EditorGUILayout.IntField(lineCount);
            if (lineCount < 1) lineCount = 1;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Spacing", "间距"), GUILayout.Width(70));
            lineSpacing = EditorGUILayout.FloatField(lineSpacing);
            if (lineSpacing < 0.01f) lineSpacing = 0.01f;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Axis", "轴向"), GUILayout.Width(70));
            if (GUILayout.Toggle(lineAxis == 0, "X", "Button", GUILayout.Width(30))) lineAxis = 0;
            if (GUILayout.Toggle(lineAxis == 1, "Y", "Button", GUILayout.Width(30))) lineAxis = 1;
            if (GUILayout.Toggle(lineAxis == 2, "Z", "Button", GUILayout.Width(30))) lineAxis = 2;
            GUILayout.Space(10);
            EditorGUILayout.LabelField(Content("Dir", "朝向"), GUILayout.Width(30));
            linePositive = EditorGUILayout.Toggle(linePositive, GUILayout.Width(20));
            EditorGUILayout.LabelField(linePositive ? "+" : "−", GUILayout.Width(15));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField($"Total Length: {lineSpacing * (lineCount - 1):F2}m", EditorStyles.miniLabel);
        }
    }

    // ── Wall Settings ──

    private void DrawWallSettings()
    {
        bool isEnclosure = buildMode == BuildMode.Enclosure;
        bool isCorridor  = buildMode == BuildMode.Corridor;

        // Size
        EditorGUILayout.LabelField(Content("Wall Size", "围墙尺寸"), EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Length (X)", "长边 (X)"), GUILayout.Width(70));
            wallLength = EditorGUILayout.FloatField(wallLength);
            EditorGUILayout.EndHorizontal();

            if (!isCorridor)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(Content("Depth (Z)", "宽边 (Z)"), GUILayout.Width(70));
                wallDepth = EditorGUILayout.FloatField(wallDepth);
                EditorGUILayout.EndHorizontal();
            }

            if (wallLength < 0.1f) wallLength = 0.1f;
            if (wallDepth  < 0.1f) wallDepth  = 0.1f;
            if (EditorGUI.EndChangeCheck()) RecalcWallSegments();
        }

        EditorGUILayout.Space(4);

        // Segments
        EditorGUILayout.LabelField(Content("Segment Settings", "墙段设置"), EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUI.BeginChangeCheck();
            autoSegment = EditorGUILayout.Toggle(Content("Auto Calculate Segments", "自动计算段数"), autoSegment);

            if (autoSegment)
            {
                EditorGUI.indentLevel++;
                segLength = EditorGUILayout.FloatField(Content("Segment Length", "每段墙长度"), segLength);
                if (segLength < 0.01f) segLength = 0.01f;
                if (EditorGUI.EndChangeCheck()) RecalcWallSegments();
                EditorGUI.indentLevel--;

                string info = buildMode switch
                {
                    BuildMode.Enclosure => $"Long: {segsLong}x2    Short: {segsShort}x2",
                    BuildMode.LShape    => $"Long: {segsLong}    Short: {segsShort}",
                    _                   => $"Long: {segsLong}x2",
                };
                EditorGUILayout.BeginHorizontal("box");
                EditorGUILayout.LabelField(info, EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();

                string sp = isCorridor
                    ? $"Actual Spacing - Long: {wallLength / segsLong:F2}m"
                    : $"Actual Spacing - Long: {wallLength / segsLong:F2}m    Short: {wallDepth / segsShort:F2}m";
                EditorGUILayout.LabelField(sp, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.indentLevel++;
                segsLong = EditorGUILayout.IntField(Content("Long Side Segments", "长边段数"), segsLong);
                if (!isCorridor)
                    segsShort = EditorGUILayout.IntField(Content("Short Side Segments", "宽边段数"), segsShort);
                if (segsLong  < 1) segsLong  = 1;
                if (segsShort < 1) segsShort = 1;
                EditorGUI.indentLevel--;
            }
        }
    }

    // ── Common Options ──

    private void DrawCommonOptions(bool isLine)
    {
        EditorGUILayout.LabelField(Content("Options", "选项"), EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            objectScale = EditorGUILayout.Vector3Field(Content("Scale", "缩放"), objectScale);

            if (isLine)
            {
                lineRotation = EditorGUILayout.Vector3Field(Content("Rotation", "旋转"), lineRotation);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                faceInward = EditorGUILayout.Toggle(Content("Face Inward", "墙面朝内"), faceInward);
                if (buildMode == BuildMode.Enclosure)
                    useCorners = EditorGUILayout.Toggle(Content("Use Corners", "使用转角"), useCorners);
                EditorGUILayout.EndHorizontal();
            }

            centerPos = EditorGUILayout.Vector3Field(Content("Center", "中心点"), centerPos);
            rootName  = EditorGUILayout.TextField(Content("Root Name", "根节点名"), rootName);
        }
    }

    // ── Build Button ──

    private void DrawBuildSection(bool isLine)
    {
        bool canBuild = wallPrefab != null && (!useDifferentFirst || firstPrefab != null);

        if (!canBuild)
            EditorGUILayout.HelpBox("请指定所需的 Prefab", MessageType.Warning);

        if (!isLine && useCorners && !cornerPrefab)
            EditorGUILayout.HelpBox("已启用转角但未指定转角 Prefab，转角将被跳过", MessageType.Warning);

        using (new EditorGUI.DisabledScope(!canBuild))
        {
            GUI.backgroundColor = new Color(0.5f, 0.85f, 0.5f);
            if (GUILayout.Button(isLine ? Content("Build Queue", "生成队列") : Content("Build Walls", "生成围墙"), GUILayout.Height(42)))
                Build();
            GUI.backgroundColor = Color.white;
        }

        if (!canBuild) return;

        EditorGUILayout.Space(2);
        int total = isLine ? lineCount : TotalWallObjects();
        string detail = isLine
            ? $"{lineCount} objects" + (useDifferentFirst ? " (first different)" : "")
            : buildMode switch
            {
                BuildMode.Enclosure => $"{segsLong}×2 + {segsShort}×2",
                BuildMode.LShape    => $"{segsLong} + {segsShort}",
                BuildMode.Corridor  => $"{segsLong}×2",
                _ => ""
            };
        string extra = (!isLine && useCorners && cornerPrefab && buildMode == BuildMode.Enclosure) ? " + 4 corners" : "";
        EditorGUILayout.LabelField($"Total: {total} objects  ({detail}{extra})", EditorStyles.centeredGreyMiniLabel);
    }

    // ════════════════ Wall Calc ════════════════

    private void RecalcWallSegments()
    {
        segsLong  = Mathf.Max(1, Mathf.RoundToInt(wallLength / segLength));
        segsShort = Mathf.Max(1, Mathf.RoundToInt(wallDepth  / segLength));
    }

    private int TotalWallObjects()
    {
        int t = buildMode switch
        {
            BuildMode.Enclosure => segsLong * 2 + segsShort * 2,
            BuildMode.LShape    => segsLong + segsShort,
            BuildMode.Corridor  => segsLong * 2,
            _ => 0
        };
        if (useCorners && cornerPrefab && buildMode == BuildMode.Enclosure) t += 4;
        return t;
    }

    // ════════════════ Build ════════════════

    private void Build()
    {
        if (!wallPrefab) return;

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Build " + buildMode);

        GameObject root = new GameObject(rootName);
        Undo.RegisterCreatedObjectUndo(root, "Create Root");
        root.transform.position = centerPos;

        List<GameObject> created = new List<GameObject>();

        if (buildMode == BuildMode.Line)
            BuildLine(root.transform, created);
        else
            BuildWallMode(root.transform, created);

        Undo.CollapseUndoOperations(group);
        Selection.activeGameObject = root;
        Debug.Log($"[Path Builder] {buildMode} — {created.Count} objects");
    }

    private void BuildWallMode(Transform parent, List<GameObject> list)
    {
        float hL = wallLength * 0.5f;
        float hD = wallDepth  * 0.5f;
        float sL = wallLength / segsLong;
        float sD = wallDepth  / segsShort;
        float yIn = faceInward ? 180f : 0f;

        switch (buildMode)
        {
            case BuildMode.Enclosure:
                WallRow(parent, new Vector3(-hL + sL * 0.5f, 0,  hD), Vector3.right,   sL, segsLong,  Quaternion.Euler(0, yIn,        0), wallPrefab, list);
                WallRow(parent, new Vector3(-hL + sL * 0.5f, 0, -hD), Vector3.right,   sL, segsLong,  Quaternion.Euler(0, 180f + yIn, 0), wallPrefab, list);
                WallRow(parent, new Vector3(-hL, 0, -hD + sD * 0.5f), Vector3.forward, sD, segsShort, Quaternion.Euler(0, -90f + yIn, 0), wallPrefab, list);
                WallRow(parent, new Vector3( hL, 0, -hD + sD * 0.5f), Vector3.forward, sD, segsShort, Quaternion.Euler(0, 90f  + yIn, 0), wallPrefab, list);
                if (useCorners && cornerPrefab)
                {
                    PlaceObject(parent, cornerPrefab, new Vector3(-hL, 0, -hD), Quaternion.Euler(0, 0f   + yIn, 0), list);
                    PlaceObject(parent, cornerPrefab, new Vector3( hL, 0, -hD), Quaternion.Euler(0, 90f  + yIn, 0), list);
                    PlaceObject(parent, cornerPrefab, new Vector3( hL, 0,  hD), Quaternion.Euler(0, 180f + yIn, 0), list);
                    PlaceObject(parent, cornerPrefab, new Vector3(-hL, 0,  hD), Quaternion.Euler(0, 270f + yIn, 0), list);
                }
                break;

            case BuildMode.LShape:
                WallRow(parent, new Vector3(-hL + sL * 0.5f, 0, hD), Vector3.right,   sL, segsLong,  Quaternion.Euler(0, yIn,       0), wallPrefab, list);
                WallRow(parent, new Vector3(hL, 0, -hD + sD * 0.5f), Vector3.forward, sD, segsShort, Quaternion.Euler(0, 90f + yIn, 0), wallPrefab, list);
                break;

            case BuildMode.Corridor:
                WallRow(parent, new Vector3(-hL + sL * 0.5f, 0,  hD), Vector3.right, sL, segsLong, Quaternion.Euler(0, yIn,        0), wallPrefab, list);
                WallRow(parent, new Vector3(-hL + sL * 0.5f, 0, -hD), Vector3.right, sL, segsLong, Quaternion.Euler(0, 180f + yIn, 0), wallPrefab, list);
                break;
        }
    }

    private void BuildLine(Transform parent, List<GameObject> list)
    {
        Vector3 dir = lineAxis switch { 0 => Vector3.right, 1 => Vector3.up, _ => Vector3.forward };
        if (!linePositive) dir = -dir;

        float total = lineSpacing * (lineCount - 1);
        Vector3 start = -dir * (total * 0.5f);
        Quaternion rot = Quaternion.Euler(lineRotation);

        for (int i = 0; i < lineCount; i++)
        {
            GameObject prefab = (i == 0 && useDifferentFirst && firstPrefab) ? firstPrefab : wallPrefab;
            Vector3 pos = start + dir * (lineSpacing * i);
            PlaceObject(parent, prefab, pos, rot, list);
        }
    }

    // ════════════════ Primitives ════════════════

    /// <summary>沿方向排列一排相同 prefab 的墙段。</summary>
    private void WallRow(Transform parent, Vector3 start, Vector3 dir,
        float spacing, int count, Quaternion rotation, GameObject prefab, List<GameObject> list)
    {
        for (int i = 0; i < count; i++)
            PlaceObject(parent, prefab, start + dir * (spacing * i), rotation, list);
    }

    /// <summary>放置单个物体并注册 Undo。</summary>
    private void PlaceObject(Transform parent, GameObject prefab, Vector3 localPos,
        Quaternion localRot, List<GameObject> list)
    {
        GameObject go = InstantiateSmart(prefab);
        go.transform.SetParent(parent);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = objectScale;
        go.name = $"{prefab.name}_{list.Count:D3}";
        Undo.RegisterCreatedObjectUndo(go, "Place Object");
        list.Add(go);
    }

    private static GameObject InstantiateSmart(GameObject source)
    {
        if (!source) return null;
        if (source.scene.IsValid() && !string.IsNullOrEmpty(source.scene.name))
            return Instantiate(source);
        if (PrefabUtility.IsPartOfPrefabAsset(source) ||
            PrefabUtility.GetPrefabAssetType(source) != PrefabAssetType.NotAPrefab)
            return (GameObject)PrefabUtility.InstantiatePrefab(source);
        return Instantiate(source);
    }

    // ── Spline workspace ──
    private enum SplineStage { Draw, Adjust, Output }

    private static readonly string[] DefaultMountainPaths =
    {
        "Assets/GameRes/Sandbox/Prefabs/env_mountain_style1.prefab",
        "Assets/GameRes/Sandbox/Prefabs/env_mountain_style2.prefab",
        "Assets/GameRes/Sandbox/Prefabs/env_mountain_style3.prefab",
        "Assets/GameRes/Sandbox/Prefabs/env_mountain_style4.prefab",
        "Assets/GameRes/Sandbox/Prefabs/env_mountain_style5.prefab"
    };

    private const string DefaultRiverMaterialPath =
        "Assets/ArtRes/Art_Scene/Art_MainScene/Scene_City/Material/MA_River.mat";

    [SerializeField] private PathSpline _builder;
    [SerializeField] private Vector2 _scroll;
    [SerializeField] private bool _isDrawing;
    [SerializeField] private SplineStage _splineStage;
    private PathSplineEditor _builderEditor;
    private Vector3 _hoverWorldPosition;
    private bool _hasHoverPosition;
    private bool _splineUIEnabled;

    private void EnableSplineUI()
    {
        if (_splineUIEnabled)
            return;
        _splineUIEnabled = true;
        SceneView.duringSceneGui += DuringSceneGUI;
        RecreateEditor();
    }

    private void DisableSplineUI()
    {
        if (!_splineUIEnabled)
            return;
        _splineUIEnabled = false;
        SceneView.duringSceneGui -= DuringSceneGUI;
        DestroyEditor();
    }

    private void DrawSplineWorkspace()
    {
        if (!_builder || !_builder.Anchor)
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox(
                $"当前没有 {CurrentSplineModeName} 路径。",
                MessageType.Info);
            if (GUILayout.Button(
                $"Create {CurrentSplineModeName} Path",
                GUILayout.Height(36f)))
            {
                CreateSession(CurrentSplineMode);
            }
            GUILayout.FlexibleSpace();
            DrawAuthorFooter();
            return;
        }

        DrawStageTabs();

        if (_builderEditor == null)
            RecreateEditor();

        _scroll = EditorGUILayout.BeginScrollView(_scroll);
        if (_isDrawing || _splineStage == SplineStage.Draw)
            DrawDrawingPanel();
        else if (_builderEditor != null
                 && _splineStage == SplineStage.Adjust)
            _builderEditor.DrawAuthoringInspector();
        else if (_builderEditor != null)
            _builderEditor.DrawOutputInspector();
        EditorGUILayout.EndScrollView();

        DrawSplineBottomBar();
    }

    private PathBuildMode CurrentSplineMode =>
        activeTab == BuilderTab.River
            ? PathBuildMode.River
            : PathBuildMode.Mountain;

    private string CurrentSplineModeName =>
        CurrentSplineMode == PathBuildMode.Mountain
            ? "Mountain"
            : "River";

    private void DrawStageTabs()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            int selected = GUILayout.Toolbar(
                (int)_splineStage,
                PathBuilderSplineUI.SplineStages,
                EditorStyles.toolbarButton);
            SplineStage next = (SplineStage)selected;
            if (next != _splineStage)
                ChangeSplineStage(next);
        }
    }

    private void ChangeSplineStage(SplineStage next)
    {
        if (next == SplineStage.Draw)
        {
            BeginDrawing(false);
            return;
        }

        if (_builder.knots.Count < 2)
        {
            ShowNotification(new GUIContent("至少需要两个控制点"));
            _splineStage = SplineStage.Draw;
            return;
        }

        if (_isDrawing)
            FinishDrawing();
        _splineStage = next;
        Repaint();
        SceneView.RepaintAll();
    }

    private void DrawSplineBottomBar()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string previewState = _builder.GeneratedRoot
                    ? "Preview ready"
                    : "Preview empty";
                EditorGUILayout.LabelField(
                    $"{_builder.knots.Count} points · {previewState}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("New Path", GUILayout.Width(76f)))
                    CreateSession(_builder.mode);
                if (GUILayout.Button("Discard", GUILayout.Width(66f)))
                    DiscardSession();
            }

            if (!_builder)
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Redraw", GUILayout.Height(30f)))
                    BeginDrawing(true);

                using (new EditorGUI.DisabledScope(
                    _isDrawing || _builder.knots.Count < 2))
                {
                    GUI.backgroundColor = new Color(0.5f, 0.85f, 0.5f);
                    if (GUILayout.Button("Preview", GUILayout.Height(30f)))
                        PathBuilderGenerator.Rebuild(_builder);
                    GUI.backgroundColor = Color.white;

                    if (GUILayout.Button("Clear", GUILayout.Height(30f)))
                        PathBuilderGenerator.ClearGenerated(_builder);

                    GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                    if (GUILayout.Button("Bake", GUILayout.Height(30f)))
                        BakeCurrentSession();
                    GUI.backgroundColor = Color.white;
                }
            }
        }
    }

    private void DrawDrawingPanel()
    {
        string modeName = _builder.mode == PathBuildMode.Mountain
            ? "Mountain"
            : "River";

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField(
            $"{modeName} · Draw Spline",
            EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "在 Scene 视图中直接左键连续落点。\n"
            + "Enter 或右键完成；Backspace 删除上一个点。"
            + "点击第一个点可直接闭合并完成。",
            MessageType.Info);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                $"Control Points: {_builder.knots.Count}");
            _builder.projectToSurface = EditorGUILayout.Toggle(
                "Project To Surface",
                _builder.projectToSurface);
            if (_builder.projectToSurface)
            {
                _builder.surfaceMask = LayerMaskField(
                    "Surface Layer",
                    _builder.surfaceMask);
                _builder.surfaceOffset = EditorGUILayout.FloatField(
                    "Height Offset",
                    _builder.surfaceOffset);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(
                _builder.knots.Count == 0))
            {
                if (GUILayout.Button("Undo Last Point", GUILayout.Height(30f)))
                    RemoveLastDrawingPoint();
            }

            using (new EditorGUI.DisabledScope(
                _builder.knots.Count < 2))
            {
                if (GUILayout.Button("Finish & Preview", GUILayout.Height(30f)))
                    FinishDrawing();
            }
        }
    }

    private void SelectSplineMode(PathBuildMode mode)
    {
        BuilderTab requestedTab = mode == PathBuildMode.Mountain
            ? BuilderTab.Mountain
            : BuilderTab.River;

        if (_builder && _builder.Anchor && _builder.mode == mode)
        {
            activeTab = requestedTab;
            if (_isDrawing)
                _splineStage = SplineStage.Draw;
            Repaint();
            return;
        }

        if (CreateSession(mode))
            activeTab = requestedTab;
        else if (_builder && _builder.Anchor)
            activeTab = _builder.mode == PathBuildMode.Mountain
                ? BuilderTab.Mountain
                : BuilderTab.River;
    }

    private bool CreateSession(PathBuildMode mode)
    {
        if (_builder && _builder.Anchor)
        {
            bool replace = EditorUtility.DisplayDialog(
                "Path Builder",
                "当前有未烘焙的样条。是否丢弃并创建新的样条？",
                "创建新的",
                "取消");
            if (!replace)
                return false;
            DiscardSession();
        }

        _builder = CreateInstance<PathSpline>();
        _builder.hideFlags = HideFlags.DontSaveInBuild;
        _builder.mode = mode;
        _builder.projectToSurface = true;
        _builder.EnsureDefaults();

        string objectName = mode == PathBuildMode.Mountain
            ? "Mountain Spline Authoring"
            : "River Spline Authoring";
        GameObject anchor = new GameObject(objectName);
        anchor.hideFlags = HideFlags.DontSaveInBuild;
        Undo.RegisterCreatedObjectUndo(anchor, $"Create {objectName}");

        Vector3 position = Vector3.zero;
        if (Selection.activeTransform)
            position = Selection.activeTransform.position;
        else if (SceneView.lastActiveSceneView)
            position = SceneView.lastActiveSceneView.pivot;
        anchor.transform.position = position;
        _builder.Anchor = anchor.transform;

        if (mode == PathBuildMode.Mountain)
            LoadDefaultMountains();
        else
            _builder.riverMaterial =
                AssetDatabase.LoadAssetAtPath<Material>(
                    DefaultRiverMaterialPath);

        RecreateEditor();
        activeTab = mode == PathBuildMode.Mountain
            ? BuilderTab.Mountain
            : BuilderTab.River;
        _splineStage = SplineStage.Draw;
        Selection.activeGameObject = anchor;
        BeginDrawing(true);
        SceneView.lastActiveSceneView?.Focus();
        Repaint();
        return true;
    }

    private void BeginDrawing(bool resetPath)
    {
        if (!_builder || !_builder.Anchor)
            return;

        PathBuilderGenerator.ClearGenerated(_builder);
        if (resetPath)
        {
            Undo.RecordObject(_builder, "Redraw Path Builder");
            _builder.knots.Clear();
            _builder.closed = false;
        }

        _isDrawing = true;
        _splineStage = SplineStage.Draw;
        _hasHoverPosition = false;
        EditorUtility.SetDirty(_builder);
        SceneView.RepaintAll();
        Repaint();
    }

    private void FinishDrawing(bool closeLoop = false)
    {
        if (!_builder || _builder.knots.Count < 2)
        {
            ShowNotification(new GUIContent("至少需要两个控制点"));
            return;
        }

        Undo.RecordObject(_builder, "Finish Path Builder");
        if (closeLoop && _builder.knots.Count >= 3)
            _builder.closed = true;

        for (int i = 0; i < _builder.knots.Count; i++)
            _builder.SmoothKnot(i);

        _isDrawing = false;
        _splineStage = SplineStage.Adjust;
        _hasHoverPosition = false;
        EditorUtility.SetDirty(_builder);
        PathBuilderGenerator.Rebuild(_builder, true);
        SceneView.RepaintAll();
        Repaint();
    }

    private void BakeCurrentSession()
    {
        if (!_builder || !PathBuilderGenerator.Bake(_builder))
            return;

        DestroyEditor();
        DestroyImmediate(_builder);
        _builder = null;
        _isDrawing = false;
        _splineStage = SplineStage.Output;
        _hasHoverPosition = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private void DiscardSession()
    {
        DestroyEditor();
        if (_builder)
        {
            PathBuilderGenerator.ClearGenerated(_builder);
            if (_builder.Anchor)
                Undo.DestroyObjectImmediate(_builder.Anchor.gameObject);
            DestroyImmediate(_builder);
        }
        _builder = null;
        _isDrawing = false;
        _hasHoverPosition = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private void LoadDefaultMountains()
    {
        _builder.mountainPrefabs.Clear();
        for (int i = 0; i < DefaultMountainPaths.Length; i++)
        {
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    DefaultMountainPaths[i]);
            if (!prefab)
                continue;
            _builder.mountainPrefabs.Add(new MountainPrefabEntry
            {
                prefab = prefab,
                weight = 1f
            });
        }
    }

    private void RecreateEditor()
    {
        DestroyEditor();
        if (_builder)
            _builderEditor =
                Editor.CreateEditor(_builder)
                    as PathSplineEditor;
    }

    private void DestroyEditor()
    {
        if (_builderEditor)
            DestroyImmediate(_builderEditor);
        _builderEditor = null;
    }

    private void DuringSceneGUI(SceneView sceneView)
    {
        if (activeTab == BuilderTab.Wall
            || !_builder || !_builder.Anchor)
            return;

        DrawSceneWorkspaceOverlay(sceneView);
        if (_isDrawing)
            DrawSplinePlacement(sceneView);
        else if (_builderEditor
                 && _splineStage == SplineStage.Adjust)
            _builderEditor.DrawSceneGUI();
    }

    private void DrawSplinePlacement(SceneView sceneView)
    {
        Event current = Event.current;
        if (current.type == EventType.Layout)
        {
            HandleUtility.AddDefaultControl(
                GUIUtility.GetControlID(FocusType.Passive));
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
        _hasHoverPosition = TryGetPlacementPosition(
            ray,
            out _hoverWorldPosition);

        DrawDraftSpline();

        if (current.alt)
            return;

        if (current.type == EventType.MouseDown
            && current.button == 0)
        {
            if (CanCloseAtFirstPoint(current.mousePosition))
            {
                FinishDrawing(true);
                current.Use();
                return;
            }

            if (_hasHoverPosition)
            {
                Undo.RecordObject(_builder, "Add Spline Point");
                _builder.AppendKnot(
                    _builder.Anchor.InverseTransformPoint(
                        _hoverWorldPosition));
                EditorUtility.SetDirty(_builder);
                current.Use();
                Repaint();
            }
        }
        else if (current.type == EventType.MouseDown
                 && current.button == 1)
        {
            if (_builder.knots.Count >= 2)
                FinishDrawing();
            current.Use();
        }
        else if (current.type == EventType.KeyDown
                 && (current.keyCode == KeyCode.Return
                     || current.keyCode == KeyCode.KeypadEnter))
        {
            FinishDrawing();
            current.Use();
        }
        else if (current.type == EventType.KeyDown
                 && (current.keyCode == KeyCode.Backspace
                     || current.keyCode == KeyCode.Delete))
        {
            RemoveLastDrawingPoint();
            current.Use();
        }

        if (current.type == EventType.MouseMove)
            sceneView.Repaint();
    }

    private void DrawDraftSpline()
    {
        if (_builder.knots.Count == 0)
        {
            if (_hasHoverPosition)
            {
                Handles.color = new Color(0.2f, 0.85f, 1f, 0.9f);
                Handles.DrawWireDisc(
                    _hoverWorldPosition,
                    Vector3.up,
                    HandleUtility.GetHandleSize(_hoverWorldPosition) * 0.08f);
            }
            return;
        }

        Color pathColor = _builder.mode == PathBuildMode.River
            ? new Color(0.1f, 0.75f, 1f, 1f)
            : new Color(0.85f, 0.55f, 0.15f, 1f);
        Handles.color = pathColor;

        for (int i = 0; i < _builder.knots.Count; i++)
        {
            PathKnot knot = _builder.knots[i];
            Vector3 point = _builder.Anchor.TransformPoint(knot.position);
            float size = HandleUtility.GetHandleSize(point) * 0.08f;
            Handles.SphereHandleCap(
                0,
                point,
                Quaternion.identity,
                size,
                EventType.Repaint);
            Handles.Label(
                point + Vector3.up * size * 1.5f,
                i.ToString());

            if (i >= _builder.knots.Count - 1)
                continue;

            PathKnot next = _builder.knots[i + 1];
            Vector3 nextPoint =
                _builder.Anchor.TransformPoint(next.position);
            Handles.DrawBezier(
                point,
                nextPoint,
                _builder.Anchor.TransformPoint(
                    knot.position + knot.tangentOut),
                _builder.Anchor.TransformPoint(
                    next.position + next.tangentIn),
                pathColor,
                null,
                3f);
        }

        if (_hasHoverPosition)
        {
            Vector3 last = _builder.Anchor.TransformPoint(
                _builder.knots[_builder.knots.Count - 1].position);
            Handles.DrawDottedLine(last, _hoverWorldPosition, 4f);
            Handles.DrawWireDisc(
                _hoverWorldPosition,
                Vector3.up,
                HandleUtility.GetHandleSize(_hoverWorldPosition) * 0.08f);
        }
    }

    private void DrawSceneWorkspaceOverlay(SceneView sceneView)
    {
        Handles.BeginGUI();
        Rect area = new Rect(12f, 12f, 360f, 88f);
        GUILayout.BeginArea(area, EditorStyles.helpBox);
        GUILayout.Label(
            $"{CurrentSplineModeName} · {_splineStage}",
            EditorStyles.miniBoldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            bool drawSelected = GUILayout.Toggle(
                _splineStage == SplineStage.Draw,
                "Draw",
                EditorStyles.miniButtonLeft);
            if (drawSelected && _splineStage != SplineStage.Draw)
            {
                BeginDrawing(false);
            }

            using (new EditorGUI.DisabledScope(
                _builder.knots.Count < 2))
            {
                bool adjustSelected = GUILayout.Toggle(
                    _splineStage == SplineStage.Adjust,
                    "Adjust",
                    EditorStyles.miniButtonMid);
                if (adjustSelected
                    && _splineStage != SplineStage.Adjust)
                {
                    ChangeSplineStage(SplineStage.Adjust);
                }

                bool outputSelected = GUILayout.Toggle(
                    _splineStage == SplineStage.Output,
                    "Output",
                    EditorStyles.miniButtonRight);
                if (outputSelected
                    && _splineStage != SplineStage.Output)
                {
                    ChangeSplineStage(SplineStage.Output);
                }
            }

            if (GUILayout.Button("Preview", EditorStyles.miniButton))
                PathBuilderGenerator.Rebuild(_builder, false);
        }

        GUILayout.Label(
            _isDrawing
                ? "左键落点 · Enter/右键完成 · Backspace 撤点 · 点击首点闭合"
                : "拖动控制点、黄色切线；河流可拖动两侧宽度手柄",
            EditorStyles.wordWrappedMiniLabel);
        GUILayout.EndArea();
        Handles.EndGUI();
    }

    private bool TryGetPlacementPosition(
        Ray ray,
        out Vector3 worldPosition)
    {
        if (_builder.projectToSurface
            && Physics.Raycast(
                ray,
                out RaycastHit hit,
                Mathf.Infinity,
                _builder.surfaceMask,
                QueryTriggerInteraction.Ignore))
        {
            worldPosition =
                hit.point + Vector3.up * _builder.surfaceOffset;
            return true;
        }

        Plane plane = new Plane(Vector3.up, _builder.Anchor.position);
        if (plane.Raycast(ray, out float distance))
        {
            worldPosition = ray.GetPoint(distance);
            return true;
        }

        worldPosition = Vector3.zero;
        return false;
    }

    private bool CanCloseAtFirstPoint(Vector2 mousePosition)
    {
        if (_builder.knots.Count < 3)
            return false;

        Vector3 first = _builder.Anchor.TransformPoint(
            _builder.knots[0].position);
        Vector2 firstGui = HandleUtility.WorldToGUIPoint(first);
        return Vector2.Distance(firstGui, mousePosition) <= 14f;
    }

    private void RemoveLastDrawingPoint()
    {
        if (!_builder || _builder.knots.Count == 0)
            return;

        Undo.RecordObject(_builder, "Remove Spline Point");
        _builder.knots.RemoveAt(_builder.knots.Count - 1);
        EditorUtility.SetDirty(_builder);
        SceneView.RepaintAll();
        Repaint();
    }

    private static LayerMask LayerMaskField(
        string label,
        LayerMask selected)
    {
        string[] layers = UnityEditorInternal.InternalEditorUtility.layers;
        int maskWithoutEmpty = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            int layer = LayerMask.NameToLayer(layers[i]);
            if ((selected.value & (1 << layer)) != 0)
                maskWithoutEmpty |= 1 << i;
        }

        maskWithoutEmpty = EditorGUILayout.MaskField(
            label,
            maskWithoutEmpty,
            layers);

        int mask = 0;
        for (int i = 0; i < layers.Length; i++)
        {
            if ((maskWithoutEmpty & (1 << i)) == 0)
                continue;
            mask |= 1 << LayerMask.NameToLayer(layers[i]);
        }
        selected.value = mask;
        return selected;
    }
}
