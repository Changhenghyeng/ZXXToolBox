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

        private Vector2 _scroll;
        private int _columns = 10;
        private int _rows = 8;
        private Vector2 _worldOrigin = Vector2.zero;
        private Vector2 _worldSize = new Vector2(10f, 8f);
        private float _heightOffset = 0.05f;
        private Color _fogColor = new Color(1f, 1f, 1f, 0.55f);
        private Color _debugGridColor = new Color(0f, 1f, 1f, 0.85f);
        private float _debugGridThickness = 0.02f;
        private bool _forceSquareCells = true;
        private PivotChoice _pivot = PivotChoice.BottomLeft;
        private string _assetFolder = "Assets/FogOfWar";

        private enum PivotChoice
        {
            BottomLeft,
            Center
        }

        public static void Open()
        {
            FogOfWarToolWindow window = GetWindow<FogOfWarToolWindow>("Fog Grid Tool");
            window.minSize = new Vector2(380f, 390f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Fog Mask Grid", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "根据选中物体的俯视包围盒生成一张可点击的战争迷雾遮罩面。点击会转换成网格坐标，并写入运行时 Mask 贴图。",
                MessageType.Info);

            DrawBoundsSection();
            EditorGUILayout.Space(8f);
            DrawGridSection();
            EditorGUILayout.Space(8f);
            DrawCreateSection();
            EditorGUILayout.Space(8f);
            DrawSelectedTools();

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
            EditorGUILayout.LabelField("1. Area", EditorStyles.boldLabel);
            _pivot = (PivotChoice)EditorGUILayout.EnumPopup("Pivot", _pivot);
            _worldOrigin = EditorGUILayout.Vector2Field("Origin XZ", _worldOrigin);
            _worldSize = EditorGUILayout.Vector2Field("Size XZ", _worldSize);
            _heightOffset = EditorGUILayout.FloatField("Height Offset", _heightOffset);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fit To Selection Top View")) FitToSelectionBounds();
                if (GUILayout.Button("Use Scene Origin")) _worldOrigin = Vector2.zero;
            }
        }

        private void DrawGridSection()
        {
            EditorGUILayout.LabelField("2. Grid", EditorStyles.boldLabel);
            _columns = EditorGUILayout.IntField("Columns", Mathf.Max(1, _columns));
            _rows = EditorGUILayout.IntField("Rows", Mathf.Max(1, _rows));
            _fogColor = EditorGUILayout.ColorField("Fog Color", _fogColor);
            _forceSquareCells = EditorGUILayout.ToggleLeft("Force Square Cells", _forceSquareCells);

            Vector2 previewSize = GetResolvedWorldSize();
            float cellW = Mathf.Max(0.001f, previewSize.x) / Mathf.Max(1, _columns);
            float cellH = Mathf.Max(0.001f, previewSize.y) / Mathf.Max(1, _rows);
            EditorGUILayout.LabelField("Cell Size", cellW.ToString("0.###") + " x " + cellH.ToString("0.###"));
            EditorGUILayout.LabelField("Cell Count", (_columns * _rows).ToString());
        }

        private void DrawCreateSection()
        {
            EditorGUILayout.LabelField("3. Create", EditorStyles.boldLabel);
            _assetFolder = EditorGUILayout.TextField("Asset Folder", _assetFolder);

            if (GUILayout.Button("Create Clickable Fog Grid", GUILayout.Height(34f)))
            {
                CreateGrid();
            }
        }

        private void DrawSelectedTools()
        {
            EditorGUILayout.LabelField("4. Selected Grid", EditorStyles.boldLabel);

            Component grid = FindSelectedGrid();
            using (new EditorGUI.DisabledScope(grid == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reset Mask")) InvokeNoArg(grid, "ResetMask");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Show All")) InvokeNoArg(grid, "ShowAll");
                    if (GUILayout.Button("Hide All")) InvokeNoArg(grid, "HideAll");
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Ping Runtime Mask")) PingRuntimeMask(grid);
                    if (GUILayout.Button("Export Mask PNG")) ExportRuntimeMask(grid);
                }

                if (GUILayout.Button("Refresh Cloud Material"))
                {
                    RefreshSelectedCloudMaterial(grid);
                }

                bool debugGridVisible = IsDebugGridVisible(grid);
                bool nextDebugGridVisible = EditorGUILayout.ToggleLeft("Show Debug Grid", debugGridVisible);
                if (nextDebugGridVisible != debugGridVisible)
                {
                    SetDebugGridVisible(grid, nextDebugGridVisible);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _debugGridColor = EditorGUILayout.ColorField("Debug Grid Color", _debugGridColor);
                    if (GUILayout.Button("Refresh Grid", GUILayout.Width(96f))) RefreshDebugGrid(grid);
                }

                _debugGridThickness = EditorGUILayout.FloatField("Debug Grid Thickness", Mathf.Max(0.001f, _debugGridThickness));
            }

            if (grid == null)
            {
                EditorGUILayout.HelpBox("选中已生成的 Fog Grid 对象后，才能使用这些操作按钮。", MessageType.None);
            }
        }

        private void CreateGrid()
        {
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

            Selection.activeGameObject = root;
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
            if (material.HasProperty("_TilingFactor")) material.SetFloat("_TilingFactor", 2f);
            if (material.HasProperty("_SecondTilingFactor")) material.SetFloat("_SecondTilingFactor", 1f);
            if (material.HasProperty("_CloudLayer1Speed")) material.SetVector("_CloudLayer1Speed", new Vector4(0.018f, 0.011f, 0f, 0f));
            if (material.HasProperty("_CloudLayer2Speed")) material.SetVector("_CloudLayer2Speed", new Vector4(-0.013f, -0.017f, 0f, 0f));
            if (material.HasProperty("_CloudLayer1SpeedX")) material.SetFloat("_CloudLayer1SpeedX", 0.018f);
            if (material.HasProperty("_CloudLayer1SpeedY")) material.SetFloat("_CloudLayer1SpeedY", 0.011f);
            if (material.HasProperty("_CloudLayer2SpeedX")) material.SetFloat("_CloudLayer2SpeedX", -0.013f);
            if (material.HasProperty("_CloudLayer2SpeedY")) material.SetFloat("_CloudLayer2SpeedY", -0.017f);
            if (material.HasProperty("_CloudAlphaPower")) material.SetFloat("_CloudAlphaPower", 1.25f);
            if (material.HasProperty("_MaskEdgeSoftness")) material.SetFloat("_MaskEdgeSoftness", 0.05f);
            if (material.HasProperty("_MaskEdgeContrast")) material.SetFloat("_MaskEdgeContrast", 1f);
            if (material.HasProperty("_ShadeOffset")) material.SetVector("_ShadeOffset", new Vector4(0.5f, 0.5f, 1f, 0f));
            if (material.HasProperty("_ShadowOffsetX")) material.SetFloat("_ShadowOffsetX", 0f);
            if (material.HasProperty("_ShadowOffsetY")) material.SetFloat("_ShadowOffsetY", 0f);
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
            Texture2D mask = GetTextureProperty(grid, "MaskTexture");
            if (mask == null) return;

            string folder = NormalizeAssetFolder(_assetFolder);
            Directory.CreateDirectory(AssetPathToFullPath(folder));
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/FogGrid_RuntimeMask.png");
            File.WriteAllBytes(AssetPathToFullPath(path), mask.EncodeToPNG());
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
