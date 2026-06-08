using UnityEngine;
using UnityEditor;
using TMPro;

namespace LabelTool
{
    [CustomEditor(typeof(LabelManager))]
    public class LabelManagerEditor : UnityEditor.Editor
    {
        private bool _foldStyle, _foldPosition, _foldLod, _foldAlias;
        private Vector2 _entriesScroll;
        private int _lastToggledIdx = -1;

        private void OnEnable()  { Undo.undoRedoPerformed += OnUndoRedo; }
        private void OnDisable() { Undo.undoRedoPerformed -= OnUndoRedo; }

        private void OnUndoRedo()
        {
            var m = target as LabelManager;
            if (m) m.ForceRebuildStyle();
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var m = (LabelManager)target;

            LabelManagerGUI.DrawToolbar(m);
            LabelManagerGUI.DrawScanRoots(m);
            LabelManagerGUI.DrawEntries(m, ref _entriesScroll, ref _lastToggledIdx);
            _foldStyle    = LabelManagerGUI.DrawStyleFoldout(m, _foldStyle);
            _foldPosition = LabelManagerGUI.DrawPositionFoldout(m, _foldPosition);
            _foldLod      = LabelManagerGUI.DrawLodFoldout(m, _foldLod);
            _foldAlias    = LabelManagerGUI.DrawAliasFoldout(m, _foldAlias);

            if (GUI.changed) SceneView.RepaintAll();
        }
    }

    // ══════════════════════════════════════════════
    // 独立窗口
    // ══════════════════════════════════════════════

    public class LabelManagerWindow : EditorWindow
    {
        private Vector2 _scroll, _entriesScroll;
        private bool _foldStyle, _foldPosition, _foldLod, _foldAlias;
        private int _lastToggledIdx = -1;

        [MenuItem("Window/名称标签")]
        public static void Open()
        {
            var w = GetWindow<LabelManagerWindow>("名称标签");
            w.minSize = new Vector2(360, 500);
            w.Show();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var m = FindObjectOfType<LabelManager>();

            // 正常显示所有 UI；LabelManager 在首次「扫描」时按需创建
            LabelManagerGUI.DrawToolbar(m);
            LabelManagerGUI.DrawScanRoots(m);
            LabelManagerGUI.DrawEntries(m, ref _entriesScroll, ref _lastToggledIdx);
            _foldStyle    = LabelManagerGUI.DrawStyleFoldout(m, _foldStyle);
            _foldPosition = LabelManagerGUI.DrawPositionFoldout(m, _foldPosition);
            _foldLod      = LabelManagerGUI.DrawLodFoldout(m, _foldLod);
            _foldAlias    = LabelManagerGUI.DrawAliasFoldout(m, _foldAlias);

            if (GUI.changed) SceneView.RepaintAll();
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

    // ══════════════════════════════════════════════
    // 共享 GUI 绘制
    // ══════════════════════════════════════════════

    internal static class LabelManagerGUI
    {
        private const float EntriesMaxHeight = 260f;

        private static bool HasGameObjectInDrag()
        {
            if (DragAndDrop.objectReferences == null) return false;
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is GameObject)
                    return true;
            return false;
        }

        // ── 工具栏 ──────────────────────────────────

        public static void DrawToolbar(LabelManager m)
        {
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("扫描", GUILayout.Height(24)))
            {
                // 首次扫描时按需创建 LabelManager
                if (!m) m = EnsureExists();
                if (m) { Undo.RecordObject(m, "扫描"); m.Scan(); }
            }

            using (new EditorGUI.DisabledScope(!m))
            {
                if (GUILayout.Button("清空", GUILayout.Height(24)))
                {
                    Undo.RecordObject(m, "清空"); m.entries.Clear(); m.Refresh();
                }
                if (GUILayout.Button("刷新", GUILayout.Height(24)))
                {
                    Undo.RecordObject(m, "刷新"); m.ForceRebuildStyle();
                }
            }

            var baked = GameObject.Find("[Baked Labels]");
            if (baked)
            {
                if (GUILayout.Button("清除烘培", GUILayout.Height(24)))
                {
                    Undo.DestroyObjectImmediate(baked);
                    if (m) m.enabled = true;
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(!m))
                {
                    if (GUILayout.Button("烘培", GUILayout.Height(24)) && m)
                        m.BakeToScene();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private static LabelManager EnsureExists()
        {
            var m = Object.FindObjectOfType<LabelManager>();
            if (m) return m;
            var host = new GameObject("[LabelTool]");
            Undo.RegisterCreatedObjectUndo(host, "创建 LabelManager");
            return Undo.AddComponent<LabelManager>(host);
        }

        // ── 扫描范围 ────────────────────────────────

        public static void DrawScanRoots(LabelManager m)
        {
            if (!m)
            {
                EditorGUILayout.LabelField("扫描范围：拖入根节点", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  未指定则扫描整个场景", EditorStyles.miniLabel);
                EditorGUILayout.Space(4);
                return;
            }
            bool hasRoots = m.scanRoots.Count > 0;

            if (hasRoots)
            {
                int removeIndex = -1;
                for (int i = 0; i < m.scanRoots.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    var r = (GameObject)EditorGUILayout.ObjectField(m.scanRoots[i], typeof(GameObject), true);
                    if (r != m.scanRoots[i]) { Undo.RecordObject(m, "修改根节点"); m.scanRoots[i] = r; }
                    if (GUILayout.Button("×", GUILayout.Width(22))) removeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }
                if (removeIndex >= 0) { Undo.RecordObject(m, "删除根节点"); m.scanRoots.RemoveAt(removeIndex); }
            }

            // 添加根节点
            var drop = (GameObject)EditorGUILayout.ObjectField(
                hasRoots ? "+ 根节点" : "扫描范围：拖入根节点", null, typeof(GameObject), true);
            if (drop) { Undo.RecordObject(m, "添加根节点"); m.scanRoots.Add(drop); }

            if (!hasRoots && m.scanRoots.Count == 0)
                EditorGUILayout.LabelField("  未指定则扫描整个场景", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            m.usePrefabName = EditorGUILayout.ToggleLeft("使用 Prefab 名", m.usePrefabName, GUILayout.Width(110));
            m.onlyDirectChildren = EditorGUILayout.ToggleLeft("仅直接子节点", m.onlyDirectChildren);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
        }

        // ── 标签条目 ──────────────────────────────────

        public static void DrawEntries(LabelManager m, ref Vector2 scroll, ref int lastToggledIdx)
        {
            // ── 标题栏总复选框（全选/全不选） ──
            int count = m?.entries?.Count ?? 0;
            bool allOn = count > 0, allOff = count > 0;
            if (m?.entries != null)
                foreach (var e in m.entries) { if (!e.show) allOn = false; if (e.show) allOff = false; }
            EditorGUI.showMixedValue = !allOn && !allOff;
            EditorGUI.BeginChangeCheck();
            bool newAll = EditorGUILayout.ToggleLeft($"标签条目 ({count})", allOn || allOff ? allOn : true, EditorStyles.boldLabel);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && m && count > 0)
            {
                Undo.RecordObject(m, "切换全部");
                foreach (var e in m.entries) e.show = newAll;
                m.Refresh();
            }

        // ── 拖入区（支持多选 GameObject 一次性拖入） ──
            var dropLabel = "拖入添加";
            var dropRect = GUILayoutUtility.GetRect(new GUIContent(dropLabel), EditorStyles.textField, GUILayout.Height(20));

            // 拖拽时高亮
            bool isDraggingGO = DragAndDrop.objectReferences != null && HasGameObjectInDrag();
            if (dropRect.Contains(Event.current.mousePosition) && isDraggingGO)
                GUI.color = new Color(0.7f, 1f, 0.7f, 1f);

            GUI.Box(dropRect, "", EditorStyles.textField);
            GUI.color = Color.white;

            // 提示文字
            var hintStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.gray }
            };
            EditorGUI.LabelField(dropRect, "  " + dropLabel, hintStyle);

            // 拖拽事件处理
            var evt = Event.current;
            if (dropRect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.DragUpdated && isDraggingGO)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    evt.Use();
                }
                else if (evt.type == EventType.DragPerform && isDraggingGO)
                {
                    DragAndDrop.AcceptDrag();
                    if (m)
                    {
                        Undo.RecordObject(m, "添加条目");
                        foreach (var obj in DragAndDrop.objectReferences)
                            if (obj is GameObject go)
                                m.entries.Add(new LabelManager.Entry { target = go, show = true });
                    }
                    evt.Use();
                }
            }

            EditorGUILayout.Space(2);

            if (!m)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("点击「扫描」创建 LabelManager", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
                return;
            }

            int removeIndex = -1;
            bool hasEntries = m.entries != null && m.entries.Count > 0;

            if (hasEntries)
            {
                scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MaxHeight(EntriesMaxHeight));
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                for (int i = 0; i < m.entries.Count; i++)
                {
                    var e = m.entries[i];
                    EditorGUILayout.BeginHorizontal();

                    bool show = EditorGUILayout.Toggle(e.show, GUILayout.Width(18));
                    if (show != e.show)
                    {
                        Undo.RecordObject(m, "切换可见");
                        bool shiftHeld = Event.current != null && Event.current.shift;
                        if (shiftHeld && lastToggledIdx >= 0 && lastToggledIdx < m.entries.Count && lastToggledIdx != i)
                        {
                            int from = Mathf.Min(i, lastToggledIdx);
                            int to   = Mathf.Max(i, lastToggledIdx);
                            for (int j = from; j <= to; j++)
                                m.entries[j].show = show;
                        }
                        else
                        {
                            e.show = show;
                        }
                        lastToggledIdx = i;
                        m.Refresh();
                    }
                    var go = (GameObject)EditorGUILayout.ObjectField(e.target, typeof(GameObject), true, GUILayout.MinWidth(80));
                    if (go != e.target) { Undo.RecordObject(m, "修改条目"); e.target = go; }

                    string preview = e.target ? m.DisplayName(e.target) : "—";
                    EditorGUILayout.LabelField(preview, EditorStyles.miniLabel, GUILayout.MinWidth(40), GUILayout.ExpandWidth(true));

                    if (GUILayout.Button("×", GUILayout.Width(22))) removeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.EndScrollView();
            }
            else
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField("拖入物体或点击「扫描」", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
            }

            if (removeIndex >= 0) { Undo.RecordObject(m, "删除条目"); m.entries.RemoveAt(removeIndex); }

            if (hasEntries)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("清理无效", EditorStyles.miniButtonRight))
                {
                    Undo.RecordObject(m, "清理无效");
                    int n = m.CleanInvalid();
                    Debug.Log($"[Label] 清理了 {n} 个无效条目");
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(4);
        }

        // ── 样式 ────────────────────────────────────

        public static bool DrawStyleFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, "样式设置", true); return false; }
            fold = EditorGUILayout.Foldout(fold, "样式设置", true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            var st = m.style;

            Undo.RecordObject(m, "修改样式");
            EditorGUI.BeginChangeCheck();

            // 底板模式
            st.bgMode = (BackgroundMode)EditorGUILayout.EnumPopup("底板模式", st.bgMode);

            if (st.bgMode == BackgroundMode.Sprite)
            {
                st.bgSprite = (Sprite)EditorGUILayout.ObjectField("底板贴图", st.bgSprite, typeof(Sprite), false);
            }
            else
            {
                st.backgroundColor = EditorGUILayout.ColorField("底板颜色", st.backgroundColor);
                st.cornerRadius    = EditorGUILayout.Slider("圆角", st.cornerRadius, 0, 120);
                st.padding         = EditorGUILayout.Vector2Field("内边距", st.padding);
                st.bgOutlineColor  = EditorGUILayout.ColorField("描边颜色", st.bgOutlineColor);
                st.bgOutlineWidth  = EditorGUILayout.Slider("描边粗细", st.bgOutlineWidth, 0, 20);
                st.edgeBlur        = EditorGUILayout.Slider("边缘模糊", st.edgeBlur, 0, 60);
            }

            EditorGUILayout.Space(3);

            // 文字
            EditorGUILayout.LabelField("文字", EditorStyles.miniBoldLabel);
            st.fontAsset   = (TMP_FontAsset)EditorGUILayout.ObjectField("字体", st.fontAsset, typeof(TMP_FontAsset), false);
            st.textColor   = EditorGUILayout.ColorField("颜色", st.textColor);
            st.fontSize    = EditorGUILayout.IntSlider("字号", st.fontSize, 8, 200);
            st.fontStyle   = (FontStyles)EditorGUILayout.EnumPopup("样式", st.fontStyle);
            st.textAlignment = (TextAlignmentOptions)EditorGUILayout.EnumPopup("对齐", st.textAlignment);
            st.outlineColor = EditorGUILayout.ColorField("描边颜色", st.outlineColor);
            st.outlineWidth = EditorGUILayout.Slider("描边粗细", st.outlineWidth, 0, 0.5f);

            if (EditorGUI.EndChangeCheck()) m.ForceRebuildStyle();
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── 定位 ──────────────────────────────────────

        public static bool DrawPositionFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, "定位", true); return false; }
            fold = EditorGUILayout.Foldout(fold, "定位", true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            Undo.RecordObject(m, "修改定位");
            m.useBounds = EditorGUILayout.Toggle("包围盒定位", m.useBounds);
            if (m.useBounds)
            {
                EditorGUI.indentLevel++;
                m.heightMargin = EditorGUILayout.FloatField("顶部边距", m.heightMargin);
                m.worldOffset = new Vector3(
                    EditorGUILayout.FloatField("偏移 X", m.worldOffset.x),
                    EditorGUILayout.FloatField("偏移 Y", m.worldOffset.y),
                    EditorGUILayout.FloatField("偏移 Z", m.worldOffset.z));
                EditorGUI.indentLevel--;
            }
            else
            {
                m.worldOffset = EditorGUILayout.Vector3Field("偏移", m.worldOffset);
            }
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── LOD ──────────────────────────────────────

        public static bool DrawLodFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, "LOD", true); return false; }
            fold = EditorGUILayout.Foldout(fold, "LOD", true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            Undo.RecordObject(m, "修改LOD");
            m.maxDistance       = EditorGUILayout.FloatField("最大距离", m.maxDistance);
            m.recomputeInterval = EditorGUILayout.IntSlider("检测间隔（帧）", m.recomputeInterval, 1, 30);
            m.viewportMargin    = EditorGUILayout.Slider("视口余量", m.viewportMargin, 0, 0.3f);
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── Prefab 别名 ────────────────────────────────

        public static bool DrawAliasFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, "Prefab 别名 (0)", true); return false; }
            fold = EditorGUILayout.Foldout(fold, $"Prefab 别名 ({m.aliases?.Count ?? 0})", true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;

            int removeIndex = -1;
            if (m.aliases != null)
            {
                for (int i = 0; i < m.aliases.Count; i++)
                {
                    var a = m.aliases[i];
                    EditorGUILayout.BeginHorizontal();
                    var pf = (GameObject)EditorGUILayout.ObjectField(a.prefab, typeof(GameObject), false, GUILayout.MinWidth(80));
                    if (pf != a.prefab) { Undo.RecordObject(m, "修改别名"); a.prefab = pf; }
                    var alias = EditorGUILayout.TextField(a.alias, GUILayout.MinWidth(60));
                    if (alias != a.alias) { Undo.RecordObject(m, "修改别名"); a.alias = alias; }
                    if (GUILayout.Button("×", GUILayout.Width(22))) removeIndex = i;
                    EditorGUILayout.EndHorizontal();
                }
            }
            if (removeIndex >= 0) { Undo.RecordObject(m, "删除别名"); m.aliases.RemoveAt(removeIndex); }

            if (GUILayout.Button("+ 添加别名", GUILayout.Height(20)))
            {
                Undo.RecordObject(m, "添加别名");
                m.aliases.Add(new LabelManager.Alias());
            }

            EditorGUI.indentLevel--;
            return fold;
        }
    }
}
