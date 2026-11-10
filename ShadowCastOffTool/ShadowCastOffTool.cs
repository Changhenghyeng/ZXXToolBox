using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;

public static class ShadowCastOffTool
{
    public static void Execute(GameObject[] roots)
    {
        var usageTimer = ZXXToolUsageStats.Begin();
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

        ZXXToolUsageStats.Complete("Cast Shadows OFF", changed, usageTimer);
        Debug.Log($"[Set Cast Shadows Off] Renderers updated: {changed}");
    }
}
