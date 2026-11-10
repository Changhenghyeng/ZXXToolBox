using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FogOfWarTool.Editor
{
    public class FogOfWarToolWindow : EditorWindow
    {
        private const string PrefKey = "FogGridTool.Settings_v1";
        private const string DebugGridName = "Fog Grid Debug Overlay";
        private const string DebugGridShaderName = "Hidden/Fog Of War/Grid Debug Overlay";
        private const string DebugGridShaderAssetPath = "Assets/Scripts/Editor/ArtTools/ZXXTools/FogOfWarTool/FogGridDebugOverlay.shader";
        private static readonly GUIContent[] ViewTabs =
        {
            new GUIContent("Create Grid", "配置并创建一个可点击交互的战争迷雾网格。"),
            new GUIContent("Selected Grid", "编辑和测试当前选中的战争迷雾网格。")
        };

        private Vector2 _scroll;
        private int _viewMode;
        private int _selectedGridInstanceId;
        private int _selectedColumns;
        private int _selectedRows;
        private int _columns = 10;
        private int _rows = 8;
        private Vector2 _worldOrigin = Vector2.zero;
        private Vector2 _worldSize = new Vector2(10f, 8f);
        private float _heightOffset = 0.05f;
        private Color _fogColor = new Color(1f, 1f, 1f, 0.55f);
        private int _clickAreaSize = 2;
        private float _transitionDuration = 1f;
        private Color _debugGridColor = new Color(0f, 1f, 1f, 0.85f);
        private float _debugGridThickness = 0.02f;
        private bool _forceSquareCells = true;
        private PivotChoice _pivot = PivotChoice.BottomLeft;
        private string _assetFolder = "Assets/LocalArtRes/FogOfWar";

        private enum PivotChoice
        {
            BottomLeft,
            Center
        }

        public static void Open()
        {
            FogOfWarToolWindow window = GetWindow<FogOfWarToolWindow>("Fog Grid Tool");
            window.minSize = new Vector2(420f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
            Selection.selectionChanged += Repaint;
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= Repaint;
            SaveSettings();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Fog of War Grid", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);

            _viewMode = GUILayout.Toolbar(_viewMode, ViewTabs);
            EditorGUILayout.Space(8f);

            if (_viewMode == 0)
            {
                DrawBoundsSection();
                EditorGUILayout.Space(6f);
                DrawGridSection();
                EditorGUILayout.Space(6f);
                DrawCreateSection();
            }
            else
            {
                DrawSelectedTools();
            }

            EditorGUILayout.EndScrollView();

            DrawSignature();
        }

        private static void DrawSignature()
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

        private void DrawBoundsSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Grid Area", EditorStyles.boldLabel);
                _pivot = (PivotChoice)EditorGUILayout.EnumPopup(
                    new GUIContent("Pivot", "设置 Origin XZ 在矩形网格区域中的锚点方式。"),
                    _pivot);
                _worldOrigin = EditorGUILayout.Vector2Field(
                    new GUIContent("Origin XZ", "网格原点在世界空间中的 XZ 坐标。"),
                    _worldOrigin);
                _worldSize = EditorGUILayout.Vector2Field(
                    new GUIContent("Size XZ", "网格在世界空间中覆盖的总宽度和总深度。"),
                    _worldSize);
                _heightOffset = EditorGUILayout.FloatField(
                    new GUIContent("Height Offset", "相对于选中物体包围盒顶部的垂直偏移。"),
                    _heightOffset);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(
                            "Fit Selection Top View",
                            "根据选中物体的顶视图包围盒自动设置 Origin XZ 和 Size XZ。")))
                    {
                        FitToSelectionBounds();
                    }

                    if (GUILayout.Button(new GUIContent(
                            "Reset Origin",
                            "将 Origin XZ 重置为世界原点 (0, 0)。")))
                    {
                        _worldOrigin = Vector2.zero;
                    }
                }
            }
        }

        private void DrawGridSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Grid Settings", EditorStyles.boldLabel);
                _columns = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent("Columns", "世界空间 X 方向的网格数量。"),
                        _columns));
                _rows = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent("Rows", "世界空间 Z 方向的网格数量。"),
                        _rows));
                _forceSquareCells = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Force Square Cells",
                        "自动调整最终网格区域，使每个格子的宽度和高度相等。"),
                    _forceSquareCells);

                Vector2 previewSize = GetResolvedWorldSize();
                float cellW = Mathf.Max(0.001f, previewSize.x) / _columns;
                float cellH = Mathf.Max(0.001f, previewSize.y) / _rows;
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Vector2Field(
                        new GUIContent("Cell Size", "单个格子计算后的世界空间尺寸。"),
                        new Vector2(cellW, cellH));
                    EditorGUILayout.IntField(
                        new GUIContent("Cell Count", "格子总数，等于 Columns 乘以 Rows。"),
                        _columns * _rows);
                }

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Fog and Interaction", EditorStyles.boldLabel);
                _fogColor = EditorGUILayout.ColorField(
                    new GUIContent("Fog Color", "新建战争迷雾材质使用的初始颜色和透明度。"),
                    _fogColor);
                _clickAreaSize = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent(
                            "Click Area (N x N)",
                            "单次点击影响的格子范围。数值为 2 时会影响 2 x 2 个格子。"),
                        _clickAreaSize));
                _transitionDuration = Mathf.Max(
                    0f,
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Transition (Seconds)",
                            "云雾消散和出现的过渡时间。设置为 0 时立即完成变化。"),
                        _transitionDuration));
            }
        }

        private void DrawCreateSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                _assetFolder = EditorGUILayout.TextField(
                    new GUIContent("Asset Folder", "用于保存新生成战争迷雾材质的项目文件夹。"),
                    _assetFolder);

                if (GUILayout.Button(
                        new GUIContent(
                            "Create Fog Grid",
                            "创建运行时网格组件、云雾面片、碰撞体和材质。"),
                        GUILayout.Height(32f)))
                {
                    CreateGrid();
                }
            }
        }

        private void DrawSelectedTools()
        {
            Component grid = FindSelectedGrid();
            if (grid == null)
            {
                EditorGUILayout.HelpBox(
                    "Select a generated Fog Grid object or its Fog Mask Surface.",
                    MessageType.Info);
                return;
            }

            UnityEngine.Object gridObject = grid as UnityEngine.Object;
            SyncSelectedGridResolution(grid);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Selected Grid", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField(
                        new GUIContent("Object", "当前由此面板控制的战争迷雾网格对象。"),
                        gridObject,
                        grid.GetType(),
                        true);
                }

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Grid Resolution", EditorStyles.boldLabel);
                _selectedColumns = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent(
                            "Columns",
                            "修改当前面片在世界空间 X 方向划分的格子数量。"),
                        _selectedColumns));
                _selectedRows = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent(
                            "Rows",
                            "修改当前面片在世界空间 Z 方向划分的格子数量。"),
                        _selectedRows));

                Vector2 selectedWorldSize = GetVector2Property(grid, "WorldSize", Vector2.one);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Vector2Field(
                        new GUIContent(
                            "Cell Size",
                            "修改分辨率后单个格子的世界空间尺寸。"),
                        new Vector2(
                            selectedWorldSize.x / _selectedColumns,
                            selectedWorldSize.y / _selectedRows));
                    EditorGUILayout.IntField(
                        new GUIContent("Cell Count", "修改后的格子总数。"),
                        _selectedColumns * _selectedRows);
                }

                int currentColumns = Mathf.Max(1, GetIntProperty(grid, "Columns", _selectedColumns));
                int currentRows = Mathf.Max(1, GetIntProperty(grid, "Rows", _selectedRows));
                bool resolutionChanged =
                    _selectedColumns != currentColumns
                    || _selectedRows != currentRows;
                using (new EditorGUI.DisabledScope(!resolutionChanged || EditorApplication.isPlaying))
                {
                    if (GUILayout.Button(new GUIContent(
                            "Apply Grid Resolution (Reset Mask)",
                            "保持面片世界尺寸不变，重新划分网格并将 Mask 重置为全云雾状态。")))
                    {
                        ApplySelectedGridResolution(grid, _selectedColumns, _selectedRows);
                    }
                }

                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Interaction", EditorStyles.boldLabel);
                int currentClickAreaSize = GetIntField(grid, "clickAreaSize", _clickAreaSize);
                float currentTransitionDuration = GetFloatField(
                    grid,
                    "transitionDuration",
                    _transitionDuration);

                EditorGUI.BeginChangeCheck();
                int nextClickAreaSize = Mathf.Max(
                    1,
                    EditorGUILayout.IntField(
                        new GUIContent(
                            "Click Area (N x N)",
                            "鼠标左键消散和右键恢复使用的格子范围，两种操作的范围完全相同。"),
                        currentClickAreaSize));
                float nextTransitionDuration = Mathf.Max(
                    0f,
                    EditorGUILayout.FloatField(
                        new GUIContent(
                            "Transition (Seconds)",
                            "当前网格在云雾消散和出现时共同使用的过渡时间。"),
                        currentTransitionDuration));
                if (EditorGUI.EndChangeCheck())
                {
                    if (gridObject != null) Undo.RecordObject(gridObject, "Change Fog Grid Interaction");
                    SetField(grid, "clickAreaSize", nextClickAreaSize);
                    SetField(grid, "transitionDuration", nextTransitionDuration);
                    if (gridObject != null) EditorUtility.SetDirty(gridObject);
                }
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Mask Controls", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(
                            "Show All",
                            "按照当前过渡时间恢复所有格子的云雾。")))
                    {
                        InvokeNoArg(grid, "ShowAll");
                    }

                    if (GUILayout.Button(new GUIContent(
                            "Hide All",
                            "按照当前过渡时间消散所有格子的云雾。")))
                    {
                        InvokeNoArg(grid, "HideAll");
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(
                            "Reset Mask",
                            "重新创建运行时 Mask，并恢复为初始的全云雾状态。")))
                    {
                        InvokeNoArg(grid, "ResetMask");
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent(
                            "Ping Runtime Mask",
                            "在 Unity 编辑器中定位并高亮运行时生成的 Mask 贴图。")))
                    {
                        PingRuntimeMask(grid);
                    }

                    if (GUILayout.Button(new GUIContent(
                            "Export Mask PNG",
                            "将当前运行时 Mask 贴图导出为 PNG 资源。")))
                    {
                        ExportRuntimeMask(grid);
                    }
                }
            }

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Debug Grid", EditorStyles.boldLabel);
                bool debugGridVisible = IsDebugGridVisible(grid);
                bool nextDebugGridVisible = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Visible",
                        "在 Game View 中显示参考网格，不会参与或影响战争迷雾 Mask。"),
                    debugGridVisible);
                if (nextDebugGridVisible != debugGridVisible)
                {
                    SetDebugGridVisible(grid, nextDebugGridVisible);
                }

                if (nextDebugGridVisible)
                {
                    _debugGridColor = EditorGUILayout.ColorField(
                        new GUIContent("Color", "Game View 参考网格的颜色和透明度。"),
                        _debugGridColor);
                    _debugGridThickness = Mathf.Max(
                        0.001f,
                        EditorGUILayout.FloatField(
                            new GUIContent(
                                "Line Thickness",
                                "参考网格线的宽度，使用世界空间单位。"),
                            _debugGridThickness));

                    if (GUILayout.Button(new GUIContent(
                            "Refresh Debug Grid",
                            "修改颜色或线宽后，重新生成参考网格。")))
                    {
                        RefreshDebugGrid(grid);
                    }
                }
            }
        }

        private void SyncSelectedGridResolution(Component grid)
        {
            int instanceId = grid == null ? 0 : grid.GetInstanceID();
            if (_selectedGridInstanceId == instanceId) return;

            _selectedGridInstanceId = instanceId;
            _selectedColumns = Mathf.Max(1, GetIntProperty(grid, "Columns", _columns));
            _selectedRows = Mathf.Max(1, GetIntProperty(grid, "Rows", _rows));
        }

        private void ApplySelectedGridResolution(Component grid, int columns, int rows)
        {
            if (grid == null || EditorApplication.isPlaying) return;

            columns = Mathf.Max(1, columns);
            rows = Mathf.Max(1, rows);
            if (!EditorUtility.DisplayDialog(
                    "应用网格分辨率",
                    "修改网格分辨率会重建运行时 Mask，并将全部格子重置为云雾状态。是否继续？",
                    "应用",
                    "取消"))
            {
                return;
            }

            Undo.RecordObject(grid, "Change Fog Grid Resolution");
            SetField(grid, "columns", columns);
            SetField(grid, "rows", rows);
            InvokeNoArg(grid, "RebuildGrid");
            EditorUtility.SetDirty(grid);

            _selectedColumns = columns;
            _selectedRows = rows;
            if (FindDebugGridTransform(grid.transform) != null)
            {
                RefreshDebugGrid(grid);
            }

            SceneView.RepaintAll();
            Repaint();
        }

        private void CreateGrid()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            Type gridType = FindRuntimeGridType(true);
            if (gridType == null) return;

            string folder = NormalizeAssetFolder(_assetFolder);
            Directory.CreateDirectory(AssetPathToFullPath(folder));

            Material maskMaterial = CreateMaskPreviewMaterial(folder);
            Bounds? selectedBounds = GetSelectionBounds();
            float height = selectedBounds.HasValue ? selectedBounds.Value.max.y + _heightOffset : _heightOffset;

            GameObject root = new GameObject("Fog Grid Click System");
            Undo.RegisterCreatedObjectUndo(root, "Create Fog Grid Click System");
            root.transform.position = Vector3.zero;

            Component grid = root.AddComponent(gridType);
            SetField(grid, "inputCamera", Camera.main);
            SetField(grid, "clickAreaSize", Mathf.Max(1, _clickAreaSize));
            SetField(grid, "transitionDuration", Mathf.Max(0f, _transitionDuration));

            MethodInfo configure = gridType.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public);
            Type pivotType = gridType.GetNestedType("PivotType");
            object pivotValue = Enum.Parse(pivotType, _pivot.ToString());
            Vector2 resolvedSize = GetResolvedWorldSize();
            configure.Invoke(grid, new object[]
            {
                Mathf.Max(1, _columns),
                Mathf.Max(1, _rows),
                GetResolvedWorldOrigin(resolvedSize),
                resolvedSize,
                height,
                pivotValue
            });

            MeshRenderer previewRenderer = CreateMaskSurface(root.transform, resolvedSize, height + 0.01f, maskMaterial);
            SetField(grid, "maskPreviewMaterial", maskMaterial);
            SetField(grid, "maskPreviewRenderers", new Renderer[] { previewRenderer });
            SetField(grid, "maskPixelsPerCell", 1);
            SetField(grid, "maskFilterMode", FilterMode.Bilinear);
            SetField(grid, "maskWrapMode", TextureWrapMode.Clamp);

            InvokeNoArg(grid, "RebuildGrid");

            ZXXToolUsageStats.Complete("Fog Of War Tool", 1, usageTimer);
            Selection.activeGameObject = root;
            _viewMode = 1;
            EditorGUIUtility.PingObject(root);
        }

        private Material CreateMaskPreviewMaterial(string folder)
        {
            Shader shader = Shader.Find("Custom/Fog Of War");
            if (shader == null) shader = Shader.Find("ZXX/Fog Grid Mask Preview");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/FogGrid_MaskPreview.mat");
            Material material = new Material(shader) { name = "FogGrid_MaskPreview" };

            ConfigureMaskMaterial(material);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 60;

            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private void ConfigureMaskMaterial(Material material)
        {
            if (material == null) return;
            if (material.HasProperty("_FogColor")) material.SetColor("_FogColor", _fogColor);
            if (material.HasProperty("_CloudColor")) material.SetColor("_CloudColor", _fogColor);
            if (material.HasProperty("_DissolveStrength")) material.SetFloat("_DissolveStrength", 1f);
            if (material.HasProperty("_DissolveSpeedX")) material.SetFloat("_DissolveSpeedX", 0.1f);
            if (material.HasProperty("_DissolveSpeedY")) material.SetFloat("_DissolveSpeedY", 0.06f);
            if (material.HasProperty("_TillingFactor")) material.SetFloat("_TillingFactor", 2f);
            if (material.HasProperty("_CloudSpeed"))
            {
                material.SetVector("_CloudSpeed", new Vector4(0.018f, 0.011f, -0.013f, -0.017f));
            }
            if (material.HasProperty("_MaskEdgeMin")) material.SetFloat("_MaskEdgeMin", 0.2f);
            if (material.HasProperty("_MaskEdgeMax")) material.SetFloat("_MaskEdgeMax", 0.8f);
            if (material.HasProperty("_MaskEdgePower")) material.SetFloat("_MaskEdgePower", 1f);
            if (material.HasProperty("_MaskDistortCenterX")) material.SetFloat("_MaskDistortCenterX", 0.5f);
            if (material.HasProperty("_MaskDistortCenterY")) material.SetFloat("_MaskDistortCenterY", 0.5f);
            if (material.HasProperty("_MaskDistortStrength")) material.SetFloat("_MaskDistortStrength", 10f);
            if (material.HasProperty("_ShadowOffset")) material.SetVector("_ShadowOffset", Vector4.zero);
            if (material.HasProperty("_EnableDepthOcclusion")) material.SetFloat("_EnableDepthOcclusion", 1f);
            if (material.HasProperty("_DepthOcclusionDistance")) material.SetFloat("_DepthOcclusionDistance", 1f);
            if (material.HasProperty("_DepthOcclusionStrength")) material.SetFloat("_DepthOcclusionStrength", 1f);
            if (material.HasProperty("_DepthOcclusionOffset")) material.SetFloat("_DepthOcclusionOffset", 0f);
            SetMaterialColor(material, _fogColor);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 60;
            EditorUtility.SetDirty(material);
        }

        private MeshRenderer CreateMaskSurface(Transform parent, Vector2 size, float height, Material material)
        {
            GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Undo.RegisterCreatedObjectUndo(surface, "Create Fog Mask Surface");
            surface.name = "Fog Mask Surface";
            surface.transform.SetParent(parent, false);
            surface.transform.position = new Vector3(GetGridCenter().x, height, GetGridCenter().y);
            surface.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            surface.transform.localScale = new Vector3(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y), 1f);

            MeshRenderer renderer = surface.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = material;
            return renderer;
        }

        private void SetDebugGridVisible(object grid, bool visible)
        {
            GameObject debugGrid = GetOrCreateDebugGrid(grid);
            if (debugGrid == null) return;
            debugGrid.SetActive(visible);
            if (visible) RefreshDebugGrid(grid);
        }

        private bool IsDebugGridVisible(object grid)
        {
            Transform root = GetGridTransform(grid);
            if (root == null) return false;
            Transform debugGrid = FindDebugGridTransform(root);
            return debugGrid != null && debugGrid.gameObject.activeSelf;
        }

        private void RefreshDebugGrid(object grid)
        {
            GameObject debugGrid = GetOrCreateDebugGrid(grid);
            if (debugGrid == null) return;

            MeshFilter meshFilter = debugGrid.GetComponent<MeshFilter>();
            MeshRenderer meshRenderer = debugGrid.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshRenderer == null) return;

            int columns = Mathf.Max(1, GetIntProperty(grid, "Columns", _columns));
            int rows = Mathf.Max(1, GetIntProperty(grid, "Rows", _rows));
            Vector2 origin = GetVector2Property(grid, "WorldOrigin", _worldOrigin);
            Vector2 size = GetVector2Property(grid, "WorldSize", _worldSize);
            float height = GetFloatField(grid, "gridHeight", _heightOffset) + 0.03f;
            object pivot = GetFieldValue(grid, "pivotType");
            if (pivot != null && pivot.ToString() == "Center")
            {
                origin -= size * 0.5f;
            }

            Transform root = GetGridTransform(grid);
            int lineCount = columns + 1 + rows + 1;
            Vector3[] vertices = new Vector3[lineCount * 4];
            int[] indices = new int[lineCount * 6];
            int v = 0;
            int t = 0;
            float thickness = Mathf.Max(0.001f, _debugGridThickness);

            for (int x = 0; x <= columns; x++)
            {
                float px = origin.x + size.x * x / columns;
                AddDebugLineQuad(
                    root,
                    vertices,
                    indices,
                    ref v,
                    ref t,
                    new Vector3(px, height, origin.y),
                    new Vector3(px, height, origin.y + size.y),
                    thickness);
            }

            for (int y = 0; y <= rows; y++)
            {
                float pz = origin.y + size.y * y / rows;
                AddDebugLineQuad(
                    root,
                    vertices,
                    indices,
                    ref v,
                    ref t,
                    new Vector3(origin.x, height, pz),
                    new Vector3(origin.x + size.x, height, pz),
                    thickness);
            }

            Mesh mesh = meshFilter.sharedMesh;
            if (mesh == null)
            {
                mesh = new Mesh { name = "Fog Grid Debug Lines Mesh" };
                meshFilter.sharedMesh = mesh;
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateBounds();

            Material material = meshRenderer.sharedMaterial;
            if (material != null)
            {
                ConfigureDebugGridMaterial(material);
            }
        }

        private static void AddDebugLineQuad(
            Transform root,
            Vector3[] vertices,
            int[] indices,
            ref int vertexIndex,
            ref int triangleIndex,
            Vector3 start,
            Vector3 end,
            float thickness)
        {
            Vector3 direction = end - start;
            Vector3 side = Vector3.Cross(Vector3.up, direction).normalized * (thickness * 0.5f);
            if (side.sqrMagnitude <= 0.000001f) side = Vector3.right * (thickness * 0.5f);

            int baseIndex = vertexIndex;
            vertices[vertexIndex++] = root.InverseTransformPoint(start - side);
            vertices[vertexIndex++] = root.InverseTransformPoint(start + side);
            vertices[vertexIndex++] = root.InverseTransformPoint(end + side);
            vertices[vertexIndex++] = root.InverseTransformPoint(end - side);

            indices[triangleIndex++] = baseIndex;
            indices[triangleIndex++] = baseIndex + 1;
            indices[triangleIndex++] = baseIndex + 2;
            indices[triangleIndex++] = baseIndex;
            indices[triangleIndex++] = baseIndex + 2;
            indices[triangleIndex++] = baseIndex + 3;
        }

        private GameObject GetOrCreateDebugGrid(object grid)
        {
            Transform root = GetGridTransform(grid);
            if (root == null) return null;

            Transform existing = FindDebugGridTransform(root);
            if (existing != null)
            {
                existing.name = DebugGridName;
                existing.gameObject.hideFlags = HideFlags.DontSaveInBuild;
                return existing.gameObject;
            }

            GameObject debugGrid = new GameObject(DebugGridName);
            Undo.RegisterCreatedObjectUndo(debugGrid, "Create Fog Grid Debug Overlay");
            debugGrid.transform.SetParent(root, false);
            debugGrid.transform.localPosition = Vector3.zero;
            debugGrid.transform.localRotation = Quaternion.identity;
            debugGrid.transform.localScale = Vector3.one;
            debugGrid.hideFlags = HideFlags.DontSaveInBuild;
            debugGrid.AddComponent<MeshFilter>();
            MeshRenderer renderer = debugGrid.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateDebugGridMaterial(NormalizeAssetFolder(_assetFolder));
            return debugGrid;
        }

        private static Transform FindDebugGridTransform(Transform root)
        {
            if (root == null) return null;
            Transform debugGrid = root.Find(DebugGridName);
            if (debugGrid != null) return debugGrid;
            return root.Find("Fog Grid Debug Lines");
        }

        private Material CreateDebugGridMaterial(string folder)
        {
            Directory.CreateDirectory(AssetPathToFullPath(folder));
            AssetDatabase.Refresh();

            string path = folder + "/FogGrid_DebugOverlay.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null)
            {
                ConfigureDebugGridMaterial(material);
                return material;
            }

            Shader shader = FindDebugGridShader();
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            material = new Material(shader) { name = "FogGrid_DebugOverlay" };
            ConfigureDebugGridMaterial(material);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            return material;
        }

        private void ConfigureDebugGridMaterial(Material material)
        {
            if (material == null) return;

            Shader debugShader = FindDebugGridShader();
            if (debugShader != null && material.shader != debugShader)
            {
                material.shader = debugShader;
            }

            SetMaterialColor(material, _debugGridColor);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            ConfigureTransparentMaterial(material);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 100;
            EditorUtility.SetDirty(material);
        }

        private static Shader FindDebugGridShader()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(DebugGridShaderAssetPath);
            return shader != null ? shader : Shader.Find(DebugGridShaderName);
        }

        private static void SetMaterialColor(Material material, Color color)
        {
            if (material == null) return;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        private static void ConfigureTransparentMaterial(Material material)
        {
            if (material == null) return;
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 80;
        }

        private void FitToSelectionBounds()
        {
            Bounds? bounds = GetSelectionBounds();
            if (!bounds.HasValue)
            {
                EditorUtility.DisplayDialog("Fog Grid Tool", "当前选中对象里没有找到 Renderer 包围盒。", "OK");
                return;
            }

            Bounds b = bounds.Value;
            Vector2 boundsSize = new Vector2(Mathf.Max(0.001f, b.size.x), Mathf.Max(0.001f, b.size.z));
            if (_forceSquareCells)
            {
                float cellSize = Mathf.Max(boundsSize.x / Mathf.Max(1, _columns), boundsSize.y / Mathf.Max(1, _rows));
                _worldSize = new Vector2(cellSize * Mathf.Max(1, _columns), cellSize * Mathf.Max(1, _rows));
                Vector2 center = new Vector2(b.center.x, b.center.z);
                _worldOrigin = _pivot == PivotChoice.Center ? center : center - _worldSize * 0.5f;
            }
            else
            {
                _worldSize = boundsSize;
                _worldOrigin = _pivot == PivotChoice.Center
                    ? new Vector2(b.center.x, b.center.z)
                    : new Vector2(b.min.x, b.min.z);
            }
        }

        private Vector2 GetResolvedWorldSize()
        {
            Vector2 size = new Vector2(Mathf.Max(0.001f, _worldSize.x), Mathf.Max(0.001f, _worldSize.y));
            if (!_forceSquareCells) return size;

            float cellSize = Mathf.Max(size.x / Mathf.Max(1, _columns), size.y / Mathf.Max(1, _rows));
            return new Vector2(cellSize * Mathf.Max(1, _columns), cellSize * Mathf.Max(1, _rows));
        }

        private Vector2 GetResolvedWorldOrigin(Vector2 resolvedSize)
        {
            if (!_forceSquareCells) return _worldOrigin;

            Vector2 currentSize = new Vector2(Mathf.Max(0.001f, _worldSize.x), Mathf.Max(0.001f, _worldSize.y));
            if (_pivot == PivotChoice.Center) return _worldOrigin;

            Vector2 center = _worldOrigin + currentSize * 0.5f;
            return center - resolvedSize * 0.5f;
        }

        private Vector2 GetGridCenter()
        {
            Vector2 resolvedSize = GetResolvedWorldSize();
            Vector2 resolvedOrigin = GetResolvedWorldOrigin(resolvedSize);
            return _pivot == PivotChoice.Center ? resolvedOrigin : resolvedOrigin + resolvedSize * 0.5f;
        }

        private Bounds? GetSelectionBounds()
        {
            Bounds? merged = null;
            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null) continue;
                Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    if (merged.HasValue)
                    {
                        Bounds value = merged.Value;
                        value.Encapsulate(renderer.bounds);
                        merged = value;
                    }
                    else
                    {
                        merged = renderer.bounds;
                    }
                }
            }

            return merged;
        }

        private Component FindSelectedGrid()
        {
            Type gridType = FindRuntimeGridType(false);
            if (gridType == null || Selection.activeGameObject == null) return null;

            Component grid = Selection.activeGameObject.GetComponent(gridType);
            if (grid == null) grid = Selection.activeGameObject.GetComponentInParent(gridType);
            if (grid != null) return grid;

            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null) continue;
                grid = go.GetComponent(gridType);
                if (grid == null) grid = go.GetComponentInParent(gridType);
                if (grid != null) return grid;
            }

            return grid;
        }

        private static Type FindRuntimeGridType(bool showError)
        {
            Type type = Type.GetType("ArtCode.FogGridClickSystem, ArtCode");
            if (type == null) type = Type.GetType("ArtCode.FogGridClickSystem, Assembly-CSharp");
            if (type == null && showError)
            {
                EditorUtility.DisplayDialog(
                    "Fog Grid Tool",
                    "没有找到 ArtCode.FogGridClickSystem。请先确认 Unity 已经编译 Assets/Scripts/HotUpdate/ArtCode/Helper/FogOfWar/FogGridClickSystem.cs，然后再重试。",
                    "OK");
            }

            return type;
        }

        private static void SetField(object target, string fieldName, object value)
        {
            if (target == null) return;
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) field.SetValue(target, value);
        }

        private static void InvokeNoArg(object target, string methodName)
        {
            if (target == null) return;
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            if (method != null) method.Invoke(target, null);
        }

        private static void PingRuntimeMask(object grid)
        {
            Texture2D mask = GetTextureProperty(grid, "MaskTexture");
            if (mask != null) EditorGUIUtility.PingObject(mask);
        }

        private void ExportRuntimeMask(object grid)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            Texture2D mask = GetTextureProperty(grid, "MaskTexture");
            if (mask == null) return;

            string folder = NormalizeAssetFolder(_assetFolder);
            Directory.CreateDirectory(AssetPathToFullPath(folder));
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/FogGrid_RuntimeMask.png");
            File.WriteAllBytes(AssetPathToFullPath(path), mask.EncodeToPNG());
            ZXXToolUsageStats.Complete("Fog Of War Tool", 1, usageTimer);
            AssetDatabase.ImportAsset(path);
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
        }

        private void RefreshSelectedCloudMaterial(object grid)
        {
            Material material = GetFieldValue(grid, "maskPreviewMaterial") as Material;
            if (material == null)
            {
                Renderer[] renderers = GetFieldValue(grid, "maskPreviewRenderers") as Renderer[];
                if (renderers != null)
                {
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        if (renderers[i] == null || renderers[i].sharedMaterial == null) continue;
                        material = renderers[i].sharedMaterial;
                        break;
                    }
                }
            }

            if (material == null)
            {
                EditorUtility.DisplayDialog("Fog Grid Tool", "当前选中的 Fog Grid 上没有找到材质球。", "OK");
                return;
            }

            string folder = NormalizeAssetFolder(_assetFolder);
            Directory.CreateDirectory(AssetPathToFullPath(folder));
            ConfigureMaskMaterial(material);
            SetField(grid, "maskPixelsPerCell", 1);
            SetField(grid, "maskFilterMode", FilterMode.Bilinear);
            SetField(grid, "maskWrapMode", TextureWrapMode.Clamp);
            InvokeNoArg(grid, "RebuildMaskTexture");
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(material);
        }

        private static Texture2D GetTextureProperty(object target, string propertyName)
        {
            if (target == null) return null;
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property == null ? null : property.GetValue(target, null) as Texture2D;
        }

        private static object GetFieldValue(object target, string fieldName)
        {
            if (target == null) return null;
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(target);
        }

        private static Transform GetGridTransform(object grid)
        {
            Component component = grid as Component;
            return component == null ? null : component.transform;
        }

        private static int GetIntProperty(object target, string propertyName, int fallback)
        {
            if (target == null) return fallback;
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property == null ? fallback : (int)property.GetValue(target, null);
        }

        private static Vector2 GetVector2Property(object target, string propertyName, Vector2 fallback)
        {
            if (target == null) return fallback;
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property == null ? fallback : (Vector2)property.GetValue(target, null);
        }

        private static float GetFloatField(object target, string fieldName, float fallback)
        {
            if (target == null) return fallback;
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? fallback : (float)field.GetValue(target);
        }

        private static int GetIntField(object target, string fieldName, int fallback)
        {
            if (target == null) return fallback;
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? fallback : (int)field.GetValue(target);
        }

        private static string NormalizeAssetFolder(string folder)
        {
            folder = string.IsNullOrEmpty(folder) ? "Assets" : folder.Replace('\\', '/').Trim();
            folder = folder.TrimEnd('/');
            int assetsIndex = folder.IndexOf("Assets", StringComparison.OrdinalIgnoreCase);
            if (assetsIndex >= 0) folder = "Assets" + folder.Substring(assetsIndex + "Assets".Length);
            else folder = "Assets/" + folder.TrimStart('/');
            return string.IsNullOrEmpty(folder) ? "Assets" : folder;
        }

        private static string AssetPathToFullPath(string assetPath)
        {
            DirectoryInfo projectRoot = Directory.GetParent(Application.dataPath);
            return projectRoot == null ? string.Empty : Path.Combine(projectRoot.FullName, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private void SaveSettings()
        {
            EditorPrefs.SetInt(PrefKey + ".columns", _columns);
            EditorPrefs.SetInt(PrefKey + ".rows", _rows);
            EditorPrefs.SetFloat(PrefKey + ".originX", _worldOrigin.x);
            EditorPrefs.SetFloat(PrefKey + ".originY", _worldOrigin.y);
            EditorPrefs.SetFloat(PrefKey + ".sizeX", _worldSize.x);
            EditorPrefs.SetFloat(PrefKey + ".sizeY", _worldSize.y);
            EditorPrefs.SetFloat(PrefKey + ".heightOffset", _heightOffset);
            EditorPrefs.SetInt(PrefKey + ".clickAreaSize", _clickAreaSize);
            EditorPrefs.SetFloat(PrefKey + ".transitionDuration", _transitionDuration);
            EditorPrefs.SetBool(PrefKey + ".forceSquareCells", _forceSquareCells);
            EditorPrefs.SetInt(PrefKey + ".pivot", (int)_pivot);
            EditorPrefs.SetString(PrefKey + ".assetFolder", _assetFolder);
            EditorPrefs.SetFloat(PrefKey + ".colorR", _fogColor.r);
            EditorPrefs.SetFloat(PrefKey + ".colorG", _fogColor.g);
            EditorPrefs.SetFloat(PrefKey + ".colorB", _fogColor.b);
            EditorPrefs.SetFloat(PrefKey + ".colorA", _fogColor.a);
            EditorPrefs.SetFloat(PrefKey + ".debugColorR", _debugGridColor.r);
            EditorPrefs.SetFloat(PrefKey + ".debugColorG", _debugGridColor.g);
            EditorPrefs.SetFloat(PrefKey + ".debugColorB", _debugGridColor.b);
            EditorPrefs.SetFloat(PrefKey + ".debugColorA", _debugGridColor.a);
            EditorPrefs.SetFloat(PrefKey + ".debugGridThickness", _debugGridThickness);
        }

        private void LoadSettings()
        {
            _columns = EditorPrefs.GetInt(PrefKey + ".columns", _columns);
            _rows = EditorPrefs.GetInt(PrefKey + ".rows", _rows);
            _worldOrigin = new Vector2(EditorPrefs.GetFloat(PrefKey + ".originX", _worldOrigin.x), EditorPrefs.GetFloat(PrefKey + ".originY", _worldOrigin.y));
            _worldSize = new Vector2(EditorPrefs.GetFloat(PrefKey + ".sizeX", _worldSize.x), EditorPrefs.GetFloat(PrefKey + ".sizeY", _worldSize.y));
            _heightOffset = EditorPrefs.GetFloat(PrefKey + ".heightOffset", _heightOffset);
            _clickAreaSize = Mathf.Max(
                1,
                EditorPrefs.GetInt(PrefKey + ".clickAreaSize", _clickAreaSize));
            _transitionDuration = Mathf.Max(
                0f,
                EditorPrefs.GetFloat(PrefKey + ".transitionDuration", _transitionDuration));
            _forceSquareCells = EditorPrefs.GetBool(PrefKey + ".forceSquareCells", _forceSquareCells);
            _pivot = (PivotChoice)Mathf.Clamp(EditorPrefs.GetInt(PrefKey + ".pivot", (int)_pivot), 0, 1);
            _assetFolder = EditorPrefs.GetString(PrefKey + ".assetFolder", _assetFolder);
            _fogColor = new Color(
                EditorPrefs.GetFloat(PrefKey + ".colorR", _fogColor.r),
                EditorPrefs.GetFloat(PrefKey + ".colorG", _fogColor.g),
                EditorPrefs.GetFloat(PrefKey + ".colorB", _fogColor.b),
                EditorPrefs.GetFloat(PrefKey + ".colorA", _fogColor.a));
            _debugGridColor = new Color(
                EditorPrefs.GetFloat(PrefKey + ".debugColorR", _debugGridColor.r),
                EditorPrefs.GetFloat(PrefKey + ".debugColorG", _debugGridColor.g),
                EditorPrefs.GetFloat(PrefKey + ".debugColorB", _debugGridColor.b),
                EditorPrefs.GetFloat(PrefKey + ".debugColorA", _debugGridColor.a));
            _debugGridThickness = EditorPrefs.GetFloat(PrefKey + ".debugGridThickness", _debugGridThickness);
        }
    }
}
