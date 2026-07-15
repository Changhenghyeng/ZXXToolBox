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

        public static void Open()
        {
            var w = GetWindow<LabelManagerWindow>("Label Tool");
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

        private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

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

            if (GUILayout.Button(Content("Scan", "扫描场景中所有带 Prefab 的物体，生成名称标签列表"), GUILayout.Height(24)))
            {
                // 首次扫描时按需创建 LabelManager
                if (!m) m = EnsureExists();
                if (m) { Undo.RecordObject(m, "扫描"); m.Scan(); }
            }

            using (new EditorGUI.DisabledScope(!m))
            {
                if (GUILayout.Button(Content("Clear", "清空所有标签条目"), GUILayout.Height(24)))
                {
                    Undo.RecordObject(m, "清空"); m.entries.Clear(); m.Refresh();
                }
                if (GUILayout.Button(Content("Refresh", "强制重建标签样式"), GUILayout.Height(24)))
                {
                    Undo.RecordObject(m, "刷新"); m.ForceRebuildStyle();
                }
            }

            var baked = GameObject.Find("[Baked Labels]");
            if (baked)
            {
                if (GUILayout.Button(Content("Clear Bake", "删除烘培到场景中的标签对象"), GUILayout.Height(24)))
                {
                    Undo.DestroyObjectImmediate(baked);
                    if (m) m.enabled = true;
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(!m))
                {
                    if (GUILayout.Button(Content("Bake", "将标签烘培为场景中的静态 TextMeshPro 对象"), GUILayout.Height(24)) && m)
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
                EditorGUILayout.LabelField("Scan Roots: drag root nodes here", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("  (if empty, scans the entire scene)", EditorStyles.miniLabel);
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
                hasRoots ? "+ Add Root" : "Scan Roots: drag root nodes here", null, typeof(GameObject), true);
            if (drop) { Undo.RecordObject(m, "添加根节点"); m.scanRoots.Add(drop); }

            if (!hasRoots && m.scanRoots.Count == 0)
                EditorGUILayout.LabelField("  (if empty, scans the entire scene)", EditorStyles.miniLabel);

            EditorGUILayout.BeginHorizontal();
            m.usePrefabName = EditorGUILayout.ToggleLeft(
                Content("Use Prefab Name", "使用 Prefab 资产名代替场景物体名作为标签文字").text, m.usePrefabName, GUILayout.Width(130));
            m.onlyDirectChildren = EditorGUILayout.ToggleLeft(
                Content("Direct Children Only", "仅扫描根节点的直接子节点，不递归子级").text, m.onlyDirectChildren);
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
            bool newAll = EditorGUILayout.ToggleLeft(Content($"Label Entries ({count})", "标签条目列表，拖入 GameObject 即可添加").text, allOn || allOff ? allOn : true, EditorStyles.boldLabel);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck() && m && count > 0)
            {
                Undo.RecordObject(m, "切换全部");
                foreach (var e in m.entries) e.show = newAll;
                m.Refresh();
            }

        // ── 拖入区（支持多选 GameObject 一次性拖入） ──
            var dropLabel = "Drop GameObjects here to add";
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
                EditorGUILayout.LabelField("Click Scan to create a LabelManager", EditorStyles.centeredGreyMiniLabel);
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
                EditorGUILayout.LabelField("Drop objects or click Scan", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndVertical();
            }

            if (removeIndex >= 0) { Undo.RecordObject(m, "删除条目"); m.entries.RemoveAt(removeIndex); }

            if (hasEntries)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(Content("Clean Invalid", "清理引用已丢失的无效条目").text, EditorStyles.miniButtonRight))
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
            if (!m) { EditorGUILayout.Foldout(false, Content("Style", "样式设置").text, true); return false; }
            fold = EditorGUILayout.Foldout(fold, Content("Style", "样式设置：底板、文字外观").text, true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            var st = m.style;

            Undo.RecordObject(m, "修改样式");
            EditorGUI.BeginChangeCheck();

            // Background Mode
            st.bgMode = (BackgroundMode)EditorGUILayout.EnumPopup(Content("BG Mode", "底板模式：Sprite 贴图 或 纯色圆角矩形"), st.bgMode);

            if (st.bgMode == BackgroundMode.Sprite)
            {
                st.bgSprite = (Sprite)EditorGUILayout.ObjectField(Content("BG Sprite", "底板贴图 Sprite"), st.bgSprite, typeof(Sprite), false);
            }
            else
            {
                st.backgroundColor = EditorGUILayout.ColorField(Content("BG Color", "底板颜色"), st.backgroundColor);
                st.cornerRadius    = EditorGUILayout.Slider(Content("Corner Radius", "底板圆角半径（像素）"), st.cornerRadius, 0, 120);
                st.padding         = EditorGUILayout.Vector2Field(Content("Padding", "底板内边距"), st.padding);
                st.bgOutlineColor  = EditorGUILayout.ColorField(Content("Outline Color", "底板描边颜色"), st.bgOutlineColor);
                st.bgOutlineWidth  = EditorGUILayout.Slider(Content("Outline Width", "底板描边粗细"), st.bgOutlineWidth, 0, 20);
                st.edgeBlur        = EditorGUILayout.Slider(Content("Edge Blur", "底板边缘模糊程度"), st.edgeBlur, 0, 60);
            }

            EditorGUILayout.Space(3);

            // Text
            EditorGUILayout.LabelField(Content("Text", "文字样式"), EditorStyles.miniBoldLabel);
            st.fontAsset   = (TMP_FontAsset)EditorGUILayout.ObjectField(Content("Font", "字体资源（TMP Font Asset）"), st.fontAsset, typeof(TMP_FontAsset), false);
            st.textColor   = EditorGUILayout.ColorField(Content("Color", "文字颜色"), st.textColor);
            st.fontSize    = EditorGUILayout.IntSlider(Content("Font Size", "字号"), st.fontSize, 8, 200);
            st.fontStyle   = (FontStyles)EditorGUILayout.EnumPopup(Content("Font Style", "文字样式（粗体/斜体等）"), st.fontStyle);
            st.textAlignment = (TextAlignmentOptions)EditorGUILayout.EnumPopup(Content("Alignment", "文字对齐方式"), st.textAlignment);
            st.outlineColor = EditorGUILayout.ColorField(Content("Outline Color", "文字描边颜色"), st.outlineColor);
            st.outlineWidth = EditorGUILayout.Slider(Content("Outline Width", "文字描边粗细"), st.outlineWidth, 0, 0.5f);

            if (EditorGUI.EndChangeCheck()) m.ForceRebuildStyle();
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── 定位 ──────────────────────────────────────

        public static bool DrawPositionFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, Content("Position", "定位").text, true); return false; }
            fold = EditorGUILayout.Foldout(fold, Content("Position", "定位：标签在场景中的放置方式").text, true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            Undo.RecordObject(m, "修改定位");
            m.useBounds = EditorGUILayout.Toggle(Content("Bounds Positioning", "使用包围盒定位标签（自动计算物体顶部位置）"), m.useBounds);
            if (m.useBounds)
            {
                EditorGUI.indentLevel++;
                m.heightMargin = EditorGUILayout.FloatField(Content("Top Margin", "标签距离包围盒顶部的额外边距"), m.heightMargin);
                m.worldOffset = new Vector3(
                    EditorGUILayout.FloatField(Content("Offset X", "世界偏移 X"), m.worldOffset.x),
                    EditorGUILayout.FloatField(Content("Offset Y", "世界偏移 Y"), m.worldOffset.y),
                    EditorGUILayout.FloatField(Content("Offset Z", "世界偏移 Z"), m.worldOffset.z));
                EditorGUI.indentLevel--;
            }
            else
            {
                m.worldOffset = EditorGUILayout.Vector3Field(Content("World Offset", "标签的世界空间偏移量"), m.worldOffset);
            }
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── LOD ──────────────────────────────────────

        public static bool DrawLodFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, Content("LOD", "LOD 距离设置").text, true); return false; }
            fold = EditorGUILayout.Foldout(fold, Content("LOD", "LOD：标签显示距离与刷新频率").text, true);
            if (!fold) return fold;

            EditorGUI.indentLevel++;
            Undo.RecordObject(m, "修改LOD");
            m.maxDistance       = EditorGUILayout.FloatField(Content("Max Distance", "标签可见的最大距离（世界单位）"), m.maxDistance);
            m.recomputeInterval = EditorGUILayout.IntSlider(Content("Recompute Interval (frames)", "检测标签可见性的间隔帧数"), m.recomputeInterval, 1, 30);
            m.viewportMargin    = EditorGUILayout.Slider(Content("Viewport Margin", "视口边缘余量，在此范围内的标签也会显示"), m.viewportMargin, 0, 0.3f);
            EditorGUI.indentLevel--;
            return fold;
        }

        // ── Prefab 别名 ────────────────────────────────

        public static bool DrawAliasFoldout(LabelManager m, bool fold)
        {
            if (!m) { EditorGUILayout.Foldout(false, Content("Prefab Alias (0)", "Prefab 别名").text, true); return false; }
            fold = EditorGUILayout.Foldout(fold, Content($"Prefab Alias ({m.aliases?.Count ?? 0})", "Prefab 别名：为特定 Prefab 指定自定义显示名称").text, true);
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

            if (GUILayout.Button(Content("+ Add Alias", "添加新的 Prefab 别名映射"), GUILayout.Height(20)))
            {
                Undo.RecordObject(m, "添加别名");
                m.aliases.Add(new LabelManager.Alias());
            }

            EditorGUI.indentLevel--;
            return fold;
        }
    }
}
