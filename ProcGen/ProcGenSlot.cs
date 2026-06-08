using UnityEngine;
using System.Collections.Generic;

namespace ProcGen
{
    public enum BoundsMode
    {
        Auto = 0,           // 用 prefab 所有 Renderer 合并 bounds
        SelectRenderers = 1, // 只用勾选的子 Renderer (仅单 prefab 时生效)
        Custom = 2          // 手填半尺寸
    }

    [System.Serializable]
    public class PrefabEntry
    {
        public GameObject prefab;
        [Tooltip("权重 (相对值)。例如 3:1 表示这个被抽中的概率是另一个的 3 倍")]
        public float weight = 1f;
    }

    [System.Serializable]
    public class ProcGenSlot
    {
        public string name = "New Slot";

        [Tooltip("此插槽包含的预制体列表 (按权重随机抽取)")]
        public List<PrefabEntry> prefabEntries = new List<PrefabEntry>();

        [Tooltip("是否启用此插槽。关闭后生成时跳过，不会删除配置")]
        public bool enabled = true;

        [Tooltip("生成数量上限")]
        public int count = 10;

        [Tooltip("缩放系数最小值")]
        public float scaleMin = 1f;

        [Tooltip("缩放系数最大值。每个实例在 [scaleMin, scaleMax] 之间随机取值")]
        public float scaleMax = 1f;

        [Tooltip("同插槽实例之间的最小间距 (米)")]
        public float minDistance = 1.5f;

        [Tooltip("优先级 (数值越大越优先生成，低优先级会被高优先级覆盖)")]
        public int priority = 0;

        [Tooltip("是否允许被更高优先级的物体销毁。关闭后，即使优先级更低，也不会被覆盖")]
        public bool canBeDestroyed = true;

        [Tooltip("碰撞盒计算方式")]
        public BoundsMode boundsMode = BoundsMode.Auto;

        [Tooltip("Custom 模式: 自定义占用 box 的半尺寸 (米)")]
        public Vector3 customHalfExtents = Vector3.zero;

        [Tooltip("SelectRenderers 模式: 用于碰撞计算的子 Renderer 相对路径列表")]
        public List<string> selectedRendererPaths = new List<string>();

        [Tooltip("Y 轴旋转角度 (度)。所有此插槽实例都按这个角度生成")]
        public float yRotation = 0f;

        [Tooltip("位置偏移 (本地空间，会跟随 Y 旋转)。Y 偏移用于贴地抬高")]
        public Vector3 positionOffset = Vector3.zero;

        [Tooltip("材质覆盖。留空则使用 prefab 原本的材质。填入后会覆盖所有子 Renderer 的全部材质")]
        public Material overrideMaterial = null;

        // UI 折叠状态
        [System.NonSerialized] public bool foldout = true;
        [System.NonSerialized] public bool rendererListFoldout = false;
    }
}