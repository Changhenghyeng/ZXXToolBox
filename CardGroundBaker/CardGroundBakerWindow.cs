using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZXXTools.CardGroundBaker
{
    /// <summary>
    /// 将面向指定摄像机的场景面片沿视线投射到水平面或 Collider 地表。
    /// 原对象不修改；Preview 使用临时 Mesh，Bake 输出新的 Mesh 资产和场景对象。
    /// </summary>
    public sealed class CardGroundBakerWindow : EditorWindow
    {
        private enum ProjectionTarget
        {
            WorldPlane,
            SurfaceColliders
        }

        private sealed class ProjectedMesh
        {
            public MeshFilter sourceFilter;
            public MeshRenderer sourceRenderer;
            public Mesh mesh;
            public Vector3 pivot;
        }

        private const string OutputFolder = "Assets/Generated/CardGroundBaker/Baked/Models";
        private const string PreviewRootName = "__CardGroundBakerPreview";

        private readonly List<GameObject> _targets = new List<GameObject>();
        private readonly Dictionary<MeshRenderer, bool> _previewSourceStates =
            new Dictionary<MeshRenderer, bool>();

        private Camera _referenceCamera;
        private ProjectionTarget _projectionTarget = ProjectionTarget.WorldPlane;
        private float _worldPlaneY;
        private LayerMask _surfaceMask = ~0;
        private float _projectionDistance = 2000f;
        private float _surfaceOffset = 0.02f;
        private bool _hideSourcesDuringPreview = true;
        private GameObject _previewRoot;
        private Vector2 _scroll;
        private string _status;
        private MessageType _statusType = MessageType.Info;

        public static void Open()
        {
            CardGroundBakerWindow window = GetWindow<CardGroundBakerWindow>("Card Ground Baker");
            window.minSize = new Vector2(390f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            if (!_referenceCamera)
                _referenceCamera = Camera.main;

            if (_targets.Count == 0)
                FillFromSelection(false);
        }

        private void OnDisable()
        {
            ClearPreview();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField(
                new GUIContent("Card Ground Baker", "将摄像机朝向面片沿视线拍平到地面"),
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "从参考摄像机看，拍平后的轮廓和 UV 位置保持不变。原物体不会被修改；"
                + "Preview 是临时结果，Bake 才会创建正式 Mesh。",
                MessageType.Info);

            DrawTargets();
            EditorGUILayout.Space(8f);
            DrawProjectionSettings();
            EditorGUILayout.Space(8f);
            DrawActions();

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.Space(6f);
                EditorGUILayout.HelpBox(_status, _statusType);
            }

            EditorGUILayout.EndScrollView();
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

        private void DrawTargets()
        {
            EditorGUILayout.LabelField("1. Target Cards", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill From Selection", GUILayout.Height(25f)))
                    FillFromSelection(true);
                if (GUILayout.Button("Clear", GUILayout.Width(70f), GUILayout.Height(25f)))
                {
                    ClearPreview();
                    _targets.Clear();
                    SetStatus("目标列表已清空。", MessageType.Info);
                }
            }

            Rect dropRect = GUILayoutUtility.GetRect(0f, 48f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "拖入场景中的方片或包含方片的父物体", EditorStyles.helpBox);
            HandleDrop(dropRect);

            int removeIndex = -1;
            for (int i = 0; i < _targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GameObject replacement = (GameObject)EditorGUILayout.ObjectField(
                        _targets[i], typeof(GameObject), true);
                    if (replacement != _targets[i])
                    {
                        ClearPreview();
                        _targets[i] = IsSceneObject(replacement) ? replacement : null;
                    }

                    if (GUILayout.Button("×", GUILayout.Width(24f)))
                        removeIndex = i;
                }
            }

            if (removeIndex >= 0)
            {
                ClearPreview();
                _targets.RemoveAt(removeIndex);
            }

            _targets.RemoveAll(item => !item);
            EditorGUILayout.LabelField($"有效目标：{_targets.Count}", EditorStyles.miniLabel);
        }

        private void DrawProjectionSettings()
        {
            EditorGUILayout.LabelField("2. Projection", EditorStyles.boldLabel);

            _referenceCamera = (Camera)EditorGUILayout.ObjectField(
                new GUIContent("Reference Camera", "烘焙结果将保持这个摄像机中的屏幕投影"),
                _referenceCamera,
                typeof(Camera),
                true);

            if (!_referenceCamera)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Use Main Camera", GUILayout.Width(130f)))
                        _referenceCamera = Camera.main;
                }
            }

            _projectionTarget = (ProjectionTarget)EditorGUILayout.EnumPopup(
                new GUIContent("Flatten Target", "投到统一水平面，或逐顶点投到 Collider 地表"),
                _projectionTarget);

            if (_projectionTarget == ProjectionTarget.WorldPlane)
            {
                _worldPlaneY = EditorGUILayout.FloatField(
                    new GUIContent("World Plane Y", "拍平结果所在的世界坐标高度"),
                    _worldPlaneY);
            }
            else
            {
                int concatenatedMask = InternalEditorUtility.LayerMaskToConcatenatedLayersMask(
                    _surfaceMask.value);
                concatenatedMask = EditorGUILayout.MaskField(
                    new GUIContent("Surface Layers", "参与地表射线检测的 Layer"),
                    concatenatedMask,
                    InternalEditorUtility.layers);
                _surfaceMask = InternalEditorUtility.ConcatenatedLayersMaskToLayerMask(
                    concatenatedMask);
                _projectionDistance = Mathf.Max(0.1f, EditorGUILayout.FloatField(
                    new GUIContent("Max Distance", "每个顶点沿摄像机视线检测地表的最大距离"),
                    _projectionDistance));
                _surfaceOffset = Mathf.Max(0f, EditorGUILayout.FloatField(
                    new GUIContent("Surface Offset", "沿地表法线抬高，避免与地面闪烁"),
                    _surfaceOffset));
            }

            _hideSourcesDuringPreview = EditorGUILayout.Toggle(
                new GUIContent("Hide Sources In Preview", "预览时临时关闭源 Renderer；清除预览或关闭窗口会恢复"),
                _hideSourcesDuringPreview);

            string cameraMode = _referenceCamera
                ? (_referenceCamera.orthographic ? "Orthographic" : "Perspective")
                : "None";
            EditorGUILayout.LabelField($"Camera Mode: {cameraMode}", EditorStyles.miniLabel);
        }

        private void DrawActions()
        {
            EditorGUILayout.LabelField("3. Preview / Bake", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview", GUILayout.Height(32f)))
                    CreatePreview();

                using (new EditorGUI.DisabledScope(!_previewRoot))
                {
                    if (GUILayout.Button("Clear Preview", GUILayout.Height(32f)))
                    {
                        ClearPreview();
                        SetStatus("预览已清除，源 Renderer 已恢复。", MessageType.Info);
                    }
                }
            }

            if (GUILayout.Button("Bake To New Group", GUILayout.Height(32f)))
                Bake(false);

            GUI.backgroundColor = new Color(1f, 0.78f, 0.48f);
            if (GUILayout.Button("Bake And Overwrite Original Prefab / Mesh", GUILayout.Height(36f)))
                Bake(true);
            GUI.backgroundColor = Color.white;

            EditorGUILayout.HelpBox(
                $"普通 Bake：生成新 Mesh 和场景组，资源输出到 {OutputFolder}。\n"
                + "覆盖 Bake：写回原 Mesh；FBX 内嵌 Mesh 会生成独立 Mesh 并替换原 Prefab 的引用。",
                MessageType.None);
        }

        private void FillFromSelection(bool report)
        {
            ClearPreview();
            _targets.Clear();

            foreach (GameObject selected in Selection.gameObjects)
            {
                if (IsSceneObject(selected) && !_targets.Contains(selected))
                    _targets.Add(selected);
            }

            if (report)
            {
                SetStatus(
                    _targets.Count > 0
                        ? $"已从 Hierarchy 填入 {_targets.Count} 个目标。"
                        : "当前没有选中场景物体。",
                    _targets.Count > 0 ? MessageType.Info : MessageType.Warning);
            }
        }

        private void HandleDrop(Rect rect)
        {
            Event current = Event.current;
            if (!rect.Contains(current.mousePosition)
                || (current.type != EventType.DragUpdated
                    && current.type != EventType.DragPerform))
                return;

            bool hasSceneObject = DragAndDrop.objectReferences
                .OfType<GameObject>()
                .Any(IsSceneObject);
            DragAndDrop.visualMode = hasSceneObject
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (current.type == EventType.DragPerform && hasSceneObject)
            {
                ClearPreview();
                DragAndDrop.AcceptDrag();
                foreach (GameObject gameObject in DragAndDrop.objectReferences.OfType<GameObject>())
                {
                    if (IsSceneObject(gameObject) && !_targets.Contains(gameObject))
                        _targets.Add(gameObject);
                }
                SetStatus($"当前共有 {_targets.Count} 个目标。", MessageType.Info);
            }

            current.Use();
        }

        private static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject && !EditorUtility.IsPersistent(gameObject)
                && gameObject.scene.IsValid();
        }

        private void CreatePreview()
        {
            ClearPreview();
            if (!TryBuildProjectedMeshes(out List<ProjectedMesh> projected, out string error))
            {
                SetStatus(error, MessageType.Error);
                return;
            }

            _previewRoot = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild
            };

            foreach (ProjectedMesh item in projected)
            {
                CreateOutputObject(item, _previewRoot.transform, false);
                if (_hideSourcesDuringPreview && item.sourceRenderer
                    && !_previewSourceStates.ContainsKey(item.sourceRenderer))
                {
                    _previewSourceStates.Add(item.sourceRenderer, item.sourceRenderer.enabled);
                    item.sourceRenderer.enabled = false;
                }
            }

            Selection.activeGameObject = _previewRoot;
            SceneView.RepaintAll();
            SetStatus(
                $"已生成 {projected.Count} 个临时拍平 Mesh。切换到参考摄像机检查视觉重合后再 Bake。",
                MessageType.Info);
        }

        private void Bake(bool overwriteSourceAssets)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            ClearPreview();
            if (!TryBuildProjectedMeshes(out List<ProjectedMesh> projected, out string error))
            {
                SetStatus(error, MessageType.Error);
                return;
            }

            EnsureAssetFolder(OutputFolder);
            if (overwriteSourceAssets)
            {
                BakeOverSourceAssets(projected, usageTimer);
                return;
            }

            string rootName = _targets.Count == 1 && _targets[0]
                ? _targets[0].name + "_CardGround_Baked"
                : "CardGround_Baked";
            GameObject bakedRoot = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(bakedRoot, "Bake Card Ground Meshes");

            int created = 0;
            try
            {
                foreach (ProjectedMesh item in projected)
                {
                    string fileName = SanitizeFileName(item.sourceFilter.name)
                        + "_Ground.asset";
                    string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                        OutputFolder + "/" + fileName);
                    item.mesh.name = Path.GetFileNameWithoutExtension(assetPath);
                    item.mesh.hideFlags = HideFlags.None;
                    AssetDatabase.CreateAsset(item.mesh, assetPath);
                    CreateOutputObject(item, bakedRoot.transform, true);
                    created++;
                }

                AssetDatabase.SaveAssets();
                ZXXToolUsageStats.Complete("Card Ground Baker", created, usageTimer);
                Selection.activeGameObject = bakedRoot;
                EditorGUIUtility.PingObject(bakedRoot);
                SceneView.RepaintAll();
                SetStatus(
                    $"Bake 完成：生成 {created} 个 Mesh。原物体保持不变。",
                    MessageType.Info);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetStatus("Bake 中断，请查看 Console。已生成的资产会保留以便检查。", MessageType.Error);
            }
        }

        private void BakeOverSourceAssets(
            List<ProjectedMesh> projected,
            System.Diagnostics.Stopwatch usageTimer)
        {
            var prefabRootsToApply = new HashSet<GameObject>();
            var prefabStageRootsToSave = new HashSet<GameObject>();
            var prefabPathsByRoot = new Dictionary<GameObject, string>();
            var meshPathCounts = new Dictionary<string, int>();
            PrefabStage currentPrefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            foreach (ProjectedMesh item in projected)
            {
                string meshPath = AssetDatabase.GetAssetPath(item.sourceFilter.sharedMesh);
                if (!string.IsNullOrEmpty(meshPath))
                    meshPathCounts[meshPath] = meshPathCounts.TryGetValue(meshPath, out int count) ? count + 1 : 1;

                bool inCurrentPrefabStage = currentPrefabStage != null
                    && item.sourceFilter.gameObject.scene == currentPrefabStage.scene;
                GameObject prefabRoot = inCurrentPrefabStage
                    ? currentPrefabStage.prefabContentsRoot
                    : PrefabUtility.GetNearestPrefabInstanceRoot(item.sourceFilter.gameObject);
                if (!prefabRoot)
                {
                    DestroyProjectedMeshes(projected);
                    SetStatus($"“{item.sourceFilter.name}”不属于场景中的预制体实例，无法覆盖原预制体。", MessageType.Error);
                    return;
                }

                string prefabPath = inCurrentPrefabStage
                    ? currentPrefabStage.assetPath
                    : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(prefabRoot);
                if (string.IsNullOrEmpty(prefabPath)
                    || !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    || (!inCurrentPrefabStage && PrefabUtility.IsPartOfModelPrefab(prefabRoot)))
                {
                    DestroyProjectedMeshes(projected);
                    SetStatus($"“{prefabRoot.name}”不是可写入的普通预制体资源。", MessageType.Error);
                    return;
                }

                prefabPathsByRoot[prefabRoot] = prefabPath;
                if (inCurrentPrefabStage)
                    prefabStageRootsToSave.Add(prefabRoot);
            }

            int updated = 0;
            try
            {
                foreach (ProjectedMesh item in projected)
                {
                    bool itemInPrefabStage = currentPrefabStage != null
                        && item.sourceFilter.gameObject.scene == currentPrefabStage.scene;
                    Mesh sourceMesh = item.sourceFilter.sharedMesh;
                    Mesh bakedMesh = CreateMeshInSourceLocalSpace(item);
                    string sourcePath = AssetDatabase.GetAssetPath(sourceMesh);
                    bool canOverwriteMesh = !string.IsNullOrEmpty(sourcePath)
                        && sourcePath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                        && meshPathCounts[sourcePath] == 1;
                    Mesh meshToAssign;

                    if (canOverwriteMesh)
                    {
                        Undo.RecordObject(sourceMesh, "Overwrite Card Ground Mesh");
                        OverwriteMeshVertices(sourceMesh, bakedMesh);
                        EditorUtility.SetDirty(sourceMesh);
                        DestroyImmediate(bakedMesh);
                        meshToAssign = sourceMesh;
                    }
                    else
                    {
                        string replacementPath = OutputFolder + "/"
                            + SanitizeFileName(GetSourceObjectPath(item.sourceFilter.transform))
                            + "_CardGround_Overwrite.asset";
                        Mesh replacementAsset = AssetDatabase.LoadAssetAtPath<Mesh>(replacementPath);
                        if (replacementAsset)
                        {
                            Undo.RecordObject(replacementAsset, "Update Card Ground Mesh");
                            OverwriteMeshVertices(replacementAsset, bakedMesh);
                            EditorUtility.SetDirty(replacementAsset);
                            DestroyImmediate(bakedMesh);
                        }
                        else
                        {
                            bakedMesh.name = Path.GetFileNameWithoutExtension(replacementPath);
                            AssetDatabase.CreateAsset(bakedMesh, replacementPath);
                            replacementAsset = bakedMesh;
                        }

                        meshToAssign = replacementAsset;
                    }

                    // Reassign the source MeshFilter itself so the selected object visibly
                    // adopts the flattened geometry before the prefab write-back completes.
                    Undo.RecordObject(item.sourceFilter, "Apply Card Ground Mesh To Prefab");
                    item.sourceFilter.sharedMesh = meshToAssign;
                    EditorUtility.SetDirty(item.sourceFilter);
                    if (meshToAssign != sourceMesh)
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(item.sourceFilter);
                        GameObject targetRoot = itemInPrefabStage
                            ? currentPrefabStage.prefabContentsRoot
                            : PrefabUtility.GetNearestPrefabInstanceRoot(item.sourceFilter.gameObject);
                        if (!itemInPrefabStage)
                            prefabRootsToApply.Add(targetRoot);
                    }
                    updated++;
                }

                foreach (GameObject prefabRoot in prefabRootsToApply)
                {
                    PrefabUtility.ApplyPrefabInstance(prefabRoot, InteractionMode.AutomatedAction);
                }
                foreach (GameObject prefabRoot in prefabStageRootsToSave)
                {
                    GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(
                        prefabRoot,
                        prefabPathsByRoot[prefabRoot],
                        out bool saveSucceeded);
                    if (!saveSucceeded || !savedPrefab)
                        throw new InvalidOperationException(
                            $"无法保存原预制体：{prefabPathsByRoot[prefabRoot]}");
                }
                AssetDatabase.SaveAssets();
                ZXXToolUsageStats.Complete("Card Ground Baker", updated, usageTimer);
                SceneView.RepaintAll();
                SetStatus($"覆盖完成：处理 {updated} 个 Mesh；需要替换 Mesh 引用的预制体已写回。", MessageType.Info);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                SetStatus("覆盖中断，请查看 Console。已写入的资源可能需要检查。", MessageType.Error);
            }
            finally
            {
                DestroyProjectedMeshes(projected);
            }
        }

        private static Mesh CreateMeshInSourceLocalSpace(ProjectedMesh item)
        {
            Mesh result = Instantiate(item.mesh);
            Vector3[] vertices = result.vertices;
            Vector3[] normals = result.normals;
            Matrix4x4 worldToLocal = item.sourceFilter.transform.worldToLocalMatrix;
            Matrix4x4 normalToLocal = item.sourceFilter.transform.localToWorldMatrix.transpose;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = worldToLocal.MultiplyPoint3x4(item.pivot + vertices[i]);
                if (i < normals.Length)
                    normals[i] = normalToLocal.MultiplyVector(normals[i]).normalized;
            }

            result.vertices = vertices;
            if (normals.Length == vertices.Length)
                result.normals = normals;
            result.name = item.sourceFilter.sharedMesh.name;
            return result;
        }

        private static void OverwriteMeshVertices(Mesh target, Mesh source)
        {
            // 只覆盖顶点位置与法线，保留拓扑、UV、颜色、材质等其余属性。
            // 不用 CopySerialized：它对 Mesh 的顶点/索引原生缓冲不可靠，覆盖后场景不刷新。
            Vector3[] vertices = source.vertices;
            Vector3[] normals = source.normals;
            target.vertices = vertices;
            if (normals.Length == vertices.Length)
                target.normals = normals;
            target.RecalculateBounds();
            if (source.uv != null && source.uv.Length == vertices.Length)
            {
                try
                {
                    target.RecalculateTangents();
                }
                catch
                {
                    // 某些旧 Mesh 拓扑不支持 tangent 重算。
                }
            }
        }

        private static string GetSourceObjectPath(Transform target)
        {
            var parts = new Stack<string>();
            Transform current = target;
            while (current)
            {
                parts.Push(current.name);
                current = current.parent;
            }
            return string.Join("_", parts);
        }

        private bool TryBuildProjectedMeshes(
            out List<ProjectedMesh> projected,
            out string error)
        {
            projected = new List<ProjectedMesh>();
            error = string.Empty;

            _targets.RemoveAll(item => !item);
            if (_targets.Count == 0)
            {
                error = "请先从 Hierarchy 填入至少一个场景物体。";
                return false;
            }

            if (!_referenceCamera)
            {
                error = "请指定 Reference Camera。";
                return false;
            }

            List<MeshFilter> filters = CollectMeshFilters();
            if (filters.Count == 0)
            {
                error = "目标中没有找到同时带 MeshFilter 和 MeshRenderer 的有效面片。";
                return false;
            }

            foreach (MeshFilter filter in filters)
            {
                Mesh sourceMesh = filter.sharedMesh;
                MeshRenderer sourceRenderer = filter.GetComponent<MeshRenderer>();
                if (!sourceMesh || !sourceRenderer)
                    continue;
                if (!sourceMesh.isReadable)
                {
                    error = $"Mesh“{sourceMesh.name}”未开启 Read/Write，无法读取顶点。";
                    DestroyProjectedMeshes(projected);
                    projected.Clear();
                    return false;
                }

                if (!TryProjectMesh(filter, sourceRenderer, out ProjectedMesh item, out error))
                {
                    DestroyProjectedMeshes(projected);
                    projected.Clear();
                    return false;
                }
                projected.Add(item);
            }

            if (projected.Count == 0)
            {
                error = "没有生成任何有效的投影 Mesh。";
                return false;
            }
            return true;
        }

        private List<MeshFilter> CollectMeshFilters()
        {
            var result = new List<MeshFilter>();
            var seen = new HashSet<int>();
            foreach (GameObject target in _targets)
            {
                if (!target)
                    continue;

                foreach (MeshFilter filter in target.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh
                        && filter.GetComponent<MeshRenderer>()
                        && seen.Add(filter.GetInstanceID()))
                        result.Add(filter);
                }
            }
            return result;
        }

        private bool TryProjectMesh(
            MeshFilter sourceFilter,
            MeshRenderer sourceRenderer,
            out ProjectedMesh result,
            out string error)
        {
            result = null;
            error = string.Empty;
            Mesh sourceMesh = sourceFilter.sharedMesh;
            Vector3[] sourceVertices = sourceMesh.vertices;
            if (sourceVertices == null || sourceVertices.Length < 3)
            {
                error = $"“{sourceFilter.name}”的 Mesh 顶点不足。";
                return false;
            }

            var worldVertices = new Vector3[sourceVertices.Length];
            var worldNormals = new Vector3[sourceVertices.Length];
            Vector3 sum = Vector3.zero;

            for (int i = 0; i < sourceVertices.Length; i++)
            {
                Vector3 sourceWorld = sourceFilter.transform.TransformPoint(sourceVertices[i]);
                Vector3 direction = GetProjectionDirection(sourceWorld);
                if (Mathf.Abs(direction.y) < 0.0001f)
                {
                    error = $"“{sourceFilter.name}”有顶点视线接近平行地面，无法拍平。";
                    return false;
                }

                if (_projectionTarget == ProjectionTarget.WorldPlane)
                {
                    float distance = (_worldPlaneY - sourceWorld.y) / direction.y;
                    worldVertices[i] = sourceWorld + direction * distance;
                    worldVertices[i].y = _worldPlaneY;
                    worldNormals[i] = Vector3.up;
                }
                else
                {
                    if (!TryRaycastSurface(sourceWorld, direction, out RaycastHit hit))
                    {
                        error = $"“{sourceFilter.name}”的第 {i + 1} 个顶点没有投射到 Collider。";
                        return false;
                    }

                    Vector3 normal = hit.normal.normalized;
                    if (normal.y < 0f)
                        normal = -normal;
                    worldVertices[i] = hit.point + normal * _surfaceOffset;
                    worldNormals[i] = normal;
                }

                sum += worldVertices[i];
            }

            Vector3 pivot = sum / worldVertices.Length;
            if (_projectionTarget == ProjectionTarget.WorldPlane)
                pivot.y = _worldPlaneY;

            var localVertices = new Vector3[worldVertices.Length];
            for (int i = 0; i < worldVertices.Length; i++)
                localVertices[i] = worldVertices[i] - pivot;

            Mesh projectedMesh = Instantiate(sourceMesh);
            projectedMesh.name = sourceMesh.name + "_Ground";
            projectedMesh.vertices = localVertices;

            Vector3 desiredNormal = Vector3.zero;
            for (int i = 0; i < worldNormals.Length; i++)
                desiredNormal += worldNormals[i];
            desiredNormal = desiredNormal.sqrMagnitude > 0.000001f
                ? desiredNormal.normalized
                : Vector3.up;

            if (NeedsWindingReversal(projectedMesh, localVertices, desiredNormal))
                ReverseTriangleWinding(projectedMesh);

            projectedMesh.normals = worldNormals;
            projectedMesh.RecalculateBounds();
            if (projectedMesh.uv != null && projectedMesh.uv.Length == localVertices.Length)
            {
                try
                {
                    projectedMesh.RecalculateTangents();
                }
                catch
                {
                    // 某些旧 Mesh 拓扑不支持 tangent 重算；不影响 UV 和几何投影。
                }
            }

            result = new ProjectedMesh
            {
                sourceFilter = sourceFilter,
                sourceRenderer = sourceRenderer,
                mesh = projectedMesh,
                pivot = pivot
            };
            return true;
        }

        private Vector3 GetProjectionDirection(Vector3 sourceWorld)
        {
            if (_referenceCamera.orthographic)
                return _referenceCamera.transform.forward.normalized;

            Vector3 direction = sourceWorld - _referenceCamera.transform.position;
            return direction.sqrMagnitude > 0.000001f
                ? direction.normalized
                : _referenceCamera.transform.forward.normalized;
        }

        private bool TryRaycastSurface(
            Vector3 origin,
            Vector3 direction,
            out RaycastHit selectedHit)
        {
            selectedHit = default;
            float bestDistance = float.PositiveInfinity;
            RaycastHit[] forwardHits = Physics.RaycastAll(
                origin + direction * 0.001f,
                direction,
                _projectionDistance,
                _surfaceMask,
                QueryTriggerInteraction.Ignore);
            RaycastHit[] backwardHits = Physics.RaycastAll(
                origin - direction * 0.001f,
                -direction,
                _projectionDistance,
                _surfaceMask,
                QueryTriggerInteraction.Ignore);

            TrySelectClosestSurfaceHit(forwardHits, ref selectedHit, ref bestDistance);
            TrySelectClosestSurfaceHit(backwardHits, ref selectedHit, ref bestDistance);
            return bestDistance < float.PositiveInfinity;
        }

        private void TrySelectClosestSurfaceHit(
            RaycastHit[] hits,
            ref RaycastHit selectedHit,
            ref float bestDistance)
        {
            if (hits == null)
                return;

            foreach (RaycastHit hit in hits)
            {
                if (hit.distance >= bestDistance)
                    continue;

                bool belongsToSource = _targets.Any(target =>
                    target && hit.transform && hit.transform.IsChildOf(target.transform));
                if (belongsToSource)
                    continue;

                selectedHit = hit;
                bestDistance = hit.distance;
            }
        }

        private GameObject CreateOutputObject(
            ProjectedMesh item,
            Transform parent,
            bool registerUndo)
        {
            GameObject output = new GameObject(item.sourceFilter.name + "_Ground");
            output.transform.SetParent(parent, false);
            output.transform.SetPositionAndRotation(item.pivot, Quaternion.identity);
            output.transform.localScale = Vector3.one;
            output.layer = item.sourceFilter.gameObject.layer;
            output.tag = item.sourceFilter.gameObject.tag;
            GameObjectUtility.SetStaticEditorFlags(
                output,
                GameObjectUtility.GetStaticEditorFlags(item.sourceFilter.gameObject));

            MeshFilter filter = output.AddComponent<MeshFilter>();
            filter.sharedMesh = item.mesh;
            MeshRenderer renderer = output.AddComponent<MeshRenderer>();
            CopyRendererSettings(item.sourceRenderer, renderer);

            if (!registerUndo)
            {
                output.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
                item.mesh.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
            }
            return output;
        }

        private static void CopyRendererSettings(MeshRenderer source, MeshRenderer destination)
        {
            destination.sharedMaterials = source.sharedMaterials;
            destination.shadowCastingMode = source.shadowCastingMode;
            destination.receiveShadows = source.receiveShadows;
            destination.lightProbeUsage = source.lightProbeUsage;
            destination.reflectionProbeUsage = source.reflectionProbeUsage;
            destination.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            destination.renderingLayerMask = source.renderingLayerMask;
            destination.sortingLayerID = source.sortingLayerID;
            destination.sortingOrder = source.sortingOrder;
            destination.enabled = source.enabled;
        }

        private static bool NeedsWindingReversal(
            Mesh mesh,
            Vector3[] vertices,
            Vector3 desiredNormal)
        {
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    continue;

                int[] indices = mesh.GetIndices(subMesh);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    Vector3 normal = Vector3.Cross(
                        vertices[indices[i + 1]] - vertices[indices[i]],
                        vertices[indices[i + 2]] - vertices[indices[i]]);
                    if (normal.sqrMagnitude > 0.000001f)
                        return Vector3.Dot(normal, desiredNormal) < 0f;
                }
            }
            return false;
        }

        private static void ReverseTriangleWinding(Mesh mesh)
        {
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                MeshTopology topology = mesh.GetTopology(subMesh);
                if (topology != MeshTopology.Triangles)
                    continue;

                int[] indices = mesh.GetIndices(subMesh);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int swap = indices[i + 1];
                    indices[i + 1] = indices[i + 2];
                    indices[i + 2] = swap;
                }
                mesh.SetIndices(indices, topology, subMesh, false);
            }
        }

        private void ClearPreview()
        {
            foreach (KeyValuePair<MeshRenderer, bool> pair in _previewSourceStates)
            {
                if (pair.Key)
                    pair.Key.enabled = pair.Value;
            }
            _previewSourceStates.Clear();

            if (_previewRoot)
            {
                foreach (MeshFilter filter in _previewRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh && !AssetDatabase.Contains(filter.sharedMesh))
                        DestroyImmediate(filter.sharedMesh);
                }
                DestroyImmediate(_previewRoot);
            }
            _previewRoot = null;
            SceneView.RepaintAll();
        }

        private static void DestroyProjectedMeshes(IEnumerable<ProjectedMesh> projected)
        {
            foreach (ProjectedMesh item in projected)
            {
                if (item != null && item.mesh && !AssetDatabase.Contains(item.mesh))
                    DestroyImmediate(item.mesh);
            }
        }

        private static void EnsureAssetFolder(string folder)
        {
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string value)
        {
            string result = string.IsNullOrWhiteSpace(value) ? "Card" : value;
            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');
            return result;
        }

        private void SetStatus(string message, MessageType type)
        {
            _status = message;
            _statusType = type;
            Repaint();
        }
    }
}
