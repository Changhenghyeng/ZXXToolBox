using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;

namespace SelfShadowBakerTool.Editor
{
    /// <summary>
    /// 自阴影烘焙窗口。将固定太阳方向的自阴影遮罩烘焙到模型 uv2 图集，配合 Lit_BakedOcclusion 着色器使用。
    /// 光照方向需与场景主光源一致，否则阴影对不齐。
    /// </summary>
    public class SelfShadowBakerWindow : EditorWindow
    {
        private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

        // ---- 1. Source ----
        List<GameObject> _targets = new List<GameObject>();

        // ---- 2. Light ----
        enum LightMode { SceneMainLight, ManualLight, EulerAngles, DirectionVector }
        LightMode _lightMode = LightMode.SceneMainLight;
        Light _manualLight;
        Vector3 _lightEuler = new Vector3(50f, -30f, 0f);
        Vector3 _lightDir = new Vector3(0.3f, -1f, 0.2f);
        bool _previewInScene = true;

        // ---- 3. Bake settings ----
        int _resolution = 1024;
        int _shadowRes = 2048;
        readonly int[] _resChoices = { 256, 512, 1024, 2048, 4096 };
        float _pcfRadius = 1.5f;
        int _pcfTaps = 5;
        float _depthBias = 3.0f;
        float _normalBias = 3.0f;
        int _dilate = 4;
        bool _flipV = false;
        bool _invert = false;

        // ---- 3b. AO (G channel) ----
        int _aoSamples = 64;
        readonly int[] _aoSampleChoices = { 16, 32, 64, 128, 256 };
        int _aoDepthRes = 1024;
        float _aoMaxDistance = 0f; // 0 = auto
        float _aoLightBias = 0f;
        float _aoPower = 1.0f;

        // ---- 3c. Fill / reflected light (B channel) ----
        Vector3 _fillOffset = Vector3.zero;

        // ---- 4. Output ----
        string _outputFolder = "Assets/BakedSelfShadows";

        // ---- results / preview ----
        class PreviewItem { public string name; public Texture2D tex; public string info; }
        readonly List<PreviewItem> _previews = new List<PreviewItem>();

        Vector2 _scroll;
        bool _sceneHooked;

        public static void Open()
        {
            var w = GetWindow<SelfShadowBakerWindow>("Self Shadow Baker");
            w.minSize = new Vector2(400, 620);
            w.Show();
        }

        void OnEnable()
        {
            LoadSettings();
            if (_targets.Count == 0) FillFromSelection();
            HookSceneGui(_previewInScene);
        }

        void OnDisable()
        {
            SaveSettings();
            HookSceneGui(false);
        }

        void OnDestroy() => HookSceneGui(false);

        void HookSceneGui(bool on)
        {
            if (on && !_sceneHooked) { SceneView.duringSceneGui += OnSceneGui; _sceneHooked = true; }
            else if (!on && _sceneHooked) { SceneView.duringSceneGui -= OnSceneGui; _sceneHooked = false; }
        }

        // =====================================================================
        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Self Shadow Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "把自投影阴影(R)和 AO(G)烘进 uv2(lightmap UV)，供 Lit_BakedOcclusion 使用。\n" +
                "• R=自阴影(1=受光)，G=AO(1=开阔)，写入 _Baked_Map。\n" +
                "• R/G 可分开烘、互不覆盖。太阳方向需与场景实时主光一致。",
                MessageType.None);

            DrawSource();
            EditorGUILayout.Space(6); DrawLight();
            EditorGUILayout.Space(6); DrawBakeSettings();
            EditorGUILayout.Space(6); DrawAoSettings();
            EditorGUILayout.Space(6); DrawFillSettings();
            EditorGUILayout.Space(6); DrawOutput();
            EditorGUILayout.Space(10); DrawActions();
            if (_previews.Count > 0) { EditorGUILayout.Space(8); DrawPreviews(); }

            EditorGUILayout.EndScrollView();

            var authorStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.6f) }
            };
            EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
            EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
        }

        // ---------------------------------------------------------- Source
        void DrawSource()
        {
            EditorGUILayout.LabelField("1. Source", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Content("Fill From Selection", "从选中项填入"))) FillFromSelection();
                if (GUILayout.Button(Content("Clear", "清空"), GUILayout.Width(60))) _targets.Clear();
            }

            int removeIdx = -1;
            for (int i = 0; i < _targets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _targets[i] = (GameObject)EditorGUILayout.ObjectField(_targets[i], typeof(GameObject), true);
                    if (GUILayout.Button("-", GUILayout.Width(22))) removeIdx = i;
                }
            }
            if (removeIdx >= 0) _targets.RemoveAt(removeIdx);
            if (GUILayout.Button(Content("+ Add", "添加"))) _targets.Add(null);

            int valid = 0;
            var disabledModels = new List<string>(); // "FBX名 ← 目标名"
            foreach (var t in _targets)
            {
                if (t == null) continue;
                valid++;
                disabledModels.AddRange(GetModelsWithLightmapUVOff(t));
            }

            string statusSuffix = disabledModels.Count > 0
                ? $"   ⚠ {disabledModels.Count} model(s) missing Lightmap UV"
                : "   ✓ All Lightmap UV enabled";
            EditorGUILayout.LabelField(Content($"Valid Targets: {valid}{statusSuffix}", "有效目标统计"), EditorStyles.miniLabel);

            if (disabledModels.Count > 0)
            {
                disabledModels.Sort();
                EditorGUILayout.HelpBox(
                    "以下模型的「生成灯光贴图 UV」未勾选：\n• " +
                    string.Join("\n• ", disabledModels),
                    MessageType.Warning);
                if (GUILayout.Button(Content("Generate Lightmap UVs", "生成灯光贴图 UV")))
                {
                    int n = FixLightmapUVImport(_targets);
                    EditorUtility.DisplayDialog("完成",
                        $"已为 {n} 个模型生成灯光贴图 UV。", "OK");
                    AssetDatabase.Refresh();
                }
            }
        }

        void FillFromSelection()
        {
            _targets.Clear();
            foreach (var go in Selection.gameObjects)
                if (go != null) _targets.Add(go);
            if (_targets.Count == 0)
                Debug.Log("[SelfShadowBaker] 未选中物体，请手动拖入或先选中再点「从选中项填入」。");
        }

        static bool HasAnyUv2(GameObject go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && mf.sharedMesh.uv2 != null && mf.sharedMesh.uv2.Length > 0)
                    return true;
            return false;
        }

        /// <summary>收集未开启 generateSecondaryUV 的模型资源名（含目标名）。</summary>
        static List<string> GetModelsWithLightmapUVOff(GameObject go)
        {
            var result = new List<string>();
            var seen = new HashSet<string>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;

                string path = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer != null && !importer.generateSecondaryUV)
                    result.Add($"{Path.GetFileName(path)}  ← {go.name}");
            }
            return result;
        }

        /// <summary>为目标下未开启 generateSecondaryUV 的 ModelImporter 开启该选项，返回修改数量。</summary>
        static int FixLightmapUVImport(List<GameObject> targets)
        {
            var changed = new HashSet<string>();
            foreach (var go in targets)
            {
                if (go == null) continue;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;

                    string path = AssetDatabase.GetAssetPath(mesh);
                    if (string.IsNullOrEmpty(path) || !changed.Add(path)) continue;

                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer != null && !importer.generateSecondaryUV)
                    {
                        importer.generateSecondaryUV = true;
                        importer.SaveAndReimport();
                    }
                }
            }
            return changed.Count;
        }

        // ---------------------------------------------------------- Light
        void DrawLight()
        {
            EditorGUILayout.LabelField("2. Light (Sun Direction)", EditorStyles.boldLabel);
            _lightMode = (LightMode)EditorGUILayout.EnumPopup("Source Mode", _lightMode);

            switch (_lightMode)
            {
                case LightMode.SceneMainLight:
                    var main = FindSceneMainLight();
                    EditorGUILayout.LabelField(Content("Main Light", "主光"),
                        main != null ? main.name : "(No directional light found, using default direction)");
                    break;
                case LightMode.ManualLight:
                    _manualLight = (Light)EditorGUILayout.ObjectField("Light", _manualLight, typeof(Light), true);
                    if (GUILayout.Button(Content("Grab Scene Main Light", "抓取场景主光"))) _manualLight = FindSceneMainLight();
                    break;
                case LightMode.EulerAngles:
                    _lightEuler = EditorGUILayout.Vector3Field("Euler (X=pitch, Y=yaw)", _lightEuler);
                    break;
                case LightMode.DirectionVector:
                    _lightDir = EditorGUILayout.Vector3Field("Direction (world)", _lightDir);
                    EditorGUILayout.HelpBox("从太阳指向地面，Y 应为负。", MessageType.None);
                    break;
            }

            EditorGUILayout.LabelField("→ Direction", ResolveLightDirection().ToString("F3"), EditorStyles.miniLabel);

            bool prev = _previewInScene;
            _previewInScene = EditorGUILayout.ToggleLeft(Content("Preview light range in Scene View (Yellow=Direction, Orange=Coverage box)", "在场景中预览光照范围（黄=方向, 橙=覆盖框）"), _previewInScene);
            if (_previewInScene != prev) { HookSceneGui(_previewInScene); SceneView.RepaintAll(); }
        }

        static Light FindSceneMainLight()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            Light best = null; float bestI = -1f;
            foreach (var l in Object.FindObjectsOfType<Light>())
            {
                if (l.type != LightType.Directional || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                if (l.intensity > bestI) { bestI = l.intensity; best = l; }
            }
            return best;
        }

        Vector3 ResolveLightDirection()
        {
            switch (_lightMode)
            {
                case LightMode.SceneMainLight:
                    var main = FindSceneMainLight();
                    if (main != null) return main.transform.forward;
                    break;
                case LightMode.ManualLight:
                    if (_manualLight != null) return _manualLight.transform.forward;
                    break;
                case LightMode.EulerAngles:
                    return Quaternion.Euler(_lightEuler) * Vector3.forward;
                case LightMode.DirectionVector:
                    return _lightDir.sqrMagnitude < 1e-6f ? Vector3.down : _lightDir.normalized;
            }
            return new Vector3(0.3f, -1f, 0.2f).normalized;
        }

        // ---------------------------------------------------------- Bake settings
        void DrawBakeSettings()
        {
            EditorGUILayout.LabelField("3. Self-Shadow (R) + Common Params", EditorStyles.boldLabel);

            _resolution = ResPopup("Bake Resolution (uv2)", _resolution);
            _shadowRes = ResPopup("Shadow Map Resolution", _shadowRes);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Content("Soft Edges (PCF)", "软边 (PCF)"), EditorStyles.miniBoldLabel);
            _pcfRadius = EditorGUILayout.Slider("PCF Radius (texel)", _pcfRadius, 0f, 8f);
            _pcfTaps = EditorGUILayout.IntPopup("PCF Taps", _pcfTaps,
                new[] { "1x1 (Hard)", "3x3", "5x5", "7x7", "9x9" },
                new[] { 1, 3, 5, 7, 9 });

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Content("Anti-Acne (bias, unit=shadow map texel)", "去 Acne (bias, 单位=阴影贴图纹素)"), EditorStyles.miniBoldLabel);
            _depthBias = EditorGUILayout.Slider("Depth Bias", _depthBias, 0f, 16f);
            _normalBias = EditorGUILayout.Slider("Normal Bias", _normalBias, 0f, 16f);
            EditorGUILayout.HelpBox(
                "细密结构(如屋顶瓦片)自遮挡出现灰色/噪点 → 调大 Normal Bias(本例~8) 或 Depth Bias 跳过小高差。\n" +
                "阴影与物体脱开(peter-panning) → 调小。bias 按纹素自动缩放，换分辨率/模型大小一般不用重调。",
                MessageType.None);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField(Content("Finishing", "收尾"), EditorStyles.miniBoldLabel);
            _dilate = EditorGUILayout.IntSlider("Seam Dilation (px)", _dilate, 0, 16);
            _invert = EditorGUILayout.Toggle(Content("Invert R (1=Shadow)", "Invert R (1=阴影)"), _invert);
            _flipV = EditorGUILayout.Toggle(Content("Flip V (UV Flip)", "Flip V (展开翻转)"), _flipV);
            EditorGUILayout.HelpBox("R 通道存自阴影、G/B=1（匹配 _Baked_Map）。朝向已在 shader 内按平台适配，Flip V 常态保持关闭；仅个别模型上下镜像时才勾。",
                MessageType.None);
        }

        int ResPopup(string label, int cur)
        {
            int idx = System.Array.IndexOf(_resChoices, cur);
            if (idx < 0) idx = 2;
            var labels = new string[_resChoices.Length];
            for (int i = 0; i < _resChoices.Length; i++) labels[i] = _resChoices[i].ToString();
            idx = EditorGUILayout.Popup(label, idx, labels);
            return _resChoices[idx];
        }

        // ---------------------------------------------------------- AO
        void DrawAoSettings()
        {
            EditorGUILayout.LabelField("3b. Ambient Occlusion (G Channel)", EditorStyles.boldLabel);
            _aoSamples = EditorGUILayout.IntPopup("AO Samples", _aoSamples,
                new[] { "16 (Fast/Rough)", "32", "64", "128", "256 (Slow/Fine)" }, _aoSampleChoices);
            _aoDepthRes = ResPopup("AO Depth Res", _aoDepthRes);
            _aoMaxDistance = Mathf.Max(0f, EditorGUILayout.FloatField(Content("Max Distance (0=Auto)", "Max Distance (0=自动)"), _aoMaxDistance));
            _aoLightBias = EditorGUILayout.Slider(Content("Light Bias (0=Ambient 1=Directional)", "Light Bias (0=环境 1=偏光)"), _aoLightBias, 0f, 1f);
            _aoPower = EditorGUILayout.Slider(Content("Power (Contrast)", "Power (对比)"), _aoPower, 0.2f, 4f);
            EditorGUILayout.HelpBox(
                "局部 AO：只统计 Max Distance 内的遮挡(0=按模型自动)。Light Bias 让 AO 偏向太阳方向(0=普通环境 AO)。\n" +
                "结果写入 G 通道、不动 R。采样越多越细但越慢。",
                MessageType.None);
        }

        // ---------------------------------------------------------- Fill (reflected light)
        void DrawFillSettings()
        {
            EditorGUILayout.LabelField("3c. Fill / Reflected Light (B Channel)", EditorStyles.boldLabel);
            _fillOffset = EditorGUILayout.Vector3Field(Content("Fill Direction Offset (added to -Sun)", "Fill 方向偏移 (加到 -太阳)"), _fillOffset);

            Vector3 fillDir = -ResolveLightDirection() + _fillOffset;
            if (fillDir.sqrMagnitude < 1e-6f) fillDir = -ResolveLightDirection();
            fillDir.Normalize();
            EditorGUILayout.LabelField("→ Fill Direction", fillDir.ToString("F3"), EditorStyles.miniLabel);

            EditorGUILayout.HelpBox(
                "把暗部反光的「可见性」烘进 B：补光方向 = -太阳 + 偏移(往下压做地面回弹味)。\n" +
                "B 只存“反光能不能照到”(被自身挡住的夹缝自动压暗)；亮度和朝向在 shader 里用 _FillColor 和 Fill Direction 实时算。\n" +
                "把上面这条 → Fill Direction 填进材质的 _FillDir 属性。",
                MessageType.None);
        }

        // ---------------------------------------------------------- Output
        void DrawOutput()
        {
            EditorGUILayout.LabelField("4. Output", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField("Folder", _outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("选择输出目录", _outputFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string projectAssets = Application.dataPath.Replace('\\', '/');
                        string p = picked.Replace('\\', '/');
                        if (p.StartsWith(projectAssets))
                            _outputFolder = "Assets" + p.Substring(projectAssets.Length);
                        else
                            EditorUtility.DisplayDialog("提示", "请选工程 Assets 目录内的文件夹。", "OK");
                    }
                }
            }
        }

        // ---------------------------------------------------------- Actions
        void DrawActions()
        {
            int count = _targets.FindAll(t => t != null).Count;
            using (new EditorGUI.DisabledScope(count == 0))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(Content("Bake Self-Shadow R", "Bake 自阴影 R"), GUILayout.Height(30))) DoBatchBake(true, false, false);
                    if (GUILayout.Button(Content("Bake AO G", "Bake AO G"), GUILayout.Height(30))) DoBatchBake(false, true, false);
                    if (GUILayout.Button(Content("Bake Fill B", "Bake 反光 B"), GUILayout.Height(30))) DoBatchBake(false, false, true);
                }
                if (GUILayout.Button(Content($"Bake R+G+B All ({count} targets)", "Bake R+G+B 全部"), GUILayout.Height(26))) DoBatchBake(true, true, true);
            }
            EditorGUILayout.LabelField(Content("Bake separately to preserve other channels; 'All' recomputes all three.", "分开烘各自保留其它通道；全部则三者都重算。"), EditorStyles.miniLabel);
        }

        void DrawPreviews()
        {
            EditorGUILayout.LabelField(Content("Latest Bake Results", "最近烘焙结果"), EditorStyles.boldLabel);
            foreach (var p in _previews)
            {
                using (new EditorGUILayout.HorizontalScope("box"))
                {
                    var r = GUILayoutUtility.GetRect(72, 72, GUILayout.Width(72), GUILayout.Height(72));
                    if (p.tex != null) EditorGUI.DrawPreviewTexture(r, p.tex);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(p.name, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(p.info, EditorStyles.miniLabel);
                        if (p.tex != null && GUILayout.Button("Ping", GUILayout.Width(60)))
                            EditorGUIUtility.PingObject(p.tex);
                    }
                }
            }
        }

        // ---------------------------------------------------------- Bake logic
        void DoBatchBake(bool bakeShadow, bool bakeAO, bool bakeFill)
        {
            var valid = _targets.FindAll(t => t != null);
            if (valid.Count == 0) return;

            _previews.Clear();
            int success = 0;
            var failures = new List<string>();
            try
            {
                for (int i = 0; i < valid.Count; i++)
                {
                    var target = valid[i];
                    EditorUtility.DisplayProgressBar("Self Shadow Baker",
                        $"{target.name} ({i + 1}/{valid.Count})", (float)i / valid.Count);
                    if (BakeOneTarget(target, bakeShadow, bakeAO, bakeFill, out string err)) success++;
                    else failures.Add($"{target.name}: {err}");
                }
            }
            finally { EditorUtility.ClearProgressBar(); }

            AssetDatabase.SaveAssets();
            Debug.Log($"[SelfShadowBaker] Batch done: {success}/{valid.Count}");
            foreach (var f in failures) Debug.LogWarning("[SelfShadowBaker] " + f);

            var whatParts = new List<string>();
            if (bakeShadow) whatParts.Add("R 自阴影");
            if (bakeAO) whatParts.Add("G AO");
            if (bakeFill) whatParts.Add("B 反光");
            string what = string.Join("+", whatParts);
            string msg = $"完成({what}): {success}/{valid.Count}\n输出: {_outputFolder}";
            if (failures.Count > 0) msg += "\n\n失败:\n• " + string.Join("\n• ", failures);
            EditorUtility.DisplayDialog("完成", msg, "OK");
        }

        // scene object: use the live renderers (world transforms are correct);
        // prefab asset: load contents to read its renderers.
        bool BakeOneTarget(GameObject target, bool bakeShadow, bool bakeAO, bool bakeFill, out string error)
        {
            error = null;
            bool isSceneObject = target.scene.IsValid() && !string.IsNullOrEmpty(target.scene.name);
            GameObject loadedPrefab = null;
            var renderers = new List<MeshRenderer>();

            if (isSceneObject)
            {
                renderers.AddRange(target.GetComponentsInChildren<MeshRenderer>(false));
            }
            else
            {
                string prefabPath = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrEmpty(prefabPath))
                    loadedPrefab = PrefabUtility.LoadPrefabContents(prefabPath);
                if (loadedPrefab != null)
                    renderers.AddRange(loadedPrefab.GetComponentsInChildren<MeshRenderer>(false));
            }

            renderers.RemoveAll(r => r == null);
            if (renderers.Count == 0)
            {
                error = "No MeshRenderer";
                if (loadedPrefab != null) PrefabUtility.UnloadPrefabContents(loadedPrefab);
                return false;
            }

            var settings = new SelfShadowBakeSettings
            {
                Renderers = renderers,
                LightDirection = ResolveLightDirection(),
                BakeShadow = bakeShadow,
                BakeAO = bakeAO,
                BakeFill = bakeFill,
                FillOffset = _fillOffset,
                Resolution = _resolution,
                ShadowMapResolution = _shadowRes,
                PcfRadius = _pcfRadius,
                PcfTaps = _pcfTaps,
                InvertOutput = _invert,
                AoSamples = _aoSamples,
                AoDepthResolution = _aoDepthRes,
                AoMaxDistance = _aoMaxDistance,
                AoLightBias = _aoLightBias,
                AoPower = _aoPower,
                DepthBias = _depthBias,
                NormalBias = _normalBias,
                DilatePixels = _dilate,
                FlipV = _flipV,
                OutputFolder = _outputFolder,
                AssetName = target.name,
            };

            var result = SelfShadowBakerCore.Bake(settings, out error);

            if (loadedPrefab != null) PrefabUtility.UnloadPrefabContents(loadedPrefab);

            if (result != null)
            {
                string info = $"{result.MeshCount} mesh";
                if (bakeShadow) info += $" · Shadow {result.ShadowedPercent:F0}%";
                if (bakeAO) info += $" · AO {result.AoPercent:F0}%";
                if (bakeFill) info += " · Fill B";
                if (result.Skipped.Count > 0) info += $" · Skipped {result.Skipped.Count}";
                _previews.Add(new PreviewItem { name = target.name, tex = result.Texture, info = info });
                return true;
            }
            return false;
        }

        // ---------------------------------------------------------- Scene preview gizmo
        void OnSceneGui(SceneView sv)
        {
            if (!_previewInScene) return;
            if (!TryGetTargetsBounds(out Bounds wb)) return;

            Vector3 sun = ResolveLightDirection();
            if (sun.sqrMagnitude < 1e-6f) sun = Vector3.down;
            sun.Normalize();

            float halfDiag = Mathf.Max(wb.extents.magnitude, 1e-3f);
            float camDist = halfDiag * 3f;
            Vector3 camPos = wb.center - sun * camDist;
            Quaternion rot = Quaternion.LookRotation(sun);

            // light direction (yellow)
            Handles.color = Color.yellow;
            Handles.DrawLine(wb.center - sun * halfDiag * 1.5f, wb.center + sun * halfDiag * 1.5f);
            Handles.ConeHandleCap(0, wb.center + sun * halfDiag * 1.5f, rot, halfDiag * 0.18f, EventType.Repaint);

            // coverage box (orange) — mirrors Core's frustum sizing by projecting the
            // 8 bounds corners onto the light's right/up/forward axes.
            Vector3 R = rot * Vector3.right, U = rot * Vector3.up, F = rot * Vector3.forward;
            float dMin = float.PositiveInfinity, dMax = float.NegativeInfinity, wMax = 0f, hMax = 0f;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = wb.center + Vector3.Scale(wb.extents, new Vector3(
                    (c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                Vector3 rel = corner - camPos;
                float dz = Vector3.Dot(rel, F);
                dMin = Mathf.Min(dMin, dz); dMax = Mathf.Max(dMax, dz);
                wMax = Mathf.Max(wMax, Mathf.Abs(Vector3.Dot(rel, R)));
                hMax = Mathf.Max(hMax, Mathf.Abs(Vector3.Dot(rel, U)));
            }
            float pad = halfDiag * 0.05f;
            float halfH = hMax + pad, halfW = wMax + pad;

            Vector3 cn = camPos + F * (dMin - pad), cf = camPos + F * (dMax + pad);
            Vector3[] near = {
                cn - R*halfW - U*halfH, cn + R*halfW - U*halfH, cn + R*halfW + U*halfH, cn - R*halfW + U*halfH };
            Vector3[] far = {
                cf - R*halfW - U*halfH, cf + R*halfW - U*halfH, cf + R*halfW + U*halfH, cf - R*halfW + U*halfH };
            Handles.color = new Color(1f, 0.55f, 0f, 0.9f);
            for (int k = 0; k < 4; k++)
            {
                Handles.DrawLine(near[k], near[(k + 1) % 4]);
                Handles.DrawLine(far[k], far[(k + 1) % 4]);
                Handles.DrawLine(near[k], far[k]);
            }
        }

        bool TryGetTargetsBounds(out Bounds wb)
        {
            wb = default; bool has = false;
            foreach (var t in _targets)
            {
                if (t == null) continue;
                foreach (var r in t.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (r == null) continue;
                    if (!has) { wb = r.bounds; has = true; }
                    else wb.Encapsulate(r.bounds);
                }
            }
            return has;
        }

        // ---------------------------------------------------------- Persistence
        const string PrefKey = "SelfShadowBaker.Settings.v2";

        [System.Serializable]
        class SavedState
        {
            public int lightMode;
            public float eX, eY, eZ, dX, dY, dZ;
            public int resolution, shadowRes, pcfTaps, dilate;
            public float pcfRadius, depthBias, normalBias;
            public bool flipV, invert, previewInScene;
            public string outputFolder;
            // AO
            public int aoSamples, aoDepthRes;
            public float aoMaxDistance, aoLightBias, aoPower;
            // Fill (B)
            public float fillOffX, fillOffY, fillOffZ;
        }

        void SaveSettings()
        {
            var s = new SavedState
            {
                lightMode = (int)_lightMode,
                eX = _lightEuler.x, eY = _lightEuler.y, eZ = _lightEuler.z,
                dX = _lightDir.x, dY = _lightDir.y, dZ = _lightDir.z,
                resolution = _resolution, shadowRes = _shadowRes, pcfTaps = _pcfTaps,
                dilate = _dilate,
                pcfRadius = _pcfRadius, depthBias = _depthBias, normalBias = _normalBias,
                flipV = _flipV, invert = _invert, previewInScene = _previewInScene,
                outputFolder = _outputFolder,
                aoSamples = _aoSamples, aoDepthRes = _aoDepthRes,
                aoMaxDistance = _aoMaxDistance, aoLightBias = _aoLightBias, aoPower = _aoPower,
                fillOffX = _fillOffset.x, fillOffY = _fillOffset.y, fillOffZ = _fillOffset.z,
            };
            EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));
        }

        void LoadSettings()
        {
            string json = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var s = JsonUtility.FromJson<SavedState>(json);
                _lightMode = (LightMode)s.lightMode;
                _lightEuler = new Vector3(s.eX, s.eY, s.eZ);
                _lightDir = new Vector3(s.dX, s.dY, s.dZ);
                if (s.resolution > 0) _resolution = s.resolution;
                if (s.shadowRes > 0) _shadowRes = s.shadowRes;
                if (s.pcfTaps > 0) _pcfTaps = s.pcfTaps;
                _dilate = s.dilate;
                _pcfRadius = s.pcfRadius; _depthBias = s.depthBias; _normalBias = s.normalBias;
                _invert = s.invert; _previewInScene = s.previewInScene;
                // _flipV intentionally not restored: orientation is handled in-shader now, off is correct.
                if (!string.IsNullOrEmpty(s.outputFolder)) _outputFolder = s.outputFolder;
                if (s.aoSamples > 0) _aoSamples = s.aoSamples;
                if (s.aoDepthRes > 0) _aoDepthRes = s.aoDepthRes;
                _aoMaxDistance = Mathf.Max(0f, s.aoMaxDistance);
                _aoLightBias = Mathf.Clamp01(s.aoLightBias);
                if (s.aoPower > 0.01f) _aoPower = s.aoPower;
                _fillOffset = new Vector3(s.fillOffX, s.fillOffY, s.fillOffZ);
            }
            catch { /* ignore corrupt prefs */ }
        }
    }
}
