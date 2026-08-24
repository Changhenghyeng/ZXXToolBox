using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ZXXTools.FBXMeshEditor
{
    [Serializable]
    public sealed class FBXMeshSnapshot
    {
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector4[] tangents;
        public Color32[] colors;
        public Vector2[] uv0;
        public Vector2[] uv1;
        public Vector2[] uv2;
        public Vector2[] uv3;
        public int[] indices;
        public int[] subMeshStarts;
        public int[] subMeshCounts;
        public int[] subMeshTopologies;
        public Bounds bounds;
        public int indexFormat;
        public int[] dissolvedEdges;
        public bool selectionOnly;
        public int componentMode;
        public int[] selectedVertices;
        public int[] selectedEdges;
        public int[] selectedFaces;

        public static FBXMeshSnapshot Capture(Mesh mesh, List<int> dissolvedEdges = null)
        {
            var snapshot = new FBXMeshSnapshot
            {
                vertices = mesh.vertices,
                normals = mesh.normals,
                tangents = mesh.tangents,
                colors = mesh.colors32,
                uv0 = mesh.uv,
                uv1 = mesh.uv2,
                uv2 = mesh.uv3,
                uv3 = mesh.uv4,
                bounds = mesh.bounds,
                indexFormat = (int)mesh.indexFormat,
                subMeshStarts = new int[mesh.subMeshCount],
                subMeshCounts = new int[mesh.subMeshCount],
                subMeshTopologies = new int[mesh.subMeshCount],
                dissolvedEdges = dissolvedEdges != null ? dissolvedEdges.ToArray() : Array.Empty<int>()
            };

            var allIndices = new List<int>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                int[] subIndices = mesh.GetIndices(i);
                snapshot.subMeshStarts[i] = allIndices.Count;
                snapshot.subMeshCounts[i] = subIndices.Length;
                snapshot.subMeshTopologies[i] = (int)mesh.GetTopology(i);
                allIndices.AddRange(subIndices);
            }
            snapshot.indices = allIndices.ToArray();
            return snapshot;
        }

        public void ApplyTo(Mesh mesh)
        {
            mesh.Clear();
            mesh.indexFormat = (IndexFormat)indexFormat;
            mesh.vertices = vertices ?? Array.Empty<Vector3>();
            if (normals != null && normals.Length == mesh.vertexCount) mesh.normals = normals;
            if (tangents != null && tangents.Length == mesh.vertexCount) mesh.tangents = tangents;
            if (colors != null && colors.Length == mesh.vertexCount) mesh.colors32 = colors;
            if (uv0 != null && uv0.Length == mesh.vertexCount) mesh.uv = uv0;
            if (uv1 != null && uv1.Length == mesh.vertexCount) mesh.uv2 = uv1;
            if (uv2 != null && uv2.Length == mesh.vertexCount) mesh.uv3 = uv2;
            if (uv3 != null && uv3.Length == mesh.vertexCount) mesh.uv4 = uv3;

            int subMeshCount = subMeshStarts != null ? subMeshStarts.Length : 0;
            mesh.subMeshCount = subMeshCount;
            for (int i = 0; i < subMeshCount; i++)
            {
                var subIndices = new int[subMeshCounts[i]];
                Array.Copy(indices, subMeshStarts[i], subIndices, 0, subIndices.Length);
                mesh.SetIndices(subIndices, (MeshTopology)subMeshTopologies[i], i, false);
            }
            mesh.bounds = bounds;
        }
    }

    /// <summary>持久化记录一个工作 Mesh 的来源及独立编辑历史。</summary>
    public sealed class FBXMeshEditRecord : ScriptableObject
    {
        public Mesh sourceMesh;
        public Mesh workingMesh;
        public List<FBXMeshSnapshot> undoHistory = new List<FBXMeshSnapshot>();
        public List<FBXMeshSnapshot> redoHistory = new List<FBXMeshSnapshot>();
        [Tooltip("每两个整数表示一条已逻辑溶解的边。")]
        public List<int> dissolvedEdges = new List<int>();
        [Tooltip("用户手动退出后，禁止选择对象时自动恢复编辑模式。")]
        public bool manualExitSuppressed;
    }
}
