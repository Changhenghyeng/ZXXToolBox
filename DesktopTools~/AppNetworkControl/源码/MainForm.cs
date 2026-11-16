using System.Diagnostics;

namespace AppNetworkControl;

internal sealed class MainForm : Form
{
    private readonly ControlState state;
    private readonly Action<string, bool> applyRule;
    private readonly Action<ControlState> saveState;
    private readonly bool preview;
    private readonly DataGridView grid = new();
    private readonly Label message = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label metrics = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label hint = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.DimGray, Margin = new Padding(3, 6, 3, 10) };
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(16), Margin = Padding.Empty, RowCount = 5, ColumnCount = 1 };
    private readonly FlowLayoutPanel toolbar = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
    private bool sizingLayout;
    private readonly CheckBox startup = new() { Text = "开机自启（托盘）", AutoSize = true };
    private readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer metricsTimer = new() { Interval = 2000 };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Shield, Text = "应用联网控制 v1.1.1" };
    private readonly ContextMenuStrip trayMenu = new();
    private readonly Dictionary<string, string> runtime = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PathRuleState> rules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Process ownProcess = Process.GetCurrentProcess();
    private readonly Stopwatch metricsClock = Stopwatch.StartNew();
    private TimeSpan previousCpu;
    private long previousSample;
    private bool updating, scanning, exiting, startupBusy;
    private long mutation;
    private double lastScanMs;
    private DateTime nextRuleCheck = DateTime.MinValue;
    private DateTime? lastRuleCheck;
    private string? ruleError;

    public MainForm(bool startHidden, ControlState? previewState = null, Action<string, bool>? applyRule = null, Action<ControlState>? saveState = null)
    {
        preview = previewState is not null;
        this.applyRule = applyRule ?? FirewallRules.SetPathBlocked;
        this.saveState = saveState ?? ControlStore.Save;
        state = previewState ?? ControlStore.Load();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "应用联网控制 v1.1.1";
        ClientSize = new Size(1080, 680);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(247, 248, 250);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var add = new Button { Text = "添加 EXE…", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 6) };
        var remove = new Button { Text = "移除选中组", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 6, 10, 6) };
        add.Click += AddExecutables;
        remove.Click += RemoveGroup;
        startup.Margin = new Padding(20, 8, 3, 3);
        startupBusy = true;
        startup.Checked = previewState is null && StartupManager.IsEnabled();
        startupBusy = false;
        startup.CheckedChanged += (_, _) =>
        {
            if (startupBusy) return;
            try { if (startup.Checked) StartupManager.Enable(); else StartupManager.Disable(); }
            catch (Exception ex)
            {
                startupBusy = true; startup.Checked = !startup.Checked; startupBusy = false;
                ShowError("开机自启设置失败", ex);
            }
        };
        toolbar.Controls.AddRange([add, remove, startup]);
        layout.Controls.Add(toolbar, 0, 0);
        hint.Text = "勾选即禁网，取消即解除本工具限制 · 同一 EXE 自动同步 · 新发现的程序默认不勾选";
        layout.Controls.Add(hint, 0, 1);
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.AutoGenerateColumns = false;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.Padding = new Padding(3, 5, 3, 5);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Blocked", HeaderText = "禁网", AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Program", HeaderText = "应用组 / 关联程序", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 28, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Running", HeaderText = "运行状态", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 18, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rule", HeaderText = "防火墙规则检查", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 22, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "完整路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 32, ReadOnly = true });
        grid.Columns["Running"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns["Rule"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        foreach (DataGridViewColumn column in grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += ChangeBlocked;
        grid.CellContentClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && grid.Rows[e.RowIndex].Tag is RowIdentity { Path: null } && e.ColumnIndex == 0)
                grid.CancelEdit();
        };
        layout.Controls.Add(grid, 0, 2);
        layout.Controls.Add(message, 0, 3);
        layout.Controls.Add(metrics, 0, 4);
        Controls.Add(layout);
        layout.SizeChanged += (_, _) => ApplyResponsiveSizing();
        message.TextChanged += (_, _) => ApplyResponsiveSizing();
        metrics.TextChanged += (_, _) => ApplyResponsiveSizing();
        DpiChanged += (_, _) => ApplyResponsiveSizing();
        FontChanged += (_, _) => ApplyResponsiveSizing();
        ApplyResponsiveSizing();
        trayMenu.Items.Add("打开主界面", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("退出工具（保留禁网规则）", null, (_, _) => { exiting = true; Close(); });
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => RestoreWindow();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideWindow(); };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideWindow(); }
        };
        FormClosed += (_, _) => { scanTimer.Stop(); metricsTimer.Stop(); tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); ownProcess.Dispose(); scanTimer.Dispose(); metricsTimer.Dispose(); };
        scanTimer.Tick += async (_, _) => await ScanAsync();
        metricsTimer.Tick += (_, _) => UpdateMetrics();
        previousCpu = ownProcess.TotalProcessorTime;
        RebuildRows();
        UpdateMessage();
        Shown += async (_, _) =>
        {
            if (previewState is not null) return;
            if (startHidden) HideWindow();
            // Refresh an existing startup registration to this release's actual path.
            if (startup.Checked)
            {
                try { StartupManager.Enable(); }
                catch (Exception ex) { ShowError("更新开机自启路径失败", ex); }
            }
            scanTimer.Start(); metricsTimer.Start();
            await ScanAsync();
        };
    }

    private void ApplyResponsiveSizing()
    {
        if (sizingLayout || grid.Columns.Count == 0) return;
        sizingLayout = true;
        try
        {
            int panelWidth = Math.Max(1, ClientSize.Width);
            int width = Math.Max(1, panelWidth - layout.Padding.Horizontal - 6);
            foreach (var label in new[] { hint, message, metrics })
                label.MaximumSize = new Size(width, 0);
            toolbar.MaximumSize = new Size(width, 0);
            float scale = DeviceDpi / 96f;
            grid.Columns["Blocked"]!.Width = TextRenderer.MeasureText("禁网", Font).Width + (int)(24 * scale);
            foreach (var item in new[] { ("Program", 130), ("Running", 95), ("Rule", 115), ("Path", 120) })
            {
                var column = grid.Columns[item.Item1]!;
                column.MinimumWidth = Math.Max((int)(item.Item2 * scale), TextRenderer.MeasureText(column.HeaderText, Font).Width + (int)(24 * scale));
            }
            grid.Columns["Program"]!.FillWeight = 28;
            grid.Columns["Running"]!.FillWeight = 18;
            grid.Columns["Rule"]!.FillWeight = 22;
            grid.Columns["Path"]!.FillWeight = 32;
            // Keep room for the header and several rows even with large system text.
            int textHeight = new[] { hint, message, metrics }.Sum(x => x.GetPreferredSize(new Size(width, 0)).Height + x.Margin.Vertical);
            int toolbarHeight = toolbar.GetPreferredSize(new Size(width, 0)).Height + toolbar.Margin.Vertical;
            int gridHeight = (Font.Height + (int)(16 * scale)) * 4 + SystemInformation.HorizontalScrollBarHeight;
            int minimumHeight = textHeight + toolbarHeight + gridHeight + layout.Padding.Vertical + grid.Margin.Vertical;
            var area = Screen.FromControl(this).WorkingArea;
            var minimum = new Size(Math.Min(area.Width, (int)(640 * scale)), Math.Min(area.Height, Math.Max((int)(420 * scale), minimumHeight + Height - ClientSize.Height)));
            if (MinimumSize != minimum && IsHandleCreated)
            {
                // Change window constraints after the current layout pass has completed.
                BeginInvoke(() => { if (!IsDisposed && MinimumSize != minimum) { MinimumSize = minimum; PerformLayout(); } });
            }
        }
        finally { sizingLayout = false; }
    }

    private void AddExecutables(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Title = "添加要监控的程序（可多选）", Filter = "程序或快捷方式|*.exe;*.lnk", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var errors = new List<string>();
        foreach (string filename in dialog.FileNames)
        {
            try
            {
                string path = ExecutableResolver.Resolve(filename);
                if (state.Groups.Any(x => string.Equals(x.RootPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                state.Groups.Add(new ApplicationGroup { RootPath = path, Paths = [path] });
                mutation++;
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }
        TrySave(); RebuildRows(); UpdateMessage();
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "部分程序未添加");
        _ = ScanAsync();
    }

    private void RemoveGroup(object? sender, EventArgs e)
    {
        if (grid.CurrentRow?.Tag is not RowIdentity id) return;
        var group = state.Groups.FirstOrDefault(x => string.Equals(x.RootPath, id.Root, StringComparison.OrdinalIgnoreCase));
        if (group is null) return;
        if (MessageBox.Show(this, $"移除 {Path.GetFileName(group.RootPath)}？\n\n仅属于此组的程序将解除本工具的禁网限制；其他组共用的程序保持原状态。", "移除应用组", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var others = state.Groups.Where(x => x != group).SelectMany(x => x.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in group.Paths.Where(x => !others.Contains(x)))
            {
                applyRule(path, false);
                state.SetBlocked(path, false);
            }
            state.Groups.Remove(group);
            mutation++; nextRuleCheck = DateTime.MinValue;
            TrySave(); RebuildRows(); UpdateMessage();
        }
        catch (Exception ex)
        {
            mutation++; nextRuleCheck = DateTime.MinValue;
            TrySave(); RebuildRows(); ShowError("移除未完成，已保留此组", ex);
        }
    }

    private void ChangeBlocked(object? sender, DataGridViewCellEventArgs e)
    {
        if (updating || e.RowIndex < 0 || e.ColumnIndex != 0 || grid.Rows[e.RowIndex].Tag is not RowIdentity { Path: not null } id) return;
        bool requested = grid.Rows[e.RowIndex].Cells[0].Value is true;
        bool old = state.IsBlocked(id.Path);
        try
        {
            applyRule(id.Path, requested);
            state.SetBlocked(id.Path, requested);
            saveState(state);
        }
        catch (Exception ex)
        {
            state.SetBlocked(id.Path, old);
            try { applyRule(id.Path, old); }
            catch (Exception rollback) { ex = new InvalidOperationException(ex.Message + "\n恢复先前规则失败：" + rollback.Message); }
            ShowError("修改未成功，请检查规则状态", ex);
        }
        mutation++; nextRuleCheck = DateTime.MinValue;
        rules.Remove(id.Path);
        UpdateRows();
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (preview || scanning || IsDisposed) return;
        scanning = true;
        string[] roots = state.Groups.Select(x => x.RootPath).ToArray();
        long revision = mutation;
        bool check = DateTime.UtcNow >= nextRuleCheck;
        try
        {
            var result = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var found = NetworkProcessScanner.FindMany(roots);
                Dictionary<string, PathRuleState>? checkedRules = null;
                string? error = null;
                if (check)
                {
                    try { checkedRules = FirewallRules.ReadPathStates(); }
                    catch (Exception ex) { error = ex.Message; }
                }
                return (found, checkedRules, error, elapsed: watch.Elapsed.TotalMilliseconds);
            });
            if (IsDisposed || exiting) return;
            lastScanMs = result.elapsed;
            bool changed = false;
            foreach (var pair in result.found)
            {
                changed |= state.Merge(pair.Key, pair.Value.Select(x => x.Path));
                foreach (var item in pair.Value)
                    runtime[item.Path] = item.Pids.Length > 0 ? "PID " + string.Join(", ", item.Pids) : "未运行 / 未检测到连接";
            }
            var livePaths = result.found.Values.SelectMany(x => x).Where(x => x.Pids.Length > 0).Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in state.Groups.SelectMany(x => x.Paths))
                if (!livePaths.Contains(path)) runtime[path] = "未运行 / 未检测到连接";
            if (check && revision == mutation)
            {
                ruleError = result.error;
                if (result.checkedRules is not null) rules = result.checkedRules;
                lastRuleCheck = DateTime.Now;
                nextRuleCheck = DateTime.UtcNow.AddSeconds(30);
            }
            if (changed) { TrySave(); RebuildRows(); } else UpdateRows();
            UpdateMessage();
        }
        catch (Exception ex) { message.Text = "扫描失败，将在下次重试：" + ex.Message; message.ForeColor = Color.Firebrick; }
        finally { scanning = false; }
    }

    private void RebuildRows()
    {
        var selected = grid.CurrentRow?.Tag as RowIdentity;
        int first = grid.FirstDisplayedScrollingRowIndex;
        updating = true;
        try
        {
            grid.Rows.Clear();
            foreach (var group in state.Groups)
            {
                int index = grid.Rows.Add(false, "▼ " + Path.GetFileName(group.RootPath), "定时监控中", $"关联 {group.Paths.Count - 1} 个程序", group.RootPath);
                var header = grid.Rows[index];
                header.Tag = new RowIdentity(group.RootPath, null);
                header.ReadOnly = true;
                header.Cells[0] = new DataGridViewTextBoxCell { Value = "" };
                header.DefaultCellStyle.BackColor = Color.FromArgb(225, 234, 245);
                foreach (string path in group.Paths)
                {
                    bool root = string.Equals(path, group.RootPath, StringComparison.OrdinalIgnoreCase);
                    index = grid.Rows.Add(state.IsBlocked(path), "    " + Path.GetFileName(path) + (root ? "（主程序）" : ""), "", "", path);
                    grid.Rows[index].Tag = new RowIdentity(group.RootPath, path);
                }
            }
            if (selected is not null)
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is RowIdentity id && id == selected) { grid.CurrentCell = row.Cells[1]; break; }
            if (first >= 0 && first < grid.Rows.Count) grid.FirstDisplayedScrollingRowIndex = first;
        }
        finally { updating = false; }
        UpdateRows();
    }

    private void UpdateRows()
    {
        updating = true;
        try
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not RowIdentity { Path: not null } id) continue;
                bool blocked = state.IsBlocked(id.Path);
                SetCell(row.Cells[0], blocked);
                SetCell(row.Cells[2], runtime.GetValueOrDefault(id.Path, "等待扫描"));
                string status;
                if (ruleError is not null) status = "检查失败";
                else if (nextRuleCheck == DateTime.MinValue || lastRuleCheck is null) status = "等待检查";
                else if (rules.TryGetValue(id.Path, out var rule))
                    status = blocked ? (rule.Effective ? "已禁网（规则有效）" : "规则异常") : "仍有本工具规则";
                else status = blocked ? "规则异常：缺少规则" : "未限制";
                SetCell(row.Cells[3], status);
                row.Cells[3].Style.ForeColor = status.Contains("异常") || status.Contains("失败") || status.Contains("仍有") ? Color.Firebrick : blocked ? Color.FromArgb(170, 55, 40) : Color.FromArgb(30, 120, 70);
                row.Cells[3].ToolTipText = ruleError ?? "仅检查本工具的防火墙规则及防火墙开启状态，不代表真实联网测试；未限制不代表其他软件未拦截。";
            }
        }
        finally { updating = false; }
    }

    private static void SetCell(DataGridViewCell cell, object value) { if (!Equals(cell.Value, value)) cell.Value = value; }
    private void UpdateMessage()
    {
        message.ForeColor = Color.DimGray;
        message.Text = $"{state.Groups.Count} 个应用组 · 每 5 秒扫描 · 每 30 秒检查规则 · 最近检查：{(lastRuleCheck?.ToString("HH:mm:ss") ?? "等待检查")}";
    }
    private void UpdateMetrics()
    {
        ownProcess.Refresh();
        long now = metricsClock.ElapsedMilliseconds;
        var cpu = ownProcess.TotalProcessorTime;
        double percent = (cpu - previousCpu).TotalMilliseconds / Math.Max(1, now - previousSample) / Environment.ProcessorCount * 100;
        previousCpu = cpu; previousSample = now;
        metrics.Text = $"本工具 CPU：{Math.Clamp(percent, 0, 100):F1}%  ｜ 内存：{ownProcess.WorkingSet64 / 1048576d:F1} MB  ｜ 上次扫描及检查：{lastScanMs:F0} ms";
    }
    private void TrySave() { try { saveState(state); } catch (Exception ex) { ShowError("保存失败，本次修改可能无法在重启后恢复", ex); } }
    private void ShowError(string title, Exception ex) => MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    private void HideWindow() { tray.Visible = true; ShowInTaskbar = false; Hide(); }
    private void RestoreWindow() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; tray.Visible = false; Activate(); }
    private sealed record RowIdentity(string Root, string? Path);
}
