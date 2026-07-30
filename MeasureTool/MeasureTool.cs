using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;

/// <summary>
/// 物体尺寸 / 距离测量工具。
/// Unity 默认 1 unit = 1 米。
/// 提供两种模式：包围盒测量（支持多物体）、自由标尺。
/// 标尺模式下按住 V 可用 Unity 内置顶点吸附。
/// </summary>
public class MeasureTool : EditorWindow
{
    // ════════════════ 枚举 ════════════════

    private enum MeasureMode { BoundingBox, Ruler }

    private enum DisplayUnit { Meters, Centimeters, Millimeters }

    // ════════════════ 状态字段 ════════════════

    private MeasureMode _mode = MeasureMode.BoundingBox;
    private DisplayUnit _unit = DisplayUnit.Meters;

    // 外观
    private Color _lineColor = new Color(1f, 0.85f, 0.2f, 0.9f);  // 暖黄
    private Color _dimColor   = new Color(1f, 0.5f, 0.3f, 0.7f);  // 橙（分量线）
    private int   _labelFontSize = 14;
    private float _lineThickness = 2f;

    // 包围盒（多物体）
    private List<GameObject> _targetObjects = new List<GameObject>();
    private bool _showWireframe = true;
    private bool _showDimLabels = true;
    private bool _includeChildren = true;
    private Vector2 _bboxScrollPos;
    private Vector3 _targetSizeMeters = Vector3.one;
    private bool _resizeX = true;
    private bool _resizeY;
    private bool _resizeZ = true;

    // 标尺
    private Vector3? _rulerA;
    private Vector3? _rulerB;
    private bool _previewSnapped;
    private bool _rulerVertexSnap = true;
    private float _rulerSnapPixelRadius = 14f;
    private readonly List<Vector3> _snapVertexBuffer = new List<Vector3>(4096);
    private Vector3? _previewPoint;       // 鼠标悬停预览点

    // 面板折叠
    private bool _foldDisplay  = true;
    private bool _foldBBox     = true;
    private bool _foldRuler    = true;
    private Vector2 _mainScrollPos;

    // Scene 标签样式（延迟初始化）
    private GUIStyle  _sceneLabelStyle;
    private Texture2D _labelBgTex;
    private GUIContent _cachedLabelContent; // 减少分配

    // ── EditorPrefs Key ──
    private const string PrefKey = "MeasureTool.Settings_v5";

    // ════════════════ 窗口入口 ════════════════

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    public static void Open()
    {
        var w = GetWindow<MeasureTool>("Measure Tool");
        w.minSize = new Vector2(340, 500);
        w.Show();
    }

    // ════════════════ 生命周期 ════════════════

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        LoadPrefs();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SavePrefs();
        CleanupResources();
    }

    private void CleanupResources()
    {
        if (_labelBgTex != null) { DestroyImmediate(_labelBgTex); _labelBgTex = null; }
        _sceneLabelStyle = null;
        _cachedLabelContent = null;
    }

    // ════════════════ EditorPrefs 持久化 ════════════════

    [Serializable]
    private class SavedState
    {
        public int version;
        public int mode;
        public int unit;
        public float lineR, lineG, lineB, lineA;
        public float dimR, dimG, dimB, dimA;
        public int labelFontSize;
        public float lineThickness;
        public bool showWireframe;
        public bool showDimLabels;
        public bool includeChildren;
        public bool rulerVertexSnap;
        public float rulerSnapPixelRadius;
        public float targetSizeX;
        public float targetSizeY;
        public float targetSizeZ;
        public bool resizeX;
        public bool resizeY;
        public bool resizeZ;
    }

    private void LoadPrefs()
    {
        string json = EditorPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(json))
        {
            // 尝试迁移旧版数据（v1 / v2 / v3 / v4）
            json = EditorPrefs.GetString("MeasureTool.Settings_v4", "");
            if (string.IsNullOrEmpty(json))
                json = EditorPrefs.GetString("MeasureTool.Settings_v3", "");
            if (string.IsNullOrEmpty(json))
                json = EditorPrefs.GetString("MeasureTool.Settings_v2", "");
            if (string.IsNullOrEmpty(json))
                json = EditorPrefs.GetString("MeasureTool.Settings", "");
            if (string.IsNullOrEmpty(json)) return;
        }
        try
        {
            var s = JsonUtility.FromJson<SavedState>(json);

            // 旧枚举迁移: v1 { BoundingBox=0, Distance=1, Ruler=2 } → v2 { BoundingBox=0, Ruler=1 }
            _mode = s.mode switch { 0 => MeasureMode.BoundingBox, _ => MeasureMode.Ruler };

            _unit           = (DisplayUnit)s.unit;
            _lineColor      = new Color(s.lineR, s.lineG, s.lineB, s.lineA);
            _dimColor       = new Color(s.dimR, s.dimG, s.dimB, s.dimA);
            _labelFontSize  = Mathf.Clamp(s.labelFontSize, 10, 36);
            _lineThickness  = Mathf.Clamp(s.lineThickness, 0.5f, 6f);
            _showWireframe  = s.showWireframe;
            _showDimLabels  = s.showDimLabels;
            _includeChildren = s.includeChildren;
            if (s.version >= 4)
            {
                _rulerVertexSnap = s.rulerVertexSnap;
                _rulerSnapPixelRadius = Mathf.Clamp(s.rulerSnapPixelRadius, 6f, 30f);
            }
            if (s.version >= 5)
            {
                _targetSizeMeters = new Vector3(
                    Mathf.Max(0f, s.targetSizeX),
                    Mathf.Max(0f, s.targetSizeY),
                    Mathf.Max(0f, s.targetSizeZ));
                _resizeX = s.resizeX;
                _resizeY = s.resizeY;
                _resizeZ = s.resizeZ;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MeasureTool] 加载设置失败，使用默认值。原因: {e.Message}");
        }
    }

    private void SavePrefs()
    {
        var s = new SavedState
        {
            version         = 5,
            mode            = (int)_mode,
            unit            = (int)_unit,
            lineR = _lineColor.r, lineG = _lineColor.g, lineB = _lineColor.b, lineA = _lineColor.a,
            dimR  = _dimColor.r,  dimG  = _dimColor.g,  dimB  = _dimColor.b,  dimA  = _dimColor.a,
            labelFontSize   = _labelFontSize,
            lineThickness   = _lineThickness,
            showWireframe   = _showWireframe,
            showDimLabels   = _showDimLabels,
            includeChildren = _includeChildren,
            rulerVertexSnap = _rulerVertexSnap,
            rulerSnapPixelRadius = _rulerSnapPixelRadius,
            targetSizeX     = _targetSizeMeters.x,
            targetSizeY     = _targetSizeMeters.y,
            targetSizeZ     = _targetSizeMeters.z,
            resizeX         = _resizeX,
            resizeY         = _resizeY,
            resizeZ         = _resizeZ,
        };
        EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));
    }

    // ════════════════ GUI ════════════════

    private void OnGUI()
    {
        // 自动清理已删除的物体引用
        _targetObjects.RemoveAll(go => go == null);

        _mainScrollPos = EditorGUILayout.BeginScrollView(_mainScrollPos);

        DrawModeToolbar();
        EditorGUILayout.Space(4);

        _foldDisplay = DrawDisplayFoldout(_foldDisplay);
        EditorGUILayout.Space(2);

        switch (_mode)
        {
            case MeasureMode.BoundingBox:
                _foldBBox = DrawBoundingBoxFoldout(_foldBBox);
                break;
            case MeasureMode.Ruler:
                _foldRuler = DrawRulerFoldout(_foldRuler);
                break;
        }

        // ── 署名 ──
        EditorGUILayout.Space(8);
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);

        EditorGUILayout.EndScrollView();
    }

    // ── 模式工具栏 ──

    private void DrawModeToolbar()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(Content("Mode", "测量模式"), GUILayout.Width(60));

        var names = new[]
        {
            Content("Bounding Box", "测量一个或多个物体的包围盒尺寸"),
            Content("Ruler", "在 Scene 视图中放置两个点测量距离"),
        };
        int idx = GUILayout.Toolbar((int)_mode, names);
        if (idx != (int)_mode)
        {
            _mode = (MeasureMode)idx;
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndHorizontal();
    }

    // ── 显示设置 ──

    private bool DrawDisplayFoldout(bool fold)
    {
        fold = EditorGUILayout.Foldout(fold, Content("Display Settings", "显示设置：单位、颜色、字号、线宽"), true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;
        EditorGUI.BeginChangeCheck();

        _unit = (DisplayUnit)EditorGUILayout.EnumPopup(Content("Unit", "显示单位：米/厘米/毫米"), _unit);
        _lineColor  = EditorGUILayout.ColorField(Content("Line Color", "测量线条的颜色"), _lineColor);
        _dimColor   = EditorGUILayout.ColorField(Content("Dimension Color", "分量分解线的颜色"), _dimColor);
        _labelFontSize = EditorGUILayout.IntSlider(Content("Font Size", "标签字号"), _labelFontSize, 10, 36);
        _lineThickness = EditorGUILayout.Slider(Content("Line Width", "线条粗细"), _lineThickness, 0.5f, 6f);

        if (EditorGUI.EndChangeCheck())
        {
            CleanupResources(); // 重建样式用
            SceneView.RepaintAll();
        }

        EditorGUILayout.HelpBox("Unity default: 1 unit = 1 meter", MessageType.None);
        EditorGUI.indentLevel--;
        return fold;
    }

    // ── 包围盒面板 ──

    private bool DrawBoundingBoxFoldout(bool fold)
    {
        fold = EditorGUILayout.Foldout(fold, Content("Bounding Box", "包围盒测量模式"), true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;

        EditorGUILayout.LabelField(Content("Target Objects", "目标物体列表"), EditorStyles.boldLabel);

        // 列表区域（超过 3 个物体时显示滚动条）
        EditorGUI.BeginChangeCheck();
        if (_targetObjects.Count > 3)
            _bboxScrollPos = EditorGUILayout.BeginScrollView(_bboxScrollPos, GUILayout.Height(100));

        int removeIndex = -1;
        for (int i = 0; i < _targetObjects.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            _targetObjects[i] = (GameObject)EditorGUILayout.ObjectField(
                _targetObjects[i], typeof(GameObject), true);
            if (GUILayout.Button(
                    Content("✕", "从目标列表中移除此物体"),
                    GUILayout.Width(24), GUILayout.Height(18)))
                removeIndex = i;
            EditorGUILayout.EndHorizontal();
        }
        if (removeIndex >= 0)
        {
            _targetObjects.RemoveAt(removeIndex);
            SceneView.RepaintAll();
        }

        if (_targetObjects.Count > 3)
            EditorGUILayout.EndScrollView();

        if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

        // 操作按钮
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(Content("Add Selected", "从 Hierarchy 当前选中添加物体"), GUILayout.Height(22)))
        {
            if (Selection.activeGameObject != null)
            {
                if (!_targetObjects.Contains(Selection.activeGameObject))
                {
                    _targetObjects.Add(Selection.activeGameObject);
                    SceneView.RepaintAll();
                }
            }
        }
        if (GUILayout.Button(Content("Clear All", "清除全部目标物体"), GUILayout.Height(22)))
        {
            _targetObjects.Clear();
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        _includeChildren = EditorGUILayout.ToggleLeft(
            Content("Include Children", "包含子物体的 Renderer 参与尺寸计算"),
            _includeChildren, GUILayout.Width(110));
        _showWireframe = EditorGUILayout.ToggleLeft(
            Content("Wireframe", "在 Scene 视图中显示包围盒线框"),
            _showWireframe, GUILayout.Width(80));
        _showDimLabels = EditorGUILayout.ToggleLeft(
            Content("Labels", "在 Scene 视图中显示尺寸标签"),
            _showDimLabels, GUILayout.Width(70));
        if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
        EditorGUILayout.EndHorizontal();

        // 显示测量结果
        if (_targetObjects.Count > 0 && TryGetCombinedBounds(_targetObjects, _includeChildren, out OrientedMeasurementBounds b))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Content("Results", "测量结果"), EditorStyles.boldLabel);
            Vector3 size = b.LocalBounds.size;
            EditorGUILayout.SelectableLabel($"  Width (X): {FormatValue(size.x)}");
            EditorGUILayout.SelectableLabel($"  Height (Y): {FormatValue(size.y)}");
            EditorGUILayout.SelectableLabel($"  Depth (Z): {FormatValue(size.z)}");
            float vol = size.x * size.y * size.z;
            EditorGUILayout.SelectableLabel($"  Volume: {FormatVolume(vol)}");
            EditorGUILayout.LabelField($"  Axes: {_targetObjects[0].name} local rotation", EditorStyles.miniLabel);

            EditorGUILayout.Space(6);
            DrawResizeTargetControls(b);
        }
        else if (_targetObjects.Count > 0)
        {
            EditorGUILayout.HelpBox("No Renderer found on target objects or their children.", MessageType.Warning);
        }

        EditorGUI.indentLevel--;
        return fold;
    }

    private void DrawResizeTargetControls(OrientedMeasurementBounds measurement)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(
            Content("Set Target Size", "将单个目标物体缩放到指定的实际测量尺寸"),
            EditorStyles.boldLabel);

        if (_targetObjects.Count != 1)
        {
            EditorGUILayout.HelpBox(
                "Set Target Size requires exactly one target object.",
                MessageType.Info);
            EditorGUILayout.EndVertical();
            return;
        }

        Vector3 currentSize = measurement.LocalBounds.size;
        EditorGUILayout.LabelField(
            $"Current: {FormatValue(currentSize.x)} × {FormatValue(currentSize.y)} × {FormatValue(currentSize.z)}",
            EditorStyles.miniLabel);

        _targetSizeMeters.x = DrawTargetSizeField("Width (X)", _resizeX, _targetSizeMeters.x, out _resizeX);
        _targetSizeMeters.y = DrawTargetSizeField("Height (Y)", _resizeY, _targetSizeMeters.y, out _resizeY);
        _targetSizeMeters.z = DrawTargetSizeField("Depth (Z)", _resizeZ, _targetSizeMeters.z, out _resizeZ);

        bool hasAxis = _resizeX || _resizeY || _resizeZ;
        bool valid =
            hasAxis &&
            (!_resizeX || (_targetSizeMeters.x > 0f && currentSize.x > Mathf.Epsilon)) &&
            (!_resizeY || (_targetSizeMeters.y > 0f && currentSize.y > Mathf.Epsilon)) &&
            (!_resizeZ || (_targetSizeMeters.z > 0f && currentSize.z > Mathf.Epsilon));

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(
                Content("Use Current Size", "将当前测量尺寸复制到目标尺寸输入框"),
                GUILayout.Height(24)))
        {
            _targetSizeMeters = currentSize;
        }

        using (new EditorGUI.DisabledScope(!valid))
        {
            if (GUILayout.Button(
                    Content("Apply Size", "调整目标物体的 Transform 缩放，使勾选轴达到指定尺寸；支持撤销"),
                    GUILayout.Height(24)))
            {
                ApplyTargetSize(measurement);
            }
        }
        EditorGUILayout.EndHorizontal();

        if (!hasAxis)
            EditorGUILayout.HelpBox("Enable at least one axis.", MessageType.Warning);
        else if (!valid)
            EditorGUILayout.HelpBox(
                "Enabled axes require a target value greater than zero and a measurable current size.",
                MessageType.Warning);

        EditorGUILayout.EndVertical();
    }

    private float DrawTargetSizeField(string label, bool enabled, float meters, out bool newEnabled)
    {
        EditorGUILayout.BeginHorizontal();
        newEnabled = GUILayout.Toggle(
            enabled,
            Content("", $"勾选后修改 {label}；取消勾选则保持该轴当前尺寸"),
            GUILayout.Width(18));

        float displayValue = ConvertFromMeters(meters);
        using (new EditorGUI.DisabledScope(!newEnabled))
        {
            displayValue = EditorGUILayout.FloatField(
                Content($"{label} ({GetUnitLabel()})", $"输入目标 {label}，单位为当前显示单位 {GetUnitLabel()}"),
                displayValue);
        }

        EditorGUILayout.EndHorizontal();
        return Mathf.Max(0f, ConvertToMeters(displayValue));
    }

    private void ApplyTargetSize(OrientedMeasurementBounds measurement)
    {
        if (_targetObjects.Count != 1 || _targetObjects[0] == null) return;

        Transform targetTransform = _targetObjects[0].transform;
        Vector3 currentSize = measurement.LocalBounds.size;
        Vector3 newScale = targetTransform.localScale;

        if (_resizeX)
            newScale.x *= _targetSizeMeters.x / currentSize.x;
        if (_resizeY)
            newScale.y *= _targetSizeMeters.y / currentSize.y;
        if (_resizeZ)
            newScale.z *= _targetSizeMeters.z / currentSize.z;

        Undo.RecordObject(targetTransform, "Set Measured Object Size");
        targetTransform.localScale = newScale;
        PrefabUtility.RecordPrefabInstancePropertyModifications(targetTransform);
        EditorUtility.SetDirty(targetTransform);

        SceneView.RepaintAll();
        Repaint();
    }

    // ── 标尺面板 ──

    private bool DrawRulerFoldout(bool fold)
    {
        fold = EditorGUILayout.Foldout(fold, Content("Ruler (Click in Scene)", "标尺模式：在 Scene 视图中点击放置测量点"), true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;

        EditorGUILayout.HelpBox(
            "Click in Scene to place start point → click again to place end point.\n" +
            "Move near a mesh vertex to snap automatically.\n" +
            "Drag arrow handles to adjust  |  Hold V: Unity vertex snapping\n" +
            "Esc: cancel / clear measurement",
            MessageType.Info);

        EditorGUILayout.Space(4);

        EditorGUI.BeginChangeCheck();
        _rulerVertexSnap = EditorGUILayout.ToggleLeft(
            Content("Auto Vertex Snap", "自动将标尺点吸附到鼠标附近可见网格的顶点"),
            _rulerVertexSnap);
        if (_rulerVertexSnap)
        {
            EditorGUI.indentLevel++;
            _rulerSnapPixelRadius = EditorGUILayout.Slider(
                Content("Snap Radius", "顶点吸附的检测半径，单位为 Scene 视图像素"),
                _rulerSnapPixelRadius, 6f, 30f);
            EditorGUI.indentLevel--;
        }
        if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

        EditorGUILayout.Space(4);

        if (_rulerA.HasValue)
            EditorGUILayout.SelectableLabel($"  Start: ({_rulerA.Value.x:F3}, {_rulerA.Value.y:F3}, {_rulerA.Value.z:F3})");
        if (_rulerB.HasValue)
            EditorGUILayout.SelectableLabel($"  End: ({_rulerB.Value.x:F3}, {_rulerB.Value.y:F3}, {_rulerB.Value.z:F3})");

        if (_rulerA.HasValue && _rulerB.HasValue)
        {
            Vector3 delta = _rulerB.Value - _rulerA.Value;
            float dist = Vector3.Distance(_rulerA.Value, _rulerB.Value);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(Content("Results", "测量结果"), EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"  Distance: {FormatValue(dist)}");
            EditorGUILayout.SelectableLabel($"  ΔX: {FormatValue(Mathf.Abs(delta.x))}");
            EditorGUILayout.SelectableLabel($"  ΔY: {FormatValue(Mathf.Abs(delta.y))}");
            EditorGUILayout.SelectableLabel($"  ΔZ: {FormatValue(Mathf.Abs(delta.z))}");
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button(Content("Clear Ruler", "清除标尺测量"), GUILayout.Height(24)))
        {
            _rulerA = null; _rulerB = null; _previewPoint = null; _previewSnapped = false;
            SceneView.RepaintAll();
        }

        EditorGUI.indentLevel--;
        return fold;
    }

    // ════════════════ Scene View 绘制 ════════════════

    private void OnSceneGUI(SceneView sv)
    {
        if (_mode == MeasureMode.Ruler)
            DrawRulerScene();
        else if (_mode == MeasureMode.BoundingBox)
            DrawBoundingBoxScene();
    }

    // ── 包围盒 Scene 绘制 ──

    private void DrawBoundingBoxScene()
    {
        if (_targetObjects.Count == 0) return;
        if (!TryGetCombinedBounds(_targetObjects, _includeChildren, out OrientedMeasurementBounds measurement)) return;

        Color prevColor = Handles.color;
        Matrix4x4 prevMatrix = Handles.matrix;
        Bounds bounds = measurement.LocalBounds;

        if (_showWireframe)
        {
            Handles.matrix = Matrix4x4.TRS(measurement.Origin, measurement.Rotation, Vector3.one);
            Handles.color = _lineColor;
            Handles.DrawWireCube(bounds.center, bounds.size);

            // 半透明实心面（帮助识别体积）
            Color fill = new Color(_lineColor.r, _lineColor.g, _lineColor.b, 0.08f);
            Handles.color = fill;
            Handles.DrawSolidRectangleWithOutline(
                new Vector3[]
                {
                    new Vector3(bounds.min.x, bounds.max.y, bounds.min.z),
                    new Vector3(bounds.max.x, bounds.max.y, bounds.min.z),
                    new Vector3(bounds.max.x, bounds.max.y, bounds.max.z),
                    new Vector3(bounds.min.x, bounds.max.y, bounds.max.z),
                },
                fill, Color.clear);
        }

        Handles.matrix = prevMatrix;

        if (_showDimLabels)
        {
            Handles.color = _lineColor;
            Vector3 topCenter = measurement.ToWorld(
                new Vector3(bounds.center.x, bounds.max.y, bounds.center.z));
            float labelOffset = HandleUtility.GetHandleSize(topCenter) * 0.15f;
            Vector3 labelPos = topCenter + measurement.Rotation * Vector3.up * labelOffset;

            string dimText = $"{FormatValue(bounds.size.x)} × {FormatValue(bounds.size.y)} × {FormatValue(bounds.size.z)}";
            DrawSceneLabel(labelPos, dimText);

            // 从 box 顶面到标签画一条连接线
            Handles.DrawDottedLine(topCenter, labelPos, 3f);
        }

        Handles.color = prevColor;
        Handles.matrix = prevMatrix;
    }

    // ── 标尺 Scene 绘制 ──

    private void DrawRulerScene()
    {
        Event e = Event.current;
        int controlId = GUIUtility.GetControlID(FocusType.Passive);

        // ── Layout: 注册默认控件以捕获空白点击 ──
        if (e.type == EventType.Layout)
        {
            HandleUtility.AddDefaultControl(controlId);
        }

        // ── Escape 取消 ──
        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            _rulerA = null;
            _rulerB = null;
            _previewPoint = null;
            _previewSnapped = false;
            e.Use();
            Repaint();
            return;
        }

        // ── 鼠标移动时更新预览 ──
        if (e.type == EventType.MouseMove)
        {
            if (_rulerA.HasValue && !_rulerB.HasValue)
            {
                _previewPoint = GetRulerPlacementPoint(e.mousePosition, _rulerA.Value.y, out _previewSnapped);
                SceneView.RepaintAll();
            }
        }

        // ── 点击放置 ──
        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            if (HandleUtility.nearestControl == controlId)
            {
                float planeY = _rulerA.HasValue && !_rulerB.HasValue ? _rulerA.Value.y : 0f;
                Vector3? hit = GetRulerPlacementPoint(e.mousePosition, planeY, out _);

                if (hit.HasValue)
                {
                    if (!_rulerA.HasValue || _rulerB.HasValue)
                    {
                        // 开始新测量
                        _rulerA = hit;
                        _rulerB = null;
                        _previewPoint = null;
                        _previewSnapped = false;
                    }
                    else if (!_rulerB.HasValue)
                    {
                        _rulerB = hit;
                        _previewPoint = null;
                        _previewSnapped = false;
                    }
                    e.Use();
                    Repaint();
                }
            }
        }

        // ── 绘制 ──

        Color prevColor = Handles.color;

        // 预览线（正在放置第二个点）
        if (_rulerA.HasValue && !_rulerB.HasValue && _previewPoint.HasValue)
        {
            Handles.color = new Color(_lineColor.r, _lineColor.g, _lineColor.b, 0.5f);
            Handles.DrawDottedLine(_rulerA.Value, _previewPoint.Value, 3f);

            float previewDist = Vector3.Distance(_rulerA.Value, _previewPoint.Value);
            Vector3 previewLabelPos = _previewPoint.Value + Vector3.up * HandleUtility.GetHandleSize(_previewPoint.Value) * 0.1f;
            DrawSceneLabel(previewLabelPos, FormatValue(previewDist));

            if (_previewSnapped)
            {
                Handles.color = Color.cyan;
                float dotSize = HandleUtility.GetHandleSize(_previewPoint.Value) * 0.06f;
                Handles.DotHandleCap(0, _previewPoint.Value, Quaternion.identity, dotSize, EventType.Repaint);
            }
        }

        // 测量线
        if (_rulerA.HasValue && _rulerB.HasValue)
        {
            Vector3 a = _rulerA.Value;
            Vector3 b = _rulerB.Value;

            Handles.color = _lineColor;
            Handles.DrawLine(a, b, _lineThickness);

            // 分量虚线（三轴分解: A → projX → projY → B）
            Handles.color = _dimColor;
            Vector3 projX = new Vector3(b.x, a.y, a.z);
            Vector3 projY = new Vector3(b.x, b.y, a.z);

            if (Vector3.Distance(a, projX) > 0.001f)
            {
                Handles.DrawDottedLine(a, projX, 3f);
                Handles.DrawDottedLine(projX, projY, 3f);
                Handles.DrawDottedLine(projY, b, 3f);
            }
            else if (Vector3.Distance(a, projY) > 0.001f)
            {
                Handles.DrawDottedLine(a, projY, 3f);
                Handles.DrawDottedLine(projY, b, 3f);
            }
            else if (Vector3.Distance(a, b) > 0.001f)
            {
                Handles.DrawDottedLine(a, b, 3f);
            }

            // 中点标签
            Vector3 mid = (a + b) * 0.5f;
            Vector3 delta = b - a;
            float dist = Vector3.Distance(a, b);
            string label = $"{FormatValue(dist)}\nΔX:{FormatValue(Mathf.Abs(delta.x))}  ΔY:{FormatValue(Mathf.Abs(delta.y))}  ΔZ:{FormatValue(Mathf.Abs(delta.z))}";
            DrawSceneLabel(mid + Vector3.up * HandleUtility.GetHandleSize(mid) * 0.08f, label);
        }

        // ── PositionHandle 拖拽（自带轴约束 + V 键顶点吸附）──
        if (_rulerA.HasValue)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 newA = Handles.PositionHandle(_rulerA.Value, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                if (_rulerVertexSnap && TryFindNearestVertex(e.mousePosition, out Vector3 snappedA))
                    newA = snappedA;
                _rulerA = newA;
                Repaint();
            }
        }

        if (_rulerB.HasValue)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 newB = Handles.PositionHandle(_rulerB.Value, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                if (_rulerVertexSnap && TryFindNearestVertex(e.mousePosition, out Vector3 snappedB))
                    newB = snappedB;
                _rulerB = newB;
                Repaint();
            }
        }

        Handles.color = prevColor;

        // 状态提示条
        DrawRulerStatusBar();
    }

    /// <summary>Scene View 左上角状态提示。</summary>
    private void DrawRulerStatusBar()
    {
        Handles.BeginGUI();
        string text;
        if (!_rulerA.HasValue)
            text = _rulerVertexSnap
                ? "Click in Scene to place start point  |  Auto vertex snap ON"
                : "Click in Scene to place start point  |  Hold V for vertex snap";
        else if (!_rulerB.HasValue)
            text = _previewSnapped
                ? "Geometry snapped  |  Click to place end point  |  Esc to cancel"
                : "Click in Scene to place end point  |  Esc to cancel  |  Move near a vertex to snap";
        else
            text = "Measurement complete  |  Click again to restart  |  Esc to clear  |  Drag handles to adjust";

        var style = new GUIStyle(EditorStyles.helpBox)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(12, 12, 6, 6),
        };

        GUIContent content = new GUIContent(text);
        Vector2 size = style.CalcSize(content);
        Rect rect = new Rect(8, 8, size.x + 4, size.y);
        GUI.Box(rect, content, style);
        Handles.EndGUI();
    }

    // ════════════════ 工具函数 ════════════════

    /// <summary>放置标尺点：依次尝试顶点吸附、碰撞体表面和水平面。</summary>
    private Vector3? GetRulerPlacementPoint(Vector2 mousePos, float planeY, out bool snapped)
    {
        snapped = false;

        if (_rulerVertexSnap && TryFindNearestVertex(mousePos, out Vector3 vertex))
        {
            snapped = true;
            return vertex;
        }

        if (HandleUtility.PlaceObject(mousePos, out Vector3 surfacePoint, out _))
        {
            snapped = true;
            return surfacePoint;
        }

        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);

        // 优先检测 Collider
        // 落到水平面
        Plane plane = new Plane(Vector3.up, new Vector3(0, planeY, 0));
        if (plane.Raycast(ray, out float enter))
            return ray.GetPoint(enter);

        return null;
    }

    /// <summary>在鼠标附近查找当前可见 Mesh 的最近顶点。</summary>
    private bool TryFindNearestVertex(Vector2 mousePos, out Vector3 nearestVertex)
    {
        nearestVertex = default;
        GameObject picked = HandleUtility.PickGameObject(mousePos, false);
        if (picked == null) return false;

        float bestDistanceSq = _rulerSnapPixelRadius * _rulerSnapPixelRadius;
        bool found = false;

        foreach (var filter in picked.GetComponentsInChildren<MeshFilter>())
        {
            if (filter.sharedMesh == null) continue;
            Renderer renderer = filter.GetComponent<Renderer>();
            if (renderer != null && (!renderer.enabled || !renderer.gameObject.activeInHierarchy)) continue;
            ConsiderMeshVertices(filter.sharedMesh, filter.transform, mousePos,
                ref bestDistanceSq, ref nearestVertex, ref found);
        }

        foreach (var skinned in picked.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (skinned.sharedMesh == null || !skinned.enabled || !skinned.gameObject.activeInHierarchy) continue;
            ConsiderMeshVertices(skinned.sharedMesh, skinned.transform, mousePos,
                ref bestDistanceSq, ref nearestVertex, ref found);
        }

        return found;
    }

    private void ConsiderMeshVertices(
        Mesh mesh,
        Transform meshTransform,
        Vector2 mousePos,
        ref float bestDistanceSq,
        ref Vector3 nearestVertex,
        ref bool found)
    {
        _snapVertexBuffer.Clear();
        try
        {
            mesh.GetVertices(_snapVertexBuffer);
        }
        catch (UnityException)
        {
            return;
        }

        for (int i = 0; i < _snapVertexBuffer.Count; i++)
        {
            Vector3 worldPoint = meshTransform.TransformPoint(_snapVertexBuffer[i]);
            Vector3 guiPoint = HandleUtility.WorldToGUIPointWithDepth(worldPoint);
            if (guiPoint.z <= 0f) continue;

            float distanceSq = ((Vector2)guiPoint - mousePos).sqrMagnitude;
            if (distanceSq > bestDistanceSq) continue;

            bestDistanceSq = distanceSq;
            nearestVertex = worldPoint;
            found = true;
        }
    }

    private struct OrientedMeasurementBounds
    {
        public Bounds LocalBounds;
        public Vector3 Origin;
        public Quaternion Rotation;

        public Vector3 ToWorld(Vector3 localPoint)
        {
            return Origin + Rotation * localPoint;
        }
    }

    private static bool TryGetCombinedBounds(
        List<GameObject> objects,
        bool includeChildren,
        out OrientedMeasurementBounds measurement)
    {
        measurement = default;
        if (objects == null || objects.Count == 0) return false;

        GameObject referenceObject = null;
        foreach (var go in objects)
        {
            if (go == null) continue;
            referenceObject = go;
            break;
        }
        if (referenceObject == null) return false;

        Vector3 origin = referenceObject.transform.position;
        Quaternion rotation = referenceObject.transform.rotation;
        Quaternion inverseRotation = Quaternion.Inverse(rotation);
        Bounds localBounds = default;
        bool found = false;

        foreach (var go in objects)
        {
            if (go == null) continue;

            var renderers = includeChildren
                ? go.GetComponentsInChildren<Renderer>()
                : go.GetComponents<Renderer>();

            foreach (var r in renderers)
            {
                if (r == null) continue;

                Bounds rendererBounds = r.localBounds;
                Vector3 center = rendererBounds.center;
                Vector3 extents = rendererBounds.extents;

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 localCorner = center + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    Vector3 worldCorner = r.transform.TransformPoint(localCorner);
                    Vector3 measurementPoint = inverseRotation * (worldCorner - origin);

                    if (!found)
                    {
                        localBounds = new Bounds(measurementPoint, Vector3.zero);
                        found = true;
                    }
                    else
                    {
                        localBounds.Encapsulate(measurementPoint);
                    }
                }
            }
        }

        if (found)
        {
            measurement = new OrientedMeasurementBounds
            {
                LocalBounds = localBounds,
                Origin = origin,
                Rotation = rotation,
            };
        }

        return found;
    }

    private float ConvertFromMeters(float meters)
    {
        return _unit switch
        {
            DisplayUnit.Centimeters => meters * 100f,
            DisplayUnit.Millimeters => meters * 1000f,
            _ => meters,
        };
    }

    private float ConvertToMeters(float value)
    {
        return _unit switch
        {
            DisplayUnit.Centimeters => value / 100f,
            DisplayUnit.Millimeters => value / 1000f,
            _ => value,
        };
    }

    private string GetUnitLabel()
    {
        return _unit switch
        {
            DisplayUnit.Centimeters => "cm",
            DisplayUnit.Millimeters => "mm",
            _ => "m",
        };
    }

    /// <summary>根据当前单位格式化距离值。</summary>
    private string FormatValue(float meters)
    {
        float converted = ConvertFromMeters(meters);
        string suffix = $" {GetUnitLabel()}";

        // 小数值多显示几位小数
        float abs = Mathf.Abs(converted);
        if (abs < 0.01f)
            return $"{converted:F4}{suffix}";
        if (abs < 1f)
            return $"{converted:F3}{suffix}";
        if (abs < 10f)
            return $"{converted:F2}{suffix}";
        return $"{converted:F1}{suffix}";
    }

    /// <summary>格式化体积。</summary>
    private string FormatVolume(float cubicMeters)
    {
        return _unit switch
        {
            DisplayUnit.Centimeters => $"{cubicMeters * 1_000_000f:F1} cm³",
            DisplayUnit.Millimeters => $"{cubicMeters * 1_000_000_000f:F1} mm³",
            _ => $"{cubicMeters:F3} m³",
        };
    }

    // ════════════════ Scene 标签绘制 ════════════════

    private void EnsureSceneLabelStyle()
    {
        if (_sceneLabelStyle != null && _labelBgTex != null) return;

        _labelBgTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        _labelBgTex.SetPixel(0, 0, new Color(0.1f, 0.1f, 0.1f, 0.75f));
        _labelBgTex.Apply();

        _sceneLabelStyle = new GUIStyle(EditorStyles.whiteBoldLabel)
        {
            fontSize  = _labelFontSize,
            alignment = TextAnchor.MiddleCenter,
            wordWrap  = true,
            normal = { textColor = Color.white, background = _labelBgTex },
            padding = new RectOffset(8, 8, 4, 4),
        };

        _cachedLabelContent = new GUIContent();
    }

    /// <summary>在 Scene View 中绘制带背景的标签。</summary>
    private void DrawSceneLabel(Vector3 worldPos, string text)
    {
        EnsureSceneLabelStyle();

        // 只绘制屏幕内的标签
        Vector3 screenPos = HandleUtility.WorldToGUIPoint(worldPos);
        if (screenPos.z < 0f) return;

        Handles.BeginGUI();
        _cachedLabelContent.text = text;
        Vector2 size = _sceneLabelStyle.CalcSize(_cachedLabelContent);
        size.x = Mathf.Max(size.x, 180f);
        Rect rect = new Rect(screenPos.x - size.x / 2f, screenPos.y - size.y / 2f, size.x, size.y);
        GUI.Box(rect, _cachedLabelContent, _sceneLabelStyle);
        Handles.EndGUI();
    }
}
