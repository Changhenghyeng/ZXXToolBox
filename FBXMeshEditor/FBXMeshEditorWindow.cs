using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ZXXTools.FBXMeshEditor
{
    /// <summary>ZXX 工具箱中的 FBX Mesh Editor 控制窗口。</summary>
    public sealed class FBXMeshEditorWindow : EditorWindow
    {
        private static bool _isOpen;
        private Vector2 _scrollPosition;
        private readonly Dictionary<int, bool> _outputSelection = new Dictionary<int, bool>();
        private GUIStyle _targetRowStyle;
        private GUIStyle _activeTargetRowStyle;

        internal static bool IsOpen => _isOpen;

        public static void Open()
        {
            FBXMeshEditorWindow window = GetWindow<FBXMeshEditorWindow>("FBX Mesh Editor");
            window.minSize = new Vector2(360f, 250f);
            window.Show();
        }

        internal static void RepaintOpenWindow()
        {
            foreach (FBXMeshEditorWindow window in Resources.FindObjectsOfTypeAll<FBXMeshEditorWindow>())
                window.Repaint();
        }

        private void OnEnable()
        {
            _isOpen = true;
            SceneView.RepaintAll();
        }

        private void OnGUI()
        {
            FBXMeshEditorController.HandleHistoryShortcut(Event.current);
            FBXMeshEditSession session = FBXMeshEditorController.Session;
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Unity FBX Mesh Editor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "V1 第一阶段：静态单 Mesh 顶点显示、选择、移动与 Undo。进入编辑后使用工作副本，不会直接修改导入的 FBX。",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(session.IsActive))
            {
                EditorGUILayout.LabelField("Selected Objects", Selection.gameObjects.Length.ToString());
                if (GUILayout.Button("Enter Mesh Edit Mode", GUILayout.Height(32)))
                {
                    if (!FBXMeshEditorController.EnterSelection(out string error))
                        EditorUtility.DisplayDialog("FBX Mesh Editor", error, "确定");
                }
            }

            if (session.IsActive)
            {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Active Model", session.MeshFilter.name);
            EditorGUILayout.LabelField("Edit Targets", FBXMeshEditorController.Sessions.Count.ToString());
            foreach (FBXMeshEditSession targetSession in FBXMeshEditorController.Sessions)
            {
                EnsureTargetRowStyles();
                int id = targetSession.MeshFilter.GetInstanceID();
                if (!_outputSelection.ContainsKey(id)) _outputSelection[id] = true;
                EditorGUILayout.BeginHorizontal();
                _outputSelection[id] = EditorGUILayout.Toggle(_outputSelection[id], GUILayout.Width(18f));
                GUIStyle rowStyle = targetSession == session ? _activeTargetRowStyle : _targetRowStyle;
                if (GUILayout.Button((targetSession == session ? "● " : "") + targetSession.MeshFilter.name,
                        rowStyle))
                    FBXMeshEditorController.SetActiveSession(targetSession);
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.LabelField("Vertex Count", session.EditableMesh.vertexCount.ToString());
            EditorGUILayout.LabelField("Inferred Quads", session.Topology.QuadCount.ToString());
            int componentMode = GUILayout.Toolbar((int)session.ComponentMode,
                new[] { "Vertex", "Edge", "Face" });
            if (componentMode != (int)session.ComponentMode)
                FBXMeshEditorController.SetComponentMode((FBXMeshComponentMode)componentMode);
            EditorGUILayout.LabelField("Selected Components", session.SelectedComponentCount.ToString());
            if (session.ComponentMode == FBXMeshComponentMode.Vertex)
            {
                FBXMeshEditorController.MergeDistance = EditorGUILayout.FloatField(
                    new GUIContent("Merge Distance", "Ctrl+M 按距离合并所选顶点时使用的模型空间距离"),
                    FBXMeshEditorController.MergeDistance);
                EditorGUILayout.HelpBox(
                    "M：合并所选顶点到中心。Ctrl+M：只合并所选顶点中距离小于 Merge Distance 的点。Delete：安全溶解内部顶点并重建周围面。",
                    MessageType.None);
            }
            EditorGUILayout.LabelField("Soft Selection",
                FBXMeshEditorController.SoftSelectionEnabled ? "On  (B)" : "Off  (B)");
            EditorGUILayout.HelpBox(
                "按一下 B 开关软选择；按住 B 在 Scene 里左右拖动可实时调整半径，并沿用上一次的半径。移动 Vertex / Edge / Face 时均生效。",
                MessageType.None);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Bend Mode",
                FBXMeshEditorController.BendModeEnabled ? "Active  (Ctrl+B)" : "Inactive  (Ctrl+B)");
            if (GUILayout.Button(FBXMeshEditorController.BendModeEnabled ? "Exit Bend" : "Enter Bend",
                    GUILayout.Width(92f)))
                FBXMeshEditorController.ToggleBendMode();
            EditorGUILayout.EndHorizontal();
            if (FBXMeshEditorController.BendModeEnabled)
            {
                EditorGUI.BeginChangeCheck();
                float bendAngle = EditorGUILayout.DelayedFloatField(
                    new GUIContent("Curvature", "C 形弯曲角度，正值朝本地 +X，负值朝本地 -X"),
                    FBXMeshEditorController.BendAngle);
                float bendLow = EditorGUILayout.DelayedFloatField(
                    new GUIContent("Low Bound", "本地负 Y 侧范围，最大为 0"),
                    FBXMeshEditorController.BendLowBound);
                float bendHigh = EditorGUILayout.DelayedFloatField(
                    new GUIContent("High Bound", "本地正 Y 侧范围，最小为 0"),
                    FBXMeshEditorController.BendHighBound);
                if (EditorGUI.EndChangeCheck())
                    FBXMeshEditorController.SetBendNumericParameters(
                        bendAngle, bendLow, bendHigh);
            }
            EditorGUILayout.HelpBox(
                "Ctrl+B 开关 C 形弯曲。全部 Edit Targets 共用一个世界空间弯曲器：中心手柄控制整体弯曲中心，旋转环控制公共轴与方向，橙色手柄控制角度，Low / High 控制两端范围。点线面选区和软选择不会改变 Bend 范围。Esc 退出。",
                MessageType.None);
            EditorGUILayout.LabelField("Lock Edit Target",
                FBXMeshEditorController.LockEditTarget ? "On  (Ctrl+L)" : "Off  (Ctrl+L)");
            EditorGUILayout.HelpBox(
                FBXMeshEditorController.LockEditTarget
                    ? "当前编辑对象已锁定。其他模型不会抢走点击，可选择被遮挡区域；关闭后可再次点击其他对象退出编辑。"
                    : "默认状态下，单击其他模型会保存当前结果并退出编辑。需要选择被遮挡区域时按 Ctrl+L 开启锁定。",
                MessageType.None);
            if (session.ComponentMode == FBXMeshComponentMode.Edge)
            {
                EditorGUILayout.LabelField("Insert Edge Loop",
                    FBXMeshEditorController.LoopPreviewEnabled ? "Active  (C)" : "Inactive  (C)");
                EditorGUILayout.HelpBox(
                    "开启后 Hover Edge 查看 50% 插线预览；按住左键后，插线会沿目标边方向跟随鼠标，Shift 按 10% 吸附，松开生成。右键或 Esc 取消。遇到三角面会收束到对角顶点，遇到边界允许结束；不确定极点仍会停止。",
                    MessageType.None);
                EditorGUILayout.LabelField("Dissolve Selected Edges", "Delete");
                EditorGUILayout.HelpBox(
                    "支持单边、连续边段、开放或闭合 Loop。双击 Edge 可加选整条 Loop；点击 Dissolve Selected Edges 或按 Delete 会合并两侧逻辑面，并隐藏边段内部失去控制意义的点。边界边不会执行，以免开洞。",
                    MessageType.None);
            }

            if (session.ComponentMode == FBXMeshComponentMode.Face)
            {
                if (GUILayout.Button(new GUIContent("Rebuild Selected Patch",
                    "将单个连续、无洞且接近四边形的选中区域重建为规则四边面。两组对边的边数必须分别一致；不安全的选区会拒绝执行。")))
                    FBXMeshEditorController.RebuildSelectedPatch();
                EditorGUILayout.HelpBox(
                    "用于清理三角扇和混乱内部布线。只替换选区内部，保留外轮廓；这是局部 Patch 重建，不是自动重拓扑。",
                    MessageType.None);
                EditorGUILayout.LabelField("Subdivide", "D");
                EditorGUILayout.HelpBox(
                    "按 D：有选中面时细分选中面；没有选中面时细分整个 Mesh。保持原轮廓，不自动平滑。",
                    MessageType.None);
                EditorGUILayout.LabelField("Smooth Subdivide Whole Mesh", "Shift+D");
                EditorGUILayout.HelpBox(
                    "按 Shift+D：细分整个 Mesh 并平滑轮廓。硬表面边角也会变圆。",
                    MessageType.None);
            }

            EditorGUILayout.LabelField("Undo / Redo",
                FBXMeshEditorController.BendModeEnabled
                    ? $"Ctrl+Z / Ctrl+Y  Bend ({FBXMeshEditorController.BendUndoCount} / {FBXMeshEditorController.BendRedoCount})"
                    : $"Ctrl+Z / Ctrl+Y  ({session.UndoCount} / {session.RedoCount})");

            if (GUILayout.Button(new GUIContent("Check Topology",
                "对比原始 Mesh 与当前工作 Mesh，判断拓扑问题来自原模型还是编辑过程")))
                FBXMeshEditorController.CheckTopology();

            EditorGUILayout.Space(6);
            if (!FBXMeshExporter.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    FBXMeshExporter.IsInstalling
                        ? "正在安装 FBX Exporter 4.2.1，请等待 Package Manager 完成处理…"
                        : "保存 FBX 需要 Unity FBX Exporter 4.2.1。点击下方按钮安装。",
                    MessageType.Info);
                using (new EditorGUI.DisabledScope(FBXMeshExporter.IsInstalling))
                {
                    if (GUILayout.Button(FBXMeshExporter.IsInstalling
                        ? "Installing FBX Exporter..." : "Install FBX Exporter 4.2.1"))
                        FBXMeshExporter.InstallExporter();
                }
            }
            else
            {
                if (GUILayout.Button(FBXMeshEditorController.Sessions.Count > 1
                        ? "Save Checked As New FBX" : "Save As New FBX"))
                {
                    var checkedSessions = new List<FBXMeshEditSession>();
                    foreach (FBXMeshEditSession targetSession in FBXMeshEditorController.Sessions)
                    {
                        int id = targetSession.MeshFilter.GetInstanceID();
                        if (_outputSelection.TryGetValue(id, out bool selected) && selected)
                            checkedSessions.Add(targetSession);
                    }
                    if (FBXMeshEditorController.Sessions.Count > 1)
                        FBXMeshEditorController.SaveSessionsAsNewFBX(checkedSessions);
                    else
                        FBXMeshEditorController.SaveAsNewFBX();
                }

                using (new EditorGUI.DisabledScope(FBXMeshEditorController.Sessions.Count == 1
                    && !FBXMeshExporter.CanOverwriteSource(session)))
                {
                    if (GUILayout.Button(new GUIContent(FBXMeshEditorController.Sessions.Count > 1
                            ? "Overwrite Checked Source FBX" : "Overwrite Source FBX",
                        "覆盖最初导入的 FBX；执行前会备份，并在失败时自动恢复")))
                    {
                        if (FBXMeshEditorController.Sessions.Count == 1)
                            FBXMeshEditorController.OverwriteSourceFBX();
                        else
                        {
                            var checkedSessions = new List<FBXMeshEditSession>();
                            foreach (FBXMeshEditSession targetSession in FBXMeshEditorController.Sessions)
                            {
                                int id = targetSession.MeshFilter.GetInstanceID();
                                if (_outputSelection.TryGetValue(id, out bool selected) && selected)
                                    checkedSessions.Add(targetSession);
                            }
                            FBXMeshEditorController.OverwriteSessionsSourceFBX(checkedSessions);
                        }
                    }
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear Selection"))
            {
                session.ClearSelection();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("Exit Edit Mode"))
                FBXMeshEditorController.ManualExit();
            EditorGUILayout.EndHorizontal();

            GUI.backgroundColor = new Color(1f, 0.65f, 0.55f);
            using (new EditorGUI.DisabledScope(!session.CanRevert))
            {
                if (GUILayout.Button("Revert Changes"))
                {
                    if (EditorUtility.DisplayDialog(
                            "FBX Mesh Editor",
                            "确定放弃本次编辑结果并恢复原始 Mesh 吗？此操作会删除当前工作 Mesh。",
                            "确定恢复", "取消"))
                    {
                        FBXMeshEditorController.Revert();
                    }
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.HelpBox(
                "使用 Vertex / Edge / Face 切换组件模式；单击选择，空白处拖动框选；Shift 只加选，Ctrl/Cmd 只减选；W / E / R 切换 Move / Rotate / Scale。可使用 Unity 原生 Ctrl+Z / Ctrl+Y。",
                MessageType.None);
            EditorGUILayout.HelpBox(
                "编辑结果会自动保存为 Assets/Generated/FBXMeshEditor/Working 下的工作文件。退出编辑模式或误选其他物体不会丢失修改；当前阶段尚未写回 FBX。",
                MessageType.Warning);
            }
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

        private void OnDestroy()
        {
            _isOpen = false;
            if (FBXMeshEditorController.Session.IsActive)
                FBXMeshEditorController.Exit();
            SceneView.RepaintAll();
        }

        private void EnsureTargetRowStyles()
        {
            if (_targetRowStyle != null && _activeTargetRowStyle != null) return;
            _targetRowStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 4, 1, 1),
                fixedHeight = 20f
            };
            _activeTargetRowStyle = new GUIStyle(_targetRowStyle);
            _activeTargetRowStyle.normal.textColor = new Color(0.45f, 0.75f, 1f);
            _activeTargetRowStyle.hover.textColor = new Color(0.65f, 0.85f, 1f);
        }
    }
}
