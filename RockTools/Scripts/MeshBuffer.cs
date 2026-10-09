using System;
using UnityEngine;

namespace RockTools
{
    /// <summary>
    /// This is a more cache friendly object to hold a mesh's data. with each operation we modify this and later we convert this ti .
    /// </summary>
    public class MeshBuffer
    {
        public readonly Vector3[] vertices;
        public readonly int[] triangles;
        public readonly Color[] colors;
        public readonly Vector2[] uv;

        public int vertexCount;
        public int triangleCount;
        public int colorCount;
        public int uvCount;

        public MeshBuffer(int capacity)
        {
            vertices = new Vector3[capacity];
            triangles = new int[capacity];
            colors = new Color[capacity];
            uv = new Vector2[capacity];
        }

        public void Clear()
        {
            vertexCount = 0;
            triangleCount = 0;
            colorCount = 0;
            uvCount = 0;
        }

        public void Append(Mesh mesh)
        {
            var mVertices = mesh.vertices;
            var mTriangles = mesh.triangles;
            var mColors = mesh.colors;
            var mUv = mesh.uv;
            Append(mVertices, mTriangles, mColors, mUv, mVertices.Length, mTriangles.Length, mColors.Length, mUv.Length);
        }

        public void Append(MeshBuffer other)
        {
            Append(other.vertices, other.triangles, other.colors, other.uv, other.vertexCount, other.triangleCount, other.colorCount, other.uvCount);
        }

        public void Append(Vector3[] vs, int[] ts, Color[] cs)
        {
            Append(vs, ts, cs, Array.Empty<Vector2>(), vs.Length, ts.Length, cs.Length, 0);
        }

        private void Append(Vector3[] vs, int[] ts, Color[] cs, Vector2[] uvs, int vCount, int tCount, int cCount, int uCount)
        {
            var cachedVertexCount = vertexCount;

            // append vertices
            vs.BlockCopy(0, vertices, vertexCount, vCount);
            vertexCount += vCount;

            // append triangles
            var tmp = new int[tCount];
            ts.BlockCopy(0, tmp, 0, tCount);

            for (var i = 0; i < tCount; i++)
                tmp[i] += cachedVertexCount;

            tmp.BlockCopy(0, triangles, triangleCount, tCount);
            triangleCount += tCount;

            // append colors
            cs.BlockCopy(0, colors, colorCount, cCount);
            colorCount += cCount;

            // append colors
            uvs.BlockCopy(0, uv, uvCount, uCount);
            uvCount += uCount;
        }

        public void AddVertex(Vector3 vector3)
        {
            vertices[vertexCount] = vector3;
            vertexCount++;
        }

        public void AddTriangle(int i0, int i1, int i2)
        {
            triangles[triangleCount] = i0;
            triangles[triangleCount + 1] = i1;
            triangles[triangleCount + 2] = i2;
            triangleCount += 3;
        }

        public void AddColor(Color color)
        {
            colors[colorCount] = color;
            colorCount++;
        }

        public void ReplaceVertex(int source, int destination)
        {
            vertices[destination] = vertices[source];
        }

        public void OverrideTriangles(int[] ts, int tCount)
        {
            ts.BlockCopy(0, triangles, 0, tCount);
            triangleCount = tCount;
        }

        public void Override(Vector3[] vs, int[] ts, Color[] cs, Vector2[] uvs)
        {
            Clear();
            Append(vs, ts, cs, uvs, vs.Length, ts.Length, cs.Length, uvs.Length);
        }

        public Mesh GetMesh()
        {
            var mVertices = new Vector3[vertexCount];
            vertices.BlockCopy(0, mVertices, 0, vertexCount);

            var mTriangles = new int[triangleCount];
            triangles.BlockCopy(0, mTriangles, 0, triangleCount);

            var mColors = new Color[colorCount];
            colors.BlockCopy(0, mColors, 0, colorCount);

            var mUv = new Vector2[uvCount];
            uv.BlockCopy(0, mUv, 0, uvCount);

            return new Mesh {vertices = mVertices, triangles = mTriangles, colors = mColors, uv = mUv};
        }
    }
}