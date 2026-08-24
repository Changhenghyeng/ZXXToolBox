using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Spine.Unity;
using Spine.Unity.Editor;

/// <summary>
/// SLG 建筑面片预制体生成工具：从透明底正交渲染图一键生成带碰撞体的面片预制体。
/// 世界缩放根据图像中可见地脚面、网格尺寸和参考相机朝向自动计算。
/// </summary>
public sealed class BuildingCardTool : EditorWindow
{
    private const string PrefsKey = "BuildingCardTool.Settings";
    private const string PanelRatioPrefsKey = PrefsKey + ".PanelRatio";
    private const string UnityPreviewPrefsKey = PrefsKey + ".UnityPreview";
    private const string WorkspacePrefsKey = PrefsKey + ".Workspace";
    private const string ScenePreviewRootName = "Building Card Preview";
    private const int MaxAnalysisDimension = 1024;
    private const float SplitterWidth = 6f;
    private const float MinimumLeftPanelWidth = 300f;
    private const float MinimumPreviewPanelWidth = 260f;

    [Serializable]
    private sealed class Entry
    {
        public Texture2D texture;
        public int gridWidth = 1;
        public int gridDepth = 1;
        public bool enabled = true;
        public bool useManualGroundPoints;
        public Vector2 manualNearPoint;
        public Vector2 manualLeftPoint;
        public Vector2 manualRightPoint;
        public Vector2 manualFarPoint;
        [NonSerialized] public AnalysisResult result;
        [NonSerialized] public string generationError;
    }

    [Serializable]
    private sealed class SavedEntry
    {
        public string textureGuid;
        public int gridWidth = 1;
        public int gridDepth = 1;
        public bool enabled = true;
        public bool useManualGroundPoints;
        public Vector2 manualNearPoint;
        public Vector2 manualLeftPoint;
        public Vector2 manualRightPoint;
        public Vector2 manualFarPoint;
    }

    [Serializable]
    private sealed class WorkspaceState
    {
        public string referenceCameraGlobalId;
        public int referenceCameraInstanceId;
        public int previewIndex = -1;
        public string spineDataGuid;
        public string spineAnimation;
        public bool spineLoop = true;
        public int spineGridWidth = 1;
        public int spineGridDepth = 1;
        public bool spineUseManualGroundPoints;
        public Vector2 spineManualNearPoint;
        public Vector2 spineManualLeftPoint;
        public Vector2 spineManualRightPoint;
        public Vector2 spineManualFarPoint;
        public List<SavedEntry> entries = new List<SavedEntry>();
    }

    [Serializable]
    private sealed class Settings
    {
        public float cellWorldSize = 1f;
        public float bodyAlphaThreshold = 0.95f;
        public float clickHeightPadding = 0.15f;
        public float clickHorizontalPadding = 0.05f;
        public float visualMargin;
        public bool placePrefabsInScene;
        public string outputFolder = "Assets/Art/BuildingCards";
    }

    private sealed class AnalysisResult
    {
        public bool success;
        public string message;
        public Vector2 rootPixel;
        public Vector2 nearCorner;
        public Vector2 leftCorner;
        public Vector2 rightCorner;
        public Vector2 farCorner;
        public float analysisPixelsPerWorldUnit;
        public float worldPerSourcePixel;
        public float clickHeight;
        public float clickCenterY;
        public float clickFitScore;
        public float visualGroundLift;
        public Vector3 colliderCenterFromRoot;
        public Vector2 visualMinPixel;
        public Vector2 visualMaxPixel;
        public float groundFitQuality;
    }

    private struct Line
    {
        public float slope;
        public float intercept;
        public float residual;
    }

    private struct OutputGeometry
    {
        public float groundWidth;
        public float groundDepth;
        public float scaleFactor;
        public float colliderWidth;
        public float colliderDepth;
    }

    private sealed class MathGuideWindow : EditorWindow
    {
        private Vector2 scrollPosition;

        public static void ShowWindow()
        {
            var window = GetWindow<MathGuideWindow>(false, "Building Card Guide", true);
            window.minSize = new Vector2(400f, 320f);
            window.Show();
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Math Model & Data Source", EditorStyles.boldLabel);
            EditorGUILayout.Space(3f);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            DrawMathModelGuideContent();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space(3f);
            DrawSignature();
        }
    }

    private readonly List<Entry> entries = new List<Entry>();
    private readonly Settings settings = new Settings();
    private readonly BuildingCardAtlasPanel atlasPanel = new BuildingCardAtlasPanel();

    private Camera referenceCamera;
    private SkeletonDataAsset spineDataAsset;
    private string spineAnimation = string.Empty;
    private bool spineLoop = true;
    private int spineGridWidth = 1;
    private int spineGridDepth = 1;
    private Texture2D spinePreviewTexture;
    private Entry spinePreviewEntry;
    private bool spinePreviewActive;
    private SkeletonAnimation spinePreviewSkeleton;
    private PreviewRenderUtility spinePreviewUtility;
    private Texture spinePreviewRenderedFrame;
    private Vector2 spinePreviewViewCenter;
    private float spinePreviewViewWidth;
    private float spinePreviewViewHeight;
    private double spinePreviewLastFrameTime;
    private bool spinePreviewPlaying;
    private bool spinePreviewRefreshPending;
    private bool spineUseManualGroundPoints;
    private Vector2 spineManualNearPoint;
    private Vector2 spineManualLeftPoint;
    private Vector2 spineManualRightPoint;
    private Vector2 spineManualFarPoint;
    private Vector2 scroll;
    private Vector2 leftPanelScroll;
    private float leftPanelRatio = 0.54f;
    private bool draggingPanelSplitter;
    private bool useUnityPreview;
    private int previewIndex = -1;
    private int draggingEntryIndex = -1;
    private PreviewPoint draggingPoint;
    private Vector2 previousDragPixel;
    private readonly HashSet<Entry> pendingEntryAnalyses = new HashSet<Entry>();
    private bool pendingAnalyzeAll;
    private bool analysisUpdateRegistered;
    private double analysisDueTime;

    private enum PreviewPoint { None, Near, Left, Right, Far, Center }

    public static void ShowWindow()
    {
        var window = GetWindow<BuildingCardTool>("Building Card Prefab");
        window.minSize = new Vector2(720, 460);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += UpdateSpinePreviewAnimation;
        LoadSettings();
        LoadWorkspaceState();
        leftPanelRatio = Mathf.Clamp(EditorPrefs.GetFloat(PanelRatioPrefsKey, leftPanelRatio), 0.2f, 0.8f);
        useUnityPreview = EditorPrefs.GetBool(UnityPreviewPrefsKey, true);
        if (entries.Count > 0 && referenceCamera != null)
            ScheduleAnalysis(null, true, 0.05f);
        if (spineDataAsset != null)
            ScheduleSpinePreviewRefresh();
    }

    private void OnDisable()
    {
        EditorApplication.update -= UpdateSpinePreviewAnimation;
        EditorApplication.delayCall -= ProcessScheduledSpinePreviewRefresh;
        spinePreviewRefreshPending = false;
        ClearSpinePreview();
        SaveSettings();
        SaveWorkspaceState();
        EditorPrefs.SetFloat(PanelRatioPrefsKey, leftPanelRatio);
        EditorPrefs.SetBool(UnityPreviewPrefsKey, useUnityPreview);
        if (analysisUpdateRegistered)
        {
            EditorApplication.update -= ProcessScheduledAnalysis;
            analysisUpdateRegistered = false;
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("SLG Building Card Prefab", EditorStyles.boldLabel);
        var contentWidth = Mathf.Max(1f, position.width - 18f);
        var panelsWidth = Mathf.Max(1f, contentWidth - SplitterWidth);
        var minLeft = Mathf.Min(MinimumLeftPanelWidth, panelsWidth - 1f);
        var minPreview = Mathf.Min(MinimumPreviewPanelWidth, panelsWidth - minLeft);
        var leftPanelWidth = Mathf.Clamp(panelsWidth * leftPanelRatio, minLeft, Mathf.Max(minLeft, panelsWidth - minPreview));
        var previewPanelWidth = panelsWidth - leftPanelWidth;
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.BeginVertical(GUILayout.Width(leftPanelWidth));
        leftPanelScroll = EditorGUILayout.BeginScrollView(leftPanelScroll, false, false);
        DrawSettings();
        EditorGUILayout.Space(4);
        DrawInputToolbar();
        DrawEntries();
        DrawSpineInput();
        atlasPanel.Draw(settings.outputFolder);
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        var splitterRect = GUILayoutUtility.GetRect(SplitterWidth, 0f, GUILayout.Width(SplitterWidth), GUILayout.ExpandHeight(true));
        HandlePanelSplitter(splitterRect, panelsWidth, minLeft, minPreview);

        // Use the actual right panel bounds as the drop target. This remains valid when the
        // preview is empty, aspect-fitted, or resized with the splitter.
        var previewPanelDropRect = new Rect(
            splitterRect.xMax,
            splitterRect.y,
            previewPanelWidth,
            Mathf.Max(1f, position.height - splitterRect.y - 6f));
        HandleDragAndDrop(previewPanelDropRect);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(previewPanelWidth));
        DrawPreview(previewPanelWidth);
        if (!atlasPanel.IsPreviewActive) DrawActions();
        GUILayout.FlexibleSpace();
        DrawSignature();
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();
    }

    private void HandlePanelSplitter(Rect splitterRect, float panelsWidth, float minLeft, float minPreview)
    {
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
        if (Event.current.type == EventType.Repaint)
            EditorGUI.DrawRect(splitterRect, new Color(1f, 1f, 1f, 0.12f));

        var evt = Event.current;
        if (evt.type == EventType.MouseDown && evt.button == 0 && splitterRect.Contains(evt.mousePosition))
        {
            draggingPanelSplitter = true;
            evt.Use();
            return;
        }
        if (!draggingPanelSplitter) return;

        if (evt.type == EventType.MouseDrag && evt.button == 0)
        {
            var currentLeft = panelsWidth * leftPanelRatio;
            var maxLeft = Mathf.Max(minLeft, panelsWidth - minPreview);
            var nextLeft = Mathf.Clamp(currentLeft + evt.delta.x, minLeft, maxLeft);
            leftPanelRatio = nextLeft / Mathf.Max(1f, panelsWidth);
            Repaint();
            evt.Use();
        }
        else if (evt.type == EventType.MouseUp && evt.button == 0)
        {
            draggingPanelSplitter = false;
            EditorPrefs.SetFloat(PanelRatioPrefsKey, leftPanelRatio);
            evt.Use();
        }
    }

    private static void DrawSignature()
    {
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    private static void DrawMathModelGuideContent()
    {
        EditorGUILayout.HelpBox(
            "PNG + Camera + Grid + Cell Size  →  Ground Footprint  →  Root / Scale  →  Visual + BoxCollider",
            MessageType.None);

        DrawGuideSection(
            "1. Required Inputs",
            "Reference Camera：提供正交投影方向和面片旋转。\n" +
            "Grid W × D：建筑占用的地面格数。\n" +
            "Cell Size：一个格子的世界边长（m）。\n" +
            "Body Alpha：区分建筑实体与低 Alpha 阴影；所有非透明像素仍会保留在 Visual 中。\n" +
            "当前数学模型只支持 Orthographic Camera，不支持带 FOV 的透视相机。");

        DrawGuideSection(
            "2. Ground Footprint",
            "自动分析先从高 Alpha 实体底部寻找地面边；手调点会覆盖自动结果。四点随后统一拟合成参考相机投影下的 Grid W × D 地面矩形。\n" +
            "两组对边保持平行且等长，相邻边比例由 Grid 与 Cell Size 决定，夹角由相机的地面 X/Z 投影决定。\n" +
            "Pixels Per Meter = 图片中的地面投影长度 ÷ 对应世界长度。\n" +
            "Ground Fit 只评价自动识别的底边与拟合直线是否吻合；手动调整后显示 Manual。");

        DrawGuideSection(
            "3. Root / Pivot",
            "红点 = 两条地面对角线的交点，它代表建筑在 Y=0 地面上的真实原点。\n" +
            "生成资源中的 Root 恒为：Position (0,0,0)、Rotation (0,0,0)、Scale (1,1,1)。\n" +
            "图片原点与 Root 的差值不会写到 Root，而是通过正交投影矩阵的逆运算补偿到 Visual.localPosition。");

        DrawGuideSection(
            "4. Visual (Mesh Quad)",
            "Rotation = Reference Camera Rotation。\n" +
            "World Per Pixel = 1 ÷ Pixels Per Meter；Visual Scale = 有像素区域尺寸 × World Per Pixel × Margin Scale。\n" +
            "Visual Position = Root 反向补偿 + 图片地面锚点补偿 + 底部贴地补偿。\n" +
            "Mesh 只裁掉全透明空白；低 Alpha 阴影仍保留显示。");

        DrawGuideSection(
            "5. BoxCollider (on Root)",
            "Ground W = Grid W × Cell Size；Ground D = Grid D × Cell Size。\n" +
            "Margin Scale = 1 - 2 × Ground Margin ÷ min(Ground W, Ground D)。\n" +
            "Size X/Z = Ground W/D × Margin Scale + 2 × Horizontal Padding。\n" +
            "Size Y = 最终缩放比例下的自动拟合高度 + Height Padding；Center = (0, Size Y ÷ 2, 0)。\n" +
            "两个 Padding 都在缩放完成后以世界距离加入，因此不跟随 Ground Margin 缩放；碰撞底面始终位于 Y=0。");

        DrawGuideSection(
            "6. Point Controls",
            "红点：平移整个底面，四个角保持相对关系。\n" +
            "黄色 ↔ 绿色：互为对角点；拖动其中一个时，另一个固定。\n" +
            "青色 ↔ 青色：互为对角点；规则相同。\n" +
            "角点拖动只改变统一比例，不允许破坏相机角度、对边关系或 Grid 宽深比；红点会自动移动到新的对角线交点。");

    }

    private static void DrawGuideSection(string title, string body)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        GUILayout.Label(body, EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandWidth(true));
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2f);
    }

    private void DrawSettings()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        var previousCamera = referenceCamera;
        EditorGUI.BeginChangeCheck();
        referenceCamera = (Camera)EditorGUILayout.ObjectField(Content("Reference Camera", "拖入用于还原建筑面片角度的正交相机。工具会读取它的旋转与地面投影关系。"), referenceCamera, typeof(Camera), true);
        DrawFloatWithUnit(Content("Cell Size", "一个 SLG 格子在 Unity 世界中的边长。建筑的 BoxCollider 宽度和深度由格子数乘以该值计算。"), ref settings.cellWorldSize, "m");
        settings.bodyAlphaThreshold = EditorGUILayout.Slider(Content("Body Alpha", "Alpha 高于该值的像素视为建筑实体，会参与地面点、缩放和碰撞高度计算；较低 Alpha 只作为阴影显示。"), settings.bodyAlphaThreshold, 0.5f, 1f);
        DrawFloatWithUnit(Content("Height Padding", "在自动拟合出的点击碰撞高度上额外增加的余量。"), ref settings.clickHeightPadding, "m");
        DrawFloatWithUnit(Content("Horizontal Padding", "在格子定义的 X/Z 碰撞尺寸外侧增加的余量。"), ref settings.clickHorizontalPadding, "m");
        DrawFloatWithUnit(Content("Ground Margin", "底面短边每侧的世界间距。默认 0 m；正值会以底面中心等比缩小 Visual 与 BoxCollider，短边两侧各精确留出该距离；长边按同一比例变化以保持图片不变形。"), ref settings.visualMargin, "m");
        settings.clickHeightPadding = Mathf.Max(0f, settings.clickHeightPadding);
        settings.clickHorizontalPadding = Mathf.Max(0f, settings.clickHorizontalPadding);
        settings.visualMargin = Mathf.Max(0f, settings.visualMargin);
        settings.placePrefabsInScene = EditorGUILayout.Toggle(Content("Place In Scene", "开启后，预制体会放入当前场景的 Building Card Preview 节点并沿 X 轴排列；同名预览实例会被替换，不影响该节点外的正式建筑。"), settings.placePrefabsInScene);

        EditorGUILayout.BeginHorizontal();
        settings.outputFolder = EditorGUILayout.TextField(Content("Output Folder", "预制体、材质和 Mesh 的保存目录。必须位于项目的 Assets 文件夹内。"), settings.outputFolder);
        if (GUILayout.Button(Content("Browse", "选择位于当前 Unity 项目 Assets 下的输出目录。"), GUILayout.Width(66)))
            SelectOutputFolder();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.EndVertical();

        if (EditorGUI.EndChangeCheck())
        {
            SaveSettings();
            if (referenceCamera != previousCamera) SaveWorkspaceState();
            ScheduleAnalysis(null, true);
            if (spineDataAsset != null)
                ScheduleSpinePreviewRefresh();
            else if (spinePreviewActive)
                ClearSpinePreview();
        }
    }

    private static GUIContent Content(string label, string tooltip)
    {
        return new GUIContent(label, tooltip);
    }

    private static void DrawFloatWithUnit(GUIContent label, ref float value, string unit)
    {
        EditorGUILayout.BeginHorizontal();
        value = EditorGUILayout.FloatField(label, value);
        EditorGUILayout.LabelField(unit, EditorStyles.miniLabel, GUILayout.Width(24));
        EditorGUILayout.EndHorizontal();
    }

    private void SelectOutputFolder()
    {
        var initialPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", settings.outputFolder));
        var selectedPath = EditorUtility.OpenFolderPanel("Select Output Folder", initialPath, string.Empty);
        if (string.IsNullOrEmpty(selectedPath)) return;

        var assetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
        var normalizedSelectedPath = Path.GetFullPath(selectedPath).Replace('\\', '/').TrimEnd('/');
        if (normalizedSelectedPath != assetsPath && !normalizedSelectedPath.StartsWith(assetsPath + "/", StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog("Building Card", "输出目录必须位于当前 Unity 项目的 Assets 文件夹内。", "确定");
            return;
        }

        settings.outputFolder = "Assets" + normalizedSelectedPath.Substring(assetsPath.Length);
        SaveSettings();
    }

    private void DrawInputToolbar()
    {
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(Content("Add Selected", "把 Project 窗口当前选中的 PNG 加入列表，并立即按当前参数自动分析。"), GUILayout.Height(25)))
        {
            foreach (var obj in Selection.objects)
                AddObject(obj);
        }
        if (GUILayout.Button(Content("Refresh", "用当前相机和参数重新分析列表。已手动调整过的控制点会保留，并重新按约束拟合。"), GUILayout.Width(82), GUILayout.Height(25)))
            AnalyzeAll();
        using (new EditorGUI.DisabledScope(entries.Count == 0))
        {
            if (GUILayout.Button(Content("Clear", "清空当前工具列表，不会删除任何图片或已生成资源。"), GUILayout.Width(60), GUILayout.Height(25)))
            {
                entries.Clear();
                previewIndex = -1;
                SaveWorkspaceState();
            }
        }
        EditorGUILayout.EndHorizontal();

    }

    private void DrawEntries()
    {
        if (entries.Count == 0)
        {
            EditorGUILayout.HelpBox("拖入 PNG 后会立刻自动分析。请在列表中填写 Grid W / D（单位：格）；预览中的四个点随时可直接拖动。", MessageType.None);
            return;
        }

        EditorGUILayout.Space(3);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.MinHeight(145), GUILayout.MaxHeight(260));
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var removeRequested = false;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            var previousEnabled = entry.enabled;
            entry.enabled = EditorGUILayout.Toggle(Content("", "是否在点击 Generate 时生成此项。"), entry.enabled, GUILayout.Width(18));
            if (entry.enabled != previousEnabled) SaveWorkspaceState();
            var textureFieldRect = EditorGUILayout.GetControlRect(
                false,
                EditorGUIUtility.singleLineHeight,
                GUILayout.MinWidth(70),
                GUILayout.ExpandWidth(true));
            var replacementTexture = EditorGUI.ObjectField(
                textureFieldRect,
                Content("", "输入的建筑 PNG。可直接替换；替换后会清除旧控制点并自动重新分析。"),
                entry.texture,
                typeof(Texture2D),
                false) as Texture2D;
            if (replacementTexture != null && replacementTexture != entry.texture &&
                BuildingCardAtlasBuilder.IsGeneratedAtlasTexture(replacementTexture))
            {
                ShowNotification(Content("Atlas output is read-only", "生成的图集不能用于再次生成建筑预制体。"));
            }
            else if (replacementTexture != null && replacementTexture != entry.texture &&
                     !entries.Exists(other => other != entry && other.texture == replacementTexture))
            {
                entry.texture = replacementTexture;
                entry.useManualGroundPoints = false;
                entry.generationError = null;
                entry.result = Analyze(entry);
                previewIndex = i;
                atlasPanel.HidePreview();
                SaveWorkspaceState();
            }
            if (GUILayout.Button(Content("Preview", "查看分析结果；可直接拖动四个角点或红色中心点，松开鼠标后会立即保存并重新计算。"), GUILayout.Width(60)))
            {
                ClearSpinePreview();
                previewIndex = i;
                atlasPanel.HidePreview();
                SaveWorkspaceState();
            }
            if (GUILayout.Button(Content("×", "从当前工具列表移除此图片，不删除源文件或已生成资源。"), GUILayout.Width(22)))
                removeRequested = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(18);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(Content("Grid", "建筑占用的地面格子数量：宽 × 深。会直接决定 BoxCollider 的 X/Z 尺寸。"), GUILayout.Width(30));
            entry.gridWidth = Mathf.Max(1, EditorGUILayout.IntField(entry.gridWidth, GUILayout.MinWidth(34), GUILayout.MaxWidth(58)));
            EditorGUILayout.LabelField("×", GUILayout.Width(14));
            entry.gridDepth = Mathf.Max(1, EditorGUILayout.IntField(entry.gridDepth, GUILayout.MinWidth(34), GUILayout.MaxWidth(58)));
            EditorGUILayout.LabelField("cells", EditorStyles.miniLabel, GUILayout.Width(30));
            if (EditorGUI.EndChangeCheck())
            {
                entry.generationError = null;
                SaveWorkspaceState();
                ScheduleAnalysis(entry);
            }

            var resultText = !string.IsNullOrEmpty(entry.generationError)
                ? entry.generationError
                : entry.result == null ? "未分析" : entry.result.message;
            var oldColor = GUI.color;
            if (!string.IsNullOrEmpty(entry.generationError) || entry.result != null && !entry.result.success)
                GUI.color = new Color(1f, 0.6f, 0.6f);
            EditorGUILayout.LabelField(resultText, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
            GUI.color = oldColor;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            if (removeRequested)
            {
                entries.RemoveAt(i);
                if (previewIndex == i) previewIndex = -1;
                else if (previewIndex > i) previewIndex--;
                i--;
                SaveWorkspaceState();
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawSpineInput()
    {
        EditorGUILayout.Space(3f);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField("Spine Building Prefab", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        var replacement = (SkeletonDataAsset)EditorGUILayout.ObjectField(
            Content("Skeleton Data", "拖入 Spine 生成的 SkeletonDataAsset。工具会创建 SkeletonAnimation 建筑预制体。"),
            spineDataAsset, typeof(SkeletonDataAsset), false);
        if (replacement != spineDataAsset)
        {
            spineDataAsset = replacement;
            var replacementAnimationNames = GetSpineAnimationNames(spineDataAsset);
            spineAnimation = replacementAnimationNames.Count > 0 ? replacementAnimationNames[0] : string.Empty;
        }

        var animationNames = GetSpineAnimationNames(spineDataAsset);
        if (animationNames.Count > 0)
        {
            var currentIndex = Mathf.Max(0, animationNames.IndexOf(spineAnimation));
            var selectedIndex = EditorGUILayout.Popup(
                Content("Animation", "预制体启用时默认播放的 Spine 动画。"), currentIndex, animationNames.ToArray());
            spineAnimation = animationNames[Mathf.Clamp(selectedIndex, 0, animationNames.Count - 1)];
        }
        else
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(Content("Animation", "当前 SkeletonDataAsset 没有可用动画。"), "No animations");
        }

        spineLoop = EditorGUILayout.Toggle(Content("Loop", "默认动画是否循环播放。"), spineLoop);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(Content("Grid", "Spine 建筑占用的地面格子数量：宽 × 深。"), GUILayout.Width(EditorGUIUtility.labelWidth));
        spineGridWidth = Mathf.Max(1, EditorGUILayout.IntField(spineGridWidth, GUILayout.Width(48f)));
        EditorGUILayout.LabelField("×", GUILayout.Width(14f));
        spineGridDepth = Mathf.Max(1, EditorGUILayout.IntField(spineGridDepth, GUILayout.Width(48f)));
        EditorGUILayout.LabelField("cells", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox(
            "自动分析 Scale、地脚位置和 BoxCollider；也可在右侧直接拖动黄/绿/青控制点调整大小，拖动红点整体平移。",
            MessageType.None);

        if (EditorGUI.EndChangeCheck())
        {
            SaveWorkspaceState();
            if (spineDataAsset != null)
                ScheduleSpinePreviewRefresh();
            else
                ClearSpinePreview();
        }

        using (new EditorGUI.DisabledScope(spineDataAsset == null))
        {
            GUI.backgroundColor = new Color(0.65f, 0.85f, 1f);
            if (GUILayout.Button(Content("Generate Spine Prefab", "生成带 SkeletonAnimation 和 BoxCollider 的建筑预制体。"), GUILayout.Height(28f)))
                EditorApplication.delayCall += GenerateSpinePrefab;
            GUI.backgroundColor = Color.white;
        }
        EditorGUILayout.EndVertical();
    }

    private void RefreshSpinePreview()
    {
        ClearSpinePreview();
        if (spineDataAsset == null) return;

        try
        {
            spinePreviewSkeleton = EditorInstantiation.InstantiateSkeletonAnimation(
                spineDataAsset, "", useObjectFactory: false);
            if (spinePreviewSkeleton == null) throw new InvalidOperationException("SkeletonAnimation 初始化失败。 ");
            spinePreviewSkeleton.gameObject.hideFlags = HideFlags.HideAndDontSave;
            spinePreviewSkeleton.UpdateTiming = Spine.Unity.UpdateTiming.ManualUpdate;
            spinePreviewSkeleton.AnimationName = spineAnimation;
            spinePreviewSkeleton.loop = spineLoop;
            spinePreviewSkeleton.Initialize(true);
            if (!string.IsNullOrEmpty(spineAnimation) && spinePreviewSkeleton.valid)
                spinePreviewSkeleton.AnimationState.SetAnimation(0, spineAnimation, spineLoop);
            spinePreviewSkeleton.Update(0f);
            spinePreviewSkeleton.LateUpdate();

            var previewRenderer = spinePreviewSkeleton.GetComponent<Renderer>();
            if (previewRenderer == null || previewRenderer.bounds.size.x <= 0.0001f ||
                previewRenderer.bounds.size.y <= 0.0001f)
                throw new InvalidOperationException("Spine 当前动画没有可预览的可见内容。 ");
            var previewBounds = previewRenderer.bounds;
            spinePreviewViewCenter = new Vector2(previewBounds.center.x, previewBounds.center.y);
            var previewAspect = Mathf.Max(0.01f, previewBounds.size.x / previewBounds.size.y);
            spinePreviewViewHeight = previewBounds.size.y * 1.1f;
            spinePreviewViewWidth = spinePreviewViewHeight * previewAspect;

            spinePreviewUtility = new PreviewRenderUtility(true);
            var previewCamera = spinePreviewUtility.camera;
            previewCamera.orthographic = true;
            previewCamera.nearClipPlane = 0.01f;
            previewCamera.farClipPlane = 100f;
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            previewCamera.backgroundColor = Color.clear;
            previewCamera.transform.position = new Vector3(
                spinePreviewViewCenter.x, spinePreviewViewCenter.y, -10f);
            previewCamera.transform.rotation = Quaternion.identity;
            previewCamera.orthographicSize = spinePreviewViewHeight * 0.5f;
            if (referenceCamera != null && referenceCamera.orthographic)
            {
                try
                {
                    spinePreviewTexture = RenderSpineAnalysisTexture(spinePreviewSkeleton,
                        out _, out _, out _);
                    spinePreviewEntry = new Entry
                    {
                        texture = spinePreviewTexture,
                        gridWidth = spineGridWidth,
                        gridDepth = spineGridDepth,
                        useManualGroundPoints = spineUseManualGroundPoints,
                        manualNearPoint = spineManualNearPoint,
                        manualLeftPoint = spineManualLeftPoint,
                        manualRightPoint = spineManualRightPoint,
                        manualFarPoint = spineManualFarPoint
                    };
                    spinePreviewEntry.result = Analyze(spinePreviewEntry);
                }
                catch (Exception analysisException)
                {
                    spinePreviewEntry = new Entry
                    {
                        gridWidth = spineGridWidth,
                        gridDepth = spineGridDepth,
                        result = new AnalysisResult
                        {
                            success = false,
                            message = "分析标记暂不可用：" + analysisException.Message
                        }
                    };
                    Debug.LogWarning("[BuildingCardTool] Spine 预览分析跳过：" + analysisException.Message);
                }
            }
            else
            {
                spinePreviewEntry = new Entry
                {
                    gridWidth = spineGridWidth,
                    gridDepth = spineGridDepth,
                    result = new AnalysisResult
                    {
                        success = false,
                        message = "动画可以直接预览；拖入正交 Reference Camera 后显示地脚与碰撞分析。"
                    }
                };
            }
            SetLayerRecursively(spinePreviewSkeleton.gameObject, 30);
            previewCamera.cullingMask = 1 << 30;
            spinePreviewUtility.AddSingleGO(spinePreviewSkeleton.gameObject);
            spinePreviewActive = true;
            spinePreviewPlaying = false;
            spinePreviewLastFrameTime = EditorApplication.timeSinceStartup;
            previewIndex = -1;
            atlasPanel.HidePreview();
            Repaint();
        }
        catch (Exception exception)
        {
            ClearSpinePreview();
            Debug.LogError("[BuildingCardTool] Spine 预览失败：" + exception);
            EditorUtility.DisplayDialog("Building Card", "Spine 预览失败：\n" + exception.Message, "确定");
        }
    }

    private void ScheduleSpinePreviewRefresh()
    {
        if (spinePreviewRefreshPending) return;
        spinePreviewRefreshPending = true;
        EditorApplication.delayCall += ProcessScheduledSpinePreviewRefresh;
    }

    private void ProcessScheduledSpinePreviewRefresh()
    {
        EditorApplication.delayCall -= ProcessScheduledSpinePreviewRefresh;
        spinePreviewRefreshPending = false;
        if (this == null) return;
        RefreshSpinePreview();
    }

    private void ClearSpinePreview()
    {
        spinePreviewActive = false;
        spinePreviewPlaying = false;
        spinePreviewEntry = null;
        if (spinePreviewTexture != null)
        {
            DestroyImmediate(spinePreviewTexture);
            spinePreviewTexture = null;
        }
        if (spinePreviewSkeleton != null)
        {
            DestroyImmediate(spinePreviewSkeleton.gameObject);
            spinePreviewSkeleton = null;
        }
        if (spinePreviewUtility != null)
        {
            spinePreviewUtility.Cleanup();
            spinePreviewUtility = null;
        }
        spinePreviewRenderedFrame = null;
    }

    private void UpdateSpinePreviewAnimation()
    {
        if (!spinePreviewActive || !spinePreviewPlaying || spinePreviewSkeleton == null) return;
        var now = EditorApplication.timeSinceStartup;
        var delta = now - spinePreviewLastFrameTime;
        if (delta < 1.0 / 20.0) return;
        spinePreviewLastFrameTime = now;

        try
        {
            spinePreviewSkeleton.Update(Mathf.Min(0.1f, (float)delta));
            spinePreviewSkeleton.LateUpdate();
            Repaint();
        }
        catch (Exception exception)
        {
            spinePreviewPlaying = false;
            Debug.LogWarning("[BuildingCardTool] Spine 动画预览已暂停：" + exception.Message);
        }
    }

    private void DrawSpinePreview(float previewPanelWidth)
    {
        DrawPreviewHeader();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Spine Animation Preview", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        var playbackIcon = EditorGUIUtility.IconContent(spinePreviewPlaying ? "PauseButton" : "PlayButton");
        var playbackTooltip = spinePreviewPlaying ? "暂停动画预览。" : "播放动画预览。";
        if (playbackIcon.image == null)
            playbackIcon = new GUIContent(spinePreviewPlaying ? "||" : ">", playbackTooltip);
        else
            playbackIcon.tooltip = playbackTooltip;
        if (GUILayout.Button(playbackIcon, GUILayout.Width(34f), GUILayout.Height(22f)))
        {
            spinePreviewPlaying = !spinePreviewPlaying;
            spinePreviewLastFrameTime = EditorApplication.timeSinceStartup;
        }
        EditorGUILayout.EndHorizontal();
        if (spinePreviewUtility == null || spinePreviewSkeleton == null)
        {
            EditorGUILayout.HelpBox("Spine 预览尚未生成。", MessageType.Info);
            return;
        }

        var result = spinePreviewEntry?.result;
        var previewMaxHeight = Mathf.Max(120f, position.height - 145f);
        var rect = GUILayoutUtility.GetAspectRect(
            Mathf.Max(0.01f, spinePreviewViewWidth / spinePreviewViewHeight),
            GUILayout.ExpandWidth(true), GUILayout.MaxHeight(previewMaxHeight));
        if (Event.current.type == EventType.Repaint && spinePreviewUtility != null)
        {
            spinePreviewUtility.BeginPreview(rect, GUIStyle.none);
            spinePreviewUtility.camera.Render();
            spinePreviewRenderedFrame = spinePreviewUtility.EndPreview();
        }
        if (spinePreviewRenderedFrame != null)
            GUI.DrawTexture(rect, spinePreviewRenderedFrame, ScaleMode.StretchToFill, true);
        else if (spinePreviewTexture != null && useUnityPreview)
            DrawUnityStyleTexturePreview(rect, spinePreviewTexture);
        else if (spinePreviewTexture != null)
            EditorGUI.DrawPreviewTexture(rect, spinePreviewTexture, null, ScaleMode.ScaleToFit);

        if (spinePreviewTexture == null || spinePreviewEntry == null || result == null || !result.success)
        {
            if (result != null && !string.IsNullOrEmpty(result.message))
                EditorGUILayout.HelpBox(result.message, MessageType.Warning);
            return;
        }

        GetPreviewGroundPoints(spinePreviewEntry, result, out var nearPoint, out var leftPoint,
            out var rightPoint, out var farPoint);
        DrawMarker(rect, spinePreviewTexture, nearPoint, Color.yellow);
        DrawMarker(rect, spinePreviewTexture, leftPoint, Color.cyan);
        DrawMarker(rect, spinePreviewTexture, rightPoint, Color.cyan);
        DrawMarker(rect, spinePreviewTexture, farPoint, Color.green);
        DrawFootprintOverlay(rect, spinePreviewTexture, nearPoint, leftPoint, farPoint, rightPoint);
        var centerPoint = GetPreviewFootprintCenter(spinePreviewEntry, result, nearPoint, leftPoint,
            rightPoint, farPoint);
        DrawMarker(rect, spinePreviewTexture, centerPoint, Color.red);
        HandleSpinePreviewPointEditing(rect);
        GUILayout.Label(
            "Spine Analysis · Click Height: " + result.clickHeight.ToString("0.##") +
            " m · Collider Fit: " + result.clickFitScore.ToString("0.00") +
            " · Ground Fit: " + result.groundFitQuality.ToString("0.00"),
            EditorStyles.wordWrappedMiniLabel);
    }

    private void DrawActions()
    {
        var canGenerate = entries.Exists(e => e.enabled);
        using (new EditorGUI.DisabledScope(!canGenerate))
        {
            GUI.backgroundColor = new Color(0.65f, 1f, 0.65f);
            if (GUILayout.Button(Content("Generate", "为勾选项生成预制体、材质和 Mesh；如启用 Place In Scene，也会实例化到当前场景。"), GUILayout.Height(34)))
                EditorApplication.delayCall += GenerateAll;
            GUI.backgroundColor = Color.white;
        }
    }

    private void DrawPreview(float previewPanelWidth)
    {
        if (spinePreviewActive)
        {
            DrawSpinePreview(previewPanelWidth);
            return;
        }
        if (atlasPanel.TryGetPreview(out var atlasTexture, out var atlasPage, out var atlasPageCount))
        {
            DrawAtlasPreview(previewPanelWidth, atlasTexture, atlasPage, atlasPageCount);
            return;
        }
        DrawPreviewHeader();
        if (previewIndex < 0 || previewIndex >= entries.Count)
        {
            var emptyDropRect = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUI.Box(emptyDropRect, Content("Drop Building PNGs Here", "也可直接拖入此预览区域；图片会加入左侧列表并自动分析。"));
            HandleDragAndDrop(emptyDropRect);
            return;
        }
        var entry = entries[previewIndex];
        if (entry.texture == null)
        {
            var emptyDropRect = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUI.Box(emptyDropRect, Content("Drop Building PNGs Here", "也可直接拖入此预览区域；图片会加入左侧列表并自动分析。"));
            HandleDragAndDrop(emptyDropRect);
            return;
        }

        var result = entry.result;
        const string constraintText = "约束：红点整体平移；拖角点时对角点固定并等比缩放。对边平行且等长，夹角由参考相机固定。";
        var groundFitText = entry.useManualGroundPoints
            ? "Ground Fit: Manual"
            : "Ground Fit: " + (result == null ? "--" : result.groundFitQuality.ToString("0.00"));
        var detailsText = result != null && result.success
            ? "可随时直接拖动红/黄点和两个青点；松开后自动保存并重新计算。黄/绿：一组对角点　青：另一组对角点　" +
              "Click Height: " + result.clickHeight.ToString("0.##") + " m　Collider Fit: " + result.clickFitScore.ToString("0.00") +
              "　" + groundFitText
            : string.Empty;
        var previewTextStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { wordWrap = true };
        var textWidth = Mathf.Max(80f, previewPanelWidth - 16f);
        var reservedTextHeight = previewTextStyle.CalcHeight(new GUIContent(constraintText), textWidth);
        if (!string.IsNullOrEmpty(detailsText))
            reservedTextHeight += previewTextStyle.CalcHeight(new GUIContent(detailsText), textWidth) + 2f;
        var previewMaxHeight = Mathf.Max(120f, position.height - 110f - reservedTextHeight);
        var rect = GUILayoutUtility.GetAspectRect(
            (float)entry.texture.width / entry.texture.height,
            GUILayout.ExpandWidth(true),
            GUILayout.MaxHeight(previewMaxHeight));
        if (useUnityPreview)
            DrawUnityStyleTexturePreview(rect, entry.texture);
        else
            EditorGUI.DrawPreviewTexture(rect, entry.texture, null, ScaleMode.ScaleToFit);
        // Keep the whole right-hand preview body as a drop target, not only the visible image.
        // This matters when the panel is resized and leaves blank space below the aspect-fitted image.
        var previewDropRect = new Rect(
            rect.x,
            rect.y,
            rect.width,
            Mathf.Max(rect.height, position.height - rect.y - 10f));
        HandleDragAndDrop(previewDropRect);

        if (result == null || !result.success)
        {
            HandlePreviewTexturePing(rect, entry.texture);
            EditorGUILayout.HelpBox(result == null ? "请先分析此图片。" : result.message, MessageType.Warning);
            return;
        }

        GetPreviewGroundPoints(entry, result, out var nearPoint, out var leftPoint, out var rightPoint, out var farPoint);
        DrawMarker(rect, entry.texture, nearPoint, Color.yellow);
        DrawMarker(rect, entry.texture, leftPoint, Color.cyan);
        DrawMarker(rect, entry.texture, rightPoint, Color.cyan);
        DrawMarker(rect, entry.texture, farPoint, Color.green);
        DrawFootprintOverlay(rect, entry.texture, nearPoint, leftPoint, farPoint, rightPoint);
        var centerPoint = GetPreviewFootprintCenter(entry, result, nearPoint, leftPoint, rightPoint, farPoint);
        DrawMarker(rect, entry.texture, centerPoint, Color.red);
        HandlePreviewPointEditing(rect, entry);
        HandlePreviewTexturePing(rect, entry.texture);
        GUILayout.Label(constraintText, previewTextStyle, GUILayout.Width(textWidth));
        GUILayout.Label(
            new GUIContent(detailsText, "Collider Fit：碰撞投影与高 Alpha 建筑实体轮廓的重合度。Ground Fit：自动地面边的拟合质量；Manual 表示已采用手动控制点。"),
            previewTextStyle,
            GUILayout.Width(textWidth));
    }

    private static void HandlePreviewTexturePing(Rect imageRect, Texture2D texture)
    {
        if (texture == null) return;
        var evt = Event.current;
        if (evt.type != EventType.MouseDown || evt.button != 0 || !imageRect.Contains(evt.mousePosition)) return;
        Selection.activeObject = texture;
        EditorGUIUtility.PingObject(texture);
        evt.Use();
    }

    private void DrawAtlasPreview(float previewPanelWidth, Texture2D texture, int pageIndex, int pageCount)
    {
        EditorGUILayout.Space(5f);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Atlas Preview", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (pageCount > 1)
        {
            if (GUILayout.Button("◀", EditorStyles.miniButtonLeft, GUILayout.Width(24f)))
                atlasPanel.MovePreviewPage(-1);
            GUILayout.Label((pageIndex + 1) + " / " + pageCount, EditorStyles.miniLabel, GUILayout.Width(42f));
            if (GUILayout.Button("▶", EditorStyles.miniButtonRight, GUILayout.Width(24f)))
                atlasPanel.MovePreviewPage(1);
        }
        var nextUnityPreview = GUILayout.Toggle(
            useUnityPreview,
            Content("Alpha Preview", "切换图集透明棋盘预览。"),
            EditorStyles.miniButton,
            GUILayout.Width(105f));
        if (nextUnityPreview != useUnityPreview)
        {
            useUnityPreview = nextUnityPreview;
            EditorPrefs.SetBool(UnityPreviewPrefsKey, useUnityPreview);
        }
        EditorGUILayout.EndHorizontal();

        var previewMaxHeight = Mathf.Max(120f, position.height - 105f);
        var rect = GUILayoutUtility.GetAspectRect(
            (float)texture.width / Mathf.Max(1, texture.height),
            GUILayout.ExpandWidth(true),
            GUILayout.MaxHeight(previewMaxHeight));
        if (useUnityPreview)
            DrawUnityStyleTexturePreview(rect, texture);
        else
            EditorGUI.DrawPreviewTexture(rect, texture, null, ScaleMode.ScaleToFit);
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        var evt = Event.current;
        if (evt.type == EventType.MouseDown && evt.button == 0 && rect.Contains(evt.mousePosition))
        {
            Selection.activeObject = texture;
            EditorGUIUtility.PingObject(texture);
            evt.Use();
        }
        GUILayout.Label(
            new GUIContent("Read-only Atlas output · Click the image to locate it in Project.",
                "图集只用于预览和定位，不能作为建筑预制体的输入图片。"),
            EditorStyles.wordWrappedMiniLabel,
            GUILayout.Width(Mathf.Max(80f, previewPanelWidth - 16f)));
    }

    private void DrawPreviewHeader()
    {
        EditorGUILayout.Space(5);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Analysis Preview", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(
                Content("? Guide", "查看原点、Visual、BoxCollider 与控制点约束所使用的完整数学关系。"),
                EditorStyles.miniButton,
                GUILayout.Width(72f)))
        {
            MathGuideWindow.ShowWindow();
        }
        var nextUnityPreview = GUILayout.Toggle(
            useUnityPreview,
            Content("Alpha Preview", "切换透明纹理预览：棋盘背景和底部纹理信息。关闭后保持原来的图片显示模式。"),
            EditorStyles.miniButton,
            GUILayout.Width(105));
        if (nextUnityPreview != useUnityPreview)
        {
            useUnityPreview = nextUnityPreview;
            EditorPrefs.SetBool(UnityPreviewPrefsKey, useUnityPreview);
        }
        EditorGUILayout.EndHorizontal();
    }

    private static void DrawUnityStyleTexturePreview(Rect rect, Texture2D texture)
    {
        if (Event.current.type == EventType.Repaint)
        {
            const float tileSize = 16f;
            var dark = new Color(0.22f, 0.22f, 0.22f, 1f);
            var light = new Color(0.34f, 0.34f, 0.34f, 1f);
            var row = 0;
            for (var y = rect.y; y < rect.yMax; y += tileSize, row++)
            {
                var column = 0;
                for (var x = rect.x; x < rect.xMax; x += tileSize, column++)
                {
                    var tileRect = new Rect(
                        x,
                        y,
                        Mathf.Min(tileSize, rect.xMax - x),
                        Mathf.Min(tileSize, rect.yMax - y));
                    EditorGUI.DrawRect(tileRect, ((row + column) & 1) == 0 ? dark : light);
                }
            }
        }

        GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);

        var infoStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            clipping = TextClipping.Clip,
            normal = { textColor = Color.white }
        };
        var shadowStyle = new GUIStyle(infoStyle)
        {
            normal = { textColor = new Color(0f, 0f, 0f, 0.9f) }
        };
        var overlayRect = new Rect(rect.x, Mathf.Max(rect.y, rect.yMax - 36f), rect.width, Mathf.Min(36f, rect.height));
        var nameRect = new Rect(overlayRect.x + 3f, overlayRect.y, overlayRect.width - 6f, 18f);
        DrawPassivePreviewText(new Rect(nameRect.x + 1f, nameRect.y + 1f, nameRect.width, nameRect.height), texture.name, shadowStyle);
        DrawPassivePreviewText(nameRect, texture.name, infoStyle);

        var isPowerOfTwo = IsPowerOfTwo(texture.width) && IsPowerOfTwo(texture.height);
        var estimatedBytes = (long)texture.width * texture.height * 4L;
        if (texture.mipmapCount > 1) estimatedBytes = estimatedBytes * 4L / 3L;
        var memoryText = estimatedBytes >= 1024L * 1024L
            ? (estimatedBytes / (1024f * 1024f)).ToString("0.0") + " MB"
            : (estimatedBytes / 1024f).ToString("0") + " KB";
        var infoText = texture.width + "×" + texture.height +
                       " (" + (isPowerOfTwo ? "POT" : "NPOT") + ")  " +
                       texture.format + "  " + memoryText;
        var detailsRect = new Rect(overlayRect.x + 3f, overlayRect.yMax - 18f, overlayRect.width - 6f, 18f);
        DrawPassivePreviewText(new Rect(detailsRect.x + 1f, detailsRect.y + 1f, detailsRect.width, detailsRect.height), infoText, shadowStyle);
        DrawPassivePreviewText(detailsRect, infoText, infoStyle);
    }

    private static void DrawPassivePreviewText(Rect rect, string text, GUIStyle style)
    {
        if (Event.current.type != EventType.Repaint) return;
        style.Draw(rect, new GUIContent(text), false, false, false, false);
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static void DrawMarker(Rect imageRect, Texture2D texture, Vector2 pixel, Color color)
    {
        var x = imageRect.x + imageRect.width * pixel.x / Mathf.Max(1, texture.width - 1);
        var y = imageRect.yMax - imageRect.height * pixel.y / Mathf.Max(1, texture.height - 1);
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - 3, y - 3, 7, 7), EditorGUIUtility.whiteTexture);
        GUI.color = previous;
    }

    private static void DrawFootprintOverlay(Rect imageRect, Texture2D texture, Vector2 near, Vector2 left, Vector2 far, Vector2 right)
    {
        if (Event.current.type != EventType.Repaint) return;
        var pNear = PixelToGui(imageRect, texture, near);
        var pLeft = PixelToGui(imageRect, texture, left);
        var pFar = PixelToGui(imageRect, texture, far);
        var pRight = PixelToGui(imageRect, texture, right);
        Handles.BeginGUI();
        Handles.color = new Color(1f, 0.85f, 0.1f, 0.95f);
        Handles.DrawAAPolyLine(2.5f, new Vector3[] { pNear, pLeft, pFar, pRight, pNear });
        Handles.color = new Color(1f, 0.3f, 0.3f, 0.8f);
        Handles.DrawAAPolyLine(1.5f, new Vector3[] { pNear, pFar });
        Handles.DrawAAPolyLine(1.5f, new Vector3[] { pLeft, pRight });
        Handles.EndGUI();
    }

    private static void GetPreviewGroundPoints(Entry entry, AnalysisResult result, out Vector2 near, out Vector2 left, out Vector2 right, out Vector2 far)
    {
        if (entry.useManualGroundPoints)
        {
            near = entry.manualNearPoint;
            left = entry.manualLeftPoint;
            right = entry.manualRightPoint;
            far = entry.manualFarPoint;
            return;
        }
        near = result.nearCorner;
        left = result.leftCorner;
        right = result.rightCorner;
        far = result.farCorner;
    }

    private static Vector2 GetPreviewFootprintCenter(Entry entry, AnalysisResult result, Vector2 near, Vector2 left, Vector2 right, Vector2 far)
    {
        if (entry.useManualGroundPoints && TrySegmentIntersection(near, far, left, right, out var center))
            return center;
        return result.rootPixel;
    }

    private void HandlePreviewPointEditing(Rect imageRect, Entry entry)
    {
        var evt = Event.current;
        if (evt.type != EventType.MouseDown && draggingEntryIndex != previewIndex) return;
        GetPreviewGroundPoints(entry, entry.result, out var near, out var left, out var right, out var far);
        var center = GetPreviewFootprintCenter(entry, entry.result, near, left, right, far);

        if (evt.type == EventType.MouseDown && evt.button == 0 && imageRect.Contains(evt.mousePosition))
        {
            var mouse = evt.mousePosition;
            var nearest = PreviewPoint.None;
            var nearestDistance = 12f * 12f;
            TrySelectPoint(mouse, PixelToGui(imageRect, entry.texture, near), PreviewPoint.Near, ref nearest, ref nearestDistance);
            TrySelectPoint(mouse, PixelToGui(imageRect, entry.texture, left), PreviewPoint.Left, ref nearest, ref nearestDistance);
            TrySelectPoint(mouse, PixelToGui(imageRect, entry.texture, right), PreviewPoint.Right, ref nearest, ref nearestDistance);
            TrySelectPoint(mouse, PixelToGui(imageRect, entry.texture, far), PreviewPoint.Far, ref nearest, ref nearestDistance);
            TrySelectPoint(mouse, PixelToGui(imageRect, entry.texture, center), PreviewPoint.Center, ref nearest, ref nearestDistance);
            if (nearest == PreviewPoint.None) return;

            if (!entry.useManualGroundPoints)
            {
                entry.useManualGroundPoints = true;
                entry.manualNearPoint = near;
                entry.manualLeftPoint = left;
                entry.manualRightPoint = right;
                entry.manualFarPoint = far;
            }
            draggingEntryIndex = previewIndex;
            draggingPoint = nearest;
            previousDragPixel = GuiToPixel(imageRect, entry.texture, evt.mousePosition);
            evt.Use();
            return;
        }

        if (draggingEntryIndex != previewIndex) return;
        if (evt.type == EventType.MouseDrag && evt.button == 0)
        {
            var pixel = GuiToPixel(imageRect, entry.texture, evt.mousePosition);
            if (draggingPoint == PreviewPoint.Center)
            {
                var delta = pixel - previousDragPixel;
                entry.manualNearPoint += delta;
                entry.manualLeftPoint += delta;
                entry.manualRightPoint += delta;
                entry.manualFarPoint += delta;
            }
            else
            {
                ResizeManualFootprint(entry, draggingPoint, pixel);
            }
            previousDragPixel = pixel;
            Repaint();
            evt.Use();
        }
        else if (evt.type == EventType.MouseUp && evt.button == 0)
        {
            entry.generationError = null;
            entry.result = Analyze(entry);
            SaveWorkspaceState();
            draggingEntryIndex = -1;
            draggingPoint = PreviewPoint.None;
            Repaint();
            evt.Use();
        }
    }

    private void HandleSpinePreviewPointEditing(Rect imageRect)
    {
        if (spinePreviewEntry == null || spinePreviewEntry.texture == null ||
            spinePreviewEntry.result == null || !spinePreviewEntry.result.success) return;

        const int spineDragIndex = -2;
        var previousPreviewIndex = previewIndex;
        var wasDraggingSpine = draggingEntryIndex == spineDragIndex;
        previewIndex = spineDragIndex;
        HandlePreviewPointEditing(imageRect, spinePreviewEntry);
        previewIndex = previousPreviewIndex;

        if (!spinePreviewEntry.useManualGroundPoints) return;
        spineUseManualGroundPoints = true;
        spineManualNearPoint = spinePreviewEntry.manualNearPoint;
        spineManualLeftPoint = spinePreviewEntry.manualLeftPoint;
        spineManualRightPoint = spinePreviewEntry.manualRightPoint;
        spineManualFarPoint = spinePreviewEntry.manualFarPoint;
        if (wasDraggingSpine && draggingEntryIndex != spineDragIndex)
            SaveWorkspaceState();
    }

    private static void TrySelectPoint(Vector2 mouse, Vector2 point, PreviewPoint candidate, ref PreviewPoint nearest, ref float nearestDistance)
    {
        var distance = (mouse - point).sqrMagnitude;
        if (distance > nearestDistance) return;
        nearestDistance = distance;
        nearest = candidate;
    }

    private static Vector2 PixelToGui(Rect imageRect, Texture2D texture, Vector2 pixel)
    {
        return new Vector2(
            imageRect.x + imageRect.width * pixel.x / Mathf.Max(1, texture.width - 1),
            imageRect.yMax - imageRect.height * pixel.y / Mathf.Max(1, texture.height - 1));
    }

    private static Vector2 GuiToPixel(Rect imageRect, Texture2D texture, Vector2 guiPoint)
    {
        return new Vector2(
            Mathf.Clamp((guiPoint.x - imageRect.x) / imageRect.width * (texture.width - 1), 0, texture.width - 1),
            Mathf.Clamp((imageRect.yMax - guiPoint.y) / imageRect.height * (texture.height - 1), 0, texture.height - 1));
    }

    /// <summary>
    /// 正交相机下的地面矩形在图像中只有三个可编辑自由度：平移 + 统一像素/世界缩放比。
    /// 两个侧边方向和宽高比由相机朝向和网格尺寸决定。
    /// </summary>
    private void ResizeManualFootprint(Entry entry, PreviewPoint point, Vector2 pixel)
    {
        GetPreviewGroundPoints(entry, entry.result, out var near, out var left, out var right, out var far);
        if (!TryGetConstrainedFootprintBasis(entry.gridWidth, entry.gridDepth, near, left, right,
                out var leftSide, out var rightSide))
        {
            return;
        }

        Vector2 fixedPoint;
        Vector2 diagonalBasis;
        switch (point)
        {
            case PreviewPoint.Near:
                fixedPoint = far;
                diagonalBasis = -(leftSide + rightSide);
                break;
            case PreviewPoint.Left:
                fixedPoint = right;
                diagonalBasis = leftSide - rightSide;
                break;
            case PreviewPoint.Right:
                fixedPoint = left;
                diagonalBasis = rightSide - leftSide;
                break;
            case PreviewPoint.Far:
                fixedPoint = near;
                diagonalBasis = leftSide + rightSide;
                break;
            default:
                return;
        }
        if (diagonalBasis.sqrMagnitude < 0.0001f) return;

        var pixelsPerWorld = Vector2.Dot(pixel - fixedPoint, diagonalBasis) / diagonalBasis.sqrMagnitude;
        pixelsPerWorld = Mathf.Max(0.01f, pixelsPerWorld);

        // Rebuild the complete projected rectangle from the locked opposite corner.
        // The red point is derived from the diagonal intersection, so it follows the resize.
        switch (point)
        {
            case PreviewPoint.Near:
                entry.manualFarPoint = fixedPoint;
                entry.manualNearPoint = fixedPoint - (leftSide + rightSide) * pixelsPerWorld;
                entry.manualLeftPoint = fixedPoint - rightSide * pixelsPerWorld;
                entry.manualRightPoint = fixedPoint - leftSide * pixelsPerWorld;
                break;
            case PreviewPoint.Left:
                entry.manualRightPoint = fixedPoint;
                entry.manualNearPoint = fixedPoint - rightSide * pixelsPerWorld;
                entry.manualLeftPoint = fixedPoint + (leftSide - rightSide) * pixelsPerWorld;
                entry.manualFarPoint = fixedPoint + leftSide * pixelsPerWorld;
                break;
            case PreviewPoint.Right:
                entry.manualLeftPoint = fixedPoint;
                entry.manualNearPoint = fixedPoint - leftSide * pixelsPerWorld;
                entry.manualRightPoint = fixedPoint + (rightSide - leftSide) * pixelsPerWorld;
                entry.manualFarPoint = fixedPoint + rightSide * pixelsPerWorld;
                break;
            case PreviewPoint.Far:
                entry.manualNearPoint = fixedPoint;
                entry.manualLeftPoint = fixedPoint + leftSide * pixelsPerWorld;
                entry.manualRightPoint = fixedPoint + rightSide * pixelsPerWorld;
                entry.manualFarPoint = fixedPoint + (leftSide + rightSide) * pixelsPerWorld;
                break;
        }
    }

    private void HandleDragAndDrop(Rect rect)
    {
        var evt = Event.current;
        if ((evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) || !rect.Contains(evt.mousePosition)) return;
        var hasBuildingSource = false;
        var hasAtlasOutput = false;
        foreach (var obj in DragAndDrop.objectReferences)
        {
            if (obj is SkeletonDataAsset)
            {
                hasBuildingSource = true;
                continue;
            }
            var texture = GetTextureFromObject(obj);
            if (texture == null) continue;
            if (BuildingCardAtlasBuilder.IsGeneratedAtlasTexture(texture)) hasAtlasOutput = true;
            else hasBuildingSource = true;
        }
        DragAndDrop.visualMode = hasBuildingSource
            ? DragAndDropVisualMode.Copy
            : hasAtlasOutput ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.None;
        if (evt.type != EventType.DragPerform) return;

        DragAndDrop.AcceptDrag();
        foreach (var obj in DragAndDrop.objectReferences)
            AddObject(obj);
        evt.Use();
    }

    private void AddObject(UnityEngine.Object obj)
    {
        if (obj == null) return;
        if (obj is SkeletonDataAsset skeletonData)
        {
            spineDataAsset = skeletonData;
            spineUseManualGroundPoints = false;
            var names = GetSpineAnimationNames(spineDataAsset);
            spineAnimation = names.Count > 0 ? names[0] : string.Empty;
            SaveWorkspaceState();
            ScheduleSpinePreviewRefresh();
            Repaint();
            return;
        }
        AddTexture(GetTextureFromObject(obj));
    }

    private static List<string> GetSpineAnimationNames(SkeletonDataAsset skeletonData)
    {
        var names = new List<string>();
        if (skeletonData == null) return names;
        try
        {
            var data = skeletonData.GetSkeletonData(true);
            if (data?.Animations == null) return names;
            foreach (var animation in data.Animations)
                if (animation != null && !string.IsNullOrEmpty(animation.Name)) names.Add(animation.Name);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[BuildingCardTool] 无法读取 Spine 动画：" + exception.Message);
        }
        return names;
    }

    private static Texture2D GetTextureFromObject(UnityEngine.Object obj)
    {
        var texture = obj as Texture2D;
        if (texture == null && obj is Sprite sprite) texture = sprite.texture;
        if (texture != null) return texture;
        var assetPath = AssetDatabase.GetAssetPath(obj);
        return string.IsNullOrEmpty(assetPath) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    private void AddTexture(Texture2D texture)
    {
        if (texture == null || entries.Exists(e => e.texture == texture)) return;
        if (BuildingCardAtlasBuilder.IsGeneratedAtlasTexture(texture))
        {
            ShowNotification(Content("Atlas output is read-only", "生成的图集不能用于再次生成建筑预制体。"));
            return;
        }
        var entry = new Entry { texture = texture };
        ClearSpinePreview();
        entries.Add(entry);
        entry.result = Analyze(entry);
        previewIndex = entries.Count - 1;
        atlasPanel.HidePreview();
        SaveWorkspaceState();
        Repaint();
    }

    private void AnalyzeAll()
    {
        CancelScheduledAnalysis();
        if (!ValidateInputs(false)) return;
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Building Card", "分析 " + entries[i].texture.name, (float)i / entries.Count);
                entries[i].generationError = null;
                entries[i].result = Analyze(entries[i]);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        SaveWorkspaceState();
        Repaint();
    }

    private void ScheduleAnalysis(Entry entry = null, bool analyzeAll = false, float delaySeconds = 0.2f)
    {
        if (analyzeAll)
        {
            pendingAnalyzeAll = true;
            pendingEntryAnalyses.Clear();
        }
        else if (!pendingAnalyzeAll && entry != null)
        {
            pendingEntryAnalyses.Add(entry);
        }

        analysisDueTime = EditorApplication.timeSinceStartup + Mathf.Max(0.01f, delaySeconds);
        if (analysisUpdateRegistered) return;
        analysisUpdateRegistered = true;
        EditorApplication.update += ProcessScheduledAnalysis;
    }

    private void CancelScheduledAnalysis()
    {
        if (analysisUpdateRegistered)
            EditorApplication.update -= ProcessScheduledAnalysis;
        analysisUpdateRegistered = false;
        pendingAnalyzeAll = false;
        pendingEntryAnalyses.Clear();
    }

    private void ProcessScheduledAnalysis()
    {
        if (EditorApplication.timeSinceStartup < analysisDueTime) return;
        EditorApplication.update -= ProcessScheduledAnalysis;
        analysisUpdateRegistered = false;

        if (pendingAnalyzeAll)
        {
            pendingAnalyzeAll = false;
            pendingEntryAnalyses.Clear();
            AnalyzeAll();
            return;
        }

        var scheduledEntries = new List<Entry>(pendingEntryAnalyses);
        pendingEntryAnalyses.Clear();
        for (var i = 0; i < scheduledEntries.Count; i++)
        {
            var entry = scheduledEntries[i];
            if (!entries.Contains(entry) || entry.texture == null) continue;
            entry.generationError = null;
            entry.result = Analyze(entry);
        }
        SaveWorkspaceState();
        Repaint();
    }

    private bool TryGetOutputGeometry(Entry entry, out OutputGeometry geometry, out string error)
    {
        geometry = new OutputGeometry();
        error = null;
        if (entry == null || entry.gridWidth < 1 || entry.gridDepth < 1)
        {
            error = "Grid W / D 必须大于 0。";
            return false;
        }
        if (!IsFinite(settings.cellWorldSize) || settings.cellWorldSize <= 0f)
        {
            error = "Cell Size 必须是大于 0 的有效数值。";
            return false;
        }
        if (!IsFinite(settings.visualMargin) || !IsFinite(settings.clickHorizontalPadding) ||
            !IsFinite(settings.clickHeightPadding) || settings.visualMargin < 0f ||
            settings.clickHorizontalPadding < 0f || settings.clickHeightPadding < 0f)
        {
            error = "Ground Margin 与 Padding 必须是大于或等于 0 的有效世界距离。";
            return false;
        }

        geometry.groundWidth = entry.gridWidth * settings.cellWorldSize;
        geometry.groundDepth = entry.gridDepth * settings.cellWorldSize;
        var shortSide = Mathf.Min(geometry.groundWidth, geometry.groundDepth);
        if (!IsFinite(shortSide) || shortSide <= 0f)
        {
            error = "计算出的建筑底面尺寸无效。";
            return false;
        }
        if (settings.visualMargin * 2f >= shortSide)
        {
            error = "Ground Margin 必须小于底面短边的一半（当前上限 " +
                    (shortSide * 0.5f).ToString("0.###") + " m）。";
            return false;
        }

        geometry.scaleFactor = 1f - settings.visualMargin * 2f / shortSide;
        geometry.colliderWidth = geometry.groundWidth * geometry.scaleFactor + settings.clickHorizontalPadding * 2f;
        geometry.colliderDepth = geometry.groundDepth * geometry.scaleFactor + settings.clickHorizontalPadding * 2f;
        if (!IsFinite(geometry.scaleFactor) || !IsFinite(geometry.colliderWidth) ||
            !IsFinite(geometry.colliderDepth) || geometry.scaleFactor <= 0f ||
            geometry.colliderWidth <= 0f || geometry.colliderDepth <= 0f)
        {
            error = "计算出的 Visual 缩放或 BoxCollider X/Z 尺寸无效。";
            return false;
        }
        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private AnalysisResult Analyze(Entry entry)
    {
        var result = new AnalysisResult();
        if (!ValidateInputs(false))
        {
            result.message = "请先指定正交相机与有效单格长度。";
            return result;
        }
        if (entry.texture == null)
        {
            result.message = "图片为空。";
            return result;
        }
        if (!TryGetOutputGeometry(entry, out var outputGeometry, out result.message))
            return result;

        Texture2D analysisTexture = null;
        try
        {
            analysisTexture = CreateReadableCopy(entry.texture);
            if (analysisTexture == null)
            {
                result.message = "无法读取图片 Alpha。";
                return result;
            }

            var width = analysisTexture.width;
            var height = analysisTexture.height;
            var pixels = analysisTexture.GetPixels32();
            var bottom = new List<Vector2>();
            var minX = width;
            var maxX = -1;
            var minY = height;
            var maxBodyY = -1;
            var minBodyY = height;
            var minVisibleX = width;
            var minVisibleY = height;
            var maxVisibleX = -1;
            var maxVisibleY = -1;

            for (var x = 0; x < width; x++)
            {
                var found = false;
                for (var y = 0; y < height; y++)
                {
                    if (pixels[y * width + x].a / 255f < settings.bodyAlphaThreshold) continue;
                    bottom.Add(new Vector2(x, y));
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y);
                    found = true;
                    break;
                }
                if (!found) continue;
            }

            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > 0)
                {
                    var visibleX = i % width;
                    var visibleY = i / width;
                    minVisibleX = Mathf.Min(minVisibleX, visibleX);
                    minVisibleY = Mathf.Min(minVisibleY, visibleY);
                    maxVisibleX = Mathf.Max(maxVisibleX, visibleX);
                    maxVisibleY = Mathf.Max(maxVisibleY, visibleY);
                }
                if (pixels[i].a / 255f < settings.bodyAlphaThreshold) continue;
                var bodyY = i / width;
                minBodyY = Mathf.Min(minBodyY, bodyY);
                maxBodyY = Mathf.Max(maxBodyY, bodyY);
            }

            if (bottom.Count < 12 || maxX <= minX || maxBodyY < 0 || maxVisibleX < minVisibleX)
            {
                result.message = "实体 Alpha 点不足；请降低阈值或检查图片 Alpha。";
                return result;
            }

            var sourceScaleX = entry.texture.width / (float)width;
            var sourceScaleY = entry.texture.height / (float)height;
            Vector2 nearCorner;
            Vector2 leftCorner;
            Vector2 rightCorner;
            Vector2 farCorner;
            var fitResidual = 0f;
            if (entry.useManualGroundPoints)
            {
                nearCorner = new Vector2(entry.manualNearPoint.x / sourceScaleX, entry.manualNearPoint.y / sourceScaleY);
                leftCorner = new Vector2(entry.manualLeftPoint.x / sourceScaleX, entry.manualLeftPoint.y / sourceScaleY);
                rightCorner = new Vector2(entry.manualRightPoint.x / sourceScaleX, entry.manualRightPoint.y / sourceScaleY);
                farCorner = new Vector2(entry.manualFarPoint.x / sourceScaleX, entry.manualFarPoint.y / sourceScaleY);
            }
            else
            {
                var nearCandidates = new List<Vector2>();
                for (var i = 0; i < bottom.Count; i++)
                    if (bottom[i].y <= minY + 2) nearCandidates.Add(bottom[i]);
                nearCandidates.Sort((a, b) => a.x.CompareTo(b.x));
                var nearX = nearCandidates[nearCandidates.Count / 2].x;

                var left = new List<Vector2>();
                var right = new List<Vector2>();
                for (var i = 0; i < bottom.Count; i++)
                {
                    if (bottom[i].x <= nearX) left.Add(bottom[i]);
                    if (bottom[i].x >= nearX) right.Add(bottom[i]);
                }
                if (left.Count < 6 || right.Count < 6)
                {
                    result.message = "未找到两侧底边；请在预览中拖动三个地面标注点。";
                    return result;
                }

                var leftLine = FitLine(left);
                var rightLine = FitLine(right);
                var slopeDifference = Mathf.Abs(leftLine.slope - rightLine.slope);
                if (slopeDifference < 0.03f)
                {
                    result.message = "两条底边接近平行；请在预览中拖动三个地面标注点。";
                    return result;
                }

                nearCorner = new Vector2(
                    (rightLine.intercept - leftLine.intercept) / (leftLine.slope - rightLine.slope),
                    0f);
                nearCorner.y = leftLine.slope * nearCorner.x + leftLine.intercept;

                var leftX = left[0].x;
                var rightX = right[right.Count - 1].x;
                leftCorner = new Vector2(leftX, leftLine.slope * leftX + leftLine.intercept);
                rightCorner = new Vector2(rightX, rightLine.slope * rightX + rightLine.intercept);
                farCorner = leftCorner + rightCorner - nearCorner;
                fitResidual = leftLine.residual + rightLine.residual;
            }
            var projectedX = GroundAxisOnImage(Vector3.right);
            var projectedZ = GroundAxisOnImage(Vector3.forward);
            if (projectedX.sqrMagnitude < 0.001f || projectedZ.sqrMagnitude < 0.001f)
            {
                result.message = "相机角度无法投影地面坐标轴。";
                return result;
            }

            // Both automatic detection and hand annotation are fitted to the same projected
            // ground rectangle.  This removes alpha-edge noise and prevents an invalid free
            // quadrilateral from changing the world-space math.
            if (!TryConstrainFootprint(entry.gridWidth, entry.gridDepth,
                    ref nearCorner, ref leftCorner, ref rightCorner, ref farCorner, out var pixelsPerWorld))
            {
                result.message = "底面标注无法拟合为当前相机下的正交网格。";
                return result;
            }

            var imageCenter = new Vector2((width - 1) * 0.5f, (height - 1) * 0.5f);
            // The prefab pivot is the intersection of the two footprint diagonals (C).
            if (!TrySegmentIntersection(nearCorner, farCorner, leftCorner, rightCorner, out var footprintCenter))
            {
                result.message = "底面四边形的对角线无法相交；请调整四个标注点。";
                return result;
            }
            if (!TryUnprojectGroundDelta(footprintCenter - nearCorner, pixelsPerWorld, projectedX, projectedZ,
                out var parentToNearOffset))
            {
                result.message = "相机地面投影矩阵不可逆，无法反算面片偏移。";
                return result;
            }
            // A is the visible near ground corner. Visual is positioned by aligning this image point
            // to its ground-space offset from the parent centre C.
            var nearAnchorLocal = new Vector3((nearCorner.x - imageCenter.x) / pixelsPerWorld, (nearCorner.y - imageCenter.y) / pixelsPerWorld, 0f);
            var minimumVisualWorldY = float.MaxValue;
            var visualRotation = GetVisualRotation();
            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a / 255f < settings.bodyAlphaThreshold) continue;
                var x = i % width;
                var y = i / width;
                var local = new Vector3((x - imageCenter.x) / pixelsPerWorld, (y - imageCenter.y) / pixelsPerWorld, 0f);
                minimumVisualWorldY = Mathf.Min(minimumVisualWorldY, (visualRotation * (local - nearAnchorLocal)).y);
            }

            result.success = true;
            result.message = "可生成";
            result.rootPixel = new Vector2(footprintCenter.x * sourceScaleX, footprintCenter.y * sourceScaleY);
            result.nearCorner = new Vector2(nearCorner.x * sourceScaleX, nearCorner.y * sourceScaleY);
            result.leftCorner = new Vector2(leftCorner.x * sourceScaleX, leftCorner.y * sourceScaleY);
            result.rightCorner = new Vector2(rightCorner.x * sourceScaleX, rightCorner.y * sourceScaleY);
            result.farCorner = new Vector2(farCorner.x * sourceScaleX, farCorner.y * sourceScaleY);
            if (entry.useManualGroundPoints)
            {
                // Keep the preview markers on the constrained solution too, including entries
                // that were saved before this constraint was introduced.
                entry.manualNearPoint = result.nearCorner;
                entry.manualLeftPoint = result.leftCorner;
                entry.manualRightPoint = result.rightCorner;
                entry.manualFarPoint = result.farCorner;
            }
            result.analysisPixelsPerWorldUnit = pixelsPerWorld;
            result.worldPerSourcePixel = ((width / (float)entry.texture.width) + (height / (float)entry.texture.height)) * 0.5f / pixelsPerWorld;
            // The source image represents the unscaled footprint. After Ground Margin uniformly
            // scales the Visual, one final-world metre spans more source pixels by 1 / scaleFactor.
            // Fitting with the final Box dimensions keeps both paddings as exact world distances.
            var finalPixelsPerWorld = pixelsPerWorld / outputGeometry.scaleFactor;
            result.clickHeight = FitColliderHeight(
                pixels, width, height, footprintCenter, finalPixelsPerWorld,
                outputGeometry.colliderWidth, outputGeometry.colliderDepth,
                minBodyY, maxBodyY, out result.clickFitScore);
            result.clickHeight = Mathf.Max(0.1f, result.clickHeight + settings.clickHeightPadding);
            // The parent is the footprint centre on Y=0. The fitted Box bottom is always on the ground.
            result.clickCenterY = result.clickHeight * 0.5f;
            result.visualGroundLift = Mathf.Max(0f, -minimumVisualWorldY);
            // C - A in ground space, obtained by the inverse orthographic projection of the
            // image-space diagonal intersection. Visual applies its negative while Root stays identity.
            result.colliderCenterFromRoot = parentToNearOffset;
            result.visualMinPixel = new Vector2(minVisibleX * sourceScaleX, minVisibleY * sourceScaleY);
            result.visualMaxPixel = new Vector2(maxVisibleX * sourceScaleX, maxVisibleY * sourceScaleY);
            result.groundFitQuality = entry.useManualGroundPoints ? 1f : Mathf.Clamp01(1f - fitResidual / 12f);
            return result;
        }
        catch (Exception exception)
        {
            result.message = "分析异常：" + exception.Message;
            return result;
        }
        finally
        {
            if (analysisTexture != null) DestroyImmediate(analysisTexture);
        }
    }

    private Vector2 GroundAxisOnImage(Vector3 axis)
    {
        return new Vector2(Vector3.Dot(referenceCamera.transform.right, axis), Vector3.Dot(referenceCamera.transform.up, axis));
    }

    /// <summary>
    /// 构建世界矩形两个边在相机平面中的投影（未应用像素缩放），方向由相机定义。
    /// </summary>
    private bool TryGetConstrainedFootprintBasis(int gridWidth, int gridDepth,
        Vector2 near, Vector2 left, Vector2 right, out Vector2 leftSide, out Vector2 rightSide)
    {
        leftSide = Vector2.zero;
        rightSide = Vector2.zero;
        if (referenceCamera == null || gridWidth < 1 || gridDepth < 1) return false;

        var projectedX = GroundAxisOnImage(Vector3.right);
        var projectedZ = GroundAxisOnImage(Vector3.forward);
        if (projectedX.sqrMagnitude < 0.0001f || projectedZ.sqrMagnitude < 0.0001f) return false;

        var leftCandidate = left - near;
        var rightCandidate = right - near;
        if (leftCandidate.sqrMagnitude < 0.0001f || rightCandidate.sqrMagnitude < 0.0001f) return false;

        var leftIsX = Alignment(leftCandidate, projectedX) + Alignment(rightCandidate, projectedZ) >=
                      Alignment(leftCandidate, projectedZ) + Alignment(rightCandidate, projectedX);
        var leftAxis = leftIsX ? projectedX : projectedZ;
        var rightAxis = leftIsX ? projectedZ : projectedX;
        var leftLength = (leftIsX ? gridWidth : gridDepth) * settings.cellWorldSize;
        var rightLength = (leftIsX ? gridDepth : gridWidth) * settings.cellWorldSize;
        var leftSign = Vector2.Dot(leftCandidate, leftAxis) < 0f ? -1f : 1f;
        var rightSign = Vector2.Dot(rightCandidate, rightAxis) < 0f ? -1f : 1f;
        leftSide = leftAxis * (leftSign * leftLength);
        rightSide = rightAxis * (rightSign * rightLength);
        return leftSide.sqrMagnitude > 0.0001f && rightSide.sqrMagnitude > 0.0001f;
    }

    /// <summary>
    /// 四点标注点最小二乘拟合到已知网格矩形正射投影，保持两对等边和相机夹角。
    /// </summary>
    private bool TryConstrainFootprint(int gridWidth, int gridDepth,
        ref Vector2 near, ref Vector2 left, ref Vector2 right, ref Vector2 far, out float pixelsPerWorld)
    {
        pixelsPerWorld = 0f;
        if (!TryGetConstrainedFootprintBasis(gridWidth, gridDepth, near, left, right,
                out var leftSide, out var rightSide))
            return false;

        var observed = new[] { near, left, right, far };
        var basis = new[] { Vector2.zero, leftSide, rightSide, leftSide + rightSide };
        var observedMean = Vector2.zero;
        var basisMean = Vector2.zero;
        for (var i = 0; i < observed.Length; i++)
        {
            observedMean += observed[i];
            basisMean += basis[i];
        }
        observedMean *= 0.25f;
        basisMean *= 0.25f;

        var numerator = 0f;
        var denominator = 0f;
        for (var i = 0; i < observed.Length; i++)
        {
            var centredBasis = basis[i] - basisMean;
            numerator += Vector2.Dot(observed[i] - observedMean, centredBasis);
            denominator += centredBasis.sqrMagnitude;
        }
        if (denominator < 0.0001f) return false;

        pixelsPerWorld = numerator / denominator;
        if (pixelsPerWorld < 0.01f || float.IsNaN(pixelsPerWorld) || float.IsInfinity(pixelsPerWorld))
            return false;

        var constrainedNear = observedMean - basisMean * pixelsPerWorld;
        near = constrainedNear;
        left = constrainedNear + leftSide * pixelsPerWorld;
        right = constrainedNear + rightSide * pixelsPerWorld;
        far = constrainedNear + (leftSide + rightSide) * pixelsPerWorld;
        return true;
    }

    private static float Alignment(Vector2 a, Vector2 b)
    {
        return Mathf.Abs(Vector2.Dot(a.normalized, b.normalized));
    }

    /// <summary>
    /// 逆正交地面投影：pixelDelta = pixelsPerWorld * [projectedX projectedZ] * worldDeltaXZ。
    /// </summary>
    private static bool TryUnprojectGroundDelta(Vector2 pixelDelta, float pixelsPerWorld,
        Vector2 projectedX, Vector2 projectedZ, out Vector3 worldDelta)
    {
        var determinant = projectedX.x * projectedZ.y - projectedX.y * projectedZ.x;
        if (Mathf.Abs(determinant) < 0.0001f || pixelsPerWorld < 0.0001f)
        {
            worldDelta = Vector3.zero;
            return false;
        }
        var screenDelta = pixelDelta / pixelsPerWorld;
        var x = (screenDelta.x * projectedZ.y - projectedZ.x * screenDelta.y) / determinant;
        var z = (projectedX.x * screenDelta.y - screenDelta.x * projectedX.y) / determinant;
        worldDelta = new Vector3(x, 0f, z);
        return true;
    }

    private static bool TrySegmentIntersection(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, out Vector2 intersection)
    {
        var directionA = a2 - a1;
        var directionB = b2 - b1;
        var denominator = directionA.x * directionB.y - directionA.y * directionB.x;
        if (Mathf.Abs(denominator) < 0.0001f)
        {
            intersection = Vector2.zero;
            return false;
        }
        var offset = b1 - a1;
        var t = (offset.x * directionB.y - offset.y * directionB.x) / denominator;
        var u = (offset.x * directionA.y - offset.y * directionA.x) / denominator;
        if (t < 0f || t > 1f || u < 0f || u > 1f)
        {
            intersection = Vector2.zero;
            return false;
        }
        intersection = a1 + directionA * t;
        return true;
    }

    private Quaternion GetVisualRotation()
    {
        // The card keeps exactly the authored camera rotation.  The generated material is double-sided.
        return referenceCamera.transform.rotation;
    }

    /// <summary>
    /// 在正交相机下搜索碰撞盒高度，使盒子与建筑不透明区域的像素重叠最匹配，底部固定在 Y=0。
    /// </summary>
    private float FitColliderHeight(Color32[] pixels, int imageWidth, int imageHeight,
        Vector2 footprintCenterPixel, float pixelsPerWorld, float boxWidth, float boxDepth,
        int minBodyY, int maxBodyY, out float bestScore)
    {
        var verticalPixelsPerWorld = pixelsPerWorld * Mathf.Abs(Vector3.Dot(referenceCamera.transform.up, Vector3.up));
        if (verticalPixelsPerWorld < 0.0001f)
        {
            bestScore = 0f;
            return 0.1f;
        }

        var projectedGround = BuildProjectedBoxHull(footprintCenterPixel, pixelsPerWorld, boxWidth, boxDepth, 0f);
        var highestGroundPixel = float.MinValue;
        for (var i = 0; i < projectedGround.Count; i++)
            highestGroundPixel = Mathf.Max(highestGroundPixel, projectedGround[i].y);

        var initialHeight = Mathf.Max(0.1f, (maxBodyY - highestGroundPixel) / verticalPixelsPerWorld);
        var visibleHeight = Mathf.Max(0.1f, (maxBodyY - minBodyY) / verticalPixelsPerWorld);
        var maximumHeight = Mathf.Max(0.5f, initialHeight * 2.5f, visibleHeight * 2.5f);
        var sampleStep = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(imageWidth * imageHeight / 30000f)));
        var sampledBodyCount = 0;
        for (var y = 0; y < imageHeight; y += sampleStep)
            for (var x = 0; x < imageWidth; x += sampleStep)
                if (pixels[y * imageWidth + x].a / 255f >= settings.bodyAlphaThreshold)
                    sampledBodyCount++;

        var bestHeight = initialHeight;
        bestScore = -1f;
        const int candidateCount = 48;
        for (var candidate = 0; candidate < candidateCount; candidate++)
        {
            var t = candidate / (float)(candidateCount - 1);
            var height = Mathf.Lerp(0.05f, maximumHeight, t);
            var hull = BuildProjectedBoxHull(footprintCenterPixel, pixelsPerWorld, boxWidth, boxDepth, height);
            var bounds = PolygonBounds(hull);
            var xMin = Mathf.Clamp(Mathf.FloorToInt(bounds.xMin), 0, imageWidth - 1);
            var xMax = Mathf.Clamp(Mathf.CeilToInt(bounds.xMax), 0, imageWidth - 1);
            var yMin = Mathf.Clamp(Mathf.FloorToInt(bounds.yMin), 0, imageHeight - 1);
            var yMax = Mathf.Clamp(Mathf.CeilToInt(bounds.yMax), 0, imageHeight - 1);
            var projectedCount = 0;
            var overlapCount = 0;
            for (var y = yMin; y <= yMax; y += sampleStep)
            {
                for (var x = xMin; x <= xMax; x += sampleStep)
                {
                    if (!PointInConvexPolygon(new Vector2(x, y), hull)) continue;
                    projectedCount++;
                    if (pixels[y * imageWidth + x].a / 255f >= settings.bodyAlphaThreshold)
                        overlapCount++;
                }
            }

            var score = projectedCount == 0 || sampledBodyCount == 0
                ? 0f
                : 2f * overlapCount / (projectedCount + sampledBodyCount);
            if (score <= bestScore) continue;
            bestScore = score;
            bestHeight = height;
        }
        return bestHeight;
    }

    private List<Vector2> BuildProjectedBoxHull(Vector2 footprintCenterPixel, float pixelsPerWorld,
        float boxWidth, float boxDepth, float boxHeight)
    {
        var vertices = new List<Vector2>(8);
        var halfWidth = boxWidth * 0.5f;
        var halfDepth = boxDepth * 0.5f;
        var heights = new[] { 0f, boxHeight };
        foreach (var y in heights)
        {
            vertices.Add(ProjectWorldPoint(new Vector3(-halfWidth, y, -halfDepth), footprintCenterPixel, pixelsPerWorld));
            vertices.Add(ProjectWorldPoint(new Vector3(halfWidth, y, -halfDepth), footprintCenterPixel, pixelsPerWorld));
            vertices.Add(ProjectWorldPoint(new Vector3(halfWidth, y, halfDepth), footprintCenterPixel, pixelsPerWorld));
            vertices.Add(ProjectWorldPoint(new Vector3(-halfWidth, y, halfDepth), footprintCenterPixel, pixelsPerWorld));
        }
        return ConvexHull(vertices);
    }

    private Vector2 ProjectWorldPoint(Vector3 point, Vector2 footprintCenterPixel, float pixelsPerWorld)
    {
        return footprintCenterPixel + pixelsPerWorld * new Vector2(
            Vector3.Dot(referenceCamera.transform.right, point),
            Vector3.Dot(referenceCamera.transform.up, point));
    }

    private static List<Vector2> ConvexHull(List<Vector2> points)
    {
        points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var hull = new List<Vector2>();
        for (var i = 0; i < points.Count; i++)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], points[i] - hull[hull.Count - 1]) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }
        var lowerCount = hull.Count + 1;
        for (var i = points.Count - 2; i >= 0; i--)
        {
            while (hull.Count >= lowerCount && Cross(hull[hull.Count - 1] - hull[hull.Count - 2], points[i] - hull[hull.Count - 1]) <= 0f)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(points[i]);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.x * b.y - a.y * b.x;
    }

    private static Rect PolygonBounds(List<Vector2> polygon)
    {
        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        for (var i = 0; i < polygon.Count; i++)
        {
            minX = Mathf.Min(minX, polygon[i].x);
            minY = Mathf.Min(minY, polygon[i].y);
            maxX = Mathf.Max(maxX, polygon[i].x);
            maxY = Mathf.Max(maxY, polygon[i].y);
        }
        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }

    private static bool PointInConvexPolygon(Vector2 point, List<Vector2> polygon)
    {
        var hasPositive = false;
        var hasNegative = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var cross = Cross(b - a, point - a);
            hasPositive |= cross > 0.001f;
            hasNegative |= cross < -0.001f;
            if (hasPositive && hasNegative) return false;
        }
        return true;
    }

    private static Line FitLine(List<Vector2> points)
    {
        var count = points.Count;
        var sumX = 0f;
        var sumY = 0f;
        var sumXX = 0f;
        var sumXY = 0f;
        for (var i = 0; i < count; i++)
        {
            sumX += points[i].x;
            sumY += points[i].y;
            sumXX += points[i].x * points[i].x;
            sumXY += points[i].x * points[i].y;
        }
        var denominator = count * sumXX - sumX * sumX;
        var slope = Mathf.Abs(denominator) < 0.0001f ? 0f : (count * sumXY - sumX * sumY) / denominator;
        var intercept = (sumY - slope * sumX) / count;
        var residual = 0f;
        for (var i = 0; i < count; i++)
            residual += Mathf.Abs(points[i].y - (slope * points[i].x + intercept));
        return new Line { slope = slope, intercept = intercept, residual = residual / count };
    }

    private static Texture2D CreateReadableCopy(Texture source)
    {
        if (source == null) return null;
        var factor = Mathf.Min(1f, MaxAnalysisDimension / (float)Mathf.Max(source.width, source.height));
        var width = Mathf.Max(1, Mathf.RoundToInt(source.width * factor));
        var height = Mathf.Max(1, Mathf.RoundToInt(source.height * factor));
        var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply(false, false);
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private void GenerateSpinePrefab()
    {
        if (spineDataAsset == null) return;
        if (!ValidateInputs(true)) return;

        EnsureAssetFolder(settings.outputFolder);
        GameObject prefabAsset = null;
        try
        {
            prefabAsset = CreateSpinePrefab();
            if (settings.placePrefabsInScene)
            {
                var previewRoot = GetOrCreateScenePreviewRoot();
                var sceneObject = PlaceOrReplaceScenePreview(prefabAsset, previewRoot);
                ArrangeScenePreviews(previewRoot);
                if (sceneObject != null) Selection.activeGameObject = sceneObject;
            }
            else
            {
                Selection.activeObject = prefabAsset;
                EditorGUIUtility.PingObject(prefabAsset);
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("Building Card", "Spine 建筑预制体已生成：\n" + AssetDatabase.GetAssetPath(prefabAsset), "确定");
        }
        catch (Exception exception)
        {
            Debug.LogError("[BuildingCardTool] Spine 预制体生成失败：" + exception);
            EditorUtility.DisplayDialog("Building Card", "Spine 预制体生成失败：\n" + exception.Message, "确定");
        }
    }

    private GameObject CreateSpinePrefab()
    {
        var cleanName = SanitizeAssetName(spineDataAsset.name);
        const string skeletonDataSuffix = "_SkeletonData";
        if (cleanName.EndsWith(skeletonDataSuffix, StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName.Substring(0, cleanName.Length - skeletonDataSuffix.Length);
        var prefabPath = settings.outputFolder.TrimEnd('/') + "/B_" + cleanName + ".prefab";

        var root = new GameObject("B_" + cleanName);
        GameObject prefabAsset = null;
        try
        {
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var skeletonAnimation = EditorInstantiation.InstantiateSkeletonAnimation(
                spineDataAsset, "", useObjectFactory: false);
            if (skeletonAnimation == null)
                throw new InvalidOperationException("SkeletonAnimation 初始化失败，请检查 SkeletonDataAsset 与 Atlas。 ");

            var visual = skeletonAnimation.gameObject;
            visual.name = "Visual";
            visual.hideFlags = HideFlags.None;
            visual.transform.SetParent(root.transform, false);
            skeletonAnimation.skeletonDataAsset = spineDataAsset;
            skeletonAnimation.AnimationName = spineAnimation;
            skeletonAnimation.loop = spineLoop;
            skeletonAnimation.Initialize(true);
            if (!string.IsNullOrEmpty(spineAnimation) && skeletonAnimation.valid)
                skeletonAnimation.AnimationState.SetAnimation(0, spineAnimation, spineLoop);
            skeletonAnimation.LateUpdate();

            var renderer = visual.GetComponent<Renderer>();
            if (renderer == null)
                throw new InvalidOperationException("生成的 SkeletonAnimation 缺少 Renderer。 ");
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            var renderedTexture = RenderSpineAnalysisTexture(skeletonAnimation, out var viewCenter,
                out var viewWidth, out var viewHeight);
            try
            {
                var analysisEntry = new Entry
                {
                    texture = renderedTexture,
                    gridWidth = spineGridWidth,
                    gridDepth = spineGridDepth,
                    useManualGroundPoints = spineUseManualGroundPoints,
                    manualNearPoint = spineManualNearPoint,
                    manualLeftPoint = spineManualLeftPoint,
                    manualRightPoint = spineManualRightPoint,
                    manualFarPoint = spineManualFarPoint
                };
                var result = Analyze(analysisEntry);
                if (result == null || !result.success)
                    throw new InvalidOperationException("Spine 自动分析失败：" + (result?.message ?? "无分析结果"));
                if (!TryGetOutputGeometry(analysisEntry, out var outputGeometry, out var geometryError))
                    throw new InvalidOperationException(geometryError);

                var pixelsPerLocalUnit = renderedTexture.width / viewWidth;
                var uniformScale = result.worldPerSourcePixel * pixelsPerLocalUnit * outputGeometry.scaleFactor;
                var nearLocal = new Vector3(
                    viewCenter.x - viewWidth * 0.5f + (result.nearCorner.x + 0.5f) / renderedTexture.width * viewWidth,
                    viewCenter.y - viewHeight * 0.5f + (result.nearCorner.y + 0.5f) / renderedTexture.height * viewHeight,
                    0f);
                var rotation = GetVisualRotation();
                visual.transform.localRotation = rotation;
                visual.transform.localScale = Vector3.one * uniformScale;
                visual.transform.localPosition = -result.colliderCenterFromRoot * outputGeometry.scaleFactor -
                                                 rotation * (nearLocal * uniformScale) +
                                                 Vector3.up * (result.visualGroundLift * outputGeometry.scaleFactor);

                var collider = root.AddComponent<BoxCollider>();
                collider.center = new Vector3(0f, result.clickCenterY, 0f);
                collider.size = new Vector3(outputGeometry.colliderWidth, result.clickHeight,
                    outputGeometry.colliderDepth);

                CreateBuildingCardQueueMaterials(renderer, cleanName);

                // A regular card has a fixed quad, so Unity's transparent distance sorting uses a
                // stable Renderer bounds centre. Spine rebuilds its mesh (and bounds) every frame.
                // Sort the animated renderer from the building root on the ground instead, otherwise
                // its order against regular cards changes with the current animation pose.
                var sortingGroup = root.AddComponent<SortingGroup>();
                sortingGroup.sortingLayerID = renderer.sortingLayerID;
                sortingGroup.sortingOrder = renderer.sortingOrder;
                sortingGroup.sortAtRoot = true;

                prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefabAsset == null) throw new InvalidOperationException("Prefab 保存失败：" + prefabPath);
                Debug.Log("[BuildingCardTool] " + spineDataAsset.name + " → " + prefabPath +
                          " | Animation " + (string.IsNullOrEmpty(spineAnimation) ? "<none>" : spineAnimation) +
                          " | 自动比例 " + uniformScale.ToString("F4") +
                          " | 点击盒 " + collider.size.ToString("F2"));
            }
            finally
            {
                DestroyImmediate(renderedTexture);
            }
        }
        finally
        {
            DestroyImmediate(root);
        }
        return prefabAsset;
    }

    private static Texture2D RenderSpineAnalysisTexture(SkeletonAnimation skeletonAnimation,
        out Vector2 viewCenter, out float viewWidth, out float viewHeight, bool useFixedView = false,
        Vector2 fixedViewCenter = default, float fixedViewWidth = 0f, float fixedViewHeight = 0f)
    {
        const int analysisLayer = 31;
        const int maxSize = 1024;
        var renderer = skeletonAnimation.GetComponent<Renderer>();
        if (renderer == null || renderer.bounds.size.x <= 0.0001f || renderer.bounds.size.y <= 0.0001f)
            throw new InvalidOperationException("Spine 当前动画没有可分析的可见内容。 ");

        var bounds = renderer.bounds;
        viewCenter = useFixedView ? fixedViewCenter : new Vector2(bounds.center.x, bounds.center.y);
        var sourceAspect = Mathf.Max(0.01f,
            useFixedView ? fixedViewWidth / fixedViewHeight : bounds.size.x / bounds.size.y);
        var width = sourceAspect >= 1f ? maxSize : Mathf.Max(64, Mathf.RoundToInt(maxSize * sourceAspect));
        var height = sourceAspect >= 1f ? Mathf.Max(64, Mathf.RoundToInt(maxSize / sourceAspect)) : maxSize;
        var cameraAspect = width / (float)height;
        if (useFixedView)
        {
            viewWidth = fixedViewWidth;
            viewHeight = fixedViewHeight;
        }
        else
        {
            viewHeight = Mathf.Max(bounds.size.y * 1.02f, bounds.size.x * 1.02f / cameraAspect);
            viewWidth = viewHeight * cameraAspect;
        }

        var oldLayer = skeletonAnimation.gameObject.layer;
        var cameraObject = new GameObject("Building Card Spine Analysis Camera") { hideFlags = HideFlags.HideAndDontSave };
        if (skeletonAnimation.gameObject.scene.IsValid() && skeletonAnimation.gameObject.scene.isLoaded)
            SceneManager.MoveGameObjectToScene(cameraObject, skeletonAnimation.gameObject.scene);
        var camera = cameraObject.AddComponent<Camera>();
        var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear);
        var previousActive = RenderTexture.active;
        try
        {
            skeletonAnimation.gameObject.layer = analysisLayer;
            camera.orthographic = true;
            camera.orthographicSize = viewHeight * 0.5f;
            camera.aspect = cameraAspect;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << analysisLayer;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.transform.position = new Vector3(viewCenter.x, viewCenter.y, -10f);
            camera.transform.rotation = Quaternion.identity;
            camera.targetTexture = renderTexture;
            camera.Render();

            RenderTexture.active = renderTexture;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            {
                name = skeletonAnimation.skeletonDataAsset.name + "_Analysis"
            };
            texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            texture.Apply(false, false);
            return texture;
        }
        finally
        {
            skeletonAnimation.gameObject.layer = oldLayer;
            camera.targetTexture = null;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(renderTexture);
            DestroyImmediate(cameraObject);
        }
    }

    private static void SetLayerRecursively(GameObject gameObject, int layer)
    {
        gameObject.layer = layer;
        var transform = gameObject.transform;
        for (var i = 0; i < transform.childCount; i++)
            SetLayerRecursively(transform.GetChild(i).gameObject, layer);
    }

    private Material[] CreateBuildingCardQueueMaterials(Renderer renderer, string cleanName)
    {
        EnsureAssetFolder(settings.outputFolder + "/Materials");
        var sources = renderer.sharedMaterials;
        var replacements = new Material[sources.Length];
        for (var i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            if (source == null) continue;
            var suffix = sources.Length > 1 ? "_" + i : string.Empty;
            var path = settings.outputFolder + "/Materials/M_" + cleanName + "_Spine" + suffix + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(source);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                EditorUtility.CopySerialized(source, material);
            }
            // Spine keeps its own atlas/material pipeline. The Building Card shader is only for
            // static card quads and does not consume Spine vertex tint data.
            if (material.shader != null &&
                (material.shader.name == "Custom/Building Card URP" ||
                 material.shader.name == "ZXX/Building Card URP"))
            {
                var spineShader = Shader.Find("Spine/Skeleton");
                if (spineShader == null)
                    throw new InvalidOperationException("Spine/Skeleton Shader 未找到。请检查 spine-unity Runtime Shaders。");
                material.shader = spineShader;
            }
            material.name = Path.GetFileNameWithoutExtension(path);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
            replacements[i] = material;
        }
        renderer.sharedMaterials = replacements;
        return replacements;
    }

    private void GenerateAll()
    {
        CancelScheduledAnalysis();
        if (!ValidateInputs(true)) return;
        if (!TryPrepareGeneration(out var generationEntries)) return;
        EnsureOutputFolders();
        var generated = 0;
        var failed = 0;
        var placedObjects = new List<GameObject>();
        var failureMessages = new List<string>();
        var scenePreviewRoot = settings.placePrefabsInScene ? GetOrCreateScenePreviewRoot() : null;
        try
        {
            for (var i = 0; i < generationEntries.Count; i++)
            {
                var entry = generationEntries[i];
                EditorUtility.DisplayProgressBar("Building Card", "生成 " + entry.texture.name, (float)i / generationEntries.Count);
                try
                {
                    entry.generationError = null;
                    var prefabAsset = CreatePrefab(entry, entry.result);
                    if (scenePreviewRoot != null)
                    {
                        var sceneObject = PlaceOrReplaceScenePreview(prefabAsset, scenePreviewRoot);
                        if (sceneObject != null) placedObjects.Add(sceneObject);
                    }
                    generated++;
                }
                catch (Exception exception)
                {
                    failed++;
                    entry.generationError = "生成失败：" + exception.Message;
                    failureMessages.Add(entry.texture.name + "：" + exception.Message);
                    Debug.LogError("[BuildingCardTool] " + entry.texture.name + " 生成失败：" + exception);
                }
            }
            if (scenePreviewRoot != null) ArrangeScenePreviews(scenePreviewRoot);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        if (placedObjects.Count > 0)
        {
            Selection.objects = placedObjects.ToArray();
        }
        var dialogMessage = "已生成 " + generated + " 个预制体。失败 " + failed + " 个。";
        if (failureMessages.Count > 0)
        {
            var shownCount = Mathf.Min(5, failureMessages.Count);
            for (var i = 0; i < shownCount; i++) dialogMessage += "\n• " + failureMessages[i];
            if (failureMessages.Count > shownCount) dialogMessage += "\n其余失败项可在列表中查看。";
        }
        EditorUtility.DisplayDialog("Building Card", dialogMessage, "确定");
        Repaint();
    }

    private bool TryPrepareGeneration(out List<Entry> generationEntries)
    {
        generationEntries = new List<Entry>();
        var errors = new List<string>();
        var outputOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!entry.enabled) continue;
            entry.generationError = null;
            if (entry.texture == null)
            {
                entry.generationError = "图片为空。";
                errors.Add("第 " + (i + 1) + " 项：图片为空。");
                continue;
            }
            if (!TryGetOutputGeometry(entry, out _, out var geometryError))
            {
                entry.generationError = geometryError;
                errors.Add(entry.texture.name + "：" + geometryError);
                continue;
            }
            var outputKey = SanitizeAssetName(entry.texture.name);
            var sourcePath = AssetDatabase.GetAssetPath(entry.texture);
            if (outputOwners.TryGetValue(outputKey, out var existingSource))
            {
                entry.generationError = "输出名称与另一张图片冲突。";
                errors.Add("输出 B_" + outputKey + " 冲突：\n  " + existingSource + "\n  " + sourcePath);
                continue;
            }
            outputOwners.Add(outputKey, sourcePath);
            generationEntries.Add(entry);
        }

        if (generationEntries.Count == 0 && errors.Count == 0)
            errors.Add("没有可生成的图片。");
        if (errors.Count > 0)
        {
            ShowGenerationValidationErrors(errors);
            Repaint();
            return false;
        }

        try
        {
            for (var i = 0; i < generationEntries.Count; i++)
            {
                var entry = generationEntries[i];
                EditorUtility.DisplayProgressBar(
                    "Building Card",
                    "生成前检查 " + entry.texture.name,
                    (float)i / generationEntries.Count);
                entry.result = Analyze(entry);
                if (entry.result != null && entry.result.success) continue;

                entry.generationError = entry.result == null ? "分析结果为空。" : entry.result.message;
                errors.Add(entry.texture.name + "：" + entry.generationError);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        SaveWorkspaceState();
        if (errors.Count == 0) return true;
        ShowGenerationValidationErrors(errors);
        Repaint();
        return false;
    }

    private static void ShowGenerationValidationErrors(List<string> errors)
    {
        var message = "生成前检查未通过，尚未写入任何资源：";
        var shownCount = Mathf.Min(8, errors.Count);
        for (var i = 0; i < shownCount; i++) message += "\n• " + errors[i];
        if (errors.Count > shownCount) message += "\n• 其余 " + (errors.Count - shownCount) + " 项请在列表中查看。";
        EditorUtility.DisplayDialog("Building Card", message, "确定");
    }

    private static Transform GetOrCreateScenePreviewRoot()
    {
        var activeScene = SceneManager.GetActiveScene();
        if (!activeScene.IsValid() || !activeScene.isLoaded)
            throw new InvalidOperationException("当前没有可用的活动场景。");

        var roots = activeScene.GetRootGameObjects();
        for (var i = 0; i < roots.Length; i++)
            if (roots[i].name == ScenePreviewRootName) return roots[i].transform;

        var root = new GameObject(ScenePreviewRootName);
        if (root.scene != activeScene) SceneManager.MoveGameObjectToScene(root, activeScene);
        Undo.RegisterCreatedObjectUndo(root, "Create Building Card Preview Root");
        return root.transform;
    }

    private static GameObject PlaceOrReplaceScenePreview(GameObject prefabAsset, Transform previewRoot)
    {
        for (var i = previewRoot.childCount - 1; i >= 0; i--)
        {
            var child = previewRoot.GetChild(i);
            if (child.name == prefabAsset.name)
                Undo.DestroyObjectImmediate(child.gameObject);
        }

        var sceneObject = PrefabUtility.InstantiatePrefab(prefabAsset) as GameObject;
        if (sceneObject == null) return null;
        Undo.RegisterCreatedObjectUndo(sceneObject, "Generate Building Card");
        sceneObject.name = prefabAsset.name;
        sceneObject.transform.SetParent(previewRoot, false);
        return sceneObject;
    }

    private static void ArrangeScenePreviews(Transform previewRoot)
    {
        var currentX = 0f;
        var previousHalfWidth = 0f;
        for (var i = 0; i < previewRoot.childCount; i++)
        {
            var child = previewRoot.GetChild(i);
            var collider = child.GetComponent<BoxCollider>();
            var halfWidth = collider == null ? 0.5f : collider.size.x * 0.5f;
            if (i > 0) currentX += previousHalfWidth + halfWidth + 1f;
            Undo.RecordObject(child, "Arrange Building Card Previews");
            child.localPosition = new Vector3(currentX, 0f, 0f);
            child.localRotation = Quaternion.identity;
            child.localScale = Vector3.one;
            previousHalfWidth = halfWidth;
        }
    }

    private GameObject CreatePrefab(Entry entry, AnalysisResult result)
    {
        var cleanName = SanitizeAssetName(entry.texture.name);
        var prefabPath = GetPrefabPath(entry.texture);
        var materialPath = settings.outputFolder + "/Materials/M_" + cleanName + ".mat";
        var meshPath = settings.outputFolder + "/Meshes/Q_" + cleanName + ".asset";
        if (!TryGetOutputGeometry(entry, out var outputGeometry, out var geometryError))
            throw new InvalidOperationException(geometryError);

        var material = LoadOrCreateMaterial(materialPath, entry.texture);
        var mesh = LoadOrCreateQuadMesh(meshPath, new Vector4(
            result.visualMinPixel.x / Mathf.Max(1, entry.texture.width - 1),
            result.visualMinPixel.y / Mathf.Max(1, entry.texture.height - 1),
            result.visualMaxPixel.x / Mathf.Max(1, entry.texture.width - 1),
            result.visualMaxPixel.y / Mathf.Max(1, entry.texture.height - 1)));
        var root = new GameObject("B_" + cleanName);
        GameObject prefabAsset = null;
        try
        {
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            var collider = root.AddComponent<BoxCollider>();
            collider.center = new Vector3(0f, result.clickCenterY, 0f);
            collider.size = new Vector3(outputGeometry.colliderWidth, result.clickHeight, outputGeometry.colliderDepth);

            var visual = new GameObject("Visual");
            visual.transform.SetParent(root.transform, false);
            var rotation = GetVisualRotation();
            visual.transform.localRotation = rotation;
            var visualPixelSize = result.visualMaxPixel - result.visualMinPixel + Vector2.one;
            visual.transform.localScale = new Vector3(
                visualPixelSize.x * result.worldPerSourcePixel * outputGeometry.scaleFactor,
                visualPixelSize.y * result.worldPerSourcePixel * outputGeometry.scaleFactor,
                1f);

            var visualPixelCenter = (result.visualMinPixel + result.visualMaxPixel) * 0.5f;
            var nearAnchorInQuadUnits = new Vector3(
                (result.nearCorner.x - visualPixelCenter.x) * result.worldPerSourcePixel,
                (result.nearCorner.y - visualPixelCenter.y) * result.worldPerSourcePixel,
                0f);
            visual.transform.localPosition = -result.colliderCenterFromRoot * outputGeometry.scaleFactor -
                                             (rotation * (nearAnchorInQuadUnits * outputGeometry.scaleFactor)) +
                                             Vector3.up * (result.visualGroundLift * outputGeometry.scaleFactor);

            var filter = visual.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            // Transparent MeshRenderers are sorted from Renderer.bounds.center. Keep the soft
            // alpha edge, but make that comparison point the building's ground Root instead of
            // the visual image centre (which moves with transparent padding and building height).
            BuildingCardTransparentSortUtility.AlignBoundsCenterToRoot(root);

            prefabAsset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            if (prefabAsset == null)
                throw new InvalidOperationException("Prefab 保存失败：" + prefabPath);
            BuildingCardMetadataStore.RecordGeneratedPrefab(
                prefabPath, entry.texture, material, entry.gridWidth, entry.gridDepth);
            Debug.Log("[BuildingCardTool] " + entry.texture.name +
                " → " + prefabPath +
                " | 原点像素 " + result.rootPixel.ToString("F1") +
                " | 点击盒 " + collider.size.ToString("F2"));
        }
        finally
        {
            DestroyImmediate(root);
        }
        return prefabAsset;
    }

    private string GetPrefabPath(Texture2D texture)
    {
        return settings.outputFolder.TrimEnd('/') + "/B_" + SanitizeAssetName(texture.name) + ".prefab";
    }

    private static string SanitizeAssetName(string name)
    {
        foreach (var character in Path.GetInvalidFileNameChars())
            name = name.Replace(character, '_');
        return name;
    }

    private static Mesh LoadOrCreateQuadMesh(string path, Vector4 uvRect)
    {
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        var isNew = mesh == null;
        if (isNew)
            mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
        else
            Undo.RecordObject(mesh, "Update Building Card Mesh");

        // UV0 is the permanent standalone mapping. TEXCOORD2 (the third UV set / Mesh.uv3) is
        // reserved for atlas UVs and preserved when the standalone prefab is regenerated.
        var atlasUvs = new List<Vector2>();
        if (!isNew)
        {
            mesh.GetUVs(2, atlasUvs);
            if (atlasUvs.Count != 4) mesh.GetUVs(3, atlasUvs); // Migrate the former fourth UV set.
        }
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
            new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
        };
        mesh.SetUVs(0, new[]
        {
            new Vector2(uvRect.x, uvRect.y), new Vector2(uvRect.z, uvRect.y),
            new Vector2(uvRect.z, uvRect.w), new Vector2(uvRect.x, uvRect.w)
        });
        if (atlasUvs.Count == 4) mesh.SetUVs(2, atlasUvs);
        mesh.SetUVs(3, new List<Vector2>());
        // Visual.rotation equals the reference camera rotation, so the Quad front must face -Z.
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
        mesh.RecalculateBounds();
        if (isNew) AssetDatabase.CreateAsset(mesh, path);
        EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        if (mesh.vertexCount != 4 || mesh.triangles.Length != 6)
            throw new InvalidOperationException("生成 Quad Mesh 失败。");
        return mesh;
    }

    private static Material LoadOrCreateMaterial(string path, Texture2D texture)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                         Shader.Find("Unlit/Transparent") ??
                         Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("No transparent Unlit shader was found.");
            material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };
            AssetDatabase.CreateAsset(material, path);
        }
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        material.mainTexture = texture;
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", 5f);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", 10f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        EditorUtility.SetDirty(material);
        return material;
    }

    private bool ValidateInputs(bool showDialog)
    {
        var valid = referenceCamera != null && referenceCamera.orthographic &&
                    IsFinite(settings.cellWorldSize) && settings.cellWorldSize > 0f &&
                    IsFinite(settings.bodyAlphaThreshold) && settings.bodyAlphaThreshold > 0f && settings.bodyAlphaThreshold <= 1f &&
                    IsFinite(settings.visualMargin) && settings.visualMargin >= 0f &&
                    IsFinite(settings.clickHorizontalPadding) && settings.clickHorizontalPadding >= 0f &&
                    IsFinite(settings.clickHeightPadding) && settings.clickHeightPadding >= 0f &&
                    !string.IsNullOrEmpty(settings.outputFolder) &&
                    (settings.outputFolder == "Assets" || settings.outputFolder.StartsWith("Assets/", StringComparison.Ordinal));
        if (!valid && showDialog)
            EditorUtility.DisplayDialog(
                "Building Card",
                "请指定正交参考相机；尺寸参数必须有效；Output Folder 必须位于 Assets 下。",
                "确定");
        return valid;
    }

    private void EnsureOutputFolders()
    {
        EnsureAssetFolder(settings.outputFolder);
        EnsureAssetFolder(settings.outputFolder + "/Materials");
        EnsureAssetFolder(settings.outputFolder + "/Meshes");
    }

    private static void EnsureAssetFolder(string assetPath)
    {
        if (AssetDatabase.IsValidFolder(assetPath)) return;
        var fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
        Directory.CreateDirectory(fullPath);
        AssetDatabase.Refresh();
    }

    private void LoadWorkspaceState()
    {
        entries.Clear();
        var json = EditorPrefs.GetString(WorkspacePrefsKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var saved = JsonUtility.FromJson<WorkspaceState>(json);
            if (saved == null) return;

            if (!string.IsNullOrEmpty(saved.referenceCameraGlobalId) &&
                GlobalObjectId.TryParse(saved.referenceCameraGlobalId, out var cameraGlobalId))
                referenceCamera = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(cameraGlobalId) as Camera;
            if (referenceCamera == null && saved.referenceCameraInstanceId != 0)
                referenceCamera = EditorUtility.InstanceIDToObject(saved.referenceCameraInstanceId) as Camera;

            if (!string.IsNullOrEmpty(saved.spineDataGuid))
            {
                var spinePath = AssetDatabase.GUIDToAssetPath(saved.spineDataGuid);
                spineDataAsset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(spinePath);
            }
            spineAnimation = saved.spineAnimation ?? string.Empty;
            spineLoop = saved.spineLoop;
            spineGridWidth = Mathf.Max(1, saved.spineGridWidth);
            spineGridDepth = Mathf.Max(1, saved.spineGridDepth);
            spineUseManualGroundPoints = saved.spineUseManualGroundPoints;
            spineManualNearPoint = saved.spineManualNearPoint;
            spineManualLeftPoint = saved.spineManualLeftPoint;
            spineManualRightPoint = saved.spineManualRightPoint;
            spineManualFarPoint = saved.spineManualFarPoint;

            if (saved.entries != null)
            {
                for (var i = 0; i < saved.entries.Count; i++)
                {
                    var savedEntry = saved.entries[i];
                    var assetPath = AssetDatabase.GUIDToAssetPath(savedEntry.textureGuid);
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                    if (texture == null) continue;
                    var entry = new Entry
                    {
                        texture = texture,
                        gridWidth = Mathf.Max(1, savedEntry.gridWidth),
                        gridDepth = Mathf.Max(1, savedEntry.gridDepth),
                        enabled = savedEntry.enabled,
                        useManualGroundPoints = savedEntry.useManualGroundPoints,
                        manualNearPoint = savedEntry.manualNearPoint,
                        manualLeftPoint = savedEntry.manualLeftPoint,
                        manualRightPoint = savedEntry.manualRightPoint,
                        manualFarPoint = savedEntry.manualFarPoint
                    };
                    entries.Add(entry);
                }
            }
            previewIndex = entries.Count == 0 ? -1 : Mathf.Clamp(saved.previewIndex, 0, entries.Count - 1);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[BuildingCardTool] 无法恢复工作状态：" + exception.Message);
        }
    }

    private void SaveWorkspaceState()
    {
        var saved = new WorkspaceState
        {
            previewIndex = previewIndex,
            spineAnimation = spineAnimation,
            spineLoop = spineLoop,
            spineGridWidth = spineGridWidth,
            spineGridDepth = spineGridDepth,
            spineUseManualGroundPoints = spineUseManualGroundPoints,
            spineManualNearPoint = spineManualNearPoint,
            spineManualLeftPoint = spineManualLeftPoint,
            spineManualRightPoint = spineManualRightPoint,
            spineManualFarPoint = spineManualFarPoint
        };
        if (spineDataAsset != null)
            saved.spineDataGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(spineDataAsset));
        if (referenceCamera != null)
        {
            saved.referenceCameraInstanceId = referenceCamera.GetInstanceID();
            saved.referenceCameraGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(referenceCamera).ToString();
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.texture == null) continue;
            var assetPath = AssetDatabase.GetAssetPath(entry.texture);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid)) continue;
            saved.entries.Add(new SavedEntry
            {
                textureGuid = guid,
                gridWidth = entry.gridWidth,
                gridDepth = entry.gridDepth,
                enabled = entry.enabled,
                useManualGroundPoints = entry.useManualGroundPoints,
                manualNearPoint = entry.manualNearPoint,
                manualLeftPoint = entry.manualLeftPoint,
                manualRightPoint = entry.manualRightPoint,
                manualFarPoint = entry.manualFarPoint
            });
        }
        EditorPrefs.SetString(WorkspacePrefsKey, JsonUtility.ToJson(saved));
    }

    private void LoadSettings()
    {
        var json = EditorPrefs.GetString(PrefsKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var saved = JsonUtility.FromJson<Settings>(json);
            if (saved == null) return;
            settings.cellWorldSize = saved.cellWorldSize;
            settings.bodyAlphaThreshold = saved.bodyAlphaThreshold;
            settings.clickHeightPadding = saved.clickHeightPadding;
            settings.clickHorizontalPadding = saved.clickHorizontalPadding;
            settings.visualMargin = saved.visualMargin;
            settings.placePrefabsInScene = saved.placePrefabsInScene;
            settings.outputFolder = saved.outputFolder;
        }
        catch { }
    }

    private void SaveSettings()
    {
        EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(settings));
    }
}
