using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace ShadowBakerTool.Editor
{
    /// <summary>
    /// Main window for the Shadow Baker tool.
    /// Opens via menu: Tools / Shadow Baker.
    /// Workflow: add targets → set light → tune softness → batch bake + generate decal.
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

            EditorGUILayout.LabelField("Shadow Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bakes a soft top-down shadow texture for a building and generates a ground-decal quad.",
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
            EditorGUILayout.LabelField("1. Source", EditorStyles.boldLabel);
            DrawBatchSourceList();
        }

        private void DrawBatchSourceList()
        {
            EditorGUILayout.LabelField("Batch Targets", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("从选中项填入"))
                    FillBatchFromSelection();
                if (GUILayout.Button("清空", GUILayout.Width(60)))
                    _batchTargets.Clear();
            }

            int removeIdx = -1;
            for (int i = 0; i < _batchTargets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _batchTargets[i] = (GameObject)EditorGUILayout.ObjectField(
                        _batchTargets[i], typeof(GameObject), true);
                    if (GUILayout.Button("-", GUILayout.Width(22)))
                        removeIdx = i;
                }
            }
            if (removeIdx >= 0) _batchTargets.RemoveAt(removeIdx);

            if (GUILayout.Button("+ 添加"))
                _batchTargets.Add(null);

            int valid = _batchTargets.FindAll(t => t != null).Count;
            EditorGUILayout.LabelField($"有效目标: {valid}", EditorStyles.miniLabel);
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
            EditorGUILayout.LabelField("2. Light", EditorStyles.boldLabel);

            _lightMode = (LightSourceMode)EditorGUILayout.EnumPopup("Source Mode", _lightMode);
            switch (_lightMode)
            {
                case LightSourceMode.ManualLight:
                    _manualLight = (Light)EditorGUILayout.ObjectField("Light", _manualLight, typeof(Light), true);
                    if (_manualLight != null)
                    {
                        EditorGUILayout.LabelField("→ Direction", _manualLight.transform.forward.ToString("F2"));
                    }
                    break;
                case LightSourceMode.EulerAngles:
                    _lightEuler = EditorGUILayout.Vector3Field("Euler (X=pitch, Y=yaw)", _lightEuler);
                    break;
                case LightSourceMode.DirectionVector:
                    _lightDir = EditorGUILayout.Vector3Field("Direction (world)", _lightDir);
                    EditorGUILayout.HelpBox("Light points from sun toward ground. Y should be negative.", MessageType.None);
                    break;
            }

            _lightIntensity = EditorGUILayout.Slider("Intensity", _lightIntensity, 0f, 1f);
        }

        private void DrawBakeSettingsSection()
        {
            EditorGUILayout.LabelField("3. Bake Settings", EditorStyles.boldLabel);

            // Resolution dropdown
            int curIdx = System.Array.IndexOf(_resolutionChoices, _resolution);
            if (curIdx < 0) curIdx = 2; // default to 512
            string[] labels = new string[_resolutionChoices.Length];
            for (int i = 0; i < _resolutionChoices.Length; i++)
                labels[i] = _resolutionChoices[i].ToString();
            curIdx = EditorGUILayout.Popup("Base Resolution", curIdx, labels);
            _resolution = _resolutionChoices[curIdx];

            _softness = (SoftnessMode)EditorGUILayout.EnumPopup("Softness Mode", _softness);
            if (_softness == SoftnessMode.DistanceField)
            {
                _falloffPx = EditorGUILayout.Slider("Falloff Distance (px)", _falloffPx, 1f, 128f);
                _maxAlpha = EditorGUILayout.Slider("Max Alpha (at contact)", _maxAlpha, 0f, 1f);
                _minAlpha = EditorGUILayout.Slider("Min Alpha (far edge)", _minAlpha, 0f, 1f);
                _falloffPow = EditorGUILayout.Slider("Falloff Curve (1 = linear)", _falloffPow, 0.2f, 4f);
                _samples = EditorGUILayout.IntSlider("Distance Samples", _samples, 8, 96);

                EditorGUILayout.HelpBox(
                    "Distance-field falloff: pixel darkness depends on distance from the shadow's edge. " +
                    "Contact areas stay deep; far edges fade out smoothly.\n" +
                    "• Falloff Distance: how many pixels the soft halo extends.\n" +
                    "• Curve > 1 = stays dark longer then falls off fast (solid core).\n" +
                    "• Curve < 1 = fades immediately (very soft, like ambient occlusion).",
                    MessageType.None);
            }
            else
            {
                _falloffPx = EditorGUILayout.Slider("Blur Radius (px)", _falloffPx, 1f, 40f);
                EditorGUILayout.HelpBox("Plain uniform Gaussian blur.", MessageType.None);
            }

            // Updated padding UI (fixed world units)
            _footprintPadding = EditorGUILayout.FloatField("Footprint Padding (m)", _footprintPadding);
            _footprintPadding = Mathf.Max(0f, _footprintPadding);

            EditorGUILayout.HelpBox(
                "Padding in world units (meters) around the shadow. Smaller " +
                "values = tighter fit & higher UV utilization, but if padding " +
                "is less than FalloffPx in world units, the halo will be clipped.\n" +
                "Recommended: 0.1-0.3m for most buildings.",
                MessageType.None);
        }

        private void DrawDecalSection()
        {
            EditorGUILayout.LabelField("4. Decal Output", EditorStyles.boldLabel);

            _decalShader = (DecalShaderType)EditorGUILayout.EnumPopup("Decal Shader", _decalShader);
            _yOffset = EditorGUILayout.FloatField("Y Offset (from pivot)", _yOffset);
            _shadowColor = EditorGUILayout.ColorField(new GUIContent(
                "Shadow Color",
                "颜色 = 影子色调 (RGB) + 强度 (Alpha)"),
                _shadowColor, true, true, false);
            _attachUnderBuilding = EditorGUILayout.Toggle("Attach Under Building", _attachUnderBuilding);
            _disableCastShadows = EditorGUILayout.Toggle("烘培后关闭 Cast Shadows", _disableCastShadows);
            _skipTransparentMaterials = EditorGUILayout.Toggle("跳过半透材质（不参与烘培）", _skipTransparentMaterials);
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField("5. Output Paths", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _outputFolder = EditorGUILayout.TextField("Folder", _outputFolder);
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
                            EditorUtility.DisplayDialog("Invalid folder",
                                "Please pick a folder inside the project's Assets directory.", "OK");
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

                    if (BakeOneTarget(target))
                        success++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"[ShadowBaker] Batch done: {success}/{valid.Count} baked");
            EditorUtility.DisplayDialog("Batch Bake Complete",
                $"烘培完成: {success}/{valid.Count}\n输出目录: {_outputFolder}", "OK");
        }

        private bool BakeOneTarget(GameObject target)
        {
            bool isSceneObject = target.scene.IsValid() && !string.IsNullOrEmpty(target.scene.name);
            GameObject loadedPrefab = null;
            string prefabPath = null;
            Transform attachTo = null;

            var targetRenderers = new List<MeshRenderer>();

            if (isSceneObject)
            {
                attachTo = target.transform;
                // Bake from the SCENE renderers (correct world positions)
                targetRenderers.AddRange(target.GetComponentsInChildren<MeshRenderer>(false));
                // Also get prefab path for saving cast-shadow changes later
                prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
                if (!string.IsNullOrEmpty(prefabPath))
                {
                    loadedPrefab = PrefabUtility.LoadPrefabContents(prefabPath);
                }
            }
            else
            {
                prefabPath = AssetDatabase.GetAssetPath(target);
                if (!string.IsNullOrEmpty(prefabPath))
                    loadedPrefab = PrefabUtility.LoadPrefabContents(prefabPath);
                if (loadedPrefab != null)
                    targetRenderers.AddRange(loadedPrefab.GetComponentsInChildren<MeshRenderer>(false));
            }

            if (prefabPath == null && !isSceneObject)
            {
                Debug.LogWarning($"[ShadowBaker] '{target.name}' is not a valid asset, skipped.");
                return false;
            }

            targetRenderers.RemoveAll(r => r == null);
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
                    // Prefab instance or asset: modify the prefab asset only
                    var prefabRenderers = new List<MeshRenderer>();
                    prefabRenderers.AddRange(loadedPrefab.GetComponentsInChildren<MeshRenderer>(false));
                    DisableCastShadowsOnRenderers(prefabRenderers);
                    PrefabUtility.SaveAsPrefabAsset(loadedPrefab, prefabPath);
                }
                else
                {
                    // Pure scene object: modify directly
                    DisableCastShadowsOnRenderers(targetRenderers);
                }
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
                _outputFolder = s.outputFolder;
            }
            catch { /* ignore corrupt data */ }
        }

    }
}