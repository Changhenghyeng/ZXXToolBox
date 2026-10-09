using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZXXTools.SLGCityEditor
{
    /// <summary>
    /// 可序列化的二维占用网格。使用一维数组，避免 Unity 对多维数组和 Dictionary 的序列化限制。
    /// </summary>
    [Serializable]
    public sealed class GridData
    {
        [SerializeField, Min(1)] private int _width = 24;
        [SerializeField, Min(1)] private int _height = 24;
        [SerializeField] private List<byte> _cells = new List<byte>();
        [SerializeField] private List<byte> _gates = new List<byte>();

        public int Width => _width;
        public int Height => _height;
        public int CellCount => _width * _height;

        public void EnsureValid()
        {
            _width = Mathf.Clamp(_width, 1, 256);
            _height = Mathf.Clamp(_height, 1, 256);
            EnsureStorage();
        }

        public bool Contains(Vector2Int cell)
        {
            return cell.x >= 0 && cell.y >= 0 && cell.x < _width && cell.y < _height;
        }

        public bool IsOccupied(Vector2Int cell)
        {
            if (!Contains(cell)) return false;
            EnsureStorage();
            return _cells[ToIndex(cell)] != 0;
        }

        public bool SetOccupied(Vector2Int cell, bool occupied)
        {
            if (!Contains(cell)) return false;
            EnsureStorage();
            int index = ToIndex(cell);
            byte value = occupied ? (byte)1 : (byte)0;
            if (_cells[index] == value) return false;
            _cells[index] = value;
            if (!occupied) _gates[index] = 0;
            return true;
        }

        public bool IsGate(Vector2Int cell)
        {
            if (!Contains(cell)) return false;
            EnsureStorage();
            return _gates[ToIndex(cell)] != 0;
        }

        public bool SetGate(Vector2Int cell, bool gate)
        {
            if (!Contains(cell) || !IsOccupied(cell)) return false;
            EnsureStorage();
            int index = ToIndex(cell);
            byte value = gate ? (byte)1 : (byte)0;
            if (_gates[index] == value) return false;
            _gates[index] = value;
            return true;
        }

        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 1, 256);
            height = Mathf.Clamp(height, 1, 256);
            EnsureStorage();

            var resized = new List<byte>(width * height);
            var resizedGates = new List<byte>(width * height);
            for (int i = 0; i < width * height; i++) resized.Add(0);
            for (int i = 0; i < width * height; i++) resizedGates.Add(0);

            int copyWidth = Mathf.Min(_width, width);
            int copyHeight = Mathf.Min(_height, height);
            for (int y = 0; y < copyHeight; y++)
            {
                for (int x = 0; x < copyWidth; x++)
                {
                    int oldIndex = y * _width + x;
                    int newIndex = y * width + x;
                    resized[newIndex] = _cells[oldIndex];
                    resizedGates[newIndex] = _gates[oldIndex];
                }
            }

            _width = width;
            _height = height;
            _cells = resized;
            _gates = resizedGates;
        }

        public void Clear()
        {
            EnsureStorage();
            for (int i = 0; i < _cells.Count; i++)
            {
                _cells[i] = 0;
                _gates[i] = 0;
            }
        }

        public int CountOccupied()
        {
            EnsureStorage();
            int count = 0;
            for (int i = 0; i < _cells.Count; i++)
            {
                if (_cells[i] != 0) count++;
            }
            return count;
        }

        public int CountGates()
        {
            EnsureStorage();
            int count = 0;
            for (int i = 0; i < _gates.Count; i++)
            {
                if (_gates[i] != 0) count++;
            }
            return count;
        }

        public void ClearGates()
        {
            EnsureStorage();
            for (int i = 0; i < _gates.Count; i++) _gates[i] = 0;
        }

        private int ToIndex(Vector2Int cell)
        {
            return cell.y * _width + cell.x;
        }

        private void EnsureStorage()
        {
            if (_cells == null) _cells = new List<byte>();
            if (_gates == null) _gates = new List<byte>();
            int required = _width * _height;
            if (_cells.Count > required) _cells.RemoveRange(required, _cells.Count - required);
            if (_gates.Count > required) _gates.RemoveRange(required, _gates.Count - required);
            while (_cells.Count < required) _cells.Add(0);
            while (_gates.Count < required) _gates.Add(0);
        }
    }
}
