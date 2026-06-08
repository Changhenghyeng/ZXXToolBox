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

    // 标尺
    private Vector3? _rulerA;
    private Vector3? _rulerB;
    private Vector3? _previewPoint;       // 鼠标悬停预览点

    // 面板折叠
    private bool _foldDisplay  = true;
    private bool _foldBBox     = true;
    private bool _foldRuler    = true;

    // Scene 标签样式（延迟初始化）
    private GUIStyle  _sceneLabelStyle;
    private Texture2D _labelBgTex;
    private GUIContent _cachedLabelContent; // 减少分配

    // ── EditorPrefs Key ──
    private const string PrefKey = "MeasureTool.Settings_v3";

    // ════════════════ 窗口入口 ════════════════

    public static void Open()
    {
        var w = GetWindow<MeasureTool>("测量工具");
        w.minSize = new Vector2(340, 400);
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
        public int mode;
        public int unit;
        public float lineR, lineG, lineB, lineA;
        public float dimR, dimG, dimB, dimA;
        public int labelFontSize;
        public float lineThickness;
        public bool showWireframe;
        public bool showDimLabels;
        public bool includeChildren;
    }

    private void LoadPrefs()
    {
        string json = EditorPrefs.GetString(PrefKey, "");
        if (string.IsNullOrEmpty(json))
        {
            // 尝试迁移旧版数据（v1 / v2）
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
            mode            = (int)_mode,
            unit            = (int)_unit,
            lineR = _lineColor.r, lineG = _lineColor.g, lineB = _lineColor.b, lineA = _lineColor.a,
            dimR  = _dimColor.r,  dimG  = _dimColor.g,  dimB  = _dimColor.b,  dimA  = _dimColor.a,
            labelFontSize   = _labelFontSize,
            lineThickness   = _lineThickness,
            showWireframe   = _showWireframe,
            showDimLabels   = _showDimLabels,
            includeChildren = _includeChildren,
        };
        EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));
    }

    // ════════════════ GUI ════════════════

    private void OnGUI()
    {
        // 自动清理已删除的物体引用
        _targetObjects.RemoveAll(go => go == null);

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
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    // ── 模式工具栏 ──

    private void DrawModeToolbar()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("测量模式", GUILayout.Width(60));

        var names = new[] { "包围盒", "标尺" };
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
        fold = EditorGUILayout.Foldout(fold, "显示设置", true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;
        EditorGUI.BeginChangeCheck();

        _unit = (DisplayUnit)EditorGUILayout.EnumPopup("单位", _unit);
        _lineColor  = EditorGUILayout.ColorField("线条颜色", _lineColor);
        _dimColor   = EditorGUILayout.ColorField("分量颜色", _dimColor);
        _labelFontSize = EditorGUILayout.IntSlider("字号", _labelFontSize, 10, 36);
        _lineThickness = EditorGUILayout.Slider("线宽", _lineThickness, 0.5f, 6f);

        if (EditorGUI.EndChangeCheck())
        {
            CleanupResources(); // 重建样式用
            SceneView.RepaintAll();
        }

        EditorGUILayout.HelpBox("Unity 默认 1 unit = 1 米", MessageType.None);
        EditorGUI.indentLevel--;
        return fold;
    }

    // ── 包围盒面板 ──

    private bool DrawBoundingBoxFoldout(bool fold)
    {
        fold = EditorGUILayout.Foldout(fold, "包围盒测量", true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;

        EditorGUILayout.LabelField("目标物体", EditorStyles.boldLabel);

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
            if (GUILayout.Button("✕", GUILayout.Width(24), GUILayout.Height(18)))
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
        if (GUILayout.Button("从选中添加", GUILayout.Height(22)))
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
        if (GUILayout.Button("清除全部", GUILayout.Height(22)))
        {
            _targetObjects.Clear();
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        _includeChildren = EditorGUILayout.ToggleLeft("包含子物体", _includeChildren, GUILayout.Width(100));
        _showWireframe   = EditorGUILayout.ToggleLeft("线框", _showWireframe, GUILayout.Width(60));
        _showDimLabels   = EditorGUILayout.ToggleLeft("尺寸标签", _showDimLabels, GUILayout.Width(80));
        if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();
        EditorGUILayout.EndHorizontal();

        // 显示测量结果
        if (_targetObjects.Count > 0 && TryGetCombinedBounds(_targetObjects, _includeChildren, out Bounds b))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("测量结果", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"  宽 (X): {FormatValue(b.size.x)}");
            EditorGUILayout.SelectableLabel($"  高 (Y): {FormatValue(b.size.y)}");
            EditorGUILayout.SelectableLabel($"  深 (Z): {FormatValue(b.size.z)}");
            float vol = b.size.x * b.size.y * b.size.z;
            EditorGUILayout.SelectableLabel($"  体积:   {FormatVolume(vol)}");
        }
        else if (_targetObjects.Count > 0)
        {
            EditorGUILayout.HelpBox("列表中所有物体及其子节点没有 Renderer 组件，无法计算包围盒。", MessageType.Warning);
        }

        EditorGUI.indentLevel--;
        return fold;
    }

    // ── 标尺面板 ──

    private bool DrawRulerFoldout(bool fold)
    {
        fold = EditorGUILayout.Foldout(fold, "标尺（Scene 点击放置）", true);
        if (!fold) return fold;

        EditorGUI.indentLevel++;

        EditorGUILayout.HelpBox(
            "点击 Scene 空白处放置起点 → 再次点击放置终点\n" +
            "拖拽: 拖动箭头手柄沿轴移动  |  按住 V: Unity 顶点吸附\n" +
            "Esc: 取消/清除测量",
            MessageType.Info);

        EditorGUILayout.Space(4);

        if (_rulerA.HasValue)
            EditorGUILayout.SelectableLabel($"  起点: ({_rulerA.Value.x:F3}, {_rulerA.Value.y:F3}, {_rulerA.Value.z:F3})");
        if (_rulerB.HasValue)
            EditorGUILayout.SelectableLabel($"  终点: ({_rulerB.Value.x:F3}, {_rulerB.Value.y:F3}, {_rulerB.Value.z:F3})");

        if (_rulerA.HasValue && _rulerB.HasValue)
        {
            Vector3 delta = _rulerB.Value - _rulerA.Value;
            float dist = Vector3.Distance(_rulerA.Value, _rulerB.Value);
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("测量结果", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel($"  直线距离: {FormatValue(dist)}");
            EditorGUILayout.SelectableLabel($"  ΔX: {FormatValue(Mathf.Abs(delta.x))}");
            EditorGUILayout.SelectableLabel($"  ΔY: {FormatValue(Mathf.Abs(delta.y))}");
            EditorGUILayout.SelectableLabel($"  ΔZ: {FormatValue(Mathf.Abs(delta.z))}");
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("清除标尺", GUILayout.Height(24)))
        {
            _rulerA = null; _rulerB = null; _previewPoint = null;
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
        if (!TryGetCombinedBounds(_targetObjects, _includeChildren, out Bounds bounds)) return;

        Color prevColor = Handles.color;

        if (_showWireframe)
        {
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

        if (_showDimLabels)
        {
            Handles.color = _lineColor;
            float labelH = bounds.max.y + HandleUtility.GetHandleSize(bounds.center) * 0.15f;
            Vector3 topCenter = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            Vector3 labelPos = new Vector3(bounds.center.x, labelH, bounds.center.z);

            string dimText = $"{FormatValue(bounds.size.x)} × {FormatValue(bounds.size.y)} × {FormatValue(bounds.size.z)}";
            DrawSceneLabel(labelPos, dimText);

            // 从 box 顶面到标签画一条连接线
            Handles.DrawDottedLine(topCenter, labelPos, 3f);
        }

        Handles.color = prevColor;
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
            e.Use();
            Repaint();
            return;
        }

        // ── 鼠标移动时更新预览 ──
        if (e.type == EventType.MouseMove)
        {
            if (_rulerA.HasValue && !_rulerB.HasValue)
            {
                _previewPoint = RaycastPlacementPoint(e.mousePosition, _rulerA.Value.y);
                SceneView.RepaintAll();
            }
        }

        // ── 点击放置 ──
        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            if (HandleUtility.nearestControl == controlId)
            {
                float planeY = _rulerA.HasValue && !_rulerB.HasValue ? _rulerA.Value.y : 0f;
                Vector3? hit = RaycastPlacementPoint(e.mousePosition, planeY);

                if (hit.HasValue)
                {
                    if (!_rulerA.HasValue || _rulerB.HasValue)
                    {
                        // 开始新测量
                        _rulerA = hit;
                        _rulerB = null;
                        _previewPoint = null;
                    }
                    else if (!_rulerB.HasValue)
                    {
                        _rulerB = hit;
                        _previewPoint = null;
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
            if (EditorGUI.EndChangeCheck()) { _rulerA = newA; Repaint(); }
        }

        if (_rulerB.HasValue)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 newB = Handles.PositionHandle(_rulerB.Value, Quaternion.identity);
            if (EditorGUI.EndChangeCheck()) { _rulerB = newB; Repaint(); }
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
            text = "⏺ 点击 Scene 放置起点  |  按住 V 吸附顶点";
        else if (!_rulerB.HasValue)
            text = "⏺ 点击 Scene 放置终点  |  Esc 取消  |  按住 V 吸附顶点";
        else
            text = "✅ 测量完成  |  再次点击重新开始  |  Esc 清除  |  拖拽箭头微调";

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

    /// <summary>射线检测放置点：优先碰撞体，其次水平面。</summary>
    private static Vector3? RaycastPlacementPoint(Vector2 mousePos, float planeY)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(mousePos);

        // 优先检测 Collider
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
            return hit.point;

        // 落到水平面
        Plane plane = new Plane(Vector3.up, new Vector3(0, planeY, 0));
        if (plane.Raycast(ray, out float enter))
            return ray.GetPoint(enter);

        return null;
    }

    /// <summary>合并多个物体所有 Renderer 的世界空间包围盒。</summary>
    private static bool TryGetCombinedBounds(List<GameObject> objects, bool includeChildren, out Bounds bounds)
    {
        bounds = default;
        if (objects == null || objects.Count == 0) return false;

        bool found = false;
        foreach (var go in objects)
        {
            if (go == null) continue;

            var renderers = includeChildren
                ? go.GetComponentsInChildren<Renderer>()
                : go.GetComponents<Renderer>();

            foreach (var r in renderers)
            {
                if (!found)
                {
                    bounds = r.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }
        }
        return found;
    }

    /// <summary>根据当前单位格式化距离值。</summary>
    private string FormatValue(float meters)
    {
        float converted = _unit switch
        {
            DisplayUnit.Centimeters => meters * 100f,
            DisplayUnit.Millimeters => meters * 1000f,
            _ => meters,
        };

        string suffix = _unit switch
        {
            DisplayUnit.Centimeters => " cm",
            DisplayUnit.Millimeters => " mm",
            _ => " m",
        };

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
