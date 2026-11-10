using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed class ToonMaterialConverterWindow : EditorWindow
{
    private const string StandardShaderName = "Custom/ToonOutline";
    private const string PackedShaderName = "Custom/ToonOutlinePacked";
    private const string ConversionShaderName = "Hidden/ZXXTools/ToonTextureConversion";
    private const string OutputFolderKey = "ZXXTools.ToonMaterialConverter.OutputFolder";
    private const string OutputBesideSourcesKey = "ZXXTools.ToonMaterialConverter.OutputBesideSources";
    private const string DeleteOldMaterialKey = "ZXXTools.ToonMaterialConverter.DeleteOldMaterial";
    private const string DeleteOldTexturesKey = "ZXXTools.ToonMaterialConverter.DeleteOldTextures";
    private const string BaseMaxSizeKey = "ZXXTools.ToonMaterialConverter.BaseMaxSize";
    private const string PackedMaxSizeKey = "ZXXTools.ToonMaterialConverter.PackedMaxSize";
    private const string JpegQualityKey = "ZXXTools.ToonMaterialConverter.JpegQuality";
    private const string PackedCompressionKey = "ZXXTools.ToonMaterialConverter.PackedCompression";
    private const string DefaultOutputFolder = "Assets/Generated/ToonMaterialConverter";

    private static readonly string[] TextureSizeLabels = { "Source", "256", "512", "1024", "2048", "4096" };
    private static readonly int[] TextureSizeValues = { 0, 256, 512, 1024, 2048, 4096 };

    private enum PackedCompressionMode
    {
        Uncompressed,
        HighQuality
    }

    private readonly List<Material> _materials = new List<Material>();
    private Vector2 _scroll;
    private string _outputFolder;
    private bool _outputBesideSources;
    private bool _deleteOldMaterial;
    private bool _deleteOldTextures;
    private int _baseMaxSize;
    private int _packedMaxSize;
    private int _jpegQuality;
    private PackedCompressionMode _packedCompression;

    public static void Open()
    {
        ToonMaterialConverterWindow window = GetWindow<ToonMaterialConverterWindow>("Toon Material Converter");
        window.titleContent = Content("Toon Material Converter", "ToonOutline 材质与贴图双向转换工具。");
        window.minSize = new Vector2(470f, 430f);
        window.Show();
    }

    private static GUIContent Content(string label, string tooltip)
    {
        return new GUIContent(label, tooltip);
    }

    private void OnEnable()
    {
        titleContent = Content("Toon Material Converter", "ToonOutline 材质与贴图双向转换工具。");
        _outputFolder = EditorPrefs.GetString(OutputFolderKey, DefaultOutputFolder);
        _outputBesideSources = EditorPrefs.GetBool(OutputBesideSourcesKey, true);
        _deleteOldMaterial = EditorPrefs.GetBool(DeleteOldMaterialKey, false);
        _deleteOldTextures = EditorPrefs.GetBool(DeleteOldTexturesKey, false);
        _baseMaxSize = NormalizeTextureSize(EditorPrefs.GetInt(BaseMaxSizeKey, 0));
        _packedMaxSize = NormalizeTextureSize(EditorPrefs.GetInt(PackedMaxSizeKey, 0));
        _jpegQuality = Mathf.Clamp(EditorPrefs.GetInt(JpegQualityKey, 95), 1, 100);
        _packedCompression = (PackedCompressionMode)Mathf.Clamp(
            EditorPrefs.GetInt(PackedCompressionKey, (int)PackedCompressionMode.Uncompressed),
            0,
            Enum.GetValues(typeof(PackedCompressionMode)).Length - 1);
        if (!_deleteOldMaterial && _deleteOldTextures)
        {
            _deleteOldTextures = false;
            EditorPrefs.SetBool(DeleteOldTexturesKey, false);
        }
        if (_materials.Count == 0)
            CollectSelection();
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "普通版 → 打包版：\n"
            + "  _BC.jpg：RGB = Base Color\n"
            + "  _N_M_R.tga：RG = Normal XY，B = Metallic，A = Roughness\n\n"
            + "打包版 → 普通版：自动拆出 Normal 与 Metallic/Smoothness。\n"
            + "转换会生成新材质，不会覆盖源材质。",
            MessageType.Info);

        DrawMaterialList();
        DrawOutputFolder();
        DrawTextureOutputSettings();
        DrawCleanupOptions();

        using (new EditorGUI.DisabledScope(_materials.Count == 0))
        {
            if (GUILayout.Button(
                    Content("Convert (Auto Detect)", "自动识别当前材质使用的 Shader，并转换到另一个版本。"),
                    GUILayout.Height(36f)))
                ConvertAll();
        }

        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    private void DrawTextureOutputSettings()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField(
            Content("Texture Output", "设置输出贴图尺寸、JPEG 质量以及运行时压缩格式。"),
            EditorStyles.boldLabel);

        int baseSizeIndex = Mathf.Max(0, Array.IndexOf(TextureSizeValues, _baseMaxSize));
        int nextBaseSizeIndex = EditorGUILayout.Popup(
            Content("BC Max Size", "限制 _BC 的最长边；Source 表示使用当前源贴图尺寸，不会主动放大。"),
            baseSizeIndex,
            TextureSizeLabels);
        if (nextBaseSizeIndex != baseSizeIndex)
        {
            _baseMaxSize = TextureSizeValues[nextBaseSizeIndex];
            EditorPrefs.SetInt(BaseMaxSizeKey, _baseMaxSize);
        }

        int packedSizeIndex = Mathf.Max(0, Array.IndexOf(TextureSizeValues, _packedMaxSize));
        int nextPackedSizeIndex = EditorGUILayout.Popup(
            Content("NMR Max Size", "限制 _N_M_R 的最长边并保持宽高比；Source 表示使用法线源文件的原始尺寸。"),
            packedSizeIndex,
            TextureSizeLabels);
        if (nextPackedSizeIndex != packedSizeIndex)
        {
            _packedMaxSize = TextureSizeValues[nextPackedSizeIndex];
            EditorPrefs.SetInt(PackedMaxSizeKey, _packedMaxSize);
        }

        int nextJpegQuality = EditorGUILayout.IntSlider(
            Content("BC JPEG Quality", "控制输出 JPG 文件质量。数值越低文件越小，但颜色压缩痕迹越明显。"),
            _jpegQuality,
            1,
            100);
        if (nextJpegQuality != _jpegQuality)
        {
            _jpegQuality = nextJpegQuality;
            EditorPrefs.SetInt(JpegQualityKey, _jpegQuality);
        }

        PackedCompressionMode nextCompression = (PackedCompressionMode)EditorGUILayout.EnumPopup(
            Content("NMR Compression", "Uncompressed：质量最高且无块压缩；High Quality：显存和包体更小，但可能出现轻微压缩方块。"),
            _packedCompression);
        if (nextCompression != _packedCompression)
        {
            _packedCompression = nextCompression;
            EditorPrefs.SetInt(PackedCompressionKey, (int)_packedCompression);
        }

        EditorGUILayout.HelpBox(
            _packedCompression == PackedCompressionMode.Uncompressed
                ? "当前 NMR 使用 RGBA32：法线质量最高，但显存和包体较大。"
                : "当前 NMR 使用高质量平台压缩：PC 为 BC7，移动端为 ASTC 4×4；更省内存，但无法保证完全没有块压缩痕迹。",
            MessageType.None);
    }

    private void DrawCleanupOptions()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.LabelField(
            Content("Cleanup After Conversion", "配置转换成功后是否清理旧材质和旧贴图。"),
            EditorStyles.boldLabel);

        bool deleteMaterial = EditorGUILayout.ToggleLeft(
            Content("Replace Old Material", "转换结果写回原材质并删除临时新材质，保留原 GUID 以及场景和 Prefab 引用。"),
            _deleteOldMaterial);
        if (deleteMaterial != _deleteOldMaterial)
        {
            _deleteOldMaterial = deleteMaterial;
            if (!_deleteOldMaterial)
            {
                _deleteOldTextures = false;
                EditorPrefs.SetBool(DeleteOldTexturesKey, false);
            }
            EditorPrefs.SetBool(DeleteOldMaterialKey, _deleteOldMaterial);
        }

        bool deleteTextures;
        using (new EditorGUI.DisabledScope(!_deleteOldMaterial))
        {
            deleteTextures = EditorGUILayout.ToggleLeft(
                Content("Delete Old Textures", "仅在旧材质替换成功后删除旧贴图；仍被其他资源引用的贴图会自动保留并列出引用者。"),
                _deleteOldTextures);
        }
        if (deleteTextures != _deleteOldTextures)
        {
            _deleteOldTextures = deleteTextures;
            EditorPrefs.SetBool(DeleteOldTexturesKey, _deleteOldTextures);
        }

        if (!_deleteOldMaterial)
        {
            EditorGUILayout.HelpBox(
                "删除旧贴图前必须先替换旧材质，否则旧材质自身仍会引用这些贴图。",
                MessageType.None);
        }

        if (_deleteOldMaterial || _deleteOldTextures)
        {
            EditorGUILayout.HelpBox(
                "删除只会在对应材质转换成功后执行，并在执行前再次确认。旧材质会被安全地就地替换，不会产生 Missing Material。反向转换时 _BC 会继续被转换后材质使用，因此不会删除。",
                MessageType.Warning);
        }
    }

    private void DrawMaterialList()
    {
        EditorGUILayout.Space(4f);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            Content($"Materials ({_materials.Count})", "当前等待转换的材质列表。"),
            EditorStyles.boldLabel);
        if (GUILayout.Button(Content("Load Selection", "读取 Project、Hierarchy 当前选择中的材质。"), GUILayout.Width(105f)))
            CollectSelection();
        if (GUILayout.Button(Content("Clear", "清空当前材质列表。"), GUILayout.Width(55f)))
            _materials.Clear();
        EditorGUILayout.EndHorizontal();

        Rect dropRect = GUILayoutUtility.GetRect(0f, 38f, GUILayout.ExpandWidth(true));
        GUI.Box(
            dropRect,
            Content("Drag Materials, Prefabs or GameObjects Here", "拖入材质、Prefab 或场景物体，会自动收集其中使用的材质。"),
            EditorStyles.helpBox);
        HandleDragAndDrop(dropRect);

        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(90f), GUILayout.MaxHeight(150f));
        for (int i = 0; i < _materials.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            _materials[i] = (Material)EditorGUILayout.ObjectField(
                Content($"Material {i + 1}", "需要进行 Toon Shader 转换的材质球。"),
                _materials[i],
                typeof(Material),
                false);
            if (GUILayout.Button(Content("×", "从转换列表移除此材质。"), GUILayout.Width(24f)))
            {
                _materials.RemoveAt(i--);
            }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawOutputFolder()
    {
        EditorGUILayout.Space(4f);
        bool nextOutputBesideSources = EditorGUILayout.ToggleLeft(
            Content("Output Beside Source Assets", "材质输出到源材质目录；贴图输出到各自对应的源贴图目录。"),
            _outputBesideSources);
        if (nextOutputBesideSources != _outputBesideSources)
        {
            _outputBesideSources = nextOutputBesideSources;
            EditorPrefs.SetBool(OutputBesideSourcesKey, _outputBesideSources);
        }

        using (new EditorGUI.DisabledScope(_outputBesideSources))
        {
            EditorGUILayout.BeginHorizontal();
            string nextFolder = EditorGUILayout.TextField(
                Content("Output Folder", "关闭原目录输出时，所有生成资源统一保存到这个 Assets 目录。"),
                _outputFolder);
            if (nextFolder != _outputFolder)
                SetOutputFolder(nextFolder);

            if (GUILayout.Button(Content("Browse", "选择 Assets 内的统一输出目录。"), GUILayout.Width(64f)))
            {
                string selected = EditorUtility.OpenFolderPanel(
                    "Select Toon Conversion Output Folder",
                    AssetFolderToAbsolute(_outputFolder),
                    string.Empty);
                if (!string.IsNullOrEmpty(selected))
                {
                    string assetFolder = AbsoluteToAssetFolder(selected);
                    if (!string.IsNullOrEmpty(assetFolder))
                        SetOutputFolder(assetFolder);
                    else
                        EditorUtility.DisplayDialog("Invalid Folder", "请选择当前 Unity 项目的 Assets 目录或其子目录。", "OK");
                }
            }

            using (new EditorGUI.DisabledScope(!AssetDatabase.IsValidFolder(_outputFolder)))
            {
                if (GUILayout.Button(Content("Show", "在 Project 窗口中定位当前输出目录。"), GUILayout.Width(48f)))
                {
                    UnityEngine.Object folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(_outputFolder);
                    EditorGUIUtility.PingObject(folder);
                    Selection.activeObject = folder;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        if (_outputBesideSources)
        {
            EditorGUILayout.HelpBox(
                "默认输出：新材质 → 源材质目录；_BC → 原 BaseMap 目录；_N_M_R → 原 Normal Map 目录（缺失时依次回退到 Metallic、BaseMap、材质目录）。",
                MessageType.None);
        }
    }

    private void CollectSelection()
    {
        _materials.Clear();
        AddObjects(Selection.objects);
        Repaint();
    }

    private void AddObjects(IEnumerable<UnityEngine.Object> objects)
    {
        foreach (UnityEngine.Object selected in objects)
        {
            if (selected is Material material)
            {
                AddMaterial(material);
                continue;
            }

            if (selected is GameObject gameObject)
            {
                foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material sharedMaterial in renderer.sharedMaterials)
                        AddMaterial(sharedMaterial);
                }
            }
        }
    }

    private void AddMaterial(Material material)
    {
        if (material != null && !_materials.Contains(material))
            _materials.Add(material);
    }

    private void HandleDragAndDrop(Rect dropRect)
    {
        Event current = Event.current;
        if (!dropRect.Contains(current.mousePosition))
            return;

        if (current.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            current.Use();
        }
        else if (current.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            AddObjects(DragAndDrop.objectReferences);
            current.Use();
        }
    }

    private void ConvertAll()
    {
        var usageTimer = ZXXToolUsageStats.Begin();
        _materials.RemoveAll(material => material == null);
        if (_materials.Count == 0)
            return;

        if ((_deleteOldMaterial || _deleteOldTextures)
            && !EditorUtility.DisplayDialog(
                "Confirm Cleanup",
                "转换成功后将按当前选项删除旧材质球和/或旧贴图。\n\n"
                + "旧材质会在原路径就地替换以保留引用；旧贴图仍被其他资源引用时会自动保留。是否继续？",
                "Convert and Delete",
                "Cancel"))
        {
            return;
        }

        string requiredFolder = _outputBesideSources ? DefaultOutputFolder : _outputFolder;
        if (!TryEnsureAssetFolder(requiredFolder))
        {
            EditorUtility.DisplayDialog("Invalid Output Folder", "输出目录必须位于当前项目 Assets 内。", "OK");
            return;
        }

        Shader standardShader = Shader.Find(StandardShaderName);
        Shader packedShader = Shader.Find(PackedShaderName);
        Shader conversionShader = Shader.Find(ConversionShaderName);
        if (standardShader == null || packedShader == null || conversionShader == null)
        {
            EditorUtility.DisplayDialog(
                "Shader Missing",
                $"请确认以下 Shader 均已成功编译：\n{StandardShaderName}\n{PackedShaderName}\n{ConversionShaderName}",
                "OK");
            return;
        }

        var outputs = new List<Material>();
        var errors = new List<string>();
        var oldTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int replacedMaterialCount = 0;
        Material conversionMaterial = new Material(conversionShader) { hideFlags = HideFlags.HideAndDontSave };

        try
        {
            foreach (Material source in _materials)
            {
                try
                {
                    Material output;
                    bool standardToPacked = source.shader != null && source.shader.name == StandardShaderName;
                    if (standardToPacked)
                        output = ConvertToPacked(source, packedShader, conversionMaterial);
                    else if (source.shader != null && source.shader.name == PackedShaderName)
                        output = ConvertToStandard(source, standardShader, conversionMaterial);
                    else
                        throw new InvalidOperationException("不是 ToonOutline 普通版或打包版材质");

                    if (output != null)
                    {
                        var sourceTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        CollectTextureCleanupCandidates(source, standardToPacked, sourceTexturePaths);
                        if (_deleteOldMaterial && TryReplaceSourceMaterial(source, output, out Material replacedMaterial))
                        {
                            oldTexturePaths.UnionWith(sourceTexturePaths);
                            output = replacedMaterial;
                            replacedMaterialCount++;
                        }
                        outputs.Add(output);
                    }
                }
                catch (Exception exception)
                {
                    errors.Add($"{source.name}: {exception.Message}");
                    Debug.LogException(exception);
                }
            }
        }
        finally
        {
            DestroyImmediate(conversionMaterial);
            AssetDatabase.SaveAssets();
        }

        CleanupResult cleanup = DeleteOldTextures(oldTexturePaths);

        if (outputs.Count > 0)
        {
            Selection.objects = outputs.Cast<UnityEngine.Object>().ToArray();
            EditorGUIUtility.PingObject(outputs[0]);
        }

        ZXXToolUsageStats.Complete("Toon Material Converter", outputs.Count, usageTimer);
        string message = $"转换完成：{outputs.Count} 个材质";
        if (_deleteOldMaterial || _deleteOldTextures)
        {
            message += $"\n已替换：{replacedMaterialCount} 个旧材质；已删除：{cleanup.deletedTextures} 张旧贴图";
            if (cleanup.skippedTextures.Count > 0)
            {
                message += $"\n保留共用贴图：{cleanup.skippedTextures.Count} 张"
                           + "\n\n未删除贴图及引用资源：\n"
                           + string.Join("\n", cleanup.skippedTextureDetails);
            }
        }
        if (errors.Count > 0)
            message += $"\n失败：{errors.Count} 个\n\n" + string.Join("\n", errors);
        EditorUtility.DisplayDialog("Toon Material Converter", message, "OK");
    }

    private void CollectTextureCleanupCandidates(
        Material source,
        bool standardToPacked,
        HashSet<string> texturePaths)
    {
        if (!_deleteOldTextures)
            return;

        if (standardToPacked)
        {
            AddTexturePath(source.GetTexture("_BaseMap"), texturePaths);
            AddTexturePath(source.GetTexture("_NormalMap"), texturePaths);
            AddTexturePath(source.GetTexture("_MetallicGlossMap"), texturePaths);
        }
        else
        {
            // _BC is reused by the generated standard material; only the old packed map can be removed.
            AddTexturePath(source.GetTexture("_N_M_R"), texturePaths);
        }
    }

    private static bool TryReplaceSourceMaterial(Material source, Material converted, out Material result)
    {
        result = converted;
        string sourcePath = AssetDatabase.GetAssetPath(source);
        string convertedPath = AssetDatabase.GetAssetPath(converted);
        if (!string.Equals(Path.GetExtension(sourcePath), ".mat", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(convertedPath))
        {
            Debug.LogWarning($"[Toon Material Converter] {source.name} 不是独立 .mat 文件，无法安全替换，已保留新旧两个材质。");
            return false;
        }

        string originalName = source.name;
        EditorUtility.CopySerialized(converted, source);
        source.name = originalName;
        EditorUtility.SetDirty(source);
        AssetDatabase.DeleteAsset(convertedPath);
        result = source;
        return true;
    }

    private static void AddTexturePath(Texture texture, HashSet<string> paths)
    {
        string path = texture != null ? AssetDatabase.GetAssetPath(texture) : string.Empty;
        if (!string.IsNullOrEmpty(path)
            && path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
            && IsStandaloneTexturePath(path))
        {
            paths.Add(path);
        }
    }

    private static bool IsStandaloneTexturePath(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".png"
               || extension == ".jpg"
               || extension == ".jpeg"
               || extension == ".tga"
               || extension == ".tif"
               || extension == ".tiff"
               || extension == ".psd"
               || extension == ".exr"
               || extension == ".bmp";
    }

    private struct CleanupResult
    {
        public int deletedTextures;
        public List<string> skippedTextures;
        public List<string> skippedTextureDetails;
    }

    private static CleanupResult DeleteOldTextures(HashSet<string> texturePaths)
    {
        var result = new CleanupResult
        {
            skippedTextures = new List<string>(),
            skippedTextureDetails = new List<string>()
        };
        AssetDatabase.SaveAssets();
        Dictionary<string, List<string>> textureReferences = FindTextureReferences(texturePaths);
        foreach (string texturePath in texturePaths)
        {
            if (textureReferences.TryGetValue(texturePath, out List<string> referencingAssets))
            {
                result.skippedTextures.Add(texturePath);
                string detail = texturePath + " ← " + string.Join("，", referencingAssets);
                result.skippedTextureDetails.Add(detail);
                Debug.Log(
                    $"[Toon Material Converter] 未删除贴图：{texturePath}\n"
                    + "仍在引用的资源：\n- "
                    + string.Join("\n- ", referencingAssets));
                continue;
            }

            if (AssetDatabase.DeleteAsset(texturePath))
                result.deletedTextures++;
        }

        if (result.deletedTextures > 0)
            AssetDatabase.SaveAssets();
        return result;
    }

    private static Dictionary<string, List<string>> FindTextureReferences(HashSet<string> candidates)
    {
        var references = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
            return references;

        string[] assetPaths = AssetDatabase.GetAllAssetPaths()
            .Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                           && !candidates.Contains(path)
                           && !AssetDatabase.IsValidFolder(path))
            .ToArray();

        // Query whole groups first and only split groups that actually contain a
        // candidate dependency. This keeps complete reference reporting without
        // issuing one AssetDatabase call for every asset in a large project.
        CollectTextureReferences(assetPaths, 0, assetPaths.Length, candidates, references);
        return references;
    }

    private static void CollectTextureReferences(
        string[] assetPaths,
        int start,
        int count,
        HashSet<string> candidates,
        Dictionary<string, List<string>> references)
    {
        if (count <= 0)
            return;

        var group = new string[count];
        Array.Copy(assetPaths, start, group, 0, count);
        string[] dependencies = AssetDatabase.GetDependencies(group, false);
        var matchedCandidates = new List<string>();
        foreach (string dependencyPath in dependencies)
        {
            if (candidates.Contains(dependencyPath))
                matchedCandidates.Add(dependencyPath);
        }

        if (matchedCandidates.Count == 0)
            return;

        if (count == 1)
        {
            foreach (string texturePath in matchedCandidates)
            {
                if (!references.TryGetValue(texturePath, out List<string> referencingAssets))
                {
                    referencingAssets = new List<string>();
                    references.Add(texturePath, referencingAssets);
                }
                referencingAssets.Add(assetPaths[start]);
            }
            return;
        }

        int leftCount = count / 2;
        CollectTextureReferences(assetPaths, start, leftCount, candidates, references);
        CollectTextureReferences(assetPaths, start + leftCount, count - leftCount, candidates, references);
    }

    private Material ConvertToPacked(Material source, Shader packedShader, Material conversionMaterial)
    {
        Texture baseMap = source.GetTexture("_BaseMap");
        Texture normalMap = source.GetTexture("_NormalMap");
        Texture metallicGlossMap = source.GetTexture("_MetallicGlossMap");
        Vector2Int baseSize = LimitTextureSize(ChooseSize(baseMap), _baseMaxSize);
        Vector2Int packedSize = ChooseSize(normalMap, metallicGlossMap, baseMap);
        string safeName = SanitizeFileName(source.name);
        string materialFolder = ResolveOutputFolder(source, DefaultOutputFolder + "/Materials");
        string baseFolder = ResolveOutputFolder(baseMap, DefaultOutputFolder + "/Textures");
        string packedFolder = ResolveOutputFolder(normalMap ?? metallicGlossMap ?? baseMap,
            DefaultOutputFolder + "/Textures");

        string basePath = GenerateUniqueOutputPath(baseFolder, safeName + "_BC.jpg");
        string packedPath = GenerateUniqueOutputPath(packedFolder, safeName + "_N_M_R.tga");
        string materialPath = GenerateUniqueOutputPath(materialFolder, safeName + "_Packed.mat");

        conversionMaterial.SetTexture("_BaseTex", baseMap != null ? baseMap : Texture2D.whiteTexture);
        Texture2D baseOutput = RenderPassToTexture(conversionMaterial, 0, baseSize.x, baseSize.y, false);
        WriteTexture(basePath, ImageConversion.EncodeToJPG(baseOutput, _jpegQuality));
        DestroyImmediate(baseOutput);

        Texture2D packedOutput = CreatePackedTextureFromSourceFiles(
            normalMap,
            metallicGlossMap,
            packedSize.x,
            packedSize.y,
            _packedMaxSize);
        WriteTexture(packedPath, ImageConversion.EncodeToTGA(packedOutput));
        DestroyImmediate(packedOutput);

        AssetDatabase.ImportAsset(basePath, ImportAssetOptions.ForceSynchronousImport);
        ConfigureBaseImporter(basePath);
        AssetDatabase.ImportAsset(packedPath, ImportAssetOptions.ForceSynchronousImport);
        ConfigurePackedImporter(packedPath, _packedCompression);

        Material output = CreateConvertedMaterial(source, packedShader, materialPath);
        output.SetTexture("_BC", AssetDatabase.LoadAssetAtPath<Texture2D>(basePath));
        output.SetTextureScale("_BC", source.GetTextureScale("_BaseMap"));
        output.SetTextureOffset("_BC", source.GetTextureOffset("_BaseMap"));
        output.SetTexture("_N_M_R", AssetDatabase.LoadAssetAtPath<Texture2D>(packedPath));
        EditorUtility.SetDirty(output);
        return output;
    }

    private Material ConvertToStandard(Material source, Shader standardShader, Material conversionMaterial)
    {
        Texture baseMap = source.GetTexture("_BC");
        Texture packedNMR = source.GetTexture("_N_M_R");
        Vector2Int size = LimitTextureSize(ChooseSize(packedNMR, baseMap), _packedMaxSize);
        string safeName = SanitizeFileName(source.name);
        string materialFolder = ResolveOutputFolder(source, DefaultOutputFolder + "/Materials");
        string textureFolder = ResolveOutputFolder(packedNMR, DefaultOutputFolder + "/Textures");

        string normalPath = GenerateUniqueOutputPath(textureFolder, safeName + "_N.tga");
        string metallicPath = GenerateUniqueOutputPath(textureFolder, safeName + "_M_S.tga");
        string materialPath = GenerateUniqueOutputPath(materialFolder, safeName + "_Standard.mat");

        conversionMaterial.SetTexture("_PackedTex", packedNMR != null ? packedNMR : Texture2D.whiteTexture);
        Texture2D normalOutput = RenderPassToTexture(conversionMaterial, 1, size.x, size.y, true);
        WriteTexture(normalPath, ImageConversion.EncodeToTGA(normalOutput));
        DestroyImmediate(normalOutput);

        Texture2D metallicOutput = RenderPassToTexture(conversionMaterial, 2, size.x, size.y, true);
        WriteTexture(metallicPath, ImageConversion.EncodeToTGA(metallicOutput));
        DestroyImmediate(metallicOutput);

        AssetDatabase.ImportAsset(normalPath, ImportAssetOptions.ForceSynchronousImport);
        ConfigureNormalImporter(normalPath);
        AssetDatabase.ImportAsset(metallicPath, ImportAssetOptions.ForceSynchronousImport);
        ConfigurePackedImporter(metallicPath, _packedCompression);

        Material output = CreateConvertedMaterial(source, standardShader, materialPath);
        output.SetTexture("_BaseMap", baseMap);
        output.SetTextureScale("_BaseMap", source.GetTextureScale("_BC"));
        output.SetTextureOffset("_BaseMap", source.GetTextureOffset("_BC"));
        output.SetTexture("_NormalMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath));
        output.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(metallicPath));
        EditorUtility.SetDirty(output);
        return output;
    }

    private static Material CreateConvertedMaterial(Material source, Shader shader, string path)
    {
        Material output = new Material(shader)
        {
            name = Path.GetFileNameWithoutExtension(path),
            renderQueue = source.renderQueue,
            enableInstancing = source.enableInstancing,
            doubleSidedGI = source.doubleSidedGI,
            globalIlluminationFlags = source.globalIlluminationFlags
        };
        // Only copy properties declared by the target shader. Cloning the old material and
        // changing its shader leaves obsolete texture references serialized in the .mat file.
        output.CopyMatchingPropertiesFromMaterial(source);
        output.shaderKeywords = source.shaderKeywords;
        SyncToggleKeyword(output, "_UseOutline", "_OUTLINE_ON");
        SyncToggleKeyword(output, "_Smooth", "_SMOOTH_ON");
        AssetDatabase.CreateAsset(output, path);
        return output;
    }

    private static void SyncToggleKeyword(Material material, string propertyName, string keyword)
    {
        if (material.HasProperty(propertyName) && material.GetFloat(propertyName) > 0.5f)
            material.EnableKeyword(keyword);
        else
            material.DisableKeyword(keyword);
    }

    private static Texture2D RenderPassToTexture(Material material, int pass, int width, int height, bool linear)
    {
        RenderTextureReadWrite readWrite = linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB;
        RenderTexture renderTarget = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
        RenderTexture previous = RenderTexture.active;
        try
        {
            Graphics.Blit(Texture2D.whiteTexture, renderTarget, material, pass);
            RenderTexture.active = renderTarget;
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
            output.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
            output.Apply(false, false);
            return output;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTarget);
        }
    }

    private static Texture2D CreatePackedTextureFromSourceFiles(
        Texture normalMap,
        Texture metallicGlossMap,
        int fallbackWidth,
        int fallbackHeight,
        int maxSize)
    {
        Texture2D rawNormal = null;
        Texture2D rawMetallic = null;
        try
        {
            if (normalMap != null)
                rawNormal = LoadRawSourceTexture(normalMap, "法线");
            if (metallicGlossMap != null)
                rawMetallic = LoadRawSourceTexture(metallicGlossMap, "金属度/光滑度");

            int width = rawNormal != null
                ? rawNormal.width
                : rawMetallic != null ? rawMetallic.width : Mathf.Max(1, fallbackWidth);
            int height = rawNormal != null
                ? rawNormal.height
                : rawMetallic != null ? rawMetallic.height : Mathf.Max(1, fallbackHeight);
            Vector2Int outputSize = LimitTextureSize(new Vector2Int(width, height), maxSize);
            width = outputSize.x;
            height = outputSize.y;
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var outputPixels = new Color32[width * height];
            Color32[] normalPixels = rawNormal != null ? rawNormal.GetPixels32() : null;
            Color32[] metallicPixels = rawMetallic != null ? rawMetallic.GetPixels32() : null;
            bool flipNormalGreen = ShouldFlipNormalGreen(normalMap);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 normal = rawNormal != null
                        ? SampleRawPixel(rawNormal, normalPixels, x, y, width, height)
                        : new Color32(128, 128, 255, 255);
                    Color32 metallic = rawMetallic != null
                        ? SampleRawPixel(rawMetallic, metallicPixels, x, y, width, height)
                        : new Color32(255, 255, 255, 255);
                    byte normalY = flipNormalGreen ? (byte)(255 - normal.g) : normal.g;
                    outputPixels[y * width + x] = new Color32(
                        normal.r,
                        normalY,
                        metallic.r,
                        (byte)(255 - metallic.a));
                }
            }

            output.SetPixels32(outputPixels);
            output.Apply(false, false);
            return output;
        }
        finally
        {
            if (rawNormal != null)
                DestroyImmediate(rawNormal);
            if (rawMetallic != null)
                DestroyImmediate(rawMetallic);
        }
    }

    private static Color32 SampleRawPixel(
        Texture2D texture,
        Color32[] pixels,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        if (texture.width == targetWidth && texture.height == targetHeight)
            return pixels[y * targetWidth + x];

        float u = (x + 0.5f) / targetWidth;
        float v = (y + 0.5f) / targetHeight;
        return texture.GetPixelBilinear(u, v);
    }

    private static bool ShouldFlipNormalGreen(Texture texture)
    {
        string path = texture != null ? AssetDatabase.GetAssetPath(texture) : string.Empty;
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        return importer != null && importer.flipGreenChannel;
    }

    private static Texture2D LoadRawSourceTexture(Texture source, string usage)
    {
        string assetPath = AssetDatabase.GetAssetPath(source);
        string extension = Path.GetExtension(assetPath).ToLowerInvariant();
        string absolutePath = AssetFolderToAbsolute(assetPath);
        byte[] bytes = File.ReadAllBytes(absolutePath);

        if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (ImageConversion.LoadImage(texture, bytes, false))
                return texture;
            DestroyImmediate(texture);
        }
        else if (extension == ".tga")
        {
            return DecodeTga(bytes, assetPath);
        }

        throw new InvalidOperationException(
            $"{usage}贴图 {assetPath} 无法直接读取原始像素。为避免把压缩方格烘焙进结果，请使用 PNG、JPG 或 TGA 源文件。");
    }

    private static Texture2D DecodeTga(byte[] data, string assetPath)
    {
        if (data == null || data.Length < 18)
            throw new InvalidOperationException($"TGA 文件无效：{assetPath}");

        int idLength = data[0];
        int colorMapType = data[1];
        int imageType = data[2];
        int width = data[12] | (data[13] << 8);
        int height = data[14] | (data[15] << 8);
        int bitsPerPixel = data[16];
        int descriptor = data[17];
        bool isTrueColor = imageType == 2 || imageType == 10;
        bool isGray = imageType == 3 || imageType == 11;
        bool isRle = imageType == 10 || imageType == 11;
        int bytesPerPixel = bitsPerPixel / 8;

        if (colorMapType != 0 || width <= 0 || height <= 0
            || (!isTrueColor && !isGray)
            || (isTrueColor && bytesPerPixel != 3 && bytesPerPixel != 4)
            || (isGray && bytesPerPixel != 1))
        {
            throw new InvalidOperationException(
                $"不支持的 TGA 格式：{assetPath}（仅支持 8 位灰度、24/32 位真彩色及其 RLE 格式）");
        }

        int offset = 18 + idLength;
        int pixelCount = checked(width * height);
        var fileOrderPixels = new Color32[pixelCount];
        int written = 0;
        while (written < pixelCount)
        {
            int runLength = 1;
            bool repeated = false;
            if (isRle)
            {
                EnsureTgaBytes(data, offset, 1, assetPath);
                int packet = data[offset++];
                repeated = (packet & 0x80) != 0;
                runLength = (packet & 0x7F) + 1;
            }

            if (repeated)
            {
                Color32 pixel = ReadTgaPixel(data, ref offset, bytesPerPixel, isGray, assetPath);
                for (int i = 0; i < runLength && written < pixelCount; i++)
                    fileOrderPixels[written++] = pixel;
            }
            else
            {
                for (int i = 0; i < runLength && written < pixelCount; i++)
                    fileOrderPixels[written++] = ReadTgaPixel(data, ref offset, bytesPerPixel, isGray, assetPath);
            }
        }

        bool originTop = (descriptor & 0x20) != 0;
        bool originRight = (descriptor & 0x10) != 0;
        var unityPixels = new Color32[pixelCount];
        for (int index = 0; index < pixelCount; index++)
        {
            int fileX = index % width;
            int fileY = index / width;
            int x = originRight ? width - 1 - fileX : fileX;
            int y = originTop ? height - 1 - fileY : fileY;
            unityPixels[y * width + x] = fileOrderPixels[index];
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        texture.SetPixels32(unityPixels);
        texture.Apply(false, false);
        return texture;
    }

    private static Color32 ReadTgaPixel(
        byte[] data,
        ref int offset,
        int bytesPerPixel,
        bool isGray,
        string assetPath)
    {
        EnsureTgaBytes(data, offset, bytesPerPixel, assetPath);
        if (isGray)
        {
            byte value = data[offset++];
            return new Color32(value, value, value, 255);
        }

        byte blue = data[offset++];
        byte green = data[offset++];
        byte red = data[offset++];
        byte alpha = bytesPerPixel == 4 ? data[offset++] : (byte)255;
        return new Color32(red, green, blue, alpha);
    }

    private static void EnsureTgaBytes(byte[] data, int offset, int count, string assetPath)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new InvalidOperationException($"TGA 像素数据不完整：{assetPath}");
    }

    private static void WriteTexture(string assetPath, byte[] bytes)
    {
        string absolutePath = AssetFolderToAbsolute(assetPath);
        File.WriteAllBytes(absolutePath, bytes);
    }

    private static void ConfigureBaseImporter(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.mipmapEnabled = true;
        importer.SaveAndReimport();
    }

    private static void ConfigurePackedImporter(string path, PackedCompressionMode compressionMode)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;
        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = false;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.mipmapEnabled = true;
        // RG stores tangent-space normal XY. Keep the generated TGA uncompressed to avoid
        // a second block-compression pass after decoding the source normal texture.
        bool uncompressed = compressionMode == PackedCompressionMode.Uncompressed;
        importer.textureCompression = uncompressed
            ? TextureImporterCompression.Uncompressed
            : TextureImporterCompression.CompressedHQ;
        importer.crunchedCompression = false;
        importer.compressionQuality = 100;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.filterMode = FilterMode.Bilinear;
        importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
        if (uncompressed)
        {
            SetPlatformFormat(importer, "Standalone", TextureImporterFormat.RGBA32);
            SetPlatformFormat(importer, "Android", TextureImporterFormat.RGBA32);
            SetPlatformFormat(importer, "iPhone", TextureImporterFormat.RGBA32);
            SetPlatformFormat(importer, "WebGL", TextureImporterFormat.RGBA32);
        }
        else
        {
            SetPlatformFormat(importer, "Standalone", TextureImporterFormat.BC7);
            SetPlatformFormat(importer, "Android", TextureImporterFormat.ASTC_4x4);
            SetPlatformFormat(importer, "iPhone", TextureImporterFormat.ASTC_4x4);
            SetPlatformFormat(importer, "WebGL", TextureImporterFormat.RGBA32);
        }
        importer.SaveAndReimport();
    }

    private static void SetPlatformFormat(
        TextureImporter importer,
        string platformName,
        TextureImporterFormat format)
    {
        TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings(platformName);
        settings.name = platformName;
        settings.overridden = true;
        settings.format = format;
        settings.textureCompression = format == TextureImporterFormat.RGBA32
            ? TextureImporterCompression.Uncompressed
            : TextureImporterCompression.CompressedHQ;
        settings.crunchedCompression = false;
        settings.compressionQuality = 100;
        importer.SetPlatformTextureSettings(settings);
    }

    private static void ConfigureNormalImporter(string path)
    {
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;
        importer.textureType = TextureImporterType.NormalMap;
        importer.alphaSource = TextureImporterAlphaSource.None;
        importer.mipmapEnabled = true;
        importer.SaveAndReimport();
    }

    private static Vector2Int ChooseSize(params Texture[] textures)
    {
        Texture texture = textures.FirstOrDefault(candidate => candidate != null);
        return texture != null
            ? new Vector2Int(Mathf.Max(1, texture.width), Mathf.Max(1, texture.height))
            : new Vector2Int(4, 4);
    }

    private static Vector2Int LimitTextureSize(Vector2Int sourceSize, int maxSize)
    {
        int width = Mathf.Max(1, sourceSize.x);
        int height = Mathf.Max(1, sourceSize.y);
        if (maxSize <= 0 || (width <= maxSize && height <= maxSize))
            return new Vector2Int(width, height);

        float scale = maxSize / (float)Mathf.Max(width, height);
        return new Vector2Int(
            Mathf.Max(1, Mathf.RoundToInt(width * scale)),
            Mathf.Max(1, Mathf.RoundToInt(height * scale)));
    }

    private static int NormalizeTextureSize(int value)
    {
        return TextureSizeValues.Contains(value) ? value : 0;
    }

    private string ResolveOutputFolder(UnityEngine.Object source, string fallback)
    {
        if (!_outputBesideSources)
        {
            string category = Path.GetFileName(fallback);
            string categorizedFolder = _outputFolder.TrimEnd('/') + "/" + category;
            TryEnsureAssetFolder(categorizedFolder);
            return categorizedFolder;
        }

        string assetPath = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;
        string folder = string.IsNullOrEmpty(assetPath)
            ? fallback
            : Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(folder) ? fallback : folder;
    }

    private static string GenerateUniqueOutputPath(string folder, string fileName)
    {
        return AssetDatabase.GenerateUniqueAssetPath(folder.TrimEnd('/') + "/" + fileName);
    }

    private void SetOutputFolder(string path)
    {
        _outputFolder = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        EditorPrefs.SetString(OutputFolderKey, _outputFolder);
    }

    private static bool TryEnsureAssetFolder(string folder)
    {
        folder = (folder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
            return false;
        if (AssetDatabase.IsValidFolder(folder))
            return true;

        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
        return AssetDatabase.IsValidFolder(folder);
    }

    private static string AbsoluteToAssetFolder(string absolutePath)
    {
        string assetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
        string selected = Path.GetFullPath(absolutePath).Replace('\\', '/').TrimEnd('/');
        if (string.Equals(selected, assetsPath, StringComparison.OrdinalIgnoreCase))
            return "Assets";
        if (!selected.StartsWith(assetsPath + "/", StringComparison.OrdinalIgnoreCase))
            return null;
        return "Assets" + selected.Substring(assetsPath.Length);
    }

    private static string AssetFolderToAbsolute(string assetPath)
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return Path.GetFullPath(Path.Combine(projectRoot, assetPath ?? "Assets"));
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(value) ? "ToonMaterial" : value.Trim();
    }
}
