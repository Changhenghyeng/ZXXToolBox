using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class OverlapSpreaderWindow : EditorWindow
{
    [SerializeField] private GameObject targetGroup;
    [SerializeField] private float footprintScale = 0.7f;
    [SerializeField] private float spacing;
    [SerializeField] private bool includeInactive = true;

    private string result;

    private sealed class Entry
    {
        public GameObject gameObject;
        public Transform transform;
        public Bounds bounds;
        public Vector2 center;
        public Vector2 extents;
        public int stableIndex;
    }

    public static void Open()
    {
        GetWindow<OverlapSpreaderWindow>("重叠物体散开").minSize = new Vector2(360f, 220f);
    }

    private void OnSelectionChange()
    {
        result = null;
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("重叠物体散开", EditorStyles.boldLabel);
        EditorGUILayout.Space(4f);

        targetGroup = (GameObject)EditorGUILayout.ObjectField(
            "物体组", targetGroup, typeof(GameObject), true);

        bool validSceneGroup = targetGroup != null && !EditorUtility.IsPersistent(targetGroup);
        int groupChildCount = validSceneGroup ? targetGroup.transform.childCount : 0;
        if (targetGroup != null && !validSceneGroup)
            EditorGUILayout.HelpBox("请拖入 Hierarchy 中的场景父物体，不能使用 Project Prefab。", MessageType.Warning);
        else if (validSceneGroup)
            EditorGUILayout.LabelField("组内直接子物体", groupChildCount.ToString());

        int selectedCount = GetTopLevelSceneSelection().Length;
        EditorGUILayout.LabelField("当前选中的顶层场景物体", selectedCount.ToString());

        footprintScale = EditorGUILayout.Slider("占用范围比例", footprintScale, 0.1f, 1f);
        spacing = Mathf.Max(0f, EditorGUILayout.FloatField("额外间距", spacing));
        includeInactive = EditorGUILayout.Toggle("包含未激活子 Renderer", includeInactive);

        EditorGUILayout.Space(6f);
        EditorGUILayout.HelpBox(
            "拖入一个场景父物体时，每个直接子节点会作为一个整体参与处理；也可以直接多选场景物体。" +
            "工具使用各对象全部子 Renderer 的世界 Bounds 在 XZ 地面检测；占用范围比例可以忽略透明面片外围。" +
            "数值越小，散开距离越短。只散开彼此重叠的分组。" +
            "父物体、Y 高度、旋转和缩放保持不变。整次操作支持 Ctrl+Z。",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(
                   EditorApplication.isPlayingOrWillChangePlaymode || !validSceneGroup || groupChildCount < 2))
        {
            if (GUILayout.Button("散开物体组的直接子物体", GUILayout.Height(34f)))
                SpreadObjects(GetDirectChildren(targetGroup), "物体组");
        }

        EditorGUILayout.Space(3f);
        using (new EditorGUI.DisabledScope(
                   EditorApplication.isPlayingOrWillChangePlaymode || selectedCount < 2))
        {
            if (GUILayout.Button("散开选中的重叠物体", GUILayout.Height(34f)))
                SpreadSelection();
        }

        if (!string.IsNullOrEmpty(result))
            EditorGUILayout.HelpBox(result, MessageType.Info);
    }

    private void SpreadSelection()
    {
        SpreadObjects(GetTopLevelSceneSelection(), "当前选择");
    }

    private void SpreadObjects(GameObject[] candidates, string sourceLabel)
    {
        var entries = new List<Entry>(candidates.Length);
        int skippedWithoutRenderer = 0;

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!TryGetRendererBounds(candidates[i], includeInactive, out Bounds bounds))
            {
                skippedWithoutRenderer++;
                continue;
            }

            entries.Add(new Entry
            {
                gameObject = candidates[i],
                transform = candidates[i].transform,
                bounds = bounds,
                center = new Vector2(bounds.center.x, bounds.center.z),
                extents = new Vector2(bounds.extents.x, bounds.extents.z) * footprintScale,
                stableIndex = i
            });
        }

        if (entries.Count < 2)
        {
            result = skippedWithoutRenderer > 0
                ? $"{sourceLabel}中有效 Renderer 不足 2 个，无法检测重叠。"
                : $"{sourceLabel}至少需要 2 个物体。";
            return;
        }

        List<List<Entry>> groups = BuildOverlapGroups(entries, spacing);
        List<List<Entry>> overlapGroups = groups.Where(group => group.Count > 1).ToList();
        if (overlapGroups.Count == 0)
        {
            result = $"{sourceLabel}中没有检测到重叠物体。";
            return;
        }

        Transform[] undoTargets = overlapGroups
            .SelectMany(group => group)
            .Select(entry => entry.transform)
            .Distinct()
            .ToArray();

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("散开重叠物体");
        Undo.RecordObjects(undoTargets, "散开重叠物体");

        int movedCount = 0;
        int unresolvedCount = 0;

        foreach (List<Entry> group in overlapGroups)
        {
            SpreadGroup(group, entries, spacing, ref movedCount, ref unresolvedCount);
        }

        unresolvedCount = CountEntriesStillOverlapping(entries, spacing);

        foreach (var scene in undoTargets.Select(target => target.gameObject.scene).Distinct())
        {
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        Undo.CollapseUndoOperations(undoGroup);
        SceneView.RepaintAll();

        result = $"完成：处理 {overlapGroups.Count} 组重叠物体，移动 {movedCount} 个" +
                 (skippedWithoutRenderer > 0 ? $"，跳过 {skippedWithoutRenderer} 个无 Renderer 物体" : string.Empty) +
                 (unresolvedCount > 0 ? $"。仍有 {unresolvedCount} 个物体未能完全分离。" : "。Ctrl+Z 可撤销。");
    }

    private static GameObject[] GetDirectChildren(GameObject group)
    {
        if (group == null || EditorUtility.IsPersistent(group)) return Array.Empty<GameObject>();

        var children = new GameObject[group.transform.childCount];
        for (int i = 0; i < children.Length; i++)
            children[i] = group.transform.GetChild(i).gameObject;
        return children;
    }

    private static GameObject[] GetTopLevelSceneSelection()
    {
        GameObject[] selected = Selection.gameObjects
            .Where(gameObject => gameObject != null && !EditorUtility.IsPersistent(gameObject))
            .Distinct()
            .ToArray();

        return selected
            .Where(gameObject => !selected.Any(other =>
                other != gameObject && gameObject.transform.IsChildOf(other.transform)))
            .ToArray();
    }

    private static bool TryGetRendererBounds(GameObject root, bool includeInactiveObjects, out Bounds bounds)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactiveObjects);
        bool found = false;
        bounds = default;

        foreach (Renderer renderer in renderers)
        {
            if (renderer == null || renderer is ParticleSystemRenderer ||
                (!includeInactiveObjects && !renderer.gameObject.activeInHierarchy))
                continue;

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return found;
    }

    private static List<List<Entry>> BuildOverlapGroups(List<Entry> entries, float requestedSpacing)
    {
        var groups = new List<List<Entry>>();
        var visited = new bool[entries.Count];

        for (int start = 0; start < entries.Count; start++)
        {
            if (visited[start]) continue;

            var group = new List<Entry>();
            var queue = new Queue<int>();
            queue.Enqueue(start);
            visited[start] = true;

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                group.Add(entries[current]);

                for (int candidate = 0; candidate < entries.Count; candidate++)
                {
                    if (visited[candidate] ||
                        !Overlaps(entries[current], entries[candidate], requestedSpacing))
                        continue;

                    visited[candidate] = true;
                    queue.Enqueue(candidate);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static void SpreadGroup(
        List<Entry> group,
        List<Entry> allEntries,
        float requestedSpacing,
        ref int movedCount,
        ref int unresolvedCount)
    {
        Vector2 groupCenter = Vector2.zero;
        foreach (Entry entry in group) groupCenter += entry.center;
        groupCenter /= group.Count;

        List<Entry> ordered = group
            .OrderBy(entry => (entry.center - groupCenter).sqrMagnitude)
            .ThenBy(entry => entry.stableIndex)
            .ToList();

        var placed = allEntries.Where(entry => !group.Contains(entry)).ToList();
        placed.Add(ordered[0]);
        int coincidentDirectionIndex = 0;

        for (int i = 1; i < ordered.Count; i++)
        {
            Entry moving = ordered[i];
            Vector2 direction = moving.center - groupCenter;

            if (direction.sqrMagnitude < 0.000001f)
            {
                float angle = 2f * Mathf.PI * coincidentDirectionIndex /
                              Mathf.Max(1, ordered.Count - 1);
                direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                coincidentDirectionIndex++;
            }
            else
            {
                direction.Normalize();
            }

            Vector2 originalCenter = moving.center;
            bool resolved = MoveOutwardUntilClear(moving, direction, placed, requestedSpacing);
            Vector2 delta = moving.center - originalCenter;

            if (delta.sqrMagnitude > 0.0000001f)
            {
                Vector3 position = moving.transform.position;
                moving.transform.position = new Vector3(position.x + delta.x, position.y, position.z + delta.y);
                moving.bounds.center += new Vector3(delta.x, 0f, delta.y);
                movedCount++;
            }

            if (!resolved) unresolvedCount++;
            placed.Add(moving);
        }
    }

    private static bool MoveOutwardUntilClear(
        Entry moving,
        Vector2 direction,
        List<Entry> placed,
        float requestedSpacing)
    {
        const int maxIterations = 512;
        const float exitEpsilon = 0.0001f;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            Entry blocker = placed.FirstOrDefault(other => Overlaps(moving, other, requestedSpacing));
            if (blocker == null) return true;

            Vector2 delta = moving.center - blocker.center;
            float limitX = moving.extents.x + blocker.extents.x + requestedSpacing;
            float limitZ = moving.extents.y + blocker.extents.y + requestedSpacing;

            float exitX = ExitDistance(delta.x, direction.x, limitX);
            float exitZ = ExitDistance(delta.y, direction.y, limitZ);
            float step = Mathf.Min(exitX, exitZ);

            if (float.IsInfinity(step) || step < 0f)
                return false;

            moving.center += direction * (step + exitEpsilon);
        }

        return false;
    }

    private static float ExitDistance(float currentDelta, float direction, float separationLimit)
    {
        if (Mathf.Abs(direction) < 0.000001f) return float.PositiveInfinity;

        float target = direction > 0f ? separationLimit : -separationLimit;
        float distance = (target - currentDelta) / direction;
        return distance >= 0f ? distance : float.PositiveInfinity;
    }

    private static bool Overlaps(Entry a, Entry b, float requestedSpacing)
    {
        float requiredX = a.extents.x + b.extents.x + requestedSpacing;
        float requiredZ = a.extents.y + b.extents.y + requestedSpacing;
        Vector2 delta = a.center - b.center;
        return Mathf.Abs(delta.x) < requiredX && Mathf.Abs(delta.y) < requiredZ;
    }

    private static int CountEntriesStillOverlapping(List<Entry> entries, float requestedSpacing)
    {
        var unresolved = new HashSet<Entry>();
        for (int i = 0; i < entries.Count; i++)
        {
            for (int j = i + 1; j < entries.Count; j++)
            {
                if (!Overlaps(entries[i], entries[j], requestedSpacing)) continue;
                unresolved.Add(entries[i]);
                unresolved.Add(entries[j]);
            }
        }

        return unresolved.Count;
    }
}
