using UnityEngine;
using UnityEditor;
using UnityEditor.Overlays;
using System.Collections.Generic;
using ZXXTools.PathBuilder;

public class PathBuilderWindow : EditorWindow
{
    internal static PathBuilderWindow ActiveSceneOverlayWindow { get; private set; }

    private static readonly int PenToolControlHash =
        "PathBuilder.PenTool".GetHashCode();
    private const float PenDragThreshold = 3f;

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
    private bool _batchBaking;
    private readonly List<PathSpline> _selectedMountainBuilders =
        new List<PathSpline>();
    [SerializeField] private Camera _batchMountainCamera;

    public static void ShowWindow()
    {
        var w = GetWindow<PathBuilderWindow>("路径构建");
        w.minSize = new Vector2(420, 560);
        w.Show();
    }

    public static PathBuilderWindow Open(PathBuildMode mode)
    {
        PathBuilderWindow window = GetWindow<PathBuilderWindow>("路径构建");
        window.minSize = new Vector2(420, 560);
        window.SelectSplineMode(mode);
        window.Show();
        return window;
    }

    private void OnEnable()
    {
        ActiveSceneOverlayWindow = this;
        RecalcWallSegments();
        RestoreWindowSession();
        EnableSplineUI();
        RefreshSelectedMountainBuilders(false);
    }

    private void OnDisable()
    {
        ResetPenDrag();
        EditorApplication.delayCall -= RestoreDeferredSession;
        _restoreScheduled = false;
        if (!EditorApplication.isCompiling
            && !EditorApplication.isUpdating)
        {
            SaveCurrentSession();
        }
        DisableSplineUI();
        if (ActiveSceneOverlayWindow == this)
            ActiveSceneOverlayWindow = null;
    }

    private void OnSelectionChange()
    {
        if (_batchBaking)
            return;
        RefreshSelectedMountainBuilders(true);
        if (PathBuilderAuthoringStorage.TryRestoreSelected(
                out PathSpline selectedBuilder)
            && selectedBuilder != _builder)
        {
            SaveCurrentSession();
            ActivateSession(selectedBuilder);
        }
        Repaint();
    }

    private static GUIContent Content(
        string englishLabel,
        string chineseTooltip)
    {
        return new GUIContent(englishLabel, chineseTooltip);
    }

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
        buildMode = (BuildMode)EditorGUILayout.Popup(
            Content("Build Mode", "选择封闭围墙、L 形围墙、双排通道或直线队列"),
            (int)buildMode,
            new[] { "Enclosure", "L Shape", "Corridor", "Line" });
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
        EditorGUILayout.LabelField(
            isLine
                ? Content("Object", "设置直线队列使用的物体")
                : Content("Wall Segments", "设置围墙使用的墙段和转角预制体"),
            EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (isLine)
            {
                useDifferentFirst = EditorGUILayout.Toggle(Content("Use Different First Prefab", "让队列中的第一个物体使用单独的预制体"), useDifferentFirst);
                if (useDifferentFirst)
                {
                    firstPrefab = (GameObject)EditorGUILayout.ObjectField(Content("First Prefab", "拖入队列第一个位置使用的预制体"), firstPrefab, typeof(GameObject), true);
                    wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Rest Prefab", "拖入队列其余位置使用的预制体"), wallPrefab, typeof(GameObject), true);
                }
                else
                {
                    wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Object Prefab", "拖入直线队列重复使用的预制体"), wallPrefab, typeof(GameObject), true);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                wallPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Wall Segment Prefab", "拖入沿围墙边缘重复使用的墙段预制体"), wallPrefab, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) RecalcWallSegments();

                if (buildMode == BuildMode.Enclosure)
                    cornerPrefab = (GameObject)EditorGUILayout.ObjectField(Content("Corner Prefab (Optional)", "可选：拖入封闭围墙四个转角使用的预制体"), cornerPrefab, typeof(GameObject), true);
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
            EditorGUILayout.LabelField(Content("Count", "设置直线队列生成的物体数量"), GUILayout.Width(70));
            lineCount = EditorGUILayout.IntField(lineCount);
            if (lineCount < 1) lineCount = 1;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Spacing", "设置相邻物体中心之间的距离"), GUILayout.Width(70));
            lineSpacing = EditorGUILayout.FloatField(lineSpacing);
            if (lineSpacing < 0.01f) lineSpacing = 0.01f;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Content("Axis", "选择直线队列延伸的世界轴向"), GUILayout.Width(70));
            if (GUILayout.Toggle(lineAxis == 0, new GUIContent("X", "沿世界 X 轴排列"), "Button", GUILayout.Width(30))) lineAxis = 0;
            if (GUILayout.Toggle(lineAxis == 1, new GUIContent("Y", "沿世界 Y 轴排列"), "Button", GUILayout.Width(30))) lineAxis = 1;
            if (GUILayout.Toggle(lineAxis == 2, new GUIContent("Z", "沿世界 Z 轴排列"), "Button", GUILayout.Width(30))) lineAxis = 2;
            GUILayout.Space(10);
            EditorGUILayout.LabelField(Content("Dir", "切换所选轴向的正方向或负方向"), GUILayout.Width(30));
            linePositive = EditorGUILayout.Toggle(linePositive, GUILayout.Width(20));
            EditorGUILayout.LabelField(linePositive ? "+" : "−", GUILayout.Width(15));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(
                $"Total Length: {lineSpacing * (lineCount - 1):F2}m",
                EditorStyles.miniLabel);
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
            EditorGUILayout.LabelField(Content("Length (X)", "设置围墙沿世界 X 轴的总长度"), GUILayout.Width(70));
            wallLength = EditorGUILayout.FloatField(wallLength);
            EditorGUILayout.EndHorizontal();

            if (!isCorridor)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(Content("Depth (Z)", "设置围墙沿世界 Z 轴的总深度"), GUILayout.Width(70));
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
            autoSegment = EditorGUILayout.Toggle(Content("Auto Calculate Segments", "根据围墙尺寸和目标墙段长度自动计算段数"), autoSegment);

            if (autoSegment)
            {
                EditorGUI.indentLevel++;
                segLength = EditorGUILayout.FloatField(Content("Segment Length", "用于自动计算段数的目标墙段长度"), segLength);
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
                segsLong = EditorGUILayout.IntField(Content("Long Side Segments", "手动设置每条长边使用的墙段数量"), segsLong);
                if (!isCorridor)
                    segsShort = EditorGUILayout.IntField(Content("Short Side Segments", "手动设置每条短边使用的墙段数量"), segsShort);
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
            objectScale = EditorGUILayout.Vector3Field(Content("Scale", "设置所有生成物体使用的缩放"), objectScale);

            if (isLine)
            {
                lineRotation = EditorGUILayout.Vector3Field(Content("Rotation", "设置直线队列中所有物体的旋转"), lineRotation);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                faceInward = EditorGUILayout.Toggle(Content("Face Inward", "让围墙墙段朝向围墙内部"), faceInward);
                if (buildMode == BuildMode.Enclosure)
                    useCorners = EditorGUILayout.Toggle(Content("Use Corners", "在封闭围墙四角生成转角预制体"), useCorners);
                EditorGUILayout.EndHorizontal();
            }

            centerPos = EditorGUILayout.Vector3Field(Content("Center", "设置生成结果的世界中心位置"), centerPos);
            rootName  = EditorGUILayout.TextField(Content("Root Name", "设置生成结果根节点的名称"), rootName);
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
            if (GUILayout.Button(
                isLine
                    ? Content("Build Queue", "按当前数量、间距和轴向生成物体队列")
                    : Content("Build Walls", "按当前尺寸、墙段和选项生成围墙"),
                GUILayout.Height(42)))
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
        string extra = (!isLine && useCorners && cornerPrefab && buildMode == BuildMode.Enclosure)
            ? " + 4 corners"
            : "";
        EditorGUILayout.LabelField(
            $"Total: {total} objects  ({detail}{extra})",
            EditorStyles.centeredGreyMiniLabel);
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
    [SerializeField] private bool _drawingChanged;
    [SerializeField] private SplineStage _splineStage;
    private PathSplineEditor _builderEditor;
    private Vector3 _hoverWorldPosition;
    private bool _hasHoverPosition;
    private bool _splineUIEnabled;
    private bool _restoreScheduled;
    private bool _penDragging;
    private bool _penHandleDragged;
    private int _penDragControlId;
    private int _penDragKnotIndex = -1;
    private Vector2 _penDragStartGui;
    private Vector3 _penDragAnchorWorld;

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
            string chineseModeName = CurrentSplineMode
                == PathBuildMode.Mountain
                    ? "山脉"
                    : "河流 / 道路";
            EditorGUILayout.HelpBox(
                $"当前没有{chineseModeName}路径。",
                MessageType.Info);
            if (GUILayout.Button(
                new GUIContent(
                    $"Create {CurrentSplineModeName} Path",
                    $"新建一条可编辑的{chineseModeName}样条路径"),
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
            : "River / Road";

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
                    ? (_builder.GeneratedRoot.gameObject.activeSelf
                        ? "Preview ready"
                        : "Preview retained · hidden")
                    : "Preview empty";
                EditorGUILayout.LabelField(
                    $"{_builder.Anchor.name} · "
                        + $"{_builder.knots.Count} points · {previewState}",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                    new GUIContent(
                        "New Path",
                        "保存并保留当前路径，然后创建一条新的独立路径"),
                    GUILayout.Width(76f)))
                    CreateSession(_builder.mode);
                if (GUILayout.Button(
                    new GUIContent("Discard", "删除当前未烘焙的路径和预览"),
                    GUILayout.Width(66f)))
                    DiscardSession();
            }

            if (!_builder)
                return;

            if (_builder.mode == PathBuildMode.Mountain)
            {
                DrawSelectedMountainBatchControls();

                bool flattenPreview = GUILayout.Toggle(
                    _builder.mountainFlattenPreview,
                    new GUIContent(
                        "Flatten Preview",
                        "未烘焙时将图片山拍平到地表；开启后自动使用 Ground Decal 输出，仍保留样条编辑"),
                    "Button",
                    GUILayout.Height(26f));
                if (flattenPreview != _builder.mountainFlattenPreview)
                {
                    Undo.RecordObject(_builder, "Toggle Flatten Preview");
                    _builder.mountainFlattenPreview = flattenPreview;
                    if (flattenPreview)
                    {
                        _builder.mountainBakeOutput =
                            MountainBakeOutput.GroundDecal;
                    }
                    EditorUtility.SetDirty(_builder);
                    PathBuilderGenerator.Rebuild(_builder);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                    new GUIContent("Redraw", "清空控制点并重新绘制样条"),
                    GUILayout.Height(30f)))
                    BeginDrawing(true);

                using (new EditorGUI.DisabledScope(
                    _isDrawing || _builder.knots.Count < 2))
                {
                    GUI.backgroundColor = new Color(0.5f, 0.85f, 0.5f);
                    if (GUILayout.Button(
                        new GUIContent("Preview", "根据当前样条和参数更新实时预览"),
                        GUILayout.Height(30f)))
                        PathBuilderGenerator.Rebuild(_builder);
                    GUI.backgroundColor = Color.white;

                    if (_builder.mode == PathBuildMode.Mountain
                        && GUILayout.Button(
                            new GUIContent(
                                "Reset Rocks",
                                "清空所有石头的单体位移和缩放记录，并根据当前样条、随机种子和参数重新生成；样条不会删除"),
                            GUILayout.Height(30f)))
                    {
                        Undo.RecordObject(
                            _builder,
                            "Reset Mountain Instances");
                        _builder.ClearMountainInstanceOverrides();
                        EditorUtility.SetDirty(_builder);
                        PathBuilderGenerator.Rebuild(_builder);
                    }

                    if (GUILayout.Button(
                        new GUIContent("Clear", "仅清除生成结果，保留样条和参数"),
                        GUILayout.Height(30f)))
                        PathBuilderGenerator.ClearGenerated(_builder);

                    GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
                    string bakeTooltip = _builder.mode == PathBuildMode.Mountain
                        ? (_builder.keepMountainAuthoringAfterBake
                            ? "生成普通山脉分组，同时保留样条线和设置"
                            : "生成普通山脉分组，然后结束当前样条编辑")
                        : "保留当前结果并结束该路径的编辑状态";
                    if (GUILayout.Button(
                        new GUIContent("Bake", bakeTooltip),
                        GUILayout.Height(30f)))
                        BakeCurrentSession();
                    GUI.backgroundColor = Color.white;
                }
            }

            using (new EditorGUI.DisabledScope(
                _isDrawing || _builder.knots.Count < 2))
            {
                string batchLabel = _builder.mode == PathBuildMode.Mountain
                    ? "Bake All Mountain Paths"
                    : "Bake All River / Road Paths";
                if (GUILayout.Button(
                    new GUIContent(
                        batchLabel,
                        _builder.mode == PathBuildMode.Mountain
                            ? "批量烘焙当前场景中的全部山脉路径；每条路径使用自己的设置和输出目录"
                            : "批量烘焙当前场景中的全部河流/道路路径；每条路径使用自己的设置和输出目录"),
                    GUILayout.Height(28f)))
                {
                    BakeAllCurrentScenePaths();
                }
            }
        }
    }

    private void DrawSelectedMountainBatchControls()
    {
        if (_selectedMountainBuilders.Count < 2)
            return;

        EditorGUILayout.Space(3f);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(
                $"Selected Mountain Paths ({_selectedMountainBuilders.Count})",
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Batch targets locked · dragging a camera will not clear them",
                EditorStyles.miniLabel);
            _batchMountainCamera = EditorGUILayout.ObjectField(
                new GUIContent(
                    "Batch Camera",
                    "将此正交相机统一写入当前多选的 Mountain Spline Authoring，用于面片朝向和拍平投射"),
                _batchMountainCamera,
                typeof(Camera),
                true) as Camera;

            bool validCamera = TryValidateBatchMountainCamera(
                out string cameraMessage);
            if (!validCamera)
            {
                EditorGUILayout.HelpBox(cameraMessage, MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "只处理 Hierarchy 中直接选中的山脉 Authoring 根节点；每条路径仍使用自己的输出目录和保留样条设置。",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(
                _batchBaking || _isDrawing || !validCamera))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                    new GUIContent(
                        "Flatten Selected",
                        "为选中的山脉统一设置相机并开启拍平实时预览；保留全部样条和设置"),
                    GUILayout.Height(28f)))
                {
                    FlattenSelectedMountainPaths();
                }

                if (GUILayout.Button(
                    new GUIContent(
                        "Bake Selected",
                        "使用指定相机拍平并只烘焙选中的山脉；分别沿用各自输出目录和 Keep Spline 设置"),
                    GUILayout.Height(28f)))
                {
                    BakeSelectedMountainPaths();
                }
            }
        }
    }

    private void DrawDrawingPanel()
    {
        string modeName = _builder.mode == PathBuildMode.Mountain
            ? "Mountain"
            : GetRibbonModeName(_builder.ribbonPreset);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField(
            $"{modeName} · Draw Spline",
            EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "单击生成直线角点；左键按住并拖动可拉出贝塞尔手柄。\n"
            + "拖动时会实时调整进入和离开该点的曲线。\n"
            + "回车键或右键完成；退格键删除上一个点；点击首点闭合。",
            MessageType.Info);

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                $"Control Points: {_builder.knots.Count}");
            _builder.projectToSurface = EditorGUILayout.Toggle(
                new GUIContent(
                    "Place Points On Surface",
                    "绘制时通过射线把新增控制点放到指定地表上"),
                _builder.projectToSurface);
            if (_builder.projectToSurface)
            {
                _builder.surfaceMask = LayerMaskField(
                    "Surface Layer",
                    _builder.surfaceMask);
                _builder.surfaceOffset = EditorGUILayout.FloatField(
                    new GUIContent("Height Offset", "控制点相对命中地表的垂直偏移"),
                    _builder.surfaceOffset);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(
                _builder.knots.Count == 0))
            {
                if (GUILayout.Button(
                    new GUIContent("Undo Last Point", "删除最后绘制的控制点"),
                    GUILayout.Height(30f)))
                    RemoveLastDrawingPoint();
            }

            using (new EditorGUI.DisabledScope(
                _builder.knots.Count < 2))
            {
                if (GUILayout.Button(
                    new GUIContent("Finish & Preview", "结束绘制并生成实时预览"),
                    GUILayout.Height(30f)))
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

        SaveCurrentSession();
        DestroyEditor();
        _builder = null;
        _isDrawing = false;
        _hasHoverPosition = false;

        if (PathBuilderAuthoringStorage.TryRestore(
                mode,
                out PathSpline restored))
        {
            ActivateSession(restored);
            return;
        }

        CreateSession(mode);
    }

    private bool CreateSession(PathBuildMode mode)
    {
        if (_builder && _builder.Anchor)
        {
            SaveCurrentSession();
            DestroyEditor();
            _builder = null;
            _isDrawing = false;
            _drawingChanged = false;
            _hasHoverPosition = false;
        }

        _builder = PathBuilderAuthoringStorage.Create(mode);
        _builder.projectToSurface = true;
        _builder.EnsureDefaults();

        string objectName = mode == PathBuildMode.Mountain
            ? "Mountain Spline Authoring"
            : "River Spline Authoring";
        objectName = GameObjectUtility.GetUniqueNameForSibling(
            null, objectName);
        GameObject anchor = new GameObject(objectName);
        anchor.hideFlags = HideFlags.DontSaveInBuild;
        Undo.RegisterCreatedObjectUndo(anchor, $"Create {objectName}");

        Vector3 position = Vector3.zero;
        if (Selection.activeTransform)
            position = Selection.activeTransform.position;
        else if (SceneView.lastActiveSceneView)
            position = SceneView.lastActiveSceneView.pivot;
        anchor.transform.position = position;
        PathBuilderAuthoringStorage.Bind(_builder, anchor.transform);

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

    private void RestoreWindowSession()
    {
        if (_builder && PathBuilderAuthoringStorage.TryRebind(_builder))
            return;

        _builder = null;
        if (activeTab == BuilderTab.Wall)
            return;

        ScheduleDeferredRestore();
    }

    private void ScheduleDeferredRestore()
    {
        if (_restoreScheduled)
            return;
        _restoreScheduled = true;
        EditorApplication.delayCall += RestoreDeferredSession;
    }

    private void RestoreDeferredSession()
    {
        EditorApplication.delayCall -= RestoreDeferredSession;
        _restoreScheduled = false;
        if (!this || activeTab == BuilderTab.Wall
            || (_builder && _builder.Anchor))
        {
            return;
        }

        if (EditorApplication.isCompiling
            || EditorApplication.isUpdating)
        {
            ScheduleDeferredRestore();
            return;
        }

        if (PathBuilderAuthoringStorage.TryRestore(
                CurrentSplineMode,
                out PathSpline restored))
        {
            ActivateSession(restored);
        }
    }

    private void ActivateSession(PathSpline builder)
    {
        if (!builder || !PathBuilderAuthoringStorage.TryRebind(builder))
            return;

        DestroyEditor();
        _builder = builder;
        _builder.EnsureDefaults();
        activeTab = builder.mode == PathBuildMode.Mountain
            ? BuilderTab.Mountain
            : BuilderTab.River;
        _isDrawing = false;
        _hasHoverPosition = false;
        _splineStage = builder.knots.Count >= 2
            ? SplineStage.Adjust
            : SplineStage.Draw;
        RecreateEditor();

        if (!_builder.GeneratedRoot
            && _builder.autoRebuild
            && _builder.knots.Count >= 2)
        {
            PathBuilderGenerator.Rebuild(_builder, false);
        }

        Repaint();
        SceneView.RepaintAll();
    }

    private void SaveCurrentSession()
    {
        if (_builder && _builder.Anchor)
            PathBuilderAuthoringStorage.Save(_builder);
    }

    private void BeginDrawing(bool resetPath)
    {
        if (!_builder || !_builder.Anchor)
            return;

        ResetPenDrag();

        if (resetPath)
        {
            PathBuilderGenerator.ClearGenerated(_builder);
            Undo.RecordObject(_builder, "Redraw Path Builder");
            _builder.knots.Clear();
            _builder.closed = false;
            _drawingChanged = true;
        }
        else
        {
            _drawingChanged = false;
        }

        _isDrawing = true;
        _splineStage = SplineStage.Draw;
        _hasHoverPosition = false;
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

        bool closeChanged = closeLoop
            && _builder.knots.Count >= 3
            && !_builder.closed;
        bool rebuild = _drawingChanged || closeChanged;
        if (rebuild)
        {
            Undo.RecordObject(_builder, "Finish Path Builder");
        }
        if (closeChanged)
            _builder.closed = true;

        ResetPenDrag();
        _isDrawing = false;
        _drawingChanged = false;
        _splineStage = SplineStage.Adjust;
        _hasHoverPosition = false;
        if (rebuild)
        {
            EditorUtility.SetDirty(_builder);
            PathBuilderGenerator.Rebuild(_builder, true);
        }
        SceneView.RepaintAll();
        Repaint();
    }

    private void BakeCurrentSession()
    {
        PathSpline completedBuilder = _builder;
        if (!completedBuilder
            || !PathBuilderGenerator.Bake(completedBuilder))
            return;

        bool keepAuthoring = completedBuilder.mode == PathBuildMode.Mountain
            && completedBuilder.keepMountainAuthoringAfterBake;
        if (keepAuthoring)
        {
            PathBuilderAuthoringStorage.Save(completedBuilder);
            _isDrawing = false;
            _splineStage = SplineStage.Adjust;
            _hasHoverPosition = false;
            ShowNotification(new GUIContent("山脉分组已生成，样条与设置已保留"));
            SceneView.RepaintAll();
            Repaint();
            return;
        }

        DestroyEditor();
        _builder = null;
        PathBuilderAuthoringStorage.Delete(completedBuilder);
        _isDrawing = false;
        _splineStage = SplineStage.Output;
        _hasHoverPosition = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private void RefreshSelectedMountainBuilders(
        bool preserveLockedTargetsWhenSelectionChanges)
    {
        List<PathSpline> selected =
            PathBuilderAuthoringStorage.GetSelectedBuilders(
                PathBuildMode.Mountain);
        if (preserveLockedTargetsWhenSelectionChanges
            && selected.Count < 2
            && _selectedMountainBuilders.Count >= 2
            && SelectionContainsCamera())
        {
            _selectedMountainBuilders.RemoveAll(
                candidate => !candidate || !candidate.Anchor);
            return;
        }

        _selectedMountainBuilders.Clear();
        _selectedMountainBuilders.AddRange(selected);
    }

    private static bool SelectionContainsCamera()
    {
        Object[] selectedObjects = Selection.objects;
        for (int i = 0; i < selectedObjects.Length; i++)
        {
            if (selectedObjects[i] is Camera)
                return true;
            if (selectedObjects[i] is GameObject gameObject
                && gameObject.GetComponent<Camera>())
            {
                return true;
            }
        }
        return false;
    }

    private List<PathSpline> GetLockedMountainBuilders()
    {
        _selectedMountainBuilders.RemoveAll(
            candidate => !candidate || !candidate.Anchor
                || candidate.mode != PathBuildMode.Mountain);
        return new List<PathSpline>(_selectedMountainBuilders);
    }

    private bool TryValidateBatchMountainCamera(out string message)
    {
        if (!_batchMountainCamera)
        {
            message = "请拖入用于山脉面片朝向和拍平投射的正交相机。";
            return false;
        }
        if (!_batchMountainCamera.orthographic)
        {
            message = "Batch Camera 必须是正交相机。";
            return false;
        }
        if (_batchMountainCamera.transform.forward.y >= -0.01f)
        {
            message = "Batch Camera 必须朝向地面，才能将山脉面片拍平。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void FlattenSelectedMountainPaths()
    {
        if (!TryValidateBatchMountainCamera(out _))
            return;

        SaveCurrentSession();
        List<PathSpline> builders = GetLockedMountainBuilders();
        int succeeded = 0;
        int failed = 0;
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Flatten Selected Mountain Paths");
        _batchBaking = true;
        try
        {
            for (int i = 0; i < builders.Count; i++)
            {
                PathSpline candidate = builders[i];
                if (!candidate || !candidate.Anchor)
                {
                    failed++;
                    continue;
                }

                Undo.RecordObject(
                    candidate,
                    "Flatten Selected Mountain Paths");
                PathBuilderAuthoringStorage.SetMountainFacingCamera(
                    candidate,
                    _batchMountainCamera);
                candidate.mountainBakeOutput =
                    MountainBakeOutput.GroundDecal;
                candidate.mountainFlattenPreview = true;
                EditorUtility.SetDirty(candidate);
                bool rebuilt = PathBuilderGenerator.Rebuild(
                    candidate,
                    true,
                    out bool flattened);
                if (rebuilt && flattened)
                    succeeded++;
                else
                    failed++;
                PathBuilderAuthoringStorage.Save(candidate);
            }
        }
        finally
        {
            Undo.CollapseUndoOperations(undoGroup);
            _batchBaking = false;
        }

        RefreshSelectedMountainBuilders(true);
        string summary = $"批量拍平完成：成功 {succeeded}，失败 {failed}";
        ShowNotification(new GUIContent(summary));
        Debug.Log($"[Path Builder] {summary}");
        SceneView.RepaintAll();
        Repaint();
    }

    private void BakeSelectedMountainPaths()
    {
        if (!TryValidateBatchMountainCamera(out _))
            return;

        SaveCurrentSession();
        List<PathSpline> builders = GetLockedMountainBuilders();
        List<GameObject> survivingAuthoringRoots = new List<GameObject>();
        int succeeded = 0;
        int failed = 0;
        bool currentRemoved = false;
        _batchBaking = true;
        try
        {
            for (int i = 0; i < builders.Count; i++)
            {
                PathSpline candidate = builders[i];
                if (!candidate || !candidate.Anchor)
                {
                    failed++;
                    continue;
                }

                Undo.RecordObject(candidate, "Bake Selected Mountain Paths");
                PathBuilderAuthoringStorage.SetMountainFacingCamera(
                    candidate,
                    _batchMountainCamera);
                candidate.mountainBakeOutput =
                    MountainBakeOutput.GroundDecal;
                candidate.mountainFlattenPreview = true;
                EditorUtility.SetDirty(candidate);
                bool rebuilt = PathBuilderGenerator.Rebuild(
                    candidate,
                    true,
                    out bool flattened);
                if (!rebuilt || !flattened
                    || !PathBuilderGenerator.Bake(candidate))
                {
                    failed++;
                    if (candidate.Anchor)
                        survivingAuthoringRoots.Add(candidate.Anchor.gameObject);
                    PathBuilderAuthoringStorage.Save(candidate);
                    continue;
                }

                succeeded++;
                if (candidate.keepMountainAuthoringAfterBake)
                {
                    PathBuilderAuthoringStorage.Save(candidate);
                    if (candidate.Anchor)
                        survivingAuthoringRoots.Add(candidate.Anchor.gameObject);
                }
                else
                {
                    if (candidate == _builder)
                        currentRemoved = true;
                    PathBuilderAuthoringStorage.Delete(candidate);
                }
            }

            if (survivingAuthoringRoots.Count > 0)
                Selection.objects = survivingAuthoringRoots.ToArray();
        }
        finally
        {
            _batchBaking = false;
        }

        if (currentRemoved)
        {
            DestroyEditor();
            _builder = null;
            for (int i = 0; i < builders.Count; i++)
            {
                PathSpline candidate = builders[i];
                if (candidate && candidate.Anchor)
                {
                    ActivateSession(candidate);
                    break;
                }
            }
        }

        RefreshSelectedMountainBuilders(true);
        string summary = $"选中山脉烘焙完成：成功 {succeeded}，失败 {failed}";
        ShowNotification(new GUIContent(summary));
        Debug.Log($"[Path Builder] {summary}");
        SceneView.RepaintAll();
        Repaint();
    }

    private void BakeAllCurrentScenePaths()
    {
        if (!_builder || !_builder.Anchor)
            return;

        SaveCurrentSession();
        PathBuildMode mode = _builder.mode;
        List<PathSpline> builders =
            PathBuilderAuthoringStorage.GetSceneBuilders(
                mode,
                _builder.Anchor.gameObject.scene);
        int succeeded = 0;
        int failed = 0;
        bool currentRemoved = false;
        _batchBaking = true;
        try
        {
            for (int i = 0; i < builders.Count; i++)
            {
                PathSpline candidate = builders[i];
                if (!candidate || !candidate.Anchor)
                {
                    failed++;
                    continue;
                }

                bool keepAuthoring =
                    candidate.mode == PathBuildMode.Mountain
                    && candidate.keepMountainAuthoringAfterBake;
                if (!PathBuilderGenerator.Bake(candidate))
                {
                    failed++;
                    continue;
                }

                succeeded++;
                if (keepAuthoring)
                {
                    PathBuilderAuthoringStorage.Save(candidate);
                }
                else
                {
                    if (candidate == _builder)
                        currentRemoved = true;
                    PathBuilderAuthoringStorage.Delete(candidate);
                }
            }
        }
        finally
        {
            _batchBaking = false;
        }

        if (currentRemoved)
        {
            DestroyEditor();
            _builder = null;
            for (int i = 0; i < builders.Count; i++)
            {
                PathSpline candidate = builders[i];
                if (candidate && candidate.Anchor)
                {
                    ActivateSession(candidate);
                    break;
                }
            }
        }

        string summary = $"批量烘焙完成：成功 {succeeded}，失败 {failed}";
        ShowNotification(new GUIContent(summary));
        Debug.Log($"[Path Builder] {summary}");
        SceneView.RepaintAll();
        Repaint();
    }

    private void DiscardSession()
    {
        DestroyEditor();
        PathSpline discardedBuilder = _builder;
        _builder = null;
        if (discardedBuilder)
        {
            PathBuilderGenerator.ClearGenerated(discardedBuilder);
            if (discardedBuilder.Anchor)
                Undo.DestroyObjectImmediate(
                    discardedBuilder.Anchor.gameObject);
            PathBuilderAuthoringStorage.Delete(discardedBuilder);
        }
        _isDrawing = false;
        _hasHoverPosition = false;
        SceneView.RepaintAll();
        Repaint();
    }

    private void LoadDefaultMountains()
    {
        _builder.mountainSources.Clear();
        for (int i = 0; i < DefaultMountainPaths.Length; i++)
        {
            GameObject prefab =
                AssetDatabase.LoadAssetAtPath<GameObject>(
                    DefaultMountainPaths[i]);
            if (!prefab)
                continue;
            _builder.mountainSources.Add(new MountainSourceEntry
            {
                sourceType = MountainSourceType.Prefab,
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

        // Once the user selects another scene object, stop registering spline
        // controls so Unity's native W/E/R transform handles remain available.
        if (HasExternalSceneSelection())
        {
            if (_builderEditor)
                _builderEditor.SuspendSceneEditing();
            return;
        }

        if (_isDrawing)
            DrawSplinePlacement(sceneView);
        else if (_builderEditor
                 && _splineStage == SplineStage.Adjust)
            _builderEditor.DrawSceneGUI();
    }

    private void DrawSplinePlacement(SceneView sceneView)
    {
        Event current = Event.current;
        int penControlId = GUIUtility.GetControlID(
            PenToolControlHash,
            FocusType.Passive);
        if (current.type == EventType.Layout)
            HandleUtility.AddDefaultControl(penControlId);

        Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
        _hasHoverPosition = TryGetPlacementPosition(
            ray,
            out _hoverWorldPosition);

        DrawDraftSpline();

        if (_penDragging
            && GUIUtility.hotControl == _penDragControlId)
        {
            if (current.type == EventType.MouseDrag
                || (current.type == EventType.MouseUp
                    && current.button == 0))
            {
                UpdatePenDrag(current.mousePosition);
                EditorUtility.SetDirty(_builder);
                sceneView.Repaint();
                Repaint();
            }

            if (current.type == EventType.MouseUp
                && current.button == 0)
            {
                GUIUtility.hotControl = 0;
                ResetPenDrag();
            }

            if (current.type == EventType.MouseDrag
                || current.type == EventType.MouseUp)
            {
                current.Use();
                return;
            }
        }

        if (current.alt)
            return;

        if (current.type == EventType.MouseDown
            && current.button == 0
            && (current.control || current.command))
        {
            PathBuilderSplineUI.PickSceneObject(
                current.mousePosition,
                current.shift);
            current.Use();
            sceneView.Repaint();
            return;
        }

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
                MarkDrawingChanged();
                _penDragKnotIndex = _builder.AppendPenKnot(
                    _builder.Anchor.InverseTransformPoint(
                        _hoverWorldPosition));
                _penDragging = true;
                _penHandleDragged = false;
                _penDragControlId = penControlId;
                _penDragStartGui = current.mousePosition;
                _penDragAnchorWorld = _hoverWorldPosition;
                GUIUtility.hotControl = penControlId;
                EditorUtility.SetDirty(_builder);
                current.Use();
                sceneView.Repaint();
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

        DrawActivePenTangents();
    }

    private void DrawActivePenTangents()
    {
        if (!_penDragging
            || _penDragKnotIndex < 0
            || _penDragKnotIndex >= _builder.knots.Count)
        {
            return;
        }

        PathKnot knot = _builder.knots[_penDragKnotIndex];
        Vector3 point = _builder.Anchor.TransformPoint(knot.position);
        Vector3 tangentIn = _builder.Anchor.TransformPoint(
            knot.position + knot.tangentIn);
        Vector3 tangentOut = _builder.Anchor.TransformPoint(
            knot.position + knot.tangentOut);
        float size = HandleUtility.GetHandleSize(point) * 0.055f;

        Color previousColor = Handles.color;
        Handles.color = new Color(1f, 0.72f, 0.08f, 1f);
        Handles.DrawLine(tangentIn, tangentOut);
        Handles.CubeHandleCap(
            0,
            tangentIn,
            Quaternion.identity,
            size,
            EventType.Repaint);
        Handles.CubeHandleCap(
            0,
            tangentOut,
            Quaternion.identity,
            size,
            EventType.Repaint);
        Handles.color = previousColor;
    }

    private void UpdatePenDrag(Vector2 mousePosition)
    {
        if (_penDragKnotIndex < 0
            || _penDragKnotIndex >= _builder.knots.Count)
        {
            return;
        }

        if (!_penHandleDragged
            && Vector2.Distance(_penDragStartGui, mousePosition)
                < PenDragThreshold)
        {
            return;
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);
        if (!TryGetPlacementPosition(ray, out Vector3 handleWorld))
            return;

        _penHandleDragged = true;
        Vector3 worldTangent = handleWorld - _penDragAnchorWorld;
        Vector3 localTangent =
            _builder.Anchor.InverseTransformVector(worldTangent);
        PathKnot knot = _builder.knots[_penDragKnotIndex];
        knot.tangentIn = -localTangent;
        knot.tangentOut = localTangent;
        knot.tangentMode = PathTangentMode.Mirrored;
        knot.brokenTangents = false;
    }

    private void ResetPenDrag()
    {
        _penDragging = false;
        _penHandleDragged = false;
        _penDragControlId = 0;
        _penDragKnotIndex = -1;
    }

    internal bool IsSceneOverlayVisible =>
        activeTab != BuilderTab.Wall
        && _builder
        && _builder.Anchor;

    private bool HasExternalSceneSelection()
    {
        Transform active = Selection.activeTransform;
        return !active
            || !PathBuilderAuthoringStorage.IsAuthoringHierarchy(active);
    }

    private void ResumeSplineEditing()
    {
        if (!_builder || !_builder.Anchor)
            return;

        Selection.activeGameObject = _builder.Anchor.gameObject;
        SceneView.RepaintAll();
        Repaint();
    }

    internal void DrawSceneWorkspaceOverlayContent()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(340f)))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool drawSelected = GUILayout.Toggle(
                    _splineStage == SplineStage.Draw,
                    new GUIContent("Draw", "进入样条绘制模式"),
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
                        new GUIContent("Adjust", "调整样条控制点和生成参数"),
                        EditorStyles.miniButtonMid);
                    if (adjustSelected
                        && _splineStage != SplineStage.Adjust)
                    {
                        ChangeSplineStage(SplineStage.Adjust);
                    }

                    bool outputSelected = GUILayout.Toggle(
                        _splineStage == SplineStage.Output,
                        new GUIContent("Output", "查看摘要并烘焙最终结果"),
                        EditorStyles.miniButtonRight);
                    if (outputSelected
                        && _splineStage != SplineStage.Output)
                    {
                        ChangeSplineStage(SplineStage.Output);
                    }
                }

                if (GUILayout.Button(
                    new GUIContent("Preview", "立即重建实时预览"),
                    EditorStyles.miniButton))
                    PathBuilderGenerator.Rebuild(_builder, false);
            }

            if (HasExternalSceneSelection())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(
                        Selection.activeTransform
                            ? "已退出路径编辑，W/E/R 操作当前物体"
                            : "已退出路径编辑，当前未选择场景物体",
                        EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(
                        new GUIContent("Resume Path", "重新选择路径并继续编辑"),
                        EditorStyles.miniButton,
                        GUILayout.Width(86f)))
                    {
                        ResumeSplineEditing();
                    }
                }
                return;
            }

            GUILayout.Label(
                _isDrawing
                    ? "单击直线点 · 按住拖动创建曲线手柄 · Ctrl+左键选择模型 · Enter/右键完成"
                    : "双击曲线插点 · 单击/框选控制点 · W/E/R 调整 · Shift+S 平滑",
                EditorStyles.wordWrappedMiniLabel);
        }
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

    private static string GetRibbonModeName(RibbonPathPreset preset)
    {
        switch (preset)
        {
            case RibbonPathPreset.NaturalRoad:
                return "Natural Road";
            case RibbonPathPreset.RegularRoad:
                return "Regular Road";
            default:
                return "River";
        }
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
        MarkDrawingChanged();
        _builder.knots.RemoveAt(_builder.knots.Count - 1);
        EditorUtility.SetDirty(_builder);
        SceneView.RepaintAll();
        Repaint();
    }

    private void MarkDrawingChanged()
    {
        if (_drawingChanged)
            return;

        _drawingChanged = true;
        PathBuilderGenerator.ClearGenerated(_builder);
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

[Overlay(
    typeof(SceneView),
    "ZXXTools.PathBuilder.SceneWorkspace",
    "路径构建",
    true)]
internal sealed class PathBuilderSceneOverlay : IMGUIOverlay, ITransientOverlay
{
    public bool visible
    {
        get
        {
            PathBuilderWindow window =
                PathBuilderWindow.ActiveSceneOverlayWindow;
            return window && window.IsSceneOverlayVisible;
        }
    }

    public override void OnGUI()
    {
        PathBuilderWindow window =
            PathBuilderWindow.ActiveSceneOverlayWindow;
        if (!window || !window.IsSceneOverlayVisible)
            return;

        window.DrawSceneWorkspaceOverlayContent();
    }
}
