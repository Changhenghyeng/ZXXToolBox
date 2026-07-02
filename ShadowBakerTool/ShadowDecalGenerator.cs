using System.IO;
using UnityEngine;
using UnityEditor;

namespace ShadowBakerTool.Editor
{
    public enum DecalShaderType { URP_Unlit_Multiply, ShadowDecal_Gradient_URP }

    public class DecalGenerateSettings
    {
        // Whether to parent the generated decal under the source building.
        public Transform AttachUnder = null;
        // Y position of the decal in world units, relative to the building footprint.
        // Final Y = building pivot Y + YOffset.
        public Transform PivotReference = null;  // usually the building's root transform
        public float YOffset = 0.01f;
        // Output paths.
        public string PrefabFolder = "Assets/BakedShadows";
        public string AssetName = "Building";
        // Decal appearance defaults — written into the material.
        public Color ShadowColor = new Color(0, 0, 0, 1f);
        public DecalShaderType ShaderType;
    }

    public static class ShadowDecalGenerator
    {
        private const string URPUnlitShader = "Universal Render Pipeline/Unlit";
        private const string CustomDecalShader = "Custom/ShadowDecal_Gradient_URP";

        /// <summary>
        /// Build a quad mesh + material + GameObject from a bake result, save it as a prefab.
        /// Returns the instantiated scene GameObject (so the user sees it immediately).
        /// </summary>
        public static GameObject Generate(BakeResult bake, DecalGenerateSettings opts)
        {
            if (bake == null || bake.Texture == null)
            {
                Debug.LogError("[ShadowBaker] Bake result is empty.");
                return null;
            }

            string shaderName = opts.ShaderType == DecalShaderType.ShadowDecal_Gradient_URP
                ? CustomDecalShader : URPUnlitShader;

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError("[ShadowBaker] Decal shader not found: " + shaderName);
                return null;
            }

            // --- 1. Create the quad mesh (XZ plane, facing up). ---
            // Quad size = the padded shadow footprint computed during bake (1:1 with the
            // baked texture's world coverage). No extra scaling — the bake already encodes
            // the exact world dimensions the decal should occupy.
            float sizeX = bake.PaddedSizeXZ.x;
            float sizeZ = bake.PaddedSizeXZ.y;
            var mesh = BuildGroundQuad(sizeX, sizeZ);
            mesh.name = opts.AssetName + "_ShadowMesh";

            // --- 2. Save mesh to disk as an asset ---
            EnsureFolderExists(opts.PrefabFolder);
            string meshPath = AssetDatabase.GenerateUniqueAssetPath($"{opts.PrefabFolder}/{opts.AssetName}_ShadowMesh.asset");
            AssetDatabase.CreateAsset(mesh, meshPath);

            // --- 3. Create material ---
            var mat = new Material(shader)
            {
                name = opts.AssetName + "_ShadowMat"
            };

            if (opts.ShaderType == DecalShaderType.ShadowDecal_Gradient_URP)
            {
                mat.SetTexture("_BaseMap", bake.Texture);
                mat.SetColor("_BaseColor", opts.ShadowColor);

                // The shader's CustomEditor normally applies the transparent/multiply setup when
                // the shader is assigned in the inspector. A code-created material skips that, so
                // do it explicitly here — otherwise the material stays in the opaque queue (2000)
                // and _BaseColor.a cannot drive the shadow opacity (multiply needs _ALPHAMODULATE_ON).
                mat.SetFloat("_Surface", 1f);   // Transparent
                mat.SetFloat("_Blend", 3f);     // Multiply
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.DstColor); // 2
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);     // 0
                mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHAMODULATE_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent; // 3000
            }
            else
            {
                // URP Unlit: Transparent + Multiply blend
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 3f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_BLENDMODE_MULTIPLY");
                mat.renderQueue = 3000;

                mat.SetTexture("_BaseMap", bake.Texture);
                mat.SetColor("_BaseColor", opts.ShadowColor);
            }

            string matPath = AssetDatabase.GenerateUniqueAssetPath($"{opts.PrefabFolder}/{opts.AssetName}_ShadowMat.mat");
            AssetDatabase.CreateAsset(mat, matPath);

            // --- 4. Build the GameObject ---
            var go = new GameObject(opts.AssetName + "_ShadowDecal");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            mf.sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);

            // Shadows are decals — they shouldn't cast or receive other shadows themselves.
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Position in world space
            float worldY = (opts.PivotReference != null ? opts.PivotReference.position.y : bake.FootprintWS.min.y) + opts.YOffset;
            go.transform.position = new Vector3(bake.FootprintCenterWS.x, worldY, bake.FootprintCenterWS.z);
            go.transform.rotation = Quaternion.identity;

            // --- 5. Save as a prefab ---
            string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{opts.PrefabFolder}/{opts.AssetName}_ShadowDecal.prefab");
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, prefabPath, InteractionMode.UserAction);

            // --- 6. Optionally re-parent ---
            if (opts.AttachUnder != null)
            {
                go.transform.SetParent(opts.AttachUnder, worldPositionStays: true);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return go;
        }

        /// <summary>
        /// Build a flat quad on the XZ plane, centered on origin, facing up (+Y).
        /// UVs cover the full quad (0,0) → (1,1) so the baked texture maps 1:1.
        /// </summary>
        private static Mesh BuildGroundQuad(float sizeX, float sizeZ)
        {
            float hx = sizeX * 0.5f;
            float hz = sizeZ * 0.5f;

            var mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-hx, 0, -hz),
                new Vector3( hx, 0, -hz),
                new Vector3( hx, 0,  hz),
                new Vector3(-hx, 0,  hz),
            };

            mesh.uv = new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(1, 0),
                new Vector2(1, 1),
                new Vector2(0, 1),
            };

            mesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
            mesh.normals = new Vector3[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;
            string parent = Path.GetDirectoryName(folderPath)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(parent)) return;
            if (!AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolderExists(parent);
            }
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}