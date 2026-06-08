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

    private enum NamingMode
    {
        Default,
        PrefabName
    }

    private Camera targetCamera;
    private int width = 1024;
    private int height = 1024;
    private bool transparentBackground = true;
    private bool keepPostProcessing = true;
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

    // === Sequence capture ===
    private bool sequenceFoldout;

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

    // === Shadow Catcher ===
    private bool shadowCatcherFoldout;
    private bool enableShadowCatcher = false;
    private List<GameObject> shadowCasterRoots =
        new List<GameObject> { null };
    private List<GameObject> shadowReceivers =
        new List<GameObject> { null };
    private float shadowIntensity = 1.0f;
    private bool shadowCatcherPPComposite = true;

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
        bool kpp = EditorPrefs.GetBool(PREF_PREFIX + "kpp", true);
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

        // 渲染
        int rw = outW * ss;
        int rh = outH * ss;
        Texture2D finalTex = null;
        try
        {
            if (!tbg)
                finalTex = RenderAndDownsample(cam, rw, rh, false, false, false, default, outW, outH, ss);
            else if (!kpp)
                finalTex = RenderAndDownsample(cam, rw, rh, true, true, false, default, outW, outH, ss);
            else
                finalTex = RenderWithGameView(cam, rw, rh, outW, outH, ss);

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
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button(
                new GUIContent("Capture",
                    "用当前设置截取一帧\n快捷键: Ctrl+Shift+X"),
                GUILayout.Height(30))) Capture();

            GUI.backgroundColor = new Color(0.7f, 0.85f, 1f);
            if (GUILayout.Button(
                new GUIContent("Capture Sequence", "用当前设置截取序列帧 Sprite Sheet"),
                GUILayout.Height(30))) CaptureSequence();
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Camera", EditorStyles.boldLabel);
        targetCamera = (Camera)EditorGUILayout.ObjectField(
            new GUIContent("Target Camera", "留空则使用 MainCamera"),
            targetCamera, typeof(Camera), true);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        useGameViewSize = EditorGUILayout.Toggle(
            new GUIContent("Use Game View Size",
                "勾选后输出尺寸跟随 Game 视图当前分辨率，Width/Height 设置被忽略。\n"
                + "Super Size 仍然生效。"),
            useGameViewSize);
        using (new EditorGUI.DisabledScope(useGameViewSize))
        {
            width  = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("Width", "输出图片宽度"), width));
            height = Mathf.Max(1, EditorGUILayout.IntField(
                new GUIContent("Height", "输出图片高度"), height));
        }
        if (useGameViewSize)
        {
            var gv = UnityEditor.Handles.GetMainGameViewSize();
            EditorGUILayout.LabelField(" ",
                $"Game View: {(int)gv.x} x {(int)gv.y}",
                EditorStyles.miniLabel);
        }
        superSize = Mathf.Clamp(EditorGUILayout.IntSlider(
            new GUIContent("Super Size", "渲染时放大倍数再下采样，>1 减少锯齿，2~3 即可"),
            superSize, 1, 4), 1, 4);
        using (new EditorGUI.DisabledScope(enableShadowCatcher))
        {
            transparentBackground = EditorGUILayout.Toggle(
                new GUIContent("Transparent BG", "输出 PNG 带透明通道"),
                transparentBackground);
        }
        using (new EditorGUI.DisabledScope(!transparentBackground || enableShadowCatcher))
        {
            keepPostProcessing = EditorGUILayout.Toggle(
                new GUIContent("Keep Post Processing",
                    "勾选后从 Game 视图捕获颜色，完整保留 Bloom / Tonemap 等后处理效果。\n"
                    + "未勾选时透明背景会临时关闭后处理（速度更快）。"),
                keepPostProcessing);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("File", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            outputFolder = EditorGUILayout.TextField(
                new GUIContent("Folder", "截图保存目录，相对路径或绝对路径"), outputFolder);
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
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Naming", EditorStyles.boldLabel);
        namingMode = (NamingMode)EditorGUILayout.EnumPopup(
            new GUIContent("Naming Mode",
                "Default: 使用现有规则 {prefix}_{timestamp}_{W}x{H}\n"
                + "Prefab Name: 使用 Hierarchy 中选中物体的名字"),
            namingMode);

        if (namingMode == NamingMode.Default)
        {
            fileNamePrefix = EditorGUILayout.TextField(
                new GUIContent("Name Prefix", "文件名前缀，如 shot → shot_20260527_153947.png"),
                fileNamePrefix);
        }
        else
        {
            addPrefix = EditorGUILayout.Toggle(
                new GUIContent("Add Prefix", "在预制体名称前添加前缀"),
                addPrefix);
            if (addPrefix)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    fileNamePrefix = EditorGUILayout.TextField(
                        new GUIContent("Prefix", "前缀文本"),
                        fileNamePrefix);
                }
            }

            addTimestamp = EditorGUILayout.Toggle(
                new GUIContent("Add Timestamp", "文件名末尾追加时间戳"),
                addTimestamp);
            addResolution = EditorGUILayout.Toggle(
                new GUIContent("Add Resolution", "文件名末尾追加分辨率"),
                addResolution);

            // 预览当前命名模式的效果
            var selName = GetSelectedGameObjectName();
            if (!string.IsNullOrEmpty(selName))
            {
                var previewName = BuildFileName(selName, 1024, 1024);
                EditorGUILayout.LabelField(" ",
                    $"Preview: {previewName}",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(" ",
                    "Preview: 未在 Hierarchy 中选中物体，无法预览",
                    EditorStyles.miniLabel);
            }
        }

        openFolderAfterSave = EditorGUILayout.Toggle(
            new GUIContent("Open Folder After Save",
                "截图完成后在资源管理器中打开所在目录"),
            openFolderAfterSave);

        EditorGUILayout.Space();
        sequenceFoldout = EditorGUILayout.Foldout(sequenceFoldout,
            "Sequence (Animation Sprite Sheet)", false);
        if (sequenceFoldout)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Animation Pairs", EditorStyles.boldLabel);
                int removeIndex = -1;
                for (int i = 0; i < animPairs.Count; i++)
                {
                    var pair = animPairs[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // Clip slot (AnimationClip only)
                        var clipRect = EditorGUILayout.GetControlRect(
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        var clip = pair.clip;
                        MultiDragObjectField(clipRect, ref clip,
                            typeof(AnimationClip), false, i, MD_ZONE_CLIP);
                        pair.clip = clip;

                        // Target slot (GameObject, allow scene objects)
                        var targetRect = EditorGUILayout.GetControlRect(
                            GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        var target = pair.target;
                        MultiDragObjectField(targetRect, ref target,
                            typeof(GameObject), true, i, MD_ZONE_TARGET);
                        pair.target = target;

                        if (animPairs.Count > 1)
                        {
                            if (GUILayout.Button("X", GUILayout.Width(22)))
                                removeIndex = i;
                        }
                    }
                }
                if (removeIndex >= 0)
                    animPairs.RemoveAt(removeIndex);

                // 处理 animation pairs 的多对象拖入分发
                if (_pendingMultiDragObjs != null
                    && (_pendingMultiDragZone == MD_ZONE_CLIP || _pendingMultiDragZone == MD_ZONE_TARGET))
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

                EditorGUILayout.Space();

                sequenceFrameCount = Mathf.Max(1,
                    EditorGUILayout.IntField(
                        new GUIContent("Frame Count", "均匀采样动画时长得到的帧数"),
                        sequenceFrameCount));

                sequenceColumns = Mathf.Max(1,
                    EditorGUILayout.IntField(
                        new GUIContent("Columns",
                            "Sheet 的列数；行数自动 = ceil(FrameCount / Columns)"),
                        sequenceColumns));

                int rows = Mathf.CeilToInt(sequenceFrameCount / (float)sequenceColumns);
                EditorGUILayout.LabelField(" ",
                    $"Layout: {sequenceColumns} x {rows} (= {sequenceColumns * rows} cells, "
                    + $"{sequenceFrameCount} used)",
                    EditorStyles.miniLabel);

                exportIndividualFrames = EditorGUILayout.Toggle(
                    new GUIContent("Export Individual Frames",
                        "同时输出每一帧的单张 PNG（除 Sheet 外）"),
                    exportIndividualFrames);

                sequenceUseEndpoints = EditorGUILayout.Toggle(
                    new GUIContent("Include Endpoints",
                        "勾选：均匀采样含首尾帧 (t = i/(N-1) * length)，"
                        + "适合非循环动作。\n"
                        + "不勾：中心采样 (t = (i+0.5)/N * length)，"
                        + "适合循环动作。"),
                    sequenceUseEndpoints);

                EditorGUILayout.Space();
                if (GUILayout.Button(
                    new GUIContent("Diagnose Bindings",
                        "检查所有 Animation Pair 的骨骼路径匹配情况，"
                        + "用于排查骨骼动画不动的问题"),
                    GUILayout.Height(22)))
                    DiagnoseBindings();
            }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
        shadowCatcherFoldout = EditorGUILayout.Foldout(shadowCatcherFoldout,
            "Shadow Catcher", false);
        if (shadowCatcherFoldout)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                enableShadowCatcher = EditorGUILayout.Toggle(
                    new GUIContent("Enable Shadow Catcher",
                        "启用阴影捕捉模式。物体不透明，影子半透明黑色，地面不可见。\n"
                        + "需要场景里有开启阴影的方向光。"),
                    enableShadowCatcher);

                using (new EditorGUI.DisabledScope(!enableShadowCatcher))
                {
                    // Shadow Casters list
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
                            if (shadowCasterRoots.Count > 1)
                            {
                                if (GUILayout.Button("X", GUILayout.Width(22)))
                                    removeCaster = i;
                            }
                        }
                    }
                    if (removeCaster >= 0) shadowCasterRoots.RemoveAt(removeCaster);
                    // 处理 Shadow Casters 多对象拖入分发
                    if (_pendingMultiDragObjs != null && _pendingMultiDragZone == MD_ZONE_CASTER)
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

                    EditorGUILayout.Space();

                    // Shadow Receivers list
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
                            if (shadowReceivers.Count > 1)
                            {
                                if (GUILayout.Button("X", GUILayout.Width(22)))
                                    removeReceiver = i;
                            }
                        }
                    }
                    if (removeReceiver >= 0) shadowReceivers.RemoveAt(removeReceiver);
                    // 处理 Shadow Receivers 多对象拖入分发
                    if (_pendingMultiDragObjs != null && _pendingMultiDragZone == MD_ZONE_RECEIVER)
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
                        new GUIContent("Shadow Intensity", "阴影深浅倍率，1=原始"),
                        shadowIntensity, 0f, 2f);

                    shadowCatcherPPComposite = EditorGUILayout.Toggle(
                        new GUIContent("Composite Post Processing",
                            "额外渲染一 pass 带后处理的物体，自动合成到影子图上。\n"
                            + "勾选 = 物体有后处理效果 + 影子正常。"),
                        shadowCatcherPPComposite);
                }

                if (enableShadowCatcher)
                    EditorGUILayout.HelpBox("Shadow Catcher 开启后，上方透明背景/后处理设置将被忽略。"
                        + "直接将物体和面片拖入插槽即可，无需改 Layer。",
                        MessageType.Info);
            }
        }

        EditorGUILayout.EndScrollView();
        if (GUI.changed) SavePrefs();

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

    public void Capture()
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

    // === 静态辅助方法（供 QuickCapture 快捷键使用） ===

    // RenderOnce + 下采样 + Gamma 校正，一步到位
    private static Texture2D RenderAndDownsample(Camera cam, int rw, int rh,
        bool transparent, bool disablePost, bool overrideClear, Color clearColor,
        int outW, int outH, int superSize)
    {
        var tex = RenderOnce(cam, rw, rh, transparent, disablePost, overrideClear, clearColor);
        if (superSize > 1)
        {
            var resized = Downsample(tex, outW, outH);
            Object.DestroyImmediate(tex);
            tex = resized;
        }
        LinearToGamma(tex);
        return tex;
    }

    // Game 视图 RGB + B/C alpha 的完整流程
    private static Texture2D RenderWithGameView(Camera cam, int rw, int rh,
        int outW, int outH, int superSize)
    {
        var gameViewTex = CaptureGameViewRGB(cam);
        if (gameViewTex != null)
        {
            SrgbToLinear(gameViewTex);

            Texture2D rgbTex;
            if (gameViewTex.width == rw && gameViewTex.height == rh)
            {
                rgbTex = gameViewTex;
            }
            else
            {
                var tmp = RenderTexture.GetTemporary(rw, rh, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1);
                tmp.filterMode = FilterMode.Bilinear;
                Graphics.Blit(gameViewTex, tmp);
                Object.DestroyImmediate(gameViewTex);
                var prevActive = RenderTexture.active;
                RenderTexture.active = tmp;
                rgbTex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, false);
                rgbTex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
                rgbTex.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tmp);
            }

            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var finalTex = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            Object.DestroyImmediate(rgbTex);
            Object.DestroyImmediate(blackTex);
            Object.DestroyImmediate(whiteTex);

            if (superSize > 1)
            {
                var resized = Downsample(finalTex, outW, outH);
                Object.DestroyImmediate(finalTex);
                finalTex = resized;
            }
            LinearToGamma(finalTex);
            return finalTex;
        }

        // Fallback：三 pass 渲染
        var fbRgbTex = RenderOnce(cam, rw, rh, true, false, false, default);
        var fbBlackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
        var fbWhiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
        var fbTex = DualBackgroundAlpha(fbRgbTex, fbBlackTex, fbWhiteTex);
        Object.DestroyImmediate(fbRgbTex);
        Object.DestroyImmediate(fbBlackTex);
        Object.DestroyImmediate(fbWhiteTex);
        if (superSize > 1)
        {
            var resized = Downsample(fbTex, outW, outH);
            Object.DestroyImmediate(fbTex);
            fbTex = resized;
        }
        LinearToGamma(fbTex);
        return fbTex;
    }

    // 用当前场景姿态渲染一帧到 outW x outH 的 Texture2D（已应用 supersize 下采样）。
    // 序列帧捕捉时会在外层先 SampleAnimationClip 把姿态推到指定时刻，再调本方法。
    private Texture2D RenderCurrentFrame(Camera cam, int outW, int outH)
    {
        int rw = outW * superSize;
        int rh = outH * superSize;

        Texture2D finalTex;
        if (enableShadowCatcher)
        {
            return RenderCurrentFrameShadowCatcher(cam, outW, outH);
        }
        else if (!transparentBackground)
        {
            finalTex = RenderOnce(cam, rw, rh, false, false, false, default);
        }
        else if (!keepPostProcessing)
        {
            finalTex = RenderOnce(cam, rw, rh, true, true, false, default);
        }
        else
        {
            // 透明背景 + 保留后处理：优先用 Game 视图 RT 作为 RGB 源
            // 这样可以保证颜色和后处理效果跟 Game 视图看到的完全一致
            // 失败时 fallback 到三 pass RenderOnce 方案
            var gameViewTex = CaptureGameViewRGB(cam);
            if (gameViewTex != null)
            {
                // Game 视图 RGB 是 sRGB → 转到线性空间
                SrgbToLinear(gameViewTex);

                // 如果 Game 视图分辨率与目标分辨率不同，缩放 RGB 到 rw x rh
                Texture2D rgbTex;
                if (gameViewTex.width == rw && gameViewTex.height == rh)
                {
                    rgbTex = gameViewTex;
                }
                else
                {
                    var tmp = RenderTexture.GetTemporary(rw, rh, 0,
                        RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1);
                    tmp.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(gameViewTex, tmp);
                    Object.DestroyImmediate(gameViewTex);
                    var prevActive2 = RenderTexture.active;
                    RenderTexture.active = tmp;
                    rgbTex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, false);
                    rgbTex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
                    rgbTex.Apply();
                    RenderTexture.active = prevActive2;
                    RenderTexture.ReleaseTemporary(tmp);
                }

                var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
                var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
                finalTex = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
                Object.DestroyImmediate(rgbTex);
                Object.DestroyImmediate(blackTex);
                Object.DestroyImmediate(whiteTex);
            }
            else
            {
                // Fallback：原三 pass 双背景抠图
                var rgbTex   = RenderOnce(cam, rw, rh, true,  false, false, default);
                var blackTex = RenderOnce(cam, rw, rh, false, true,  true,  Color.black);
                var whiteTex = RenderOnce(cam, rw, rh, false, true,  true,  Color.white);
                finalTex = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
                Object.DestroyImmediate(rgbTex);
                Object.DestroyImmediate(blackTex);
                Object.DestroyImmediate(whiteTex);
            }
        }

        if (superSize > 1)
        {
            var resized = Downsample(finalTex, outW, outH);
            Object.DestroyImmediate(finalTex);
            finalTex = resized;
        }

        // RenderOnce 返回线性数据，PNG 输出前做 gamma 校正
        LinearToGamma(finalTex);
        return finalTex;
    }

    // 诊断所有 pair 的 clip 曲线路径在对应 target 下能否找到 Transform。
    public void DiagnoseBindings()
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
    private static void ForceSkinnedMeshRenderersRefresh()
    {
        var all = Object.FindObjectsOfType<SkinnedMeshRenderer>();
        if (all == null || all.Length == 0) return;

        // BakeMesh 需要一个临时 Mesh 容器，复用一个避免每帧分配
        var dummy = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            for (int i = 0; i < all.Length; i++)
            {
                var smr = all[i];
                if (smr == null || !smr.enabled) continue;
                if (smr.sharedMesh == null) continue;

                smr.forceMatrixRecalculationPerRender = true;
                // BakeMesh 会强制 Unity 用当前骨骼姿态做一次完整蒙皮，
                // 副作用是让 SMR 内部的"当前姿态"缓存被更新。
                // 即使我们丢弃 dummy 结果，cam.Render() 时 SMR 也会用最新姿态。
                try { smr.BakeMesh(dummy); }
                catch { /* 极少数 mesh 不支持 BakeMesh，忽略 */ }
            }
        }
        finally
        {
            Object.DestroyImmediate(dummy);
        }
    }

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

    public void CaptureSequence()
    {
        var cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null)
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
        Directory.CreateDirectory(folderAbs);
        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string framesDir = null;
        if (exportIndividualFrames)
        {
            var clipName = validPairs[0].clip.name;
            var dirName = namingMode == NamingMode.Default
                ? $"{fileNamePrefix}_{clipName}_{stamp}_frames"
                : $"{(addPrefix && !string.IsNullOrEmpty(fileNamePrefix) ? fileNamePrefix + "_" : "")}{prefabName}_{clipName}_{(addTimestamp ? stamp + "_" : "")}frames";
            framesDir = Path.Combine(folderAbs, dirName).Replace('\\', '/');
            Directory.CreateDirectory(framesDir);
        }

        bool gpuCopySupported =
            (SystemInfo.copyTextureSupport & CopyTextureSupport.TextureToRT) != 0;

        var sheetRT = RenderTexture.GetTemporary(cols * outW, rows * outH, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 1);
        sheetRT.filterMode = FilterMode.Bilinear;
        var prevActiveClear = RenderTexture.active;
        RenderTexture.active = sheetRT;
        GL.Clear(true, true, new Color(0, 0, 0, 0));
        RenderTexture.active = prevActiveClear;

        Texture2D cpuSheet = null;
        if (!gpuCopySupported)
        {
            cpuSheet = new Texture2D(cols * outW, rows * outH,
                TextureFormat.RGBA32, false, false);
            var clear = new Color32[cpuSheet.width * cpuSheet.height];
            cpuSheet.SetPixels32(clear);
        }

        // 禁用所有涉及 target 上的 Animator / Animation（去重）
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
        bool canceled = false;
        string sheetPath = null;

        try
        {
            AnimationMode.StartAnimationMode();
            started = true;

            for (int i = 0; i < frameCount; i++)
            {
                if (EditorUtility.DisplayCancelableProgressBar(
                    "Capturing Sequence",
                    $"Frame {i + 1}/{frameCount}  ({validPairs.Count} pairs)",
                    (i + 1) / (float)frameCount))
                {
                    canceled = true;
                    break;
                }

                float t;
                if (sequenceUseEndpoints && frameCount > 1)
                    t = i / (float)(frameCount - 1) * masterLength;
                else
                    t = (i + 0.5f) / frameCount * masterLength;

                // 对所有 pair 依次采样
                AnimationMode.BeginSampling();
                foreach (var pair in validPairs)
                {
                    AnimationMode.SampleAnimationClip(pair.target, pair.clip, t);
                }
                AnimationMode.EndSampling();

                ForceSkinnedMeshRenderersRefresh();

                var frameTex = RenderCurrentFrame(cam, outW, outH);
                try
                {
                    int col = i % cols;
                    int row = i / cols;
                    int dstX = col * outW;
                    int dstY = (rows - 1 - row) * outH;
                    if (gpuCopySupported)
                    {
                        Graphics.CopyTexture(frameTex, 0, 0, 0, 0, outW, outH,
                            sheetRT, 0, 0, dstX, dstY);
                    }
                    else
                    {
                        var px = frameTex.GetPixels32();
                        cpuSheet.SetPixels32(dstX, dstY, outW, outH, px);
                    }

                    if (exportIndividualFrames)
                    {
                        var fbytes = frameTex.EncodeToPNG();
                        var fname = BuildSequenceFrameName(prefabName,
                            validPairs[0].clip.name, i);
                        var fpath = Path.Combine(framesDir, fname).Replace('\\', '/');
                        File.WriteAllBytes(fpath, fbytes);
                    }
                }
                finally
                {
                    Object.DestroyImmediate(frameTex);
                }
            }

            if (!canceled)
            {
                Texture2D sheetTex;
                if (gpuCopySupported)
                {
                    var prevActive2 = RenderTexture.active;
                    RenderTexture.active = sheetRT;
                    sheetTex = new Texture2D(sheetRT.width, sheetRT.height,
                        TextureFormat.RGBA32, false, false);
                    sheetTex.ReadPixels(new Rect(0, 0, sheetRT.width, sheetRT.height), 0, 0, false);
                    sheetTex.Apply();
                    RenderTexture.active = prevActive2;
                }
                else
                {
                    cpuSheet.Apply();
                    sheetTex = cpuSheet;
                    cpuSheet = null;
                }

                var bytes = sheetTex.EncodeToPNG();
                Object.DestroyImmediate(sheetTex);

                var sname = BuildSequenceSheetName(prefabName,
                    validPairs[0].clip.name, stamp, cols, rows, outW, outH);
                sheetPath = Path.Combine(folderAbs, sname).Replace('\\', '/');
                File.WriteAllBytes(sheetPath, bytes);

                Debug.Log($"<color=#7fcf6f>[Screenshot]</color> Sheet saved: {sheetPath} "
                        + $"(layout {cols}x{rows}, frame {outW}x{outH}, {validPairs.Count} pairs)");
            }
            else
            {
                Debug.Log("[Screenshot] Sequence canceled.");
            }
        }
        finally
        {
            if (started) AnimationMode.StopAnimationMode();
            EditorUtility.ClearProgressBar();
            RenderTexture.ReleaseTemporary(sheetRT);
            if (cpuSheet != null) Object.DestroyImmediate(cpuSheet);

            // 恢复所有 Animator / Animation 状态
            foreach (var kvp in disabledAnimators)
                if (kvp.Key != null) kvp.Key.enabled = kvp.Value;
            foreach (var kvp in disabledAnimations)
                if (kvp.Key != null) kvp.Key.enabled = kvp.Value;

            if (!canceled && sheetPath != null)
            {
                var projectRoot = Directory.GetParent(Application.dataPath)
                                    .FullName.Replace('\\', '/');
                if (sheetPath.StartsWith(projectRoot)) AssetDatabase.Refresh();
                if (openFolderAfterSave) EditorUtility.RevealInFinder(sheetPath);
            }
        }
    }

    private static Texture2D RenderOnce(Camera cam, int rw, int rh,
        bool transparent, bool disablePost,
        bool overrideClear, Color clearColor,
        int cullingMask = -1)
    {
        var prevClearFlags = cam.clearFlags;
        var prevBg         = cam.backgroundColor;
        var prevTarget     = cam.targetTexture;
        var prevCulling    = cam.cullingMask;

#if UNITY_2019_3_OR_NEWER
        UniversalAdditionalCameraData urpData = null;
        bool prevPost = false;
        cam.TryGetComponent(out urpData);
        if (urpData != null)
        {
            prevPost = urpData.renderPostProcessing;
            if (disablePost) urpData.renderPostProcessing = false;
        }
#endif

        // 相机 HDR 开启时用 ARGBHalf 保留 HDR 数据，避免亮部截断导致 Bloom 失效
        var rtFormat = cam.allowHDR
            ? RenderTextureFormat.ARGBHalf
            : RenderTextureFormat.ARGB32;

        // MSAA 级别跟随相机设置，保持和 Game 视图一致
        int msaaSamples = cam.allowMSAA ? Mathf.Max(1, QualitySettings.antiAliasing) : 1;

        var rt = RenderTexture.GetTemporary(rw, rh, 24,
            rtFormat, RenderTextureReadWrite.Linear, msaaSamples);

        try
        {
            if (cullingMask >= 0) cam.cullingMask = cullingMask;
            if (transparent)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
            }
            else if (overrideClear)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = clearColor;
            }

            cam.targetTexture = rt;
            cam.Render();

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            // linear=true: prevent Unity 2022+ auto sRGB conversion on ReadPixels
            // from a Linear RT, so LinearToGamma is the only gamma application.
            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
            tex.Apply();
            RenderTexture.active = prevActive;
            return tex;
        }
        finally
        {
            cam.targetTexture = prevTarget;
            cam.clearFlags    = prevClearFlags;
            cam.backgroundColor = prevBg;
            cam.cullingMask   = prevCulling;
#if UNITY_2019_3_OR_NEWER
            if (urpData != null) urpData.renderPostProcessing = prevPost;
#endif
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // 双背景抠图：对每个像素用黑底/白底两张图反推 alpha
    //   黑底:  black = src.rgb * a + 0 * (1-a) = src.rgb * a
    //   白底:  white = src.rgb * a + 1 * (1-a) = src.rgb * a + (1-a)
    //   相减:  white - black = 1 - a   =>   a = 1 - mean(white - black)
    // RGB 用 PASS A（透明背景渲染结果，已含后处理），它对实心和半透像素都给出
    // 正确的"显示在透明上的最终颜色"。完全透明的像素 a≈0，其 RGB 不影响 PNG 显示。
    private static Texture2D DualBackgroundAlpha(Texture2D rgbTex,
        Texture2D blackTex, Texture2D whiteTex)
    {
        if (rgbTex.width != blackTex.width || rgbTex.height != blackTex.height ||
            rgbTex.width != whiteTex.width || rgbTex.height != whiteTex.height)
        {
            Debug.LogError("[Screenshot] Pass size mismatch.");
            return rgbTex;
        }

        int w = rgbTex.width, h = rgbTex.height;
        var rgb = rgbTex.GetPixels32();
        var b   = blackTex.GetPixels32();
        var wt  = whiteTex.GetPixels32();

        // 阈值：白-黑差异在此之下视为完全实心（避免噪点把实心像素错判成半透）
        const int SOLID_EPS = 4;     // 0~255
        // 完全透明的判定：alpha 极低
        const int CLEAR_EPS = 2;

        for (int i = 0; i < rgb.Length; i++)
        {
            // 三通道取均值更稳，单通道遇到纯色物体会偏
            int dr = wt[i].r - b[i].r;
            int dg = wt[i].g - b[i].g;
            int db = wt[i].b - b[i].b;
            // dr/dg/db 理论上 >=0；负值当噪点处理
            if (dr < 0) dr = 0; if (dg < 0) dg = 0; if (db < 0) db = 0;
            int diff = (dr + dg + db) / 3;

            byte alpha;
            if (diff <= SOLID_EPS)         alpha = 255;
            else if (diff >= 255 - CLEAR_EPS) alpha = 0;
            else                            alpha = (byte)(255 - diff);

            rgb[i] = new Color32(rgb[i].r, rgb[i].g, rgb[i].b, alpha);
        }

        var result = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        result.SetPixels32(rgb);
        result.Apply();
        return result;
    }

    // 将 Texture2D 的 RGB 通道从线性空间转换到 sRGB 空间（Gamma 校正）。
    // 所有 RenderOnce 返回的纹理数据都是线性的，PNG 输出前必须过一遍此方法。
    private static void LinearToGamma(Texture2D tex)
    {
        var px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = px[i].gamma;
        tex.SetPixels(px);
        tex.Apply();
    }

    // sRGB → Linear 逆 gamma 校正
    private static void SrgbToLinear(Texture2D tex)
    {
        var px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = px[i].linear;
        tex.SetPixels(px);
        tex.Apply();
    }

    // 通过反射获取 Game 视图的内部 RenderTexture
    private static RenderTexture GetGameViewRT()
    {
        var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
        if (gameViewType == null) return null;

        var getMainMethod = gameViewType.GetMethod("GetMainGameView",
            BindingFlags.NonPublic | BindingFlags.Static);
        EditorWindow gameView = null;
        if (getMainMethod != null)
            gameView = getMainMethod.Invoke(null, null) as EditorWindow;
        if (gameView == null)
            gameView = EditorWindow.GetWindow(gameViewType);
        if (gameView == null) return null;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;

        // 先尝试 PlayModeView.m_TargetTexture（基类，更稳定）
        var baseType = gameViewType.BaseType;
        if (baseType != null)
        {
            var field = baseType.GetField("m_TargetTexture", flags);
            if (field != null)
            {
                var rt = field.GetValue(gameView) as RenderTexture;
                if (rt != null && rt.IsCreated() && rt.width > 0)
                    return rt;
            }
        }

        // 再尝试 GameView 自己的字段
        foreach (var name in new[] { "m_RenderTexture", "m_TargetTexture", "m_Target" })
        {
            var field = gameViewType.GetField(name, flags);
            if (field != null)
            {
                var rt = field.GetValue(gameView) as RenderTexture;
                if (rt != null && rt.IsCreated() && rt.width > 0)
                    return rt;
            }
        }

        return null;
    }

    // 将相机渲染到 Game 视图的内部 RT 并捕获 RGB（sRGB 空间）。
    // 失败时返回 null，调用方应 fallback 到原方案。
    private static Texture2D CaptureGameViewRGB(Camera cam)
    {
        var gameViewRT = GetGameViewRT();
        if (gameViewRT == null) return null;

        var prevTarget = cam.targetTexture;
        cam.targetTexture = gameViewRT;
        cam.Render();
        cam.targetTexture = prevTarget;

        var prevActive = RenderTexture.active;
        RenderTexture.active = gameViewRT;
        var tex = new Texture2D(gameViewRT.width, gameViewRT.height,
            TextureFormat.RGBA32, false, false);
        tex.ReadPixels(new Rect(0, 0, gameViewRT.width, gameViewRT.height), 0, 0, false);
        tex.Apply();
        RenderTexture.active = prevActive;
        return tex;
    }

    private static Texture2D Downsample(Texture2D src, int dstW, int dstH)
    {
        var tmp = RenderTexture.GetTemporary(dstW, dstH, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, 1);
        tmp.filterMode = FilterMode.Bilinear;
        var prev = RenderTexture.active;
        Graphics.Blit(src, tmp);
        RenderTexture.active = tmp;
        var result = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false, false);
        result.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0, false);
        result.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(tmp);
        return result;
    }

    // ---- Shadow Catcher ----

    /// <summary>
    /// Render a normal transparent screenshot at the given resolution and return it
    /// in LINEAR color space (no gamma correction applied).
    /// Uses the same GameView-capture + dual-background-alpha pipeline as the normal mode.
    /// </summary>
    private Texture2D RenderNormalTransparentLinear(Camera cam, int rw, int rh)
    {
        var gameViewTex = CaptureGameViewRGB(cam);
        if (gameViewTex != null)
        {
            // GameView capture is sRGB → convert to linear
            SrgbToLinear(gameViewTex);

            // Resize to target resolution if needed
            Texture2D rgbTex;
            if (gameViewTex.width == rw && gameViewTex.height == rh)
            {
                rgbTex = gameViewTex;
            }
            else
            {
                var tmp = RenderTexture.GetTemporary(rw, rh, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear, 1);
                tmp.filterMode = FilterMode.Bilinear;
                Graphics.Blit(gameViewTex, tmp);
                Object.DestroyImmediate(gameViewTex);
                var prevActive = RenderTexture.active;
                RenderTexture.active = tmp;
                rgbTex = new Texture2D(rw, rh, TextureFormat.RGBA32, false, true);
                rgbTex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
                rgbTex.Apply();
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(tmp);
            }

            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var result = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            Object.DestroyImmediate(rgbTex);
            Object.DestroyImmediate(blackTex);
            Object.DestroyImmediate(whiteTex);
            return result;
        }
        else
        {
            // Fallback: 3-pass dual-background matting
            var rgbTex  = RenderOnce(cam, rw, rh, true, false, false, default);
            var blackTex = RenderOnce(cam, rw, rh, false, true, true, Color.black);
            var whiteTex = RenderOnce(cam, rw, rh, false, true, true, Color.white);
            var result = DualBackgroundAlpha(rgbTex, blackTex, whiteTex);
            Object.DestroyImmediate(rgbTex);
            Object.DestroyImmediate(blackTex);
            Object.DestroyImmediate(whiteTex);
            return result;
        }
    }

    private Texture2D RenderCurrentFrameShadowCatcher(Camera cam, int outW, int outH)
    {
        int rw = outW * superSize;
        int rh = outH * superSize;

        var casterRenderers = GetRenderersFromList(shadowCasterRoots);
        var receiverRenderers = GetRenderersFromList(shadowReceivers);

        var casterEnabled = new bool[casterRenderers.Length];
        var receiverEnabled = new bool[receiverRenderers.Length];
        var receiverRecvShadow = new bool[receiverRenderers.Length];
        for (int i = 0; i < casterRenderers.Length; i++)
            casterEnabled[i] = casterRenderers[i].enabled;
        for (int i = 0; i < receiverRenderers.Length; i++)
        {
            receiverEnabled[i] = receiverRenderers[i].enabled;
            receiverRecvShadow[i] = receiverRenderers[i].receiveShadows;
        }

        try
        {
            // 1. Normal transparent screenshot — correct Game-view object colors (linear)
            var normalTex = RenderNormalTransparentLinear(cam, rw, rh);

            // 2. Object mask (receivers hidden, no PP)
            SetRenderersEnabled(receiverRenderers, false);
            var objectTex = RenderOnce(cam, rw, rh, true, true, false, default);
            SetRenderersEnabled(receiverRenderers, true);

            // 3. Ground with shadows
            var fullTex = RenderOnce(cam, rw, rh, true, true, false, default);

            // 4. Ground without shadows
            SetRenderersEnabled(casterRenderers, false);
            SetReceiveShadows(receiverRenderers, false);
            var groundNoShadowTex = RenderOnce(cam, rw, rh, true, true, false, default);
            SetReceiveShadows(receiverRenderers, true);
            SetRenderersEnabled(casterRenderers, true);

            // 5. Composite: objects from normalTex, shadows from ground diff
            var finalTex = CompositeShadowWithNormalColors(
                objectTex, fullTex, groundNoShadowTex, normalTex);

            Object.DestroyImmediate(objectTex);
            Object.DestroyImmediate(fullTex);
            Object.DestroyImmediate(groundNoShadowTex);
            Object.DestroyImmediate(normalTex);

            if (superSize > 1)
            {
                var resized = Downsample(finalTex, outW, outH);
                Object.DestroyImmediate(finalTex);
                finalTex = resized;
            }
            LinearToGamma(finalTex);
            return finalTex;
        }
        finally
        {
            for (int i = 0; i < casterRenderers.Length; i++)
                if (casterRenderers[i] != null)
                    casterRenderers[i].enabled = casterEnabled[i];
            for (int i = 0; i < receiverRenderers.Length; i++)
            {
                if (receiverRenderers[i] != null)
                {
                    receiverRenderers[i].enabled = receiverEnabled[i];
                    receiverRenderers[i].receiveShadows = receiverRecvShadow[i];
                }
            }
        }
    }

    private static Renderer[] GetRenderersFromList(
        List<GameObject> roots)
    {
        if (roots == null || roots.Count == 0) return new Renderer[0];
        var list = new List<Renderer>();
        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] != null)
            {
                var r = roots[i].GetComponentsInChildren<Renderer>();
                if (r != null && r.Length > 0) list.AddRange(r);
            }
        }
        return list.ToArray();
    }

    private static void SetRenderersEnabled(Renderer[] renderers, bool enabled)
    {
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].enabled = enabled;
    }

    private static void SetReceiveShadows(Renderer[] renderers, bool enabled)
    {
        for (int i = 0; i < renderers.Length; i++)
            if (renderers[i] != null)
                renderers[i].receiveShadows = enabled;
    }

    /// <summary>
    /// Composite: objects from normalTransparent render (correct Game-view colors),
    /// shadows extracted from ground passes underneath.
    /// All inputs are in LINEAR color space.
    /// </summary>
    private Texture2D CompositeShadowWithNormalColors(
        Texture2D objectTex, Texture2D fullTex,
        Texture2D groundNoShadowTex, Texture2D normalTex)
    {
        int w = objectTex.width, h = objectTex.height;
        var objPx  = objectTex.GetPixels32();
        var fullPx = fullTex.GetPixels32();
        var gndPx  = groundNoShadowTex.GetPixels32();
        var normalPx = normalTex.GetPixels32();

        const int OBJ_ALPHA_THRESH = 8;
        const int GND_ALPHA_THRESH = 8;

        for (int i = 0; i < objPx.Length; i++)
        {
            if (objPx[i].a >= OBJ_ALPHA_THRESH)
            {
                // Object pixel: use normal transparent screenshot color (Game-view accurate)
                normalPx[i].a = 255;
            }
            else if (gndPx[i].a >= GND_ALPHA_THRESH)
            {
                // Ground pixel: compute shadow alpha from luminance difference
                int lumC = (gndPx[i].r + gndPx[i].g + gndPx[i].b) / 3;
                int lumB = (fullPx[i].r + fullPx[i].g + fullPx[i].b) / 3;
                int diff = lumC - lumB;
                if (diff < 0) diff = 0;
                float ratio = lumC > 0 ? (float)diff / lumC : 0f;
                int a = Mathf.RoundToInt(Mathf.Clamp01(ratio * shadowIntensity) * 255);
                if (a > 255) a = 255;
                normalPx[i] = new Color32(0, 0, 0, (byte)a);
            }
            else
            {
                // Background: transparent
                normalPx[i] = new Color32(0, 0, 0, 0);
            }
        }

        var result = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        result.SetPixels32(normalPx);
        result.Apply();
        return result;
    }

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
        keepPostProcessing = EditorPrefs.GetBool(PREF_PREFIX + "kpp", true);
        outputFolder = EditorPrefs.GetString(PREF_PREFIX + "folder", "Screenshots");
        fileNamePrefix = EditorPrefs.GetString(PREF_PREFIX + "prefix", "shot");
        openFolderAfterSave = EditorPrefs.GetBool(PREF_PREFIX + "open", true);
        useGameViewSize = EditorPrefs.GetBool(PREF_PREFIX + "gvs", false);
        namingMode = (NamingMode)EditorPrefs.GetInt(PREF_PREFIX + "nm", 0);
        addPrefix = EditorPrefs.GetBool(PREF_PREFIX + "ap", true);
        addTimestamp = EditorPrefs.GetBool(PREF_PREFIX + "ats", true);
        addResolution = EditorPrefs.GetBool(PREF_PREFIX + "ar", true);
        sequenceFrameCount = EditorPrefs.GetInt(PREF_PREFIX + "seqN", 64);
        sequenceColumns = EditorPrefs.GetInt(PREF_PREFIX + "seqC", 8);
        exportIndividualFrames = EditorPrefs.GetBool(PREF_PREFIX + "seqInd", false);
        sequenceUseEndpoints = EditorPrefs.GetBool(PREF_PREFIX + "seqEnd", false);
        enableShadowCatcher = EditorPrefs.GetBool(PREF_PREFIX + "scEn", false);
        shadowIntensity = EditorPrefs.GetFloat(PREF_PREFIX + "scInt", 1f);
        shadowCatcherPPComposite = EditorPrefs.GetBool(PREF_PREFIX + "scPP", true);
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
        EditorPrefs.SetBool(PREF_PREFIX + "kpp", keepPostProcessing);
        EditorPrefs.SetString(PREF_PREFIX + "folder", outputFolder ?? "");
        EditorPrefs.SetString(PREF_PREFIX + "prefix", fileNamePrefix ?? "shot");
        EditorPrefs.SetBool(PREF_PREFIX + "open", openFolderAfterSave);
        EditorPrefs.SetBool(PREF_PREFIX + "gvs", useGameViewSize);
        EditorPrefs.SetInt(PREF_PREFIX + "nm", (int)namingMode);
        EditorPrefs.SetBool(PREF_PREFIX + "ap", addPrefix);
        EditorPrefs.SetBool(PREF_PREFIX + "ats", addTimestamp);
        EditorPrefs.SetBool(PREF_PREFIX + "ar", addResolution);
        EditorPrefs.SetInt(PREF_PREFIX + "seqN", sequenceFrameCount);
        EditorPrefs.SetInt(PREF_PREFIX + "seqC", sequenceColumns);
        EditorPrefs.SetBool(PREF_PREFIX + "seqInd", exportIndividualFrames);
        EditorPrefs.SetBool(PREF_PREFIX + "seqEnd", sequenceUseEndpoints);
        EditorPrefs.SetBool(PREF_PREFIX + "scEn", enableShadowCatcher);
        EditorPrefs.SetFloat(PREF_PREFIX + "scInt", shadowIntensity);
        EditorPrefs.SetBool(PREF_PREFIX + "scPP", shadowCatcherPPComposite);
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
