using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System.Collections.Generic;

public static class PrefabNameSyncTool
{
    public static void Execute(GameObject[] roots)
    {
        var usageTimer = ZXXToolUsageStats.Begin();
        int renamedCount = 0;
        List<string> logs = new List<string>();

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Sync Prefab Names");

        foreach (GameObject root in roots)
        {
            renamedCount += ProcessRecursive(root, logs);
        }

        if (renamedCount > 0)
        {
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        }

        ZXXToolUsageStats.Complete("Sync Names", renamedCount, usageTimer);
        Debug.Log(
            $"[Sync Prefab Names] Complete — Renamed: {renamedCount}\n" +
            string.Join("\n", logs)
        );
    }

    private static int ProcessRecursive(GameObject go, List<string> logs)
    {
        int count = 0;

        if (PrefabUtility.IsPartOfPrefabInstance(go))
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (source != null && go.name != source.name)
            {
                Undo.RecordObject(go, "Rename Prefab Instance");
                logs.Add($"{go.name} -> {source.name}");
                go.name = source.name;
                count++;
            }
        }

        foreach (Transform child in go.transform)
            count += ProcessRecursive(child.gameObject, logs);

        return count;
    }
}
