using UnityEngine;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>SLG 主城区域的持久化编辑数据，不包含任何运行时逻辑。</summary>
    [CreateAssetMenu(
        fileName = "CityLayoutData",
        menuName = "ZXX Tools/SLG City/City Layout Data")]
    public sealed class CityLayoutData : ScriptableObject
    {
        [SerializeField] private GridData _grid = new GridData();
        [SerializeField, Min(0.01f)] private float _cellSize = 4f;
        [SerializeField] private Vector3 _origin;
        [SerializeField] private TerrainData _terrainData;

        public GridData Grid => _grid;
        public TerrainData TerrainData
        {
            get => _terrainData;
            set => _terrainData = value;
        }
        public float CellSize
        {
            get => _cellSize;
            set => _cellSize = Mathf.Max(0.01f, value);
        }
        public Vector3 Origin
        {
            get => _origin;
            set => _origin = value;
        }

        public Vector3 GridPointToWorld(Vector2Int point)
        {
            return _origin + new Vector3(
                (point.x - _grid.Width * 0.5f) * _cellSize,
                0f,
                (point.y - _grid.Height * 0.5f) * _cellSize);
        }

        public Vector3 CellCenterToWorld(Vector2Int cell)
        {
            return _origin + new Vector3(
                (cell.x + 0.5f - _grid.Width * 0.5f) * _cellSize,
                0f,
                (cell.y + 0.5f - _grid.Height * 0.5f) * _cellSize);
        }

        public bool TryWorldToNearestGridPoint(Vector3 world, out Vector2Int point)
        {
            Vector3 local = world - _origin;
            point = new Vector2Int(
                Mathf.RoundToInt(local.x / _cellSize + _grid.Width * 0.5f),
                Mathf.RoundToInt(local.z / _cellSize + _grid.Height * 0.5f));
            return point.x >= 0
                && point.y >= 0
                && point.x <= _grid.Width
                && point.y <= _grid.Height;
        }

        public bool TrySnapFootprintCenter(
            Vector3 worldCenter,
            Vector2Int footprint,
            out Vector3 snappedCenter)
        {
            footprint.x = Mathf.Max(1, footprint.x);
            footprint.y = Mathf.Max(1, footprint.y);
            snappedCenter = worldCenter;
            if (footprint.x > _grid.Width || footprint.y > _grid.Height) return false;

            Vector3 local = worldCenter - _origin;
            float gridX = local.x / _cellSize + _grid.Width * 0.5f;
            float gridY = local.z / _cellSize + _grid.Height * 0.5f;
            float snappedX = SnapFootprintAxis(gridX, footprint.x, _grid.Width);
            float snappedY = SnapFootprintAxis(gridY, footprint.y, _grid.Height);
            snappedCenter.x = _origin.x + (snappedX - _grid.Width * 0.5f) * _cellSize;
            snappedCenter.z = _origin.z + (snappedY - _grid.Height * 0.5f) * _cellSize;
            return true;
        }

        public bool TryWorldToCell(Vector3 world, out Vector2Int cell)
        {
            Vector3 local = world - _origin;
            cell = new Vector2Int(
                Mathf.FloorToInt(local.x / _cellSize + _grid.Width * 0.5f),
                Mathf.FloorToInt(local.z / _cellSize + _grid.Height * 0.5f));
            return _grid.Contains(cell);
        }

        private static float SnapFootprintAxis(float value, int footprint, int gridSize)
        {
            // 偶数占格的中心在线交点；奇数占格的中心在格子中心。
            float snapped = (footprint & 1) == 0
                ? Mathf.Round(value)
                : Mathf.Round(value - 0.5f) + 0.5f;
            float halfSize = footprint * 0.5f;
            return Mathf.Clamp(snapped, halfSize, gridSize - halfSize);
        }

        private void OnEnable()
        {
            if (_grid == null) _grid = new GridData();
            _grid.EnsureValid();
            _cellSize = Mathf.Max(0.01f, _cellSize);
        }

        private void OnValidate()
        {
            if (_grid == null) _grid = new GridData();
            _grid.EnsureValid();
            _cellSize = Mathf.Max(0.01f, _cellSize);
        }
    }
}
