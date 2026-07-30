using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using ZXXTools.PathBuilder;
using Object = UnityEngine.Object;

internal static class PathBuilderGenerator
{
    private const string MountainRootName = "[Live Preview] Mountains";
    private const string RiverRootName = "[Live Preview] River";
    private const string MeshOutputFolder = "Assets/Generated/PathBuilderMeshes";

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

        bool built = builder.mode == PathBuildMode.Mountain
            ? BuildMountains(builder, root.transform, arcTable, logWarnings)
            : BuildRiver(builder, root, arcTable, logWarnings);

        root.hideFlags = HideFlags.DontSaveInBuild;
        EditorUtility.SetDirty(root);
        return built;
    }

    public static void ClearGenerated(PathSpline builder)
    {
        if (!builder)
            return;

        Transform root = builder.GeneratedRoot;
        if (root)
        {
            MeshFilter meshFilter = root.GetComponent<MeshFilter>();
            if (meshFilter && meshFilter.sharedMesh
                && !AssetDatabase.Contains(meshFilter.sharedMesh))
            {
                Object.DestroyImmediate(meshFilter.sharedMesh);
            }
            Object.DestroyImmediate(root.gameObject);
        }

        builder.GeneratedRoot = null;
        EditorUtility.SetDirty(builder);
    }

    public static bool Bake(PathSpline builder)
    {
        if (!builder || !builder.Anchor)
            return false;

        if (!builder.GeneratedRoot && !Rebuild(builder))
            return false;

        Transform root = builder.GeneratedRoot;
        if (!root)
            return false;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Bake Path Builder");
        Undo.RecordObject(builder, "Bake Path Builder");
        Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Bake Path Builder");

        if (builder.mode == PathBuildMode.River)
        {
            MeshFilter meshFilter = root.GetComponent<MeshFilter>();
            if (!meshFilter || !meshFilter.sharedMesh)
                return false;

            EnsureAssetFolder(MeshOutputFolder);
            string safeName = MakeSafeFileName(builder.Anchor.gameObject.name);
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{MeshOutputFolder}/{safeName}_River.asset");
            Mesh meshAsset = Object.Instantiate(meshFilter.sharedMesh);
            meshAsset.name = Path.GetFileNameWithoutExtension(assetPath);
            meshAsset.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(meshAsset, assetPath);
            meshFilter.sharedMesh = meshAsset;

            MeshCollider meshCollider = root.GetComponent<MeshCollider>();
            if (meshCollider)
                meshCollider.sharedMesh = meshAsset;
            EditorGUIUtility.PingObject(meshAsset);
        }

        SetHideFlagsRecursively(root, HideFlags.None);
        Transform anchor = builder.Anchor;
        root.name = builder.mode == PathBuildMode.Mountain
            ? $"{anchor.gameObject.name}_Mountains_Baked"
            : $"{anchor.gameObject.name}_River_Baked";
        Undo.SetTransformParent(root, anchor.parent, "Bake Path Builder");
        builder.GeneratedRoot = null;
        builder.Anchor = null;
        builder.autoRebuild = false;
        EditorUtility.SetDirty(builder);
        EditorUtility.SetDirty(root.gameObject);
        AssetDatabase.SaveAssets();
        Undo.CollapseUndoOperations(undoGroup);
        Undo.DestroyObjectImmediate(anchor.gameObject);
        Selection.activeGameObject = root.gameObject;
        return true;
    }

    private static bool BuildMountains(
        PathSpline builder,
        Transform root,
        IList<PathSample> arcTable,
        bool logWarnings)
    {
        List<MountainPrefabEntry> palette = GetValidPalette(builder);
        if (palette.Count == 0)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] 山脉笔刷没有可用 Prefab。", builder);
            return false;
        }

        float totalWeight = 0f;
        for (int i = 0; i < palette.Count; i++)
            totalWeight += Mathf.Max(0f, palette[i].weight);
        if (totalWeight <= 0f)
        {
            if (logWarnings)
                Debug.LogWarning("[Path Builder] 山体 Prefab 权重之和必须大于 0。", builder);
            return false;
        }

        System.Random random = new System.Random(builder.randomSeed);
        float totalLength = arcTable[arcTable.Count - 1].distance;
        float firstSpacing = NextRange(random, builder.spacingRange);
        float distance = builder.closed ? firstSpacing * 0.5f : 0f;
        int createdCount = 0;
        int guard = 0;

        while (distance < totalLength + 0.0001f && guard++ < 100000)
        {
            if (random.NextDouble() <= builder.density
                && PathSplineUtility.TryEvaluateAtDistance(
                    arcTable, distance, out PathSample sample))
            {
                MountainPrefabEntry entry =
                    ChooseWeighted(random, palette, totalWeight);
                if (entry != null && entry.prefab)
                {
                    Vector3 forward = FlattenDirection(sample.tangent);
                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
                    float lateral = NextRange(
                        random,
                        -builder.brushWidth * 0.5f,
                        builder.brushWidth * 0.5f);
                    Vector3 position = sample.position + right * lateral;
                    ProjectToSurface(builder, ref position);

                    float yaw = NextRange(random, builder.yawRange);
                    Quaternion rotation = builder.alignToSpline
                        ? Quaternion.LookRotation(forward, Vector3.up)
                            * Quaternion.Euler(0f, yaw, 0f)
                        : Quaternion.Euler(0f, yaw, 0f);

                    GameObject instance = InstantiateSmart(entry.prefab);
                    if (instance)
                    {
                        instance.transform.SetParent(root, true);
                        instance.transform.SetPositionAndRotation(
                            position,
                            rotation * entry.prefab.transform.rotation);

                        float horizontalScale =
                            NextRange(random, builder.horizontalScaleRange);
                        float verticalScale =
                            NextRange(random, builder.verticalScaleRange);
                        Vector3 baseScale = entry.prefab.transform.localScale;
                        instance.transform.localScale = new Vector3(
                            baseScale.x * horizontalScale,
                            baseScale.y * verticalScale,
                            baseScale.z * horizontalScale);
                        instance.name = $"{entry.prefab.name}_{createdCount:D3}";
                        createdCount++;
                    }
                }
            }

            distance += NextRange(random, builder.spacingRange);
        }

        return createdCount > 0;
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

        Vector3 localUp = root.transform.InverseTransformDirection(Vector3.up).normalized;
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
            if (!ProjectToSurface(builder, ref center))
                center += Vector3.up * builder.surfaceOffset;

            Vector3 forward = FlattenDirection(sample.tangent);
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            float halfWidth = Mathf.Max(0.005f, sample.width * 0.5f);
            Vector3 leftWorld = center - right * halfWidth;
            Vector3 rightWorld = center + right * halfWidth;
            int vertexIndex = section * 2;
            vertices[vertexIndex] = root.transform.InverseTransformPoint(leftWorld);
            vertices[vertexIndex + 1] = root.transform.InverseTransformPoint(rightWorld);
            normals[vertexIndex] = localUp;
            normals[vertexIndex + 1] = localUp;

            Vector3 localRight =
                root.transform.InverseTransformDirection(right).normalized;
            Vector4 meshTangent =
                new Vector4(localRight.x, localRight.y, localRight.z, -1f);
            tangents[vertexIndex] = meshTangent;
            tangents[vertexIndex + 1] = meshTangent;

            // UV0 is continuous over the whole river. Mesh sections never
            // restart the 0-1 range; values above 1 repeat with Repeat textures.
            float v = distance / builder.riverUvRepeatLength;
            uv[vertexIndex] = new Vector2(0f, v);
            uv[vertexIndex + 1] = new Vector2(1f, v);
            uv2[vertexIndex] = new Vector2(0f, distance);
            uv2[vertexIndex + 1] = new Vector2(1f, distance);
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
            name = $"{builder.Anchor.gameObject.name}_RiverPreview",
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

        if (builder.generateCollider)
        {
            MeshCollider collider = root.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
        }

        return true;
    }

    private static List<MountainPrefabEntry> GetValidPalette(
        PathSpline builder)
    {
        List<MountainPrefabEntry> result = new List<MountainPrefabEntry>();
        if (builder.mountainPrefabs == null)
            return result;

        for (int i = 0; i < builder.mountainPrefabs.Count; i++)
        {
            MountainPrefabEntry entry = builder.mountainPrefabs[i];
            if (entry != null && entry.prefab && entry.weight > 0f)
                result.Add(entry);
        }
        return result;
    }

    private static MountainPrefabEntry ChooseWeighted(
        System.Random random,
        IList<MountainPrefabEntry> palette,
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

    private static bool ProjectToSurface(
        PathSpline builder,
        ref Vector3 position)
    {
        if (!builder.projectToSurface)
            return false;

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

    private static void EnsureAssetFolder(string folder)
    {
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
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
