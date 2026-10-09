using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class RandomMaterialWindow : EditorWindow
{
    [SerializeField] private List<Material> materials = new List<Material>();
    [SerializeField] private bool includeChildren = true;
    [SerializeField] private int materialSlot;

    [SerializeField] private bool randomMaterial = true;

    [SerializeField] private bool randomScale;
    [SerializeField] private float scaleMin = 0.8f;
    [SerializeField] private float scaleMax = 1.2f;

    [SerializeField] private bool randomRotation;
    [SerializeField] private bool rotationX;
    [SerializeField] private bool rotationY = true;
    [SerializeField] private bool rotationZ;
    [SerializeField] private Vector2 rotationXRange = new Vector2(0f, 0f);
    [SerializeField] private Vector2 rotationYRange = new Vector2(0f, 360f);
    [SerializeField] private Vector2 rotationZRange = new Vector2(0f, 0f);

    [SerializeField] private bool randomPosition;
    [SerializeField] private bool positionX = true;
    [SerializeField] private bool positionY;
    [SerializeField] private bool positionZ = true;
    [SerializeField] private Vector2 positionXRange = new Vector2(-1f, 1f);
    [SerializeField] private Vector2 positionYRange = new Vector2(0f, 0f);
    [SerializeField] private Vector2 positionZRange = new Vector2(-1f, 1f);

    private Vector2 scroll;
    private string result;

    public static void Open()
    {
        GetWindow<RandomMaterialWindow>("随机材质").minSize = new Vector2(360, 300);
    }

    private void OnSelectionChange() => Repaint();

    private void OnGUI()
    {
        EditorGUILayout.LabelField("随机处理", EditorStyles.boldLabel);

        randomScale = EditorGUILayout.ToggleLeft("随机大小", randomScale, EditorStyles.boldLabel);
        if (randomScale)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.BeginHorizontal();
            scaleMin = EditorGUILayout.FloatField("Min", scaleMin);
            scaleMax = EditorGUILayout.FloatField("Max", scaleMax);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("等比缩放系数，基于对象当前 Local Scale。", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);
        randomRotation = EditorGUILayout.ToggleLeft("随机旋转", randomRotation, EditorStyles.boldLabel);
        if (randomRotation)
        {
            EditorGUI.indentLevel++;
            DrawAxisRange("X", ref rotationX, ref rotationXRange);
            DrawAxisRange("Y", ref rotationY, ref rotationYRange);
            DrawAxisRange("Z", ref rotationZ, ref rotationZRange);
            EditorGUILayout.LabelField("数值为相对当前 Local Rotation 的随机角度增量。", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(5);
        randomPosition = EditorGUILayout.ToggleLeft("随机位置", randomPosition, EditorStyles.boldLabel);
        if (randomPosition)
        {
            EditorGUI.indentLevel++;
            DrawAxisRange("X", ref positionX, ref positionXRange);
            DrawAxisRange("Y", ref positionY, ref positionYRange);
            DrawAxisRange("Z", ref positionZ, ref positionZRange);
            EditorGUILayout.LabelField("数值为相对当前 Local Position 的随机偏移。", EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);
        randomMaterial = EditorGUILayout.ToggleLeft("随机材质", randomMaterial, EditorStyles.boldLabel);
        if (randomMaterial)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("随机材质列表（数量不限）", EditorStyles.boldLabel);
            Rect dropArea = GUILayoutUtility.GetRect(0, 42, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "将一个或多个材质拖到这里添加");
            Event evt = Event.current;
            if (dropArea.Contains(evt.mousePosition) &&
                (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                var dropped = DragAndDrop.objectReferences.OfType<Material>().ToArray();
                if (dropped.Length > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        foreach (var material in dropped)
                            if (!materials.Contains(material)) materials.Add(material);
                    }
                    evt.Use();
                }
            }

            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(180));
            int remove = -1;
            for (int i = 0; i < materials.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                materials[i] = (Material)EditorGUILayout.ObjectField(
                    (i + 1).ToString(), materials[i], typeof(Material), false);
                if (GUILayout.Button("移除", GUILayout.Width(45))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (remove >= 0) materials.RemoveAt(remove);
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("添加材质槽")) materials.Add(null);
            includeChildren = EditorGUILayout.Toggle("包含子物体（含隐藏物体）", includeChildren);
            materialSlot = Mathf.Max(0, EditorGUILayout.IntField("替换材质槽（从 0 开始）", materialSlot));
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "随机大小 / 旋转 / 位置彼此独立，可只开启其中一个。Transform 只处理场景中的选中对象；" +
            "父子同时选中时只随机顶层对象，避免叠加。场景修改支持 Ctrl+Z。Project Prefab 仍只按原逻辑修改随机材质。",
            MessageType.Info);

        bool anyOperation = randomScale || randomRotation || randomPosition || randomMaterial;
        bool materialReady = !randomMaterial ||
                             materials.Any(m => m != null && EditorUtility.IsPersistent(m));

        using (new EditorGUI.DisabledScope(
                   EditorApplication.isPlayingOrWillChangePlaymode ||
                   !anyOperation || !materialReady || Selection.gameObjects.Length == 0))
        {
            if (GUILayout.Button("随机处理当前选中对象", GUILayout.Height(32)))
                ApplyToSelection();
        }

        if (randomMaterial && !materialReady)
            EditorGUILayout.HelpBox("已开启随机材质，但没有有效材质。", MessageType.Warning);

        if (!string.IsNullOrEmpty(result))
            EditorGUILayout.HelpBox(result, MessageType.Info);
    }

    private static void DrawAxisRange(string axis, ref bool enabled, ref Vector2 range)
    {
        EditorGUILayout.BeginHorizontal();
        enabled = EditorGUILayout.ToggleLeft(axis, enabled, GUILayout.Width(32));
        using (new EditorGUI.DisabledScope(!enabled))
        {
            GUILayout.Label("Min", GUILayout.Width(26));
            range.x = EditorGUILayout.FloatField(range.x);
            GUILayout.Label("Max", GUILayout.Width(28));
            range.y = EditorGUILayout.FloatField(range.y);
        }
        EditorGUILayout.EndHorizontal();
    }

    private void ApplyToSelection()
    {
        var pool = randomMaterial
            ? materials.Where(m => m != null && EditorUtility.IsPersistent(m)).Distinct().ToArray()
            : Array.Empty<Material>();
        if (randomMaterial && pool.Length == 0) return;

        var selected = Selection.gameObjects;

        var transformTargets = selected
            .Where(go => !EditorUtility.IsPersistent(go))
            .Where(go => !selected.Any(other =>
                other != go &&
                !EditorUtility.IsPersistent(other) &&
                go.transform.IsChildOf(other.transform)))
            .ToArray();

        var sceneObjects = selected.Where(go => !EditorUtility.IsPersistent(go)).ToArray();
        if (includeChildren)
            sceneObjects = sceneObjects.Where(go => !sceneObjects.Any(other =>
                other != go && go.transform.IsChildOf(other.transform))).ToArray();

        var prefabPaths = new HashSet<string>();
        int skipped = 0;

        if (randomMaterial)
        {
            foreach (var go in selected.Where(EditorUtility.IsPersistent))
            {
                string path = AssetDatabase.GetAssetPath(go);
                if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                    PrefabUtility.IsPartOfImmutablePrefab(go) || !AssetDatabase.IsOpenForEdit(path))
                {
                    skipped++;
                    continue;
                }
                prefabPaths.Add(path);
            }
        }

        if (prefabPaths.Count > 0 && !EditorUtility.DisplayDialog("保存 Prefab 材质修改",
                $"将直接修改并保存 {prefabPaths.Count} 个 Project Prefab：\n" +
                string.Join("\n", prefabPaths) + "\n\n这些资源修改不支持 Ctrl+Z。是否继续？", "继续", "取消"))
            return;

        var random = new System.Random();
        int changedRenderers = 0;
        int changedTransforms = 0;
        int failures = 0;

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("随机处理对象");

        try
        {
            if (randomScale || randomRotation || randomPosition)
            {
                foreach (var go in transformTargets)
                {
                    try
                    {
                        if (ApplyRandomTransform(go.transform, random))
                            changedTransforms++;
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        Debug.LogException(exception, go);
                    }
                }
            }

            if (randomMaterial)
            {
                foreach (var go in sceneObjects)
                {
                    try { changedRenderers += Assign(go, pool[random.Next(pool.Length)], true); }
                    catch (Exception exception) { failures++; Debug.LogException(exception, go); }
                }

                foreach (var path in prefabPaths)
                {
                    GameObject root = null;
                    try
                    {
                        root = PrefabUtility.LoadPrefabContents(path);
                        int count = Assign(root, pool[random.Next(pool.Length)], false);
                        if (count > 0)
                        {
                            PrefabUtility.SaveAsPrefabAsset(root, path, out bool success);
                            if (!success) throw new InvalidOperationException("Prefab 保存失败：" + path);
                            changedRenderers += count;
                        }
                    }
                    catch (Exception exception) { failures++; Debug.LogException(exception); }
                    finally { if (root != null) PrefabUtility.UnloadPrefabContents(root); }
                }
            }
        }
        finally { Undo.CollapseUndoOperations(undoGroup); }

        result = $"修改 {changedTransforms} 个 Transform；修改 {changedRenderers} 个 Renderer；" +
                 $"跳过 {skipped} 个不可编辑资源；失败 {failures} 个目标。";
        SceneView.RepaintAll();
    }

    private bool ApplyRandomTransform(Transform target, System.Random random)
    {
        bool willChange =
            randomScale ||
            (randomRotation && (rotationX || rotationY || rotationZ)) ||
            (randomPosition && (positionX || positionY || positionZ));

        if (!willChange) return false;

        Undo.RecordObject(target, "随机处理 Transform");

        if (randomScale)
        {
            float factor = RandomRange(random, scaleMin, scaleMax);
            target.localScale *= factor;
        }

        if (randomRotation)
        {
            Vector3 euler = target.localEulerAngles;
            if (rotationX) euler.x += RandomRange(random, rotationXRange.x, rotationXRange.y);
            if (rotationY) euler.y += RandomRange(random, rotationYRange.x, rotationYRange.y);
            if (rotationZ) euler.z += RandomRange(random, rotationZRange.x, rotationZRange.y);
            target.localEulerAngles = euler;
        }

        if (randomPosition)
        {
            Vector3 position = target.localPosition;
            if (positionX) position.x += RandomRange(random, positionXRange.x, positionXRange.y);
            if (positionY) position.y += RandomRange(random, positionYRange.x, positionYRange.y);
            if (positionZ) position.z += RandomRange(random, positionZRange.x, positionZRange.y);
            target.localPosition = position;
        }

        if (PrefabUtility.IsPartOfPrefabInstance(target))
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        EditorUtility.SetDirty(target);
        return true;
    }

    private static float RandomRange(System.Random random, float a, float b)
    {
        float min = Mathf.Min(a, b);
        float max = Mathf.Max(a, b);
        if (Mathf.Approximately(min, max)) return min;
        return min + (float)random.NextDouble() * (max - min);
    }

    private int Assign(GameObject root, Material material, bool recordUndo)
    {
        var renderers = includeChildren ? root.GetComponentsInChildren<Renderer>(true) : root.GetComponents<Renderer>();
        int changed = 0;
        foreach (var renderer in renderers)
        {
            var slots = renderer.sharedMaterials;
            if (materialSlot >= slots.Length || slots[materialSlot] == material) continue;
            if (recordUndo) Undo.RecordObject(renderer, "随机赋予材质");
            slots[materialSlot] = material;
            renderer.sharedMaterials = slots;
            if (PrefabUtility.IsPartOfPrefabInstance(renderer))
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            if (recordUndo) EditorUtility.SetDirty(renderer);
            changed++;
        }
        return changed;
    }
}
