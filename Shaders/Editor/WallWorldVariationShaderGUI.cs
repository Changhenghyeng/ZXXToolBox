using UnityEditor;

namespace X26Art.Editor
{
    public sealed class WallWorldVariationShaderGUI : ShaderGUI
    {
        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            materialEditor.PropertiesDefaultGUI(properties);
        }
    }
}