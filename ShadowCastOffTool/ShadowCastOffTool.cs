using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

public static class ShadowCastOffTool
{
    [MenuItem("GameObject/Cast Shadows OFF", false, 31)]
    private static void ExecuteFromHierarchy()
    {
        Execute(Selection.gameObjects);
    }

    [MenuItem("GameObject/Cast Shadows OFF", true)]
    private static bool ValidateExecuteFromHierarchy()
    {
        return Selection.gameObjects != null && Selection.gameObjects.Length > 0;
    }

    public static void Execute(GameObject[] roots)
    {
        int changed = 0;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Set Cast Shadows Off");

        foreach (GameObject root in roots)
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.shadowCastingMode != ShadowCastingMode.Off)
                {
                    Undo.RecordObject(renderer, "Disable Cast Shadows");
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                    EditorUtility.SetDirty(renderer);
                    changed++;
                }
            }
        }

        if (changed > 0)
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        Debug.Log($"[Set Cast Shadows Off] Renderers updated: {changed}");
    }
}
