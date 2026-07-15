// Outline Tool — 外描边工具，平滑法线写入 UV2，配合 Custom/ToonOutline Shader 使用
// Author: ZXX
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

/// <summary>
/// 外描边工具窗口。拖入 Prefab 或场景物体，Bake 生成 _baked.mesh，不动原始 fbx。
/// </summary>
public class OutlineToolWindow : EditorWindow
{
    private GameObject _target;
    private GameObject _lastTarget;
    private Material   _mat;
    private string     _outputPath = "";

    public static void Open()
    {
        var w = GetWindow<OutlineToolWindow>("Outline");
        w.minSize = new Vector2(400, 180);
        w.Show();
    }

    private static GUIContent Content(string label, string tooltip) => new GUIContent(label, tooltip);

    private void OnGUI()
    {
        _target = (GameObject)EditorGUILayout.ObjectField(
            Content("Target", "拖入 Prefab 或场景物体。"), _target, typeof(GameObject), true);
        _mat    = (Material)  EditorGUILayout.ObjectField(
            Content("Material", "用于外描边的材质球，需使用 Custom/ToonOutline Shader。"), _mat, typeof(Material), false);

        // 切换 Target 时自动填入目标目录
        if (_target != _lastTarget)
        {
            _lastTarget = _target;
            if (_target != null)
                _outputPath = GetAssetFolder(_target);
        }

        // Output Path
        EditorGUILayout.BeginHorizontal();
        _outputPath = EditorGUILayout.TextField(
            Content("Output Path", "输出目录：烘焙后的 _baked.mesh 文件存放位置。留空则输出到各源文件所在目录。"), _outputPath);
        if (GUILayout.Button(Content("...", "选择输出目录，必须在当前项目 Assets 内。"), GUILayout.Width(28)))
        {
            string defaultFolder = Application.dataPath;
            if (!string.IsNullOrEmpty(_outputPath))
            {
                string abs = Path.GetFullPath(Path.Combine(Application.dataPath, "..", _outputPath));
                if (Directory.Exists(abs))
                    defaultFolder = abs;
            }
            string selected = EditorUtility.OpenFolderPanel("选择输出目录", defaultFolder, "");
            if (!string.IsNullOrEmpty(selected))
            {
                string dataPath = Application.dataPath.Replace("\\", "/");
                if (selected.Replace("\\", "/").StartsWith(dataPath))
                    _outputPath = "Assets" + selected.Replace("\\", "/").Substring(dataPath.Length);
                else
                    Debug.LogWarning("[Outline] 请选择 Assets 内的目录");
            }
        }
        EditorGUILayout.EndHorizontal();
        if (string.IsNullOrEmpty(_outputPath))
            EditorGUILayout.HelpBox("路径为空时输出到各源文件所在目录", MessageType.None);

        if (_target == null)
        {
            EditorGUILayout.HelpBox("拖入 Prefab 或场景物体。", MessageType.Info);
            return;
        }

        var renderers = CollectRenderers(_target);
        if (renderers.Count == 0)
        {
            EditorGUILayout.HelpBox("未找到 Renderer。", MessageType.Warning);
            return;
        }

        var meshes   = renderers.Select(GetSharedMesh).Where(m => m != null).Distinct().ToList();
        bool allDone = meshes.All(IsBaked);

        EditorGUILayout.LabelField(
            $"{renderers.Count} Renderers / {meshes.Count} Meshes   {(allDone ? "OK" : "需要烘焙")}",
            EditorStyles.miniLabel);

        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button(Content("Bake", "复制 Mesh，平滑法线写入 UV2，存为 XXX_baked.mesh。重复 Bake 会覆盖旧文件，原始 fbx 不会被修改。"), GUILayout.Height(30)))         BakeAll();
        if (GUILayout.Button(Content("Add Material", "为所有 Renderer 追加 Outline 材质。若未指定 Material 则自动创建。"), GUILayout.Height(30))) AddMaterial();
        if (GUILayout.Button(Content("Clear", "还原所有 Renderer 的 Mesh 为原始 fbx Mesh，删除已生成的 _baked.mesh 文件。"), GUILayout.Height(30)))        ClearBake();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.HelpBox(
            "Bake: 复制 Mesh，平滑法线写入 UV2，存为 XXX_baked.mesh\n"
            + "Shader 读 UV2 获取平滑法线方向。Tangents 保持 MikkTSpace 不动。\n"
            + "重复 Bake 会覆盖旧文件，原始 fbx 不会被修改。",
            MessageType.None);

        GUILayout.FlexibleSpace();
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal    = { textColor = new Color(0.45f, 0.45f, 0.45f, 0.5f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56",      authorStyle);
    }

    private void BakeAll()
    {
        var renderers = CollectRenderers(_target);
        if (renderers.Count == 0) { Debug.LogError("[Outline] No renderer found."); return; }

        Undo.SetCurrentGroupName("Bake Outline Normals");
        int undoGroup = Undo.GetCurrentGroup();

        var sources = new Dictionary<Mesh, (string bakedName, string dir)>();
        foreach (var r in renderers)
        {
            var mesh = GetSharedMesh(r);
            if (mesh == null || sources.ContainsKey(mesh)) continue;

            string assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath)) continue;

            string sourceDir = Path.GetDirectoryName(assetPath).Replace("\\", "/");
            string dir = ResolveOutputDir(sourceDir);
            bool isRebake   = assetPath.EndsWith(".mesh") && mesh.name.EndsWith("_baked");
            string bakedName = isRebake ? mesh.name : mesh.name + "_baked";

            if (!isRebake) EnsureReadable(assetPath);
            sources[mesh] = (bakedName, dir);
        }

        var baked = new Dictionary<Mesh, Mesh>();
        foreach (var kv in sources)
        {
            var result = BakeMesh(kv.Key, kv.Value.bakedName, kv.Value.dir);
            if (result == null) continue;
            Undo.RegisterCreatedObjectUndo(result, "");
            baked[kv.Key] = result;
        }

        int count = 0;
        foreach (var r in renderers)
        {
            var mesh = GetSharedMesh(r);
            if (mesh == null || !baked.TryGetValue(mesh, out var newMesh)) continue;

            Undo.RecordObject(r, "");
            if (r is SkinnedMeshRenderer smr)
                smr.sharedMesh = newMesh;
            else
                r.GetComponent<MeshFilter>().sharedMesh = newMesh;
            EditorUtility.SetDirty(r);
            count++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Outline] {_target.name}: {baked.Count} meshes baked, {count} renderers assigned");
    }

    static Mesh BakeMesh(Mesh src, string bakedName, string dir)
    {
        var m = Object.Instantiate(src);
        m.name = bakedName;

        var verts = m.vertices;
        var norms = m.normals;
        var tris  = m.triangles;

        // 按面法线累积每个顶点的平滑法线
        var acc = new Dictionary<Vector3Int, Vector3>();
        for (int i = 0; i < tris.Length; i += 3)
        {
            int a = tris[i], b = tris[i + 1], c = tris[i + 2];
            var fn = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).normalized;
            AddNormal(acc, verts[a], fn);
            AddNormal(acc, verts[b], fn);
            AddNormal(acc, verts[c], fn);
        }

        // 平滑法线写入 UV2，tangents 不动，法线贴图不受影响
        var uvData = new List<Vector4>(verts.Length);
        for (int i = 0; i < verts.Length; i++)
        {
            var k = Key(verts[i]);
            var smoothN = acc.TryGetValue(k, out var v) ? v.normalized : norms[i];
            uvData.Add(new Vector4(smoothN.x, smoothN.y, smoothN.z, 0f));
        }
        m.SetUVs(2, uvData);

        string newPath = Path.Combine(dir, bakedName + ".mesh").Replace("\\", "/");
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(newPath);

        if (existing != null)
        {
            EditorUtility.CopySerialized(m, existing);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(m);
            return existing;
        }

        AssetDatabase.CreateAsset(m, newPath);
        return m;
    }

    private void AddMaterial()
    {
        var renderers = CollectRenderers(_target);
        if (renderers.Count == 0) { Debug.LogError("[Outline] No renderer found."); return; }

        var mat = _mat ?? CreateOutlineMaterial();
        if (mat == null) return;

        int n = 0;
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials.ToList();
            if (mats.Contains(mat)) continue;

            Undo.RecordObject(r, "Add Outline Material");
            mats.Add(mat);
            r.sharedMaterials = mats.ToArray();
            EditorUtility.SetDirty(r);
            n++;
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Outline] {_target.name}: material added to {n} renderers");
    }

    Material CreateOutlineMaterial()
    {
        var shader = Shader.Find("Custom/ToonOutline");
        if (shader == null) { Debug.LogError("[Outline] Shader not found."); return null; }

        string folder = string.IsNullOrEmpty(_outputPath)
            ? GetAssetFolder(_target)
            : _outputPath;
        var mat = new Material(shader)
        {
            name              = $"M_{_target.name}_Outline",
            enableInstancing  = true
        };
        mat.SetFloat("_Smooth", 1);
        mat.EnableKeyword("_SMOOTH_ON");

        string mp = AssetDatabase.GenerateUniqueAssetPath($"{folder}/M_{_target.name}_Outline.mat");
        AssetDatabase.CreateAsset(mat, mp);
        AssetDatabase.SaveAssets();
        EditorGUIUtility.PingObject(mat);
        return mat;
    }

    private void ClearBake()
    {
        var renderers = CollectRenderers(_target);
        var toDelete = new HashSet<string>();
        int reverted = 0;

        foreach (var r in renderers)
        {
            var cur = GetSharedMesh(r);
            if (cur == null) continue;
            string curPath = AssetDatabase.GetAssetPath(cur);
            if (string.IsNullOrEmpty(curPath)) continue;
            if (!curPath.EndsWith(".mesh") || !cur.name.EndsWith("_baked")) continue;

            var original = FindOriginalMesh(cur, curPath);
            if (original == null)
            {
                Debug.LogWarning($"[Outline] Cannot find original for {cur.name}, skip");
                continue;
            }

            Undo.RecordObject(r, "Clear Baked Mesh");
            if (r is SkinnedMeshRenderer smr)
                smr.sharedMesh = original;
            else
                r.GetComponent<MeshFilter>().sharedMesh = original;
            EditorUtility.SetDirty(r);

            toDelete.Add(curPath);
            reverted++;
        }

        foreach (var p in toDelete)
            AssetDatabase.DeleteAsset(p);

        // 原始 mesh 没有 UV2，关掉 _SMOOTH_ON 避免 shader 读脏数据
        foreach (var r in renderers)
        {
            var mats = r.sharedMaterials;
            foreach (var mat in mats)
            {
                if (mat != null && mat.shader != null && mat.shader.name == "Custom/ToonOutline")
                    mat.DisableKeyword("_SMOOTH_ON");
            }
        }

        // 重导源 FBX，清掉 Library 里的 UV2 缓存
        var fbxs = new HashSet<string>();
        foreach (var r in renderers)
        {
            var m = GetSharedMesh(r);
            if (m == null) continue;
            string ap = AssetDatabase.GetAssetPath(m);
            if (!string.IsNullOrEmpty(ap) && ap.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                fbxs.Add(ap);
        }
        foreach (var fbx in fbxs)
            AssetDatabase.ImportAsset(fbx);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Outline] {_target.name}: {reverted} renderers reverted, {toDelete.Count} files deleted");
    }

    static Mesh FindOriginalMesh(Mesh baked, string bakedPath)
    {
        string dir = Path.GetDirectoryName(bakedPath).Replace("\\", "/");
        string expectedName = baked.name.EndsWith("_baked")
            ? baked.name.Substring(0, baked.name.Length - "_baked".Length)
            : null;

        int vc = baked.vertexCount;
        int tc = baked.triangles.Length;

        var fbxs = FindFbxInDir(dir).ToList();
        Debug.Log($"[Outline] FindOriginal: dir={dir}, expectedName={expectedName}, vc={vc}, tc={tc}, fbxCount={fbxs.Count}");

        // 先按名称匹配
        foreach (var fbx in fbxs)
        {
            var subMeshes = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().ToList();
            Debug.Log($"[Outline]   FBX: {Path.GetFileName(fbx)}, meshes: {string.Join(", ", subMeshes.Select(m => $"{m.name}(vc={m.vertexCount},tc={m.triangles.Length})"))}");

            foreach (var m in subMeshes)
            {
                if (m.name == expectedName)
                {
                    Debug.Log($"[Outline]   -> name match: {m.name}");
                    return m;
                }
            }
        }

        // 回退：顶点数 + 三角面数
        foreach (var fbx in fbxs)
        {
            foreach (var m in AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>())
            {
                if (!m.name.EndsWith("_baked") && m.vertexCount == vc && m.triangles.Length == tc)
                {
                    Debug.Log($"[Outline]   -> vc+tc match: {m.name}");
                    return m;
                }
            }
        }

        Debug.LogWarning($"[Outline] FindOriginal: no match found");
        return null;
    }

    static IEnumerable<string> FindFbxInDir(string dir)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { dir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                yield return path;
        }
    }

    static List<Renderer> CollectRenderers(GameObject root)
    {
        var list = new List<Renderer>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            if (mf.TryGetComponent<Renderer>(out var r)) list.Add(r);
        list.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        return list;
    }

    static Mesh GetSharedMesh(Renderer r) =>
        r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;

    static bool IsBaked(Mesh m)
    {
        if (m == null) return false;
        if (m.name.EndsWith("_baked")) return true;
        // UV2 通道有数据就算 baked
        var uv = new List<Vector4>();
        m.GetUVs(2, uv);
        if (uv.Count == 0) return false;
        for (int i = 0; i < Mathf.Min(10, uv.Count); i++)
            if (uv[i].x != 0 || uv[i].y != 0 || uv[i].z != 0) return true;
        return false;
    }

    static void AddNormal(Dictionary<Vector3Int, Vector3> d, Vector3 pos, Vector3 n)
    {
        var k = Key(pos);
        d[k] = d.TryGetValue(k, out var v) ? v + n : n;
    }

    static void EnsureReadable(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null || importer.isReadable) return;
        importer.isReadable = true;
        importer.SaveAndReimport();
        Debug.Log($"[Outline] Enabled Read/Write: {Path.GetFileName(assetPath)}");
    }

    static Vector3Int Key(Vector3 v) => new(
        Mathf.RoundToInt(v.x * 10000f),
        Mathf.RoundToInt(v.y * 10000f),
        Mathf.RoundToInt(v.z * 10000f));

    string ResolveOutputDir(string sourceDir)
    {
        if (string.IsNullOrEmpty(_outputPath)) return sourceDir;
        if (!AssetDatabase.IsValidFolder(_outputPath))
        {
            Debug.LogWarning($"[Outline] 输出路径无效: {_outputPath}，回退到源文件目录");
            return sourceDir;
        }
        return _outputPath;
    }

    static string GetAssetFolder(GameObject go)
    {
        var pp = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
        if (!string.IsNullOrEmpty(pp)) return Path.GetDirectoryName(pp);
        if (Selection.activeObject != null)
        {
            var ap = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(ap))
                return File.Exists(ap) ? Path.GetDirectoryName(ap) : ap;
        }
        return "Assets";
    }
}
