using System;
using UnityEngine;

namespace ArtCode
{
    public class FogGridClickSystem : MonoBehaviour
    {
        public enum PivotType
        {
            BottomLeft,
            Center
        }

        private const byte FogValue = 255;
        private const byte ClearValue = 0;

        [Header("Grid")]
        [SerializeField] private int columns = 10;
        [SerializeField] private int rows = 8;
        [SerializeField] private Vector2 worldOrigin;
        [SerializeField] private Vector2 worldSize = new Vector2(10f, 8f);
        [SerializeField] private float gridHeight;
        [SerializeField] private PivotType pivotType = PivotType.BottomLeft;

        [Header("Mask")]
        [SerializeField] private int maskPixelsPerCell = 1;
        [SerializeField] private FilterMode maskFilterMode = FilterMode.Bilinear;
        [SerializeField] private TextureWrapMode maskWrapMode = TextureWrapMode.Clamp;
        [SerializeField] private Material maskPreviewMaterial;
        [SerializeField] private Renderer[] maskPreviewRenderers;
        [SerializeField] private string maskTextureProperty = "_MainTex";
        [SerializeField, HideInInspector] private byte[] maskRData;

        [Header("Interaction")]
        [SerializeField] private Camera inputCamera;
        [SerializeField] private LayerMask clickLayer = -1;
        [SerializeField] private bool handleMouseInput = true;

        private Texture2D maskTexture;
        private Color32[] maskPixels;

        public int Columns { get { return columns; } }
        public int Rows { get { return rows; } }
        public Vector2 WorldOrigin { get { return worldOrigin; } }
        public Vector2 WorldSize { get { return worldSize; } }
        public float CellWidth { get { return worldSize.x / Mathf.Max(1, columns); } }
        public float CellHeight { get { return worldSize.y / Mathf.Max(1, rows); } }

        public Texture2D MaskTexture
        {
            get
            {
                EnsureMask();
                return maskTexture;
            }
        }

        public byte[] MaskRData
        {
            get
            {
                EnsureMask();
                return maskRData;
            }
        }

        private void OnEnable()
        {
            EnsureMask();
            PushMaskTextureToMaterials();
        }

        private void OnValidate()
        {
            columns = Mathf.Max(1, columns);
            rows = Mathf.Max(1, rows);
            maskPixelsPerCell = Mathf.Max(1, maskPixelsPerCell);
            worldSize.x = Mathf.Max(0.001f, worldSize.x);
            worldSize.y = Mathf.Max(0.001f, worldSize.y);
            if (maskTexture != null)
            {
                int expectedWidth = columns * maskPixelsPerCell;
                int expectedHeight = rows * maskPixelsPerCell;
                if (maskTexture.width != expectedWidth || maskTexture.height != expectedHeight)
                {
                    DestroyMaskTexture();
                    maskPixels = null;
                    EnsureMask();
                    return;
                }

                maskTexture.filterMode = maskFilterMode;
                maskTexture.wrapMode = maskWrapMode;
                PushMaskTextureToMaterials();
            }
        }

        private void OnDestroy()
        {
            DestroyMaskTexture();
        }

        private void Update()
        {
            if (!handleMouseInput) return;
            if (Input.GetMouseButtonDown(0)) TryClick(Input.mousePosition, false);
            if (Input.GetMouseButtonDown(1)) TryClick(Input.mousePosition, true);
        }

        public void Configure(int columnCount, int rowCount, Vector2 origin, Vector2 size, float height, PivotType pivot)
        {
            columns = Mathf.Max(1, columnCount);
            rows = Mathf.Max(1, rowCount);
            worldOrigin = origin;
            worldSize = new Vector2(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y));
            gridHeight = height;
            pivotType = pivot;
            ResetMaskData(true);
        }

        public void SetMaskPreview(Material material, Renderer[] renderers)
        {
            maskPreviewMaterial = material;
            maskPreviewRenderers = renderers;
            PushMaskTextureToMaterials();
        }

        public void RebuildGrid()
        {
            ResetMask();
        }

        public void RebuildMaskTexture()
        {
            maskPixels = null;
            DestroyMaskTexture();
            EnsureMask();
            ApplyMask();
        }

        public void ResetMask()
        {
            ResetMaskData(true);
            ApplyMask();
        }

        public bool TryClick(Vector3 screenPosition, bool visible)
        {
            Camera cam = ResolveCamera();
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(screenPosition);
            RaycastHit[] hits = Physics.RaycastAll(ray, float.PositiveInfinity, clickLayer);
            if (hits == null || hits.Length == 0) return false;

            Array.Sort(hits, CompareHitDistance);
            for (int i = 0; i < hits.Length; i++)
            {
                int x;
                int y;
                if (!WorldToCell(hits[i].point, out x, out y)) continue;
                SetCellVisible(x, y, visible);
                return true;
            }

            return false;
        }

        public void SetCellVisible(int x, int y, bool visible)
        {
            if (!IsValidCell(x, y)) return;
            EnsureMask();

            int index = ToIndex(x, y);
            byte value = visible ? FogValue : ClearValue;
            maskRData[index] = value;
            FillCellPixels(x, y, value);
            ApplyMask();
        }

        public bool IsCellVisible(int x, int y)
        {
            if (!IsValidCell(x, y)) return false;
            EnsureMask();
            return maskRData[ToIndex(x, y)] == FogValue;
        }

        public bool IsCellRevealed(int x, int y)
        {
            return IsValidCell(x, y) && !IsCellVisible(x, y);
        }

        public void ShowAll()
        {
            SetAllVisible(true);
        }

        public void HideAll()
        {
            SetAllVisible(false);
        }

        public byte[] CopyMaskRData()
        {
            EnsureMask();
            byte[] copy = new byte[maskRData.Length];
            Array.Copy(maskRData, copy, maskRData.Length);
            return copy;
        }

        public void SetMaskRData(byte[] data)
        {
            EnsureMask();
            int count = Mathf.Min(data == null ? 0 : data.Length, maskRData.Length);
            for (int i = 0; i < maskRData.Length; i++)
            {
                maskRData[i] = i < count && data[i] == ClearValue ? ClearValue : FogValue;
            }

            RebuildMaskPixelsFromData();
            ApplyMask();
        }

        public bool WorldToCell(Vector3 worldPosition, out int x, out int y)
        {
            Vector2 bottomLeft = GetBottomLeft();
            float localX = worldPosition.x - bottomLeft.x;
            float localY = worldPosition.z - bottomLeft.y;
            x = Mathf.FloorToInt(localX / CellWidth);
            y = Mathf.FloorToInt(localY / CellHeight);
            return IsValidCell(x, y);
        }

        public Vector3 CellToWorld(int x, int y)
        {
            Vector2 bottomLeft = GetBottomLeft();
            return new Vector3(bottomLeft.x + (x + 0.5f) * CellWidth, gridHeight, bottomLeft.y + (y + 0.5f) * CellHeight);
        }

        private void SetAllVisible(bool visible)
        {
            EnsureMask();
            byte value = visible ? FogValue : ClearValue;
            for (int i = 0; i < maskRData.Length; i++)
            {
                maskRData[i] = value;
            }

            FillAllMaskPixels(value);
            ApplyMask();
        }

        private void EnsureMask()
        {
            columns = Mathf.Max(1, columns);
            rows = Mathf.Max(1, rows);
            maskPixelsPerCell = Mathf.Max(1, maskPixelsPerCell);
            int count = columns * rows;
            int textureWidth = columns * maskPixelsPerCell;
            int textureHeight = rows * maskPixelsPerCell;
            int texturePixelCount = textureWidth * textureHeight;

            if (maskRData == null || maskRData.Length != count)
            {
                maskRData = new byte[count];
                for (int i = 0; i < maskRData.Length; i++) maskRData[i] = FogValue;
            }

            if (maskPixels == null || maskPixels.Length != texturePixelCount)
            {
                maskPixels = new Color32[texturePixelCount];
                RebuildMaskPixelsFromData();
            }

            if (maskTexture == null || maskTexture.width != textureWidth || maskTexture.height != textureHeight)
            {
                DestroyMaskTexture();
                maskTexture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false, true)
                {
                    name = "FogGrid_RuntimeMask",
                    filterMode = maskFilterMode,
                    wrapMode = maskWrapMode
                };
                ApplyMask();
            }
        }

        private void ResetMaskData(bool fogVisible)
        {
            int count = Mathf.Max(1, columns) * Mathf.Max(1, rows);
            maskRData = new byte[count];
            maskPixels = null;
            byte value = fogVisible ? FogValue : ClearValue;
            for (int i = 0; i < count; i++)
            {
                maskRData[i] = value;
            }

            EnsureMask();
            FillAllMaskPixels(value);
        }

        private void RebuildMaskPixelsFromData()
        {
            if (maskRData == null || maskPixels == null) return;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    int index = ToIndex(x, y);
                    if (index < 0 || index >= maskRData.Length) continue;
                    byte value = maskRData[index] == ClearValue ? ClearValue : FogValue;
                    maskRData[index] = value;
                    FillCellPixels(x, y, value);
                }
            }
        }

        private void FillAllMaskPixels(byte value)
        {
            if (maskPixels == null) return;
            Color32 color = new Color32(value, ClearValue, ClearValue, FogValue);
            for (int i = 0; i < maskPixels.Length; i++) maskPixels[i] = color;
        }

        private void FillCellPixels(int cellX, int cellY, byte value)
        {
            if (maskPixels == null) return;
            int pixelsPerCell = Mathf.Max(1, maskPixelsPerCell);
            int textureWidth = Mathf.Max(1, columns) * pixelsPerCell;
            int startX = cellX * pixelsPerCell;
            int startY = cellY * pixelsPerCell;
            Color32 color = new Color32(value, ClearValue, ClearValue, FogValue);

            for (int y = 0; y < pixelsPerCell; y++)
            {
                int pixelY = startY + y;
                int rowStart = pixelY * textureWidth;
                for (int x = 0; x < pixelsPerCell; x++)
                {
                    int pixelX = startX + x;
                    int pixelIndex = rowStart + pixelX;
                    if (pixelIndex >= 0 && pixelIndex < maskPixels.Length) maskPixels[pixelIndex] = color;
                }
            }
        }

        private void ApplyMask()
        {
            if (maskTexture == null || maskPixels == null) return;
            maskTexture.SetPixels32(maskPixels);
            maskTexture.Apply(false, false);
            PushMaskTextureToMaterials();
        }

        private void PushMaskTextureToMaterials()
        {
            if (maskTexture == null) return;
            ApplyMaskTexture(maskPreviewMaterial);
            if (maskPreviewRenderers == null) return;

            for (int i = 0; i < maskPreviewRenderers.Length; i++)
            {
                Renderer renderer = maskPreviewRenderers[i];
                if (renderer == null) continue;
                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++) ApplyMaskTexture(materials[m]);
            }
        }

        private void ApplyMaskTexture(Material material)
        {
            if (material == null || string.IsNullOrEmpty(maskTextureProperty)) return;
            if (material.HasProperty(maskTextureProperty)) material.SetTexture(maskTextureProperty, maskTexture);
        }

        private void DestroyMaskTexture()
        {
            if (maskTexture == null) return;
            if (Application.isPlaying) Destroy(maskTexture);
            else DestroyImmediate(maskTexture);
            maskTexture = null;
        }

        private Vector2 GetBottomLeft()
        {
            if (pivotType == PivotType.Center)
            {
                return new Vector2(worldOrigin.x - worldSize.x * 0.5f, worldOrigin.y - worldSize.y * 0.5f);
            }

            return worldOrigin;
        }

        private Camera ResolveCamera()
        {
            if (inputCamera != null) return inputCamera;
            if (Camera.main != null) return Camera.main;

            Camera[] cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null && cameras[i].isActiveAndEnabled) return cameras[i];
            }

            return null;
        }

        private int ToIndex(int x, int y)
        {
            return y * columns + x;
        }

        private bool IsValidCell(int x, int y)
        {
            return x >= 0 && y >= 0 && x < columns && y < rows;
        }

        private static int CompareHitDistance(RaycastHit a, RaycastHit b)
        {
            return a.distance.CompareTo(b.distance);
        }
    }
}
