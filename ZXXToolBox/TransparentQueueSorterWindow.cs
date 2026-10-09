using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class TransparentQueueSorterWindow : EditorWindow
{
    [Serializable]
    private sealed class QueueNode
    {
        public GameObject GameObject;
        public List<QueueNode> Children = new List<QueueNode>();
        public bool Expanded = true;
        public bool HasTransparentRenderer;
        public bool ManualQueue;
        public int ManualQueueValue;

        [NonSerialized] public int AssignedQueue = -1;
        [NonSerialized] public int FirstQueue = -1;
        [NonSerialized] public int LastQueue = -1;
        [NonSerialized] public int SortableCount;
    }

    private sealed class SortItem
    {
        public Renderer Renderer;
        public Material[] OriginalMaterials;
        public readonly List<int> TransparentSlots = new List<int>();
        public int Queue;
    }

    private sealed class PreviewRecord
    {
        public Renderer Renderer;
        public Material[] Originals;
        public Material[] PreviewMaterials;
    }

    private sealed class NodeLocation
    {
        public QueueNode Node;
        public List<QueueNode> Siblings;
        public int Index;
    }

    [SerializeField] private List<QueueNode> roots = new List<QueueNode>();
    [SerializeField] private bool includeInactive = true;
    [SerializeField] private bool shareQueueByRoot = true;
    [SerializeField] private int minQueue = 2950;
    [SerializeField] private int maxQueue = 2999;

    private readonly List<SortItem> items = new List<SortItem>();
    private readonly List<PreviewRecord> previewRecords = new List<PreviewRecord>();
    private Vector2 scroll;
    private string message;
    private MessageType messageType = MessageType.Info;
    private bool allocationValid;

    public static void Open()
    {
        GetWindow<TransparentQueueSorterWindow>("半透队列排序").minSize = new Vector2(570f, 480f);
    }

    private void OnEnable()
    {
        AssemblyReloadEvents.beforeAssemblyReload += RestorePreview;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        RebuildItems();
    }

    private void OnDisable()
    {
        AssemblyReloadEvents.beforeAssemblyReload -= RestorePreview;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        RestorePreview();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode)
            RestorePreview();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("场景半透明材质手动队列", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "面板越靠上，Render Queue 越大（越晚绘制，通常显示在更上层）。\n" +
            "父节点移动时整棵子树跟随；这里只改变材质队列，不改变 Hierarchy 顺序。",
            MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("读取当前选择", GUILayout.Height(28f))) LoadSelection();
        if (GUILayout.Button("读取整个激活场景", GUILayout.Height(28f))) LoadActiveScene();
        using (new EditorGUI.DisabledScope(roots.Count == 0))
        {
            if (GUILayout.Button("清空列表", GUILayout.Height(28f))) ClearList();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUI.BeginChangeCheck();
        bool newIncludeInactive = EditorGUILayout.Toggle("包含未激活子物体", includeInactive);
        if (EditorGUI.EndChangeCheck())
        {
            includeInactive = newIncludeInactive;
            RestorePreview();
            roots.Clear();
            RebuildItems();
            SetMessage("包含范围已改变，请重新读取选择或场景。", MessageType.Warning);
        }

        EditorGUI.BeginChangeCheck();
        bool newShareQueueByRoot = EditorGUILayout.Toggle("根节点与全部子物体共用 Queue", shareQueueByRoot);
        if (EditorGUI.EndChangeCheck())
        {
            RestorePreview();
            shareQueueByRoot = newShareQueueByRoot;
            ClearManualQueueFlags(roots);
            RebuildItems();
            SetMessage(
                shareQueueByRoot
                    ? "已启用根组共用 Queue。每个根节点及其全部子物体只占用一个队列值；组内绘制先后由 Unity 决定。"
                    : "已改为每个半透明物体独立分配 Queue。",
                MessageType.Info);
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Render Queue 范围");
        EditorGUI.BeginChangeCheck();
        int newMin = EditorGUILayout.IntField(minQueue, GUILayout.Width(70f));
        GUILayout.Label("—", GUILayout.Width(16f));
        int newMax = EditorGUILayout.IntField(maxQueue, GUILayout.Width(70f));
        GUILayout.FlexibleSpace();
        GUILayout.Label($"可用 {Mathf.Max(0, maxQueue - minQueue + 1)} 个数值", EditorStyles.miniLabel);
        if (EditorGUI.EndChangeCheck())
        {
            RestorePreview();
            minQueue = Mathf.Clamp(newMin, 0, 5000);
            maxQueue = Mathf.Clamp(newMax, 0, 5000);
            RebuildItems();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5f);
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || !allocationValid))
        {
            if (GUILayout.Button("预览排序", GUILayout.Height(32f))) Preview();
        }
        using (new EditorGUI.DisabledScope(previewRecords.Count == 0))
        {
            if (GUILayout.Button("取消预览", GUILayout.Height(32f)))
            {
                RestorePreview();
                SetMessage("已取消预览，场景材质引用已还原。", MessageType.Info);
            }
            if (GUILayout.Button("烘培独立材质", GUILayout.Height(32f))) Bake();
        }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(message))
            EditorGUILayout.HelpBox(message, messageType);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField(
            $"手动排序列表 · {CountSortableNodes()} 个物体 / {items.Count} 个 Renderer / {CountQueueOwners()} 个队列组",
            EditorStyles.boldLabel);
        scroll = EditorGUILayout.BeginScrollView(scroll, "box");
        if (roots.Count == 0)
            EditorGUILayout.HelpBox("先在 Hierarchy 选择父节点，再点击“读取当前选择”。", MessageType.None);
        else
            DrawNodeList(roots, 0);
        EditorGUILayout.EndScrollView();
    }

    private void LoadSelection()
    {
        RestorePreview();
        GameObject[] selected = Selection.gameObjects
            .Where(go => go != null && !EditorUtility.IsPersistent(go) && go.scene.IsValid())
            .Where(go => !Selection.gameObjects.Any(other =>
                other != null && other != go && go.transform.IsChildOf(other.transform)))
            .OrderBy(go => go.transform.GetSiblingIndex())
            .ToArray();

        roots.Clear();
        foreach (GameObject selectedRoot in selected)
        {
            QueueNode node = BuildNode(selectedRoot.transform);
            if (node != null) roots.Add(node);
        }
        RebuildItems();
        SetLoadMessage("当前选择");
    }

    private void LoadActiveScene()
    {
        RestorePreview();
        roots.Clear();
        foreach (GameObject sceneRoot in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            QueueNode node = BuildNode(sceneRoot.transform);
            if (node != null) roots.Add(node);
        }
        RebuildItems();
        SetLoadMessage("当前激活场景");
    }

    private void SetLoadMessage(string source)
    {
        int count = CountSortableNodes();
        if (count > 0 && !allocationValid) return;

        int duplicateGroups = CountDuplicateQueueGroups();
        string reuseNotice = duplicateGroups > 0
            ? $" 当前范围只有 {maxQueue - minQueue + 1} 个数值，已按顺序复用 Queue；共有 {duplicateGroups} 组重复队列。"
            : string.Empty;
        SetMessage(
            count == 0
                ? source + "中没有找到半透明 Renderer。"
                : $"已从{source}读取 {count} 个半透明物体。" + reuseNotice,
            count == 0 || duplicateGroups > 0 ? MessageType.Warning : MessageType.Info);
    }

    private void ClearList()
    {
        RestorePreview();
        roots.Clear();
        RebuildItems();
        SetMessage("已清空工具列表，没有删除场景物体。", MessageType.Info);
    }

    private QueueNode BuildNode(Transform transform)
    {
        if (transform == null || (!includeInactive && !transform.gameObject.activeInHierarchy)) return null;

        var node = new QueueNode { GameObject = transform.gameObject };
        node.HasTransparentRenderer = transform.GetComponents<Renderer>()
            .Any(renderer => renderer.sharedMaterials.Any(IsTransparent));

        foreach (Transform child in transform)
        {
            QueueNode childNode = BuildNode(child);
            if (childNode != null) node.Children.Add(childNode);
        }
        return node.HasTransparentRenderer || node.Children.Count > 0 ? node : null;
    }

    private void DrawNodeList(List<QueueNode> siblings, int depth)
    {
        for (int index = 0; index < siblings.Count; index++)
        {
            QueueNode node = siblings[index];
            if (node == null || node.GameObject == null) continue;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(depth * 16f);
            if (node.Children.Count > 0)
                node.Expanded = GUILayout.Toggle(node.Expanded, GUIContent.none, EditorStyles.foldout, GUILayout.Width(14f));
            else
                GUILayout.Space(14f);

            EditorGUILayout.ObjectField(node.GameObject, typeof(GameObject), true);
            DrawQueueControl(node, siblings, index, depth);

            bool childOrderHasNoQueueEffect = shareQueueByRoot && depth > 0;
            using (new EditorGUI.DisabledScope(index == 0 || childOrderHasNoQueueEffect))
            {
                if (GUILayout.Button("▲", GUILayout.Width(26f)))
                {
                    MoveNode(siblings, index, index - 1);
                    GUIUtility.ExitGUI();
                }
            }
            using (new EditorGUI.DisabledScope(index >= siblings.Count - 1 || childOrderHasNoQueueEffect))
            {
                if (GUILayout.Button("▼", GUILayout.Width(26f)))
                {
                    MoveNode(siblings, index, index + 1);
                    GUIUtility.ExitGUI();
                }
            }
            if (GUILayout.Button("移除", GUILayout.Width(42f)))
            {
                RestorePreview();
                siblings.RemoveAt(index);
                RebuildItems();
                SetMessage("已从列表移除该组，没有删除场景物体。", MessageType.Info);
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();

            if (node.Expanded && node.Children.Count > 0)
                DrawNodeList(node.Children, depth + 1);
        }
    }

    private void DrawQueueControl(QueueNode node, List<QueueNode> siblings, int index, int depth)
    {
        bool isQueueOwner = shareQueueByRoot ? depth == 0 : node.HasTransparentRenderer;
        if (!isQueueOwner)
        {
            string label = shareQueueByRoot
                ? $"跟随 RQ {node.AssignedQueue}"
                : GetGroupQueueLabel(node);
            GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.Width(112f));
            return;
        }

        Color oldColor = GUI.backgroundColor;
        if (node.ManualQueue) GUI.backgroundColor = new Color(1f, 0.86f, 0.45f);
        EditorGUI.BeginChangeCheck();
        int entered = EditorGUILayout.IntField(node.AssignedQueue, GUILayout.Width(62f));
        if (EditorGUI.EndChangeCheck())
        {
            GUI.backgroundColor = oldColor;
            TrySetManualQueue(node, siblings, index, entered);
            GUIUtility.ExitGUI();
        }
        GUI.backgroundColor = oldColor;

        using (new EditorGUI.DisabledScope(!node.ManualQueue))
        {
            if (GUILayout.Button("自动", GUILayout.Width(42f)))
            {
                RestorePreview();
                node.ManualQueue = false;
                RebuildItems();
                SetMessage("已恢复自动分配，并保留当前面板顺序。", MessageType.Info);
                GUIUtility.ExitGUI();
            }
        }
    }

    private static string GetGroupQueueLabel(QueueNode node)
    {
        if (node.SortableCount <= 0) return "组";
        return node.FirstQueue == node.LastQueue
            ? $"子项 RQ {node.FirstQueue}"
            : $"子项 {node.FirstQueue}–{node.LastQueue}";
    }

    private void TrySetManualQueue(QueueNode node, List<QueueNode> siblings, int index, int requestedQueue)
    {
        if (requestedQueue < minQueue || requestedQueue > maxQueue)
        {
            EditorUtility.DisplayDialog(
                "超出队列范围",
                $"输入值 {requestedQueue} 不在允许范围 {minQueue}–{maxQueue} 内，本次不会修改。",
                "确定");
            return;
        }

        List<QueueNode> flat = GetQueueOwners();
        int flatIndex = flat.IndexOf(node);
        QueueNode duplicate = flat.FirstOrDefault(other => other != node && other.AssignedQueue == requestedQueue);
        bool orderConflict = flatIndex >= 0 &&
            ((flatIndex > 0 && flat[flatIndex - 1].AssignedQueue < requestedQueue) ||
             (flatIndex < flat.Count - 1 && flat[flatIndex + 1].AssignedQueue > requestedQueue));

        if (duplicate != null || orderConflict)
        {
            string reason = string.Empty;
            if (orderConflict)
                reason += "这个数值与当前面板上下顺序不一致。继续后，该物体及全部子节点会作为一个整体移动到新的顺序位置。\n\n";
            if (duplicate != null)
                reason += $"它还与“{duplicate.GameObject.name}”的 Queue {requestedQueue} 重复。相同 Queue 的透明物体由 Unity 自行排序，无法保证固定的前后关系。\n\n";

            if (!EditorUtility.DisplayDialog(
                    "队列排序冲突",
                    reason + "是否确认修改并调整面板顺序？",
                    "确认修改", "取消"))
                return;
        }

        RestorePreview();
        node.ManualQueue = true;
        node.ManualQueueValue = requestedQueue;
        NodeLocation moveLocation = FindMoveLocation(node, requestedQueue) ?? new NodeLocation
        {
            Node = node,
            Siblings = siblings,
            Index = index
        };
        ReorderNodeForQueue(moveLocation.Siblings, moveLocation.Index, requestedQueue);
        RebuildItems();

        if (!allocationValid)
        {
            SetMessage("已记录手动 Queue，但当前范围无法形成从高到低的完整排序，请处理面板中的红色错误提示。", MessageType.Error);
        }
        else
        {
            SetMessage($"“{node.GameObject.name}”已固定为 Queue {requestedQueue}，父子组顺序已同步调整。", MessageType.Info);
        }
    }

    private NodeLocation FindMoveLocation(QueueNode target, int requestedQueue)
    {
        var path = new List<NodeLocation>();
        if (!TryFindNodePath(roots, target, path) || path.Count == 0) return null;

        int moveLevel = path.Count - 1;
        while (moveLevel > 0)
        {
            QueueNode parent = path[moveLevel - 1].Node;
            if (parent.FirstQueue < 0 ||
                (requestedQueue >= parent.FirstQueue && requestedQueue <= parent.LastQueue))
                break;
            moveLevel--;
        }
        return path[moveLevel];
    }

    private static bool TryFindNodePath(
        List<QueueNode> siblings,
        QueueNode target,
        List<NodeLocation> path)
    {
        for (int i = 0; i < siblings.Count; i++)
        {
            QueueNode node = siblings[i];
            path.Add(new NodeLocation { Node = node, Siblings = siblings, Index = i });
            if (node == target) return true;
            if (TryFindNodePath(node.Children, target, path)) return true;
            path.RemoveAt(path.Count - 1);
        }
        return false;
    }

    private void ReorderNodeForQueue(List<QueueNode> siblings, int oldIndex, int requestedQueue)
    {
        QueueNode moving = siblings[oldIndex];
        siblings.RemoveAt(oldIndex);

        int insertIndex = siblings.Count;
        for (int i = 0; i < siblings.Count; i++)
        {
            int comparisonQueue = siblings[i].FirstQueue;
            if (comparisonQueue >= 0 && requestedQueue >= comparisonQueue)
            {
                insertIndex = i;
                break;
            }
        }
        siblings.Insert(insertIndex, moving);
    }

    private void MoveNode(List<QueueNode> siblings, int from, int to)
    {
        RestorePreview();
        QueueNode moving = siblings[from];
        siblings.RemoveAt(from);
        siblings.Insert(to, moving);
        RebuildItems();
        SetMessage("顺序已调整；父节点的全部子节点已随组移动。", MessageType.Info);
    }

    private void RebuildItems()
    {
        items.Clear();
        allocationValid = AllocateQueues(out string error);
        if (!allocationValid && !string.IsNullOrEmpty(error))
            SetMessage(error, MessageType.Error);
        if (!allocationValid) return;

        foreach (QueueNode node in FlattenSortableNodes())
        {
            foreach (Renderer renderer in node.GameObject.GetComponents<Renderer>())
            {
                Material[] originals = renderer.sharedMaterials;
                var item = new SortItem
                {
                    Renderer = renderer,
                    OriginalMaterials = originals,
                    Queue = node.AssignedQueue
                };
                for (int slot = 0; slot < originals.Length; slot++)
                {
                    if (IsTransparent(originals[slot])) item.TransparentSlots.Add(slot);
                }
                if (item.TransparentSlots.Count > 0) items.Add(item);
            }
        }
    }

    private bool AllocateQueues(out string error)
    {
        error = null;
        ResetComputedValues(roots);
        if (minQueue > maxQueue)
        {
            error = $"队列范围无效：最小值 {minQueue} 不能大于最大值 {maxQueue}。";
            return false;
        }

        List<QueueNode> flat = GetQueueOwners();
        if (flat.Count == 0) return false;

        List<int> manualIndices = new List<int>();
        int previousManual = int.MaxValue;
        for (int i = 0; i < flat.Count; i++)
        {
            QueueNode node = flat[i];
            if (!node.ManualQueue) continue;
            if (node.ManualQueueValue < minQueue || node.ManualQueueValue > maxQueue)
            {
                error = $"“{node.GameObject.name}”的手动 Queue {node.ManualQueueValue} 超出范围 {minQueue}–{maxQueue}。";
                return false;
            }
            if (node.ManualQueueValue > previousManual)
            {
                error = $"“{node.GameObject.name}”的 Queue {node.ManualQueueValue} 与当前父子分组顺序冲突。请调整该节点或父节点顺序。";
                return false;
            }
            previousManual = node.ManualQueueValue;
            manualIndices.Add(i);
        }

        if (manualIndices.Count == 0)
        {
            AssignAutomaticRange(flat, 0, flat.Count - 1, maxQueue, minQueue, true, true);
        }
        else
        {
            int firstManualIndex = manualIndices[0];
            AssignAutomaticRange(
                flat, 0, firstManualIndex - 1,
                maxQueue, flat[firstManualIndex].ManualQueueValue,
                true, false);

            for (int manualOrder = 0; manualOrder < manualIndices.Count; manualOrder++)
            {
                int currentIndex = manualIndices[manualOrder];
                flat[currentIndex].AssignedQueue = flat[currentIndex].ManualQueueValue;

                if (manualOrder >= manualIndices.Count - 1) continue;
                int nextIndex = manualIndices[manualOrder + 1];
                AssignAutomaticRange(
                    flat, currentIndex + 1, nextIndex - 1,
                    flat[currentIndex].ManualQueueValue, flat[nextIndex].ManualQueueValue,
                    false, false);
            }

            int lastManualIndex = manualIndices[manualIndices.Count - 1];
            AssignAutomaticRange(
                flat, lastManualIndex + 1, flat.Count - 1,
                flat[lastManualIndex].ManualQueueValue, minQueue,
                false, true);
        }

        if (shareQueueByRoot)
        {
            foreach (QueueNode root in roots)
                ApplySharedQueue(root, root.AssignedQueue);
        }
        UpdateGroupRanges(roots);
        return true;
    }

    private static void AssignAutomaticRange(
        List<QueueNode> flat,
        int startIndex,
        int endIndex,
        int lowQueue,
        int highQueue,
        bool includeLow,
        bool includeHigh)
    {
        int count = endIndex - startIndex + 1;
        if (count <= 0) return;

        for (int offset = 0; offset < count; offset++)
        {
            float t;
            if (includeLow && includeHigh)
                t = count == 1 ? 0f : offset / (float)(count - 1);
            else if (includeLow)
                t = offset / (float)count;
            else if (includeHigh)
                t = (offset + 1) / (float)count;
            else
                t = (offset + 1) / (float)(count + 1);

            flat[startIndex + offset].AssignedQueue =
                Mathf.FloorToInt(Mathf.Lerp(lowQueue, highQueue, t));
        }
    }

    private static void ResetComputedValues(IEnumerable<QueueNode> nodes)
    {
        foreach (QueueNode node in nodes)
        {
            node.AssignedQueue = -1;
            node.FirstQueue = -1;
            node.LastQueue = -1;
            node.SortableCount = 0;
            ResetComputedValues(node.Children);
        }
    }

    private static void ClearManualQueueFlags(IEnumerable<QueueNode> nodes)
    {
        foreach (QueueNode node in nodes)
        {
            node.ManualQueue = false;
            ClearManualQueueFlags(node.Children);
        }
    }

    private static void ApplySharedQueue(QueueNode node, int queue)
    {
        if (node == null) return;
        node.AssignedQueue = queue;
        foreach (QueueNode child in node.Children) ApplySharedQueue(child, queue);
    }

    private static void UpdateGroupRanges(IEnumerable<QueueNode> nodes)
    {
        foreach (QueueNode node in nodes)
        {
            UpdateGroupRanges(node.Children);
            var queues = new List<int>();
            if (node.HasTransparentRenderer && node.AssignedQueue >= 0) queues.Add(node.AssignedQueue);
            foreach (QueueNode child in node.Children)
            {
                if (child.SortableCount <= 0) continue;
                queues.Add(child.FirstQueue);
                if (child.LastQueue != child.FirstQueue) queues.Add(child.LastQueue);
                node.SortableCount += child.SortableCount;
            }
            if (node.HasTransparentRenderer && node.AssignedQueue >= 0) node.SortableCount++;
            if (queues.Count > 0)
            {
                node.FirstQueue = queues.Min();
                node.LastQueue = queues.Max();
            }
        }
    }

    private List<QueueNode> FlattenSortableNodes()
    {
        var result = new List<QueueNode>();
        foreach (QueueNode root in roots) FlattenRecursive(root, result);
        return result;
    }

    private static void FlattenRecursive(QueueNode node, List<QueueNode> result)
    {
        if (node == null || node.GameObject == null) return;
        if (node.HasTransparentRenderer) result.Add(node);
        foreach (QueueNode child in node.Children) FlattenRecursive(child, result);
    }

    private int CountSortableNodes()
    {
        return FlattenSortableNodes().Count;
    }

    private List<QueueNode> GetQueueOwners()
    {
        return shareQueueByRoot
            ? roots.Where(root => root != null && root.GameObject != null).ToList()
            : FlattenSortableNodes();
    }

    private int CountQueueOwners()
    {
        return roots.Count == 0 ? 0 : GetQueueOwners().Count;
    }

    private int CountDuplicateQueueGroups()
    {
        if (!allocationValid) return 0;
        return GetQueueOwners()
            .Where(node => node.AssignedQueue >= 0)
            .GroupBy(node => node.AssignedQueue)
            .Count(group => group.Count() > 1);
    }

    private void Preview()
    {
        RestorePreview();
        RebuildItems();
        if (!allocationValid) return;

        int duplicateGroups = CountDuplicateQueueGroups();
        if (duplicateGroups > 0 && !EditorUtility.DisplayDialog(
                "存在重复 Render Queue",
                $"当前 {CountSortableNodes()} 个物体使用范围 {minQueue}–{maxQueue}，共有 {duplicateGroups} 组物体会共享 Queue。\n\n" +
                "面板仍会保持从大到小的分组顺序，但同一个 Queue 内部由 Unity 自行排序，无法保证严格的固定前后关系。是否继续预览？",
                "继续预览", "取消"))
            return;

        foreach (SortItem item in items)
        {
            Material[] preview = (Material[])item.OriginalMaterials.Clone();
            var clones = new Dictionary<Material, Material>();
            foreach (int slot in item.TransparentSlots)
            {
                Material source = item.OriginalMaterials[slot];
                if (!clones.TryGetValue(source, out Material clone))
                {
                    clone = new Material(source)
                    {
                        name = source.name + $" [Queue Preview {item.Queue}]",
                        hideFlags = HideFlags.HideAndDontSave,
                        renderQueue = item.Queue
                    };
                    clones.Add(source, clone);
                }
                preview[slot] = clone;
            }

            previewRecords.Add(new PreviewRecord
            {
                Renderer = item.Renderer,
                Originals = item.OriginalMaterials,
                PreviewMaterials = preview
            });
            item.Renderer.sharedMaterials = preview;
        }

        SetMessage($"已按面板顺序预览 {items.Count} 个 Renderer。关闭窗口或脚本重载会自动还原。", MessageType.Info);
        SceneView.RepaintAll();
    }

    private void Bake()
    {
        if (previewRecords.Count == 0 || !allocationValid) return;

        string invalidSource = items
            .SelectMany(item => item.TransparentSlots.Select(slot => item.OriginalMaterials[slot]))
            .Where(material => material != null)
            .Select(AssetDatabase.GetAssetPath)
            .FirstOrDefault(path => string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
        if (invalidSource != null)
        {
            SetMessage(
                "存在不在 Assets 目录内的内置或 Package 材质，无法在原路径生成独立材质。请先将材质提取到 Assets。" +
                (string.IsNullOrEmpty(invalidSource) ? string.Empty : "\n" + invalidSource),
                MessageType.Error);
            return;
        }

        int duplicateCount = GetQueueOwners()
            .GroupBy(node => node.AssignedQueue)
            .Count(group => group.Count() > 1);
        string duplicateWarning = duplicateCount > 0
            ? $"\n\n注意：存在 {duplicateCount} 组重复 Queue。重复队列的透明物体无法保证固定绘制先后。"
            : string.Empty;

        if (!EditorUtility.DisplayDialog(
                "烘培半透明队列材质",
                $"将按面板顺序为 {items.Count} 个 Renderer 生成独立材质。\n" +
                $"使用范围：{minQueue}–{maxQueue}。原材质不会修改，新材质生成在原材质同级目录。" +
                duplicateWarning,
                "烘培", "取消"))
            return;

        RestorePreview();
        var createdAssets = new List<string>();
        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("烘培半透明队列材质");

        try
        {
            foreach (SortItem item in items)
            {
                if (item.Renderer == null) continue;
                Material[] bakedSlots = item.Renderer.sharedMaterials;
                var bakedBySource = new Dictionary<Material, Material>();
                foreach (int slot in item.TransparentSlots)
                {
                    Material source = item.OriginalMaterials[slot];
                    if (!bakedBySource.TryGetValue(source, out Material baked))
                    {
                        string sourcePath = AssetDatabase.GetAssetPath(source);
                        string directory = Path.GetDirectoryName(sourcePath).Replace('\\', '/');
                        string objectName = SanitizeFileName(item.Renderer.gameObject.name);
                        string fileName = SanitizeFileName(source.name) + $"_RQ{item.Queue}_{objectName}.mat";
                        string outputPath = AssetDatabase.GenerateUniqueAssetPath(directory + "/" + fileName);

                        baked = new Material(source)
                        {
                            name = Path.GetFileNameWithoutExtension(outputPath),
                            renderQueue = item.Queue
                        };
                        AssetDatabase.CreateAsset(baked, outputPath);
                        Undo.RegisterCreatedObjectUndo(baked, "创建半透明队列材质");
                        createdAssets.Add(outputPath);
                        bakedBySource.Add(source, baked);
                    }
                    bakedSlots[slot] = baked;
                }

                Undo.RecordObject(item.Renderer, "赋予半透明队列材质");
                item.Renderer.sharedMaterials = bakedSlots;
                EditorUtility.SetDirty(item.Renderer);
                if (PrefabUtility.IsPartOfPrefabInstance(item.Renderer))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(item.Renderer);
                if (item.Renderer.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(item.Renderer.gameObject.scene);
            }

            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            previewRecords.Clear();
            SetMessage($"烘培完成：生成 {createdAssets.Count} 个独立材质，原材质未修改。", MessageType.Info);
            SceneView.RepaintAll();
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            SetMessage("烘培失败，详细错误请查看 Console。已生成资源会保留，避免误删现有文件。", MessageType.Error);
        }
    }

    private static bool IsTransparent(Material material)
    {
        if (material == null || material.shader == null) return false;
        string renderType = material.GetTag("RenderType", false, string.Empty);
        return string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase) ||
               material.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent ||
               material.shader.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    private void RestorePreview()
    {
        foreach (PreviewRecord record in previewRecords)
        {
            if (record.Renderer != null) record.Renderer.sharedMaterials = record.Originals;
            if (record.PreviewMaterials == null) continue;
            foreach (Material material in record.PreviewMaterials.Distinct())
            {
                if (material != null && (material.hideFlags & HideFlags.DontSave) != 0)
                    DestroyImmediate(material);
            }
        }
        previewRecords.Clear();
        SceneView.RepaintAll();
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "Renderer" : value.Trim();
    }

    private void SetMessage(string value, MessageType type)
    {
        message = value;
        messageType = type;
    }
}
