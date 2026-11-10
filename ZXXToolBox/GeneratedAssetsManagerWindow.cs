using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ZXXTools.GeneratedAssets
{
    /// <summary>统一迁移、审计和清理 Assets/Generated 中的工具产物。</summary>
    public sealed class GeneratedAssetsManagerWindow : EditorWindow
    {
        private const string Root = "Assets/Generated";
        private const string MigrationKey = "ZXX.GeneratedAssets.LayoutVersion";
        private const int LayoutVersion = 1;

        private sealed class Entry
        {
            public string path;
            public string owner;
            public string lifecycle;
            public string type;
            public bool inUse;
            public bool selected;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private Vector2 _scroll;
        private bool _showWorking = true;
        private bool _showBaked = true;
        private bool _showOther = true;

        public static void Open()
        {
            GetWindow<GeneratedAssetsManagerWindow>("Generated Assets");
        }

        [InitializeOnLoadMethod]
        private static void ScheduleMigration()
        {
            if (EditorPrefs.GetInt(MigrationKey, 0) >= LayoutVersion) return;
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (MigrateLegacyLayout(false))
                    Debug.Log("[Generated Assets] 已按工具、生命周期和资源类型整理旧目录，所有移动均保留 GUID。");
                EditorPrefs.SetInt(MigrationKey, LayoutVersion);
            };
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Working 是可恢复编辑档；Baked 是正式输出；Archive 只用于用户明确留档。\n"
                + "清理不会自动勾选任何文件，仍在被工程资产引用的文件禁止删除。",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                {
                    var usageTimer = ZXXToolUsageStats.Begin();
                    Refresh();
                    ZXXToolUsageStats.Complete("Generated Assets Manager", 1, usageTimer);
                }
                if (GUILayout.Button("Migrate Legacy Layout", GUILayout.Width(160)))
                {
                    MigrateLegacyLayout(true);
                    Refresh();
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!_entries.Any(e => e.selected && !e.inUse)))
                {
                    if (GUILayout.Button("Delete Selected Unused", GUILayout.Width(170)))
                        DeleteSelected();
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _showWorking = GUILayout.Toggle(_showWorking, "Working", EditorStyles.toolbarButton);
                _showBaked = GUILayout.Toggle(_showBaked, "Baked / Archive", EditorStyles.toolbarButton);
                _showOther = GUILayout.Toggle(_showOther, "Other", EditorStyles.toolbarButton);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{_entries.Count} assets", EditorStyles.miniLabel);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (Entry entry in _entries)
            {
                if (!ShouldShow(entry)) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(entry.inUse))
                        entry.selected = EditorGUILayout.Toggle(entry.selected, GUILayout.Width(18));
                    GUILayout.Label(entry.inUse ? "In Use" : "Unused", GUILayout.Width(52));
                    GUILayout.Label(entry.owner, GUILayout.Width(135));
                    GUILayout.Label(entry.lifecycle, GUILayout.Width(60));
                    GUILayout.Label(entry.type, GUILayout.Width(70));
                    if (GUILayout.Button(entry.path.Substring(Root.Length + 1), EditorStyles.linkLabel))
                    {
                        UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(entry.path);
                        Selection.activeObject = asset;
                        EditorGUIUtility.PingObject(asset);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private bool ShouldShow(Entry entry)
        {
            if (entry.lifecycle == "Working") return _showWorking;
            if (entry.lifecycle == "Baked" || entry.lifecycle == "Archive") return _showBaked;
            return _showOther;
        }

        private void Refresh()
        {
            _entries.Clear();
            if (!AssetDatabase.IsValidFolder(Root)) return;

            HashSet<string> protectedPaths = BuildProtectedGeneratedPaths();
            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { Root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                string relative = path.Substring(Root.Length + 1);
                string[] parts = relative.Split('/');
                _entries.Add(new Entry
                {
                    path = path,
                    owner = parts.Length > 0 ? parts[0] : "Unknown",
                    lifecycle = parts.FirstOrDefault(p => p == "Working" || p == "Baked" || p == "Archive") ?? "Other",
                    type = ClassifyType(path),
                    inUse = protectedPaths.Contains(path)
                });
            }
            _entries.Sort((a, b) => string.Compare(a.path, b.path, StringComparison.OrdinalIgnoreCase));
            Repaint();
        }

        private static HashSet<string> BuildProtectedGeneratedPaths()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Path Builder 的编辑会话通过场景中的隐藏 Session Marker 关联，
            // 并不是普通的 Unity 资产依赖。只要它仍位于 Working/Settings，
            // 就必须由 Path Builder 的 Bake/Discard 流程决定何时删除，
            // 通用清理器不得把“保留样条与设置”的会话误判为孤立资产。
            const string pathBuilderWorkingSettings =
                "Assets/Generated/PathBuilder/Working/Settings";
            if (AssetDatabase.IsValidFolder(pathBuilderWorkingSettings))
            {
                foreach (string guid in AssetDatabase.FindAssets(
                             string.Empty, new[] { pathBuilderWorkingSettings }))
                {
                    string protectedPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!AssetDatabase.IsValidFolder(protectedPath))
                        result.Add(protectedPath);
                }
            }

            string[] allPaths = AssetDatabase.GetAllAssetPaths();
            foreach (string rootPath in allPaths)
            {
                if (rootPath.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase)
                    && !rootPath.Contains("/Baked/Prefabs/")
                    && !rootPath.Contains("/Archive/"))
                    continue;
                foreach (string dependency in AssetDatabase.GetDependencies(rootPath, true))
                {
                    if (dependency.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase))
                        result.Add(dependency);
                }
            }
            return result;
        }

        private void DeleteSelected()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            List<Entry> targets = _entries.Where(e => e.selected && !e.inUse).ToList();
            if (targets.Count == 0) return;
            string preview = string.Join("\n", targets.Take(12).Select(e => "• " + e.path));
            if (targets.Count > 12) preview += $"\n…以及另外 {targets.Count - 12} 个文件";
            if (!EditorUtility.DisplayDialog(
                    "删除未使用的生成文件",
                    $"将通过 AssetDatabase 删除 {targets.Count} 个未发现引用的文件：\n\n{preview}\n\n此操作不能通过普通 Undo 恢复。",
                    "Delete", "Cancel"))
                return;

            int deleted = 0;
            foreach (Entry entry in targets)
                if (AssetDatabase.DeleteAsset(entry.path)) deleted++;
            AssetDatabase.SaveAssets();
            Refresh();
            ZXXToolUsageStats.Complete("Generated Assets Manager", deleted, usageTimer);
            EditorUtility.DisplayDialog("Generated Assets", $"已删除 {deleted} 个未使用文件。", "确定");
        }

        private static string ClassifyType(string path)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == typeof(Mesh)) return "Models";
            if (typeof(Texture).IsAssignableFrom(type)) return "Textures";
            if (type == typeof(Material)) return "Materials";
            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return "Prefabs";
            if (typeof(ScriptableObject).IsAssignableFrom(type)) return "Settings";
            return "Data";
        }

        private static bool MigrateLegacyLayout(bool showResult)
        {
            int moved = 0;
            moved += MoveFolderContents("Assets/Generated/FBXMeshEditorWorking", path =>
                path.EndsWith("_Record.asset", StringComparison.OrdinalIgnoreCase)
                    ? "Assets/Generated/FBXMeshEditor/Working/Settings"
                    : "Assets/Generated/FBXMeshEditor/Working/Models");
            moved += MoveFolderContents("Assets/Generated/PathBuilderAuthoring", _ =>
                "Assets/Generated/PathBuilder/Working/Settings");
            moved += MoveFolderContents("Assets/Generated/PathBuilderMeshes", path =>
                path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                    ? "Assets/Generated/PathBuilder/Baked/Materials"
                    : "Assets/Generated/PathBuilder/Baked/Models");
            moved += MoveFolderContents("Assets/Generated/PathBuilderPrefabs", _ =>
                "Assets/Generated/PathBuilder/Baked/Prefabs");
            moved += MoveFolderContents("Assets/Generated/ToonMaterialConversion", ClassifyToonFolder);
            moved += MoveFolderContents("Assets/Generated/SLGMapTiles", ClassifySlgFolder);

            UpdatePathBuilderSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (showResult)
                EditorUtility.DisplayDialog("Generated Assets", $"迁移完成，共移动 {moved} 个文件；GUID 保持不变。", "确定");
            return moved > 0;
        }

        private static string ClassifyToonFolder(string path)
        {
            return path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                ? "Assets/Generated/ToonMaterialConverter/Materials"
                : "Assets/Generated/ToonMaterialConverter/Textures";
        }

        private static string ClassifySlgFolder(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".mat") return "Assets/Generated/SLGMapTileCutter/Materials";
            if (extension == ".png" || extension == ".tga" || extension == ".jpg" || extension == ".exr")
                return "Assets/Generated/SLGMapTileCutter/Textures";
            if (extension == ".prefab") return "Assets/Generated/SLGMapTileCutter/Prefabs";
            return path.EndsWith("Config.asset", StringComparison.OrdinalIgnoreCase)
                ? "Assets/Generated/SLGMapTileCutter/Settings"
                : "Assets/Generated/SLGMapTileCutter/Models";
        }

        private static int MoveFolderContents(string sourceFolder, Func<string, string> destinationResolver)
        {
            if (!AssetDatabase.IsValidFolder(sourceFolder)) return 0;
            int moved = 0;
            string[] paths = AssetDatabase.FindAssets(string.Empty, new[] { sourceFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !AssetDatabase.IsValidFolder(path))
                .ToArray();
            foreach (string source in paths)
            {
                string destinationFolder = destinationResolver(source);
                EnsureFolder(destinationFolder);
                string destination = AssetDatabase.GenerateUniqueAssetPath(
                    destinationFolder + "/" + Path.GetFileName(source));
                string error = AssetDatabase.MoveAsset(source, destination);
                if (string.IsNullOrEmpty(error)) moved++;
                else Debug.LogError($"[Generated Assets] 移动失败：{source}\n{error}");
            }
            DeleteFolderIfEmpty(sourceFolder);
            return moved;
        }

        private static void UpdatePathBuilderSettings()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Generated/PathBuilder/Working/Settings")) return;
            foreach (string guid in AssetDatabase.FindAssets(string.Empty,
                         new[] { "Assets/Generated/PathBuilder/Working/Settings" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (!asset) continue;
                var serialized = new SerializedObject(asset);
                bool changed = ReplacePath(serialized.FindProperty("generatedAssetOutputFolder"),
                    "Assets/Generated/PathBuilderMeshes", "Assets/Generated/PathBuilder/Baked/Models");
                changed |= ReplacePath(serialized.FindProperty("prefabOutputFolder"),
                    "Assets/Generated/PathBuilderPrefabs", "Assets/Generated/PathBuilder/Baked/Prefabs");
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool ReplacePath(SerializedProperty property, string oldValue, string newValue)
        {
            if (property == null || property.propertyType != SerializedPropertyType.String
                || !string.Equals(property.stringValue, oldValue, StringComparison.OrdinalIgnoreCase)) return false;
            property.stringValue = newValue;
            return true;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)) return;
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static void DeleteFolderIfEmpty(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            if (AssetDatabase.FindAssets(string.Empty, new[] { folder }).Length == 0)
                AssetDatabase.DeleteAsset(folder);
        }
    }
}
