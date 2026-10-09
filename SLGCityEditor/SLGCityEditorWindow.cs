using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>ZXX ToolBox 中的 SLG 主城布局编辑器 V1。</summary>
    public sealed class SLGCityEditorWindow : EditorWindow
    {
        private enum EditMode
        {
            Paint,
            Erase,
            BoxFill,
            BoxErase,
            Gate
        }

        private enum GroundType
        {
            Quad,
            Terrain
        }

        [SerializeField] private CityLayoutData _layout;
        [SerializeField] private CityWallPrefabSet _wallPrefabSet;
        [SerializeField] private Transform _outputParent;
        [SerializeField] private Material _groundMaterial;
        [SerializeField] private float _groundHeightOffset = -0.02f;
        [SerializeField] private GroundType _groundType;
        [SerializeField] private TerrainLayer _terrainLayer;
        [SerializeField] private float _terrainHeight = 50f;
        [SerializeField] private int _terrainHeightmapResolution = 33;
        [SerializeField] private bool _snapBuildingsToGrid;
        [SerializeField] private bool _editInScene;
        [SerializeField] private bool _showGrid = true;
        [SerializeField] private bool _showPrefabAlignment;
        [SerializeField] private bool _showVisualizationColors;
        [SerializeField] private Color _gridLineColor = new Color(0.55f, 0.7f, 0.8f, 0.35f);
        [SerializeField] private Color _occupiedFillColor = new Color(0.15f, 0.65f, 1f, 0.18f);
        [SerializeField] private Color _occupiedOutlineColor = new Color(0.2f, 0.75f, 1f, 0.28f);
        [SerializeField] private Color _boundaryColor = new Color(1f, 0.58f, 0.12f, 1f);
        [SerializeField] private Color _gateColor = new Color(0.25f, 1f, 0.45f, 0.85f);
        [SerializeField] private Color _centerColor = new Color(1f, 0.85f, 0.15f, 1f);
        [SerializeField] private EditMode _editMode;
        [SerializeField] private Vector2 _scrollPosition;

        private readonly HashSet<Vector2Int> _strokeCells = new HashSet<Vector2Int>();
        private bool _isDragging;
        private bool _undoRecorded;
        private int _activeUndoGroup = -1;
        private Vector2Int _dragStart;
        private Vector2Int _dragCurrent;
        private int _pendingWidth = 24;
        private int _pendingHeight = 24;
        private bool _gateStrokeState;
        private bool _toolsHiddenByCityEditor;
        private bool _previousToolsHidden;
        private string _lastGenerationMessage = string.Empty;
        private MessageType _lastGenerationMessageType = MessageType.None;

        public static void Open()
        {
            SLGCityEditorWindow window = GetWindow<SLGCityEditorWindow>("SLG City Editor");
            window.minSize = new Vector2(390f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Undo.undoRedoPerformed += OnUndoRedo;
            SyncPendingGridSize();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            SetUnityMoveHandleHidden(false);
            FinishStroke();
            SceneView.RepaintAll();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeObject is CityLayoutData selectedLayout)
            {
                FinishStroke();
                _layout = selectedLayout;
                SyncPendingGridSize();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private void OnUndoRedo()
        {
            _isDragging = false;
            _undoRecorded = false;
            _activeUndoGroup = -1;
            _strokeCells.Clear();
            Repaint();
            SceneView.RepaintAll();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("SLG City Editor V1", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "编辑期主城布局原型：绘制占用区域，自动提取边界并生成 Straight / Corner Prefab。"
                + "数据与旧 ProcGen 完全独立。",
                MessageType.Info);

            DrawDataSection();
            DrawGridSection();
            DrawSceneEditingSection();
            DrawGroundSection();
            DrawWallSection();

            if (!string.IsNullOrEmpty(_lastGenerationMessage))
                EditorGUILayout.HelpBox(_lastGenerationMessage, _lastGenerationMessageType);

            EditorGUILayout.EndScrollView();
            GUILayout.FlexibleSpace();
            DrawSignature();
        }

        private void DrawDataSection()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Layout Data", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            CityLayoutData newLayout = (CityLayoutData)EditorGUILayout.ObjectField(
                new GUIContent("City Layout Data", "主城网格和占用区域的持久化数据。"),
                _layout,
                typeof(CityLayoutData),
                false);
            if (EditorGUI.EndChangeCheck())
            {
                FinishStroke();
                _layout = newLayout;
                SyncPendingGridSize();
                SceneView.RepaintAll();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Create Layout", "创建新的 CityLayoutData 资源。")))
                    CreateLayoutAsset();
                using (new EditorGUI.DisabledScope(_layout == null))
                {
                    if (GUILayout.Button(new GUIContent("Select Asset", "在 Project 窗口中定位当前布局。")))
                    {
                        Selection.activeObject = _layout;
                        EditorGUIUtility.PingObject(_layout);
                    }
                }
            }
        }

        private void DrawGridSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Grid", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_layout == null))
            {
                _pendingWidth = EditorGUILayout.DelayedIntField(
                    new GUIContent("Width", "X 方向的网格数量，范围 1–256。"),
                    _pendingWidth);
                _pendingHeight = EditorGUILayout.DelayedIntField(
                    new GUIContent("Height", "Z 方向的网格数量，范围 1–256。"),
                    _pendingHeight);

                if (_layout != null)
                {
                    EditorGUI.BeginChangeCheck();
                    float cellSize = EditorGUILayout.FloatField(
                        new GUIContent("Cell Size", "单个网格的世界尺寸；Straight Prefab 应匹配这个长度。"),
                        _layout.CellSize);
                    Vector3 origin = EditorGUILayout.Vector3Field(
                        new GUIContent("Grid Center", "整个网格中心在世界空间的位置。"),
                        _layout.Origin);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_layout, "Edit SLG City Grid Settings");
                        _layout.CellSize = cellSize;
                        _layout.Origin = origin;
                        EditorUtility.SetDirty(_layout);
                        SceneView.RepaintAll();
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool sizeChanged = _pendingWidth != _layout.Grid.Width
                            || _pendingHeight != _layout.Grid.Height;
                        using (new EditorGUI.DisabledScope(!sizeChanged))
                        {
                            if (GUILayout.Button(new GUIContent(
                                "Apply Grid Size",
                                "调整网格尺寸；重叠区域的数据会保留。")))
                            {
                                Undo.RecordObject(_layout, "Resize SLG City Grid");
                                _layout.Grid.Resize(_pendingWidth, _pendingHeight);
                                _pendingWidth = _layout.Grid.Width;
                                _pendingHeight = _layout.Grid.Height;
                                EditorUtility.SetDirty(_layout);
                                SceneView.RepaintAll();
                            }
                        }

                        using (new EditorGUI.DisabledScope(_layout.Grid.CountOccupied() == 0))
                        {
                            if (GUILayout.Button(new GUIContent("Clear Cells", "清空全部主城区域，支持 Undo。")))
                                ClearCells();
                        }
                    }

                    EditorGUILayout.LabelField("Occupied Cells", _layout.Grid.CountOccupied().ToString());
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Gate Cells", _layout.Grid.CountGates().ToString());
                        using (new EditorGUI.DisabledScope(_layout.Grid.CountGates() == 0))
                        {
                            if (GUILayout.Button(new GUIContent(
                                "Clear Gates",
                                "清除全部 Gate 格标记，支持 Undo。"),
                                GUILayout.Width(90f)))
                            {
                                Undo.RecordObject(_layout, "Clear SLG City Gates");
                                _layout.Grid.ClearGates();
                                EditorUtility.SetDirty(_layout);
                                SceneView.RepaintAll();
                            }
                        }
                    }
                }
            }
        }

        private void DrawSceneEditingSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Scene Editing", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_layout == null))
            {
                bool wasEditing = _editInScene;
                EditorGUI.BeginChangeCheck();
                _showGrid = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show Grid", "在 Scene 视图中显示网格、占用区域和提取边界。"),
                    _showGrid);
                _editInScene = EditorGUILayout.ToggleLeft(
                    new GUIContent("Enable Scene Editing", "开启后左键会绘制网格；按住 Alt 仍可操作 Scene 相机。"),
                    _editInScene);
                _snapBuildingsToGrid = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Snap Scene Objects To Grid",
                        "优先根据根节点 BoxCollider 推算占格；没有时才使用渲染范围。偶数轴对齐网格线，奇数轴对齐格子中心。"),
                    _snapBuildingsToGrid);
                if (EditorGUI.EndChangeCheck())
                {
                    if (wasEditing && !_editInScene) FinishStroke();
                    SceneView.RepaintAll();
                }

                _editMode = (EditMode)GUILayout.Toolbar(
                    (int)_editMode,
                    new[] { "Paint", "Erase", "Box Fill", "Box Erase", "Gate" });
                DrawVisualizationColors();
                EditorGUILayout.HelpBox(
                    "Scene 操作：左键拖动连续绘制；Box 模式从按下位置拖到松开位置；"
                    + "Gate 模式拖动刷选连续直墙格，每段连续标记生成一座门。"
                    + "Alt + 鼠标保持 Unity 原生视角操作，Ctrl+Z / Ctrl+Y 撤销重做。",
                    MessageType.None);
            }
        }

        private void DrawVisualizationColors()
        {
            _showVisualizationColors = EditorGUILayout.Foldout(
                _showVisualizationColors,
                new GUIContent("Visualization Colors", "调整 Scene 视图中的网格和区域预览颜色。"),
                true);
            if (!_showVisualizationColors) return;

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            _gridLineColor = EditorGUILayout.ColorField(
                new GUIContent("Grid Lines", "网格线颜色和透明度。"),
                _gridLineColor);
            _occupiedFillColor = EditorGUILayout.ColorField(
                new GUIContent("Occupied Fill", "已绘制主城区域的填充颜色和透明度。"),
                _occupiedFillColor);
            _occupiedOutlineColor = EditorGUILayout.ColorField(
                new GUIContent("Occupied Outline", "已绘制区域的行段轮廓颜色。"),
                _occupiedOutlineColor);
            _boundaryColor = EditorGUILayout.ColorField(
                new GUIContent("Boundary", "提取出来的城墙边界颜色。"),
                _boundaryColor);
            _gateColor = EditorGUILayout.ColorField(
                new GUIContent("Gate Cells", "有效城门格的预览颜色。"),
                _gateColor);
            _centerColor = EditorGUILayout.ColorField(
                new GUIContent("Grid Center", "网格中心标记颜色。"),
                _centerColor);
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

            if (GUILayout.Button(new GUIContent("Reset Colors", "恢复工具默认预览颜色。")))
            {
                ResetVisualizationColors();
                SceneView.RepaintAll();
            }
            EditorGUI.indentLevel--;
        }

        private void ResetVisualizationColors()
        {
            _gridLineColor = new Color(0.55f, 0.7f, 0.8f, 0.35f);
            _occupiedFillColor = new Color(0.15f, 0.65f, 1f, 0.18f);
            _occupiedOutlineColor = new Color(0.2f, 0.75f, 1f, 0.28f);
            _boundaryColor = new Color(1f, 0.58f, 0.12f, 1f);
            _gateColor = new Color(0.25f, 1f, 0.45f, 0.85f);
            _centerColor = new Color(1f, 0.85f, 0.15f, 1f);
        }

        private void DrawGroundSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Ground Generation", EditorStyles.boldLabel);
            _groundType = (GroundType)EditorGUILayout.EnumPopup(
                new GUIContent("Ground Type", "选择生成两三角面 Quad 或 Unity Terrain。"),
                _groundType);
            TerrainData protectedTerrainData = CityGroundGenerator.FindTerrainData(_layout);
            if (_groundType == GroundType.Quad)
            {
                _groundMaterial = (Material)EditorGUILayout.ObjectField(
                    new GUIContent("Ground Material", "可选。指定 Quad 地面使用的材质。"),
                    _groundMaterial,
                    typeof(Material),
                    false);
                _groundHeightOffset = EditorGUILayout.FloatField(
                    new GUIContent("Height Offset", "Quad 相对网格平面的 Y 轴偏移。"),
                    _groundHeightOffset);
            }
            else
            {
                if (protectedTerrainData != null)
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            "Bound Terrain Data",
                            protectedTerrainData,
                            typeof(TerrainData),
                            false);
                    }
                }
                using (new EditorGUI.DisabledScope(protectedTerrainData != null))
                {
                    _terrainLayer = (TerrainLayer)EditorGUILayout.ObjectField(
                        new GUIContent("Initial Terrain Layer", "只在首次创建 TerrainData 时设置。"),
                        _terrainLayer,
                        typeof(TerrainLayer),
                        false);
                    _terrainHeight = Mathf.Max(0.01f, EditorGUILayout.FloatField(
                        new GUIContent("Initial Terrain Height", "只在首次创建 TerrainData 时设置。"),
                        _terrainHeight));
                    _terrainHeightmapResolution = EditorGUILayout.IntPopup(
                        "Initial Heightmap Resolution",
                        _terrainHeightmapResolution,
                        new[] { "33", "65", "129", "257", "513", "1025" },
                        new[] { 33, 65, 129, 257, 513, 1025 });
                    _groundHeightOffset = EditorGUILayout.FloatField(
                        new GUIContent("Initial Height Offset", "只用于首次创建 Terrain 场景对象。"),
                        _groundHeightOffset);
                }
            }
            _outputParent = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Output Parent", "可选。地面和城墙生成到该场景节点下。"),
                _outputParent,
                typeof(Transform),
                true);

            if (_layout != null
                && CityGroundGenerator.TryGetOccupiedBounds(
                    _layout.Grid,
                    out Vector2Int min,
                    out Vector2Int maxExclusive))
            {
                int width = maxExclusive.x - min.x;
                int height = maxExclusive.y - min.y;
                EditorGUILayout.LabelField(
                    "Ground Bounds",
                    $"{width} × {height} Cells  /  "
                    + $"{width * _layout.CellSize:0.###} × {height * _layout.CellSize:0.###} Units");
            }

            string groundHelp = _groundType == GroundType.Quad
                ? "使用当前已占用格子的外接矩形生成 4 个顶点、2 个三角面的 Quad，并通过 Transform 缩放到目标范围。"
                : protectedTerrainData == null
                    ? "首次创建会生成并绑定 TerrainData。创建完成后，它会被视为正式美术资产，工具不再修改其高度、尺寸、分辨率或图层。"
                    : "TerrainData 已绑定并受保护。下面的操作只会选中现有 Terrain，或在场景对象丢失时恢复引用，不会写入 TerrainData。";
            EditorGUILayout.HelpBox(
                groundHelp,
                _groundType == GroundType.Terrain ? MessageType.Info : MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool hasOccupiedCells = _layout != null && _layout.Grid.CountOccupied() > 0;
                bool canGenerate = _groundType == GroundType.Terrain
                    ? _layout != null && (protectedTerrainData != null || hasOccupiedCells)
                    : hasOccupiedCells;
                using (new EditorGUI.DisabledScope(!canGenerate))
                {
                    string generateLabel = _groundType == GroundType.Quad
                        ? "Generate / Replace Quad"
                        : protectedTerrainData == null
                            ? "Create Terrain Once"
                            : "Select / Restore Terrain";
                    if (GUILayout.Button(generateLabel, GUILayout.Height(30f)))
                        RequestGenerateGround();
                }
                if (_groundType == GroundType.Quad)
                {
                    using (new EditorGUI.DisabledScope(_layout == null))
                    {
                        if (GUILayout.Button("Clear Quad", GUILayout.Height(30f)))
                        {
                            bool cleared = CityGroundGenerator.TryClearQuad(_layout, _outputParent);
                            _lastGenerationMessage = cleared
                                ? "已清除当前布局生成的 Quad。"
                                : "没有找到可清除的 Quad。";
                            _lastGenerationMessageType = cleared
                                ? MessageType.Info
                                : MessageType.Warning;
                        }
                    }
                }
                else if (protectedTerrainData != null
                    && GUILayout.Button("Select Terrain Data", GUILayout.Height(30f)))
                {
                    Selection.activeObject = protectedTerrainData;
                    EditorGUIUtility.PingObject(protectedTerrainData);
                }
            }
        }

        private void DrawWallSection()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Wall Generation", EditorStyles.boldLabel);
            _wallPrefabSet = (CityWallPrefabSet)EditorGUILayout.ObjectField(
                new GUIContent("Wall Prefab Set", "Straight / Corner Prefab 及可控偏移配置。"),
                _wallPrefabSet,
                typeof(CityWallPrefabSet),
                false);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Create Wall Set", "创建新的 Wall Prefab Set 资源。")))
                    CreateWallPrefabSetAsset();
                using (new EditorGUI.DisabledScope(_wallPrefabSet == null))
                {
                    if (GUILayout.Button(new GUIContent("Select Wall Set", "在 Project 与 Inspector 中定位当前配置资源。")))
                    {
                        Selection.activeObject = _wallPrefabSet;
                        EditorGUIUtility.PingObject(_wallPrefabSet);
                    }
                }
            }

            if (_wallPrefabSet != null) DrawWallPrefabSetFields();

            EditorGUILayout.HelpBox(
                "Straight / Corner 每个边界格生成一个模块；每段连续 Gate 标记只生成一座门，"
                + "并替换该段全部普通城墙格。"
                + "Straight 的长度沿 Local +X；Corner 的两条连接方向沿 Local +X / +Z。"
                + "模型轴向不一致时使用 Alignment 修正。",
                MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                bool canGenerate = _layout != null && _wallPrefabSet != null;
                using (new EditorGUI.DisabledScope(!canGenerate))
                {
                    if (GUILayout.Button("Generate / Replace Walls", GUILayout.Height(32f)))
                        GenerateWalls();
                }
                using (new EditorGUI.DisabledScope(_layout == null))
                {
                    if (GUILayout.Button("Clear Generated", GUILayout.Height(32f)))
                    {
                        bool cleared = CityWallGenerator.TryClear(_layout, _outputParent);
                        _lastGenerationMessage = cleared ? "已清除当前布局生成的城墙。" : "没有找到可清除的生成结果。";
                        _lastGenerationMessageType = cleared ? MessageType.Info : MessageType.Warning;
                    }
                }
            }
        }

        private void DrawWallPrefabSetFields()
        {
            var serializedSet = new SerializedObject(_wallPrefabSet);
            serializedSet.Update();
            DrawPrefabFieldWithSceneDrop(
                serializedSet.FindProperty("_straightPrefab"),
                "Straight Prefab",
                "占用一个格子，枢轴在格子中心，连接方向沿 Local +X。");
            DrawPrefabFieldWithSceneDrop(
                serializedSet.FindProperty("_cornerPrefab"),
                "Corner Prefab",
                "占用一个格子，枢轴在格子中心，连接方向沿 Local +X / +Z。");
            DrawPrefabFieldWithSceneDrop(
                serializedSet.FindProperty("_gatePrefab"),
                "Gate Prefab",
                "可选。枢轴在模块中心，城墙沿 Local +X 连接，门洞沿 Local +Z 穿行；每段连续 Gate 标记只生成一个，且不会自动缩放。");
            EditorGUILayout.HelpBox(
                "可以从 Project 或 Hierarchy 直接拖入。Prefab Instance 会自动绑定源 Prefab；"
                + "普通场景对象会先要求保存为 Prefab。",
                MessageType.None);

            _showPrefabAlignment = EditorGUILayout.Foldout(
                _showPrefabAlignment,
                new GUIContent("Prefab Alignment", "模型轴向或枢轴不符合约定时展开修正。"),
                true);
            if (_showPrefabAlignment)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_heightOffset"),
                    new GUIContent("Height Offset", "全部城墙相对布局平面的统一高度偏移。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_straightYawOffset"),
                    new GUIContent("Straight Yaw Offset", "直墙的 Y 轴旋转修正。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_cornerYawOffset"),
                    new GUIContent("Corner Yaw Offset", "转角的 Y 轴旋转修正。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_gateYawOffset"),
                    new GUIContent("Gate Yaw Offset", "城门的 Y 轴旋转修正。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_straightLocalOffset"),
                    new GUIContent("Straight Local Offset", "直墙在自身旋转空间中的位置修正。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_cornerLocalOffset"),
                    new GUIContent("Corner Local Offset", "转角在自身旋转空间中的位置修正。"));
                EditorGUILayout.PropertyField(
                    serializedSet.FindProperty("_gateLocalOffset"),
                    new GUIContent("Gate Local Offset", "城门在自身旋转空间中的位置修正。"));
                EditorGUI.indentLevel--;
            }
            if (serializedSet.ApplyModifiedProperties()) SceneView.RepaintAll();
        }

        private void DrawPrefabFieldWithSceneDrop(
            SerializedProperty property,
            string label,
            string tooltip)
        {
            Rect rect = EditorGUILayout.GetControlRect();
            EditorGUI.PropertyField(rect, property, new GUIContent(label, tooltip));
            HandleSceneObjectDrop(rect, property, label);
        }

        private void HandleSceneObjectDrop(
            Rect rect,
            SerializedProperty property,
            string fieldLabel)
        {
            Event current = Event.current;
            if (!rect.Contains(current.mousePosition)
                || (current.type != EventType.DragUpdated
                    && current.type != EventType.DragPerform)) return;

            GameObject dropped = GetFirstDraggedGameObject();
            if (dropped == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Link;
            if (current.type != EventType.DragPerform)
            {
                current.Use();
                return;
            }

            DragAndDrop.AcceptDrag();
            GameObject prefabAsset = ResolveOrCreatePrefabAsset(dropped, fieldLabel);
            if (prefabAsset != null)
            {
                property.objectReferenceValue = prefabAsset;
                _lastGenerationMessage = $"已将 {prefabAsset.name} 绑定到 {fieldLabel}。";
                _lastGenerationMessageType = MessageType.Info;
            }
            current.Use();
        }

        private static GameObject GetFirstDraggedGameObject()
        {
            Object[] objects = DragAndDrop.objectReferences;
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is GameObject gameObject) return gameObject;
                if (objects[i] is Component component) return component.gameObject;
            }
            return null;
        }

        private GameObject ResolveOrCreatePrefabAsset(GameObject dropped, string fieldLabel)
        {
            if (PrefabUtility.IsPartOfPrefabAsset(dropped)) return dropped;

            if (PrefabUtility.IsPartOfPrefabInstance(dropped))
            {
                GameObject instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(dropped);
                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(instanceRoot);
                if (source != null) return source;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                $"Create {fieldLabel}",
                dropped.name,
                "prefab",
                "该场景对象还不是 Prefab。请选择保存位置，工具会创建 Prefab 并自动绑定。");
            if (string.IsNullOrEmpty(path)) return null;

            GameObject created = PrefabUtility.SaveAsPrefabAsset(dropped, path);
            if (created == null)
            {
                _lastGenerationMessage = $"无法从场景对象 {dropped.name} 创建 Prefab。";
                _lastGenerationMessageType = MessageType.Error;
            }
            return created;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            // 主城网格只属于普通场景；进入 Prefab Stage 后不绘制，也不接管鼠标输入。
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                SetUnityMoveHandleHidden(false);
                return;
            }
            if (_layout == null)
            {
                SetUnityMoveHandleHidden(false);
                return;
            }
            if (_showGrid)
            {
                DrawGridAndRegion();
                DrawSceneOverlay();
            }
            HandleBuildingSnapMoveTool();
            if (!_showGrid
                || !_editInScene
                || EditorApplication.isPlayingOrWillChangePlaymode) return;

            Event current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape && _isDragging)
            {
                FinishStroke();
                current.Use();
                return;
            }
            if (current.alt) return;
            if ((current.isMouse || current.type == EventType.MouseDrag)
                && current.button != 0) return;
            if (current.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            bool hasCell = TryGetMouseCell(current.mousePosition, out Vector2Int cell);
            if (current.type == EventType.MouseDown)
            {
                if (!hasCell) return;
                BeginStroke(cell);
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _isDragging)
            {
                if (!hasCell) return;
                _dragCurrent = cell;
                if (_editMode == EditMode.Paint || _editMode == EditMode.Erase)
                    ApplyStrokeCell(cell);
                else if (_editMode == EditMode.Gate)
                    ApplyGateCell(cell);
                current.Use();
                sceneView.Repaint();
            }
            else if (current.type == EventType.MouseUp && _isDragging)
            {
                if (hasCell) _dragCurrent = cell;
                if (_editMode == EditMode.BoxFill || _editMode == EditMode.BoxErase)
                    ApplyBox();
                FinishStroke();
                current.Use();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private void BeginStroke(Vector2Int cell)
        {
            _isDragging = true;
            _dragStart = cell;
            _dragCurrent = cell;
            _strokeCells.Clear();
            _undoRecorded = false;
            BeginUndoIfNeeded();
            if (_editMode == EditMode.Paint || _editMode == EditMode.Erase)
                ApplyStrokeCell(cell);
            else if (_editMode == EditMode.Gate)
            {
                _gateStrokeState = !_layout.Grid.IsGate(cell);
                ApplyGateCell(cell);
            }
        }

        private void BeginUndoIfNeeded()
        {
            if (_undoRecorded || _layout == null) return;
            Undo.IncrementCurrentGroup();
            _activeUndoGroup = Undo.GetCurrentGroup();
            string undoName = _editMode == EditMode.Gate
                ? "Edit SLG City Gates"
                : "Paint SLG City Layout";
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(_layout, undoName);
            _undoRecorded = true;
        }

        private void ApplyStrokeCell(Vector2Int cell)
        {
            if (!_strokeCells.Add(cell)) return;
            bool occupied = _editMode == EditMode.Paint;
            if (_layout.Grid.SetOccupied(cell, occupied)) EditorUtility.SetDirty(_layout);
        }

        private void ApplyGateCell(Vector2Int cell)
        {
            if (!_strokeCells.Add(cell)) return;
            if (_gateStrokeState && !CityWallGenerator.CanPlaceGate(_layout.Grid, cell))
            {
                _lastGenerationMessage =
                    "Gate 只能标记在 Straight 边界格上；请沿同一条直墙连续拖动。";
                _lastGenerationMessageType = MessageType.Warning;
                Repaint();
                return;
            }
            if (_layout.Grid.SetGate(cell, _gateStrokeState))
            {
                EditorUtility.SetDirty(_layout);
                _lastGenerationMessage = string.Empty;
            }
        }

        private void ApplyBox()
        {
            bool occupied = _editMode == EditMode.BoxFill;
            int minX = Mathf.Min(_dragStart.x, _dragCurrent.x);
            int maxX = Mathf.Max(_dragStart.x, _dragCurrent.x);
            int minY = Mathf.Min(_dragStart.y, _dragCurrent.y);
            int maxY = Mathf.Max(_dragStart.y, _dragCurrent.y);
            bool changed = false;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                    changed |= _layout.Grid.SetOccupied(new Vector2Int(x, y), occupied);
            }
            if (changed) EditorUtility.SetDirty(_layout);
        }

        private void FinishStroke()
        {
            if (_activeUndoGroup >= 0) Undo.CollapseUndoOperations(_activeUndoGroup);
            _isDragging = false;
            _undoRecorded = false;
            _activeUndoGroup = -1;
            _strokeCells.Clear();
        }

        private bool TryGetMouseCell(Vector2 guiPosition, out Vector2Int cell)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPosition);
            var plane = new Plane(Vector3.up, _layout.Origin);
            if (plane.Raycast(ray, out float distance))
                return _layout.TryWorldToCell(ray.GetPoint(distance), out cell);
            cell = default;
            return false;
        }

        private void HandleBuildingSnapMoveTool()
        {
            Transform active = Selection.activeTransform;
            bool canUseSnapHandle = _snapBuildingsToGrid
                && !_editInScene
                && !EditorApplication.isPlayingOrWillChangePlaymode
                && Tools.current == Tool.Move
                && active != null
                && !EditorUtility.IsPersistent(active)
                && active.gameObject.scene.IsValid()
                && !IsGeneratedOutput(active);
            SetUnityMoveHandleHidden(canUseSnapHandle);
            if (!canUseSnapHandle) return;

            bool hasFootprint = TryGetBuildingFootprint(active, out Bounds bounds, out Vector2Int footprint);
            Vector3 handlePosition = Tools.pivotMode == PivotMode.Center
                ? bounds.center
                : active.position;
            Quaternion handleRotation = Tools.pivotRotation == PivotRotation.Local
                ? active.rotation
                : Quaternion.identity;

            EditorGUI.BeginChangeCheck();
            Vector3 requestedHandlePosition = Handles.PositionHandle(handlePosition, handleRotation);
            if (!EditorGUI.EndChangeCheck()) return;

            Vector3 requestedDelta = requestedHandlePosition - handlePosition;
            Vector3 requestedCenter = bounds.center + requestedDelta;
            Vector3 snappedCenter;
            if (hasFootprint)
            {
                if (!_layout.TrySnapFootprintCenter(requestedCenter, footprint, out snappedCenter)) return;
            }
            else
            {
                if (!_layout.TryWorldToNearestGridPoint(requestedCenter, out Vector2Int point)) return;
                snappedCenter = _layout.GridPointToWorld(point);
            }
            Vector3 snapCorrection = snappedCenter - requestedCenter;
            snapCorrection.y = 0f;
            Vector3 finalDelta = requestedDelta + snapCorrection;

            Transform[] selected = Selection.transforms;
            Undo.RecordObjects(selected, "Move Buildings On SLG Grid");
            for (int i = 0; i < selected.Length; i++)
            {
                Transform target = selected[i];
                if (target == null || IsGeneratedOutput(target)) continue;
                target.position += finalDelta;
                EditorUtility.SetDirty(target);
            }
        }

        private void SetUnityMoveHandleHidden(bool hidden)
        {
            if (hidden && !_toolsHiddenByCityEditor)
            {
                _previousToolsHidden = Tools.hidden;
                Tools.hidden = true;
                _toolsHiddenByCityEditor = true;
            }
            else if (!hidden && _toolsHiddenByCityEditor)
            {
                Tools.hidden = _previousToolsHidden;
                _toolsHiddenByCityEditor = false;
            }
        }

        private bool TryGetBuildingFootprint(
            Transform target,
            out Bounds bounds,
            out Vector2Int footprint)
        {
            // 模块化建筑的根 BoxCollider 表示真实占地；2D Visual 往往有旋转和装饰外扩，不能作为首选。
            BoxCollider footprintCollider = target.GetComponent<BoxCollider>();
            if (footprintCollider != null && footprintCollider.enabled)
            {
                // 编辑态拖动不能依赖物理世界尚未同步的 Collider.bounds。
                // 直接从当前矩阵计算世界包围盒，兼容偏心、父级缩放和旋转。
                Matrix4x4 matrix = target.localToWorldMatrix;
                Vector3 halfSize = footprintCollider.size * 0.5f;
                Vector3 axisX = matrix.MultiplyVector(Vector3.right * halfSize.x);
                Vector3 axisY = matrix.MultiplyVector(Vector3.up * halfSize.y);
                Vector3 axisZ = matrix.MultiplyVector(Vector3.forward * halfSize.z);
                Vector3 extents = new Vector3(
                    Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                    Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                    Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
                bounds = new Bounds(matrix.MultiplyPoint3x4(footprintCollider.center), extents * 2f);
                footprint = new Vector2Int(
                    Mathf.Max(1, Mathf.RoundToInt(bounds.size.x / _layout.CellSize)),
                    Mathf.Max(1, Mathf.RoundToInt(bounds.size.z / _layout.CellSize)));
                return true;
            }

            // 没有明确占地时，以根节点为网格锚点。模型外观和特效范围不代表占地，
            // 也不能假设它们占一格，否则角点 Pivot 会被强制偏移半格。
            bounds = new Bounds(target.position, Vector3.zero);
            footprint = Vector2Int.zero;
            return false;
        }

        private bool IsGeneratedOutput(Transform target)
        {
            string legacyGroundName = _layout.name + "_Ground";
            string quadName = _layout.name + "_GroundQuad";
            string terrainName = _layout.name + "_Terrain";
            string wallsName = _layout.name + "_Walls";
            for (Transform current = target; current != null; current = current.parent)
            {
                if (current.name == legacyGroundName
                    || current.name == quadName
                    || current.name == terrainName
                    || current.name == wallsName) return true;
            }
            return false;
        }

        private void DrawGridAndRegion()
        {
            GridData grid = _layout.Grid;
            Color previousColor = Handles.color;
            for (int y = 0; y < grid.Height; y++)
            {
                int x = 0;
                while (x < grid.Width)
                {
                    while (x < grid.Width && !grid.IsOccupied(new Vector2Int(x, y))) x++;
                    if (x >= grid.Width) break;
                    int runStart = x;
                    while (x < grid.Width && grid.IsOccupied(new Vector2Int(x, y))) x++;
                    DrawCellRect(
                        new Vector2Int(runStart, y),
                        new Vector2Int(x, y + 1),
                        _occupiedFillColor,
                        _occupiedOutlineColor);
                }
            }

            // 使用边界色淡填充实际会放置城墙的格子，预览与生成使用同一份边界格数据。
            Color wallCellFill = new Color(
                _boundaryColor.r,
                _boundaryColor.g,
                _boundaryColor.b,
                _boundaryColor.a * 0.2f);
            Color wallCellOutline = new Color(
                _boundaryColor.r,
                _boundaryColor.g,
                _boundaryColor.b,
                _boundaryColor.a * 0.55f);
            List<Vector2Int> wallCells = CityBoundaryExtractor.ExtractBoundaryCells(grid);
            for (int i = 0; i < wallCells.Count; i++)
            {
                DrawCellRect(
                    wallCells[i],
                    wallCells[i] + Vector2Int.one,
                    wallCellFill,
                    wallCellOutline);
            }

            List<CityWallGenerator.GatePlacement> gatePlacements =
                CityWallGenerator.ResolveGatePlacements(grid);
            for (int i = 0; i < gatePlacements.Count; i++)
            {
                CityWallGenerator.GatePlacement placement = gatePlacements[i];
                Color color = placement.IsValid
                    ? _gateColor
                    : new Color(1f, 0.15f, 0.12f, 0.9f);
                for (int cellIndex = 0; cellIndex < placement.Cells.Count; cellIndex++)
                {
                    Vector2Int footprintCell = placement.Cells[cellIndex];
                    DrawCellRect(
                        footprintCell,
                        footprintCell + Vector2Int.one,
                        new Color(color.r, color.g, color.b, color.a * 0.3f),
                        color);
                }
                Vector3 gateCenter = Vector3.zero;
                for (int cellIndex = 0; cellIndex < placement.Cells.Count; cellIndex++)
                    gateCenter += _layout.CellCenterToWorld(placement.Cells[cellIndex]);
                gateCenter /= placement.Cells.Count;
                Handles.color = color;
                Handles.Label(
                    gateCenter + Vector3.up * 0.08f,
                    placement.IsValid
                        ? $"Gate ({placement.Cells.Count} Cells)"
                        : "Invalid Gate");
            }

            Handles.color = _gridLineColor;
            for (int x = 0; x <= grid.Width; x++)
            {
                Handles.DrawLine(
                    _layout.GridPointToWorld(new Vector2Int(x, 0)),
                    _layout.GridPointToWorld(new Vector2Int(x, grid.Height)));
            }
            for (int y = 0; y <= grid.Height; y++)
            {
                Handles.DrawLine(
                    _layout.GridPointToWorld(new Vector2Int(0, y)),
                    _layout.GridPointToWorld(new Vector2Int(grid.Width, y)));
            }

            Handles.color = _boundaryColor;
            List<CityBoundaryExtractor.Edge> edges = CityBoundaryExtractor.ExtractEdges(grid);
            for (int i = 0; i < edges.Count; i++)
            {
                Handles.DrawAAPolyLine(
                    4f,
                    _layout.GridPointToWorld(edges[i].Start) + Vector3.up * 0.03f,
                    _layout.GridPointToWorld(edges[i].End) + Vector3.up * 0.03f);
            }

            // 明确显示整个网格的世界中心，避免与单元中心或左下角混淆。
            Handles.color = _centerColor;
            float centerSize = HandleUtility.GetHandleSize(_layout.Origin) * 0.08f;
            Handles.SphereHandleCap(
                0,
                _layout.Origin + Vector3.up * 0.04f,
                Quaternion.identity,
                centerSize,
                EventType.Repaint);
            Handles.Label(
                _layout.Origin + Vector3.up * (centerSize + 0.06f),
                "Grid Center");

            if (_isDragging && (_editMode == EditMode.BoxFill || _editMode == EditMode.BoxErase))
            {
                Color boxFill = _editMode == EditMode.BoxFill
                    ? new Color(0.2f, 0.9f, 0.45f, 0.2f)
                    : new Color(1f, 0.25f, 0.2f, 0.2f);
                int minX = Mathf.Min(_dragStart.x, _dragCurrent.x);
                int maxX = Mathf.Max(_dragStart.x, _dragCurrent.x);
                int minY = Mathf.Min(_dragStart.y, _dragCurrent.y);
                int maxY = Mathf.Max(_dragStart.y, _dragCurrent.y);
                DrawCellRect(
                    new Vector2Int(minX, minY),
                    new Vector2Int(maxX + 1, maxY + 1),
                    boxFill,
                    Color.white);
                DrawBoxSizeAtMouse(maxX - minX + 1, maxY - minY + 1);
            }
            Handles.color = previousColor;
        }

        private static void DrawBoxSizeAtMouse(int width, int height)
        {
            // Scene GUI 使用屏幕坐标，可让尺寸提示始终跟随当前鼠标，而不是固定在某个世界坐标。
            Vector2 mousePosition = Event.current.mousePosition;
            const float labelWidth = 112f;
            const float labelHeight = 24f;
            var sceneView = SceneView.currentDrawingSceneView;
            float maxX = sceneView != null
                ? Mathf.Max(4f, sceneView.position.width - labelWidth - 4f)
                : mousePosition.x + 18f;
            float maxY = sceneView != null
                ? Mathf.Max(4f, sceneView.position.height - labelHeight - 4f)
                : mousePosition.y + 18f;
            var labelRect = new Rect(
                Mathf.Clamp(mousePosition.x + 18f, 4f, maxX),
                Mathf.Clamp(mousePosition.y + 18f, 4f, maxY),
                labelWidth,
                labelHeight);

            Handles.BeginGUI();
            GUI.Label(labelRect, $"{width} × {height} Cells", EditorStyles.helpBox);
            Handles.EndGUI();
        }

        private void DrawCellRect(
            Vector2Int minInclusive,
            Vector2Int maxExclusive,
            Color fill,
            Color outline)
        {
            Vector3 a = _layout.GridPointToWorld(minInclusive) + Vector3.up * 0.01f;
            Vector3 c = _layout.GridPointToWorld(maxExclusive) + Vector3.up * 0.01f;
            Vector3 b = new Vector3(c.x, a.y, a.z);
            Vector3 d = new Vector3(a.x, a.y, c.z);
            Handles.DrawSolidRectangleWithOutline(new[] { a, b, c, d }, fill, outline);
        }

        private void DrawSceneOverlay()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(12f, 12f, 310f, 64f), EditorStyles.helpBox);
            GUILayout.Label("SLG City Editor", EditorStyles.boldLabel);
            GUILayout.Label(_editInScene
                ? $"Editing: {_editMode}  |  Alt: Scene Camera"
                : "Grid Preview  |  Enable Scene Editing in the tool window");
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private void ClearCells()
        {
            if (_layout == null) return;
            if (!EditorUtility.DisplayDialog(
                "SLG City Editor",
                "确定清空当前布局中的全部主城区域吗？此操作支持 Undo。",
                "确定清空",
                "取消")) return;
            Undo.RecordObject(_layout, "Clear SLG City Layout");
            _layout.Grid.Clear();
            EditorUtility.SetDirty(_layout);
            SceneView.RepaintAll();
        }

        private void GenerateWalls()
        {
            if (CityWallGenerator.TryGenerate(
                _layout,
                _wallPrefabSet,
                _outputParent,
                out CityWallGenerator.Result result,
                out string error))
            {
                _lastGenerationMessage =
                    $"生成完成：{result.StraightCount} 个 Straight，{result.CornerCount} 个 Corner，"
                    + $"{result.GateCount} 个 Gate。";
                if (result.InvalidGateCount > 0)
                {
                    _lastGenerationMessage +=
                        $" 有 {result.InvalidGateCount} 段 Gate 标记不是连续直线或包含 Corner，"
                        + "本次未生成，并在 Scene 中显示为红色。";
                    _lastGenerationMessageType = MessageType.Warning;
                }
                if (result.UnsupportedCount > 0)
                {
                    _lastGenerationMessage +=
                        $" 其中 {result.UnsupportedCount} 个格子存在三向、四向或孤立连接，"
                        + "当前使用 Straight 近似；后续需要 T / Cross 模块才能完整表达。";
                    _lastGenerationMessageType = MessageType.Warning;
                }
                else if (result.InvalidGateCount == 0)
                {
                    _lastGenerationMessageType = MessageType.Info;
                }
            }
            else
            {
                _lastGenerationMessage = error;
                _lastGenerationMessageType = MessageType.Error;
            }
        }

        private void RequestGenerateGround()
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                GenerateGround();
                Repaint();
                SceneView.RepaintAll();
            };
        }

        private void GenerateGround()
        {
            if (_groundType == GroundType.Terrain)
            {
                if (CityGroundGenerator.TryCreateOrRestoreTerrain(
                    _layout,
                    _terrainLayer,
                    _terrainHeight,
                    _terrainHeightmapResolution,
                    _groundHeightOffset,
                    _outputParent,
                    out GameObject terrainObject,
                    out TerrainData terrainData,
                    out bool createdTerrainData,
                    out string terrainError))
                {
                    _lastGenerationMessage = createdTerrainData
                        ? $"Terrain 首次创建完成：{AssetDatabase.GetAssetPath(terrainData)}。"
                            + "该 TerrainData 已绑定，工具后续不会再修改它。"
                        : "已选中或恢复 Terrain 场景对象；TerrainData 没有被修改。";
                    _lastGenerationMessageType = MessageType.Info;
                }
                else
                {
                    _lastGenerationMessage = terrainError;
                    _lastGenerationMessageType = MessageType.Error;
                }
                return;
            }

            if (CityGroundGenerator.TryGenerateQuad(
                _layout,
                _groundMaterial,
                _groundHeightOffset,
                _outputParent,
                out GameObject ground,
                out string error))
            {
                MeshFilter meshFilter = ground.GetComponent<MeshFilter>();
                int triangleCount = meshFilter != null && meshFilter.sharedMesh != null
                    ? meshFilter.sharedMesh.triangles.Length / 3
                    : 0;
                _lastGenerationMessage = $"地面生成完成：1 个 Quad，{triangleCount} 个三角面。";
                _lastGenerationMessageType = MessageType.Info;
                return;
            }

            _lastGenerationMessage = error;
            _lastGenerationMessageType = MessageType.Error;
        }

        private void CreateLayoutAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create City Layout Data",
                "CityLayoutData",
                "asset",
                "请选择布局数据的保存位置。");
            if (string.IsNullOrEmpty(path)) return;
            var asset = CreateInstance<CityLayoutData>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _layout = asset;
            SyncPendingGridSize();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            SceneView.RepaintAll();
        }

        private void CreateWallPrefabSetAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Wall Prefab Set",
                "CityWallPrefabSet",
                "asset",
                "请选择城墙配置的保存位置。");
            if (string.IsNullOrEmpty(path)) return;
            var asset = CreateInstance<CityWallPrefabSet>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _wallPrefabSet = asset;
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private void SyncPendingGridSize()
        {
            if (_layout == null) return;
            _pendingWidth = _layout.Grid.Width;
            _pendingHeight = _layout.Grid.Height;
        }

        private static void DrawSignature()
        {
            var authorStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
            };
            EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
            EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
        }
    }
}
