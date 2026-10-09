using System.Collections.Generic;
using UnityEngine;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>从占用单元中提取有向边界；边界行进时，主城区域始终位于左侧。</summary>
    public static class CityBoundaryExtractor
    {
        private static readonly Vector2Int[] CardinalDirections =
        {
            Vector2Int.right,
            Vector2Int.up,
            Vector2Int.left,
            Vector2Int.down
        };

        public readonly struct Edge
        {
            public readonly Vector2Int Start;
            public readonly Vector2Int End;

            public Edge(Vector2Int start, Vector2Int end)
            {
                Start = start;
                End = end;
            }

            public Vector2Int Direction => End - Start;
        }

        public sealed class Loop
        {
            public readonly List<Edge> Edges = new List<Edge>();
            public bool IsClosed;
        }

        public static List<Edge> ExtractEdges(GridData grid)
        {
            var edges = new List<Edge>();
            if (grid == null) return edges;

            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!grid.IsOccupied(cell)) continue;

                    if (!grid.IsOccupied(cell + Vector2Int.down))
                        edges.Add(new Edge(new Vector2Int(x, y), new Vector2Int(x + 1, y)));
                    if (!grid.IsOccupied(cell + Vector2Int.right))
                        edges.Add(new Edge(new Vector2Int(x + 1, y), new Vector2Int(x + 1, y + 1)));
                    if (!grid.IsOccupied(cell + Vector2Int.up))
                        edges.Add(new Edge(new Vector2Int(x + 1, y + 1), new Vector2Int(x, y + 1)));
                    if (!grid.IsOccupied(cell + Vector2Int.left))
                        edges.Add(new Edge(new Vector2Int(x, y + 1), new Vector2Int(x, y)));
                }
            }
            return edges;
        }

        /// <summary>返回区域内部最外圈的占用格；这些格子就是一格宽城墙的放置位置。</summary>
        public static List<Vector2Int> ExtractBoundaryCells(GridData grid)
        {
            var cells = new List<Vector2Int>();
            if (grid == null) return cells;
            for (int y = 0; y < grid.Height; y++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (!grid.IsOccupied(cell)) continue;
                    for (int i = 0; i < CardinalDirections.Length; i++)
                    {
                        if (grid.IsOccupied(cell + CardinalDirections[i])) continue;
                        cells.Add(cell);
                        break;
                    }
                }
            }
            return cells;
        }

        public static List<Loop> ExtractLoops(GridData grid)
        {
            List<Edge> edges = ExtractEdges(grid);
            var starts = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                if (!starts.TryGetValue(edges[i].Start, out List<int> indices))
                {
                    indices = new List<int>();
                    starts.Add(edges[i].Start, indices);
                }
                indices.Add(i);
            }

            var used = new bool[edges.Count];
            var loops = new List<Loop>();
            for (int seed = 0; seed < edges.Count; seed++)
            {
                if (used[seed]) continue;
                var loop = new Loop();
                int current = seed;
                Vector2Int firstPoint = edges[seed].Start;

                while (current >= 0 && !used[current])
                {
                    Edge edge = edges[current];
                    used[current] = true;
                    loop.Edges.Add(edge);
                    if (edge.End == firstPoint)
                    {
                        loop.IsClosed = true;
                        break;
                    }
                    current = FindNextEdge(edge, edges, starts, used);
                }
                loops.Add(loop);
            }
            return loops;
        }

        private static int FindNextEdge(
            Edge incoming,
            IReadOnlyList<Edge> edges,
            IReadOnlyDictionary<Vector2Int, List<int>> starts,
            IReadOnlyList<bool> used)
        {
            if (!starts.TryGetValue(incoming.End, out List<int> candidates)) return -1;
            int bestIndex = -1;
            int bestScore = int.MinValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                int index = candidates[i];
                if (used[index]) continue;
                int score = TurnScore(incoming.Direction, edges[index].Direction);
                if (score <= bestScore) continue;
                bestScore = score;
                bestIndex = index;
            }
            return bestIndex;
        }

        // 对角相接时优先左转，使两个区域保持为独立闭环，不产生交叉城墙。
        private static int TurnScore(Vector2Int from, Vector2Int to)
        {
            int cross = from.x * to.y - from.y * to.x;
            int dot = from.x * to.x + from.y * to.y;
            if (cross > 0) return 3;
            if (dot > 0) return 2;
            if (cross < 0) return 1;
            return 0;
        }
    }
}
