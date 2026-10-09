using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed class CaptureHelper : EditorWindow
{
    private const string Out = "Assets/Scripts/Editor/ArtTools/ZXXTools/README/CaptureHelperOutput";

    private static readonly (string file, string steps, Action open)[] List =
    {
        ("ProcGen",              "1.拖入目标模型  2.设置散布参数  3.添加Prefab插槽  4.点Generate",                    ProcGen.ProcGenWindow.Open),
        ("PathBuilder",          "1.选择类型  2.绘制或设置路径  3.调整生成参数  4.预览或生成",                        PathBuilderWindow.ShowWindow),
        ("FogOfWar",             "1.Fit选区自动算范围  2.设行列数  3.Create Clickable Fog Grid",                        FogOfWarTool.Editor.FogOfWarToolWindow.Open),
        ("DayChangeTransition",  "1.指定主光源  2.调过渡时长  3.Create/Repair Setup  4.预览",                           DayChangeTransitionTool.Editor.DayChangeTransitionToolWindow.Open),
        ("BuildingCard",         "1.拖入正交相机+CellSize  2.拖入建筑PNG  3.填Grid WxD  4.拖角点校准  5.Generate",     BuildingCardTool.ShowWindow),
        ("BoxColliderGenerator", "1.Import预制体  2.选挂点+参与节点  3.Generate Preview  4.Confirm Write",              BoxColliderGenerator.ShowWindow),
        ("ShadowBaker",          "1.添加建筑  2.设光照方向  3.调参数  4.Bake+Generate",                                 ShadowBakerTool.Editor.ShadowBakerWindow.Open),
        ("SelfShadowBaker",      "1.AddSelected导入模型  2.选光源方向  3.调AO/阴影参数  4.Bake R+G+B All",               SelfShadowBakerTool.Editor.SelfShadowBakerWindow.Open),
        ("Outline",              "1.拖入Target  2.可选拖Outline材质  3.Bake烘焙  4.Add Material",                        OutlineToolWindow.Open),
        ("ShadingMode",          "1.选中Hierarchy模型  2.Capture捕获  3.拖滑块调软硬边  4.Restore恢复",                  ShadingModeToggleWindow.Open),
        ("BatchReplacer",        "1.Hierarchy多选旧物体  2.拖入新Prefab  3.勾选保留项  4.Replace",                      PrefabBatchReplacer.ShowWindow),
        ("BatchRename",          "1.选来源  2.设重命名规则  3.Analyze Preview  4.Execute Rename",                        BatchRenameTool.Open),
        ("PrefabExtractor",      "1.拖入大预制体列表  2.Analyze分析  3.确认共享组  4.Execute提取",                      PrefabTools.PrefabExtractorTool.Open),
        ("NormalizeRoot",        "1.选中物体 Ctrl+Shift+N  2.根节点归零 Content保留偏移",                                PrefabTools.PrefabRootNormalizerWindow.ShowWindow),
        ("TransparentScreenshot","1.选Camera  2.设输出尺寸  3.选模式(单帧/动画/粒子)  4.Capture",                       TransparentScreenshot.OpenWindow),
        ("LabelTool",            "1.设扫描根节点  2.Scan扫描  3.调样式  4.可选Bake烘培",                                  LabelTool.LabelManagerWindow.Open),
        ("MeasureTool",          "1.包围盒:Add Selected  2.标尺:Scene点击两点  3.切单位",                                 MeasureTool.Open),
        ("GitPathNavigator",     "1.Ctrl+G打开  2.粘贴Git路径  3.自动定位Ping高亮",                                       GitPathNavigator.ShowWindow),
    };

    private enum Ph { Idle, WaitOpen, Snap, WaitClose, End }
    private Ph _ph;
    private int _i, _f;
    private double _t;
    private string _s = "";
    private readonly List<string> _e = new List<string>();
    private HashSet<EditorWindow> _pre;

    public static void Run()
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Application.dataPath, "..", Out)));
        var w = GetWindow<CaptureHelper>("Generating…");
        w.minSize = new Vector2(380, 140);
        w.Show();
        w.Begin();
    }

    void Begin() { _i = 0; _e.Clear(); _ph = Ph.WaitOpen; _s = "…"; _f = 0; Snap(); OpenOne(); EditorApplication.update += Tick; }
    void OnDestroy() { EditorApplication.update -= Tick; Clean(); }
    void Snap() { _pre = new HashSet<EditorWindow>(Resources.FindObjectsOfTypeAll<EditorWindow>().Where(x => x != null)); }
    void Clean() { if (_pre == null) return; foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>()) { if (w != null && w != this && !_pre.Contains(w)) try { w.Close(); } catch { } } }

    void OnGUI()
    {
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("README 截图生成", EditorStyles.boldLabel);
        var t = List.Length; var c = _ph == Ph.End ? t : Mathf.Min(_i + 1, t);
        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(false, 24), (float)c / t, $"{c}/{t}");
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField(_s, EditorStyles.wordWrappedLabel);
        EditorGUILayout.HelpBox(_ph == Ph.End ? "完成，可以关掉了" : "请勿操作鼠标键盘", _ph == Ph.End ? MessageType.Info : MessageType.Warning);
    }

    void Tick()
    {
        switch (_ph)
        {
            case Ph.WaitOpen: _f++; if (_f >= 8 && EditorApplication.timeSinceStartup >= _t) { _ph = Ph.Snap; Tick(); } break;
            case Ph.Snap: DoSnap(); Clean(); Snap(); _t = EditorApplication.timeSinceStartup + 0.2; _f = 0; _ph = Ph.WaitClose; break;
            case Ph.WaitClose: _f++; if (_f >= 3 && EditorApplication.timeSinceStartup >= _t) { _i++; if (_i >= List.Length) { End(); return; } OpenOne(); _ph = Ph.WaitOpen; _f = 0; } break;
        }
        Repaint();
    }

    void OpenOne() { var t = List[_i]; _s = $"[{_i + 1}/{List.Length}] {t.file}"; try { t.open(); } catch (Exception e) { _e.Add($"{t.file}: {e.Message}"); } _t = EditorApplication.timeSinceStartup + 0.45; _f = 0; }

    void DoSnap()
    {
        var t = List[_i];
        EditorWindow target = null;
        foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
        { if (w != null && w != this && !_pre.Contains(w) && w.position.width > 60 && w.position.height > 60) { target = w; break; } }
        if (target == null)
        { EditorWindow best = null; float bestA = 0; foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>()) { if (w != null && w != this) { var a = w.position.width * w.position.height; if (a > bestA) { bestA = a; best = w; } } } target = best; }
        if (target == null) { _e.Add($"{t.file}: ?"); return; }
        try
        {
            var p = target.position;
            var sr = new Rectangle((int)p.x, (int)p.y, Math.Max(1, (int)p.width), Math.Max(1, (int)p.height));
            using (var bmp = new Bitmap(sr.Width, sr.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp)) { g.CopyFromScreen(sr.X, sr.Y, 0, 0, sr.Size); }
                Stamp(bmp, t.file, t.steps);
                bmp.Save(Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..", Out)), t.file + ".png"), ImageFormat.Png);
            }
            _s = $"[{_i + 1}/{List.Length}] OK {t.file}";
        }
        catch (Exception e) { _e.Add($"{t.file}: {e.Message}"); }
    }

    static void Stamp(Bitmap bmp, string title, string steps)
    {
        var m = 10; var mw = bmp.Width - m * 2; const float ms = 9f;
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAlias;
            float ts = 14f, ss = 11f;
            using (var xf = new System.Drawing.Font(new FontFamily("Microsoft YaHei"), ss)) { if (g.MeasureString(steps, xf).Width > mw) ss = ms; }
            var ls = new List<string>();
            using (var sf = new System.Drawing.Font(new FontFamily("Microsoft YaHei"), ss))
            {
                if (g.MeasureString(steps, sf).Width <= mw) ls.Add(steps);
                else { var ps = steps.Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries); var cur = ""; foreach (var p in ps) { var tst = string.IsNullOrEmpty(cur) ? p : cur + "  " + p; if (g.MeasureString(tst, sf).Width <= mw) cur = tst; else { if (!string.IsNullOrEmpty(cur)) ls.Add(cur); cur = p; } } if (!string.IsNullOrEmpty(cur)) ls.Add(cur); }
            }
            ts = Math.Min(ts, ss + 2f);
            using (var tf = new System.Drawing.Font(new FontFamily("Microsoft YaHei"), ts, System.Drawing.FontStyle.Bold))
            using (var sf = new System.Drawing.Font(new FontFamily("Microsoft YaHei"), ss))
            {
                var th = g.MeasureString(title, tf).Height; var lh = g.MeasureString("Ag", sf).Height;
                var bh = (int)(th + lh * ls.Count + 14); var by = bmp.Height - bh;
                using (var bb = new SolidBrush(System.Drawing.Color.FromArgb(220, 18, 18, 18))) { g.FillRectangle(bb, 0, by, bmp.Width, bh); }
                using (var lp = new Pen(System.Drawing.Color.FromArgb(200, 255, 185, 60), 2f)) { g.DrawLine(lp, 0, by, bmp.Width, by); }
                using (var tb = new SolidBrush(System.Drawing.Color.White)) { g.DrawString(title, tf, tb, m, by + 4); }
                using (var sb = new SolidBrush(System.Drawing.Color.FromArgb(240, 255, 210, 120))) { for (int i = 0; i < ls.Count; i++) g.DrawString(ls[i], sf, sb, m, by + th + 4 + lh * i); }
            }
        }
    }

    void End()
    {
        EditorApplication.update -= Tick; _ph = Ph.End;
        _s = $"OK {List.Length - _e.Count}/{List.Length}";
        if (_e.Count > 0) Debug.LogWarning("[Capture] " + string.Join(", ", _e));
        Debug.Log($"[Capture] {List.Length - _e.Count}/{List.Length} → {Out}");
        AssetDatabase.Refresh(); Repaint();
    }
}
