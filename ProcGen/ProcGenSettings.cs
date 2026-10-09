using UnityEngine;
using System.Collections.Generic;

namespace ProcGen
{
    [System.Serializable]
    public class ProcGenSettings
    {
        [Tooltip("生成目标 (含 MeshFilter 的 GameObject 或 Prefab)")]
        public GameObject target;

        [Tooltip("网格单元大小 (米)")]
        public float cellSize = 1.5f;

        [Tooltip("候选点位置随机度。0 = 严格网格中心，1 = 格子内任意位置")]
        [Range(0f, 1f)]
        public float jitter = 0.2f;

        [Tooltip("散布偏好。0 = 均匀散开，0.5 = 纯随机，1 = 抱团聚集")]
        [Range(0f, 1f)]
        public float spreadBias = 0f;

        [Tooltip("朝上判定阈值 (法线 y 分量大于此值才算朝上)")]
        [Range(0.5f, 1f)]
        public float upNormalThreshold = 0.9f;

        [Tooltip("随机种子。同一个种子产生相同结果")]
        public int seed = 12345;

        [Tooltip("边界内缩 (米)。距离 mesh 边缘小于此值的位置不生成。可避免建筑悬空在边缘外")]
        public float boundaryInset = 1.0f;

        [Tooltip("每个候选点放置失败的最大重试次数")]
        public int maxRetriesPerPoint = 5;

        [Header("场景物体避让")]
        [Tooltip("生成时需要避开的场景物体。拖入父物体后会自动包含它的所有子物体")]
        [System.NonSerialized]
        public List<GameObject> avoidanceObjects = new List<GameObject>();

        [HideInInspector]
        public List<string> avoidanceObjectGlobalIds = new List<string>();

        [Tooltip("在场景物体包围盒外额外保留的安全距离 (米)")]
        public float avoidancePadding = 0f;

        [Header("调试设置")]
        [Tooltip("开启调试可视化（Scene视图显示）")]
        public bool showDebugGizmos = false;

        [Tooltip("显示所有合法候选点（蓝色十字）")]
        public bool showCandidatePoints = true;

        [Tooltip("显示物体实际碰撞盒（绿色线框）")]
        public bool showCollisionBoxes = true;

        [Tooltip("显示包含最小间距的避让范围（黄色线框）")]
        public bool showAvoidanceBounds = true;

        [Tooltip("显示被销毁的低优先级物体（红色线框）")]
        public bool showDestroyedObjects = true;

        [Tooltip("调试线框持续时间（秒）")]
        public float debugGizmoDuration = 30f;

        [Tooltip("候选点颜色")]
        public Color candidatePointColor = new Color(0f, 0.5f, 1f, 1f);

        [Tooltip("碰撞盒颜色")]
        public Color collisionBoxColor = new Color(0f, 1f, 0f, 0.8f);

        [Tooltip("避让范围颜色")]
        public Color avoidanceColor = new Color(1f, 1f, 0f, 0.5f);

        [Tooltip("被销毁物体颜色")]
        public Color destroyedColor = new Color(1f, 0f, 0f, 0.8f);

        public List<ProcGenSlot> slots = new List<ProcGenSlot>();
    }

    /// <summary>
    /// ScriptableObject 包装器，用于持久化 ProcGen 配置。
    /// </summary>
    public class ProcGenSettingsAsset : ScriptableObject
    {
        public ProcGenSettings settings = new ProcGenSettings();
    }
}
