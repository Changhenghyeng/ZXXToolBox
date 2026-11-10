using System;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ZXXTools.FBXMeshEditor
{
    [InitializeOnLoad]
    internal static class FBXMeshExporter
    {
        private const string PackageId = "com.unity.formats.fbx@4.2.1";
        private const string InstallingEditorPref = "ZXXTools.FBXMeshEditor.InstallingFBXExporter";
        private const string ExporterTypeName =
            "UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor";
        private static AddRequest _installRequest;

        static FBXMeshExporter()
        {
            EditorApplication.delayCall += CheckInstallAfterReload;
        }

        public static bool IsAvailable => Type.GetType(ExporterTypeName) != null;
        public static bool IsInstalling => (_installRequest != null && !_installRequest.IsCompleted)
            || (EditorPrefs.GetBool(InstallingEditorPref, false) && !IsAvailable);

        public static void InstallExporter()
        {
            if (IsAvailable)
            {
                EditorUtility.DisplayDialog("FBX Mesh Editor", "FBX Exporter 已安装。", "确定");
                return;
            }
            if (_installRequest != null && !_installRequest.IsCompleted) return;
            EditorPrefs.SetBool(InstallingEditorPref, true);
            _installRequest = Client.Add(PackageId);
            EditorApplication.update += MonitorInstall;
            SceneView.lastActiveSceneView?.ShowNotification(
                new GUIContent("正在安装 FBX Exporter，请稍候…"), 2.5f);
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        private static void CheckInstallAfterReload()
        {
            if (!EditorPrefs.GetBool(InstallingEditorPref, false)) return;
            if (!IsAvailable) return;
            EditorPrefs.DeleteKey(InstallingEditorPref);
            EditorUtility.DisplayDialog("FBX Mesh Editor",
                "FBX Exporter 4.2.1 安装完成，可以开始导出 FBX。", "确定");
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        private static void MonitorInstall()
        {
            if (_installRequest == null || !_installRequest.IsCompleted) return;
            EditorApplication.update -= MonitorInstall;
            if (_installRequest.Status == StatusCode.Success)
            {
                EditorPrefs.DeleteKey(InstallingEditorPref);
                EditorUtility.DisplayDialog("FBX Mesh Editor",
                    "FBX Exporter 4.2.1 安装完成，等待脚本刷新后即可导出。", "确定");
            }
            else
            {
                EditorPrefs.DeleteKey(InstallingEditorPref);
                EditorUtility.DisplayDialog("FBX Mesh Editor",
                    "FBX Exporter 安装失败：" + _installRequest.Error?.message, "确定");
            }
            _installRequest = null;
            FBXMeshEditorWindow.RepaintOpenWindow();
        }

        public static bool SaveAsNewFBX(FBXMeshEditSession session, out string message)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            if (!session.IsActive || session.MeshFilter == null || session.EditableMesh == null)
            {
                message = "请先选择模型并进入编辑模式。";
                return false;
            }
            Type exporterType = Type.GetType(ExporterTypeName);
            if (exporterType == null)
            {
                message = "请先安装 FBX Exporter 4.2.1。";
                return false;
            }

            string defaultName = session.MeshFilter.name + "_Edited.fbx";
            string assetPath = EditorUtility.SaveFilePanelInProject(
                "Save As New FBX", defaultName, "fbx", "选择新 FBX 的保存位置");
            if (string.IsNullOrEmpty(assetPath))
            {
                message = string.Empty;
                return false;
            }

            GameObject source = session.MeshFilter.gameObject;
            GameObject exportObject = new GameObject(source.name);
            try
            {
                exportObject.hideFlags = HideFlags.HideAndDontSave;
                MeshFilter filter = exportObject.AddComponent<MeshFilter>();
                MeshRenderer renderer = exportObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = session.EditableMesh;
                MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
                if (sourceRenderer != null) renderer.sharedMaterials = sourceRenderer.sharedMaterials;

                MethodInfo exportMethod = exporterType.GetMethod("ExportObject",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(string), typeof(UnityEngine.Object) }, null);
                if (exportMethod == null)
                {
                    message = "当前 FBX Exporter 缺少 ExportObject API。";
                    return false;
                }
                object result = exportMethod.Invoke(null, new object[] { assetPath, exportObject });
                if (result == null || string.IsNullOrEmpty(result.ToString()))
                {
                    message = "FBX 导出失败，没有生成有效文件。";
                    return false;
                }
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                UnityEngine.Object imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (imported == null)
                {
                    message = "FBX 已写入，但重新导入验证失败。";
                    return false;
                }
                Selection.activeObject = imported;
                EditorGUIUtility.PingObject(imported);
                ZXXToolUsageStats.Complete("FBX Mesh Editor", 1, usageTimer);
                message = "新 FBX 已保存并通过重新导入验证。";
                return true;
            }
            catch (TargetInvocationException exception)
            {
                message = "FBX 导出失败：" + (exception.InnerException?.Message ?? exception.Message);
                return false;
            }
            catch (Exception exception)
            {
                message = "FBX 导出失败：" + exception.Message;
                return false;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(exportObject);
            }
        }

        public static bool SaveMultipleAsNewFBX(IReadOnlyList<FBXMeshEditSession> sessions,
            out string message)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            message = string.Empty;
            if (sessions == null || sessions.Count == 0)
            {
                message = "请至少勾选一个需要输出的模型。";
                return false;
            }
            if (!IsAvailable)
            {
                message = "请先安装 FBX Exporter 4.2.1。";
                return false;
            }

            string absoluteFolder = EditorUtility.OpenFolderPanel(
                "Choose FBX Output Folder", Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(absoluteFolder)) return false;
            string normalizedFolder = Path.GetFullPath(absoluteFolder).Replace('\\', '/').TrimEnd('/');
            string assetsRoot = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
            if (!normalizedFolder.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase)
                && !normalizedFolder.StartsWith(assetsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                message = "输出位置必须位于当前项目的 Assets 文件夹内。";
                return false;
            }
            string assetFolder = "Assets" + normalizedFolder.Substring(assetsRoot.Length);
            int succeeded = 0;
            var failures = new List<string>();
            foreach (FBXMeshEditSession session in sessions)
            {
                if (session == null || !session.IsActive || session.MeshFilter == null) continue;
                string safeName = SanitizeFileName(session.MeshFilter.name);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath(assetFolder + "/" + safeName + "_Edited.fbx");
                try
                {
                    if (ExportToPath(session, assetPath, out string exportMessage)) succeeded++;
                    else failures.Add(session.MeshFilter.name + "：" + exportMessage);
                }
                catch (Exception exception)
                {
                    failures.Add(session.MeshFilter.name + "：" + exception.Message);
                }
            }
            AssetDatabase.Refresh();
            ZXXToolUsageStats.Complete("FBX Mesh Editor", succeeded, usageTimer);
            message = $"已输出 {succeeded} 个 FBX。";
            if (failures.Count > 0) message += "\n\n失败：\n" + string.Join("\n", failures);
            return succeeded > 0 && failures.Count == 0;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(value) ? "EditedMesh" : value;
        }

        public static bool CanOverwriteSource(FBXMeshEditSession session)
        {
            if (session == null || !session.IsActive || session.SourceMesh == null)
                return false;
            string path = AssetDatabase.GetAssetPath(session.SourceMesh);
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase);
        }

        public static bool OverwriteSourceFBX(FBXMeshEditSession session, out string message)
        {
            return OverwriteSourceFBX(session, true, out message);
        }

        public static bool OverwriteMultipleSourceFBX(IReadOnlyList<FBXMeshEditSession> sessions,
            out string message)
        {
            message = string.Empty;
            if (sessions == null || sessions.Count == 0)
            {
                message = "请至少勾选一个需要覆盖的模型。";
                return false;
            }
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FBXMeshEditSession session in sessions)
            {
                if (!CanOverwriteSource(session))
                {
                    message = "勾选项中存在无法覆盖源 FBX 的模型。";
                    return false;
                }
                string path = AssetDatabase.GetAssetPath(session.SourceMesh);
                if (!sourcePaths.Add(path))
                {
                    message = "多个勾选对象来自同一个源 FBX，无法分别覆盖。请只勾选其中一个。";
                    return false;
                }
            }
            if (!EditorUtility.DisplayDialog("Overwrite Checked Source FBX",
                    $"即将覆盖 {sessions.Count} 个源 FBX。工具会逐个验证并备份，是否继续？",
                    "覆盖", "取消"))
                return false;

            int succeeded = 0;
            var failures = new List<string>();
            foreach (FBXMeshEditSession session in sessions)
            {
                string name = session.MeshFilter != null ? session.MeshFilter.name : "Unknown";
                if (OverwriteSourceFBX(session, false, out string itemMessage)) succeeded++;
                else failures.Add(name + "：" + itemMessage);
            }
            message = $"已覆盖 {succeeded} 个源 FBX。";
            if (failures.Count > 0) message += "\n\n失败：\n" + string.Join("\n", failures);
            return succeeded > 0 && failures.Count == 0;
        }

        private static bool OverwriteSourceFBX(FBXMeshEditSession session, bool showConfirmation,
            out string message)
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            message = string.Empty;
            if (!CanOverwriteSource(session))
            {
                message = "当前编辑对象不是从 FBX 导入的 Mesh，无法覆盖源文件。";
                return false;
            }
            if (!IsAvailable)
            {
                message = "请先安装 FBX Exporter 4.2.1。";
                return false;
            }

            string sourcePath = AssetDatabase.GetAssetPath(session.SourceMesh);
            string sourceMeshName = session.SourceMesh.name;
            if (showConfirmation && !EditorUtility.DisplayDialog("Overwrite Source FBX",
                    $"即将覆盖：\n{sourcePath}\n\n工具会先创建备份；覆盖成功后，当前编辑历史将结束。是否继续？",
                    "覆盖", "取消"))
                return false;

            string sourceFullPath = Path.GetFullPath(sourcePath);
            string metaFullPath = sourceFullPath + ".meta";
            string backupFolder = Path.Combine(Directory.GetCurrentDirectory(), "Library", "FBXMeshEditorBackups");
            Directory.CreateDirectory(backupFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string backupFbx = Path.Combine(backupFolder, Path.GetFileNameWithoutExtension(sourcePath) + "_" + stamp + ".fbx");
            string backupMeta = backupFbx + ".meta";
            string folder = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            // Unity 会忽略以点开头的资源文件，因此临时 FBX 使用普通文件名并在 finally 中清理。
            string tempPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(sourcePath) + "_MeshEditorTemp.fbx");

            Mesh[] originalMeshes = GetMeshesAtPath(sourcePath);
            string sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            Dictionary<long, string> oldMeshIds = BuildMeshIdToName(originalMeshes);
            List<SerializedReferenceFile> serializedReferenceFiles =
                CaptureSerializedReferenceFiles(sourceGuid, oldMeshIds);
            var filterReferences = new List<ComponentMeshReference<MeshFilter>>();
            var colliderReferences = new List<ComponentMeshReference<MeshCollider>>();
            foreach (MeshFilter filter in Resources.FindObjectsOfTypeAll<MeshFilter>())
            {
                if (filter == null || filter.sharedMesh == null) continue;
                if (AssetDatabase.GetAssetPath(filter.sharedMesh) == sourcePath)
                    filterReferences.Add(new ComponentMeshReference<MeshFilter>(filter, filter.sharedMesh.name));
            }
            foreach (MeshCollider collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
            {
                if (collider == null || collider.sharedMesh == null) continue;
                if (AssetDatabase.GetAssetPath(collider.sharedMesh) == sourcePath)
                    colliderReferences.Add(new ComponentMeshReference<MeshCollider>(collider, collider.sharedMesh.name));
            }

            bool sourceReplaced = false;
            try
            {
                if (!ExportSourceHierarchyToPath(session, sourcePath, tempPath, out message))
                    return false;
                UnityEngine.Object[] tempAssets = AssetDatabase.LoadAllAssetsAtPath(tempPath);
                Mesh[] tempMeshes = GetMeshes(tempAssets);
                if (FindMesh(tempAssets, sourceMeshName) == null || tempMeshes.Length < originalMeshes.Length)
                {
                    message = $"临时 FBX 验证失败：原文件包含 {originalMeshes.Length} 个 Mesh，导出结果只有 {tempMeshes.Length} 个。原文件未被修改。";
                    return false;
                }

                File.Copy(sourceFullPath, backupFbx, true);
                if (File.Exists(metaFullPath)) File.Copy(metaFullPath, backupMeta, true);
                File.Copy(Path.GetFullPath(tempPath), sourceFullPath, true);
                sourceReplaced = true;
                AssetDatabase.DeleteAsset(tempPath);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                UnityEngine.Object[] importedAssets = AssetDatabase.LoadAllAssetsAtPath(sourcePath);
                Mesh importedMesh = FindMesh(importedAssets, sourceMeshName);
                if (importedMesh == null)
                    throw new InvalidOperationException("重新导入后找不到对应 Mesh：" + sourceMeshName);

                foreach (ComponentMeshReference<MeshFilter> reference in filterReferences)
                {
                    if (reference.Component == null) continue;
                    Mesh replacement = FindMesh(importedAssets, reference.MeshName);
                    if (replacement == null)
                        throw new InvalidOperationException("无法重建 MeshFilter 引用：" + reference.MeshName);
                    reference.Component.sharedMesh = replacement;
                    EditorUtility.SetDirty(reference.Component);
                }
                foreach (ComponentMeshReference<MeshCollider> reference in colliderReferences)
                {
                    if (reference.Component == null) continue;
                    Mesh replacement = FindMesh(importedAssets, reference.MeshName);
                    if (replacement == null)
                        throw new InvalidOperationException("无法重建 MeshCollider 引用：" + reference.MeshName);
                    reference.Component.sharedMesh = replacement;
                    EditorUtility.SetDirty(reference.Component);
                }
                RebuildSerializedReferences(serializedReferenceFiles, sourceGuid,
                    oldMeshIds, BuildMeshNameToId(GetMeshes(importedAssets)));
                session.CompleteOverwrite(importedMesh);
                AssetDatabase.SaveAssets();
                Selection.activeObject = importedMesh;
                EditorGUIUtility.PingObject(importedMesh);
                ZXXToolUsageStats.Complete("FBX Mesh Editor", 1, usageTimer);
                message = $"源 FBX 已安全覆盖。\n备份位置：{backupFbx}";
                return true;
            }
            catch (Exception exception)
            {
                RestoreSerializedReferenceFiles(serializedReferenceFiles);
                if (sourceReplaced && File.Exists(backupFbx))
                {
                    File.Copy(backupFbx, sourceFullPath, true);
                    if (File.Exists(backupMeta)) File.Copy(backupMeta, metaFullPath, true);
                    AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                message = "覆盖失败，原 FBX 已恢复：" + exception.Message;
                return false;
            }
            finally
            {
                if (AssetDatabase.LoadMainAssetAtPath(tempPath) != null)
                    AssetDatabase.DeleteAsset(tempPath);
            }
        }

        private static Mesh FindMesh(UnityEngine.Object[] assets, string preferredName)
        {
            Mesh first = null;
            foreach (UnityEngine.Object asset in assets)
            {
                if (!(asset is Mesh mesh)) continue;
                if (first == null) first = mesh;
                if (mesh.name == preferredName) return mesh;
            }
            return first;
        }

        private static Mesh[] GetMeshesAtPath(string assetPath)
        {
            return GetMeshes(AssetDatabase.LoadAllAssetsAtPath(assetPath));
        }

        private static Mesh[] GetMeshes(UnityEngine.Object[] assets)
        {
            var meshes = new List<Mesh>();
            foreach (UnityEngine.Object asset in assets)
                if (asset is Mesh mesh) meshes.Add(mesh);
            return meshes.ToArray();
        }

        private sealed class ComponentMeshReference<T> where T : Component
        {
            public readonly T Component;
            public readonly string MeshName;

            public ComponentMeshReference(T component, string meshName)
            {
                Component = component;
                MeshName = meshName;
            }
        }

        private sealed class SerializedReferenceFile
        {
            public string AssetPath;
            public string OriginalText;
            public bool Modified;
        }

        private static Dictionary<long, string> BuildMeshIdToName(Mesh[] meshes)
        {
            var result = new Dictionary<long, string>();
            foreach (Mesh mesh in meshes)
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long localId))
                    result[localId] = mesh.name;
            }
            return result;
        }

        private static Dictionary<string, long> BuildMeshNameToId(Mesh[] meshes)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (Mesh mesh in meshes)
            {
                if (result.ContainsKey(mesh.name))
                    throw new InvalidOperationException("FBX 内存在同名 Mesh，无法安全重建引用：" + mesh.name);
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string _, out long localId))
                    result.Add(mesh.name, localId);
            }
            return result;
        }

        /// <summary>覆盖前保存所有未打开的文本资源中指向源 FBX Mesh 的引用。</summary>
        private static List<SerializedReferenceFile> CaptureSerializedReferenceFiles(
            string guid, Dictionary<long, string> meshIds)
        {
            var result = new List<SerializedReferenceFile>();
            var openScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
                openScenes.Add(EditorSceneManager.GetSceneAt(i).path);

            string assetsRoot = Path.GetFullPath("Assets");
            string[] extensions = { ".prefab", ".unity", ".asset", ".controller", ".overrideController" };
            foreach (string fullPath in Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(fullPath);
                bool supported = false;
                foreach (string candidate in extensions)
                    if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) { supported = true; break; }
                if (!supported) continue;

                string assetPath = "Assets" + fullPath.Substring(assetsRoot.Length).Replace('\\', '/');
                if (openScenes.Contains(assetPath)) continue;
                string text;
                try { text = File.ReadAllText(fullPath); }
                catch { continue; }
                if (text.IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0) continue;

                bool containsMeshId = false;
                foreach (long localId in meshIds.Keys)
                {
                    if (Regex.IsMatch(text, @"fileID:\s*" + localId + @"\s*,\s*guid:\s*" + Regex.Escape(guid),
                            RegexOptions.IgnoreCase))
                    {
                        containsMeshId = true;
                        break;
                    }
                }
                if (containsMeshId)
                    result.Add(new SerializedReferenceFile { AssetPath = assetPath, OriginalText = text });
            }
            return result;
        }

        private static void RebuildSerializedReferences(List<SerializedReferenceFile> files, string guid,
            Dictionary<long, string> oldIds, Dictionary<string, long> newIds)
        {
            foreach (SerializedReferenceFile file in files)
            {
                string updated = file.OriginalText;
                foreach (KeyValuePair<long, string> oldEntry in oldIds)
                {
                    if (!newIds.TryGetValue(oldEntry.Value, out long newId))
                        throw new InvalidOperationException("重新导入后缺少 Mesh，无法重建引用：" + oldEntry.Value);
                    string pattern = @"(?<prefix>fileID:\s*)" + oldEntry.Key
                        + @"(?=\s*,\s*guid:\s*" + Regex.Escape(guid) + @"\b)";
                    updated = Regex.Replace(updated, pattern,
                        match => match.Groups["prefix"].Value + newId, RegexOptions.IgnoreCase);
                }
                if (updated == file.OriginalText) continue;
                File.WriteAllText(Path.GetFullPath(file.AssetPath), updated);
                file.Modified = true;
                AssetDatabase.ImportAsset(file.AssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            }
        }

        private static void RestoreSerializedReferenceFiles(List<SerializedReferenceFile> files)
        {
            if (files == null) return;
            foreach (SerializedReferenceFile file in files)
            {
                if (file == null || !file.Modified || string.IsNullOrEmpty(file.AssetPath)) continue;
                try
                {
                    File.WriteAllText(Path.GetFullPath(file.AssetPath), file.OriginalText);
                    AssetDatabase.ImportAsset(file.AssetPath,
                        ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                }
                catch { /* 回滚原 FBX 仍会继续执行。 */ }
            }
        }

        /// <summary>保留源 FBX 的完整层级，只把当前正在编辑的 Mesh 换成工作 Mesh。</summary>
        private static bool ExportSourceHierarchyToPath(FBXMeshEditSession session, string sourcePath,
            string assetPath, out string message)
        {
            message = string.Empty;
            Type exporterType = Type.GetType(ExporterTypeName);
            MethodInfo exportMethod = exporterType?.GetMethod("ExportObject",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(UnityEngine.Object) }, null);
            if (exportMethod == null)
            {
                message = "当前 FBX Exporter 缺少 ExportObject API。";
                return false;
            }

            GameObject sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (sourceRoot == null)
            {
                message = "无法读取源 FBX 的完整模型层级。";
                return false;
            }

            GameObject exportRoot = UnityEngine.Object.Instantiate(sourceRoot);
            exportRoot.name = sourceRoot.name;
            exportRoot.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                bool replaced = false;
                foreach (MeshFilter filter in exportRoot.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh != session.SourceMesh) continue;
                    filter.sharedMesh = session.EditableMesh;
                    replaced = true;
                }
                foreach (MeshCollider collider in exportRoot.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (collider.sharedMesh == session.SourceMesh)
                        collider.sharedMesh = session.EditableMesh;
                }
                if (!replaced)
                {
                    message = "源 FBX 层级中找不到当前编辑的 Mesh，原文件未被修改。";
                    return false;
                }

                object result = exportMethod.Invoke(null, new object[] { assetPath, exportRoot });
                if (result == null || string.IsNullOrEmpty(result.ToString()))
                {
                    message = "FBX 导出失败，没有生成有效文件。";
                    return false;
                }
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(exportRoot);
            }
        }

        private static bool ExportToPath(FBXMeshEditSession session, string assetPath, out string message)
        {
            message = string.Empty;
            Type exporterType = Type.GetType(ExporterTypeName);
            MethodInfo exportMethod = exporterType?.GetMethod("ExportObject",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(UnityEngine.Object) }, null);
            if (exportMethod == null)
            {
                message = "当前 FBX Exporter 缺少 ExportObject API。";
                return false;
            }

            GameObject source = session.MeshFilter.gameObject;
            GameObject exportObject = new GameObject(source.name) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                MeshFilter filter = exportObject.AddComponent<MeshFilter>();
                MeshRenderer renderer = exportObject.AddComponent<MeshRenderer>();
                filter.sharedMesh = session.EditableMesh;
                MeshRenderer sourceRenderer = source.GetComponent<MeshRenderer>();
                if (sourceRenderer != null) renderer.sharedMaterials = sourceRenderer.sharedMaterials;
                object result = exportMethod.Invoke(null, new object[] { assetPath, exportObject });
                if (result == null || string.IsNullOrEmpty(result.ToString()))
                {
                    message = "FBX 导出失败，没有生成有效文件。";
                    return false;
                }
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                return AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(exportObject);
            }
        }
    }
}
