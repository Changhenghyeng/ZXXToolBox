using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SLGTerrainBakerTool.Editor
{
    public sealed class SLGTerrainBakeWindow : EditorWindow
    {
        private const string PrefKey =
            "SLGTerrainMobileBaker.Settings";

        private static readonly int[] TerrainResolutions =
        {
            512,
            1024,
            2048
        };

        private static readonly int[] NoiseResolutions =
        {
            256,
            512,
            1024
        };

        private static readonly int[] FarResolutions =
        {
            512,
            1024,
            2048,
            4096
        };

        private static readonly string[] CompressionLabels =
        {
            "Uncompressed (Validation)",
            "ASTC 6×6 (Mobile Recommended)"
        };

        [Serializable]
        private sealed class PersistedSettings
        {
            public string SourceMaterialPath;
            public int TerrainResolution = 1024;
            public int NoiseResolution = 1024;
            public int FarBasemapResolution = 1024;
            public string OutputFolder;
            public int Compression =
                (int)SLGTerrainBakeCompression.MobileAstc6x6;
        }

        private readonly SLGTerrainBakeSettings _settings =
            new SLGTerrainBakeSettings();

        private Vector2 _scroll;
        private SLGTerrainBakeResult _lastResult;
        private List<string> _validationErrors =
            new List<string>();
        private List<string> _validationWarnings =
            new List<string>();
        private bool _showAdvanced;
        private bool _outputFolderWasAutomatic = true;

        public static void Open()
        {
            var window = GetWindow<SLGTerrainBakeWindow>(
                "SLG Terrain Mobile Baker");
            window.minSize = new Vector2(480f, 650f);
            window.Show();
        }

        private static GUIContent Content(
            string label,
            string tooltip)
        {
            return new GUIContent(label, tooltip);
        }

        private void OnEnable()
        {
            LoadSettings();
            TryUseSelection(false);
            RefreshValidation();
        }

        private void OnDisable()
        {
            SaveSettings();
        }

        private void OnSelectionChange()
        {
            if (_settings.SourceMaterial == null)
            {
                TryUseSelection(false);
                Repaint();
            }
        }

        private void OnProjectChange()
        {
            RefreshValidation();
            Repaint();
        }

        private void OnFocus()
        {
            RefreshValidation();
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField(
                "SLG Terrain Mobile Baker",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "近中景继续使用可平铺地貌纹理，不再把地貌压进整张地图。\n" +
                "工具输出：8层地貌纹理数组、3层全分辨率Noise数组、LOD3远景图和手机材质。" +
                "制作材质和源贴图不会被修改。",
                MessageType.Info);

            EditorGUILayout.Space(8f);
            DrawSourceSection();
            EditorGUILayout.Space(8f);
            DrawOutputSection();
            EditorGUILayout.Space(8f);
            DrawValidationSection();
            EditorGUILayout.Space(8f);
            DrawActionSection();

            if (_lastResult != null)
            {
                EditorGUILayout.Space(10f);
                DrawResultSection();
            }

            EditorGUILayout.EndScrollView();

            var authorStyle = new GUIStyle(
                EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                normal =
                {
                    textColor = new Color(
                        0.45f,
                        0.45f,
                        0.45f,
                        0.6f)
                }
            };
            EditorGUILayout.LabelField(
                "Developed by ZXX",
                authorStyle);
            EditorGUILayout.LabelField(
                "114.21, 30.56",
                authorStyle);
        }

        private void DrawSourceSection()
        {
            EditorGUILayout.LabelField(
                "1. Source Material",
                EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            Material material =
                (Material)EditorGUILayout.ObjectField(
                    Content(
                        "Authoring Material",
                        "选择地形制作材质。工具按所需材质属性判断兼容性，不依赖 Shader 菜单路径。"),
                    _settings.SourceMaterial,
                    typeof(Material),
                    false);
            if (EditorGUI.EndChangeCheck())
            {
                SetSourceMaterial(material, true);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                    Content(
                        "Use Selected Material",
                        "使用Project窗口中当前选中的制作材质。")))
                {
                    TryUseSelection(true);
                }

                using (new EditorGUI.DisabledScope(
                    _settings.SourceMaterial == null))
                {
                    if (GUILayout.Button(
                        Content(
                            "Ping Source Material",
                            "在Project窗口中定位当前制作材质。"),
                        GUILayout.Width(145f)))
                    {
                        Selection.activeObject =
                            _settings.SourceMaterial;
                        EditorGUIUtility.PingObject(
                            _settings.SourceMaterial);
                    }
                }
            }

            if (_settings.SourceMaterial != null)
            {
                string shaderName =
                    _settings.SourceMaterial.shader != null
                        ? _settings.SourceMaterial.shader.name
                        : "Missing Shader";
                MessageType type =
                    SLGTerrainBakeCore.IsSupportedSource(
                        _settings.SourceMaterial)
                        ? MessageType.None
                        : MessageType.Error;
                EditorGUILayout.HelpBox(
                    $"当前Shader：{shaderName}",
                    type);
            }
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.LabelField(
                "2. Output Settings",
                EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _settings.TerrainResolution =
                DrawResolutionPopup(
                    Content(
                        "Terrain Array Resolution",
                        "8个切片统一使用此分辨率。保持平铺，不随地图面积变糊。"),
                    _settings.TerrainResolution,
                    TerrainResolutions);
            _settings.NoiseResolution =
                DrawResolutionPopup(
                    Content(
                        "Noise Resolution",
                        "三个Noise LOD切片统一使用此分辨率。1024更接近制作材质。"),
                    _settings.NoiseResolution,
                    NoiseResolutions);
            _settings.FarBasemapResolution =
                DrawResolutionPopup(
                    Content(
                        "LOD3 Far Basemap Resolution",
                        "只在最远距离显示，可以低于近景地貌纹理。"),
                    _settings.FarBasemapResolution,
                    FarResolutions);
            if (EditorGUI.EndChangeCheck())
            {
                RefreshValidation();
                SaveSettings();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                string outputFolder = EditorGUILayout.TextField(
                    Content(
                        "Output Folder",
                        "重复烘焙会更新同名资源并保持GUID。"),
                    _settings.OutputFolder);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.OutputFolder = outputFolder;
                    _outputFolderWasAutomatic = false;
                    RefreshValidation();
                }

                if (GUILayout.Button(
                    Content(
                        "Select",
                        "选择Assets目录内的输出文件夹。"),
                    GUILayout.Width(52f)))
                {
                    ChooseOutputFolder();
                }
            }

            _showAdvanced = EditorGUILayout.Foldout(
                _showAdvanced,
                Content(
                    "Advanced Settings",
                    "显示纹理压缩格式设置。"),
                true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                int compression = Mathf.Clamp(
                    (int)_settings.Compression,
                    0,
                    CompressionLabels.Length - 1);
                EditorGUI.BeginChangeCheck();
                compression = EditorGUILayout.Popup(
                    Content(
                        "Texture Format",
                        "ASTC 6×6用于手机；不压缩仅用于检查烘焙结果。"),
                    compression,
                    CompressionLabels);
                if (EditorGUI.EndChangeCheck())
                {
                    _settings.Compression =
                        (SLGTerrainBakeCompression)compression;
                    SaveSettings();
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.HelpBox(
                "手机材质运行逻辑：\n" +
                "• LOD0～2：控制图 + 权重最高的两种地貌主次图 + 1张Noise\n" +
                "• LOD3：按材质开关读取远景图或Bottom\n" +
                "• 普通纹理Mipmap只负责过滤，不再保存艺术LOD",
                MessageType.None);

            float memory =
                SLGTerrainBakeCore.EstimateRuntimeTextureMemoryMb(
                    _settings);
            EditorGUILayout.LabelField(
                $"预计新增运行时纹理显存：约 {memory:0.0} MB（含Mipmap，不含原控制图和Bottom）",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawValidationSection()
        {
            EditorGUILayout.LabelField(
                "3. Bake Validation",
                EditorStyles.boldLabel);

            if (_validationErrors.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "检查通过，可以开始烘焙。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "需要先处理：\n• " +
                    string.Join("\n• ", _validationErrors),
                    MessageType.Error);
            }

            if (_validationWarnings.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "注意：\n• " +
                    string.Join("\n• ", _validationWarnings),
                    MessageType.Warning);
            }

            string[] paths =
                SLGTerrainBakeCore.GetExpectedOutputPaths(_settings);
            if (paths.Length > 0)
            {
                int existing = 0;
                foreach (string path in paths)
                {
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                    {
                        existing++;
                    }
                }

                EditorGUILayout.LabelField(
                    existing == 0
                        ? "将创建4个新资源。"
                        : $"将更新{existing}个已有资源，并创建{paths.Length - existing}个新资源；已有GUID保持不变。",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        private void DrawActionSection()
        {
            using (new EditorGUI.DisabledScope(
                _validationErrors.Count > 0))
            {
                Color previous = GUI.backgroundColor;
                GUI.backgroundColor =
                    new Color(0.58f, 0.92f, 0.58f);
                if (GUILayout.Button(
                    Content(
                        "Generate Mobile Terrain Assets",
                        "生成或更新地貌数组、Noise数组、远景图和手机材质。"),
                    GUILayout.Height(38f)))
                {
                    Bake();
                }
                GUI.backgroundColor = previous;
            }
        }

        private void DrawResultSection()
        {
            EditorGUILayout.LabelField(
                "4. Last Result",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "烘焙完成。把手机材质赋给地面即可进行效果和性能对比。",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(
                    Content(
                        "Mobile Material",
                        "工具生成的移动端运行材质。"),
                    _lastResult.RuntimeMaterial,
                    typeof(Material),
                    false);
                EditorGUILayout.ObjectField(
                    Content(
                        "Terrain Texture Array",
                        "包含4张主图和4张次图的纹理数组。"),
                    _lastResult.TerrainArray,
                    typeof(Texture2DArray),
                    false);
                EditorGUILayout.ObjectField(
                    Content(
                        "Noise LOD Array",
                        "以三个全分辨率切片保存LOD0、LOD1、LOD2的Noise结果。"),
                    _lastResult.NoiseArray,
                    typeof(Texture2DArray),
                    false);
                EditorGUILayout.ObjectField(
                    Content(
                        "LOD3 Far Basemap",
                        "完全进入LOD3后使用的远景底图。"),
                    _lastResult.FarBasemap,
                    typeof(Texture2D),
                    false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(
                    Content(
                        "Ping Mobile Material",
                        "在Project窗口中定位生成的手机材质。")))
                {
                    Selection.activeObject =
                        _lastResult.RuntimeMaterial;
                    EditorGUIUtility.PingObject(
                        _lastResult.RuntimeMaterial);
                }

                if (GUILayout.Button(
                    Content(
                        "Open Output Folder",
                        "在Project窗口中打开烘焙资源输出目录。")))
                {
                    UnityEngine.Object folder =
                        AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                            _settings.OutputFolder);
                    Selection.activeObject = folder;
                    EditorGUIUtility.PingObject(folder);
                }
            }
        }

        private static int DrawResolutionPopup(
            GUIContent label,
            int current,
            int[] values)
        {
            string[] labels = new string[values.Length];
            int selected = 0;
            for (int i = 0; i < values.Length; i++)
            {
                labels[i] = values[i].ToString();
                if (values[i] == current)
                {
                    selected = i;
                }
            }
            selected = EditorGUILayout.Popup(
                label,
                selected,
                labels);
            return values[selected];
        }

        private void SetSourceMaterial(
            Material material,
            bool resetAutomaticFolder)
        {
            if (_settings.SourceMaterial == material)
            {
                return;
            }

            _settings.SourceMaterial = material;
            _lastResult = null;
            if (material != null &&
                (resetAutomaticFolder ||
                 _outputFolderWasAutomatic ||
                 string.IsNullOrWhiteSpace(
                     _settings.OutputFolder)))
            {
                _settings.OutputFolder =
                    SLGTerrainBakeCore.GetDefaultOutputFolder(
                        material);
                _outputFolderWasAutomatic = true;
            }

            RefreshValidation();
            SaveSettings();
        }

        private void TryUseSelection(bool showMessage)
        {
            Material selected = Selection.activeObject as Material;
            if (selected != null &&
                SLGTerrainBakeCore.IsSupportedSource(selected))
            {
                SetSourceMaterial(selected, true);
                return;
            }

            if (showMessage)
            {
                EditorUtility.DisplayDialog(
                    "No Valid Material",
                    "请在Project窗口选中包含控制图、Bottom底图和四种地貌主图属性的制作材质。",
                    "OK");
            }
        }

        private void ChooseOutputFolder()
        {
            string currentAbsolute =
                GetOutputFolderPickerStartPath(
                    _settings.OutputFolder);

            string selected = EditorUtility.OpenFolderPanel(
                "Select SLG Terrain Output Folder",
                currentAbsolute,
                string.Empty);
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            string projectRoot = Path.GetFullPath(
                    Path.Combine(Application.dataPath, ".."))
                .Replace('\\', '/')
                .TrimEnd('/');
            string normalized = Path.GetFullPath(selected)
                .Replace('\\', '/')
                .TrimEnd('/');
            string assetsRoot = Path.GetFullPath(
                    Application.dataPath)
                .Replace('\\', '/')
                .TrimEnd('/');
            bool isInsideAssets =
                string.Equals(
                    normalized,
                    assetsRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(
                    assetsRoot + "/",
                    StringComparison.OrdinalIgnoreCase);
            if (!isInsideAssets)
            {
                EditorUtility.DisplayDialog(
                    "Invalid Folder",
                    "输出目录必须位于当前项目的Assets文件夹内。",
                    "OK");
                return;
            }

            _settings.OutputFolder =
                normalized.Substring(projectRoot.Length + 1);
            _outputFolderWasAutomatic = false;
            RefreshValidation();
            SaveSettings();
        }

        private static string GetOutputFolderPickerStartPath(
            string outputFolder)
        {
            string assetsRoot = Path.GetFullPath(
                Application.dataPath);
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                return assetsRoot;
            }

            string normalizedAssetPath = outputFolder
                .Trim()
                .Replace('\\', '/')
                .TrimEnd('/');
            bool isAssetPath =
                string.Equals(
                    normalizedAssetPath,
                    "Assets",
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedAssetPath.StartsWith(
                    "Assets/",
                    StringComparison.OrdinalIgnoreCase);
            if (!isAssetPath)
            {
                return assetsRoot;
            }

            string projectRoot = Path.GetFullPath(
                Path.Combine(Application.dataPath, ".."));
            string candidate = Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    normalizedAssetPath));
            string normalizedAssetsRoot = assetsRoot
                .Replace('\\', '/')
                .TrimEnd('/');
            string normalizedCandidate = candidate
                .Replace('\\', '/')
                .TrimEnd('/');
            bool isInsideAssets =
                string.Equals(
                    normalizedCandidate,
                    normalizedAssetsRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.StartsWith(
                    normalizedAssetsRoot + "/",
                    StringComparison.OrdinalIgnoreCase);
            if (!isInsideAssets)
            {
                return assetsRoot;
            }

            while (!Directory.Exists(candidate))
            {
                DirectoryInfo parent =
                    Directory.GetParent(candidate);
                if (parent == null)
                {
                    return assetsRoot;
                }

                candidate = parent.FullName;
            }

            return candidate;
        }

        private void Bake()
        {
            RefreshValidation();
            if (_validationErrors.Count > 0)
            {
                return;
            }

            string[] paths =
                SLGTerrainBakeCore.GetExpectedOutputPaths(_settings);
            int existing = 0;
            foreach (string path in paths)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                {
                    existing++;
                }
            }

            if (existing > 0 &&
                !EditorUtility.DisplayDialog(
                    "Confirm Rebake",
                    $"将更新{existing}个已有资源，GUID保持不变。\n" +
                    "制作材质和源贴图不会被修改。",
                    "Continue",
                    "Cancel"))
            {
                return;
            }

            SaveSettings();
            try
            {
                _lastResult = SLGTerrainBakeCore.Bake(
                    _settings,
                    (message, progress) =>
                        EditorUtility.DisplayCancelableProgressBar(
                            "SLG Terrain Mobile Baker",
                            message,
                            progress),
                    out string error);

                if (_lastResult == null)
                {
                    if (!string.IsNullOrEmpty(error))
                    {
                        EditorUtility.DisplayDialog(
                            "Bake Incomplete",
                            error,
                            "OK");
                    }
                    return;
                }

                RefreshValidation();
                EditorUtility.DisplayDialog(
                    "Bake Complete",
                    "已生成地貌纹理数组、Noise数组、LOD3远景图和手机材质。\n\n" +
                    "旧版四张Baked贴图没有自动删除，可以在确认新方案后手动清理。",
                    "OK");
                Selection.activeObject =
                    _lastResult.RuntimeMaterial;
                EditorGUIUtility.PingObject(
                    _lastResult.RuntimeMaterial);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void RefreshValidation()
        {
            SLGTerrainBakeCore.Validate(
                _settings,
                out _validationErrors,
                out _validationWarnings);
        }

        private void SaveSettings()
        {
            var persisted = new PersistedSettings
            {
                SourceMaterialPath =
                    _settings.SourceMaterial != null
                        ? AssetDatabase.GetAssetPath(
                            _settings.SourceMaterial)
                        : string.Empty,
                TerrainResolution =
                    _settings.TerrainResolution,
                NoiseResolution =
                    _settings.NoiseResolution,
                FarBasemapResolution =
                    _settings.FarBasemapResolution,
                OutputFolder = _settings.OutputFolder,
                Compression = (int)_settings.Compression
            };
            EditorPrefs.SetString(
                PrefKey,
                JsonUtility.ToJson(persisted));
        }

        private void LoadSettings()
        {
            string json =
                EditorPrefs.GetString(PrefKey, string.Empty);
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    PersistedSettings persisted =
                        JsonUtility.FromJson<PersistedSettings>(
                            json);
                    if (persisted != null)
                    {
                        _settings.SourceMaterial =
                            AssetDatabase.LoadAssetAtPath<Material>(
                                persisted.SourceMaterialPath);
                        _settings.TerrainResolution =
                            IsOneOf(
                                persisted.TerrainResolution,
                                TerrainResolutions)
                                ? persisted.TerrainResolution
                                : 1024;
                        _settings.NoiseResolution =
                            IsOneOf(
                                persisted.NoiseResolution,
                                NoiseResolutions)
                                ? persisted.NoiseResolution
                                : 1024;
                        _settings.FarBasemapResolution =
                            IsOneOf(
                                persisted.FarBasemapResolution,
                                FarResolutions)
                                ? persisted.FarBasemapResolution
                                : 1024;
                        _settings.OutputFolder =
                            persisted.OutputFolder;
                        _settings.Compression =
                            Enum.IsDefined(
                                typeof(
                                    SLGTerrainBakeCompression),
                                persisted.Compression)
                                ? (SLGTerrainBakeCompression)
                                    persisted.Compression
                                : SLGTerrainBakeCompression
                                    .MobileAstc6x6;
                        _outputFolderWasAutomatic =
                            string.IsNullOrWhiteSpace(
                                persisted.OutputFolder);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[SLGTerrainBaker] 设置读取失败，" +
                        $"将使用默认值：{exception.Message}");
                }
            }

            if (_settings.SourceMaterial != null &&
                string.IsNullOrWhiteSpace(
                    _settings.OutputFolder))
            {
                _settings.OutputFolder =
                    SLGTerrainBakeCore.GetDefaultOutputFolder(
                        _settings.SourceMaterial);
                _outputFolderWasAutomatic = true;
            }
        }

        private static bool IsOneOf(
            int value,
            int[] options)
        {
            foreach (int option in options)
            {
                if (option == value)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
