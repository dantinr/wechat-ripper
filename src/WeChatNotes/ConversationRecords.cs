namespace WeChatNotes;

internal sealed class ConversationRecords : UserControl
{
    private sealed class BrowseState
    {
        internal long Page = 1;
        internal string? SelectedId;
    }
    private readonly Store store;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true };
    private readonly ContextMenuStrip tabMenu = new();
    private readonly Dictionary<string, TabPage> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrowseState> states = new(StringComparer.Ordinal);
    private readonly BrowseState emptyState = new();
    private readonly Label empty = new() { Text = "暂无会话，请点击上方“添加会话”。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray };
    private readonly ComboBox pageSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 65 };
    private readonly NumericUpDown pageNumber = new() { Minimum = 1, Maximum = 1, Width = 65 };
    private readonly Label pageTotal = new() { AutoSize = true, Padding = new(0, 6, 0, 0) };
    private readonly Label summary = new() { AutoSize = true, Margin = new(3, 5, 3, 3) };
    private readonly Button first = PageButton("首页");
    private readonly Button previous = PageButton("上一页");
    private readonly Button next = PageButton("下一页");
    private readonly Button last = PageButton("末页");
    private bool updating;
    private string search = "";
    private BrowseState State => SelectedConversationId is string id ? states[id] : emptyState;

    internal event Action? SelectionChanged;
    internal event Action<string>? CloseRequested;
    internal CaptureRecord? SelectedRecord => CurrentGrid?.CurrentRow?.Tag as CaptureRecord;
    internal string? SelectedConversationId => tabs.SelectedTab?.Tag as string;
    internal long TotalCount { get; private set; }
    internal long MatchCount { get; private set; }
    internal long PageNumber => State.Page;
    internal long PageCount { get; private set; } = 1;
    internal int PageSize => (int)pageSize.SelectedItem!;
    internal bool IsFiltering => !string.IsNullOrWhiteSpace(search);
    internal int VisibleCount => CurrentGrid?.Rows.Count ?? 0;
    internal DataGridView? CurrentGrid => tabs.SelectedTab?.Controls.OfType<DataGridView>().Single();
    internal string[] ConversationIds => pages.Keys.ToArray();
    internal bool HasExportableConversations { get; private set; }

    internal ConversationRecords(Store store)
    {
        this.store = store; Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        var content = new Panel { Dock = DockStyle.Fill };
        content.Controls.Add(tabs); content.Controls.Add(empty);
        layout.Controls.Add(content, 0, 0); layout.Controls.Add(summary, 0, 1);
        var pager = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        pageSize.Items.AddRange([50, 100, 200, 500]); pageSize.SelectedItem = 100;
        pager.Controls.Add(new Label { Text = "每页", AutoSize = true, Padding = new(0, 6, 0, 0) }); pager.Controls.Add(pageSize);
        pager.Controls.Add(first); pager.Controls.Add(previous);
        pager.Controls.Add(new Label { Text = "第", AutoSize = true, Padding = new(0, 6, 0, 0) }); pager.Controls.Add(pageNumber); pager.Controls.Add(pageTotal);
        pager.Controls.Add(next); pager.Controls.Add(last);
        layout.Controls.Add(pager, 0, 2); Controls.Add(layout);
        tabs.SelectedIndexChanged += (_, _) => { if (!updating) LoadCurrent(); };
        tabMenu.Items.Add("关闭会话", null, (_, _) => { if (tabMenu.Tag is string id) CloseRequested?.Invoke(id); });
        tabs.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowConversationMenu(e.Location); };
        first.Click += (_, _) => GoToPage(1); previous.Click += (_, _) => GoToPage(PageNumber - 1);
        next.Click += (_, _) => GoToPage(PageNumber + 1); last.Click += (_, _) => GoToPage(PageCount);
        pageNumber.ValueChanged += (_, _) => { if (!updating) GoToPage((long)pageNumber.Value); };
        pageSize.SelectedIndexChanged += (_, _) => { ResetPages(); LoadCurrent(); };
        LoadCurrent();
    }

    internal ContextMenuStrip? ShowConversationMenu(Point location)
    {
        tabMenu.Close(); tabMenu.Tag = null;
        for (int i = 0; i < tabs.TabCount; i++)
        {
            if (!tabs.GetTabRect(i).Contains(location)) continue;
            tabMenu.Tag = tabs.TabPages[i].Tag; tabMenu.Show(tabs, location); return tabMenu;
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) tabMenu.Dispose();
        base.Dispose(disposing);
    }

    private static Button PageButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new(48, 29), Margin = new(3, 0, 3, 0), FlatStyle = FlatStyle.Flat, BackColor = Color.White };
    private void AddPage(string id, string name)
    {
        var page = new TabPage(name) { Tag = id, ToolTipText = name };
        var grid = MainForm.Grid();
        grid.Columns.Add("number", "序号");
        grid.Columns["number"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        grid.Columns["number"]!.MinimumWidth = 60;
        grid.Columns["number"]!.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.Columns.Add("time", "发送时间"); grid.Columns.Add("name", "会话");
        grid.Columns.Add("sender", "发送人"); grid.Columns.Add("text", "消息原文");
        grid.Columns["time"]!.FillWeight = 27; grid.Columns["name"]!.FillWeight = 22;
        grid.Columns["sender"]!.FillWeight = 25; grid.Columns["text"]!.FillWeight = 65;
        void Changed(object? sender, EventArgs e)
        {
            if (!updating && tabs.SelectedTab == page)
            { State.SelectedId = SelectedRecord?.Id; SelectionChanged?.Invoke(); }
        }
        grid.SelectionChanged += Changed; grid.CurrentCellChanged += Changed;
        states.Add(id, new()); page.Controls.Add(grid); pages.Add(id, page); tabs.TabPages.Add(page);
    }

    internal void RefreshRecords()
    {
        string selected = SelectedConversationId ?? "";
        var bound = store.Targets().ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal);
        var conversations = bound.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var recorded = store.CaptureConversations();
        HasExportableConversations = recorded.Count > 0;
        foreach (var (id, name) in recorded) conversations.TryAdd(id, name + "（已移除）");
        updating = true; tabs.SuspendLayout();
        try
        {
            foreach (string id in pages.Keys.Where(id => !conversations.ContainsKey(id)).ToArray())
            { var page = pages[id]; tabs.TabPages.Remove(page); pages.Remove(id); states.Remove(id); page.Dispose(); }
            foreach (var (id, name) in conversations)
            {
                if (!pages.TryGetValue(id, out var page)) AddPage(id, name);
                else { page.Text = name; page.ToolTipText = name; }
            }
            tabs.SelectedTab = pages.GetValueOrDefault(selected) ?? tabs.TabPages.Cast<TabPage>().FirstOrDefault();
        }
        finally { tabs.ResumeLayout(); updating = false; }
        LoadCurrent();
    }

    internal bool SelectConversation(string? id)
    {
        if (!pages.TryGetValue(id ?? "", out var page)) return false;
        tabs.SelectedTab = page; return true;
    }
    internal void Search(string text) { search = text; ResetPages(); LoadCurrent(); }
    private void ResetPages() { foreach (var state in states.Values) { state.Page = 1; state.SelectedId = null; } }
    internal void SetPageSize(int size)
    {
        if (!pageSize.Items.Contains(size)) throw new ArgumentOutOfRangeException(nameof(size));
        pageSize.SelectedItem = size;
    }
    internal void GoToPage(long number)
    {
        State.Page = Math.Clamp(number, 1, PageCount); State.SelectedId = null; LoadCurrent();
    }

    private void LoadCurrent()
    {
        empty.Visible = pages.Count == 0; tabs.Visible = pages.Count > 0;
        if (CurrentGrid is not { } grid)
        {
            TotalCount = MatchCount = 0; PageCount = State.Page = 1; State.SelectedId = null;
            UpdatePager(); SelectionChanged?.Invoke(); return;
        }
        string? selected = State.SelectedId;
        var result = store.CapturePage(PageSize, State.Page, SelectedConversationId, search, selected);
        TotalCount = result.TotalCount; MatchCount = result.MatchCount; PageCount = result.PageCount; State.Page = result.PageNumber;
        updating = true;
        try
        {
            grid.Rows.Clear();
            foreach (var item in result.Records)
            {
                long number = (PageNumber - 1) * PageSize + grid.Rows.Count + 1;
                int index = grid.Rows.Add(number, (item.Message?.SentAt ?? item.ObservedAt).ToString("MM-dd HH:mm:ss"), item.TargetName,
                    item.Message?.DisplaySender ?? "旧版 OCR（无发送人信息）", item.AddedText.ReplaceLineEndings(" "));
                grid.Rows[index].Tag = item;
            }
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as CaptureRecord)?.Id == selected)
                ?? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (row != null) { grid.CurrentCell = row.Cells[0]; row.Selected = true; }
            State.SelectedId = SelectedRecord?.Id;
            UpdatePager();
        }
        finally { updating = false; }
        SelectionChanged?.Invoke();
    }

    private void UpdatePager()
    {
        bool wasUpdating = updating; updating = true;
        try
        {
            pageNumber.Maximum = PageCount; pageNumber.Value = PageNumber; pageTotal.Text = $"/ {PageCount:N0} 页";
            first.Enabled = previous.Enabled = MatchCount > 0 && PageNumber > 1;
            next.Enabled = last.Enabled = MatchCount > 0 && PageNumber < PageCount; pageNumber.Enabled = MatchCount > 0;
            long start = VisibleCount == 0 ? 0 : (PageNumber - 1) * PageSize + 1;
            long end = VisibleCount == 0 ? 0 : start + VisibleCount - 1;
            summary.Text = $"共 {TotalCount:N0} 条" + (IsFiltering ? $" · 匹配 {MatchCount:N0} 条" : "") + $" · 显示 {start:N0}–{end:N0} 条";
        }
        finally { updating = wasUpdating; }
    }
}
