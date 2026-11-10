using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace ZXXTools.PathBuilder
{
    public enum PathBuildMode
    {
        Mountain,
        River
    }

    public enum PathTangentMode
    {
        Continuous,
        Mirrored,
        Broken,
        Auto,
        Linear
    }

    public enum RiverHeightMode
    {
        SplineHeight,
        FollowSurface
    }

    public enum MountainHeightMode
    {
        SplineHeight,
        FollowSurface
    }

    public enum RibbonPathPreset
    {
        River,
        NaturalRoad,
        RegularRoad
    }

    public enum PathOutputType
    {
        Scatter,
        ProfileExtrude,
        MeshDeform,
        PrefabPlace
    }

    public enum PathForwardAxis
    {
        X,
        Z
    }

    public enum PathPivotAlignment
    {
        SourcePivot,
        BoundsCenter,
        BoundsBottomCenter
    }

    public enum PathMeshPartMode
    {
        Deform,
        Place,
        Ignore
    }

    public enum PathCornerHandling
    {
        Off,
        CompressInner,
        [InspectorName("保宽裁切")]
        TrimInner
    }

    public enum MountainSourceType
    {
        Prefab,
        SpriteCard,
        TextureCard
    }

    public enum MountainCardPivot
    {
        BottomCenter,
        SpritePivot
    }

    public enum MountainCardSortingMode
    {
        AccurateBackToFront,
        FastByTexture
    }

    public enum MountainBakeOutput
    {
        GroundDecal,
        CameraFacingCards
    }

    public enum MountainFlattenTarget
    {
        WorldPlane,
        SurfaceColliders
    }

    [Serializable]
    public sealed class PathKnot
    {
        public Vector3 position;
        public Vector3 tangentIn = Vector3.left * 2f;
        public Vector3 tangentOut = Vector3.right * 2f;
        public PathTangentMode tangentMode = PathTangentMode.Continuous;
        [HideInInspector]
        public bool brokenTangents;
        [Min(0.01f)] public float width = 4f;
        [Min(0.01f)] public float meshWidthScale = 1f;

        public PathKnot()
        {
        }

        public PathKnot(Vector3 position)
        {
            this.position = position;
        }

        public PathKnot Clone()
        {
            return new PathKnot
            {
                position = position,
                tangentIn = tangentIn,
                tangentOut = tangentOut,
                tangentMode = tangentMode,
                brokenTangents = brokenTangents,
                width = width,
                meshWidthScale = meshWidthScale
            };
        }
    }

    [Serializable]
    public sealed class MountainSourceEntry
    {
        public MountainSourceType sourceType = MountainSourceType.Prefab;
        public GameObject prefab;
        public Sprite sprite;
        public Texture2D texture;
        [Min(0f)] public float weight = 1f;
    }

    [Serializable]
    public sealed class MountainInstanceOverride
    {
        public int placementIndex;
        public bool relativeTransform;
        public bool overridePosition;
        public Vector3 localPosition;
        public bool overrideScale;
        public Vector3 scale = Vector3.one;
    }

    // Kept only so scenes created before the unified source list can migrate
    // their serialized prefab entries without losing data.
    [Serializable]
    public sealed class MountainPrefabEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;
    }

    [Serializable]
    public sealed class PathEdgePrefabEntry
    {
        public GameObject prefab;
        [Min(0f)] public float weight = 1f;
    }

    [Serializable]
    public sealed class PathMeshPartSetting
    {
        public string transformPath;
        public PathMeshPartMode mode = PathMeshPartMode.Deform;
        [Range(1, 32)] public int segments = 1;
    }

    [Serializable]
    public sealed class PathOutputLayer
    {
        public string name = "Output";
        public bool enabled = true;
        public PathOutputType type = PathOutputType.ProfileExtrude;

        [Header("Mesh Deform")]
        public GameObject sourcePrefab;
        [HideInInspector] public string sourceSceneObjectGlobalId;
        public PathForwardAxis forwardAxis = PathForwardAxis.Z;
        [HideInInspector] public string segmentMeshPath;
        public List<PathMeshPartSetting> meshParts =
            new List<PathMeshPartSetting>();
        [Min(0f)] public float startSegmentOffset;
        [Min(0f)] public float endSegmentOffset;
        public bool repeat = true;
        public bool fitLastSegment = true;
        public bool useSplineWidth;
        [FormerlySerializedAs("flattenAboveWorldHeight")]
        public bool flattenFromCamera;
        [FormerlySerializedAs("flattenWorldHeight")]
        public float flattenPlaneHeight;
        public float spacing;
        public PathCornerHandling cornerHandling =
            PathCornerHandling.CompressInner;
        [Range(0.5f, 0.95f)] public float cornerSafety = 0.85f;
        public bool useRootPosition;
        public PathPivotAlignment pivotAlignment =
            PathPivotAlignment.BoundsBottomCenter;
        public Vector3 positionOffset;
        public Vector3 rotationOffset;
        public Vector3 scale = Vector3.one;
        public bool updateCollider = true;
    }

    public sealed class PathSpline : ScriptableObject
    {
        public const string DefaultGeneratedAssetFolder =
            "Assets/Generated/PathBuilder/Baked/Models";
        public const string DefaultPrefabOutputFolder =
            "Assets/Generated/PathBuilder/Baked/Prefabs";

        [NonSerialized]
        private Transform anchor;

        [NonSerialized]
        private Camera mountainFacingCamera;

        [SerializeField, HideInInspector]
        private string sessionId;

        [SerializeField, HideInInspector]
        private string mountainFacingCameraGlobalId;

        [Header("Spline")]
        public PathBuildMode mode = PathBuildMode.Mountain;
        public bool closed;
        public bool autoRebuild = true;
        [Range(4, 64)] public int samplesPerCurve = 24;
        public List<PathKnot> knots = new List<PathKnot>();

        [Header("Outputs")]
        public List<PathOutputLayer> outputs = new List<PathOutputLayer>();
        [SerializeField, HideInInspector] private int outputStackVersion;

        [Header("Surface")]
        public bool projectToSurface;
        public LayerMask surfaceMask = ~0;
        [Min(0.1f)] public float rayHeight = 500f;
        public float surfaceOffset = 0.02f;

        [Header("Mountain")]
        public MountainDecalSet mountainDecalSet;
        public List<MountainSourceEntry> mountainSources =
            new List<MountainSourceEntry>();
        [SerializeField, HideInInspector]
        private List<MountainInstanceOverride> mountainInstanceOverrides =
            new List<MountainInstanceOverride>();
        [SerializeField, HideInInspector]
        private List<MountainPrefabEntry> mountainPrefabs =
            new List<MountainPrefabEntry>();
        public int randomSeed = 12345;
        [Min(0.05f)] public float mountainSpacing = 3f;
        [Range(0f, 1f)] public float mountainDistributionUniformity = 0f;
        [SerializeField, HideInInspector]
        private float mountainDistributionRandomness = 1f;
        [SerializeField, HideInInspector]
        private int mountainDistributionVersion;
        [HideInInspector]
        public Vector2 spacingRange = new Vector2(2f, 4f);
        [Min(0f)] public float brushWidth = 2f;
        [HideInInspector, Range(0f, 1f)] public float density = 1f;
        public bool uniformScale = true;
        public Vector2 uniformScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 horizontalScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 verticalScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 yawRange = new Vector2(0f, 360f);
        public bool alignToSpline;
        public bool useSplineWidth = true;
        public MountainHeightMode mountainHeightMode =
            MountainHeightMode.SplineHeight;

        [Header("Mountain Cards")]
        public Material mountainCardMaterial;
        [Min(0.01f)] public float mountainCardHeight = 8f;
        public MountainCardPivot mountainCardPivot =
            MountainCardPivot.BottomCenter;
        public bool mountainCardRandomFlip = true;
        public bool mountainCardFlipX;
        public bool mountainCardFlipY;
        public bool mountainCardRandomFlipY;
        public Color mountainCardColor = Color.white;
        public MountainCardSortingMode mountainCardSortingMode =
            MountainCardSortingMode.AccurateBackToFront;
        public int mountainCardSortingLayerId;
        public int mountainCardSortingOrder;
        public int mountainCardRenderQueue = 3000;

        [Header("Mountain Bake")]
        public MountainBakeOutput mountainBakeOutput =
            MountainBakeOutput.GroundDecal;
        public bool mountainFlattenPreview;
        public bool keepMountainAuthoringAfterBake = true;
        public MountainFlattenTarget mountainFlattenTarget =
            MountainFlattenTarget.WorldPlane;
        public float mountainWorldPlaneHeight;
        public LayerMask mountainDecalSurfaceMask = ~0;
        [Min(0.1f)] public float mountainDecalProjectionDistance = 2000f;
        [HideInInspector, Min(0.1f)]
        public float mountainDecalCellSize = 2f;
        [Min(0f)] public float mountainDecalSurfaceOffset = 0.02f;

        [Header("Output Paths")]
        public string generatedAssetOutputFolder =
            DefaultGeneratedAssetFolder;
        public string prefabOutputFolder = DefaultPrefabOutputFolder;
        [Tooltip("面片山共享材质保存目录；留空则跟随 Asset Folder 的 Materials 子目录")]
        public string mountainMaterialOutputFolder;

        [Header("River")]
        public RibbonPathPreset ribbonPreset = RibbonPathPreset.River;
        public Material riverMaterial;
        public RiverHeightMode riverHeightMode = RiverHeightMode.SplineHeight;
        [Min(0.05f)] public float riverSegmentLength = 0.75f;
        [Min(0.01f)] public float riverUvLength = 1f;
        [FormerlySerializedAs("uvMetersPerTile")]
        [HideInInspector]
        [Min(0.01f)] public float riverUvRepeatLength = 4f;
        public bool generateCollider;

        [Header("Ribbon Edge Shape")]
        public bool edgeNoiseEnabled;
        public int edgeNoiseSeed = 24680;
        [Min(0f)] public float leftEdgeNoiseAmplitude = 0.25f;
        [Min(0f)] public float rightEdgeNoiseAmplitude = 0.25f;
        [Min(0.1f)] public float edgeNoiseWavelength = 8f;

        [Header("Ribbon Edge Objects")]
        public bool generateEdgeObjects;
        public bool generateLeftEdge = true;
        public bool generateRightEdge = true;
        public List<PathEdgePrefabEntry> edgePrefabs =
            new List<PathEdgePrefabEntry>();
        public int edgeObjectSeed = 13579;
        public Vector2 edgeObjectSpacingRange = new Vector2(2f, 4f);
        [Range(0f, 1f)] public float edgeObjectDensity = 1f;
        public Vector2 edgeObjectScaleRange = new Vector2(0.85f, 1.15f);
        public Vector2 edgeObjectYawRange = new Vector2(-20f, 20f);
        public float edgeObjectOutwardOffset;
        public float edgeObjectHeightOffset;
        public bool edgeObjectsAlignToPath = true;
        public bool edgeObjectsProjectToSurface = true;

        [NonSerialized]
        private Transform generatedRoot;

        public string SessionId
        {
            get { return sessionId; }
        }

        public Transform GeneratedRoot
        {
            get { return generatedRoot; }
            set { generatedRoot = value; }
        }

        public Transform Anchor
        {
            get { return anchor; }
            set { anchor = value; }
        }

        public Camera MountainFacingCamera
        {
            get { return mountainFacingCamera; }
            set { mountainFacingCamera = value; }
        }

        public string MountainFacingCameraGlobalId
        {
            get { return mountainFacingCameraGlobalId; }
            set { mountainFacingCameraGlobalId = value; }
        }

        public string EnsureSessionId()
        {
            if (string.IsNullOrEmpty(sessionId))
                sessionId = Guid.NewGuid().ToString("N");
            return sessionId;
        }

        internal string RegenerateSessionId()
        {
            sessionId = Guid.NewGuid().ToString("N");
            return sessionId;
        }

        public int SegmentCount
        {
            get
            {
                if (knots == null || knots.Count < 2)
                    return 0;
                return closed ? knots.Count : knots.Count - 1;
            }
        }

        private void OnValidate()
        {
            samplesPerCurve = Mathf.Clamp(samplesPerCurve, 4, 64);
            rayHeight = Mathf.Max(0.1f, rayHeight);
            riverSegmentLength = Mathf.Max(0.05f, riverSegmentLength);
            riverUvLength = Mathf.Max(0.01f, riverUvLength);
            riverUvRepeatLength = Mathf.Max(0.01f, riverUvRepeatLength);
            leftEdgeNoiseAmplitude = Mathf.Max(0f, leftEdgeNoiseAmplitude);
            rightEdgeNoiseAmplitude = Mathf.Max(0f, rightEdgeNoiseAmplitude);
            edgeNoiseWavelength = Mathf.Max(0.1f, edgeNoiseWavelength);
            edgeObjectDensity = Mathf.Clamp01(edgeObjectDensity);
            edgeObjectSpacingRange =
                OrderedPositiveRange(edgeObjectSpacingRange, 0.05f);
            edgeObjectScaleRange =
                OrderedPositiveRange(edgeObjectScaleRange, 0.01f);
            mountainCardHeight = Mathf.Max(0.01f, mountainCardHeight);
            mountainCardSortingOrder = Mathf.Clamp(
                mountainCardSortingOrder,
                short.MinValue,
                short.MaxValue);
            mountainCardRenderQueue = Mathf.Clamp(
                mountainCardRenderQueue,
                2501,
                3999);
            mountainDecalProjectionDistance = Mathf.Max(
                0.1f,
                mountainDecalProjectionDistance);
            mountainDecalCellSize = Mathf.Max(
                0.1f,
                mountainDecalCellSize);
            mountainDecalSurfaceOffset = Mathf.Max(
                0f,
                mountainDecalSurfaceOffset);
            brushWidth = Mathf.Max(0f, brushWidth);
            density = Mathf.Clamp01(density);
            spacingRange = OrderedPositiveRange(spacingRange, 0.05f);
            MigrateMountainDistribution();
            mountainSpacing = Mathf.Max(0.05f, mountainSpacing);
            mountainDistributionUniformity = Mathf.Clamp01(
                mountainDistributionUniformity);
            uniformScaleRange = OrderedPositiveRange(uniformScaleRange, 0.01f);
            horizontalScaleRange = OrderedPositiveRange(horizontalScaleRange, 0.01f);
            verticalScaleRange = OrderedPositiveRange(verticalScaleRange, 0.01f);

            if (knots == null)
                knots = new List<PathKnot>();
            if (edgePrefabs == null)
                edgePrefabs = new List<PathEdgePrefabEntry>();
            if (mountainSources == null)
                mountainSources = new List<MountainSourceEntry>();
            EnsureOutputStack();
            MigrateLegacyMountainSources();

            for (int i = 0; i < knots.Count; i++)
            {
                if (knots[i] == null)
                    knots[i] = new PathKnot();
                MigrateLegacyTangentMode(knots[i]);
                knots[i].width = Mathf.Max(0.01f, knots[i].width);
                knots[i].meshWidthScale = Mathf.Max(0.01f, knots[i].meshWidthScale);
            }
        }

        public void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(generatedAssetOutputFolder))
            {
                generatedAssetOutputFolder = DefaultGeneratedAssetFolder;
            }
            if (string.IsNullOrWhiteSpace(prefabOutputFolder))
                prefabOutputFolder = DefaultPrefabOutputFolder;
            if (knots == null)
                knots = new List<PathKnot>();
            if (edgePrefabs == null)
                edgePrefabs = new List<PathEdgePrefabEntry>();
            if (mountainSources == null)
                mountainSources = new List<MountainSourceEntry>();
            if (mountainInstanceOverrides == null)
            {
                mountainInstanceOverrides =
                    new List<MountainInstanceOverride>();
            }
            EnsureOutputStack();
            MigrateLegacyMountainSources();
            spacingRange = OrderedPositiveRange(spacingRange, 0.05f);
            MigrateMountainDistribution();
            mountainSpacing = Mathf.Max(0.05f, mountainSpacing);
            mountainDistributionUniformity = Mathf.Clamp01(
                mountainDistributionUniformity);
            for (int i = 0; i < knots.Count; i++)
            {
                if (knots[i] == null)
                    knots[i] = new PathKnot();
                MigrateLegacyTangentMode(knots[i]);
            }
            riverUvLength = Mathf.Max(0.01f, riverUvLength);
            edgeNoiseWavelength = Mathf.Max(0.1f, edgeNoiseWavelength);
            edgeObjectSpacingRange =
                OrderedPositiveRange(edgeObjectSpacingRange, 0.05f);
            edgeObjectScaleRange =
                OrderedPositiveRange(edgeObjectScaleRange, 0.01f);
            mountainDecalProjectionDistance = Mathf.Max(
                0.1f,
                mountainDecalProjectionDistance);
            mountainDecalCellSize = Mathf.Max(
                0.1f,
                mountainDecalCellSize);
            mountainDecalSurfaceOffset = Mathf.Max(
                0f,
                mountainDecalSurfaceOffset);
        }

        internal bool TryGetMountainInstanceOverride(
            int placementIndex,
            out MountainInstanceOverride result)
        {
            if (mountainInstanceOverrides != null)
            {
                for (int i = 0; i < mountainInstanceOverrides.Count; i++)
                {
                    MountainInstanceOverride item =
                        mountainInstanceOverrides[i];
                    if (item != null && item.placementIndex == placementIndex)
                    {
                        result = item;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }

        internal void ApplyMountainInstanceTransformDelta(
            int placementIndex,
            bool setPosition,
            Vector3 localPositionDelta,
            bool setScale,
            Vector3 scaleRatio)
        {
            if (mountainInstanceOverrides == null)
            {
                mountainInstanceOverrides =
                    new List<MountainInstanceOverride>();
            }

            for (int i = 0; i < mountainInstanceOverrides.Count; i++)
            {
                MountainInstanceOverride item = mountainInstanceOverrides[i];
                if (item != null && item.placementIndex == placementIndex)
                {
                    item.relativeTransform = true;
                    if (setPosition)
                    {
                        item.overridePosition = true;
                        item.localPosition += localPositionDelta;
                    }
                    if (setScale)
                    {
                        item.overrideScale = true;
                        item.scale = Vector3.Scale(item.scale, scaleRatio);
                    }
                    return;
                }
            }

            mountainInstanceOverrides.Add(new MountainInstanceOverride
            {
                placementIndex = placementIndex,
                relativeTransform = true,
                overridePosition = setPosition,
                localPosition = localPositionDelta,
                overrideScale = setScale,
                scale = setScale ? scaleRatio : Vector3.one
            });
        }

        internal bool HasLegacyMountainInstanceOverrides()
        {
            if (mountainInstanceOverrides == null)
                return false;
            for (int i = 0; i < mountainInstanceOverrides.Count; i++)
            {
                MountainInstanceOverride item = mountainInstanceOverrides[i];
                if (item != null && !item.relativeTransform)
                    return true;
            }
            return false;
        }

        internal void ClearMountainInstanceOverrides()
        {
            if (mountainInstanceOverrides == null)
            {
                mountainInstanceOverrides =
                    new List<MountainInstanceOverride>();
                return;
            }
            mountainInstanceOverrides.Clear();
        }

        public void EnsureOutputStack()
        {
            if (outputs == null)
                outputs = new List<PathOutputLayer>();
            if (outputStackVersion <= 0 && outputs.Count == 0)
            {
                outputs.Add(new PathOutputLayer
                {
                    name = mode == PathBuildMode.Mountain
                        ? "Mountain Scatter"
                        : "River / Road Surface",
                    type = mode == PathBuildMode.Mountain
                        ? PathOutputType.Scatter
                        : PathOutputType.ProfileExtrude
                });
            }
            for (int i = 0; i < outputs.Count; i++)
            {
                if (outputs[i] == null)
                    outputs[i] = new PathOutputLayer();
                if (outputs[i].meshParts == null)
                {
                    outputs[i].meshParts =
                        new List<PathMeshPartSetting>();
                }
                for (int partIndex = 0;
                    partIndex < outputs[i].meshParts.Count;
                    partIndex++)
                {
                    PathMeshPartSetting part = outputs[i].meshParts[partIndex];
                    if (part != null)
                        part.segments = Mathf.Clamp(part.segments, 1, 32);
                }
                if (outputStackVersion < 3
                    && outputs[i].type == PathOutputType.MeshDeform)
                {
                    outputs[i].pivotAlignment =
                        PathPivotAlignment.BoundsBottomCenter;
                }
                if (outputStackVersion < 4
                    && outputs[i].type == PathOutputType.MeshDeform)
                {
                    outputs[i].cornerHandling =
                        PathCornerHandling.CompressInner;
                    outputs[i].cornerSafety = 0.85f;
                }
                outputs[i].startSegmentOffset = Mathf.Max(
                    0f, outputs[i].startSegmentOffset);
                outputs[i].endSegmentOffset = Mathf.Max(
                    0f, outputs[i].endSegmentOffset);
                outputs[i].cornerSafety = Mathf.Clamp(
                    outputs[i].cornerSafety, 0.5f, 0.95f);
                outputs[i].scale.x = Mathf.Max(0.0001f, outputs[i].scale.x);
                outputs[i].scale.y = Mathf.Max(0.0001f, outputs[i].scale.y);
                outputs[i].scale.z = Mathf.Max(0.0001f, outputs[i].scale.z);
            }
            outputStackVersion = 4;
        }

        private void MigrateMountainDistribution()
        {
            if (mountainDistributionVersion >= 2)
                return;

            if (mountainDistributionVersion < 1)
            {
                float minimum = Mathf.Max(0.05f, spacingRange.x);
                float maximum = Mathf.Max(minimum, spacingRange.y);
                float average = (minimum + maximum) * 0.5f;
                float legacyDensity = Mathf.Clamp(density, 0.01f, 1f);
                mountainSpacing = average / legacyDensity;
                mountainDistributionRandomness = Mathf.Clamp01(
                    (maximum - minimum) / Mathf.Max(0.05f, average));
            }

            mountainDistributionUniformity =
                1f - Mathf.Clamp01(mountainDistributionRandomness);
            mountainDistributionVersion = 2;
        }

        private void MigrateLegacyMountainSources()
        {
            if (mountainPrefabs == null || mountainPrefabs.Count == 0)
                return;

            for (int i = 0; i < mountainPrefabs.Count; i++)
            {
                MountainPrefabEntry legacy = mountainPrefabs[i];
                if (legacy == null || legacy.prefab == null)
                    continue;

                mountainSources.Add(new MountainSourceEntry
                {
                    sourceType = MountainSourceType.Prefab,
                    prefab = legacy.prefab,
                    weight = legacy.weight
                });
            }

            mountainPrefabs.Clear();
        }

        public void ApplyRibbonPreset()
        {
            switch (ribbonPreset)
            {
                case RibbonPathPreset.NaturalRoad:
                    edgeNoiseEnabled = true;
                    leftEdgeNoiseAmplitude = 0.15f;
                    rightEdgeNoiseAmplitude = 0.15f;
                    edgeNoiseWavelength = 6f;
                    break;
                case RibbonPathPreset.RegularRoad:
                    edgeNoiseEnabled = false;
                    leftEdgeNoiseAmplitude = 0f;
                    rightEdgeNoiseAmplitude = 0f;
                    break;
                default:
                    edgeNoiseEnabled = true;
                    leftEdgeNoiseAmplitude = 0.25f;
                    rightEdgeNoiseAmplitude = 0.25f;
                    edgeNoiseWavelength = 8f;
                    break;
            }
        }

        public int AppendKnot(Vector3 localPosition)
        {
            if (knots == null)
                knots = new List<PathKnot>();

            PathKnot knot = new PathKnot(localPosition);
            if (knots.Count > 0)
            {
                PathKnot previous = knots[knots.Count - 1];
                Vector3 direction = localPosition - previous.position;
                float length = direction.magnitude;
                if (length > 0.001f)
                {
                    Vector3 tangent = direction.normalized * Mathf.Max(0.25f, length / 3f);
                    previous.tangentOut = tangent;
                    ApplyOppositeTangent(previous, false);
                    knot.tangentIn = -tangent;
                    knot.tangentOut = tangent;
                    knot.tangentMode = PathTangentMode.Mirrored;
                }
                knot.width = previous.width;
                knot.meshWidthScale = previous.meshWidthScale;
            }

            knots.Add(knot);
            return knots.Count - 1;
        }

        public int AppendPenKnot(Vector3 localPosition)
        {
            if (knots == null)
                knots = new List<PathKnot>();

            PathKnot knot = new PathKnot(localPosition)
            {
                tangentIn = Vector3.zero,
                tangentOut = Vector3.zero,
                tangentMode = PathTangentMode.Linear
            };
            if (knots.Count > 0)
            {
                knot.width = knots[knots.Count - 1].width;
                knot.meshWidthScale = knots[knots.Count - 1].meshWidthScale;
            }

            knots.Add(knot);
            return knots.Count - 1;
        }

        public int InsertAfter(int index)
        {
            EnsureDefaults();
            if (knots.Count == 0)
                return AppendKnot(Vector3.zero);
            index = Mathf.Clamp(index, 0, knots.Count - 1);

            int nextIndex = index + 1;
            if (nextIndex >= knots.Count)
            {
                if (!closed)
                {
                    Vector3 direction = knots[index].tangentOut;
                    if (direction.sqrMagnitude < 0.001f)
                        direction = Vector3.right * 3f;
                    return AppendKnot(knots[index].position + direction.normalized * 5f);
                }
                nextIndex = 0;
            }

            return InsertKnotOnSegment(index, 0.5f);
        }

        public int InsertKnotOnSegment(int segmentIndex, float t)
        {
            EnsureDefaults();
            if (SegmentCount == 0)
                return AppendKnot(Vector3.zero);

            segmentIndex = Mathf.Clamp(segmentIndex, 0, SegmentCount - 1);
            int nextIndex = (segmentIndex + 1) % knots.Count;
            t = Mathf.Clamp(t, 0.001f, 0.999f);

            PathKnot start = knots[segmentIndex];
            PathKnot end = knots[nextIndex];
            Vector3 p0 = start.position;
            Vector3 p1 = start.position + start.tangentOut;
            Vector3 p2 = end.position + end.tangentIn;
            Vector3 p3 = end.position;

            // De Casteljau split keeps both resulting Beziers exactly on the
            // original curve, so inserting a point never changes its shape.
            Vector3 q0 = Vector3.Lerp(p0, p1, t);
            Vector3 q1 = Vector3.Lerp(p1, p2, t);
            Vector3 q2 = Vector3.Lerp(p2, p3, t);
            Vector3 r0 = Vector3.Lerp(q0, q1, t);
            Vector3 r1 = Vector3.Lerp(q1, q2, t);
            Vector3 position = Vector3.Lerp(r0, r1, t);

            Vector3 tangentIn = r0 - position;
            Vector3 tangentOut = r1 - position;
            PathKnot inserted = new PathKnot(position)
            {
                tangentIn = tangentIn,
                tangentOut = tangentOut,
                tangentMode = PathTangentMode.Continuous,
                width = PathSplineUtility.EvaluateWidth(start, end, t),
                meshWidthScale = PathSplineUtility.EvaluateMeshWidthScale(start, end, t)
            };

            start.tangentOut = q0 - p0;
            end.tangentIn = q2 - p3;
            if (start.tangentMode == PathTangentMode.Auto
                || start.tangentMode == PathTangentMode.Mirrored)
            {
                start.tangentMode = PathTangentMode.Continuous;
            }
            if (end.tangentMode == PathTangentMode.Auto
                || end.tangentMode == PathTangentMode.Mirrored)
            {
                end.tangentMode = PathTangentMode.Continuous;
            }

            int insertIndex = segmentIndex + 1;
            knots.Insert(insertIndex, inserted);
            return insertIndex;
        }

        public void RemoveKnot(int index)
        {
            if (knots == null || knots.Count <= 2)
                return;
            if (index < 0 || index >= knots.Count)
                return;
            knots.RemoveAt(index);
        }

        public void Reverse()
        {
            if (knots == null)
                return;

            knots.Reverse();
            for (int i = 0; i < knots.Count; i++)
            {
                Vector3 oldIn = knots[i].tangentIn;
                knots[i].tangentIn = knots[i].tangentOut;
                knots[i].tangentOut = oldIn;
            }
        }

        public void SmoothKnot(int index)
        {
            if (knots == null || knots.Count < 2 || index < 0 || index >= knots.Count)
                return;

            CalculateAutoTangents(index);
            knots[index].tangentMode = PathTangentMode.Auto;
            knots[index].brokenTangents = false;
        }

        public void RefreshAutoTangents()
        {
            if (knots == null || knots.Count < 2)
                return;
            for (int index = 0; index < knots.Count; index++)
            {
                if (knots[index].tangentMode == PathTangentMode.Auto)
                    CalculateAutoTangents(index);
            }
        }

        public void SetTangentMode(int index, PathTangentMode mode)
        {
            if (knots == null || index < 0 || index >= knots.Count)
                return;

            PathKnot knot = knots[index];
            knot.tangentMode = mode;
            knot.brokenTangents = false;
            if (mode == PathTangentMode.Linear)
            {
                knot.tangentIn = Vector3.zero;
                knot.tangentOut = Vector3.zero;
                return;
            }
            if (mode == PathTangentMode.Auto)
            {
                CalculateAutoTangents(index);
                return;
            }
            if (knot.tangentOut.sqrMagnitude < 0.000001f
                && knot.tangentIn.sqrMagnitude < 0.000001f)
            {
                CalculateAutoTangents(index);
            }

            if (mode == PathTangentMode.Mirrored)
            {
                Vector3 main = knot.tangentOut.sqrMagnitude > 0.000001f
                    ? knot.tangentOut
                    : -knot.tangentIn;
                knot.tangentOut = main;
                knot.tangentIn = -main;
            }
            else if (mode == PathTangentMode.Continuous)
            {
                ApplyOppositeTangent(knot, false);
            }
        }

        public void SetTangent(int index, bool incoming, Vector3 value)
        {
            if (knots == null || index < 0 || index >= knots.Count)
                return;

            PathKnot knot = knots[index];
            if (knot.tangentMode == PathTangentMode.Auto
                || knot.tangentMode == PathTangentMode.Linear)
            {
                return;
            }

            if (incoming)
                knot.tangentIn = value;
            else
                knot.tangentOut = value;
            ApplyOppositeTangent(knot, incoming);
        }

        private void CalculateAutoTangents(int index)
        {
            if (knots == null || knots.Count < 2
                || index < 0 || index >= knots.Count)
            {
                return;
            }

            int previousIndex = index - 1;
            int nextIndex = index + 1;

            if (closed)
            {
                previousIndex = (previousIndex + knots.Count) % knots.Count;
                nextIndex %= knots.Count;
            }
            else
            {
                previousIndex = Mathf.Max(0, previousIndex);
                nextIndex = Mathf.Min(knots.Count - 1, nextIndex);
            }

            PathKnot knot = knots[index];
            Vector3 previousDelta = knot.position - knots[previousIndex].position;
            Vector3 nextDelta = knots[nextIndex].position - knot.position;

            Vector3 direction;
            if (!closed && index == 0)
                direction = nextDelta.normalized;
            else if (!closed && index == knots.Count - 1)
                direction = previousDelta.normalized;
            else
                direction = (previousDelta.normalized + nextDelta.normalized).normalized;

            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.right;

            knot.tangentIn = -direction * Mathf.Max(0.25f, previousDelta.magnitude / 3f);
            knot.tangentOut = direction * Mathf.Max(0.25f, nextDelta.magnitude / 3f);
        }

        private static void ApplyOppositeTangent(
            PathKnot knot,
            bool incomingChanged)
        {
            if (knot.tangentMode == PathTangentMode.Broken)
                return;
            if (knot.tangentMode == PathTangentMode.Mirrored)
            {
                if (incomingChanged)
                    knot.tangentOut = -knot.tangentIn;
                else
                    knot.tangentIn = -knot.tangentOut;
                return;
            }
            if (knot.tangentMode != PathTangentMode.Continuous)
                return;

            Vector3 changed = incomingChanged
                ? knot.tangentIn
                : knot.tangentOut;
            if (changed.sqrMagnitude < 0.000001f)
                return;
            if (incomingChanged)
            {
                float length = knot.tangentOut.magnitude;
                knot.tangentOut = -changed.normalized * length;
            }
            else
            {
                float length = knot.tangentIn.magnitude;
                knot.tangentIn = -changed.normalized * length;
            }
        }

        private static void MigrateLegacyTangentMode(PathKnot knot)
        {
            if (!knot.brokenTangents)
                return;
            knot.tangentMode = PathTangentMode.Broken;
            knot.brokenTangents = false;
        }

        private static Vector2 OrderedPositiveRange(Vector2 range, float minimum)
        {
            float min = Mathf.Max(minimum, Mathf.Min(range.x, range.y));
            float max = Mathf.Max(min, Mathf.Max(range.x, range.y));
            return new Vector2(min, max);
        }
    }

    public struct PathSample
    {
        public Vector3 position;
        public Vector3 tangent;
        public float width;
        public float meshWidthScale;
        public float distance;
    }

    public static class PathSplineUtility
    {
        public static Vector3 EvaluatePosition(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.Clamp01(t);
            float oneMinusT = 1f - t;
            Vector3 p0 = start.position;
            Vector3 p1 = start.position + start.tangentOut;
            Vector3 p2 = end.position + end.tangentIn;
            Vector3 p3 = end.position;
            return oneMinusT * oneMinusT * oneMinusT * p0
                + 3f * oneMinusT * oneMinusT * t * p1
                + 3f * oneMinusT * t * t * p2
                + t * t * t * p3;
        }

        public static Vector3 EvaluateTangent(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.Clamp01(t);
            float oneMinusT = 1f - t;
            Vector3 p0 = start.position;
            Vector3 p1 = start.position + start.tangentOut;
            Vector3 p2 = end.position + end.tangentIn;
            Vector3 p3 = end.position;
            Vector3 tangent =
                3f * oneMinusT * oneMinusT * (p1 - p0)
                + 6f * oneMinusT * t * (p2 - p1)
                + 3f * t * t * (p3 - p2);
            if (tangent.sqrMagnitude < 0.000001f)
                tangent = p3 - p0;
            return tangent;
        }

        public static float EvaluateWidth(
            PathKnot start,
            PathKnot end,
            float t)
        {
            t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            return Mathf.Lerp(start.width, end.width, t);
        }

        public static float EvaluateMeshWidthScale(PathKnot start, PathKnot end, float t)
        {
            t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            return Mathf.Lerp(start.meshWidthScale, end.meshWidthScale, t);
        }

        public static List<PathSample> BuildArcTable(PathSpline builder)
        {
            List<PathSample> samples = new List<PathSample>();
            if (builder == null || builder.SegmentCount == 0)
                return samples;

            float distance = 0f;
            Vector3 previousPosition = Vector3.zero;
            int resolution = Mathf.Clamp(builder.samplesPerCurve, 4, 64);

            for (int segmentIndex = 0; segmentIndex < builder.SegmentCount; segmentIndex++)
            {
                int nextIndex = (segmentIndex + 1) % builder.knots.Count;
                PathKnot start = builder.knots[segmentIndex];
                PathKnot end = builder.knots[nextIndex];
                int firstStep = segmentIndex == 0 ? 0 : 1;

                for (int step = firstStep; step <= resolution; step++)
                {
                    float t = step / (float)resolution;
                    Vector3 localPosition = EvaluatePosition(start, end, t);
                    Vector3 localTangent = EvaluateTangent(start, end, t);
                    if (!builder.Anchor)
                        return samples;
                    Vector3 worldPosition = builder.Anchor.TransformPoint(localPosition);
                    Vector3 worldTangent = builder.Anchor.TransformVector(localTangent).normalized;

                    if (samples.Count > 0)
                        distance += Vector3.Distance(previousPosition, worldPosition);

                    samples.Add(new PathSample
                    {
                        position = worldPosition,
                        tangent = worldTangent,
                        width = EvaluateWidth(start, end, t),
                        meshWidthScale = EvaluateMeshWidthScale(start, end, t),
                        distance = distance
                    });
                    previousPosition = worldPosition;
                }
            }

            return samples;
        }

        public static bool TryEvaluateAtDistance(
            IList<PathSample> arcTable,
            float distance,
            out PathSample result)
        {
            result = default(PathSample);
            if (arcTable == null || arcTable.Count == 0)
                return false;
            if (arcTable.Count == 1)
            {
                result = arcTable[0];
                return true;
            }

            float totalLength = arcTable[arcTable.Count - 1].distance;
            distance = Mathf.Clamp(distance, 0f, totalLength);

            int low = 0;
            int high = arcTable.Count - 1;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (arcTable[middle].distance < distance)
                    low = middle + 1;
                else
                    high = middle;
            }

            int upperIndex = Mathf.Clamp(low, 1, arcTable.Count - 1);
            int lowerIndex = upperIndex - 1;
            PathSample lower = arcTable[lowerIndex];
            PathSample upper = arcTable[upperIndex];
            float span = upper.distance - lower.distance;
            float t = span > 0.000001f ? (distance - lower.distance) / span : 0f;

            result.position = Vector3.Lerp(lower.position, upper.position, t);
            result.tangent = Vector3.Lerp(lower.tangent, upper.tangent, t).normalized;
            result.width = Mathf.Lerp(lower.width, upper.width, t);
            result.meshWidthScale = Mathf.Lerp(lower.meshWidthScale, upper.meshWidthScale, t);
            result.distance = distance;
            return true;
        }
    }
}
