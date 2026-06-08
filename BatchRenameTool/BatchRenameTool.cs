// =====================================================
// 批量重命名工具（Batch Rename Tool）
// 适用版本：Unity 2022+
// 修改说明：新增排除规则、手动跳过开关、嵌套预制体依赖过滤
//           修复预览面板长文件名截断问题，支持自动换行
//           重构为左右分栏布局，预览区可跟随窗口自由缩放
//           彻底修复：所有资源类型（模型/材质/贴图）Manual模式完全生效
//           终极版：所有资源（白项/黄项/红项）全部支持Manual手动命名
//           ✅ 修复：贴图覆盖后缀不生效的问题
// =====================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

public class BatchRenameTool : EditorWindow
{
    private static string DefaultConfigPath
    {
        get
        {
            // 脚本同目录
            var guids = AssetDatabase.FindAssets("t:MonoScript BatchRenameTool");
            if (guids.Length == 0) return "Assets/Editor/BatchRenameConfig.json";
            var scriptPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            return System.IO.Path.GetDirectoryName(scriptPath).Replace('\\', '/') + "/BatchRenameConfig.json";
        }
    }

    // =====================================================
    // 目标来源
    // =====================================================
    private enum TargetSource { ProjectSelection, ProjectFolder, HierarchySelection, PrefabDependencies }
    private TargetSource targetSource = TargetSource.ProjectSelection;
    private DefaultAsset targetFolder;
    private bool includeSubFolders = true;
    private string fileExtensionFilter = "";

    // =====================================================
    // Prefab依赖模式设置
    // =====================================================
    private bool depIncludeModels = true;
    private bool depIncludeTextures = true;
    private bool depIncludeMaterials = true;
    private bool depIncludeOthers = false;
    private bool depUsePrefabNameAsBase = true;

    private enum SharedAssetPolicy { Skip, FirstWins, Warn }
    private SharedAssetPolicy sharedAssetPolicy = SharedAssetPolicy.Warn;

    private enum PerItemPolicy { UseGlobal, Skip, Manual } // 统一所有资源的策略
    private string prefixForModel = "SM_";
    private string prefixForTexture = "TEX_";
    private string prefixForMaterial = "MA_";
    private string prefixForOther = "Asset_";

    // =====================================================
    // 贴图槽位规则
    // =====================================================
    [System.Serializable]
    public class TextureSlotRule
    {
        public bool enabled = true;
        public string propertyNames = "";
        public string suffix = "";
    }

    private List<TextureSlotRule> textureSlotRules = new List<TextureSlotRule>();
    private bool textureSlotRulesInitialized = false;
    private const string MANUAL_SKIP = "(跳过)";
    private const string MANUAL_NONE = "(无后缀)";
    private const string MANUAL_CUSTOM = "(自定义...)";
    private enum TextureIdentifyState { None, Identified, Ambiguous, Unknown }

    // =====================================================
    // 字符串替换与前后缀
    // =====================================================
    [System.Serializable]
    public class ReplaceRule { public bool enabled = true; public string from = ""; public string to = ""; public bool caseSensitive = true; }
    private List<ReplaceRule> replaceRules = new List<ReplaceRule>();

    [System.Serializable]
    public class AffixRule { public bool enabled = true; public string text = ""; }
    private List<AffixRule> prefixRules = new List<AffixRule>();
    private List<AffixRule> suffixRules = new List<AffixRule>();

    // =====================================================
    // 排除规则
    // =====================================================
    [System.Serializable]
    public class ExcludeRule
    {
        public bool enabled = true;
        public ExcludeType excludeType = ExcludeType.NameContains;
        public string matchText = "";
        public bool caseSensitive = false;
    }

    public enum ExcludeType
    {
        NameContains,    // 名称包含
        PathContains,    // 路径包含
        TypeEquals,      // 类型等于（如Texture、Model、Material）
        IsNestedPrefab   // 是嵌套子预制体的依赖
    }

    private bool foldoutExclude = false;
    private List<ExcludeRule> excludeRules = new List<ExcludeRule>();

    // =====================================================
    // 基础重命名设置
    // =====================================================
    private bool removePrefixEnabled = false;
    private int removePrefixCount = 0;
    private bool removeSuffixEnabled = false;
    private int removeSuffixCount = 0;

    private bool numberingEnabled = false;
    private enum NumberPosition { Suffix, Prefix }
    private NumberPosition numberPosition = NumberPosition.Suffix;
    private int numberStart = 1;
    private int numberStep = 1;
    private int numberPadding = 2;
    private string numberSeparator = "_";

    private bool trimSpaces = true;
    private bool debugMode = false;

    // =====================================================
    // UI 状态
    // =====================================================
    private Vector2 configScroll; // 左侧配置区滚动
    private Vector2 previewScroll; // 右侧预览区滚动
    private bool needRebuildPreview = false;
    private bool foldoutConfigIO = false;
    private bool foldoutNamingRules = false;
    private bool foldoutTweaks = false;
    private bool foldoutTypePrefix = true;
    private bool foldoutTextureSlotInner = true;
    private bool foldoutReplace = false;
    private bool foldoutAffix = false;
    private bool foldoutRemove = false;
    private bool foldoutNumbering = false;
    private bool foldoutMisc = false;

    // =====================================================
    // 预览数据
    // =====================================================
    private List<PreviewItem> previewList = new List<PreviewItem>();

    private class PreviewItem
    {
        public Object obj;
        public string assetPath;
        public string oldName;
        public string newName;
        public bool changed;
        public string sourcePrefabName;
        public string assetTypeLabel;
        public bool isShared;
        public List<string> sharedBy;
        public bool skipDueToShared;
        public PerItemPolicy perItemPolicy = PerItemPolicy.UseGlobal; // 统一策略
        public int selectedPrefabIndex = 0; // 仅共享资源使用
        public string manualName = "";
        public TextureIdentifyState texState = TextureIdentifyState.None;
        public List<string> texCandidateSlots;
        public string texAutoSuffix;
        public string texManualSuffix;
        public string texCustomSuffix = "";
        public bool texSkippedByManual;
        public bool expanded = false;
        public string perItemPrefixOverride = "";
        public string perItemExtraPrefix = "";
        public bool manualSkip = false; // 手动标记跳过该资源重命名
    }

    public static void Open()
    {
        var win = GetWindow<BatchRenameTool>("Batch Rename Tool");
        win.minSize = new Vector2(1000, 700); // 调整为适合分栏的最小尺寸
        win.Show();
    }

    private void OnEnable()
    {
        EnsureDefaultTextureSlotRules();
        TryAutoLoadConfig();
    }

    private void EnsureDefaultTextureSlotRules()
    {
        if (textureSlotRulesInitialized) return;
        if (textureSlotRules.Count == 0) AddDefaultTextureSlotRules();
        textureSlotRulesInitialized = true;
    }

    private void AddDefaultTextureSlotRules()
    {
        textureSlotRules.Add(new TextureSlotRule { propertyNames = "_MainTex,_BaseMap,_BaseColorMap,_BaseColor", suffix = "_BC" });
        textureSlotRules.Add(new TextureSlotRule { propertyNames = "_BumpMap,_NormalMap,_DetailNormalMap", suffix = "_N" });
        textureSlotRules.Add(new TextureSlotRule { propertyNames = "_MetallicGlossMap,_MaskMap,_SpecGlossMap", suffix = "_M_S" });
        textureSlotRules.Add(new TextureSlotRule { propertyNames = "_OcclusionMap", suffix = "_AO" });
        textureSlotRules.Add(new TextureSlotRule { propertyNames = "_EmissionMap,_EmissiveColorMap", suffix = "_E" });
    }

    // =====================================================
    // GUI 主入口（核心修改：左右分栏布局）
    // =====================================================
    private void OnGUI()
    {
        EnsureDefaultTextureSlotRules();

        // 整体水平分栏：左侧配置区 + 右侧预览区
        using (new EditorGUILayout.HorizontalScope())
        {
            // --------------------------
            // 左侧：配置面板（固定最小宽度，可垂直滚动）
            // --------------------------
            configScroll = EditorGUILayout.BeginScrollView(configScroll, GUILayout.Width(480), GUILayout.ExpandHeight(true));

            DrawTopHelp();
            EditorGUILayout.Space(6);
            DrawConfigIOSection();
            EditorGUILayout.Space(6);
            DrawTargetSection();
            EditorGUILayout.Space(6);

            if (targetSource == TargetSource.PrefabDependencies)
            {
                DrawNamingRulesGroup();
                EditorGUILayout.Space(6);
            }

            DrawTweaksGroup();
            EditorGUILayout.Space(10);
            DrawButtons();
            EditorGUILayout.Space(6);

            EditorGUILayout.EndScrollView();

            // --------------------------
            // 右侧：预览面板（占满剩余空间，可自由缩放）
            // --------------------------
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
            {
                DrawPreview();
            }
        }

        if (needRebuildPreview)
        {
            needRebuildPreview = false;
            BuildPreview();
            Repaint();
        }

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

    private void DrawTopHelp()
    {
        EditorGUILayout.LabelField("选择目标 → 设置规则 → 分析预览 → 执行重命名（执行前请提交Git/SVN）", EditorStyles.miniLabel);
    }

    private void DrawConfigIOSection()
    {
        foldoutConfigIO = EditorGUILayout.Foldout(foldoutConfigIO, "配置导入 / 导出", true, EditorStyles.foldoutHeader);
        if (!foldoutConfigIO) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUILayout.HelpBox($"把当前所有规则保存为 JSON。\n启动时会自动加载 {DefaultConfigPath}", MessageType.None);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("保存配置...")) SaveConfigDialog();
                if (GUILayout.Button("加载配置...")) LoadConfigDialog();
                if (GUILayout.Button("重置为默认", GUILayout.Width(120)))
                {
                    if (EditorUtility.DisplayDialog("重置为默认", "将清空所有规则并恢复默认。继续？", "确认", "取消"))
                    {
                        ResetToDefaults();
                        needRebuildPreview = true;
                    }
                }
            }
        }
    }

    // ---  目标选择 ---
    private void DrawTargetSection()
    {
        EditorGUILayout.LabelField("目标选择", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box"))
        {
            EditorGUI.BeginChangeCheck();
            targetSource = (TargetSource)EditorGUILayout.EnumPopup(new GUIContent("目标来源"), targetSource);
            if (EditorGUI.EndChangeCheck()) previewList.Clear();

            if (targetSource == TargetSource.ProjectFolder)
            {
                targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("目标文件夹", targetFolder, typeof(DefaultAsset), false);
                includeSubFolders = EditorGUILayout.Toggle("包含子文件夹", includeSubFolders);
                fileExtensionFilter = EditorGUILayout.TextField("文件后缀过滤", fileExtensionFilter);
            }
            else if (targetSource == TargetSource.ProjectSelection)
            {
                EditorGUILayout.HelpBox("使用 Project 当前选中的资源。", MessageType.None);
            }
            else if (targetSource == TargetSource.HierarchySelection)
            {
                EditorGUILayout.HelpBox("使用 Hierarchy 当前选中的 GameObject。", MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox("在 Project 选中一个或多个 Prefab，工具会收集依赖的资源并同步重命名。", MessageType.None);
            }
        }
    }

    // ---  命名规则组 (Prefab 依赖模式专用) ---
    private void DrawNamingRulesGroup()
    {
        string summary = BuildNamingRulesSummary();
        foldoutNamingRules = DrawGroupFoldout(foldoutNamingRules, "命名规则", summary);
        if (!foldoutNamingRules) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foldoutTypePrefix = EditorGUILayout.Foldout(foldoutTypePrefix, "类型前缀", true);
            if (foldoutTypePrefix) DrawDependencyInner();

            EditorGUILayout.Space(4);
            foldoutTextureSlotInner = EditorGUILayout.Foldout(foldoutTextureSlotInner, "贴图槽位 → 后缀 映射", true);
            if (foldoutTextureSlotInner) DrawTextureSlotRulesInner();
        }
    }

    // ---  微调组 ---
    private void DrawTweaksGroup()
    {
        string summary = BuildTweaksSummary();
        foldoutTweaks = DrawGroupFoldout(foldoutTweaks, "微调", summary);
        if (!foldoutTweaks) return;

        using (new EditorGUILayout.VerticalScope("box"))
        {
            foldoutReplace = EditorGUILayout.Foldout(foldoutReplace, $"字符串替换{CountLabel(replaceRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.from)))}", true);
            if (foldoutReplace) DrawReplaceInner();

            EditorGUILayout.Space(4);
            foldoutAffix = EditorGUILayout.Foldout(foldoutAffix, $"通用前后缀{CountLabel(prefixRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.text)) + suffixRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.text)))}", true);
            if (foldoutAffix) DrawAffixInner();

            EditorGUILayout.Space(4);
            foldoutRemove = EditorGUILayout.Foldout(foldoutRemove, $"删除字符{(removePrefixEnabled || removeSuffixEnabled ? " (已启用)" : "")}", true);
            if (foldoutRemove) DrawRemoveInner();

            EditorGUILayout.Space(4);
            foldoutNumbering = EditorGUILayout.Foldout(foldoutNumbering, $"自动编号{(numberingEnabled ? " (已启用)" : "")}", true);
            if (foldoutNumbering) DrawNumberingInner();

            EditorGUILayout.Space(4);
            foldoutExclude = EditorGUILayout.Foldout(foldoutExclude, $"排除规则{CountLabel(excludeRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.matchText)))}", true);
            if (foldoutExclude) DrawExcludeInner();

            EditorGUILayout.Space(4);
            foldoutMisc = EditorGUILayout.Foldout(foldoutMisc, "其他", true);
            if (foldoutMisc) DrawMiscInner();
        }
    }

    private bool DrawGroupFoldout(bool current, string title, string summary)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            var newVal = EditorGUILayout.Foldout(current, title, true, EditorStyles.foldoutHeader);
            if (!string.IsNullOrEmpty(summary) && !newVal)
            {
                var oldColor = GUI.color;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
                GUI.color = oldColor;
            }
            return newVal;
        }
    }

    private string CountLabel(int n) => n > 0 ? $" ({n})" : "";

    private string BuildNamingRulesSummary()
    {
        var parts = new List<string>();
        var types = new List<string>();
        if (depIncludeModels && !string.IsNullOrEmpty(prefixForModel)) types.Add(prefixForModel);
        if (depIncludeTextures && !string.IsNullOrEmpty(prefixForTexture)) types.Add(prefixForTexture);
        if (depIncludeMaterials && !string.IsNullOrEmpty(prefixForMaterial)) types.Add(prefixForMaterial);
        if (types.Count > 0) parts.Add(string.Join(" ", types));

        int slotCount = textureSlotRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.suffix));
        if (slotCount > 0) parts.Add($"{slotCount}条贴图规则");

        return parts.Count > 0 ? "▶ " + string.Join(" • ", parts) : "";
    }

    private string BuildTweaksSummary()
    {
        var parts = new List<string>();
        int rep = replaceRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.from));
        if (rep > 0) parts.Add($"替换×{rep}");

        int pre = prefixRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.text));
        int suf = suffixRules.Count(r => r.enabled && !string.IsNullOrEmpty(r.text));
        if (pre > 0) parts.Add($"前缀×{pre}");
        if (suf > 0) parts.Add($"后缀×{suf}");

        if (removePrefixEnabled && removePrefixCount > 0) parts.Add($"删前{removePrefixCount}");
        if (removeSuffixEnabled && removeSuffixCount > 0) parts.Add($"删后{removeSuffixCount}");

        if (numberingEnabled) parts.Add("编号:开");
        if (excludeRules.Count(r => r.enabled) > 0) parts.Add($"排除×{excludeRules.Count(r => r.enabled)}");

        return parts.Count > 0 ? "▶ " + string.Join(" • ", parts) : "▶ 无";
    }

    // =====================================================
    // 子项渲染逻辑 (Inner Methods)
    // =====================================================
    private void DrawDependencyInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("处理的资源类型：");
            using (new EditorGUILayout.HorizontalScope())
            {
                depIncludeModels = GUILayout.Toggle(depIncludeModels, " 模型", GUILayout.Width(80));
                depIncludeTextures = GUILayout.Toggle(depIncludeTextures, " 贴图", GUILayout.Width(80));
                depIncludeMaterials = GUILayout.Toggle(depIncludeMaterials, " 材质", GUILayout.Width(80));
                depIncludeOthers = GUILayout.Toggle(depIncludeOthers, " 其他", GUILayout.Width(80));
            }

            EditorGUILayout.Space(4);
            depUsePrefabNameAsBase = EditorGUILayout.Toggle("用Prefab名作为基础", depUsePrefabNameAsBase);
            if (depUsePrefabNameAsBase)
            {
                prefixForModel = EditorGUILayout.TextField("模型前缀", prefixForModel);
                prefixForTexture = EditorGUILayout.TextField("贴图前缀", prefixForTexture);
                prefixForMaterial = EditorGUILayout.TextField("材质前缀", prefixForMaterial);
                prefixForOther = EditorGUILayout.TextField("其他前缀", prefixForOther);
            }

            EditorGUILayout.Space(4);
            sharedAssetPolicy = (SharedAssetPolicy)EditorGUILayout.EnumPopup("共享资源全局策略", sharedAssetPolicy);
            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
        }
    }

    private void DrawTextureSlotRulesInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(20);
                EditorGUILayout.LabelField("Shader 属性名（逗号分隔）", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("后缀", EditorStyles.miniBoldLabel, GUILayout.Width(100));
                GUILayout.Space(58);
            }

            int removeIndex = -1;
            for (int i = 0; i < textureSlotRules.Count; i++)
            {
                var rule = textureSlotRules[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    rule.enabled = EditorGUILayout.Toggle(rule.enabled, GUILayout.Width(16));
                    rule.propertyNames = EditorGUILayout.TextField(rule.propertyNames);
                    rule.suffix = EditorGUILayout.TextField(rule.suffix, GUILayout.Width(100));
                    if (GUILayout.Button("删除", GUILayout.Width(50))) removeIndex = i;
                }
            }

            if (removeIndex >= 0) textureSlotRules.RemoveAt(removeIndex);
            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("+ 添加规则")) { textureSlotRules.Add(new TextureSlotRule()); needRebuildPreview = true; }
                if (GUILayout.Button("清空", GUILayout.Width(80))) { textureSlotRules.Clear(); needRebuildPreview = true; }
                if (GUILayout.Button("加载默认", GUILayout.Width(100))) { AddDefaultTextureSlotRules(); needRebuildPreview = true; }
            }
        }
    }

    private void DrawReplaceInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            int removeIndex = -1;
            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < replaceRules.Count; i++)
            {
                var rule = replaceRules[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    rule.enabled = EditorGUILayout.Toggle(rule.enabled, GUILayout.Width(16));
                    rule.from = EditorGUILayout.TextField(rule.from);
                    GUILayout.Label("→", GUILayout.Width(20));
                    rule.to = EditorGUILayout.TextField(rule.to);
                    rule.caseSensitive = GUILayout.Toggle(rule.caseSensitive, new GUIContent("Aa", "区分大小写"), "Button", GUILayout.Width(32));
                    if (GUILayout.Button("删除", GUILayout.Width(50))) removeIndex = i;
                }
            }

            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
            if (removeIndex >= 0) { replaceRules.RemoveAt(removeIndex); needRebuildPreview = true; }
            if (GUILayout.Button("+ 添加替换规则")) { replaceRules.Add(new ReplaceRule()); needRebuildPreview = true; }
        }
    }

    private void DrawAffixInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUILayout.LabelField("前缀（按顺序叠加）");
            DrawAffixList(prefixRules, true);
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("后缀（按顺序叠加）");
            DrawAffixList(suffixRules, false);
        }
    }

    private void DrawAffixList(List<AffixRule> list, bool isPrefix)
    {
        int removeIndex = -1;
        EditorGUI.BeginChangeCheck();
        for (int i = 0; i < list.Count; i++)
        {
            var rule = list[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                rule.enabled = EditorGUILayout.Toggle(rule.enabled, GUILayout.Width(16));
                rule.text = EditorGUILayout.TextField(rule.text);
                if (GUILayout.Button("↑", GUILayout.Width(26)) && i > 0)
                {
                    (list[i], list[i - 1]) = (list[i - 1], list[i]);
                    needRebuildPreview = true;
                }
                if (GUILayout.Button("↓", GUILayout.Width(26)) && i < list.Count - 1)
                {
                    (list[i], list[i + 1]) = (list[i + 1], list[i]);
                    needRebuildPreview = true;
                }
                if (GUILayout.Button("删除", GUILayout.Width(50))) removeIndex = i;
            }
        }

        if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
        if (removeIndex >= 0) { list.RemoveAt(removeIndex); needRebuildPreview = true; }
        if (GUILayout.Button(isPrefix ? "+ 添加前缀" : "+ 添加后缀")) { list.Add(new AffixRule()); needRebuildPreview = true; }
    }

    private void DrawRemoveInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                removePrefixEnabled = EditorGUILayout.Toggle(removePrefixEnabled, GUILayout.Width(16));
                EditorGUILayout.LabelField("删除前N个字符", GUILayout.Width(160));
                removePrefixCount = EditorGUILayout.IntField(removePrefixCount);
                if (removePrefixCount < 0) removePrefixCount = 0;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                removeSuffixEnabled = EditorGUILayout.Toggle(removeSuffixEnabled, GUILayout.Width(16));
                EditorGUILayout.LabelField("删除后N个字符", GUILayout.Width(160));
                removeSuffixCount = EditorGUILayout.IntField(removeSuffixCount);
                if (removeSuffixCount < 0) removeSuffixCount = 0;
            }

            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
        }
    }

    private void DrawNumberingInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUI.BeginChangeCheck();
            numberingEnabled = EditorGUILayout.Toggle("启用自动编号", numberingEnabled);
            using (new EditorGUI.DisabledScope(!numberingEnabled))
            {
                numberPosition = (NumberPosition)EditorGUILayout.EnumPopup("编号位置", numberPosition);
                numberStart = EditorGUILayout.IntField("起始编号", numberStart);
                numberStep = EditorGUILayout.IntField("递增步长", numberStep);
                numberPadding = EditorGUILayout.IntField("编号位数", numberPadding);
                if (numberPadding < 0) numberPadding = 0;
                numberSeparator = EditorGUILayout.TextField("编号分隔符", numberSeparator);
            }

            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
        }
    }

    // 绘制排除规则UI
    private void DrawExcludeInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            int removeIndex = -1;
            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < excludeRules.Count; i++)
            {
                var rule = excludeRules[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    rule.enabled = EditorGUILayout.Toggle(rule.enabled, GUILayout.Width(16));
                    rule.excludeType = (ExcludeType)EditorGUILayout.EnumPopup(rule.excludeType, GUILayout.Width(120));

                    // IsNestedPrefab类型不需要输入匹配文本
                    if (rule.excludeType != ExcludeType.IsNestedPrefab)
                    {
                        rule.matchText = EditorGUILayout.TextField(rule.matchText);
                        rule.caseSensitive = GUILayout.Toggle(rule.caseSensitive, new GUIContent("Aa", "区分大小写"), "Button", GUILayout.Width(32));
                    }
                    else
                    {
                        EditorGUILayout.LabelField("（自动过滤嵌套子预制体的依赖）", EditorStyles.miniLabel);
                    }

                    if (GUILayout.Button("删除", GUILayout.Width(50))) removeIndex = i;
                }
            }

            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
            if (removeIndex >= 0) { excludeRules.RemoveAt(removeIndex); needRebuildPreview = true; }
            if (GUILayout.Button("+ 添加排除规则")) { excludeRules.Add(new ExcludeRule()); needRebuildPreview = true; }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox("提示：IsNestedPrefab类型会自动忽略所有嵌套子预制体的依赖资源，只处理根预制体的直接依赖", MessageType.Info);
        }
    }

    private void DrawMiscInner()
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUI.BeginChangeCheck();
            trimSpaces = EditorGUILayout.Toggle("自动去除前后空格", trimSpaces);
            debugMode = EditorGUILayout.Toggle("调试模式", debugMode);
            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
        }
    }

    private void DrawButtons()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("分析预览", GUILayout.Height(32))) BuildPreview();
            GUI.backgroundColor = new Color(0.6f, 1f, 0.6f);
            if (GUILayout.Button("执行重命名", GUILayout.Height(32))) ApplyRename();
            GUI.backgroundColor = Color.white;
        }
    }

    // ✅ 预览面板：占满右侧全部空间，支持自由缩放
    private void DrawPreview()
    {
        EditorGUILayout.LabelField($"重命名预览（{previewList.Count}）", EditorStyles.boldLabel);
        using (new EditorGUILayout.VerticalScope("box", GUILayout.ExpandHeight(true)))
        {
            // 启用水平+垂直滚动，完全自适应右侧区域大小
            previewScroll = EditorGUILayout.BeginScrollView(previewScroll, true, true);
            if (previewList.Count == 0)
                EditorGUILayout.LabelField("点击左侧【分析预览】按钮查看结果。", EditorStyles.centeredGreyMiniLabel);
            else
            {
                bool isDepMode = targetSource == TargetSource.PrefabDependencies;
                foreach (var p in previewList) DrawPreviewItem(p, isDepMode);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    // ✅ 预览项：所有资源都显示处理方式
    private void DrawPreviewItem(PreviewItem p, bool isDepMode)
    {
        bool isUnidentifiedTexture = p.texState == TextureIdentifyState.Ambiguous || p.texState == TextureIdentifyState.Unknown;
        bool userExplicitlyChoseName = p.perItemPolicy == PerItemPolicy.Manual;
        bool needsManual = isUnidentifiedTexture && !userExplicitlyChoseName;
        bool manualResolved = needsManual && IsManualSuffixResolved(p);
        bool stillNeedsManual = needsManual && !manualResolved;
        bool showSuffixPanel = p.assetTypeLabel == "Texture";

        using (new EditorGUILayout.HorizontalScope())
        {
            var oldColor = GUI.color;
            if (p.manualSkip) GUI.color = Color.gray; // 手动跳过的资源显示灰色
            else if (p.isShared) GUI.color = new Color(1f, 0.55f, 0.55f);
            else if (stillNeedsManual) GUI.color = new Color(1f, 0.92f, 0.4f);
            else if (!p.changed) GUI.color = Color.gray;

            if (isDepMode)
            {
                // 类型标签：固定宽度，支持换行
                EditorGUILayout.LabelField($"[{p.assetTypeLabel}]", EditorStyles.wordWrappedLabel, GUILayout.Width(70));
                // 来源预制体名：固定宽度，支持换行
                EditorGUILayout.LabelField(p.isShared ? "[共享]" : p.sourcePrefabName, EditorStyles.wordWrappedLabel, GUILayout.Width(100));
            }

            // 旧名称：自动拉伸，支持自动换行
            EditorGUILayout.LabelField(p.oldName, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true), GUILayout.MinWidth(120));

            // 箭头：稍微加宽避免拥挤
            GUILayout.Label("→", GUILayout.Width(22));

            // 新名称：自动拉伸，支持自动换行
            if ((p.isShared && p.skipDueToShared) || p.texSkippedByManual || p.manualSkip)
                EditorGUILayout.LabelField("（跳过）", EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true), GUILayout.MinWidth(120));
            else
                EditorGUILayout.LabelField(p.newName, EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true), GUILayout.MinWidth(120));

            GUI.color = oldColor;

            if (!string.IsNullOrEmpty(p.assetPath))
            {
                // 跳转：只 Ping 高亮，不替换 Selection，预览列表不会丢失
                var jumpContent = new GUIContent("跳转", $"单击定位到 Project 窗口\n路径: {p.assetPath}");
                if (GUILayout.Button(jumpContent, GUILayout.Width(45)))
                {
                    var o = AssetDatabase.LoadAssetAtPath<Object>(p.assetPath);
                    if (o != null) { EditorGUIUtility.PingObject(o); }
                }

                if (isDepMode)
                {
                    bool hasOverride = !string.IsNullOrEmpty(p.perItemPrefixOverride) || !string.IsNullOrEmpty(p.perItemExtraPrefix);
                    string label = p.expanded ? "▼" : (hasOverride ? "▶*" : "▶");
                    var oldBg = GUI.backgroundColor;
                    if (hasOverride) GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
                    if (GUILayout.Button(label, GUILayout.Width(32))) p.expanded = !p.expanded;
                    GUI.backgroundColor = oldBg;
                }
            }

            // 手动跳过开关
            bool oldSkip = p.manualSkip;
            p.manualSkip = GUILayout.Toggle(p.manualSkip, new GUIContent("跳过", "勾选后该资源不会被重命名"), GUILayout.Width(55));
            if (p.manualSkip != oldSkip) needRebuildPreview = true;
        }

        // 显示资产路径，不用跳转也能看到文件位置
        if (isDepMode && !string.IsNullOrEmpty(p.assetPath))
        {
            var oldPathColor = GUI.color;
            GUI.color = new Color(0.45f, 0.45f, 0.45f);
            EditorGUILayout.LabelField($"        {p.assetPath}", EditorStyles.miniLabel);
            GUI.color = oldPathColor;
        }

        // ✅ 核心修改：所有资源（白项/黄项/红项）都显示处理方式
        if (isDepMode)
        {
            DrawPerItemConfig(p);
        }

        if (isDepMode && showSuffixPanel && !string.IsNullOrEmpty(p.assetPath))
        {
            DrawTextureManualPanel(p);
        }

        if (isDepMode && p.expanded && !string.IsNullOrEmpty(p.assetPath))
        {
            DrawPerItemOverridePanel(p);
        }
    }

    private void DrawPerItemOverridePanel(PreviewItem p)
    {
        using (new EditorGUILayout.VerticalScope("helpbox"))
        {
            EditorGUILayout.LabelField("单项前缀设置", EditorStyles.miniBoldLabel);
            string globalTypePrefix = GetPrefixForType(p.assetTypeLabel);
            EditorGUI.BeginChangeCheck();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("覆盖类型前缀"), GUILayout.Width(120));
                p.perItemPrefixOverride = EditorGUILayout.TextField(p.perItemPrefixOverride);
                EditorGUILayout.LabelField(string.IsNullOrEmpty(p.perItemPrefixOverride) ? $"(用 {globalTypePrefix})" : "(已覆盖)", EditorStyles.miniLabel, GUILayout.Width(80));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(new GUIContent("额外前缀"), GUILayout.Width(120));
                p.perItemExtraPrefix = EditorGUILayout.TextField(p.perItemExtraPrefix);
                EditorGUILayout.LabelField("(可选)", EditorStyles.miniLabel, GUILayout.Width(80));
            }

            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("清空", GUILayout.Width(60)))
                {
                    p.perItemPrefixOverride = "";
                    p.perItemExtraPrefix = "";
                    needRebuildPreview = true;
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField("最终格式：{额外前缀}{类型前缀}{Prefab名}{贴图后缀}", EditorStyles.miniLabel);
            }
        }
    }

    private bool IsManualSuffixResolved(PreviewItem p)
    {
        if (string.IsNullOrEmpty(p.texManualSuffix)) return false;
        if (p.texManualSuffix == MANUAL_CUSTOM) return !string.IsNullOrEmpty(p.texCustomSuffix);
        return true;
    }

    // ✅ 核心修改：所有资源都显示处理方式和Manual输入框
    private void DrawPerItemConfig(PreviewItem p)
    {
        // 共享资源显示红色提示
        if (p.isShared)
        {
            var oldColor = GUI.color;
            GUI.color = new Color(0.8f, 0.4f, 0.4f);
            EditorGUILayout.LabelField($"        ↑ 被以下Prefab共享：{string.Join(", ", p.sharedBy)}", EditorStyles.miniLabel);
            GUI.color = oldColor;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(20);
            EditorGUILayout.LabelField("处理方式：", GUILayout.Width(70));
            EditorGUI.BeginChangeCheck();
            p.perItemPolicy = (PerItemPolicy)EditorGUILayout.EnumPopup(p.perItemPolicy, GUILayout.Width(110));
            if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;

            // ✅ 所有资源切换到Manual都显示输入框
            if (p.perItemPolicy == PerItemPolicy.Manual)
            {
                EditorGUI.BeginChangeCheck();
                p.manualName = EditorGUILayout.TextField(p.manualName);
                if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
            }
            // 共享资源额外显示UsePrefab选项
            else if (p.isShared && p.perItemPolicy == PerItemPolicy.UseGlobal)
            {
                EditorGUI.BeginChangeCheck();
                p.selectedPrefabIndex = EditorGUILayout.Popup(p.selectedPrefabIndex, p.sharedBy.ToArray(), GUILayout.Width(160));
                if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
            }
        }

        // ✅ 所有资源切换到Manual都显示快速填入按钮
        if (p.perItemPolicy == PerItemPolicy.Manual && p.sharedBy != null && p.sharedBy.Count > 0)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(20);
                EditorGUILayout.LabelField("快速填入：", GUILayout.Width(70));
                string typePrefix = !string.IsNullOrEmpty(p.perItemPrefixOverride) ? p.perItemPrefixOverride : GetPrefixForType(p.assetTypeLabel);
                string extraPrefix = p.perItemExtraPrefix ?? "";

                foreach (var prefabName in p.sharedBy)
                {
                    string candidate = extraPrefix + typePrefix + prefabName;
                    // 贴图资源自动添加后缀
                    if (p.assetTypeLabel == "Texture")
                    {
                        candidate += GetCurrentTextureSuffix(p);
                    }

                    if (GUILayout.Button(prefabName, EditorStyles.miniButton))
                    {
                        p.manualName = candidate;
                        needRebuildPreview = true;
                        GUI.FocusControl(null);
                    }
                }
            }
        }
    }

    // ✅ 修复：优先使用手动指定的覆盖后缀
    private string GetCurrentTextureSuffix(PreviewItem p)
    {
        if (p.assetTypeLabel != "Texture") return "";

        // 优先使用手动指定的后缀（覆盖自动识别）
        if (!string.IsNullOrEmpty(p.texManualSuffix))
        {
            if (p.texManualSuffix == MANUAL_NONE) return "";
            if (p.texManualSuffix == MANUAL_CUSTOM) return p.texCustomSuffix ?? "";
            return p.texManualSuffix;
        }

        // 没有手动指定，使用自动识别的后缀
        if (p.texState == TextureIdentifyState.Identified) return p.texAutoSuffix ?? "";

        // 都没有，返回空
        return "";
    }

    private void DrawTextureManualPanel(PreviewItem p)
    {
        string reason; Color reasonColor;
        if (p.texState == TextureIdentifyState.Ambiguous)
        {
            reason = $"⚠ 多材质冲突：{string.Join(" / ", p.texCandidateSlots ?? new List<string>())}";
            reasonColor = new Color(0.85f, 0.65f, 0.2f);
        }
        else if (p.texState == TextureIdentifyState.Unknown)
        {
            reason = "⚠ 未找到关联槽位";
            reasonColor = new Color(0.85f, 0.65f, 0.2f);
        }
        else
        {
            reason = $"✓ 已识别：{p.texAutoSuffix}";
            reasonColor = new Color(0.5f, 0.85f, 0.5f);
        }

        var oldColor = GUI.color; GUI.color = reasonColor;
        EditorGUILayout.LabelField("        " + reason, EditorStyles.miniLabel);
        GUI.color = oldColor;

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(20);
            string label = p.texState == TextureIdentifyState.Identified ? "覆盖后缀：" : "手动指定后缀：";
            EditorGUILayout.LabelField(label, GUILayout.Width(100));

            var options = BuildManualSuffixOptions();
            string defaultValue = p.texState == TextureIdentifyState.Identified ? (p.texAutoSuffix ?? MANUAL_NONE) : MANUAL_NONE;
            string current = string.IsNullOrEmpty(p.texManualSuffix) ? defaultValue : p.texManualSuffix;
            int currentIndex = System.Array.IndexOf(options, current);
            if (currentIndex < 0) currentIndex = 0;

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(currentIndex, options, GUILayout.Width(140));
            if (EditorGUI.EndChangeCheck()) { p.texManualSuffix = options[newIndex]; needRebuildPreview = true; }

            if (p.texManualSuffix == MANUAL_CUSTOM)
            {
                EditorGUI.BeginChangeCheck();
                p.texCustomSuffix = EditorGUILayout.TextField(p.texCustomSuffix, GUILayout.Width(140));
                if (EditorGUI.EndChangeCheck()) needRebuildPreview = true;
            }
        }
    }

    private string[] BuildManualSuffixOptions()
    {
        var list = new List<string> { MANUAL_NONE, MANUAL_CUSTOM };
        var seen = new HashSet<string>();
        foreach (var rule in textureSlotRules)
        {
            if (rule.enabled && !string.IsNullOrEmpty(rule.suffix) && seen.Add(rule.suffix)) list.Add(rule.suffix);
        }
        return list.ToArray();
    }

    // =====================================================
    // 逻辑实现
    // =====================================================
    private void ResetToDefaults()
    {
        textureSlotRules.Clear();
        AddDefaultTextureSlotRules();
        textureSlotRulesInitialized = true;

        prefixForModel = "SM_"; prefixForTexture = "TEX_"; prefixForMaterial = "MA_"; prefixForOther = "Asset_";
        depUsePrefabNameAsBase = true; sharedAssetPolicy = SharedAssetPolicy.Warn;

        replaceRules.Clear(); prefixRules.Clear(); suffixRules.Clear(); excludeRules.Clear();

        removePrefixEnabled = false; removePrefixCount = 0; removeSuffixEnabled = false; removeSuffixCount = 0;
        numberingEnabled = false; numberPosition = NumberPosition.Suffix; numberStart = 1; numberStep = 1; numberPadding = 2; numberSeparator = "_";
        trimSpaces = true;
    }

    private void SaveConfigDialog()
    {
        string path = EditorUtility.SaveFilePanel("保存配置", Path.GetDirectoryName(DefaultConfigPath), Path.GetFileName(DefaultConfigPath), "json");
        if (string.IsNullOrEmpty(path)) return;

        var config = new
        {
            prefixForModel,
            prefixForTexture,
            prefixForMaterial,
            prefixForOther,
            depUsePrefabNameAsBase,
            sharedAssetPolicy,
            textureSlotRules,
            replaceRules,
            prefixRules,
            suffixRules,
            excludeRules,
            removePrefixEnabled,
            removePrefixCount,
            removeSuffixEnabled,
            removeSuffixCount,
            numberingEnabled,
            numberPosition,
            numberStart,
            numberStep,
            numberPadding,
            numberSeparator,
            trimSpaces
        };

        File.WriteAllText(path, JsonUtility.ToJson(config, true));
        AssetDatabase.Refresh();
    }

    private void LoadConfigDialog()
    {
        string path = EditorUtility.OpenFilePanel("加载配置", Path.GetDirectoryName(DefaultConfigPath), "json");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            JsonUtility.FromJsonOverwrite(json, this);
            textureSlotRulesInitialized = true;
            needRebuildPreview = true;
        }
        catch
        {
            EditorUtility.DisplayDialog("错误", "配置文件格式错误。", "确定");
        }
    }

    private void TryAutoLoadConfig()
    {
        if (File.Exists(DefaultConfigPath))
        {
            try
            {
                var json = File.ReadAllText(DefaultConfigPath);
                JsonUtility.FromJsonOverwrite(json, this);
                textureSlotRulesInitialized = true;
            }
            catch { /* 静默失败 */ }
        }
    }

    private List<Object> CollectTargets()
    {
        var result = new List<Object>();
        switch (targetSource)
        {
            case TargetSource.ProjectSelection:
                foreach (var obj in Selection.objects)
                {
                    if (obj == null) continue;
                    string path = AssetDatabase.GetAssetPath(obj);
                    if (!string.IsNullOrEmpty(path) && !AssetDatabase.IsValidFolder(path)) result.Add(obj);
                }
                break;

            case TargetSource.HierarchySelection:
                foreach (var go in Selection.gameObjects) result.Add(go);
                break;

            case TargetSource.ProjectFolder:
                if (targetFolder == null) break;
                string folderPath = AssetDatabase.GetAssetPath(targetFolder);
                var guids = AssetDatabase.FindAssets("", new[] { folderPath });
                var extFilters = ParseExtensionFilter(fileExtensionFilter);
                var seen = new HashSet<string>();

                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path) || !seen.Add(path) || AssetDatabase.IsValidFolder(path)) continue;
                    if (!includeSubFolders && Path.GetDirectoryName(path).Replace('\\', '/') != folderPath) continue;
                    if (extFilters.Count > 0 && !extFilters.Contains(Path.GetExtension(path).ToLowerInvariant())) continue;

                    var obj = AssetDatabase.LoadAssetAtPath<Object>(path);
                    if (obj != null) result.Add(obj);
                }
                break;
        }
        return result;
    }

    private HashSet<string> ParseExtensionFilter(string raw)
    {
        var set = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(raw)) return set;
        foreach (var part in raw.Split(','))
        {
            var s = part.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(s)) set.Add(s.StartsWith(".") ? s : "." + s);
        }
        return set;
    }

    private void CollectPrefabDependencies(out Dictionary<string, List<string>> dependencies, out Dictionary<string, string> assetTypes)
    {
        dependencies = new Dictionary<string, List<string>>();
        assetTypes = new Dictionary<string, string>();

        var prefabPaths = Selection.objects
            .Select(obj =>
            {
                // Hierarchy 中的预制体实例：获取源预制体资产路径
                if (obj is GameObject go)
                {
                    var source = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (source != null) return AssetDatabase.GetAssetPath(source);
                }
                // Project 窗口中的预制体资产：直接获取路径
                return AssetDatabase.GetAssetPath(obj);
            })
            .Where(p => !string.IsNullOrEmpty(p) && p.ToLowerInvariant().EndsWith(".prefab"))
            .Distinct()
            .ToList();

        if (prefabPaths.Count == 0) return;

        foreach (var prefabPath in prefabPaths)
        {
            foreach (var depPath in AssetDatabase.GetDependencies(prefabPath, true))
            {
                if (string.IsNullOrEmpty(depPath) || depPath == prefabPath) continue;
                string lower = depPath.ToLowerInvariant();

                // 排除脚本、内置资源和其他预制体本身
                if (lower.EndsWith(".prefab") || lower.EndsWith(".cs") || lower.EndsWith(".dll") ||
                    depPath.StartsWith("Resources/unity_builtin_extra") || depPath.StartsWith("Library/"))
                    continue;

                string typeLabel = ClassifyAsset(depPath);
                if (typeLabel == null) continue;

                assetTypes[depPath] = typeLabel;
                if (!dependencies.TryGetValue(depPath, out var list)) dependencies[depPath] = list = new List<string>();
                if (!list.Contains(prefabPath)) list.Add(prefabPath);
            }
        }
    }

    private string ClassifyAsset(string assetPath)
    {
        string ext = Path.GetExtension(assetPath).ToLowerInvariant();
        if (ext == ".fbx" || ext == ".obj") return depIncludeModels ? "Model" : null;
        if (ext == ".png" || ext == ".tga" || ext == ".psd" || ext == ".jpg" || ext == ".hdr") return depIncludeTextures ? "Texture" : null;
        if (ext == ".mat") return depIncludeMaterials ? "Material" : null;
        if (ext == ".shader" || ext == ".anim" || ext == ".controller") return depIncludeOthers ? "Other" : null;
        return null;
    }

    private string GetPrefixForType(string typeLabel)
    {
        switch (typeLabel)
        {
            case "Model": return prefixForModel ?? "";
            case "Texture": return prefixForTexture ?? "";
            case "Material": return prefixForMaterial ?? "";
            case "Other": return prefixForOther ?? "";
            default: return "";
        }
    }

    private void IdentifyTextures(Dictionary<string, string> assetTypes, out Dictionary<string, TextureIdentifyState> texStates, out Dictionary<string, string> texSuffixes, out Dictionary<string, List<string>> texCandidateSlots)
    {
        texStates = new Dictionary<string, TextureIdentifyState>();
        texSuffixes = new Dictionary<string, string>();
        texCandidateSlots = new Dictionary<string, List<string>>();

        var materialPaths = assetTypes.Where(kv => kv.Value == "Material").Select(kv => kv.Key).ToList();
        var texHitSuffixes = new Dictionary<string, HashSet<string>>();
        var texHitProps = new Dictionary<string, HashSet<string>>();

        foreach (var matPath in materialPaths)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null || mat.shader == null) continue;

            for (int i = 0; i < ShaderUtil.GetPropertyCount(mat.shader); i++)
            {
                if (ShaderUtil.GetPropertyType(mat.shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                string propName = ShaderUtil.GetPropertyName(mat.shader, i);
                var tex = mat.GetTexture(propName);
                if (tex == null) continue;

                string texPath = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(texPath) || !assetTypes.TryGetValue(texPath, out var typeLabel) || typeLabel != "Texture") continue;

                if (!texHitProps.ContainsKey(texPath)) texHitProps[texPath] = new HashSet<string>();
                texHitProps[texPath].Add(propName);

                string matchedSuffix = MatchSuffixByProperty(propName);
                if (matchedSuffix != null)
                {
                    if (!texHitSuffixes.ContainsKey(texPath)) texHitSuffixes[texPath] = new HashSet<string>();
                    texHitSuffixes[texPath].Add(matchedSuffix);
                }
            }
        }

        foreach (var kv in assetTypes.Where(k => k.Value == "Texture"))
        {
            string texPath = kv.Key;
            if (!texHitSuffixes.TryGetValue(texPath, out var suffixes) || suffixes.Count == 0)
            {
                texStates[texPath] = TextureIdentifyState.Unknown;
                if (texHitProps.TryGetValue(texPath, out var props)) texCandidateSlots[texPath] = props.ToList();
            }
            else if (suffixes.Count == 1)
            {
                texStates[texPath] = TextureIdentifyState.Identified;
                texSuffixes[texPath] = suffixes.First();
            }
            else
            {
                texStates[texPath] = TextureIdentifyState.Ambiguous;
                texCandidateSlots[texPath] = suffixes.ToList();
            }
        }
    }

    private string MatchSuffixByProperty(string propName)
    {
        if (string.IsNullOrEmpty(propName)) return null;
        foreach (var rule in textureSlotRules.Where(r => r.enabled && !string.IsNullOrEmpty(r.propertyNames)))
        {
            if (rule.propertyNames.Split(',').Select(n => n.Trim()).Any(n => string.Equals(n, propName, System.StringComparison.OrdinalIgnoreCase)))
                return rule.suffix ?? "";
        }
        return null;
    }

    // 判断资源是否需要排除重命名
    private bool ShouldExcludeResource(PreviewItem p)
    {
        // 1. 手动跳过优先级最高
        if (p.manualSkip) return true;

        // 2. 遍历所有启用的排除规则
        foreach (var rule in excludeRules)
        {
            if (!rule.enabled) continue;

            switch (rule.excludeType)
            {
                case ExcludeType.NameContains:
                    if (StringMatch(p.oldName, rule.matchText, rule.caseSensitive))
                        return true;
                    break;

                case ExcludeType.PathContains:
                    if (StringMatch(p.assetPath, rule.matchText, rule.caseSensitive))
                        return true;
                    break;

                case ExcludeType.TypeEquals:
                    if (StringMatch(p.assetTypeLabel, rule.matchText, rule.caseSensitive))
                        return true;
                    break;

                case ExcludeType.IsNestedPrefab:
                    // 简化版判断：如果资源被多个预制体共享，则视为嵌套依赖
                    // 如需更精确判断，可修改依赖收集逻辑添加嵌套层级标记
                    if (p.isShared && p.sharedBy.Count > 1)
                        return true;
                    break;
            }
        }
        return false;
    }

    // 辅助：字符串匹配（支持大小写不敏感）
    private bool StringMatch(string input, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(pattern)) return false;
        return caseSensitive
            ? input.Contains(pattern)
            : input.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string BuildNewName(string oldName, int index)
    {
        string name = oldName;

        foreach (var rule in replaceRules.Where(r => r.enabled && !string.IsNullOrEmpty(r.from)))
            name = rule.caseSensitive
                ? name.Replace(rule.from, rule.to ?? "")
                : System.Text.RegularExpressions.Regex.Replace(name, System.Text.RegularExpressions.Regex.Escape(rule.from), (rule.to ?? "").Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (removePrefixEnabled && removePrefixCount > 0 && name.Length > 0)
            name = name.Substring(Mathf.Min(removePrefixCount, name.Length));

        if (removeSuffixEnabled && removeSuffixCount > 0 && name.Length > 0)
            name = name.Substring(0, name.Length - Mathf.Min(removeSuffixCount, name.Length));

        name = prefixRules.Where(r => r.enabled).Aggregate("", (current, r) => current + r.text)
               + name
               + suffixRules.Where(r => r.enabled).Aggregate("", (current, r) => current + r.text);

        if (numberingEnabled)
        {
            string numStr = (numberStart + index * numberStep).ToString().PadLeft(Mathf.Max(0, numberPadding), '0');
            name = numberPosition == NumberPosition.Suffix
                ? name + (numberSeparator ?? "") + numStr
                : numStr + (numberSeparator ?? "") + name;
        }

        return trimSpaces ? name.Trim() : name;
    }

    private void BuildPreview()
    {
        var oldConfig = previewList.Where(p => !string.IsNullOrEmpty(p.assetPath)).ToDictionary(p => p.assetPath);
        previewList.Clear();

        if (targetSource == TargetSource.PrefabDependencies)
            BuildPreviewForDependencies(oldConfig);
        else
            BuildPreviewForNormal();
    }

    private void BuildPreviewForNormal()
    {
        var targets = CollectTargets();
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == null) continue;
            string oldName = targets[i].name;
            string newName = BuildNewName(oldName, i);

            previewList.Add(new PreviewItem
            {
                obj = targets[i],
                assetPath = AssetDatabase.GetAssetPath(targets[i]),
                oldName = oldName,
                newName = newName,
                changed = oldName != newName && !string.IsNullOrEmpty(newName),
                assetTypeLabel = string.IsNullOrEmpty(AssetDatabase.GetAssetPath(targets[i])) ? "GameObject" : "Asset"
            });
        }
    }

    // ✅ 核心修改：所有资源都支持Manual模式
    private void BuildPreviewForDependencies(Dictionary<string, PreviewItem> oldConfig)
    {
        CollectPrefabDependencies(out var dependencies, out var assetTypes);
        if (dependencies.Count == 0) return;

        IdentifyTextures(assetTypes, out var texStates, out var texSuffixesAuto, out var texCandidateSlots);

        var perPrefabTypeCounter = new Dictionary<string, int>();
        int globalIndex = 0;

        foreach (var assetPath in dependencies.Keys.OrderBy(k => k))
        {
            var ownerPrefabs = dependencies[assetPath];
            string typeLabel = assetTypes[assetPath];
            bool isShared = ownerPrefabs.Count > 1;

            oldConfig.TryGetValue(assetPath, out var existing);
            var p = new PreviewItem
            {
                obj = AssetDatabase.LoadAssetAtPath<Object>(assetPath),
                assetPath = assetPath,
                oldName = Path.GetFileNameWithoutExtension(assetPath),
                assetTypeLabel = typeLabel,
                isShared = isShared,
                sharedBy = ownerPrefabs.Select(Path.GetFileNameWithoutExtension).ToList(),
                perItemPolicy = existing?.perItemPolicy ?? PerItemPolicy.UseGlobal,
                selectedPrefabIndex = existing?.selectedPrefabIndex ?? 0,
                manualName = existing?.manualName ?? "",
                texManualSuffix = existing?.texManualSuffix,
                texCustomSuffix = existing?.texCustomSuffix ?? "",
                expanded = existing?.expanded ?? false,
                perItemPrefixOverride = existing?.perItemPrefixOverride ?? "",
                perItemExtraPrefix = existing?.perItemExtraPrefix ?? "",
                manualSkip = existing?.manualSkip ?? false // 保留手动跳过状态
            };

            if (typeLabel == "Texture")
            {
                p.texState = texStates.TryGetValue(assetPath, out var st) ? st : TextureIdentifyState.Unknown;
                if (texSuffixesAuto.TryGetValue(assetPath, out var autoSfx)) p.texAutoSuffix = autoSfx;
                if (texCandidateSlots.TryGetValue(assetPath, out var cs)) p.texCandidateSlots = cs;
            }

            string sourcePrefabPath = ownerPrefabs[0];
            bool skipDueToShared = false;
            string baseName = "";

            // ✅ 所有资源都处理perItemPolicy
            switch (p.perItemPolicy)
            {
                case PerItemPolicy.Skip:
                    skipDueToShared = true;
                    break;
                case PerItemPolicy.Manual:
                    baseName = p.manualName;
                    skipDueToShared = string.IsNullOrEmpty(baseName);
                    break;
                default: // UseGlobal
                    if (isShared)
                    {
                        // 共享资源使用全局策略
                        if (sharedAssetPolicy == SharedAssetPolicy.Skip)
                            skipDueToShared = true;
                        else if (sharedAssetPolicy == SharedAssetPolicy.FirstWins)
                            sourcePrefabPath = ownerPrefabs[0];
                        else // Warn
                            skipDueToShared = true;
                    }
                    // 非共享资源继续自动生成
                    break;
            }

            p.sourcePrefabName = Path.GetFileNameWithoutExtension(sourcePrefabPath);
            p.skipDueToShared = skipDueToShared;

            string texFinalSuffix = "";
            bool stillNeedsManual = false;

            if (typeLabel == "Texture")
            {
                texFinalSuffix = GetCurrentTextureSuffix(p);
                if (p.texState != TextureIdentifyState.Identified
                    && string.IsNullOrEmpty(p.texManualSuffix)
                    && p.perItemPolicy != PerItemPolicy.Manual)
                {
                    stillNeedsManual = true;
                }
                if (p.texManualSuffix == MANUAL_CUSTOM && string.IsNullOrEmpty(p.texCustomSuffix))
                {
                    stillNeedsManual = true;
                }
            }

            // ✅ 非贴图资源Manual模式下不需要添加后缀
            if (p.perItemPolicy == PerItemPolicy.Manual
                && !string.IsNullOrEmpty(baseName)
                && typeLabel == "Texture"
                && !baseName.EndsWith(texFinalSuffix))
            {
                baseName += texFinalSuffix;
            }

            // 应用排除规则
            if (ShouldExcludeResource(p))
            {
                p.newName = p.oldName;
                p.changed = false;
            }
            else if (!skipDueToShared && !stillNeedsManual)
            {
                // ✅ Manual模式下直接使用baseName，不再自动生成
                if (p.perItemPolicy != PerItemPolicy.Manual)
                {
                    if (depUsePrefabNameAsBase)
                    {
                        string typePrefix = !string.IsNullOrEmpty(p.perItemPrefixOverride) ? p.perItemPrefixOverride : GetPrefixForType(typeLabel);
                        baseName = (p.perItemExtraPrefix ?? "") + typePrefix + p.sourcePrefabName + texFinalSuffix;

                        string counterKey = $"{sourcePrefabPath}|{typeLabel}|{texFinalSuffix}|{p.perItemExtraPrefix}|{typePrefix}";
                        int count = perPrefabTypeCounter.ContainsKey(counterKey) ? perPrefabTypeCounter[counterKey] : 0;
                        perPrefabTypeCounter[counterKey] = count + 1;

                        if (count > 0) baseName += $"_{count + 1:D2}";
                    }
                    else
                    {
                        baseName = p.oldName;
                    }
                }

                // ✅ Manual模式下输入的名称也会经过全局重命名规则处理
                p.newName = BuildNewName(baseName, globalIndex);
                p.changed = p.oldName != p.newName;
            }
            else
            {
                p.newName = p.oldName;
                p.changed = false;
            }

            globalIndex++;
            previewList.Add(p);
        }

        DeduplicateNewNames();
    }

    private void DeduplicateNewNames()
    {
        foreach (var group in previewList.Where(p => !string.IsNullOrEmpty(p.assetPath)).GroupBy(p => Path.GetDirectoryName(p.assetPath).Replace('\\', '/')))
        {
            var occurrence = new Dictionary<string, int>();
            foreach (var p in group.Where(x => x.changed))
            {
                string key = p.newName + Path.GetExtension(p.assetPath).ToLowerInvariant();
                int count = group.Count(x => x.changed && x.newName + Path.GetExtension(x.assetPath).ToLowerInvariant() == key);

                if (count > 1)
                {
                    int idx = occurrence.ContainsKey(key) ? occurrence[key] : 0;
                    occurrence[key] = idx + 1;
                    p.newName += $"_{idx + 1:D2}";
                }
            }
        }
    }

    private void ApplyRename()
    {
        BuildPreview();
        if (previewList.Count == 0) return;

        int changeCount = previewList.Count(p => p.changed);
        if (changeCount == 0)
        {
            EditorUtility.DisplayDialog("批量重命名", "没有需要修改的对象。", "确定");
            return;
        }

        if (!EditorUtility.DisplayDialog("批量重命名", $"确认重命名 {changeCount} 个对象？\n操作无法撤销。", "确认执行", "取消"))
            return;

        AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < previewList.Count; i++)
            {
                if (!previewList[i].changed || string.IsNullOrEmpty(previewList[i].newName)) continue;

                EditorUtility.DisplayProgressBar("批量重命名", $"{previewList[i].oldName} → {previewList[i].newName}", (float)i / previewList.Count);

                if (!string.IsNullOrEmpty(previewList[i].assetPath))
                {
                    // 只修改资源文件名
                    // 不修改 FBX 内部节点
                    // 不修改 Mesh 名
                    AssetDatabase.RenameAsset(previewList[i].assetPath, previewList[i].newName);
                }
                else if (previewList[i].obj is GameObject go)
                {
                    Undo.RecordObject(go, "Batch Rename");
                    go.name = previewList[i].newName;
                    EditorUtility.SetDirty(go);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        BuildPreview();
        Repaint();
    }
}