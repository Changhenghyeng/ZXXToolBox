using UnityEngine;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>V1 城墙模块配置。Prefab 的轴向约定会在工具窗口中显示。</summary>
    [CreateAssetMenu(
        fileName = "CityWallPrefabSet",
        menuName = "ZXX Tools/SLG City/Wall Prefab Set")]
    public sealed class CityWallPrefabSet : ScriptableObject
    {
        [SerializeField, Tooltip("直墙 Prefab。占用一个格子，枢轴在格子中心，连接方向沿 Local +X。")]
        private GameObject _straightPrefab;
        [SerializeField, Tooltip("转角 Prefab。占用一个格子，枢轴在格子中心，连接方向沿 Local +X / +Z。")]
        private GameObject _cornerPrefab;
        [SerializeField, Tooltip("城门 Prefab。枢轴在模块中心，城墙连接方向沿 Local +X，门洞穿行方向沿 Local +Z；Prefab 会放在连续 Gate 标记区域的几何中心，不会自动缩放。")]
        private GameObject _gatePrefab;
        [SerializeField, Tooltip("全部城墙相对布局平面的统一高度偏移。")]
        private float _heightOffset;
        [SerializeField, Tooltip("直墙模型轴向与 Local +X 不一致时使用的 Y 轴旋转修正。")]
        private float _straightYawOffset;
        [SerializeField, Tooltip("转角模型轴向与 +X / +Z 约定不一致时使用的 Y 轴旋转修正。")]
        private float _cornerYawOffset;
        [SerializeField, Tooltip("城门的城墙连接方向与 Local +X 不一致时使用的 Y 轴旋转修正。")]
        private float _gateYawOffset;
        [SerializeField, Tooltip("直墙在自身旋转空间中的位置修正。")]
        private Vector3 _straightLocalOffset;
        [SerializeField, Tooltip("转角在自身旋转空间中的位置修正。")]
        private Vector3 _cornerLocalOffset;
        [SerializeField, Tooltip("城门在自身旋转空间中的位置修正。")]
        private Vector3 _gateLocalOffset;

        public GameObject StraightPrefab => _straightPrefab;
        public GameObject CornerPrefab => _cornerPrefab;
        public GameObject GatePrefab => _gatePrefab;
        public float HeightOffset => _heightOffset;
        public float StraightYawOffset => _straightYawOffset;
        public float CornerYawOffset => _cornerYawOffset;
        public float GateYawOffset => _gateYawOffset;
        public Vector3 StraightLocalOffset => _straightLocalOffset;
        public Vector3 CornerLocalOffset => _cornerLocalOffset;
        public Vector3 GateLocalOffset => _gateLocalOffset;
    }
}
