using UnityEngine;
using System.Collections.Generic;

namespace ProcGen
{
    /// <summary>
    /// 简单 AABB 列表 + 均匀网格哈希，用于快速邻近查询。
    /// 所有 AABB 是世界空间、轴对齐。
    /// 扩展：支持优先级管理、实例追踪、低优先级物体清理、可销毁标记
    /// </summary>
    public class SpatialHash
    {
        public struct Entry
        {
            public Vector3 center;
            public Vector3 halfExtents;
            public int priority;       // 实例所属插槽的优先级
            public GameObject instance;// 对应的生成实例
            public int slotIndex;      // 插槽索引（用于调试）
            public bool canBeDestroyed;// 是否允许被更高优先级物体销毁
        }

        private readonly float cellSize;
        private readonly Dictionary<Vector3Int, List<int>> cells = new Dictionary<Vector3Int, List<int>>();
        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<GameObject, int> instanceToEntryIdx = new Dictionary<GameObject, int>();
        private int validCount;

        public SpatialHash(float cellSize)
        {
            this.cellSize = Mathf.Max(0.01f, cellSize);
        }

        public int Count => validCount;

        /// <summary>
        /// 添加一个碰撞盒（带优先级、实例引用、可销毁标记）
        /// </summary>
        public void Add(Vector3 center, Vector3 halfExtents, int priority, GameObject instance, int slotIndex, bool canBeDestroyed)
        {
            int idx = entries.Count;
            entries.Add(new Entry
            {
                center = center,
                halfExtents = halfExtents,
                priority = priority,
                instance = instance,
                slotIndex = slotIndex,
                canBeDestroyed = canBeDestroyed
            });
            instanceToEntryIdx[instance] = idx;
            validCount++;

            Vector3Int min = WorldToCell(center - halfExtents);
            Vector3Int max = WorldToCell(center + halfExtents);
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (!cells.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            cells[key] = list;
                        }
                        list.Add(idx);
                    }
        }

        /// <summary>
        /// 移除指定实例的碰撞盒
        /// </summary>
        public bool Remove(GameObject instance)
        {
            if (!instanceToEntryIdx.TryGetValue(instance, out int idx))
                return false;

            Entry entry = entries[idx];
            Vector3Int min = WorldToCell(entry.center - entry.halfExtents);
            Vector3Int max = WorldToCell(entry.center + entry.halfExtents);

            // 从所有关联格子中移除索引
            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (cells.TryGetValue(key, out var list))
                        {
                            list.Remove(idx);
                            if (list.Count == 0)
                                cells.Remove(key);
                        }
                    }

            // 标记为已移除（保留索引位置避免错乱）
            entries[idx] = new Entry
            {
                center = entry.center,
                halfExtents = entry.halfExtents,
                priority = entry.priority,
                instance = null,
                slotIndex = entry.slotIndex,
                canBeDestroyed = entry.canBeDestroyed
            };

            instanceToEntryIdx.Remove(instance);
            validCount--;
            return true;
        }

        /// <summary>
        /// 尝试获取指定实例的碰撞盒信息（用于调试）
        /// </summary>
        public bool TryGetEntry(GameObject instance, out Entry entry)
        {
            entry = default;
            if (!instanceToEntryIdx.TryGetValue(instance, out int idx))
                return false;

            entry = entries[idx];
            return entry.instance != null;
        }

        /// <summary>
        /// 获取与指定AABB重叠的所有**可被销毁的**低优先级实例
        /// </summary>
        public List<GameObject> GetOverlappedLowerPriorityInstances(Vector3 center, Vector3 halfExtents, int currentPriority)
        {
            List<GameObject> result = new List<GameObject>();
            Vector3Int min = WorldToCell(center - halfExtents);
            Vector3Int max = WorldToCell(center + halfExtents);
            HashSet<int> visited = new HashSet<int>();

            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (!cells.TryGetValue(key, out var list)) continue;

                        foreach (int idx in list)
                        {
                            if (!visited.Add(idx)) continue;
                            Entry e = entries[idx];

                            // 核心判断：已移除/同/高优先级/不允许被销毁 → 跳过
                            if (e.instance == null
                                || e.priority >= currentPriority
                                || !e.canBeDestroyed)
                                continue;

                            if (AABBOverlap(center, halfExtents, e.center, e.halfExtents))
                            {
                                result.Add(e.instance);
                            }
                        }
                    }
            return result;
        }

        /// <summary>
        /// 检测AABB是否与已有任何有效实例重叠
        /// </summary>
        public bool Overlaps(Vector3 center, Vector3 halfExtents)
        {
            Vector3Int min = WorldToCell(center - halfExtents);
            Vector3Int max = WorldToCell(center + halfExtents);
            HashSet<int> visited = new HashSet<int>();

            for (int x = min.x; x <= max.x; x++)
                for (int y = min.y; y <= max.y; y++)
                    for (int z = min.z; z <= max.z; z++)
                    {
                        var key = new Vector3Int(x, y, z);
                        if (!cells.TryGetValue(key, out var list)) continue;

                        foreach (int idx in list)
                        {
                            if (!visited.Add(idx)) continue;
                            Entry e = entries[idx];

                            if (e.instance == null) continue; // 跳过已移除的无效条目

                            if (AABBOverlap(center, halfExtents, e.center, e.halfExtents))
                                return true;
                        }
                    }
            return false;
        }

        private Vector3Int WorldToCell(Vector3 p)
        {
            return new Vector3Int(
                Mathf.FloorToInt(p.x / cellSize),
                Mathf.FloorToInt(p.y / cellSize),
                Mathf.FloorToInt(p.z / cellSize)
            );
        }

        private static bool AABBOverlap(Vector3 c1, Vector3 h1, Vector3 c2, Vector3 h2)
        {
            return Mathf.Abs(c1.x - c2.x) <= (h1.x + h2.x)
                && Mathf.Abs(c1.y - c2.y) <= (h1.y + h2.y)
                && Mathf.Abs(c1.z - c2.z) <= (h1.z + h2.z);
        }
    }
}