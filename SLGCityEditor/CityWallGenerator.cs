using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>将边界转换为保持 Prefab 连接的编辑期场景对象。</summary>
    public static class CityWallGenerator
    {
        public readonly struct Result
        {
            public readonly int StraightCount;
            public readonly int CornerCount;
            public readonly int GateCount;
            public readonly int InvalidGateCount;
            public readonly int UnsupportedCount;
            public readonly Transform Root;

            public Result(
                int straightCount,
                int cornerCount,
                int gateCount,
                int invalidGateCount,
                int unsupportedCount,
                Transform root)
            {
                StraightCount = straightCount;
                CornerCount = cornerCount;
                GateCount = gateCount;
                InvalidGateCount = invalidGateCount;
                UnsupportedCount = unsupportedCount;
                Root = root;
            }
        }

        public sealed class GatePlacement
        {
            public Vector2Int Anchor;
            public Vector2Int Direction;
            public readonly List<Vector2Int> Cells = new List<Vector2Int>();
            public bool IsValid;
        }

        private static readonly Vector2Int[] CardinalDirections =
        {
            Vector2Int.right,
            Vector2Int.up,
            Vector2Int.left,
            Vector2Int.down
        };

        public static bool TryGenerate(
            CityLayoutData layout,
            CityWallPrefabSet prefabSet,
            Transform outputParent,
            out Result result,
            out string error)
        {
            result = default;
            error = string.Empty;
            if (layout == null)
            {
                error = "请先指定 City Layout Data。";
                return false;
            }
            if (prefabSet == null || prefabSet.StraightPrefab == null || prefabSet.CornerPrefab == null)
            {
                error = "请指定 Wall Prefab Set，并设置 Straight 与 Corner Prefab。";
                return false;
            }
            if (layout.Grid.CountOccupied() == 0)
            {
                error = "当前主城区域为空，请先在 Scene 视图中绘制区域。";
                return false;
            }

            List<Vector2Int> boundaryCells =
                CityBoundaryExtractor.ExtractBoundaryCells(layout.Grid);
            if (boundaryCells.Count == 0)
            {
                error = "没有提取到有效边界。";
                return false;
            }

            var boundarySet = new HashSet<Vector2Int>(boundaryCells);
            List<GatePlacement> gatePlacements = ResolveGatePlacements(layout.Grid);
            var validGates = new List<GatePlacement>();
            var coveredByGates = new HashSet<Vector2Int>();
            int invalidGateCount = 0;
            for (int i = 0; i < gatePlacements.Count; i++)
            {
                GatePlacement placement = gatePlacements[i];
                if (!placement.IsValid)
                {
                    invalidGateCount++;
                    continue;
                }
                validGates.Add(placement);
                for (int cellIndex = 0; cellIndex < placement.Cells.Count; cellIndex++)
                    coveredByGates.Add(placement.Cells[cellIndex]);
            }
            int validGateCount = validGates.Count;
            if (validGateCount > 0 && prefabSet.GatePrefab == null)
            {
                error = $"布局中有 {validGateCount} 段有效 Gate 标记，请先在 Wall Prefab Set 中指定 Gate Prefab。";
                return false;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Generate SLG City Walls");
            string rootName = layout.name + "_Walls";
            Transform oldRoot = FindDirectChild(outputParent, rootName);
            if (oldRoot != null) Undo.DestroyObjectImmediate(oldRoot.gameObject);

            var rootObject = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(rootObject, "Generate SLG City Walls");
            if (outputParent != null)
            {
                SceneManager.MoveGameObjectToScene(rootObject, outputParent.gameObject.scene);
                Undo.SetTransformParent(rootObject.transform, outputParent, "Generate SLG City Walls");
            }

            int straightCount = 0;
            int cornerCount = 0;
            int gateCount = 0;
            int unsupportedCount = 0;
            var wallRoot = new GameObject("Wall Cells");
            Undo.RegisterCreatedObjectUndo(wallRoot, "Generate SLG City Walls");
            Undo.SetTransformParent(wallRoot.transform, rootObject.transform, "Generate SLG City Walls");

            // 每段连续 Gate 标记只生成一个 Prefab，并覆盖掉该段所有普通城墙格。
            for (int i = 0; i < validGates.Count; i++)
            {
                CreateGate(layout, prefabSet, validGates[i], wallRoot.transform, gateCount++);
            }

            for (int i = 0; i < boundaryCells.Count; i++)
            {
                Vector2Int cell = boundaryCells[i];
                if (coveredByGates.Contains(cell)) continue;

                List<Vector2Int> neighbors = GetBoundaryNeighbors(cell, boundarySet);
                if (neighbors.Count == 2 && AreAdjacent(neighbors[0], neighbors[1]))
                {
                    CreateCorner(
                        layout,
                        prefabSet,
                        cell,
                        neighbors[0],
                        neighbors[1],
                        wallRoot.transform,
                        cornerCount++);
                    continue;
                }

                Vector2Int straightDirection = ResolveStraightDirection(neighbors);
                if (neighbors.Count != 1 && neighbors.Count != 2) unsupportedCount++;
                CreateStraight(
                    layout,
                    prefabSet,
                    cell,
                    straightDirection,
                    wallRoot.transform,
                    straightCount++);
            }

            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = rootObject;
            result = new Result(
                straightCount,
                cornerCount,
                gateCount,
                invalidGateCount,
                unsupportedCount,
                rootObject.transform);
            return true;
        }

        public static bool TryClear(CityLayoutData layout, Transform outputParent)
        {
            if (layout == null) return false;
            Transform root = FindDirectChild(outputParent, layout.name + "_Walls");
            if (root == null) return false;
            Undo.DestroyObjectImmediate(root.gameObject);
            return true;
        }

        public static bool CanPlaceGate(GridData grid, Vector2Int cell)
        {
            if (grid == null || !grid.IsOccupied(cell)) return false;
            var boundaryCells = CityBoundaryExtractor.ExtractBoundaryCells(grid);
            var boundarySet = new HashSet<Vector2Int>(boundaryCells);
            return boundarySet.Contains(cell)
                && IsSupportedStraight(GetBoundaryNeighbors(cell, boundarySet));
        }

        public static List<GatePlacement> ResolveGatePlacements(GridData grid)
        {
            var placements = new List<GatePlacement>();
            if (grid == null) return placements;
            var boundarySet = new HashSet<Vector2Int>(
                CityBoundaryExtractor.ExtractBoundaryCells(grid));
            var visited = new HashSet<Vector2Int>();

            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var start = new Vector2Int(x, y);
                    if (!grid.IsGate(start) || !visited.Add(start)) continue;

                    var placement = new GatePlacement { Anchor = start };
                    var queue = new Queue<Vector2Int>();
                    queue.Enqueue(start);
                    while (queue.Count > 0)
                    {
                        Vector2Int current = queue.Dequeue();
                        placement.Cells.Add(current);
                        for (int directionIndex = 0;
                             directionIndex < CardinalDirections.Length;
                             directionIndex++)
                        {
                            Vector2Int next = current + CardinalDirections[directionIndex];
                            if (!grid.IsGate(next) || !visited.Add(next)) continue;
                            queue.Enqueue(next);
                        }
                    }

                    placement.IsValid = ValidateGatePlacement(placement, boundarySet);
                    placements.Add(placement);
                }
            }
            return placements;
        }

        private static void CreateStraight(
            CityLayoutData layout,
            CityWallPrefabSet prefabSet,
            Vector2Int cell,
            Vector2Int direction,
            Transform parent,
            int index)
        {
            Quaternion rotation = Quaternion.FromToRotation(
                    Vector3.right,
                    ToWorldDirection(direction))
                * Quaternion.Euler(0f, prefabSet.StraightYawOffset, 0f);
            GameObject instance = InstantiatePrefab(prefabSet.StraightPrefab, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Generate SLG City Walls");
            Undo.RegisterFullObjectHierarchyUndo(instance, "Generate SLG City Walls");
            instance.name = $"Wall_Straight_{index + 1:000}";
            Undo.SetTransformParent(instance.transform, parent, "Generate SLG City Walls");
            instance.transform.SetPositionAndRotation(
                layout.CellCenterToWorld(cell) + Vector3.up * prefabSet.HeightOffset,
                rotation);
            instance.transform.position += rotation * prefabSet.StraightLocalOffset;
        }

        private static void CreateCorner(
            CityLayoutData layout,
            CityWallPrefabSet prefabSet,
            Vector2Int cell,
            Vector2Int firstDirection,
            Vector2Int secondDirection,
            Transform parent,
            int index)
        {
            int cross = Cross(firstDirection, secondDirection);
            Vector2Int localXDirection = cross > 0 ? firstDirection : secondDirection;
            Quaternion rotation = Quaternion.FromToRotation(
                    Vector3.right,
                    ToWorldDirection(localXDirection))
                * Quaternion.Euler(0f, prefabSet.CornerYawOffset, 0f);
            GameObject instance = InstantiatePrefab(prefabSet.CornerPrefab, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Generate SLG City Walls");
            Undo.RegisterFullObjectHierarchyUndo(instance, "Generate SLG City Walls");
            instance.name = $"Wall_Corner_{index + 1:000}";
            Undo.SetTransformParent(instance.transform, parent, "Generate SLG City Walls");
            instance.transform.SetPositionAndRotation(
                layout.CellCenterToWorld(cell) + Vector3.up * prefabSet.HeightOffset,
                rotation);
            instance.transform.position += rotation * prefabSet.CornerLocalOffset;
        }

        private static void CreateGate(
            CityLayoutData layout,
            CityWallPrefabSet prefabSet,
            GatePlacement placement,
            Transform parent,
            int index)
        {
            Quaternion rotation = Quaternion.FromToRotation(
                    Vector3.right,
                    ToWorldDirection(placement.Direction))
                * Quaternion.Euler(0f, prefabSet.GateYawOffset, 0f);
            Vector3 center = Vector3.zero;
            for (int i = 0; i < placement.Cells.Count; i++)
                center += layout.CellCenterToWorld(placement.Cells[i]);
            center /= placement.Cells.Count;
            GameObject instance = InstantiatePrefab(prefabSet.GatePrefab, parent.gameObject.scene);
            Undo.RegisterCreatedObjectUndo(instance, "Generate SLG City Walls");
            Undo.RegisterFullObjectHierarchyUndo(instance, "Generate SLG City Walls");
            instance.name = $"Wall_Gate_{index + 1:000}";
            Undo.SetTransformParent(instance.transform, parent, "Generate SLG City Walls");
            instance.transform.SetPositionAndRotation(
                center + Vector3.up * prefabSet.HeightOffset,
                rotation);
            instance.transform.position += rotation * prefabSet.GateLocalOffset;
        }

        private static GameObject InstantiatePrefab(GameObject prefab, Scene scene)
        {
            Object created = PrefabUtility.InstantiatePrefab(prefab, scene);
            if (created is GameObject gameObject) return gameObject;
            throw new InvalidOperationException($"无法实例化 Prefab：{prefab.name}");
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

        private static Vector3 ToWorldDirection(Vector2Int direction)
        {
            return new Vector3(direction.x, 0f, direction.y).normalized;
        }

        private static List<Vector2Int> GetBoundaryNeighbors(
            Vector2Int cell,
            HashSet<Vector2Int> boundarySet)
        {
            var neighbors = new List<Vector2Int>(4);
            for (int i = 0; i < CardinalDirections.Length; i++)
            {
                Vector2Int direction = CardinalDirections[i];
                if (boundarySet.Contains(cell + direction)) neighbors.Add(direction);
            }
            return neighbors;
        }

        private static bool AreAdjacent(Vector2Int first, Vector2Int second)
        {
            return Cross(first, second) != 0;
        }

        private static bool IsSupportedStraight(IReadOnlyList<Vector2Int> neighbors)
        {
            return neighbors.Count == 1
                || (neighbors.Count == 2 && !AreAdjacent(neighbors[0], neighbors[1]));
        }

        private static bool ValidateGatePlacement(
            GatePlacement placement,
            HashSet<Vector2Int> boundarySet)
        {
            if (placement.Cells.Count == 0) return false;

            Vector2Int first = placement.Cells[0];
            bool sameX = true;
            bool sameY = true;
            int minAxis = int.MaxValue;
            int maxAxis = int.MinValue;
            for (int i = 1; i < placement.Cells.Count; i++)
            {
                sameX &= placement.Cells[i].x == first.x;
                sameY &= placement.Cells[i].y == first.y;
            }
            if (!sameX && !sameY) return false;

            Vector2Int direction;
            if (placement.Cells.Count == 1)
            {
                if (!boundarySet.Contains(first)) return false;
                List<Vector2Int> neighbors = GetBoundaryNeighbors(first, boundarySet);
                if (!IsSupportedStraight(neighbors)) return false;
                direction = CanonicalAxis(ResolveStraightDirection(neighbors));
            }
            else
            {
                direction = sameY ? Vector2Int.right : Vector2Int.up;
            }
            placement.Direction = direction;

            for (int i = 0; i < placement.Cells.Count; i++)
            {
                Vector2Int cell = placement.Cells[i];
                if (!boundarySet.Contains(cell)) return false;
                List<Vector2Int> neighbors = GetBoundaryNeighbors(cell, boundarySet);
                if (!IsSupportedStraight(neighbors)) return false;
                if (CanonicalAxis(ResolveStraightDirection(neighbors)) != direction) return false;
                int axis = direction.x != 0 ? cell.x : cell.y;
                minAxis = Mathf.Min(minAxis, axis);
                maxAxis = Mathf.Max(maxAxis, axis);
            }
            return maxAxis - minAxis + 1 == placement.Cells.Count;
        }

        private static Vector2Int ResolveStraightDirection(IReadOnlyList<Vector2Int> neighbors)
        {
            for (int i = 0; i < neighbors.Count; i++)
            {
                for (int j = i + 1; j < neighbors.Count; j++)
                {
                    if (neighbors[i] + neighbors[j] == Vector2Int.zero)
                        return CanonicalAxis(neighbors[i]);
                }
            }
            return neighbors.Count > 0 ? CanonicalAxis(neighbors[0]) : Vector2Int.right;
        }

        private static Vector2Int CanonicalAxis(Vector2Int direction)
        {
            if (direction.x != 0) return Vector2Int.right;
            return Vector2Int.up;
        }

        private static int Cross(Vector2Int first, Vector2Int second)
        {
            return first.x * second.y - first.y * second.x;
        }
    }
}
