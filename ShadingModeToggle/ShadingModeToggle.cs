using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 模型软硬边显示切换工具（逐模型独立调节平滑度）
/// 非破坏性预览：临时替换 Mesh 法线来切换软边/硬边显示。
/// Scene 和 Game 视图同步生效，不修改原始 Mesh 资产。
/// </summary>
public class ShadingModeToggleWindow : EditorWindow
{
    private class MeshRecord
    {
        public Component rendererComponent;
        public Mesh originalSharedMesh;
        public Mesh workingMesh;
        public Vector3[] hardNormals;
        public Vector3[] softNormals;
        public float smoothness = 1f; // 每个模型独立的平滑度
    }

    private static readonly List<MeshRecord> _records = new List<MeshRecord>();
    private Vector2 _scrollPos;

    // ── 入口 ──

    public static void Open()
    {
        var w = GetWindow<ShadingModeToggleWindow>("软硬边切换");
        w.minSize = new Vector2(400, 300);
        w.Show();
    }

    // ── Unity 生命周期 ──

    private void OnEnable()
    {
        CleanupDestroyedRecords();
    }

    private void OnGUI()
    {
        CleanupDestroyedRecords();

        // ── 标题 ──
        EditorGUILayout.LabelField("软硬边显示切换", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        EditorGUILayout.HelpBox(
            "非破坏性预览 — 不修改原始 Mesh 资产。Scene / Game 同步生效。",
            MessageType.Info);

        EditorGUILayout.Space(4);

        // ── 状态 ──
        DrawStatusBox();

        EditorGUILayout.Space(6);

        // ── 捕获按钮 ──
        var selected = GetSelectedMeshComponents();
        int newCount = selected.Count(r => !IsCaptured(r));
        bool hasNewSelection = newCount > 0;

        using (new EditorGUI.DisabledScope(!hasNewSelection))
        {
            string btnText = _records.Count == 0
                ? "📥  捕获选中模型"
                : $"📥  捕获选中模型（+{newCount} 个）";
            if (GUILayout.Button(btnText, GUILayout.Height(30)))
                CaptureSelection(selected);
        }

        EditorGUILayout.Space(6);

        // ── 统一调节 ──
        if (_records.Count > 0)
        {
            EditorGUILayout.LabelField("统一调节", EditorStyles.miniBoldLabel);

            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("硬", GUILayout.Width(28), GUILayout.Height(20)))
                    SetAllSmoothness(0f);
                if (GUILayout.Button("25%", GUILayout.Width(32), GUILayout.Height(20)))
                    SetAllSmoothness(0.25f);
                if (GUILayout.Button("50%", GUILayout.Width(32), GUILayout.Height(20)))
                    SetAllSmoothness(0.5f);
                if (GUILayout.Button("75%", GUILayout.Width(32), GUILayout.Height(20)))
                    SetAllSmoothness(0.75f);
                if (GUILayout.Button("软", GUILayout.Width(28), GUILayout.Height(20)))
                    SetAllSmoothness(1f);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("⟳  恢复全部", GUILayout.Height(20)))
                    RestoreAll();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);

            // ── 逐模型列表 ──
            EditorGUILayout.LabelField($"模型列表（{_records.Count} 个） — 各自拖动调节", EditorStyles.miniBoldLabel);
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos, GUILayout.ExpandHeight(true));

            for (int i = _records.Count - 1; i >= 0; i--)
            {
                var rec = _records[i];
                if (rec?.rendererComponent == null) continue;

                DrawRecordRow(rec);
            }

            EditorGUILayout.EndScrollView();
        }

        // ── 署名 ──
        EditorGUILayout.Space(4);
        var authorStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.35f, 0.35f, 0.35f, 1f) }
        };
        EditorGUILayout.LabelField("Developed by ZXX", authorStyle);
        EditorGUILayout.LabelField("114.21, 30.56", authorStyle);
    }

    private void DrawStatusBox()
    {
        EditorGUILayout.BeginVertical("box");
        {
            if (_records.Count > 0)
            {
                float avg = _records.Average(r => r.smoothness);
                EditorGUILayout.LabelField($"预览中: {_records.Count} 个模型  |  平均平滑度: {avg:P0}");
            }
            else
            {
                EditorGUILayout.LabelField("暂未捕获任何模型");
            }

            var selected = GetSelectedMeshComponents();
            int captured = selected.Count(IsCaptured);
            if (selected.Count > 0)
                EditorGUILayout.LabelField($"选中物体中的 Mesh: {selected.Count} 个（{captured} 个已捕获）");
            else
                EditorGUILayout.LabelField("未选中任何带 Mesh 的物体 — 请在 Hierarchy 中选中模型");
        }
        EditorGUILayout.EndVertical();
    }

    /// <summary>绘制单个模型的调节行</summary>
    private void DrawRecordRow(MeshRecord rec)
    {
        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.BeginHorizontal();

        // 物体名称（只读）
        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.ObjectField(rec.rendererComponent.gameObject, typeof(GameObject), true, GUILayout.Width(140));

        // 平滑度滑块
        EditorGUI.BeginChangeCheck();
        float newVal = EditorGUILayout.Slider(rec.smoothness, 0f, 1f);
        if (EditorGUI.EndChangeCheck())
        {
            rec.smoothness = newVal;
            ApplySmoothnessToRecord(rec);
        }

        // 数值
        EditorGUILayout.LabelField($"{rec.smoothness:F2}", GUILayout.Width(32));

        // 快设微调按钮
        if (GUILayout.Button("硬", GUILayout.Width(22), GUILayout.Height(16)))
        {
            rec.smoothness = 0f;
            ApplySmoothnessToRecord(rec);
        }
        if (GUILayout.Button("软", GUILayout.Width(22), GUILayout.Height(16)))
        {
            rec.smoothness = 1f;
            ApplySmoothnessToRecord(rec);
        }

        // 恢复
        if (GUILayout.Button("✕", GUILayout.Width(24), GUILayout.Height(18)))
            RestoreRecord(rec);

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndVertical();
    }

    // ══════════════════════════════════════════════════
    // 核心逻辑
    // ══════════════════════════════════════════════════

    private static List<Component> GetSelectedMeshComponents()
    {
        var list = new List<Component>();
        foreach (var go in Selection.gameObjects)
        {
            list.AddRange(go.GetComponentsInChildren<MeshFilter>());
            list.AddRange(go.GetComponentsInChildren<SkinnedMeshRenderer>());
        }
        return list;
    }

    private static bool IsCaptured(Component comp)
    {
        return _records.Any(r => r.rendererComponent == comp);
    }

    private static Mesh GetSharedMesh(Component comp)
    {
        if (comp is MeshFilter mf) return mf.sharedMesh;
        if (comp is SkinnedMeshRenderer smr) return smr.sharedMesh;
        return null;
    }

    private static void SetMeshToComponent(Component comp, Mesh mesh)
    {
        if (comp is MeshFilter mf)
            mf.mesh = mesh;
        else if (comp is SkinnedMeshRenderer smr)
            smr.sharedMesh = mesh;
    }

    private static void RestoreMeshToComponent(Component comp, Mesh originalShared)
    {
        if (comp is MeshFilter mf)
            mf.sharedMesh = originalShared;
        else if (comp is SkinnedMeshRenderer smr)
            smr.sharedMesh = originalShared;
    }

    /// <summary>捕获选中物体</summary>
    private static void CaptureSelection(List<Component> renderers)
    {
        int count = 0;

        foreach (var r in renderers)
        {
            if (r == null) continue;
            if (IsCaptured(r)) continue;

            var shared = GetSharedMesh(r);
            if (shared == null) continue;

            // 同 Mesh 资产复用已计算的软边法线
            var existing = _records.FirstOrDefault(rec => rec.originalSharedMesh == shared);
            Vector3[] hardNormals;
            Vector3[] softNormals;
            Mesh working;

            if (existing != null)
            {
                hardNormals = existing.hardNormals;
                softNormals = existing.softNormals;
                working = Object.Instantiate(shared);
            }
            else
            {
                working = Object.Instantiate(shared);
                hardNormals = working.normals;
                softNormals = ComputeSmoothedNormals(working);
            }

            working.name = shared.name + "_Preview";
            working.hideFlags = HideFlags.DontSave;

            var rec = new MeshRecord
            {
                rendererComponent = r,
                originalSharedMesh = shared,
                workingMesh = working,
                hardNormals = hardNormals,
                softNormals = softNormals,
                smoothness = 1f, // 默认全软
            };
            _records.Add(rec);

            SetMeshToComponent(r, working);
            ApplySmoothnessToRecord(rec);
            count++;
        }

        if (count > 0)
        {
            SceneView.RepaintAll();
            Debug.Log($"[软硬边切换] 已捕获 {count} 个模型");
        }
    }

    /// <summary>统一设置所有模型的平滑度</summary>
    private void SetAllSmoothness(float t)
    {
        foreach (var rec in _records)
        {
            rec.smoothness = t;
            ApplySmoothnessToRecord(rec);
        }
        SceneView.RepaintAll();
    }

    /// <summary>对单条记录应用其当前的平滑度</summary>
    private static void ApplySmoothnessToRecord(MeshRecord rec)
    {
        if (rec?.workingMesh == null) return;

        float t = rec.smoothness;
        var normals = rec.workingMesh.normals;
        var hard = rec.hardNormals;
        var soft = rec.softNormals;

        if (normals.Length != hard.Length || normals.Length != soft.Length)
            return;

        for (int i = 0; i < normals.Length; i++)
            normals[i] = Vector3.Lerp(hard[i], soft[i], t).normalized;

        rec.workingMesh.normals = normals;
    }

    private static void RestoreRecord(MeshRecord rec)
    {
        if (rec?.rendererComponent != null && rec.originalSharedMesh != null)
            RestoreMeshToComponent(rec.rendererComponent, rec.originalSharedMesh);
        if (rec?.workingMesh != null)
            Object.DestroyImmediate(rec.workingMesh);
        _records.Remove(rec);
    }

    private static void RestoreAll()
    {
        int count = _records.Count;
        for (int i = _records.Count - 1; i >= 0; i--)
            RestoreRecord(_records[i]);
        _records.Clear();
        SceneView.RepaintAll();
        Debug.Log($"[软硬边切换] 已恢复全部 {count} 个模型");
    }

    private static void CleanupDestroyedRecords()
    {
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            if (_records[i]?.rendererComponent == null || _records[i]?.originalSharedMesh == null)
            {
                if (_records[i]?.workingMesh != null)
                    Object.DestroyImmediate(_records[i].workingMesh);
                _records.RemoveAt(i);
            }
        }
    }

    // ══════════════════════════════════════════════════
    // 法线平滑算法
    // ══════════════════════════════════════════════════

    private static Vector3[] ComputeSmoothedNormals(Mesh source)
    {
        var vertices = source.vertices;
        var normals = source.normals;

        var groups = new Dictionary<Vector3Int, List<int>>(vertices.Length / 2);
        for (int i = 0; i < vertices.Length; i++)
        {
            var key = QuantizePosition(vertices[i]);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                groups[key] = list;
            }
            list.Add(i);
        }

        foreach (var kv in groups)
        {
            var indices = kv.Value;
            if (indices.Count <= 1) continue;

            Vector3 sum = Vector3.zero;
            foreach (int idx in indices)
                sum += normals[idx];

            if (sum.sqrMagnitude < 0.0001f) continue;

            var avg = sum.normalized;
            foreach (int idx in indices)
                normals[idx] = avg;
        }

        return normals;
    }

    private static Vector3Int QuantizePosition(Vector3 v)
    {
        const float scale = 100000f;
        return new Vector3Int(
            Mathf.RoundToInt(v.x * scale),
            Mathf.RoundToInt(v.y * scale),
            Mathf.RoundToInt(v.z * scale)
        );
    }
}
