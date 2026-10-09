//#define PRINT_OPTIMIZATION_LOGS

using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;

#endif

namespace RockTools
{
    /// <summary>
    /// This class is responsible for baking the original RockGenerator and generating the final mesh.
    /// </summary>
    public static class RockBaker
    {
        private static readonly MeshBuffer KMergeBuffer;

        /// <summary>
        /// static constructor used to initialize buffers and will be called only once.
        /// </summary>
        static RockBaker()
        {
            KMergeBuffer = new MeshBuffer(150 * 1000);
        }

        public static void Bake(RockGenerator rockGenerator, BakeParameters parameters, MeshFilter meshFilter)
        {
#if UNITY_EDITOR
            EditorUtility.DisplayProgressBar("Processing", "Please wait while meshes are being baked", 0f);
            try
            {
#endif
                // cache current position
                var rockGeneratorTransform = rockGenerator.transform;
                var tmpPos = rockGeneratorTransform.position;

                // move all to Vector3.zero
                meshFilter.transform.position = Vector3.zero;
                rockGeneratorTransform.position = Vector3.zero;

                KMergeBuffer.Clear();
                KMergeBuffer.Append(rockGenerator.pMeshFilter.sharedMesh);

                if (parameters.optimize)
                {
                    if (parameters.mergeVerticesThreshold > 0)
                    {
#if PRINT_OPTIMIZATION_LOGS
                        var vBefore = KMergeBuffer.vertexCount;
                        var tBefore = KMergeBuffer.triangleCount;
#endif
                        MergeCloseVertices(KMergeBuffer, parameters.mergeVerticesThreshold);
                        SeparateTriangles(KMergeBuffer);

#if PRINT_OPTIMIZATION_LOGS
                        var vertsAfter = KMergeBuffer.vertexCount;
                        var trisAfter = KMergeBuffer.triangleCount;

                        var vOpt = vBefore - vertsAfter;
                        var tOpt = tBefore - trisAfter;

                        var vOptP = (int) (vOpt / (float) vBefore * 100);
                        var tOptP = (int) (tOpt / (float) tBefore * 100);

                        Debug.Log($"vertices optimized: {vOpt}({vOptP}%), tris optimized: {tOpt}({tOptP}%)");
#endif
                    }
                }

                var combinedMesh = KMergeBuffer.GetMesh();
                combinedMesh.Optimize();
                combinedMesh.RecalculateNormals();
                if (parameters.generateSecondaryUVSet)
                {
                    GenerateSecondaryUVSet(combinedMesh);
                    combinedMesh.uv = combinedMesh.uv2;
                }

                combinedMesh.name = meshFilter.gameObject.name;
                meshFilter.sharedMesh = combinedMesh;

                // reset rock generator to original position
                rockGeneratorTransform.position = tmpPos;
                meshFilter.transform.position = tmpPos;

                // generate colliders
                if (parameters.addCollider)
                {
                    var meshCollider = meshFilter.gameObject.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = combinedMesh;
                }

#if UNITY_EDITOR
                // save mesh as an asset
                if (!string.IsNullOrEmpty(parameters.path))
                    SaveMesh(meshFilter, parameters.path);

                Selection.activeObject = meshFilter.gameObject;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
#endif
        }

        /*private static void CombineAllMeshes(RockGenerator rockGenerator, BakeParameters parameters)
        {
            var colliders = rockGenerator.pRoot.GetComponentsInChildren<Collider>(false).Where(x => x.gameObject.activeSelf).ToArray();
            var meshFilters = rockGenerator.pRoot.GetComponentsInChildren<MeshFilter>(false).Where(x => x.gameObject.activeSelf).Where(mf => mf.sharedMesh != null).ToArray();

            KMergeBuffer.Clear();

            var meshCount = meshFilters.Length;
            for (var i = 0; i < meshCount; i++)
            {
                KTmpBuffer.Clear();
                KTmpBuffer.Append(meshFilters[i].sharedMesh);

                var localToWorldMatrix = meshFilters[i].transform.localToWorldMatrix;
                for (var j = 0; j < KTmpBuffer.vertexCount; j++)
                {
                    KTmpBuffer.vertices[j] = localToWorldMatrix.MultiplyPoint3x4(KTmpBuffer.vertices[j]);
                }

                if (parameters.crop)
                {
                    MeshSplitter.Split(KTmpBuffer, new Plane(Vector3.up, Vector3.zero));
                }

                if (parameters.optimize && rockGenerator.pType != ERockType.Sharp)
                {
                    var skip = meshFilters[i].gameObject.GetComponentInChildren<Collider>();
                    ClearInsidePolys(KTmpBuffer, colliders, skip);
                }

                KMergeBuffer.Append(KTmpBuffer);
            }
        }*/

        private static void ClearInsidePolys(MeshBuffer meshBuffer, Collider[] colliders, Collider skip)
        {
            var newTriangles = new List<int>(meshBuffer.triangleCount);
            for (var i = 0; i < meshBuffer.triangleCount; i++)
            {
                newTriangles.Add(meshBuffer.triangles[i]);
            }

            var count = meshBuffer.triangleCount / 3;
            for (var j = count - 1; j >= 0; j--)
            {
                var v1 = meshBuffer.vertices[meshBuffer.triangles[j * 3]];
                var v2 = meshBuffer.vertices[meshBuffer.triangles[j * 3 + 1]];
                var v3 = meshBuffer.vertices[meshBuffer.triangles[j * 3 + 2]];

                if (CollisionCheck(colliders, skip, v1) && CollisionCheck(colliders, skip, v2) && CollisionCheck(colliders, skip, v3))
                {
                    newTriangles.RemoveRange(j * 3, 3);
                }
            }

            meshBuffer.OverrideTriangles(newTriangles.ToArray(), newTriangles.Count);
        }

        private static bool CollisionCheck(Collider[] colliders, Collider skip, Vector3 point)
        {
            var count = colliders.Length;
            for (var i = 0; i < count; i++)
            {
                if (colliders[i] == skip)
                {
                    continue;
                }

                if (colliders[i].ClosestPoint(point) == point)
                {
                    return true;
                }
            }

            return false;
        }

        private static void ResizeColliders(List<MeshCollider> colliders, float factor)
        {
            var count = colliders.Count;
            for (var i = 0; i < count; i++)
            {
                var item = colliders[i];
                if (item.gameObject.activeSelf)
                    item.transform.localScale *= factor;
            }
        }

        private static void SaveMesh(MeshFilter meshFilter, string path)
        {
#if UNITY_EDITOR
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                existing.SetVertices(meshFilter.sharedMesh.vertices);
                existing.SetTriangles(meshFilter.sharedMesh.triangles, 0);
                existing.SetColors(meshFilter.sharedMesh.colors);
                existing.SetNormals(meshFilter.sharedMesh.normals);
            }
            else
                AssetDatabase.CreateAsset(meshFilter.sharedMesh, path);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
#endif
        }

        private static void MergeCloseVertices(MeshBuffer meshBuffer, float threshold = 0.001f)
        {
            var sqrThreshold = threshold * threshold;

            var triangles = new List<int>(meshBuffer.triangleCount);
            var vertices = new List<Vector3>(meshBuffer.vertexCount);
            var colors = new List<Color>(meshBuffer.colorCount);

            // dictionary to store triangles hashes and find triangle duplicates
            var hashList = new List<string>(triangles.Count);

            int vIndex;
            var tmp_tris = new int [3];
            for (var i = 0; i < meshBuffer.triangleCount; i += 3)
            {
                for (var j = 0; j < 3; j++)
                {
                    vIndex = meshBuffer.triangles[j + i];
                    var foundIndex = vertices.FindIndex(x => Mathf.Abs((x - meshBuffer.vertices[vIndex]).sqrMagnitude) < sqrThreshold);
                    if (foundIndex == -1)
                    {
                        vertices.Add(meshBuffer.vertices[vIndex]);
                        colors.Add(meshBuffer.colors[vIndex]);
                        tmp_tris[j] = vertices.Count - 1;
                    }
                    else
                        tmp_tris[j] = foundIndex;
                }

                var i1 = tmp_tris[0];
                var i2 = tmp_tris[1];
                var i3 = tmp_tris[2];

                var v1 = vertices[i1];
                var v2 = vertices[i2];
                var v3 = vertices[i3];

                // add if triangle doesn't have two duplicate points
                if (v1 != v2 && v1 != v3 && v2 != v3)
                {
                    var hash = $"{i1}-{i2}-{i3}/{i2}-{i3}-{i1}/{i3}-{i1}-{i2}";
                    if (!hashList.Contains(hash))
                    {
                        triangles.AddRange(tmp_tris);
                        hashList.Add(hash);
                    }
                }
            }

            meshBuffer.Override(vertices.ToArray(), triangles.ToArray(), colors.ToArray(), Array.Empty<Vector2>());
        }

        /// <summary>
        /// Separate each triangle so the final mesh looks Hard-edge
        /// </summary>
        /// <param name="meshBuffer"></param>
        private static void SeparateTriangles(MeshBuffer meshBuffer)
        {
            var triangles = new List<int>(meshBuffer.triangleCount);
            var vertices = new List<Vector3>(meshBuffer.vertexCount);
            var colors = new List<Color>(meshBuffer.colorCount);

            for (var i = 0; i < meshBuffer.triangleCount; i += 3)
            {
                for (var j = 0; j < 3; j++)
                {
                    var vIndex = meshBuffer.triangles[j + i];
                    vertices.Add(meshBuffer.vertices[vIndex]);
                    colors.Add(meshBuffer.colors[vIndex]);
                    triangles.Add(vertices.Count - 1);
                }
            }

            meshBuffer.Override(vertices.ToArray(), triangles.ToArray(), colors.ToArray(), Array.Empty<Vector2>());
        }

        /// <summary>
        /// Will Generate uv2 that maybe used for lighting.
        /// Uses Unity builtin UV unwrap functionality which is available only in the editor  
        /// </summary>
        /// <param name="mesh"></param>
        private static void GenerateSecondaryUVSet(Mesh mesh)
        {
#if UNITY_EDITOR
            Unwrapping.GenerateSecondaryUVSet(mesh);
#endif
        }
    }

    /// <summary>
    /// Parameters we pass to the RockBaker
    /// </summary>
    public struct BakeParameters
    {
        public float mergeVerticesThreshold;
        public bool addCollider;
        public bool optimize;
        public string path;
        public bool generateSecondaryUVSet;
    }
}