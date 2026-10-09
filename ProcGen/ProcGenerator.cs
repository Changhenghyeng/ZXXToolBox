using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace ProcGen
{
    public static class ProcGenerator
    {
        private const string ROOT_PREFIX = "[ProcGen Root]";
        private const string GROUP_PREVIEW_PREFIX = "[ProcGen Group Preview] ";

        private static int GetNextRootIndex()
        {
            int maxIdx = 0;
            var all = GameObject.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || t.parent != null) continue;
                if (t.name.StartsWith(ROOT_PREFIX + " #"))
                {
                    string numStr = t.name.Substring((ROOT_PREFIX + " #").Length);
                    if (int.TryParse(numStr, out int idx) && idx > maxIdx)
                        maxIdx = idx;
                }
            }
            return maxIdx + 1;
        }

        public static void Generate(ProcGenSettings settings)
        {
            if (settings.target == null)
            {
                Debug.LogError("[ProcGen] 未指定 target");
                return;
            }
            if (settings.slots == null || settings.slots.Count == 0)
            {
                Debug.LogWarning("[ProcGen] 没有任何插槽");
                return;
            }

            // 清除之前的调试信息
#if UNITY_EDITOR
            if (settings.showDebugGizmos)
            {
                ProcGenDebugManager.Instance.ClearAll();
                ProcGenDebugManager.Instance.showCandidatePoints = settings.showCandidatePoints;
                ProcGenDebugManager.Instance.showCollisionBoxes = settings.showCollisionBoxes;
                ProcGenDebugManager.Instance.showAvoidanceBounds = settings.showAvoidanceBounds;
                ProcGenDebugManager.Instance.showDestroyedObjects = settings.showDestroyedObjects;
            }
#endif

            // 1. 拿到 target 上的 mesh
            MeshFilter mf = settings.target.GetComponentInChildren<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Debug.LogError("[ProcGen] target 上找不到 MeshFilter / Mesh");
                return;
            }

            // 2. 生成候选点 (世界空间)
            List<Vector3> candidates = BuildCandidates(settings, mf);
            if (candidates.Count == 0)
            {
                Debug.LogWarning("[ProcGen] 没有生成任何候选点 (检查 mesh 是否朝上)");
                return;
            }

            // ========== 调试：添加所有候选点 ==========
#if UNITY_EDITOR
            if (settings.showDebugGizmos && settings.showCandidatePoints)
            {
                Debug.Log($"[ProcGen] 共生成 {candidates.Count} 个候选点");
                foreach (Vector3 point in candidates)
                {
                    ProcGenDebugManager.Instance.AddCandidatePoint(point, settings.candidatePointColor);
                }
            }
#endif
            // ======================================

            // 3. 创建父物体（每次生成独立 Root）
            int rootIndex = GetNextRootIndex();
            GameObject root = new GameObject($"{ROOT_PREFIX} #{rootIndex}");
            Undo.RegisterCreatedObjectUndo(root, "ProcGen Generate");

            // 4. 按优先级降序排序插槽（高优先级先生成）
            var sortedSlots = new List<ProcGenSlot>(settings.slots);
            sortedSlots.Sort((a, b) => b.priority.CompareTo(a.priority));

            // 5. 初始化随机和空间哈希
            var rng = new System.Random(settings.seed);
            var spatial = new SpatialHash(Mathf.Max(settings.cellSize, 1f));

            // 5.5 把场景中已有的生成物体注册到空间哈希，避免二次生成穿模
            PrePopulateSpatialHash(spatial);

            // 5.6 注册用户指定的场景避让物体；这些物体始终受保护
            int avoidanceCount = PrePopulateAvoidanceObjects(spatial, settings);

            // 6. 逐插槽生成
            int totalPlaced = 0;
            for (int i = 0; i < sortedSlots.Count; i++)
            {
                var slot = sortedSlots[i];
                if (!slot.enabled) continue;
                if (slot.count <= 0) continue;
                if (!HasAnyValidPrefab(slot)) continue;

                totalPlaced += PlaceSlot(slot, i, candidates, spatial, rng, root.transform, settings);
            }

            Debug.Log($"[ProcGen] 第 {rootIndex} 次生成完成，新增 {totalPlaced} 个物体，已避让 {avoidanceCount} 个场景物体");
        }

        public static void PreviewGroupAtOrigin(ProcGenSlot slot)
        {
            if (slot == null || !HasAnyValidPrefab(slot))
            {
                Debug.LogWarning("[ProcGen] 当前 Slot 没有可用的 Prefab，无法制作组群");
                return;
            }

            CancelGroupPreview();
            string parentName = string.IsNullOrWhiteSpace(slot.groupParentName)
                ? slot.name
                : slot.groupParentName.Trim();
            GameObject groupRoot = CreateGroupAtOrigin(slot, GROUP_PREVIEW_PREFIX + parentName, false);
            if (groupRoot == null) return;

            SetHideFlagsRecursive(groupRoot, HideFlags.DontSaveInEditor);
            Selection.activeGameObject = groupRoot;
            EditorGUIUtility.PingObject(groupRoot);
            Debug.Log($"[ProcGen] 已在世界原点预览组群 \"{slot.name}\"；确认后才会正式保留", groupRoot);
        }

        public static void ConfirmGroupPreview()
        {
            GameObject preview = FindGroupPreview();
            if (preview == null)
            {
                Debug.LogWarning("[ProcGen] 当前没有组群预览，请先点击 Preview Group");
                return;
            }

            string requestedName = preview.name.Substring(GROUP_PREVIEW_PREFIX.Length);
            preview.name = GetUniqueGroupName(requestedName);
            SetHideFlagsRecursive(preview, HideFlags.None);
            Undo.RegisterCreatedObjectUndo(preview, "ProcGen Confirm Group");
            Selection.activeGameObject = preview;
            EditorGUIUtility.PingObject(preview);
            Debug.Log($"[ProcGen] 已确认生成组群 \"{preview.name}\"，父节点位于世界原点", preview);
        }

        public static void CancelGroupPreview()
        {
            GameObject preview = FindGroupPreview();
            if (preview != null)
                Object.DestroyImmediate(preview);
        }

        private static GameObject CreateGroupAtOrigin(ProcGenSlot slot, string rootName, bool registerUndo)
        {
            GameObject groupRoot = new GameObject(rootName);
            groupRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            groupRoot.transform.localScale = Vector3.one;
            if (registerUndo)
                Undo.RegisterCreatedObjectUndo(groupRoot, "ProcGen Create Group");

            int requestedCount = Mathf.Max(1, slot.groupObjectCount);
            float radius = Mathf.Max(0f, slot.groupRadius);
            float minSpacing = Mathf.Max(0f, slot.groupMinSpacing);
            float scaleMin = Mathf.Max(0.0001f, Mathf.Min(slot.groupScaleMin, slot.groupScaleMax));
            float scaleMax = Mathf.Max(scaleMin, Mathf.Max(slot.groupScaleMin, slot.groupScaleMax));
            float rotationMin = Mathf.Min(slot.groupRotationMin, slot.groupRotationMax);
            float rotationMax = Mathf.Max(slot.groupRotationMin, slot.groupRotationMax);
            var rng = new System.Random(slot.groupSeed);
            var positions = new List<Vector3>();

            for (int i = 0; i < requestedCount; i++)
            {
                Vector3 localPosition = Vector3.zero;
                bool positionFound = i == 0;
                int maxPositionAttempts = 40;

                for (int attempt = 0; !positionFound && attempt < maxPositionAttempts; attempt++)
                {
                    float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float distance = Mathf.Sqrt((float)rng.NextDouble()) * radius;
                    Vector3 candidate = new Vector3(Mathf.Cos(angle) * distance, 0f, Mathf.Sin(angle) * distance);

                    positionFound = true;
                    if (minSpacing > 0f)
                    {
                        float minSpacingSqr = minSpacing * minSpacing;
                        foreach (var existing in positions)
                        {
                            if ((existing - candidate).sqrMagnitude < minSpacingSqr)
                            {
                                positionFound = false;
                                break;
                            }
                        }
                    }

                    if (positionFound)
                        localPosition = candidate;
                }

                if (!positionFound) continue;

                GameObject prefab = PickPrefab(slot, rng);
                if (prefab == null) continue;

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (instance == null) instance = Object.Instantiate(prefab);
                if (registerUndo)
                    Undo.RegisterCreatedObjectUndo(instance, "ProcGen Create Group Item");

                float yRotation = Mathf.Lerp(rotationMin, rotationMax, (float)rng.NextDouble());

                instance.transform.SetParent(groupRoot.transform, false);
                instance.transform.localPosition = localPosition;
                instance.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f) * prefab.transform.localRotation;
                Vector3 scaleMultiplier;
                if (slot.groupSeparateAxisScale)
                {
                    float xMin = Mathf.Max(0.0001f, Mathf.Min(slot.groupScaleXMin, slot.groupScaleXMax));
                    float xMax = Mathf.Max(xMin, Mathf.Max(slot.groupScaleXMin, slot.groupScaleXMax));
                    float yMin = Mathf.Max(0.0001f, Mathf.Min(slot.groupScaleYMin, slot.groupScaleYMax));
                    float yMax = Mathf.Max(yMin, Mathf.Max(slot.groupScaleYMin, slot.groupScaleYMax));
                    float zMin = Mathf.Max(0.0001f, Mathf.Min(slot.groupScaleZMin, slot.groupScaleZMax));
                    float zMax = Mathf.Max(zMin, Mathf.Max(slot.groupScaleZMin, slot.groupScaleZMax));
                    scaleMultiplier = new Vector3(
                        Mathf.Lerp(xMin, xMax, (float)rng.NextDouble()),
                        Mathf.Lerp(yMin, yMax, (float)rng.NextDouble()),
                        Mathf.Lerp(zMin, zMax, (float)rng.NextDouble()));
                }
                else
                {
                    float uniformScale = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
                    scaleMultiplier = Vector3.one * uniformScale;
                }

                Vector3 localScale = Vector3.Scale(prefab.transform.localScale, scaleMultiplier);
                if (slot.groupFixedFlipX || (slot.groupRandomFlipX && rng.NextDouble() < 0.5)) localScale.x *= -1f;
                if (slot.groupFixedFlipY || (slot.groupRandomFlipY && rng.NextDouble() < 0.5)) localScale.y *= -1f;
                if (slot.groupFixedFlipZ || (slot.groupRandomFlipZ && rng.NextDouble() < 0.5)) localScale.z *= -1f;
                instance.transform.localScale = localScale;

                if (slot.overrideMaterial != null)
                    ApplyOverrideMaterial(instance, slot.overrideMaterial);

                positions.Add(localPosition);
            }

            if (positions.Count < requestedCount)
            {
                Debug.LogWarning($"[ProcGen] 组群 \"{groupRoot.name}\" 生成了 {positions.Count}/{requestedCount} 个物体；" +
                    "当前半径无法满足最小间距，请增大 Radius 或减小 Min Spacing", groupRoot);
            }
            return groupRoot;
        }

        private static GameObject FindGroupPreview()
        {
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform == null || transform.parent != null || !transform.gameObject.scene.IsValid()) continue;
                if (transform.name.StartsWith(GROUP_PREVIEW_PREFIX))
                    return transform.gameObject;
            }
            return null;
        }

        private static string GetUniqueGroupName(string requestedName)
        {
            string baseName = string.IsNullOrWhiteSpace(requestedName) ? "ProcGen Group" : requestedName.Trim();
            var rootNames = new HashSet<string>();
            foreach (var transform in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (transform == null || transform.parent != null || !transform.gameObject.scene.IsValid()) continue;
                if (!transform.name.StartsWith(GROUP_PREVIEW_PREFIX))
                    rootNames.Add(transform.name);
            }

            if (!rootNames.Contains(baseName)) return baseName;

            int index = 2;
            while (rootNames.Contains($"{baseName} #{index}")) index++;
            return $"{baseName} #{index}";
        }

        private static void SetHideFlagsRecursive(GameObject root, HideFlags flags)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = flags;
        }

        // ---- 预填充 ----

        private static void PrePopulateSpatialHash(SpatialHash spatial)
        {
            var all = GameObject.FindObjectsOfType<Transform>();
            foreach (var t in all)
            {
                if (t == null || !t.name.StartsWith(ROOT_PREFIX + " #") || t.parent != null) continue;

                foreach (Transform child in t)
                {
                    var renderers = child.GetComponentsInChildren<Renderer>();
                    if (renderers.Length == 0) continue;

                    Bounds bounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        bounds.Encapsulate(renderers[i].bounds);

                    spatial.Add(bounds.center, bounds.extents,
                        int.MaxValue, child.gameObject, -1, false);
                }
            }
        }

        private static int PrePopulateAvoidanceObjects(SpatialHash spatial, ProcGenSettings settings)
        {
            if (settings.avoidanceObjects == null || settings.avoidanceObjects.Count == 0)
                return 0;

            float padding = Mathf.Max(0f, settings.avoidancePadding);
            int registeredCount = 0;
            var registeredRoots = new HashSet<GameObject>();
            var registeredRenderers = new HashSet<Renderer>();
            var registeredColliders = new HashSet<Collider>();

            foreach (var go in settings.avoidanceObjects)
            {
                if (go == null || !registeredRoots.Add(go)) continue;

                if (!go.scene.IsValid())
                {
                    Debug.LogWarning($"[ProcGen] 避让列表中的 \"{go.name}\" 不是场景物体，已跳过；请从 Hierarchy 拖入", go);
                    continue;
                }

                bool foundBounds = false;

                // 每个子 Renderer 单独注册，避免分散子物体被合并成一个巨大 AABB。
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer == null) continue;
                    foundBounds = true;
                    if (!registeredRenderers.Add(renderer)) continue;

                    AddRendererAvoidance(spatial, renderer, padding);
                }

                // 整个层级没有 Renderer 时，才按每个子 Collider 分别注册。
                if (!foundBounds)
                {
                    var colliders = go.GetComponentsInChildren<Collider>(true);
                    foreach (var collider in colliders)
                    {
                        if (collider == null) continue;
                        foundBounds = true;
                        if (!registeredColliders.Add(collider)) continue;

                        Bounds bounds = collider.bounds;
                        spatial.Add(bounds.center, bounds.extents + Vector3.one * padding,
                            int.MaxValue, collider.gameObject, -1, false);
                    }
                }

                if (foundBounds)
                    registeredCount++;
                else
                    Debug.LogWarning($"[ProcGen] 避让物体 \"{go.name}\" 没有 Renderer 或 Collider，已跳过", go);
            }

            return registeredCount;
        }

        private static void AddRendererAvoidance(SpatialHash spatial, Renderer renderer, float padding)
        {
            // 河流、道路等可能是一整条弯曲网格。如果直接使用 Renderer.bounds，
            // 会把弯曲网格外接矩形里的大片空地也挡住，因此 MeshRenderer 按三角面注册。
            var meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;

            if (mesh != null)
            {
                try
                {
                    Vector3[] vertices = mesh.vertices;
                    int[] triangles = mesh.triangles;
                    Transform meshTransform = meshFilter.transform;

                    if (vertices.Length > 0 && triangles.Length >= 3)
                    {
                        for (int i = 0; i + 2 < triangles.Length; i += 3)
                        {
                            Vector3 a = meshTransform.TransformPoint(vertices[triangles[i]]);
                            Vector3 b = meshTransform.TransformPoint(vertices[triangles[i + 1]]);
                            Vector3 c = meshTransform.TransformPoint(vertices[triangles[i + 2]]);

                            Bounds triangleBounds = new Bounds(a, Vector3.zero);
                            triangleBounds.Encapsulate(b);
                            triangleBounds.Encapsulate(c);

                            spatial.Add(triangleBounds.center,
                                triangleBounds.extents + Vector3.one * padding,
                                int.MaxValue, renderer.gameObject, -1, false);
                        }
                        return;
                    }
                }
                catch (UnityException)
                {
                    // 极少数不可读网格无法取得顶点，回退到 Renderer 包围盒。
                }
            }

            Bounds bounds = renderer.bounds;
            spatial.Add(bounds.center, bounds.extents + Vector3.one * padding,
                int.MaxValue, renderer.gameObject, -1, false);
        }

        // ----------------------------------------------------------------
        private static List<Vector3> BuildCandidates(ProcGenSettings settings, MeshFilter mf)
        {
            var result = new List<Vector3>();
            Transform targetTr = settings.target.transform;
            Mesh mesh = mf.sharedMesh;
            Transform meshTr = mf.transform;

            // mesh 的世界 AABB
            Bounds worldBounds = mf.GetComponent<Renderer>() != null
                ? mf.GetComponent<Renderer>().bounds
                : TransformBounds(mesh.bounds, meshTr);

            // 以 target 原点为锚点，按 cellSize 对齐
            Vector3 origin = targetTr.position;
            float cs = Mathf.Max(0.01f, settings.cellSize);
            int xMin = Mathf.FloorToInt((worldBounds.min.x - origin.x) / cs);
            int xMax = Mathf.CeilToInt((worldBounds.max.x - origin.x) / cs);
            int zMin = Mathf.FloorToInt((worldBounds.min.z - origin.z) / cs);
            int zMax = Mathf.CeilToInt((worldBounds.max.z - origin.z) / cs);

            // 射线从 mesh 顶部往下打。为了不依赖物理碰撞体，
            // 我们手动对 mesh 三角形做相交检测。
            var triData = BakeTriangles(mesh, meshTr, settings.upNormalThreshold);
            if (triData.Count == 0) return result;

            float rayTopY = worldBounds.max.y + 1f;
            float rayBotY = worldBounds.min.y - 1f;

            // jitter 用一个独立的 RNG，避免影响放置随机
            var jitterRng = new System.Random(settings.seed ^ 0x5A5A);
            float maxJitter = cs * 0.5f * settings.jitter;
            float inset = Mathf.Max(0f, settings.boundaryInset);

            for (int xi = xMin; xi <= xMax; xi++)
                for (int zi = zMin; zi <= zMax; zi++)
                {
                    float cx = origin.x + (xi + 0.5f) * cs;
                    float cz = origin.z + (zi + 0.5f) * cs;

                    if (maxJitter > 0f)
                    {
                        cx += (float)(jitterRng.NextDouble() * 2.0 - 1.0) * maxJitter;
                        cz += (float)(jitterRng.NextDouble() * 2.0 - 1.0) * maxJitter;
                    }

                    Vector3 rayOrigin = new Vector3(cx, rayTopY, cz);
                    if (!RaycastDownAgainstTris(rayOrigin, rayBotY, triData, out Vector3 hit))
                        continue;

                    // 边界内缩检测：四方向偏移 inset 距离的点也必须命中
                    if (inset > 0.01f)
                    {
                        if (!RaycastDownAgainstTris(new Vector3(cx + inset, rayTopY, cz), rayBotY, triData, out _)) continue;
                        if (!RaycastDownAgainstTris(new Vector3(cx - inset, rayTopY, cz), rayBotY, triData, out _)) continue;
                        if (!RaycastDownAgainstTris(new Vector3(cx, rayTopY, cz + inset), rayBotY, triData, out _)) continue;
                        if (!RaycastDownAgainstTris(new Vector3(cx, rayTopY, cz - inset), rayBotY, triData, out _)) continue;
                    }

                    result.Add(hit);
                }

            return result;
        }

        private struct Tri
        {
            public Vector3 a, b, c;
            public Vector3 normal;
        }

        private static List<Tri> BakeTriangles(Mesh mesh, Transform tr, float upThreshold)
        {
            var list = new List<Tri>();
            var verts = mesh.vertices;
            var tris = mesh.triangles;

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = tr.TransformPoint(verts[tris[i]]);
                Vector3 b = tr.TransformPoint(verts[tris[i + 1]]);
                Vector3 c = tr.TransformPoint(verts[tris[i + 2]]);
                Vector3 n = Vector3.Cross(b - a, c - a).normalized;

                if (n.y >= upThreshold)
                {
                    list.Add(new Tri { a = a, b = b, c = c, normal = n });
                }
            }

            return list;
        }

        /// <summary>
        /// 从 rayOrigin 沿 -Y 方向找最近的朝上三角形交点
        /// </summary>
        private static bool RaycastDownAgainstTris(Vector3 rayOrigin, float minY, List<Tri> tris, out Vector3 hit)
        {
            hit = default;
            float bestY = float.NegativeInfinity;
            bool found = false;

            foreach (var t in tris)
            {
                // 用 Möller–Trumbore，方向 (0,-1,0)
                if (RayTriIntersect(rayOrigin, new Vector3(0, -1, 0), t.a, t.b, t.c, out float dist))
                {
                    float y = rayOrigin.y - dist;
                    if (y > bestY && y >= minY)
                    {
                        bestY = y;
                        hit = new Vector3(rayOrigin.x, y, rayOrigin.z);
                        found = true;
                    }
                }
            }

            return found;
        }

        private static bool RayTriIntersect(Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            const float EPS = 1e-7f;
            t = 0f;

            Vector3 e1 = b - a;
            Vector3 e2 = c - a;
            Vector3 p = Vector3.Cross(rd, e2);
            float det = Vector3.Dot(e1, p);

            if (det > -EPS && det < EPS) return false;

            float invDet = 1f / det;
            Vector3 tv = ro - a;
            float u = Vector3.Dot(tv, p) * invDet;

            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(tv, e1);
            float v = Vector3.Dot(rd, q) * invDet;

            if (v < 0f || u + v > 1f) return false;

            t = Vector3.Dot(e2, q) * invDet;
            return t > EPS;
        }

        private static Bounds TransformBounds(Bounds local, Transform tr)
        {
            Vector3 c = tr.TransformPoint(local.center);
            Vector3 ext = local.extents;

            Vector3 axisX = tr.TransformVector(new Vector3(ext.x, 0, 0));
            Vector3 axisY = tr.TransformVector(new Vector3(0, ext.y, 0));
            Vector3 axisZ = tr.TransformVector(new Vector3(0, 0, ext.z));

            Vector3 worldExt = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z)
            );

            return new Bounds(c, worldExt * 2f);
        }

        // ----------------------------------------------------------------
        private static int PlaceSlot(ProcGenSlot slot, int slotIndex, List<Vector3> candidates,
            SpatialHash spatial, System.Random rng, Transform parent, ProcGenSettings settings)
        {
            // 规范化缩放范围
            float scaleMin = Mathf.Min(slot.scaleMin, slot.scaleMax);
            float scaleMax = Mathf.Max(slot.scaleMin, slot.scaleMax);
            if (scaleMin <= 0f) scaleMin = 0.0001f;

            int placed = 0;
            int maxAttempts = slot.count * Mathf.Max(1, settings.maxRetriesPerPoint) + 50;

            // 候选点池
            var pool = new List<int>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++) pool.Add(i);

            // 记录此插槽已放置点的位置（用于 best-candidate 选点）
            var placedPositions = new List<Vector3>();

            // 散布偏好:
            //   spreadBias = 0   -> mode = +1 (选最远，最均匀)
            //   spreadBias = 0.5 -> mode =  0 (纯随机)
            //   spreadBias = 1   -> mode = -1 (选最近，最聚集)
            float mode = 1f - settings.spreadBias * 2f;  // [+1, -1]
            float strength = Mathf.Abs(mode);         // [0, 1]

            // K 是每次抽几个候选挑一个。strength 越大 K 越大
            int Kbase = Mathf.Max(1, pool.Count / Mathf.Max(1, slot.count));
            int Kmax = Mathf.Clamp(Kbase * 4, 8, 64);
            int K = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(1f, Kmax, strength)));

            int attempts = 0;
            while (placed < slot.count && pool.Count > 0 && attempts < maxAttempts)
            {
                attempts++;
                int sampleSize = Mathf.Min(K, pool.Count);
                int bestPoolIdx = -1;
                float bestScore = float.NegativeInfinity;

                if (placedPositions.Count == 0)
                {
                    // 第一个直接随机抽一个
                    bestPoolIdx = rng.Next(pool.Count);
                }
                else
                {
                    for (int s = 0; s < sampleSize; s++)
                    {
                        int tryIdx = rng.Next(pool.Count);
                        Vector3 p = candidates[pool[tryIdx]];

                        // 找到 p 距离已放置点的最近距离平方
                        float minD = float.PositiveInfinity;
                        for (int j = 0; j < placedPositions.Count; j++)
                        {
                            float d = (placedPositions[j] - p).sqrMagnitude;
                            if (d < minD) minD = d;
                        }

                        // 评分: mode > 0 选最远 (minD 大者优), mode < 0 选最近 (minD 小者优)
                        float score = minD * mode;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestPoolIdx = tryIdx;
                        }
                    }
                }

                int candIdx = pool[bestPoolIdx];
                pool.RemoveAt(bestPoolIdx);
                Vector3 pos = candidates[candIdx];

                // ========== 核心修复：先计算旋转和缩放，再算碰撞盒 ==========
                // 1. 先随机抽取一个预制体
                GameObject pickedPrefab = PickPrefab(slot, rng);
                if (pickedPrefab == null) break;

                // 2. 随机生成缩放
                float instScale = scaleMin + (float)rng.NextDouble() * (scaleMax - scaleMin);

                // 3. 计算最终旋转（插槽旋转 + 预制体自身旋转）
                Quaternion finalRotation = Quaternion.Euler(0f, slot.yRotation, 0f) * pickedPrefab.transform.rotation;

                // 4. 计算**旋转+缩放后**的实际世界空间AABB碰撞盒
                Bounds worldBounds = GetWorldBoundsForInstance(slot, pickedPrefab, instScale, finalRotation);
                Vector3 actualHalfExtents = worldBounds.extents;

                // 5. 加上最小间距，得到实际避让范围
                Vector3 actualInflated = actualHalfExtents + Vector3.one * (slot.minDistance * 0.5f);

                // 6. 计算碰撞盒中心（含位置偏移 + 旋转后包围盒偏移）
                Vector3 worldOffset = finalRotation * slot.positionOffset;
                Vector3 boxCenter = pos + worldOffset + worldBounds.center;
                Vector3 testCenter = boxCenter + new Vector3(0, slot.minDistance * 0.5f, 0);

                // ========== 调试：绘制当前测试的避让范围 ==========
#if UNITY_EDITOR
                if (settings.showDebugGizmos && settings.showAvoidanceBounds && attempts <= 100)
                {
                    Color transparentColor = new Color(settings.avoidanceColor.r, settings.avoidanceColor.g, settings.avoidanceColor.b, 0.2f);
                    ProcGenDebugManager.Instance.AddAvoidanceBound(testCenter, actualInflated, transparentColor);
                }
#endif
                // ==============================================

                // ========== 重叠检测 + 低优先级清理 ==========
                bool hasOverlap = spatial.Overlaps(testCenter, actualInflated);
                if (hasOverlap)
                {
                    // 查找所有重叠的**可被销毁的**低优先级实例
                    List<GameObject> lowerPriorityInstances = spatial.GetOverlappedLowerPriorityInstances(
                        testCenter, actualInflated, slot.priority);

                    if (lowerPriorityInstances.Count > 0)
                    {
                        int destroyedCount = 0;
                        foreach (var inst in lowerPriorityInstances)
                        {
                            if (inst != null)
                            {
                                // ========== 调试：添加被销毁的物体碰撞盒 ==========
#if UNITY_EDITOR
                                if (settings.showDebugGizmos && settings.showDestroyedObjects)
                                {
                                    if (spatial.TryGetEntry(inst, out SpatialHash.Entry entry))
                                    {
                                        ProcGenDebugManager.Instance.AddDestroyedObject(entry.center, entry.halfExtents, settings.destroyedColor);
                                    }
                                }
#endif
                                // ==============================================

                                // 支持Undo撤销销毁操作
                                Undo.DestroyObjectImmediate(inst);
                                if (spatial.Remove(inst)) destroyedCount++;
                            }
                        }

                        if (destroyedCount > 0)
                        {
                            Debug.Log($"[ProcGen] 插槽 \"{slot.name}\" (优先级 {slot.priority}) 清理了 {destroyedCount} 个可销毁的低优先级重叠物体");
                        }

                        // 清理后重新检测重叠
                        hasOverlap = spatial.Overlaps(testCenter, actualInflated);
                    }

                    // 若仍有重叠（同优先级/更高优先级/不可销毁的低优先级），跳过该点
                    if (hasOverlap) continue;
                }
                // ======================================================

                // 放置实例（传入正确的碰撞盒信息）
                PlaceOneInstance(slot, slotIndex, pos, actualHalfExtents, actualInflated, instScale, pickedPrefab, finalRotation,
                    spatial, placedPositions, parent, boxCenter, settings);
                placed++;
            }

            // ============ Fallback 阶段 1：穷尽剩余候选池找还能放下的位置 ============
            if (placed < slot.count && pool.Count > 0)
            {
                // 按"离已放置点最远"排序，优先尝试空旷区域
                var sortedPool = new List<int>(pool);
                if (placedPositions.Count > 0)
                {
                    sortedPool.Sort((a, b) =>
                    {
                        Vector3 pa = candidates[a];
                        Vector3 pb = candidates[b];
                        float da = float.PositiveInfinity, db = float.PositiveInfinity;

                        for (int j = 0; j < placedPositions.Count; j++)
                        {
                            float ta = (placedPositions[j] - pa).sqrMagnitude;
                            float tb = (placedPositions[j] - pb).sqrMagnitude;
                            if (ta < da) da = ta;
                            if (tb < db) db = tb;
                        }

                        return db.CompareTo(da); // 大的在前
                    });
                }

                foreach (int candIdx in sortedPool)
                {
                    if (placed >= slot.count) break;

                    Vector3 pos = candidates[candIdx];

                    // 同样先计算旋转和缩放，再算碰撞盒
                    GameObject pickedPrefab = PickPrefab(slot, rng);
                    if (pickedPrefab == null) break;

                    float instScale = scaleMin + (float)rng.NextDouble() * (scaleMax - scaleMin);
                    Quaternion finalRotation = Quaternion.Euler(0f, slot.yRotation, 0f) * pickedPrefab.transform.rotation;
                    Bounds worldBounds = GetWorldBoundsForInstance(slot, pickedPrefab, instScale, finalRotation);
                    Vector3 actualHalfExtents = worldBounds.extents;
                    Vector3 actualInflated = actualHalfExtents + Vector3.one * (slot.minDistance * 0.5f);

                    Vector3 worldOffset1 = finalRotation * slot.positionOffset;
                    Vector3 boxCenter = pos + worldOffset1 + worldBounds.center;
                    Vector3 testCenter = boxCenter + new Vector3(0, slot.minDistance * 0.5f, 0);

                    // 同样应用低优先级清理逻辑
                    bool hasOverlap = spatial.Overlaps(testCenter, actualInflated);
                    if (hasOverlap)
                    {
                        List<GameObject> lowerPriorityInstances = spatial.GetOverlappedLowerPriorityInstances(
                            testCenter, actualInflated, slot.priority);

                        if (lowerPriorityInstances.Count > 0)
                        {
                            int fallback1Destroyed = 0;
                            foreach (var inst in lowerPriorityInstances)
                            {
                                if (inst != null)
                                {
#if UNITY_EDITOR
                                    if (settings.showDebugGizmos && settings.showDestroyedObjects)
                                    {
                                        if (spatial.TryGetEntry(inst, out SpatialHash.Entry entry))
                                        {
                                            ProcGenDebugManager.Instance.AddDestroyedObject(entry.center, entry.halfExtents, settings.destroyedColor);
                                        }
                                    }
#endif
                                    Undo.DestroyObjectImmediate(inst);
                                    if (spatial.Remove(inst)) fallback1Destroyed++;
                                }
                            }
                            if (fallback1Destroyed > 0)
                            {
                                Debug.Log($"[ProcGen] 补充搜索：插槽 \"{slot.name}\" (优先级 {slot.priority}) 清理了 {fallback1Destroyed} 个低优先级重叠物体");
                            }
                            hasOverlap = spatial.Overlaps(testCenter, actualInflated);
                        }

                        if (hasOverlap) continue;
                    }

                    PlaceOneInstance(slot, slotIndex, pos, actualHalfExtents, actualInflated, instScale, pickedPrefab, finalRotation,
                        spatial, placedPositions, parent, boxCenter, settings);
                    placed++;
                }

                pool.Clear();
            }

            // ============ Fallback 阶段 2：硬挤模式 ============
            bool forcePlacedAny = false;
            int forcePlacedCount = 0;
            if (placed < slot.count)
            {
                var loosePool = new List<int>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++) loosePool.Add(i);

                if (placedPositions.Count > 0)
                {
                    loosePool.Sort((a, b) =>
                    {
                        Vector3 pa = candidates[a];
                        Vector3 pb = candidates[b];
                        float da = float.PositiveInfinity, db = float.PositiveInfinity;

                        for (int j = 0; j < placedPositions.Count; j++)
                        {
                            float ta = (placedPositions[j] - pa).sqrMagnitude;
                            float tb = (placedPositions[j] - pb).sqrMagnitude;
                            if (ta < da) da = ta;
                            if (tb < db) db = tb;
                        }

                        return db.CompareTo(da);
                    });
                }

                foreach (int candIdx in loosePool)
                {
                    if (placed >= slot.count) break;

                    Vector3 pos = candidates[candIdx];

                    GameObject pickedPrefab = PickPrefab(slot, rng);
                    if (pickedPrefab == null) break;

                    float instScale = scaleMin + (float)rng.NextDouble() * (scaleMax - scaleMin);
                    Quaternion finalRotation = Quaternion.Euler(0f, slot.yRotation, 0f) * pickedPrefab.transform.rotation;
                    Bounds worldBounds = GetWorldBoundsForInstance(slot, pickedPrefab, instScale, finalRotation);
                    Vector3 actualHalfExtents = worldBounds.extents;

                    Vector3 worldOffset2 = finalRotation * slot.positionOffset;
                    Vector3 boxCenter = pos + worldOffset2 + worldBounds.center;

                    // 硬挤模式：只检测物理重叠，忽略最小间距
                    bool hasOverlap = spatial.Overlaps(boxCenter, actualHalfExtents);
                    if (hasOverlap)
                    {
                        List<GameObject> lowerPriorityInstances = spatial.GetOverlappedLowerPriorityInstances(
                            boxCenter, actualHalfExtents, slot.priority);

                        if (lowerPriorityInstances.Count > 0)
                        {
                            int hardDestroyed = 0;
                            foreach (var inst in lowerPriorityInstances)
                            {
                                if (inst != null)
                                {
#if UNITY_EDITOR
                                    if (settings.showDebugGizmos && settings.showDestroyedObjects)
                                    {
                                        if (spatial.TryGetEntry(inst, out SpatialHash.Entry entry))
                                        {
                                            ProcGenDebugManager.Instance.AddDestroyedObject(entry.center, entry.halfExtents, settings.destroyedColor);
                                        }
                                    }
#endif
                                    Undo.DestroyObjectImmediate(inst);
                                    if (spatial.Remove(inst)) hardDestroyed++;
                                }
                            }
                            if (hardDestroyed > 0)
                            {
                                Debug.Log($"[ProcGen] 硬挤模式：插槽 \"{slot.name}\" (优先级 {slot.priority}) 清理了 {hardDestroyed} 个低优先级重叠物体");
                            }
                            hasOverlap = spatial.Overlaps(boxCenter, actualHalfExtents);
                        }

                        if (hasOverlap) continue;
                    }

                    PlaceOneInstance(slot, slotIndex, pos, actualHalfExtents, actualHalfExtents, instScale, pickedPrefab, finalRotation,
                        spatial, placedPositions, parent, boxCenter, settings);
                    placed++;
                    forcePlacedCount++;
                    forcePlacedAny = true;
                }
            }

            if (forcePlacedAny)
            {
                Debug.LogWarning($"[ProcGen] 插槽 \"{slot.name}\"：数量设定超出距离规则可容纳的上限，" +
                    $"其中 {forcePlacedCount} 个使用了宽松间距强制放置（可能贴得很近）。" +
                    $"建议减小最小间距或降低数量。");
            }

            if (placed < slot.count)
            {
                Debug.LogWarning($"[ProcGen] 插槽 \"{slot.name}\" 仍然只放置了 {placed}/{slot.count} 个 " +
                    $"(候选点 {candidates.Count} 个，确实容纳不下，请检查边界内缩/网格单元/Mesh 大小)");
            }

            return placed;
        }

        // 重构后的实例化逻辑：接收最终旋转和正确的碰撞盒
        private static void PlaceOneInstance(ProcGenSlot slot, int slotIndex, Vector3 pos, Vector3 halfExtents, Vector3 inflatedExtents,
            float instScale, GameObject pickedPrefab, Quaternion finalRotation,
            SpatialHash spatial, List<Vector3> placedPositions, Transform parent, Vector3 boxCenter, ProcGenSettings settings)
        {
            Vector3 worldOffset = finalRotation * slot.positionOffset;

            // 实例化（优先使用PrefabUtility保持预制体关联）
            GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(pickedPrefab);
            if (go == null) go = Object.Instantiate(pickedPrefab);
            Undo.RegisterCreatedObjectUndo(go, "ProcGen Place");

            go.transform.SetParent(parent, true);
            go.transform.position = pos + worldOffset;
            go.transform.rotation = finalRotation;
            go.transform.localScale = pickedPrefab.transform.localScale * instScale;

            // 材质覆盖
            if (slot.overrideMaterial != null)
            {
                ApplyOverrideMaterial(go, slot.overrideMaterial);
            }

            // 记录到空间哈希（使用正确的旋转后碰撞盒）
            spatial.Add(boxCenter, halfExtents, slot.priority, go, slotIndex, slot.canBeDestroyed);
            placedPositions.Add(go.transform.position);

            // ========== 调试：添加碰撞盒和避让范围 ==========
#if UNITY_EDITOR
            if (settings.showDebugGizmos)
            {
                if (settings.showCollisionBoxes)
                {
                    ProcGenDebugManager.Instance.AddCollisionBox(boxCenter, halfExtents, settings.collisionBoxColor);
                }
                if (settings.showAvoidanceBounds && inflatedExtents != halfExtents)
                {
                    Vector3 inflatedCenter = boxCenter + new Vector3(0, slot.minDistance * 0.5f, 0);
                    ProcGenDebugManager.Instance.AddAvoidanceBound(inflatedCenter, inflatedExtents, settings.avoidanceColor);
                }
            }
#endif
            // ==============================================
        }

        // ========== 核心修复：计算旋转+缩放后的世界空间AABB ==========
        /// <summary>
        /// 计算预制体实例在应用旋转和缩放后的世界空间AABB碰撞盒
        /// </summary>
        private static Bounds GetWorldBoundsForInstance(ProcGenSlot slot, GameObject prefab, float scale, Quaternion rotation)
        {
            // 自定义模式：使用用户指定的半尺寸，应用旋转
            if (slot.boundsMode == BoundsMode.Custom && slot.customHalfExtents.sqrMagnitude > 0.0001f)
            {
                Vector3 extents = slot.customHalfExtents * scale;
                return TransformLocalBounds(new Bounds(new Vector3(0, extents.y, 0), extents * 2f), 1f, rotation);
            }

            // 选择子 Renderer 模式
            if (slot.boundsMode == BoundsMode.SelectRenderers
                && slot.selectedRendererPaths != null
                && slot.selectedRendererPaths.Count > 0)
            {
                Bounds? localBounds = null;

                foreach (var path in slot.selectedRendererPaths)
                {
                    Transform tr;
                    if (path == "(self)") tr = prefab.transform;
                    else tr = prefab.transform.Find(path);

                    if (tr == null) continue;
                    var r = tr.GetComponent<Renderer>();
                    if (r == null) continue;

                    // 转换到预制体本地空间
                    Bounds rendererLocalBounds = r.localBounds;
                    Vector3 localCenter = r.transform.localPosition + rendererLocalBounds.center;
                    Vector3 localExtents = rendererLocalBounds.extents;

                    if (localBounds.HasValue)
                    {
                        var bb = localBounds.Value;
                        bb.Encapsulate(new Bounds(localCenter, localExtents * 2f));
                        localBounds = bb;
                    }
                    else
                    {
                        localBounds = new Bounds(localCenter, localExtents * 2f);
                    }
                }

                if (localBounds.HasValue)
                {
                    // 应用缩放和旋转，计算世界空间AABB
                    return TransformLocalBounds(localBounds.Value, scale, rotation);
                }
            }

            // Auto 模式：计算所有Renderer的本地合并bounds
            var renderers = prefab.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                Vector3 defaultExtents = Vector3.one * 0.5f * scale;
                return TransformLocalBounds(new Bounds(new Vector3(0, defaultExtents.y, 0), defaultExtents * 2f), 1f, rotation);
            }

            Bounds overallLocalBounds = new Bounds();
            bool first = true;

            foreach (var r in renderers)
            {
                // 转换到预制体本地空间
                Bounds rendererLocalBounds = r.localBounds;
                Vector3 localCenter = r.transform.localPosition + rendererLocalBounds.center;
                Vector3 localExtents = rendererLocalBounds.extents;

                if (first)
                {
                    overallLocalBounds = new Bounds(localCenter, localExtents * 2f);
                    first = false;
                }
                else
                {
                    overallLocalBounds.Encapsulate(new Bounds(localCenter, localExtents * 2f));
                }
            }

            // 应用缩放和旋转，计算世界空间AABB
            return TransformLocalBounds(overallLocalBounds, scale, rotation);
        }

        /// <summary>
        /// 将本地空间的Bounds应用缩放和旋转，转换为世界空间的AABB
        /// </summary>
        private static Bounds TransformLocalBounds(Bounds localBounds, float scale, Quaternion rotation)
        {
            // 先应用缩放
            Vector3 scaledCenter = localBounds.center * scale;
            Vector3 scaledExtents = localBounds.extents * scale;

            // 计算旋转后的8个顶点
            Vector3[] vertices = new Vector3[8];
            vertices[0] = rotation * (scaledCenter + new Vector3(-scaledExtents.x, -scaledExtents.y, -scaledExtents.z));
            vertices[1] = rotation * (scaledCenter + new Vector3(scaledExtents.x, -scaledExtents.y, -scaledExtents.z));
            vertices[2] = rotation * (scaledCenter + new Vector3(scaledExtents.x, -scaledExtents.y, scaledExtents.z));
            vertices[3] = rotation * (scaledCenter + new Vector3(-scaledExtents.x, -scaledExtents.y, scaledExtents.z));
            vertices[4] = rotation * (scaledCenter + new Vector3(-scaledExtents.x, scaledExtents.y, -scaledExtents.z));
            vertices[5] = rotation * (scaledCenter + new Vector3(scaledExtents.x, scaledExtents.y, -scaledExtents.z));
            vertices[6] = rotation * (scaledCenter + new Vector3(scaledExtents.x, scaledExtents.y, scaledExtents.z));
            vertices[7] = rotation * (scaledCenter + new Vector3(-scaledExtents.x, scaledExtents.y, scaledExtents.z));

            // 计算包含所有顶点的AABB
            Bounds worldBounds = new Bounds(vertices[0], Vector3.zero);
            for (int i = 1; i < 8; i++)
            {
                worldBounds.Encapsulate(vertices[i]);
            }

            return worldBounds;
        }

        private static void ApplyOverrideMaterial(GameObject go, Material mat)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                // 用 sharedMaterials 避免编辑器材质实例泄漏
                int slotCount = r.sharedMaterials.Length;
                if (slotCount <= 1)
                {
                    r.sharedMaterial = mat;
                }
                else
                {
                    var mats = new Material[slotCount];
                    for (int i = 0; i < slotCount; i++) mats[i] = mat;
                    r.sharedMaterials = mats;
                }
            }
        }

        private static bool HasAnyValidPrefab(ProcGenSlot slot)
        {
            if (slot.prefabEntries == null) return false;
            foreach (var e in slot.prefabEntries)
                if (e != null && e.prefab != null && e.weight > 0f) return true;
            return false;
        }

        private static GameObject PickPrefab(ProcGenSlot slot, System.Random rng)
        {
            if (slot.prefabEntries == null || slot.prefabEntries.Count == 0) return null;

            // 计算总权重
            float total = 0f;
            for (int i = 0; i < slot.prefabEntries.Count; i++)
            {
                var e = slot.prefabEntries[i];
                if (e == null || e.prefab == null) continue;
                float w = Mathf.Max(0f, e.weight);
                total += w;
            }

            if (total <= 0f) return null;

            float pick = (float)rng.NextDouble() * total;
            float acc = 0f;

            for (int i = 0; i < slot.prefabEntries.Count; i++)
            {
                var e = slot.prefabEntries[i];
                if (e == null || e.prefab == null) continue;
                float w = Mathf.Max(0f, e.weight);
                acc += w;
                if (pick <= acc) return e.prefab;
            }

            // 兜底
            for (int i = slot.prefabEntries.Count - 1; i >= 0; i--)
            {
                var e = slot.prefabEntries[i];
                if (e != null && e.prefab != null) return e.prefab;
            }

            return null;
        }
    }
}
