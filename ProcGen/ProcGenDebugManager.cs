#if UNITY_EDITOR
using UnityEngine;
using UnityEditor; // ✅ 新增：添加SceneView所需的命名空间引用
using System.Collections.Generic;

namespace ProcGen
{
    /// <summary>
    /// ProcGen调试信息管理器，使用Gizmos实现永久显示和一键清除
    /// </summary>
    [ExecuteInEditMode]
    public class ProcGenDebugManager : MonoBehaviour
    {
        private static ProcGenDebugManager _instance;
        public static ProcGenDebugManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    // 查找场景中已有的管理器
                    _instance = FindObjectOfType<ProcGenDebugManager>();
                    if (_instance == null)
                    {
                        // 创建新的管理器
                        GameObject go = new GameObject("[ProcGen Debug Manager]");
                        go.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
                        _instance = go.AddComponent<ProcGenDebugManager>();
                    }
                }
                return _instance;
            }
        }

        [System.Serializable]
        public class DebugBox
        {
            public Vector3 center;
            public Vector3 halfExtents;
            public Color color;
        }

        [System.Serializable]
        public class DebugPoint
        {
            public Vector3 position;
            public Color color;
            public float size;
        }

        // 调试数据存储
        public List<DebugPoint> candidatePoints = new List<DebugPoint>();
        public List<DebugBox> collisionBoxes = new List<DebugBox>();
        public List<DebugBox> avoidanceBounds = new List<DebugBox>();
        public List<DebugBox> destroyedObjects = new List<DebugBox>();

        // 显示开关
        public bool showCandidatePoints = true;
        public bool showCollisionBoxes = true;
        public bool showAvoidanceBounds = true;
        public bool showDestroyedObjects = true;

        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
            }
            else if (_instance != this)
            {
                DestroyImmediate(gameObject);
            }
        }

        private void OnDrawGizmos()
        {
            // 绘制候选点
            if (showCandidatePoints)
            {
                foreach (var point in candidatePoints)
                {
                    Gizmos.color = point.color;
                    Gizmos.DrawLine(point.position + Vector3.left * point.size, point.position + Vector3.right * point.size);
                    Gizmos.DrawLine(point.position + Vector3.forward * point.size, point.position + Vector3.back * point.size);
                    Gizmos.DrawLine(point.position + Vector3.down * point.size, point.position + Vector3.up * point.size);
                }
            }

            // 绘制碰撞盒
            if (showCollisionBoxes)
            {
                foreach (var box in collisionBoxes)
                {
                    Gizmos.color = box.color;
                    Gizmos.DrawWireCube(box.center, box.halfExtents * 2f);
                }
            }

            // 绘制避让范围
            if (showAvoidanceBounds)
            {
                foreach (var box in avoidanceBounds)
                {
                    Gizmos.color = box.color;
                    Gizmos.DrawWireCube(box.center, box.halfExtents * 2f);
                }
            }

            // 绘制被销毁物体
            if (showDestroyedObjects)
            {
                foreach (var box in destroyedObjects)
                {
                    Gizmos.color = box.color;
                    Gizmos.DrawWireCube(box.center, box.halfExtents * 2f);
                }
            }
        }

        /// <summary>
        /// 清除所有调试信息
        /// </summary>
        public void ClearAll()
        {
            candidatePoints.Clear();
            collisionBoxes.Clear();
            avoidanceBounds.Clear();
            destroyedObjects.Clear();
            SceneView.RepaintAll(); // 现在可以正常使用了
        }

        /// <summary>
        /// 添加候选点
        /// </summary>
        public void AddCandidatePoint(Vector3 position, Color color, float size = 0.2f)
        {
            candidatePoints.Add(new DebugPoint { position = position, color = color, size = size });
        }

        /// <summary>
        /// 添加碰撞盒
        /// </summary>
        public void AddCollisionBox(Vector3 center, Vector3 halfExtents, Color color)
        {
            collisionBoxes.Add(new DebugBox { center = center, halfExtents = halfExtents, color = color });
        }

        /// <summary>
        /// 添加避让范围
        /// </summary>
        public void AddAvoidanceBound(Vector3 center, Vector3 halfExtents, Color color)
        {
            avoidanceBounds.Add(new DebugBox { center = center, halfExtents = halfExtents, color = color });
        }

        /// <summary>
        /// 添加被销毁物体
        /// </summary>
        public void AddDestroyedObject(Vector3 center, Vector3 halfExtents, Color color)
        {
            destroyedObjects.Add(new DebugBox { center = center, halfExtents = halfExtents, color = color });
        }
    }
}
#endif