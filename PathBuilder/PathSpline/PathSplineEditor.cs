using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ZXXTools.PathBuilder;

[CustomEditor(typeof(PathSpline))]
internal sealed class PathSplineEditor : Editor
{
    private static readonly int BoxSelectionControlHash =
        "PathBuilderBoxSelection".GetHashCode();
    private const float BoxSelectionDragThreshold = 4f;
    private const double RiverLiveRebuildInterval = 1d / 30d;
    private const double EdgeObjectLiveRebuildInterval = 1d / 8d;
    private const double MountainLiveRebuildInterval = 1d / 12d;
    private static readonly PathTangentMode[] TangentModeValues =
    {
        PathTangentMode.Linear,
        PathTangentMode.Auto,
        PathTangentMode.Mirrored,
        PathTangentMode.Continuous,
        PathTangentMode.Broken
    };
    private static readonly string[] TangentModeLabels =
    {
        "Linear",
        "Auto",
        "Mirrored",
        "Continuous",
        "Broken"
    };

    private PathSpline _builder;
    private int _selectedKnot;
    private readonly HashSet<int> _selectedKnots = new HashSet<int>();
    private int _selectionVersion;
    private bool _sceneChangePending;
    private double _lastLiveRebuildTime;
    private Tool _groupHandleTool = Tool.None;
    private int _groupHandleSelectionVersion = -1;
    private Vector3 _groupHandlePivot;
    private Quaternion _groupHandleFrame = Quaternion.identity;
    private readonly Dictionary<int, KnotHandleState> _groupHandleStates =
        new Dictionary<int, KnotHandleState>();
    private Quaternion _rotationHandleValue = Quaternion.identity;
    private Quaternion _rotationHandleStart = Quaternion.identity;
    private Vector3 _scaleHandleValue = Vector3.one;
    private bool _boxSelecting;
    private bool _boxDragged;
    private bool _boxAdditive;
    private bool _boxToggle;
    private int _boxControlId;
    private Vector2 _boxStart;
    private Vector2 _boxCurrent;
    private bool _showPathSettings = true;
    private bool _showSurfaceSettings;
    private bool _showOutputSettings = true;
    private bool _showOutputDetails;
    private bool _showControlPointSettings = true;
    private bool _showSceneHelp;
    private bool _showMeshPlacement = true;
    private bool _showMeshParts = true;
    private bool _showMeshAdvanced;

    private struct KnotHandleState
    {
        public Vector3 position;
        public Vector3 tangentIn;
        public Vector3 tangentOut;
        public float width;
    }

    private SerializedProperty _closed;
    private SerializedProperty _autoRebuild;
    private SerializedProperty _samplesPerCurve;
    private SerializedProperty _projectToSurface;
    private SerializedProperty _surfaceMask;
    private SerializedProperty _rayHeight;
    private SerializedProperty _surfaceOffset;
    private SerializedProperty _outputs;
    private SerializedProperty _mountainDecalSet;
    private SerializedProperty _mountainSources;
    private SerializedProperty _randomSeed;
    private SerializedProperty _mountainSpacing;
    private SerializedProperty _mountainDistributionUniformity;
    private SerializedProperty _brushWidth;
    private SerializedProperty _uniformScale;
    private SerializedProperty _uniformScaleRange;
    private SerializedProperty _horizontalScaleRange;
    private SerializedProperty _verticalScaleRange;
    private SerializedProperty _yawRange;
    private SerializedProperty _alignToSpline;
    private SerializedProperty _useSplineWidth;
    private SerializedProperty _mountainHeightMode;
    private SerializedProperty _mountainCardMaterial;
    private SerializedProperty _mountainCardHeight;
    private SerializedProperty _mountainCardPivot;
    private SerializedProperty _mountainCardRandomFlip;
    private SerializedProperty _mountainCardColor;
    private SerializedProperty _mountainCardSortingMode;
    private SerializedProperty _mountainCardSortingLayerId;
    private SerializedProperty _mountainCardSortingOrder;
    private SerializedProperty _mountainCardRenderQueue;
    private SerializedProperty _mountainBakeOutput;
    private SerializedProperty _keepMountainAuthoringAfterBake;
    private SerializedProperty _mountainDecalSurfaceMask;
    private SerializedProperty _mountainDecalProjectionDistance;
    private SerializedProperty _mountainDecalSurfaceOffset;
    private SerializedProperty _ribbonPreset;
    private SerializedProperty _riverMaterial;
    private SerializedProperty _riverHeightMode;
    private SerializedProperty _riverSegmentLength;
    private SerializedProperty _riverUvLength;
    private SerializedProperty _generateCollider;
    private SerializedProperty _edgeNoiseEnabled;
    private SerializedProperty _edgeNoiseSeed;
    private SerializedProperty _leftEdgeNoiseAmplitude;
    private SerializedProperty _rightEdgeNoiseAmplitude;
    private SerializedProperty _edgeNoiseWavelength;
    private SerializedProperty _generateEdgeObjects;
    private SerializedProperty _generateLeftEdge;
    private SerializedProperty _generateRightEdge;
    private SerializedProperty _edgePrefabs;
    private SerializedProperty _edgeObjectSeed;
    private SerializedProperty _edgeObjectSpacingRange;
    private SerializedProperty _edgeObjectDensity;
    private SerializedProperty _edgeObjectScaleRange;
    private SerializedProperty _edgeObjectYawRange;
    private SerializedProperty _edgeObjectOutwardOffset;
    private SerializedProperty _edgeObjectHeightOffset;
    private SerializedProperty _edgeObjectsAlignToPath;
    private SerializedProperty _edgeObjectsProjectToSurface;
    private SerializedProperty _generatedAssetOutputFolder;
    private SerializedProperty _prefabOutputFolder;

    private void OnEnable()
    {
        _builder = (PathSpline)target;
        _builder.EnsureDefaults();
        EnsureSelection();
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
        serializedObject.Update();
        DrawOutputPathSettings();
        serializedObject.ApplyModifiedProperties();
        EditorGUILayout.Space(8f);
        DrawGenerationButtons();
    }

    internal void DrawAuthoringInspector()
    {
        serializedObject.Update();

        EditorGUI.BeginChangeCheck();
        DrawSplineSettings();
        EditorGUILayout.Space(5f);
        if (DrawSectionFoldout(
            ref _showOutputSettings,
            "Output",
            "选择生成类型并设置当前输出"))
        {
            DrawModeSettings();
        }
        EditorGUILayout.Space(5f);
        if (DrawSectionFoldout(
            ref _showControlPointSettings,
            $"Control Points ({(_builder.knots != null ? _builder.knots.Count : 0)})",
            "添加、删除和编辑样条控制点"))
        {
            DrawPathEditing();
        }
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
                GetCurrentOutputLabel());
            EditorGUILayout.LabelField(
                "Path",
                $"{_builder.knots.Count} points · {length:F1} m");
            EditorGUILayout.LabelField(
                "Loop",
                _builder.closed ? "Closed" : "Open");
            EditorGUILayout.LabelField(
                "Preview",
                _builder.GeneratedRoot ? "Ready" : "Not generated");

            PathOutputLayer currentOutput = _builder.outputs != null
                && _builder.outputs.Count > 0
                    ? _builder.outputs[0]
                    : null;
            if (currentOutput != null
                && currentOutput.type == PathOutputType.Scatter)
            {
                IList<MountainSourceEntry> sources = _builder.mountainDecalSet
                    && _builder.mountainDecalSet.sources != null
                        ? _builder.mountainDecalSet.sources
                        : _builder.mountainSources;
                int prefabCount = sources != null
                    ? sources.Count
                    : 0;
                EditorGUILayout.LabelField(
                    "Mountain Sources",
                    $"{prefabCount} entries");
            }
            else if (currentOutput != null
                && currentOutput.type == PathOutputType.ProfileExtrude)
            {
                EditorGUILayout.LabelField(
                    "Material",
                    _builder.riverMaterial
                        ? _builder.riverMaterial.name
                        : "None");
                float uvLength = Mathf.Max(
                    0.01f,
                    _builder.riverUvLength);
                EditorGUILayout.LabelField(
                    "UV Range",
                    $"U: 0 → 1   V: 0 → {uvLength:F2}");
            }
            else if (currentOutput != null)
            {
                GameObject source = currentOutput.sourcePrefab;
                if (!source
                    && !string.IsNullOrEmpty(
                        currentOutput.sourceSceneObjectGlobalId)
                    && GlobalObjectId.TryParse(
                        currentOutput.sourceSceneObjectGlobalId,
                        out GlobalObjectId sourceId))
                {
                    source = GlobalObjectId
                        .GlobalObjectIdentifierToObjectSlow(sourceId)
                        as GameObject;
                }
                EditorGUILayout.LabelField(
                    "Source",
                    source ? source.name : "None");
                EditorGUILayout.LabelField(
                    "Segment Mesh",
                    string.IsNullOrEmpty(currentOutput.segmentMeshPath)
                        ? "Not selected"
                        : currentOutput.segmentMeshPath);
            }
        }

        EditorGUILayout.Space(6f);
        DrawOutputPathSettings();

        EditorGUILayout.HelpBox(
            "使用窗口底部的预览按钮检查实时结果，确认后点击烘焙按钮输出。",
            MessageType.Info);
        serializedObject.ApplyModifiedProperties();
    }

    private string GetCurrentOutputLabel()
    {
        _builder.EnsureOutputStack();
        if (_builder.outputs == null || _builder.outputs.Count == 0
            || _builder.outputs[0] == null)
        {
            return "None";
        }
        switch (_builder.outputs[0].type)
        {
            case PathOutputType.Scatter:
                return "Scatter";
            case PathOutputType.ProfileExtrude:
                return "Profile Extrude · "
                    + GetRibbonPresetLabel(_builder.ribbonPreset);
            case PathOutputType.MeshDeform:
                return "Mesh Deform";
            case PathOutputType.PrefabPlace:
                return "Prefab Place";
            default:
                return "None";
        }
    }

    private void DrawOutputPathSettings()
    {
        if (string.IsNullOrWhiteSpace(
            _generatedAssetOutputFolder.stringValue))
        {
            _generatedAssetOutputFolder.stringValue =
                PathSpline.DefaultGeneratedAssetFolder;
        }
        if (string.IsNullOrWhiteSpace(_prefabOutputFolder.stringValue))
        {
            _prefabOutputFolder.stringValue =
                PathSpline.DefaultPrefabOutputFolder;
        }

        EditorGUILayout.LabelField(
            new GUIContent(
                "Bake & Export Paths",
                "设置烘焙 Prefab 以及 Mesh、材质等文件的项目内输出位置"),
            EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            if (_builder.mode == PathBuildMode.Mountain)
            {
                DrawOutputFolderRow(
                    _prefabOutputFolder,
                    new GUIContent(
                        "Prefab Folder",
                        "山脉 Bake 后生成并连接的 Prefab 保存目录"),
                    "选择山脉 Prefab 输出目录");
            }
            DrawOutputFolderRow(
                _generatedAssetOutputFolder,
                new GUIContent(
                    "Asset Folder",
                    _builder.mode == PathBuildMode.Mountain
                        ? "山脉生成的 Mesh 和独立材质资源保存目录"
                        : "河流或道路生成的 Mesh 资源保存目录"),
                "选择生成资源输出目录");
        }

        bool assetValid = PathBuilderGenerator.TryNormalizeAssetFolder(
            _generatedAssetOutputFolder.stringValue,
            out _,
            out string assetError);
        string prefabError = string.Empty;
        bool prefabValid = _builder.mode != PathBuildMode.Mountain
            || PathBuilderGenerator.TryNormalizeAssetFolder(
                _prefabOutputFolder.stringValue,
                out _,
                out prefabError);
        if (!assetValid || !prefabValid)
        {
            string error = !assetValid ? assetError : prefabError;
            EditorGUILayout.HelpBox(error, MessageType.Error);
        }
        else
        {
            EditorGUILayout.HelpBox(
                "输出目录必须位于当前项目的 Assets 内。Browse 用于选择目录，Show 会在 Unity Project 窗口中创建并定位该目录；设置会随当前样条保存。共享 Mountain Decal Set 的材质页仍保存在 Set 资源内部。",
                MessageType.Info);
        }
    }

    private void DrawOutputFolderRow(
        SerializedProperty property,
        GUIContent label,
        string dialogTitle)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            property.stringValue = EditorGUILayout.TextField(
                label,
                property.stringValue);
            if (GUILayout.Button(
                new GUIContent("Browse", "选择 Assets 内的现有文件夹"),
                GUILayout.Width(58f)))
            {
                BrowseOutputFolder(property, dialogTitle);
            }
            if (GUILayout.Button(
                new GUIContent(
                    "Show",
                    "目录不存在时会自动创建，然后在 Unity Project 窗口中定位"),
                GUILayout.Width(46f)))
            {
                ShowOutputFolder(property);
            }
        }
    }

    private static void BrowseOutputFolder(
        SerializedProperty property,
        string dialogTitle)
    {
        string startFolder = Application.dataPath;
        if (PathBuilderGenerator.TryNormalizeAssetFolder(
            property.stringValue,
            out string currentPath,
            out _))
        {
            startFolder = GetNearestExistingFolder(
                GetAbsoluteProjectPath(currentPath));
        }

        string selectedFolder = EditorUtility.OpenFolderPanel(
            dialogTitle,
            startFolder,
            string.Empty);
        if (string.IsNullOrEmpty(selectedFolder))
            return;
        if (!TryConvertToAssetPath(selectedFolder, out string assetPath))
        {
            EditorUtility.DisplayDialog(
                "路径构建",
                "输出目录必须位于当前项目的 Assets 文件夹内。",
                "确定");
            return;
        }
        property.stringValue = assetPath;
    }

    private void ShowOutputFolder(SerializedProperty property)
    {
        if (!PathBuilderGenerator.TryEnsureAssetFolder(
            property.stringValue,
            out string normalized,
            out string error))
        {
            EditorUtility.DisplayDialog("路径构建", error, "确定");
            return;
        }

        property.stringValue = normalized;
        serializedObject.ApplyModifiedProperties();
        DefaultAsset folder =
            AssetDatabase.LoadAssetAtPath<DefaultAsset>(normalized);
        if (!folder)
            return;
        EditorUtility.FocusProjectWindow();
        Selection.activeObject = folder;
        EditorGUIUtility.PingObject(folder);
    }

    private static string GetAbsoluteProjectPath(string assetPath)
    {
        string projectRoot = Path.GetFullPath(
            Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static string GetNearestExistingFolder(string folder)
    {
        string current = folder;
        while (!string.IsNullOrEmpty(current)
            && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }
        return !string.IsNullOrEmpty(current)
            ? current
            : Application.dataPath;
    }

    private static bool TryConvertToAssetPath(
        string absoluteFolder,
        out string assetPath)
    {
        string assetsRoot = Path.GetFullPath(Application.dataPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string selected = Path.GetFullPath(absoluteFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(
            selected,
            assetsRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            assetPath = "Assets";
            return true;
        }

        string prefix = assetsRoot + Path.DirectorySeparatorChar;
        if (!selected.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            assetPath = string.Empty;
            return false;
        }

        assetPath = "Assets/" + selected.Substring(prefix.Length)
            .Replace('\\', '/');
        return true;
    }

    private void DrawSplineSettings()
    {
        if (DrawSectionFoldout(
            ref _showPathSettings,
            "Path",
            "样条本身与实时预览设置"))
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.PropertyField(
                    _closed,
                    new GUIContent("Closed", "连接首尾控制点"));
                EditorGUILayout.PropertyField(
                    _autoRebuild,
                    new GUIContent(
                        "Live Preview",
                        "参数或样条修改后自动更新预览"));
                EditorGUILayout.PropertyField(
                    _samplesPerCurve,
                    new GUIContent(
                        "Curve Accuracy",
                        "每段贝塞尔曲线的弧长采样精度；通常保持默认"));
            }
        }

        if (DrawSectionFoldout(
            ref _showSurfaceSettings,
            "Surface",
            "绘制控制点时的地表吸附设置"))
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.PropertyField(
                    _projectToSurface,
                    new GUIContent(
                        "Snap Drawing To Surface",
                        "只影响新建控制点的位置，不决定生成结果的高度模式"));
                if (_projectToSurface.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(
                        _surfaceMask,
                        new GUIContent(
                            "Surface Layer",
                            "绘制控制点时用于检测地表的物理层级"));
                    EditorGUILayout.PropertyField(
                        _rayHeight,
                        new GUIContent(
                            "Ray Height",
                            "向下检测地表时的射线起始高度"));
                    EditorGUILayout.PropertyField(
                        _surfaceOffset,
                        new GUIContent(
                            "Height Offset",
                            "控制点相对命中地表的高度偏移"));
                    EditorGUI.indentLevel--;
                }
            }
        }
    }

    private static bool DrawSectionFoldout(
        ref bool expanded,
        string label,
        string tooltip)
    {
        expanded = EditorGUILayout.Foldout(
            expanded,
            new GUIContent(label, tooltip),
            true,
            EditorStyles.foldoutHeader);
        return expanded;
    }

    private void DrawModeSettings()
    {
        PathOutputType outputType = DrawPrimaryOutputSettings();
        if (outputType == PathOutputType.Scatter)
        {
            if (DrawSectionFoldout(
                ref _showOutputDetails,
                "Scatter Settings",
                "山脉资源、随机分布、朝向和烘焙设置"))
            {
                DrawMountainSettings();
            }
        }
        else if (outputType == PathOutputType.ProfileExtrude)
        {
            if (DrawSectionFoldout(
                ref _showOutputDetails,
                "Surface Settings",
                "河流或道路连续面的材质、UV、边缘和碰撞设置"))
            {
                DrawRiverSettings();
            }
        }
    }

    private PathOutputType DrawPrimaryOutputSettings()
    {
        _builder.EnsureOutputStack();
        if (_outputs.arraySize == 0)
            return _builder.mode == PathBuildMode.Mountain
                ? PathOutputType.Scatter
                : PathOutputType.ProfileExtrude;

        SerializedProperty output = _outputs.GetArrayElementAtIndex(0);
        SerializedProperty enabled = output.FindPropertyRelative("enabled");
        SerializedProperty type = output.FindPropertyRelative("type");
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(
                enabled,
                new GUIContent("Enabled", "启用或关闭当前生成输出"));
            type.intValue = EditorGUILayout.IntPopup(
                new GUIContent(
                    "Output Type",
                    "Scatter 用于随机分布；Profile Extrude 用于连续面；Mesh Deform 用于模型弯曲；Prefab Place 用于刚性摆放"),
                type.intValue,
                new[]
                {
                    new GUIContent("Scatter"),
                    new GUIContent("Profile Extrude"),
                    new GUIContent("Mesh Deform"),
                    new GUIContent("Prefab Place")
                },
                new[] { 0, 1, 2, 3 });
            PathOutputType outputType =
                (PathOutputType)type.enumValueIndex;
            if (outputType == PathOutputType.MeshDeform
                || outputType == PathOutputType.PrefabPlace)
            {
                DrawMeshDeformSettings(
                    output,
                    outputType == PathOutputType.MeshDeform);
                EditorGUILayout.HelpBox(
                    outputType == PathOutputType.MeshDeform
                        ? "连续主体会沿样条变形。源 Mesh 沿长度方向的分段越多，弯曲越平滑。"
                        : "完整对象会沿样条刚性摆放，不会弯曲，适合建筑、石头和独立模块。",
                    MessageType.None);
            }
            return outputType;
        }
    }

    private void DrawMeshDeformSettings(
        SerializedProperty output,
        bool deform)
    {
        EditorGUILayout.LabelField("Source", EditorStyles.miniBoldLabel);
        DrawMeshDeformSource(output);
        EditorGUILayout.PropertyField(
            output.FindPropertyRelative("forwardAxis"),
            new GUIContent("Forward Axis", "模型自身的长度方向，通常为 Z 或 X"));
        DrawSegmentMeshSelector(output);
        if (deform)
            DrawMeshPartSettings(output);

        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField("Segment", EditorStyles.miniBoldLabel);
        EditorGUILayout.PropertyField(
            output.FindPropertyRelative("startSegmentOffset"),
            new GUIContent(
                "Start Segment Offset",
                "从主体 Mesh 起点向内裁切的距离；用于定义中间重复段起点"));
        EditorGUILayout.PropertyField(
            output.FindPropertyRelative("endSegmentOffset"),
            new GUIContent(
                "End Segment Offset",
                "从主体 Mesh 终点向内裁切的距离；用于定义中间重复段终点"));
        EditorGUILayout.PropertyField(
            output.FindPropertyRelative("repeat"),
            new GUIContent("Repeat", "按模型长度连续重复；关闭后把一个模型拉伸到整条样条"));
        if (output.FindPropertyRelative("repeat").boolValue)
        {
            if (deform)
            {
                EditorGUILayout.PropertyField(
                    output.FindPropertyRelative("fitLastSegment"),
                    new GUIContent("Fit Last Segment", "缩放最后一节以正好贴合样条终点"));
            }
            EditorGUILayout.PropertyField(
                output.FindPropertyRelative("spacing"),
                new GUIContent(
                    "Segment Offset",
                    "重复段偏移：0 为边界相接，正数留缝，负数重叠"));

            PathOutputLayer runtimeOutput =
                _builder.outputs != null && _builder.outputs.Count > 0
                    ? _builder.outputs[0]
                    : null;
            if (PathBuilderGenerator.TryGetMeshDeformMetrics(
                _builder,
                runtimeOutput,
                out float detectedLength,
                out float pathLength,
                out int plannedCount))
            {
                EditorGUILayout.HelpBox(
                    $"当前重复段：{detectedLength:F2} m    样条长度：{pathLength:F2} m    预计生成：{plannedCount} 段",
                    plannedCount <= 1
                        ? MessageType.Warning
                        : MessageType.None);
            }
        }

        if (deform)
        {
            SerializedProperty cornerHandling =
                output.FindPropertyRelative("cornerHandling");
            EditorGUILayout.PropertyField(
                cornerHandling,
                new GUIContent(
                    "Corner Handling",
                    "Off 不处理；Compress Inner 会根据局部曲率压缩弯道内侧，避免内侧翻折和重叠"));
            if ((PathCornerHandling)cornerHandling.enumValueIndex
                == PathCornerHandling.CompressInner)
            {
                EditorGUILayout.Slider(
                    output.FindPropertyRelative("cornerSafety"),
                    0.5f,
                    0.95f,
                    new GUIContent(
                        "Corner Safety",
                        "内侧允许接近曲率中心的安全比例；数值越小越保守，急弯内侧压缩越明显"));
            }
        }

        EditorGUILayout.Space(4f);
        if (DrawSectionFoldout(
            ref _showMeshPlacement,
            "Placement",
            "模型相对样条的位置、旋转和缩放"))
        {
            EditorGUILayout.PropertyField(
                output.FindPropertyRelative("positionOffset"),
                new GUIContent(
                    "Position Offset",
                    "相对于样条坐标系的位置偏移"));
            EditorGUILayout.PropertyField(
                output.FindPropertyRelative("rotationOffset"),
                new GUIContent(
                    "Rotation Offset",
                    "模型进入样条生成前的旋转修正"));
            EditorGUILayout.PropertyField(
                output.FindPropertyRelative("scale"),
                new GUIContent(
                    "Scale",
                    "模型进入样条生成前的缩放"));
        }

        if (DrawSectionFoldout(
            ref _showMeshAdvanced,
            "Advanced",
            "轴心、根节点和碰撞体等低频设置"))
        {
            if (deform)
            {
                EditorGUILayout.PropertyField(
                    output.FindPropertyRelative("useRootPosition"),
                    new GUIContent(
                        "Use Root Position",
                        "保留源对象根节点相对父级的位置"));
                EditorGUILayout.PropertyField(
                    output.FindPropertyRelative("pivotAlignment"),
                    new GUIContent(
                        "Pivot Alignment",
                        "设置模型轴心如何对齐样条"));
                EditorGUILayout.PropertyField(
                    output.FindPropertyRelative("updateCollider"),
                    new GUIContent(
                        "Update Collider",
                        "同步更新源对象已有的 Mesh Collider"));
            }
        }
    }

    private static void DrawMeshDeformSource(SerializedProperty output)
    {
        SerializedProperty prefab =
            output.FindPropertyRelative("sourcePrefab");
        SerializedProperty sceneId =
            output.FindPropertyRelative("sourceSceneObjectGlobalId");
        GameObject current = prefab.objectReferenceValue as GameObject;
        if (!current
            && sceneId != null
            && !string.IsNullOrEmpty(sceneId.stringValue)
            && GlobalObjectId.TryParse(
                sceneId.stringValue,
                out GlobalObjectId globalId))
        {
            current = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(
                globalId) as GameObject;
        }

        EditorGUI.BeginChangeCheck();
        GameObject selected = EditorGUILayout.ObjectField(
            new GUIContent(
                "Source Object",
                "可从 Project 拖入 Prefab，也可从 Hierarchy 拖入场景对象"),
            current,
            typeof(GameObject),
            true) as GameObject;
        if (!EditorGUI.EndChangeCheck())
            return;

        if (!selected)
        {
            prefab.objectReferenceValue = null;
            sceneId.stringValue = string.Empty;
        }
        else if (EditorUtility.IsPersistent(selected))
        {
            prefab.objectReferenceValue = selected;
            sceneId.stringValue = string.Empty;
        }
        else
        {
            prefab.objectReferenceValue = null;
            sceneId.stringValue = GlobalObjectId
                .GetGlobalObjectIdSlow(selected).ToString();
        }
        output.FindPropertyRelative("segmentMeshPath").stringValue =
            string.Empty;
        output.FindPropertyRelative("meshParts").ClearArray();
    }

    private void DrawMeshPartSettings(SerializedProperty output)
    {
        GameObject source = ResolveMeshDeformSourceForEditor(output);
        if (!source)
            return;
        MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0)
            return;

        SerializedProperty parts = output.FindPropertyRelative("meshParts");
        for (int i = 0; i < filters.Length; i++)
        {
            string path = GetMeshPartPath(source, filters[i]);
            if (FindMeshPart(parts, path) != null)
                continue;
            int index = parts.arraySize;
            parts.InsertArrayElementAtIndex(index);
            SerializedProperty part = parts.GetArrayElementAtIndex(index);
            part.FindPropertyRelative("transformPath").stringValue = path;
            part.FindPropertyRelative("mode").enumValueIndex =
                (int)PathMeshPartMode.Deform;
            part.FindPropertyRelative("segments").intValue = 1;
        }

        if (!DrawSectionFoldout(
            ref _showMeshParts,
            $"Mesh Parts ({filters.Length})",
            "分别设置预制体内每个子 Mesh 的生成方式和纵向细分段数"))
        {
            return;
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            for (int i = 0; i < filters.Length; i++)
            {
                string path = GetMeshPartPath(source, filters[i]);
                SerializedProperty part = FindMeshPart(parts, path);
                if (part == null)
                    continue;
                EditorGUILayout.LabelField(
                    (path == "." ? $"(Root) / {filters[i].name}" : path)
                    + $"  ·  {(filters[i].sharedMesh ? filters[i].sharedMesh.vertexCount : 0)} verts",
                    EditorStyles.miniBoldLabel);
                SerializedProperty mode = part.FindPropertyRelative("mode");
                EditorGUILayout.PropertyField(
                    mode,
                    new GUIContent(
                        "Mode",
                        "Deform 沿样条弯曲；Place 保持刚性；Ignore 不生成"));
                if ((PathMeshPartMode)mode.enumValueIndex
                    == PathMeshPartMode.Deform)
                {
                    SerializedProperty segments =
                        part.FindPropertyRelative("segments");
                    segments.intValue = EditorGUILayout.IntSlider(
                        new GUIContent(
                            "Segments",
                            "沿 Forward Axis 增加的纵向切片数；数值越高弯曲越平滑"),
                        Mathf.Clamp(segments.intValue, 1, 32),
                        1,
                        32);
                }
                if (i < filters.Length - 1)
                    EditorGUILayout.Space(3f);
            }
        }
    }

    private static SerializedProperty FindMeshPart(
        SerializedProperty parts,
        string path)
    {
        for (int i = 0; i < parts.arraySize; i++)
        {
            SerializedProperty part = parts.GetArrayElementAtIndex(i);
            if (part.FindPropertyRelative("transformPath").stringValue == path)
                return part;
        }
        return null;
    }

    private static string GetMeshPartPath(
        GameObject source,
        MeshFilter filter)
    {
        string path = AnimationUtility.CalculateTransformPath(
            filter.transform, source.transform);
        return string.IsNullOrEmpty(path) ? "." : path;
    }

    private static void DrawSegmentMeshSelector(SerializedProperty output)
    {
        GameObject source = ResolveMeshDeformSourceForEditor(output);
        SerializedProperty pathProperty =
            output.FindPropertyRelative("segmentMeshPath");
        if (!source)
            return;

        MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0)
        {
            EditorGUILayout.HelpBox(
                "Source Object 中没有 MeshFilter。",
                MessageType.Error);
            return;
        }

        string[] paths = new string[filters.Length];
        string[] labels = new string[filters.Length];
        int selectedIndex = -1;
        for (int i = 0; i < filters.Length; i++)
        {
            paths[i] = AnimationUtility.CalculateTransformPath(
                filters[i].transform, source.transform);
            if (string.IsNullOrEmpty(paths[i]))
                paths[i] = ".";
            labels[i] = paths[i] == "."
                ? $"(Root) / {filters[i].name}"
                : paths[i];
            if (paths[i] == pathProperty.stringValue)
                selectedIndex = i;
        }

        if (filters.Length == 1 && selectedIndex < 0)
        {
            selectedIndex = 0;
            pathProperty.stringValue = paths[0];
        }

        string[] popupLabels = new string[labels.Length + 1];
        popupLabels[0] = "Select Mesh...";
        Array.Copy(labels, 0, popupLabels, 1, labels.Length);
        int popupIndex = Mathf.Max(0, selectedIndex + 1);
        int nextIndex = EditorGUILayout.Popup(
            new GUIContent(
                "Segment Mesh",
                "明确指定连续拼接主体；只有该 Mesh 的起止边界决定重复段长度"),
            popupIndex,
            popupLabels);
        if (nextIndex != popupIndex)
        {
            pathProperty.stringValue = nextIndex <= 0
                ? string.Empty
                : paths[nextIndex - 1];
        }
        if (filters.Length > 1 && nextIndex <= 0)
        {
            EditorGUILayout.HelpBox(
                "源对象包含多个 Mesh。请选择真正需要首尾连接的主体 Mesh。",
                MessageType.Warning);
        }
    }

    private static GameObject ResolveMeshDeformSourceForEditor(
        SerializedProperty output)
    {
        GameObject source = output.FindPropertyRelative("sourcePrefab")
            .objectReferenceValue as GameObject;
        if (source)
            return source;
        string sceneId = output
            .FindPropertyRelative("sourceSceneObjectGlobalId").stringValue;
        if (string.IsNullOrEmpty(sceneId)
            || !GlobalObjectId.TryParse(sceneId, out GlobalObjectId globalId))
        {
            return null;
        }
        return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId)
            as GameObject;
    }

    private void DrawMountainSettings()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.LabelField(
                new GUIContent("Style Randomization", "山脉资源与样式权重"),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _mountainDecalSet,
                new GUIContent(
                    "Mountain Decal Set",
                    "多条山脉可引用同一个资源集，共享图集、权重和烘焙材质"));
            if (_mountainDecalSet.objectReferenceValue)
            {
                MountainDecalSet decalSet =
                    _mountainDecalSet.objectReferenceValue as MountainDecalSet;
                using (new EditorGUILayout.HorizontalScope())
                {
                    int sourceCount = decalSet != null
                        && decalSet.sources != null
                            ? decalSet.sources.Count
                            : 0;
                    EditorGUILayout.LabelField(
                        "Shared Sources",
                        $"{sourceCount} entries");
                    if (GUILayout.Button(
                        new GUIContent("Select Set", "在 Project 中选中并编辑共享资源集"),
                        GUILayout.Width(78f)))
                    {
                        Selection.activeObject = decalSet;
                        EditorGUIUtility.PingObject(decalSet);
                    }
                }
                EditorGUILayout.HelpBox(
                    "当前样条使用共享贴花集。本地资源列表会被保留，移除共享集后可继续使用。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.PropertyField(
                    _mountainSources,
                    new GUIContent(
                        "Mountain Sources",
                        "可混合模型、Sprite 面片和 Texture 面片；每项权重决定随机出现概率"),
                    true);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        "资源条目的权重越大，该样式随机出现得越多。",
                        EditorStyles.miniLabel);
                    if (GUILayout.Button(
                        new GUIContent(
                            "Create Shared Set",
                            "把当前资源列表复制成可供多条山脉共用的贴花集"),
                        GUILayout.Width(116f)))
                    {
                        serializedObject.ApplyModifiedProperties();
                        CreateMountainDecalSet();
                        serializedObject.Update();
                    }
                }
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                new GUIContent("Randomization", "控制所有随机结果的基础设置"),
                EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(
                    _randomSeed,
                    new GUIContent(
                        "Random Seed",
                        "相同种子会得到完全相同的随机结果"));
                if (GUILayout.Button(
                    new GUIContent("Random", "生成一个新的随机种子"),
                    GUILayout.Width(54f)))
                {
                    _randomSeed.intValue = Guid.NewGuid().GetHashCode();
                }
            }

            _mountainHeightMode.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent(
                    "Height Source",
                    "样条高度保留控制点的 Y；跟随地表会在生成时向下吸附"),
                _mountainHeightMode.enumValueIndex,
                new[] { "Spline Height", "Follow Surface" });
            if ((MountainHeightMode)_mountainHeightMode.enumValueIndex
                == MountainHeightMode.SplineHeight)
            {
                EditorGUILayout.LabelField(
                    "选择控制点后按 W，拖动绿色 Y 轴即可调整山脉高度。",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    _surfaceMask,
                    new GUIContent("Surface Layer", "用于检测地表的物理层级"));
                EditorGUILayout.PropertyField(
                    _rayHeight,
                    new GUIContent("Ray Height", "向下检测地表时的射线起始高度"));
                EditorGUILayout.PropertyField(
                    _surfaceOffset,
                    new GUIContent("Height Offset", "生成物体相对地表的高度偏移"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                new GUIContent("Path Distribution", "控制山沿样条方向的分布"),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _mountainSpacing,
                new GUIContent(
                    "Target Spacing",
                    "根据样条总长度和该间隔稳定计算生成数量"));
            EditorGUILayout.PropertyField(
                _mountainDistributionUniformity,
                new GUIContent(
                    "Distribution Uniformity",
                    "0 为完全随机疏密，1 为完全等距；只改变位置，不改变数量"));
            EditorGUILayout.LabelField(
                "0：完全随机疏密　　1：完全均匀等距　　数量始终不变",
                EditorStyles.miniLabel);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                new GUIContent("Lateral Scatter", "控制山在样条左右方向的随机离散"),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _useSplineWidth,
                new GUIContent(
                    "Use Spline Width",
                    "开启后使用控制点 Width 控制每一段的随机离散宽度"));
            if (!_useSplineWidth.boolValue)
            {
                EditorGUILayout.PropertyField(
                    _brushWidth,
                    new GUIContent(
                        "Fixed Scatter Width",
                        "山会在样条中心线左右各一半宽度内随机偏移"));
            }
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                new GUIContent("Scale Randomization", "控制山体大小的随机变化"),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _uniformScale,
                new GUIContent(
                    "Uniform Scale",
                    "开启后宽度和高度使用同一个随机倍率，不改变图片或模型比例"));
            if (_uniformScale.boolValue)
            {
                EditorGUILayout.PropertyField(
                    _uniformScaleRange,
                    new GUIContent(
                        "Overall Scale Range",
                        "在保持原始比例的情况下随机改变整体大小"));
            }
            else
            {
                EditorGUILayout.PropertyField(
                    _horizontalScaleRange,
                    new GUIContent(
                        "Width Scale Range",
                        "随机改变模型或图片山的横向尺寸"));
                EditorGUILayout.PropertyField(
                    _verticalScaleRange,
                    new GUIContent(
                        "Height Scale Range",
                        "随机改变模型或图片山的高度"));
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                new GUIContent("Prefab Orientation", "模型山的朝向设置"),
                EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _alignToSpline,
                new GUIContent("Align To Spline", "让模型山沿样条切线方向排列"));
            EditorGUILayout.PropertyField(
                _yawRange,
                new GUIContent(
                    _alignToSpline.boolValue ? "Random Yaw Offset" : "Random Yaw Range",
                    "控制模型山绕 Y 轴的随机旋转范围"));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                new GUIContent("Card Settings", "图片面片山的尺寸、朝向和透明排序"),
                EditorStyles.miniBoldLabel);
            Camera assignedCamera =
                PathBuilderAuthoringStorage.ResolveMountainFacingCamera(
                    _builder);
            Camera selectedCamera = EditorGUILayout.ObjectField(
                new GUIContent(
                    "Facing Camera",
                    "可直接拖入场景中的 Camera；留空仅供实时预览自动使用 Main Camera，地面贴花烘焙必须明确指定"),
                assignedCamera,
                typeof(Camera),
                true) as Camera;
            if (selectedCamera != assignedCamera)
            {
                Undo.RecordObject(_builder, "Set Mountain Facing Camera");
                PathBuilderAuthoringStorage.SetMountainFacingCamera(
                    _builder,
                    selectedCamera);
            }
            if (!_mountainDecalSet.objectReferenceValue)
            {
                EditorGUILayout.PropertyField(
                    _mountainCardMaterial,
                    new GUIContent(
                        "Base Material",
                        "可留空，工具会使用 URP Unlit 自动创建贴图材质"));
            }
            EditorGUILayout.PropertyField(
                _mountainCardHeight,
                new GUIContent("Base Height", "面片山未缩放前的基础高度"));
            EditorGUILayout.PropertyField(
                _mountainCardPivot,
                new GUIContent("Pivot", "选择面片山在生成位置上的锚点"));
            EditorGUILayout.PropertyField(
                _mountainCardRandomFlip,
                new GUIContent("Random Flip X", "随机水平翻转图片以增加变化"));
            EditorGUILayout.PropertyField(
                _mountainCardColor,
                new GUIContent("Vertex Color", "乘到面片材质上的整体顶点颜色"));
            _mountainCardSortingMode.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent(
                    "Transparency Sorting",
                    "精确模式会按摄像机从远到近拆分绘制；高性能模式按贴图合批"),
                _mountainCardSortingMode.enumValueIndex,
                new[] { "Accurate: Back To Front", "Fast: Group By Texture" });
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Other Transparent Objects",
                    "控制面片山与场景中其他半透明物体的绘制先后"),
                EditorStyles.miniBoldLabel);
            DrawMountainCardSortingLayer();
            EditorGUILayout.PropertyField(
                _mountainCardSortingOrder,
                new GUIContent(
                    "Order in Layer",
                    "数值越大越晚绘制；精确排序会以该值为中心安排山面片批次"));
            if (!_mountainDecalSet.objectReferenceValue)
            {
                EditorGUILayout.PropertyField(
                    _mountainCardRenderQueue,
                    new GUIContent(
                        "Render Queue",
                        "透明物体通常为 3000；较大值更晚绘制，较小值更早绘制"));
                _mountainCardRenderQueue.intValue = Mathf.Clamp(
                    _mountainCardRenderQueue.intValue,
                    2501,
                    3999);
            }
            else
            {
                MountainDecalSet decalSet =
                    _mountainDecalSet.objectReferenceValue as MountainDecalSet;
                EditorGUILayout.LabelField(
                    new GUIContent(
                        "Render Queue",
                        "共享模式下渲染队列由 Mountain Decal Set 统一控制"),
                    decalSet ? decalSet.renderQueue.ToString() : "-");
            }
            EditorGUILayout.LabelField(
                "透明方式：渐变透明混合，不使用透明裁剪。",
                EditorStyles.miniLabel);
            if ((MountainCardSortingMode)_mountainCardSortingMode.enumValueIndex
                == MountainCardSortingMode.AccurateBackToFront)
            {
                EditorGUILayout.LabelField(
                    "建议山图使用同一个精灵图集，可兼顾精确排序和较少的绘制批次。",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.HelpBox(
                "与其他半透明物体冲突时，优先调整渲染队列；同一队列内再使用排序层和层内顺序。数值越大通常越晚绘制、显示越靠前。",
                MessageType.Info);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField(
                new GUIContent("Bake Output", "控制图片山最终烘焙成摄像机面片还是地表贴花"),
                EditorStyles.miniBoldLabel);
            _mountainBakeOutput.enumValueIndex = EditorGUILayout.Popup(
                new GUIContent(
                    "Output Geometry",
                    "地面贴花会在烘焙时沿指定正交摄像机投射到地表"),
                _mountainBakeOutput.enumValueIndex,
                new[] { "Ground Decal", "Camera Facing Cards" });
            EditorGUILayout.PropertyField(
                _keepMountainAuthoringAfterBake,
                new GUIContent(
                    "Keep Spline & Settings",
                    "开启后 Bake 只新增 Prefab，并保留烘焙前的样条线、参数和编辑会话"));
            if ((MountainBakeOutput)_mountainBakeOutput.enumValueIndex
                == MountainBakeOutput.GroundDecal)
            {
                EditorGUILayout.PropertyField(
                    _mountainDecalSurfaceMask,
                    new GUIContent(
                        "Decal Surface Layer",
                        "只选择地面或 Terrain 所在层；该层必须具有 Collider"));
                EditorGUILayout.PropertyField(
                    _mountainDecalProjectionDistance,
                    new GUIContent(
                        "Projection Distance",
                        "沿摄像机视线搜索地表的最大距离"));
                EditorGUILayout.LabelField(
                    new GUIContent(
                        "Geometry",
                        "每张山图固定使用四个角点和两个三角面"),
                    "4 vertices / 2 triangles per image");
                EditorGUILayout.PropertyField(
                    _mountainDecalSurfaceOffset,
                    new GUIContent(
                        "Surface Offset",
                        "沿地表法线轻微抬高贴花，避免与地面闪烁"));
                EditorGUILayout.HelpBox(
                    "实时预览仍显示朝向摄像机的山图；Bake 时每张图片固定生成 4 个顶点、2 个三角面，只让四角贴到地表。适合平地和缓坡；起伏过大的地形中部可能悬空或被遮挡。Prefab 山不会被转换，地表层不要包含建筑。",
                    MessageType.Info);
            }
            EditorGUILayout.HelpBox(
                _keepMountainAuthoringAfterBake.boolValue
                    ? "Bake 后会创建并连接山脉 Prefab，同时保留原样条线和全部设置；原预览会暂时隐藏，修改参数或点击 Preview 后可继续生成。"
                    : "Bake 后会创建并连接山脉 Prefab，并删除当前样条编辑会话。",
                _keepMountainAuthoringAfterBake.boolValue
                    ? MessageType.Info
                    : MessageType.Warning);

            Camera explicitCamera =
                PathBuilderAuthoringStorage.ResolveMountainFacingCamera(
                    _builder);
            Camera facingCamera = explicitCamera
                ? explicitCamera
                : Camera.main;
            bool usingFallback = !explicitCamera;
            string cameraLabel = facingCamera
                ? $"朝向摄像机：{facingCamera.name}"
                    + (facingCamera.orthographic ? "（正交）" : "（透视）")
                    + (usingFallback ? "［使用主摄像机作为后备］" : string.Empty)
                : "朝向摄像机：场景视图摄像机（未指定摄像机，也没有主摄像机）";
            bool groundDecalBake =
                (MountainBakeOutput)_mountainBakeOutput.enumValueIndex
                    == MountainBakeOutput.GroundDecal;
            if (groundDecalBake && !explicitCamera)
            {
                cameraLabel +=
                    "\n地面贴花烘焙必须在 Facing Camera 中明确指定正交摄像机。";
            }
            EditorGUILayout.HelpBox(
                cameraLabel + "\n图片山会按摄像机方向合并为面片网格，不会为每张图创建独立的精灵渲染器。",
                (groundDecalBake && !explicitCamera)
                    || (facingCamera && !facingCamera.orthographic)
                    ? MessageType.Warning
                    : MessageType.Info);
        }
    }

    private void CreateMountainDecalSet()
    {
        string assetPath = EditorUtility.SaveFilePanelInProject(
            "创建共享山脉贴花集",
            "MountainDecalSet",
            "asset",
            "选择共享资源集的保存位置");
        if (string.IsNullOrEmpty(assetPath))
            return;

        MountainDecalSet decalSet =
            ScriptableObject.CreateInstance<MountainDecalSet>();
        decalSet.baseMaterial = _builder.mountainCardMaterial;
        decalSet.renderQueue = _builder.mountainCardRenderQueue;
        if (_builder.mountainSources != null)
        {
            for (int i = 0; i < _builder.mountainSources.Count; i++)
            {
                MountainSourceEntry source = _builder.mountainSources[i];
                if (source == null)
                    continue;
                decalSet.sources.Add(new MountainSourceEntry
                {
                    sourceType = source.sourceType,
                    prefab = source.prefab,
                    sprite = source.sprite,
                    texture = source.texture,
                    weight = source.weight
                });
            }
        }

        AssetDatabase.CreateAsset(decalSet, assetPath);
        Undo.RecordObject(_builder, "Assign Mountain Decal Set");
        _builder.mountainDecalSet = decalSet;
        EditorUtility.SetDirty(_builder);
        EditorUtility.SetDirty(decalSet);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(decalSet);
    }

    private void DrawRiverSettings()
    {
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(
                _ribbonPreset,
                new GUIContent(
                    "Path Preset",
                    "选择河流、自然道路或规则道路的参数预设"));
            if (GUILayout.Button(
                new GUIContent("Apply Preset Values", "把当前路径类型的推荐参数应用到面板")))
            {
                Undo.RecordObject(_builder, "Apply Path Preset");
                _builder.ApplyRibbonPreset();
                EditorUtility.SetDirty(_builder);
                serializedObject.Update();
                MarkBuilderChanged(true);
            }

            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField("Surface Mesh", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _riverMaterial,
                new GUIContent("Material", "生成河流或道路网格使用的材质"));
            EditorGUILayout.PropertyField(
                _riverHeightMode,
                new GUIContent(
                    "Height Source",
                    "样条高度会保留控制点的 Y 高度；跟随地表会在生成时重新向下贴地"));
            if ((RiverHeightMode)_riverHeightMode.enumValueIndex
                == RiverHeightMode.SplineHeight)
            {
                EditorGUILayout.HelpBox(
                    "河面使用样条线的完整三维高度。选择控制点后按 W，沿绿色 Y 轴移动即可制作高低变化。",
                    MessageType.None);
            }
            else
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    _surfaceMask,
                    new GUIContent("Surface Layer", "生成网格时用于检测地表的物理层级"));
                EditorGUILayout.PropertyField(
                    _rayHeight,
                    new GUIContent("Ray Height", "向下检测地表时的射线起始高度"));
                EditorGUILayout.PropertyField(
                    _surfaceOffset,
                    new GUIContent("Height Offset", "网格相对命中地表的高度偏移"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.PropertyField(
                _riverSegmentLength,
                new GUIContent(
                    "Mesh Segment Length",
                    "仅控制河流网格精度；越小越平滑、顶点越多，不会重置 UV"));
            EditorGUILayout.PropertyField(
                _riverUvLength,
                new GUIContent(
                    "UV Length",
                    "整条河流长度方向的最终 V 值；1 表示整条河流占满一次 0-1，超过 1 的部分由 Repeat 纹理重复"));
            EditorGUILayout.LabelField(
                $"整条河流 UV：U 0 → 1，V 0 → "
                + $"{Mathf.Max(0.01f, _riverUvLength.floatValue):F2}",
                EditorStyles.miniLabel);
            EditorGUILayout.PropertyField(
                _generateCollider,
                new GUIContent("Generate Mesh Collider", "为生成的河流或道路添加网格碰撞体"));

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Edge Shape", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _edgeNoiseEnabled,
                new GUIContent(
                    "Edge Noise",
                    "只改变左右边界，不扰动中心样条和 UV"));
            if (_edgeNoiseEnabled.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    _edgeNoiseSeed,
                    new GUIContent("Random Seed", "控制左右边缘噪声的可复现随机结果"));
                EditorGUILayout.PropertyField(
                    _leftEdgeNoiseAmplitude,
                    new GUIContent("Left Amplitude", "左侧边缘相对中心线的最大噪声幅度"));
                EditorGUILayout.PropertyField(
                    _rightEdgeNoiseAmplitude,
                    new GUIContent("Right Amplitude", "右侧边缘相对中心线的最大噪声幅度"));
                EditorGUILayout.PropertyField(
                    _edgeNoiseWavelength,
                    new GUIContent(
                        "Noise Size",
                        "数值越大，岸线变化越缓慢、轮廓越大"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Edge Objects", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(
                _generateEdgeObjects,
                new GUIContent(
                    "Generate Edge Objects",
                    "沿左右边缘散布河岸、路边或护栏模型"));
            if (_generateEdgeObjects.boolValue)
            {
                EditorGUI.indentLevel++;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(
                        _generateLeftEdge,
                        new GUIContent("Left", "在路径左侧生成边缘物体"));
                    EditorGUILayout.PropertyField(
                        _generateRightEdge,
                        new GUIContent("Right", "在路径右侧生成边缘物体"));
                }
                EditorGUILayout.PropertyField(
                    _edgePrefabs,
                    new GUIContent("Prefab Palette", "用于随机散布的河岸、路边或护栏预制体"),
                    true);
                EditorGUILayout.PropertyField(
                    _edgeObjectSeed,
                    new GUIContent("Random Seed", "控制边缘物体分布的可复现随机结果"));
                EditorGUILayout.PropertyField(
                    _edgeObjectSpacingRange,
                    new GUIContent("Spacing Range", "相邻边缘物体之间的随机间距范围"));
                EditorGUILayout.PropertyField(
                    _edgeObjectDensity,
                    new GUIContent("Density", "控制候选位置实际生成物体的比例"));
                EditorGUILayout.PropertyField(
                    _edgeObjectScaleRange,
                    new GUIContent("Scale Range", "边缘物体的随机整体缩放范围"));
                EditorGUILayout.PropertyField(
                    _edgeObjectYawRange,
                    new GUIContent("Yaw Range", "边缘物体绕 Y 轴的随机旋转范围"));
                EditorGUILayout.PropertyField(
                    _edgeObjectOutwardOffset,
                    new GUIContent("Outward Offset", "边缘物体向路径外侧移动的距离"));
                EditorGUILayout.PropertyField(
                    _edgeObjectHeightOffset,
                    new GUIContent("Height Offset", "边缘物体相对路径或地表的高度偏移"));
                EditorGUILayout.PropertyField(
                    _edgeObjectsAlignToPath,
                    new GUIContent("Align To Path", "让边缘物体沿路径切线方向排列"));
                EditorGUILayout.PropertyField(
                    _edgeObjectsProjectToSurface,
                    new GUIContent("Project To Surface", "将边缘物体向下吸附到指定地表"));
                if (_edgeObjectsProjectToSurface.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(
                        _surfaceMask,
                        new GUIContent("Surface Layer", "边缘物体吸附时用于检测地表的物理层级"));
                    EditorGUILayout.PropertyField(
                        _rayHeight,
                        new GUIContent("Ray Height", "边缘物体向下检测地表的射线起始高度"));
                    EditorGUI.indentLevel--;
                }
                EditorGUI.indentLevel--;
            }
        }
    }

    private static string GetRibbonPresetLabel(RibbonPathPreset preset)
    {
        switch (preset)
        {
            case RibbonPathPreset.NaturalRoad:
                return "Natural Road Mesh";
            case RibbonPathPreset.RegularRoad:
                return "Regular Road Mesh";
            default:
                return "River Mesh";
        }
    }

    private void EnsureSelection()
    {
        int knotCount = _builder != null && _builder.knots != null
            ? _builder.knots.Count
            : 0;
        _selectedKnots.RemoveWhere(
            index => index < 0 || index >= knotCount);
        if (knotCount == 0)
        {
            _selectedKnot = 0;
            return;
        }

        _selectedKnot = Mathf.Clamp(_selectedKnot, 0, knotCount - 1);
        if (_selectedKnots.Count == 0)
            _selectedKnots.Add(_selectedKnot);
        else if (!_selectedKnots.Contains(_selectedKnot))
            _selectedKnot = GetFirstSelectedKnot();
    }

    private int GetFirstSelectedKnot()
    {
        int first = int.MaxValue;
        foreach (int index in _selectedKnots)
            first = Mathf.Min(first, index);
        return first == int.MaxValue ? 0 : first;
    }

    private void SelectOnly(int index)
    {
        _selectedKnots.Clear();
        _selectedKnots.Add(index);
        _selectedKnot = index;
        SelectionChanged();
    }

    private void SelectKnot(int index, bool shift, bool toggle)
    {
        if (toggle)
        {
            if (_selectedKnots.Contains(index))
            {
                if (_selectedKnots.Count > 1)
                {
                    _selectedKnots.Remove(index);
                    if (_selectedKnot == index)
                        _selectedKnot = GetFirstSelectedKnot();
                }
            }
            else
            {
                _selectedKnots.Add(index);
                _selectedKnot = index;
            }
        }
        else if (shift)
        {
            _selectedKnots.Add(index);
            _selectedKnot = index;
        }
        else
        {
            _selectedKnots.Clear();
            _selectedKnots.Add(index);
            _selectedKnot = index;
        }
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        _selectionVersion++;
        _groupHandleSelectionVersion = -1;
        Repaint();
        SceneView.RepaintAll();
    }

    private void DrawMountainCardSortingLayer()
    {
        SortingLayer[] layers = SortingLayer.layers;
        if (layers == null || layers.Length == 0)
        {
            EditorGUILayout.PropertyField(
                _mountainCardSortingLayerId,
                new GUIContent("Sorting Layer ID", "当前项目没有可用的排序层名称，直接填写排序层编号"));
            return;
        }

        int currentId = _mountainCardSortingLayerId.intValue;
        int currentIndex = -1;
        for (int i = 0; i < layers.Length; i++)
        {
            if (layers[i].id == currentId)
            {
                currentIndex = i;
                break;
            }
        }

        string[] names;
        int[] ids;
        if (currentIndex >= 0)
        {
            names = new string[layers.Length];
            ids = new int[layers.Length];
            for (int i = 0; i < layers.Length; i++)
            {
                names[i] = layers[i].name;
                ids[i] = layers[i].id;
            }
        }
        else
        {
            names = new string[layers.Length + 1];
            ids = new int[layers.Length + 1];
            names[0] = $"Missing ({currentId})";
            ids[0] = currentId;
            currentIndex = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                names[i + 1] = layers[i].name;
                ids[i + 1] = layers[i].id;
            }
        }

        int selectedIndex = EditorGUILayout.Popup(
            new GUIContent(
                "Sorting Layer",
                "选择面片山所属的 Unity 渲染排序层"),
            currentIndex,
            names);
        if (selectedIndex >= 0 && selectedIndex < ids.Length)
            _mountainCardSortingLayerId.intValue = ids[selectedIndex];
    }

    private void DrawPathEditing()
    {
        int knotCount = _builder.knots != null ? _builder.knots.Count : 0;
        EnsureSelection();

        using (new EditorGUILayout.VerticalScope("box"))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                    new GUIContent("Add", "在样条末尾添加一个控制点")))
                    AddKnot();
                using (new EditorGUI.DisabledScope(knotCount == 0))
                {
                    if (GUILayout.Button(
                        new GUIContent(
                            "Insert After",
                            "在主选中点后的曲线中点插入，保持原曲线形状")))
                        InsertKnot();
                }
                using (new EditorGUI.DisabledScope(
                    knotCount - _selectedKnots.Count < 2))
                {
                    if (GUILayout.Button(
                        new GUIContent("Remove", "删除全部选中的控制点")))
                        RemoveKnot();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(knotCount == 0))
                {
                    if (GUILayout.Button(
                        new GUIContent(
                            "Auto Smooth  Shift+S",
                            "自动平滑全部选中的控制点")))
                        SmoothSelectedKnot();
                    if (GUILayout.Button(
                        new GUIContent("Reverse", "反转整条样条的方向")))
                        ReverseSpline();
                    if (GUILayout.Button(
                        new GUIContent("Frame", "让场景视图聚焦到选中的控制点")))
                        FrameSelectedKnot();
                }
            }

            if (knotCount > 0 && _selectedKnots.Count > 0)
                DrawSelectedKnotFields(_builder.knots[_selectedKnot]);
        }

        if (DrawSectionFoldout(
            ref _showSceneHelp,
            "Scene Controls",
            "查看场景视图选择、框选和快捷键说明"))
        {
            EditorGUILayout.HelpBox(
                "双击蓝色曲线插点；Shift + 左键追加控制点。\n"
                + "单击选择；Shift 追加；Ctrl 切换；空白拖动框选。\n"
                + "W 移动；E 旋转；R 缩放；Shift+S 自动平滑；Delete 删除。\n"
                + "黄色端点用于编辑切线，具体联动由 Tangent Mode 决定。",
                MessageType.Info);
        }
    }

    private void DrawSelectedKnotFields(PathKnot knot)
    {
        EditorGUILayout.Space(4f);
        string selectionLabel = _selectedKnots.Count > 1
            ? $"Selected Points {_selectedKnots.Count}  (Primary {_selectedKnot})"
            : $"Selected Point {_selectedKnot}";
        EditorGUILayout.LabelField(selectionLabel, EditorStyles.miniBoldLabel);

        Vector3 position = EditorGUILayout.Vector3Field(
            new GUIContent("Position", "主控制点相对路径根节点的位置"),
            knot.position);
        int tangentModeIndex = Mathf.Max(
            0,
            Array.IndexOf(TangentModeValues, knot.tangentMode));
        tangentModeIndex = EditorGUILayout.Popup(
            new GUIContent(
                "Tangent Mode",
                "Linear 直线；Auto 自动平滑；Mirrored 等长镜像；"
                + "Continuous 同方向不同长度；Broken 两侧独立"),
            tangentModeIndex,
            TangentModeLabels);
        PathTangentMode tangentMode = TangentModeValues[tangentModeIndex];
        bool tangentsEditable = tangentMode == PathTangentMode.Mirrored
            || tangentMode == PathTangentMode.Continuous
            || tangentMode == PathTangentMode.Broken;
        Vector3 tangentIn;
        Vector3 tangentOut;
        using (new EditorGUI.DisabledScope(!tangentsEditable))
        {
            tangentIn = EditorGUILayout.Vector3Field(
                new GUIContent("Tangent In", "进入该控制点一侧的贝塞尔切线"),
                knot.tangentIn);
            tangentOut = EditorGUILayout.Vector3Field(
                new GUIContent("Tangent Out", "离开该控制点一侧的贝塞尔切线"),
                knot.tangentOut);
        }
        float width = knot.width;
        if (_builder.mode == PathBuildMode.River)
            width = EditorGUILayout.FloatField(
                new GUIContent("Path Width", "该控制点处河流、道路或离散区域的宽度"),
                knot.width);

        if (position == knot.position
            && tangentMode == knot.tangentMode
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
        knot.width = Mathf.Max(0.01f, width);
        if (tangentMode != knot.tangentMode)
        {
            foreach (int index in _selectedKnots)
                _builder.SetTangentMode(index, tangentMode);
        }
        if (tangentsEditable)
        {
            if (tangentOutChanged)
                _builder.SetTangent(_selectedKnot, false, tangentOut);
            else if (tangentInChanged)
                _builder.SetTangent(_selectedKnot, true, tangentIn);
        }
        MarkBuilderChanged(true);
    }

    private void DrawGenerationButtons()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.backgroundColor = new Color(0.5f, 0.85f, 0.5f);
            if (GUILayout.Button(
                new GUIContent("Rebuild Preview", "根据当前样条和参数立即重建预览"),
                GUILayout.Height(32f)))
                PathBuilderGenerator.Rebuild(_builder);
            GUI.backgroundColor = Color.white;

            if (GUILayout.Button(
                new GUIContent("Clear Preview", "仅清除生成预览，保留样条和参数"),
                GUILayout.Height(32f)))
                PathBuilderGenerator.ClearGenerated(_builder);
        }

        GUI.backgroundColor = new Color(0.55f, 0.75f, 1f);
        if (GUILayout.Button(
            new GUIContent(
                "Bake Generated Result",
                _builder.mode == PathBuildMode.Mountain
                    ? (_builder.keepMountainAuthoringAfterBake
                        ? "生成并连接山脉 Prefab，同时保留样条和设置"
                        : "生成并连接山脉 Prefab，然后结束当前样条编辑")
                    : "保留当前结果并与样条编辑器分离；河流 Mesh 会保存为资产"),
            GUILayout.Height(32f)))
        {
            PathSpline completedBuilder = _builder;
            bool deleteAuthoring = completedBuilder.mode
                    == PathBuildMode.Mountain
                && !completedBuilder.keepMountainAuthoringAfterBake;
            if (PathBuilderGenerator.Bake(completedBuilder)
                && deleteAuthoring)
            {
                EditorApplication.delayCall += () =>
                {
                    if (completedBuilder)
                        PathBuilderAuthoringStorage.Delete(completedBuilder);
                };
            }
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

        EventType sceneEventType = Event.current.rawType;

        DrawSplineCurve();
        if (_builder.mode == PathBuildMode.River)
            DrawRiverWidthPreview();
        DrawKnotButtons();
        DrawSelectedKnotHandles();
        HandleSceneShortcuts();

        if (_sceneChangePending
            && _builder.autoRebuild
            && (sceneEventType == EventType.MouseUp
                || sceneEventType == EventType.KeyUp))
        {
            _sceneChangePending = false;
            PathBuilderGenerator.Rebuild(_builder, false);
            _lastLiveRebuildTime = EditorApplication.timeSinceStartup;
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
        Vector3[] centers = new Vector3[table.Count];
        float[] requestedLeftHalfWidths = new float[table.Count];
        float[] requestedRightHalfWidths = new float[table.Count];
        Vector3[] sides = new Vector3[table.Count];
        float[] safeLeftHalfWidths = new float[table.Count];
        float[] safeRightHalfWidths = new float[table.Count];
        float totalLength = table[table.Count - 1].distance;
        for (int i = 0; i < table.Count; i++)
        {
            centers[i] = table[i].position;
            PathBuilderGenerator.GetRequestedRibbonHalfWidths(
                _builder,
                table[i].width,
                table[i].distance,
                totalLength,
                out requestedLeftHalfWidths[i],
                out requestedRightHalfWidths[i]);
        }

        PathBuilderGenerator.BuildSafeRiverFrames(
            centers,
            requestedLeftHalfWidths,
            requestedRightHalfWidths,
            _builder.closed,
            sides,
            safeLeftHalfWidths,
            safeRightHalfWidths);

        for (int i = 0; i < table.Count; i++)
        {
            left[i] = centers[i] - sides[i] * safeLeftHalfWidths[i];
            right[i] = centers[i] + sides[i] * safeRightHalfWidths[i];
        }

        Color previous = Handles.color;
        Handles.color = new Color(0.1f, 0.65f, 1f, 0.7f);
        Handles.DrawAAPolyLine(2f, left);
        Handles.DrawAAPolyLine(2f, right);
        Handles.color = previous;
    }

    private void DrawKnotButtons()
    {
        EnsureSelection();
        for (int i = 0; i < _builder.knots.Count; i++)
        {
            Vector3 worldPosition =
                _builder.Anchor.TransformPoint(_builder.knots[i].position);
            float size = HandleUtility.GetHandleSize(worldPosition) * 0.09f;
            Color previous = Handles.color;
            bool selected = _selectedKnots.Contains(i);
            Handles.color = i == _selectedKnot
                ? Color.yellow
                : selected
                    ? new Color(0.15f, 0.9f, 1f, 1f)
                    : Color.white;
            bool shift = Event.current.shift;
            bool toggle = Event.current.control || Event.current.command;
            if (Handles.Button(
                worldPosition,
                Quaternion.identity,
                size,
                size * 1.25f,
                Handles.SphereHandleCap))
            {
                SelectKnot(i, shift, toggle);
            }
            Handles.color = previous;
        }
    }

    private void DrawSelectedKnotHandles()
    {
        EnsureSelection();
        if (_selectedKnots.Count == 0)
            return;

        Transform transform = _builder.Anchor;
        Vector3 pivot = GetSelectionPivot(transform);

        foreach (int index in _selectedKnots)
        {
            PathKnot selectedKnot = _builder.knots[index];
            Vector3 selectedPosition =
                transform.TransformPoint(selectedKnot.position);
            DrawTangentHandles(index, selectedKnot, selectedPosition);
        }

        Tool currentTool = Tools.current;
        if (currentTool == Tool.Move || currentTool == Tool.Transform)
        {
            pivot = DrawMoveHandle(transform, pivot);
        }
        else if (currentTool == Tool.Rotate)
        {
            DrawRotationHandle(transform, pivot);
        }
        else if (currentTool == Tool.Scale)
        {
            DrawScaleHandle(transform, pivot);
        }

        PathKnot primary = _builder.knots[_selectedKnot];
        Handles.Label(
            pivot + Vector3.up * HandleUtility.GetHandleSize(pivot) * 0.15f,
            _selectedKnots.Count > 1
                ? $"{_selectedKnots.Count} points"
                : _builder.mode == PathBuildMode.River
                    ? $"{_selectedKnot}  W:{primary.width:F2}"
                    : _selectedKnot.ToString());
    }

    private Vector3 GetSelectionPivot(Transform transform)
    {
        Vector3 pivot = Vector3.zero;
        foreach (int index in _selectedKnots)
            pivot += transform.TransformPoint(_builder.knots[index].position);
        return pivot / Mathf.Max(1, _selectedKnots.Count);
    }

    private Vector3 DrawMoveHandle(Transform transform, Vector3 pivot)
    {
        Quaternion rotation = Tools.pivotRotation == PivotRotation.Local
            ? transform.rotation
            : Quaternion.identity;
        EditorGUI.BeginChangeCheck();
        Vector3 movedPosition =
            Handles.PositionHandle(pivot, rotation);
        if (!EditorGUI.EndChangeCheck())
            return pivot;

        Undo.RecordObject(_builder, "Move Spline Points");
        Vector3 delta = movedPosition - pivot;
        foreach (int index in _selectedKnots)
        {
            PathKnot knot = _builder.knots[index];
            Vector3 world = transform.TransformPoint(knot.position) + delta;
            knot.position = transform.InverseTransformPoint(world);
        }
        MarkBuilderChanged(false);
        return movedPosition;
    }

    private void DrawRotationHandle(
        Transform transform,
        Vector3 pivot)
    {
        PathKnot primary = _builder.knots[_selectedKnot];
        Quaternion frame = Tools.pivotRotation == PivotRotation.Local
            ? GetTangentFrame(primary, transform)
            : Quaternion.identity;
        PrepareGroupHandle(Tool.Rotate, pivot, frame);

        EditorGUI.BeginChangeCheck();
        Quaternion rotated = Handles.RotationHandle(
            _rotationHandleValue,
            _groupHandlePivot);
        if (!EditorGUI.EndChangeCheck())
            return;

        Undo.RecordObject(_builder, "Rotate Spline Points");
        _rotationHandleValue = rotated;
        Quaternion delta = rotated * Quaternion.Inverse(_rotationHandleStart);
        foreach (KeyValuePair<int, KnotHandleState> pair
                 in _groupHandleStates)
        {
            PathKnot knot = _builder.knots[pair.Key];
            KnotHandleState state = pair.Value;
            Vector3 startWorld = transform.TransformPoint(state.position);
            Vector3 rotatedWorld = _groupHandlePivot
                + delta * (startWorld - _groupHandlePivot);
            knot.position = transform.InverseTransformPoint(rotatedWorld);
            knot.tangentIn = transform.InverseTransformVector(
                delta * transform.TransformVector(state.tangentIn));
            knot.tangentOut = transform.InverseTransformVector(
                delta * transform.TransformVector(state.tangentOut));
        }
        MarkBuilderChanged(false);
    }

    private void DrawScaleHandle(
        Transform transform,
        Vector3 pivot)
    {
        PathKnot primary = _builder.knots[_selectedKnot];
        Quaternion frame = Tools.pivotRotation == PivotRotation.Local
            ? GetTangentFrame(primary, transform)
            : Quaternion.identity;
        PrepareGroupHandle(Tool.Scale, pivot, frame);

        float size = HandleUtility.GetHandleSize(_groupHandlePivot);
        EditorGUI.BeginChangeCheck();
        Vector3 scaled = Handles.ScaleHandle(
            _scaleHandleValue,
            _groupHandlePivot,
            _groupHandleFrame,
            size);
        if (!EditorGUI.EndChangeCheck())
            return;

        Undo.RecordObject(_builder, "Scale Spline Points");
        scaled.x = Mathf.Max(0.01f, scaled.x);
        scaled.y = Mathf.Max(0.01f, scaled.y);
        scaled.z = Mathf.Max(0.01f, scaled.z);
        _scaleHandleValue = scaled;

        foreach (KeyValuePair<int, KnotHandleState> pair
                 in _groupHandleStates)
        {
            PathKnot knot = _builder.knots[pair.Key];
            KnotHandleState state = pair.Value;
            Vector3 startWorld = transform.TransformPoint(state.position);
            Vector3 offset = startWorld - _groupHandlePivot;
            Vector3 scaledWorld = _groupHandlePivot
                + ScaleVectorInFrame(offset, _groupHandleFrame, scaled);
            knot.position = transform.InverseTransformPoint(scaledWorld);

            Vector3 inWorld = transform.TransformVector(state.tangentIn);
            Vector3 outWorld = transform.TransformVector(state.tangentOut);
            knot.tangentIn = transform.InverseTransformVector(
                ScaleVectorInFrame(inWorld, _groupHandleFrame, scaled));
            knot.tangentOut = transform.InverseTransformVector(
                ScaleVectorInFrame(outWorld, _groupHandleFrame, scaled));

            if (_builder.mode == PathBuildMode.River)
            {
                Vector3 riverRight = GetRiverRight(
                    state.tangentOut,
                    transform);
                float widthScale = ScaleVectorInFrame(
                    riverRight,
                    _groupHandleFrame,
                    scaled).magnitude;
                knot.width = Mathf.Max(0.01f, state.width * widthScale);
            }
        }
        MarkBuilderChanged(false);
    }

    private void PrepareGroupHandle(
        Tool tool,
        Vector3 pivot,
        Quaternion frame)
    {
        if (_groupHandleTool == tool
            && _groupHandleSelectionVersion == _selectionVersion
            && GUIUtility.hotControl != 0)
        {
            return;
        }

        _groupHandleTool = tool;
        _groupHandleSelectionVersion = _selectionVersion;
        _groupHandlePivot = pivot;
        _groupHandleFrame = frame;
        _groupHandleStates.Clear();
        foreach (int index in _selectedKnots)
        {
            PathKnot knot = _builder.knots[index];
            _groupHandleStates[index] = new KnotHandleState
            {
                position = knot.position,
                tangentIn = knot.tangentIn,
                tangentOut = knot.tangentOut,
                width = knot.width
            };
        }

        _rotationHandleStart = frame;
        _rotationHandleValue = frame;
        _scaleHandleValue = Vector3.one;
    }

    private static Vector3 ScaleVectorInFrame(
        Vector3 worldVector,
        Quaternion frame,
        Vector3 scale)
    {
        Vector3 local = Quaternion.Inverse(frame) * worldVector;
        return frame * Vector3.Scale(local, scale);
    }

    private static Quaternion GetTangentFrame(
        PathKnot knot,
        Transform transform)
    {
        Vector3 forward = transform.TransformVector(knot.tangentOut);
        if (forward.sqrMagnitude < 0.000001f)
            forward = transform.forward;
        forward.Normalize();

        Vector3 up = transform.up;
        if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.999f)
            up = transform.forward;
        return Quaternion.LookRotation(forward, up);
    }

    private static Vector3 GetRiverRight(
        Vector3 tangentOut,
        Transform transform)
    {
        Vector3 tangent = transform.TransformVector(tangentOut);
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.000001f)
            tangent = Vector3.forward;
        tangent.Normalize();
        return Vector3.Cross(Vector3.up, tangent).normalized;
    }

    private void DrawTangentHandles(
        int knotIndex,
        PathKnot knot,
        Vector3 worldPosition)
    {
        Transform transform = _builder.Anchor;
        Vector3 inWorld =
            transform.TransformPoint(knot.position + knot.tangentIn);
        Vector3 outWorld =
            transform.TransformPoint(knot.position + knot.tangentOut);
        Color previous = Handles.color;
        Handles.color = new Color(1f, 0.75f, 0.1f, 1f);
        Handles.DrawLine(worldPosition, inWorld);
        Handles.DrawLine(worldPosition, outWorld);

        bool editable = knot.tangentMode == PathTangentMode.Mirrored
            || knot.tangentMode == PathTangentMode.Continuous
            || knot.tangentMode == PathTangentMode.Broken;
        if (editable)
        {
            float inSize = HandleUtility.GetHandleSize(inWorld) * 0.07f;
            float outSize = HandleUtility.GetHandleSize(outWorld) * 0.07f;

            EditorGUI.BeginChangeCheck();
            Vector3 movedIn = Handles.FreeMoveHandle(
                inWorld,
                inSize,
                Vector3.zero,
                Handles.RectangleHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_builder, "Move Spline In Tangent");
                Vector3 local = transform.InverseTransformPoint(movedIn)
                    - knot.position;
                _builder.SetTangent(knotIndex, true, local);
                MarkBuilderChanged(false);
            }

            EditorGUI.BeginChangeCheck();
            Vector3 movedOut = Handles.FreeMoveHandle(
                outWorld,
                outSize,
                Vector3.zero,
                Handles.RectangleHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_builder, "Move Spline Out Tangent");
                Vector3 local = transform.InverseTransformPoint(movedOut)
                    - knot.position;
                _builder.SetTangent(knotIndex, false, local);
                MarkBuilderChanged(false);
            }
        }
        Handles.color = previous;
    }

    private void HandleSceneShortcuts()
    {
        Event current = Event.current;
        _boxControlId = GUIUtility.GetControlID(
            BoxSelectionControlHash,
            FocusType.Passive);

        if (current.type == EventType.Layout && !current.alt)
        {
            HandleUtility.AddDefaultControl(_boxControlId);
        }

        if (current.type == EventType.MouseDown
            && current.button == 0
            && current.clickCount >= 2
            && !current.alt
            && !current.shift
            && !current.control
            && !current.command
            && GUIUtility.hotControl == 0
            && InsertKnotOnCurve(current.mousePosition))
        {
            current.Use();
            SceneView.RepaintAll();
            return;
        }

        if (current.type == EventType.MouseDown
            && current.button == 0
            && !current.alt
            && GUIUtility.hotControl == 0)
        {
            _boxSelecting = true;
            _boxDragged = false;
            _boxAdditive = current.shift && !current.control
                && !current.command;
            _boxToggle = current.control || current.command;
            _boxStart = current.mousePosition;
            _boxCurrent = _boxStart;
            GUIUtility.hotControl = _boxControlId;
            current.Use();
        }

        if (_boxSelecting
            && GUIUtility.hotControl == _boxControlId
            && current.type == EventType.MouseDrag)
        {
            _boxCurrent = current.mousePosition;
            _boxDragged = _boxDragged
                || Vector2.Distance(_boxStart, _boxCurrent)
                    >= BoxSelectionDragThreshold;
            current.Use();
            SceneView.RepaintAll();
        }

        if (_boxSelecting
            && GUIUtility.hotControl == _boxControlId
            && current.type == EventType.MouseUp
            && current.button == 0)
        {
            _boxCurrent = current.mousePosition;
            if (_boxDragged)
            {
                ApplyBoxSelection();
            }
            else if (_boxAdditive)
            {
                AppendKnotAtGuiPoint(_boxStart);
            }
            else
            {
                PathBuilderSplineUI.PickSceneObject(
                    _boxStart,
                    _boxToggle);
            }

            _boxSelecting = false;
            _boxDragged = false;
            GUIUtility.hotControl = 0;
            current.Use();
            SceneView.RepaintAll();
        }

        if (_boxSelecting
            && current.type == EventType.KeyDown
            && current.keyCode == KeyCode.Escape)
        {
            _boxSelecting = false;
            _boxDragged = false;
            if (GUIUtility.hotControl == _boxControlId)
                GUIUtility.hotControl = 0;
            current.Use();
            SceneView.RepaintAll();
        }

        if (current.type == EventType.KeyDown
            && current.shift
            && !current.control
            && !current.command
            && !current.alt
            && current.keyCode == KeyCode.S)
        {
            SmoothSelectedKnot();
            current.Use();
            SceneView.RepaintAll();
            return;
        }

        if (current.type == EventType.KeyDown
            && (current.keyCode == KeyCode.Delete
                || current.keyCode == KeyCode.Backspace)
            && _builder.knots.Count > 2)
        {
            RemoveKnot();
            current.Use();
        }

        DrawBoxSelectionOverlay();
    }

    private bool InsertKnotOnCurve(Vector2 guiPoint)
    {
        if (!TryFindNearestCurvePoint(
                guiPoint,
                out int segmentIndex,
                out float segmentT))
        {
            return false;
        }

        Undo.RecordObject(_builder, "Insert Spline Point On Curve");
        int insertedIndex = _builder.InsertKnotOnSegment(
            segmentIndex,
            segmentT);
        SelectOnly(insertedIndex);
        MarkBuilderChanged(true);
        return true;
    }

    private bool TryFindNearestCurvePoint(
        Vector2 guiPoint,
        out int nearestSegment,
        out float nearestT)
    {
        nearestSegment = -1;
        nearestT = 0f;
        const float hitDistance = 12f;
        float bestDistance = hitDistance;
        int sampleCount = Mathf.Clamp(
            _builder.samplesPerCurve * 2,
            24,
            128);

        for (int segment = 0; segment < _builder.SegmentCount; segment++)
        {
            int nextIndex = (segment + 1) % _builder.knots.Count;
            PathKnot start = _builder.knots[segment];
            PathKnot end = _builder.knots[nextIndex];
            Vector3 previousWorld = _builder.Anchor.TransformPoint(
                PathSplineUtility.EvaluatePosition(start, end, 0f));
            Vector2 previousGui = HandleUtility.WorldToGUIPoint(previousWorld);

            for (int step = 1; step <= sampleCount; step++)
            {
                float endT = step / (float)sampleCount;
                Vector3 world = _builder.Anchor.TransformPoint(
                    PathSplineUtility.EvaluatePosition(start, end, endT));
                Vector2 endGui = HandleUtility.WorldToGUIPoint(world);
                float lineT = ClosestLineParameter(
                    guiPoint,
                    previousGui,
                    endGui);
                Vector2 closest = Vector2.Lerp(previousGui, endGui, lineT);
                float distance = Vector2.Distance(guiPoint, closest);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearestSegment = segment;
                    nearestT = ((step - 1) + lineT) / sampleCount;
                }

                previousGui = endGui;
            }
        }

        return nearestSegment >= 0;
    }

    private static float ClosestLineParameter(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 direction = end - start;
        float lengthSquared = direction.sqrMagnitude;
        if (lengthSquared < 0.0001f)
            return 0f;
        return Mathf.Clamp01(
            Vector2.Dot(point - start, direction) / lengthSquared);
    }

    private void AppendKnotAtGuiPoint(Vector2 guiPoint)
    {
        Ray ray = HandleUtility.GUIPointToWorldRay(guiPoint);
        if (!TryGetAppendPosition(ray, out Vector3 worldPosition))
            return;

        Undo.RecordObject(_builder, "Append Spline Point");
        int appended = _builder.AppendKnot(
            _builder.Anchor.InverseTransformPoint(worldPosition));
        SelectOnly(appended);
        MarkBuilderChanged(true);
    }

    private void ApplyBoxSelection()
    {
        Rect selectionRect = MakeRect(_boxStart, _boxCurrent);
        bool crossingSelection = _boxCurrent.x < _boxStart.x;
        List<int> hits = new List<int>();
        Camera camera = SceneView.currentDrawingSceneView
            ? SceneView.currentDrawingSceneView.camera
            : null;

        for (int i = 0; i < _builder.knots.Count; i++)
        {
            Vector3 worldPosition =
                _builder.Anchor.TransformPoint(_builder.knots[i].position);
            if (camera && camera.WorldToScreenPoint(worldPosition).z < 0f)
                continue;
            Vector2 guiPosition = HandleUtility.WorldToGUIPoint(worldPosition);
            float radius = GetKnotGuiRadius(worldPosition, camera);
            Rect knotRect = new Rect(
                guiPosition.x - radius,
                guiPosition.y - radius,
                radius * 2f,
                radius * 2f);
            bool hit = crossingSelection
                ? selectionRect.Overlaps(knotRect, true)
                : selectionRect.Contains(knotRect.min)
                    && selectionRect.Contains(knotRect.max);
            if (hit)
                hits.Add(i);
        }

        if (hits.Count == 0)
            return;

        if (_boxToggle)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                int index = hits[i];
                if (_selectedKnots.Contains(index))
                {
                    if (_selectedKnots.Count > 1)
                        _selectedKnots.Remove(index);
                }
                else
                {
                    _selectedKnots.Add(index);
                }
            }
        }
        else
        {
            if (!_boxAdditive)
                _selectedKnots.Clear();
            for (int i = 0; i < hits.Count; i++)
                _selectedKnots.Add(hits[i]);
        }

        if (_selectedKnots.Count == 0)
            _selectedKnots.Add(hits[0]);
        _selectedKnot = _selectedKnots.Contains(hits[hits.Count - 1])
            ? hits[hits.Count - 1]
            : GetFirstSelectedKnot();
        SelectionChanged();
    }

    private static float GetKnotGuiRadius(
        Vector3 worldPosition,
        Camera camera)
    {
        float worldRadius =
            HandleUtility.GetHandleSize(worldPosition) * 0.09f;
        if (!camera)
            return 6f;
        Vector2 center = HandleUtility.WorldToGUIPoint(worldPosition);
        Vector2 edge = HandleUtility.WorldToGUIPoint(
            worldPosition + camera.transform.right * worldRadius);
        return Mathf.Max(3f, Vector2.Distance(center, edge));
    }

    private static Rect MakeRect(Vector2 start, Vector2 end)
    {
        return Rect.MinMaxRect(
            Mathf.Min(start.x, end.x),
            Mathf.Min(start.y, end.y),
            Mathf.Max(start.x, end.x),
            Mathf.Max(start.y, end.y));
    }

    private void DrawBoxSelectionOverlay()
    {
        if (!_boxSelecting || !_boxDragged
            || Event.current.type != EventType.Repaint)
            return;

        Rect rect = MakeRect(_boxStart, _boxCurrent);
        bool crossingSelection = _boxCurrent.x < _boxStart.x;
        Color fill = crossingSelection
            ? new Color(0.15f, 0.75f, 1f, 0.12f)
            : new Color(0.2f, 0.55f, 1f, 0.12f);
        Color border = crossingSelection
            ? new Color(0.2f, 0.9f, 1f, 0.9f)
            : new Color(0.25f, 0.65f, 1f, 0.9f);

        Handles.BeginGUI();
        EditorGUI.DrawRect(rect, fill);
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, 1f), border);
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - 1f, rect.width, 1f), border);
        EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, 1f, rect.height), border);
        EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.yMin, 1f, rect.height), border);
        Handles.EndGUI();
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
        SelectOnly(_builder.AppendKnot(localPosition));
        MarkBuilderChanged(true);
    }

    private void InsertKnot()
    {
        Undo.RecordObject(_builder, "Insert Spline Point");
        SelectOnly(_builder.InsertAfter(_selectedKnot));
        MarkBuilderChanged(true);
    }

    private void RemoveKnot()
    {
        EnsureSelection();
        if (_builder.knots.Count - _selectedKnots.Count < 2)
            return;
        Undo.RecordObject(_builder, "Remove Spline Points");
        List<int> indices = new List<int>(_selectedKnots);
        indices.Sort((a, b) => b.CompareTo(a));
        int nextPrimary = _selectedKnot;
        for (int i = 0; i < indices.Count; i++)
        {
            _builder.RemoveKnot(indices[i]);
            if (indices[i] < nextPrimary)
                nextPrimary--;
        }
        nextPrimary = Mathf.Clamp(nextPrimary, 0, _builder.knots.Count - 1);
        SelectOnly(nextPrimary);
        MarkBuilderChanged(true);
    }

    private void SmoothSelectedKnot()
    {
        EnsureSelection();
        Undo.RecordObject(_builder, "Smooth Spline Points");
        foreach (int index in _selectedKnots)
            _builder.SmoothKnot(index);
        MarkBuilderChanged(true);
    }

    private void ReverseSpline()
    {
        Undo.RecordObject(_builder, "Reverse Spline");
        int knotCount = _builder.knots.Count;
        _builder.Reverse();
        HashSet<int> reversedSelection = new HashSet<int>();
        foreach (int index in _selectedKnots)
            reversedSelection.Add(knotCount - 1 - index);
        _selectedKnots.Clear();
        foreach (int index in reversedSelection)
            _selectedKnots.Add(index);
        _selectedKnot = knotCount - 1 - _selectedKnot;
        SelectionChanged();
        MarkBuilderChanged(true);
    }

    private void FrameSelectedKnot()
    {
        if (!SceneView.lastActiveSceneView || _builder.knots.Count == 0)
            return;
        EnsureSelection();
        Vector3 worldPosition = GetSelectionPivot(_builder.Anchor);
        SceneView.lastActiveSceneView.LookAt(
            worldPosition,
            SceneView.lastActiveSceneView.rotation,
            10f);
    }

    private void MarkBuilderChanged(bool rebuildImmediately)
    {
        _builder.RefreshAutoTangents();
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
            _lastLiveRebuildTime = EditorApplication.timeSinceStartup;
        }
        else
        {
            _sceneChangePending = true;
            double now = EditorApplication.timeSinceStartup;
            double interval;
            if (_builder.mode == PathBuildMode.Mountain)
            {
                interval = MountainLiveRebuildInterval;
            }
            else
            {
                interval = _builder.generateEdgeObjects
                    ? EdgeObjectLiveRebuildInterval
                    : RiverLiveRebuildInterval;
            }

            if (now - _lastLiveRebuildTime >= interval)
            {
                PathBuilderGenerator.Rebuild(_builder, false);
                _lastLiveRebuildTime = now;
            }
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
        _outputs = serializedObject.FindProperty("outputs");
        _mountainDecalSet =
            serializedObject.FindProperty("mountainDecalSet");
        _mountainSources = serializedObject.FindProperty("mountainSources");
        _randomSeed = serializedObject.FindProperty("randomSeed");
        _mountainSpacing = serializedObject.FindProperty("mountainSpacing");
        _mountainDistributionUniformity =
            serializedObject.FindProperty("mountainDistributionUniformity");
        _brushWidth = serializedObject.FindProperty("brushWidth");
        _uniformScale = serializedObject.FindProperty("uniformScale");
        _uniformScaleRange =
            serializedObject.FindProperty("uniformScaleRange");
        _horizontalScaleRange =
            serializedObject.FindProperty("horizontalScaleRange");
        _verticalScaleRange =
            serializedObject.FindProperty("verticalScaleRange");
        _yawRange = serializedObject.FindProperty("yawRange");
        _alignToSpline = serializedObject.FindProperty("alignToSpline");
        _useSplineWidth = serializedObject.FindProperty("useSplineWidth");
        _mountainHeightMode =
            serializedObject.FindProperty("mountainHeightMode");
        _mountainCardMaterial =
            serializedObject.FindProperty("mountainCardMaterial");
        _mountainCardHeight =
            serializedObject.FindProperty("mountainCardHeight");
        _mountainCardPivot =
            serializedObject.FindProperty("mountainCardPivot");
        _mountainCardRandomFlip =
            serializedObject.FindProperty("mountainCardRandomFlip");
        _mountainCardColor =
            serializedObject.FindProperty("mountainCardColor");
        _mountainCardSortingMode =
            serializedObject.FindProperty("mountainCardSortingMode");
        _mountainCardSortingLayerId =
            serializedObject.FindProperty("mountainCardSortingLayerId");
        _mountainCardSortingOrder =
            serializedObject.FindProperty("mountainCardSortingOrder");
        _mountainCardRenderQueue =
            serializedObject.FindProperty("mountainCardRenderQueue");
        _mountainBakeOutput =
            serializedObject.FindProperty("mountainBakeOutput");
        _keepMountainAuthoringAfterBake =
            serializedObject.FindProperty("keepMountainAuthoringAfterBake");
        _mountainDecalSurfaceMask =
            serializedObject.FindProperty("mountainDecalSurfaceMask");
        _mountainDecalProjectionDistance =
            serializedObject.FindProperty("mountainDecalProjectionDistance");
        _mountainDecalSurfaceOffset =
            serializedObject.FindProperty("mountainDecalSurfaceOffset");
        _ribbonPreset = serializedObject.FindProperty("ribbonPreset");
        _riverMaterial = serializedObject.FindProperty("riverMaterial");
        _riverHeightMode = serializedObject.FindProperty("riverHeightMode");
        _riverSegmentLength =
            serializedObject.FindProperty("riverSegmentLength");
        _riverUvLength =
            serializedObject.FindProperty("riverUvLength");
        _generateCollider =
            serializedObject.FindProperty("generateCollider");
        _edgeNoiseEnabled =
            serializedObject.FindProperty("edgeNoiseEnabled");
        _edgeNoiseSeed = serializedObject.FindProperty("edgeNoiseSeed");
        _leftEdgeNoiseAmplitude =
            serializedObject.FindProperty("leftEdgeNoiseAmplitude");
        _rightEdgeNoiseAmplitude =
            serializedObject.FindProperty("rightEdgeNoiseAmplitude");
        _edgeNoiseWavelength =
            serializedObject.FindProperty("edgeNoiseWavelength");
        _generateEdgeObjects =
            serializedObject.FindProperty("generateEdgeObjects");
        _generateLeftEdge =
            serializedObject.FindProperty("generateLeftEdge");
        _generateRightEdge =
            serializedObject.FindProperty("generateRightEdge");
        _edgePrefabs = serializedObject.FindProperty("edgePrefabs");
        _edgeObjectSeed =
            serializedObject.FindProperty("edgeObjectSeed");
        _edgeObjectSpacingRange =
            serializedObject.FindProperty("edgeObjectSpacingRange");
        _edgeObjectDensity =
            serializedObject.FindProperty("edgeObjectDensity");
        _edgeObjectScaleRange =
            serializedObject.FindProperty("edgeObjectScaleRange");
        _edgeObjectYawRange =
            serializedObject.FindProperty("edgeObjectYawRange");
        _edgeObjectOutwardOffset =
            serializedObject.FindProperty("edgeObjectOutwardOffset");
        _edgeObjectHeightOffset =
            serializedObject.FindProperty("edgeObjectHeightOffset");
        _edgeObjectsAlignToPath =
            serializedObject.FindProperty("edgeObjectsAlignToPath");
        _edgeObjectsProjectToSurface =
            serializedObject.FindProperty("edgeObjectsProjectToSurface");
        _generatedAssetOutputFolder =
            serializedObject.FindProperty("generatedAssetOutputFolder");
        _prefabOutputFolder =
            serializedObject.FindProperty("prefabOutputFolder");
    }
}

[CustomEditor(typeof(MountainDecalSet))]
internal sealed class MountainDecalSetEditor : Editor
{
    private SerializedProperty _sources;
    private SerializedProperty _baseMaterial;
    private SerializedProperty _renderQueue;
    private SerializedProperty _materialPages;

    private void OnEnable()
    {
        _sources = serializedObject.FindProperty("sources");
        _baseMaterial = serializedObject.FindProperty("baseMaterial");
        _renderQueue = serializedObject.FindProperty("renderQueue");
        _materialPages = serializedObject.FindProperty("materialPages");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        EditorGUILayout.LabelField(
            new GUIContent("Mountain Decal Set", "供多条山脉路径共用的图片、权重与材质设置"),
            EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.PropertyField(
                _sources,
                new GUIContent(
                    "Mountain Sources",
                    "同一图集中的多个 Sprite 会保留各自 UV，并共用一个烘焙材质"),
                true);
            EditorGUILayout.PropertyField(
                _baseMaterial,
                new GUIContent(
                    "Base Material",
                    "共享材质模板；留空时使用 URP Unlit 透明材质"));
            EditorGUILayout.PropertyField(
                _renderQueue,
                new GUIContent(
                    "Render Queue",
                    "该资源集生成的所有贴花材质统一使用此渲染队列"));
            _renderQueue.intValue = Mathf.Clamp(
                _renderQueue.intValue,
                2501,
                3999);
            EditorGUILayout.LabelField(
                new GUIContent(
                    "Cached Material Pages",
                    "每张底层图集纹理只生成一个共享材质页"),
                _materialPages != null
                    ? _materialPages.arraySize.ToString()
                    : "0");
        }
        EditorGUILayout.HelpBox(
            "推荐把多张山图切成同一张 Sprite 图集。多条样条引用该资源集后，会共享贴图、权重和持久材质；随机种子、分布和缩放仍由各自样条控制。使用 SpriteAtlas 时请关闭 Packing Rotation 并使用 Full Rect；不同底层纹理会各自使用一个共享材质页。",
            MessageType.Info);
        if (serializedObject.ApplyModifiedProperties())
        {
            PathBuilderGenerator.RefreshMountainDecalSetMaterials(
                (MountainDecalSet)target);
        }
    }
}

[CustomPropertyDrawer(typeof(MountainSourceEntry))]
internal sealed class MountainSourceEntryDrawer : PropertyDrawer
{
    public override float GetPropertyHeight(
        SerializedProperty property,
        GUIContent label)
    {
        return EditorGUIUtility.singleLineHeight * 2f
            + EditorGUIUtility.standardVerticalSpacing;
    }

    public override void OnGUI(
        Rect position,
        SerializedProperty property,
        GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        float lineHeight = EditorGUIUtility.singleLineHeight;
        Rect firstLine = new Rect(
            position.x,
            position.y,
            position.width,
            lineHeight);
        int elementIndex = GetArrayElementIndex(property.propertyPath);
        GUIContent elementLabel = elementIndex >= 0
            ? new GUIContent(
                $"Source {elementIndex + 1}",
                "设置该山脉资源的类型、权重和资源引用")
            : label;
        Rect content = EditorGUI.PrefixLabel(firstLine, elementLabel);
        float weightWidth = Mathf.Min(100f, content.width * 0.38f);
        Rect typeRect = new Rect(
            content.x,
            content.y,
            content.width - weightWidth - 4f,
            lineHeight);
        Rect weightRect = new Rect(
            typeRect.xMax + 4f,
            content.y,
            weightWidth,
            lineHeight);

        SerializedProperty sourceType =
            property.FindPropertyRelative("sourceType");
        sourceType.enumValueIndex = EditorGUI.Popup(
            typeRect,
            sourceType.enumValueIndex,
            new[] { "Prefab Model", "Sprite Card", "Texture Card" });
        const float weightLabelWidth = 44f;
        Rect weightLabelRect = new Rect(
            weightRect.x,
            weightRect.y,
            Mathf.Min(weightLabelWidth, weightRect.width),
            weightRect.height);
        Rect weightFieldRect = new Rect(
            weightLabelRect.xMax + 2f,
            weightRect.y,
            Mathf.Max(0f, weightRect.xMax - weightLabelRect.xMax - 2f),
            weightRect.height);
        EditorGUI.LabelField(
            weightLabelRect,
            new GUIContent("Weight", "控制该样式被随机选中的概率"));
        EditorGUI.PropertyField(
            weightFieldRect,
            property.FindPropertyRelative("weight"),
            GUIContent.none);

        MountainSourceType type =
            (MountainSourceType)sourceType.enumValueIndex;
        SerializedProperty assetProperty;
        string assetLabel;
        switch (type)
        {
            case MountainSourceType.SpriteCard:
                assetProperty = property.FindPropertyRelative("sprite");
                assetLabel = "Sprite Asset";
                break;
            case MountainSourceType.TextureCard:
                assetProperty = property.FindPropertyRelative("texture");
                assetLabel = "Texture Asset";
                break;
            default:
                assetProperty = property.FindPropertyRelative("prefab");
                assetLabel = "Prefab Asset";
                break;
        }

        Rect secondLine = new Rect(
            position.x,
            position.y + lineHeight
                + EditorGUIUtility.standardVerticalSpacing,
            position.width,
            lineHeight);
        EditorGUI.PropertyField(
            secondLine,
            assetProperty,
            new GUIContent(assetLabel, "拖入该条目实际使用的山脉资源"));
        EditorGUI.EndProperty();
    }

    private static int GetArrayElementIndex(string propertyPath)
    {
        const string marker = "Array.data[";
        int markerIndex = propertyPath.LastIndexOf(
            marker,
            StringComparison.Ordinal);
        if (markerIndex < 0)
            return -1;

        int numberStart = markerIndex + marker.Length;
        int numberEnd = propertyPath.IndexOf(']', numberStart);
        if (numberEnd <= numberStart)
            return -1;

        return int.TryParse(
            propertyPath.Substring(numberStart, numberEnd - numberStart),
            out int index)
            ? index
            : -1;
    }
}
