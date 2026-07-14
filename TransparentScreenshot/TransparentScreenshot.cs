// TransparentScreenshot.cs
// URP 透明背景截图工具（兼容后处理）
// 放到 Editor 文件夹下（如 Assets/Editor/TransparentScreenshot.cs）
//
// 用法：
//   Tools / Transparent Screenshot Window     打开窗口
//
// 透明背景 + 后处理的实现思路（双背景抠图法，兼容半透明物体）：
//   渲染三次同一相机，全部保留后处理。
//     PASS A: 透明背景  -> 拿带后处理的 RGB（最终用它做颜色）
//     PASS B: 黑色背景  -> 双背景算法的黑底
//     PASS C: 白色背景  -> 双背景算法的白底
//   对每个像素：a = 1 - mean(C - B)，可正确还原任意半透明物体的 alpha，
//   不依赖 shader 是否把 alpha 写入 dest alpha（URP 默认透明 shader 不写）。

using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2019_3_OR_NEWER
using UnityEngine.Rendering.Universal;
#endif

public class TransparentScreenshot : EditorWindow
{
    private const string PREF_PREFIX = "TransparentScreenshot.";

    // === endCameraRendering 回调：在 URP 渲染完成后直接抓 RGB ===
    private static Camera        s_capCam;
    private static RenderTexture s_capRT;
    private static Texture2D     s_capResult;

    static TransparentScreenshot()
    {
        RenderPipelineManager.endCameraRendering += (ctx, cam) =>
        {
            if (cam != s_capCam || s_capRT == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = s_capRT;
            s_capResult = new Texture2D(s_capRT.width, s_capRT.height,
                TextureFormat.RGBA32, false, false);
            s_capResult.ReadPixels(new Rect(0, 0, s_capRT.width, s_capRT.height), 0, 0, false);
            s_capResult.Apply();
            RenderTexture.active = prev;
        };
    }

    private enum NamingMode
    {
        Default,
        PrefabName
    }

    private Camera targetCamera;
    private int width = 1024;
    private int height = 1024;
    private bool transparentBackground = true;
    private string outputFolder = "Screenshots";
    private string fileNamePrefix = "shot";
    private int superSize = 2;
    private bool openFolderAfterSave = true;
    private bool useGameViewSize = false;

    // Naming mode
    private NamingMode namingMode = NamingMode.Default;
    private bool addPrefix = true;
    private bool addTimestamp = true;
    private bool addResolution = true;

    // === Capture Mode ===
    private enum CaptureMode { Single, AnimSequence, ParticleSequence }
    private CaptureMode captureMode = CaptureMode.Single;

    // === Sequence capture (shared params) ===
    private int sequenceFps = 30;

    [System.Serializable]
    private class AnimPairSaveEntry
    {
        public string clipGuid;
        public string targetPath;
    }

    [System.Serializable]
    private class AnimPairSaveData
    {
        public List<AnimPairSaveEntry> pairs = new List<AnimPairSaveEntry>();
    }

    private class AnimPairEntry
    {
        public AnimationClip clip;
        public GameObject target;
    }

    private List<AnimPairEntry> animPairs =
        new List<AnimPairEntry> { new AnimPairEntry() };

    [System.Serializable]
    private class GameObjectPathList
    {
        public List<string> paths =
            new List<string>();
    }

    private int sequenceFrameCount = 64;

    // ---- multi-drag helpers ----
    // 在 MultiDragObjectField 中检测到多对象拖入时暂存于此，调用方在区块末尾处理分发。
    private Object[] _pendingMultiDragObjs;
    private int _pendingMultiDragDropRow;
    private const int MD_ZONE_CLIP = 1;
    private const int MD_ZONE_TARGET = 2;
    private const int MD_ZONE_CASTER = 3;
    private const int MD_ZONE_RECEIVER = 4;
    private int _pendingMultiDragZone;

    // 绘制一个自带多选拖拽支持的对象字段。
    // 单对象拖入走 EditorGUI.ObjectField 默认行为；
    // 多对象拖入时会验证所有对象是否匹配 expectedType / allowSceneObjects，
    // 通过则把数组暂存到 _pendingMultiDrag* 字段并返回 true。
    private bool MultiDragObjectField<T>(Rect rect, ref T currentValue,
        System.Type expectedType, bool allowSceneObjects,
        int dropRowIndex, int zoneId) where T : Object
    {
        Event evt = Event.current;
        var refs = DragAndDrop.objectReferences;
        bool isMultiDrag = (refs != null && refs.Length > 1);

        // 仅拦截多对象拖拽，单对象留给 ObjectField 处理
        if (isMultiDrag && rect.Contains(evt.mousePosition)
            && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
        {
            bool allValid = true;
            for (int i = 0; i < refs.Length; i++)
            {
                var obj = refs[i];
                if (obj == null) continue;
                if (!expectedType.IsAssignableFrom(obj.GetType())) { allValid = false; break; }
                if (!allowSceneObjects && !EditorUtility.IsPersistent(obj)) { allValid = false; break; }
            }
            if (allValid)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    _pendingMultiDragObjs = refs;
                    _pendingMultiDragDropRow = dropRowIndex;
                    _pendingMultiDragZone = zoneId;
                    evt.Use();
                    return true;
                }
                evt.Use();
                return false;
            }
            else
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                evt.Use();
                return false;
            }
        }

        // 默认：使用 EditorGUI.ObjectField
        var newVal = EditorGUI.ObjectField(rect, currentValue, expectedType, allowSceneObjects) as T;
        if (newVal != currentValue) { currentValue = newVal; }
        return false;
    }

    private int sequenceColumns = 8;      // 列数，行数自动算
    private bool exportIndividualFrames = false;
    private bool sequenceUseEndpoints = false;

    // === Particle Sequence ===
    private GameObject particleTarget;
    private string particleScanInfo = "";
    private float particleDuration = 5f;

    // === Shadow Catcher ===
    private bool shadowCatcherFoldout;
    private bool enableShadowCatcher = false;
    private List<GameObject> shadowCasterRoots =
        new List<GameObject> { null };
    private List<GameObject> shadowReceivers =
        new List<GameObject> { null };
    private float shadowIntensity = 1.0f;

    // === Depth Occlusion (粒子序列帧) ===
    private bool occlusionFoldout;
    private List<GameObject> occlusionOccluders =
        new List<GameObject> { null };
    private const int MD_ZONE_OCCLUDER = 5;

    [Shortcut("Transparent Screenshot/Capture", null, KeyCode.X,
        ShortcutModifiers.Shift | ShortcutModifiers.Control)]
    private static void QuickCapture()
    {
        // 从 EditorPrefs 恢复相机引用
        var camPath = EditorPrefs.GetString(PREF_PREFIX + "cam", "");
        Camera cam = null;
        if (!string.IsNullOrEmpty(camPath))
        {
            var go = GameObject.Find(camPath);
            if (go != null) cam = go.GetComponent<Camera>();
        }
        if (cam == null) cam = Camera.main;
        if (cam == null)
        {
            EditorUtility.DisplayDialog("Screenshot",
                "找不到相机。请先打开截图窗口指定一个相机，或场景里有一个 MainCamera。", "OK");
            return;
        }

        // 读配置
        int outW = EditorPrefs.GetInt(PREF_PREFIX + "w", 1024);
        int outH = EditorPrefs.GetInt(PREF_PREFIX + "h", 1024);
        int ss = EditorPrefs.GetInt(PREF_PREFIX + "ss", 2);
        bool tbg = EditorPrefs.GetBool(PREF_PREFIX + "tbg", true);
        bool useGVS = EditorPrefs.GetBool(PREF_PREFIX + "gvs", false);
        bool openFolder = EditorPrefs.GetBool(PREF_PREFIX + "open", true);
        string folder = EditorPrefs.GetString(PREF_PREFIX + "folder", "Screenshots");
        string prefix = EditorPrefs.GetString(PREF_PREFIX + "prefix", "shot");
        int nm = EditorPrefs.GetInt(PREF_PREFIX + "nm", 0);
        bool ap = EditorPrefs.GetBool(PREF_PREFIX + "ap", true);
        bool ats = EditorPrefs.GetBool(PREF_PREFIX + "ats", true);
        bool ar = EditorPrefs.GetBool(PREF_PREFIX + "ar", true);

        if (useGVS)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            outW = Mathf.Max(1, Mathf.RoundToInt(gv.x));
            outH = Mathf.Max(1, Mathf.RoundToInt(gv.y));
        }

        // 命名 & 预制体检查
        string prefabName = null;
        if ((NamingMode)nm == NamingMode.PrefabName)
        {
            var sel = Selection.activeGameObject;
            if (sel == null)
            {
                EditorUtility.DisplayDialog("Screenshot",
                    "PrefabName 模式需要在 Hierarchy 中选中一个 GameObject。", "OK");
                return;
            }
            prefabName = sel.name;
        }

        // 渲染：和 Single Capture 完全相同的逻辑
        Texture2D finalTex = null;
        try
        {
finalTex = CapturePipeline.CaptureFrame(cam, outW, outH, ss, tbg);

            // 命名
            var folderAbs = string.IsNullOrEmpty(folder) || !Path.IsPathRooted(folder)
                ? Path.Combine(Directory.GetParent(Application.dataPath).FullName,
                    string.IsNullOrEmpty(folder) ? "Screenshots" : folder).Replace('\\', '/')
                : folder.Replace('\\', '/');
            Directory.CreateDirectory(folderAbs);
            var fname = BuildFileName(prefix, prefabName, outW, outH,
                (NamingMode)nm, ap, ats, ar);
            var path = Path.Combine(folderAbs, fname).Replace('\\', '/');
            File.WriteAllBytes(path, finalTex.EncodeToPNG());

            Debug.Log($"<color=#7fcf6f>[Screenshot]</color> Saved: {path}");

            var projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
            if (path.StartsWith(projectRoot)) AssetDatabase.Refresh();
            if (openFolder) EditorUtility.RevealInFinder(path);
        }
        finally
        {
            if (finalTex != null) Object.DestroyImmediate(finalTex);
        }
    }

    public static void OpenWindow()
    {
        var win = GetWindow<TransparentScreenshot>(false, "Screenshot", true);
        win.minSize = new Vector2(360, 420);
        win.LoadPrefs();
    }

    private Vector2 scrollPos;

    private void OnEnable() { LoadPrefs(); }
    private void OnDisable() { SavePrefs(); }

    private void OnGUI()
    {
        // ============================================================
        //  固定顶栏：模式切换 + Capture 按钮
        // ============================================================
        DrawTopBar();

        // ============================================================
        //  滚动区：Camera / Output / Rendering / Source
        // ============================================================
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        DrawCameraSection();
        DrawOutputSection();
        DrawRenderingSection();
        DrawSourceSection();

        EditorGUILayout.EndScrollView();

        if (GUI.changed) SavePrefs();

        // 署名
        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    // ================================================================
    //  UI Section Helpers
    // ================================================================

    private void DrawTopBar()
    {
        const float BTN_H = 28f;

        EditorGUILayout.BeginHorizontal();

        // Mode 按钮（非 toolbar，手动实现选中高亮）
        var modeNames = new[] { "Single", "Anim Seq", "Particle" };
        for (int i = 0; i < modeNames.Length; i++)
        {
            bool selected = (captureMode == (CaptureMode)i);
            GUI.backgroundColor = selected
                ? new Color(0.55f, 0.75f, 1f)
                : Color.white;
            if (GUILayout.Button(modeNames[i],
                GUILayout.Height(BTN_H), GUILayout.Width(90)))
                captureMode = (CaptureMode)i;
        }
        GUI.backgroundColor = Color.white;

        GUILayout.FlexibleSpace();

        string btnLabel;
        switch (captureMode)
        {
            case CaptureMode.AnimSequence:
            case CaptureMode.ParticleSequence:
                btnLabel = "Capture Sequence"; break;
            default:
                btnLabel = "Capture  (Ctrl+Shift+X)"; break;
        }

        GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
        if (GUILayout.Button(btnLabel,
            GUILayout.Height(BTN_H), GUILayout.Width(180)))
            OnCaptureButton();
        GUI.backgroundColor = Color.white;

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();
    }

    private void OnCaptureButton()
    {
        switch (captureMode)
        {
            case CaptureMode.Single:
                Capture();
                break;
            case CaptureMode.AnimSequence:
            {
                // 从 clip 时长 + FPS 计算帧数
                var valid = GetValidPairs();
                if (valid.Count > 0)
                    sequenceFrameCount = Mathf.Max(1,
                        Mathf.CeilToInt(valid[0].clip.length * sequenceFps));
                CaptureSequence();
                break;
            }
            case CaptureMode.ParticleSequence:
                CaptureParticleSequence();
                break;
        }
    }

    private void DrawCameraSection()
    {
        EditorGUILayout.LabelField("Camera", EditorStyles.boldLabel);
        targetCamera = (Camera)EditorGUILayout.ObjectField(
            new GUIContent("Target Camera", "留空则使用 MainCamera"),
            targetCamera, typeof(Camera), true);
        EditorGUILayout.Space();
    }

    private void DrawOutputSection()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

        // Resolution
        useGameViewSize = EditorGUILayout.Toggle(
            new GUIContent("Use Game View Size",
                "输出尺寸跟随 Game 视图当前分辨率，Width/Height 被忽略"),
            useGameViewSize);
        using (new EditorGUI.DisabledScope(useGameViewSize))
        {
            EditorGUILayout.BeginHorizontal();
            width  = Mathf.Max(1, EditorGUILayout.IntField("Width", width));
            GUILayout.Label("×", GUILayout.Width(14));
            height = Mathf.Max(1, EditorGUILayout.IntField("Height", height));
            EditorGUILayout.EndHorizontal();
        }
        if (useGameViewSize)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            EditorGUILayout.LabelField(" ",
                $"Game View: {(int)gv.x} × {(int)gv.y}", EditorStyles.miniLabel);
        }

        // Super Size
        superSize = Mathf.Clamp(EditorGUILayout.IntSlider(
            new GUIContent("Super Size", "渲染放大倍数再下采样，2~3 即可"),
            superSize, 1, 4), 1, 4);

        // Folder
        EditorGUILayout.BeginHorizontal();
        outputFolder = EditorGUILayout.TextField("Folder", outputFolder);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            var picked = EditorUtility.OpenFolderPanel(
                "Choose folder", GetAbsoluteOutputFolder(), "");
            if (!string.IsNullOrEmpty(picked))
            {
                var projectRoot = Directory.GetParent(Application.dataPath)
                                    .FullName.Replace('\\', '/');
                if (picked.StartsWith(projectRoot))
                    outputFolder = picked.Substring(projectRoot.Length).TrimStart('/');
                else
                    outputFolder = picked;
            }
        }
        EditorGUILayout.EndHorizontal();

        // Naming
        namingMode = (NamingMode)EditorGUILayout.EnumPopup("Naming", namingMode);
        if (namingMode == NamingMode.Default)
        {
            fileNamePrefix = EditorGUILayout.TextField("Prefix", fileNamePrefix);
        }
        else
        {
            addPrefix = EditorGUILayout.Toggle("Add Prefix", addPrefix);
            if (addPrefix)
                using (new EditorGUI.IndentLevelScope())
                    fileNamePrefix = EditorGUILayout.TextField("Prefix", fileNamePrefix);
            addTimestamp = EditorGUILayout.Toggle("Add Timestamp", addTimestamp);
            addResolution = EditorGUILayout.Toggle("Add Resolution", addResolution);

            var selName = GetSelectedGameObjectName();
            EditorGUILayout.LabelField(" ",
                string.IsNullOrEmpty(selName)
                    ? "Preview: 未在 Hierarchy 中选中物体"
                    : $"Preview: {BuildFileName(selName, width, height)}",
                EditorStyles.miniLabel);
        }

        openFolderAfterSave = EditorGUILayout.Toggle(
            "Open Folder After Save", openFolderAfterSave);
        EditorGUILayout.Space();
    }

    private void DrawRenderingSection()
    {
        EditorGUILayout.LabelField("Rendering", EditorStyles.boldLabel);

        // Transparent BG
        using (new EditorGUI.DisabledScope(enableShadowCatcher))
        {
            transparentBackground = EditorGUILayout.Toggle(
                new GUIContent("Transparent Background",
                    "输出 PNG 带透明通道"), transparentBackground);
        }

        // Shadow Catcher
        enableShadowCatcher = EditorGUILayout.Toggle(
            new GUIContent("Shadow Catcher",
                "物体不透明，影子半透明黑色，地面不可见"),
            enableShadowCatcher);
        if (enableShadowCatcher)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                // Casters
                EditorGUILayout.LabelField("Shadow Casters", EditorStyles.boldLabel);
                int removeCaster = -1;
                for (int i = 0; i < shadowCasterRoots.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var rect = EditorGUILayout.GetControlRect(
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        var go = shadowCasterRoots[i];
                        MultiDragObjectField(rect, ref go,
                            typeof(GameObject), true, i, MD_ZONE_CASTER);
                        shadowCasterRoots[i] = go;
                        if (shadowCasterRoots.Count > 1
                            && GUILayout.Button("X", GUILayout.Width(22)))
                            removeCaster = i;
                    }
                }
                if (removeCaster >= 0) shadowCasterRoots.RemoveAt(removeCaster);
                if (_pendingMultiDragObjs != null
                    && _pendingMultiDragZone == MD_ZONE_CASTER)
                {
                    int row = _pendingMultiDragDropRow;
                    for (int j = 0; j < _pendingMultiDragObjs.Length; j++)
                    {
                        int idx = row + j;
                        while (idx >= shadowCasterRoots.Count)
                            shadowCasterRoots.Add(null);
                        shadowCasterRoots[idx] = _pendingMultiDragObjs[j] as GameObject;
                    }
                    _pendingMultiDragObjs = null;
                    _pendingMultiDragDropRow = -1;
                }
                if (GUILayout.Button("+ Add Caster", GUILayout.Height(18)))
                    shadowCasterRoots.Add(null);

                // Receivers
                EditorGUILayout.LabelField("Shadow Receivers", EditorStyles.boldLabel);
                int removeReceiver = -1;
                for (int i = 0; i < shadowReceivers.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var rect = EditorGUILayout.GetControlRect(
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        var go = shadowReceivers[i];
                        MultiDragObjectField(rect, ref go,
                            typeof(GameObject), true, i, MD_ZONE_RECEIVER);
                        shadowReceivers[i] = go;
                        if (shadowReceivers.Count > 1
                            && GUILayout.Button("X", GUILayout.Width(22)))
                            removeReceiver = i;
                    }
                }
                if (removeReceiver >= 0) shadowReceivers.RemoveAt(removeReceiver);
                if (_pendingMultiDragObjs != null
                    && _pendingMultiDragZone == MD_ZONE_RECEIVER)
                {
                    int row = _pendingMultiDragDropRow;
                    for (int j = 0; j < _pendingMultiDragObjs.Length; j++)
                    {
                        int idx = row + j;
                        while (idx >= shadowReceivers.Count)
                            shadowReceivers.Add(null);
                        shadowReceivers[idx] = _pendingMultiDragObjs[j] as GameObject;
                    }
                    _pendingMultiDragObjs = null;
                    _pendingMultiDragDropRow = -1;
                }
                if (GUILayout.Button("+ Add Receiver", GUILayout.Height(18)))
                    shadowReceivers.Add(null);

                shadowIntensity = EditorGUILayout.Slider(
                    "Shadow Intensity", shadowIntensity, 0f, 2f);

                EditorGUILayout.HelpBox(
                    "Transparent BG / Keep PP 在 Shadow Catcher 模式下被忽略。",
                    MessageType.Info);
            }
        }

        // Depth Occlusion
        if (captureMode == CaptureMode.ParticleSequence)
        {
            occlusionFoldout = EditorGUILayout.Foldout(
                occlusionFoldout, "Depth Occlusion");
            if (occlusionFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    int removeOcc = -1;
                    for (int i = 0; i < occlusionOccluders.Count; i++)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var rect = EditorGUILayout.GetControlRect(
                                GUILayout.Height(EditorGUIUtility.singleLineHeight));
                            var go = occlusionOccluders[i];
                            MultiDragObjectField(rect, ref go,
                                typeof(GameObject), true, i, MD_ZONE_OCCLUDER);
                            occlusionOccluders[i] = go;
                            if (occlusionOccluders.Count > 1
                                && GUILayout.Button("X", GUILayout.Width(22)))
                                removeOcc = i;
                        }
                    }
                    if (removeOcc >= 0) occlusionOccluders.RemoveAt(removeOcc);
                    if (_pendingMultiDragObjs != null
                        && _pendingMultiDragZone == MD_ZONE_OCCLUDER)
                    {
                        int row = _pendingMultiDragDropRow;
                        for (int j = 0; j < _pendingMultiDragObjs.Length; j++)
                        {
                            int idx = row + j;
                            while (idx >= occlusionOccluders.Count)
                                occlusionOccluders.Add(null);
                            occlusionOccluders[idx] =
                                _pendingMultiDragObjs[j] as GameObject;
                        }
                        _pendingMultiDragObjs = null;
                        _pendingMultiDragDropRow = -1;
                    }
                    if (GUILayout.Button("+ Add Occluder", GUILayout.Height(18)))
                        occlusionOccluders.Add(null);

                    EditorGUILayout.HelpBox(
                        "拖入遮挡模型（如墙、柱子）。\n"
                        + "粒子在模型后面 → 透明；粒子在前面 → 正常显示。\n"
                        + "不填模型则没有效果。",
                        MessageType.None);
                }
            }
        }

        EditorGUILayout.Space();
    }

    private void DrawSourceSection()
    {
        switch (captureMode)
        {
            case CaptureMode.AnimSequence:
                DrawAnimSource();
                break;
            case CaptureMode.ParticleSequence:
                DrawParticleSource();
                break;
            case CaptureMode.Single:
            default:
                EditorGUILayout.LabelField("Source",
                    "截取相机当前画面（无需额外配置）",
                    EditorStyles.wordWrappedMiniLabel);
                break;
        }
    }

    // ---- Anim Source ----

    private void DrawAnimSource()
    {
        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

        // Pairs list
        EditorGUILayout.LabelField("Animation Pairs",
            EditorStyles.miniBoldLabel);
        int removeIndex = -1;
        for (int i = 0; i < animPairs.Count; i++)
        {
            var pair = animPairs[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                var clipRect = EditorGUILayout.GetControlRect(
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                var clip = pair.clip;
                MultiDragObjectField(clipRect, ref clip,
                    typeof(AnimationClip), false, i, MD_ZONE_CLIP);
                pair.clip = clip;

                var targetRect = EditorGUILayout.GetControlRect(
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                var target = pair.target;
                MultiDragObjectField(targetRect, ref target,
                    typeof(GameObject), true, i, MD_ZONE_TARGET);
                pair.target = target;

                if (animPairs.Count > 1
                    && GUILayout.Button("X", GUILayout.Width(22)))
                    removeIndex = i;
            }
        }
        if (removeIndex >= 0) animPairs.RemoveAt(removeIndex);
        if (_pendingMultiDragObjs != null
            && (_pendingMultiDragZone == MD_ZONE_CLIP
                || _pendingMultiDragZone == MD_ZONE_TARGET))
        {
            bool isClip = (_pendingMultiDragZone == MD_ZONE_CLIP);
            int row = _pendingMultiDragDropRow;
            for (int j = 0; j < _pendingMultiDragObjs.Length; j++)
            {
                int idx = row + j;
                while (idx >= animPairs.Count)
                    animPairs.Add(new AnimPairEntry());
                if (isClip)
                    animPairs[idx].clip = _pendingMultiDragObjs[j] as AnimationClip;
                else
                    animPairs[idx].target = _pendingMultiDragObjs[j] as GameObject;
            }
            _pendingMultiDragObjs = null;
            _pendingMultiDragDropRow = -1;
        }
        if (GUILayout.Button("+ Add Pair", GUILayout.Height(20)))
            animPairs.Add(new AnimPairEntry());

        // Timing: Duration (clip derived) + FPS → frame count
        var valid = GetValidPairs();
        float clipDuration = valid.Count > 0 ? valid[0].clip.length : 0f;
        int derivedFrames = Mathf.Max(1,
            Mathf.CeilToInt(clipDuration * sequenceFps));

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.TextField(
                new GUIContent("Duration", "来自 AnimationClip 时长"),
                clipDuration > 0f ? $"{clipDuration:F2}s" : "—");
        sequenceFps = Mathf.Max(1,
            EditorGUILayout.IntField("FPS", sequenceFps));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(" ",
            clipDuration > 0f
                ? $"→ {derivedFrames} frames ({clipDuration:F2}s × {sequenceFps}fps)"
                : "→ 请先指定有效的 Animation Pair",
            EditorStyles.miniLabel);

        DrawSequenceCommonControls(derivedFrames);

        // Diagnose
        EditorGUILayout.Space();
        if (GUILayout.Button("Diagnose Bindings", GUILayout.Height(22)))
            DiagnoseBindings();
    }

    // ---- Particle Source ----

    private void DrawParticleSource()
    {
        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

        // Target
        var newTarget = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Particle Target",
                "拖入粒子特效根节点，会扫描所有子 ParticleSystem"),
            particleTarget, typeof(GameObject), true);
        if (newTarget != particleTarget)
        {
            particleTarget = newTarget;
            particleScanInfo = BuildParticleScanDisplay(particleTarget);
        }

        // Scan info
        if (particleTarget != null)
        {
            if (!string.IsNullOrEmpty(particleScanInfo))
                EditorGUILayout.HelpBox(particleScanInfo, MessageType.None);
            else
                EditorGUILayout.HelpBox(
                    "⚠ 未找到 ParticleSystem", MessageType.Warning);
        }

        // Timing: Duration (user input) + FPS → frame count
        particleDuration = Mathf.Max(0.1f,
            EditorGUILayout.FloatField("Duration (s)", particleDuration));
        sequenceFps = Mathf.Max(1,
            EditorGUILayout.IntField("FPS", sequenceFps));

        int derivedFrames = Mathf.Max(1,
            Mathf.CeilToInt(particleDuration * sequenceFps));
        EditorGUILayout.LabelField(" ",
            $"→ {derivedFrames} frames "
            + $"({particleDuration:F2}s × {sequenceFps}fps)",
            EditorStyles.miniLabel);

        DrawSequenceCommonControls(derivedFrames);
    }

    // ---- Shared Sequence Controls ----

    private void DrawSequenceCommonControls(int frameCount)
    {
        EditorGUILayout.Space();

        sequenceColumns = Mathf.Max(1,
            EditorGUILayout.IntField("Columns", sequenceColumns));

        int rows = Mathf.CeilToInt(frameCount / (float)sequenceColumns);
        EditorGUILayout.LabelField(" ",
            $"Layout: {sequenceColumns} × {rows} "
            + $"({sequenceColumns * rows} cells, {frameCount} used)",
            EditorStyles.miniLabel);

        sequenceUseEndpoints = EditorGUILayout.Toggle(
            new GUIContent("Include Endpoints",
                "关闭 = 中心采样（适合循环），开启 = 含首尾帧"),
            sequenceUseEndpoints);

        exportIndividualFrames = EditorGUILayout.Toggle(
            "Export Individual Frames", exportIndividualFrames);

        EditorGUILayout.Space();
    }

    private List<AnimPairEntry> GetValidPairs()
    {
        var list = new List<AnimPairEntry>();
        for (int i = 0; i < animPairs.Count; i++)
            if (animPairs[i].clip != null && animPairs[i].target != null)
                list.Add(animPairs[i]);
        return list;
    }

    private void Capture()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null)
        {
            EditorUtility.DisplayDialog("Screenshot",
                "找不到相机：请指定 Target Camera 或场景里有一个 MainCamera。", "OK");
            return;
        }

        int outW = width, outH = height;
        if (useGameViewSize)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            outW = Mathf.Max(1, Mathf.RoundToInt(gv.x));
            outH = Mathf.Max(1, Mathf.RoundToInt(gv.y));
        }

        // PrefabName 模式：获取 Hierarchy 选中物体名称
        string prefabName = null;
        if (namingMode == NamingMode.PrefabName)
        {
            prefabName = GetSelectedGameObjectName();
            if (string.IsNullOrEmpty(prefabName))
            {
                EditorUtility.DisplayDialog("Screenshot",
                    "PrefabName 模式需要在 Hierarchy 中选中一个 GameObject。",
                    "OK");
                return;
            }
        }

        Texture2D finalTex = null;
        try
        {
            finalTex = RenderCurrentFrame(cam, outW, outH);

            var bytes = finalTex.EncodeToPNG();
            var folderAbs = GetAbsoluteOutputFolder();
            Directory.CreateDirectory(folderAbs);

            var fname = BuildFileName(prefabName, outW, outH);
            var path = Path.Combine(folderAbs, fname).Replace('\\', '/');
            File.WriteAllBytes(path, bytes);

            Debug.Log($"<color=#7fcf6f>[Screenshot]</color> Saved: {path}");

            var projectRoot = Directory.GetParent(Application.dataPath)
                                .FullName.Replace('\\', '/');
            if (path.StartsWith(projectRoot)) AssetDatabase.Refresh();
            if (openFolderAfterSave) EditorUtility.RevealInFinder(path);
        }
        finally
        {
            if (finalTex != null) Object.DestroyImmediate(finalTex);
        }
    }

    private Texture2D RenderCurrentFrame(Camera cam, int outW, int outH)
    {
return CapturePipeline.CaptureFrame(cam, outW, outH, superSize,
            transparentBackground,
            enableShadowCatcher, shadowCasterRoots, shadowReceivers,
            shadowIntensity);
    }

    // 诊断所有 pair 的 clip 曲线路径在对应 target 下能否找到 Transform。
    private void DiagnoseBindings()
    {
        if (animPairs.Count == 0)
        {
            EditorUtility.DisplayDialog("Diagnose", "没有 Animation Pair。", "OK");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[Diagnose] Checking {animPairs.Count} pair(s)...");
        sb.AppendLine();

        int totalMissing = 0;
        for (int pi = 0; pi < animPairs.Count; pi++)
        {
            var pair = animPairs[pi];
            var clip = pair.clip;
            var target = pair.target;

            sb.AppendLine($"--- Pair {pi + 1} ---");
            if (clip == null)
            {
                sb.AppendLine("  Clip: (null) - SKIP");
                continue;
            }
            sb.AppendLine($"  Clip: {clip.name}");

            if (target == null)
            {
                sb.AppendLine("  Target: (null) - SKIP");
                totalMissing++;
                continue;
            }
            sb.AppendLine($"  Target: {GetGameObjectPath(target)}");

            var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
            var pathSet = new HashSet<string>();
            foreach (var b in bindings) pathSet.Add(b.path);

            int matched = 0, missing = 0;
            var missingList = new List<string>();
            foreach (var p in pathSet)
            {
                if (string.IsNullOrEmpty(p)) { matched++; continue; }
                var tr = target.transform.Find(p);
                if (tr != null) matched++;
                else { missing++; missingList.Add(p); }
            }

            var targetPaths = new List<string>();
            CollectChildPaths(target.transform, "", targetPaths);

            sb.AppendLine($"  Unique paths in clip: {pathSet.Count}, Matched: {matched}, Missing: {missing}");
            if (missing > 0)
            {
                int show = Mathf.Min(10, missingList.Count);
                for (int i = 0; i < show; i++)
                    sb.AppendLine("    MISS " + missingList[i]);
                if (missingList.Count > show)
                    sb.AppendLine($"    ... and {missingList.Count - show} more.");
            }
            sb.AppendLine();
            totalMissing += missing;
        }

        if (totalMissing == 0)
            sb.AppendLine("[Diagnose] All bindings matched across all pairs.");
        else
            sb.AppendLine($"[Diagnose] Total missing across all pairs: {totalMissing}. Check Console for details.");

        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Diagnose",
            totalMissing == 0
                ? "所有 Pair 的骨骼绑定完全匹配！"
                : $"{totalMissing} 条路径未匹配，详情见 Console。",
            "OK");
    }

    // 强制场景里所有 SkinnedMeshRenderer 重新按当前骨骼姿态蒙皮。
    // 编辑器非 Play 模式下 SMR 不会自动跟随骨骼 Transform 变化更新 mesh，
    // 必须显式触发。这里组合用多种方式叠加保险：
    //   1) forceMatrixRecalculationPerRender = true 让下一次渲染前强制重算骨骼矩阵
    //   2) BakeMesh 到一个临时 Mesh 强制走一次蒙皮计算（最关键的一步）
    //   3) updateWhenOffscreen 临时打开，避免 SMR 被视锥剔除后跳过蒙皮

    private static void CollectChildPaths(Transform t, string prefix,
        List<string> outList)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            var p = string.IsNullOrEmpty(prefix) ? c.name : prefix + "/" + c.name;
            outList.Add(p);
            CollectChildPaths(c, p, outList);
        }
    }

    // Sheet 拼接器 —— 所有序列帧入口共用
    // renderFrame(i) 返回第 i 帧的 Texture2D，BuildSheet 负责拼 Sheet、导出单帧、保存

    private void CaptureSequence()
    {
        var sourceCam = targetCamera != null ? targetCamera : Camera.main;
        if (sourceCam == null)
        {
            EditorUtility.DisplayDialog("Sequence",
                "找不到相机：请指定 Target Camera 或场景里有一个 MainCamera。", "OK");
            return;
        }

        // 过滤掉 clip 或 target 为 null 的 pair
        var validPairs = new List<AnimPairEntry>();
        for (int i = 0; i < animPairs.Count; i++)
        {
            var p = animPairs[i];
            if (p.clip != null && p.target != null)
                validPairs.Add(p);
        }
        if (validPairs.Count == 0)
        {
            EditorUtility.DisplayDialog("Sequence",
                "没有有效的 Animation Pair。请至少指定一个 Clip 和 Target。", "OK");
            return;
        }

        // 使用第一个有效 pair 的 clip 长度作为时间基准
        float masterLength = validPairs[0].clip.length;

        // PrefabName 模式：获取 Hierarchy 选中物体名称
        string prefabName = null;
        if (namingMode == NamingMode.PrefabName)
        {
            prefabName = GetSelectedGameObjectName();
            if (string.IsNullOrEmpty(prefabName))
            {
                EditorUtility.DisplayDialog("Sequence",
                    "PrefabName 模式需要在 Hierarchy 中选中一个 GameObject。",
                    "OK");
                return;
            }
        }

        int frameCount = Mathf.Max(1, sequenceFrameCount);
        int cols = Mathf.Max(1, sequenceColumns);
        int rows = Mathf.CeilToInt(frameCount / (float)cols);

        int outW = width, outH = height;
        if (useGameViewSize)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            outW = Mathf.Max(1, Mathf.RoundToInt(gv.x));
            outH = Mathf.Max(1, Mathf.RoundToInt(gv.y));
        }

        long sheetPixels = (long)cols * outW * rows * outH;
        if (sheetPixels > 16384L * 16384L)
        {
            if (!EditorUtility.DisplayDialog("Sequence",
                $"拼接后的 Sheet 大约 {cols * outW} x {rows * outH} 像素，"
                + "可能超过 GPU 纹理上限或导致编码失败。是否继续？",
                "继续", "取消")) return;
        }

        var folderAbs = GetAbsoluteOutputFolder();
        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var clipName = validPairs[0].clip.name;

        string framesDirName = null;
        if (exportIndividualFrames)
        {
            framesDirName = namingMode == NamingMode.Default
                ? $"{fileNamePrefix}_{clipName}_{stamp}_frames"
                : $"{(addPrefix && !string.IsNullOrEmpty(fileNamePrefix) ? fileNamePrefix + "_" : "")}{prefabName}_{clipName}_{(addTimestamp ? stamp + "_" : "")}frames";
        }
        var sheetFileName = BuildSequenceSheetName(prefabName, clipName, stamp, cols, rows, outW, outH);

        // 禁用 target 上的 Animator / Animation，用 AnimationMode 手动采样
        var disabledAnimators = new Dictionary<Animator, bool>();
        var disabledAnimations = new Dictionary<Animation, bool>();
        foreach (var pair in validPairs)
        {
            var ator = pair.target.GetComponent<Animator>();
            if (ator != null && !disabledAnimators.ContainsKey(ator))
            {
                disabledAnimators[ator] = ator.enabled;
                ator.enabled = false;
            }
            var leg = pair.target.GetComponent<Animation>();
            if (leg != null && !disabledAnimations.ContainsKey(leg))
            {
                disabledAnimations[leg] = leg.enabled;
                leg.enabled = false;
            }
        }

        bool started = false;
        string sheetPath = null;
        try
        {
            AnimationMode.StartAnimationMode();
            started = true;

            bool ok = CapturePipeline.BuildSheet(i =>
            {
                float t;
                if (sequenceUseEndpoints && frameCount > 1)
                    t = i / (float)(frameCount - 1) * masterLength;
                else
                    t = (i + 0.5f) / frameCount * masterLength;

                AnimationMode.BeginSampling();
                foreach (var pair in validPairs)
                    AnimationMode.SampleAnimationClip(pair.target, pair.clip, t);
                AnimationMode.EndSampling();

                CapturePipeline.ForceSkinnedRefresh();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                return RenderCurrentFrame(sourceCam, outW, outH);
            },
                frameCount, cols, rows, outW, outH,
                folderAbs, sheetFileName, framesDirName,
                exportIndividualFrames, out sheetPath);

            if (ok)
                Debug.Log($"<color=#7fcf6f>[Screenshot]</color> Sheet saved: {sheetPath} "
                        + $"(layout {cols}x{rows}, frame {outW}x{outH}, {validPairs.Count} pairs)");
            else
                Debug.Log("[Screenshot] Sequence canceled.");
        }
        finally
        {
            if (started) AnimationMode.StopAnimationMode();
            foreach (var kvp in disabledAnimators)
                if (kvp.Key != null) kvp.Key.enabled = kvp.Value;
            foreach (var kvp in disabledAnimations)
                if (kvp.Key != null) kvp.Key.enabled = kvp.Value;
            if (!string.IsNullOrEmpty(sheetPath))
            {
                var projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
                if (sheetPath.StartsWith(projectRoot)) AssetDatabase.Refresh();
                if (openFolderAfterSave) EditorUtility.RevealInFinder(sheetPath);
            }
        }
    }

    // === Particle Sequence Capture ===

    /// <summary>生成粒子扫描信息的显示字符串。</summary>
    private string BuildParticleScanDisplay(GameObject root)
    {
        if (root == null) return "";

        var info = ParticleSequenceRender.BuildScanInfo(root);
        if (info.systemCount == 0)
            return "";

        var sb = new StringBuilder();
        for (int i = 0; i < info.systemCount; i++)
        {
            sb.Append("  · ").Append(info.names[i])
              .Append("  ").Append(info.durations[i].ToString("F1"))
              .Append("s  ").Append(info.isLooping[i] ? "loop ✓" : "once")
              .AppendLine();
        }

        if (info.maxDuration <= 0f)
        {
            sb.Append("↳ 未检测到有效时长");
        }
        else if (info.allLooping)
        {
            sb.Append("↳ 建议时长: ").Append(info.maxDuration.ToString("F1"))
              .Append("s（捕获完整循环）");
        }
        else
        {
            sb.Append("↳ 建议范围: ").Append(info.minDuration.ToString("F1"))
              .Append("s ~ ").Append(info.maxDuration.ToString("F1"))
              .Append("s");
        }

        return sb.ToString();
    }

    /// <summary>执行粒子特效序列帧截图。</summary>
    private void CaptureParticleSequence()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null)
        {
            EditorUtility.DisplayDialog("Particle Sequence",
                "找不到相机：请指定 Target Camera 或场景里有一个 MainCamera。", "OK");
            return;
        }
        if (particleTarget == null)
        {
            EditorUtility.DisplayDialog("Particle Sequence",
                "请先指定 Particle Target（拖入特效预制体根节点）。", "OK");
            return;
        }

        // 检查有没有粒子系统
        var checkPS = ParticleSequenceRender.GetAllSystems(particleTarget);
        if (checkPS.Length == 0)
        {
            EditorUtility.DisplayDialog("Particle Sequence",
                "指定的 GameObject 上未找到任何 ParticleSystem 组件。", "OK");
            return;
        }

        int outW = width, outH = height;
        if (useGameViewSize)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            outW = Mathf.Max(1, Mathf.RoundToInt(gv.x));
            outH = Mathf.Max(1, Mathf.RoundToInt(gv.y));
        }

        // PrefabName 模式
        string prefabName = null;
        if (namingMode == NamingMode.PrefabName)
        {
            prefabName = GetSelectedGameObjectName();
            if (string.IsNullOrEmpty(prefabName))
            {
                EditorUtility.DisplayDialog("Particle Sequence",
                    "PrefabName 模式需要在 Hierarchy 中选中一个 GameObject。", "OK");
                return;
            }
        }

        int frameCount = Mathf.Max(1, Mathf.CeilToInt(particleDuration * sequenceFps));
        int cols = Mathf.Max(1, sequenceColumns);
        int rows = Mathf.CeilToInt(frameCount / (float)cols);

        // Sheet 尺寸检查
        long sheetPixels = (long)cols * outW * rows * outH;
        if (sheetPixels > 16384L * 16384L)
        {
            if (!EditorUtility.DisplayDialog("Particle Sequence",
                $"Sheet 大约 {cols * outW} x {rows * outH} 像素，"
                + "可能超过 GPU 上限。是否继续？",
                "继续", "取消")) return;
        }

        var folderAbs = GetAbsoluteOutputFolder();
        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // 构建文件名
        string sheetFileName;
        string framesDirName = null;
        string targetName = particleTarget.name;

        if (namingMode == NamingMode.Default)
        {
            sheetFileName =
                $"{fileNamePrefix}_{targetName}_{stamp}_sheet_{cols}x{rows}_{outW}x{outH}.png";
            if (exportIndividualFrames)
                framesDirName = $"{fileNamePrefix}_{targetName}_{stamp}_frames";
        }
        else
        {
            var sb = new StringBuilder();
            if (addPrefix && !string.IsNullOrEmpty(fileNamePrefix))
                sb.Append(fileNamePrefix).Append('_');
            sb.Append(prefabName ?? targetName);
            if (addTimestamp)
                sb.Append('_').Append(stamp);
            sb.Append("_sheet_").Append(cols).Append('x').Append(rows)
              .Append('_').Append(outW).Append('x').Append(outH);
            sb.Append(".png");
            sheetFileName = sb.ToString();

            if (exportIndividualFrames)
            {
                sb.Clear();
                if (addPrefix && !string.IsNullOrEmpty(fileNamePrefix))
                    sb.Append(fileNamePrefix).Append('_');
                sb.Append(prefabName ?? targetName);
                if (addTimestamp)
                    sb.Append('_').Append(stamp);
                sb.Append("_frames");
                framesDirName = sb.ToString();
            }
        }


        // --- 不透明：CaptureRGB 直接抓，和 Single/AnimSequence 完全一致 ---
        if (!transparentBackground)
        {
            bool ok = CaptureParticleSequence_Opaque(
                cam, particleTarget, checkPS,
                particleDuration, frameCount, cols, rows,
                outW, outH, sequenceUseEndpoints,
                exportIndividualFrames,
                folderAbs, sheetFileName, framesDirName);
            if (ok)
            {
                var sheetPath = Path.Combine(folderAbs, sheetFileName).Replace('\\', '/');
                var projectRoot = Directory.GetParent(Application.dataPath)
                                    .FullName.Replace('\\', '/');
                if (sheetPath.StartsWith(projectRoot))
                    AssetDatabase.Refresh();
                if (openFolderAfterSave)
                    EditorUtility.RevealInFinder(sheetPath);
            }
            return;
        }

        // --- 透明：检查是否有深度遮挡 ---
        var effectiveOccluders = new List<GameObject>();
        for (int i = 0; i < occlusionOccluders.Count; i++)
            if (occlusionOccluders[i] != null)
                effectiveOccluders.Add(occlusionOccluders[i]);

        bool transparentOk;
        if (effectiveOccluders.Count > 0)
        {
            // 有深度遮挡 → ParticleSequenceRender 自定义三 pass（含遮挡判断）
            transparentOk = ParticleSequenceRender.CaptureParticleSheet(
                cam, particleTarget,
                particleDuration, frameCount, cols,
                outW, outH, superSize, sequenceUseEndpoints,
                exportIndividualFrames,
                folderAbs, sheetFileName, framesDirName,
                effectiveOccluders);
        }
        else
        {
            // 无遮挡 → 直接用通用渲染（与 Single/AnimSequence 完全一致）
            transparentOk = CaptureParticleSequence_Transparent(
                cam, particleTarget, checkPS,
                particleDuration, frameCount, cols, rows,
                outW, outH, sequenceUseEndpoints,
                exportIndividualFrames,
                folderAbs, sheetFileName, framesDirName);
        }

        if (transparentOk)
        {
            var sheetPath = Path.Combine(folderAbs, sheetFileName).Replace('\\', '/');
            var projectRoot = Directory.GetParent(Application.dataPath)
                                .FullName.Replace('\\', '/');
            if (sheetPath.StartsWith(projectRoot))
                AssetDatabase.Refresh();
            if (openFolderAfterSave)
                EditorUtility.RevealInFinder(sheetPath);
        }
    }

    // 粒子序列帧不透明模式：原始相机 + CaptureRGB，和 Single/Anim 一致
    private bool CaptureParticleSequence_Opaque(
        Camera cam, GameObject target, ParticleSystem[] allPS,
        float duration, int frameCount, int cols, int rows,
        int outW, int outH, bool useEndpoints,
        bool exportIndividual,
        string folderAbs, string sheetFileName, string framesDirName)
    {
        var savedStates = ParticleSequenceRender.FixRandomSeeds(allPS);
        try
        {
            bool ok = CapturePipeline.BuildSheet(i =>
            {
                float t;
                if (useEndpoints && frameCount > 1)
                    t = i / (float)(frameCount - 1) * duration;
                else
                    t = (i + 0.5f) / frameCount * duration;

                ParticleSequenceRender.SimulateAll(allPS, t);
                var tex = CapturePipeline.CaptureRGB(cam, outW, outH);
                if (tex == null)
                    tex = CapturePipeline.RenderAndDownsample(cam, outW * superSize, outH * superSize,
                        false, false, false, default, outW, outH, superSize);
                return tex;
            },
                frameCount, cols, rows, outW, outH,
                folderAbs, sheetFileName, framesDirName,
                exportIndividual, out _);

            if (ok)
                Debug.Log($"<color=#7fcf6f>[ParticleSequence]</color> Sheet saved: "
                    + Path.Combine(folderAbs, sheetFileName)
                    + $" (layout {cols}x{rows}, frame {outW}x{outH})");
            return ok;
        }
        finally
        {
            ParticleSequenceRender.RestoreRandomSeeds(savedStates);
            ParticleSequenceRender.ClearAll(allPS);
        }
    }

    // 粒子序列帧透明无遮挡：与 Single/AnimSequence 完全相同的渲染逻辑
    private bool CaptureParticleSequence_Transparent(
        Camera cam, GameObject target, ParticleSystem[] allPS,
        float duration, int frameCount, int cols, int rows,
        int outW, int outH, bool useEndpoints,
        bool exportIndividual,
        string folderAbs, string sheetFileName, string framesDirName)
    {
        var savedStates = ParticleSequenceRender.FixRandomSeeds(allPS);
        try
        {
            bool ok = CapturePipeline.BuildSheet(i =>
            {
                float t;
                if (useEndpoints && frameCount > 1)
                    t = i / (float)(frameCount - 1) * duration;
                else
                    t = (i + 0.5f) / frameCount * duration;

                ParticleSequenceRender.SimulateAll(allPS, t);
                return CapturePipeline.CaptureFrame(cam, outW, outH, superSize, true);
            },
                frameCount, cols, rows, outW, outH,
                folderAbs, sheetFileName, framesDirName,
                exportIndividual, out _);

            if (ok)
                Debug.Log($"<color=#7fcf6f>[ParticleSequence]</color> Sheet saved: "
                    + Path.Combine(folderAbs, sheetFileName)
                    + $" (layout {cols}x{rows}, frame {outW}x{outH})");
            return ok;
        }
        finally
        {
            ParticleSequenceRender.RestoreRandomSeeds(savedStates);
            ParticleSequenceRender.ClearAll(allPS);
        }
    }


    // 双背景抠图：对每个像素用黑底/白底两张图反推 alpha
    //   黑底:  black = src.rgb * a + 0 * (1-a) = src.rgb * a
    //   白底:  white = src.rgb * a + 1 * (1-a) = src.rgb * a + (1-a)
    //   相减:  white - black = 1 - a   =>   a = 1 - mean(white - black)
    // RGB 用 PASS A（透明背景渲染结果，已含后处理），它对实心和半透像素都给出
    // 正确的"显示在透明上的最终颜色"。完全透明的像素 a≈0，其 RGB 不影响 PNG 显示。

    // 将 Texture2D 的 RGB 通道从线性空间转换到 sRGB 空间（Gamma 校正）。
    // 通过反射获取 Game 视图的内部 RenderTexture

    // 将相机渲染到 Game 视图的内部 RT 并捕获 RGB（sRGB 空间）。
    // 失败时返回 null，调用方应 fallback 到原方案。


    // ---- Shadow Catcher ----

    // 获取 Hierarchy 中当前选中 GameObject 的名称。
    private static string GetSelectedGameObjectName()
    {
        var go = Selection.activeGameObject;
        return go != null ? go.name : null;
    }

    // 静态版本：QuickCapture 快捷键使用
    private static string BuildFileName(string prefix, string prefabName, int outW, int outH,
        NamingMode mode, bool addPrefix, bool addTimestamp, bool addResolution)
    {
        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (mode == NamingMode.Default)
        {
            return $"{prefix}_{stamp}_{outW}x{outH}.png";
        }

        var sb = new StringBuilder();
        if (addPrefix && !string.IsNullOrEmpty(prefix))
            sb.Append(prefix).Append('_');
        sb.Append(prefabName ?? "unknown");
        if (addTimestamp)
            sb.Append('_').Append(stamp);
        if (addResolution)
            sb.Append('_').Append(outW).Append('x').Append(outH);
        sb.Append(".png");
        return sb.ToString();
    }

    // 实例版本：GUI Capture 按钮使用
    private string BuildFileName(string prefabName, int outW, int outH)
    {
        return BuildFileName(fileNamePrefix, prefabName, outW, outH,
            namingMode, addPrefix, addTimestamp, addResolution);
    }

    // 序列帧 Sheet 文件名
    private string BuildSequenceSheetName(string prefabName, string clipName,
        string stamp, int cols, int rows, int outW, int outH)
    {
        if (namingMode == NamingMode.Default)
        {
            return $"{fileNamePrefix}_{clipName}_{stamp}_sheet_{cols}x{rows}_{outW}x{outH}.png";
        }

        var sb = new StringBuilder();
        if (addPrefix && !string.IsNullOrEmpty(fileNamePrefix))
            sb.Append(fileNamePrefix).Append('_');
        sb.Append(prefabName ?? "unknown");
        sb.Append('_').Append(clipName);
        if (addTimestamp)
            sb.Append('_').Append(stamp);
        sb.Append("_sheet_").Append(cols).Append('x').Append(rows)
          .Append('_').Append(outW).Append('x').Append(outH);
        sb.Append(".png");
        return sb.ToString();
    }

    // 序列帧单帧文件名
    private string BuildSequenceFrameName(string prefabName, string clipName, int frameIndex)
    {
        if (namingMode == NamingMode.Default)
        {
            return $"{fileNamePrefix}_{clipName}_{frameIndex:D4}.png";
        }

        var sb = new StringBuilder();
        if (addPrefix && !string.IsNullOrEmpty(fileNamePrefix))
            sb.Append(fileNamePrefix).Append('_');
        sb.Append(prefabName ?? "unknown");
        sb.Append('_').Append(clipName);
        sb.Append('_').AppendFormat("{0:D4}", frameIndex);
        sb.Append(".png");
        return sb.ToString();
    }

    private string GetAbsoluteOutputFolder()
    {
        if (string.IsNullOrEmpty(outputFolder)) outputFolder = "Screenshots";
        if (Path.IsPathRooted(outputFolder)) return outputFolder.Replace('\\', '/');
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, outputFolder).Replace('\\', '/');
    }

    private void LoadPrefs()
    {
        width  = EditorPrefs.GetInt(PREF_PREFIX + "w", 1024);
        height = EditorPrefs.GetInt(PREF_PREFIX + "h", 1024);
        superSize = EditorPrefs.GetInt(PREF_PREFIX + "ss", 2);
        transparentBackground = EditorPrefs.GetBool(PREF_PREFIX + "tbg", true);
                outputFolder = EditorPrefs.GetString(PREF_PREFIX + "folder", "Screenshots");
        fileNamePrefix = EditorPrefs.GetString(PREF_PREFIX + "prefix", "shot");
        openFolderAfterSave = EditorPrefs.GetBool(PREF_PREFIX + "open", true);
        useGameViewSize = EditorPrefs.GetBool(PREF_PREFIX + "gvs", false);
        namingMode = (NamingMode)EditorPrefs.GetInt(PREF_PREFIX + "nm", 0);
        captureMode = (CaptureMode)EditorPrefs.GetInt(PREF_PREFIX + "cm", 0);
        addPrefix = EditorPrefs.GetBool(PREF_PREFIX + "ap", true);
        addTimestamp = EditorPrefs.GetBool(PREF_PREFIX + "ats", true);
        addResolution = EditorPrefs.GetBool(PREF_PREFIX + "ar", true);
        sequenceFrameCount = EditorPrefs.GetInt(PREF_PREFIX + "seqN", 64);
        sequenceColumns = EditorPrefs.GetInt(PREF_PREFIX + "seqC", 8);
        exportIndividualFrames = EditorPrefs.GetBool(PREF_PREFIX + "seqInd", false);
        sequenceUseEndpoints = EditorPrefs.GetBool(PREF_PREFIX + "seqEnd", false);
        sequenceFps = EditorPrefs.GetInt(PREF_PREFIX + "seqFps", 30);
        // Particle Sequence
        particleDuration = EditorPrefs.GetFloat(PREF_PREFIX + "psDur", 5f);
        var psTargetPath = EditorPrefs.GetString(PREF_PREFIX + "psTarget", "");
        if (!string.IsNullOrEmpty(psTargetPath))
            particleTarget = GameObject.Find(psTargetPath);
enableShadowCatcher = EditorPrefs.GetBool(PREF_PREFIX + "scEn", false);
        shadowIntensity = EditorPrefs.GetFloat(PREF_PREFIX + "scInt", 1f);
        // Load shadow casters
        var castersJson = EditorPrefs.GetString(PREF_PREFIX + "scCasters", "");
        if (!string.IsNullOrEmpty(castersJson))
        {
            try
            {
                var wrapper = JsonUtility.FromJson<GameObjectPathList>(castersJson);
                shadowCasterRoots.Clear();
                if (wrapper != null && wrapper.paths != null)
                {
                    foreach (var p in wrapper.paths)
                        shadowCasterRoots.Add(
                            string.IsNullOrEmpty(p) ? null : GameObject.Find(p));
                }
            }
            catch { /* fall through to migration */ }
        }
        // Backward compat: migrate old single caster
        if (shadowCasterRoots.Count == 0 || (shadowCasterRoots.Count == 1
            && shadowCasterRoots[0] == null))
        {
            var oldPath = EditorPrefs.GetString(PREF_PREFIX + "scCaster", "");
            if (!string.IsNullOrEmpty(oldPath))
            {
                shadowCasterRoots.Clear();
                shadowCasterRoots.Add(GameObject.Find(oldPath));
            }
        }
        if (shadowCasterRoots.Count == 0)
            shadowCasterRoots.Add(null);

        // Load shadow receivers
        var receiversJson = EditorPrefs.GetString(PREF_PREFIX + "scReceivers", "");
        if (!string.IsNullOrEmpty(receiversJson))
        {
            try
            {
                var wrapper = JsonUtility.FromJson<GameObjectPathList>(receiversJson);
                shadowReceivers.Clear();
                if (wrapper != null && wrapper.paths != null)
                {
                    foreach (var p in wrapper.paths)
                        shadowReceivers.Add(
                            string.IsNullOrEmpty(p) ? null : GameObject.Find(p));
                }
            }
            catch { /* fall through to migration */ }
        }
        // Backward compat: migrate old single receiver
        if (shadowReceivers.Count == 0 || (shadowReceivers.Count == 1
            && shadowReceivers[0] == null))
        {
            var oldPath = EditorPrefs.GetString(PREF_PREFIX + "scReceiver", "");
            if (!string.IsNullOrEmpty(oldPath))
            {
                shadowReceivers.Clear();
                shadowReceivers.Add(GameObject.Find(oldPath));
            }
        }
        if (shadowReceivers.Count == 0)
            shadowReceivers.Add(null);

        // Load occlusion occluders
        var occludersJson = EditorPrefs.GetString(PREF_PREFIX + "scOcc", "");
        if (!string.IsNullOrEmpty(occludersJson))
        {
            try
            {
                var wrapper = JsonUtility.FromJson<GameObjectPathList>(occludersJson);
                occlusionOccluders.Clear();
                if (wrapper != null && wrapper.paths != null)
                {
                    foreach (var p in wrapper.paths)
                        occlusionOccluders.Add(
                            string.IsNullOrEmpty(p) ? null : GameObject.Find(p));
                }
            }
            catch { /* ignore */ }
        }
        if (occlusionOccluders.Count == 0)
            occlusionOccluders.Add(null);

        // Load anim pairs (new JSON-based persistence)
        var pairsJson = EditorPrefs.GetString(PREF_PREFIX + "seqPairs", "");
        if (!string.IsNullOrEmpty(pairsJson))
        {
            try
            {
                var saveData = JsonUtility.FromJson<AnimPairSaveData>(pairsJson);
                animPairs.Clear();
                if (saveData != null && saveData.pairs != null)
                {
                    foreach (var entry in saveData.pairs)
                    {
                        var pair = new AnimPairEntry();
                        if (!string.IsNullOrEmpty(entry.clipGuid))
                        {
                            var path = AssetDatabase.GUIDToAssetPath(entry.clipGuid);
                            if (!string.IsNullOrEmpty(path))
                                pair.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                        }
                        if (!string.IsNullOrEmpty(entry.targetPath))
                            pair.target = GameObject.Find(entry.targetPath);
                        animPairs.Add(pair);
                    }
                }
            }
            catch { /* JSON parse error, fall through to migration */ }
        }

        // Backward compat: migrate old single clip/target to new list
        if (animPairs.Count == 0 || (animPairs.Count == 1
            && animPairs[0].clip == null && animPairs[0].target == null))
        {
            var clipPath = EditorPrefs.GetString(PREF_PREFIX + "seqClip", "");
            var animTargetPath = EditorPrefs.GetString(PREF_PREFIX + "seqTarget", "");
            if (!string.IsNullOrEmpty(clipPath) || !string.IsNullOrEmpty(animTargetPath))
            {
                var migrated = new AnimPairEntry();
                if (!string.IsNullOrEmpty(clipPath))
                    migrated.clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (!string.IsNullOrEmpty(animTargetPath))
                    migrated.target = GameObject.Find(animTargetPath);
                animPairs.Clear();
                animPairs.Add(migrated);
                // 清除旧 key
                EditorPrefs.DeleteKey(PREF_PREFIX + "seqClip");
                EditorPrefs.DeleteKey(PREF_PREFIX + "seqTarget");
            }
        }

        if (animPairs.Count == 0)
            animPairs.Add(new AnimPairEntry());

        var camPath = EditorPrefs.GetString(PREF_PREFIX + "cam", "");
        if (!string.IsNullOrEmpty(camPath))
        {
            var go = GameObject.Find(camPath);
            if (go != null) targetCamera = go.GetComponent<Camera>();
        }
    }

    private void SavePrefs()
    {
        EditorPrefs.SetInt(PREF_PREFIX + "w", width);
        EditorPrefs.SetInt(PREF_PREFIX + "h", height);
        EditorPrefs.SetInt(PREF_PREFIX + "ss", superSize);
        EditorPrefs.SetBool(PREF_PREFIX + "tbg", transparentBackground);
                EditorPrefs.SetString(PREF_PREFIX + "folder", outputFolder ?? "");
        EditorPrefs.SetString(PREF_PREFIX + "prefix", fileNamePrefix ?? "shot");
        EditorPrefs.SetBool(PREF_PREFIX + "open", openFolderAfterSave);
        EditorPrefs.SetBool(PREF_PREFIX + "gvs", useGameViewSize);
        EditorPrefs.SetInt(PREF_PREFIX + "nm", (int)namingMode);
        EditorPrefs.SetInt(PREF_PREFIX + "cm", (int)captureMode);
        EditorPrefs.SetBool(PREF_PREFIX + "ap", addPrefix);
        EditorPrefs.SetBool(PREF_PREFIX + "ats", addTimestamp);
        EditorPrefs.SetBool(PREF_PREFIX + "ar", addResolution);
        EditorPrefs.SetInt(PREF_PREFIX + "seqN", sequenceFrameCount);
        EditorPrefs.SetInt(PREF_PREFIX + "seqC", sequenceColumns);
        EditorPrefs.SetBool(PREF_PREFIX + "seqInd", exportIndividualFrames);
        EditorPrefs.SetBool(PREF_PREFIX + "seqEnd", sequenceUseEndpoints);
        EditorPrefs.SetInt(PREF_PREFIX + "seqFps", sequenceFps);
        // Particle Sequence
        EditorPrefs.SetFloat(PREF_PREFIX + "psDur", particleDuration);
        EditorPrefs.SetString(PREF_PREFIX + "psTarget",
            particleTarget != null ? GetGameObjectPath(particleTarget) : "");
EditorPrefs.SetBool(PREF_PREFIX + "scEn", enableShadowCatcher);
        EditorPrefs.SetFloat(PREF_PREFIX + "scInt", shadowIntensity);
        // Save shadow casters
        {
            var wrapper = new GameObjectPathList();
            for (int i = 0; i < shadowCasterRoots.Count; i++)
                wrapper.paths.Add(shadowCasterRoots[i] != null
                    ? GetGameObjectPath(shadowCasterRoots[i]) : "");
            EditorPrefs.SetString(PREF_PREFIX + "scCasters", JsonUtility.ToJson(wrapper));
        }
        // Save shadow receivers
        {
            var wrapper = new GameObjectPathList();
            for (int i = 0; i < shadowReceivers.Count; i++)
                wrapper.paths.Add(shadowReceivers[i] != null
                    ? GetGameObjectPath(shadowReceivers[i]) : "");
            EditorPrefs.SetString(PREF_PREFIX + "scReceivers", JsonUtility.ToJson(wrapper));
        }
        // Save occlusion occluders
        {
            var wrapper = new GameObjectPathList();
            for (int i = 0; i < occlusionOccluders.Count; i++)
                wrapper.paths.Add(occlusionOccluders[i] != null
                    ? GetGameObjectPath(occlusionOccluders[i]) : "");
            EditorPrefs.SetString(PREF_PREFIX + "scOcc", JsonUtility.ToJson(wrapper));
        }
        // 清除旧格式 key
        EditorPrefs.DeleteKey(PREF_PREFIX + "scCaster");
        EditorPrefs.DeleteKey(PREF_PREFIX + "scReceiver");

        // Save anim pairs as JSON
        var saveData = new AnimPairSaveData();
        for (int i = 0; i < animPairs.Count; i++)
        {
            var p = animPairs[i];
            var entry = new AnimPairSaveEntry();
            if (p.clip != null)
                entry.clipGuid = AssetDatabase.AssetPathToGUID(
                    AssetDatabase.GetAssetPath(p.clip));
            if (p.target != null)
                entry.targetPath = GetGameObjectPath(p.target);
            saveData.pairs.Add(entry);
        }
        EditorPrefs.SetString(PREF_PREFIX + "seqPairs", JsonUtility.ToJson(saveData));

        // 清除旧格式 key（迁移后不再需要）
        EditorPrefs.DeleteKey(PREF_PREFIX + "seqClip");
        EditorPrefs.DeleteKey(PREF_PREFIX + "seqTarget");

        EditorPrefs.SetString(PREF_PREFIX + "cam",
            targetCamera != null ? GetGameObjectPath(targetCamera.gameObject) : "");
    }

    private static string GetGameObjectPath(GameObject go)
    {
        var path = "/" + go.name;
        var t = go.transform;
        while (t.parent != null) { t = t.parent; path = "/" + t.name + path; }
        return path;
    }
}
