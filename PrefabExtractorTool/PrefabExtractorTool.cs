// PrefabExtractorTool.cs
// Unity 2022 Editor 工具:从大预制体中提取共享子预制体
//
// 使用方法:
//   1. 将本文件放到项目的 Editor 文件夹下(如 Assets/Editor/PrefabExtractorTool.cs)
//   2. 菜单栏 Tools -> 预制体提取工具
//
// 核心逻辑:
//   - 每个大预制体单独处理,提取出的子预制体只在该大预制体内部共享
//   - 不同大预制体之间不交叉引用,互相隔离
//   - 同一个大预制体内,Mesh 引用 + 子物体结构 完全一致的节点 -> 共用同一个子预制体
//   - 材质差异、Transform(位移/旋转/缩放)差异 作为实例 override 保留在大预制体上

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PrefabTools
{
    public class PrefabExtractorTool : EditorWindow
    {
        // ============ 配置项 ============
        private List<GameObject> _sourcePrefabs = new List<GameObject>();
        private string _outputRootFolder = "Assets/GeneratedSubPrefabs";
        private bool _onlyExtractMeshObjects = true;   // 只提取含 Mesh 的节点(及其子树)
        private bool _includeSkinnedMesh = true;       // 同时考虑 SkinnedMeshRenderer
        private int _minGroupSize = 2;                 // 至少出现 N 次才提取(1 = 单独出现也提取)
        private bool _dryRun = true;                   // 试运行
        private bool _autoBackup = true;               // 执行前自动备份(用于撤销)
        private bool _checkRefsOnUndo = true;          // 撤销前检查外部引用(项目大时可关闭)
        private bool _debugMode = false;               // 调试模式:打印每个节点的签名
        private bool _looseMatching = false;           // 宽松匹配:只比较 Mesh 资产路径,忽略 FileID
        private Vector2 _scrollPos;
        private Vector2 _logScrollPos;
        private Vector2 _previewScrollPos;
        private StringBuilder _log = new StringBuilder();

        // 全局分析结果(所有大预制体一起分析)
        private GlobalAnalyzeResult _analyzeResult;

        // 上一次执行的撤销记录(保存到 EditorPrefs 跨域可用)
        private const string UndoRecordKey = "PrefabExtractorTool.LastUndoRecord";

        public static void Open()
        {
            var win = GetWindow<PrefabExtractorTool>("Prefab Extractor");
            win.minSize = new Vector2(620, 700);
        }

        // ===================== GUI =====================

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "工作流:\n" +
                "1) 把场景里用到的大预制体都拖到下面列表里\n" +
                "2) 点【分析】预览跨大预制体的共享模型(全局合并分析)\n" +
                "3) 确认无误后取消【试运行】,再点【执行提取】\n\n" +
                "分析逻辑:所有大预制体合并在一起分析,Mesh + 子物体结构 相同的节点共用同一个子预制体;\n" +
                "材质差异、Transform 差异作为实例 override 保留在大预制体上。\n\n" +
                "重要:执行前请用版本控制(Git/SVN)提交一次当前状态,便于回滚!",
                MessageType.Info);

            EditorGUILayout.Space();
            DrawSourceList();
            EditorGUILayout.Space();
            DrawSettings();
            EditorGUILayout.Space();
            DrawButtons();
            EditorGUILayout.Space();
            DrawAnalyzePreview();
            EditorGUILayout.Space();
            DrawLog();

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

        private void DrawSourceList()
        {
            EditorGUILayout.LabelField("① 要处理的大预制体", EditorStyles.boldLabel);

            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.Height(140));
            int removeIdx = -1;
            for (int i = 0; i < _sourcePrefabs.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _sourcePrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                    $"预制体 {i}", _sourcePrefabs[i], typeof(GameObject), false);
                if (GUILayout.Button("移除", GUILayout.Width(60))) removeIdx = i;
                EditorGUILayout.EndHorizontal();
            }
            if (removeIdx >= 0) _sourcePrefabs.RemoveAt(removeIdx);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ 添加一行")) _sourcePrefabs.Add(null);
            if (GUILayout.Button("从 Project 选中导入"))
            {
                foreach (var obj in Selection.objects)
                {
                    if (obj is GameObject go
                        && PrefabUtility.IsPartOfPrefabAsset(go)
                        && !_sourcePrefabs.Contains(go))
                    {
                        _sourcePrefabs.Add(go);
                    }
                }
            }
            if (GUILayout.Button("清空列表")) _sourcePrefabs.Clear();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawSettings()
        {
            EditorGUILayout.LabelField("② 设置", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("子预制体输出根目录", GUILayout.Width(140));
            _outputRootFolder = EditorGUILayout.TextField(_outputRootFolder);
            if (GUILayout.Button("选择...", GUILayout.Width(60)))
            {
                string sel = EditorUtility.OpenFolderPanel("选择输出目录", Application.dataPath, "");
                if (!string.IsNullOrEmpty(sel))
                {
                    if (sel.StartsWith(Application.dataPath))
                        _outputRootFolder = "Assets" + sel.Substring(Application.dataPath.Length);
                    else
                        EditorUtility.DisplayDialog("提示", "请选择项目 Assets 内的目录", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                $"所有子预制体统一生成到此目录下(扁平存放):\n" +
                $"{_outputRootFolder}/<Mesh名>.prefab\n" +
                $"分析时跨所有大预制体合并相同模型,生成共享的子预制体",
                MessageType.None);

            _onlyExtractMeshObjects = EditorGUILayout.Toggle(
                new GUIContent("只提取含 Mesh 的节点", "勾选后仅 MeshFilter / SkinnedMeshRenderer 的节点会被分析"),
                _onlyExtractMeshObjects);
            _includeSkinnedMesh = EditorGUILayout.Toggle(
                new GUIContent("包含 SkinnedMeshRenderer", "勾选则也提取蒙皮网格"),
                _includeSkinnedMesh);
            _minGroupSize = EditorGUILayout.IntField(
                new GUIContent("最小共享数量", "同一大预制体内,相同结构至少出现 N 次才提取;设为 1 则单个也提取"),
                _minGroupSize);
            _minGroupSize = Mathf.Max(1, _minGroupSize);

            _dryRun = EditorGUILayout.Toggle(
                new GUIContent("试运行(不写入磁盘)", "强烈建议先试运行确认无误"),
                _dryRun);
            _autoBackup = EditorGUILayout.Toggle(
                new GUIContent("执行前自动备份(用于撤销)", "勾选后会把所有大预制体复制一份到备份目录,可通过 撤销 按钮恢复"),
                _autoBackup);
            _checkRefsOnUndo = EditorGUILayout.Toggle(
                new GUIContent("撤销时检查外部引用", "撤销前扫描项目,检测子预制体是否被外部引用。项目很大时可关闭以加快撤销速度"),
                _checkRefsOnUndo);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("匹配规则", EditorStyles.miniBoldLabel);
            _looseMatching = EditorGUILayout.Toggle(
                new GUIContent("宽松匹配(只看 Mesh 资产)",
                    "默认严格匹配会区分 fbx 内不同的 Mesh 子对象(GUID+FileID);\n" +
                    "宽松模式只看 Mesh 资产路径,fbx 重新导入产生的不同 Mesh 也会归为同一组"),
                _looseMatching);
            _debugMode = EditorGUILayout.Toggle(
                new GUIContent("调试模式(打印签名)",
                    "分析时把每个节点的签名摘要打到日志,排查为什么本应一致的节点被分到不同组"),
                _debugMode);
        }

        private void DrawButtons()
        {
            EditorGUILayout.LabelField("③ 操作", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
            if (GUILayout.Button("分析", GUILayout.Height(30))) Analyze();

            GUI.backgroundColor = _dryRun ? new Color(1f, 0.9f, 0.6f) : new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button(_dryRun ? "执行提取(试运行)" : "执行提取(写入磁盘)", GUILayout.Height(30)))
                Execute();

            // 撤销按钮:只有存在撤销记录时可用
            var record = LoadUndoRecord();
            GUI.enabled = record != null;
            GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
            if (GUILayout.Button("撤销上次执行", GUILayout.Height(30)))
                UndoLastExecute();
            GUI.enabled = true;

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            if (record != null)
            {
                EditorGUILayout.HelpBox(
                    $"可撤销:{record.Timestamp}\n" +
                    $"备份位置:{record.BackupFolder}\n" +
                    $"修改的大预制体:{record.ModifiedPrefabs.Count} 个,生成的子预制体:{record.CreatedSubPrefabs.Count} 个",
                    MessageType.None);
            }
        }

        private void DrawAnalyzePreview()
        {
            if (_analyzeResult == null || _analyzeResult.Groups.Count == 0) return;

            EditorGUILayout.LabelField("④ 分析结果预览(全局合并)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"总候选节点: {_analyzeResult.TotalCandidateCount},可提取共享组: {_analyzeResult.Groups.Count}");

            _previewScrollPos = EditorGUILayout.BeginScrollView(
                _previewScrollPos, EditorStyles.helpBox, GUILayout.Height(220));
            foreach (var g in _analyzeResult.Groups)
            {
                int prefabSpan = g.Nodes.Select(n => n.PrefabAssetPath).Distinct().Count();
                EditorGUILayout.LabelField($"  • [{g.RepresentativeName}] {g.Nodes.Count} 个实例,跨 {prefabSpan} 个大预制体");
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawLog()
        {
            EditorGUILayout.LabelField("⑤ 日志", EditorStyles.boldLabel);
            _logScrollPos = EditorGUILayout.BeginScrollView(_logScrollPos, GUILayout.Height(200));
            EditorGUILayout.TextArea(_log.ToString(), GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("清空日志")) _log.Clear();
        }

        // ===================== 分析 =====================

        private void Analyze()
        {
            _analyzeResult = null;
            _log.Clear();
            Log("=== 开始全局分析 ===");

            var globalGroups = new Dictionary<string, ExtractGroup>(); // signature -> group
            int totalCandidates = 0;

            int prefabIdx = 0;
            try
            {
                foreach (var rootPrefab in _sourcePrefabs)
                {
                    prefabIdx++;
                    if (rootPrefab == null) continue;
                    if (!PrefabUtility.IsPartOfPrefabAsset(rootPrefab))
                    {
                        Log($"[跳过] {rootPrefab.name} 不是预制体资产");
                        continue;
                    }

                    EditorUtility.DisplayProgressBar("分析中",
                        $"扫描 {rootPrefab.name} ({prefabIdx}/{_sourcePrefabs.Count})",
                        (float)prefabIdx / Mathf.Max(1, _sourcePrefabs.Count));

                    string assetPath = AssetDatabase.GetAssetPath(rootPrefab);
                    GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                    int candidatesInThis = 0;

                    try
                    {
                        var allTransforms = contents.GetComponentsInChildren<Transform>(true);
                        foreach (var t in allTransforms)
                        {
                            if (t == contents.transform) continue;
                            if (_onlyExtractMeshObjects && !HasMeshInSubtree(t)) continue;

                            string sig = ComputeStructuralSignature(t);
                            if (!globalGroups.TryGetValue(sig, out var grp))
                            {
                                grp = new ExtractGroup
                                {
                                    Signature = sig,
                                    RepresentativeName = GetMeshBasedName(t, t.name),
                                    SignatureSummary = ComputeSignatureSummary(t)
                                };
                                globalGroups[sig] = grp;
                            }
                            grp.Nodes.Add(new NodeRef
                            {
                                PrefabAssetPath = assetPath,
                                HierarchyPath = GetHierarchyPath(contents.transform, t)
                            });
                            candidatesInThis++;
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }

                    totalCandidates += candidatesInThis;
                    Log($"  [{rootPrefab.name}] 候选节点: {candidatesInThis}");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // 过滤:总出现次数 >= _minGroupSize
            var validGroups = globalGroups.Values
                .Where(g => g.Nodes.Count >= _minGroupSize)
                .OrderByDescending(g => g.Nodes.Max(n => n.HierarchyPath.Count(c => c == '/'))) // 深的先,避免父子冲突
                .ToList();

            // 父子冲突过滤(同一大预制体内,组A的节点不能是组B节点的祖先或后代)
            var keptByPrefab = new Dictionary<string, HashSet<string>>();
            var finalGroups = new List<ExtractGroup>();

            foreach (var g in validGroups)
            {
                bool conflict = false;
                // 检查每个节点是否与同大预制体内已保留节点形成父子关系
                foreach (var n in g.Nodes)
                {
                    if (!keptByPrefab.TryGetValue(n.PrefabAssetPath, out var keptPaths)) continue;
                    foreach (var kept in keptPaths)
                    {
                        if (kept.StartsWith(n.HierarchyPath + "/") || n.HierarchyPath.StartsWith(kept + "/"))
                        {
                            conflict = true;
                            break;
                        }
                    }
                    if (conflict) break;
                }
                if (conflict)
                {
                    Log($"  [冲突跳过] 组 [{g.RepresentativeName}] 与已选组存在父子关系");
                    continue;
                }
                foreach (var n in g.Nodes)
                {
                    if (!keptByPrefab.TryGetValue(n.PrefabAssetPath, out var keptPaths))
                    {
                        keptPaths = new HashSet<string>();
                        keptByPrefab[n.PrefabAssetPath] = keptPaths;
                    }
                    keptPaths.Add(n.HierarchyPath);
                }
                finalGroups.Add(g);
            }

            // 处理重名:同名的组要加序号区分
            var nameCount = new Dictionary<string, int>();
            foreach (var g in finalGroups)
            {
                string baseName = SanitizeName(g.RepresentativeName);
                if (nameCount.TryGetValue(baseName, out int count))
                {
                    nameCount[baseName] = count + 1;
                    g.RepresentativeName = $"{baseName}_{count + 1}";
                }
                else
                {
                    nameCount[baseName] = 1;
                    g.RepresentativeName = baseName;
                }
            }

            _analyzeResult = new GlobalAnalyzeResult
            {
                TotalCandidateCount = totalCandidates,
                Groups = finalGroups
            };

            Log($"\n总候选节点: {totalCandidates}");
            Log($"全部分组(去重后): {globalGroups.Count}");
            Log($"可提取共享组(>= {_minGroupSize} 次): {finalGroups.Count}");
            foreach (var g in finalGroups)
            {
                int prefabSpan = g.Nodes.Select(n => n.PrefabAssetPath).Distinct().Count();
                Log($"  ✓ [{g.RepresentativeName}] 共 {g.Nodes.Count} 个实例,跨 {prefabSpan} 个大预制体");
                if (_debugMode)
                    Log($"      签名摘要: {g.SignatureSummary}");
            }

            // 调试模式:打印所有未通过阈值的孤立组,以及可能"看起来应该合并但没合并"的相似组
            if (_debugMode)
            {
                var lonelyGroups = globalGroups.Values
                    .Where(g => g.Nodes.Count < _minGroupSize)
                    .OrderBy(g => g.RepresentativeName)
                    .ToList();
                Log($"\n[调试] 未通过阈值的孤立组: {lonelyGroups.Count}");

                // 按 RepresentativeName 聚类,如果有多个孤立组用了同一个 Mesh 但签名不同,
                // 说明它们的"结构"有差异(子物体数/层级/Mesh FileID 等)
                var byName = lonelyGroups.GroupBy(g => g.RepresentativeName)
                    .Where(grp => grp.Count() > 1)
                    .ToList();
                if (byName.Count > 0)
                {
                    Log($"[调试] 以下名字下有多个不同签名的孤立组(本应合并但没合并):");
                    foreach (var grp in byName)
                    {
                        Log($"  名字 [{grp.Key}] 出现 {grp.Count()} 个不同签名的组:");
                        foreach (var g in grp)
                        {
                            string sample = g.Nodes[0].HierarchyPath;
                            Log($"    - 节点示例: {sample}");
                            Log($"      签名摘要: {g.SignatureSummary}");
                        }
                    }
                    Log($"  → 提示:如果它们看起来一样,试试勾选【宽松匹配】再分析");
                }
                else
                {
                    Log($"[调试] 未发现同名孤立组,材质差异不是签名差异的来源");
                }
            }

            Log("\n=== 分析完成 ===");
        }

        // 子预制体的命名:直接用候选节点(父节点)自己的名字
        // 不用 Mesh 子节点的名字,因为不同模型的 mesh 子节点可能同名(美术习惯),
        // 而父节点名字才是美术真正用来标识"这是哪个模型"的(如 Tree_05 vs Tree_11)
        // 清理 Unity 复制产生的 " (1)" " (2)" 之类后缀
        private string GetMeshBasedName(Transform t, string fallback)
        {
            string raw = !string.IsNullOrEmpty(t.name) ? t.name : fallback;
            return CleanCopySuffix(raw);
        }

        // 清理 " (1)" " (2)" 之类的复制后缀(Unity 在 Hierarchy 里复制对象时自动加的)
        private static readonly System.Text.RegularExpressions.Regex _copySuffixRegex
            = new System.Text.RegularExpressions.Regex(@"\s*\(\d+\)\s*$");

        private string CleanCopySuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            // 可能有多重后缀: "Tree (1) (2)" -> "Tree"
            string s = name;
            string prev;
            do
            {
                prev = s;
                s = _copySuffixRegex.Replace(s, "").Trim();
            } while (s != prev && !string.IsNullOrEmpty(s));
            return string.IsNullOrEmpty(s) ? name : s;
        }

        // 判断该 Transform 子树内是否含有 Mesh
        private bool HasMeshInSubtree(Transform t)
        {
            var mfs = t.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in mfs)
                if (mf.sharedMesh != null) return true;

            if (_includeSkinnedMesh)
            {
                var smrs = t.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                foreach (var smr in smrs)
                    if (smr.sharedMesh != null) return true;
            }
            return false;
        }

        // 计算结构签名:Mesh 引用 + 子物体结构(不含材质、不含 Transform 值)
        // 同一签名 = 视为同一个"可共享模型"
        private string ComputeStructuralSignature(Transform root)
        {
            var sb = new StringBuilder();
            BuildSignatureRecursive(root, sb, 0);
            return sb.ToString();
        }

        // 生成人类可读的签名摘要,用于调试日志
        private string ComputeSignatureSummary(Transform root)
        {
            var sb = new StringBuilder();
            BuildSummaryRecursive(root, sb, 0);
            return sb.ToString();
        }

        private void BuildSummaryRecursive(Transform t, StringBuilder sb, int depth)
        {
            if (depth > 0) sb.Append(" / ");
            sb.Append(t.name);

            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                sb.Append("[Mesh=").Append(mf.sharedMesh.name).Append("]");

            if (_includeSkinnedMesh)
            {
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                    sb.Append("[SkinnedMesh=").Append(smr.sharedMesh.name).Append("]");
            }

            if (t.childCount > 0)
            {
                sb.Append("(").Append(t.childCount).Append(" children)");
                // 限制深度避免日志过长
                if (depth < 2)
                {
                    for (int i = 0; i < t.childCount; i++)
                        BuildSummaryRecursive(t.GetChild(i), sb, depth + 1);
                }
            }
        }

        private void BuildSignatureRecursive(Transform t, StringBuilder sb, int depth)
        {
            // 深度标记(用于结构对齐;不依赖名字本身,但顺序敏感)
            sb.Append('[').Append(depth).Append(']');

            // MeshFilter 的 Mesh
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                sb.Append("M:").Append(GetMeshIdentifier(mf.sharedMesh)).Append(';');
            else
                sb.Append("M:-;");

            // SkinnedMeshRenderer 的 Mesh
            if (_includeSkinnedMesh)
            {
                var smr = t.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                    sb.Append("SM:").Append(GetMeshIdentifier(smr.sharedMesh)).Append(';');
                else
                    sb.Append("SM:-;");
            }

            // 子物体数量(确保结构一致)
            sb.Append("C:").Append(t.childCount).Append('{');
            // 按 sibling 顺序递归
            for (int i = 0; i < t.childCount; i++)
            {
                BuildSignatureRecursive(t.GetChild(i), sb, depth + 1);
            }
            sb.Append('}');
        }

        // 严格模式:GUID + FileID,精确到 fbx 内某个具体的 Mesh 子对象
        // 宽松模式:仅 GUID + Mesh 名,fbx 重新导入产生的不同 FileID 也能匹配
        private string GetMeshIdentifier(Mesh mesh)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long fileId))
            {
                return _looseMatching
                    ? $"{guid}|{mesh.name}"
                    : $"{guid}:{fileId}";
            }
            // 非资产 mesh（如内置 Cube/Sphere）用名称作为稳定标识，避免 InstanceID 在不同会话变化导致签名不一致
            return mesh.name;
        }

        private string GetHierarchyPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add($"{cur.name}#{cur.GetSiblingIndex()}"); // 加 sibling 索引避免同名歧义
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // ===================== 执行 =====================

        private void Execute()
        {
            if (_analyzeResult == null || _analyzeResult.Groups.Count == 0)
            {
                if (!EditorUtility.DisplayDialog("提示", "尚未分析,是否现在分析并执行?", "是", "取消"))
                    return;
                Analyze();
                if (_analyzeResult == null || _analyzeResult.Groups.Count == 0)
                {
                    EditorUtility.DisplayDialog("提示", "没有可提取的共享组", "OK");
                    return;
                }
            }

            if (!_dryRun)
            {
                if (!EditorUtility.DisplayDialog(
                    "⚠️ 确认执行",
                    "即将修改所有大预制体并生成共享子预制体。\n" +
                    (_autoBackup ? "已开启自动备份,可通过【撤销上次执行】恢复。\n" : "未开启自动备份,操作不可逆!\n") +
                    "建议先用 Git/SVN 提交当前状态。\n\n是否继续?",
                    "继续", "取消"))
                    return;
            }

            Log(_dryRun ? "\n=== 开始试运行(不写入磁盘) ===" : "\n=== 开始执行(写入磁盘) ===");

            // 准备撤销记录
            UndoRecord undoRecord = null;
            if (!_dryRun && _autoBackup)
            {
                undoRecord = new UndoRecord
                {
                    Timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    BackupFolder = $"{_outputRootFolder}/_Backup_{System.DateTime.Now:yyyyMMdd_HHmmss}"
                };
                EnsureFolder(undoRecord.BackupFolder);
                Log($"备份目录: {undoRecord.BackupFolder}");
            }

            int totalCreated = 0, totalReplaced = 0;

            // 收集所有涉及的大预制体路径
            var allPrefabPaths = _analyzeResult.Groups
                .SelectMany(g => g.Nodes.Select(n => n.PrefabAssetPath))
                .Distinct()
                .ToList();

            // 备份所有大预制体
            if (undoRecord != null)
            {
                foreach (var assetPath in allPrefabPaths)
                {
                    string fileName = Path.GetFileName(assetPath);
                    string backupPath = $"{undoRecord.BackupFolder}/{fileName}";
                    backupPath = AssetDatabase.GenerateUniqueAssetPath(backupPath);
                    if (AssetDatabase.CopyAsset(assetPath, backupPath))
                    {
                        undoRecord.ModifiedPrefabs.Add(new BackupEntry
                        {
                            OriginalPath = assetPath,
                            BackupPath = backupPath
                        });
                        Log($"  [备份] {assetPath} -> {backupPath}");
                    }
                    else
                    {
                        Log($"  [警告] 备份失败: {assetPath}");
                    }
                }
            }

            if (!_dryRun) EnsureFolder(_outputRootFolder);

            // group -> 已创建的子预制体资产(用于第二阶段替换)
            var groupToAsset = new Dictionary<ExtractGroup, GameObject>();
            // group -> 用于创建子预制体的"模板大预制体路径"(该路径下的第一个节点会被消耗)
            var groupToTemplatePrefab = new Dictionary<ExtractGroup, string>();

            try
            {
                // ========== 第一阶段:为每个 group 创建共享子预制体 ==========
                // 把 group 按"第一个 NodeRef 所在的大预制体"聚合,以便一次性打开/保存
                var firstNodePrefabToGroups = new Dictionary<string, List<ExtractGroup>>();
                foreach (var g in _analyzeResult.Groups)
                {
                    string firstPrefab = g.Nodes[0].PrefabAssetPath;
                    if (!firstNodePrefabToGroups.TryGetValue(firstPrefab, out var list))
                    {
                        list = new List<ExtractGroup>();
                        firstNodePrefabToGroups[firstPrefab] = list;
                    }
                    list.Add(g);
                    groupToTemplatePrefab[g] = firstPrefab;
                }

                int phase1Idx = 0;
                int phase1Total = firstNodePrefabToGroups.Count;
                foreach (var kv in firstNodePrefabToGroups)
                {
                    phase1Idx++;
                    string assetPath = kv.Key;
                    var groups = kv.Value;

                    if (!_dryRun)
                        EditorUtility.DisplayProgressBar("执行中(阶段 1/2:创建共享子预制体)",
                            $"{Path.GetFileName(assetPath)} ({phase1Idx}/{phase1Total})",
                            (float)phase1Idx / phase1Total);

                    Log($"\n[阶段1] 从 {assetPath} 中提取 {groups.Count} 个共享子预制体");

                    if (_dryRun)
                    {
                        foreach (var g in groups)
                        {
                            string subPrefabPath = $"{_outputRootFolder}/{SanitizeName(g.RepresentativeName)}.prefab";
                            Log($"  [试运行] 将创建子预制体: {subPrefabPath}(基于模板节点 {g.Nodes[0].HierarchyPath})");
                            totalCreated++;
                        }
                        continue;
                    }

                    // 真实执行:加载 contents,把每个 group 的模板节点提取为干净的子预制体,然后替换回原位置
                    GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                    try
                    {
                        foreach (var g in groups)
                        {
                            var firstNode = FindByPath(contents.transform, g.Nodes[0].HierarchyPath);
                            if (firstNode == null)
                            {
                                Log($"  [警告] 找不到模板节点 {g.Nodes[0].HierarchyPath},跳过组 {g.RepresentativeName}");
                                continue;
                            }

                            string subPrefabPath = $"{_outputRootFolder}/{SanitizeName(g.RepresentativeName)}.prefab";
                            subPrefabPath = GetUniqueAssetPath(subPrefabPath);

                            // === 关键:用干净的临时副本生成子预制体,而不是直接用原节点 ===
                            // 1) 克隆一份模板节点(脱离原父级,放到一个临时根下)
                            var tempRoot = new GameObject("__PrefabExtractor_TempRoot");
                            var clone = Object.Instantiate(firstNode.gameObject, tempRoot.transform);
                            clone.name = firstNode.name;
                            // 2) 清空根节点的 Transform、Layer、Static,让子预制体本体保持干净
                            clone.transform.localPosition = Vector3.zero;
                            clone.transform.localRotation = Quaternion.identity;
                            clone.transform.localScale = Vector3.one;
                            clone.layer = 0; // Default
                            GameObjectUtility.SetStaticEditorFlags(clone, 0);
                            // 2.5) 重置子树里所有 Renderer 的 Lighting / Probes / Additional Settings 为 Unity 默认值
                            ResetRendererSettingsToDefaults(clone.transform);
                            // 3) 保存为预制体资产(不连接,因为它是临时的)
                            var subPrefabAsset = PrefabUtility.SaveAsPrefabAsset(clone, subPrefabPath);
                            // 4) 销毁临时副本
                            Object.DestroyImmediate(tempRoot);

                            if (subPrefabAsset == null)
                            {
                                Log($"  [错误] 保存子预制体失败: {subPrefabPath}");
                                continue;
                            }

                            // 5) 现在把原模板节点也替换成新子预制体的实例(保留它原来的 Transform 和材质差异)
                            ReplaceWithPrefabInstance(firstNode, subPrefabAsset, null);

                            groupToAsset[g] = subPrefabAsset;
                            if (undoRecord != null) undoRecord.CreatedSubPrefabs.Add(subPrefabPath);
                            Log($"  [创建] {subPrefabPath}");
                            totalCreated++;
                        }

                        // 保存该大预制体
                        PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                        Log($"  [保存] {assetPath}");
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                }

                // ========== 第二阶段:在所有大预制体里替换剩余节点 ==========
                // 把 NodeRef 按大预制体分组(只处理非模板节点,即每个 group 的 Nodes[1..])
                var prefabToReplacements = new Dictionary<string, List<(ExtractGroup group, NodeRef node)>>();
                foreach (var g in _analyzeResult.Groups)
                {
                    for (int i = 1; i < g.Nodes.Count; i++)
                    {
                        var n = g.Nodes[i];
                        if (!prefabToReplacements.TryGetValue(n.PrefabAssetPath, out var list))
                        {
                            list = new List<(ExtractGroup, NodeRef)>();
                            prefabToReplacements[n.PrefabAssetPath] = list;
                        }
                        list.Add((g, n));
                    }
                }

                int phase2Idx = 0;
                int phase2Total = prefabToReplacements.Count;
                foreach (var kv in prefabToReplacements)
                {
                    phase2Idx++;
                    string assetPath = kv.Key;
                    var replacements = kv.Value;

                    if (!_dryRun)
                        EditorUtility.DisplayProgressBar("执行中(阶段 2/2:替换节点)",
                            $"{Path.GetFileName(assetPath)} ({phase2Idx}/{phase2Total})",
                            (float)phase2Idx / phase2Total);

                    Log($"\n[阶段2] 在 {assetPath} 中替换 {replacements.Count} 个节点");

                    if (_dryRun)
                    {
                        foreach (var r in replacements)
                            Log($"  [试运行] 替换 {r.node.HierarchyPath} -> {r.group.RepresentativeName}");
                        totalReplaced += replacements.Count;
                        continue;
                    }

                    GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                    try
                    {
                        // 替换可能会改变 sibling index,所以按深度从深到浅、同层从大 sibling 到小 sibling 处理更稳
                        // 简单做法:每次替换前重新按路径查找(InstantiatePrefab + DestroyImmediate 是同位置替换,影响很小)
                        // 但仍然按层级深度倒序排,避免父节点先被替换导致子节点路径失效
                        var sorted = replacements
                            .OrderByDescending(r => r.node.HierarchyPath.Count(c => c == '/'))
                            .ToList();

                        foreach (var r in sorted)
                        {
                            var node = FindByPath(contents.transform, r.node.HierarchyPath);
                            if (node == null)
                            {
                                Log($"  [警告] 找不到路径 {r.node.HierarchyPath},跳过");
                                continue;
                            }
                            if (!groupToAsset.TryGetValue(r.group, out var subAsset) || subAsset == null)
                            {
                                Log($"  [警告] 组 {r.group.RepresentativeName} 的子预制体未创建,跳过");
                                continue;
                            }
                            ReplaceWithPrefabInstance(node, subAsset, null);
                            totalReplaced++;
                        }

                        PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
                        Log($"  [保存] {assetPath}");
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!_dryRun)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                if (undoRecord != null && (undoRecord.ModifiedPrefabs.Count > 0 || undoRecord.CreatedSubPrefabs.Count > 0))
                {
                    SaveUndoRecord(undoRecord);
                    Log($"\n[撤销记录已保存] 如需回滚,点击【撤销上次执行】");
                }
            }

            Log($"\n=== 完成 ===  创建子预制体: {totalCreated},替换实例: {totalReplaced}");
        }

        // ===================== 撤销 =====================

        private void UndoLastExecute()
        {
            var record = LoadUndoRecord();
            if (record == null)
            {
                EditorUtility.DisplayDialog("提示", "没有可撤销的记录", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog(
                "⚠️ 确认撤销",
                $"将执行以下操作:\n" +
                $"• 从备份恢复 {record.ModifiedPrefabs.Count} 个大预制体\n" +
                $"• 删除生成的 {record.CreatedSubPrefabs.Count} 个子预制体\n" +
                $"• 删除备份目录 {record.BackupFolder}\n\n是否继续?",
                "继续", "取消"))
                return;

            Log("\n=== 开始撤销 ===");
            int restored = 0, deleted = 0;
            bool cancelled = false;

            try
            {
                // 0) 先检测生成的子预制体是否被外部引用,提醒用户
                if (_checkRefsOnUndo && record.CreatedSubPrefabs.Count > 0)
                {
                    EditorUtility.DisplayProgressBar("撤销中", "扫描外部引用...", 0f);
                    var externallyReferenced = FindExternalReferencesBatch(
                        record.CreatedSubPrefabs,
                        excludePaths: record.ModifiedPrefabs.Select(e => e.OriginalPath).ToList(),
                        out cancelled);
                    EditorUtility.ClearProgressBar();

                    if (cancelled)
                    {
                        Log("用户取消了引用扫描,撤销已中止");
                        return;
                    }

                    if (externallyReferenced.Count > 0)
                    {
                        var lines = externallyReferenced
                            .Select(kv => $"{kv.Key}\n    被引用于: " + string.Join(", ", kv.Value.Take(3)) + (kv.Value.Count > 3 ? $" (+{kv.Value.Count - 3} 更多)" : ""))
                            .ToList();
                        string msg = "检测到这些子预制体已被外部资产引用,撤销后这些引用会丢失:\n\n"
                                     + string.Join("\n\n", lines)
                                     + "\n\n仍要继续撤销吗?";
                        if (!EditorUtility.DisplayDialog("⚠️ 引用警告", msg, "仍然撤销", "取消"))
                        {
                            Log("用户取消撤销");
                            return;
                        }
                        foreach (var kv in externallyReferenced)
                            Log($"  [警告] {kv.Key} 被 {kv.Value.Count} 个资产引用");
                    }
                }

                // 1) 恢复大预制体:直接二进制写回,保留原 GUID(关键!)
                int idx = 0;
                foreach (var entry in record.ModifiedPrefabs)
                {
                    EditorUtility.DisplayProgressBar("撤销中", $"恢复大预制体 {idx + 1}/{record.ModifiedPrefabs.Count}",
                        (float)idx / Mathf.Max(1, record.ModifiedPrefabs.Count));
                    idx++;

                    if (!File.Exists(entry.BackupPath))
                    {
                        Log($"  [警告] 备份文件不存在: {entry.BackupPath}");
                        continue;
                    }
                    try
                    {
                        string srcAbs = ToAbsolutePath(entry.BackupPath);
                        string dstAbs = ToAbsolutePath(entry.OriginalPath);
                        if (!File.Exists(dstAbs))
                        {
                            Log($"  [警告] 原文件不存在,使用 CopyAsset 恢复(引用可能丢失): {entry.OriginalPath}");
                            AssetDatabase.CopyAsset(entry.BackupPath, entry.OriginalPath);
                        }
                        else
                        {
                            File.Copy(srcAbs, dstAbs, overwrite: true);
                            AssetDatabase.ImportAsset(entry.OriginalPath, ImportAssetOptions.ForceUpdate);
                        }
                        Log($"  [恢复] {entry.OriginalPath} (GUID 保留)");
                        restored++;
                    }
                    catch (System.Exception e)
                    {
                        Log($"  [错误] 恢复失败 {entry.OriginalPath}: {e.Message}");
                    }
                }

                // 2) 删除生成的子预制体
                idx = 0;
                foreach (var path in record.CreatedSubPrefabs)
                {
                    EditorUtility.DisplayProgressBar("撤销中", $"删除子预制体 {idx + 1}/{record.CreatedSubPrefabs.Count}",
                        (float)idx / Mathf.Max(1, record.CreatedSubPrefabs.Count));
                    idx++;

                    if (AssetDatabase.DeleteAsset(path))
                    {
                        Log($"  [删除] {path}");
                        deleted++;
                    }
                }

                // 3) 清理备份目录及空的输出子目录
                if (AssetDatabase.IsValidFolder(record.BackupFolder))
                {
                    AssetDatabase.DeleteAsset(record.BackupFolder);
                    Log($"  [清理] 备份目录 {record.BackupFolder}");
                }
                CleanupEmptyOutputFolders(record);

                // 4) 清除撤销记录
                ClearUndoRecord();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Log($"\n=== 撤销完成 === 恢复 {restored} 个大预制体,删除 {deleted} 个子预制体");
            EditorUtility.DisplayDialog("撤销完成", $"恢复 {restored} 个大预制体,删除 {deleted} 个子预制体", "OK");
        }

        // 转换 Assets/xxx 路径到绝对路径
        private string ToAbsolutePath(string assetPath)
        {
            // Application.dataPath 是 .../Assets
            string projectRoot = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            return projectRoot + assetPath;
        }

        // 批量查找外部引用:一次遍历所有项目资产,同时匹配所有 GUID
        // 返回:子预制体路径 -> 引用它的资产路径列表
        private Dictionary<string, List<string>> FindExternalReferencesBatch(
            List<string> subPrefabPaths,
            List<string> excludePaths,
            out bool cancelled)
        {
            cancelled = false;
            var result = new Dictionary<string, List<string>>();

            // 1) 构建 GUID -> 子预制体路径 的映射,同时建立 GUID 集合用于快速判断
            var guidToSubPath = new Dictionary<string, string>();
            foreach (var p in subPrefabPaths)
            {
                string guid = AssetDatabase.AssetPathToGUID(p);
                if (!string.IsNullOrEmpty(guid)) guidToSubPath[guid] = p;
            }
            if (guidToSubPath.Count == 0) return result;

            var excludeSet = new HashSet<string>(excludePaths ?? new List<string>(), System.StringComparer.OrdinalIgnoreCase);
            // 排除子预制体自身、备份目录
            foreach (var p in subPrefabPaths) excludeSet.Add(p);

            // 2) 用 AssetDatabase 拿到所有相关类型的资产路径(比 Directory.GetFiles 更准、更可控)
            //    t:Prefab t:Scene t:Material t:AnimatorController t:AnimationClip t:ScriptableObject
            string[] guids = AssetDatabase.FindAssets("t:Prefab t:Scene t:Material t:AnimatorController t:AnimationClip t:ScriptableObject");
            int total = guids.Length;

            for (int i = 0; i < total; i++)
            {
                // 每 50 个文件刷新一次进度条
                if (i % 50 == 0)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                        "撤销中",
                        $"扫描外部引用 {i}/{total}",
                        (float)i / Mathf.Max(1, total)))
                    {
                        cancelled = true;
                        return result;
                    }
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(assetPath)) continue;
                if (excludeSet.Contains(assetPath)) continue;
                // 跳过备份目录里的文件
                if (assetPath.Contains("/_Backup_")) continue;

                string absPath = ToAbsolutePath(assetPath);
                if (!File.Exists(absPath)) continue;

                string content;
                try { content = File.ReadAllText(absPath); }
                catch { continue; }

                // 一次内容里检查所有目标 GUID
                foreach (var kv in guidToSubPath)
                {
                    if (content.Contains(kv.Key))
                    {
                        if (!result.TryGetValue(kv.Value, out var list))
                        {
                            list = new List<string>();
                            result[kv.Value] = list;
                        }
                        list.Add(assetPath);
                    }
                }
            }

            return result;
        }

        // 删除撤销后可能变空的输出子目录(如 GeneratedSubPrefabs/<大预制体名>/)
        private void CleanupEmptyOutputFolders(UndoRecord record)
        {
            var folders = new HashSet<string>();
            foreach (var path in record.CreatedSubPrefabs)
            {
                string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir)) folders.Add(dir);
            }
            foreach (var dir in folders)
            {
                if (!AssetDatabase.IsValidFolder(dir)) continue;
                // 只在目录为空时删除
                var assets = AssetDatabase.FindAssets("", new[] { dir });
                if (assets == null || assets.Length == 0)
                {
                    AssetDatabase.DeleteAsset(dir);
                    Log($"  [清理] 空目录 {dir}");
                }
            }
        }

        private void SaveUndoRecord(UndoRecord record)
        {
            string json = JsonUtility.ToJson(record);
            EditorPrefs.SetString(UndoRecordKey, json);
        }

        private UndoRecord LoadUndoRecord()
        {
            string json = EditorPrefs.GetString(UndoRecordKey, "");
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var rec = JsonUtility.FromJson<UndoRecord>(json);
                // 校验:备份目录还在才算有效
                if (rec == null || string.IsNullOrEmpty(rec.BackupFolder)) return null;
                if (!AssetDatabase.IsValidFolder(rec.BackupFolder))
                {
                    ClearUndoRecord();
                    return null;
                }
                return rec;
            }
            catch
            {
                return null;
            }
        }

        private void ClearUndoRecord()
        {
            EditorPrefs.DeleteKey(UndoRecordKey);
        }

        // 用子预制体实例替换 oldNode,保留:
        //   - oldNode 原本的 Transform(localPosition / localRotation / localScale,经过规范化)
        //   - oldNode 的 layer 和 static flags
        //   - oldNode 子树上每个 Renderer 的:材质、Lighting / Probes / Additional Settings
        //   全部以 instance override 形式保留在大预制体上
        private void ReplaceWithPrefabInstance(Transform oldNode, GameObject prefabAsset, Transform templateNode)
        {
            // 1) 记录旧节点的 Transform / Layer / Static
            Vector3 localPos = oldNode.localPosition;
            Vector3 localEuler = NormalizeEuler(oldNode.localEulerAngles);
            Vector3 localScale = oldNode.localScale;
            int oldLayer = oldNode.gameObject.layer;
            StaticEditorFlags oldStatic = GameObjectUtility.GetStaticEditorFlags(oldNode.gameObject);
            Transform parent = oldNode.parent;
            int siblingIndex = oldNode.GetSiblingIndex();
            string oldName = oldNode.name;

            // 2) 收集旧节点子树所有 Renderer 的完整设置(材质 + Lighting/Probes/Additional)
            var rendererSnapshots = CollectRendererSnapshots(oldNode);

            // 3) 实例化子预制体到同一个父节点下
            var newInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, parent);
            newInstance.transform.SetSiblingIndex(siblingIndex);
            newInstance.transform.localPosition = localPos;
            newInstance.transform.localEulerAngles = localEuler;
            newInstance.transform.localScale = localScale;
            newInstance.name = oldName;
            // 仅在与子预制体本体的"干净值"不同时设置,让 override 系统正确记录差异
            if (oldLayer != 0) newInstance.layer = oldLayer;
            if (oldStatic != 0) GameObjectUtility.SetStaticEditorFlags(newInstance, oldStatic);

            // 4) 把 Renderer 完整设置应用回新实例(差异自动作为 override 保留)
            ApplyRendererSnapshots(newInstance.transform, rendererSnapshots);

            // 5) 销毁旧节点
            Object.DestroyImmediate(oldNode.gameObject);
        }

        // 规范化欧拉角:把每个分量约束到 (-180, 180],去掉 -360/720 这种等价的脏值
        // 同时把接近 0 的极小浮点误差吸到 0
        private static Vector3 NormalizeEuler(Vector3 e)
        {
            return new Vector3(
                NormalizeAngle(e.x),
                NormalizeAngle(e.y),
                NormalizeAngle(e.z));
        }

        private static float NormalizeAngle(float a)
        {
            // 收到 (-180, 180]
            a = a % 360f;
            if (a > 180f) a -= 360f;
            if (a <= -180f) a += 360f;
            // 浮点误差吸到 0
            if (Mathf.Abs(a) < 1e-4f) a = 0f;
            return a;
        }

        // 收集 Renderer 上需要保留为 override 的所有属性(材质 + Lighting/Probes/AdditionalSettings)
        private class RendererSnapshot
        {
            public Material[] SharedMaterials;
            // Lighting
            public ShadowCastingMode ShadowCastingMode;
            public bool ReceiveShadows;
            public bool ContributeGlobalIllumination;        // = StaticEditorFlags.ContributeGI(在 GameObject 上)
            public bool HasReceiveGI;                         // 仅 MeshRenderer 有 receiveGI 属性
            public ReceiveGI ReceiveGI;
            // Probes
            public LightProbeUsage LightProbeUsage;
            public ReflectionProbeUsage ReflectionProbeUsage;
            public Transform ProbeAnchor;
            // Additional Settings
            public MotionVectorGenerationMode MotionVectors;
            public bool AllowOcclusionWhenDynamic;
            public bool StaticShadowCaster;
            public uint RenderingLayerMask;
            public int RendererPriority;
            // 关联(可能有的):SkinnedMeshRenderer 特有
            public bool IsSkinned;
            public bool SkinnedMotionVectors;
            public bool UpdateWhenOffscreen;
        }

        private Dictionary<string, RendererSnapshot> CollectRendererSnapshots(Transform root)
        {
            var dict = new Dictionary<string, RendererSnapshot>();
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;

                var snap = new RendererSnapshot
                {
                    SharedMaterials = r.sharedMaterials != null ? (Material[])r.sharedMaterials.Clone() : null,
                    ShadowCastingMode = r.shadowCastingMode,
                    ReceiveShadows = r.receiveShadows,
                    LightProbeUsage = r.lightProbeUsage,
                    ReflectionProbeUsage = r.reflectionProbeUsage,
                    ProbeAnchor = r.probeAnchor,
                    MotionVectors = r.motionVectorGenerationMode,
                    AllowOcclusionWhenDynamic = r.allowOcclusionWhenDynamic,
                    StaticShadowCaster = r.staticShadowCaster,
                    RenderingLayerMask = r.renderingLayerMask,
                    RendererPriority = r.rendererPriority,
                };
                // receiveGI 是 MeshRenderer 的属性,不在 Renderer 基类上
                if (r is MeshRenderer mr)
                {
                    snap.HasReceiveGI = true;
                    snap.ReceiveGI = mr.receiveGI;
                }
                // ContributeGI 是 GameObject 的 StaticEditorFlags 的一位,不是 Renderer 的属性
                var flags = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                snap.ContributeGlobalIllumination = (flags & StaticEditorFlags.ContributeGI) != 0;

                if (r is SkinnedMeshRenderer smr)
                {
                    snap.IsSkinned = true;
                    snap.SkinnedMotionVectors = smr.skinnedMotionVectors;
                    snap.UpdateWhenOffscreen = smr.updateWhenOffscreen;
                }

                dict[GetRelativePath(root, t)] = snap;
            }
            return dict;
        }

        private void ApplyRendererSnapshots(Transform root, Dictionary<string, RendererSnapshot> map)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string rel = GetRelativePath(root, t);
                if (!map.TryGetValue(rel, out var snap)) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;

                // 材质(长度对齐,以新实例为准)
                if (snap.SharedMaterials != null)
                {
                    var current = r.sharedMaterials;
                    int n = Mathf.Min(current.Length, snap.SharedMaterials.Length);
                    var result = (Material[])current.Clone();
                    for (int i = 0; i < n; i++)
                        if (snap.SharedMaterials[i] != null) result[i] = snap.SharedMaterials[i];
                    r.sharedMaterials = result;
                }

                // Lighting / Probes / Additional Settings
                r.shadowCastingMode = snap.ShadowCastingMode;
                r.receiveShadows = snap.ReceiveShadows;
                r.lightProbeUsage = snap.LightProbeUsage;
                r.reflectionProbeUsage = snap.ReflectionProbeUsage;
                r.probeAnchor = snap.ProbeAnchor;
                r.motionVectorGenerationMode = snap.MotionVectors;
                r.allowOcclusionWhenDynamic = snap.AllowOcclusionWhenDynamic;
                r.staticShadowCaster = snap.StaticShadowCaster;
                r.renderingLayerMask = snap.RenderingLayerMask;
                r.rendererPriority = snap.RendererPriority;

                // receiveGI 仅 MeshRenderer 有
                if (snap.HasReceiveGI && r is MeshRenderer mr)
                {
                    mr.receiveGI = snap.ReceiveGI;
                }

                // ContributeGI:更新 GameObject 的 StaticEditorFlags
                var flags = GameObjectUtility.GetStaticEditorFlags(t.gameObject);
                if (snap.ContributeGlobalIllumination)
                    flags |= StaticEditorFlags.ContributeGI;
                else
                    flags &= ~StaticEditorFlags.ContributeGI;
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);

                if (snap.IsSkinned && r is SkinnedMeshRenderer smr)
                {
                    smr.skinnedMotionVectors = snap.SkinnedMotionVectors;
                    smr.updateWhenOffscreen = snap.UpdateWhenOffscreen;
                }
            }
        }

        // 清理临时副本上的 Renderer 设置到默认值,让子预制体本体保持干净
        private void ResetRendererSettingsToDefaults(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                // Unity 默认值
                r.shadowCastingMode = ShadowCastingMode.On;
                r.receiveShadows = true;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
                r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
                r.probeAnchor = null;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                r.allowOcclusionWhenDynamic = true;
                r.staticShadowCaster = false;
                r.renderingLayerMask = 1;
                r.rendererPriority = 0;

                if (r is MeshRenderer mr)
                {
                    mr.receiveGI = ReceiveGI.LightProbes;
                }

                if (r is SkinnedMeshRenderer smr)
                {
                    smr.skinnedMotionVectors = true;
                    smr.updateWhenOffscreen = false;
                }
            }
            // 清子树上所有 GameObject 的 StaticEditorFlags(其中包含 ContributeGI 位)
            var allGOs = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in allGOs)
            {
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);
            }
        }

        // 保留旧的材质收集/应用接口,但已不再使用,以避免万一被引用时报错
        private Dictionary<string, Material[]> CollectMaterialsByPath(Transform root)
        {
            var dict = new Dictionary<string, Material[]>();
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string rel = GetRelativePath(root, t);
                var r = t.GetComponent<Renderer>();
                if (r != null && r.sharedMaterials != null)
                    dict[rel] = (Material[])r.sharedMaterials.Clone();
            }
            return dict;
        }

        private void ApplyMaterialsByPath(Transform root, Dictionary<string, Material[]> map)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string rel = GetRelativePath(root, t);
                if (!map.TryGetValue(rel, out var mats)) continue;
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                var current = r.sharedMaterials;
                int n = Mathf.Min(current.Length, mats.Length);
                var result = (Material[])current.Clone();
                for (int i = 0; i < n; i++)
                    if (mats[i] != null) result[i] = mats[i];
                r.sharedMaterials = result;
            }
        }

        private string GetRelativePath(Transform root, Transform t)
        {
            if (t == root) return "";
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        // 根据 ComputeHierarchyPath 生成的路径(name#siblingIndex/...)定位节点
        private Transform FindByPath(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root;
            var segs = path.Split('/');
            Transform cur = root;
            foreach (var seg in segs)
            {
                int hashIdx = seg.LastIndexOf('#');
                string name = hashIdx >= 0 ? seg.Substring(0, hashIdx) : seg;
                int sibIdx = -1;
                if (hashIdx >= 0) int.TryParse(seg.Substring(hashIdx + 1), out sibIdx);

                Transform next = null;
                if (sibIdx >= 0 && sibIdx < cur.childCount)
                {
                    var c = cur.GetChild(sibIdx);
                    if (c.name == name) next = c;
                }
                // fallback:按名字找
                if (next == null)
                {
                    for (int i = 0; i < cur.childCount; i++)
                    {
                        if (cur.GetChild(i).name == name) { next = cur.GetChild(i); break; }
                    }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        private void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            var parts = assetFolder.Split('/');
            string cur = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        private string GetUniqueAssetPath(string path)
        {
            return AssetDatabase.GenerateUniqueAssetPath(path);
        }

        private string SanitizeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }

        private void Log(string msg)
        {
            _log.AppendLine(msg);
            Debug.Log("[PrefabExtractor] " + msg);
            Repaint();
        }

        // ===================== 数据结构 =====================

        // 全局分析结果(所有大预制体合并)
        private class GlobalAnalyzeResult
        {
            public int TotalCandidateCount;
            public List<ExtractGroup> Groups = new List<ExtractGroup>();
        }

        private class ExtractGroup
        {
            public string Signature;
            public string RepresentativeName;   // 子预制体的命名(来自 Mesh 资产名)
            public string SignatureSummary;     // 人类可读的签名摘要(调试用)
            public List<NodeRef> Nodes = new List<NodeRef>();
        }

        private class NodeRef
        {
            public string PrefabAssetPath;  // 所属大预制体的 assetPath
            public string HierarchyPath;    // 在该大预制体内的层级路径
        }

        // 注意:JsonUtility 要求可序列化类有 [System.Serializable] 且为顶层或非 nested 私有
        [System.Serializable]
        public class UndoRecord
        {
            public string Timestamp;
            public string BackupFolder;
            public List<BackupEntry> ModifiedPrefabs = new List<BackupEntry>();
            public List<string> CreatedSubPrefabs = new List<string>();
        }

        [System.Serializable]
        public class BackupEntry
        {
            public string OriginalPath;
            public string BackupPath;
        }
    }
}
