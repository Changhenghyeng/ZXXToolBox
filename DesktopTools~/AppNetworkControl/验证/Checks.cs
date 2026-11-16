using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AppNetworkControl;

internal static class Checks
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string a = @"C:\Demo\Alpha.exe", b = @"C:\Demo\Beta.exe", shared = @"C:\Demo\CommonHelper.exe";
        var state = new ControlState { Groups = [new() { RootPath = a, Paths = [a, shared] }, new() { RootPath = b, Paths = [b, shared.ToUpperInvariant()] }] };
        state.SetBlocked(a, true);
        Assert(state.Merge(a, [shared.ToUpperInvariant(), @"C:\Demo\Updater.exe"]), "new path should be discovered");
        Assert(state.Groups[0].Paths.Count == 3, "case insensitive discovery deduplication");
        Assert(!state.IsBlocked(@"C:\Demo\Updater.exe"), "new paths must remain unchecked");
        Assert(!state.Merge(@"C:\Removed.exe", [shared]), "stale scan cannot recreate a removed group");
        state.SetBlocked(shared, true);
        Assert(state.IsBlocked(shared.ToUpperInvariant()), "global case insensitive state");
        state.SetBlocked(shared.ToUpperInvariant(), false);
        state.Merge(a, [shared]);
        Assert(!state.IsBlocked(shared), "rescan cannot block explicitly unchecked paths");
        state.SetBlocked(@"D:\Other\Alpha.exe", true);
        Assert(!state.IsBlocked(@"E:\Other\Alpha.exe"), "same filename at another path remains independent");
        var restored = JsonSerializer.Deserialize<ControlState>(JsonSerializer.Serialize(state))!;
        Assert(restored.Groups.Count == 2 && restored.IsBlocked(a) && !restored.IsBlocked(shared), "serialization preserves groups and unchecked choices");

        var policy = new FakePolicy();
        policy.Rules.Add(new FakeRule { Name = "Other product", ApplicationName = shared });
        policy.Rules.Add(new FakeRule { Name = "应用联网控制 [oldA] in", ApplicationName = shared });
        policy.Rules.Add(new FakeRule { Name = "应用联网控制 [oldB] out", ApplicationName = shared.ToUpperInvariant() });
        policy.Rules.Add(new FakeRule { Name = "应用联网控制 [unrelated] in", ApplicationName = b });
        FirewallRules.ApplyPathRules(policy, shared, true, () => new FakeRule());
        Assert(policy.Rules.Items.Count == 4, "legacy duplicates replaced with one rule pair, unrelated rules preserved");
        var ruleStates = FirewallRules.ReadPathStatesFrom(policy);
        Assert(ruleStates[shared].Effective, "both generated directions verified");
        policy.FirewallEnabled[1] = false;
        Assert(!FirewallRules.ReadPathStatesFrom(policy)[shared].Effective, "disabled Windows firewall must show anomaly");
        policy.FirewallEnabled[1] = true;
        policy.Rules.Items.First(x => x.Name.StartsWith("应用联网控制 [") && x.ApplicationName == shared).Enabled = false;
        Assert(!FirewallRules.ReadPathStatesFrom(policy)[shared].Effective, "disabled individual rule must show anomaly");
        FirewallRules.ApplyPathRules(policy, shared.ToUpperInvariant(), false, () => new FakeRule());
        Assert(policy.Rules.Items.Count == 2 && policy.Rules.Items.Any(x => x.Name == "Other product"), "uncheck removes all own matching rules and never foreign rules");
        Assert(!FirewallRules.ReadPathStatesFrom(policy).ContainsKey(shared), "unchecked path has no tool rules remaining");

        var writes = new List<(string Path, bool Blocked)>();
        int saves = 0;
        using var form = new MainForm(false, state, (path, blocked) => writes.Add((path, blocked)), _ => saves++);
        var grid = (DataGridView)typeof(MainForm).GetField("grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Assert(grid.Rows.Count == 7, "two expanded groups with their children");
        Assert(grid.Rows[0].ReadOnly && grid.Rows[4].ReadOnly, "group headings must not toggle firewall rules");
        grid.Rows[2].Cells[0].Value = true;
        Assert(grid.Rows[6].Cells[0].Value is true, "checking one shared EXE updates the other group immediately");
        Assert(writes.Count == 1 && writes[0].Blocked && saves == 1, "shared toggle applies rules and saves once");
        grid.Rows[6].Cells[0].Value = false;
        Assert(grid.Rows[2].Cells[0].Value is false && !state.IsBlocked(shared), "unchecking another group synchronizes back");
        Assert(writes.Count == 2 && !writes[1].Blocked && saves == 2, "uncheck applies once without recursive handlers");

        // Populate status without changing any real firewall rules.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("rules", flags)!.SetValue(form, new Dictionary<string, PathRuleState>(StringComparer.OrdinalIgnoreCase) { [a] = new(true) });
        typeof(MainForm).GetField("lastRuleCheck", flags)!.SetValue(form, DateTime.Now);
        typeof(MainForm).GetField("nextRuleCheck", flags)!.SetValue(form, DateTime.UtcNow.AddSeconds(30));
        typeof(MainForm).GetMethod("UpdateRows", flags)!.Invoke(form, null);
        Assert(grid.Rows[1].Cells[3].Value?.ToString() == "已禁网（规则有效）", "verified status displayed");
        Assert(grid.Rows[2].Cells[3].Value?.ToString() == "未限制", "unchecked status displayed");
        typeof(MainForm).GetMethod("UpdateMetrics", flags)!.Invoke(form, null);
        form.Show();
        Application.DoEvents();
        var layout = (TableLayoutPanel)typeof(MainForm).GetField("layout", flags)!.GetValue(form)!;
        var toolbar = (FlowLayoutPanel)typeof(MainForm).GetField("toolbar", flags)!.GetValue(form)!;
        foreach (int fontSize in new[] { 10, 15, 20 })
        {
            form.Font = new Font("Microsoft YaHei UI", fontSize);
            foreach (var size in new[] { new Size(640, 480), new Size(1080, 680), new Size(1500, 900) })
            {
                form.ClientSize = size;
                Application.DoEvents();
                var controls = layout.Controls.Cast<Control>().OrderBy(x => layout.GetRow(x)).ToArray();
                for (int i = 1; i < controls.Length; i++)
                    Assert(controls[i - 1].Bottom <= controls[i].Top, $"no vertical overlap at {size}, font {fontSize}");
                foreach (Label label in controls.OfType<Label>())
                    Assert(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height, $"label fully visible at {size}, font {fontSize}");
                foreach (Control item in toolbar.Controls)
                    Assert(item.Right <= toolbar.Width && item.Bottom <= toolbar.Height, $"toolbar item contained at {size}, font {fontSize}");
                Assert(grid.Height > 0, "grid has available space");
                if (size.Width == 640)
                {
                    using var sample = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(sample, new Rectangle(Point.Empty, form.Size));
                    sample.Save(Path.GetFullPath($"work/responsive-font{fontSize}.png"));
                }
            }
        }
        form.Font = new Font("Microsoft YaHei UI", 10);
        form.ClientSize = new Size(1080, 680);
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.GetFullPath("work/preview.png")); }
        form.ClientSize = new Size(800, 450);
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.GetFullPath("work/preview-small.png")); }
        form.Hide();

        string self = Environment.ProcessPath!;
        var watch = Stopwatch.StartNew();
        var found = NetworkProcessScanner.FindMany([self, self.ToUpperInvariant(), @"C:\Missing\NeverRunning.exe"]);
        Assert(found.Count == 2, "scan deduplicates identical root paths");
        Assert(found[self][0].Pids.Contains(Environment.ProcessId), "scan identifies real running root process");
        Assert(found[@"C:\Missing\NeverRunning.exe"].Count == 1, "missing root retained without invented child processes");
        Console.WriteLine($"PASS: {assertions} assertions; real process scan {watch.Elapsed.TotalMilliseconds:F1} ms; no firewall/config changes.");
    }
}

public sealed class FakePolicy
{
    public FakeRules Rules { get; } = new();
    public int CurrentProfileTypes { get; set; } = 1;
    public Dictionary<int, bool> FirewallEnabled { get; } = new() { [1] = true, [2] = true, [4] = true };
}
public sealed class FakeRules : System.Collections.IEnumerable
{
    public List<FakeRule> Items { get; } = [];
    public void Add(FakeRule rule) => Items.Add(rule);
    public void Remove(string name) => Items.RemoveAll(x => x.Name == name);
    public System.Collections.IEnumerator GetEnumerator() => Items.GetEnumerator();
}
public sealed class FakeRule
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ApplicationName { get; set; } = "";
    public int Direction { get; set; }
    public int Action { get; set; }
    public bool Enabled { get; set; }
    public int Profiles { get; set; }
    public string InterfaceTypes { get; set; } = "";
}
