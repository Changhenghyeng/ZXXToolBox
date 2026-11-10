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

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        // ---------- 标题 ----------
        EditorGUILayout.LabelField(Content("Prefab Batch Replacer", "场景预制体批量替换工具"), EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        // ---------- 使用说明 ----------
        EditorGUILayout.HelpBox(
            "Workflow:\n" +
            "  1) Select objects to replace in Hierarchy (multi-select, search filter, Ctrl+A all work)\n" +
            "  2) Drag the new prefab / model / scene object into the Target field below\n" +
            "  3) Click Replace\n\n" +
            "The new object is instantiated at each old object's position, preserving parent / sibling order.\n" +
            "Rotation, Scale, Name, Layer/Tag, Materials can optionally be kept from the original.\n\n" +
            "Important: Commit your current state (Git/SVN) before running. Ctrl+Z also undoes the operation.",
            MessageType.Info);

        EditorGUILayout.Space(6);

        // ---------- ① 替换目标 ----------
        DrawSectionHeader("1. Target");
        using (new EditorGUILayout.HorizontalScope("box"))
        {
            EditorGUILayout.LabelField(Content("New Object", "新物体：用于替换的预制体/模型/场景物体"), GUILayout.Width(80));
            target = (GameObject)EditorGUILayout.ObjectField(target, typeof(GameObject), true);
        }
        if (target != null)
        {
            string typeDesc = GetTargetTypeDesc(target);
            EditorGUILayout.LabelField("Type: " + typeDesc, EditorStyles.miniLabel);
        }
        else
        {
            EditorGUILayout.LabelField("(Can be a prefab, FBX model, or scene GameObject)",
                EditorStyles.miniLabel);
        }

        EditorGUILayout.Space(6);

        // ---------- ② 设置 ----------
        DrawSectionHeader("2. Options");
        using (new EditorGUILayout.VerticalScope("box"))
        {
            keepName      = EditorGUILayout.Toggle(Content("Keep Original Name", "保留原物体名称"), keepName);
            keepRotation  = EditorGUILayout.Toggle(Content("Keep Original Rotation", "保留原物体的旋转值"), keepRotation);
            keepScale     = EditorGUILayout.Toggle(Content("Keep Original Scale", "保留原物体的缩放值"), keepScale);
            keepLayerTag  = EditorGUILayout.Toggle(Content("Keep Layer & Tag", "保留原物体的 Layer 和 Tag"), keepLayerTag);
            keepMaterials = EditorGUILayout.Toggle(new GUIContent("Keep Original Materials",
                "把原物体根节点 Renderer 上的 sharedMaterials 复制到新物体根节点的 Renderer。\n" +
                "材质槽数量不一致时按较小的数量复制。不处理子物体上的 Renderer。"), keepMaterials);

            using (new EditorGUI.DisabledScope(target == null || !IsPrefabAsset(target)))
            {
                unpackPrefab = EditorGUILayout.Toggle(Content("Unpack Prefab After Replace", "替换后解包预制体，使其变为普通场景物体"), unpackPrefab);
            }
        }

        EditorGUILayout.Space(6);

        // ---------- ③ 当前选中 ----------
        DrawSectionHeader("3. Current Selection");
        int selCount = Selection.gameObjects.Length;
        int sceneCount = CountSceneObjects(Selection.gameObjects);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField($"Hierarchy Selection: {selCount}");
            if (selCount != sceneCount)
            {
                EditorGUILayout.LabelField(
                    $"Scene objects: {sceneCount} (non-scene objects will be skipped)",
                    EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.Space(6);

        // ---------- ④ 操作 ----------
        DrawSectionHeader("4. Execute");
        using (new EditorGUI.DisabledScope(target == null || sceneCount == 0))
        {
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.55f);
            if (GUILayout.Button(Content($"Replace ({sceneCount} objects → {(target ? target.name : "-")})",
                "执行批量替换操作"), GUILayout.Height(34)))
            {
                if (EditorUtility.DisplayDialog("Confirm Replace",
                    $"Replace {sceneCount} selected objects with \"{target.name}\"?\n\n" +
                    $"(You can undo with Ctrl+Z)",
                    "Replace", "Cancel"))
                {
                    Replace(Selection.gameObjects, target);
                }
            }
            GUI.backgroundColor = Color.white;
        }

        if (target == null)
            EditorGUILayout.HelpBox("Please specify a replacement target.", MessageType.Warning);
        else if (sceneCount == 0)
            EditorGUILayout.HelpBox("Please select scene objects in Hierarchy to replace.", MessageType.Warning);

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
        var usageTimer = ZXXToolUsageStats.Begin();
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
        ZXXToolUsageStats.Complete("Batch Replacer", ok, usageTimer);
        Debug.Log($"[Prefab Batch Replacer] 替换完成:成功 {ok} 个,跳过 {skip} 个,目标 = '{target.name}'");
    }
}
