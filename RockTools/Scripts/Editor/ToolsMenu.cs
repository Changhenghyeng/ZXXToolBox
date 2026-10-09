using UnityEditor;
using UnityEngine;

namespace RockTools
{
    public static class ToolsMenu
    {
        public static void NewRockGenerator()
        {
            var rockGenerator = RockGenerator.GetInstance();
            Selection.activeObject = rockGenerator.gameObject;
        }

        public static void ExportObj()
        {
            MeshExporter.ExportSelection(true);
        }

        public static bool ExportObjValidate()
        {
            return SelectionHasMeshFilterInChildren();
        }

        private static bool SelectionHasMeshFilterInChildren()
        {
            return Selection.activeObject != null && Selection.activeObject is GameObject gameObject &&
                   gameObject.GetComponentInChildren<MeshFilter>() != null;
        }
    }
}
