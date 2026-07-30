using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZXXTools.PathBuilder;

[CustomEditor(typeof(PathSpline))]
internal sealed class PathSplineEditor : Editor
{
    private PathSpline _builder;
    private int _selectedKnot;
    private bool _sceneChangePending;

    private SerializedProperty _closed;
    private SerializedProperty _autoRebuild;
    private SerializedProperty _samplesPerCurve;
    private SerializedProperty _projectToSurface;
    private SerializedProperty _surfaceMask;
    private SerializedProperty _rayHeight;
    private SerializedProperty _surfaceOffset;
    private SerializedProperty _mountainPrefabs;
    private SerializedProperty _randomSeed;
    private SerializedProperty _spacingRange;
    private SerializedProperty _brushWidth;
    private SerializedProperty _density;
    private SerializedProperty _horizontalScaleRange;
    private SerializedProperty _verticalScaleRange;
    private SerializedProperty _yawRange;
    private SerializedProperty _alignToSpline;
    private SerializedProperty _riverMaterial;
    private SerializedProperty _riverSegmentLength;
    private SerializedProperty _riverUvRepeatLength;
    private SerializedProperty _generateCollider;

    private void OnEnable()
    {
        _builder = (PathSpline)target;
        _builder.EnsureDefaults();
        CacheProperties();
        Undo.undoRedoPerformed += OnUndoRedo;

        EditorApplication.delayCall += RebuildMissingPreview;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    public override void OnInspectorGUI()
    {
        DrawAuthoringInspector();
        EditorGUILayout.Space(8f);
        DrawGenerationButtons();
    }

    internal void DrawAuthoringInspector()
    {
        serializedObject.Update();

        EditorGUI.BeginChangeCheck();
        DrawSplineSettings();
        EditorGUILayout.Space(5f);
        DrawModeSettings();
        EditorGUILayout.Space(5f);
        DrawPathEditing();
        bool serializedChanged = EditorGUI.EndChangeCheck();

        serializedObject.ApplyModifiedProperties();
        if (serializedChanged)
        {
            MarkBuilderChanged(true);
        }
    }

    internal void DrawOutputInspector()
    {
        serializedObject.Update();

        List<PathSample> samples =
            PathSplineUtility.BuildArcTable(_builder);
        float length = samples.Count > 0
            ? samples[samples.Count - 1].distance
            : 0f;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                "Type",
                _builder.mode == PathBuildMode.Mountain
                    ? "Mountain Instances"
                    : "River Mesh");
            EditorGUILayout.LabelField(
                "Path",
                $"{_builder.knots.Count} points · {length:F1} m");
            EditorGUILayout.LabelField(
                "Loop",
                _builder.closed ? "Closed" : "Open");
            EditorGUILayout.LabelField(
                "Preview",
                _builder.GeneratedRoot ? "Ready" : "Not generated");

            if (_builder.mode == PathBuildMode.Mountain)
            {
                int prefabCount = _builder.mountainPrefabs != null
                    ? _builder.mountainPrefabs.Count
                    : 0;
                EditorGUILayout.LabelField(
                    "Prefab Palette",
                    $"{prefabCount} entries");
            }
            else
            {
                EditorGUILayout.LabelField(
                    "Material",
                    _builder.riverMaterial
                        ? _builder.riverMaterial.name
                        : "None");
                float uvRepeatLength = Mathf.Max(
                    0.01f,
                    _builder.riverUvRepeatLength);
                EditorGUILayout.LabelField(
                    "UV Repeat Length",
                    $"{uvRepeatLength:F2} m");
                EditorGUILayout.LabelField(
                    "UV Length Range",
                    $"0 → {length / uvRepeatLength:F2}");
            }
        }

        EditorGUILayout.HelpBox(
            "使用窗口底部的 Preview 检查实时结果，确认后点击 Bake 输出。",
            MessageType.Info);

        if (_builder.mode == PathBuildMode.Mountain
            && GUILayout.Button("Randomize Mountain Seed"))
        {
            Undo.RecordObject(_builder, "Randomize Mountain Seed");
            _builder.randomSeed = Guid.NewGuid().GetHashCode();
            MarkBuilderChanged(true);
        }
    }

    private void DrawSplineSettings()
    {
        EditorGUILayout.LabelField("Path Builder", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(_closed, new GUIContent("Closed", "连接首尾控制点"));
            EditorGUILayout.PropertyField(
                _autoRebuild,
                new GUIContent("Auto Rebuild", "参数或样条修改后自动更新预览"));
            EditorGUILayout.PropertyField(
                _samplesPerCurve,
                new GUIContent("Curve Accuracy", "每段贝塞尔曲线的弧长采样精度"));
        }

        EditorGUILayout.LabelField("Surface", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(
                _projectToSurface,
                new GUIContent("Project To Surface", "向下投射到指定 Layer"));
            if (_projectToSurface.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_surfaceMask, new GUIContent("Surface Layer"));
                EditorGUILayout.PropertyField(_rayHeight, new GUIContent("Ray Height"));
                EditorGUILayout.PropertyField(_surfaceOffset, new GUIContent("Height Offset"));
                EditorGUI.indentLevel--;
            }
            else if (_builder.mode == PathBuildMode.River)
            {
                EditorGUILayout.PropertyField(
                    _surfaceOffset,
                    new GUIContent("Height Offset", "避免河流与地面 Z-Fighting"));
            }
        }
    }

    private void DrawModeSettings()
    {
        if (_builder.mode == PathBuildMode.Mountain)
            DrawMountainSettings();
        else
            DrawRiverSettings();
    }

    private void DrawMountainSettings()
    {
        EditorGUILayout.LabelField("Mountain", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(
                _mountainPrefabs,
                new GUIContent("Prefab Palette"),
                true);
            EditorGUILayout.PropertyField(_randomSeed, new GUIContent("Random Seed"));
            EditorGUILayout.PropertyField(
                _spacingRange,
                new GUIContent("Spacing Range", "沿样条的随机间隔"));
            EditorGUILayout.PropertyField(
                _brushWidth,
                new GUIContent("Scatter Width", "样条两侧的随机散布宽度"));
            EditorGUILayout.PropertyField(_density, new GUIContent("Density"));
            EditorGUILayout.PropertyField(
                _horizontalScaleRange,
                new GUIContent("XZ Scale Range"));
            EditorGUILayout.PropertyField(
                _verticalScaleRange,
                new GUIContent("Y Scale Range"));
            EditorGUILayout.PropertyField(_alignToSpline, new GUIContent("Align To Spline"));
            EditorGUILayout.PropertyField(
                _yawRange,
                new GUIContent(
                    _alignToSpline.boolValue ? "Yaw Offset Range" : "Yaw Range"));
        }
    }

    private void DrawRiverSettings()
    {
        EditorGUILayout.LabelField("River", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(_riverMaterial, new GUIContent("Material"));
            EditorGUILayout.PropertyField(
                _riverSegmentLength,
                new GUIContent(
                    "Mesh Segment Length",
                    "仅控制河流网格精度；越小越平滑、顶点越多，不会重置 UV"));
            EditorGUILayout.PropertyField(
                _riverUvRepeatLength,
                new GUIContent(
                    "UV Repeat Length",
                    "沿整条河流累计 UV；该距离对应长度方向 UV 从 0 到 1，超过 1 后由纹理 Repeat 重复"));

            List<PathSample> samples =
                PathSplineUtility.BuildArcTable(_builder);
            float pathLength = samples.Count > 0
                ? samples[samples.Count - 1].distance
                : 0f;
            float repeatLength = Mathf.Max(
                0.01f,
                _riverUvRepeatLength.floatValue);
            EditorGUILayout.LabelField(
                $"UV: 0 → {pathLength / repeatLength:F2}  "
                + $"({repeatLength:F2} m / repeat)",
                EditorStyles.miniLabel);
            EditorGUILayout.PropertyField(
                _generateCollider,
                new GUIContent("Generate Mesh Collider"));
        }
    }

    private void DrawPathEditing()
    {
        int knotCount = _builder.knots != null ? _builder.knots.Count : 0;
        _selectedKnot = Mathf.Clamp(_selectedKnot, 0, Mathf.Max(0, knotCount - 1));

        EditorGUILayout.LabelField(
            $"Control Points ({knotCount})",
            EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add"))
                    AddKnot();
                using (new EditorGUI.DisabledScope(knotCount == 0))
                {
                    if (GUILayout.Button("Insert After"))
                        InsertKnot();
                }
                using (new EditorGUI.DisabledScope(knotCount <= 2))
                {
                    if (GUILayout.Button("Remove"))
                        RemoveKnot();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(knotCount == 0))
                {
                    if (GUILayout.Button("Smooth Selected"))
                        SmoothSelectedKnot();
                    if (GUILayout.Button("Reverse"))
                        ReverseSpline();
                    if (GUILayout.Button("Frame"))
                        FrameSelectedKnot();
                }
            }

            if (knotCount > 0)
                DrawSelectedKnotFields(_builder.knots[_selectedKnot]);
        }

        EditorGUILayout.HelpBox(
            "Scene 视图：点击圆点选择；拖动位置、黄色切线和河宽手柄。"
            + "完成初次绘制后，Shift + 左键可继续追加控制点，Delete 删除当前点。",
            MessageType.Info);
    }

    private void DrawSelectedKnotFields(PathKnot knot)
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField($"Selected Point {_selectedKnot}", EditorStyles.miniBoldLabel);

        Vector3 position = EditorGUILayout.Vector3Field("Position", knot.position);
        bool broken = EditorGUILayout.Toggle(
            new GUIContent("Broken Tangents", "关闭后两侧切线镜像联动"),
            knot.brokenTangents);
        Vector3 tangentIn = EditorGUILayout.Vector3Field("Tangent In", knot.tangentIn);
        Vector3 tangentOut = EditorGUILayout.Vector3Field("Tangent Out", knot.tangentOut);
        float width = knot.width;
        if (_builder.mode == PathBuildMode.River)
            width = EditorGUILayout.FloatField("River Width", knot.width);

        if (position == knot.position
            && broken == knot.brokenTangents
            && tangentIn == knot.tangentIn
            && tangentOut == knot.tangentOut
            && Mathf.Approximately(width, knot.width))
        {
            return;
        }

        bool tangentInChanged = tangentIn != knot.tangentIn;
        bool tangentOutChanged = tangentOut != knot.tangentOut;
        Undo.RecordObject(_builder, "Edit Spline Point");
        knot.position = position;
        knot.brokenTangents = broken;
        knot.tangentIn = tangentIn;
        knot.tangentOut = tangentOut;
        knot.width = Mathf.Max(0.01f, width);
        if (!knot.brokenTangents)
        {
            if (tangentOutChanged)
                knot.tangentIn = -tangentOut;
            else if (tangentInChanged)
                knot.tangentOut = -tangentIn;
        }
        MarkBuilderChanged(true);
    }

    private void DrawGenerationButtons()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.backgroundColor = new Color(0.5f, 0.85f, 0.5f);
            if (GUILayout.Button("Rebuild Preview", GUILayout.Height(32f)))
                PathBuilderGenerator.Rebuild(_builder);
            GUI.backgroundColor = Color.white;

            if (GUILayout.Button("Clear Preview", GUILayout.Height(32f)))
                PathBuilderGenerator.ClearGenerated(_builder);
        }

        if (_builder.mode == PathBuildMode.Mountain)
        {
            if (GUILayout.Button("Randomize Seed"))
            {
                Undo.RecordObject(_builder, "Randomize Mountain Seed");
                _builder.randomSeed = Guid.NewGuid().GetHashCode();
                MarkBuilderChanged(true);
            }
        }

        GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
        if (GUILayout.Button(
            new GUIContent(
                "Bake Generated Result",
                "保留当前结果并与样条编辑器分离；河流 Mesh 会保存为资产"),
            GUILayout.Height(32f)))
        {
            PathBuilderGenerator.Bake(_builder);
        }
        GUI.backgroundColor = Color.white;
    }

    private void OnSceneGUI()
    {
        DrawSceneGUI();
    }

    internal void DrawSceneGUI()
    {
        if (!_builder || !_builder.Anchor
            || _builder.knots == null || _builder.knots.Count < 2)
            return;

        DrawSplineCurve();
        if (_builder.mode == PathBuildMode.River)
            DrawRiverWidthPreview();
        DrawKnotButtons();
        DrawSelectedKnotHandles();
        HandleSceneShortcuts();

        Event current = Event.current;
        if (_sceneChangePending
            && _builder.autoRebuild
            && (current.type == EventType.MouseUp
                || current.type == EventType.KeyUp))
        {
            _sceneChangePending = false;
            PathBuilderGenerator.Rebuild(_builder, false);
        }
    }

    private void DrawSplineCurve()
    {
        Color previous = Handles.color;
        for (int i = 0; i < _builder.SegmentCount; i++)
        {
            int nextIndex = (i + 1) % _builder.knots.Count;
            PathKnot start = _builder.knots[i];
            PathKnot end = _builder.knots[nextIndex];
            Vector3 p0 = _builder.Anchor.TransformPoint(start.position);
            Vector3 p1 = _builder.Anchor.TransformPoint(
                start.position + start.tangentOut);
            Vector3 p2 = _builder.Anchor.TransformPoint(
                end.position + end.tangentIn);
            Vector3 p3 = _builder.Anchor.TransformPoint(end.position);
            Handles.DrawBezier(
                p0,
                p3,
                p1,
                p2,
                _builder.mode == PathBuildMode.River
                    ? new Color(0.1f, 0.7f, 1f, 1f)
                    : new Color(0.75f, 0.5f, 0.15f, 1f),
                null,
                3f);
        }
        Handles.color = previous;
    }

    private void DrawRiverWidthPreview()
    {
        List<PathSample> table =
            PathSplineUtility.BuildArcTable(_builder);
        if (table.Count < 2)
            return;

        Vector3[] left = new Vector3[table.Count];
        Vector3[] right = new Vector3[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            Vector3 forward = table[i].tangent;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.000001f)
                forward = Vector3.forward;
            forward.Normalize();
            Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
            float halfWidth = table[i].width * 0.5f;
            left[i] = table[i].position - side * halfWidth;
            right[i] = table[i].position + side * halfWidth;
        }

        Color previous = Handles.color;
        Handles.color = new Color(0.1f, 0.65f, 1f, 0.7f);
        Handles.DrawAAPolyLine(2f, left);
        Handles.DrawAAPolyLine(2f, right);
        Handles.color = previous;
    }

    private void DrawKnotButtons()
    {
        for (int i = 0; i < _builder.knots.Count; i++)
        {
            Vector3 worldPosition =
                _builder.Anchor.TransformPoint(_builder.knots[i].position);
            float size = HandleUtility.GetHandleSize(worldPosition) * 0.09f;
            Color previous = Handles.color;
            Handles.color = i == _selectedKnot
                ? Color.yellow
                : Color.white;
            if (Handles.Button(
                worldPosition,
                Quaternion.identity,
                size,
                size * 1.25f,
                Handles.SphereHandleCap))
            {
                _selectedKnot = i;
                Repaint();
            }
            Handles.color = previous;
        }
    }

    private void DrawSelectedKnotHandles()
    {
        _selectedKnot = Mathf.Clamp(
            _selectedKnot,
            0,
            _builder.knots.Count - 1);
        PathKnot knot = _builder.knots[_selectedKnot];
        Transform transform = _builder.Anchor;
        Vector3 worldPosition = transform.TransformPoint(knot.position);

        EditorGUI.BeginChangeCheck();
        Vector3 movedPosition =
            Handles.PositionHandle(worldPosition, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_builder, "Move Spline Point");
            knot.position = transform.InverseTransformPoint(movedPosition);
            MarkBuilderChanged(false);
            worldPosition = movedPosition;
        }

        DrawTangentHandles(knot, worldPosition);
        if (_builder.mode == PathBuildMode.River)
            DrawWidthHandles(knot, worldPosition);

        Handles.Label(
            worldPosition
                + Vector3.up * HandleUtility.GetHandleSize(worldPosition) * 0.15f,
            _builder.mode == PathBuildMode.River
                ? $"{_selectedKnot}  W:{knot.width:F2}"
                : _selectedKnot.ToString());
    }

    private void DrawTangentHandles(
        PathKnot knot,
        Vector3 worldPosition)
    {
        Transform transform = _builder.Anchor;
        Vector3 inWorld =
            transform.TransformPoint(knot.position + knot.tangentIn);
        Vector3 outWorld =
            transform.TransformPoint(knot.position + knot.tangentOut);
        float inSize = HandleUtility.GetHandleSize(inWorld) * 0.075f;
        float outSize = HandleUtility.GetHandleSize(outWorld) * 0.075f;
        Color previous = Handles.color;
        Handles.color = new Color(1f, 0.75f, 0.1f, 1f);
        Handles.DrawLine(worldPosition, inWorld);
        Handles.DrawLine(worldPosition, outWorld);

        EditorGUI.BeginChangeCheck();
        Vector3 movedIn = Handles.FreeMoveHandle(
            inWorld,
            inSize,
            Vector3.zero,
            Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_builder, "Move Spline Tangent");
            knot.tangentIn =
                transform.InverseTransformPoint(movedIn) - knot.position;
            if (!knot.brokenTangents)
                knot.tangentOut = -knot.tangentIn;
            MarkBuilderChanged(false);
        }

        EditorGUI.BeginChangeCheck();
        Vector3 movedOut = Handles.FreeMoveHandle(
            outWorld,
            outSize,
            Vector3.zero,
            Handles.CubeHandleCap);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_builder, "Move Spline Tangent");
            knot.tangentOut =
                transform.InverseTransformPoint(movedOut) - knot.position;
            if (!knot.brokenTangents)
                knot.tangentIn = -knot.tangentOut;
            MarkBuilderChanged(false);
        }
        Handles.color = previous;
    }

    private void DrawWidthHandles(
        PathKnot knot,
        Vector3 worldPosition)
    {
        Vector3 tangent = _builder.Anchor.TransformVector(knot.tangentOut);
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.000001f)
            tangent = Vector3.forward;
        tangent.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, tangent).normalized;
        float halfWidth = knot.width * 0.5f;
        float size = HandleUtility.GetHandleSize(worldPosition) * 0.08f;
        Vector3 rightPosition = worldPosition + right * halfWidth;
        Vector3 leftPosition = worldPosition - right * halfWidth;
        Color previous = Handles.color;
        Handles.color = new Color(0.1f, 0.8f, 1f, 1f);
        Handles.DrawLine(leftPosition, rightPosition);

        EditorGUI.BeginChangeCheck();
        Vector3 movedRight = Handles.Slider(
            rightPosition,
            right,
            size,
            Handles.CubeHandleCap,
            0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_builder, "Change River Width");
            knot.width = Mathf.Max(
                0.01f,
                Vector3.Dot(movedRight - worldPosition, right) * 2f);
            MarkBuilderChanged(false);
        }

        EditorGUI.BeginChangeCheck();
        Vector3 movedLeft = Handles.Slider(
            leftPosition,
            -right,
            size,
            Handles.CubeHandleCap,
            0f);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(_builder, "Change River Width");
            knot.width = Mathf.Max(
                0.01f,
                Vector3.Dot(movedLeft - worldPosition, -right) * 2f);
            MarkBuilderChanged(false);
        }
        Handles.color = previous;
    }

    private void HandleSceneShortcuts()
    {
        Event current = Event.current;
        if (current.type == EventType.Layout && current.shift)
        {
            HandleUtility.AddDefaultControl(
                GUIUtility.GetControlID(FocusType.Passive));
        }

        if (current.type == EventType.MouseDown
            && current.button == 0
            && current.shift
            && !current.alt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(current.mousePosition);
            if (TryGetAppendPosition(ray, out Vector3 worldPosition))
            {
                Undo.RecordObject(_builder, "Append Spline Point");
                _selectedKnot = _builder.AppendKnot(
                    _builder.Anchor.InverseTransformPoint(worldPosition));
                MarkBuilderChanged(true);
                current.Use();
            }
        }

        if (current.type == EventType.KeyDown
            && (current.keyCode == KeyCode.Delete
                || current.keyCode == KeyCode.Backspace)
            && _builder.knots.Count > 2)
        {
            RemoveKnot();
            current.Use();
        }
    }

    private bool TryGetAppendPosition(Ray ray, out Vector3 worldPosition)
    {
        if (_builder.projectToSurface
            && Physics.Raycast(
                ray,
                out RaycastHit hit,
                Mathf.Infinity,
                _builder.surfaceMask,
                QueryTriggerInteraction.Ignore))
        {
            worldPosition = hit.point + Vector3.up * _builder.surfaceOffset;
            return true;
        }

        Plane plane = new Plane(Vector3.up, _builder.Anchor.position);
        if (plane.Raycast(ray, out float distance))
        {
            worldPosition = ray.GetPoint(distance);
            return true;
        }

        worldPosition = Vector3.zero;
        return false;
    }

    private void AddKnot()
    {
        Undo.RecordObject(_builder, "Add Spline Point");
        Vector3 localPosition;
        if (_builder.knots.Count == 0)
        {
            localPosition = Vector3.zero;
        }
        else
        {
            PathKnot last =
                _builder.knots[_builder.knots.Count - 1];
            Vector3 direction = last.tangentOut;
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.right;
            localPosition = last.position + direction.normalized * 5f;
        }
        _selectedKnot = _builder.AppendKnot(localPosition);
        MarkBuilderChanged(true);
    }

    private void InsertKnot()
    {
        Undo.RecordObject(_builder, "Insert Spline Point");
        _selectedKnot = _builder.InsertAfter(_selectedKnot);
        MarkBuilderChanged(true);
    }

    private void RemoveKnot()
    {
        if (_builder.knots.Count <= 2)
            return;
        Undo.RecordObject(_builder, "Remove Spline Point");
        _builder.RemoveKnot(_selectedKnot);
        _selectedKnot = Mathf.Clamp(
            _selectedKnot,
            0,
            _builder.knots.Count - 1);
        MarkBuilderChanged(true);
    }

    private void SmoothSelectedKnot()
    {
        Undo.RecordObject(_builder, "Smooth Spline Point");
        _builder.SmoothKnot(_selectedKnot);
        MarkBuilderChanged(true);
    }

    private void ReverseSpline()
    {
        Undo.RecordObject(_builder, "Reverse Spline");
        _builder.Reverse();
        _selectedKnot = _builder.knots.Count - 1 - _selectedKnot;
        MarkBuilderChanged(true);
    }

    private void FrameSelectedKnot()
    {
        if (!SceneView.lastActiveSceneView || _builder.knots.Count == 0)
            return;
        Vector3 worldPosition = _builder.Anchor.TransformPoint(
            _builder.knots[_selectedKnot].position);
        SceneView.lastActiveSceneView.LookAt(
            worldPosition,
            SceneView.lastActiveSceneView.rotation,
            10f);
    }

    private void MarkBuilderChanged(bool rebuildImmediately)
    {
        EditorUtility.SetDirty(_builder);
        if (_builder.Anchor)
            EditorSceneManager.MarkSceneDirty(_builder.Anchor.gameObject.scene);
        SceneView.RepaintAll();

        if (!_builder.autoRebuild)
            return;
        if (rebuildImmediately)
        {
            _sceneChangePending = false;
            PathBuilderGenerator.Rebuild(_builder, false);
        }
        else
        {
            _sceneChangePending = true;
        }
    }

    private void OnUndoRedo()
    {
        if (_builder && _builder.autoRebuild)
            PathBuilderGenerator.Rebuild(_builder, false);
        SceneView.RepaintAll();
        Repaint();
    }

    private void RebuildMissingPreview()
    {
        if (!_builder || !_builder.autoRebuild)
            return;
        if (!_builder.Anchor || !_builder.Anchor.gameObject.scene.IsValid())
            return;

        bool missing = !_builder.GeneratedRoot;
        if (!missing && _builder.mode == PathBuildMode.River)
        {
            MeshFilter filter = _builder.GeneratedRoot.GetComponent<MeshFilter>();
            missing = !filter || !filter.sharedMesh;
        }
        if (missing)
            PathBuilderGenerator.Rebuild(_builder, false);
    }

    private void CacheProperties()
    {
        _closed = serializedObject.FindProperty("closed");
        _autoRebuild = serializedObject.FindProperty("autoRebuild");
        _samplesPerCurve = serializedObject.FindProperty("samplesPerCurve");
        _projectToSurface = serializedObject.FindProperty("projectToSurface");
        _surfaceMask = serializedObject.FindProperty("surfaceMask");
        _rayHeight = serializedObject.FindProperty("rayHeight");
        _surfaceOffset = serializedObject.FindProperty("surfaceOffset");
        _mountainPrefabs = serializedObject.FindProperty("mountainPrefabs");
        _randomSeed = serializedObject.FindProperty("randomSeed");
        _spacingRange = serializedObject.FindProperty("spacingRange");
        _brushWidth = serializedObject.FindProperty("brushWidth");
        _density = serializedObject.FindProperty("density");
        _horizontalScaleRange =
            serializedObject.FindProperty("horizontalScaleRange");
        _verticalScaleRange =
            serializedObject.FindProperty("verticalScaleRange");
        _yawRange = serializedObject.FindProperty("yawRange");
        _alignToSpline = serializedObject.FindProperty("alignToSpline");
        _riverMaterial = serializedObject.FindProperty("riverMaterial");
        _riverSegmentLength =
            serializedObject.FindProperty("riverSegmentLength");
        _riverUvRepeatLength =
            serializedObject.FindProperty("riverUvRepeatLength");
        _generateCollider =
            serializedObject.FindProperty("generateCollider");
    }
}
