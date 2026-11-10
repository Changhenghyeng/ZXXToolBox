using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ShadowBakerTool.Editor
{
    /// <summary>
    /// 阴影烘焙工具主窗口。流程：添加目标 → 设置灯光 → 调软硬度 → 批量烘焙 + 生成贴花。
    /// </summary>
    public class ShadowBakerWindow : EditorWindow
    {
        // --- Step 1: Source ---
        private List<GameObject> _batchTargets = new List<GameObject>();

        // --- Step 2: Light ---
        private enum LightSourceMode { ManualLight, EulerAngles, DirectionVector }
        private LightSourceMode _lightMode = LightSourceMode.ManualLight;
        private Light _manualLight;
        private Vector3 _lightEuler = new Vector3(50f, -30f, 0f);
        private Vector3 _lightDir = new Vector3(0.3f, -1f, 0.2f);
        private float _lightIntensity = 1.0f;

        // --- Step 3: Bake settings ---
        private int _resolution = 512;
        private readonly int[] _resolutionChoices = { 128, 256, 512, 1024, 2048 };
        private SoftnessMode _softness = SoftnessMode.Gaussian;
        private float _falloffPx = 5f;
        private float _maxAlpha = 0.85f;
        private float _minAlpha = 0.0f;
        private float _falloffPow = 1.5f;
        private int _samples = 32;
        private float _footprintPadding = 0.2f; // Now fixed world units

        // --- Step 4: Decal generation ---
        private DecalShaderType _decalShader;
        private float _yOffset = 0.01f;
        private Color _shadowColor = new Color(0, 0, 0, 1f);
        private bool _attachUnderBuilding = true;
        private bool _disableCastShadows = true;
        private bool _skipTransparentMaterials = true;

        // --- Gradient ---
        private bool _enableGradient = false;
        private float _gradientAngle = 0f;
        private float _gradientPosition = 0.5f;
        private float _gradientWidth = 1f;
        private Color _gradientColor = new Color(1, 1, 1, 1);

        // --- Output ---
        private string _outputFolder = "Assets/BakedShadows";

        // --- State ---
        private Vector2 _scroll;

        public static void Open()
        {
            var w = GetWindow<ShadowBakerWindow>("Shadow Baker");
            w.minSize = new Vector2(380, 600);
            w.Show();
        }

        private void OnEnable()
        {
            LoadSettings();
            if (_batchTargets.Count == 0)
                FillBatchFromSelection();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField(Content("Shadow Baker", "为建筑烘培柔和的俯视投影贴图并生成地面贴花面片。"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "为建筑烘培一张柔和的俯视投影贴图，并生成对应的地面贴花面片。",
                MessageType.None);

            DrawSourceSection();
            EditorGUILayout.Space(6);
            DrawLightSection();
            EditorGUILayout.Space(6);
            DrawBakeSettingsSection();
            EditorGUILayout.Space(6);
            DrawDecalSection();
            EditorGUILayout.Space(6);
            DrawOutputSection();
            EditorGUILayout.Space(10);
            DrawActionButtons();

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

        // ---------- Sections ----------
        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField(Content("1. Source", "添加烘焙目标建筑。"), EditorStyles.boldLabel);
            DrawBatchSourceList();
        }

        private void DrawBatchSourceList()
        {
            EditorGUILayout.LabelField(Content("Batch Targets", "要烘焙阴影的建筑物体列表。"), EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Content("Fill From Selection", "从当前 Hierarchy 选中的物体填入列表。")))
                    FillBatchFromSelection();
                if (GUILayout.Button(Content("Clear", "清空目标列表。"), GUILayout.Width(60)))
                    _batchTargets.Clear();
            }

            int removeIdx = -1;
            for (int i = 0; i < _batchTargets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _batchTargets[i] = (GameObject)EditorGUILayout.ObjectField(
                        _batchTargets[i], typeof(GameObject), true);
                    if (GUILayout.Button(Content("-", "移除此项。"), GUILayout.Width(22)))
                        removeIdx = i;
                }
            }
            if (removeIdx >= 0) _batchTargets.RemoveAt(removeIdx);

            if (GUILayout.Button(Content("+ Add", "添加一个空槽位。")))
                _batchTargets.Add(null);

            int valid = _batchTargets.FindAll(t => t != null).Count;
            EditorGUILayout.LabelField(Content($"Valid Targets: {valid}", "列表中有效（非空）的目标数量。"), EditorStyles.miniLabel);
        }

        private void FillBatchFromSelection()
        {
            _batchTargets.Clear();
            foreach (var go in Selection.gameObjects)
            {
                if (go != null) _batchTargets.Add(go);
            }
            foreach (var obj in Selection.objects)
            {
                var go = obj as GameObject;
                if (go != null && !_batchTargets.Contains(go))
                    _batchTargets.Add(go);
            }
            if (_batchTargets.Count == 0)
                Debug.Log("[ShadowBaker] 未选中物体，请手动拖入或先选中再点「从选中项填入」。");
        }

        private void DrawLightSection()
        {
            EditorGUILayout.LabelField(Content("2. Light", "设置光源方向与强度。"), EditorStyles.boldLabel);

            _lightMode = (LightSourceMode)EditorGUILayout.EnumPopup(Content("Source Mode", "光源方向输入方式：手动拖入灯光、欧拉角或方向向量。"), _lightMode);
            switch (_lightMode)
            {
                case LightSourceMode.ManualLight:
                    _manualLight = (Light)EditorGUILayout.ObjectField(Content("Light", "拖入场景中的 Directional Light。"), _manualLight, typeof(Light), true);
                    if (_manualLight != null)
                    {
                        EditorGUILayout.LabelField("→ Direction", _manualLight.transform.forward.ToString("F2"));
                    }
                    break;
                case LightSourceMode.EulerAngles:
                    _lightEuler = EditorGUILayout.Vector3Field(Content("Euler (X=pitch, Y=yaw)", "用欧拉角指定光源方向。X=俯仰角，Y=偏航角。"), _lightEuler);
                    break;
                case LightSourceMode.DirectionVector:
                    _lightDir = EditorGUILayout.Vector3Field(Content("Direction (world)", "世界空间中的光源方向向量。"), _lightDir);
                    EditorGUILayout.HelpBox("光从太阳指向地面，Y 分量应为负值。", MessageType.None);
                    break;
            }

            _lightIntensity = EditorGUILayout.Slider(Content("Intensity", "光源强度，影响阴影的深浅。"), _lightIntensity, 0f, 1f);
        }

        private void DrawBakeSettingsSection()
        {
            EditorGUILayout.LabelField(Content("3. Bake Settings", "阴影烘焙质量与柔边参数。"), EditorStyles.boldLabel);

            // Resolution dropdown
            int curIdx = System.Array.IndexOf(_resolutionChoices, _resolution);
            if (curIdx < 0) curIdx = 2; // default to 512
            string[] labels = new string[_resolutionChoices.Length];
            for (int i = 0; i < _resolutionChoices.Length; i++)
                labels[i] = _resolutionChoices[i].ToString();
            curIdx = EditorGUILayout.Popup(Content("Base Resolution", "烘焙阴影贴图的基础分辨率（像素）。"), curIdx, labels);
            _resolution = _resolutionChoices[curIdx];

            _softness = (SoftnessMode)EditorGUILayout.EnumPopup(Content("Softness Mode", "柔边算法：Gaussian 均匀模糊 / DistanceField 距离场柔边。"), _softness);
            if (_softness == SoftnessMode.DistanceField)
            {
                _falloffPx = EditorGUILayout.Slider(Content("Falloff Distance (px)", "柔边光晕向外延伸的像素数。"), _falloffPx, 1f, 128f);
                _maxAlpha = EditorGUILayout.Slider(Content("Max Alpha (at contact)", "阴影接触点的最大不透明度。"), _maxAlpha, 0f, 1f);
                _minAlpha = EditorGUILayout.Slider(Content("Min Alpha (far edge)", "阴影远端边缘的最小不透明度。"), _minAlpha, 0f, 1f);
                _falloffPow = EditorGUILayout.Slider(Content("Falloff Curve (1 = linear)", "衰减曲线指数：>1 深色保持更久再快速衰减，<1 立即淡出。"), _falloffPow, 0.2f, 4f);
                _samples = EditorGUILayout.IntSlider(Content("Distance Samples", "距离场计算的采样次数，越高越精确但越慢。"), _samples, 8, 96);

                EditorGUILayout.HelpBox(
                    "距离场柔边：像素深浅取决于到影子边缘的距离。接触处保持深色，远端边缘平滑淡出。\n" +
                    "• Falloff Distance：柔边光晕向外延伸的像素数。\n" +
                    "• Curve > 1：深色保持更久、再快速衰减（实心核心）。\n" +
                    "• Curve < 1：立即淡出（非常柔和，类似环境光遮蔽）。",
                    MessageType.None);
            }
            else
            {
                _falloffPx = EditorGUILayout.Slider(Content("Blur Radius (px)", "高斯模糊的半径像素数。"), _falloffPx, 1f, 40f);
                EditorGUILayout.HelpBox("普通的均匀高斯模糊。", MessageType.None);
            }

            // Updated padding UI (fixed world units)
            _footprintPadding = EditorGUILayout.FloatField(Content("Footprint Padding (m)", "阴影四周留白的世界单位（米）。"), _footprintPadding);
            _footprintPadding = Mathf.Max(0f, _footprintPadding);

            EditorGUILayout.HelpBox(
                "影子四周的留白，单位为世界单位（米）。值越小贴合越紧、UV 利用率越高；" +
                "但若留白小于 FalloffPx 换算成的世界尺寸，光晕会被裁掉。\n" +
                "推荐：大多数建筑用 0.1-0.3 米。",
                MessageType.None);

            // Gradient
            EditorGUILayout.Space(4);
            _enableGradient = EditorGUILayout.Toggle(Content("Enable Gradient", "启用阴影渐变效果，可让影子随方向逐渐变淡。"), _enableGradient);
            if (_enableGradient)
            {
                EditorGUI.indentLevel++;
                _gradientAngle = EditorGUILayout.Slider(Content("Angle", "渐变方向角度：0°=上→下，90°=左→右。"), _gradientAngle, 0f, 360f);
                _gradientPosition = EditorGUILayout.Slider(Content("Position", "渐变过渡的中心位置（0=起点端，1=终点端）。"), _gradientPosition, 0f, 1f);
                _gradientWidth = EditorGUILayout.Slider(Content("Width", "渐变过渡带的宽度（0=锐利边界，1=铺满全图）。"), _gradientWidth, 0f, 1f);
                _gradientColor = EditorGUILayout.ColorField(Content(
                    "Color",
                    "渐变目标颜色。RGB=色调，Alpha=渐变强度（0=无效果，1=完全过渡）。\n" +
                    "例如白色 Alpha=0.1 → 影子只轻微变淡 10%。"),
                    _gradientColor, true, true, false);
                EditorGUILayout.HelpBox(
                    "Angle: 0°=上→下, 90°=左→右, 180°=下→上, 270°=右→左\n" +
                    "Position: 渐变过渡的中心位置 (0=起点端, 1=终点端)\n" +
                    "Width: 过渡带的宽度 (0=锐利边界, 1=铺满全图)",
                    MessageType.None);
                EditorGUI.indentLevel--;
            }
        }

        private void DrawDecalSection()
        {
            EditorGUILayout.LabelField(Content("4. Decal Output", "地面贴花面片生成设置。"), EditorStyles.boldLabel);

            _decalShader = (DecalShaderType)EditorGUILayout.EnumPopup(Content("Decal Shader", "生成贴花面片使用的 Shader 类型。"), _decalShader);
            _yOffset = EditorGUILayout.FloatField(Content("Y Offset (from pivot)", "贴花面片相对于建筑 pivot 的 Y 轴偏移量。"), _yOffset);
            _shadowColor = EditorGUILayout.ColorField(Content(
                "Shadow Color",
                "颜色 = 影子色调 (RGB) + 强度 (Alpha)"),
                _shadowColor, true, true, false);
            _attachUnderBuilding = EditorGUILayout.Toggle(Content("Attach Under Building", "将生成的贴花面片挂载到建筑物体下。"), _attachUnderBuilding);
            _disableCastShadows = EditorGUILayout.Toggle(Content(
                "Disable Cast Shadows After Bake",
                "烘培完成后，把参与烘培的网格的 Cast Shadows 关掉。"), _disableCastShadows);
            _skipTransparentMaterials = EditorGUILayout.Toggle(Content(
                "Skip Transparent Materials",
                "跳过半透明材质的子网格，不计入影子剪影。"), _skipTransparentMaterials);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField(Content("5. Output Paths", "设置输出目录。"), EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField(Content("Folder", "阴影贴图和贴花预制体的输出目录。"), _outputFolder);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("Pick output folder", _outputFolder, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        string projectAssets = Application.dataPath.Replace('\\', '/');
                        if (picked.Replace('\\', '/').StartsWith(projectAssets))
                        {
                            _outputFolder = "Assets" + picked.Substring(projectAssets.Length);
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("无效目录",
                                "请选择工程 Assets 目录内的文件夹。", "OK");
                        }
                    }
                }
            }
        }

        private void DrawActionButtons()
        {
            int count = _batchTargets.FindAll(t => t != null).Count;
            using (new EditorGUI.DisabledScope(count == 0))
            {
                if (GUILayout.Button($"Bake + Generate ({count} targets)", GUILayout.Height(34)))
                    DoBatchBake();
            }
        }

        // ---------- Logic ----------
        private Vector3 ResolveLightDirection()
        {
            switch (_lightMode)
            {
                case LightSourceMode.ManualLight:
                    if (_manualLight != null) return _manualLight.transform.forward;
                    break;
                case LightSourceMode.EulerAngles:
                    return Quaternion.Euler(_lightEuler) * Vector3.forward;
                case LightSourceMode.DirectionVector:
                    return _lightDir.normalized;
            }
            return new Vector3(0.3f, -1f, 0.2f).normalized;
        }

        private void DoBatchBake()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            var valid = _batchTargets.FindAll(t => t != null);
            if (valid.Count == 0) return;

            int success = 0;
            try
            {
                for (int i = 0; i < valid.Count; i++)
                {
                    var target = valid[i];
                    EditorUtility.DisplayProgressBar("Batch Shadow Bake",
                        $"{target.name} ({i + 1}/{valid.Count})",
                        (float)i / valid.Count);

                    // Isolate per-target failures: an exception here must NOT escape OnGUI, or it
                    // corrupts the IMGUI layout stack ("Invalid GUILayout state").
                    try
                    {
                        if (BakeOneTarget(target))
                            success++;
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[ShadowBaker] '{target.name}' 烘培失败，已跳过：{e.Message}\n{e}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            ZXXToolUsageStats.Complete("Shadow Baker", success, usageTimer);
            Debug.Log($"[ShadowBaker] Batch done: {success}/{valid.Count} baked");
            EditorUtility.DisplayDialog("Batch Bake Complete",
                $"烘培完成: {success}/{valid.Count}\n输出目录: {_outputFolder}", "OK");
        }

        private bool BakeOneTarget(GameObject target)
        {
            bool isSceneObject = target.scene.IsValid() && !string.IsNullOrEmpty(target.scene.name);
            GameObject loadedPrefab = null;   // editable copy — ONLY for real .prefab files
            string prefabPath = null;          // source asset path (.prefab, .fbx, … or null)
            Transform attachTo = null;

            var targetRenderers = new List<MeshRenderer>();

            if (isSceneObject)
            {
                attachTo = target.transform;
                // Bake from the SCENE renderers (correct world positions). Works for FBX/model
                // instances, prefab instances, and plain scene objects alike.
                targetRenderers.AddRange(target.GetComponentsInChildren<MeshRenderer>(false));
                // Source asset path — only used to write cast-shadow changes back to a .prefab.
                prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
                // LoadPrefabContents only accepts real .prefab files; an FBX/model source would
                // throw "is not a prefab file". Guard it so models bake from the scene instead.
                if (IsRegularPrefab(prefabPath))
                    loadedPrefab = PrefabUtility.LoadPrefabContents(prefabPath);
            }
            else
            {
                prefabPath = AssetDatabase.GetAssetPath(target);
                if (IsRegularPrefab(prefabPath))
                {
                    // Regular .prefab asset: edit an isolated copy.
                    loadedPrefab = PrefabUtility.LoadPrefabContents(prefabPath);
                    if (loadedPrefab != null)
                        targetRenderers.AddRange(loadedPrefab.GetComponentsInChildren<MeshRenderer>(false));
                }
                else
                {
                    // FBX / model asset (or any other GameObject asset): read renderers straight
                    // from the imported asset — LoadPrefabContents can't open these.
                    var assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (assetRoot != null)
                        targetRenderers.AddRange(assetRoot.GetComponentsInChildren<MeshRenderer>(false));
                }
            }

            if (string.IsNullOrEmpty(prefabPath) && !isSceneObject)
            {
                Debug.LogWarning($"[ShadowBaker] '{target.name}' is not a valid asset, skipped.");
                return false;
            }

            // Drop nulls and any previously-baked shadow decals. A leftover "*_ShadowDecal"
            // quad would otherwise inflate the footprint (it's counted) but be skipped from the
            // silhouette (it's transparent), shrinking the real shadow toward a blank/white map.
            targetRenderers.RemoveAll(r => r == null || IsShadowDecal(r));
            if (targetRenderers.Count == 0)
            {
                Debug.LogWarning($"[ShadowBaker] '{target.name}' has no MeshRenderers, skipped.");
                if (loadedPrefab != null) PrefabUtility.UnloadPrefabContents(loadedPrefab);
                return false;
            }

            var settings = new BakeSettings
            {
                Renderers = targetRenderers,
                LightDirection = ResolveLightDirection(),
                LightIntensity = _lightIntensity,
                Resolution = _resolution,
                Softness = _softness,
                FalloffPx = _falloffPx,
                MaxAlpha = _maxAlpha,
                MinAlpha = _minAlpha,
                FalloffPow = _falloffPow,
                Samples = _samples,
                FootprintPadding = _footprintPadding,
                SkipTransparentMaterials = _skipTransparentMaterials,
                EnableGradient = _enableGradient,
                GradientAngle = _gradientAngle,
                GradientPosition = _gradientPosition,
                GradientWidth = _gradientWidth,
                GradientColor = _gradientColor,
                OutputFolder = _outputFolder,
                AssetName = target.name,
            };

            var result = ShadowBakerCore.Bake(settings);

            if (result == null)
            {
                if (loadedPrefab != null) PrefabUtility.UnloadPrefabContents(loadedPrefab);
                return false;
            }

            // Disable cast shadows
            if (_disableCastShadows)
            {
                if (loadedPrefab != null)
                {
                    // Regular .prefab: modify the prefab asset copy and save it back.
                    var prefabRenderers = new List<MeshRenderer>();
                    prefabRenderers.AddRange(loadedPrefab.GetComponentsInChildren<MeshRenderer>(false));
                    DisableCastShadowsOnRenderers(prefabRenderers);
                    PrefabUtility.SaveAsPrefabAsset(loadedPrefab, prefabPath);
                }
                else if (isSceneObject)
                {
                    // Scene instance (incl. FBX/model instances): modify the instance renderers.
                    DisableCastShadowsOnRenderers(targetRenderers);
                }
                // else: an FBX/model baked straight from the asset — cast-shadow state can't be
                // persisted into an imported model, so we leave it untouched.
            }

            if (loadedPrefab != null)
                PrefabUtility.UnloadPrefabContents(loadedPrefab);

            var opts = new DecalGenerateSettings
            {
                AttachUnder = _attachUnderBuilding ? attachTo : null,
                PivotReference = attachTo,
                YOffset = _yOffset,
                PrefabFolder = _outputFolder,
                AssetName = target.name,
                ShadowColor = _shadowColor,
                ShaderType = _decalShader,
            };

            // Replace any previous "*_ShadowDecal" under the building instead of stacking a new
            // one on top, so re-baking stays clean and old decals don't pollute later bakes.
            if (isSceneObject)
                RemoveExistingDecals(target);

            var decal = ShadowDecalGenerator.Generate(result, opts);
            if (decal != null)
                decal.name = $"{target.name}_ShadowDecal";

            return true;
        }

        private void DisableCastShadowsOnRenderers(List<MeshRenderer> renderers)
        {
            int count = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                Undo.RecordObject(r, "Disable Cast Shadows");
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                count++;
            }
            if (count > 0)
                Debug.Log($"[ShadowBaker] Disabled Cast Shadows on {count} renderers");
        }

        // LoadPrefabContents / SaveAsPrefabAsset only work on real .prefab files. FBX and other
        // imported models are "model prefabs" and must be handled via the scene instance or
        // AssetDatabase.LoadAssetAtPath instead — calling LoadPrefabContents on them throws
        // "is not a prefab file".
        private static bool IsRegularPrefab(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);
        }

        // A previously-generated ground shadow decal. Identified by the generator's naming
        // convention ("<asset>_ShadowDecal") or by the dedicated decal shader. Such objects must
        // never be baked as if they were the building geometry.
        private static bool IsShadowDecal(MeshRenderer r)
        {
            if (r == null) return false;
            if (r.gameObject.name.EndsWith("_ShadowDecal", System.StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var m in r.sharedMaterials)
                if (m != null && m.shader != null && m.shader.name == "Custom/ShadowDecal_Gradient_URP")
                    return true;
            return false;
        }

        // Destroy any "*_ShadowDecal" objects under root so a re-bake replaces the previous decal
        // instead of stacking new ones on top. Only scene instances are touched.
        private static void RemoveExistingDecals(GameObject root)
        {
            if (root == null) return;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t == null || t == root.transform) continue;
                var go = t.gameObject;
                if (go != null && go.name.EndsWith("_ShadowDecal", System.StringComparison.OrdinalIgnoreCase))
                    Undo.DestroyObjectImmediate(go);
            }
        }

        private static GUIContent Content(string label, string tooltip)
        {
            return new GUIContent(label, tooltip);
        }

        // ── EditorPrefs persistence (used by Workflow) ──
        private const string PrefKey = "ShadowBaker.Settings";

        [System.Serializable]
        private class SavedState
        {
            public int lightMode;
            public float lightEulerX, lightEulerY, lightEulerZ;
            public float lightDirX, lightDirY, lightDirZ;
            public float lightIntensity;
            public int resolution;
            public int softness;
            public float falloffPx, maxAlpha, minAlpha, falloffPow;
            public int samples;
            public float footprintPadding;
            public int decalShader;
            public float yOffset;
            public float shadowColorR, shadowColorG, shadowColorB, shadowColorA;
            public bool attachUnderBuilding, disableCastShadows, skipTransparent;
            public bool enableGradient;
            public float gradientAngle, gradientPosition, gradientWidth;
            public float gradientColorR, gradientColorG, gradientColorB, gradientColorA;
            public string outputFolder;
        }

        public void SaveSettings()
        {
            var s = new SavedState
            {
                lightMode = (int)_lightMode,
                lightEulerX = _lightEuler.x, lightEulerY = _lightEuler.y, lightEulerZ = _lightEuler.z,
                lightDirX = _lightDir.x, lightDirY = _lightDir.y, lightDirZ = _lightDir.z,
                lightIntensity = _lightIntensity,
                resolution = _resolution,
                softness = (int)_softness,
                falloffPx = _falloffPx, maxAlpha = _maxAlpha, minAlpha = _minAlpha, falloffPow = _falloffPow,
                samples = _samples,
                footprintPadding = _footprintPadding,
                decalShader = (int)_decalShader,
                yOffset = _yOffset,
                shadowColorR = _shadowColor.r, shadowColorG = _shadowColor.g, shadowColorB = _shadowColor.b, shadowColorA = _shadowColor.a,
                attachUnderBuilding = _attachUnderBuilding, disableCastShadows = _disableCastShadows, skipTransparent = _skipTransparentMaterials,
                enableGradient = _enableGradient,
                gradientAngle = _gradientAngle,
                gradientPosition = _gradientPosition,
                gradientWidth = _gradientWidth,
                gradientColorR = _gradientColor.r, gradientColorG = _gradientColor.g, gradientColorB = _gradientColor.b, gradientColorA = _gradientColor.a,
                outputFolder = _outputFolder,
            };
            EditorPrefs.SetString(PrefKey, JsonUtility.ToJson(s));
        }

        public void LoadSettings()
        {
            string json = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                var s = JsonUtility.FromJson<SavedState>(json);
                _lightMode = (LightSourceMode)s.lightMode;
                _lightEuler = new Vector3(s.lightEulerX, s.lightEulerY, s.lightEulerZ);
                _lightDir = new Vector3(s.lightDirX, s.lightDirY, s.lightDirZ);
                _lightIntensity = s.lightIntensity;
                _resolution = s.resolution;
                _softness = (SoftnessMode)s.softness;
                _falloffPx = s.falloffPx; _maxAlpha = s.maxAlpha; _minAlpha = s.minAlpha; _falloffPow = s.falloffPow;
                _samples = s.samples;
                _footprintPadding = s.footprintPadding;
                _decalShader = (DecalShaderType)s.decalShader;
                _yOffset = s.yOffset;
                _shadowColor = new Color(s.shadowColorR, s.shadowColorG, s.shadowColorB, s.shadowColorA);
                _attachUnderBuilding = s.attachUnderBuilding;
                _disableCastShadows = s.disableCastShadows;
                _skipTransparentMaterials = s.skipTransparent;
                _enableGradient = s.enableGradient;
                _gradientAngle = s.gradientAngle;
                _gradientPosition = s.gradientPosition;
                _gradientWidth = s.gradientWidth;
                _gradientColor = new Color(s.gradientColorR, s.gradientColorG, s.gradientColorB, s.gradientColorA);
                _outputFolder = s.outputFolder;
            }
            catch { /* ignore corrupt data */ }
        }

    }
}
