using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

public class PrefabBatchReplacer : EditorWindow
{
    private GameObject target;          // 替换的目标(预制体 / 模型 / 场景物体)
    private bool keepName      = false; // 保留原物体名字
    private bool keepRotation  = true;  // 保留原 Rotation
    private bool keepScale     = true;  // 保留原 Scale
    private bool keepLayerTag  = true;  // 保留 Layer 和 Tag
    private bool keepMaterials = false; // 保留原材质(只处理根节点的 Renderer)
    private bool unpackPrefab  = false; // 替换后解包成普通物体(target 为预制体时生效)

    private Vector2 scroll;

    public static void ShowWindow()
    {
        var w = GetWindow<PrefabBatchReplacer>("批量替换");
        w.minSize = new Vector2(420, 480);
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        // ---------- 标题 ----------
        EditorGUILayout.LabelField("Prefab Batch Replacer  /  场景预制体批量替换", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // ---------- 使用说明 ----------
        EditorGUILayout.HelpBox(
            "工作流:\n" +
            "  1)在 Hierarchy 里选中所有要被替换掉的物体(支持多选、搜索框筛选后 Ctrl+A 全选)\n" +
            "  2)把新的预制体 / 模型 / 场景物体 拖到下面的【替换目标】栏\n" +
            "  3)点【执行替换】\n\n" +
            "替换逻辑:在原物体位置生成新物体,继承 父子关系 / 位置,\n" +
            "Rotation、Scale、Name、Layer/Tag、Materials 可选保留,原物体删除。\n\n" +
            "重要:执行前请用版本控制(Git/SVN)提交一次当前状态,便于回滚!\n" +
            "如果手滑了也可以直接 Ctrl+Z 撤销本次替换。",
            MessageType.Info);

        EditorGUILayout.Space(6);

        // ---------- ① 替换目标 ----------
        DrawSectionHeader("① 替换目标");
        using (new EditorGUILayout.HorizontalScope("box"))
        {
            EditorGUILayout.LabelField("新物体", GUILayout.Width(60));
            target = (GameObject)EditorGUILayout.ObjectField(target, typeof(GameObject), true);
        }
        if (target != null)
        {
            string typeDesc = GetTargetTypeDesc(target);
            EditorGUILayout.LabelField("类型识别:" + typeDesc, EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("（可以是 Project 里的预制体、FBX 模型,也可以是场景里的 GameObject）",
                EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(6);

        // ---------- ② 设置 ----------
        DrawSectionHeader("② 设置");
        using (new EditorGUILayout.VerticalScope("box"))
        {
            keepName      = EditorGUILayout.Toggle("保留原物体名字",   keepName);
            keepRotation  = EditorGUILayout.Toggle("保留原 Rotation",  keepRotation);
            keepScale     = EditorGUILayout.Toggle("保留原 Scale",     keepScale);
            keepLayerTag  = EditorGUILayout.Toggle("保留 Layer 和 Tag", keepLayerTag);
            keepMaterials = EditorGUILayout.Toggle(new GUIContent("保留原材质",
                "把原物体根节点 Renderer 上的 sharedMaterials 复制到新物体根节点的 Renderer。\n" +
                "材质槽数量不一致时按较小的数量复制。不处理子物体上的 Renderer。"), keepMaterials);

            using (new EditorGUI.DisabledScope(target == null || !IsPrefabAsset(target)))
            {
                unpackPrefab = EditorGUILayout.Toggle("替换后解包预制体(变普通物体)", unpackPrefab);
            }
        }

        EditorGUILayout.Space(6);

        // ---------- ③ 当前选中 ----------
        DrawSectionHeader("③ 当前选中");
        int selCount = Selection.gameObjects.Length;
        int sceneCount = CountSceneObjects(Selection.gameObjects);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField($"Hierarchy 选中物体数:{selCount}");
            if (selCount != sceneCount)
            {
                EditorGUILayout.LabelField(
                    $"其中场景物体:{sceneCount}(非场景物体会被自动跳过)",
                    EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.Space(6);

        // ---------- ④ 操作 ----------
        DrawSectionHeader("④ 操作");
        using (new EditorGUI.DisabledScope(target == null || sceneCount == 0))
        {
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);
            if (GUILayout.Button($"执行替换  ({sceneCount} 个物体  →  {(target ? target.name : "—")})",
                                 GUILayout.Height(34)))
            {
                if (EditorUtility.DisplayDialog("确认替换",
                    $"将把选中的 {sceneCount} 个物体替换为 “{target.name}”,确定继续吗?\n\n" +
                    $"(可通过 Ctrl+Z 撤销)",
                    "执行", "取消"))
                {
                    Replace(Selection.gameObjects, target);
                }
            }
            GUI.backgroundColor = Color.white;
        }

        if (target == null)
            EditorGUILayout.HelpBox("请先指定【替换目标】", MessageType.Warning);
        else if (sceneCount == 0)
            EditorGUILayout.HelpBox("请在 Hierarchy 里选中要被替换的场景物体", MessageType.Warning);

        EditorGUILayout.EndScrollView();

        // ── 署名 ──
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    private void OnSelectionChange() { Repaint(); }

    // ---------- Helpers ----------

    private static void DrawSectionHeader(string title)
    {
        var style = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        EditorGUILayout.LabelField(title, style);
    }

    private static bool IsPrefabAsset(GameObject go)
    {
        if (go == null) return false;
        return PrefabUtility.IsPartOfPrefabAsset(go) ||
               !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go));
    }

    private static string GetTargetTypeDesc(GameObject go)
    {
        string path = AssetDatabase.GetAssetPath(go);
        if (string.IsNullOrEmpty(path)) return "场景中的 GameObject(将会被 Clone)";

        if (path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            return $"预制体资源(生成预制体实例)  -  {path}";
        if (path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".obj", System.StringComparison.OrdinalIgnoreCase))
            return $"模型资源(生成模型实例)  -  {path}";
        return $"资源:{path}";
    }

    private static int CountSceneObjects(GameObject[] arr)
    {
        int n = 0;
        foreach (var g in arr)
            if (g != null && g.scene.IsValid() && !string.IsNullOrEmpty(g.scene.name))
                n++;
        return n;
    }

    /// <summary>取根节点上的 Renderer(优先 MeshRenderer,其次 SkinnedMeshRenderer)。</summary>
    private static Renderer GetRootRenderer(GameObject go)
    {
        if (go == null) return null;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null) return mr;
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) return smr;
        return null;
    }

    // ---------- Core ----------

    private void Replace(GameObject[] selected, GameObject target)
    {
        bool targetIsPrefabAsset = IsPrefabAsset(target);

        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Prefab Batch Replace");

        var newSelection = new List<GameObject>();
        int ok = 0, skip = 0;

        try
        {
            for (int i = 0; i < selected.Length; i++)
            {
                var old = selected[i];
                if (old == null) { skip++; continue; }

                // 跳过非场景物体
                if (!old.scene.IsValid() || string.IsNullOrEmpty(old.scene.name)) { skip++; continue; }
                // 不要把目标本身替换掉
                if (old == target) { skip++; continue; }

                EditorUtility.DisplayProgressBar("批量替换中",
                    $"{i + 1} / {selected.Length}: {old.name}",
                    (float)(i + 1) / selected.Length);

                var t = old.transform;
                Transform  parent    = t.parent;
                int        sibling   = t.GetSiblingIndex();
                Vector3    pos       = t.position;
                Quaternion rot       = t.rotation;
                Vector3    scale     = t.localScale;
                string     oldName   = old.name;
                int        oldLayer  = old.layer;
                string     oldTag    = old.tag;
                bool       wasActive = old.activeSelf;

                // 备份原材质(在销毁原物体之前)
                Material[] oldMats = null;
                if (keepMaterials)
                {
                    var oldRenderer = GetRootRenderer(old);
                    if (oldRenderer != null) oldMats = oldRenderer.sharedMaterials;
                }

                GameObject newGO;
                if (targetIsPrefabAsset)
                {
                    newGO = (GameObject)PrefabUtility.InstantiatePrefab(target, old.scene);
                    if (newGO != null && unpackPrefab)
                    {
                        PrefabUtility.UnpackPrefabInstance(newGO,
                            PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                    }
                }
                else
                {
                    newGO = Instantiate(target);
                    newGO.name = target.name; // 去掉 "(Clone)"
                    if (old.scene.IsValid())
                        EditorSceneManager.MoveGameObjectToScene(newGO, old.scene);
                }
                if (newGO == null) { skip++; continue; }

                Undo.RegisterCreatedObjectUndo(newGO, "Create Replacement");

                var nt = newGO.transform;
                nt.SetParent(parent, true);
                nt.SetSiblingIndex(sibling);
                nt.position = pos;
                if (keepRotation) nt.rotation = rot;
                if (keepScale)    nt.localScale = scale;

                if (keepName)     newGO.name  = oldName;
                if (keepLayerTag)
                {
                    newGO.layer = oldLayer;
                    try { newGO.tag = oldTag; } catch { /* tag 可能不存在 */ }
                }
                newGO.SetActive(wasActive);

                // 应用原材质到新物体根节点 Renderer
                if (keepMaterials && oldMats != null && oldMats.Length > 0)
                {
                    var newRenderer = GetRootRenderer(newGO);
                    if (newRenderer != null)
                    {
                        var dstMats = newRenderer.sharedMaterials;
                        int n = Mathf.Min(oldMats.Length, dstMats.Length);
                        if (n > 0)
                        {
                            var merged = new Material[dstMats.Length];
                            for (int k = 0; k < dstMats.Length; k++) merged[k] = dstMats[k];
                            for (int k = 0; k < n; k++)             merged[k] = oldMats[k];
                            Undo.RecordObject(newRenderer, "Keep Materials");
                            newRenderer.sharedMaterials = merged;
                        }
                    }
                }

                newSelection.Add(newGO);
                Undo.DestroyObjectImmediate(old);
                ok++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            Undo.CollapseUndoOperations(group);
        }

        for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetSceneAt(i));

        Selection.objects = newSelection.ToArray();
        Debug.Log($"[Prefab Batch Replacer] 替换完成:成功 {ok} 个,跳过 {skip} 个,目标 = '{target.name}'");
    }
}
