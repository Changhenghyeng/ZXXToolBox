using System.Linq;
using UnityEngine;

namespace RockTools
{
    public class Cell
    {
        public Cell()
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
        }

        public Vector3 position;

        public Quaternion rotation;

        public Vector3 scale;

        private Vector3[] vertices;

        private int[] triangles;

        private Color[] colors;

        public void Rotate(Vector3 axis, float angle, Space space = Space.Self)
        {
            if (space == Space.Self)
            {
                rotation *= Quaternion.AngleAxis(angle, axis);
            }
            else
            {
                rotation = Quaternion.AngleAxis(angle, axis) * rotation;
            }
        }

        public void WriteMesh(ref MeshBuffer meshBuffer)
        {
            if (scale.sqrMagnitude == 0)
            {
                return;
            }

            var outVertices = vertices.ToArray();

            for (var i = 0; i < outVertices.Length; i++)
            {
                outVertices[i] = Vector3.Scale(outVertices[i], scale);
                outVertices[i] = rotation * outVertices[i];
                outVertices[i] = position + outVertices[i];
            }

            meshBuffer.Append(outVertices, triangles, colors);
        }

        public void SetMesh(Mesh inMesh, bool normalize = false)
        {
            vertices = inMesh.vertices.ToArray();
            triangles = inMesh.triangles.ToArray();
            colors = inMesh.colors.ToArray();

            if (normalize && vertices.Length > 0)
            {
                var bounds = inMesh.bounds;
                var maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
                if (maxSize > Mathf.Epsilon)
                {
                    var pivot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                    var inverseSize = 1f / maxSize;
                    for (var i = 0; i < vertices.Length; i++)
                        vertices[i] = (vertices[i] - pivot) * inverseSize;
                }
            }

            if (colors.Length != vertices.Length)
            {
                colors = new Color[vertices.Length];
                var minY = vertices.Min(vertex => vertex.y);
                var maxY = vertices.Max(vertex => vertex.y);
                for (var i = 0; i < vertices.Length; i++)
                {
                    var height = Mathf.InverseLerp(minY, maxY, vertices[i].y);
                    colors[i] = Color.Lerp(Color.black, Color.white, height);
                }
            }
        }
    }
}
