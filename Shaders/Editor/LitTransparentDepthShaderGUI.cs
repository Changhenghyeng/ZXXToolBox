using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace X26Art.Editor
{
    public sealed class LitTransparentDepthShaderGUI : ShaderGUI
    {
        private const string QueueProperty = "_AbsoluteRenderQueue";
        private ShaderGUI litGUI;

        private ShaderGUI LitGUI
        {
            get
            {
                if (litGUI == null)
                {
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("UnityEditor.Rendering.Universal.ShaderGUI.LitShader", false))
                        .FirstOrDefault(candidate => candidate != null);
                    if (type == null)
                        throw new InvalidOperationException("URP Lit ShaderGUI could not be found.");
                    litGUI = (ShaderGUI)Activator.CreateInstance(type, true);
                }
                return litGUI;
            }
        }

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            var materials = editor.targets.OfType<Material>().ToArray();
            foreach (var material in materials)
            {
                PreserveExistingQueue(material);
                ConfigureTransparency(material);
            }

            LitGUI.OnGUI(editor, properties.Where(property => property.name != "_QueueOffset"
                && property.name != QueueProperty && property.name != "_Surface"
                && property.name != "_Blend" && property.name != "_BlendModePreserveSpecular").ToArray());

            foreach (var material in materials)
                ValidateMaterial(material);

            if (materials.Length == 0)
                return;

            EditorGUILayout.Space();
            int queue = materials[0].renderQueue;
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = materials.Any(material => material.renderQueue != queue);
            EditorGUI.BeginChangeCheck();
            int value = EditorGUILayout.IntField(new GUIContent("Render Queue", "Absolute render queue (0-5000)."), queue);
            bool changed = EditorGUI.EndChangeCheck();
            EditorGUI.showMixedValue = previousMixed;
            if (!changed)
                return;

            value = Mathf.Clamp(value, 0, 5000);
            Undo.RecordObjects(editor.targets, "Set Render Queue");
            foreach (var material in materials)
            {
                material.SetFloat(QueueProperty, value);
                material.renderQueue = value;
                EditorUtility.SetDirty(material);
            }
        }

        public override void ValidateMaterial(Material material)
        {
            PreserveExistingQueue(material);
            ConfigureTransparency(material);
            LitGUI.ValidateMaterial(material);
            material.SetFloat("_ZWrite", 1.0f);
            if (material.HasProperty(QueueProperty) && material.GetFloat(QueueProperty) >= 0)
                material.renderQueue = Mathf.Clamp(Mathf.RoundToInt(material.GetFloat(QueueProperty)), 0, 5000);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            int previousQueue = material.renderQueue;
            LitGUI.AssignNewShaderToMaterial(material, oldShader, newShader);
            if (oldShader != null && material.HasProperty(QueueProperty))
                material.SetFloat(QueueProperty, previousQueue);
            ValidateMaterial(material);
        }

        private static void ConfigureTransparency(Material material)
        {
            material.SetFloat("_Surface", 1.0f);
            material.SetFloat("_Blend", 0.0f);
            material.SetFloat("_BlendModePreserveSpecular", 0.0f);
            material.SetFloat("_AlphaToMask", 0.0f);
        }

        private static void PreserveExistingQueue(Material material)
        {
            if (!material.HasProperty(QueueProperty) || material.GetFloat(QueueProperty) >= 0)
                return;

            bool transparent = material.HasProperty("_Surface") && material.GetFloat("_Surface") > 0.5f;
            bool alphaClip = material.HasProperty("_AlphaClip") && material.GetFloat("_AlphaClip") > 0.5f;
            int automaticQueue = transparent ? 3000 : alphaClip ? 2450 : 2000;
            if (material.HasProperty("_QueueOffset"))
                automaticQueue += (int)material.GetFloat("_QueueOffset");

            if (material.renderQueue >= 0 && material.renderQueue != automaticQueue)
            {
                material.SetFloat(QueueProperty, material.renderQueue);
                EditorUtility.SetDirty(material);
            }
        }
    }
}