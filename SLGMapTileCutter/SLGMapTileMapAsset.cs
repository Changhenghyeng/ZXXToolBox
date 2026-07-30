using System;
using System.Collections.Generic;
using UnityEngine;

namespace SLGMapTileCutter.Editor
{
    [Serializable]
    public struct SLGMapTileManualSegment
    {
        public Vector2 start;
        public Vector2 end;
    }

    /// <summary>
    /// The editable source of a generated tile map. Generated meshes are disposable;
    /// this asset keeps the line image, resolved regions and manual repairs.
    /// </summary>
    public sealed class SLGMapTileMapAsset : ScriptableObject
    {
        public const int CurrentVersion = 5;
        public int version;

        public Texture2D lineTexture;
        public Mesh sourceMesh;
        public Material[] sourceMaterials;

        public int longSideResolution;
        public int minimumRegionPixels;
        public int gridWidth;
        public int gridHeight;
        public int[] regionLabels;

        public float darkThreshold;
        public int closeRadius;
        public float skeletonSimplifyTolerance;
        public float skeletonSmoothing;
        public float tileGap;
        public Vector2 projectionOffset;
        public Vector2 projectionScale = Vector2.one;
        public float projectionRotation;

        public bool hideSourceOnGenerate = true;
        public bool colorPreview = true;
        public bool snapManualToSkeleton = true;
        public float manualSnapDistance = 0.025f;
        public float manualStrokeSmoothing = 0.35f;
        public float manualStrokeSimplifyTolerance = 1f;
        public int manualEraseRadius = 2;
        public float outlineWidth = 0.1f;
        public float outlineSoftness = 0.2f;
        public Color outlineColor = Color.black;
        public int outlineMapLongSide = 1024;
        public string outputFolder;

        public List<SLGMapTileManualSegment> manualSegments =
            new List<SLGMapTileManualSegment>();
        public List<SLGMapTileManualSegment> manualEraseSegments =
            new List<SLGMapTileManualSegment>();
    }
}
