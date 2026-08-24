using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZXXTools.FBXMeshEditor
{
    internal enum FBXMeshComponentMode
    {
        Vertex,
        Edge,
        Face
    }

    [Serializable]
    internal readonly struct FBXMeshEdge : IEquatable<FBXMeshEdge>
    {
        public readonly int A;
        public readonly int B;

        public FBXMeshEdge(int a, int b)
        {
            A = Mathf.Min(a, b);
            B = Mathf.Max(a, b);
        }

        public bool Equals(FBXMeshEdge other) => A == other.A && B == other.B;
        public override bool Equals(object obj) => obj is FBXMeshEdge other && Equals(other);
        public override int GetHashCode() => (A * 397) ^ B;
    }

    internal sealed class FBXMeshLogicalFace
    {
        public readonly int[] Vertices;
        public readonly int TriangleA;
        public readonly int TriangleB;
        // 逻辑溶解后 TriangleA/B 会失去意义，但 Patch 重建仍需知道它覆盖的真实三角形。
        public readonly int[] SourceTriangles;
        public bool IsQuad => Vertices.Length == 4;

        public FBXMeshLogicalFace(int triangleA, int triangleB, params int[] vertices)
            : this(triangleA, triangleB,
                triangleB >= 0 ? new[] { triangleA, triangleB } : new[] { triangleA }, vertices)
        {
        }

        public FBXMeshLogicalFace(int triangleA, int triangleB, int[] sourceTriangles,
            int[] vertices)
        {
            TriangleA = triangleA;
            TriangleB = triangleB;
            SourceTriangles = sourceTriangles ?? Array.Empty<int>();
            Vertices = vertices;
        }
    }

    internal readonly struct FBXMeshLoopCutFace
    {
        public readonly int FaceIndex;
        public readonly int FirstEdgePosition;
        public readonly bool FlipPosition;

        public FBXMeshLoopCutFace(int faceIndex, int firstEdgePosition, bool flipPosition)
        {
            FaceIndex = faceIndex;
            FirstEdgePosition = firstEdgePosition;
            FlipPosition = flipPosition;
        }
    }

    /// <summary>从 Unity 三角形数据构建边、面与顶点邻接关系。</summary>
    internal sealed class FBXMeshTopology
    {
        public FBXMeshEdge[] Edges { get; private set; } = Array.Empty<FBXMeshEdge>();
        public int[] Triangles { get; private set; } = Array.Empty<int>();
        public FBXMeshLogicalFace[] LogicalFaces { get; private set; } = Array.Empty<FBXMeshLogicalFace>();
        public int QuadCount { get; private set; }
        private readonly Dictionary<FBXMeshEdge, int> _edgeIndices = new Dictionary<FBXMeshEdge, int>();
        private readonly Dictionary<int, List<int>> _edgeToLogicalFaces = new Dictionary<int, List<int>>();
        // 保留溶解顺序，连续边段会按照用户的选择顺序逐步合并逻辑面。
        private readonly List<FBXMeshEdge> _explicitDissolvedEdges = new List<FBXMeshEdge>();
        private readonly HashSet<int> _logicallyDissolvedVertices = new HashSet<int>();

        public bool IsVertexLogicallyDissolved(int vertexIndex)
        {
            return _logicallyDissolvedVertices.Contains(vertexIndex);
        }

        public void SetDissolvedEdges(IList<int> pairs)
        {
            _explicitDissolvedEdges.Clear();
            _logicallyDissolvedVertices.Clear();
            if (pairs == null) return;
            var dissolvedIncidence = new Dictionary<int, int>();
            for (int i = 0; i + 1 < pairs.Count; i += 2)
            {
                var edge = new FBXMeshEdge(pairs[i], pairs[i + 1]);
                if (_explicitDissolvedEdges.Contains(edge)) continue;
                _explicitDissolvedEdges.Add(edge);
                dissolvedIncidence.TryGetValue(edge.A, out int countA);
                dissolvedIncidence.TryGetValue(edge.B, out int countB);
                dissolvedIncidence[edge.A] = countA + 1;
                dissolvedIncidence[edge.B] = countB + 1;
            }
            // 开放边段保留两个端点；闭合 Loop 和连续边段只隐藏内部控制点。
            foreach (KeyValuePair<int, int> pair in dissolvedIncidence)
                if (pair.Value == 2) _logicallyDissolvedVertices.Add(pair.Key);
        }

        private sealed class EdgeFaces
        {
            public readonly List<int> faces = new List<int>(2);
        }

        private readonly struct QuadCandidate
        {
            public readonly FBXMeshEdge diagonal;
            public readonly int faceA;
            public readonly int faceB;
            public readonly float score;

            public QuadCandidate(FBXMeshEdge diagonal, int faceA, int faceB, float score)
            {
                this.diagonal = diagonal;
                this.faceA = faceA;
                this.faceB = faceB;
                this.score = score;
            }
        }

        public void Rebuild(Mesh mesh, int[] logicalVertexRepresentatives = null)
        {
            Triangles = mesh != null ? mesh.triangles : Array.Empty<int>();
            QuadCount = 0;
            if (mesh == null)
            {
                Edges = Array.Empty<FBXMeshEdge>();
                LogicalFaces = Array.Empty<FBXMeshLogicalFace>();
                return;
            }

            Vector3[] vertices = mesh.vertices;
            if (logicalVertexRepresentatives != null
                && logicalVertexRepresentatives.Length == vertices.Length)
            {
                for (int i = 0; i < Triangles.Length; i++)
                    Triangles[i] = logicalVertexRepresentatives[Triangles[i]];
            }
            var edgeFaces = new Dictionary<FBXMeshEdge, EdgeFaces>();
            for (int i = 0; i + 2 < Triangles.Length; i += 3)
            {
                int face = i / 3;
                AddFaceEdge(Triangles[i], Triangles[i + 1], face, edgeFaces);
                AddFaceEdge(Triangles[i + 1], Triangles[i + 2], face, edgeFaces);
                AddFaceEdge(Triangles[i + 2], Triangles[i], face, edgeFaces);
            }

            List<QuadCandidate> quads = InferQuads(vertices, edgeFaces);
            var inferredDiagonals = new HashSet<FBXMeshEdge>();
            var usedFaces = new HashSet<int>();
            var logicalFaces = new List<FBXMeshLogicalFace>();
            foreach (QuadCandidate quad in quads)
            {
                int[] boundary = BuildQuadBoundary(
                    quad.faceA, quad.faceB, quad.diagonal, vertices);
                if (boundary == null) continue;
                inferredDiagonals.Add(quad.diagonal);
                usedFaces.Add(quad.faceA);
                usedFaces.Add(quad.faceB);
                logicalFaces.Add(new FBXMeshLogicalFace(quad.faceA, quad.faceB, boundary));
            }
            for (int face = 0; face < Triangles.Length / 3; face++)
            {
                if (usedFaces.Contains(face)) continue;
                int start = face * 3;
                logicalFaces.Add(new FBXMeshLogicalFace(face, -1,
                    Triangles[start], Triangles[start + 1], Triangles[start + 2]));
            }
            HashSet<FBXMeshEdge> appliedDissolves = ApplyExplicitDissolves(logicalFaces);
            LogicalFaces = logicalFaces.ToArray();
            QuadCount = quads.Count;
            var edges = new List<FBXMeshEdge>(edgeFaces.Count - inferredDiagonals.Count);
            foreach (FBXMeshEdge edge in edgeFaces.Keys)
                if (!inferredDiagonals.Contains(edge) && !appliedDissolves.Contains(edge)) edges.Add(edge);
            Edges = edges.ToArray();
            BuildLogicalAdjacency();
        }

        private HashSet<FBXMeshEdge> ApplyExplicitDissolves(List<FBXMeshLogicalFace> faces)
        {
            var applied = new HashSet<FBXMeshEdge>();
            foreach (FBXMeshEdge dissolved in _explicitDissolvedEdges)
            {
                int first = -1;
                int second = -1;
                for (int i = 0; i < faces.Count; i++)
                {
                    if (!FaceContainsEdge(faces[i], dissolved)) continue;
                    if (first < 0) first = i;
                    else { second = i; break; }
                }
                if (first < 0 || second < 0) continue;
                int[] merged = MergeFaceBoundaries(faces[first].Vertices, faces[second].Vertices, dissolved);
                if (merged == null || merged.Length < 3) continue;
                var sourceTriangles = new List<int>(faces[first].SourceTriangles.Length
                    + faces[second].SourceTriangles.Length);
                sourceTriangles.AddRange(faces[first].SourceTriangles);
                foreach (int triangle in faces[second].SourceTriangles)
                    if (!sourceTriangles.Contains(triangle)) sourceTriangles.Add(triangle);
                faces[first] = new FBXMeshLogicalFace(-1, -1,
                    sourceTriangles.ToArray(), merged);
                faces.RemoveAt(second);
                applied.Add(dissolved);
            }
            return applied;
        }

        private static bool FaceContainsEdge(FBXMeshLogicalFace face, FBXMeshEdge edge)
        {
            for (int i = 0; i < face.Vertices.Length; i++)
                if (new FBXMeshEdge(face.Vertices[i], face.Vertices[(i + 1) % face.Vertices.Length]).Equals(edge))
                    return true;
            return false;
        }

        private static int[] MergeFaceBoundaries(int[] a, int[] b, FBXMeshEdge removed)
        {
            var boundary = new List<FBXMeshEdge>();
            AddBoundaryEdges(a, removed, boundary);
            AddBoundaryEdges(b, removed, boundary);
            var adjacency = new Dictionary<int, List<int>>();
            foreach (FBXMeshEdge edge in boundary)
            {
                if (!adjacency.TryGetValue(edge.A, out List<int> listA)) adjacency[edge.A] = listA = new List<int>();
                if (!adjacency.TryGetValue(edge.B, out List<int> listB)) adjacency[edge.B] = listB = new List<int>();
                listA.Add(edge.B);
                listB.Add(edge.A);
            }
            foreach (List<int> neighbors in adjacency.Values) if (neighbors.Count != 2) return null;
            var result = new List<int>();
            int start = boundary[0].A;
            int previous = -1;
            int current = start;
            do
            {
                result.Add(current);
                List<int> nextOptions = adjacency[current];
                int next = nextOptions[0] != previous ? nextOptions[0] : nextOptions[1];
                previous = current;
                current = next;
                if (result.Count > adjacency.Count) return null;
            } while (current != start);
            return result.ToArray();
        }

        private static void AddBoundaryEdges(int[] vertices, FBXMeshEdge removed, List<FBXMeshEdge> output)
        {
            for (int i = 0; i < vertices.Length; i++)
            {
                var edge = new FBXMeshEdge(vertices[i], vertices[(i + 1) % vertices.Length]);
                if (!edge.Equals(removed)) output.Add(edge);
            }
        }

        private int[] BuildQuadBoundary(
            int faceA, int faceB, FBXMeshEdge diagonal, Vector3[] vertices)
        {
            var boundaryEdges = new List<FBXMeshEdge>(4);
            AddTriangleBoundaryEdges(faceA, diagonal, boundaryEdges);
            AddTriangleBoundaryEdges(faceB, diagonal, boundaryEdges);
            if (boundaryEdges.Count != 4) return null;

            var adjacency = new Dictionary<int, List<int>>();
            foreach (FBXMeshEdge edge in boundaryEdges)
            {
                if (!adjacency.TryGetValue(edge.A, out List<int> a)) adjacency[edge.A] = a = new List<int>();
                if (!adjacency.TryGetValue(edge.B, out List<int> b)) adjacency[edge.B] = b = new List<int>();
                a.Add(edge.B);
                b.Add(edge.A);
            }
            foreach (List<int> neighbors in adjacency.Values)
                if (neighbors.Count != 2) return null;

            var result = new List<int>(4);
            int start = boundaryEdges[0].A;
            int previous = -1;
            int current = start;
            do
            {
                result.Add(current);
                List<int> neighbors = adjacency[current];
                int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
                previous = current;
                current = next;
                if (result.Count > 4) return null;
            } while (current != start);
            if (result.Count != 4) return null;

            Vector3 polygonNormal = Vector3.Cross(
                vertices[result[1]] - vertices[result[0]],
                vertices[result[2]] - vertices[result[1]]).normalized;
            if (Vector3.Dot(polygonNormal, GetFaceNormal(vertices, faceA)) < 0f)
                result.Reverse();
            return result.ToArray();
        }

        private void AddTriangleBoundaryEdges(
            int face, FBXMeshEdge diagonal, List<FBXMeshEdge> output)
        {
            int start = face * 3;
            for (int i = 0; i < 3; i++)
            {
                var edge = new FBXMeshEdge(
                    Triangles[start + i], Triangles[start + (i + 1) % 3]);
                if (!edge.Equals(diagonal)) output.Add(edge);
            }
        }

        private void BuildLogicalAdjacency()
        {
            _edgeIndices.Clear();
            _edgeToLogicalFaces.Clear();
            for (int i = 0; i < Edges.Length; i++)
                _edgeIndices[Edges[i]] = i;

            for (int faceIndex = 0; faceIndex < LogicalFaces.Length; faceIndex++)
            {
                int[] vertices = LogicalFaces[faceIndex].Vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    var edge = new FBXMeshEdge(vertices[i], vertices[(i + 1) % vertices.Length]);
                    if (!_edgeIndices.TryGetValue(edge, out int edgeIndex)) continue;
                    if (!_edgeToLogicalFaces.TryGetValue(edgeIndex, out List<int> faces))
                    {
                        faces = new List<int>(2);
                        _edgeToLogicalFaces.Add(edgeIndex, faces);
                    }
                    faces.Add(faceIndex);
                }
            }
        }

        public List<int> GetEdgeLoop(int startEdgeIndex)
        {
            var result = new List<int>();
            if (startEdgeIndex < 0 || startEdgeIndex >= Edges.Length)
                return result;

            result.Add(startEdgeIndex);
            if (!_edgeToLogicalFaces.TryGetValue(startEdgeIndex, out List<int> adjacentFaces))
                return result;

            foreach (int faceIndex in adjacentFaces)
                WalkLoopDirection(startEdgeIndex, faceIndex, result);
            return result;
        }

        /// <summary>
        /// 获取首尾相连的选择循环边。与插线使用的 Edge Ring 不同，
        /// 这里在每个顶点寻找不与当前边共享逻辑面的唯一延续边。
        /// </summary>
        public List<int> GetConnectedEdgeLoop(int startEdgeIndex)
        {
            var result = new List<int>();
            if (startEdgeIndex < 0 || startEdgeIndex >= Edges.Length) return result;
            result.Add(startEdgeIndex);
            var visited = new HashSet<int> { startEdgeIndex };
            FBXMeshEdge start = Edges[startEdgeIndex];
            WalkConnectedEdgeLoop(startEdgeIndex, start.A, result, visited);
            WalkConnectedEdgeLoop(startEdgeIndex, start.B, result, visited);
            return result;
        }

        private void WalkConnectedEdgeLoop(int startEdgeIndex, int startVertex,
            List<int> result, HashSet<int> visited)
        {
            int currentEdge = startEdgeIndex;
            int currentVertex = startVertex;
            while (true)
            {
                int continuation = -1;
                for (int edgeIndex = 0; edgeIndex < Edges.Length; edgeIndex++)
                {
                    if (edgeIndex == currentEdge || visited.Contains(edgeIndex)) continue;
                    FBXMeshEdge candidate = Edges[edgeIndex];
                    if (candidate.A != currentVertex && candidate.B != currentVertex) continue;
                    if (EdgesShareLogicalFace(currentEdge, edgeIndex)) continue;
                    if (continuation >= 0)
                    {
                        // 极点有多个可能方向时停止，不擅自拐弯。
                        continuation = -1;
                        break;
                    }
                    continuation = edgeIndex;
                }
                if (continuation < 0) break;
                visited.Add(continuation);
                result.Add(continuation);
                FBXMeshEdge next = Edges[continuation];
                currentVertex = next.A == currentVertex ? next.B : next.A;
                currentEdge = continuation;
            }
        }

        private bool EdgesShareLogicalFace(int firstEdge, int secondEdge)
        {
            if (!_edgeToLogicalFaces.TryGetValue(firstEdge, out List<int> firstFaces)
                || !_edgeToLogicalFaces.TryGetValue(secondEdge, out List<int> secondFaces))
                return false;
            foreach (int face in firstFaces)
                if (secondFaces.Contains(face)) return true;
            return false;
        }

        public List<int> GetLoopQuadFaces(List<int> loopEdges)
        {
            var edgeSet = new HashSet<int>(loopEdges);
            var result = new List<int>();
            for (int faceIndex = 0; faceIndex < LogicalFaces.Length; faceIndex++)
            {
                FBXMeshLogicalFace face = LogicalFaces[faceIndex];
                if (!face.IsQuad) continue;
                int hits = 0;
                for (int i = 0; i < 4; i++)
                {
                    var edge = new FBXMeshEdge(face.Vertices[i], face.Vertices[(i + 1) % 4]);
                    if (_edgeIndices.TryGetValue(edge, out int edgeIndex) && edgeSet.Contains(edgeIndex)) hits++;
                }
                if (hits == 2) result.Add(faceIndex);
            }
            return result;
        }

        public bool TryBuildOrientedLoopCut(int startEdgeIndex,
            out List<FBXMeshLoopCutFace> cutFaces, out int referenceA, out int referenceB)
        {
            cutFaces = new List<FBXMeshLoopCutFace>();
            referenceA = -1;
            referenceB = -1;
            if (startEdgeIndex < 0 || startEdgeIndex >= Edges.Length) return false;

            List<int> loop = GetEdgeLoop(startEdgeIndex);
            List<int> faces = GetLoopQuadFaces(loop);
            if (faces.Count == 0) return false;
            var loopSet = new HashSet<FBXMeshEdge>();
            foreach (int edgeIndex in loop) loopSet.Add(Edges[edgeIndex]);

            FBXMeshEdge startEdge = Edges[startEdgeIndex];
            referenceA = startEdge.A;
            referenceB = startEdge.B;
            var oriented = new Dictionary<FBXMeshEdge, Vector2Int>
            {
                [startEdge] = new Vector2Int(referenceA, referenceB)
            };

            bool changed;
            do
            {
                changed = false;
                foreach (int faceIndex in faces)
                {
                    FBXMeshLogicalFace face = LogicalFaces[faceIndex];
                    int first = -1;
                    int second = -1;
                    for (int i = 0; i < 4; i++)
                    {
                        var edge = new FBXMeshEdge(face.Vertices[i], face.Vertices[(i + 1) % 4]);
                        if (!loopSet.Contains(edge)) continue;
                        if (first < 0) first = i;
                        else { second = i; break; }
                    }
                    if (first < 0 || second < 0) continue;
                    var firstEdge = new FBXMeshEdge(
                        face.Vertices[first], face.Vertices[(first + 1) % 4]);
                    var secondEdge = new FBXMeshEdge(
                        face.Vertices[second], face.Vertices[(second + 1) % 4]);
                    bool firstKnown = oriented.TryGetValue(firstEdge, out Vector2Int firstDirection);
                    bool secondKnown = oriented.TryGetValue(secondEdge, out Vector2Int secondDirection);
                    if (firstKnown == secondKnown) continue;

                    int knownPosition = firstKnown ? first : second;
                    int unknownPosition = firstKnown ? second : first;
                    Vector2Int knownDirection = firstKnown ? firstDirection : secondDirection;
                    int knownA = face.Vertices[knownPosition];
                    int knownB = face.Vertices[(knownPosition + 1) % 4];
                    bool followsBoundary = knownDirection.x == knownA && knownDirection.y == knownB;
                    int unknownA = face.Vertices[unknownPosition];
                    int unknownB = face.Vertices[(unknownPosition + 1) % 4];
                    var unknownEdge = new FBXMeshEdge(unknownA, unknownB);
                    oriented[unknownEdge] = followsBoundary
                        ? new Vector2Int(unknownB, unknownA)
                        : new Vector2Int(unknownA, unknownB);
                    changed = true;
                }
            } while (changed);

            foreach (int faceIndex in faces)
            {
                FBXMeshLogicalFace face = LogicalFaces[faceIndex];
                int first = -1;
                for (int i = 0; i < 4; i++)
                {
                    var edge = new FBXMeshEdge(face.Vertices[i], face.Vertices[(i + 1) % 4]);
                    if (loopSet.Contains(edge)) { first = i; break; }
                }
                if (first < 0) return false;
                var firstEdge = new FBXMeshEdge(
                    face.Vertices[first], face.Vertices[(first + 1) % 4]);
                if (!oriented.TryGetValue(firstEdge, out Vector2Int direction)) return false;
                bool flip = direction.x != face.Vertices[first]
                    || direction.y != face.Vertices[(first + 1) % 4];
                cutFaces.Add(new FBXMeshLoopCutFace(faceIndex, first, flip));
            }

            // 四边面带的末端允许进入一个三角面：从被切边的新点收束到三角面的对角顶点。
            // 只接受恰好命中一条 Loop 边的三角面，避免在极点处产生不确定分支。
            for (int faceIndex = 0; faceIndex < LogicalFaces.Length; faceIndex++)
            {
                FBXMeshLogicalFace face = LogicalFaces[faceIndex];
                if (face.IsQuad || face.Vertices.Length != 3) continue;
                int hitPosition = -1;
                int hits = 0;
                for (int i = 0; i < 3; i++)
                {
                    var edge = new FBXMeshEdge(face.Vertices[i], face.Vertices[(i + 1) % 3]);
                    if (!loopSet.Contains(edge)) continue;
                    hitPosition = i;
                    hits++;
                }
                if (hits != 1) continue;
                var terminalEdge = new FBXMeshEdge(face.Vertices[hitPosition],
                    face.Vertices[(hitPosition + 1) % 3]);
                if (!oriented.TryGetValue(terminalEdge, out Vector2Int direction)) continue;
                bool flip = direction.x != face.Vertices[hitPosition]
                    || direction.y != face.Vertices[(hitPosition + 1) % 3];
                cutFaces.Add(new FBXMeshLoopCutFace(faceIndex, hitPosition, flip));
            }
            return cutFaces.Count > 0;
        }

        public IReadOnlyList<int> GetAdjacentLogicalFaces(int edgeIndex)
        {
            return _edgeToLogicalFaces.TryGetValue(edgeIndex, out List<int> faces)
                ? faces
                : Array.Empty<int>();
        }

        public bool TryGetOuterVertices(int logicalFaceIndex, FBXMeshEdge edge,
            out int outerAtA, out int outerAtB)
        {
            outerAtA = -1;
            outerAtB = -1;
            if (logicalFaceIndex < 0 || logicalFaceIndex >= LogicalFaces.Length)
                return false;
            FBXMeshLogicalFace face = LogicalFaces[logicalFaceIndex];
            if (!face.IsQuad) return false;
            int[] vertices = face.Vertices;
            int aPosition = Array.IndexOf(vertices, edge.A);
            int bPosition = Array.IndexOf(vertices, edge.B);
            if (aPosition < 0 || bPosition < 0) return false;
            if ((aPosition + 1) % 4 == bPosition)
            {
                outerAtA = vertices[(aPosition + 3) % 4];
                outerAtB = vertices[(bPosition + 1) % 4];
                return true;
            }
            if ((bPosition + 1) % 4 == aPosition)
            {
                outerAtA = vertices[(aPosition + 1) % 4];
                outerAtB = vertices[(bPosition + 3) % 4];
                return true;
            }
            return false;
        }

        private void WalkLoopDirection(int startEdgeIndex, int startFaceIndex, List<int> result)
        {
            int currentEdge = startEdgeIndex;
            int currentFace = startFaceIndex;
            var visitedFaces = new HashSet<int>();

            while (currentFace >= 0 && visitedFaces.Add(currentFace))
            {
                FBXMeshLogicalFace face = LogicalFaces[currentFace];
                if (!face.IsQuad) break;

                int edgePosition = FindFaceEdgePosition(face, Edges[currentEdge]);
                if (edgePosition < 0) break;
                int oppositePosition = (edgePosition + 2) % 4;
                var opposite = new FBXMeshEdge(
                    face.Vertices[oppositePosition], face.Vertices[(oppositePosition + 1) % 4]);
                if (!_edgeIndices.TryGetValue(opposite, out int oppositeEdge)) break;
                if (!result.Contains(oppositeEdge)) result.Add(oppositeEdge);
                if (oppositeEdge == startEdgeIndex) break;

                int nextFace = -1;
                if (_edgeToLogicalFaces.TryGetValue(oppositeEdge, out List<int> faces))
                {
                    foreach (int candidate in faces)
                        if (candidate != currentFace) { nextFace = candidate; break; }
                }
                currentEdge = oppositeEdge;
                currentFace = nextFace;
            }
        }

        private static int FindFaceEdgePosition(FBXMeshLogicalFace face, FBXMeshEdge edge)
        {
            for (int i = 0; i < face.Vertices.Length; i++)
            {
                var candidate = new FBXMeshEdge(
                    face.Vertices[i], face.Vertices[(i + 1) % face.Vertices.Length]);
                if (candidate.Equals(edge)) return i;
            }
            return -1;
        }

        private static void AddFaceEdge(int a, int b, int face,
            Dictionary<FBXMeshEdge, EdgeFaces> edgeFaces)
        {
            var edge = new FBXMeshEdge(a, b);
            if (!edgeFaces.TryGetValue(edge, out EdgeFaces data))
            {
                data = new EdgeFaces();
                edgeFaces.Add(edge, data);
            }
            data.faces.Add(face);
        }

        private List<QuadCandidate> InferQuads(Vector3[] vertices,
            Dictionary<FBXMeshEdge, EdgeFaces> edgeFaces)
        {
            var candidates = new List<QuadCandidate>();
            foreach (KeyValuePair<FBXMeshEdge, EdgeFaces> pair in edgeFaces)
            {
                if (pair.Value.faces.Count != 2)
                    continue;

                int faceA = pair.Value.faces[0];
                int faceB = pair.Value.faces[1];
                int oppositeA = GetOppositeVertex(faceA, pair.Key);
                int oppositeB = GetOppositeVertex(faceB, pair.Key);
                if (oppositeA < 0 || oppositeB < 0 || oppositeA == oppositeB)
                    continue;

                Vector3 normalA = GetFaceNormal(vertices, faceA);
                Vector3 normalB = GetFaceNormal(vertices, faceB);
                float coplanar = Vector3.Dot(normalA, normalB);
                if (coplanar < 0.995f)
                    continue;

                float diagonalLength = Vector3.Distance(vertices[pair.Key.A], vertices[pair.Key.B]);
                float perimeterAverage = (
                    Vector3.Distance(vertices[oppositeA], vertices[pair.Key.A])
                    + Vector3.Distance(vertices[pair.Key.A], vertices[oppositeB])
                    + Vector3.Distance(vertices[oppositeB], vertices[pair.Key.B])
                    + Vector3.Distance(vertices[pair.Key.B], vertices[oppositeA])) * 0.25f;
                if (perimeterAverage <= Mathf.Epsilon)
                    continue;

                float ratio = diagonalLength / perimeterAverage;
                if (ratio < 1.08f)
                    continue;
                candidates.Add(new QuadCandidate(pair.Key, faceA, faceB, ratio * coplanar));
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            var usedFaces = new HashSet<int>();
            var selected = new List<QuadCandidate>();
            foreach (QuadCandidate candidate in candidates)
            {
                if (usedFaces.Contains(candidate.faceA) || usedFaces.Contains(candidate.faceB))
                    continue;
                usedFaces.Add(candidate.faceA);
                usedFaces.Add(candidate.faceB);
                selected.Add(candidate);
            }
            return selected;
        }

        private int GetOppositeVertex(int face, FBXMeshEdge edge)
        {
            int start = face * 3;
            for (int i = 0; i < 3; i++)
            {
                int vertex = Triangles[start + i];
                if (vertex != edge.A && vertex != edge.B)
                    return vertex;
            }
            return -1;
        }

        private Vector3 GetFaceNormal(Vector3[] vertices, int face)
        {
            int start = face * 3;
            Vector3 a = vertices[Triangles[start]];
            Vector3 b = vertices[Triangles[start + 1]];
            Vector3 c = vertices[Triangles[start + 2]];
            return Vector3.Cross(b - a, c - a).normalized;
        }
    }
}
