using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SLGMapTileCutter.Editor
{
    /// <summary>
    /// First production-facing version of the SLG map tile cutter.
    /// It keeps one resolved region grid for every tile, so adjacent generated meshes
    /// always derive their shared boundaries from exactly the same coordinates.
    /// </summary>
    public sealed class SLGMapTileCutterWindow : EditorWindow
    {
        private const string DefaultOutputFolder = "Assets/Generated/SLGMapTileCutter";
        private const string PreviewColliderName = "__SLGMapTileCutterPreviewCollider";
        [SerializeField] private SLGMapTileMapAsset _configuration;
        [SerializeField] private Texture2D _lineTexture;
        [SerializeField] private MeshFilter _target;
        [SerializeField] private int _longSideResolution = 192;
        [SerializeField] private float _darkThreshold = 0.62f;
        [SerializeField] private int _gapCloseRadius = 1;
        [SerializeField] private int _minimumRegionPixels = 8;
        [SerializeField] private Vector2 _projectionOffset;
        [SerializeField] private Vector2 _projectionScale = Vector2.one;
        [SerializeField] private float _projectionRotation;
        [SerializeField] private float _tileGap;
        [SerializeField] private bool _hideSourceOnGenerate = true;
        [SerializeField] private bool _colorPreview = true;
        [SerializeField] private float _outlineWidth = 0.1f;
        [SerializeField] private float _outlineSoftness = 0.2f;
        [SerializeField] private Color _outlineColor = Color.black;
        [SerializeField] private int _outlineMapLongSide = 1024;
        [SerializeField] private bool _manualConnectMode;
        [SerializeField] private bool _snapManualToSkeleton = true;
        [SerializeField] private float _manualSnapDistance = 0.025f;
        [SerializeField] private float _skeletonSimplifyTolerance = 0.8f;
        [SerializeField] private float _skeletonSmoothing = 0.35f;
        [SerializeField] private bool _antialiasedSkeletonPreview;
        [SerializeField] private float _manualStrokeSmoothing = 0.35f;
        [SerializeField] private float _manualStrokeSimplifyTolerance = 1f;
        [SerializeField] private int _manualEraseRadius = 2;
        [SerializeField] private ManualEditOperation _manualEditOperation;
        [SerializeField] private string _outputFolder = DefaultOutputFolder;

        [SerializeField] private List<SLGMapTileManualSegment> _manualSegments =
            new List<SLGMapTileManualSegment>();
        [SerializeField] private List<SLGMapTileManualSegment> _manualEraseSegments =
            new List<SLGMapTileManualSegment>();
        private readonly List<PreviewSegment> _previewSegments =
            new List<PreviewSegment>();
        private readonly List<List<Vector2Int>> _previewGridPaths =
            new List<List<Vector2Int>>();
        private readonly List<Vector2[]> _previewUvPaths =
            new List<Vector2[]>();
        private readonly List<Vector3[]> _previewWorldPaths =
            new List<Vector3[]>();
        private Vector3[] _previewLinePairs = new Vector3[0];
        // Shared, simplified paths in image UV space. This becomes the source of
        // truth for the vector-region generator; preview and output must no longer
        // derive from separate raster and display-only representations.
        private readonly List<List<Vector2>> _vectorSkeletonPaths =
            new List<List<Vector2>>();
        private readonly Dictionary<Vector2Int, Vector2> _vectorizedGridPoints =
            new Dictionary<Vector2Int, Vector2>();

        private bool _needsRebuild = true;
        private bool _isBuilding;
        private int _gridWidth;
        private int _gridHeight;
        private int[] _labels;
        private int _tileCount;
        private int _previewSurfaceHitCount;
        private int _previewFallbackCount;
        private string _lastMinimalBuildFailure = string.Empty;
        private readonly List<Vector2> _activeManualStroke = new List<Vector2>();
        private readonly List<Vector3> _activeManualWorldStroke =
            new List<Vector3>();
        private readonly Dictionary<Vector2, Vector3> _surfaceProjectionCache =
            new Dictionary<Vector2, Vector3>();
        private bool[] _cachedImportedLineMask;
        private Texture2D _cachedLineTexture;
        private int _cachedLineGridWidth;
        private int _cachedLineGridHeight;
        private float _cachedDarkThreshold;
        private int _cachedGapCloseRadius;
        private Matrix4x4 _projectionCacheLocalToWorld;
        private Vector2 _projectionCacheOffset;
        private Vector2 _projectionCacheScale;
        private float _projectionCacheRotation;
        private int _projectionCacheTargetId;
        private bool _activeStrokeStartedOnSkeleton;
        private Vector2 _lastManualGuiPosition;
        private bool _hasLastManualGuiPosition;
        private double _rebuildNotBefore;
        private const double RebuildDebounceSeconds = 0.08;
        private double _lastRecognitionMilliseconds;
        private double _lastVectorizationMilliseconds;
        private double _lastProjectionMilliseconds;
        private double _lastPreviewDrawMilliseconds;
        private int _lastPreviewPointCount;

        private enum ManualEditOperation
        {
            Add,
            Erase
        }
        private GameObject _previewColliderObject;
        private MeshCollider _previewCollider;
        private Vector2 _scroll;

        private struct PreviewSegment
        {
            public Vector3 a;
            public Vector3 b;
            public bool isManual;
        }

        // Stored in grid space.  The grid remains the authoritative, shared cutting
        // domain; these paths are simplified only for the editor skeleton display.
        // This lets artists read diagonal borders as diagonals without introducing
        // independent per-tile preview vertices that could suggest false seams.
        private struct GridSegment
        {
            public Vector2Int a;
            public Vector2Int b;
        }

        private struct VectorRegionTriangle
        {
            public Vector2 a;
            public Vector2 b;
            public Vector2 c;
            public Rect bounds;
        }

        private sealed class VectorRegion
        {
            public int label;
            public Rect bounds;
            public readonly List<VectorRegionTriangle> triangles =
                new List<VectorRegionTriangle>();
        }

        private struct PolyVertex
        {
            public Vector2 plane;
            public Vector3 position;
            public Vector2 uv;
            public Vector3 normal;
            public Vector4 tangent;

            public static PolyVertex Lerp(PolyVertex a, PolyVertex b, float t)
            {
                return new PolyVertex
                {
                    plane = Vector2.LerpUnclamped(a.plane, b.plane, t),
                    position = Vector3.LerpUnclamped(a.position, b.position, t),
                    uv = Vector2.LerpUnclamped(a.uv, b.uv, t),
                    normal = Vector3.LerpUnclamped(a.normal, b.normal, t).normalized,
                    tangent = Vector4.LerpUnclamped(a.tangent, b.tangent, t)
                };
            }
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            private readonly int _px;
            private readonly int _py;
            private readonly int _pz;
            private readonly int _u;
            private readonly int _v;
            private readonly int _nx;
            private readonly int _ny;
            private readonly int _nz;

            public VertexKey(PolyVertex vertex)
            {
                _px = Quantize(vertex.position.x);
                _py = Quantize(vertex.position.y);
                _pz = Quantize(vertex.position.z);
                _u = Quantize(vertex.uv.x);
                _v = Quantize(vertex.uv.y);
                _nx = Quantize(vertex.normal.x);
                _ny = Quantize(vertex.normal.y);
                _nz = Quantize(vertex.normal.z);
            }

            public bool Equals(VertexKey other)
            {
                return _px == other._px && _py == other._py && _pz == other._pz
                    && _u == other._u && _v == other._v
                    && _nx == other._nx && _ny == other._ny && _nz == other._nz;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _px;
                    hash = hash * 31 + _py;
                    hash = hash * 31 + _pz;
                    hash = hash * 31 + _u;
                    hash = hash * 31 + _v;
                    hash = hash * 31 + _nx;
                    hash = hash * 31 + _ny;
                    return hash * 31 + _nz;
                }
            }

            private static int Quantize(float value)
            {
                return Mathf.RoundToInt(value * 1000000f);
            }
        }

        private struct IndexEdge : IEquatable<IndexEdge>
        {
            public readonly int a;
            public readonly int b;

            public IndexEdge(int first, int second)
            {
                a = Mathf.Min(first, second);
                b = Mathf.Max(first, second);
            }

            public bool Equals(IndexEdge other)
            {
                return a == other.a && b == other.b;
            }

            public override bool Equals(object obj)
            {
                return obj is IndexEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return a * 397 ^ b;
                }
            }
        }

        private sealed class TileBuilder
        {
            public sealed class OutlineRenderData
            {
                public readonly List<Vector3> vertices = new List<Vector3>();
                public readonly List<Vector2> uvs = new List<Vector2>();
                public readonly List<Vector3> normals = new List<Vector3>();
                public readonly List<Vector4> tangents = new List<Vector4>();
                public readonly List<Color> colors = new List<Color>();
                public readonly List<Vector3> barycentrics = new List<Vector3>();
                public readonly List<Vector3> boundaryAltitudes = new List<Vector3>();
                public readonly List<List<int>> triangles = new List<List<int>>();
            }

            private readonly Dictionary<VertexKey, int> _vertexIndices =
                new Dictionary<VertexKey, int>();

            public readonly List<Vector3> vertices = new List<Vector3>();
            public readonly List<Vector2> uvs = new List<Vector2>();
            public readonly List<Vector3> normals = new List<Vector3>();
            public readonly List<Vector4> tangents = new List<Vector4>();
            public readonly List<Color> colors = new List<Color>();
            public readonly List<List<int>> triangles;

            public TileBuilder(int subMeshCount)
            {
                triangles = new List<List<int>>(subMeshCount);
                for (int i = 0; i < subMeshCount; i++)
                {
                    triangles.Add(new List<int>());
                }
            }

            public void AddTriangle(PolyVertex a, PolyVertex b, PolyVertex c,
                int subMesh, Color color)
            {
                if (Vector3.Cross(b.position - a.position, c.position - a.position).sqrMagnitude
                    < 0.0000000000000001f)
                {
                    return;
                }
                int ia = GetOrAddVertex(a, color);
                int ib = GetOrAddVertex(b, color);
                int ic = GetOrAddVertex(c, color);
                if (ia == ib || ib == ic || ia == ic)
                {
                    return;
                }

                triangles[subMesh].Add(ia);
                triangles[subMesh].Add(ib);
                triangles[subMesh].Add(ic);
            }

            public bool AddTriangulatedPolygon(List<PolyVertex> polygon,
                List<int> polygonTriangles, int subMesh, Color color)
            {
                if (polygon == null || polygon.Count < 3
                    || polygonTriangles == null
                    || polygonTriangles.Count != (polygon.Count - 2) * 3)
                {
                    return false;
                }
                for (int i = 0; i < polygonTriangles.Count; i += 3)
                {
                    int a = polygonTriangles[i];
                    int b = polygonTriangles[i + 1];
                    int c = polygonTriangles[i + 2];
                    if (a < 0 || b < 0 || c < 0
                        || a >= polygon.Count || b >= polygon.Count
                        || c >= polygon.Count || a == b || b == c || c == a
                        || Vector3.Cross(polygon[b].position - polygon[a].position,
                            polygon[c].position - polygon[a].position).sqrMagnitude
                            <= float.Epsilon)
                    {
                        return false;
                    }
                }

                int vertexOffset = vertices.Count;
                for (int i = 0; i < polygon.Count; i++)
                {
                    PolyVertex vertex = polygon[i];
                    vertices.Add(vertex.position);
                    uvs.Add(vertex.uv);
                    normals.Add(vertex.normal);
                    tangents.Add(vertex.tangent);
                    colors.Add(color);
                }
                for (int i = 0; i < polygonTriangles.Count; i++)
                {
                    triangles[subMesh].Add(vertexOffset + polygonTriangles[i]);
                }
                return true;
            }

            public bool HasGeometry
            {
                get
                {
                    for (int i = 0; i < triangles.Count; i++)
                    {
                        if (triangles[i].Count > 0)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }

            public bool HasSingleClosedPatchTopology(out string reason)
            {
                reason = string.Empty;
                Dictionary<IndexEdge, int> edgeCounts =
                    new Dictionary<IndexEdge, int>();
                HashSet<int> usedVertices = new HashSet<int>();
                int faceCount = 0;
                for (int subMesh = 0; subMesh < triangles.Count; subMesh++)
                {
                    List<int> indices = triangles[subMesh];
                    if (indices.Count % 3 != 0)
                    {
                        reason = "triangle index count is not divisible by three";
                        return false;
                    }
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        int a = indices[i];
                        int b = indices[i + 1];
                        int c = indices[i + 2];
                        if (a == b || b == c || c == a)
                        {
                            reason = "contains a degenerate triangle";
                            return false;
                        }
                        usedVertices.Add(a);
                        usedVertices.Add(b);
                        usedVertices.Add(c);
                        CountEdge(edgeCounts, a, b);
                        CountEdge(edgeCounts, b, c);
                        CountEdge(edgeCounts, c, a);
                        faceCount++;
                    }
                }
                if (faceCount == 0)
                {
                    reason = "contains no triangles";
                    return false;
                }

                Dictionary<int, List<int>> boundaryAdjacency =
                    new Dictionary<int, List<int>>();
                foreach (KeyValuePair<IndexEdge, int> pair in edgeCounts)
                {
                    if (pair.Value > 2)
                    {
                        reason = "contains a non-manifold edge";
                        return false;
                    }
                    if (pair.Value == 1)
                    {
                        AddBoundaryNeighbour(boundaryAdjacency,
                            pair.Key.a, pair.Key.b);
                        AddBoundaryNeighbour(boundaryAdjacency,
                            pair.Key.b, pair.Key.a);
                    }
                }
                if (boundaryAdjacency.Count < 3)
                {
                    reason = "does not contain one outer boundary";
                    return false;
                }
                foreach (KeyValuePair<int, List<int>> pair in boundaryAdjacency)
                {
                    if (pair.Value.Count != 2)
                    {
                        reason = "contains an open or branching boundary";
                        return false;
                    }
                }

                int eulerCharacteristic = usedVertices.Count
                    - edgeCounts.Count + faceCount;
                if (eulerCharacteristic != 1)
                {
                    reason = "contains a hole or disconnected patch";
                    return false;
                }
                return true;
            }

            // UV1 stores triangle barycentrics. UV2 stores the local-space altitude
            // to each triangle edge, but only for tile-boundary edges.
            public OutlineRenderData BuildOutlineRenderData()
            {
                OutlineRenderData data = new OutlineRenderData();
                Dictionary<IndexEdge, int> edgeCounts =
                    new Dictionary<IndexEdge, int>();
                for (int subMesh = 0; subMesh < triangles.Count; subMesh++)
                {
                    List<int> indices = triangles[subMesh];
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        CountEdge(edgeCounts, indices[i], indices[i + 1]);
                        CountEdge(edgeCounts, indices[i + 1], indices[i + 2]);
                        CountEdge(edgeCounts, indices[i + 2], indices[i]);
                    }
                    data.triangles.Add(new List<int>(indices.Count));
                }

                Vector3[] barycentric =
                {
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 1f, 0f),
                    new Vector3(0f, 0f, 1f)
                };
                for (int subMesh = 0; subMesh < triangles.Count; subMesh++)
                {
                    List<int> indices = triangles[subMesh];
                    List<int> outputIndices = data.triangles[subMesh];
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        int a = indices[i];
                        int b = indices[i + 1];
                        int c = indices[i + 2];
                        Vector3 boundaryAltitudes = new Vector3(
                            IsBoundary(edgeCounts, b, c)
                                ? TriangleAltitude(vertices[a], vertices[b], vertices[c])
                                : 0f,
                            IsBoundary(edgeCounts, c, a)
                                ? TriangleAltitude(vertices[b], vertices[c], vertices[a])
                                : 0f,
                            IsBoundary(edgeCounts, a, b)
                                ? TriangleAltitude(vertices[c], vertices[a], vertices[b])
                                : 0f);
                        int[] sourceIndices = { a, b, c };
                        for (int corner = 0; corner < 3; corner++)
                        {
                            int source = sourceIndices[corner];
                            int destination = data.vertices.Count;
                            data.vertices.Add(vertices[source]);
                            data.uvs.Add(uvs[source]);
                            data.normals.Add(normals[source]);
                            data.tangents.Add(tangents[source]);
                            data.colors.Add(colors[source]);
                            data.barycentrics.Add(barycentric[corner]);
                            data.boundaryAltitudes.Add(boundaryAltitudes);
                            outputIndices.Add(destination);
                        }
                    }
                }
                return data;
            }

            private static bool IsBoundary(Dictionary<IndexEdge, int> edgeCounts,
                int a, int b)
            {
                return edgeCounts.TryGetValue(new IndexEdge(a, b), out int count)
                    && count == 1;
            }

            private static float TriangleAltitude(Vector3 opposite,
                Vector3 edgeA, Vector3 edgeB)
            {
                float edgeLength = Vector3.Distance(edgeA, edgeB);
                if (edgeLength <= 0.0000001f)
                {
                    return 0f;
                }
                return Vector3.Cross(edgeA - opposite, edgeB - opposite).magnitude
                    / edgeLength;
            }

            public void OptimizeFlatPlanarPatch()
            {
                int populatedSubMesh = -1;
                for (int i = 0; i < triangles.Count; i++)
                {
                    if (triangles[i].Count == 0)
                    {
                        continue;
                    }
                    if (populatedSubMesh >= 0)
                    {
                        return;
                    }
                    populatedSubMesh = i;
                }
                if (populatedSubMesh < 0 || vertices.Count < 4)
                {
                    return;
                }

                List<int> sourceTriangles = triangles[populatedSubMesh];
                if (!TryGetPlanarBoundary(sourceTriangles, out List<int> boundary,
                    out float desiredAreaSign))
                {
                    return;
                }
                RemoveCollinearBoundaryVertices(boundary);
                if (boundary.Count < 3
                    || !HasGloballyAffineUv(boundary)
                    || !TryEarClip(boundary, desiredAreaSign, out List<int> optimizedTriangles))
                {
                    return;
                }

                Dictionary<int, int> remap = new Dictionary<int, int>();
                List<Vector3> newVertices = new List<Vector3>(boundary.Count);
                List<Vector2> newUvs = new List<Vector2>(boundary.Count);
                List<Vector3> newNormals = new List<Vector3>(boundary.Count);
                List<Vector4> newTangents = new List<Vector4>(boundary.Count);
                List<Color> newColors = new List<Color>(boundary.Count);
                for (int i = 0; i < boundary.Count; i++)
                {
                    int oldIndex = boundary[i];
                    remap[oldIndex] = i;
                    newVertices.Add(vertices[oldIndex]);
                    newUvs.Add(uvs[oldIndex]);
                    newNormals.Add(normals[oldIndex]);
                    newTangents.Add(tangents[oldIndex]);
                    newColors.Add(colors[oldIndex]);
                }
                for (int i = 0; i < optimizedTriangles.Count; i++)
                {
                    optimizedTriangles[i] = remap[optimizedTriangles[i]];
                }

                vertices.Clear();
                vertices.AddRange(newVertices);
                uvs.Clear();
                uvs.AddRange(newUvs);
                normals.Clear();
                normals.AddRange(newNormals);
                tangents.Clear();
                tangents.AddRange(newTangents);
                colors.Clear();
                colors.AddRange(newColors);
                _vertexIndices.Clear();
                for (int i = 0; i < triangles.Count; i++)
                {
                    triangles[i].Clear();
                }
                triangles[populatedSubMesh].AddRange(optimizedTriangles);
            }

            private bool TryGetPlanarBoundary(List<int> sourceTriangles,
                out List<int> boundary, out float desiredAreaSign)
            {
                boundary = null;
                desiredAreaSign = 1f;
                if (sourceTriangles.Count < 3)
                {
                    return false;
                }

                int firstA = sourceTriangles[0];
                int firstB = sourceTriangles[1];
                int firstC = sourceTriangles[2];
                Vector3 planeNormal = Vector3.Cross(vertices[firstB] - vertices[firstA],
                    vertices[firstC] - vertices[firstA]);
                float scale = Mathf.Max(1f, BoundsSize());
                if (planeNormal.sqrMagnitude <= 0.0000000001f)
                {
                    return false;
                }
                planeNormal.Normalize();
                Vector3 planePoint = vertices[firstA];
                for (int i = 0; i < vertices.Count; i++)
                {
                    if (Mathf.Abs(Vector3.Dot(vertices[i] - planePoint, planeNormal))
                        > scale * 0.00001f)
                    {
                        return false;
                    }
                }

                Vector2 firstPlaneA = new Vector2(vertices[firstA].x, vertices[firstA].z);
                Vector2 firstPlaneB = new Vector2(vertices[firstB].x, vertices[firstB].z);
                Vector2 firstPlaneC = new Vector2(vertices[firstC].x, vertices[firstC].z);
                float projectedArea = Cross2D(firstPlaneB - firstPlaneA,
                    firstPlaneC - firstPlaneA);
                if (Mathf.Abs(projectedArea) <= 0.0000001f)
                {
                    return false;
                }
                desiredAreaSign = Mathf.Sign(projectedArea);

                Dictionary<IndexEdge, int> edgeCounts = new Dictionary<IndexEdge, int>();
                for (int i = 0; i < sourceTriangles.Count; i += 3)
                {
                    CountEdge(edgeCounts, sourceTriangles[i], sourceTriangles[i + 1]);
                    CountEdge(edgeCounts, sourceTriangles[i + 1], sourceTriangles[i + 2]);
                    CountEdge(edgeCounts, sourceTriangles[i + 2], sourceTriangles[i]);
                }

                Dictionary<int, List<int>> adjacency = new Dictionary<int, List<int>>();
                int boundaryEdgeCount = 0;
                foreach (KeyValuePair<IndexEdge, int> pair in edgeCounts)
                {
                    if (pair.Value != 1)
                    {
                        continue;
                    }
                    AddBoundaryNeighbour(adjacency, pair.Key.a, pair.Key.b);
                    AddBoundaryNeighbour(adjacency, pair.Key.b, pair.Key.a);
                    boundaryEdgeCount++;
                }
                if (boundaryEdgeCount < 3)
                {
                    return false;
                }
                foreach (KeyValuePair<int, List<int>> pair in adjacency)
                {
                    if (pair.Value.Count != 2)
                    {
                        return false;
                    }
                }

                int start = int.MaxValue;
                foreach (int index in adjacency.Keys)
                {
                    start = Mathf.Min(start, index);
                }
                boundary = new List<int>(boundaryEdgeCount);
                int previous = -1;
                int current = start;
                do
                {
                    boundary.Add(current);
                    List<int> neighbours = adjacency[current];
                    int next = neighbours[0] == previous ? neighbours[1] : neighbours[0];
                    previous = current;
                    current = next;
                    if (boundary.Count > boundaryEdgeCount)
                    {
                        return false;
                    }
                }
                while (current != start);
                return boundary.Count == boundaryEdgeCount;
            }

            private float BoundsSize()
            {
                if (vertices.Count == 0)
                {
                    return 0f;
                }
                Bounds bounds = new Bounds(vertices[0], Vector3.zero);
                for (int i = 1; i < vertices.Count; i++)
                {
                    bounds.Encapsulate(vertices[i]);
                }
                return bounds.size.magnitude;
            }

            private static void CountEdge(Dictionary<IndexEdge, int> counts,
                int a, int b)
            {
                IndexEdge edge = new IndexEdge(a, b);
                counts.TryGetValue(edge, out int count);
                counts[edge] = count + 1;
            }

            private static void AddBoundaryNeighbour(
                Dictionary<int, List<int>> adjacency, int from, int to)
            {
                if (!adjacency.TryGetValue(from, out List<int> neighbours))
                {
                    neighbours = new List<int>(2);
                    adjacency.Add(from, neighbours);
                }
                neighbours.Add(to);
            }

            private void RemoveCollinearBoundaryVertices(List<int> boundary)
            {
                bool removed;
                do
                {
                    removed = false;
                    for (int i = 0; i < boundary.Count && boundary.Count > 3; i++)
                    {
                        int previous = boundary[(i - 1 + boundary.Count) % boundary.Count];
                        int current = boundary[i];
                        int next = boundary[(i + 1) % boundary.Count];
                        Vector2 a = new Vector2(vertices[previous].x, vertices[previous].z);
                        Vector2 b = new Vector2(vertices[current].x, vertices[current].z);
                        Vector2 c = new Vector2(vertices[next].x, vertices[next].z);
                        float area = Mathf.Abs(Cross2D(b - a, c - b));
                        float scale = Mathf.Max(1f, (b - a).magnitude + (c - b).magnitude);
                        if (area <= scale * 0.000001f)
                        {
                            boundary.RemoveAt(i);
                            removed = true;
                            break;
                        }
                    }
                }
                while (removed);
            }

            private bool HasGloballyAffineUv(List<int> boundary)
            {
                int a = boundary[0];
                int b = -1;
                int c = -1;
                for (int i = 1; i < boundary.Count && b < 0; i++)
                {
                    if ((vertices[boundary[i]] - vertices[a]).sqrMagnitude > 0.00000001f)
                    {
                        b = boundary[i];
                    }
                }
                if (b < 0)
                {
                    return false;
                }
                Vector2 pa = new Vector2(vertices[a].x, vertices[a].z);
                Vector2 pb = new Vector2(vertices[b].x, vertices[b].z);
                for (int i = 1; i < boundary.Count; i++)
                {
                    int candidate = boundary[i];
                    Vector2 pc = new Vector2(vertices[candidate].x, vertices[candidate].z);
                    if (Mathf.Abs(Cross2D(pb - pa, pc - pa)) > 0.000001f)
                    {
                        c = candidate;
                        break;
                    }
                }
                if (c < 0)
                {
                    return false;
                }

                Vector2 p0 = pa;
                Vector2 p1 = pb;
                Vector2 p2 = new Vector2(vertices[c].x, vertices[c].z);
                float denominator = Cross2D(p1 - p0, p2 - p0);
                for (int i = 0; i < vertices.Count; i++)
                {
                    Vector2 p = new Vector2(vertices[i].x, vertices[i].z);
                    float weight1 = Cross2D(p - p0, p2 - p0) / denominator;
                    float weight2 = Cross2D(p1 - p0, p - p0) / denominator;
                    Vector2 expected = uvs[a]
                        + (uvs[b] - uvs[a]) * weight1
                        + (uvs[c] - uvs[a]) * weight2;
                    if ((expected - uvs[i]).sqrMagnitude > 0.00000001f)
                    {
                        return false;
                    }
                }
                return true;
            }

            private bool TryEarClip(List<int> boundary, float desiredAreaSign,
                out List<int> result)
            {
                result = new List<int>();
                List<int> polygon = new List<int>(boundary);
                if (SignedArea(polygon) < 0f)
                {
                    polygon.Reverse();
                }

                int guard = polygon.Count * polygon.Count;
                while (polygon.Count > 3 && guard-- > 0)
                {
                    bool clipped = false;
                    for (int i = 0; i < polygon.Count; i++)
                    {
                        int previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
                        int current = polygon[i];
                        int next = polygon[(i + 1) % polygon.Count];
                        Vector2 a = PlanePoint(previous);
                        Vector2 b = PlanePoint(current);
                        Vector2 c = PlanePoint(next);
                        if (Cross2D(b - a, c - b) <= 0.0000001f)
                        {
                            continue;
                        }
                        bool containsPoint = false;
                        for (int p = 0; p < polygon.Count; p++)
                        {
                            int test = polygon[p];
                            if (test == previous || test == current || test == next)
                            {
                                continue;
                            }
                            if (PointInTriangle(PlanePoint(test), a, b, c))
                            {
                                containsPoint = true;
                                break;
                            }
                        }
                        if (containsPoint)
                        {
                            continue;
                        }
                        AddOrientedTriangle(result, previous, current, next,
                            desiredAreaSign);
                        polygon.RemoveAt(i);
                        clipped = true;
                        break;
                    }
                    if (!clipped)
                    {
                        result.Clear();
                        return false;
                    }
                }
                if (polygon.Count != 3)
                {
                    result.Clear();
                    return false;
                }
                AddOrientedTriangle(result, polygon[0], polygon[1], polygon[2],
                    desiredAreaSign);
                return true;
            }

            private float SignedArea(List<int> polygon)
            {
                float area = 0f;
                for (int i = 0; i < polygon.Count; i++)
                {
                    Vector2 a = PlanePoint(polygon[i]);
                    Vector2 b = PlanePoint(polygon[(i + 1) % polygon.Count]);
                    area += Cross2D(a, b);
                }
                return area * 0.5f;
            }

            private Vector2 PlanePoint(int index)
            {
                return new Vector2(vertices[index].x, vertices[index].z);
            }

            private static bool PointInTriangle(Vector2 point, Vector2 a,
                Vector2 b, Vector2 c)
            {
                const float epsilon = 0.0000001f;
                float ab = Cross2D(b - a, point - a);
                float bc = Cross2D(c - b, point - b);
                float ca = Cross2D(a - c, point - c);
                return ab >= -epsilon && bc >= -epsilon && ca >= -epsilon;
            }

            private static void AddOrientedTriangle(List<int> result,
                int a, int b, int c, float desiredAreaSign)
            {
                result.Add(a);
                if (desiredAreaSign >= 0f)
                {
                    result.Add(b);
                    result.Add(c);
                }
                else
                {
                    result.Add(c);
                    result.Add(b);
                }
            }

            private int GetOrAddVertex(PolyVertex vertex, Color color)
            {
                VertexKey key = new VertexKey(vertex);
                if (_vertexIndices.TryGetValue(key, out int index))
                {
                    return index;
                }

                index = vertices.Count;
                _vertexIndices.Add(key, index);
                vertices.Add(vertex.position);
                uvs.Add(vertex.uv);
                normals.Add(vertex.normal);
                tangents.Add(vertex.tangent);
                colors.Add(color);
                return index;
            }
        }

        [MenuItem("项目工具箱/SLG 地块切分")]
        public static void Open()
        {
            SLGMapTileCutterWindow window =
                GetWindow<SLGMapTileCutterWindow>("SLG Map Tile Cutter");
            window.minSize = new Vector2(420f, 620f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;
            if (Selection.activeObject is SLGMapTileMapAsset configuration)
            {
                _configuration = configuration;
            }
            else
            {
                TryUseSelection();
            }
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            DestroyPreviewCollider();
        }

        private void OnUndoRedo()
        {
            ClearActiveManualStroke();
            _hasLastManualGuiPosition = false;
            Invalidate();
            RepaintAllSceneViews();
            Repaint();
        }

        private void OnSelectionChange()
        {
            if (Selection.activeObject is SLGMapTileMapAsset configuration)
            {
                _configuration = configuration;
                Repaint();
                return;
            }
            if (_target == null)
            {
                TryUseSelection();
            }
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("SLG Map Tile Cutter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "黑色线框会先中心化为一套共享边界，再生成独立地块。"
                + "识别分辨率会同时影响边界精度和网格密度。",
                MessageType.Info);

            _configuration = (SLGMapTileMapAsset)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Saved Configuration",
                    "选择之前保存的地块工具设置文件。"),
                _configuration, typeof(SLGMapTileMapAsset), false);
            using (new EditorGUI.DisabledScope(_configuration == null))
            {
                if (GUILayout.Button(new GUIContent(
                    "Load Configuration and Refresh Preview",
                    "读取所选设置文件，并立即刷新模型上的骨架预览。")))
                {
                    LoadConfiguration(_configuration);
                }
            }

            EditorGUILayout.Space(4f);
            EditorGUI.BeginChangeCheck();
            _lineTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Linework Texture",
                    "用于识别地块边界的黑色线框图。"),
                _lineTexture, typeof(Texture2D), false);
            _target = (MeshFilter)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Reference Mesh",
                    "骨架预览和新地块将投射到这个模型表面。"),
                _target, typeof(MeshFilter), true);
            if (EditorGUI.EndChangeCheck())
            {
                Invalidate();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(
                    "Use Selected Mesh",
                    "使用场景中当前选中的 MeshFilter 作为参考模型。")))
                {
                    TryUseSelection();
                }

                if (GUILayout.Button(new GUIContent(
                    "Import Image",
                    "从项目外部选择并导入线框图片。")))
                {
                    ImportExternalLineTexture();
                }

                using (new EditorGUI.DisabledScope(_lineTexture == null))
                {
                    if (GUILayout.Button(new GUIContent(
                        "Make Readable",
                        "修改图片导入设置，使工具能够读取像素。")))
                    {
                        MakeTextureReadable();
                    }
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Recognition", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _longSideResolution = EditorGUILayout.IntSlider(
                new GUIContent("Recognition Resolution",
                    "越高越能保留原图细节。最终边界点数主要由轮廓简化误差控制。"),
                _longSideResolution, 32, 512);
            _darkThreshold = EditorGUILayout.Slider(
                new GUIContent(
                    "Line Darkness Threshold",
                    "控制多暗的像素会被识别为边界线。"),
                _darkThreshold, 0.05f, 0.95f);
            _gapCloseRadius = EditorGUILayout.IntSlider(
                new GUIContent("Gap Closing", "以像素为单位补齐小断口。"),
                _gapCloseRadius, 0, 5);
            _minimumRegionPixels = EditorGUILayout.IntSlider(
                new GUIContent(
                    "Minimum Region Pixels",
                    "小于该像素数量的区域不会作为独立地块保留。"),
                _minimumRegionPixels, 1, 128);
            if (EditorGUI.EndChangeCheck())
            {
                Invalidate();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Projection and Output", EditorStyles.boldLabel);
            Event guiEvent = Event.current;
            if (guiEvent.type == EventType.MouseDown
                || guiEvent.type == EventType.KeyDown)
            {
                Undo.RecordObject(this, "调整 SLG 骨架投影");
            }
            EditorGUI.BeginChangeCheck();
            _projectionOffset = EditorGUILayout.Vector2Field(
                new GUIContent(
                    "Position XZ",
                    "调整骨架图在参考模型上的水平位置。"),
                _projectionOffset);
            _projectionScale = EditorGUILayout.Vector2Field(
                new GUIContent(
                    "Scale XY",
                    "调整骨架图两个方向的尺寸。"),
                _projectionScale);
            _projectionScale.x = Mathf.Max(0.01f, _projectionScale.x);
            _projectionScale.y = Mathf.Max(0.01f, _projectionScale.y);
            _projectionRotation = EditorGUILayout.FloatField(
                new GUIContent(
                    "Rotation Y",
                    "绕参考模型的 Y 轴旋转骨架图。"),
                _projectionRotation);
            _tileGap = EditorGUILayout.FloatField(
                new GUIContent("Tile Gap", "0 表示无缝；大于 0 时在公共边生成可见缝隙。"),
                Mathf.Max(0f, _tileGap));
            _hideSourceOnGenerate = EditorGUILayout.Toggle(
                new GUIContent(
                    "Hide Reference After Generation",
                    "生成完成后自动隐藏原参考模型。"),
                _hideSourceOnGenerate);
            _colorPreview = EditorGUILayout.Toggle(
                new GUIContent(
                    "Random Color Preview",
                    "为输出地块分配不同颜色，便于检查切分结果。"),
                _colorPreview);
            _outlineWidth = EditorGUILayout.FloatField(
                new GUIContent("Inner Outline Width", "模型本地单位。地块缩放时描边会一起缩放。"),
                Mathf.Max(0f, _outlineWidth));
            _outlineSoftness = EditorGUILayout.Slider(
                new GUIContent("Outline Softness", "0 为硬边，1 为整圈渐变。"),
                _outlineSoftness, 0f, 1f);
            _outlineColor = EditorGUILayout.ColorField(
                new GUIContent(
                    "Outline Color (HDR)",
                    "设置地块内描边颜色，支持 HDR 亮度。"),
                _outlineColor,
                true, true, true);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent(
                    "Outline Map Resolution",
                    "生成距离图的长边分辨率；短边按线框图比例计算。"));
                _outlineMapLongSide = EditorGUILayout.IntPopup(
                    _outlineMapLongSide,
                    new[] { "512", "1024", "2048", "4096" },
                    new[] { 512, 1024, 2048, 4096 });
            }
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Outline Map Format",
                    "Shader 使用的描边距离图输出格式。"),
                new GUIContent("PNG (8-bit Grayscale, Single Channel)"));
            _skeletonSimplifyTolerance = EditorGUILayout.Slider(
                new GUIContent("Contour Simplification",
                    "识别网格像素单位。直线自动少点，细节处按最大允许偏差保留更多点。"),
                _skeletonSimplifyTolerance, 0f, 2f);
            _skeletonSmoothing = EditorGUILayout.Slider(
                new GUIContent("Skeleton Smoothing",
                    "只消除像素阶梯抖动，不负责减少顶点；端点和交会点保持固定。"),
                _skeletonSmoothing, 0f, 1f);
            _antialiasedSkeletonPreview = EditorGUILayout.Toggle(
                new GUIContent("High Quality AA Preview",
                    "关闭时使用 Unity 批量线段绘制，复杂骨架会明显更流畅。"),
                _antialiasedSkeletonPreview);
            _outputFolder = EditorGUILayout.TextField(
                new GUIContent(
                    "Output Folder",
                    "生成的地块 Mesh、材质、配置和描边图保存位置。"),
                _outputFolder);
            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(this);
                Invalidate();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Manual Skeleton Editing", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _manualConnectMode = EditorGUILayout.Toggle(
                new GUIContent("Edit Skeleton in Scene",
                    "在模型上按住左键拖动。补线会增加分界，擦线会合并相邻地块。"),
                _manualConnectMode);
            if (EditorGUI.EndChangeCheck())
            {
                ClearActiveManualStroke();
                _hasLastManualGuiPosition = false;
                RepaintAllSceneViews();
            }

            EditorGUI.BeginChangeCheck();
            _manualEditOperation = (ManualEditOperation)GUILayout.Toolbar(
                (int)_manualEditOperation,
                new[]
                {
                    new GUIContent("Add Lines", "在骨架上补充新的分界线。"),
                    new GUIContent("Erase Lines", "擦除已有分界线并合并相邻区域。")
                });
            bool isEraseOperation =
                _manualEditOperation == ManualEditOperation.Erase;
            using (new EditorGUI.DisabledScope(isEraseOperation))
            {
                _snapManualToSkeleton = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Snap Endpoints to Skeleton",
                        "将补线的起点和终点吸附到已有骨架。"),
                    _snapManualToSkeleton);
                using (new EditorGUI.DisabledScope(!_snapManualToSkeleton))
                {
                    _manualSnapDistance = EditorGUILayout.Slider(
                        new GUIContent(
                            "Snap Distance",
                            "端点能够吸附到已有骨架的最大距离。"),
                        _manualSnapDistance, 0.002f, 0.1f);
                }
            }
            if (isEraseOperation)
            {
                _manualEraseRadius = EditorGUILayout.IntSlider(
                    new GUIContent("Erase Radius",
                        "识别网格像素单位。外框会被自动保护。"),
                    _manualEraseRadius, 1, 8);
            }
            _manualStrokeSmoothing = EditorGUILayout.Slider(
                new GUIContent("Stroke Smoothing",
                    "松开鼠标后清理手抖，起点和终点不会移动。"),
                _manualStrokeSmoothing, 0f, 1f);
            _manualStrokeSimplifyTolerance = EditorGUILayout.Slider(
                new GUIContent("Stroke Simplification",
                    "以识别网格像素为单位；只删除误差范围内的多余采样点。"),
                _manualStrokeSimplifyTolerance, 0f, 3f);
            if (EditorGUI.EndChangeCheck())
            {
                ClearActiveManualStroke();
                _hasLastManualGuiPosition = false;
                EditorUtility.SetDirty(this);
                Invalidate();
            }

            EditorGUILayout.LabelField(
                _activeManualStroke.Count > 0
                    ? (isEraseOperation
                        ? "Erasing: release the mouse to apply."
                        : "Drawing: release the mouse to apply.")
                    : string.Format("Added Strokes: {0} | Erased Strokes: {1}",
                        _manualSegments.Count, _manualEraseSegments.Count),
                EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent(
                    "Remove Last Added Stroke",
                    "删除最后一次提交的补线。")))
                {
                    if (_manualSegments.Count > 0)
                    {
                        Undo.RecordObject(this, "删除 SLG 手动骨架");
                        _manualSegments.RemoveAt(_manualSegments.Count - 1);
                        EditorUtility.SetDirty(this);
                        Invalidate();
                    }
                }
                if (GUILayout.Button(new GUIContent(
                    "Remove Last Erased Stroke",
                    "撤销最后一次提交的擦线记录。")))
                {
                    if (_manualEraseSegments.Count > 0)
                    {
                        Undo.RecordObject(this, "删除 SLG 手动擦线");
                        _manualEraseSegments.RemoveAt(
                            _manualEraseSegments.Count - 1);
                        EditorUtility.SetDirty(this);
                        Invalidate();
                    }
                }
            }
            if (GUILayout.Button(new GUIContent(
                "Clear All Manual Edits",
                "清除全部手动补线和擦线记录。")))
            {
                Undo.RecordObject(this, "清空 SLG 手动骨架编辑");
                _manualSegments.Clear();
                _manualEraseSegments.Clear();
                ClearActiveManualStroke();
                _hasLastManualGuiPosition = false;
                EditorUtility.SetDirty(this);
                Invalidate();
            }

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(!CanBuild()))
            {
                if (GUILayout.Button(
                    new GUIContent(
                        "Refresh Skeleton Preview",
                        "重新识别线框并刷新模型上的骨架预览。"),
                    GUILayout.Height(30f)))
                {
                    Rebuild();
                }
                if (GUILayout.Button(
                    new GUIContent(
                        "Generate Independent Tile Meshes",
                        "根据当前骨架生成相互贴合的独立地块 Mesh。"),
                    GUILayout.Height(34f)))
                {
                    GenerateTiles();
                }
            }

            if (_labels != null)
            {
                EditorGUILayout.HelpBox(
                    string.Format("已识别 {0} 个地块，识别网格为 {1} x {2}。",
                        _tileCount, _gridWidth, _gridHeight),
                    MessageType.None);
                EditorGUILayout.LabelField(string.Format(
                    "Performance: Recognition {0:F1} ms | Vectorization {1:F1} ms | "
                    + "Projection {2:F1} ms | Preview {3:F2} ms | "
                    + "{4} Paths / {5} Points",
                    _lastRecognitionMilliseconds,
                    _lastVectorizationMilliseconds,
                    _lastProjectionMilliseconds,
                    _lastPreviewDrawMilliseconds,
                    _previewWorldPaths.Count,
                    _lastPreviewPointCount),
                    EditorStyles.miniLabel);
                if (_previewFallbackCount > 0)
                {
                    EditorGUILayout.HelpBox(
                        "部分骨架没有命中模型表面，已显示在模型上方的诊断平面。"
                        + "请确认参考模型是朝上的 MeshFilter。",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private bool CanBuild()
        {
            return _lineTexture != null && _target != null && _target.sharedMesh != null;
        }

        private void TryUseSelection()
        {
            if (Selection.activeGameObject != null)
            {
                MeshFilter filter = Selection.activeGameObject.GetComponent<MeshFilter>();
                if (filter != null)
                {
                    _target = filter;
                    Invalidate();
                }
            }
        }

        private void LoadConfiguration(SLGMapTileMapAsset configuration)
        {
            if (configuration == null)
            {
                return;
            }

            Undo.RecordObject(this, "读取 SLG 地块设置");
            _configuration = configuration;
            _lineTexture = configuration.lineTexture;
            _darkThreshold = configuration.darkThreshold;
            _gapCloseRadius = Mathf.Max(0, configuration.closeRadius);
            _tileGap = Mathf.Max(0f, configuration.tileGap);
            _projectionOffset = configuration.projectionOffset;
            _projectionScale = new Vector2(
                Mathf.Max(0.01f, configuration.projectionScale.x),
                Mathf.Max(0.01f, configuration.projectionScale.y));
            _projectionRotation = configuration.projectionRotation;
            _manualSegments.Clear();
            if (configuration.manualSegments != null)
            {
                _manualSegments.AddRange(configuration.manualSegments);
            }
            _manualEraseSegments.Clear();
            if (configuration.manualEraseSegments != null)
            {
                _manualEraseSegments.AddRange(
                    configuration.manualEraseSegments);
            }

            if (configuration.version >= 2)
            {
                _longSideResolution = Mathf.Clamp(
                    configuration.longSideResolution, 32, 512);
                _minimumRegionPixels = Mathf.Clamp(
                    configuration.minimumRegionPixels, 1, 128);
                _skeletonSimplifyTolerance = Mathf.Clamp(
                    configuration.skeletonSimplifyTolerance, 0f, 2f);
                _hideSourceOnGenerate = configuration.hideSourceOnGenerate;
                _colorPreview = configuration.colorPreview;
                _snapManualToSkeleton = configuration.snapManualToSkeleton;
                _manualSnapDistance = Mathf.Clamp(
                    configuration.manualSnapDistance, 0.002f, 0.1f);
                _outlineWidth = Mathf.Max(0f, configuration.outlineWidth);
                _outlineSoftness = Mathf.Clamp01(configuration.outlineSoftness);
                _outlineColor = configuration.outlineColor;
                if (configuration.version >= 3)
                {
                    _skeletonSmoothing = Mathf.Clamp01(
                        configuration.skeletonSmoothing);
                    _manualStrokeSmoothing = Mathf.Clamp01(
                        configuration.manualStrokeSmoothing);
                    _manualStrokeSimplifyTolerance = Mathf.Clamp(
                        configuration.manualStrokeSimplifyTolerance, 0f, 3f);
                }
                if (configuration.version >= 4)
                {
                    _manualEraseRadius = Mathf.Clamp(
                        configuration.manualEraseRadius, 1, 8);
                }
                if (configuration.version >= 5)
                {
                    _outlineMapLongSide = Mathf.Clamp(
                        configuration.outlineMapLongSide, 512, 4096);
                }
                else
                {
                    _outlineMapLongSide = Mathf.Clamp(
                        configuration.longSideResolution * 4,
                        1024, 2048);
                }
                if (!string.IsNullOrWhiteSpace(configuration.outputFolder))
                {
                    _outputFolder = configuration.outputFolder;
                }
            }
            else
            {
                // Older assets did not contain a complete settings snapshot.
                _longSideResolution = Mathf.Clamp(
                    Mathf.Max(configuration.gridWidth, configuration.gridHeight),
                    32, 512);
            }

            if (_target == null || _target.sharedMesh != configuration.sourceMesh)
            {
                _target = FindSceneMeshFilter(configuration.sourceMesh);
            }

            ClearActiveManualStroke();
            _surfaceProjectionCache.Clear();
            EditorUtility.SetDirty(this);
            Invalidate();
            if (CanBuild())
            {
                Rebuild();
            }
            else if (configuration.sourceMesh != null)
            {
                Debug.LogWarning(
                    "已读取地块设置，但当前场景中没有找到使用同一源 Mesh 的模型。"
                    + "请在工具中重新指定参考模型。", configuration);
            }
        }

        private static MeshFilter FindSceneMeshFilter(Mesh sourceMesh)
        {
            if (sourceMesh == null)
            {
                return null;
            }

            MeshFilter inactiveMatch = null;
            MeshFilter[] filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                if (filter == null || filter.sharedMesh != sourceMesh
                    || EditorUtility.IsPersistent(filter)
                    || !filter.gameObject.scene.IsValid())
                {
                    continue;
                }
                if (filter.gameObject.activeInHierarchy)
                {
                    return filter;
                }
                inactiveMatch = filter;
            }
            return inactiveMatch;
        }

        private void Invalidate()
        {
            _needsRebuild = true;
            _rebuildNotBefore = EditorApplication.timeSinceStartup
                + RebuildDebounceSeconds;
            Repaint();
            RepaintAllSceneViews();
        }

        private static void RepaintAllSceneViews()
        {
            SceneView.RepaintAll();
        }

        private void Rebuild()
        {
            if (!CanBuild() || _isBuilding)
            {
                return;
            }

            try
            {
                _isBuilding = true;
                if (!EnsureTextureReadable(false))
                {
                    return;
                }
                System.Diagnostics.Stopwatch timer =
                    System.Diagnostics.Stopwatch.StartNew();
                BuildRegionLabels();
                timer.Stop();
                _lastRecognitionMilliseconds =
                    timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                BuildPreviewSegments();
                timer.Stop();
                _lastVectorizationMilliseconds =
                    Math.Max(0d, timer.Elapsed.TotalMilliseconds
                        - _lastProjectionMilliseconds);
                _needsRebuild = false;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("SLG 地块切分", exception.Message, "确定");
            }
            finally
            {
                _isBuilding = false;
                RepaintAllSceneViews();
                Repaint();
            }
        }

        private void BuildRegionLabels()
        {
            int imageWidth = Mathf.Max(1, _lineTexture.width);
            int imageHeight = Mathf.Max(1, _lineTexture.height);
            if (imageWidth >= imageHeight)
            {
                _gridWidth = _longSideResolution;
                _gridHeight = Mathf.Max(8,
                    Mathf.RoundToInt(_longSideResolution * imageHeight / (float)imageWidth));
            }
            else
            {
                _gridHeight = _longSideResolution;
                _gridWidth = Mathf.Max(8,
                    Mathf.RoundToInt(_longSideResolution * imageWidth / (float)imageHeight));
            }

            int count = _gridWidth * _gridHeight;
            bool[] lineMask = GetImportedLineMask();
            // Automatic gap closing is only for the imported line image.
            // Applying it to authored strokes merges a new line into a nearby
            // existing boundary and makes narrow, intentional regions disappear.
            bool[] manualLineMask = new bool[count];
            RasterizeManualSegments(manualLineMask);
            MergeLineMask(lineMask, manualLineMask);
            // Erasing is applied after automatic closing so the same closing pass
            // cannot immediately heal the artist-authored opening.
            RasterizeManualEraseSegments(lineMask);

            // The picture rectangle is the default outer boundary for partial edge tiles.
            for (int x = 0; x < _gridWidth; x++)
            {
                lineMask[Index(x, 0)] = true;
                lineMask[Index(x, _gridHeight - 1)] = true;
            }
            for (int y = 0; y < _gridHeight; y++)
            {
                lineMask[Index(0, y)] = true;
                lineMask[Index(_gridWidth - 1, y)] = true;
            }

            _labels = new int[count];
            for (int i = 0; i < count; i++)
            {
                _labels[i] = -1;
            }

            int nextLabel = 0;
            Queue<int> queue = new Queue<int>();
            List<int> component = new List<int>();
            for (int start = 0; start < count; start++)
            {
                if (lineMask[start] || _labels[start] >= 0)
                {
                    continue;
                }

                component.Clear();
                _labels[start] = nextLabel;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    component.Add(current);
                    EnqueueOpenNeighbours(current, nextLabel, lineMask, queue);
                }

                if (component.Count < _minimumRegionPixels
                    && !ComponentTouchesMask(component, manualLineMask))
                {
                    for (int i = 0; i < component.Count; i++)
                    {
                        int cell = component[i];
                        _labels[cell] = -1;
                        lineMask[cell] = true;
                    }
                }
                else
                {
                    nextLabel++;
                }
            }

            if (nextLabel == 0)
            {
                throw new InvalidOperationException("没有识别到封闭地块。请检查线框阈值、闭合距离或图片内容。");
            }

            // Assign the line band to the nearest open region with an exact squared
            // Euclidean transform. The old four-neighbour flood used Manhattan
            // distance, which visibly biased diagonal borders toward the grid axes.
            FillLinePixelsByEuclideanNearestRegion();

            _tileCount = nextLabel;
        }

        private static void MergeLineMask(bool[] destination, bool[] source)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                destination[i] |= source[i];
            }
        }

        private bool ComponentTouchesMask(List<int> component, bool[] mask)
        {
            for (int i = 0; i < component.Count; i++)
            {
                int index = component[i];
                int centerX = index % _gridWidth;
                int centerY = index / _gridWidth;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int x = centerX + offsetX;
                        int y = centerY + offsetY;
                        if (IsInside(x, y) && mask[Index(x, y)])
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void FillLinePixelsByEuclideanNearestRegion()
        {
            int count = _gridWidth * _gridHeight;
            int[] sourceLabels = (int[])_labels.Clone();
            float[] verticalDistance = new float[count];
            int[] verticalSiteY = new int[count];
            int maxLength = Mathf.Max(_gridWidth, _gridHeight);
            float[] source = new float[maxLength];
            float[] result = new float[maxLength];
            int[] nearest = new int[maxLength];
            int[] sites = new int[maxLength];
            float[] intersections = new float[maxLength + 1];

            for (int x = 0; x < _gridWidth; x++)
            {
                for (int y = 0; y < _gridHeight; y++)
                {
                    source[y] = sourceLabels[Index(x, y)] >= 0
                        ? 0f
                        : float.PositiveInfinity;
                }
                DistanceTransform1DWithSites(source, result, nearest,
                    _gridHeight, sites, intersections);
                for (int y = 0; y < _gridHeight; y++)
                {
                    int index = Index(x, y);
                    verticalDistance[index] = result[y];
                    verticalSiteY[index] = nearest[y];
                }
            }

            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    source[x] = verticalDistance[Index(x, y)];
                }
                DistanceTransform1DWithSites(source, result, nearest,
                    _gridWidth, sites, intersections);
                for (int x = 0; x < _gridWidth; x++)
                {
                    int index = Index(x, y);
                    if (sourceLabels[index] >= 0)
                    {
                        continue;
                    }
                    int sourceX = nearest[x];
                    if (sourceX < 0)
                    {
                        continue;
                    }
                    int sourceY = verticalSiteY[Index(sourceX, y)];
                    if (sourceY >= 0)
                    {
                        _labels[index] = sourceLabels[Index(sourceX, sourceY)];
                    }
                }
            }
        }

        private static void DistanceTransform1DWithSites(float[] source,
            float[] result, int[] nearest, int length, int[] sites,
            float[] intersections)
        {
            int envelopeCount = 0;
            for (int q = 0; q < length; q++)
            {
                if (float.IsInfinity(source[q]))
                {
                    continue;
                }

                if (envelopeCount == 0)
                {
                    sites[0] = q;
                    intersections[0] = float.NegativeInfinity;
                    intersections[1] = float.PositiveInfinity;
                    envelopeCount = 1;
                    continue;
                }

                float intersection;
                while (true)
                {
                    int previous = sites[envelopeCount - 1];
                    intersection = ((source[q] + q * q)
                        - (source[previous] + previous * previous))
                        / (2f * (q - previous));
                    if (envelopeCount == 1
                        || intersection > intersections[envelopeCount - 1])
                    {
                        break;
                    }
                    envelopeCount--;
                }
                sites[envelopeCount] = q;
                intersections[envelopeCount] = intersection;
                intersections[envelopeCount + 1] = float.PositiveInfinity;
                envelopeCount++;
            }

            if (envelopeCount == 0)
            {
                for (int q = 0; q < length; q++)
                {
                    result[q] = float.PositiveInfinity;
                    nearest[q] = -1;
                }
                return;
            }

            int envelope = 0;
            for (int q = 0; q < length; q++)
            {
                while (envelope + 1 < envelopeCount
                    && intersections[envelope + 1] < q)
                {
                    envelope++;
                }
                int site = sites[envelope];
                float delta = q - site;
                result[q] = delta * delta + source[site];
                nearest[q] = site;
            }
        }

        private bool[] GetImportedLineMask()
        {
            if (_cachedImportedLineMask != null
                && _cachedLineTexture == _lineTexture
                && _cachedLineGridWidth == _gridWidth
                && _cachedLineGridHeight == _gridHeight
                && Mathf.Approximately(_cachedDarkThreshold,
                    _darkThreshold)
                && _cachedGapCloseRadius == _gapCloseRadius)
            {
                return (bool[])_cachedImportedLineMask.Clone();
            }

            Color32[] pixels = _lineTexture.GetPixels32();
            int imageWidth = _lineTexture.width;
            int imageHeight = _lineTexture.height;
            bool[] imported = new bool[_gridWidth * _gridHeight];
            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    imported[Index(x, y)] = IsLineCellFromPixels(
                        x, y, pixels, imageWidth, imageHeight);
                }
            }
            if (_gapCloseRadius > 0)
            {
                imported = CloseMask(imported, _gapCloseRadius);
            }

            _cachedImportedLineMask = (bool[])imported.Clone();
            _cachedLineTexture = _lineTexture;
            _cachedLineGridWidth = _gridWidth;
            _cachedLineGridHeight = _gridHeight;
            _cachedDarkThreshold = _darkThreshold;
            _cachedGapCloseRadius = _gapCloseRadius;
            return imported;
        }

        private bool IsLineCellFromPixels(int x, int y,
            Color32[] pixels, int imageWidth, int imageHeight)
        {
            const int samplesPerAxis = 2;
            for (int sampleY = 0; sampleY < samplesPerAxis; sampleY++)
            {
                for (int sampleX = 0; sampleX < samplesPerAxis; sampleX++)
                {
                    float u = (x + (sampleX + 0.5f)
                        / samplesPerAxis) / _gridWidth;
                    float v = (y + (sampleY + 0.5f)
                        / samplesPerAxis) / _gridHeight;
                    if (IsDarkBilinearSample(pixels, imageWidth,
                        imageHeight, u, v))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private bool IsDarkBilinearSample(Color32[] pixels,
            int width, int height, float u, float v)
        {
            float pixelX = Mathf.Clamp01(u) * (width - 1);
            float pixelY = Mathf.Clamp01(v) * (height - 1);
            int x0 = Mathf.FloorToInt(pixelX);
            int y0 = Mathf.FloorToInt(pixelY);
            int x1 = Mathf.Min(x0 + 1, width - 1);
            int y1 = Mathf.Min(y0 + 1, height - 1);
            float tx = pixelX - x0;
            float ty = pixelY - y0;
            Color32 bottomLeft = pixels[y0 * width + x0];
            Color32 bottomRight = pixels[y0 * width + x1];
            Color32 topLeft = pixels[y1 * width + x0];
            Color32 topRight = pixels[y1 * width + x1];
            float alpha = BilinearByte(bottomLeft.a, bottomRight.a,
                topLeft.a, topRight.a, tx, ty);
            if (alpha <= 12.75f)
            {
                return false;
            }
            float red = BilinearByte(bottomLeft.r, bottomRight.r,
                topLeft.r, topRight.r, tx, ty);
            float green = BilinearByte(bottomLeft.g, bottomRight.g,
                topLeft.g, topRight.g, tx, ty);
            float blue = BilinearByte(bottomLeft.b, bottomRight.b,
                topLeft.b, topRight.b, tx, ty);
            float luminance = red * 0.2126f + green * 0.7152f
                + blue * 0.0722f;
            return luminance <= _darkThreshold * 255f;
        }

        private static float BilinearByte(byte bottomLeft,
            byte bottomRight, byte topLeft, byte topRight,
            float tx, float ty)
        {
            float bottom = Mathf.Lerp(bottomLeft, bottomRight, tx);
            float top = Mathf.Lerp(topLeft, topRight, tx);
            return Mathf.Lerp(bottom, top, ty);
        }

        private void EnqueueOpenNeighbours(int index, int label, bool[] lineMask,
            Queue<int> queue)
        {
            int x = index % _gridWidth;
            int y = index / _gridWidth;
            TryEnqueueOpen(x - 1, y, label, lineMask, queue);
            TryEnqueueOpen(x + 1, y, label, lineMask, queue);
            TryEnqueueOpen(x, y - 1, label, lineMask, queue);
            TryEnqueueOpen(x, y + 1, label, lineMask, queue);
        }

        private void TryEnqueueOpen(int x, int y, int label, bool[] lineMask,
            Queue<int> queue)
        {
            if (!IsInside(x, y))
            {
                return;
            }
            int index = Index(x, y);
            if (lineMask[index] || _labels[index] >= 0)
            {
                return;
            }
            _labels[index] = label;
            queue.Enqueue(index);
        }

        private void PropagateLabel(int index, int label, Queue<int> queue)
        {
            int x = index % _gridWidth;
            int y = index / _gridWidth;
            TryPropagateLabel(x - 1, y, label, queue);
            TryPropagateLabel(x + 1, y, label, queue);
            TryPropagateLabel(x, y - 1, label, queue);
            TryPropagateLabel(x, y + 1, label, queue);
        }

        private void TryPropagateLabel(int x, int y, int label, Queue<int> queue)
        {
            if (!IsInside(x, y))
            {
                return;
            }
            int neighbour = Index(x, y);
            if (_labels[neighbour] >= 0)
            {
                return;
            }
            _labels[neighbour] = label;
            queue.Enqueue(neighbour);
        }

        private void RasterizeManualSegments(bool[] lineMask)
        {
            for (int i = 0; i < _manualSegments.Count; i++)
            {
                SLGMapTileManualSegment segment = _manualSegments[i];
                int x0 = Mathf.RoundToInt(segment.start.x * (_gridWidth - 1));
                int y0 = Mathf.RoundToInt(segment.start.y * (_gridHeight - 1));
                int x1 = Mathf.RoundToInt(segment.end.x * (_gridWidth - 1));
                int y1 = Mathf.RoundToInt(segment.end.y * (_gridHeight - 1));
                RasterizeLine(lineMask, x0, y0, x1, y1);
            }
        }

        private void RasterizeManualEraseSegments(bool[] lineMask)
        {
            int radius = Mathf.Max(1, _manualEraseRadius);
            for (int i = 0; i < _manualEraseSegments.Count; i++)
            {
                SLGMapTileManualSegment segment = _manualEraseSegments[i];
                int x0 = Mathf.RoundToInt(segment.start.x * (_gridWidth - 1));
                int y0 = Mathf.RoundToInt(segment.start.y * (_gridHeight - 1));
                int x1 = Mathf.RoundToInt(segment.end.x * (_gridWidth - 1));
                int y1 = Mathf.RoundToInt(segment.end.y * (_gridHeight - 1));
                RasterizeEraseLine(lineMask, x0, y0, x1, y1, radius);
            }
        }

        private void RasterizeEraseLine(bool[] lineMask, int x0, int y0,
            int x1, int y1, int radius)
        {
            int dx = Mathf.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                ClearMaskDisk(lineMask, x0, y0, radius);
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                int twiceError = error * 2;
                if (twiceError >= dy)
                {
                    error += dy;
                    x0 += sx;
                }
                if (twiceError <= dx)
                {
                    error += dx;
                    y0 += sy;
                }
            }
        }

        private void ClearMaskDisk(bool[] lineMask, int centerX, int centerY,
            int radius)
        {
            int radiusSquared = radius * radius;
            for (int offsetY = -radius; offsetY <= radius; offsetY++)
            {
                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    if (offsetX * offsetX + offsetY * offsetY
                            > radiusSquared
                        || !IsInside(centerX + offsetX, centerY + offsetY))
                    {
                        continue;
                    }
                    lineMask[Index(centerX + offsetX,
                        centerY + offsetY)] = false;
                }
            }
        }

        private void RasterizeLine(bool[] lineMask, int x0, int y0, int x1, int y1)
        {
            int dx = Mathf.Abs(x1 - x0);
            int sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0);
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                if (IsInside(x0, y0))
                {
                    lineMask[Index(x0, y0)] = true;
                }
                if (x0 == x1 && y0 == y1)
                {
                    break;
                }
                int twiceError = error * 2;
                if (twiceError >= dy)
                {
                    error += dy;
                    x0 += sx;
                }
                if (twiceError <= dx)
                {
                    error += dx;
                    y0 += sy;
                }
            }
        }

        private bool[] CloseMask(bool[] source, int radius)
        {
            bool[] dilated = new bool[source.Length];
            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    bool found = false;
                    for (int oy = -radius; oy <= radius && !found; oy++)
                    {
                        for (int ox = -radius; ox <= radius; ox++)
                        {
                            if (IsInside(x + ox, y + oy) && source[Index(x + ox, y + oy)])
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                    dilated[Index(x, y)] = found;
                }
            }

            bool[] closed = new bool[source.Length];
            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    bool filled = true;
                    for (int oy = -radius; oy <= radius && filled; oy++)
                    {
                        for (int ox = -radius; ox <= radius; ox++)
                        {
                            if (!IsInside(x + ox, y + oy) || !dilated[Index(x + ox, y + oy)])
                            {
                                filled = false;
                                break;
                            }
                        }
                    }
                    closed[Index(x, y)] = filled;
                }
            }
            return closed;
        }

        private void BuildPreviewSegments()
        {
            _previewSegments.Clear();
            _previewGridPaths.Clear();
            _previewUvPaths.Clear();
            _previewWorldPaths.Clear();
            _previewLinePairs = new Vector3[0];
            _vectorSkeletonPaths.Clear();
            _vectorizedGridPoints.Clear();
            _previewSurfaceHitCount = 0;
            _previewFallbackCount = 0;
            if (_labels == null || _target == null)
            {
                return;
            }

            EnsurePreviewCollider();
            List<GridSegment> gridSegments = new List<GridSegment>();
            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int label = _labels[Index(x, y)];
                    if (label < 0)
                    {
                        continue;
                    }

                    // The map image is also a cut domain.  Internal borders alone
                    // leave the outer ring visually open, which is confusing when
                    // authors are checking whether their complete province map was
                    // recognised.  Draw its perimeter as part of the skeleton.
                    if (x == 0)
                    {
                        AddGridSegment(gridSegments, 0, y, 0, y + 1);
                    }
                    if (x == _gridWidth - 1)
                    {
                        AddGridSegment(gridSegments, _gridWidth, y, _gridWidth, y + 1);
                    }
                    if (y == 0)
                    {
                        AddGridSegment(gridSegments, x, 0, x + 1, 0);
                    }
                    if (y == _gridHeight - 1)
                    {
                        AddGridSegment(gridSegments, x, _gridHeight, x + 1, _gridHeight);
                    }
                    if (x + 1 < _gridWidth && label != _labels[Index(x + 1, y)])
                    {
                        AddGridSegment(gridSegments, x + 1, y, x + 1, y + 1);
                    }
                    if (y + 1 < _gridHeight && label != _labels[Index(x, y + 1)])
                    {
                        AddGridSegment(gridSegments, x, y + 1, x + 1, y + 1);
                    }
                }
            }

            AddSmoothedPreviewPaths(gridSegments);
            StabilizeVectorizedGrid();
            BuildPreviewUvPaths();
            ReprojectPreviewPaths();
        }

        private void AddExactVectorizedPreview(List<GridSegment> gridSegments)
        {
            for (int i = 0; i < gridSegments.Count; i++)
            {
                GridSegment segment = gridSegments[i];
                Vector2 a = GetVectorizedGridPoint(segment.a.x, segment.a.y);
                Vector2 b = GetVectorizedGridPoint(segment.b.x, segment.b.y);
                if ((b - a).sqrMagnitude <= 0.00000001f)
                {
                    continue;
                }
                AddPreviewSegment(GridToImageUV(a), GridToImageUV(b));
            }
        }

        private static void AddGridSegment(List<GridSegment> segments,
            int ax, int ay, int bx, int by)
        {
            segments.Add(new GridSegment
            {
                a = new Vector2Int(ax, ay),
                b = new Vector2Int(bx, by)
            });
        }

        private void AddSmoothedPreviewPaths(List<GridSegment> segments)
        {
            Dictionary<Vector2Int, List<int>> adjacency =
                new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                AddAdjacentSegment(adjacency, segments[i].a, i);
                AddAdjacentSegment(adjacency, segments[i].b, i);
            }

            bool[] visited = new bool[segments.Count];
            foreach (KeyValuePair<Vector2Int, List<int>> pair in adjacency)
            {
                if (pair.Value.Count == 2)
                {
                    continue;
                }
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    int segmentIndex = pair.Value[i];
                    if (!visited[segmentIndex])
                    {
                        AddSmoothedPath(segments, adjacency, visited, segmentIndex, pair.Key);
                    }
                }
            }
            for (int i = 0; i < segments.Count; i++)
            {
                if (!visited[i])
                {
                    AddSmoothedPath(segments, adjacency, visited, i, segments[i].a);
                }
            }
        }

        private static void AddAdjacentSegment(Dictionary<Vector2Int, List<int>> adjacency,
            Vector2Int point, int index)
        {
            if (!adjacency.TryGetValue(point, out List<int> indices))
            {
                indices = new List<int>();
                adjacency.Add(point, indices);
            }
            indices.Add(index);
        }

        private void AddSmoothedPath(List<GridSegment> segments,
            Dictionary<Vector2Int, List<int>> adjacency, bool[] visited,
            int firstSegment, Vector2Int start)
        {
            List<Vector2> path = new List<Vector2>
            {
                new Vector2(start.x, start.y)
            };
            int currentSegment = firstSegment;
            Vector2Int currentPoint = start;
            while (currentSegment >= 0 && !visited[currentSegment])
            {
                visited[currentSegment] = true;
                GridSegment segment = segments[currentSegment];
                currentPoint = segment.a == currentPoint ? segment.b : segment.a;
                path.Add(new Vector2(currentPoint.x, currentPoint.y));
                if (!adjacency.TryGetValue(currentPoint, out List<int> nextCandidates)
                    || nextCandidates.Count != 2)
                {
                    break;
                }
                int next = nextCandidates[0] == currentSegment
                    ? nextCandidates[1]
                    : nextCandidates[0];
                if (visited[next])
                {
                    break;
                }
                currentSegment = next;
            }

            if (path.Count < 2)
            {
                return;
            }
            List<Vector2Int> gridPath = new List<Vector2Int>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                gridPath.Add(new Vector2Int(
                    Mathf.RoundToInt(path[i].x),
                    Mathf.RoundToInt(path[i].y)));
            }
            _previewGridPaths.Add(gridPath);
            bool isClosed = path.Count > 3 && path[0] == path[path.Count - 1];
            List<Vector2> prepared = SmoothRasterPath(path, isClosed);
            List<Vector2> simplified = isClosed
                ? SimplifyClosedPath(prepared)
                : SimplifyPath(prepared);
            if (simplified.Count >= 2)
            {
                _vectorSkeletonPaths.Add(new List<Vector2>(simplified));
                MapPathOntoSimplifiedPolyline(path, simplified);
            }
        }

        private List<Vector2> SmoothRasterPath(List<Vector2> path, bool isClosed)
        {
            List<Vector2> current = new List<Vector2>(path);
            if (_skeletonSmoothing <= 0.0001f || path.Count < 3)
            {
                return current;
            }

            int uniqueCount = isClosed ? path.Count - 1 : path.Count;
            if (uniqueCount < 3)
            {
                return current;
            }

            int passes = _skeletonSmoothing > 0.66f ? 2 : 1;
            float blend = passes == 1
                ? _skeletonSmoothing
                : Mathf.Clamp01(_skeletonSmoothing * 0.65f);
            for (int pass = 0; pass < passes; pass++)
            {
                List<Vector2> next = new List<Vector2>(current);
                for (int i = 0; i < uniqueCount; i++)
                {
                    if (!isClosed && (i == 0 || i == uniqueCount - 1))
                    {
                        continue;
                    }
                    int previous = (i - 1 + uniqueCount) % uniqueCount;
                    int following = (i + 1) % uniqueCount;
                    Vector2 localAverage =
                        (current[previous] + current[i] * 2f
                            + current[following]) * 0.25f;
                    next[i] = Vector2.LerpUnclamped(current[i],
                        localAverage, blend);
                }
                if (isClosed)
                {
                    next[next.Count - 1] = next[0];
                }
                current = next;
            }
            return current;
        }

        private void MapPathOntoSimplifiedPolyline(List<Vector2> path,
            List<Vector2> simplified)
        {
            // Project each source boundary point to its geometrically closest
            // position. The old arc-length redistribution spread all grid points
            // evenly over a chord and erased local detail.
            MapPathByClosestPoint(path, simplified);
        }

        private static int FindPathPoint(List<Vector2> path, Vector2 point, int start)
        {
            const float epsilonSquared = 0.000001f;
            for (int i = Mathf.Max(0, start); i < path.Count; i++)
            {
                if ((path[i] - point).sqrMagnitude <= epsilonSquared)
                {
                    return i;
                }
            }
            return -1;
        }

        private void MapPathByClosestPoint(List<Vector2> path,
            List<Vector2> simplified)
        {
            int nearestSegment = 1;
            for (int i = 0; i < path.Count; i++)
            {
                Vector2Int gridPoint = new Vector2Int(
                    Mathf.RoundToInt(path[i].x), Mathf.RoundToInt(path[i].y));
                _vectorizedGridPoints[gridPoint] =
                    ClosestPointOnOrderedPolyline(path[i], simplified,
                        ref nearestSegment);
            }
        }

        private static Vector2 ClosestPointOnOrderedPolyline(
            Vector2 point, List<Vector2> polyline,
            ref int nearestSegment)
        {
            int lastSegment = polyline.Count - 1;
            if (lastSegment < 1)
            {
                return point;
            }

            const int lookBehind = 2;
            const int lookAhead = 12;
            int first = Mathf.Max(1, nearestSegment - lookBehind);
            int last = Mathf.Min(lastSegment,
                nearestSegment + lookAhead);
            Vector2 best = point;
            float bestDistance = float.MaxValue;
            int bestSegment = nearestSegment;
            for (int segment = first; segment <= last; segment++)
            {
                Vector2 candidate = ClosestPointOnSegment(point,
                    polyline[segment - 1], polyline[segment]);
                float distance = (candidate - point).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                    bestSegment = segment;
                }
            }
            nearestSegment = bestSegment;
            return best;
        }

        private static Vector2 ClosestPointOnSegment(Vector2 point,
            Vector2 a, Vector2 b)
        {
            Vector2 direction = b - a;
            float lengthSquared = direction.sqrMagnitude;
            float t = lengthSquared <= 0.000001f
                ? 0f
                : Mathf.Clamp01(Vector2.Dot(point - a, direction)
                    / lengthSquared);
            return a + direction * t;
        }

        private List<Vector2> SimplifyClosedPath(List<Vector2> path)
        {
            path.RemoveAt(path.Count - 1);
            int pivot = Mathf.Max(1, path.Count / 2);
            List<Vector2> first = SimplifyPath(path.GetRange(0, pivot + 1));
            List<Vector2> secondInput = path.GetRange(pivot, path.Count - pivot);
            secondInput.Add(path[0]);
            List<Vector2> second = SimplifyPath(secondInput);
            first.RemoveAt(first.Count - 1);
            first.AddRange(second);
            return first;
        }

        private List<Vector2> SimplifyPath(List<Vector2> path)
        {
            return SimplifyPath(path, _skeletonSimplifyTolerance);
        }

        private static List<Vector2> SimplifyPath(List<Vector2> path,
            float tolerance)
        {
            if (tolerance <= 0f || path.Count <= 2)
            {
                return new List<Vector2>(path);
            }
            bool[] keep = new bool[path.Count];
            keep[0] = true;
            keep[path.Count - 1] = true;
            MarkSimplifiedPathPoints(path, 0, path.Count - 1, keep,
                tolerance);
            List<Vector2> simplified = new List<Vector2>();
            for (int i = 0; i < path.Count; i++)
            {
                if (keep[i])
                {
                    simplified.Add(path[i]);
                }
            }
            return simplified;
        }

        private static void MarkSimplifiedPathPoints(List<Vector2> path,
            int first, int last, bool[] keep, float tolerance)
        {
            if (last <= first + 1)
            {
                return;
            }
            Vector2 a = path[first];
            Vector2 b = path[last];
            float greatestDistance = -1f;
            int farthest = -1;
            for (int i = first + 1; i < last; i++)
            {
                float distance = DistanceToLine(path[i], a, b);
                if (distance > greatestDistance)
                {
                    greatestDistance = distance;
                    farthest = i;
                }
            }
            if (greatestDistance <= tolerance || farthest < 0)
            {
                return;
            }
            keep[farthest] = true;
            MarkSimplifiedPathPoints(path, first, farthest, keep, tolerance);
            MarkSimplifiedPathPoints(path, farthest, last, keep, tolerance);
        }

        private static float DistanceToLine(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 direction = b - a;
            float length = direction.magnitude;
            if (length < 0.0001f)
            {
                return Vector2.Distance(point, a);
            }
            return Mathf.Abs(direction.x * (a.y - point.y)
                - (a.x - point.x) * direction.y) / length;
        }

        private Vector2 GridToImageUV(Vector2 point)
        {
            return new Vector2(point.x / _gridWidth, point.y / _gridHeight);
        }

        private static Vector2 ClosestPointOnPolyline(Vector2 point,
            List<Vector2> polyline)
        {
            Vector2 best = point;
            float bestDistance = float.MaxValue;
            for (int i = 1; i < polyline.Count; i++)
            {
                Vector2 a = polyline[i - 1];
                Vector2 b = polyline[i];
                Vector2 direction = b - a;
                float lengthSquared = direction.sqrMagnitude;
                float t = lengthSquared <= 0.000001f
                    ? 0f
                    : Mathf.Clamp01(Vector2.Dot(point - a, direction) / lengthSquared);
                Vector2 candidate = a + direction * t;
                float distance = (candidate - point).sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            return best;
        }

        private Vector2 GetVectorizedGridPoint(int x, int y)
        {
            Vector2Int key = new Vector2Int(x, y);
            return _vectorizedGridPoints.TryGetValue(key, out Vector2 point)
                ? point
                : new Vector2(x, y);
        }

        private void StabilizeVectorizedGrid()
        {
            if (_vectorizedGridPoints.Count == 0)
            {
                return;
            }

            List<int> affectedCells = CollectVectorizedAffectedCells();
            if (IsVectorizedGridValid(1f, affectedCells))
            {
                return;
            }

            float valid = 0f;
            float invalid = 1f;
            for (int i = 0; i < 12; i++)
            {
                float candidate = (valid + invalid) * 0.5f;
                if (IsVectorizedGridValid(candidate, affectedCells))
                {
                    valid = candidate;
                }
                else
                {
                    invalid = candidate;
                }
            }

            List<Vector2Int> keys = new List<Vector2Int>(_vectorizedGridPoints.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                Vector2Int key = keys[i];
                Vector2 original = new Vector2(key.x, key.y);
                _vectorizedGridPoints[key] = Vector2.Lerp(original,
                    _vectorizedGridPoints[key], valid * 0.999f);
            }
        }

        private List<int> CollectVectorizedAffectedCells()
        {
            HashSet<int> affected = new HashSet<int>();
            foreach (Vector2Int point in _vectorizedGridPoints.Keys)
            {
                for (int offsetY = -1; offsetY <= 0; offsetY++)
                {
                    int cellY = point.y + offsetY;
                    if (cellY < 0 || cellY >= _gridHeight)
                    {
                        continue;
                    }
                    for (int offsetX = -1; offsetX <= 0; offsetX++)
                    {
                        int cellX = point.x + offsetX;
                        if (cellX >= 0 && cellX < _gridWidth)
                        {
                            affected.Add(cellY * _gridWidth + cellX);
                        }
                    }
                }
            }
            return new List<int>(affected);
        }

        private bool IsVectorizedGridValid(float blend,
            List<int> affectedCells)
        {
            // A zero-area projected cell collapses several different boundary
            // vertices to the same position. TileBuilder then welds them and the
            // triangulation loses faces, producing the thin slivers and holes that
            // are especially visible in wireframe mode. Keep at least one percent
            // of the original unit-cell triangle area. This is visually negligible
            // but preserves an injective shared boundary map.
            const float minimumArea = 0.01f;
            for (int i = 0; i < affectedCells.Count; i++)
            {
                int cell = affectedCells[i];
                int x = cell % _gridWidth;
                int y = cell / _gridWidth;
                Vector2 bottomLeft = BlendedGridPoint(x, y, blend);
                Vector2 bottomRight = BlendedGridPoint(x + 1, y, blend);
                Vector2 topRight = BlendedGridPoint(x + 1, y + 1, blend);
                Vector2 topLeft = BlendedGridPoint(x, y + 1, blend);
                if (Cross2D(bottomRight - bottomLeft,
                        topRight - bottomLeft) < minimumArea
                    || Cross2D(topRight - bottomLeft,
                        topLeft - bottomLeft) < minimumArea)
                {
                    return false;
                }
            }
            return true;
        }

        private Vector2 BlendedGridPoint(int x, int y, float blend)
        {
            Vector2 original = new Vector2(x, y);
            Vector2Int key = new Vector2Int(x, y);
            return _vectorizedGridPoints.TryGetValue(key, out Vector2 vectorized)
                ? Vector2.Lerp(original, vectorized, blend)
                : original;
        }

        private void AddPreviewSegment(Vector2 a, Vector2 b, bool isManual = false)
        {
            if (TryProjectToSurface(a, out Vector3 worldA)
                && TryProjectToSurface(b, out Vector3 worldB))
            {
                _previewSegments.Add(new PreviewSegment
                {
                    a = worldA,
                    b = worldB,
                    isManual = isManual
                });
            }
        }

        private void BuildPreviewUvPaths()
        {
            _previewUvPaths.Clear();
            for (int pathIndex = 0;
                pathIndex < _previewGridPaths.Count; pathIndex++)
            {
                List<Vector2Int> gridPath = _previewGridPaths[pathIndex];
                if (gridPath.Count < 2)
                {
                    continue;
                }

                List<Vector2> reduced = new List<Vector2>(gridPath.Count);
                for (int i = 0; i < gridPath.Count; i++)
                {
                    Vector2 point = GetVectorizedGridPoint(
                        gridPath[i].x, gridPath[i].y);
                    if (reduced.Count > 0
                        && (point - reduced[reduced.Count - 1]).sqrMagnitude
                            <= 0.00000001f)
                    {
                        continue;
                    }

                    if (reduced.Count >= 2)
                    {
                        Vector2 previousDirection =
                            reduced[reduced.Count - 1]
                            - reduced[reduced.Count - 2];
                        Vector2 nextDirection =
                            point - reduced[reduced.Count - 1];
                        float cross = Mathf.Abs(Cross2D(previousDirection,
                            nextDirection));
                        float scale = Mathf.Max(1f,
                            previousDirection.magnitude
                            + nextDirection.magnitude);
                        if (cross <= 0.00001f * scale
                            && Vector2.Dot(previousDirection,
                                nextDirection) >= 0f)
                        {
                            reduced[reduced.Count - 1] = point;
                            continue;
                        }
                    }
                    reduced.Add(point);
                }

                if (reduced.Count < 2)
                {
                    continue;
                }
                Vector2[] uvPath = new Vector2[reduced.Count];
                for (int i = 0; i < reduced.Count; i++)
                {
                    uvPath[i] = GridToImageUV(reduced[i]);
                }
                _previewUvPaths.Add(uvPath);
            }
        }

        private void ReprojectPreviewPaths()
        {
            System.Diagnostics.Stopwatch timer =
                System.Diagnostics.Stopwatch.StartNew();
            _previewWorldPaths.Clear();
            _previewSurfaceHitCount = 0;
            _previewFallbackCount = 0;
            _lastPreviewPointCount = 0;
            EnsurePreviewCollider();
            EnsureSurfaceProjectionCacheValid();

            for (int pathIndex = 0;
                pathIndex < _previewUvPaths.Count; pathIndex++)
            {
                Vector2[] uvPath = _previewUvPaths[pathIndex];
                Vector3[] worldPath = new Vector3[uvPath.Length];
                bool valid = true;
                for (int i = 0; i < uvPath.Length; i++)
                {
                    if (!TryGetCachedSurfaceProjection(
                        uvPath[i], out worldPath[i]))
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                {
                    _previewWorldPaths.Add(worldPath);
                    _lastPreviewPointCount += worldPath.Length;
                }
            }

            int pairPointCount = 0;
            for (int i = 0; i < _previewWorldPaths.Count; i++)
            {
                pairPointCount += Mathf.Max(0,
                    (_previewWorldPaths[i].Length - 1) * 2);
            }
            _previewLinePairs = new Vector3[pairPointCount];
            int pairIndex = 0;
            for (int pathIndex = 0;
                pathIndex < _previewWorldPaths.Count; pathIndex++)
            {
                Vector3[] path = _previewWorldPaths[pathIndex];
                for (int i = 1; i < path.Length; i++)
                {
                    _previewLinePairs[pairIndex++] = path[i - 1];
                    _previewLinePairs[pairIndex++] = path[i];
                }
            }
            timer.Stop();
            _lastProjectionMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!CanBuild())
            {
                return;
            }
            if (_needsRebuild && !_isBuilding)
            {
                if (EditorApplication.timeSinceStartup >= _rebuildNotBefore)
                {
                    Rebuild();
                }
                else
                {
                    sceneView.Repaint();
                }
            }

            if (Event.current.type == EventType.Repaint)
            {
                System.Diagnostics.Stopwatch drawTimer =
                    System.Diagnostics.Stopwatch.StartNew();
                Handles.zTest = CompareFunction.LessEqual;
                Handles.color = new Color(0.1f, 0.95f, 1f, 0.95f);
                if (_antialiasedSkeletonPreview)
                {
                    for (int i = 0; i < _previewWorldPaths.Count; i++)
                    {
                        Handles.DrawAAPolyLine(2.5f,
                            _previewWorldPaths[i]);
                    }
                }
                else if (_previewLinePairs.Length >= 2)
                {
                    Handles.DrawLines(_previewLinePairs);
                }
                Handles.zTest = CompareFunction.Always;
                drawTimer.Stop();
                _lastPreviewDrawMilliseconds =
                    drawTimer.Elapsed.TotalMilliseconds;
            }

            DrawInteractiveTransformHandles();

            if (!_manualConnectMode)
            {
                return;
            }

            Event current = Event.current;
            bool erasing =
                _manualEditOperation == ManualEditOperation.Erase;
            // Do not consume the whole Scene view while editing.  Unity must still
            // receive clicks on empty space so artists can deselect normally.
            if (current.type == EventType.Layout
                && IsMouseOverEditableProjection(current.mousePosition))
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            }
            if (current.type == EventType.MouseDown && current.button == 0 && !current.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                if (TryGetManualStrokePoint(ray, !erasing,
                    out Vector2 point, out Vector3 worldPoint,
                    out bool snapped))
                {
                    ClearActiveManualStroke();
                    AddActiveManualStrokePoint(point, worldPoint);
                    _activeStrokeStartedOnSkeleton = erasing
                        || snapped || !_snapManualToSkeleton;
                    _lastManualGuiPosition = current.mousePosition;
                    _hasLastManualGuiPosition = true;
                    current.Use();
                }
            }

            if (current.type == EventType.MouseDrag && current.button == 0
                && _activeManualStroke.Count > 0 && !current.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                if ((!_hasLastManualGuiPosition
                        || Vector2.Distance(current.mousePosition,
                            _lastManualGuiPosition) >= 3f)
                    && TryGetManualStrokePoint(ray, false,
                        out Vector2 point, out Vector3 worldPoint, out _))
                {
                    AddActiveManualStrokePoint(point, worldPoint);
                    _lastManualGuiPosition = current.mousePosition;
                    _hasLastManualGuiPosition = true;
                    sceneView.Repaint();
                }
                current.Use();
            }

            if (current.type == EventType.MouseUp && current.button == 0
                && _activeManualStroke.Count > 0 && !current.alt)
            {
                Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
                bool snappedEnd = false;
                if (TryGetManualStrokePoint(ray, !erasing,
                        out Vector2 point, out Vector3 worldPoint,
                        out snappedEnd)
                    && Vector2.Distance(point, _activeManualStroke[_activeManualStroke.Count - 1])
                        >= 0.0001f)
                {
                    AddActiveManualStrokePoint(point, worldPoint);
                }
                CommitManualStroke(_activeStrokeStartedOnSkeleton,
                    erasing || snappedEnd || !_snapManualToSkeleton);
                current.Use();
            }

            if (_activeManualStroke.Count > 0)
            {
                DrawActiveManualStroke();
            }
        }

        private bool IsMouseOverEditableProjection(Vector2 guiPosition)
        {
            if (_target == null || _target.sharedMesh == null)
            {
                return false;
            }
            Ray ray = HandleUtility.GUIPointToWorldRay(guiPosition);
            return TryGetProjectionPlanePoint(ray, out _, out _);
        }

        private bool TryGetManualStrokePoint(Ray ray, bool allowSnap,
            out Vector2 point, out Vector3 worldPoint, out bool snapped)
        {
            point = default(Vector2);
            worldPoint = default(Vector3);
            snapped = false;
            if (!TryGetProjectionPlanePoint(ray, out point,
                out worldPoint))
            {
                return false;
            }
            if (allowSnap && _snapManualToSkeleton)
            {
                snapped = TrySnapToSkeleton(point,
                    out Vector2 snappedPoint);
                if (snapped)
                {
                    point = snappedPoint;
                    EnsureSurfaceProjectionCacheValid();
                    TryGetCachedSurfaceProjection(point, out worldPoint);
                }
            }
            return true;
        }

        private bool TryGetProjectionPlanePoint(Ray ray,
            out Vector2 point, out Vector3 worldPoint)
        {
            point = default(Vector2);
            worldPoint = default(Vector3);
            if (_target == null || _target.sharedMesh == null)
            {
                return false;
            }

            GetProjectionWorldFrame(out Vector3 worldCenter,
                out Quaternion worldRotation, out _, out _);
            Vector3 normal = worldRotation * Vector3.up;
            Plane projectionPlane = new Plane(normal, worldCenter);
            if (!projectionPlane.Raycast(ray, out float distance)
                || distance < 0f)
            {
                return false;
            }

            worldPoint = ray.GetPoint(distance) + normal * 0.001f;
            point = LocalToImageUV(
                _target.transform.InverseTransformPoint(worldPoint));
            return point.x >= 0f && point.x <= 1f
                && point.y >= 0f && point.y <= 1f;
        }

        private void AddActiveManualStrokePoint(Vector2 point,
            Vector3 worldPoint)
        {
            _activeManualStroke.Add(point);
            _activeManualWorldStroke.Add(worldPoint);
        }

        private void ClearActiveManualStroke()
        {
            _activeManualStroke.Clear();
            _activeManualWorldStroke.Clear();
        }

        private bool TrySnapToSkeleton(Vector2 point, out Vector2 snappedPoint)
        {
            snappedPoint = point;
            if (_vectorSkeletonPaths.Count == 0
                || _gridWidth <= 0 || _gridHeight <= 0)
            {
                return false;
            }
            float maxDistance = _manualSnapDistance;
            float bestDistance = maxDistance;
            for (int pathIndex = 0;
                pathIndex < _vectorSkeletonPaths.Count; pathIndex++)
            {
                List<Vector2> path = _vectorSkeletonPaths[pathIndex];
                for (int i = 1; i < path.Count; i++)
                {
                    TrySnapToEdge(point, GridToImageUV(path[i - 1]),
                        GridToImageUV(path[i]), ref bestDistance,
                        ref snappedPoint);
                }
            }
            return bestDistance < maxDistance;
        }

        private static void TrySnapToEdge(Vector2 point, Vector2 a, Vector2 b,
            ref float bestDistance, ref Vector2 snappedPoint)
        {
            Vector2 direction = b - a;
            float lengthSquared = direction.sqrMagnitude;
            float t = lengthSquared < 0.000001f ? 0f
                : Mathf.Clamp01(Vector2.Dot(point - a, direction) / lengthSquared);
            Vector2 candidate = a + direction * t;
            float distance = Vector2.Distance(point, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                snappedPoint = candidate;
            }
        }

        private void CommitManualStroke(bool startedOnSkeleton, bool endedOnSkeleton)
        {
            bool erasing =
                _manualEditOperation == ManualEditOperation.Erase;
            if (_activeManualStroke.Count >= 2
                && (erasing || (startedOnSkeleton && endedOnSkeleton)))
            {
                Undo.RecordObject(this, erasing
                    ? "绘制 SLG 手动擦线"
                    : "绘制 SLG 手动补线");
                List<Vector2> cleaned = CleanManualStroke(_activeManualStroke);
                List<SLGMapTileManualSegment> destination = erasing
                    ? _manualEraseSegments
                    : _manualSegments;
                for (int i = 1; i < cleaned.Count; i++)
                {
                    Vector2 a = cleaned[i - 1];
                    Vector2 b = cleaned[i];
                    if (Vector2.Distance(a, b) >= 0.0001f)
                    {
                        destination.Add(new SLGMapTileManualSegment
                            { start = a, end = b });
                    }
                }
                EditorUtility.SetDirty(this);
                Invalidate();
            }
            else
            {
                ShowNotification(new GUIContent("补线未提交：两端都需要吸附到已有骨架。"));
            }
            ClearActiveManualStroke();
            _activeStrokeStartedOnSkeleton = false;
            _hasLastManualGuiPosition = false;
            RepaintAllSceneViews();
        }

        private List<Vector2> CleanManualStroke(List<Vector2> sourceStroke)
        {
            List<Vector2> gridStroke =
                new List<Vector2>(sourceStroke.Count);
            for (int i = 0; i < sourceStroke.Count; i++)
            {
                gridStroke.Add(new Vector2(sourceStroke[i].x * _gridWidth,
                    sourceStroke[i].y * _gridHeight));
            }

            if (_manualStrokeSmoothing > 0.0001f && gridStroke.Count > 2)
            {
                int passes = _manualStrokeSmoothing > 0.66f ? 2 : 1;
                float blend = passes == 1
                    ? _manualStrokeSmoothing
                    : Mathf.Clamp01(_manualStrokeSmoothing * 0.65f);
                for (int pass = 0; pass < passes; pass++)
                {
                    List<Vector2> next = new List<Vector2>(gridStroke);
                    for (int i = 1; i + 1 < gridStroke.Count; i++)
                    {
                        Vector2 localAverage =
                            (gridStroke[i - 1] + gridStroke[i] * 2f
                                + gridStroke[i + 1]) * 0.25f;
                        next[i] = Vector2.LerpUnclamped(gridStroke[i],
                            localAverage, blend);
                    }
                    gridStroke = next;
                }
            }

            gridStroke = SimplifyPath(gridStroke,
                _manualStrokeSimplifyTolerance);
            List<Vector2> result = new List<Vector2>(gridStroke.Count);
            for (int i = 0; i < gridStroke.Count; i++)
            {
                result.Add(new Vector2(gridStroke[i].x / _gridWidth,
                    gridStroke[i].y / _gridHeight));
            }
            return result;
        }

        private void DrawActiveManualStroke()
        {
            Handles.color =
                _manualEditOperation == ManualEditOperation.Erase
                ? new Color(1f, 0.2f, 0.15f, 0.98f)
                : _activeStrokeStartedOnSkeleton
                ? new Color(1f, 0.67f, 0.1f, 0.95f)
                : new Color(1f, 0.2f, 0.2f, 0.95f);
            if (_activeManualWorldStroke.Count >= 2)
            {
                for (int i = 1; i < _activeManualWorldStroke.Count; i++)
                {
                    Handles.DrawLine(_activeManualWorldStroke[i - 1],
                        _activeManualWorldStroke[i]);
                }
            }
        }

        private void DrawInteractiveTransformHandles()
        {
            if (_target == null || _target.sharedMesh == null)
            {
                return;
            }

            GetProjectionWorldFrame(out Vector3 worldPivot,
                out Quaternion worldRotation, out float worldWidth,
                out float worldHeight);
            Tool activeTool = Tools.current;
            if (activeTool == Tool.Move)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion handleRotation = Tools.pivotRotation == PivotRotation.Local
                    ? worldRotation
                    : Quaternion.identity;
                Vector3 movedPivot = Handles.PositionHandle(worldPivot, handleRotation);
                if (EditorGUI.EndChangeCheck())
                {
                    ApplyInteractiveMove(worldPivot, movedPivot);
                }
            }

            if (activeTool == Tool.Rotate)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion rotated = Handles.RotationHandle(worldRotation, worldPivot);
                if (EditorGUI.EndChangeCheck())
                {
                    Quaternion localRotation = Quaternion.Inverse(_target.transform.rotation) * rotated;
                    Vector3 planarForward = localRotation * Vector3.forward;
                    float yaw = Mathf.Atan2(planarForward.x, planarForward.z)
                        * Mathf.Rad2Deg;
                    Undo.RecordObject(this, "旋转 SLG 骨架投影");
                    _projectionRotation = -NormalizeAngle(yaw);
                    EditorUtility.SetDirty(this);
                    RefreshAfterInteractiveTransform();
                }
            }

            if (activeTool == Tool.Scale)
            {
                // Use Unity's built-in ScaleHandle verbatim. Its Y axis remains
                // visible by design; this 2D map projection persists X/Z only.
                EditorGUI.BeginChangeCheck();
                Vector3 scaled = Handles.ScaleHandle(new Vector3(worldWidth, 1f,
                    worldHeight), worldPivot, worldRotation,
                    HandleUtility.GetHandleSize(worldPivot));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(this, "缩放 SLG 骨架投影");
                    float baseWorldWidth = GetProjectionBaseWorldWidth();
                    float baseWorldHeight = GetProjectionBaseWorldHeight();
                    _projectionScale.x = Mathf.Max(0.01f,
                        Mathf.Abs(scaled.x) / baseWorldWidth);
                    _projectionScale.y = Mathf.Max(0.01f,
                        Mathf.Abs(scaled.z) / baseWorldHeight);
                    EditorUtility.SetDirty(this);
                    RefreshAfterInteractiveTransform();
                }
            }
        }

        private void RefreshAfterInteractiveTransform()
        {
            // Moving the projection does not change image recognition or topology.
            // Reproject the already vectorized paths instead of rebuilding labels.
            if (_previewGridPaths.Count > 0 && !_isBuilding)
            {
                _surfaceProjectionCache.Clear();
                ReprojectPreviewPaths();
            }
            Repaint();
            RepaintAllSceneViews();
        }

        private void ApplyInteractiveMove(Vector3 oldWorldPivot, Vector3 newWorldPivot)
        {
            Vector3 oldLocal = _target.transform.InverseTransformPoint(oldWorldPivot);
            Vector3 newLocal = _target.transform.InverseTransformPoint(newWorldPivot);
            Undo.RecordObject(this, "移动 SLG 骨架投影");
            _projectionOffset += new Vector2(newLocal.x - oldLocal.x,
                newLocal.z - oldLocal.z);
            EditorUtility.SetDirty(this);
            RefreshAfterInteractiveTransform();
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            return angle > 180f ? angle - 360f : angle;
        }

        private void GenerateTiles()
        {
            if (!EnsureMeshReadable(true))
            {
                return;
            }
            Rebuild();
            if (_labels == null || _tileCount == 0 || _target == null)
            {
                return;
            }

            try
            {
                _isBuilding = true;
                EditorUtility.DisplayProgressBar("SLG 地块切分", "正在裁剪参考模型…", 0f);
                Dictionary<int, TileBuilder> builders = BuildTileMeshes();
                WriteGeneratedAssets(builders);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("SLG 地块切分", exception.Message, "确定");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _isBuilding = false;
            }
        }

        private Dictionary<int, TileBuilder> BuildTileMeshes()
        {
            Mesh source = _target.sharedMesh;
            if (source.uv == null || source.uv.Length != source.vertexCount)
            {
                throw new InvalidOperationException(
                    "参考 Mesh 没有完整的 UV0，无法保证生成地块的纹理位置与原模型完全一致。");
            }
            if (TryBuildMinimalPlanarTiles(source,
                out Dictionary<int, TileBuilder> planarBuilders))
            {
                return planarBuilders;
            }
            if (_tileGap <= 0f && source.subMeshCount == 1
                && IsFlatSource(source.vertices, source.bounds)
                && HasAffineSourceUv(source.vertices, source.uv))
            {
                throw new InvalidOperationException(
                    "骨架轮廓最少面三角化失败：" + _lastMinimalBuildFailure
                    + "。已取消输出，不会回退到旧模型布线。");
            }
            if (_tileGap <= 0f)
            {
                return BuildSurfaceConformingTiles(source);
            }

            Vector3[] sourceVertices = source.vertices;
            Vector2[] sourceUvs = source.uv;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            int subMeshCount = source.subMeshCount;
            Dictionary<int, TileBuilder> builders = new Dictionary<int, TileBuilder>();

            int processed = 0;
            int total = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                total += (int)(source.GetIndexCount(subMesh) / 3u);
            }

            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] triangles = source.GetTriangles(subMesh);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    processed++;
                    if ((processed & 255) == 0)
                    {
                        EditorUtility.DisplayProgressBar("SLG 地块切分", "正在裁剪参考模型…",
                            processed / (float)Mathf.Max(1, total));
                    }

                    int ia = triangles[i];
                    int ib = triangles[i + 1];
                    int ic = triangles[i + 2];
                    Vector3 aPosition = sourceVertices[ia];
                    Vector3 bPosition = sourceVertices[ib];
                    Vector3 cPosition = sourceVertices[ic];
                    Vector3 faceNormal = Vector3.Cross(bPosition - aPosition, cPosition - aPosition).normalized;
                    // Reject only faces that collapse in the XZ projection. Using the
                    // winding sign here created holes on otherwise valid source meshes.
                    if (Mathf.Abs(faceNormal.y) <= 0.001f)
                    {
                        continue;
                    }

                    PolyVertex a = CreatePolyVertex(aPosition, ia, faceNormal, sourceUvs, sourceNormals, sourceTangents);
                    PolyVertex b = CreatePolyVertex(bPosition, ib, faceNormal, sourceUvs, sourceNormals, sourceTangents);
                    PolyVertex c = CreatePolyVertex(cPosition, ic, faceNormal, sourceUvs, sourceNormals, sourceTangents);
                    a.plane = ImageUVToGrid(LocalToImageUV(a.position));
                    b.plane = ImageUVToGrid(LocalToImageUV(b.position));
                    c.plane = ImageUVToGrid(LocalToImageUV(c.position));

                    float minX = Mathf.Min(a.plane.x, Mathf.Min(b.plane.x, c.plane.x));
                    float maxX = Mathf.Max(a.plane.x, Mathf.Max(b.plane.x, c.plane.x));
                    float minY = Mathf.Min(a.plane.y, Mathf.Min(b.plane.y, c.plane.y));
                    float maxY = Mathf.Max(a.plane.y, Mathf.Max(b.plane.y, c.plane.y));
                    if (maxX < 0f || maxY < 0f || minX > _gridWidth || minY > _gridHeight)
                    {
                        continue;
                    }

                    // Vectorized boundary points can move by less than one source-grid
                    // cell, so include one neighbour around the projected source bounds.
                    int vectorReach = Mathf.CeilToInt(_skeletonSimplifyTolerance) + 1;
                    int fromX = Mathf.Clamp(Mathf.FloorToInt(minX) - vectorReach,
                        0, _gridWidth - 1);
                    int toX = Mathf.Clamp(Mathf.FloorToInt(maxX) + vectorReach,
                        0, _gridWidth - 1);
                    int fromY = Mathf.Clamp(Mathf.FloorToInt(minY) - vectorReach,
                        0, _gridHeight - 1);
                    int toY = Mathf.Clamp(Mathf.FloorToInt(maxY) + vectorReach,
                        0, _gridHeight - 1);
                    for (int y = fromY; y <= toY; y++)
                    {
                        for (int x = fromX; x <= toX; x++)
                        {
                            int tile = _labels[Index(x, y)];
                            if (!builders.TryGetValue(tile, out TileBuilder builder))
                            {
                                builder = new TileBuilder(subMeshCount);
                                builders.Add(tile, builder);
                            }

                            Vector2 bottomLeft = GetVectorizedGridPoint(x, y);
                            Vector2 bottomRight = GetVectorizedGridPoint(x + 1, y);
                            Vector2 topRight = GetVectorizedGridPoint(x + 1, y + 1);
                            Vector2 topLeft = GetVectorizedGridPoint(x, y + 1);
                            Color color = TileColor(tile);
                            if (_tileGap > 0f)
                            {
                                AddGappedVectorCell(builder, a, b, c, bottomLeft,
                                    bottomRight, topRight, topLeft, x, y, tile,
                                    subMesh, color);
                            }
                            else
                            {
                                AddVectorCellTriangle(builder, a, b, c, bottomLeft,
                                    bottomRight, topRight, subMesh, color);
                                AddVectorCellTriangle(builder, a, b, c, bottomLeft,
                                    topRight, topLeft, subMesh, color);
                            }
                        }
                    }
                }
            }
            return builders;
        }

        private Dictionary<int, TileBuilder> BuildSurfaceConformingTiles(Mesh source)
        {
            List<VectorRegion> regions = BuildVectorRegions();
            Vector3[] sourceVertices = source.vertices;
            Vector2[] sourceUvs = source.uv;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            int subMeshCount = source.subMeshCount;
            Dictionary<int, TileBuilder> builders =
                new Dictionary<int, TileBuilder>();

            int processed = 0;
            int total = 0;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                total += (int)(source.GetIndexCount(subMesh) / 3u);
            }

            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                int[] sourceTriangles = source.GetTriangles(subMesh);
                for (int i = 0; i < sourceTriangles.Length; i += 3)
                {
                    processed++;
                    if ((processed & 127) == 0)
                    {
                        EditorUtility.DisplayProgressBar("SLG 地块切分",
                            "正在按矢量骨架裁剪参考模型…",
                            processed / (float)Mathf.Max(1, total));
                    }

                    int ia = sourceTriangles[i];
                    int ib = sourceTriangles[i + 1];
                    int ic = sourceTriangles[i + 2];
                    Vector3 positionA = sourceVertices[ia];
                    Vector3 positionB = sourceVertices[ib];
                    Vector3 positionC = sourceVertices[ic];
                    Vector3 faceNormal = Vector3.Cross(positionB - positionA,
                        positionC - positionA).normalized;
                    if (Mathf.Abs(faceNormal.y) <= 0.001f)
                    {
                        continue;
                    }

                    PolyVertex a = CreatePolyVertex(positionA, ia, faceNormal,
                        sourceUvs, sourceNormals, sourceTangents);
                    PolyVertex b = CreatePolyVertex(positionB, ib, faceNormal,
                        sourceUvs, sourceNormals, sourceTangents);
                    PolyVertex c = CreatePolyVertex(positionC, ic, faceNormal,
                        sourceUvs, sourceNormals, sourceTangents);
                    a.plane = ImageUVToGrid(LocalToImageUV(a.position));
                    b.plane = ImageUVToGrid(LocalToImageUV(b.position));
                    c.plane = ImageUVToGrid(LocalToImageUV(c.position));
                    Rect sourceBounds = TriangleBounds(a.plane, b.plane, c.plane);

                    for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                    {
                        VectorRegion region = regions[regionIndex];
                        if (!RectsOverlap(sourceBounds, region.bounds))
                        {
                            continue;
                        }
                        if (!builders.TryGetValue(region.label, out TileBuilder builder))
                        {
                            builder = new TileBuilder(subMeshCount);
                            builders.Add(region.label, builder);
                        }

                        Color color = TileColor(region.label);
                        for (int triangleIndex = 0;
                            triangleIndex < region.triangles.Count; triangleIndex++)
                        {
                            VectorRegionTriangle cutter =
                                region.triangles[triangleIndex];
                            if (!RectsOverlap(sourceBounds, cutter.bounds))
                            {
                                continue;
                            }
                            AddVectorCellTriangle(builder, a, b, c,
                                cutter.a, cutter.b, cutter.c, subMesh, color);
                        }
                    }
                }
            }

            return builders;
        }

        private List<VectorRegion> BuildVectorRegions()
        {
            Dictionary<int, List<GridSegment>> boundaryEdges =
                BuildDirectedRegionBoundaryEdges();
            List<VectorRegion> regions = new List<VectorRegion>(_tileCount);
            for (int label = 0; label < _tileCount; label++)
            {
                if (!boundaryEdges.TryGetValue(label, out List<GridSegment> edges)
                    || !TryTraceSingleBoundaryLoop(edges, out List<Vector2Int> loop))
                {
                    throw new InvalidOperationException(string.Format(
                        "地块 {0} 不是单一闭合轮廓。请在骨架预览中补线或清理交叉线后重试。",
                        label + 1));
                }
                RemoveCollinearVectorBoundary(loop);
                if (loop.Count < 3)
                {
                    throw new InvalidOperationException(string.Format(
                        "地块 {0} 的有效边界点少于 3 个。", label + 1));
                }

                List<PolyVertex> polygon = new List<PolyVertex>(loop.Count);
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2 point = GetVectorizedGridPoint(loop[i].x, loop[i].y);
                    polygon.Add(new PolyVertex { plane = point });
                }
                if (!TryTriangulateVectorPolygon(polygon, 1f,
                    out List<int> triangleIndices))
                {
                    throw new InvalidOperationException(string.Format(
                        "地块 {0} 的轮廓存在自交或重叠，无法安全三角化。", label + 1));
                }

                VectorRegion region = new VectorRegion
                {
                    label = label,
                    bounds = PolygonBounds(polygon)
                };
                for (int i = 0; i < triangleIndices.Count; i += 3)
                {
                    Vector2 a = polygon[triangleIndices[i]].plane;
                    Vector2 b = polygon[triangleIndices[i + 1]].plane;
                    Vector2 c = polygon[triangleIndices[i + 2]].plane;
                    region.triangles.Add(new VectorRegionTriangle
                    {
                        a = a,
                        b = b,
                        c = c,
                        bounds = TriangleBounds(a, b, c)
                    });
                }
                regions.Add(region);
            }
            return regions;
        }

        private static Rect PolygonBounds(List<PolyVertex> polygon)
        {
            float minX = polygon[0].plane.x;
            float maxX = minX;
            float minY = polygon[0].plane.y;
            float maxY = minY;
            for (int i = 1; i < polygon.Count; i++)
            {
                Vector2 point = polygon[i].plane;
                minX = Mathf.Min(minX, point.x);
                maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y);
                maxY = Mathf.Max(maxY, point.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static Rect TriangleBounds(Vector2 a, Vector2 b, Vector2 c)
        {
            return Rect.MinMaxRect(
                Mathf.Min(a.x, Mathf.Min(b.x, c.x)),
                Mathf.Min(a.y, Mathf.Min(b.y, c.y)),
                Mathf.Max(a.x, Mathf.Max(b.x, c.x)),
                Mathf.Max(a.y, Mathf.Max(b.y, c.y)));
        }

        private static bool RectsOverlap(Rect a, Rect b)
        {
            const float epsilon = 0.00001f;
            return a.xMin <= b.xMax + epsilon && a.xMax + epsilon >= b.xMin
                && a.yMin <= b.yMax + epsilon && a.yMax + epsilon >= b.yMin;
        }

        private bool TryBuildMinimalPlanarTiles(Mesh source,
            out Dictionary<int, TileBuilder> builders)
        {
            builders = null;
            _lastMinimalBuildFailure = string.Empty;
            if (source == null || source.subMeshCount != 1 || _tileGap > 0f)
            {
                _lastMinimalBuildFailure = "源模型不是单材质平面，或地块间隔大于零";
                return false;
            }
            Vector3[] positions = source.vertices;
            Vector2[] sourceUvs = source.uv;
            Vector3[] sourceNormals = source.normals;
            Vector4[] sourceTangents = source.tangents;
            int[] sourceTriangles = source.GetTriangles(0);
            if (!IsFlatSource(positions, source.bounds)
                || !HasAffineSourceUv(positions, sourceUvs))
            {
                _lastMinimalBuildFailure = "源模型不是平面，或 UV0 不是全局连续映射";
                return false;
            }

            Dictionary<int, List<GridSegment>> boundaryEdges =
                BuildDirectedRegionBoundaryEdges();
            Dictionary<int, TileBuilder> result = new Dictionary<int, TileBuilder>();
            float desiredAreaSign = SourceProjectedAreaSign(positions, sourceTriangles);
            if (Mathf.Abs(desiredAreaSign) < 0.5f)
            {
                _lastMinimalBuildFailure = "无法确定源模型三角形朝向";
                return false;
            }

            for (int label = 0; label < _tileCount; label++)
            {
                if (!boundaryEdges.TryGetValue(label, out List<GridSegment> edges)
                    || !TryTraceSingleBoundaryLoop(edges, out List<Vector2Int> loop))
                {
                    _lastMinimalBuildFailure = string.Format(
                        "地块 {0} 不是单一闭合轮廓", label + 1);
                    return false;
                }
                RemoveCollinearVectorBoundary(loop);
                if (loop.Count < 3)
                {
                    _lastMinimalBuildFailure = string.Format(
                        "地块 {0} 的有效轮廓点少于 3 个", label + 1);
                    return false;
                }

                List<PolyVertex> polygon = new List<PolyVertex>(loop.Count);
                for (int i = 0; i < loop.Count; i++)
                {
                    Vector2Int key = loop[i];
                    Vector2 gridPoint = GetVectorizedGridPoint(key.x, key.y);
                    if (!TryCreateAffinePlanarVertex(gridPoint, positions, sourceUvs,
                        sourceNormals, sourceTangents, out PolyVertex vertex))
                    {
                        _lastMinimalBuildFailure = string.Format(
                            "地块 {0} 无法从旧模型计算位置或 UV", label + 1);
                        return false;
                    }
                    polygon.Add(vertex);
                }
                if (!TryTriangulateVectorPolygon(polygon, desiredAreaSign,
                    out List<int> polygonTriangles))
                {
                    _lastMinimalBuildFailure = string.Format(
                        "地块 {0} 的轮廓无法安全三角化", label + 1);
                    return false;
                }

                TileBuilder builder = new TileBuilder(1);
                Color color = TileColor(label);
                bool polygonAdded = builder.AddTriangulatedPolygon(
                    polygon, polygonTriangles, 0, color);
                int expectedTriangleIndexCount = (polygon.Count - 2) * 3;
                if (!polygonAdded || !builder.HasGeometry
                    || builder.vertices.Count != polygon.Count
                    || builder.triangles[0].Count != expectedTriangleIndexCount)
                {
                    // Do not emit a damaged minimal patch. Flat affine sources are
                    // never allowed to fall back to the source triangle layout.
                    _lastMinimalBuildFailure = string.Format(
                        "地块 {0} 出现重复顶点或退化三角形", label + 1);
                    return false;
                }
                result.Add(label, builder);
            }

            builders = result;
            return builders.Count == _tileCount;
        }

        private bool TryCreateAffinePlanarVertex(Vector2 gridPoint,
            Vector3[] positions, Vector2[] sourceUvs, Vector3[] sourceNormals,
            Vector4[] sourceTangents, out PolyVertex vertex)
        {
            vertex = default(PolyVertex);
            if (positions == null || positions.Length < 3
                || sourceUvs == null || sourceUvs.Length != positions.Length)
            {
                return false;
            }

            int a = 0;
            int b = -1;
            int c = -1;
            Vector2 planeA = new Vector2(positions[a].x, positions[a].z);
            for (int i = 1; i < positions.Length; i++)
            {
                Vector2 candidate = new Vector2(positions[i].x, positions[i].z);
                if ((candidate - planeA).sqrMagnitude > 0.00000001f)
                {
                    b = i;
                    break;
                }
            }
            if (b < 0)
            {
                return false;
            }
            Vector2 planeB = new Vector2(positions[b].x, positions[b].z);
            for (int i = 1; i < positions.Length; i++)
            {
                Vector2 candidate = new Vector2(positions[i].x, positions[i].z);
                if (Mathf.Abs(Cross2D(planeB - planeA, candidate - planeA))
                    > 0.000001f)
                {
                    c = i;
                    break;
                }
            }
            if (c < 0)
            {
                return false;
            }

            Vector2 planeC = new Vector2(positions[c].x, positions[c].z);
            float denominator = Cross2D(planeB - planeA, planeC - planeA);
            Vector3 position = ImageUVToLocal(GridToImageUV(gridPoint));
            Vector2 planePoint = new Vector2(position.x, position.z);
            float weightB = Cross2D(planePoint - planeA,
                planeC - planeA) / denominator;
            float weightC = Cross2D(planeB - planeA,
                planePoint - planeA) / denominator;
            float weightA = 1f - weightB - weightC;

            Vector3 fallbackNormal = Vector3.Cross(positions[b] - positions[a],
                positions[c] - positions[a]).normalized;
            Vector3 normalA = a < sourceNormals.Length
                ? sourceNormals[a] : fallbackNormal;
            Vector3 normalB = b < sourceNormals.Length
                ? sourceNormals[b] : fallbackNormal;
            Vector3 normalC = c < sourceNormals.Length
                ? sourceNormals[c] : fallbackNormal;
            Vector4 tangentA = a < sourceTangents.Length
                ? sourceTangents[a] : new Vector4(1f, 0f, 0f, 1f);
            Vector4 tangentB = b < sourceTangents.Length
                ? sourceTangents[b] : tangentA;
            Vector4 tangentC = c < sourceTangents.Length
                ? sourceTangents[c] : tangentA;
            vertex = new PolyVertex
            {
                plane = gridPoint,
                position = position,
                uv = sourceUvs[a] * weightA + sourceUvs[b] * weightB
                    + sourceUvs[c] * weightC,
                normal = (normalA * weightA + normalB * weightB
                    + normalC * weightC).normalized,
                tangent = tangentA * weightA + tangentB * weightB
                    + tangentC * weightC
            };
            return true;
        }

        private static bool IsFlatSource(Vector3[] positions, Bounds bounds)
        {
            if (positions.Length < 3)
            {
                return false;
            }
            float tolerance = Mathf.Max(0.00001f, bounds.size.magnitude * 0.00001f);
            float y = positions[0].y;
            for (int i = 1; i < positions.Length; i++)
            {
                if (Mathf.Abs(positions[i].y - y) > tolerance)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasAffineSourceUv(Vector3[] positions, Vector2[] sourceUvs)
        {
            if (sourceUvs == null || sourceUvs.Length != positions.Length
                || positions.Length < 3)
            {
                return false;
            }
            int a = 0;
            int b = -1;
            int c = -1;
            Vector2 pa = new Vector2(positions[a].x, positions[a].z);
            for (int i = 1; i < positions.Length; i++)
            {
                Vector2 candidate = new Vector2(positions[i].x, positions[i].z);
                if ((candidate - pa).sqrMagnitude > 0.00000001f)
                {
                    b = i;
                    break;
                }
            }
            if (b < 0)
            {
                return false;
            }
            Vector2 pb = new Vector2(positions[b].x, positions[b].z);
            for (int i = 1; i < positions.Length; i++)
            {
                Vector2 candidate = new Vector2(positions[i].x, positions[i].z);
                if (Mathf.Abs(Cross2D(pb - pa, candidate - pa)) > 0.000001f)
                {
                    c = i;
                    break;
                }
            }
            if (c < 0)
            {
                return false;
            }

            Vector2 pc = new Vector2(positions[c].x, positions[c].z);
            float denominator = Cross2D(pb - pa, pc - pa);
            for (int i = 0; i < positions.Length; i++)
            {
                Vector2 p = new Vector2(positions[i].x, positions[i].z);
                float weightB = Cross2D(p - pa, pc - pa) / denominator;
                float weightC = Cross2D(pb - pa, p - pa) / denominator;
                Vector2 expected = sourceUvs[a]
                    + (sourceUvs[b] - sourceUvs[a]) * weightB
                    + (sourceUvs[c] - sourceUvs[a]) * weightC;
                if ((expected - sourceUvs[i]).sqrMagnitude > 0.0000001f)
                {
                    return false;
                }
            }
            return true;
        }

        private Dictionary<int, List<GridSegment>> BuildDirectedRegionBoundaryEdges()
        {
            Dictionary<int, List<GridSegment>> result =
                new Dictionary<int, List<GridSegment>>();
            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int label = _labels[Index(x, y)];
                    if (!result.TryGetValue(label, out List<GridSegment> edges))
                    {
                        edges = new List<GridSegment>();
                        result.Add(label, edges);
                    }
                    if (y == 0 || _labels[Index(x, y - 1)] != label)
                    {
                        AddGridSegment(edges, x, y, x + 1, y);
                    }
                    if (x + 1 == _gridWidth || _labels[Index(x + 1, y)] != label)
                    {
                        AddGridSegment(edges, x + 1, y, x + 1, y + 1);
                    }
                    if (y + 1 == _gridHeight || _labels[Index(x, y + 1)] != label)
                    {
                        AddGridSegment(edges, x + 1, y + 1, x, y + 1);
                    }
                    if (x == 0 || _labels[Index(x - 1, y)] != label)
                    {
                        AddGridSegment(edges, x, y + 1, x, y);
                    }
                }
            }
            return result;
        }

        private static bool TryTraceSingleBoundaryLoop(List<GridSegment> edges,
            out List<Vector2Int> loop)
        {
            loop = null;
            if (edges == null || edges.Count < 3)
            {
                return false;
            }
            Dictionary<Vector2Int, List<Vector2Int>> outgoing =
                new Dictionary<Vector2Int, List<Vector2Int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                if (!outgoing.TryGetValue(edges[i].a, out List<Vector2Int> destinations))
                {
                    destinations = new List<Vector2Int>(1);
                    outgoing.Add(edges[i].a, destinations);
                }
                destinations.Add(edges[i].b);
            }
            foreach (KeyValuePair<Vector2Int, List<Vector2Int>> pair in outgoing)
            {
                if (pair.Value.Count != 1)
                {
                    return false;
                }
            }

            Vector2Int start = edges[0].a;
            Vector2Int current = start;
            loop = new List<Vector2Int>(edges.Count);
            HashSet<Vector2Int> visited = new HashSet<Vector2Int>();
            do
            {
                if (!visited.Add(current)
                    || !outgoing.TryGetValue(current, out List<Vector2Int> next))
                {
                    return false;
                }
                loop.Add(current);
                current = next[0];
                if (loop.Count > edges.Count)
                {
                    return false;
                }
            }
            while (current != start);
            return loop.Count == edges.Count;
        }

        private void RemoveCollinearVectorBoundary(List<Vector2Int> loop)
        {
            bool removed;
            do
            {
                removed = false;
                for (int i = 0; i < loop.Count && loop.Count > 3; i++)
                {
                    Vector2 a = GetVectorizedGridPoint(
                        loop[(i - 1 + loop.Count) % loop.Count].x,
                        loop[(i - 1 + loop.Count) % loop.Count].y);
                    Vector2 b = GetVectorizedGridPoint(loop[i].x, loop[i].y);
                    Vector2 c = GetVectorizedGridPoint(
                        loop[(i + 1) % loop.Count].x,
                        loop[(i + 1) % loop.Count].y);
                    float area = Mathf.Abs(Cross2D(b - a, c - b));
                    float scale = Mathf.Max(1f, (b - a).magnitude + (c - b).magnitude);
                    if (area <= scale * 0.00001f)
                    {
                        loop.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }
            }
            while (removed);
        }

        private float SourceProjectedAreaSign(Vector3[] positions, int[] triangles)
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector2 a = ImageUVToGrid(LocalToImageUV(positions[triangles[i]]));
                Vector2 b = ImageUVToGrid(LocalToImageUV(positions[triangles[i + 1]]));
                Vector2 c = ImageUVToGrid(LocalToImageUV(positions[triangles[i + 2]]));
                float area = Cross2D(b - a, c - a);
                if (Mathf.Abs(area) > 0.000001f)
                {
                    return Mathf.Sign(area);
                }
            }
            return 0f;
        }

        private bool TryInterpolateSourceVertex(Vector2 gridPoint,
            Vector3[] positions, Vector2[] sourceUvs, Vector3[] sourceNormals,
            Vector4[] sourceTangents, int[] triangles, out PolyVertex vertex)
        {
            vertex = default(PolyVertex);
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int ia = triangles[i];
                int ib = triangles[i + 1];
                int ic = triangles[i + 2];
                Vector2 a = ImageUVToGrid(LocalToImageUV(positions[ia]));
                Vector2 b = ImageUVToGrid(LocalToImageUV(positions[ib]));
                Vector2 c = ImageUVToGrid(LocalToImageUV(positions[ic]));
                float denominator = Cross2D(b - a, c - a);
                if (Mathf.Abs(denominator) <= 0.000001f)
                {
                    continue;
                }
                float weightA = Cross2D(b - gridPoint, c - gridPoint) / denominator;
                float weightB = Cross2D(c - gridPoint, a - gridPoint) / denominator;
                float weightC = 1f - weightA - weightB;
                if (weightA < -0.0001f || weightB < -0.0001f || weightC < -0.0001f)
                {
                    continue;
                }
                Vector3 fallbackNormal = Vector3.Cross(positions[ib] - positions[ia],
                    positions[ic] - positions[ia]).normalized;
                Vector3 normalA = ia < sourceNormals.Length ? sourceNormals[ia] : fallbackNormal;
                Vector3 normalB = ib < sourceNormals.Length ? sourceNormals[ib] : fallbackNormal;
                Vector3 normalC = ic < sourceNormals.Length ? sourceNormals[ic] : fallbackNormal;
                Vector4 tangentA = ia < sourceTangents.Length
                    ? sourceTangents[ia] : new Vector4(1f, 0f, 0f, 1f);
                Vector4 tangentB = ib < sourceTangents.Length
                    ? sourceTangents[ib] : tangentA;
                Vector4 tangentC = ic < sourceTangents.Length
                    ? sourceTangents[ic] : tangentA;
                vertex = new PolyVertex
                {
                    plane = gridPoint,
                    position = positions[ia] * weightA
                        + positions[ib] * weightB + positions[ic] * weightC,
                    uv = sourceUvs[ia] * weightA
                        + sourceUvs[ib] * weightB + sourceUvs[ic] * weightC,
                    normal = (normalA * weightA + normalB * weightB
                        + normalC * weightC).normalized,
                    tangent = tangentA * weightA + tangentB * weightB
                        + tangentC * weightC
                };
                return true;
            }
            return false;
        }

        private static bool TryTriangulateVectorPolygon(List<PolyVertex> polygon,
            float desiredAreaSign, out List<int> result)
        {
            result = new List<int>();
            if (!IsSimplePolygon(polygon))
            {
                return false;
            }
            List<int> remaining = new List<int>(polygon.Count);
            for (int i = 0; i < polygon.Count; i++)
            {
                remaining.Add(i);
            }
            if (SignedPolyArea(polygon, remaining) < 0f)
            {
                remaining.Reverse();
            }

            int guard = remaining.Count * remaining.Count;
            while (remaining.Count > 3 && guard-- > 0)
            {
                bool foundEar = false;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int previous = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int current = remaining[i];
                    int next = remaining[(i + 1) % remaining.Count];
                    Vector2 a = polygon[previous].plane;
                    Vector2 b = polygon[current].plane;
                    Vector2 c = polygon[next].plane;
                    if (Cross2D(b - a, c - b) <= 0.000001f)
                    {
                        continue;
                    }
                    bool blocked = false;
                    for (int p = 0; p < remaining.Count; p++)
                    {
                        int test = remaining[p];
                        if (test == previous || test == current || test == next)
                        {
                            continue;
                        }
                        Vector2 testPoint = polygon[test].plane;
                        if (PointStrictlyInsideCcwTriangle(testPoint, a, b, c)
                            || PointOnSegmentInterior(testPoint, a, c))
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked)
                    {
                        continue;
                    }
                    AddPolygonTriangle(result, previous, current, next, desiredAreaSign);
                    remaining.RemoveAt(i);
                    foundEar = true;
                    break;
                }
                if (!foundEar)
                {
                    return false;
                }
            }
            if (remaining.Count != 3)
            {
                return false;
            }
            AddPolygonTriangle(result, remaining[0], remaining[1], remaining[2],
                desiredAreaSign);
            ImprovePolygonTriangulation(polygon, result, desiredAreaSign);
            return true;
        }

        private static void ImprovePolygonTriangulation(List<PolyVertex> polygon,
            List<int> triangles, float desiredAreaSign)
        {
            int triangleCount = triangles.Count / 3;
            int remainingFlips = Mathf.Max(32, triangleCount * triangleCount);
            bool changed = true;
            while (changed && remainingFlips-- > 0)
            {
                changed = false;
                Dictionary<IndexEdge, List<int>> edgeTriangles =
                    new Dictionary<IndexEdge, List<int>>();
                for (int triangle = 0; triangle < triangles.Count; triangle += 3)
                {
                    AddTriangleForEdge(edgeTriangles,
                        triangles[triangle], triangles[triangle + 1], triangle);
                    AddTriangleForEdge(edgeTriangles,
                        triangles[triangle + 1], triangles[triangle + 2], triangle);
                    AddTriangleForEdge(edgeTriangles,
                        triangles[triangle + 2], triangles[triangle], triangle);
                }

                foreach (KeyValuePair<IndexEdge, List<int>> pair in edgeTriangles)
                {
                    if (pair.Value.Count != 2)
                    {
                        continue;
                    }
                    int firstTriangle = pair.Value[0];
                    int secondTriangle = pair.Value[1];
                    int a = pair.Key.a;
                    int b = pair.Key.b;
                    int c = OppositeTriangleVertex(triangles, firstTriangle, a, b);
                    int d = OppositeTriangleVertex(triangles, secondTriangle, a, b);
                    if (c < 0 || d < 0 || c == d)
                    {
                        continue;
                    }

                    Vector2 pointA = polygon[a].plane;
                    Vector2 pointB = polygon[b].plane;
                    Vector2 pointC = polygon[c].plane;
                    Vector2 pointD = polygon[d].plane;
                    if (!IsConvexFlipQuadrilateral(pointA, pointB, pointC, pointD))
                    {
                        continue;
                    }

                    float oldQuality = Mathf.Min(
                        TriangleMinimumAngle(pointA, pointB, pointC),
                        TriangleMinimumAngle(pointB, pointA, pointD));
                    float newQuality = Mathf.Min(
                        TriangleMinimumAngle(pointC, pointD, pointA),
                        TriangleMinimumAngle(pointD, pointC, pointB));
                    float oldLength = (pointA - pointB).sqrMagnitude;
                    float newLength = (pointC - pointD).sqrMagnitude;
                    bool improvesAngle = newQuality > oldQuality + 0.0001f;
                    bool equalAngleShorterEdge =
                        Mathf.Abs(newQuality - oldQuality) <= 0.0001f
                        && newLength + 0.0001f < oldLength;
                    if (!improvesAngle && !equalAngleShorterEdge)
                    {
                        continue;
                    }

                    SetPolygonTriangle(triangles, firstTriangle,
                        c, d, a, desiredAreaSign, polygon);
                    SetPolygonTriangle(triangles, secondTriangle,
                        d, c, b, desiredAreaSign, polygon);
                    changed = true;
                    break;
                }
            }
        }

        private static void AddTriangleForEdge(
            Dictionary<IndexEdge, List<int>> edgeTriangles,
            int a, int b, int triangleOffset)
        {
            IndexEdge edge = new IndexEdge(a, b);
            if (!edgeTriangles.TryGetValue(edge, out List<int> offsets))
            {
                offsets = new List<int>(2);
                edgeTriangles.Add(edge, offsets);
            }
            offsets.Add(triangleOffset);
        }

        private static int OppositeTriangleVertex(List<int> triangles,
            int triangleOffset, int edgeA, int edgeB)
        {
            for (int i = 0; i < 3; i++)
            {
                int vertex = triangles[triangleOffset + i];
                if (vertex != edgeA && vertex != edgeB)
                {
                    return vertex;
                }
            }
            return -1;
        }

        private static bool IsConvexFlipQuadrilateral(Vector2 a, Vector2 b,
            Vector2 c, Vector2 d)
        {
            const float epsilon = 0.000001f;
            float cSide = Cross2D(b - a, c - a);
            float dSide = Cross2D(b - a, d - a);
            float aSide = Cross2D(d - c, a - c);
            float bSide = Cross2D(d - c, b - c);
            return cSide * dSide < -epsilon && aSide * bSide < -epsilon;
        }

        private static float TriangleMinimumAngle(Vector2 a, Vector2 b, Vector2 c)
        {
            return Mathf.Min(
                AngleBetween(b - a, c - a),
                Mathf.Min(AngleBetween(a - b, c - b),
                    AngleBetween(a - c, b - c)));
        }

        private static float AngleBetween(Vector2 a, Vector2 b)
        {
            float denominator = Mathf.Sqrt(a.sqrMagnitude * b.sqrMagnitude);
            if (denominator <= 0.00000001f)
            {
                return 0f;
            }
            return Mathf.Acos(Mathf.Clamp(Vector2.Dot(a, b) / denominator,
                -1f, 1f));
        }

        private static void SetPolygonTriangle(List<int> triangles, int offset,
            int a, int b, int c, float desiredAreaSign,
            List<PolyVertex> polygon)
        {
            triangles[offset] = a;
            float area = Cross2D(polygon[b].plane - polygon[a].plane,
                polygon[c].plane - polygon[a].plane);
            if (Mathf.Sign(area) == Mathf.Sign(desiredAreaSign))
            {
                triangles[offset + 1] = b;
                triangles[offset + 2] = c;
            }
            else
            {
                triangles[offset + 1] = c;
                triangles[offset + 2] = b;
            }
        }

        private static bool IsSimplePolygon(List<PolyVertex> polygon)
        {
            const float duplicateEpsilonSquared = 0.00000001f;
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i].plane;
                for (int j = i + 1; j < polygon.Count; j++)
                {
                    if ((a - polygon[j].plane).sqrMagnitude
                        <= duplicateEpsilonSquared)
                    {
                        return false;
                    }
                }
            }

            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a0 = polygon[i].plane;
                Vector2 a1 = polygon[(i + 1) % polygon.Count].plane;
                for (int j = i + 1; j < polygon.Count; j++)
                {
                    if (j == i || j == i + 1
                        || (i == 0 && j == polygon.Count - 1))
                    {
                        continue;
                    }
                    Vector2 b0 = polygon[j].plane;
                    Vector2 b1 = polygon[(j + 1) % polygon.Count].plane;
                    if (SegmentsIntersect(a0, a1, b0, b1))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static bool SegmentsIntersect(Vector2 a0, Vector2 a1,
            Vector2 b0, Vector2 b1)
        {
            const float epsilon = 0.000001f;
            float ab0 = Cross2D(a1 - a0, b0 - a0);
            float ab1 = Cross2D(a1 - a0, b1 - a0);
            float ba0 = Cross2D(b1 - b0, a0 - b0);
            float ba1 = Cross2D(b1 - b0, a1 - b0);
            if (((ab0 > epsilon && ab1 < -epsilon)
                    || (ab0 < -epsilon && ab1 > epsilon))
                && ((ba0 > epsilon && ba1 < -epsilon)
                    || (ba0 < -epsilon && ba1 > epsilon)))
            {
                return true;
            }
            return Mathf.Abs(ab0) <= epsilon && PointOnSegment(b0, a0, a1)
                || Mathf.Abs(ab1) <= epsilon && PointOnSegment(b1, a0, a1)
                || Mathf.Abs(ba0) <= epsilon && PointOnSegment(a0, b0, b1)
                || Mathf.Abs(ba1) <= epsilon && PointOnSegment(a1, b0, b1);
        }

        private static bool PointOnSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            const float epsilon = 0.000001f;
            return point.x >= Mathf.Min(a.x, b.x) - epsilon
                && point.x <= Mathf.Max(a.x, b.x) + epsilon
                && point.y >= Mathf.Min(a.y, b.y) - epsilon
                && point.y <= Mathf.Max(a.y, b.y) + epsilon;
        }

        private static float SignedPolyArea(List<PolyVertex> polygon,
            List<int> indices)
        {
            float area = 0f;
            for (int i = 0; i < indices.Count; i++)
            {
                Vector2 a = polygon[indices[i]].plane;
                Vector2 b = polygon[indices[(i + 1) % indices.Count]].plane;
                area += Cross2D(a, b);
            }
            return area * 0.5f;
        }

        private static bool PointStrictlyInsideCcwTriangle(Vector2 point,
            Vector2 a, Vector2 b, Vector2 c)
        {
            const float epsilon = 0.0000001f;
            return Cross2D(b - a, point - a) > epsilon
                && Cross2D(c - b, point - b) > epsilon
                && Cross2D(a - c, point - c) > epsilon;
        }

        private static bool PointOnSegmentInterior(Vector2 point,
            Vector2 a, Vector2 b)
        {
            const float epsilon = 0.0000001f;
            Vector2 edge = b - a;
            float lengthSquared = edge.sqrMagnitude;
            if (lengthSquared <= epsilon)
            {
                return false;
            }
            float cross = Mathf.Abs(Cross2D(edge, point - a));
            if (cross > epsilon * Mathf.Sqrt(lengthSquared))
            {
                return false;
            }
            float t = Vector2.Dot(point - a, edge) / lengthSquared;
            return t > epsilon && t < 1f - epsilon;
        }

        private static void AddPolygonTriangle(List<int> result,
            int a, int b, int c, float desiredAreaSign)
        {
            result.Add(a);
            if (desiredAreaSign >= 0f)
            {
                result.Add(b);
                result.Add(c);
            }
            else
            {
                result.Add(c);
                result.Add(b);
            }
        }

        private void AddGappedVectorCell(TileBuilder builder,
            PolyVertex sourceA, PolyVertex sourceB, PolyVertex sourceC,
            Vector2 bottomLeft, Vector2 bottomRight, Vector2 topRight,
            Vector2 topLeft, int x, int y, int tile, int subMesh, Color color)
        {
            Bounds bounds = _target.sharedMesh.bounds;
            Vector2 planeScale = new Vector2(
                Mathf.Max(0.000001f, bounds.size.x * _projectionScale.x / _gridWidth),
                Mathf.Max(0.000001f, bounds.size.z * _projectionScale.y / _gridHeight));
            float halfGap = _tileGap * 0.5f;
            float bottomInset = y > 0 && _labels[Index(x, y - 1)] != tile ? halfGap : 0f;
            float rightInset = x + 1 < _gridWidth
                && _labels[Index(x + 1, y)] != tile ? halfGap : 0f;
            float topInset = y + 1 < _gridHeight
                && _labels[Index(x, y + 1)] != tile ? halfGap : 0f;
            float leftInset = x > 0 && _labels[Index(x - 1, y)] != tile ? halfGap : 0f;

            List<PolyVertex> polygon = new List<PolyVertex>(3)
            {
                sourceA, sourceB, sourceC
            };
            polygon = ClipAgainstVectorEdge(polygon, bottomLeft, bottomRight,
                planeScale, bottomInset);
            polygon = ClipAgainstVectorEdge(polygon, bottomRight, topRight,
                planeScale, rightInset);
            polygon = ClipAgainstVectorEdge(polygon, topRight, topLeft,
                planeScale, topInset);
            polygon = ClipAgainstVectorEdge(polygon, topLeft, bottomLeft,
                planeScale, leftInset);
            if (polygon.Count < 3)
            {
                return;
            }
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                builder.AddTriangle(polygon[0], polygon[i], polygon[i + 1],
                    subMesh, color);
            }
        }

        private static void AddVectorCellTriangle(TileBuilder builder,
            PolyVertex sourceA, PolyVertex sourceB, PolyVertex sourceC,
            Vector2 cellA, Vector2 cellB, Vector2 cellC, int subMesh, Color color)
        {
            float area = Cross2D(cellB - cellA, cellC - cellA);
            if (Mathf.Abs(area) <= 0.000001f)
            {
                return;
            }
            if (area < 0f)
            {
                Vector2 swap = cellB;
                cellB = cellC;
                cellC = swap;
            }

            List<PolyVertex> polygon = new List<PolyVertex>(3)
            {
                sourceA, sourceB, sourceC
            };
            polygon = ClipAgainstVectorEdge(polygon, cellA, cellB);
            polygon = ClipAgainstVectorEdge(polygon, cellB, cellC);
            polygon = ClipAgainstVectorEdge(polygon, cellC, cellA);
            if (polygon.Count < 3)
            {
                return;
            }
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                builder.AddTriangle(polygon[0], polygon[i], polygon[i + 1],
                    subMesh, color);
            }
        }

        private static List<PolyVertex> ClipAgainstVectorEdge(
            List<PolyVertex> input, Vector2 edgeA, Vector2 edgeB)
        {
            return ClipAgainstVectorEdge(input, edgeA, edgeB, Vector2.one, 0f);
        }

        private static List<PolyVertex> ClipAgainstVectorEdge(
            List<PolyVertex> input, Vector2 edgeA, Vector2 edgeB,
            Vector2 planeScale, float inset)
        {
            List<PolyVertex> output = new List<PolyVertex>();
            if (input.Count == 0)
            {
                return output;
            }

            Vector2 edge = edgeB - edgeA;
            Vector2 scaledEdge = Vector2.Scale(edge, planeScale);
            float threshold = inset * scaledEdge.magnitude;
            PolyVertex previous = input[input.Count - 1];
            float previousDistance = Cross2D(scaledEdge,
                Vector2.Scale(previous.plane - edgeA, planeScale)) - threshold;
            bool previousInside = previousDistance >= -0.000001f;
            for (int i = 0; i < input.Count; i++)
            {
                PolyVertex current = input[i];
                float currentDistance = Cross2D(scaledEdge,
                    Vector2.Scale(current.plane - edgeA, planeScale)) - threshold;
                bool currentInside = currentDistance >= -0.000001f;
                if (currentInside != previousInside)
                {
                    float denominator = previousDistance - currentDistance;
                    if (Mathf.Abs(denominator) > 0.000001f)
                    {
                        float t = previousDistance / denominator;
                        output.Add(PolyVertex.Lerp(previous, current, t));
                    }
                }
                if (currentInside)
                {
                    output.Add(current);
                }
                previous = current;
                previousDistance = currentDistance;
                previousInside = currentInside;
            }
            return output;
        }

        private static float Cross2D(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        private PolyVertex CreatePolyVertex(Vector3 position, int sourceIndex,
            Vector3 faceNormal, Vector2[] sourceUvs, Vector3[] sourceNormals,
            Vector4[] sourceTangents)
        {
            return new PolyVertex
            {
                position = position,
                uv = sourceIndex < sourceUvs.Length ? sourceUvs[sourceIndex] : Vector2.zero,
                normal = sourceIndex < sourceNormals.Length ? sourceNormals[sourceIndex] : faceNormal,
                tangent = sourceIndex < sourceTangents.Length
                    ? sourceTangents[sourceIndex]
                    : new Vector4(1f, 0f, 0f, 1f)
            };
        }

        private void WriteGeneratedAssets(Dictionary<int, TileBuilder> builders)
        {
            if (builders.Count == 0)
            {
                throw new InvalidOperationException("没有生成任何朝上的模型表面。请确认参考模型是用于俯视投射的地形 Mesh。");
            }

            List<int> sortedLabels = new List<int>(builders.Keys);
            sortedLabels.Sort();
            for (int i = 0; i < sortedLabels.Count; i++)
            {
                int label = sortedLabels[i];
                TileBuilder builder = builders[label];
                if (!builder.HasGeometry)
                {
                    continue;
                }
                if (!builder.HasSingleClosedPatchTopology(out string topologyError))
                {
                    builder.OptimizeFlatPlanarPatch();
                    if (!builder.HasSingleClosedPatchTopology(out topologyError))
                    {
                        throw new InvalidOperationException(string.Format(
                            "地块 {0} 的三角化结果不完整（{1}），已取消全部输出，避免生成破洞 Mesh。",
                            label + 1, topologyError));
                    }
                }
            }

            string folder = NormalizeAssetFolder(_outputFolder);
            EnsureAssetFolder(folder);
            string modelFolder = folder + "/Models";
            string materialFolder = folder + "/Materials";
            string textureFolder = folder + "/Textures";
            string settingFolder = folder + "/Settings";
            EnsureAssetFolder(modelFolder);
            EnsureAssetFolder(materialFolder);
            EnsureAssetFolder(textureFolder);
            EnsureAssetFolder(settingFolder);
            MeshRenderer sourceRenderer = _target.GetComponent<MeshRenderer>();
            Material[] sourceMaterials = sourceRenderer != null
                ? sourceRenderer.sharedMaterials
                : Array.Empty<Material>();
            Texture2D outlineDistanceMap = CreateOutlineDistanceMap(
                textureFolder, _target.name);

            string generatedRootName = "SLGTileMap_" + _target.name;
            RemovePreviousGeneratedRoot(generatedRootName, _target.transform.parent);
            GameObject root = new GameObject(generatedRootName);
            Undo.RegisterCreatedObjectUndo(root, "Generate SLG Map Tiles");
            Transform sourceTransform = _target.transform;
            root.transform.SetParent(sourceTransform.parent, false);
            root.transform.localPosition = sourceTransform.localPosition;
            root.transform.localRotation = sourceTransform.localRotation;
            root.transform.localScale = sourceTransform.localScale;

            for (int tileIndex = 0; tileIndex < sortedLabels.Count; tileIndex++)
            {
                int label = sortedLabels[tileIndex];
                TileBuilder builder = builders[label];
                if (!builder.HasGeometry)
                {
                    continue;
                }
                TileBuilder.OutlineRenderData renderData =
                    builder.BuildOutlineRenderData();
                Mesh mesh = new Mesh { name = string.Format("Tile_{0:000}", label + 1) };
                mesh.indexFormat = renderData.vertices.Count > 65535
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;
                mesh.SetVertices(renderData.vertices);
                mesh.SetUVs(0, renderData.uvs);
                mesh.SetUVs(1, renderData.barycentrics);
                mesh.SetUVs(2, renderData.boundaryAltitudes);
                mesh.SetNormals(renderData.normals);
                mesh.SetTangents(renderData.tangents);
                mesh.SetColors(renderData.colors);
                mesh.subMeshCount = renderData.triangles.Count;
                for (int subMesh = 0; subMesh < renderData.triangles.Count; subMesh++)
                {
                    mesh.SetTriangles(renderData.triangles[subMesh], subMesh, false);
                }
                mesh.RecalculateBounds();

                string meshPath = AssetDatabase.GenerateUniqueAssetPath(
                    modelFolder + "/" + mesh.name + ".asset");
                AssetDatabase.CreateAsset(mesh, meshPath);

                GameObject tileObject = new GameObject(mesh.name);
                Undo.RegisterCreatedObjectUndo(tileObject, "Generate SLG Map Tile");
                tileObject.transform.SetParent(root.transform, false);
                MeshFilter filter = tileObject.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                MeshRenderer renderer = tileObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = CreateTileMaterials(materialFolder, mesh.name, mesh.subMeshCount,
                    sourceMaterials, label, outlineDistanceMap);
            }

            SLGMapTileMapAsset mapAsset = CreateInstance<SLGMapTileMapAsset>();
            mapAsset.name = root.name + "_Config";
            mapAsset.version = SLGMapTileMapAsset.CurrentVersion;
            mapAsset.lineTexture = _lineTexture;
            mapAsset.sourceMesh = _target.sharedMesh;
            mapAsset.sourceMaterials = sourceMaterials;
            mapAsset.longSideResolution = _longSideResolution;
            mapAsset.minimumRegionPixels = _minimumRegionPixels;
            mapAsset.gridWidth = _gridWidth;
            mapAsset.gridHeight = _gridHeight;
            mapAsset.regionLabels = (int[])_labels.Clone();
            mapAsset.darkThreshold = _darkThreshold;
            mapAsset.closeRadius = _gapCloseRadius;
            mapAsset.skeletonSimplifyTolerance = _skeletonSimplifyTolerance;
            mapAsset.skeletonSmoothing = _skeletonSmoothing;
            mapAsset.tileGap = _tileGap;
            mapAsset.projectionOffset = _projectionOffset;
            mapAsset.projectionScale = _projectionScale;
            mapAsset.projectionRotation = _projectionRotation;
            mapAsset.hideSourceOnGenerate = _hideSourceOnGenerate;
            mapAsset.colorPreview = _colorPreview;
            mapAsset.snapManualToSkeleton = _snapManualToSkeleton;
            mapAsset.manualSnapDistance = _manualSnapDistance;
            mapAsset.manualStrokeSmoothing = _manualStrokeSmoothing;
            mapAsset.manualStrokeSimplifyTolerance =
                _manualStrokeSimplifyTolerance;
            mapAsset.manualEraseRadius = _manualEraseRadius;
            mapAsset.outlineWidth = _outlineWidth;
            mapAsset.outlineSoftness = _outlineSoftness;
            mapAsset.outlineColor = _outlineColor;
            mapAsset.outlineMapLongSide = _outlineMapLongSide;
            mapAsset.outputFolder = _outputFolder;
            mapAsset.manualSegments.AddRange(_manualSegments);
            mapAsset.manualEraseSegments.AddRange(_manualEraseSegments);
            string configPath = AssetDatabase.GenerateUniqueAssetPath(
                settingFolder + "/" + mapAsset.name + ".asset");
            AssetDatabase.CreateAsset(mapAsset, configPath);

            if (_hideSourceOnGenerate && sourceRenderer != null)
            {
                Undo.RecordObject(sourceRenderer, "Hide Source Mesh");
                sourceRenderer.enabled = false;
            }

            AssetDatabase.SaveAssets();
            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(mapAsset);
            EditorUtility.DisplayDialog("SLG 地块切分",
                string.Format("已生成 {0} 个独立地块。\n配置已保存：{1}",
                    sortedLabels.Count, configPath), "确定");
        }

        private static void RemovePreviousGeneratedRoot(string rootName,
            Transform expectedParent)
        {
            if (expectedParent == null)
            {
                GameObject[] roots = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().GetRootGameObjects();
                for (int i = roots.Length - 1; i >= 0; i--)
                {
                    if (roots[i].name == rootName)
                    {
                        Undo.DestroyObjectImmediate(roots[i]);
                    }
                }
                return;
            }

            for (int i = expectedParent.childCount - 1; i >= 0; i--)
            {
                Transform child = expectedParent.GetChild(i);
                if (child.name == rootName)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }
        }

        private Texture2D CreateOutlineDistanceMap(string folder,
            string targetName)
        {
            if (_vectorSkeletonPaths.Count == 0
                || _gridWidth <= 0 || _gridHeight <= 0)
            {
                throw new InvalidOperationException(
                    "没有可用于生成地块描边距离图的区域数据。");
            }

            int mapLongSide = Mathf.Clamp(_outlineMapLongSide,
                512, 4096);
            int mapWidth;
            int mapHeight;
            if (_gridWidth >= _gridHeight)
            {
                mapWidth = mapLongSide;
                mapHeight = Mathf.Max(1, Mathf.RoundToInt(
                    mapLongSide * _gridHeight / (float)_gridWidth));
            }
            else
            {
                mapHeight = mapLongSide;
                mapWidth = Mathf.Max(1, Mathf.RoundToInt(
                    mapLongSide * _gridWidth / (float)_gridHeight));
            }

            int pixelCount = mapWidth * mapHeight;
            float[] distances = new float[pixelCount];
            Bounds bounds = _target.sharedMesh.bounds;
            float physicalWidth = Mathf.Max(0.0001f,
                bounds.size.x * _projectionScale.x);
            float physicalHeight = Mathf.Max(0.0001f,
                bounds.size.z * _projectionScale.y);
            float maximumDistance = Mathf.Max(_outlineWidth * 4f,
                Mathf.Min(physicalWidth, physicalHeight) * 0.15f);
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = maximumDistance;
            }

            BuildAnalyticVectorDistanceField(distances, mapWidth, mapHeight,
                physicalWidth, physicalHeight, maximumDistance);

            NormalizeDistanceMap(distances, maximumDistance);
            Texture2D encodingTexture = new Texture2D(mapWidth, mapHeight,
                TextureFormat.RGB24, false, true)
            {
                name = "SLGTileMap_" + targetName + "_OutlineDistance",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            Color32[] pixels = new Color32[distances.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = (byte)Mathf.RoundToInt(
                    distances[i] * 255f);
                pixels[i] = new Color32(value, value, value, 255);
            }
            encodingTexture.SetPixels32(pixels);
            encodingTexture.Apply(false, false);
            byte[] pngBytes = encodingTexture.EncodeToPNG();
            string path = AssetDatabase.GenerateUniqueAssetPath(
                folder + "/" + encodingTexture.name + ".png");
            DestroyImmediate(encodingTexture);
            string absolutePath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", path));
            System.IO.File.WriteAllBytes(absolutePath, pngBytes);
            AssetDatabase.ImportAsset(path,
                ImportAssetOptions.ForceSynchronousImport);
            ConfigureOutlinePngImporter(path, mapLongSide);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                path);
            if (texture == null)
            {
                throw new InvalidOperationException(
                    "描边距离 PNG 导入失败：" + path);
            }
            return texture;
        }

        private static void NormalizeDistanceMap(float[] distances,
            float maximumDistance)
        {
            float inverseMaximum = 1f / Mathf.Max(0.000001f,
                maximumDistance);
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = Mathf.Clamp01(
                    distances[i] * inverseMaximum);
            }
        }

        private static void ConfigureOutlinePngImporter(string path,
            int maximumSize)
        {
            TextureImporter importer =
                AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                return;
            }
            importer.textureType = TextureImporterType.SingleChannel;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression =
                TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = Mathf.Clamp(maximumSize,
                512, 4096);
            importer.SaveAndReimport();
        }

        private void BuildAnalyticVectorDistanceField(float[] distances,
            int mapWidth, int mapHeight, float physicalWidth,
            float physicalHeight, float maximumDistance)
        {
            for (int pathIndex = 0;
                pathIndex < _vectorSkeletonPaths.Count; pathIndex++)
            {
                List<Vector2> path = _vectorSkeletonPaths[pathIndex];
                for (int segment = 0; segment + 1 < path.Count; segment++)
                {
                    Vector2 sourceA = path[segment];
                    Vector2 sourceB = path[segment + 1];
                    Vector2 a = new Vector2(
                        sourceA.x / _gridWidth * physicalWidth,
                        sourceA.y / _gridHeight * physicalHeight);
                    Vector2 b = new Vector2(
                        sourceB.x / _gridWidth * physicalWidth,
                        sourceB.y / _gridHeight * physicalHeight);
                    Vector2 direction = b - a;
                    float lengthSquared = direction.sqrMagnitude;
                    if (lengthSquared <= 0.0000000001f)
                    {
                        continue;
                    }

                    int minX = Mathf.Clamp(Mathf.FloorToInt(
                        (Mathf.Min(a.x, b.x) - maximumDistance)
                        / physicalWidth * mapWidth), 0, mapWidth - 1);
                    int maxX = Mathf.Clamp(Mathf.CeilToInt(
                        (Mathf.Max(a.x, b.x) + maximumDistance)
                        / physicalWidth * mapWidth), 0, mapWidth - 1);
                    int minY = Mathf.Clamp(Mathf.FloorToInt(
                        (Mathf.Min(a.y, b.y) - maximumDistance)
                        / physicalHeight * mapHeight), 0, mapHeight - 1);
                    int maxY = Mathf.Clamp(Mathf.CeilToInt(
                        (Mathf.Max(a.y, b.y) + maximumDistance)
                        / physicalHeight * mapHeight), 0, mapHeight - 1);

                    for (int y = minY; y <= maxY; y++)
                    {
                        float localY = (y + 0.5f) / mapHeight
                            * physicalHeight;
                        int row = y * mapWidth;
                        for (int x = minX; x <= maxX; x++)
                        {
                            Vector2 point = new Vector2(
                                (x + 0.5f) / mapWidth * physicalWidth,
                                localY);
                            float t = Mathf.Clamp01(
                                Vector2.Dot(point - a, direction)
                                / lengthSquared);
                            float distance = Vector2.Distance(point,
                                a + direction * t);
                            int index = row + x;
                            if (distance < distances[index])
                            {
                                distances[index] = distance;
                            }
                        }
                    }
                }
            }
        }

        private static void BuildEuclideanDistanceField(float[] distances,
            int width, int height, float stepX, float stepY)
        {
            float[] intermediate = new float[distances.Length];
            int maxLength = Mathf.Max(width, height);
            float[] source = new float[maxLength];
            float[] result = new float[maxLength];
            int[] sites = new int[maxLength];
            float[] intersections = new float[maxLength + 1];

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    source[x] = distances[row + x];
                }
                DistanceTransform1D(source, result, width, stepX,
                    sites, intersections);
                for (int x = 0; x < width; x++)
                {
                    intermediate[row + x] = result[x];
                }
            }

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    source[y] = intermediate[y * width + x];
                }
                DistanceTransform1D(source, result, height, stepY,
                    sites, intersections);
                for (int y = 0; y < height; y++)
                {
                    distances[y * width + x] = Mathf.Sqrt(result[y]);
                }
            }
        }

        private static void DistanceTransform1D(float[] source,
            float[] result, int length, float spacing, int[] sites,
            float[] intersections)
        {
            float spacingSquared = spacing * spacing;
            int envelopeCount = 0;
            for (int q = 0; q < length; q++)
            {
                if (float.IsInfinity(source[q]))
                {
                    continue;
                }
                if (envelopeCount == 0)
                {
                    sites[0] = q;
                    intersections[0] = float.NegativeInfinity;
                    intersections[1] = float.PositiveInfinity;
                    envelopeCount = 1;
                    continue;
                }

                float intersection;
                while (true)
                {
                    int previous = sites[envelopeCount - 1];
                    intersection = ((source[q] + q * q * spacingSquared)
                        - (source[previous]
                            + previous * previous * spacingSquared))
                        / (2f * spacingSquared * (q - previous));
                    if (intersection > intersections[envelopeCount - 1]
                        || envelopeCount == 1)
                    {
                        break;
                    }
                    envelopeCount--;
                }
                sites[envelopeCount] = q;
                intersections[envelopeCount] = intersection;
                intersections[envelopeCount + 1] = float.PositiveInfinity;
                envelopeCount++;
            }

            if (envelopeCount == 0)
            {
                for (int q = 0; q < length; q++)
                {
                    result[q] = float.PositiveInfinity;
                }
                return;
            }

            int envelope = 0;
            for (int q = 0; q < length; q++)
            {
                while (envelope + 1 < envelopeCount
                    && intersections[envelope + 1] < q)
                {
                    envelope++;
                }
                int site = sites[envelope];
                float delta = (q - site) * spacing;
                result[q] = delta * delta + source[site];
            }
        }

        private Material[] CreateTileMaterials(string folder, string meshName,
            int subMeshCount, Material[] sourceMaterials, int tileLabel,
            Texture2D outlineDistanceMap)
        {
            Material[] materials = new Material[subMeshCount];
            Shader shader = Shader.Find("Custom/SLG Map/Tile Inner Outline");
            if (shader == null)
            {
                throw new InvalidOperationException("未找到 URP Unlit Shader，无法创建颜色预览材质。");
            }

            for (int i = 0; i < subMeshCount; i++)
            {
                Material source = i < sourceMaterials.Length
                    ? sourceMaterials[i]
                    : null;
                Material material = new Material(shader)
                {
                    name = subMeshCount > 1
                        ? meshName + "_Outline_" + i
                        : meshName + "_Outline"
                };
                CopyBaseMaterialProperties(source, material);
                if (_colorPreview)
                {
                    material.SetColor("_BaseColor", TileColor(tileLabel));
                    material.SetTexture("_BaseMap", Texture2D.whiteTexture);
                }
                material.SetColor("_OutlineColor", _outlineColor);
                material.SetFloat("_OutlineWidth", Mathf.Max(0f, _outlineWidth));
                material.SetFloat("_OutlineSoftness",
                    Mathf.Clamp01(_outlineSoftness));
                material.SetTexture("_OutlineDistanceMap", outlineDistanceMap);
                Bounds bounds = _target.sharedMesh.bounds;
                material.SetFloat("_OutlineDistanceScale",
                    Mathf.Max(_outlineWidth * 4f,
                        Mathf.Min(bounds.size.x * _projectionScale.x,
                            bounds.size.z * _projectionScale.y) * 0.15f));
                material.SetVector("_ProjectionCenterAndSize", new Vector4(
                    bounds.center.x + _projectionOffset.x,
                    bounds.center.z + _projectionOffset.y,
                    Mathf.Max(0.0001f, bounds.size.x * _projectionScale.x),
                    Mathf.Max(0.0001f, bounds.size.z * _projectionScale.y)));
                material.SetFloat("_ProjectionRotation", _projectionRotation);
                SetProjectionUvRows(material);
                string path = AssetDatabase.GenerateUniqueAssetPath(
                    folder + "/" + material.name + ".mat");
                AssetDatabase.CreateAsset(material, path);
                materials[i] = material;
            }
            return materials;
        }

        private static void CopyBaseMaterialProperties(Material source,
            Material destination)
        {
            if (source == null)
            {
                destination.SetColor("_BaseColor", Color.white);
                destination.SetTexture("_BaseMap", Texture2D.whiteTexture);
                return;
            }

            Texture texture = source.HasProperty("_BaseMap")
                ? source.GetTexture("_BaseMap")
                : source.HasProperty("_MainTex")
                    ? source.GetTexture("_MainTex")
                    : null;
            Color color = source.HasProperty("_BaseColor")
                ? source.GetColor("_BaseColor")
                : source.HasProperty("_Color")
                    ? source.GetColor("_Color")
                    : Color.white;
            destination.SetTexture("_BaseMap",
                texture != null ? texture : Texture2D.whiteTexture);
            destination.SetColor("_BaseColor", color);

            string textureProperty = source.HasProperty("_BaseMap")
                ? "_BaseMap"
                : source.HasProperty("_MainTex") ? "_MainTex" : string.Empty;
            if (!string.IsNullOrEmpty(textureProperty))
            {
                destination.SetTextureScale("_BaseMap",
                    source.GetTextureScale(textureProperty));
                destination.SetTextureOffset("_BaseMap",
                    source.GetTextureOffset(textureProperty));
            }
        }

        private static Color TileColor(int label)
        {
            float hue = Mathf.Repeat(label * 0.61803398875f + 0.08f, 1f);
            return Color.HSVToRGB(hue, 0.55f, 0.95f);
        }

        private bool EnsureTextureReadable(bool showDialog)
        {
            if (_lineTexture == null || _lineTexture.isReadable)
            {
                return _lineTexture != null;
            }

            string path = AssetDatabase.GetAssetPath(_lineTexture);
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                if (showDialog)
                {
                    EditorUtility.DisplayDialog("SLG 地块切分",
                        "该纹理不可读取，且不是可修改导入设置的资源。", "确定");
                }
                return false;
            }
            importer.isReadable = true;
            importer.SaveAndReimport();
            return _lineTexture.isReadable;
        }

        private void MakeTextureReadable()
        {
            if (EnsureTextureReadable(true))
            {
                Invalidate();
            }
        }

        private void ImportExternalLineTexture()
        {
            string sourcePath = EditorUtility.OpenFilePanel(
                "选择地块线框图", Application.dataPath, "png,jpg,jpeg,tga");
            if (string.IsNullOrEmpty(sourcePath))
            {
                return;
            }

            const string inputFolder = DefaultOutputFolder + "/Inputs";
            EnsureAssetFolder(inputFolder);
            string destination = AssetDatabase.GenerateUniqueAssetPath(
                inputFolder + "/" + System.IO.Path.GetFileName(sourcePath));
            FileUtil.CopyFileOrDirectory(sourcePath, destination);
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate);
            _lineTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(destination);
            EnsureTextureReadable(false);
            Invalidate();
        }

        private bool EnsureMeshReadable(bool showDialog)
        {
            if (_target == null || _target.sharedMesh == null)
            {
                return false;
            }
            if (_target.sharedMesh.isReadable)
            {
                return true;
            }

            string path = AssetDatabase.GetAssetPath(_target.sharedMesh);
            ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer != null)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                return _target != null && _target.sharedMesh != null
                    && _target.sharedMesh.isReadable;
            }

            if (showDialog)
            {
                EditorUtility.DisplayDialog("SLG 地块切分",
                    "参考 Mesh 不可读取。请在模型导入设置中开启 Read/Write Enabled。", "确定");
            }
            return false;
        }

        private Vector2 LocalToImageUV(Vector3 localPosition)
        {
            GetProjectionWorldFrame(out Vector3 worldCenter,
                out Quaternion worldRotation, out float worldWidth,
                out float worldHeight);
            Vector3 worldPoint = _target.transform.TransformPoint(localPosition);
            Vector3 delta = worldPoint - worldCenter;
            Vector3 axisX = worldRotation * Vector3.right;
            Vector3 axisY = worldRotation * Vector3.forward;
            return new Vector2(
                Vector3.Dot(delta, axisX) / worldWidth + 0.5f,
                Vector3.Dot(delta, axisY) / worldHeight + 0.5f);
        }

        private Vector3 ImageUVToLocal(Vector2 uv)
        {
            GetProjectionWorldFrame(out Vector3 worldCenter,
                out Quaternion worldRotation, out float worldWidth,
                out float worldHeight);
            Vector3 worldPoint = worldCenter
                + worldRotation * Vector3.right
                * ((uv.x - 0.5f) * worldWidth)
                + worldRotation * Vector3.forward
                * ((uv.y - 0.5f) * worldHeight);
            return _target.transform.InverseTransformPoint(worldPoint);
        }

        private void GetProjectionWorldFrame(out Vector3 worldCenter,
            out Quaternion worldRotation, out float worldWidth,
            out float worldHeight)
        {
            Bounds bounds = _target.sharedMesh.bounds;
            Vector3 localCenter = new Vector3(
                bounds.center.x + _projectionOffset.x,
                bounds.center.y,
                bounds.center.z + _projectionOffset.y);
            worldCenter = _target.transform.TransformPoint(localCenter);
            worldRotation = _target.transform.rotation
                * Quaternion.AngleAxis(-_projectionRotation, Vector3.up);
            worldWidth = Mathf.Max(0.0001f,
                GetProjectionBaseWorldWidth() * _projectionScale.x);
            worldHeight = Mathf.Max(0.0001f,
                GetProjectionBaseWorldHeight() * _projectionScale.y);
        }

        private float GetProjectionBaseWorldWidth()
        {
            Bounds bounds = _target.sharedMesh.bounds;
            return Mathf.Max(0.0001f, bounds.size.x
                * _target.transform.TransformVector(Vector3.right).magnitude);
        }

        private float GetProjectionBaseWorldHeight()
        {
            Bounds bounds = _target.sharedMesh.bounds;
            return Mathf.Max(0.0001f, bounds.size.z
                * _target.transform.TransformVector(Vector3.forward).magnitude);
        }

        private void SetProjectionUvRows(Material material)
        {
            Vector2 origin = LocalToImageUV(Vector3.zero);
            Vector2 axisX = LocalToImageUV(Vector3.right) - origin;
            Vector2 axisY = LocalToImageUV(Vector3.up) - origin;
            Vector2 axisZ = LocalToImageUV(Vector3.forward) - origin;
            material.SetVector("_ProjectionUvRowX", new Vector4(
                axisX.x, axisY.x, axisZ.x, origin.x));
            material.SetVector("_ProjectionUvRowY", new Vector4(
                axisX.y, axisY.y, axisZ.y, origin.y));
            material.SetFloat("_UseProjectionUvRows", 1f);
        }

        private Vector2 ImageUVToGrid(Vector2 uv)
        {
            return new Vector2(uv.x * _gridWidth, uv.y * _gridHeight);
        }

        private void EnsureSurfaceProjectionCacheValid()
        {
            int targetId = _target != null
                ? _target.GetInstanceID()
                : 0;
            Matrix4x4 localToWorld = _target != null
                ? _target.transform.localToWorldMatrix
                : Matrix4x4.identity;
            if (_projectionCacheTargetId == targetId
                && _projectionCacheLocalToWorld == localToWorld
                && _projectionCacheOffset == _projectionOffset
                && _projectionCacheScale == _projectionScale
                && Mathf.Approximately(_projectionCacheRotation,
                    _projectionRotation))
            {
                return;
            }

            _surfaceProjectionCache.Clear();
            _projectionCacheTargetId = targetId;
            _projectionCacheLocalToWorld = localToWorld;
            _projectionCacheOffset = _projectionOffset;
            _projectionCacheScale = _projectionScale;
            _projectionCacheRotation = _projectionRotation;
        }

        private bool TryGetCachedSurfaceProjection(Vector2 uv,
            out Vector3 worldPoint)
        {
            if (_surfaceProjectionCache.TryGetValue(uv, out worldPoint))
            {
                return true;
            }
            if (!TryProjectToSurface(uv, out worldPoint))
            {
                return false;
            }
            _surfaceProjectionCache[uv] = worldPoint;
            return true;
        }

        private bool TryProjectToSurface(Vector2 uv, out Vector3 worldPoint)
        {
            worldPoint = default(Vector3);
            if (_target == null || _target.sharedMesh == null)
            {
                return false;
            }
            Bounds bounds = _target.sharedMesh.bounds;
            Vector3 local = ImageUVToLocal(uv);
            float rayHeight = Mathf.Max(1f, bounds.size.y + 1f);
            Vector3 start = _target.transform.TransformPoint(local + Vector3.up * rayHeight);
            Ray ray = new Ray(start, -_target.transform.up);
            if (EnsurePreviewCollider()
                && TryRaycastPreviewCollider(ray, out RaycastHit hit))
            {
                _previewSurfaceHitCount++;
                worldPoint = hit.point + hit.normal * 0.001f;
                return true;
            }

            // Keep the preview visible even when a model has no usable collider hit,
            // so the author can diagnose projection settings instead of seeing nothing.
            worldPoint = _target.transform.TransformPoint(new Vector3(
                local.x, bounds.max.y + 0.01f, local.z));
            _previewFallbackCount++;
            return true;
        }

        private bool EnsurePreviewCollider()
        {
            if (_previewCollider != null && _target != null
                && _previewCollider.sharedMesh == _target.sharedMesh)
            {
                return true;
            }
            DestroyPreviewCollider();
            if (_target == null || _target.sharedMesh == null)
            {
                return false;
            }

            _previewColliderObject = new GameObject(PreviewColliderName)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _previewColliderObject.transform.SetParent(_target.transform, false);
            _previewCollider = _previewColliderObject.AddComponent<MeshCollider>();
            _previewCollider.sharedMesh = _target.sharedMesh;
            Physics.SyncTransforms();
            return true;
        }

        private void DestroyPreviewCollider()
        {
            if (_previewColliderObject != null)
            {
                DestroyImmediate(_previewColliderObject);
            }
            _previewColliderObject = null;
            _previewCollider = null;
        }

        private bool TryRaycastPreviewCollider(Ray ray, out RaycastHit chosenHit)
        {
            chosenHit = default(RaycastHit);
            if (_previewCollider == null)
            {
                return false;
            }
            RaycastHit[] hits = Physics.RaycastAll(ray, 100000f, ~0,
                QueryTriggerInteraction.Collide);
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (hit.collider == _previewCollider && hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    chosenHit = hit;
                }
            }
            return bestDistance < float.MaxValue;
        }

        private int Index(int x, int y)
        {
            return y * _gridWidth + x;
        }

        private bool IsInside(int x, int y)
        {
            return x >= 0 && x < _gridWidth && y >= 0 && y < _gridHeight;
        }

        private static string NormalizeAssetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DefaultOutputFolder;
            }
            path = path.Replace('\\', '/').TrimEnd('/');
            return path.StartsWith("Assets", StringComparison.Ordinal)
                ? path
                : DefaultOutputFolder;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
            {
                return;
            }
            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }
    }
}
