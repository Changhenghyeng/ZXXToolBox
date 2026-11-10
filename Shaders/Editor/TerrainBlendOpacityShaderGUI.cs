using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace X26Art.Editor
{
    public sealed class TerrainBlendOpacityShaderGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            foreach (Object target in editor.targets)
                ValidateMaterial((Material)target);

            editor.PropertiesDefaultGUI(properties);

            foreach (Object target in editor.targets)
                ValidateMaterial((Material)target);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            base.AssignNewShaderToMaterial(material, oldShader, newShader);
            ValidateMaterial(material);
        }

        public override void ValidateMaterial(Material material)
        {
            // Unity treats queues through GeometryLast (2500) as opaque.
            // Preserve the user's queue; opacity only affects transparent queues.
            bool opaque = material.renderQueue <= (int)RenderQueue.GeometryLast;
            string renderType = opaque ? "Opaque" : "Transparent";
            float srcBlend = opaque ? (float)BlendMode.One : (float)BlendMode.SrcAlpha;
            float dstBlend = opaque ? (float)BlendMode.Zero : (float)BlendMode.OneMinusSrcAlpha;
            float zWrite = opaque ? 1.0f : 0.0f;

            if (material.GetTag("RenderType", false) == renderType
                && material.GetFloat("_SrcBlend") == srcBlend
                && material.GetFloat("_DstBlend") == dstBlend
                && material.GetFloat("_ZWrite") == zWrite)
                return;

            material.SetOverrideTag("RenderType", renderType);
            material.SetFloat("_SrcBlend", srcBlend);
            material.SetFloat("_DstBlend", dstBlend);
            material.SetFloat("_ZWrite", zWrite);
            EditorUtility.SetDirty(material);
        }
    }
}
