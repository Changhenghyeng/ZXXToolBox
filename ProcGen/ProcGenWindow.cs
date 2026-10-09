using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using System.Text;

namespace ProcGen
{
    public class ProcGenWindow : EditorWindow
    {
        private ProcGenSettings settings = new ProcGenSettings();

        private static string SettingsPath
        {
            get
            {
                // 脚本同目录
                var guids = UnityEditor.AssetDatabase.FindAssets("t:MonoScript ProcGenWindow");
                if (guids.Length == 0) return "Assets/ProcGenSettings.asset";
                var scriptPath = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                return System.IO.Path.GetDirectoryName(scriptPath).Replace('\\', '/') + "/ProcGenSettings.asset";
            }
        }
        private ReorderableList slotList;
        private Vector2 scroll;
        private bool debugFoldout = true;

        public static void Open()
        {
            GetWindow<ProcGenWindow>("ProcGen");
        }

        private void OnEnable()
        {
            LoadSettings();
            BuildList();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void LoadSettings()
        {
            // 按类型查找，任意位置都能找到
            var guids = UnityEditor.AssetDatabase.FindAssets("t:ProcGenSettingsAsset");
            if (guids.Length > 0)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<ProcGenSettingsAsset>(path);
                if (asset != null && asset.settings != null)
                {
                    settings = asset.settings;
                    RestoreAvoidanceObjects();
                    return;
                }
            }
            settings = new ProcGenSettings();
            RestoreAvoidanceObjects();
        }

        private void SaveSettings()
        {
            SyncAvoidanceObjectIds();

            var guids = UnityEditor.AssetDatabase.FindAssets("t:ProcGenSettingsAsset");
            ProcGenSettingsAsset asset = null;
            if (guids.Length > 0)
                asset = UnityEditor.AssetDatabase.LoadAssetAtPath<ProcGenSettingsAsset>(UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));

            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<ProcGenSettingsAsset>();
                asset.settings = settings;
                UnityEditor.AssetDatabase.CreateAsset(asset, SettingsPath);
            }
            else
            {
                asset.settings = settings;
                UnityEditor.EditorUtility.SetDirty(asset);
            }
            UnityEditor.AssetDatabase.SaveAssets();
        }

        private void RestoreAvoidanceObjects()
        {
            settings.avoidanceObjects = new System.Collections.Generic.List<GameObject>();
            if (settings.avoidanceObjectGlobalIds == null)
                settings.avoidanceObjectGlobalIds = new System.Collections.Generic.List<string>();

            foreach (string value in settings.avoidanceObjectGlobalIds)
            {
                if (string.IsNullOrEmpty(value)) continue;
                if (!GlobalObjectId.TryParse(value, out GlobalObjectId id)) continue;

                var go = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
                if (go != null && go.scene.IsValid() && !settings.avoidanceObjects.Contains(go))
                    settings.avoidanceObjects.Add(go);
            }
        }

        private void SyncAvoidanceObjectIds()
        {
            if (settings.avoidanceObjectGlobalIds == null)
                settings.avoidanceObjectGlobalIds = new System.Collections.Generic.List<string>();
            else
                settings.avoidanceObjectGlobalIds.Clear();

            if (settings.avoidanceObjects == null) return;

            foreach (var go in settings.avoidanceObjects)
            {
                if (go == null || !go.scene.IsValid()) continue;
                string value = GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
                if (!settings.avoidanceObjectGlobalIds.Contains(value))
                    settings.avoidanceObjectGlobalIds.Add(value);
            }
        }

        private void BuildList()
        {
            slotList = new ReorderableList(settings.slots, typeof(ProcGenSlot), true, true, true, true);

            slotList.drawHeaderCallback = rect =>
            {
                EditorGUI.LabelField(rect, "Prefab Slots (generated in descending priority; higher priority overwrites lower)");
            };

            // 单行高度（含间距）
            float lh = EditorGUIUtility.singleLineHeight + 2f;

            slotList.elementHeightCallback = idx =>
            {
                var slot = settings.slots[idx];
                if (!slot.foldout) return lh + 6f;
                int rows = CountRows(slot);
                return lh * rows + 10f;
            };

            slotList.drawElementCallback = (rect, idx, active, focused) =>
            {
                var slot = settings.slots[idx];
                float y = rect.y + 2f;

                // foldout header (左边一个开关 + foldout)
                var toggleRect = new Rect(rect.x, y, 20, EditorGUIUtility.singleLineHeight);
                slot.enabled = EditorGUI.Toggle(toggleRect, slot.enabled);

                var headerRect = new Rect(rect.x + 20, y, rect.width - 20, EditorGUIUtility.singleLineHeight);
                string prefix = slot.enabled ? "" : "[Disabled] ";
                string prefabInfo = "  (none)";
                int validCount = 0;
                GameObject firstPrefab = null;

                foreach (var e in slot.prefabEntries)
                {
                    if (e != null && e.prefab != null)
                    {
                        if (firstPrefab == null) firstPrefab = e.prefab;
                        validCount++;
                    }
                }

                if (firstPrefab != null)
                {
                    if (validCount == 1) prefabInfo = $"  ({firstPrefab.name})";
                    else prefabInfo = $"  ({firstPrefab.name} +{validCount - 1})";
                }

                slot.foldout = EditorGUI.Foldout(headerRect, slot.foldout,
                    $"{prefix}[{slot.priority}] {slot.name}{prefabInfo}",
                    true);

                y += lh;
                if (!slot.foldout) return;

                // 禁用时整块字段变灰
                bool prevEnabled = GUI.enabled;
                GUI.enabled = prevEnabled && slot.enabled;

                Rect Row() {
                    var r = new Rect(rect.x + 4, y, rect.width - 4, EditorGUIUtility.singleLineHeight);
                    y += lh;
                    return r;
                }

                slot.name = EditorGUI.TextField(Row(), Content("Name", "插槽名称").text, slot.name);

                // Prefab 列表 (object | weight | -)
                float prefabDropTop = y;
                var prefabDropRect = new Rect(rect.x + 4f, prefabDropTop, rect.width - 4f,
                    lh * (slot.prefabEntries.Count + 2)); // 标题 + 条目 + Add 按钮
                // 必须先处理拖放事件，避免单个 ObjectField 抢先只接收一个 Prefab。
                HandlePrefabDrop(prefabDropRect, slot);
                EditorGUI.LabelField(Row(), $"Prefabs ({slot.prefabEntries.Count})  —  Drop Multiple Here");
                int removeIdx = -1;

                for (int pi = 0; pi < slot.prefabEntries.Count; pi++)
                {
                    var entry = slot.prefabEntries[pi];
                    if (entry == null)
                    {
                        slot.prefabEntries[pi] = entry = new PrefabEntry();
                    }

                    var r0 = Row();
                    float btnW = 22f;
                    float weightW = 60f;
                    var objRect = new Rect(r0.x + 14, r0.y, r0.width - btnW - weightW - 18, r0.height);
                    var wRect = new Rect(objRect.xMax + 4, r0.y, weightW, r0.height);
                    var bRect = new Rect(wRect.xMax + 4, r0.y, btnW, r0.height);

                    entry.prefab = (GameObject)EditorGUI.ObjectField(objRect, entry.prefab, typeof(GameObject), false);
                    entry.weight = Mathf.Max(0f, EditorGUI.FloatField(wRect, entry.weight));
                    if (GUI.Button(bRect, "-")) removeIdx = pi;
                }

                if (removeIdx >= 0) slot.prefabEntries.RemoveAt(removeIdx);

                var addRow = Row();
                var addBtnRect = new Rect(addRow.x + 14, addRow.y, addRow.width - 14, addRow.height);
                if (GUI.Button(addBtnRect, "+ Add Prefab"))
                {
                    slot.prefabEntries.Add(new PrefabEntry());
                }

                slot.count = Mathf.Max(0, EditorGUI.IntField(Row(), Content("Count", "生成数量").text, slot.count));
                slot.scaleMin = EditorGUI.FloatField(Row(), Content("Scale Min", "缩放最小值").text, slot.scaleMin);
                slot.scaleMax = EditorGUI.FloatField(Row(), Content("Scale Max", "缩放最大值").text, slot.scaleMax);
                slot.minDistance = Mathf.Max(0f, EditorGUI.FloatField(Row(), Content("Min Distance (m)", "实例之间的最小间距").text, slot.minDistance));
                slot.priority = EditorGUI.IntField(Row(), Content("Priority", "优先级，数值越大越优先").text, slot.priority);
                slot.canBeDestroyed = EditorGUI.Toggle(Row(), Content("Can Be Destroyed", "允许被更高优先级的物体覆盖销毁"), slot.canBeDestroyed);
                slot.yRotation = EditorGUI.FloatField(Row(), Content("Y Rotation (°)", "绕 Y 轴旋转角度"), slot.yRotation);
                slot.positionOffset.x = EditorGUI.FloatField(Row(), Content("Offset X", "位置偏移 X"), slot.positionOffset.x);
                slot.positionOffset.y = EditorGUI.FloatField(Row(), Content("Offset Y", "位置偏移 Y"), slot.positionOffset.y);
                slot.positionOffset.z = EditorGUI.FloatField(Row(), Content("Offset Z", "位置偏移 Z"), slot.positionOffset.z);
                slot.overrideMaterial = (Material)EditorGUI.ObjectField(Row(), Content("Override Material", "覆盖材质，留空则使用 Prefab 自带材质"), slot.overrideMaterial, typeof(Material), false);
                slot.boundsMode = (BoundsMode)EditorGUI.EnumPopup(Row(), Content("Bounds Mode", "碰撞盒模式：Auto 自动 / Custom 自定义 / SelectRenderers 按子 Renderer"), slot.boundsMode);

                if (slot.boundsMode == BoundsMode.Custom)
                {
                    slot.customHalfExtents.x = EditorGUI.FloatField(Row(), Content("  Box Half X", "自定义碰撞盒半宽 X"), slot.customHalfExtents.x);
                    slot.customHalfExtents.y = EditorGUI.FloatField(Row(), Content("  Box Half Y", "自定义碰撞盒半高 Y"), slot.customHalfExtents.y);
                    slot.customHalfExtents.z = EditorGUI.FloatField(Row(), Content("  Box Half Z", "自定义碰撞盒半深 Z"), slot.customHalfExtents.z);
                }
                else if (slot.boundsMode == BoundsMode.SelectRenderers)
                {
                    // 找出唯一 prefab (此模式只在恰好一个有效 prefab 时生效)
                    GameObject solePrefab = null;
                    int prefabCount = 0;

                    foreach (var e in slot.prefabEntries)
                    {
                        if (e != null && e.prefab != null)
                        {
                            solePrefab = e.prefab;
                            prefabCount++;
                        }
                    }

                    if (prefabCount == 0)
                    {
                        EditorGUI.LabelField(Row(), "  Add a Prefab first");
                    }
                    else if (prefabCount > 1)
                    {
                        EditorGUI.LabelField(Row(), "  Falls back to Auto with multiple Prefabs");
                    }
                    else
                    {
                        // 折叠列表头
                        var headerRow = Row();
                        var foldoutRect = new Rect(headerRow.x, headerRow.y, headerRow.width - 130, headerRow.height);
                        slot.rendererListFoldout = EditorGUI.Foldout(foldoutRect, slot.rendererListFoldout,
                            $"  Child Renderers ({slot.selectedRendererPaths.Count} selected)", true);

                        var btnAllRect = new Rect(headerRow.xMax - 125, headerRow.y, 60, headerRow.height);
                        var btnNoneRect = new Rect(headerRow.xMax - 60, headerRow.y, 60, headerRow.height);

                        if (GUI.Button(btnAllRect, "All"))
                        {
                            slot.selectedRendererPaths.Clear();
                            foreach (var path in EnumerateRendererPaths(solePrefab))
                                slot.selectedRendererPaths.Add(path);
                        }

                        if (GUI.Button(btnNoneRect, "None"))
                        {
                            slot.selectedRendererPaths.Clear();
                        }

                        if (slot.rendererListFoldout)
                        {
                            foreach (var path in EnumerateRendererPaths(solePrefab))
                            {
                                bool selected = slot.selectedRendererPaths.Contains(path);
                                bool now = EditorGUI.ToggleLeft(Row(), "    " + path, selected);
                                if (now != selected)
                                {
                                    if (now) slot.selectedRendererPaths.Add(path);
                                    else slot.selectedRendererPaths.Remove(path);
                                }
                            }
                        }
                    }
                }

                var groupHeader = Row();
                slot.groupComposerFoldout = EditorGUI.Foldout(groupHeader, slot.groupComposerFoldout,
                    "Group Composer (create at world origin)", true);

                if (slot.groupComposerFoldout)
                {
                    slot.groupParentName = EditorGUI.TextField(Row(),
                        Content("  Parent Name", "确认生成后的父节点名称；留空时使用 Slot 名称"), slot.groupParentName);
                    slot.groupObjectCount = Mathf.Max(1, EditorGUI.IntField(Row(),
                        Content("  Object Count", "一个组群中随机生成多少个物体"), slot.groupObjectCount));
                    slot.groupRadius = Mathf.Max(0f, EditorGUI.FloatField(Row(),
                        Content("  Radius (m)", "组群在 XZ 平面的随机分布半径"), slot.groupRadius));
                    slot.groupMinSpacing = Mathf.Max(0f, EditorGUI.FloatField(Row(),
                        Content("  Min Spacing (m)", "组群内物体中心之间的最小距离，0 表示不限制"), slot.groupMinSpacing));
                    slot.groupSeparateAxisScale = EditorGUI.Toggle(Row(),
                        Content("  Separate XYZ Scale", "开启后分别设置三个轴；关闭时使用统一缩放系数"), slot.groupSeparateAxisScale);

                    if (slot.groupSeparateAxisScale)
                    {
                        slot.groupScaleXMin = EditorGUI.FloatField(Row(),
                            Content("    Scale X Min", "X 轴随机缩放最小值"), slot.groupScaleXMin);
                        slot.groupScaleXMax = EditorGUI.FloatField(Row(),
                            Content("    Scale X Max", "X 轴随机缩放最大值"), slot.groupScaleXMax);
                        slot.groupScaleYMin = EditorGUI.FloatField(Row(),
                            Content("    Scale Y Min", "Y 轴随机缩放最小值"), slot.groupScaleYMin);
                        slot.groupScaleYMax = EditorGUI.FloatField(Row(),
                            Content("    Scale Y Max", "Y 轴随机缩放最大值"), slot.groupScaleYMax);
                        slot.groupScaleZMin = EditorGUI.FloatField(Row(),
                            Content("    Scale Z Min", "Z 轴随机缩放最小值"), slot.groupScaleZMin);
                        slot.groupScaleZMax = EditorGUI.FloatField(Row(),
                            Content("    Scale Z Max", "Z 轴随机缩放最大值"), slot.groupScaleZMax);
                    }
                    else
                    {
                        slot.groupScaleMin = EditorGUI.FloatField(Row(),
                            Content("  Scale Min", "组群内物体统一随机缩放系数最小值"), slot.groupScaleMin);
                        slot.groupScaleMax = EditorGUI.FloatField(Row(),
                            Content("  Scale Max", "组群内物体统一随机缩放系数最大值"), slot.groupScaleMax);
                    }
                    slot.groupRotationMin = EditorGUI.FloatField(Row(),
                        Content("  Y Rotation Min", "组群内物体随机 Y 旋转最小角度"), slot.groupRotationMin);
                    slot.groupRotationMax = EditorGUI.FloatField(Row(),
                        Content("  Y Rotation Max", "组群内物体随机 Y 旋转最大角度"), slot.groupRotationMax);

                    var seedRow = Row();
                    var seedFieldRect = new Rect(seedRow.x, seedRow.y, seedRow.width - 68f, seedRow.height);
                    var seedButtonRect = new Rect(seedRow.xMax - 64f, seedRow.y, 64f, seedRow.height);
                    slot.groupSeed = EditorGUI.IntField(seedFieldRect,
                        Content("  Group Seed", "相同参数和种子会生成相同的组群"), slot.groupSeed);
                    if (GUI.Button(seedButtonRect, Content("Random", "生成新种子并立即刷新组群预览")))
                    {
                        slot.groupSeed = Random.Range(int.MinValue, int.MaxValue);
                        SaveSettings();
                        ProcGenerator.PreviewGroupAtOrigin(slot);
                    }

                    var flipRow = Row();
                    float flipLabelWidth = Mathf.Min(EditorGUIUtility.labelWidth, flipRow.width * 0.46f);
                    EditorGUI.LabelField(new Rect(flipRow.x, flipRow.y, flipLabelWidth, flipRow.height),
                        Content("  Random Axis Flip", "勾选的轴会让每个组群物体以 50% 概率随机翻转"));
                    float flipWidth = (flipRow.width - flipLabelWidth) / 3f;
                    slot.groupRandomFlipX = EditorGUI.ToggleLeft(
                        new Rect(flipRow.x + flipLabelWidth, flipRow.y, flipWidth, flipRow.height), "X", slot.groupRandomFlipX);
                    slot.groupRandomFlipY = EditorGUI.ToggleLeft(
                        new Rect(flipRow.x + flipLabelWidth + flipWidth, flipRow.y, flipWidth, flipRow.height), "Y", slot.groupRandomFlipY);
                    slot.groupRandomFlipZ = EditorGUI.ToggleLeft(
                        new Rect(flipRow.x + flipLabelWidth + flipWidth * 2f, flipRow.y, flipWidth, flipRow.height), "Z", slot.groupRandomFlipZ);

                    var fixedFlipRow = Row();
                    EditorGUI.LabelField(new Rect(fixedFlipRow.x, fixedFlipRow.y, flipLabelWidth, fixedFlipRow.height),
                        Content("  Fixed Axis Flip", "勾选的轴会让组群内所有物体始终翻转，并优先于该轴的随机翻转"));
                    slot.groupFixedFlipX = EditorGUI.ToggleLeft(
                        new Rect(fixedFlipRow.x + flipLabelWidth, fixedFlipRow.y, flipWidth, fixedFlipRow.height), "X", slot.groupFixedFlipX);
                    slot.groupFixedFlipY = EditorGUI.ToggleLeft(
                        new Rect(fixedFlipRow.x + flipLabelWidth + flipWidth, fixedFlipRow.y, flipWidth, fixedFlipRow.height), "Y", slot.groupFixedFlipY);
                    slot.groupFixedFlipZ = EditorGUI.ToggleLeft(
                        new Rect(fixedFlipRow.x + flipLabelWidth + flipWidth * 2f, fixedFlipRow.y, flipWidth, fixedFlipRow.height), "Z", slot.groupFixedFlipZ);

                    var createRow = Row();
                    float gap = 4f;
                    float buttonWidth = (createRow.width - gap * 2f) / 3f;
                    var previewRect = new Rect(createRow.x, createRow.y, buttonWidth, createRow.height);
                    var confirmRect = new Rect(previewRect.xMax + gap, createRow.y, buttonWidth, createRow.height);
                    var cancelRect = new Rect(confirmRect.xMax + gap, createRow.y, buttonWidth, createRow.height);

                    if (GUI.Button(previewRect, Content("Preview Group", "在世界原点生成临时预览；再次点击会替换旧预览")))
                    {
                        SaveSettings();
                        ProcGenerator.PreviewGroupAtOrigin(slot);
                    }
                    if (GUI.Button(confirmRect, Content("Confirm Generate", "把当前预览正式保留在场景中")))
                        ProcGenerator.ConfirmGroupPreview();
                    if (GUI.Button(cancelRect, Content("Cancel Preview", "清除当前临时预览")))
                        ProcGenerator.CancelGroupPreview();
                }

                GUI.enabled = prevEnabled;
            };

            slotList.onAddCallback = list =>
            {
                var s = new ProcGenSlot { name = $"Slot {settings.slots.Count + 1}" };
                s.prefabEntries.Add(new PrefabEntry());
                settings.slots.Add(s);
            };
        }

        // 每个插槽展开后的行数（含 header 行）。和 drawElementCallback 必须严格对应
        private static int CountRows(ProcGenSlot slot)
        {
            // 固定: header + name + prefab label + (+ 添加按钮) + count + scaleMin + scaleMax
            //   + minDist + priority + 允许被销毁 + yRot + offX + offY + offZ + material + boundsMode = 16
            // 加上每个 prefab entry 一行
            int rows = 17 + slot.prefabEntries.Count; // + 组群制作折叠头

            if (slot.groupComposerFoldout)
                // 固定 13 行；独立 XYZ 模式用 6 个缩放字段替代统一的 2 个，因此再加 4 行。
                rows += 13 + (slot.groupSeparateAxisScale ? 4 : 0);

            if (slot.boundsMode == BoundsMode.Custom)
            {
                rows += 3; // boxX boxY boxZ
            }
            else if (slot.boundsMode == BoundsMode.SelectRenderers)
            {
                // 算有效 prefab 数
                GameObject solePrefab = null;
                int prefabCount = 0;

                foreach (var e in slot.prefabEntries)
                {
                    if (e != null && e.prefab != null)
                    {
                        solePrefab = e.prefab;
                        prefabCount++;
                    }
                }

                if (prefabCount == 0)
                {
                    rows += 1; // "请先添加一个 Prefab"
                }
                else if (prefabCount > 1)
                {
                    rows += 1; // "多 Prefab 时退化为 Auto"
                }
                else
                {
                    rows += 1; // 折叠头
                    if (slot.rendererListFoldout)
                    {
                        int count = 0;
                        foreach (var _ in EnumerateRendererPaths(solePrefab)) count++;
                        rows += count;
                    }
                }
            }

            return rows;
        }

        // 列出 prefab 里所有带 Renderer 的子物体相对路径
        private static System.Collections.Generic.IEnumerable<string> EnumerateRendererPaths(GameObject prefab)
        {
            if (prefab == null) yield break;
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            var root = prefab.transform;

            foreach (var r in renderers)
            {
                Transform t = r.transform;
                if (t == root)
                {
                    yield return "(self)";
                    continue;
                }

                var sb = new StringBuilder();
                while (t != null && t != root)
                {
                    if (sb.Length > 0) sb.Insert(0, "/");
                    sb.Insert(0, t.name);
                    t = t.parent;
                }

                yield return sb.ToString();
            }
        }

        private static void HandlePrefabDrop(Rect dropRect, ProcGenSlot slot)
        {
            Event evt = Event.current;
            if (!dropRect.Contains(evt.mousePosition)) return;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;

            bool hasValidPrefab = false;
            foreach (var obj in DragAndDrop.objectReferences)
            {
                var go = obj as GameObject;
                if (go != null && EditorUtility.IsPersistent(go)
                    && PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.NotAPrefab)
                {
                    hasValidPrefab = true;
                    break;
                }
            }

            DragAndDrop.visualMode = hasValidPrefab ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (evt.type == EventType.DragUpdated)
            {
                evt.Use();
                return;
            }

            if (hasValidPrefab)
            {
                DragAndDrop.AcceptDrag();
                if (slot.prefabEntries == null)
                    slot.prefabEntries = new System.Collections.Generic.List<PrefabEntry>();

                foreach (var obj in DragAndDrop.objectReferences)
                {
                    var go = obj as GameObject;
                    if (go == null || !EditorUtility.IsPersistent(go)
                        || PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab)
                        continue;

                    bool duplicate = false;
                    foreach (var entry in slot.prefabEntries)
                    {
                        if (entry != null && entry.prefab == go)
                        {
                            duplicate = true;
                            break;
                        }
                    }

                    if (!duplicate)
                        slot.prefabEntries.Add(new PrefabEntry { prefab = go, weight = 1f });
                }
            }

            evt.Use();
        }

        private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField(Content("Target", "生成目标"), EditorStyles.boldLabel);
            settings.target = (GameObject)EditorGUILayout.ObjectField(
                Content("Target Mesh/Prefab", "目标 Mesh 或 Prefab，在其表面生成预制体"), settings.target, typeof(GameObject), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Content("Global Settings", "全局设置"), EditorStyles.boldLabel);

            settings.cellSize = EditorGUILayout.FloatField(new GUIContent("Grid Cell (m)", "网格单元大小（1 单位 = 1.5 米）"), settings.cellSize);
            settings.jitter = EditorGUILayout.Slider(new GUIContent("Position Jitter", "候选点偏离网格中心的程度。0 = 严格网格中心，1 = 格子内任意位置"), settings.jitter, 0f, 1f);
            settings.spreadBias = EditorGUILayout.Slider(new GUIContent("Spread Bias", "已放置点对新点位置的影响。0 = 均匀散开，0.5 = 纯随机，1 = 抱团聚集"), settings.spreadBias, 0f, 1f);
            settings.upNormalThreshold = EditorGUILayout.Slider(Content("Up Threshold", "面法线朝上程度的阈值，低于此值的面不会生成"), settings.upNormalThreshold, 0.5f, 1f);
            settings.boundaryInset = EditorGUILayout.FloatField(new GUIContent("Boundary Inset (m)", "距 mesh 边缘多近内不生成。建议填建筑半径"), settings.boundaryInset);

            EditorGUILayout.BeginHorizontal();
            settings.seed = EditorGUILayout.IntField(Content("Random Seed", "随机种子，相同种子产生相同结果"), settings.seed);
            if (GUILayout.Button(Content("Random", "生成随机种子"), GUILayout.Width(60)))
            {
                settings.seed = Random.Range(int.MinValue, int.MaxValue);
            }
            EditorGUILayout.EndHorizontal();

            settings.maxRetriesPerPoint = EditorGUILayout.IntField(Content("Max Retries Per Point", "每个候选点放置失败后的最大重试次数"), settings.maxRetriesPerPoint);

            DrawAvoidanceObjects();

            // ========== 调试设置面板 ==========
            EditorGUILayout.Space();
            debugFoldout = EditorGUILayout.Foldout(debugFoldout, Content("Debug Visualization", "调试可视化设置"), true, EditorStyles.boldLabel);
            if (debugFoldout)
            {
                EditorGUI.indentLevel++;
                settings.showDebugGizmos = EditorGUILayout.Toggle(Content("Enable Debug Mode", "启用调试模式，在 Scene 视图中显示 Gizmos"), settings.showDebugGizmos);

                using (new EditorGUI.DisabledScope(!settings.showDebugGizmos))
                {
                    settings.showCandidatePoints = EditorGUILayout.Toggle(Content("Show Candidate Points", "显示候选生成点"), settings.showCandidatePoints);
                    settings.showCollisionBoxes = EditorGUILayout.Toggle(Content("Show Collision Boxes", "显示碰撞盒"), settings.showCollisionBoxes);
                    settings.showAvoidanceBounds = EditorGUILayout.Toggle(Content("Show Avoidance Bounds", "显示避让范围"), settings.showAvoidanceBounds);
                    settings.showDestroyedObjects = EditorGUILayout.Toggle(Content("Show Destroyed Objects", "显示被销毁的物体"), settings.showDestroyedObjects);

                    EditorGUILayout.Space();
                    settings.candidatePointColor = EditorGUILayout.ColorField(Content("Candidate Point Color", "候选点 Gizmo 颜色"), settings.candidatePointColor);
                    settings.collisionBoxColor = EditorGUILayout.ColorField(Content("Collision Box Color", "碰撞盒 Gizmo 颜色"), settings.collisionBoxColor);
                    settings.avoidanceColor = EditorGUILayout.ColorField(Content("Avoidance Color", "避让范围 Gizmo 颜色"), settings.avoidanceColor);
                    settings.destroyedColor = EditorGUILayout.ColorField(Content("Destroyed Color", "被销毁物体 Gizmo 颜色"), settings.destroyedColor);

                    EditorGUILayout.Space();
                    EditorGUILayout.BeginHorizontal();

                    GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
                    if (GUILayout.Button(Content("Show All Debug Info", "重新生成并显示所有调试信息"), GUILayout.Height(25)))
                    {
                        if (settings.target != null)
                        {
                            SaveSettings();
                            ProcGenerator.Generate(settings);
                            Debug.Log("[ProcGen] Regenerated with all debug info");
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("提示", "请先指定生成目标", "确定");
                        }
                    }

                    GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                    if (GUILayout.Button(Content("Clear All Debug Info", "清除所有调试信息"), GUILayout.Height(25)))
                    {
#if UNITY_EDITOR
                        ProcGenDebugManager.Instance.ClearAll();
                        Debug.Log("[ProcGen] Cleared all debug info");
#endif
                    }

                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
            }
            // ======================================

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button(Content("Generate", "执行程序化生成"), GUILayout.Height(30)))
            {
                SaveSettings();
                ProcGenerator.Generate(settings);
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            if (slotList == null) BuildList();
            slotList.DoLayoutList();

            EditorGUILayout.HelpBox(
                "工作流程：\n" +
                "1. 选定目标 Mesh（必须含 MeshFilter，只在 Y+ 朝上面生成）\n" +
                "2. 添加插槽 → 指定 prefab、数量、缩放、间距、旋转、偏移、材质\n" +
                "   可在 Project 一次多选 Prefab/FBX，只拖一次到整个 Prefabs 列表区域批量加入\n" +
                "3. 点击「生成」。每次生成创建独立的 [ProcGen Root] #N，可 Ctrl+Z 撤销\n\n" +
                "多次生成：\n" +
                "• 每次点击「生成」创建独立的 [ProcGen Root] #N，新物体会自动避让已有批次\n" +
                "• 在场景中手动删除对应 Root 即可移除某次生成结果\n\n" +
                "场景物体避让：\n" +
                "• 把需要避开的场景物体拖入列表；拖入父物体会自动逐个识别所有子物体\n" +
                "• 网格按实际三角面避让，弯曲河流/道路不会用一个大矩形挡住空地\n" +
                "• 非网格 Renderer 使用各自包围盒；没有 Renderer 时使用各子 Collider\n" +
                "• 场景避让物体始终受保护，不会被生成物覆盖或销毁\n\n" +
                "组群制作：\n" +
                "• 展开 Slot 内的 Group Composer，可用当前 Prefab 列表随机组合树群/石群\n" +
                "• Parent Name 可自定义确认生成后的父节点名称，留空时使用 Slot 名称\n" +
                "• Group Seed 相同时会得到相同组合，Random 会换种子并立即刷新预览\n" +
                "• Separate XYZ Scale 关闭时统一缩放，开启后可分别设置 X/Y/Z 随机范围\n" +
                "• Random Axis Flip 可分别控制 X/Y/Z 轴，每个物体有 50% 概率翻转\n" +
                "• Fixed Axis Flip 可分别控制 X/Y/Z 轴，让组群内所有物体固定翻转\n" +
                "• Preview Group 在世界原点生成临时预览；重复点击会刷新当前预览\n" +
                "• Confirm Generate 保留当前预览，Cancel Preview 清除预览\n" +
                "• 组群不会参与 Generate 散布；调整满意后可自行拖到 Project 保存成 Prefab\n\n" +
                "优先级规则：\n" +
                "• 数值越大优先级越高，高优先级物体生成时会自动销毁重叠的**允许被销毁**的低优先级物体\n" +
                "• 同优先级物体不会互相覆盖\n" +
                "• 关闭「允许被销毁」后，该插槽物体将被永久保护，不会被任何高优先级物体覆盖\n" +
                "• 已有批次的物体始终受保护，不会被新批次销毁\n\n" +
                "参数说明：\n" +
                "• 位置随机度：候选点在网格内的偏移量，0=严格中心，1=任意位置\n" +
                "• 散布偏好：物体之间的分布模式，0=均匀散开，0.5=纯随机，1=抱团聚集\n\n" +
                "调试说明：\n" +
                "• 调试信息使用Gizmos绘制，会永久显示在Scene视图中\n" +
                "• 确保Scene视图右上角的「Gizmos」开关是打开的\n" +
                "• 点击「清除所有调试信息」可一键清除所有线框\n\n" +
                "配置持久化：\n" +
                "• 配置自动保存，关闭窗口后不会丢失",
                MessageType.Info);

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

        private void DrawAvoidanceObjects()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(Content("Scene Avoidance", "指定生成时必须避开的场景物体"), EditorStyles.boldLabel);

            if (settings.avoidanceObjects == null)
                settings.avoidanceObjects = new System.Collections.Generic.List<GameObject>();

            int removeIndex = -1;
            for (int i = 0; i < settings.avoidanceObjects.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                settings.avoidanceObjects[i] = (GameObject)EditorGUILayout.ObjectField(
                    Content($"Avoid Object {i + 1}", "该物体及其所有子物体的 Renderer/Collider 范围都不会生成"),
                    settings.avoidanceObjects[i], typeof(GameObject), true);
                if (GUILayout.Button("-", GUILayout.Width(24f)))
                    removeIndex = i;
                EditorGUILayout.EndHorizontal();
            }

            if (removeIndex >= 0)
                settings.avoidanceObjects.RemoveAt(removeIndex);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(Content("+ Add", "添加一个避让物体槽位")))
                settings.avoidanceObjects.Add(null);

            if (GUILayout.Button(Content("Add Selection", "把 Hierarchy 当前选中的场景物体加入避让列表")))
            {
                foreach (var go in Selection.gameObjects)
                {
                    if (go != null && go.scene.IsValid() && !settings.avoidanceObjects.Contains(go))
                        settings.avoidanceObjects.Add(go);
                }
            }

            using (new EditorGUI.DisabledScope(settings.avoidanceObjects.Count == 0))
            {
                if (GUILayout.Button("Clear", GUILayout.Width(52f)))
                    settings.avoidanceObjects.Clear();
            }
            EditorGUILayout.EndHorizontal();

            settings.avoidancePadding = Mathf.Max(0f, EditorGUILayout.FloatField(
                Content("Extra Padding (m)", "在避让物体包围盒外额外保留的距离"), settings.avoidancePadding));
        }
    }
}
