using UnityEngine;
using UnityEditor;
using System.IO;

public class ConvertVariantToNormalPrefab
{
    public static void ConvertSelectedVariant()
    {
        Object[] selectedObjects = Selection.objects;

        if (selectedObjects.Length == 0)
        {
            EditorUtility.DisplayDialog("提示", "请先在 Project 面板中选中 Prefab Variant 文件", "确定");
            return;
        }

        int successCount = 0;
        int skipCount = 0;

        foreach (Object obj in selectedObjects)
        {
            string assetPath = AssetDatabase.GetAssetPath(obj);

            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab"))
            {
                skipCount++;
                continue;
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null)
            {
                skipCount++;
                continue;
            }

            // 判断是否是 Variant
            PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(prefabAsset);
            if (prefabType != PrefabAssetType.Variant)
            {
                EditorUtility.DisplayDialog("提示", $"{prefabAsset.name} 不是 Prefab Variant，无需转换", "确定");
                skipCount++;
                continue;
            }

            // 实例化到场景中
            GameObject instance = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
            if (instance == null)
            {
                skipCount++;
                continue;
            }

            // 完全解包
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            // 覆盖保存为普通 Prefab
            GameObject newPrefab = PrefabUtility.SaveAsPrefabAsset(instance, assetPath);

            // 删除场景中的临时实例
            Object.DestroyImmediate(instance);

            if (newPrefab != null)
            {
                successCount++;
                Debug.Log($"[转换成功] {assetPath} 已从 Variant 转换为普通 Prefab");
            }
            else
            {
                skipCount++;
                Debug.LogWarning($"[转换失败] {assetPath}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "转换完成",
            $"成功转换：{successCount} 个\n跳过/失败：{skipCount} 个",
            "确定"
        );
    }

}
