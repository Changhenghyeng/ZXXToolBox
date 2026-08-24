using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Sprites;
using ZXXTools.PathBuilder;
using Object = UnityEngine.Object;

internal static class PathBuilderGenerator
{
    private const string MountainRootName = "[Live Preview] Mountains";
    private const string RiverRootName = "[Live Preview] River";
    private const float RiverCurvatureSafety = 0.9f;
    private const float RiverBankTaperPerMeter = 0.35f;
    private const float MinimumRiverHalfWidth = 0.005f;
    private const int MountainCardsPerMesh = 16000;
    private const int MaximumMountainPlacements = 100000;
    private const float MountainDecalMaximumStretchFactor = 4f;

    private sealed class MountainDecalProjection
    {
        public MeshFilter filter;
        public Mesh sourceMesh;
        public Mesh decalMesh;
    }

    private struct MountainDecalVertex
    {
        public int index;
        public Vector3 worldPosition;
        public Vector3 worldNormal;
    }

    private struct MountainCardPlacement
    {
        public Texture2D texture;
        public Rect uv;
        public Vector2 pivot;
        public Vector3 position;
        public float width;
        public float height;
        public bool flipX;
        public int sequence;
    }

    public static bool Rebuild(PathSpline builder, bool logWarnings = true)
    {
        if (!builder || !builder.Anchor)
            return false;

        builder.EnsureDefaults();
        ClearGenerated(builder);

        List<PathSample> arcTable =
            PathSplineUtility.BuildArcTable(builder);
        if (arcTable.Count < 2
            || arcTable[arcTable.Count - 1].distance <= 0.001f)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] 样条长度不足，无法生成。", builder);
            return false;
        }

        GameObject root = new GameObject(
            builder.mode == PathBuildMode.Mountain
                ? MountainRootName
                : RiverRootName);
        root.transform.SetParent(builder.Anchor, false);
        builder.GeneratedRoot = root.transform;
        EditorUtility.SetDirty(builder);

        PathOutputLayer output = GetPrimaryOutput(builder);
        bool built = false;
        try
        {
            if (output == null)
            {
                built = false;
            }
            else if (output.type == PathOutputType.Scatter)
            {
                built = BuildMountains(
                    builder, root.transform, arcTable, logWarnings);
            }
            else if (output.type == PathOutputType.MeshDeform)
            {
                built = BuildMeshDeform(
                    builder, output, root.transform, arcTable, logWarnings);
            }
            else if (output.type == PathOutputType.PrefabPlace)
            {
                built = BuildPrefabPlace(
                    builder, output, root.transform, arcTable, logWarnings);
            }
            else
            {
                built = BuildRiver(builder, root, arcTable, logWarnings);
            }
        }
        catch (Exception exception)
        {
            if (logWarnings)
                Debug.LogException(exception, builder);
            built = false;
        }

        if (!built)
        {
            builder.GeneratedRoot = null;
            DestroyGeneratedPreviewResources(root.transform);
            Object.DestroyImmediate(root);
            EditorUtility.SetDirty(builder);
            return false;
        }

        root.hideFlags = HideFlags.DontSaveInBuild;
        EditorUtility.SetDirty(root);
        return true;
    }

    private static PathOutputLayer GetPrimaryOutput(PathSpline builder)
    {
        builder.EnsureOutputStack();
        for (int i = 0; i < builder.outputs.Count; i++)
        {
            PathOutputLayer output = builder.outputs[i];
            if (output != null && output.enabled)
                return output;
        }
        return null;
    }

    public static void ClearGenerated(PathSpline builder)
    {
        if (!builder)
            return;

        Transform root = builder.GeneratedRoot;
        if (root)
        {
            DestroyGeneratedPreviewResources(root);
            Object.DestroyImmediate(root.gameObject);
        }

        builder.GeneratedRoot = null;
        EditorUtility.SetDirty(builder);
    }

    public static bool Bake(PathSpline builder)
    {
        if (!builder || !builder.Anchor)
            return false;

        builder.EnsureDefaults();
        PathOutputLayer primaryOutput = GetPrimaryOutput(builder);
        if (primaryOutput == null)
            return false;
        bool mountain = primaryOutput.type == PathOutputType.Scatter;
        if (!TryEnsureAssetFolder(
            builder.generatedAssetOutputFolder,
            out string assetOutputFolder,
            out string assetFolderError))
        {
            Debug.LogError(
                $"[Path Builder] 资源导出路径无效：{assetFolderError}",
                builder);
            return false;
        }

        string prefabOutputFolder = null;
        if (mountain
            && !TryEnsureAssetFolder(
                builder.prefabOutputFolder,
                out prefabOutputFolder,
                out string prefabFolderError))
        {
            Debug.LogError(
                $"[Path Builder] Prefab 输出路径无效：{prefabFolderError}",
                builder);
            return false;
        }

        if (!builder.GeneratedRoot && !Rebuild(builder))
            return false;

        Transform authoringRoot = builder.GeneratedRoot;
        if (!authoringRoot)
            return false;

        if (mountain
            && authoringRoot.childCount == 0)
        {
            if (!Rebuild(builder))
                return false;
            authoringRoot = builder.GeneratedRoot;
            if (!authoringRoot || authoringRoot.childCount == 0)
                return false;
        }

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Bake Path Builder");
        Undo.RecordObject(builder, "Bake Path Builder");

        bool keepAuthoring = mountain && builder.keepMountainAuthoringAfterBake;
        List<string> createdMountainAssetPaths = mountain
            ? new List<string>()
            : null;
        Transform root = authoringRoot;
        if (mountain)
        {
            if (!TryCreateIndependentMountainBakeRoot(builder, out root))
            {
                Undo.CollapseUndoOperations(undoGroup);
                return false;
            }
            Undo.RegisterCreatedObjectUndo(
                root.gameObject,
                "Create Mountain Bake Output");
        }
        else
        {
            Undo.RegisterFullObjectHierarchyUndo(
                root.gameObject,
                "Bake Path Builder");
        }

        if (mountain)
        {
            if (builder.mountainBakeOutput == MountainBakeOutput.GroundDecal
                && !TryBakeMountainCardsToGroundDecals(builder, root))
            {
                DestroyIndependentMountainBakeRoot(root);
                Undo.CollapseUndoOperations(undoGroup);
                return false;
            }
            ApplySharedMountainDecalMaterials(builder, root);
        }

        if (!mountain)
        {
            if (!SaveGeneratedPathMeshes(
                builder, root, assetOutputFolder))
            {
                Undo.CollapseUndoOperations(undoGroup);
                return false;
            }
        }
        else
        {
            try
            {
                SaveMountainCardAssets(
                    builder,
                    root,
                    createdMountainAssetPaths,
                    assetOutputFolder);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, builder);
                DestroyIndependentMountainBakeRoot(root);
                DeleteCreatedMountainAssets(createdMountainAssetPaths);
                Undo.CollapseUndoOperations(undoGroup);
                return false;
            }
            if (!HasPersistentMountainOutputDependencies(root))
            {
                Debug.LogError(
                    "[Path Builder] 山脉网格或材质未能保存，已取消 Prefab 烘焙。",
                    builder);
                DestroyIndependentMountainBakeRoot(root);
                DeleteCreatedMountainAssets(createdMountainAssetPaths);
                Undo.CollapseUndoOperations(undoGroup);
                return false;
            }
        }

        if (mountain)
            SetMountainOutputHideFlags(root);
        else
            SetHideFlagsRecursively(root, HideFlags.None);
        Transform anchor = builder.Anchor;
        root.name = mountain
            ? $"{anchor.gameObject.name}_Mountains_Baked"
            : $"{anchor.gameObject.name}_"
                + $"{GetRibbonOutputName(builder.ribbonPreset)}_Baked";
        Undo.SetTransformParent(root, anchor.parent, "Bake Path Builder");

        AssetDatabase.SaveAssets();
        if (mountain && !TrySaveMountainPrefab(
            builder,
            root,
            anchor,
            createdMountainAssetPaths,
            prefabOutputFolder))
        {
            DestroyIndependentMountainBakeRoot(root);
            DeleteCreatedMountainAssets(createdMountainAssetPaths);
            Undo.CollapseUndoOperations(undoGroup);
            return false;
        }

        if (keepAuthoring)
        {
            if (authoringRoot && authoringRoot.gameObject.activeSelf)
            {
                Undo.RecordObject(
                    authoringRoot.gameObject,
                    "Hide Baked Mountain Preview");
                authoringRoot.gameObject.SetActive(false);
            }
            builder.GeneratedRoot = authoringRoot;
            PathBuilderAuthoringStorage.Save(builder);
        }
        else
        {
            if (mountain && authoringRoot && authoringRoot != root)
                ClearGenerated(builder);
            builder.GeneratedRoot = null;
            builder.Anchor = null;
            builder.autoRebuild = false;
        }
        EditorUtility.SetDirty(builder);
        EditorUtility.SetDirty(root.gameObject);
        AssetDatabase.SaveAssets();
        if (!keepAuthoring)
            Undo.DestroyObjectImmediate(anchor.gameObject);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root.gameObject;
        return true;
    }

    private static bool TryCreateIndependentMountainBakeRoot(
        PathSpline source,
        out Transform root)
    {
        root = null;
        PathSpline bakeBuilder = Object.Instantiate(source);
        if (!bakeBuilder)
            return false;

        bakeBuilder.hideFlags = HideFlags.HideAndDontSave;
        bakeBuilder.Anchor = source.Anchor;
        bakeBuilder.GeneratedRoot = null;
        bakeBuilder.MountainFacingCamera =
            PathBuilderAuthoringStorage.ResolveMountainFacingCamera(source);
        bool built = Rebuild(bakeBuilder);
        if (built)
            root = bakeBuilder.GeneratedRoot;
        bakeBuilder.GeneratedRoot = null;
        Object.DestroyImmediate(bakeBuilder);

        if (root && root.childCount > 0)
            return true;
        if (root)
            DestroyIndependentMountainBakeRoot(root);
        root = null;
        return false;
    }

    private static void DestroyIndependentMountainBakeRoot(Transform root)
    {
        if (!root)
            return;
        DestroyGeneratedPreviewResources(root);
        Undo.DestroyObjectImmediate(root.gameObject);
    }

    private static bool HasPersistentMountainOutputDependencies(
        Transform root)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (!IsGeneratedMountainOutputMesh(mesh))
                continue;
            if (!AssetDatabase.Contains(mesh))
                return false;

            MeshRenderer renderer = filters[i].GetComponent<MeshRenderer>();
            if (!renderer
                || !renderer.sharedMaterial
                || !AssetDatabase.Contains(renderer.sharedMaterial))
            {
                return false;
            }
        }
        return root.childCount > 0;
    }

    private static bool TrySaveMountainPrefab(
        PathSpline builder,
        Transform root,
        Transform anchor,
        ICollection<string> createdAssetPaths,
        string prefabOutputFolder)
    {
        EnsureAssetFolder(prefabOutputFolder);
        string safeName = MakeSafeFileName(anchor.gameObject.name);
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(
            $"{prefabOutputFolder}/{safeName}_Mountains.prefab");
        createdAssetPaths.Add(assetPath);
        GameObject prefabAsset;
        try
        {
            prefabAsset = PrefabUtility.SaveAsPrefabAssetAndConnect(
                root.gameObject,
                assetPath,
                InteractionMode.UserAction);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, builder);
            return false;
        }
        if (!prefabAsset)
        {
            Debug.LogError(
                $"[Path Builder] 无法创建山脉 Prefab：{assetPath}",
                builder);
            return false;
        }

        EditorGUIUtility.PingObject(prefabAsset);
        return true;
    }

    private static void DeleteCreatedMountainAssets(
        IList<string> assetPaths)
    {
        if (assetPaths == null)
            return;
        for (int i = assetPaths.Count - 1; i >= 0; i--)
        {
            string assetPath = assetPaths[i];
            if (!string.IsNullOrEmpty(assetPath)
                && AssetDatabase.LoadMainAssetAtPath(assetPath))
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }
        AssetDatabase.SaveAssets();
    }

    private static void DestroyGeneratedPreviewResources(Transform root)
    {
        HashSet<Mesh> meshes = new HashSet<Mesh>();
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (mesh
                && !AssetDatabase.Contains(mesh)
                && (mesh.name.EndsWith("Preview", StringComparison.Ordinal)
                    || mesh.name.Contains("_Preview")))
            {
                meshes.Add(mesh);
            }
        }
        foreach (Mesh mesh in meshes)
            Object.DestroyImmediate(mesh);

        HashSet<Material> materials = new HashSet<Material>();
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] sharedMaterials = renderers[i].sharedMaterials;
            for (int materialIndex = 0;
                materialIndex < sharedMaterials.Length;
                materialIndex++)
            {
                Material material = sharedMaterials[materialIndex];
                if (material
                    && !AssetDatabase.Contains(material)
                    && material.name.StartsWith(
                        "[PathBuilder]",
                        StringComparison.Ordinal))
                {
                    materials.Add(material);
                }
            }
        }
        foreach (Material material in materials)
            Object.DestroyImmediate(material);
    }

    private static void SaveMountainCardAssets(
        PathSpline builder,
        Transform root,
        ICollection<string> createdAssetPaths,
        string assetOutputFolder)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        bool hasGeneratedCards = false;
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (mesh
                && !AssetDatabase.Contains(mesh)
                && IsGeneratedMountainOutputMesh(mesh))
            {
                hasGeneratedCards = true;
                break;
            }
        }
        if (!hasGeneratedCards)
            return;

        EnsureAssetFolder(assetOutputFolder);
        string materialOutputFolder = assetOutputFolder.EndsWith(
                "/Models", StringComparison.OrdinalIgnoreCase)
            ? assetOutputFolder.Substring(0, assetOutputFolder.Length - "/Models".Length)
                + "/Materials"
            : assetOutputFolder + "/Materials";
        EnsureAssetFolder(materialOutputFolder);
        string safeName = MakeSafeFileName(builder.Anchor.gameObject.name);
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (!mesh
                || AssetDatabase.Contains(mesh)
                || !IsGeneratedMountainOutputMesh(mesh))
            {
                continue;
            }

            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{assetOutputFolder}/{safeName}_{mesh.name.Replace("_Preview", string.Empty)}.asset");
            mesh.hideFlags = HideFlags.None;
            createdAssetPaths.Add(assetPath);
            AssetDatabase.CreateAsset(mesh, assetPath);
        }

        HashSet<Material> materials = new HashSet<Material>();
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            Material[] sharedMaterials = renderers[i].sharedMaterials;
            for (int materialIndex = 0;
                materialIndex < sharedMaterials.Length;
                materialIndex++)
            {
                Material material = sharedMaterials[materialIndex];
                if (material
                    && !AssetDatabase.Contains(material)
                    && material.name.StartsWith(
                        "[PathBuilder]",
                        StringComparison.Ordinal))
                {
                    materials.Add(material);
                }
            }
        }
        foreach (Material material in materials)
        {
            string materialName = MakeSafeFileName(
                material.name.Replace("[PathBuilder] ", string.Empty));
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{materialOutputFolder}/{safeName}_{materialName}.mat");
            material.hideFlags = HideFlags.None;
            createdAssetPaths.Add(assetPath);
            AssetDatabase.CreateAsset(material, assetPath);
        }
    }

    private static bool TryBakeMountainCardsToGroundDecals(
        PathSpline builder,
        Transform root)
    {
        Camera camera =
            PathBuilderAuthoringStorage.ResolveMountainFacingCamera(builder);
        if (!camera)
        {
            Debug.LogWarning(
                "[Path Builder] 烘焙地面贴花前必须指定一个正交摄像机。",
                builder);
            return false;
        }
        if (!camera.orthographic)
        {
            Debug.LogWarning(
                "[Path Builder] 山脉地面贴花只支持正交摄像机。",
                builder);
            return false;
        }
        if (camera.transform.forward.y >= -0.01f)
        {
            Debug.LogWarning(
                "[Path Builder] 指定摄像机必须朝向地面，才能烘焙山脉贴花。",
                builder);
            return false;
        }
        if (builder.mountainDecalSurfaceMask.value == 0)
        {
            Debug.LogWarning(
                "[Path Builder] 山脉贴花的地表层为空。",
                builder);
            return false;
        }

        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        List<MeshFilter> cardFilters = new List<MeshFilter>();
        int totalCards = 0;
        for (int i = 0; i < filters.Length; i++)
        {
            Mesh mesh = filters[i].sharedMesh;
            if (!IsGeneratedMountainCardMesh(mesh))
                continue;
            if (mesh.vertexCount % 4 != 0)
            {
                Debug.LogWarning(
                    $"[Path Builder] {mesh.name} 不是有效的山脉面片网格。",
                    builder);
                return false;
            }
            cardFilters.Add(filters[i]);
            totalCards += mesh.vertexCount / 4;
        }

        // A mountain path can contain prefab sources only.
        if (cardFilters.Count == 0)
            return root.childCount > 0;

        Physics.SyncTransforms();
        List<MountainDecalProjection> projections =
            new List<MountainDecalProjection>(cardFilters.Count);
        int processedCards = 0;
        int validCards = 0;
        int skippedCards = 0;
        bool canceled = false;
        try
        {
            for (int i = 0; i < cardFilters.Count; i++)
            {
                MeshFilter filter = cardFilters[i];
                Mesh sourceMesh = filter.sharedMesh;
                if (!TryProjectMountainCardMesh(
                    builder,
                    camera,
                    filter,
                    sourceMesh,
                    ref processedCards,
                    totalCards,
                    out Mesh decalMesh,
                    out int meshValidCards,
                    out int meshSkippedCards,
                    out canceled))
                {
                    DestroyMountainDecalProjections(projections);
                    if (canceled)
                    {
                        Debug.Log(
                            "[Path Builder] 已取消山脉地面贴花烘焙。",
                            builder);
                    }
                    else
                    {
                        Debug.LogWarning(
                            $"[Path Builder] 无法把 {sourceMesh.name} 投射到地表。",
                            builder);
                    }
                    return false;
                }

                validCards += meshValidCards;
                skippedCards += meshSkippedCards;
                projections.Add(new MountainDecalProjection
                {
                    filter = filter,
                    sourceMesh = sourceMesh,
                    decalMesh = decalMesh
                });
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (validCards == 0)
        {
            DestroyMountainDecalProjections(projections);
            Debug.LogWarning(
                "[Path Builder] 没有山脉面片命中地表。请检查地表层和 Collider。",
                builder);
            return false;
        }

        HashSet<Mesh> sourceMeshes = new HashSet<Mesh>();
        HashSet<Material> unusedMaterials = new HashSet<Material>();
        for (int i = 0; i < projections.Count; i++)
        {
            MountainDecalProjection projection = projections[i];
            sourceMeshes.Add(projection.sourceMesh);
            if (!projection.decalMesh)
            {
                MeshRenderer renderer =
                    projection.filter.GetComponent<MeshRenderer>();
                if (renderer
                    && renderer.sharedMaterial
                    && !AssetDatabase.Contains(renderer.sharedMaterial))
                {
                    unusedMaterials.Add(renderer.sharedMaterial);
                }
                Undo.DestroyObjectImmediate(projection.filter.gameObject);
                continue;
            }

            Undo.RecordObject(projection.filter, "Bake Mountain Ground Decal");
            projection.filter.sharedMesh = projection.decalMesh;
            EditorUtility.SetDirty(projection.filter);
        }
        foreach (Mesh sourceMesh in sourceMeshes)
        {
            if (sourceMesh && !AssetDatabase.Contains(sourceMesh))
                Undo.DestroyObjectImmediate(sourceMesh);
        }
        HashSet<Material> retainedMaterials = new HashSet<Material>();
        Renderer[] remainingRenderers =
            root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < remainingRenderers.Length; i++)
        {
            Material[] sharedMaterials = remainingRenderers[i].sharedMaterials;
            for (int materialIndex = 0;
                materialIndex < sharedMaterials.Length;
                materialIndex++)
            {
                if (sharedMaterials[materialIndex])
                    retainedMaterials.Add(sharedMaterials[materialIndex]);
            }
        }
        foreach (Material material in unusedMaterials)
        {
            if (material && !retainedMaterials.Contains(material))
                Undo.DestroyObjectImmediate(material);
        }

        if (skippedCards > 0)
        {
            Debug.LogWarning(
                $"[Path Builder] 地面贴花已生成；跳过 {skippedCards} 张未完整命中地表的山图。",
                root);
        }
        return true;
    }

    private static bool TryProjectMountainCardMesh(
        PathSpline builder,
        Camera camera,
        MeshFilter filter,
        Mesh sourceMesh,
        ref int processedCards,
        int totalCards,
        out Mesh decalMesh,
        out int validCards,
        out int skippedCards,
        out bool canceled)
    {
        decalMesh = null;
        validCards = 0;
        skippedCards = 0;
        canceled = false;

        Vector3[] sourceVertices = sourceMesh.vertices;
        Vector2[] sourceUv = sourceMesh.uv;
        Color[] sourceColors = sourceMesh.colors;
        if (sourceVertices.Length == 0
            || sourceVertices.Length % 4 != 0
            || sourceUv == null
            || sourceUv.Length != sourceVertices.Length)
        {
            return false;
        }

        List<Vector3> vertices = new List<Vector3>(sourceVertices.Length);
        List<Vector3> normals = new List<Vector3>(sourceVertices.Length);
        List<Vector4> tangents = new List<Vector4>(sourceVertices.Length);
        List<Vector2> uv = new List<Vector2>(sourceVertices.Length);
        List<Color> colors = new List<Color>(sourceVertices.Length);
        List<int> triangles = new List<int>(sourceVertices.Length / 2 * 3);
        Vector3 cameraForward = camera.transform.forward.normalized;
        Vector3 cameraRight = camera.transform.right.normalized;
        int cardCount = sourceVertices.Length / 4;
        Vector3[] sourcePositions = new Vector3[4];
        RaycastHit[] hits = new RaycastHit[4];
        MountainDecalVertex[] corners = new MountainDecalVertex[4];

        for (int card = 0; card < cardCount; card++)
        {
            if ((card & 31) == 0)
            {
                float progress = totalCards > 0
                    ? processedCards / (float)totalCards
                    : 0f;
                canceled = EditorUtility.DisplayCancelableProgressBar(
                    "烘焙山脉地面贴花",
                    $"正在投射 {processedCards} / {totalCards} 张山图",
                    progress);
                if (canceled)
                    return false;
            }

            int sourceIndex = card * 4;
            bool allCornersHit = true;
            for (int corner = 0; corner < 4; corner++)
            {
                sourcePositions[corner] = filter.transform.TransformPoint(
                    sourceVertices[sourceIndex + corner]);
                if (!TryRaycastMountainDecalSurface(
                    builder,
                    sourcePositions[corner],
                    cameraForward,
                    out hits[corner]))
                {
                    allCornersHit = false;
                    break;
                }
            }

            if (!allCornersHit)
            {
                skippedCards++;
                processedCards++;
                continue;
            }

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 worldPosition = hits[corner].point
                    + hits[corner].normal
                        * builder.mountainDecalSurfaceOffset;
                corners[corner] = new MountainDecalVertex
                {
                    worldPosition = worldPosition,
                    worldNormal = hits[corner].normal
                };
            }
            if (!IsMountainDecalQuadContinuous(
                corners[0],
                corners[1],
                corners[2],
                corners[3],
                sourcePositions,
                cameraForward))
            {
                skippedCards++;
                processedCards++;
                continue;
            }

            for (int corner = 0; corner < 4; corner++)
            {
                Vector3 tangentWorld = Vector3.ProjectOnPlane(
                    cameraRight,
                    hits[corner].normal);
                if (tangentWorld.sqrMagnitude < 0.000001f)
                {
                    tangentWorld = Vector3.Cross(
                        hits[corner].normal,
                        cameraForward);
                }
                tangentWorld.Normalize();
                Vector3 localTangent = filter.transform
                    .InverseTransformDirection(tangentWorld)
                    .normalized;
                corners[corner].index = vertices.Count;
                vertices.Add(filter.transform.InverseTransformPoint(
                    corners[corner].worldPosition));
                normals.Add(filter.transform
                    .InverseTransformDirection(hits[corner].normal)
                    .normalized);
                tangents.Add(new Vector4(
                    localTangent.x,
                    localTangent.y,
                    localTangent.z,
                    -1f));
                uv.Add(sourceUv[sourceIndex + corner]);
                colors.Add(sourceColors != null
                    && sourceColors.Length == sourceVertices.Length
                        ? sourceColors[sourceIndex + corner]
                        : builder.mountainCardColor);
            }

            if ((corners[0].worldPosition - corners[2].worldPosition)
                    .sqrMagnitude
                <= (corners[1].worldPosition - corners[3].worldPosition)
                    .sqrMagnitude)
            {
                AppendMountainDecalTriangle(
                    triangles,
                    corners[0],
                    corners[1],
                    corners[2]);
                AppendMountainDecalTriangle(
                    triangles,
                    corners[0],
                    corners[2],
                    corners[3]);
            }
            else
            {
                AppendMountainDecalTriangle(
                    triangles,
                    corners[0],
                    corners[1],
                    corners[3]);
                AppendMountainDecalTriangle(
                    triangles,
                    corners[1],
                    corners[2],
                    corners[3]);
            }
            validCards++;
            processedCards++;
        }

        if (triangles.Count == 0)
            return true;

        decalMesh = new Mesh
        {
            name = sourceMesh.name
                .Replace("MountainCards_", "MountainDecals_")
                .Replace("_Preview", string.Empty)
                + "_Preview",
            indexFormat = vertices.Count > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16,
            hideFlags = HideFlags.DontSaveInBuild
        };
        decalMesh.SetVertices(vertices);
        decalMesh.SetNormals(normals);
        decalMesh.SetTangents(tangents);
        decalMesh.SetUVs(0, uv);
        decalMesh.SetColors(colors);
        decalMesh.SetTriangles(triangles, 0, true);
        decalMesh.RecalculateBounds();
        return true;
    }

    private static bool TryRaycastMountainDecalSurface(
        PathSpline builder,
        Vector3 sourcePosition,
        Vector3 cameraForward,
        out RaycastHit hit)
    {
        float distance = Mathf.Max(
            0.1f,
            builder.mountainDecalProjectionDistance);
        Vector3 origin = sourcePosition - cameraForward * (distance * 0.5f);
        return Physics.Raycast(
            origin,
            cameraForward,
            out hit,
            distance,
            builder.mountainDecalSurfaceMask,
            QueryTriggerInteraction.Ignore);
    }

    private static float GetMountainDecalProjectedLength(
        Vector3 axis,
        Vector3 cameraForward)
    {
        if (Mathf.Abs(cameraForward.y) < 0.0001f)
            return axis.magnitude;
        Vector3 groundAxis = axis
            - cameraForward * (axis.y / cameraForward.y);
        return groundAxis.magnitude;
    }

    private static void AppendMountainDecalTriangle(
        ICollection<int> triangles,
        MountainDecalVertex first,
        MountainDecalVertex second,
        MountainDecalVertex third)
    {
        Vector3 triangleNormal = Vector3.Cross(
            second.worldPosition - first.worldPosition,
            third.worldPosition - first.worldPosition);
        Vector3 surfaceNormal = first.worldNormal
            + second.worldNormal
            + third.worldNormal;
        triangles.Add(first.index);
        if (Vector3.Dot(triangleNormal, surfaceNormal) >= 0f)
        {
            triangles.Add(second.index);
            triangles.Add(third.index);
        }
        else
        {
            triangles.Add(third.index);
            triangles.Add(second.index);
        }
    }

    private static bool IsMountainDecalQuadContinuous(
        MountainDecalVertex bottomLeft,
        MountainDecalVertex topLeft,
        MountainDecalVertex topRight,
        MountainDecalVertex bottomRight,
        IReadOnlyList<Vector3> sourcePositions,
        Vector3 cameraForward)
    {
        float leftMaximum = Mathf.Max(
            0.01f,
            GetMountainDecalProjectedLength(
                sourcePositions[1] - sourcePositions[0],
                cameraForward)) * MountainDecalMaximumStretchFactor;
        float topMaximum = Mathf.Max(
            0.01f,
            GetMountainDecalProjectedLength(
                sourcePositions[2] - sourcePositions[1],
                cameraForward)) * MountainDecalMaximumStretchFactor;
        float rightMaximum = Mathf.Max(
            0.01f,
            GetMountainDecalProjectedLength(
                sourcePositions[3] - sourcePositions[2],
                cameraForward)) * MountainDecalMaximumStretchFactor;
        float bottomMaximum = Mathf.Max(
            0.01f,
            GetMountainDecalProjectedLength(
                sourcePositions[0] - sourcePositions[3],
                cameraForward)) * MountainDecalMaximumStretchFactor;
        return (bottomLeft.worldPosition - topLeft.worldPosition).sqrMagnitude
                <= leftMaximum * leftMaximum
            && (topLeft.worldPosition - topRight.worldPosition).sqrMagnitude
                <= topMaximum * topMaximum
            && (topRight.worldPosition - bottomRight.worldPosition).sqrMagnitude
                <= rightMaximum * rightMaximum
            && (bottomRight.worldPosition - bottomLeft.worldPosition).sqrMagnitude
                <= bottomMaximum * bottomMaximum;
    }

    private static void DestroyMountainDecalProjections(
        IList<MountainDecalProjection> projections)
    {
        for (int i = 0; i < projections.Count; i++)
        {
            Mesh mesh = projections[i].decalMesh;
            if (mesh && !AssetDatabase.Contains(mesh))
                Object.DestroyImmediate(mesh);
        }
    }

    private static bool IsGeneratedMountainCardMesh(Mesh mesh)
    {
        return mesh
            && mesh.name.StartsWith(
                "MountainCards_",
                StringComparison.Ordinal);
    }

    private static bool IsGeneratedMountainOutputMesh(Mesh mesh)
    {
        return mesh
            && (mesh.name.StartsWith(
                    "MountainCards_",
                    StringComparison.Ordinal)
                || mesh.name.StartsWith(
                    "MountainDecals_",
                    StringComparison.Ordinal));
    }

    private static bool BuildMountains(
        PathSpline builder,
        Transform root,
        IList<PathSample> arcTable,
        bool logWarnings)
    {
        List<MountainSourceEntry> palette = GetValidPalette(builder);
        if (palette.Count == 0)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] 山脉没有可用资源。", builder);
            return false;
        }

        float totalWeight = 0f;
        for (int i = 0; i < palette.Count; i++)
            totalWeight += Mathf.Max(0f, palette[i].weight);
        if (totalWeight <= 0f)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] 山脉资源权重之和必须大于 0。", builder);
            return false;
        }

        System.Random random = new System.Random(builder.randomSeed);
        float totalLength = arcTable[arcTable.Count - 1].distance;
        System.Random distributionRandom = new System.Random(
            unchecked(builder.randomSeed ^ 0x5F3759DF));
        List<float> placementDistances =
            BuildMountainPlacementDistances(
                builder,
                totalLength,
                distributionRandom);
        int createdCount = 0;
        List<MountainCardPlacement> cardPlacements =
            new List<MountainCardPlacement>();

        for (int placementIndex = 0;
            placementIndex < placementDistances.Count;
            placementIndex++)
        {
            float distance = placementDistances[placementIndex];
            if (PathSplineUtility.TryEvaluateAtDistance(
                arcTable, distance, out PathSample sample))
            {
                MountainSourceEntry entry =
                    ChooseWeighted(random, palette, totalWeight);
                if (entry != null)
                {
                    Vector3 forward = FlattenDirection(sample.tangent);
                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                    float scatterWidth = builder.useSplineWidth
                        ? sample.width
                        : builder.brushWidth;
                    float lateral = NextRange(
                        random,
                        -scatterWidth * 0.5f,
                        scatterWidth * 0.5f);
                    Vector3 position = sample.position + right * lateral;
                    if (builder.mountainHeightMode
                        == MountainHeightMode.FollowSurface)
                    {
                        TryProjectToSurface(builder, ref position);
                    }

                    float horizontalScale;
                    float verticalScale;
                    if (builder.uniformScale)
                    {
                        float scale =
                            NextRange(random, builder.uniformScaleRange);
                        horizontalScale = scale;
                        verticalScale = scale;
                    }
                    else
                    {
                        horizontalScale =
                            NextRange(random, builder.horizontalScaleRange);
                        verticalScale =
                            NextRange(random, builder.verticalScaleRange);
                    }
                    if (entry.sourceType == MountainSourceType.Prefab)
                    {
                        if (BuildMountainPrefab(
                            builder,
                            root,
                            entry,
                            position,
                            forward,
                            horizontalScale,
                            verticalScale,
                            random,
                            createdCount))
                        {
                            createdCount++;
                        }
                    }
                    else if (TryCreateMountainCardPlacement(
                        builder,
                        entry,
                        position,
                        horizontalScale,
                        verticalScale,
                        random,
                        out MountainCardPlacement placement))
                    {
                        placement.sequence = cardPlacements.Count;
                        cardPlacements.Add(placement);
                        createdCount++;
                    }
                }
            }
        }

        if (cardPlacements.Count > 0
            && !BuildMountainCardMeshes(
                builder,
                root,
                cardPlacements,
                logWarnings))
        {
            createdCount -= cardPlacements.Count;
        }

        return createdCount > 0;
    }

    private static List<float> BuildMountainPlacementDistances(
        PathSpline builder,
        float totalLength,
        System.Random random)
    {
        float spacing = Mathf.Max(0.05f, builder.mountainSpacing);
        int count = Mathf.Clamp(
            Mathf.RoundToInt(totalLength / spacing),
            1,
            MaximumMountainPlacements);
        List<float> randomDistances = new List<float>(count);
        for (int i = 0; i < count; i++)
        {
            randomDistances.Add(
                (float)random.NextDouble() * totalLength);
        }
        randomDistances.Sort();

        float uniformity = Mathf.Clamp01(
            builder.mountainDistributionUniformity);
        List<float> result = new List<float>(count);
        for (int i = 0; i < count; i++)
        {
            float uniformDistance = totalLength * (i + 0.5f) / count;
            result.Add(Mathf.Lerp(
                randomDistances[i],
                uniformDistance,
                uniformity));
        }
        return result;
    }

    private static bool BuildMountainPrefab(
        PathSpline builder,
        Transform root,
        MountainSourceEntry entry,
        Vector3 position,
        Vector3 forward,
        float horizontalScale,
        float verticalScale,
        System.Random random,
        int index)
    {
        if (!entry.prefab)
            return false;

        float yaw = NextRange(random, builder.yawRange);
        Quaternion rotation = builder.alignToSpline
            ? Quaternion.LookRotation(forward, Vector3.up)
                * Quaternion.Euler(0f, yaw, 0f)
            : Quaternion.Euler(0f, yaw, 0f);
        GameObject instance = InstantiateSmart(entry.prefab);
        if (!instance)
            return false;

        instance.transform.SetParent(root, true);
        instance.transform.SetPositionAndRotation(
            position,
            rotation * entry.prefab.transform.rotation);
        Vector3 baseScale = entry.prefab.transform.localScale;
        instance.transform.localScale = new Vector3(
            baseScale.x * horizontalScale,
            baseScale.y * verticalScale,
            baseScale.z * horizontalScale);
        instance.name = $"{entry.prefab.name}_{index:D3}";
        return true;
    }

    private static bool TryCreateMountainCardPlacement(
        PathSpline builder,
        MountainSourceEntry entry,
        Vector3 position,
        float horizontalScale,
        float verticalScale,
        System.Random random,
        out MountainCardPlacement placement)
    {
        placement = default;
        Texture2D texture;
        Rect uv;
        float aspect;
        Vector2 pivot = new Vector2(0.5f, 0f);

        if (entry.sourceType == MountainSourceType.SpriteCard)
        {
            if (!entry.sprite || !entry.sprite.texture)
                return false;
            texture = entry.sprite.texture;
            Vector4 outerUv = DataUtility.GetOuterUV(entry.sprite);
            uv = Rect.MinMaxRect(
                outerUv.x,
                outerUv.y,
                outerUv.z,
                outerUv.w);
            aspect = entry.sprite.rect.height > 0.001f
                ? entry.sprite.rect.width / entry.sprite.rect.height
                : 1f;
            if (builder.mountainCardPivot == MountainCardPivot.SpritePivot)
            {
                pivot = new Vector2(
                    entry.sprite.pivot.x
                        / Mathf.Max(1f, entry.sprite.rect.width),
                    entry.sprite.pivot.y
                        / Mathf.Max(1f, entry.sprite.rect.height));
            }
        }
        else
        {
            if (!entry.texture)
                return false;
            texture = entry.texture;
            uv = new Rect(0f, 0f, 1f, 1f);
            aspect = texture.height > 0
                ? texture.width / (float)texture.height
                : 1f;
        }

        placement.texture = texture;
        placement.uv = uv;
        placement.pivot = pivot;
        placement.position = position;
        placement.width = builder.mountainCardHeight
            * aspect * horizontalScale;
        placement.height = builder.mountainCardHeight * verticalScale;
        placement.flipX = builder.mountainCardRandomFlip
            && random.NextDouble() < 0.5;
        return true;
    }

    private static bool BuildMountainCardMeshes(
        PathSpline builder,
        Transform root,
        IList<MountainCardPlacement> placements,
        bool logWarnings)
    {
        Camera camera =
            PathBuilderAuthoringStorage.ResolveMountainFacingCamera(builder);
        if (!camera)
            camera = Camera.main;
        if (!camera && SceneView.lastActiveSceneView)
            camera = SceneView.lastActiveSceneView.camera;
        if (!camera)
        {
            if (logWarnings)
            {
                Debug.LogWarning(
                    "[Path Builder] 没有找到 Main Camera 或 Scene Camera，无法确定面片山朝向。",
                    builder);
            }
            return false;
        }
        if (!camera.orthographic && logWarnings)
        {
            Debug.LogWarning(
                "[Path Builder] 面片山按平行方向朝向摄像机，建议使用正交摄像机。",
                builder);
        }

        Vector3 cameraRight = camera.transform.right.normalized;
        Vector3 cameraUp = camera.transform.up.normalized;
        Vector3 cameraForward = camera.transform.forward.normalized;
        Vector3 cameraNormal = -camera.transform.forward.normalized;
        Dictionary<Texture2D, Material> materials =
            new Dictionary<Texture2D, Material>();
        int meshIndex = 0;

        if (builder.mountainCardSortingMode
            == MountainCardSortingMode.AccurateBackToFront)
        {
            List<MountainCardPlacement> sorted =
                new List<MountainCardPlacement>(placements);
            SortMountainCardsBackToFront(sorted, cameraForward);
            int batchCount = CountMountainCardBatches(sorted);
            int batchIndex = 0;
            int start = 0;
            while (start < sorted.Count)
            {
                Texture2D texture = sorted[start].texture;
                int count = 1;
                while (start + count < sorted.Count
                    && count < MountainCardsPerMesh
                    && sorted[start + count].texture == texture)
                {
                    count++;
                }

                Material material = GetOrCreateMountainCardMaterial(
                    builder,
                    texture,
                    materials);
                if (material)
                {
                    int sortingOrder = Mathf.Clamp(
                        batchIndex - batchCount / 2,
                        short.MinValue,
                        short.MaxValue);
                    CreateMountainCardMesh(
                        builder,
                        root,
                        texture,
                        material,
                        sorted,
                        start,
                        count,
                        cameraRight,
                        cameraUp,
                        cameraNormal,
                        meshIndex++,
                        sortingOrder);
                }
                start += count;
                batchIndex++;
            }
        }
        else
        {
            Dictionary<Texture2D, List<MountainCardPlacement>> groups =
                new Dictionary<Texture2D, List<MountainCardPlacement>>();
            for (int i = 0; i < placements.Count; i++)
            {
                MountainCardPlacement placement = placements[i];
                if (!groups.TryGetValue(
                    placement.texture,
                    out List<MountainCardPlacement> group))
                {
                    group = new List<MountainCardPlacement>();
                    groups.Add(placement.texture, group);
                }
                group.Add(placement);
            }

            foreach (KeyValuePair<Texture2D, List<MountainCardPlacement>> pair
                in groups)
            {
                SortMountainCardsBackToFront(pair.Value, cameraForward);
                Material material = GetOrCreateMountainCardMaterial(
                    builder,
                    pair.Key,
                    materials);
                if (!material)
                    continue;

                for (int start = 0;
                    start < pair.Value.Count;
                    start += MountainCardsPerMesh)
                {
                    int count = Mathf.Min(
                        MountainCardsPerMesh,
                        pair.Value.Count - start);
                    CreateMountainCardMesh(
                        builder,
                        root,
                        pair.Key,
                        material,
                        pair.Value,
                        start,
                        count,
                        cameraRight,
                        cameraUp,
                        cameraNormal,
                        meshIndex++,
                        0);
                }
            }
        }
        return meshIndex > 0;
    }

    private static void SortMountainCardsBackToFront(
        List<MountainCardPlacement> placements,
        Vector3 cameraForward)
    {
        placements.Sort((left, right) =>
        {
            float leftDepth = Vector3.Dot(left.position, cameraForward);
            float rightDepth = Vector3.Dot(right.position, cameraForward);
            int depthOrder = rightDepth.CompareTo(leftDepth);
            return depthOrder != 0
                ? depthOrder
                : left.sequence.CompareTo(right.sequence);
        });
    }

    private static int CountMountainCardBatches(
        IList<MountainCardPlacement> placements)
    {
        int batchCount = 0;
        int start = 0;
        while (start < placements.Count)
        {
            Texture2D texture = placements[start].texture;
            int count = 1;
            while (start + count < placements.Count
                && count < MountainCardsPerMesh
                && placements[start + count].texture == texture)
            {
                count++;
            }
            start += count;
            batchCount++;
        }
        return batchCount;
    }

    private static Material GetOrCreateMountainCardMaterial(
        PathSpline builder,
        Texture2D texture,
        IDictionary<Texture2D, Material> materials)
    {
        if (materials.TryGetValue(texture, out Material material))
            return material;

        material = CreateMountainCardMaterial(builder, texture);
        if (material)
            materials.Add(texture, material);
        return material;
    }

    private static void CreateMountainCardMesh(
        PathSpline builder,
        Transform root,
        Texture2D texture,
        Material material,
        IList<MountainCardPlacement> placements,
        int start,
        int count,
        Vector3 cameraRight,
        Vector3 cameraUp,
        Vector3 cameraNormal,
        int meshIndex,
        int sortingOrder)
    {
        Vector3[] vertices = new Vector3[count * 4];
        Vector3[] normals = new Vector3[count * 4];
        Vector4[] tangents = new Vector4[count * 4];
        Vector2[] uv = new Vector2[count * 4];
        Color[] colors = new Color[count * 4];
        int[] triangles = new int[count * 6];
        Vector3 localNormal =
            root.InverseTransformDirection(cameraNormal).normalized;
        Vector3 localRight =
            root.InverseTransformDirection(cameraRight).normalized;
        Vector4 tangent = new Vector4(
            localRight.x,
            localRight.y,
            localRight.z,
            -1f);

        for (int i = 0; i < count; i++)
        {
            MountainCardPlacement placement = placements[start + i];
            float left = -placement.pivot.x * placement.width;
            float right = (1f - placement.pivot.x) * placement.width;
            float bottom = -placement.pivot.y * placement.height;
            float top = (1f - placement.pivot.y) * placement.height;
            Vector3 bottomLeft = placement.position
                + cameraRight * left + cameraUp * bottom;
            Vector3 topLeft = placement.position
                + cameraRight * left + cameraUp * top;
            Vector3 topRight = placement.position
                + cameraRight * right + cameraUp * top;
            Vector3 bottomRight = placement.position
                + cameraRight * right + cameraUp * bottom;

            int vertex = i * 4;
            vertices[vertex] = root.InverseTransformPoint(bottomLeft);
            vertices[vertex + 1] = root.InverseTransformPoint(topLeft);
            vertices[vertex + 2] = root.InverseTransformPoint(topRight);
            vertices[vertex + 3] = root.InverseTransformPoint(bottomRight);
            for (int corner = 0; corner < 4; corner++)
            {
                normals[vertex + corner] = localNormal;
                tangents[vertex + corner] = tangent;
                colors[vertex + corner] = builder.mountainCardColor;
            }

            float uLeft = placement.flipX
                ? placement.uv.xMax
                : placement.uv.xMin;
            float uRight = placement.flipX
                ? placement.uv.xMin
                : placement.uv.xMax;
            uv[vertex] = new Vector2(uLeft, placement.uv.yMin);
            uv[vertex + 1] = new Vector2(uLeft, placement.uv.yMax);
            uv[vertex + 2] = new Vector2(uRight, placement.uv.yMax);
            uv[vertex + 3] = new Vector2(uRight, placement.uv.yMin);

            int triangle = i * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 1;
            triangles[triangle + 2] = vertex + 2;
            triangles[triangle + 3] = vertex;
            triangles[triangle + 4] = vertex + 2;
            triangles[triangle + 5] = vertex + 3;
        }

        Mesh mesh = new Mesh
        {
            name = $"MountainCards_{texture.name}_{meshIndex:D2}_Preview",
            indexFormat = vertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16,
            hideFlags = HideFlags.DontSaveInBuild
        };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.uv = uv;
        mesh.colors = colors;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        GameObject cardObject = new GameObject(
            $"Mountain Cards - {texture.name} {meshIndex:D2}");
        cardObject.transform.SetParent(root, false);
        MeshFilter filter = cardObject.AddComponent<MeshFilter>();
        MeshRenderer renderer = cardObject.AddComponent<MeshRenderer>();
        filter.sharedMesh = mesh;
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = builder.mountainCardSortingLayerId;
        renderer.sortingOrder = Mathf.Clamp(
            builder.mountainCardSortingOrder + sortingOrder,
            short.MinValue,
            short.MaxValue);
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    private static Material CreateMountainCardMaterial(
        PathSpline builder,
        Texture2D texture)
    {
        Material material;
        Material template = builder.mountainDecalSet
            && builder.mountainDecalSet.baseMaterial
                ? builder.mountainDecalSet.baseMaterial
                : builder.mountainCardMaterial;
        if (template)
        {
            material = new Material(template);
        }
        else
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (!shader)
                shader = Shader.Find("Unlit/Transparent");
            if (!shader)
                return null;
            material = new Material(shader);
        }

        material.name = $"[PathBuilder] Mountain Card - {texture.name}";
        material.hideFlags = HideFlags.DontSaveInBuild;
        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", 0f);

        ConfigureMountainCardTransparency(
            material,
            builder.mountainDecalSet
                ? builder.mountainDecalSet.renderQueue
                : builder.mountainCardRenderQueue);
        return material;
    }

    private static void ConfigureMountainCardTransparency(
        Material material,
        int renderQueue)
    {
        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend"))
            material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_AlphaClip"))
            material.SetFloat("_AlphaClip", 0f);
        if (material.HasProperty("_Cutoff"))
            material.SetFloat("_Cutoff", 0f);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);
        if (material.HasProperty("_SrcBlend"))
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);

        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHAMODULATE_ON");
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.SetOverrideTag("RenderType", "Transparent");
        material.renderQueue = Mathf.Clamp(renderQueue, 2501, 3999);
    }

    private static void ApplySharedMountainDecalMaterials(
        PathSpline builder,
        Transform root)
    {
        MountainDecalSet decalSet = builder.mountainDecalSet;
        if (!decalSet || !AssetDatabase.Contains(decalSet))
            return;

        Dictionary<Texture2D, Material> sharedMaterials =
            new Dictionary<Texture2D, Material>();
        HashSet<Material> temporaryMaterials = new HashSet<Material>();
        MeshRenderer[] renderers =
            root.GetComponentsInChildren<MeshRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            MeshRenderer renderer = renderers[i];
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (!filter || !IsGeneratedMountainOutputMesh(filter.sharedMesh))
                continue;

            Material formerMaterial = renderer.sharedMaterial;
            Texture2D texture = GetMountainCardTexture(formerMaterial);
            if (!texture)
                continue;
            if (!sharedMaterials.TryGetValue(
                texture,
                out Material sharedMaterial))
            {
                sharedMaterial = GetOrCreateSharedMountainDecalMaterial(
                    decalSet,
                    texture);
                if (!sharedMaterial)
                    continue;
                sharedMaterials.Add(texture, sharedMaterial);
            }

            Undo.RecordObject(renderer, "Use Shared Mountain Material");
            renderer.sharedMaterial = sharedMaterial;
            EditorUtility.SetDirty(renderer);
            if (formerMaterial
                && formerMaterial != sharedMaterial
                && !AssetDatabase.Contains(formerMaterial))
            {
                temporaryMaterials.Add(formerMaterial);
            }
        }

        foreach (Material material in temporaryMaterials)
        {
            if (material)
                Undo.DestroyObjectImmediate(material);
        }
        EditorUtility.SetDirty(decalSet);
    }

    internal static void RefreshMountainDecalSetMaterials(
        MountainDecalSet decalSet)
    {
        if (!decalSet
            || !AssetDatabase.Contains(decalSet)
            || decalSet.materialPages == null)
        {
            return;
        }

        for (int i = 0; i < decalSet.materialPages.Count; i++)
        {
            MountainDecalMaterialPage page = decalSet.materialPages[i];
            if (page == null || !page.texture)
                continue;
            if (page.material)
            {
                Undo.RecordObject(
                    page.material,
                    "Update Mountain Decal Set Materials");
            }
            GetOrCreateSharedMountainDecalMaterial(decalSet, page.texture);
        }
        AssetDatabase.SaveAssetIfDirty(decalSet);
    }

    private static Texture2D GetMountainCardTexture(Material material)
    {
        if (!material)
            return null;
        if (material.HasProperty("_BaseMap"))
        {
            Texture2D texture = material.GetTexture("_BaseMap") as Texture2D;
            if (texture)
                return texture;
        }
        return material.HasProperty("_MainTex")
            ? material.GetTexture("_MainTex") as Texture2D
            : null;
    }

    private static Material GetOrCreateSharedMountainDecalMaterial(
        MountainDecalSet decalSet,
        Texture2D texture)
    {
        if (!decalSet || !texture || !AssetDatabase.Contains(decalSet))
            return null;
        if (decalSet.materialPages == null)
        {
            decalSet.materialPages =
                new List<MountainDecalMaterialPage>();
        }

        MountainDecalMaterialPage page = null;
        for (int i = 0; i < decalSet.materialPages.Count; i++)
        {
            MountainDecalMaterialPage candidate =
                decalSet.materialPages[i];
            if (candidate != null && candidate.texture == texture)
            {
                page = candidate;
                break;
            }
        }
        if (page == null)
        {
            page = new MountainDecalMaterialPage { texture = texture };
            decalSet.materialPages.Add(page);
        }

        Shader shader = decalSet.baseMaterial
            ? decalSet.baseMaterial.shader
            : Shader.Find("Universal Render Pipeline/Unlit");
        if (!shader)
            shader = Shader.Find("Unlit/Transparent");
        if (!shader)
            return null;

        Material material = page.material;
        if (!material)
        {
            material = new Material(shader)
            {
                name = $"Mountain Decal - {texture.name}",
                hideFlags = HideFlags.HideInHierarchy
            };
            AssetDatabase.AddObjectToAsset(material, decalSet);
            page.material = material;
        }
        else
        {
            material.shader = shader;
        }

        if (decalSet.baseMaterial)
        {
            material.CopyPropertiesFromMaterial(decalSet.baseMaterial);
        }
        else
        {
            Material defaults = new Material(shader);
            material.CopyPropertiesFromMaterial(defaults);
            Object.DestroyImmediate(defaults);
        }

        material.name = $"Mountain Decal - {texture.name}";
        material.hideFlags = HideFlags.HideInHierarchy;
        if (material.HasProperty("_BaseMap"))
            material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", Color.white);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", Color.white);
        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", 0f);
        ConfigureMountainCardTransparency(material, decalSet.renderQueue);
        EditorUtility.SetDirty(material);
        EditorUtility.SetDirty(decalSet);
        return material;
    }

    private static bool BuildMeshDeform(
        PathSpline builder,
        PathOutputLayer output,
        Transform root,
        IList<PathSample> arcTable,
        bool logWarnings)
    {
        GameObject sourceObject = ResolveMeshDeformSource(output);
        if (!sourceObject)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] Mesh Deform 没有可用的 Source Object。", builder);
            return false;
        }
        if (!TryGetPrefabAxisBounds(
            sourceObject,
            output,
            out float axisMinimum,
            out float axisMaximum,
            out float lateralCenter,
            out float verticalMinimum,
            out float verticalCenter))
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] Source Prefab 中没有可变形的 MeshFilter。", builder);
            return false;
        }

        float sourceLength = Mathf.Max(0.05f, axisMaximum - axisMinimum);
        float totalLength = arcTable[arcTable.Count - 1].distance;
        BuildTransportFrames(arcTable, out Vector3[] frameRights,
            out Vector3[] frameUps);
        float moduleLength = output.repeat ? sourceLength : totalLength;
        moduleLength = Mathf.Max(0.05f, moduleLength);
        float moduleStep = Mathf.Max(
            0.01f, moduleLength + output.spacing);
        float distance = 0f;
        int moduleIndex = 0;
        while (distance < totalLength - 0.0001f && moduleIndex < 100000)
        {
            float remaining = totalLength - distance;
            float span = output.repeat ? Mathf.Min(moduleLength, remaining) : totalLength;
            if (output.repeat && !output.fitLastSegment && remaining < moduleLength)
                break;

            GameObject instance = InstantiateMeshDeformSource(
                output, sourceObject);
            if (!instance)
                return false;
            instance.name = $"{sourceObject.name}_{moduleIndex:D3}";
            instance.transform.SetParent(root, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            if (!DeformPrefabMeshes(
                output,
                sourceObject,
                instance,
                root,
                arcTable,
                distance,
                span,
                axisMinimum,
                axisMaximum,
                lateralCenter,
                verticalMinimum,
                verticalCenter,
                frameRights,
                frameUps))
            {
                Object.DestroyImmediate(instance);
                return false;
            }
            distance += output.repeat ? moduleStep : totalLength;
            moduleIndex++;
            if (!output.repeat)
                break;
        }
        return moduleIndex > 0;
    }

    private static bool BuildPrefabPlace(
        PathSpline builder,
        PathOutputLayer output,
        Transform root,
        IList<PathSample> arcTable,
        bool logWarnings)
    {
        GameObject sourceObject = ResolveMeshDeformSource(output);
        if (!sourceObject)
        {
            if (logWarnings)
                Debug.LogWarning(
                    "[Path Builder] Prefab Place 没有可用的 Source Object。",
                    builder);
            return false;
        }
        if (!TryGetPrefabAxisBounds(
            sourceObject,
            output,
            out float axisMinimum,
            out float axisMaximum,
            out _,
            out _,
            out _))
        {
            if (logWarnings)
            {
                Debug.LogWarning(
                    "[Path Builder] 请在 Mesh 设置中明确选择 Segment Mesh。",
                    builder);
            }
            return false;
        }

        float moduleLength = Mathf.Max(
            0.05f, axisMaximum - axisMinimum);
        float totalLength = arcTable[arcTable.Count - 1].distance;
        float step = Mathf.Max(0.01f, moduleLength + output.spacing);
        BuildTransportFrames(
            arcTable, out Vector3[] frameRights, out Vector3[] frameUps);
        int moduleCount = output.repeat
            ? Mathf.Max(1, Mathf.FloorToInt(
                Mathf.Max(0f, totalLength + output.spacing) / step))
            : 1;
        Quaternion axisCorrection = output.forwardAxis == PathForwardAxis.X
            ? Quaternion.Euler(0f, -90f, 0f)
            : Quaternion.identity;
        Quaternion sourceCorrection = Quaternion.Euler(output.rotationOffset);

        for (int moduleIndex = 0; moduleIndex < moduleCount; moduleIndex++)
        {
            float centerDistance = output.repeat
                ? moduleIndex * step + moduleLength * 0.5f
                : totalLength * 0.5f;
            centerDistance = Mathf.Clamp(centerDistance, 0f, totalLength);
            if (!PathSplineUtility.TryEvaluateAtDistance(
                arcTable, centerDistance, out PathSample sample))
            {
                return false;
            }
            Vector3 forward = sample.tangent.normalized;
            EvaluateTransportFrame(
                arcTable,
                frameRights,
                frameUps,
                sample.distance,
                forward,
                out Vector3 right,
                out Vector3 up);
            GameObject instance = InstantiateMeshDeformSource(
                output, sourceObject);
            if (!instance)
                return false;
            instance.name = $"{sourceObject.name}_{moduleIndex:D3}";
            instance.transform.SetParent(root, true);
            Quaternion pathRotation = Quaternion.LookRotation(forward, up);
            instance.transform.SetPositionAndRotation(
                sample.position
                    + right * output.positionOffset.x
                    + up * output.positionOffset.y
                    + forward * output.positionOffset.z,
                pathRotation * axisCorrection * sourceCorrection);
            instance.transform.localScale = Vector3.Scale(
                sourceObject.transform.localScale, output.scale);
        }
        return moduleCount > 0;
    }

    internal static bool TryGetMeshDeformMetrics(
        PathSpline builder,
        PathOutputLayer output,
        out float detectedLength,
        out float pathLength,
        out int plannedCount)
    {
        detectedLength = 0f;
        pathLength = 0f;
        plannedCount = 0;
        GameObject source = ResolveMeshDeformSource(output);
        if (!builder || output == null || !source
            || !TryGetPrefabAxisBounds(
                source,
                output,
                out float minimum,
                out float maximum,
                out _,
                out _,
                out _))
        {
            return false;
        }
        detectedLength = Mathf.Max(0.05f, maximum - minimum);
        List<PathSample> arcTable = PathSplineUtility.BuildArcTable(builder);
        if (arcTable.Count == 0)
            return false;
        pathLength = arcTable[arcTable.Count - 1].distance;
        float step = Mathf.Max(0.01f, detectedLength + output.spacing);
        plannedCount = output.fitLastSegment
            ? Mathf.CeilToInt(Mathf.Max(0f, pathLength) / step)
            : Mathf.FloorToInt(
                Mathf.Max(0f, pathLength + output.spacing) / step);
        plannedCount = Mathf.Max(0, plannedCount);
        return true;
    }

    private static GameObject ResolveMeshDeformSource(PathOutputLayer output)
    {
        if (output == null)
            return null;
        if (output.sourcePrefab)
            return output.sourcePrefab;
        if (string.IsNullOrEmpty(output.sourceSceneObjectGlobalId)
            || !GlobalObjectId.TryParse(
                output.sourceSceneObjectGlobalId,
                out GlobalObjectId globalId))
        {
            return null;
        }
        return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId)
            as GameObject;
    }

    private static GameObject InstantiateMeshDeformSource(
        PathOutputLayer output,
        GameObject source)
    {
        if (!source)
            return null;
        return output != null
            && !string.IsNullOrEmpty(output.sourceSceneObjectGlobalId)
            ? Object.Instantiate(source)
            : InstantiateSmart(source);
    }

    private static bool TryGetPrefabAxisBounds(
        GameObject source,
        PathOutputLayer output,
        out float minimum,
        out float maximum,
        out float lateralCenter,
        out float verticalMinimum,
        out float verticalCenter)
    {
        minimum = 0f;
        maximum = 0f;
        lateralCenter = 0f;
        verticalMinimum = 0f;
        verticalCenter = 0f;
        bool found = false;
        Matrix4x4 worldToRoot = source.transform.worldToLocalMatrix;
        Matrix4x4 sourceRootBasis = GetSourceRootBasis(
            source.transform, output.useRootPosition);
        Quaternion outputRotation = Quaternion.Euler(output.rotationOffset);
        MeshFilter segmentFilter = ResolveSegmentMeshFilter(source, output);
        if (!segmentFilter || !segmentFilter.sharedMesh)
            return false;
        Mesh mesh = segmentFilter.sharedMesh;
        Matrix4x4 toRoot = worldToRoot
            * segmentFilter.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            bool meshFound = false;
            float meshAxisMinimum = 0f;
            float meshAxisMaximum = 0f;
            float meshLateralMinimum = 0f;
            float meshLateralMaximum = 0f;
            float meshVerticalMinimum = 0f;
            float meshVerticalMaximum = 0f;
            for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
            {
                Vector3 point = toRoot.MultiplyPoint3x4(vertices[vertexIndex]);
                point = sourceRootBasis.MultiplyVector(point);
                point = Vector3.Scale(point, output.scale);
                point = outputRotation * point;
                float axis = output.forwardAxis == PathForwardAxis.X
                    ? point.x
                    : point.z;
                float lateral = output.forwardAxis == PathForwardAxis.X
                    ? point.z
                    : point.x;
                if (!meshFound)
                {
                    meshAxisMinimum = meshAxisMaximum = axis;
                    meshLateralMinimum = meshLateralMaximum = lateral;
                    meshVerticalMinimum = meshVerticalMaximum = point.y;
                    meshFound = true;
                }
                else
                {
                    meshAxisMinimum = Mathf.Min(meshAxisMinimum, axis);
                    meshAxisMaximum = Mathf.Max(meshAxisMaximum, axis);
                    meshLateralMinimum = Mathf.Min(
                        meshLateralMinimum, lateral);
                    meshLateralMaximum = Mathf.Max(
                        meshLateralMaximum, lateral);
                    meshVerticalMinimum = Mathf.Min(
                        meshVerticalMinimum, point.y);
                    meshVerticalMaximum = Mathf.Max(
                        meshVerticalMaximum, point.y);
                }
            }
        if (meshFound)
        {
            minimum = meshAxisMinimum + output.startSegmentOffset;
            maximum = meshAxisMaximum - output.endSegmentOffset;
            lateralCenter = (meshLateralMinimum
                + meshLateralMaximum) * 0.5f;
            verticalMinimum = meshVerticalMinimum;
            verticalCenter = (meshVerticalMinimum
                + meshVerticalMaximum) * 0.5f;
            found = true;
        }
        return found && maximum - minimum > 0.0001f;
    }

    private static MeshFilter ResolveSegmentMeshFilter(
        GameObject source,
        PathOutputLayer output)
    {
        if (!source || output == null)
            return null;
        MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 1 && string.IsNullOrEmpty(output.segmentMeshPath))
            return filters[0];
        if (string.IsNullOrEmpty(output.segmentMeshPath))
            return null;
        Transform target = output.segmentMeshPath == "."
            ? source.transform
            : source.transform.Find(output.segmentMeshPath);
        return target ? target.GetComponent<MeshFilter>() : null;
    }

    private static Matrix4x4 GetSourceRootBasis(
        Transform sourceRoot,
        bool includePosition)
    {
        Matrix4x4 basis = sourceRoot.localToWorldMatrix;
        Vector3 relativePosition = Vector3.zero;
        if (includePosition)
        {
            relativePosition = sourceRoot.parent
                ? sourceRoot.position - sourceRoot.parent.position
                : sourceRoot.localPosition;
        }
        // 去掉父级的世界平移，只保留根节点相对父级的位置；旋转、缩放
        // 仍使用完整层级矩阵，因此 Hierarchy 中的实际外观会被保留。
        basis.m03 = relativePosition.x;
        basis.m13 = relativePosition.y;
        basis.m23 = relativePosition.z;
        return basis;
    }

    private static bool DeformPrefabMeshes(
        PathOutputLayer output,
        GameObject source,
        GameObject instance,
        Transform outputRoot,
        IList<PathSample> arcTable,
        float startDistance,
        float spanDistance,
        float axisMinimum,
        float axisMaximum,
        float lateralCenter,
        float verticalMinimum,
        float verticalCenter,
        Vector3[] frameRights,
        Vector3[] frameUps)
    {
        MeshFilter[] sourceFilters = source.GetComponentsInChildren<MeshFilter>(true);
        MeshFilter[] targetFilters = instance.GetComponentsInChildren<MeshFilter>(true);
        if (sourceFilters.Length != targetFilters.Length)
            return false;
        Matrix4x4 sourceWorldToLocal = source.transform.worldToLocalMatrix;
        Matrix4x4 sourceRootBasis = GetSourceRootBasis(
            source.transform, output.useRootPosition);
        Quaternion localRotation = Quaternion.Euler(output.rotationOffset);

        for (int filterIndex = 0; filterIndex < sourceFilters.Length; filterIndex++)
        {
            Mesh sourceMesh = sourceFilters[filterIndex].sharedMesh;
            MeshFilter targetFilter = targetFilters[filterIndex];
            if (!sourceMesh || !targetFilter)
                continue;
            string partPath = GetMeshPartPath(
                source, sourceFilters[filterIndex]);
            PathMeshPartSetting partSetting = GetMeshPartSetting(
                output, partPath);
            PathMeshPartMode partMode = partSetting != null
                ? partSetting.mode
                : PathMeshPartMode.Deform;
            if (partMode == PathMeshPartMode.Ignore)
            {
                targetFilter.sharedMesh = null;
                MeshCollider ignoredCollider =
                    targetFilter.GetComponent<MeshCollider>();
                if (ignoredCollider)
                    ignoredCollider.sharedMesh = null;
                continue;
            }
            Matrix4x4 toSourceRoot = sourceWorldToLocal
                * sourceFilters[filterIndex].transform.localToWorldMatrix;
            Matrix4x4 pointTransform = Matrix4x4.Rotate(localRotation)
                * Matrix4x4.Scale(output.scale)
                * sourceRootBasis
                * toSourceRoot;
            int subdivisionCount = partSetting != null
                ? Mathf.Clamp(partSetting.segments, 1, 32)
                : 1;
            Mesh workingMesh = partMode == PathMeshPartMode.Deform
                && subdivisionCount > 1
                    ? CreateLongitudinallySubdividedMesh(
                        sourceMesh,
                        pointTransform,
                        output.forwardAxis,
                        subdivisionCount)
                    : null;
            if (!workingMesh)
                workingMesh = sourceMesh;
            Vector3[] sourceVertices = workingMesh.vertices;
            Vector3[] targetVertices = new Vector3[sourceVertices.Length];
            Vector3[] sourceNormals = workingMesh.normals;
            Vector4[] sourceTangents = workingMesh.tangents;
            bool hasNormals = sourceNormals != null
                && sourceNormals.Length == sourceVertices.Length;
            bool hasTangents = sourceTangents != null
                && sourceTangents.Length == sourceVertices.Length;
            Vector3[] targetNormals = hasNormals
                ? new Vector3[sourceVertices.Length]
                : null;
            Vector4[] targetTangents = hasTangents
                ? new Vector4[sourceVertices.Length]
                : null;
            Matrix4x4 normalTransform = pointTransform.inverse.transpose;
            PathSample rigidSample = default;
            Vector3 rigidForward = Vector3.forward;
            Vector3 rigidRight = Vector3.right;
            Vector3 rigidUp = Vector3.up;
            float rigidAxis = 0f;
            if (partMode == PathMeshPartMode.Place)
            {
                Vector3 pivot = pointTransform.MultiplyPoint3x4(Vector3.zero);
                rigidAxis = output.forwardAxis == PathForwardAxis.X
                    ? pivot.x
                    : pivot.z;
                float pivotNormalized = (rigidAxis - axisMinimum)
                    / Mathf.Max(0.0001f, axisMaximum - axisMinimum);
                if (!PathSplineUtility.TryEvaluateAtDistance(
                    arcTable,
                    startDistance + pivotNormalized * spanDistance,
                    out rigidSample))
                {
                    return false;
                }
                rigidForward = rigidSample.tangent.normalized;
                EvaluateTransportFrame(
                    arcTable,
                    frameRights,
                    frameUps,
                    rigidSample.distance,
                    rigidForward,
                    out rigidRight,
                    out rigidUp);
            }
            for (int vertexIndex = 0; vertexIndex < sourceVertices.Length; vertexIndex++)
            {
                Vector3 point = pointTransform.MultiplyPoint3x4(
                    sourceVertices[vertexIndex]);
                float axis = output.forwardAxis == PathForwardAxis.X
                    ? point.x : point.z;
                float lateral = output.forwardAxis == PathForwardAxis.X
                    ? point.z : point.x;
                float height = point.y;
                if (output.pivotAlignment != PathPivotAlignment.SourcePivot)
                    lateral -= lateralCenter;
                if (output.pivotAlignment == PathPivotAlignment.BoundsCenter)
                    height -= verticalCenter;
                else if (output.pivotAlignment
                    == PathPivotAlignment.BoundsBottomCenter)
                    height -= verticalMinimum;

                if (partMode == PathMeshPartMode.Place)
                {
                    Vector3 rigidWorld = rigidSample.position
                        + rigidRight * (lateral + output.positionOffset.x)
                        + rigidUp * (height + output.positionOffset.y)
                        + rigidForward * (
                            axis - rigidAxis + output.positionOffset.z);
                    targetVertices[vertexIndex] =
                        targetFilter.transform.InverseTransformPoint(
                            rigidWorld);
                    TransformDeformedVertexBasis(
                        sourceNormals,
                        sourceTangents,
                        vertexIndex,
                        pointTransform,
                        normalTransform,
                        output.forwardAxis,
                        rigidRight,
                        rigidUp,
                        rigidForward,
                        targetFilter,
                        targetNormals,
                        targetTangents);
                    continue;
                }

                float normalized = (axis - axisMinimum)
                    / Mathf.Max(0.0001f, axisMaximum - axisMinimum);
                if (!PathSplineUtility.TryEvaluateAtDistance(
                    arcTable,
                    startDistance + normalized * spanDistance,
                    out PathSample sample))
                {
                    return false;
                }
                Vector3 forward = sample.tangent.normalized;
                EvaluateTransportFrame(
                    arcTable,
                    frameRights,
                    frameUps,
                    sample.distance,
                    forward,
                    out Vector3 right,
                    out Vector3 up);
                if (output.cornerHandling
                    == PathCornerHandling.CompressInner)
                {
                    lateral = ApplyInnerCornerCompression(
                        arcTable,
                        sample.distance,
                        lateral,
                        up,
                        output.cornerSafety);
                }
                Vector3 world = sample.position
                    + right * (lateral + output.positionOffset.x)
                    + up * (height + output.positionOffset.y)
                    + forward * output.positionOffset.z;
                targetVertices[vertexIndex] =
                    targetFilter.transform.InverseTransformPoint(world);
                TransformDeformedVertexBasis(
                    sourceNormals,
                    sourceTangents,
                    vertexIndex,
                    pointTransform,
                    normalTransform,
                    output.forwardAxis,
                    right,
                    up,
                    forward,
                    targetFilter,
                    targetNormals,
                    targetTangents);
            }
            Mesh mesh = workingMesh == sourceMesh
                ? Object.Instantiate(sourceMesh)
                : workingMesh;
            mesh.name = sourceMesh.name + "_PathDeformPreview";
            mesh.hideFlags = HideFlags.DontSaveInBuild;
            mesh.vertices = targetVertices;
            if (targetNormals != null)
                mesh.normals = targetNormals;
            else
                mesh.RecalculateNormals();
            if (targetTangents != null)
                mesh.tangents = targetTangents;
            else if (mesh.uv != null && mesh.uv.Length == targetVertices.Length)
                mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            targetFilter.sharedMesh = mesh;
            if (output.updateCollider)
            {
                MeshCollider collider = targetFilter.GetComponent<MeshCollider>();
                if (collider)
                    collider.sharedMesh = mesh;
            }
        }
        return true;
    }

    private static float ApplyInnerCornerCompression(
        IList<PathSample> arcTable,
        float distance,
        float lateral,
        Vector3 up,
        float safety)
    {
        float curvature = EvaluateSignedCurvature(
            arcTable, distance, up);
        if (Mathf.Abs(curvature) < 0.00001f
            || curvature * lateral <= 0f)
        {
            return lateral;
        }

        // 当内侧横向距离接近局部曲率半径时，普通样条挤出会越过
        // 曲率中心并翻折。tanh 保持中心附近近似不变，再平滑限制内侧宽度。
        float safeRadius = Mathf.Max(
            0.01f,
            Mathf.Clamp(safety, 0.5f, 0.95f)
                / Mathf.Abs(curvature));
        float ratio = Mathf.Min(Mathf.Abs(lateral) / safeRadius, 20f);
        double exponent = Math.Exp(2.0 * ratio);
        float compressed = safeRadius
            * (float)((exponent - 1.0) / (exponent + 1.0));
        return Mathf.Sign(lateral) * compressed;
    }

    private static float EvaluateSignedCurvature(
        IList<PathSample> arcTable,
        float distance,
        Vector3 up)
    {
        if (arcTable == null || arcTable.Count < 3)
            return 0f;

        int low = 0;
        int high = arcTable.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (arcTable[middle].distance < distance)
                low = middle + 1;
            else
                high = middle;
        }

        int center = Mathf.Clamp(low, 1, arcTable.Count - 2);
        int lower = Mathf.Max(0, center - 1);
        int upper = Mathf.Min(arcTable.Count - 1, center + 1);
        float distanceSpan = arcTable[upper].distance
            - arcTable[lower].distance;
        if (distanceSpan <= 0.0001f)
            return 0f;

        Vector3 lowerTangent = arcTable[lower].tangent.normalized;
        Vector3 upperTangent = arcTable[upper].tangent.normalized;
        if (lowerTangent.sqrMagnitude < 0.000001f
            || upperTangent.sqrMagnitude < 0.000001f)
        {
            return 0f;
        }

        return Vector3.SignedAngle(
                lowerTangent,
                upperTangent,
                up)
            * Mathf.Deg2Rad
            / distanceSpan;
    }

    private static void TransformDeformedVertexBasis(
        Vector3[] sourceNormals,
        Vector4[] sourceTangents,
        int vertexIndex,
        Matrix4x4 pointTransform,
        Matrix4x4 normalTransform,
        PathForwardAxis forwardAxis,
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        MeshFilter targetFilter,
        Vector3[] targetNormals,
        Vector4[] targetTangents)
    {
        if (targetNormals != null)
        {
            Vector3 sourceNormal = normalTransform.MultiplyVector(
                sourceNormals[vertexIndex]).normalized;
            Vector3 worldNormal = MapSourceVectorToFrame(
                sourceNormal, forwardAxis, right, up, forward);
            targetNormals[vertexIndex] = targetFilter.transform
                .InverseTransformDirection(worldNormal).normalized;
        }
        if (targetTangents != null)
        {
            Vector4 sourceTangent = sourceTangents[vertexIndex];
            Vector3 orientedTangent = pointTransform.MultiplyVector(
                new Vector3(
                    sourceTangent.x,
                    sourceTangent.y,
                    sourceTangent.z)).normalized;
            Vector3 worldTangent = MapSourceVectorToFrame(
                orientedTangent, forwardAxis, right, up, forward);
            Vector3 localTangent = targetFilter.transform
                .InverseTransformDirection(worldTangent).normalized;
            targetTangents[vertexIndex] = new Vector4(
                localTangent.x,
                localTangent.y,
                localTangent.z,
                sourceTangent.w);
        }
    }

    private static Vector3 MapSourceVectorToFrame(
        Vector3 sourceVector,
        PathForwardAxis forwardAxis,
        Vector3 right,
        Vector3 up,
        Vector3 forward)
    {
        return forwardAxis == PathForwardAxis.X
            ? right * sourceVector.z
                + up * sourceVector.y
                + forward * sourceVector.x
            : right * sourceVector.x
                + up * sourceVector.y
                + forward * sourceVector.z;
    }

    private static PathMeshPartSetting GetMeshPartSetting(
        PathOutputLayer output,
        string path)
    {
        if (output == null || output.meshParts == null)
            return null;
        for (int i = 0; i < output.meshParts.Count; i++)
        {
            PathMeshPartSetting setting = output.meshParts[i];
            if (setting != null && setting.transformPath == path)
                return setting;
        }
        return null;
    }

    private static string GetMeshPartPath(
        GameObject source,
        MeshFilter filter)
    {
        string path = AnimationUtility.CalculateTransformPath(
            filter.transform, source.transform);
        return string.IsNullOrEmpty(path) ? "." : path;
    }

    private struct SliceVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector4 tangent;
        public Color color;
        public Vector2 uv;
        public float axis;
    }

    private static Mesh CreateLongitudinallySubdividedMesh(
        Mesh source,
        Matrix4x4 pointTransform,
        PathForwardAxis forwardAxis,
        int segmentCount)
    {
        const int vertexBudget = 500000;
        segmentCount = Mathf.Clamp(segmentCount, 1, 32);
        if (!source || segmentCount <= 1)
            return null;

        Vector3[] positions = source.vertices;
        if (positions == null || positions.Length == 0)
            return null;
        Vector3[] normals = source.normals;
        Vector4[] tangents = source.tangents;
        Color[] colors = source.colors;
        Vector2[] uvs = source.uv;
        bool hasNormals = normals != null && normals.Length == positions.Length;
        bool hasTangents = tangents != null && tangents.Length == positions.Length;
        bool hasColors = colors != null && colors.Length == positions.Length;
        bool hasUvs = uvs != null && uvs.Length == positions.Length;

        SliceVertex[] sourceData = new SliceVertex[positions.Length];
        float axisMinimum = float.PositiveInfinity;
        float axisMaximum = float.NegativeInfinity;
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 transformed = pointTransform.MultiplyPoint3x4(positions[i]);
            float axis = forwardAxis == PathForwardAxis.X
                ? transformed.x
                : transformed.z;
            axisMinimum = Mathf.Min(axisMinimum, axis);
            axisMaximum = Mathf.Max(axisMaximum, axis);
            sourceData[i] = new SliceVertex
            {
                position = positions[i],
                normal = hasNormals ? normals[i] : Vector3.up,
                tangent = hasTangents
                    ? tangents[i]
                    : new Vector4(1f, 0f, 0f, 1f),
                color = hasColors ? colors[i] : Color.white,
                uv = hasUvs ? uvs[i] : Vector2.zero,
                axis = axis
            };
        }
        float axisLength = axisMaximum - axisMinimum;
        if (axisLength <= 0.0001f)
            return null;

        List<Vector3> outputPositions = new List<Vector3>();
        List<Vector3> outputNormals = new List<Vector3>();
        List<Vector4> outputTangents = new List<Vector4>();
        List<Color> outputColors = new List<Color>();
        List<Vector2> outputUvs = new List<Vector2>();
        List<int>[] outputTriangles = new List<int>[source.subMeshCount];
        for (int subMesh = 0; subMesh < source.subMeshCount; subMesh++)
        {
            outputTriangles[subMesh] = new List<int>();
            int[] triangles = source.GetTriangles(subMesh);
            for (int triangleIndex = 0;
                triangleIndex + 2 < triangles.Length;
                triangleIndex += 3)
            {
                List<SliceVertex> triangle = new List<SliceVertex>(3)
                {
                    sourceData[triangles[triangleIndex]],
                    sourceData[triangles[triangleIndex + 1]],
                    sourceData[triangles[triangleIndex + 2]]
                };
                for (int segment = 0; segment < segmentCount; segment++)
                {
                    float low = Mathf.Lerp(
                        axisMinimum,
                        axisMaximum,
                        segment / (float)segmentCount);
                    float high = Mathf.Lerp(
                        axisMinimum,
                        axisMaximum,
                        (segment + 1f) / segmentCount);
                    List<SliceVertex> polygon = ClipSlicePolygon(
                        triangle, low, true);
                    polygon = ClipSlicePolygon(polygon, high, false);
                    if (polygon.Count < 3)
                        continue;
                    int first = AddSliceVertex(
                        polygon[0],
                        outputPositions,
                        outputNormals,
                        outputTangents,
                        outputColors,
                        outputUvs);
                    for (int polygonIndex = 1;
                        polygonIndex + 1 < polygon.Count;
                        polygonIndex++)
                    {
                        int second = AddSliceVertex(
                            polygon[polygonIndex],
                            outputPositions,
                            outputNormals,
                            outputTangents,
                            outputColors,
                            outputUvs);
                        int third = AddSliceVertex(
                            polygon[polygonIndex + 1],
                            outputPositions,
                            outputNormals,
                            outputTangents,
                            outputColors,
                            outputUvs);
                        outputTriangles[subMesh].Add(first);
                        outputTriangles[subMesh].Add(second);
                        outputTriangles[subMesh].Add(third);
                        if (outputPositions.Count > vertexBudget)
                        {
                            throw new InvalidOperationException(
                                $"子模型 {source.name} 的细分结果超过 {vertexBudget} 顶点。请降低 Segments 或先简化源模型。");
                        }
                    }
                }
            }
        }

        if (outputPositions.Count == 0)
            return null;
        Mesh result = new Mesh
        {
            name = source.name + "_LongitudinalSubdivided",
            indexFormat = outputPositions.Count > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16,
            subMeshCount = source.subMeshCount,
            hideFlags = HideFlags.DontSaveInBuild
        };
        result.SetVertices(outputPositions);
        if (hasNormals)
            result.SetNormals(outputNormals);
        if (hasTangents)
            result.SetTangents(outputTangents);
        if (hasColors)
            result.SetColors(outputColors);
        if (hasUvs)
            result.SetUVs(0, outputUvs);
        for (int subMesh = 0; subMesh < outputTriangles.Length; subMesh++)
            result.SetTriangles(outputTriangles[subMesh], subMesh, false);
        result.RecalculateBounds();
        if (!hasNormals)
            result.RecalculateNormals();
        if (!hasTangents && hasUvs)
            result.RecalculateTangents();
        return result;
    }

    private static List<SliceVertex> ClipSlicePolygon(
        List<SliceVertex> input,
        float boundary,
        bool keepGreater)
    {
        List<SliceVertex> output = new List<SliceVertex>();
        if (input == null || input.Count == 0)
            return output;
        SliceVertex previous = input[input.Count - 1];
        bool previousInside = keepGreater
            ? previous.axis >= boundary - 0.00001f
            : previous.axis <= boundary + 0.00001f;
        for (int i = 0; i < input.Count; i++)
        {
            SliceVertex current = input[i];
            bool currentInside = keepGreater
                ? current.axis >= boundary - 0.00001f
                : current.axis <= boundary + 0.00001f;
            if (currentInside != previousInside)
            {
                float denominator = current.axis - previous.axis;
                float t = Mathf.Abs(denominator) > 0.000001f
                    ? (boundary - previous.axis) / denominator
                    : 0f;
                output.Add(LerpSliceVertex(previous, current, t));
            }
            if (currentInside)
                output.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return output;
    }

    private static SliceVertex LerpSliceVertex(
        SliceVertex a,
        SliceVertex b,
        float t)
    {
        t = Mathf.Clamp01(t);
        Vector4 tangent = Vector4.Lerp(a.tangent, b.tangent, t);
        Vector3 tangentDirection = new Vector3(
            tangent.x, tangent.y, tangent.z).normalized;
        return new SliceVertex
        {
            position = Vector3.LerpUnclamped(a.position, b.position, t),
            normal = Vector3.Slerp(a.normal, b.normal, t).normalized,
            tangent = new Vector4(
                tangentDirection.x,
                tangentDirection.y,
                tangentDirection.z,
                Mathf.Lerp(a.tangent.w, b.tangent.w, t) >= 0f ? 1f : -1f),
            color = Color.LerpUnclamped(a.color, b.color, t),
            uv = Vector2.LerpUnclamped(a.uv, b.uv, t),
            axis = Mathf.LerpUnclamped(a.axis, b.axis, t)
        };
    }

    private static int AddSliceVertex(
        SliceVertex vertex,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector4> tangents,
        List<Color> colors,
        List<Vector2> uvs)
    {
        int index = positions.Count;
        positions.Add(vertex.position);
        normals.Add(vertex.normal);
        tangents.Add(vertex.tangent);
        colors.Add(vertex.color);
        uvs.Add(vertex.uv);
        return index;
    }

    private static void BuildTransportFrames(
        IList<PathSample> arcTable,
        out Vector3[] rights,
        out Vector3[] ups)
    {
        int count = arcTable.Count;
        rights = new Vector3[count];
        ups = new Vector3[count];
        Vector3 forward = arcTable[0].tangent.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        if (right.sqrMagnitude < 0.000001f)
            right = Vector3.Cross(Vector3.forward, forward);
        right.Normalize();
        Vector3 up = Vector3.Cross(forward, right).normalized;
        rights[0] = right;
        ups[0] = up;

        for (int i = 1; i < count; i++)
        {
            Vector3 nextForward = arcTable[i].tangent.normalized;
            Quaternion transport = Quaternion.FromToRotation(
                forward, nextForward);
            Vector3 transportedUp = transport * up;
            right = Vector3.Cross(transportedUp, nextForward);
            if (right.sqrMagnitude < 0.000001f)
                right = transport * rights[i - 1];
            right.Normalize();
            up = Vector3.Cross(nextForward, right).normalized;
            rights[i] = right;
            ups[i] = up;
            forward = nextForward;
        }
    }

    private static void EvaluateTransportFrame(
        IList<PathSample> arcTable,
        Vector3[] rights,
        Vector3[] ups,
        float distance,
        Vector3 forward,
        out Vector3 right,
        out Vector3 up)
    {
        int low = 0;
        int high = arcTable.Count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (arcTable[middle].distance < distance)
                low = middle + 1;
            else
                high = middle;
        }
        int upper = Mathf.Clamp(low, 1, arcTable.Count - 1);
        int lower = upper - 1;
        float span = arcTable[upper].distance - arcTable[lower].distance;
        float t = span > 0.000001f
            ? (distance - arcTable[lower].distance) / span
            : 0f;
        right = Vector3.Slerp(rights[lower], rights[upper], t).normalized;
        right = Vector3.ProjectOnPlane(right, forward).normalized;
        if (right.sqrMagnitude < 0.000001f)
            right = rights[lower];
        up = Vector3.Cross(forward, right).normalized;
    }

    private static bool SaveGeneratedPathMeshes(
        PathSpline builder,
        Transform root,
        string outputFolder)
    {
        EnsureAssetFolder(outputFolder);
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        List<string> createdPaths = new List<string>();
        List<MeshFilter> changedFilters = new List<MeshFilter>();
        List<Mesh> originalMeshes = new List<Mesh>();
        List<MeshCollider> changedColliders = new List<MeshCollider>();
        List<Mesh> originalColliderMeshes = new List<Mesh>();
        Object firstAsset = null;
        int savedCount = 0;
        try
        {
            string safeName = MakeSafeFileName(builder.Anchor.gameObject.name);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh sourceMesh = filters[i].sharedMesh;
                if (!sourceMesh)
                    continue;
                if (AssetDatabase.Contains(sourceMesh))
                {
                    savedCount++;
                    continue;
                }
                string partName = MakeSafeFileName(filters[i].gameObject.name);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{outputFolder}/{safeName}_{partName}.asset");
                Mesh meshAsset = Object.Instantiate(sourceMesh);
                meshAsset.name = Path.GetFileNameWithoutExtension(assetPath);
                meshAsset.hideFlags = HideFlags.None;
                AssetDatabase.CreateAsset(meshAsset, assetPath);
                if (!AssetDatabase.Contains(meshAsset))
                    throw new InvalidOperationException(
                        $"无法保存生成 Mesh：{assetPath}");
                createdPaths.Add(assetPath);
                changedFilters.Add(filters[i]);
                originalMeshes.Add(sourceMesh);
                filters[i].sharedMesh = meshAsset;
                MeshCollider[] colliders = filters[i]
                    .GetComponents<MeshCollider>();
                for (int colliderIndex = 0;
                    colliderIndex < colliders.Length;
                    colliderIndex++)
                {
                    if (colliders[colliderIndex].sharedMesh == sourceMesh)
                    {
                        changedColliders.Add(colliders[colliderIndex]);
                        originalColliderMeshes.Add(sourceMesh);
                        colliders[colliderIndex].sharedMesh = meshAsset;
                    }
                }
                if (!firstAsset)
                    firstAsset = meshAsset;
                savedCount++;
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception, builder);
            for (int i = 0; i < changedFilters.Count; i++)
            {
                if (changedFilters[i])
                    changedFilters[i].sharedMesh = originalMeshes[i];
            }
            for (int i = 0; i < changedColliders.Count; i++)
            {
                if (changedColliders[i])
                {
                    changedColliders[i].sharedMesh =
                        originalColliderMeshes[i];
                }
            }
            for (int i = createdPaths.Count - 1; i >= 0; i--)
                AssetDatabase.DeleteAsset(createdPaths[i]);
            return false;
        }
        if (firstAsset)
            EditorGUIUtility.PingObject(firstAsset);
        return savedCount > 0;
    }

    private static bool BuildRiver(
        PathSpline builder,
        GameObject root,
        IList<PathSample> arcTable,
        bool logWarnings)
    {
        float totalLength = arcTable[arcTable.Count - 1].distance;
        int segmentCount = Mathf.Max(
            1,
            Mathf.CeilToInt(totalLength / builder.riverSegmentLength));
        int sectionCount = segmentCount + 1;

        Vector3[] vertices = new Vector3[sectionCount * 2];
        Vector3[] normals = new Vector3[vertices.Length];
        Vector4[] tangents = new Vector4[vertices.Length];
        Vector2[] uv = new Vector2[vertices.Length];
        Vector2[] uv2 = new Vector2[vertices.Length];
        int[] triangles = new int[segmentCount * 6];

        Vector3[] centers = new Vector3[sectionCount];
        float[] requestedLeftHalfWidths = new float[sectionCount];
        float[] requestedRightHalfWidths = new float[sectionCount];
        float[] distances = new float[sectionCount];

        for (int section = 0; section < sectionCount; section++)
        {
            float distance = section == sectionCount - 1
                ? totalLength
                : totalLength * section / segmentCount;

            if (!PathSplineUtility.TryEvaluateAtDistance(
                arcTable, distance, out PathSample sample))
            {
                if (logWarnings)
                    Debug.LogWarning("[Path Builder] 河流采样失败。", builder);
                return false;
            }

            Vector3 center = sample.position;
            if (builder.riverHeightMode == RiverHeightMode.FollowSurface)
                TryProjectToSurface(builder, ref center);

            centers[section] = center;
            GetRequestedRibbonHalfWidths(
                builder,
                sample.width,
                distance,
                totalLength,
                out requestedLeftHalfWidths[section],
                out requestedRightHalfWidths[section]);
            distances[section] = distance;
        }

        Vector3[] sides = new Vector3[sectionCount];
        float[] safeLeftHalfWidths = new float[sectionCount];
        float[] safeRightHalfWidths = new float[sectionCount];
        BuildSafeRiverFrames(
            centers,
            requestedLeftHalfWidths,
            requestedRightHalfWidths,
            builder.closed,
            sides,
            safeLeftHalfWidths,
            safeRightHalfWidths);

        for (int section = 0; section < sectionCount; section++)
        {
            Vector3 right = sides[section];
            Vector3 leftWorld = centers[section]
                - right * safeLeftHalfWidths[section];
            Vector3 rightWorld = centers[section]
                + right * safeRightHalfWidths[section];
            int vertexIndex = section * 2;
            vertices[vertexIndex] = root.transform.InverseTransformPoint(leftWorld);
            vertices[vertexIndex + 1] = root.transform.InverseTransformPoint(rightWorld);
            Vector3 worldNormal = GetRiverSurfaceNormal(
                centers,
                section,
                builder.closed,
                right);
            Vector3 localNormal =
                root.transform.InverseTransformDirection(worldNormal).normalized;
            normals[vertexIndex] = localNormal;
            normals[vertexIndex + 1] = localNormal;

            Vector3 localRight =
                root.transform.InverseTransformDirection(right).normalized;
            Vector4 meshTangent =
                new Vector4(localRight.x, localRight.y, localRight.z, -1f);
            tangents[vertexIndex] = meshTangent;
            tangents[vertexIndex + 1] = meshTangent;

            // The whole river shares one continuous UV strip. UV Length is the
            // final V coordinate, independent of how many mesh segments exist.
            float distance = distances[section];
            float normalizedDistance = totalLength > 0.0001f
                ? distance / totalLength
                : 0f;
            float v = normalizedDistance * builder.riverUvLength;
            uv[vertexIndex] = new Vector2(0f, v);
            uv[vertexIndex + 1] = new Vector2(1f, v);
            uv2[vertexIndex] = new Vector2(0f, v);
            uv2[vertexIndex + 1] = new Vector2(1f, v);
        }

        for (int segment = 0; segment < segmentCount; segment++)
        {
            int vertex = segment * 2;
            int triangle = segment * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 2;
            triangles[triangle + 2] = vertex + 1;
            triangles[triangle + 3] = vertex + 1;
            triangles[triangle + 4] = vertex + 2;
            triangles[triangle + 5] = vertex + 3;
        }

        Mesh mesh = new Mesh
        {
            name = $"{builder.Anchor.gameObject.name}_"
                + $"{GetRibbonOutputName(builder.ribbonPreset)}Preview",
            indexFormat = vertices.Length > 65535
                ? IndexFormat.UInt32
                : IndexFormat.UInt16,
            hideFlags = HideFlags.DontSaveInBuild
        };
        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.uv = uv;
        mesh.uv2 = uv2;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();

        MeshFilter meshFilter = root.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = root.AddComponent<MeshRenderer>();
        meshFilter.sharedMesh = mesh;
        meshRenderer.sharedMaterial = builder.riverMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        if (builder.generateEdgeObjects)
        {
            BuildEdgeObjects(
                builder,
                root.transform,
                centers,
                sides,
                safeLeftHalfWidths,
                safeRightHalfWidths,
                totalLength,
                logWarnings);
        }

        if (builder.generateCollider)
        {
            MeshCollider collider = root.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
        }

        return true;
    }

    private static string GetRibbonOutputName(RibbonPathPreset preset)
    {
        switch (preset)
        {
            case RibbonPathPreset.NaturalRoad:
                return "NaturalRoad";
            case RibbonPathPreset.RegularRoad:
                return "RegularRoad";
            default:
                return "River";
        }
    }

    private static Vector3 GetRiverSurfaceNormal(
        Vector3[] centers,
        int index,
        bool closed,
        Vector3 right)
    {
        int count = centers != null ? centers.Length : 0;
        if (count < 2)
            return Vector3.up;

        bool duplicateClosure = closed
            && count > 2
            && (centers[0] - centers[count - 1]).sqrMagnitude < 0.0001f;
        int uniqueCount = duplicateClosure ? count - 1 : count;
        int currentIndex = duplicateClosure && index == count - 1
            ? 0
            : Mathf.Clamp(index, 0, uniqueCount - 1);
        bool hasPrevious = closed || currentIndex > 0;
        bool hasNext = closed || currentIndex < uniqueCount - 1;
        int previousIndex = (currentIndex - 1 + uniqueCount) % uniqueCount;
        int nextIndex = (currentIndex + 1) % uniqueCount;

        Vector3 previousDirection = hasPrevious
            ? (centers[currentIndex] - centers[previousIndex]).normalized
            : Vector3.zero;
        Vector3 nextDirection = hasNext
            ? (centers[nextIndex] - centers[currentIndex]).normalized
            : Vector3.zero;
        Vector3 forward = previousDirection + nextDirection;
        if (forward.sqrMagnitude < 0.000001f)
            forward = hasNext ? nextDirection : previousDirection;
        if (forward.sqrMagnitude < 0.000001f)
            forward = Vector3.Cross(right, Vector3.up);
        forward.Normalize();

        Vector3 normal = Vector3.Cross(forward, right);
        if (normal.sqrMagnitude < 0.000001f)
            return Vector3.up;
        normal.Normalize();
        if (Vector3.Dot(normal, Vector3.up) < 0f)
            normal = -normal;
        return normal;
    }

    internal static void GetRequestedRibbonHalfWidths(
        PathSpline builder,
        float fullWidth,
        float distance,
        float totalLength,
        out float leftHalfWidth,
        out float rightHalfWidth)
    {
        float baseHalfWidth = Mathf.Max(
            MinimumRiverHalfWidth,
            fullWidth * 0.5f);
        leftHalfWidth = baseHalfWidth;
        rightHalfWidth = baseHalfWidth;
        if (!builder.edgeNoiseEnabled)
            return;

        float sampleDistance = builder.closed && totalLength > 0.0001f
            ? Mathf.Repeat(distance, totalLength)
            : distance;
        float coordinate = sampleDistance
            / Mathf.Max(0.1f, builder.edgeNoiseWavelength);
        float leftNoise = builder.closed && totalLength > 0.0001f
            ? EvaluateClosedEdgeNoise(
                builder.edgeNoiseSeed,
                sampleDistance / totalLength,
                totalLength / Mathf.Max(0.1f, builder.edgeNoiseWavelength),
                true)
            : EvaluateEdgeNoise(builder.edgeNoiseSeed, coordinate, true);
        float rightNoise = builder.closed && totalLength > 0.0001f
            ? EvaluateClosedEdgeNoise(
                builder.edgeNoiseSeed,
                sampleDistance / totalLength,
                totalLength / Mathf.Max(0.1f, builder.edgeNoiseWavelength),
                false)
            : EvaluateEdgeNoise(builder.edgeNoiseSeed, coordinate, false);
        leftHalfWidth = Mathf.Max(
            MinimumRiverHalfWidth,
            baseHalfWidth + leftNoise * builder.leftEdgeNoiseAmplitude);
        rightHalfWidth = Mathf.Max(
            MinimumRiverHalfWidth,
            baseHalfWidth + rightNoise * builder.rightEdgeNoiseAmplitude);
    }

    private static float EvaluateEdgeNoise(
        int seed,
        float coordinate,
        bool left)
    {
        float seedOffset = Mathf.Abs(seed % 100000) * 0.01371f;
        float sideOffset = left ? 17.31f : 83.79f;
        float primary = Mathf.PerlinNoise(
            seedOffset + sideOffset + coordinate,
            sideOffset * 0.37f) * 2f - 1f;
        float detail = Mathf.PerlinNoise(
            seedOffset * 0.53f + sideOffset + coordinate * 2.17f,
            sideOffset * 0.71f) * 2f - 1f;
        return primary * 0.75f + detail * 0.25f;
    }

    private static float EvaluateClosedEdgeNoise(
        int seed,
        float normalizedDistance,
        float approximateCycleCount,
        bool left)
    {
        float seedOffset = Mathf.Abs(seed % 100000) * 0.01371f;
        float sideOffset = left ? 17.31f : 83.79f;
        float angle = normalizedDistance * Mathf.PI * 2f;
        float radius = Mathf.Max(
            0.75f,
            approximateCycleCount / (Mathf.PI * 2f));
        float x = seedOffset + sideOffset + Mathf.Cos(angle) * radius;
        float y = sideOffset * 0.37f + Mathf.Sin(angle) * radius;
        float primary = Mathf.PerlinNoise(x, y) * 2f - 1f;
        float detail = Mathf.PerlinNoise(
            x * 1.73f + 11.7f,
            y * 1.73f + 29.3f) * 2f - 1f;
        return primary * 0.75f + detail * 0.25f;
    }

    private static void BuildEdgeObjects(
        PathSpline builder,
        Transform root,
        Vector3[] centers,
        Vector3[] sides,
        float[] leftHalfWidths,
        float[] rightHalfWidths,
        float totalLength,
        bool logWarnings)
    {
        List<PathEdgePrefabEntry> palette = GetValidEdgePalette(builder);
        if (palette.Count == 0)
        {
            if (logWarnings)
            {
                Debug.LogWarning(
                    "[Path Builder] 已开启边缘模型，但没有可用 Prefab。",
                    builder);
            }
            return;
        }

        float totalWeight = 0f;
        for (int i = 0; i < palette.Count; i++)
            totalWeight += Mathf.Max(0f, palette[i].weight);
        if (totalWeight <= 0f)
            return;

        GameObject group = new GameObject("Edge Objects");
        group.transform.SetParent(root, false);
        if (builder.generateLeftEdge)
        {
            BuildEdgeObjectSide(
                builder,
                group.transform,
                palette,
                totalWeight,
                centers,
                sides,
                leftHalfWidths,
                totalLength,
                true);
        }
        if (builder.generateRightEdge)
        {
            BuildEdgeObjectSide(
                builder,
                group.transform,
                palette,
                totalWeight,
                centers,
                sides,
                rightHalfWidths,
                totalLength,
                false);
        }

        if (group.transform.childCount == 0)
            Object.DestroyImmediate(group);
    }

    private static void BuildEdgeObjectSide(
        PathSpline builder,
        Transform parent,
        IList<PathEdgePrefabEntry> palette,
        float totalWeight,
        Vector3[] centers,
        Vector3[] sides,
        float[] halfWidths,
        float totalLength,
        bool left)
    {
        int sideSeed = unchecked(
            builder.edgeObjectSeed + (left ? 0 : 7919));
        System.Random random = new System.Random(sideSeed);
        float firstSpacing = NextRange(
            random,
            builder.edgeObjectSpacingRange);
        float distance = builder.closed ? firstSpacing * 0.5f : 0f;
        int createdCount = 0;
        int guard = 0;

        while (distance < totalLength + 0.0001f && guard++ < 100000)
        {
            if (random.NextDouble() <= builder.edgeObjectDensity
                && TryEvaluateGeneratedRiverFrame(
                    centers,
                    sides,
                    halfWidths,
                    distance,
                    totalLength,
                    out Vector3 center,
                    out Vector3 side,
                    out Vector3 forward,
                    out float halfWidth))
            {
                PathEdgePrefabEntry entry = ChooseWeightedEdge(
                    random,
                    palette,
                    totalWeight);
                if (entry != null && entry.prefab)
                {
                    float direction = left ? -1f : 1f;
                    Vector3 position = center + side * direction
                        * (halfWidth + builder.edgeObjectOutwardOffset);
                    if (builder.edgeObjectsProjectToSurface)
                        TryProjectToSurface(builder, ref position);
                    position += Vector3.up * builder.edgeObjectHeightOffset;

                    float yaw = NextRange(
                        random,
                        builder.edgeObjectYawRange);
                    Quaternion rotation = builder.edgeObjectsAlignToPath
                        ? Quaternion.LookRotation(
                            FlattenDirection(forward),
                            Vector3.up) * Quaternion.Euler(0f, yaw, 0f)
                        : Quaternion.Euler(0f, yaw, 0f);
                    GameObject instance = InstantiateSmart(entry.prefab);
                    if (instance)
                    {
                        instance.transform.SetParent(parent, true);
                        instance.transform.SetPositionAndRotation(
                            position,
                            rotation * entry.prefab.transform.rotation);
                        float scale = NextRange(
                            random,
                            builder.edgeObjectScaleRange);
                        instance.transform.localScale =
                            entry.prefab.transform.localScale * scale;
                        instance.name = string.Format(
                            "{0}_{1}_{2:D3}",
                            left ? "Left" : "Right",
                            entry.prefab.name,
                            createdCount++);
                    }
                }
            }

            distance += NextRange(random, builder.edgeObjectSpacingRange);
        }
    }

    private static bool TryEvaluateGeneratedRiverFrame(
        Vector3[] centers,
        Vector3[] sides,
        float[] halfWidths,
        float distance,
        float totalLength,
        out Vector3 center,
        out Vector3 side,
        out Vector3 forward,
        out float halfWidth)
    {
        center = Vector3.zero;
        side = Vector3.right;
        forward = Vector3.forward;
        halfWidth = MinimumRiverHalfWidth;
        int count = centers != null ? centers.Length : 0;
        if (count < 2 || sides == null || halfWidths == null)
            return false;

        float section = totalLength > 0.0001f
            ? Mathf.Clamp01(distance / totalLength) * (count - 1)
            : 0f;
        int start = Mathf.Min(Mathf.FloorToInt(section), count - 2);
        int end = start + 1;
        float t = section - start;
        center = Vector3.Lerp(centers[start], centers[end], t);
        side = Vector3.Lerp(sides[start], sides[end], t).normalized;
        if (side.sqrMagnitude < 0.000001f)
            side = sides[start];
        forward = centers[end] - centers[start];
        if (forward.sqrMagnitude < 0.000001f)
            forward = Vector3.Cross(side, Vector3.up);
        halfWidth = Mathf.Lerp(halfWidths[start], halfWidths[end], t);
        return true;
    }

    internal static void BuildSafeRiverFrames(
        Vector3[] centers,
        float[] requestedLeftHalfWidths,
        float[] requestedRightHalfWidths,
        bool closed,
        Vector3[] sides,
        float[] safeLeftHalfWidths,
        float[] safeRightHalfWidths)
    {
        if (centers == null
            || requestedLeftHalfWidths == null
            || requestedRightHalfWidths == null
            || sides == null
            || safeLeftHalfWidths == null
            || safeRightHalfWidths == null)
        {
            return;
        }

        int count = Mathf.Min(
            Mathf.Min(
                centers.Length,
                Mathf.Min(
                    requestedLeftHalfWidths.Length,
                    requestedRightHalfWidths.Length)),
            Mathf.Min(
                sides.Length,
                Mathf.Min(
                    safeLeftHalfWidths.Length,
                    safeRightHalfWidths.Length)));
        if (count == 0)
            return;

        bool duplicateClosure = closed
            && count > 2
            && (centers[0] - centers[count - 1]).sqrMagnitude < 0.0001f;
        int uniqueCount = duplicateClosure ? count - 1 : count;

        for (int index = 0; index < uniqueCount; index++)
        {
            bool hasPrevious = closed || index > 0;
            bool hasNext = closed || index < uniqueCount - 1;
            int previousIndex = (index - 1 + uniqueCount) % uniqueCount;
            int nextIndex = (index + 1) % uniqueCount;

            Vector3 previousDirection = Vector3.zero;
            Vector3 nextDirection = Vector3.zero;
            float previousLength = 0f;
            float nextLength = 0f;
            if (hasPrevious)
            {
                previousDirection = FlattenDirection(
                    centers[index] - centers[previousIndex]);
                previousLength = FlatDistance(
                    centers[index], centers[previousIndex]);
            }
            if (hasNext)
            {
                nextDirection = FlattenDirection(
                    centers[nextIndex] - centers[index]);
                nextLength = FlatDistance(centers[nextIndex], centers[index]);
            }

            Vector3 forward;
            if (hasPrevious && hasNext)
            {
                forward = previousDirection + nextDirection;
                if (forward.sqrMagnitude < 0.000001f)
                    forward = nextDirection;
            }
            else
            {
                forward = hasNext ? nextDirection : previousDirection;
            }
            forward = FlattenDirection(forward);

            Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
            if (index > 0 && Vector3.Dot(side, sides[index - 1]) < 0f)
                side = -side;
            sides[index] = side;

            float leftHalfWidth = Mathf.Max(
                MinimumRiverHalfWidth,
                requestedLeftHalfWidths[index]);
            float rightHalfWidth = Mathf.Max(
                MinimumRiverHalfWidth,
                requestedRightHalfWidths[index]);
            safeLeftHalfWidths[index] = leftHalfWidth;
            safeRightHalfWidths[index] = rightHalfWidth;
            if (hasPrevious
                && hasNext
                && previousLength > 0.0001f
                && nextLength > 0.0001f)
            {
                float turn = Mathf.Acos(Mathf.Clamp(
                    Vector3.Dot(previousDirection, nextDirection),
                    -1f,
                    1f));
                float sine = Mathf.Sin(turn * 0.5f);
                if (sine > 0.0001f)
                {
                    float localRadius = Mathf.Min(
                        previousLength,
                        nextLength) / (2f * sine);
                    float turnDirection = Vector3.Cross(
                        previousDirection,
                        nextDirection).y;
                    if (turnDirection > 0.00001f)
                    {
                        safeRightHalfWidths[index] = Mathf.Min(
                            rightHalfWidth,
                            localRadius * RiverCurvatureSafety);
                    }
                    else if (turnDirection < -0.00001f)
                    {
                        safeLeftHalfWidths[index] = Mathf.Min(
                            leftHalfWidth,
                            localRadius * RiverCurvatureSafety);
                    }
                }
            }
        }

        if (duplicateClosure)
        {
            sides[count - 1] = sides[0];
            safeLeftHalfWidths[count - 1] = safeLeftHalfWidths[0];
            safeRightHalfWidths[count - 1] = safeRightHalfWidths[0];
        }

        SmoothRiverBankWidths(
            centers,
            safeLeftHalfWidths,
            uniqueCount,
            closed);
        SmoothRiverBankWidths(
            centers,
            safeRightHalfWidths,
            uniqueCount,
            closed);

        PreventRiverQuadFolding(
            centers,
            sides,
            safeLeftHalfWidths,
            safeRightHalfWidths,
            count,
            duplicateClosure);

        SmoothRiverBankWidths(
            centers,
            safeLeftHalfWidths,
            uniqueCount,
            closed);
        SmoothRiverBankWidths(
            centers,
            safeRightHalfWidths,
            uniqueCount,
            closed);

        if (duplicateClosure)
        {
            sides[count - 1] = sides[0];
            safeLeftHalfWidths[count - 1] = safeLeftHalfWidths[0];
            safeRightHalfWidths[count - 1] = safeRightHalfWidths[0];
        }
    }

    private static void SmoothRiverBankWidths(
        Vector3[] centers,
        float[] widths,
        int count,
        bool closed)
    {
        if (count < 2)
            return;

        int passCount = closed ? 4 : 2;
        for (int pass = 0; pass < passCount; pass++)
        {
            for (int index = 1; index < count; index++)
            {
                float maxChange = FlatDistance(
                    centers[index - 1],
                    centers[index]) * RiverBankTaperPerMeter;
                widths[index] = Mathf.Min(
                    widths[index],
                    widths[index - 1] + maxChange);
            }

            for (int index = count - 2; index >= 0; index--)
            {
                float maxChange = FlatDistance(
                    centers[index],
                    centers[index + 1]) * RiverBankTaperPerMeter;
                widths[index] = Mathf.Min(
                    widths[index],
                    widths[index + 1] + maxChange);
            }

            if (!closed)
                continue;

            float closureChange = FlatDistance(
                centers[count - 1],
                centers[0]) * RiverBankTaperPerMeter;
            widths[0] = Mathf.Min(
                widths[0],
                widths[count - 1] + closureChange);
            widths[count - 1] = Mathf.Min(
                widths[count - 1],
                widths[0] + closureChange);
        }
    }

    private static void PreventRiverQuadFolding(
        Vector3[] centers,
        Vector3[] sides,
        float[] leftHalfWidths,
        float[] rightHalfWidths,
        int count,
        bool duplicateClosure)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            for (int segment = 0; segment < count - 1; segment++)
            {
                if (IsRiverQuadValid(
                    centers,
                    sides,
                    leftHalfWidths,
                    rightHalfWidths,
                    segment,
                    1f,
                    1f))
                {
                    continue;
                }

                float sideTurn = Vector3.Cross(
                    sides[segment],
                    sides[segment + 1]).y;
                bool shrinkRight = sideTurn >= 0f;
                float safeScale = 0f;
                float unsafeScale = 1f;
                for (int iteration = 0; iteration < 12; iteration++)
                {
                    float scale = (safeScale + unsafeScale) * 0.5f;
                    if (IsRiverQuadValid(
                        centers,
                        sides,
                        leftHalfWidths,
                        rightHalfWidths,
                        segment,
                        shrinkRight ? 1f : scale,
                        shrinkRight ? scale : 1f))
                    {
                        safeScale = scale;
                    }
                    else
                    {
                        unsafeScale = scale;
                    }
                }

                safeScale *= 0.98f;
                float[] innerWidths = shrinkRight
                    ? rightHalfWidths
                    : leftHalfWidths;
                innerWidths[segment] = Mathf.Max(
                    MinimumRiverHalfWidth,
                    innerWidths[segment] * safeScale);
                innerWidths[segment + 1] = Mathf.Max(
                    MinimumRiverHalfWidth,
                    innerWidths[segment + 1] * safeScale);
                if (duplicateClosure)
                {
                    float closureWidth = Mathf.Min(
                        innerWidths[0],
                        innerWidths[count - 1]);
                    innerWidths[0] = closureWidth;
                    innerWidths[count - 1] = closureWidth;
                }
                changed = true;
            }

            if (!changed)
                break;
        }
    }

    private static bool IsRiverQuadValid(
        Vector3[] centers,
        Vector3[] sides,
        float[] leftHalfWidths,
        float[] rightHalfWidths,
        int segment,
        float leftScale,
        float rightScale)
    {
        float leftStartWidth = leftHalfWidths[segment] * leftScale;
        float leftEndWidth = leftHalfWidths[segment + 1] * leftScale;
        float rightStartWidth = rightHalfWidths[segment] * rightScale;
        float rightEndWidth = rightHalfWidths[segment + 1] * rightScale;
        Vector3 leftStart = centers[segment]
            - sides[segment] * leftStartWidth;
        Vector3 rightStart = centers[segment]
            + sides[segment] * rightStartWidth;
        Vector3 leftEnd =
            centers[segment + 1] - sides[segment + 1] * leftEndWidth;
        Vector3 rightEnd =
            centers[segment + 1] + sides[segment + 1] * rightEndWidth;

        const float minimumArea = 0.000001f;
        float firstArea = Vector3.Cross(
            leftEnd - leftStart,
            rightStart - leftStart).y;
        float secondArea = Vector3.Cross(
            leftEnd - rightStart,
            rightEnd - rightStart).y;
        return firstArea > minimumArea && secondArea > minimumArea;
    }

    private static float FlatDistance(Vector3 first, Vector3 second)
    {
        Vector3 delta = first - second;
        delta.y = 0f;
        return delta.magnitude;
    }

    private static List<MountainSourceEntry> GetValidPalette(
        PathSpline builder)
    {
        List<MountainSourceEntry> result = new List<MountainSourceEntry>();
        IList<MountainSourceEntry> sources = builder.mountainDecalSet
            && builder.mountainDecalSet.sources != null
            ? builder.mountainDecalSet.sources
            : builder.mountainSources;
        if (sources == null)
            return result;

        for (int i = 0; i < sources.Count; i++)
        {
            MountainSourceEntry entry = sources[i];
            if (IsValidMountainSource(entry))
                result.Add(entry);
        }

        // An empty shared set does not destroy the path's local fallback data.
        if (result.Count == 0
            && builder.mountainDecalSet
            && builder.mountainSources != null)
        {
            for (int i = 0; i < builder.mountainSources.Count; i++)
            {
                MountainSourceEntry entry = builder.mountainSources[i];
                if (IsValidMountainSource(entry))
                    result.Add(entry);
            }
        }
        return result;
    }

    private static bool IsValidMountainSource(MountainSourceEntry entry)
    {
        if (entry == null || entry.weight <= 0f)
            return false;
        switch (entry.sourceType)
        {
            case MountainSourceType.SpriteCard:
                return entry.sprite;
            case MountainSourceType.TextureCard:
                return entry.texture;
            default:
                return entry.prefab;
        }
    }

    private static List<PathEdgePrefabEntry> GetValidEdgePalette(
        PathSpline builder)
    {
        List<PathEdgePrefabEntry> result =
            new List<PathEdgePrefabEntry>();
        if (builder.edgePrefabs == null)
            return result;

        for (int i = 0; i < builder.edgePrefabs.Count; i++)
        {
            PathEdgePrefabEntry entry = builder.edgePrefabs[i];
            if (entry != null && entry.prefab && entry.weight > 0f)
                result.Add(entry);
        }
        return result;
    }

    private static MountainSourceEntry ChooseWeighted(
        System.Random random,
        IList<MountainSourceEntry> palette,
        float totalWeight)
    {
        float value = (float)random.NextDouble() * totalWeight;
        for (int i = 0; i < palette.Count; i++)
        {
            value -= Mathf.Max(0f, palette[i].weight);
            if (value <= 0f)
                return palette[i];
        }
        return palette[palette.Count - 1];
    }

    private static PathEdgePrefabEntry ChooseWeightedEdge(
        System.Random random,
        IList<PathEdgePrefabEntry> palette,
        float totalWeight)
    {
        float value = (float)random.NextDouble() * totalWeight;
        for (int i = 0; i < palette.Count; i++)
        {
            value -= Mathf.Max(0f, palette[i].weight);
            if (value <= 0f)
                return palette[i];
        }
        return palette[palette.Count - 1];
    }

    private static float NextRange(System.Random random, Vector2 range)
    {
        return NextRange(
            random,
            Mathf.Min(range.x, range.y),
            Mathf.Max(range.x, range.y));
    }

    private static float NextRange(
        System.Random random,
        float minimum,
        float maximum)
    {
        return Mathf.Lerp(minimum, maximum, (float)random.NextDouble());
    }

    private static Vector3 FlattenDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.000001f)
            direction = Vector3.forward;
        return direction.normalized;
    }

    private static bool TryProjectToSurface(
        PathSpline builder,
        ref Vector3 position)
    {

        Vector3 origin = position + Vector3.up * builder.rayHeight;
        if (!Physics.Raycast(
            origin,
            Vector3.down,
            out RaycastHit hit,
            builder.rayHeight * 2f,
            builder.surfaceMask,
            QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        position.y = hit.point.y + builder.surfaceOffset;
        return true;
    }

    private static GameObject InstantiateSmart(GameObject source)
    {
        if (!source)
            return null;

        if (PrefabUtility.IsPartOfPrefabAsset(source)
            || PrefabUtility.GetPrefabAssetType(source)
                != PrefabAssetType.NotAPrefab)
        {
            return PrefabUtility.InstantiatePrefab(source) as GameObject;
        }
        return Object.Instantiate(source);
    }

    private static void SetHideFlagsRecursively(Transform root, HideFlags flags)
    {
        Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < transforms.Length; i++)
            transforms[i].gameObject.hideFlags = flags;
    }

    private static void SetMountainOutputHideFlags(Transform root)
    {
        root.gameObject.hideFlags = HideFlags.None;
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            if (IsGeneratedMountainOutputMesh(filters[i].sharedMesh))
                filters[i].gameObject.hideFlags = HideFlags.None;
        }
    }

    internal static bool TryNormalizeAssetFolder(
        string folder,
        out string normalized,
        out string error)
    {
        normalized = string.IsNullOrWhiteSpace(folder)
            ? string.Empty
            : folder.Trim().Replace('\\', '/');
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");
        normalized = normalized.TrimEnd('/');

        if (string.IsNullOrEmpty(normalized))
        {
            error = "路径不能为空。";
            return false;
        }
        if (!string.Equals(
                normalized,
                "Assets",
                StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(
                "Assets/",
                StringComparison.OrdinalIgnoreCase))
        {
            error = "路径必须位于当前项目的 Assets 文件夹内。";
            return false;
        }
        if (normalized.Length == "Assets".Length)
            normalized = "Assets";
        else
            normalized = "Assets" + normalized.Substring("Assets".Length);

        string[] parts = normalized.Split('/');
        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (string.IsNullOrWhiteSpace(part)
                || part == "."
                || part == ".."
                || part.IndexOfAny(invalid) >= 0)
            {
                error = $"文件夹名称“{part}”无效。";
                return false;
            }
        }

        try
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            string projectRoot = Path.GetFullPath(
                Path.Combine(assetsRoot, ".."));
            string absoluteFolder = Path.GetFullPath(
                Path.Combine(projectRoot, normalized))
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            string assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(
                    absoluteFolder,
                    assetsRoot,
                    StringComparison.OrdinalIgnoreCase)
                && !absoluteFolder.StartsWith(
                    assetsPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "路径不能离开当前项目的 Assets 文件夹。";
                return false;
            }
        }
        catch (Exception exception)
        {
            error = $"路径格式无效：{exception.Message}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    internal static bool TryEnsureAssetFolder(
        string folder,
        out string normalized,
        out string error)
    {
        if (!TryNormalizeAssetFolder(folder, out normalized, out error))
            return false;

        try
        {
            EnsureAssetFolder(normalized);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }

        if (AssetDatabase.IsValidFolder(normalized))
            return true;
        error = $"无法创建文件夹：{normalized}";
        return false;
    }

    private static void EnsureAssetFolder(string folder)
    {
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                string guid = AssetDatabase.CreateFolder(current, parts[i]);
                if (string.IsNullOrEmpty(guid)
                    || !AssetDatabase.IsValidFolder(next))
                {
                    throw new IOException($"无法创建文件夹：{next}");
                }
            }
            current = next;
        }
    }

    private static string MakeSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "PathBuilder";

        char[] invalid = Path.GetInvalidFileNameChars();
        for (int i = 0; i < invalid.Length; i++)
            value = value.Replace(invalid[i], '_');
        return value;
    }
}
