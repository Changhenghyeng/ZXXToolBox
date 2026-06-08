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
                    return;
                }
            }
            settings = new ProcGenSettings();
        }

        private void SaveSettings()
        {
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

        private void BuildList()
        {
            slotList = new ReorderableList(settings.slots, typeof(ProcGenSlot), true, true, true, true);

            slotList.drawHeaderCallback = rect =>
            {
                EditorGUI.LabelField(rect, "预制体插槽 (按优先级降序生成，高优先级覆盖低优先级)");
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
                string prefix = slot.enabled ? "" : "[禁用] ";
                string prefabInfo = "  (未指定)";
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

                slot.name = EditorGUI.TextField(Row(), "名称", slot.name);

                // Prefab 列表 (object | weight | -)
                EditorGUI.LabelField(Row(), $"Prefabs ({slot.prefabEntries.Count})");
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
                if (GUI.Button(addBtnRect, "+ 添加预制体"))
                {
                    slot.prefabEntries.Add(new PrefabEntry());
                }

                slot.count = Mathf.Max(0, EditorGUI.IntField(Row(), "数量", slot.count));
                slot.scaleMin = EditorGUI.FloatField(Row(), "缩放最小", slot.scaleMin);
                slot.scaleMax = EditorGUI.FloatField(Row(), "缩放最大", slot.scaleMax);
                slot.minDistance = Mathf.Max(0f, EditorGUI.FloatField(Row(), "最小间距 (m)", slot.minDistance));
                slot.priority = EditorGUI.IntField(Row(), "优先级", slot.priority);
                slot.canBeDestroyed = EditorGUI.Toggle(Row(), "允许被销毁", slot.canBeDestroyed);
                slot.yRotation = EditorGUI.FloatField(Row(), "Y 旋转 (°)", slot.yRotation);
                slot.positionOffset.x = EditorGUI.FloatField(Row(), "位置偏移 X", slot.positionOffset.x);
                slot.positionOffset.y = EditorGUI.FloatField(Row(), "位置偏移 Y", slot.positionOffset.y);
                slot.positionOffset.z = EditorGUI.FloatField(Row(), "位置偏移 Z", slot.positionOffset.z);
                slot.overrideMaterial = (Material)EditorGUI.ObjectField(Row(), "覆盖材质", slot.overrideMaterial, typeof(Material), false);
                slot.boundsMode = (BoundsMode)EditorGUI.EnumPopup(Row(), "碰撞盒模式", slot.boundsMode);

                if (slot.boundsMode == BoundsMode.Custom)
                {
                    slot.customHalfExtents.x = EditorGUI.FloatField(Row(), "  Box 半宽 X", slot.customHalfExtents.x);
                    slot.customHalfExtents.y = EditorGUI.FloatField(Row(), "  Box 半高 Y", slot.customHalfExtents.y);
                    slot.customHalfExtents.z = EditorGUI.FloatField(Row(), "  Box 半深 Z", slot.customHalfExtents.z);
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
                        EditorGUI.LabelField(Row(), "  请先添加一个 Prefab");
                    }
                    else if (prefabCount > 1)
                    {
                        EditorGUI.LabelField(Row(), "  多 Prefab 时此模式退化为 Auto");
                    }
                    else
                    {
                        // 折叠列表头
                        var headerRow = Row();
                        var foldoutRect = new Rect(headerRow.x, headerRow.y, headerRow.width - 130, headerRow.height);
                        slot.rendererListFoldout = EditorGUI.Foldout(foldoutRect, slot.rendererListFoldout,
                            $"  子 Renderer ({slot.selectedRendererPaths.Count} 已选)", true);

                        var btnAllRect = new Rect(headerRow.xMax - 125, headerRow.y, 60, headerRow.height);
                        var btnNoneRect = new Rect(headerRow.xMax - 60, headerRow.y, 60, headerRow.height);

                        if (GUI.Button(btnAllRect, "全选"))
                        {
                            slot.selectedRendererPaths.Clear();
                            foreach (var path in EnumerateRendererPaths(solePrefab))
                                slot.selectedRendererPaths.Add(path);
                        }

                        if (GUI.Button(btnNoneRect, "全不选"))
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
            int rows = 16 + slot.prefabEntries.Count;

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

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            EditorGUILayout.LabelField("目标", EditorStyles.boldLabel);
            settings.target = (GameObject)EditorGUILayout.ObjectField("目标 Mesh/Prefab", settings.target, typeof(GameObject), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("全局设置", EditorStyles.boldLabel);

            settings.cellSize = EditorGUILayout.FloatField(new GUIContent("网格单元 (m)", "1 单位 = 1.5 米"), settings.cellSize);
            settings.jitter = EditorGUILayout.Slider(new GUIContent("位置随机度", "候选点偏离网格中心的程度。0 = 严格网格中心，1 = 格子内任意位置"), settings.jitter, 0f, 1f);
            settings.spreadBias = EditorGUILayout.Slider(new GUIContent("散布偏好", "已放置点对新点位置的影响。0 = 均匀散开，0.5 = 纯随机，1 = 抱团聚集"), settings.spreadBias, 0f, 1f);
            settings.upNormalThreshold = EditorGUILayout.Slider("朝上阈值", settings.upNormalThreshold, 0.5f, 1f);
            settings.boundaryInset = EditorGUILayout.FloatField(new GUIContent("边界内缩 (m)", "距 mesh 边缘多近内不生成。建议填建筑半径"), settings.boundaryInset);

            EditorGUILayout.BeginHorizontal();
            settings.seed = EditorGUILayout.IntField("随机种子", settings.seed);
            if (GUILayout.Button("随机", GUILayout.Width(60)))
            {
                settings.seed = Random.Range(int.MinValue, int.MaxValue);
            }
            EditorGUILayout.EndHorizontal();

            settings.maxRetriesPerPoint = EditorGUILayout.IntField("每点最大重试", settings.maxRetriesPerPoint);

            // ========== 调试设置面板 ==========
            EditorGUILayout.Space();
            debugFoldout = EditorGUILayout.Foldout(debugFoldout, "🔍 调试可视化设置", true, EditorStyles.boldLabel);
            if (debugFoldout)
            {
                EditorGUI.indentLevel++;
                settings.showDebugGizmos = EditorGUILayout.Toggle("启用调试模式", settings.showDebugGizmos);
                
                using (new EditorGUI.DisabledScope(!settings.showDebugGizmos))
                {
                    settings.showCandidatePoints = EditorGUILayout.Toggle("显示候选点", settings.showCandidatePoints);
                    settings.showCollisionBoxes = EditorGUILayout.Toggle("显示碰撞盒", settings.showCollisionBoxes);
                    settings.showAvoidanceBounds = EditorGUILayout.Toggle("显示避让范围", settings.showAvoidanceBounds);
                    settings.showDestroyedObjects = EditorGUILayout.Toggle("显示被销毁物体", settings.showDestroyedObjects);
                    
                    EditorGUILayout.Space();
                    settings.candidatePointColor = EditorGUILayout.ColorField("候选点颜色", settings.candidatePointColor);
                    settings.collisionBoxColor = EditorGUILayout.ColorField("碰撞盒颜色", settings.collisionBoxColor);
                    settings.avoidanceColor = EditorGUILayout.ColorField("避让范围颜色", settings.avoidanceColor);
                    settings.destroyedColor = EditorGUILayout.ColorField("被销毁物体颜色", settings.destroyedColor);
                    
                    EditorGUILayout.Space();
                    EditorGUILayout.BeginHorizontal();
                    
                    GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
                    if (GUILayout.Button("📊 显示所有调试信息", GUILayout.Height(25)))
                    {
                        if (settings.target != null)
                        {
                            SaveSettings();
                            ProcGenerator.Generate(settings);
                            Debug.Log("[ProcGen] 已重新生成并显示所有调试信息");
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("提示", "请先指定生成目标", "确定");
                        }
                    }
                    
                    GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                    if (GUILayout.Button("🗑️ 清除所有调试信息", GUILayout.Height(25)))
                    {
#if UNITY_EDITOR
                        ProcGenDebugManager.Instance.ClearAll();
                        Debug.Log("[ProcGen] 已清除所有调试信息");
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
            if (GUILayout.Button("生成", GUILayout.Height(30)))
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
                "3. 点击「生成」。每次生成创建独立的 [ProcGen Root] #N，可 Ctrl+Z 撤销\n\n" +
                "多次生成：\n" +
                "• 每次点击「生成」创建独立的 [ProcGen Root] #N，新物体会自动避让已有批次\n" +
                "• 在场景中手动删除对应 Root 即可移除某次生成结果\n\n" +
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
    }
}