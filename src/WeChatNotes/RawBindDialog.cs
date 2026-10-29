namespace WeChatNotes;

internal sealed class RawBindDialog : Form
{
    private readonly RawRecorder recorder;
    private readonly ComboBox accounts = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly TextBox filter = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索群名称或联系人" };
    private readonly RangeCheckedListBox sessions = new() { Dock = DockStyle.Fill, CheckOnClick = true, DisplayMember = "DisplayName", IntegralHeight = false, AccessibleName = "可选会话" };
    private readonly Button selectAll = new() { Text = "全选", AutoSize = true, Enabled = false };
    private readonly Button clearAll = new() { Text = "取消全选", AutoSize = true, Enabled = false };
    private readonly Label selectionCount = new() { AutoSize = true, Padding = new(6, 7, 0, 0), AccessibleName = "会话选择数量" };
    private readonly ComboBox history = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, AccessibleName = "首次导入范围" };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "正在自动识别微信数据目录…" };
    private readonly Button connect = new() { Text = "读取会话列表", AutoSize = true };
    private readonly Button save = new() { Text = "添加选中会话", AutoSize = true, Enabled = false };
    private readonly Button browse = new() { Text = "选择目录", Dock = DockStyle.Fill };
    private readonly Button detect = new() { Text = "重新识别", Dock = DockStyle.Fill };
    private readonly HashSet<string> selected = [];
    private List<ChatSession> all = [];
    private readonly CancellationTokenSource cancellation = new();
    private bool busy;
    private bool populating;
    internal List<Target> Results { get; } = [];
    internal RawBindDialog(RawRecorder recorder)
    {
        this.recorder = recorder;
        Text = "选择需要记录的群或联系人"; Size = new(820, 660); MinimumSize = new(680, 520);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 1, RowCount = 8 };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 40), new(SizeType.Absolute, 42), new(SizeType.Absolute, 42), new(SizeType.Absolute, 40), new(SizeType.Percent, 100), new(SizeType.Absolute, 48), new(SizeType.Absolute, 42), new(SizeType.Absolute, 48) }) layout.RowStyles.Add(style);
        layout.Controls.Add(new Label { Text = "选择会话后自动记录原始消息文字，微信窗口可最小化。", AutoSize = true }, 0, 0);
        var source = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        source.ColumnStyles.Add(new(SizeType.Percent, 100)); source.ColumnStyles.Add(new(SizeType.Absolute, 100)); source.ColumnStyles.Add(new(SizeType.Absolute, 100));
        source.Controls.Add(accounts, 0, 0);
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择微信账号的 db_storage 文件夹", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) accounts.Text = dialog.SelectedPath; };
        source.Controls.Add(detect, 1, 0); source.Controls.Add(browse, 2, 0); layout.Controls.Add(source, 0, 1);
        layout.Controls.Add(filter, 0, 2);
        var selection = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        selection.Controls.AddRange([selectAll, clearAll, selectionCount]);
        layout.Controls.Add(selection, 0, 3); layout.Controls.Add(sessions, 0, 4);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 8, 0, 0) };
        options.Controls.Add(new Label { Text = "首次导入范围", AutoSize = true, Padding = new(0, 5, 6, 0) });
        history.Items.AddRange([new ImportRange("全部本地历史", 0), new ImportRange("最近 15 天", 15), new ImportRange("最近 30 天", 30),
            new ImportRange("最近 7 天", 7), new ImportRange("最近 24 小时", 1), new ImportRange("从添加时开始", null)]);
        history.SelectedIndex = 0; options.Controls.Add(history);
        layout.Controls.Add(options, 0, 5); layout.Controls.Add(status, 0, 6);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(save); buttons.Controls.Add(connect); layout.Controls.Add(buttons, 0, 7); Controls.Add(layout);
        accounts.TextChanged += (_, _) => { all.Clear(); selected.Clear(); Populate(); };
        filter.TextChanged += (_, _) => Populate();
        sessions.ItemCheck += (_, e) =>
        {
            if (populating) return;
            if (sessions.Items[e.Index] is ChatSession item) { if (e.NewValue == CheckState.Checked) selected.Add(item.Username); else selected.Remove(item.Username); }
            if (!sessions.UpdatingChecks) UpdateSelection();
        };
        sessions.ChecksUpdated += UpdateSelection;
        selectAll.Click += (_, _) => sessions.CheckAll(true);
        clearAll.Click += (_, _) => sessions.CheckAll(false);
        connect.Click += async (_, _) => await Connect();
        detect.Click += async (_, _) => await Detect();
        Shown += async (_, _) => await Detect();
        save.Click += (_, _) => Save();
        FormClosing += (_, e) => { if (busy) { cancellation.Cancel(); e.Cancel = true; status.Text = "正在取消，请稍后关闭。"; } };
        FormClosed += (_, _) => cancellation.Dispose();
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        selectionCount.Text = $"已选 {selected.Count} / 共 {all.Count} · 当前 {sessions.Items.Count} 项 · Shift 连选";
        selectAll.Enabled = clearAll.Enabled = !busy && sessions.Items.Count > 0;
        save.Enabled = !busy && selected.Count > 0;
    }
    private void SetBusy(bool value)
    {
        busy = value;
        connect.Enabled = accounts.Enabled = browse.Enabled = detect.Enabled = filter.Enabled = sessions.Enabled = !busy;
        UpdateSelection();
    }
    internal void SetSessions(IEnumerable<ChatSession> loaded)
    {
        all = loaded.ToList();
        selected.Clear(); Populate();
        status.Text = $"已读取 {all.Count} 个会话，请勾选需要记录的群或联系人。";
    }
    private async Task Detect()
    {
        if (busy) return;
        SetBusy(true);
        status.Text = "正在自动识别微信数据目录…";
        all.Clear(); selected.Clear(); Populate();
        string current = accounts.Text;
        string[] known = recorder.KnownRoots();
        try
        {
            string[] found = await Task.Run(() => RawChatSource.Accounts(known, cancellation.Token), cancellation.Token);
            accounts.Items.Clear(); accounts.Items.AddRange(found);
            accounts.Text = found.FirstOrDefault(p => p.Equals(current, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault() ?? current;
            status.Text = found.Length switch
            {
                0 => "未找到微信数据目录，请登录微信后重新识别，或手动选择 db_storage 文件夹。",
                1 => "已自动识别微信数据目录。保持微信登录，点击“读取会话列表”。",
                _ => $"已识别 {found.Length} 个账号目录，请从下拉列表选择，再读取会话列表。"
            };
        }
        catch (OperationCanceledException) { status.Text = "已取消，请关闭窗口后重试。"; }
        catch (Exception ex) { status.Text = "自动识别未完成，请手动选择目录。"; MessageBox.Show(this, ex.Message, "无法识别目录"); }
        finally { SetBusy(false); }
    }
    private void Populate()
    {
        populating = true; sessions.ResetRange(); sessions.BeginUpdate();
        try
        {
            sessions.Items.Clear();
            foreach (var item in all.Where(s => (s.DisplayName + s.Username).Contains(filter.Text, StringComparison.OrdinalIgnoreCase))) sessions.Items.Add(item, selected.Contains(item.Username));
        }
        finally { sessions.EndUpdate(); populating = false; UpdateSelection(); }
    }
    private async Task Connect()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            string root = accounts.Text.Trim();
            var progress = new Progress<string>(s => { if (!IsDisposed) status.Text = s; });
            var loaded = await Task.Run(() => recorder.Source(root).Sessions(s => ((IProgress<string>)progress).Report(s), cancellation.Token));
            SetSessions(loaded);
        }
        catch (OperationCanceledException) { status.Text = "已取消，请关闭窗口后重试。"; }
        catch (Exception ex) { status.Text = "连接未完成"; MessageBox.Show(this, ex.Message, "无法读取会话"); }
        finally { SetBusy(false); }
    }
    private void Save()
    {
        if (selected.Count == 0) { status.Text = "请至少勾选一个会话。"; return; }
        string root = Path.GetFullPath(accounts.Text.Trim());
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        long from = ((ImportRange)history.SelectedItem!).FromUnix(now);
        foreach (var session in all.Where(s => selected.Contains(s.Username)))
        {
            string id = RawChatSource.TargetIdFor(root, session.Username);
            Results.Add(new(id, session.DisplayName, "", 0,0,0,0,0,0,root,session.Username,from));
        }
        DialogResult = DialogResult.OK;
    }
}

internal sealed record ImportRange(string Label, int? Days)
{
    public override string ToString() => Label;
    internal long FromUnix(long now) => Days is int days ? days == 0 ? 0 : now - days * 86400L : now;
}
