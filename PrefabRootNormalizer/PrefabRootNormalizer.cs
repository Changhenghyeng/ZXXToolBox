using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PrefabTools
{
    /// <summary>
    /// 规范化 Prefab 根节点 Transform：根节点重置为 identity，
    /// 原 TRS 保留在子级，视觉结果不变。
    ///
    ///   首次：PrefabInstance(p,r,s) → Wrapper(1,1,1) → Content(p,r,s)
    ///        场景 P/R 写回为 override，prefab 始终 identity
    ///
    ///   重复：Wrapper(S=3) → Content(S=2)
    ///        原位烘焙：Content.S *= 3 → Wrapper.S = 1，P/R 不动
    ///
    /// 数据源为 Hierarchy 当前选中物体的 localTransform。
    /// </summary>
    public static class PrefabRootNormalizer
    {
        public const string ContentSuffix = "_Content";

        // ════════════════ 入口 ════════════════

        public static void WrapSelected()
        {
            var usageTimer = ZXXToolUsageStats.Begin();
            var targets = Selection.gameObjects;
            if (targets == null || targets.Length == 0)
            {
                EditorUtility.DisplayDialog("Normalize Root", "请先在 Hierarchy 中选择要处理的物体。", "OK");
                return;
            }

            var warnings = new List<string>();
            int success = 0, renormalized = 0, saved = 0;

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Normalize Root");

            var list = new List<GameObject>(targets);
            foreach (var go in list)
            {
                if (go == null) continue;

                if (!go.scene.IsValid())
                {
                    warnings.Add($"[{go.name}] 非场景物体，已跳过。请将物体拖入场景后重试。");
                    continue;
                }

                bool wasWrapped = IsWrappedInstance(go);
                var workGo = go;

                // ── 记录 Prefab 路径和场景 P/R ──
                string prefabPath = null;
                Vector3? overridePos = null;
                Quaternion? overrideRot = null;

                if (PrefabUtility.IsPartOfPrefabInstance(go) &&
                    PrefabUtility.GetNearestPrefabInstanceRoot(go) == go)
                {
                    prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    if (!wasWrapped)
                    {
                        overridePos = go.transform.localPosition;
                        overrideRot = go.transform.localRotation;
                    }
                }

                try
                {
                    if (wasWrapped)
                    {
                        BakeScaleInPlace(go);
                        renormalized++;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(prefabPath))
                            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.UserAction);

                        workGo = WrapInScene(go);
                    }

                    success++;

                    if (!string.IsNullOrEmpty(prefabPath))
                    {
                        // ── 保存前暂存 P/R，归零写入，再恢复为 scene override ──
                        var scenePos = workGo.transform.localPosition;
                        var sceneRot = workGo.transform.localRotation;

                        // 首次：用 Unpack 前记录的值；重复：用当前值
                        if (!wasWrapped && overridePos.HasValue) scenePos = overridePos.Value;
                        if (!wasWrapped && overrideRot.HasValue) sceneRot = overrideRot.Value;

                        workGo.transform.localPosition = Vector3.zero;
                        workGo.transform.localRotation = Quaternion.identity;

                        PrefabUtility.SaveAsPrefabAssetAndConnect(workGo, prefabPath, InteractionMode.UserAction);
                        saved++;

                        // 恢复为 scene override
                        workGo.transform.localPosition = scenePos;
                        workGo.transform.localRotation = sceneRot;
                    }
                }
                catch (System.Exception e)
                {
                    warnings.Add($"[{go.name}] 失败: {e.Message}");
                    Debug.LogException(e);
                }
            }

            Undo.CollapseUndoOperations(group);

            if (saved > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            ZXXToolUsageStats.Complete("Normalize Prefab Root", success, usageTimer);
            var msg = $"规范化完成。成功: {success}";
            if (renormalized > 0) msg += $", 重新规范化: {renormalized}";
            if (saved > 0) msg += $", 存回 Prefab: {saved}";
            if (warnings.Count > 0)
                Debug.LogWarning($"[PrefabRootNormalizer] {msg}\n  - {string.Join("\n  - ", warnings)}");
            else
                Debug.Log($"[PrefabRootNormalizer] {msg}");
        }

        // ════════════════ 首次：插入 identity 父节点 ════════════════

        private static GameObject WrapInScene(GameObject go)
        {
            var t = go.transform;
            var parent = t.parent;
            int siblingIndex = t.GetSiblingIndex();

            var lp = t.localPosition;
            var lr = t.localRotation;
            var ls = t.localScale;

            var wrapper = new GameObject(go.name);
            Undo.RegisterCreatedObjectUndo(wrapper, "Create root");
            SceneManager.MoveGameObjectToScene(wrapper, go.scene);

            var wt = wrapper.transform;
            Undo.SetTransformParent(wt, parent, "Place root");
            wt.localPosition = Vector3.zero;
            wt.localRotation = Quaternion.identity;
            wt.localScale = Vector3.one;
            wt.SetSiblingIndex(siblingIndex);

            Undo.RecordObject(go, "Rename content");
            go.name += ContentSuffix;
            Undo.SetTransformParent(t, wt, "Reparent content");
            t.localPosition = lp;
            t.localRotation = lr;
            t.localScale = ls;

            return wrapper;
        }

        // ════════════════ 重复：原位烘焙 scale ════════════════

        /// <summary>
        /// 将 wrapper 的 scale 烘焙到 _Content 子级，wrapper scale 归 1。
        /// P/R 不改动（保留为 scene override）。
        /// </summary>
        internal static void BakeScaleInPlace(GameObject wrapperGo)
        {
            var wt = wrapperGo.transform;
            var ws = wt.localScale;
            if (ws == Vector3.one) return;

            var content = wt.GetChild(0);
            Undo.RecordObject(wrapperGo, "Normalize scale");
            Undo.RecordObject(content, "Bake scale");

            content.localPosition = Vector3.Scale(content.localPosition, ws);
            content.localScale = Vector3.Scale(content.localScale, ws);
            wt.localScale = Vector3.one;
        }

        // ════════════════ Project 窗口：直接处理 Prefab 资源 ════════════════

        /// <summary>对 Project 中选中的 Prefab 资源直接加 identity 父节点。</summary>
        public static void ProcessProjectAssets()
        {
            var paths = new List<string>();
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) continue;
                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("Normalize Root", "请在 Project 窗口中选中 Prefab 资源。", "OK");
                return;
            }

            int success = 0, skipped = 0;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    var path = paths[i];
                    EditorUtility.DisplayProgressBar("Normalize Prefab Root",
                        $"({i + 1}/{paths.Count}) {path}", (float)i / paths.Count);

                    if (ProcessOnePrefabAsset(path))
                        success++;
                    else
                        skipped++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            Debug.Log($"[PrefabRootNormalizer] Project 处理完成。成功: {success}, 已跳过(已处理): {skipped}");
        }

        private static bool ProcessOnePrefabAsset(string prefabPath)
        {
            // 先检查磁盘上的 prefab 是否已规范化
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset != null && IsWrappedInstance(asset))
            {
                // 已规范化：加载 → 原位烘焙 scale → 保存
                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    BakeScaleInPlace(root);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    return true;
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }

            // 首次规范化：加载 → 插入 identity 父节点 → 保存
            var contentsRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var originalName = contentsRoot.name;
                var wrapper = new GameObject(originalName);
                contentsRoot.name = originalName + ContentSuffix;
                contentsRoot.transform.SetParent(wrapper.transform, worldPositionStays: false);
                wrapper.transform.localPosition = Vector3.zero;
                wrapper.transform.localRotation = Quaternion.identity;
                wrapper.transform.localScale = Vector3.one;

                PrefabUtility.SaveAsPrefabAsset(wrapper, prefabPath);
                return true;
            }
            finally { PrefabUtility.UnloadPrefabContents(contentsRoot); }
        }

        // ════════════════ 工具 ════════════════

        internal static bool IsWrappedInstance(GameObject go)
        {
            var t = go.transform;
            if (t.childCount != 1) return false;
            return t.GetChild(0).name == go.name + ContentSuffix;
        }
    }

    // ════════════════════════════════════════════════════
    // EditorWindow
    // ════════════════════════════════════════════════════

    public class PrefabRootNormalizerWindow : EditorWindow
    {
        private Vector2 _scroll;

        public static void ShowWindow()
        {
            var win = GetWindow<PrefabRootNormalizerWindow>("Normalize Prefab Root");
            win.minSize = new Vector2(380, 260);
            win.Show();
        }

        private static void ProcessFromProject() => PrefabRootNormalizer.ProcessProjectAssets();

        private static bool ValidateProcessFromProject()
        {
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
                    return true;
            }
            return false;
        }

        [MenuItem("GameObject/Normalize Prefab Root %#n", false, 30)]
        private static void WrapFromHierarchy() => PrefabRootNormalizer.WrapSelected();

        [MenuItem("GameObject/Normalize Prefab Root %#n", true)]
        private static bool ValidateWrapFromHierarchy()
            => Selection.gameObjects != null && Selection.gameObjects.Length > 0;

        private void OnSelectionChange() => Repaint();

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Normalize Prefab Root", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "将根节点 Transform 重置为 identity，视觉结果不变。\n" +
                "• Prefab 资产：根节点 identity（P 0, R 0, S 1）\n" +
                "• 场景实例：P / R 保留为 override，维持当前摆放\n" +
                "• 重复执行：原位烘焙 scale，P / R 不触碰",
                MessageType.Info);

            EditorGUILayout.Space(6);

            // ── 场景选中 ──
            var sceneGOs = Selection.gameObjects;
            bool hasScene = false;
            if (sceneGOs != null)
            {
                foreach (var go in sceneGOs)
                {
                    if (go == null || !go.scene.IsValid()) continue;
                    hasScene = true;
                    var t = go.transform;
                    var p = t.localPosition;
                    var r = t.localEulerAngles;
                    var s = t.localScale;
                    bool isWrapped = PrefabRootNormalizer.IsWrappedInstance(go);
                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField(go.name + (isWrapped ? "  [Wrapped]" : ""), EditorStyles.boldLabel);
                    EditorGUILayout.LabelField($"P  ({p.x:0.###}, {p.y:0.###}, {p.z:0.###})    R  ({r.x:0.###}, {r.y:0.###}, {r.z:0.###})    S  ({s.x:0.###}, {s.y:0.###}, {s.z:0.###})");
                    EditorGUILayout.EndVertical();
                }
            }

            // ── Project 选中 ──
            var projectPrefabs = new List<string>();
            foreach (var obj in Selection.objects)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
                    projectPrefabs.Add(path);
            }
            foreach (var path in projectPrefabs)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                bool isWrapped = asset != null && PrefabRootNormalizer.IsWrappedInstance(asset);
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField(Path.GetFileNameWithoutExtension(path) + (isWrapped ? "  [Wrapped]" : ""), EditorStyles.boldLabel);
                EditorGUILayout.LabelField(path, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
            }

            if (!hasScene && projectPrefabs.Count == 0)
                EditorGUILayout.HelpBox("在 Hierarchy 或 Project 中选中目标后此处预览。", MessageType.Warning);

            // ── 按钮 ──
            EditorGUILayout.Space(8);
            int total = (hasScene ? sceneGOs.Count(g => g != null && g.scene.IsValid()) : 0) + projectPrefabs.Count;
            using (new EditorGUI.DisabledScope(total == 0))
            {
                GUI.backgroundColor = new Color(0.7f, 1f, 0.7f);
                if (GUILayout.Button($"Normalize {total} Selected", GUILayout.Height(36)))
                {
                    if (hasScene) PrefabRootNormalizer.WrapSelected();
                    if (projectPrefabs.Count > 0) PrefabRootNormalizer.ProcessProjectAssets();
                }
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Ctrl+Shift+N · Hierarchy / Project 右键 → Normalize Prefab Root",
                MessageType.None);

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
    }
}
