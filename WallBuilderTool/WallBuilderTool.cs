using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class WallBuilderTool : EditorWindow
{
    private enum BuildMode { Enclosure, LShape, Corridor, Line }

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
        var w = GetWindow<WallBuilderTool>("Wall Builder");
        w.minSize = new Vector2(400, 520);
    }

    private void OnEnable() { RecalcWallSegments(); }

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    // ════════════════ GUI ════════════════

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        bool isLine = buildMode == BuildMode.Line;

        buildMode = (BuildMode)EditorGUILayout.EnumPopup(Content("Build Mode", "生成模式"), buildMode);
        EditorGUILayout.Space(4);

        DrawPrefabSection(isLine);
        EditorGUILayout.Space(4);

        if (isLine) DrawLineSettings();
        else        DrawWallSettings();
        EditorGUILayout.Space(4);

        DrawCommonOptions(isLine);
        EditorGUILayout.Space(8);

        DrawBuildSection(isLine);

        EditorGUILayout.EndScrollView();

        // ── 署名 ──
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
        Debug.Log($"[Wall Builder] {buildMode} — {created.Count} objects");
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
}
