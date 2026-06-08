using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 批量为预制体生成 BoxCollider 的编辑器工具。
/// 工作流：选预制体 → 勾选挂点+参与节点 → 预览 → 确认写入。
/// 回滚机制：A=备份还原（真撤销）, B=预览后写入（写前可撤）, C=清除本次新增 BoxCollider。
/// </summary>
public class BoxColliderGenerator : EditorWindow
{
    // ───────── 数据模型 ─────────

    private class NodeState
    {
        public string path;
        public string name;
        public int depth;
        public bool hasMesh;
        public bool included;
    }

    private class PrefabEntry
    {
        public GameObject asset;
        public List<NodeState> nodes = new List<NodeState>();
        public string mountPath = "";
        public bool foldout = true;
    }

    private enum PlanAction { New, Overwrite, SkipExisting, SkipNoMesh, SkipNoParticipants, ErrorMountMissing }

    private class PlanItem
    {
        public PrefabEntry entry;
        public string assetPath;
        public PlanAction action;
        public string note;
        public Bounds boundsToApply;
    }

    private class LastRunRecord
    {
        public string backupDir;
        public List<(string assetPath, string mountPath)> newColliders
            = new List<(string, string)>();
        public int totalWritten;
    }

    // ───────── 配置 ─────────

    private enum ExistingColliderPolicy { Overwrite, Skip }

    private List<PrefabEntry> prefabs = new List<PrefabEntry>();
    private ExistingColliderPolicy existingPolicy = ExistingColliderPolicy.Overwrite;
    private int targetLayer = 0;
    private bool setMountLayer = true;
    private bool isTrigger = false;
    private string batchNameKeyword = "";

    private List<PlanItem> pendingPlan = null;
    private LastRunRecord lastRun = null;

    private Vector2 leftScroll;
    private Vector2 rightScroll;

    private const string BackupRoot = "Library/BoxColliderGeneratorBackup";

    public static void ShowWindow()
    {
        BoxColliderGenerator w = GetWindow<BoxColliderGenerator>("Box Collider Generator");
        w.minSize = new Vector2(720, 480);
    }

    private void OnEnable()
    {
        LoadSettings();
    }

    private void OnDisable()
    {
        SaveSettings();
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.55f));
        leftScroll = EditorGUILayout.BeginScrollView(leftScroll);
        DrawStep1_PrefabList();
        EditorGUILayout.Space(8);
        DrawStep2_NodeSelection();
        EditorGUILayout.Space(8);
        DrawStep3_Options();
        EditorGUILayout.Space(12);
        DrawPreviewButton();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(4);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        rightScroll = EditorGUILayout.BeginScrollView(rightScroll);
        DrawRightPanel();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

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

    private void DrawStep1_PrefabList()
    {
        EditorGUILayout.LabelField("步骤 1：选预制体", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("从当前选中导入", GUILayout.Height(22)))
        {
            ImportFromSelection();
        }
        using (new EditorGUI.DisabledScope(prefabs.Count == 0))
        {
            if (GUILayout.Button("清空", GUILayout.Width(60), GUILayout.Height(22)))
            {
                prefabs.Clear();
                pendingPlan = null;
            }
        }
        EditorGUILayout.EndHorizontal();

        Rect dropRect = GUILayoutUtility.GetRect(0, 36, GUILayout.ExpandWidth(true));
        GUI.Box(dropRect, "拖入预制体资产到这里");
        HandleDragAndDrop(dropRect);

        if (prefabs.Count == 0)
        {
            EditorGUILayout.HelpBox("当前未选择任何预制体。", MessageType.Info);
            return;
        }

        for (int i = 0; i < prefabs.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"  {prefabs[i].asset.name}", GUILayout.ExpandWidth(true));
            if (GUILayout.Button("×", GUILayout.Width(22)))
            {
                prefabs.RemoveAt(i);
                pendingPlan = null;
                i--;
            }
            EditorGUILayout.EndHorizontal();
        }
    }

    private void DrawStep2_NodeSelection()
    {
        EditorGUILayout.LabelField("步骤 2：为每个预制体勾选", EditorStyles.boldLabel);

        if (prefabs.Count == 0)
        {
            EditorGUILayout.HelpBox("先在上方添加预制体。", MessageType.None);
            return;
        }

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(prefabs.Count < 2))
        {
            if (GUILayout.Button("复制第一个的选择到所有", GUILayout.Height(22)))
            {
                CopyFirstSelectionToAll();
                pendingPlan = null;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        batchNameKeyword = EditorGUILayout.TextField("按名称批量勾选（参与）", batchNameKeyword);
        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(batchNameKeyword)))
        {
            if (GUILayout.Button("应用", GUILayout.Width(60)))
            {
                BatchCheckByName(batchNameKeyword);
                pendingPlan = null;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);

        foreach (PrefabEntry entry in prefabs)
        {
            DrawPrefabTree(entry);
        }
    }

    private void DrawPrefabTree(PrefabEntry entry)
    {
        entry.foldout = EditorGUILayout.Foldout(entry.foldout, entry.asset.name, true);
        if (!entry.foldout) return;

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("挂点", GUILayout.Width(32));
        EditorGUILayout.LabelField("参与", GUILayout.Width(32));
        EditorGUILayout.LabelField("节点");
        EditorGUILayout.EndHorizontal();

        foreach (NodeState node in entry.nodes)
        {
            EditorGUILayout.BeginHorizontal();

            bool isMount = entry.mountPath == node.path;
            bool newMount = GUILayout.Toggle(isMount, GUIContent.none, EditorStyles.radioButton, GUILayout.Width(32));
            if (newMount && !isMount)
            {
                entry.mountPath = node.path;
                pendingPlan = null;
            }

            if (node.hasMesh)
            {
                bool newIncluded = EditorGUILayout.Toggle(node.included, GUILayout.Width(32));
                if (newIncluded != node.included)
                {
                    node.included = newIncluded;
                    pendingPlan = null;
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Toggle(false, GUILayout.Width(32));
                }
            }

            string indent = new string(' ', node.depth * 2);
            string label = $"{indent}{node.name}";
            if (!node.hasMesh) label += "  (空)";
            EditorGUILayout.LabelField(label);

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndVertical();
    }

    private void DrawStep3_Options()
    {
        EditorGUILayout.LabelField("步骤 3：选项", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        existingPolicy = (ExistingColliderPolicy)EditorGUILayout.EnumPopup(
            "已有 BoxCollider 时", existingPolicy);
        targetLayer = EditorGUILayout.LayerField("目标 Layer", targetLayer);
        setMountLayer = EditorGUILayout.Toggle("同时设置挂点 Layer", setMountLayer);
        isTrigger = EditorGUILayout.Toggle("设为 Trigger", isTrigger);
        if (EditorGUI.EndChangeCheck()) pendingPlan = null;
    }

    private void DrawPreviewButton()
    {
        using (new EditorGUI.DisabledScope(prefabs.Count == 0))
        {
            if (GUILayout.Button("生成预览", GUILayout.Height(36)))
            {
                pendingPlan = BuildPlan();
            }
        }
    }

    private void DrawRightPanel()
    {
        EditorGUILayout.LabelField("预览 / 操作", EditorStyles.boldLabel);

        if (pendingPlan != null)
        {
            DrawPlanPanel();
            EditorGUILayout.Space(8);
        }
        else
        {
            EditorGUILayout.HelpBox("点击左侧「生成预览」查看计划，确认后再写入磁盘。", MessageType.Info);
            EditorGUILayout.Space(8);
        }

        DrawHistoryPanel();
    }

    private void DrawPlanPanel()
    {
        EditorGUILayout.LabelField("待写入计划（B）", EditorStyles.boldLabel);

        int newCount = pendingPlan.Count(p => p.action == PlanAction.New);
        int overwriteCount = pendingPlan.Count(p => p.action == PlanAction.Overwrite);
        int skipCount = pendingPlan.Count(p => IsSkip(p.action));
        int errorCount = pendingPlan.Count(p => p.action == PlanAction.ErrorMountMissing);

        EditorGUILayout.LabelField($"新增 {newCount}    覆盖 {overwriteCount}    跳过 {skipCount}    错误 {errorCount}");

        EditorGUILayout.Space(4);

        foreach (PlanItem item in pendingPlan)
        {
            DrawPlanRow(item);
        }

        EditorGUILayout.Space(6);

        EditorGUILayout.BeginHorizontal();
        int willWrite = newCount + overwriteCount;
        using (new EditorGUI.DisabledScope(willWrite == 0))
        {
            GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button($"确认写入 {willWrite} 个预制体", GUILayout.Width(200), GUILayout.Height(30)))
            {
                CommitPlan();
            }
            GUI.backgroundColor = Color.white;
        }
        if (GUILayout.Button("取消", GUILayout.Width(60), GUILayout.Height(30)))
        {
            pendingPlan = null;
        }
        EditorGUILayout.EndHorizontal();
    }

    private static void DrawPlanRow(PlanItem item)
    {
        string icon;
        Color tint;
        switch (item.action)
        {
            case PlanAction.New: icon = "✚"; tint = new Color(0.6f, 1f, 0.6f); break;
            case PlanAction.Overwrite: icon = "↻"; tint = new Color(1f, 0.9f, 0.5f); break;
            case PlanAction.ErrorMountMissing: icon = "✕"; tint = new Color(1f, 0.5f, 0.5f); break;
            default: icon = "—"; tint = new Color(0.7f, 0.7f, 0.7f); break;
        }

        Color prev = GUI.color;
        GUI.color = tint;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(icon, GUILayout.Width(20));
        EditorGUILayout.LabelField(item.entry.asset.name, GUILayout.Width(180));
        EditorGUILayout.LabelField(item.note);
        EditorGUILayout.EndHorizontal();
        GUI.color = prev;
    }

    private void DrawHistoryPanel()
    {
        EditorGUILayout.LabelField("上次操作", EditorStyles.boldLabel);

        if (lastRun == null)
        {
            EditorGUILayout.HelpBox("尚无写入操作。", MessageType.None);
            return;
        }

        EditorGUILayout.LabelField($"已写入：{lastRun.totalWritten} 个预制体");
        EditorGUILayout.LabelField($"本次新增 BoxCollider：{lastRun.newColliders.Count} 个");

        EditorGUILayout.Space(4);

        bool backupExists = !string.IsNullOrEmpty(lastRun.backupDir) && Directory.Exists(lastRun.backupDir);

        using (new EditorGUI.DisabledScope(!backupExists))
        {
            if (GUILayout.Button("还原上次写入（A：从备份还原）", GUILayout.Height(26)))
            {
                if (EditorUtility.DisplayDialog("确认还原",
                    $"将用备份覆盖 {lastRun.totalWritten} 个预制体文件，操作不可逆。继续吗？",
                    "还原", "取消"))
                {
                    RestoreFromBackup();
                }
            }
        }

        using (new EditorGUI.DisabledScope(lastRun.newColliders.Count == 0))
        {
            if (GUILayout.Button("清除上次新增的 BoxCollider（C）", GUILayout.Height(26)))
            {
                if (EditorUtility.DisplayDialog("确认清除",
                    $"将移除 {lastRun.newColliders.Count} 个本次工具新增的 BoxCollider（不影响覆盖更新的）。继续吗？",
                    "清除", "取消"))
                {
                    ClearNewlyAddedColliders();
                }
            }
        }

        EditorGUILayout.HelpBox(
            "还原：完整覆盖文件（含层级、size、center 等所有改动）。\n" +
            "清除：仅移除本次新增的 BoxCollider，被覆盖前的旧数据无法恢复。",
            MessageType.None);
    }

    private static bool IsSkip(PlanAction a)
    {
        return a == PlanAction.SkipExisting || a == PlanAction.SkipNoMesh || a == PlanAction.SkipNoParticipants;
    }

    // ───────── 导入 ─────────

    private void ImportFromSelection()
    {
        foreach (Object obj in Selection.objects)
        {
            GameObject go = ResolveToPrefabAsset(obj as GameObject);
            if (go == null) continue;
            AddPrefab(go);
        }
        pendingPlan = null;
    }

    private void HandleDragAndDrop(Rect area)
    {
        Event evt = Event.current;
        if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;
        if (!area.Contains(evt.mousePosition)) return;

        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
        if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            foreach (Object o in DragAndDrop.objectReferences)
            {
                GameObject go = ResolveToPrefabAsset(o as GameObject);
                if (go == null) continue;
                AddPrefab(go);
            }
            pendingPlan = null;
            evt.Use();
        }
    }

    private void AddPrefab(GameObject prefabAsset)
    {
        if (prefabs.Any(p => p.asset == prefabAsset)) return;

        PrefabEntry entry = new PrefabEntry { asset = prefabAsset };
        BuildNodeList(prefabAsset.transform, "", 0, entry.nodes);
        prefabs.Add(entry);
    }

    private static GameObject ResolveToPrefabAsset(GameObject go)
    {
        if (go == null) return null;

        // Already a project asset (not in any scene)
        if (!go.scene.IsValid()) return go;

        // Scene object: resolve to its source prefab asset
        if (PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab)
            return null;

        string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
        if (string.IsNullOrEmpty(path)) return null;

        return AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    private static void BuildNodeList(Transform t, string pathToThis, int depth, List<NodeState> nodes)
    {
        nodes.Add(new NodeState
        {
            path = pathToThis,
            name = t.name,
            depth = depth,
            hasMesh = HasMesh(t.gameObject),
            included = false,
        });

        foreach (Transform child in t)
        {
            string childPath = string.IsNullOrEmpty(pathToThis) ? child.name : pathToThis + "/" + child.name;
            BuildNodeList(child, childPath, depth + 1, nodes);
        }
    }

    private static bool HasMesh(GameObject go)
    {
        MeshFilter mf = go.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null) return true;
        SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
        return smr != null && smr.sharedMesh != null;
    }

    // ───────── 批量勾选 ─────────

    private void CopyFirstSelectionToAll()
    {
        if (prefabs.Count < 2) return;
        PrefabEntry template = prefabs[0];

        HashSet<string> includedNames = new HashSet<string>(
            template.nodes.Where(n => n.included).Select(n => n.name));
        string mountNodeName = template.nodes.FirstOrDefault(n => n.path == template.mountPath)?.name;

        for (int i = 1; i < prefabs.Count; i++)
        {
            PrefabEntry target = prefabs[i];
            foreach (NodeState node in target.nodes)
            {
                if (node.hasMesh && includedNames.Contains(node.name))
                    node.included = true;
            }
            if (!string.IsNullOrEmpty(mountNodeName))
            {
                NodeState match = target.nodes.FirstOrDefault(n => n.name == mountNodeName);
                if (match != null) target.mountPath = match.path;
            }
        }
    }

    private void BatchCheckByName(string keyword)
    {
        foreach (PrefabEntry entry in prefabs)
        {
            foreach (NodeState node in entry.nodes)
            {
                if (node.hasMesh && node.name.Contains(keyword))
                    node.included = true;
            }
        }
    }

    // ───────── 构建预览（B） ─────────

    private List<PlanItem> BuildPlan()
    {
        List<PlanItem> plan = new List<PlanItem>();

        try
        {
            for (int i = 0; i < prefabs.Count; i++)
            {
                PrefabEntry entry = prefabs[i];
                EditorUtility.DisplayProgressBar("分析中",
                    $"{entry.asset.name} ({i + 1}/{prefabs.Count})",
                    (float)i / prefabs.Count);

                plan.Add(BuildPlanItem(entry));
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        return plan;
    }

    private PlanItem BuildPlanItem(PrefabEntry entry)
    {
        PlanItem item = new PlanItem { entry = entry };
        string assetPath = AssetDatabase.GetAssetPath(entry.asset);
        item.assetPath = assetPath;

        if (string.IsNullOrEmpty(assetPath))
        {
            item.action = PlanAction.ErrorMountMissing;
            item.note = "找不到资产路径";
            return item;
        }

        NodeState mountNode = entry.nodes.FirstOrDefault(n => n.path == entry.mountPath);
        if (mountNode == null)
        {
            item.action = PlanAction.ErrorMountMissing;
            item.note = $"挂点 '{entry.mountPath}' 不存在";
            return item;
        }

        List<NodeState> participants = entry.nodes.Where(n => n.included && n.hasMesh).ToList();
        if (participants.Count == 0)
        {
            item.action = PlanAction.SkipNoParticipants;
            item.note = "未勾选任何参与节点";
            return item;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
        try
        {
            Transform mount = ResolveMount(root.transform, entry.mountPath);
            if (mount == null)
            {
                item.action = PlanAction.ErrorMountMissing;
                item.note = "实际加载后未找到挂点";
                return item;
            }

            List<Transform> partTransforms = new List<Transform>();
            foreach (NodeState n in participants)
            {
                Transform t = string.IsNullOrEmpty(n.path) ? root.transform : root.transform.Find(n.path);
                if (t != null) partTransforms.Add(t);
                else Debug.LogWarning($"[BoxColliderGen] 路径 '{n.path}' 在加载后的 prefab 中未找到");
            }

            if (partTransforms.Count == 0)
            {
                item.action = PlanAction.SkipNoMesh;
                item.note = "参与节点在 prefab 中未找到";
                return item;
            }

            Debug.Log($"[BoxColliderGen] === {entry.asset.name} === 挂点: '{entry.mountPath}', 参与节点数: {partTransforms.Count}");
            foreach (Transform pt in partTransforms)
            {
                Renderer r = pt.GetComponent<Renderer>();
                if (r != null)
                    Debug.Log($"[BoxColliderGen]   参与节点: {pt.name}, Renderer.bounds = center:{r.bounds.center} size:{r.bounds.size}");
                else
                    Debug.LogWarning($"[BoxColliderGen]   参与节点: {pt.name}, 无 Renderer 组件!");
            }

            if (!TryComputeBoundsInLocalSpace(mount, partTransforms, out Bounds bounds))
            {
                item.action = PlanAction.SkipNoMesh;
                item.note = "参与节点无有效网格";
                return item;
            }

            Debug.Log($"[BoxColliderGen]   最终 mount-local bounds: center={bounds.center} size={bounds.size}");
            Debug.Log($"[BoxColliderGen]   mount lossyScale={mount.lossyScale}");

            if (bounds.size == Vector3.zero)
            {
                item.action = PlanAction.SkipNoMesh;
                item.note = "参与节点的包围盒尺寸为零";
                return item;
            }

            item.boundsToApply = bounds;

            bool hasExisting = mount.GetComponent<BoxCollider>() != null;
            if (hasExisting)
            {
                if (existingPolicy == ExistingColliderPolicy.Skip)
                {
                    item.action = PlanAction.SkipExisting;
                    item.note = "已有 BoxCollider，按设置跳过";
                }
                else
                {
                    item.action = PlanAction.Overwrite;
                    item.note = $"覆盖现有 BoxCollider（挂点：{mountNode.name}）";
                }
            }
            else
            {
                item.action = PlanAction.New;
                item.note = $"新增 BoxCollider（挂点：{mountNode.name}）";
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        return item;
    }

    // ───────── 提交（B 确认 + A 备份） ─────────

    private void CommitPlan()
    {
        if (pendingPlan == null) return;

        List<PlanItem> toWrite = pendingPlan
            .Where(p => p.action == PlanAction.New || p.action == PlanAction.Overwrite)
            .ToList();

        if (toWrite.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "没有需要写入的预制体。", "确定");
            return;
        }

        // 清空旧备份,建立新备份目录
        if (Directory.Exists(BackupRoot))
        {
            try { Directory.Delete(BackupRoot, true); } catch { /* 忽略 */ }
        }
        string backupDir = Path.Combine(BackupRoot, System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupDir);

        LastRunRecord record = new LastRunRecord { backupDir = backupDir };
        int written = 0;
        int layersChanged = 0;
        List<string> errors = new List<string>();

        try
        {
            for (int i = 0; i < toWrite.Count; i++)
            {
                PlanItem item = toWrite[i];
                EditorUtility.DisplayProgressBar("写入中",
                    $"{item.entry.asset.name} ({i + 1}/{toWrite.Count})",
                    (float)i / toWrite.Count);

                if (!BackupPrefabFile(item.assetPath, backupDir, out string backupErr))
                {
                    errors.Add($"{item.entry.asset.name}: 备份失败 - {backupErr}");
                    continue;
                }

                if (!ApplyToPrefab(item, out bool isNewCollider, out int layerChange, out string applyErr))
                {
                    errors.Add($"{item.entry.asset.name}: 写入失败 - {applyErr}");
                    continue;
                }

                if (isNewCollider)
                {
                    record.newColliders.Add((item.assetPath, item.entry.mountPath));
                }
                layersChanged += layerChange;
                written++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        record.totalWritten = written;
        lastRun = record;
        pendingPlan = null;

        string msg = $"已写入：{written} 个预制体\n本次新增 BoxCollider：{record.newColliders.Count} 个\n修改 Layer：{layersChanged}";
        if (errors.Count > 0)
        {
            msg += "\n\n错误：\n" + string.Join("\n", errors);
        }
        msg += "\n\n如需撤销，使用右侧「还原上次写入」或「清除上次新增」。";
        EditorUtility.DisplayDialog("完成", msg, "确定");
    }

    private static bool BackupPrefabFile(string assetPath, string backupDir, out string error)
    {
        error = null;
        try
        {
            string fullAssetPath = Path.GetFullPath(assetPath);
            string fullBackupDir = Path.GetFullPath(backupDir);
            string dest = Path.Combine(fullBackupDir, assetPath);
            string destDir = Path.GetDirectoryName(dest);
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            File.Copy(fullAssetPath, dest, true);

            string metaSrc = fullAssetPath + ".meta";
            if (File.Exists(metaSrc))
            {
                File.Copy(metaSrc, dest + ".meta", true);
            }
            return true;
        }
        catch (System.Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private bool ApplyToPrefab(PlanItem item, out bool isNewCollider, out int layerChange, out string error)
    {
        isNewCollider = false;
        layerChange = 0;
        error = null;

        GameObject root = PrefabUtility.LoadPrefabContents(item.assetPath);
        try
        {
            Transform mount = ResolveMount(root.transform, item.entry.mountPath);
            if (mount == null)
            {
                error = "挂点丢失";
                return false;
            }

            BoxCollider bc = mount.GetComponent<BoxCollider>();
            if (bc == null)
            {
                bc = mount.gameObject.AddComponent<BoxCollider>();
                isNewCollider = true;
            }
            bc.center = item.boundsToApply.center;
            bc.size = item.boundsToApply.size;
            bc.isTrigger = isTrigger;

            if (setMountLayer && mount.gameObject.layer != targetLayer)
            {
                mount.gameObject.layer = targetLayer;
                layerChange = 1;
            }

            PrefabUtility.SaveAsPrefabAsset(root, item.assetPath);
            return true;
        }
        catch (System.Exception e)
        {
            error = e.Message;
            return false;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ───────── 还原（A） ─────────

    private void RestoreFromBackup()
    {
        if (lastRun == null || string.IsNullOrEmpty(lastRun.backupDir) || !Directory.Exists(lastRun.backupDir))
        {
            EditorUtility.DisplayDialog("错误", "备份不存在，无法还原。", "确定");
            return;
        }

        int restored = 0;
        List<string> errors = new List<string>();

        try
        {
            string[] backupFiles = Directory.GetFiles(lastRun.backupDir, "*", SearchOption.AllDirectories);
            for (int i = 0; i < backupFiles.Length; i++)
            {
                string backupFile = backupFiles[i];
                EditorUtility.DisplayProgressBar("还原中", Path.GetFileName(backupFile),
                    (float)i / backupFiles.Length);

                string relative = backupFile.Substring(lastRun.backupDir.Length).TrimStart('/', '\\');
                relative = relative.Replace('\\', '/');

                try
                {
                    string destPath = Path.GetFullPath(relative);
                    File.Copy(backupFile, destPath, true);
                    restored++;
                }
                catch (System.Exception e)
                {
                    errors.Add($"{relative}: {e.Message}");
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.Refresh();

        lastRun.newColliders.Clear();

        string msg = $"已还原 {restored} 个预制体。";
        if (errors.Count > 0) msg += "\n\n错误：\n" + string.Join("\n", errors);
        EditorUtility.DisplayDialog("完成", msg, "确定");
    }

    // ───────── 清除上次新增（C） ─────────

    private void ClearNewlyAddedColliders()
    {
        if (lastRun == null || lastRun.newColliders.Count == 0) return;

        int removed = 0;
        List<string> errors = new List<string>();
        var snapshot = lastRun.newColliders.ToList();

        try
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                var (assetPath, mountPath) = snapshot[i];
                EditorUtility.DisplayProgressBar("清除中", assetPath, (float)i / snapshot.Count);

                GameObject root = PrefabUtility.LoadPrefabContents(assetPath);
                try
                {
                    Transform mount = ResolveMount(root.transform, mountPath);
                    if (mount == null)
                    {
                        errors.Add($"{assetPath}: 挂点丢失");
                        continue;
                    }

                    BoxCollider bc = mount.GetComponent<BoxCollider>();
                    if (bc == null)
                    {
                        errors.Add($"{assetPath}: BoxCollider 已不存在");
                        continue;
                    }

                    Object.DestroyImmediate(bc, true);
                    PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                    removed++;
                }
                catch (System.Exception e)
                {
                    errors.Add($"{assetPath}: {e.Message}");
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        lastRun.newColliders.Clear();

        string msg = $"已清除 {removed} 个 BoxCollider。";
        if (errors.Count > 0) msg += "\n\n警告：\n" + string.Join("\n", errors);
        EditorUtility.DisplayDialog("完成", msg, "确定");
    }

    // ───────── 计算工具 ─────────

    private static Transform ResolveMount(Transform root, string mountPath)
    {
        if (string.IsNullOrEmpty(mountPath)) return root;
        return root.Find(mountPath);
    }

    private static bool TryComputeBoundsInLocalSpace(
        Transform mount, List<Transform> participants, out Bounds localBounds)
    {
        localBounds = default;

        // 第一步：用 Renderer.bounds 构建世界空间组合 AABB
        Bounds combinedWorld = default;
        bool hasWorld = false;

        foreach (Transform t in participants)
        {
            Renderer r = t.GetComponent<Renderer>();
            if (r == null) continue;

            Bounds wb = r.bounds;
            if (!hasWorld)
            {
                combinedWorld = wb;
                hasWorld = true;
            }
            else
            {
                combinedWorld.Encapsulate(wb);
            }
        }

        if (!hasWorld) return false;

        // 第二步：将世界空间 AABB 的 8 个角点转换到挂点 local 空间
        bool has = false;
        Vector3 wc = combinedWorld.center;
        Vector3 we = combinedWorld.extents;

        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        for (int sz = -1; sz <= 1; sz += 2)
        {
            Vector3 worldCorner = wc + new Vector3(sx * we.x, sy * we.y, sz * we.z);
            Vector3 local = mount.InverseTransformPoint(worldCorner);
            if (!has)
            {
                localBounds = new Bounds(local, Vector3.zero);
                has = true;
            }
            else
            {
                localBounds.Encapsulate(local);
            }
        }

        Vector3 size = localBounds.size;
        localBounds = new Bounds(localBounds.center,
            new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)));

        // 第三步：扩展 1% 边距，避免浮点精度导致碰撞盒略小于模型
        Vector3 margin = localBounds.size * 0.005f;
        localBounds.size += margin * 2f;
        return true;
    }

    // ── EditorPrefs persistence (used by Workflow) ──
    private const string PrefKey = "BoxColliderGenerator.Settings";

    [System.Serializable]
    private class SavedState
    {
        public int existingPolicy;
        public int targetLayer;
        public bool setMountLayer;
        public bool isTrigger;
    }

    public void SaveSettings()
    {
        var s = new SavedState
        {
            existingPolicy = (int)existingPolicy,
            targetLayer = targetLayer,
            setMountLayer = setMountLayer,
            isTrigger = isTrigger,
        };
        EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));
    }

    public void LoadSettings()
    {
        string json = EditorPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var s = JsonUtility.FromJson<SavedState>(json);
            existingPolicy = (ExistingColliderPolicy)s.existingPolicy;
            targetLayer = s.targetLayer;
            setMountLayer = s.setMountLayer;
            isTrigger = s.isTrigger;
        }
        catch { }
    }
}
