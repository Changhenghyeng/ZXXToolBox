using System.IO;
using UnityEditor;
using UnityEngine;

namespace RockTools
{
    public class ContextMenu : MonoBehaviour
    {
        [MenuItem("Assets/Prepare Mesh For RockGenerator", false, 10)]
        private static void PrepareMeshForRockGenerator()
        {
            var modifiedAssets = new Object[Selection.objects.Length];
            for (var i = 0; i < Selection.objects.Length; i++)
            {
                var selectedAsset = Selection.objects[i];
                modifiedAssets[i] = PrepareAndSaveMesh(selectedAsset as Mesh);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.objects = modifiedAssets;
        }

        [MenuItem("Assets/Prepare Mesh For RockGenerator", true)]
        private static bool ValidatePrepareMeshForRockGenerator()
        {
            foreach (var selectedAsset in Selection.objects)
            {
                if (selectedAsset == null)
                {
                    return false;
                }

                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(selectedAsset)))
                {
                    return false;
                }

                if (!(selectedAsset is Mesh))
                {
                    return false;
                }
            }

            return true;
        }

        private static Mesh PrepareAndSaveMesh(Mesh selectedMesh)
        {
            var modifiedMesh = new Mesh()
            {
                name = selectedMesh.name,
                vertices = selectedMesh.vertices,
                triangles = selectedMesh.triangles,
                normals = selectedMesh.normals,
                colors = selectedMesh.colors,
                colors32 = selectedMesh.colors32,
                uv = selectedMesh.uv,
                uv2 = selectedMesh.uv2,
                uv3 = selectedMesh.uv3,
                uv4 = selectedMesh.uv4,
                uv5 = selectedMesh.uv5,
                uv6 = selectedMesh.uv6,
                uv7 = selectedMesh.uv7,
                uv8 = selectedMesh.uv8,
                bindposes = selectedMesh.bindposes,
                bounds = selectedMesh.bounds,
                tangents = selectedMesh.tangents,
                boneWeights = selectedMesh.boneWeights,
                hideFlags = selectedMesh.hideFlags,
                indexFormat = selectedMesh.indexFormat,
                subMeshCount = selectedMesh.subMeshCount,
            };

            var verticesLength = selectedMesh.vertices.Length;
            var colors = new Color[verticesLength];
            var minY = selectedMesh.bounds.min.y;
            var maxY = selectedMesh.bounds.max.y;
            for (var i = 0; i < verticesLength; i++)
            {
                var f = Mathf.InverseLerp(minY, maxY, selectedMesh.vertices[i].y);
                colors[i] = Color.Lerp(Color.black, Color.white, f);
            }

            modifiedMesh.colors = colors;
            modifiedMesh.RecalculateBounds();
            modifiedMesh.RecalculateNormals();

            var originalPath = AssetDatabase.GetAssetPath(selectedMesh);
            var newPath = Path.ChangeExtension(originalPath, null);
            newPath = Path.ChangeExtension($"{newPath}-{modifiedMesh.name}", "mesh");
            newPath = AssetDatabase.GenerateUniqueAssetPath(newPath);

            AssetDatabase.CreateAsset(modifiedMesh, newPath);

            return modifiedMesh;
        }
    }
}