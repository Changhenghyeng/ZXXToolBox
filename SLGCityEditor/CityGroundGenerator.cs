using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>根据已占用格子的外接矩形生成编辑期 Quad 或 Unity Terrain。</summary>
    public static class CityGroundGenerator
    {
        private const string TerrainDataFolder = "Assets/Generated/SLGCity/TerrainData";
        private const string QuadSuffix = "_GroundQuad";
        private const string TerrainSuffix = "_Terrain";

        public static bool TryGenerateQuad(
            CityLayoutData layout,
            Material material,
            float heightOffset,
            Transform outputParent,
            out GameObject ground,
            out string error)
        {
            ground = null;
            error = string.Empty;
            if (layout == null)
            {
                error = "请先指定 City Layout Data。";
                return false;
            }
            if (!TryGetOccupiedBounds(layout.Grid, out Vector2Int min, out Vector2Int maxExclusive))
            {
                error = "当前没有已绘制区域，无法生成地面。";
                return false;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate SLG City Ground");

            string groundName = layout.name + QuadSuffix;
            Transform oldGround = FindDirectChild(outputParent, groundName);
            if (oldGround == null)
            {
                Transform legacyGround = FindDirectChild(outputParent, layout.name + "_Ground");
                if (legacyGround != null && legacyGround.GetComponent<Terrain>() == null)
                    oldGround = legacyGround;
            }
            if (oldGround != null) Undo.DestroyObjectImmediate(oldGround.gameObject);

            // Unity 内置 Quad 只有 4 个顶点和 2 个三角面，直接缩放即可覆盖框选范围。
            ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
            ground.name = groundName;
            Collider collider = ground.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            Undo.RegisterCreatedObjectUndo(ground, "Generate SLG City Ground");

            if (outputParent != null)
            {
                SceneManager.MoveGameObjectToScene(ground, outputParent.gameObject.scene);
                Undo.SetTransformParent(ground.transform, outputParent, "Generate SLG City Ground");
            }

            Vector3 minWorld = layout.GridPointToWorld(min);
            Vector3 maxWorld = layout.GridPointToWorld(maxExclusive);
            Vector3 center = (minWorld + maxWorld) * 0.5f + Vector3.up * heightOffset;
            float width = (maxExclusive.x - min.x) * layout.CellSize;
            float height = (maxExclusive.y - min.y) * layout.CellSize;
            // Quad 的默认法线朝 Local -Z；绕 X 轴 +90° 后法线朝世界 +Y。
            ground.transform.SetPositionAndRotation(center, Quaternion.Euler(90f, 0f, 0f));
            ground.transform.localScale = new Vector3(width, height, 1f);

            if (material != null)
                ground.GetComponent<MeshRenderer>().sharedMaterial = material;

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = ground;
            return true;
        }

        public static bool TryCreateOrRestoreTerrain(
            CityLayoutData layout,
            TerrainLayer terrainLayer,
            float terrainHeight,
            int heightmapResolution,
            float heightOffset,
            Transform outputParent,
            out GameObject ground,
            out TerrainData terrainData,
            out bool createdTerrainData,
            out string error)
        {
            ground = null;
            terrainData = null;
            createdTerrainData = false;
            error = string.Empty;
            if (layout == null)
            {
                error = "请先指定 City Layout Data。";
                return false;
            }
            terrainData = FindTerrainData(layout);
            if (terrainData != null)
            {
                BindTerrainData(layout, terrainData);
                Terrain existingTerrain = FindTerrainUsingData(terrainData);
                if (existingTerrain != null)
                {
                    ground = existingTerrain.gameObject;
                    Selection.activeGameObject = ground;
                    return true;
                }
            }

            bool hasBounds = TryGetOccupiedBounds(
                layout.Grid,
                out Vector2Int min,
                out Vector2Int maxExclusive);
            if (terrainData == null && !hasBounds)
            {
                error = "当前没有已绘制区域，无法首次创建 Terrain。";
                return false;
            }
            if (!hasBounds) min = Vector2Int.zero;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Or Restore SLG City Terrain");

            if (terrainData == null)
            {
                string assetPath = GetTerrainDataAssetPath(layout);
                EnsureAssetFolder(TerrainDataFolder);
                terrainData = new TerrainData { name = layout.name + "_TerrainData" };
                int resolution = NormalizeHeightmapResolution(heightmapResolution);
                float width = (maxExclusive.x - min.x) * layout.CellSize;
                float depth = (maxExclusive.y - min.y) * layout.CellSize;
                terrainData.heightmapResolution = resolution;
                terrainData.size = new Vector3(width, Mathf.Max(0.01f, terrainHeight), depth);
                terrainData.SetHeights(0, 0, new float[resolution, resolution]);
                terrainData.terrainLayers = terrainLayer != null
                    ? new[] { terrainLayer }
                    : new TerrainLayer[0];
                AssetDatabase.CreateAsset(terrainData, assetPath);
                createdTerrainData = true;
            }

            BindTerrainData(layout, terrainData);

            ground = Terrain.CreateTerrainGameObject(terrainData);
            ground.name = layout.name + TerrainSuffix;
            Undo.RegisterCreatedObjectUndo(ground, "Generate SLG City Terrain");
            if (outputParent != null)
            {
                SceneManager.MoveGameObjectToScene(ground, outputParent.gameObject.scene);
                Undo.SetTransformParent(ground.transform, outputParent, "Generate SLG City Terrain");
            }

            Vector3 minWorld = layout.GridPointToWorld(min);
            ground.transform.position = minWorld + Vector3.up * heightOffset;
            ground.transform.rotation = Quaternion.identity;
            ground.transform.localScale = Vector3.one;

            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = ground;
            return true;
        }

        public static TerrainData FindTerrainData(CityLayoutData layout)
        {
            if (layout == null) return null;
            if (layout.TerrainData != null) return layout.TerrainData;
            return AssetDatabase.LoadAssetAtPath<TerrainData>(GetTerrainDataAssetPath(layout));
        }

        public static bool TryClearQuad(CityLayoutData layout, Transform outputParent)
        {
            if (layout == null) return false;
            Transform ground = FindDirectChild(outputParent, layout.name + QuadSuffix);
            if (ground == null)
            {
                Transform legacyGround = FindDirectChild(outputParent, layout.name + "_Ground");
                if (legacyGround != null && legacyGround.GetComponent<Terrain>() == null)
                    ground = legacyGround;
            }
            if (ground == null) return false;
            Undo.DestroyObjectImmediate(ground.gameObject);
            return true;
        }

        public static bool TryGetOccupiedBounds(
            GridData grid,
            out Vector2Int min,
            out Vector2Int maxExclusive)
        {
            min = new Vector2Int(int.MaxValue, int.MaxValue);
            maxExclusive = Vector2Int.zero;
            var maxInclusive = new Vector2Int(int.MinValue, int.MinValue);
            if (grid == null) return false;

            bool found = false;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!grid.IsOccupied(cell)) continue;
                    found = true;
                    min = Vector2Int.Min(min, cell);
                    maxInclusive = Vector2Int.Max(maxInclusive, cell);
                }
            }

            if (found) maxExclusive = maxInclusive + Vector2Int.one;
            return found;
        }

        private static Transform FindDirectChild(Transform parent, string name)
        {
            if (parent != null) return parent.Find(name);
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name) return roots[i].transform;
            }
            return null;
        }

        private static Terrain FindTerrainUsingData(TerrainData terrainData)
        {
            Terrain[] terrains = Object.FindObjectsOfType<Terrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                if (terrains[i].terrainData == terrainData) return terrains[i];
            }
            return null;
        }

        private static void BindTerrainData(CityLayoutData layout, TerrainData terrainData)
        {
            if (layout.TerrainData == terrainData) return;
            Undo.RecordObject(layout, "Bind SLG City Terrain Data");
            layout.TerrainData = terrainData;
            EditorUtility.SetDirty(layout);
        }

        private static int NormalizeHeightmapResolution(int resolution)
        {
            int[] supported = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
            int nearest = supported[0];
            int nearestDistance = Mathf.Abs(resolution - nearest);
            for (int i = 1; i < supported.Length; i++)
            {
                int distance = Mathf.Abs(resolution - supported[i]);
                if (distance >= nearestDistance) continue;
                nearest = supported[i];
                nearestDistance = distance;
            }
            return nearest;
        }

        private static string GetTerrainDataAssetPath(CityLayoutData layout)
        {
            string layoutPath = AssetDatabase.GetAssetPath(layout);
            string guid = AssetDatabase.AssetPathToGUID(layoutPath);
            if (string.IsNullOrEmpty(guid)) guid = layout.GetInstanceID().ToString("X8");
            return $"{TerrainDataFolder}/Terrain_{guid}.asset";
        }

        private static void EnsureAssetFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
