using System.Diagnostics;

namespace WeChatNotes;

public sealed class MainForm : Form
{
    private readonly Store store;
    private readonly RawRecorder recorder;
    private readonly System.Windows.Forms.Timer timer = new();
    private CancellationTokenSource cancellation = new();
    private readonly DataGridView targets = Grid();
    private readonly ContextMenuStrip targetMenu = new();
    private readonly AccountProfileView accountProfile = new();
    private readonly ConversationRecords captures;
    private readonly TextBox search = new() { Width = 285, PlaceholderText = "搜索当前标签的全部已采集消息", AccessibleName = "会话搜索" };
    private readonly TextBox detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly Button locateFile;
    private readonly Label fileStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray };
    private bool locating;
    private readonly Label badge = new() { AutoSize = true, ForeColor = Color.FromArgb(85, 99, 113), Text = "已暂停", Padding = new Padding(10) };
    private readonly Label footer = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(85, 99, 113) };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 60, Value = 30, Width = 55 };
    private readonly Button start;
    private readonly Button once;
    private readonly Button exportCurrent;
    private readonly ExportProgressView exportProgress = new();
    private readonly RowStyle exportProgressRow = new(SizeType.Absolute, 0);
    private readonly CheckBox transcribeVoices = new() { Text = "语音转文字（离线）", AutoSize = true, Checked = SpeechModel.Supported, Enabled = SpeechModel.Supported, Padding = new(8, 6, 0, 0) };
    private bool exporting;
    private bool preparingExport;
    private Task? activeExport;
    private bool closingConversation;
    private bool readingConversation;
    private bool closing;
    private bool allowClose;

    public MainForm(Store store)
    {
        this.store = store; recorder = new(store); captures = new(store);
        Text = $"Ripper v{Application.ProductVersion}"; Width = 1380; Height = 940; MinimumSize = new(1060, 760);
        StartPosition = FormStartPosition.CenterScreen; Font = new("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(246, 248, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 10), ColumnCount = 1, RowCount = 6 };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 90), new(SizeType.Absolute, 46), new(SizeType.Absolute, 135), new(SizeType.Absolute, 48), new(SizeType.Percent, 100), new(SizeType.Absolute, 40) }) layout.RowStyles.Add(style);
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "Ripper", Location = new(0, 0), AutoSize = true, Font = new("Microsoft YaHei UI", 24, FontStyle.Bold), ForeColor = Color.FromArgb(24, 41, 57) });
        header.Controls.Add(new Label { Text = $"v{Application.ProductVersion} · 保留聊天原文，让每一条记录都有来源。", Location = new(3, 52), AutoSize = true, ForeColor = Color.FromArgb(95, 110, 125) });
        badge.Location = new(450, 12); header.Controls.Add(badge); layout.Controls.Add(header, 0, 0);
        accountProfile.Dock = DockStyle.Right; header.Controls.Add(accountProfile);
        header.Resize += (_, _) => badge.MaximumSize = new(Math.Max(80, header.ClientSize.Width - accountProfile.Width - badge.Left - 16), 64);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.Add(Button("添加会话", AddTarget, true));
        start = Button("开始记录", Toggle, true); toolbar.Controls.Add(start);
        once = Button("立即采集", async (_, _) => await Poll()); toolbar.Controls.Add(once);
        toolbar.Controls.Add(new Label { Text = "间隔（秒）", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }); toolbar.Controls.Add(interval);
        toolbar.Controls.Add(Button("打开数据目录", (_, _) => OpenFolder(store.Root)));
        toolbar.Controls.Add(Button("使用说明", (_, _) => MessageBox.Show(this,
            "1. 保持 Windows 微信登录。\n2. 点击“添加会话”→“读取会话列表”，勾选群或联系人，可选择首次导入范围。\n3. 点击“开始记录”，默认每 30 秒同步一次。窗口可最小化，无需切换聊天。\n4. 选择会话和消息，查看原文及来源；文件消息可点击“定位文件”查找本机附件。\n5. 选择具体会话标签，点击“导出当前聊天记录”，生成该会话的 HTML、Markdown、CSV、JSONL，并导出本地文件附件和语音。\n\n通过只读连接本地聊天数据库获取原文，不使用 OCR。完整解密数据库只存在于内存中，只保存选定会话的消息。\n已下载的普通文件附件和本地原始语音可在导出时一并保存，并在 Markdown 中添加链接。SILK 语音自动转换为可播放的 WAV，同时保留原始文件；勾选“语音转文字（离线）”后，识别文字会随聊天记录导出，首次请在“设置 → 模型设置”选择已有模型路径或自动下载模型；导出语音时请保持微信登录。图片和视频仅保留文字说明 / XML。只读取已经同步到本机的消息。\n关闭程序、电脑休眠或微信退出期间停止同步；重启后点击开始可补采仍在本地的消息。\n微信升级可能需要适配。", "使用说明")));
        layout.Controls.Add(toolbar, 0, 1);
        targets.Columns.Add("number", "会话编号");
        targets.Columns["number"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        targets.Columns["number"]!.MinimumWidth = 90;
        targets.Columns["number"]!.SortMode = DataGridViewColumnSortMode.NotSortable;
        targets.Columns.Add("name", "记录会话"); targets.Columns.Add("window", "记录起点"); targets.Columns.Add("status", "采集状态");
        targets.Columns["name"]!.FillWeight = 25; targets.Columns["window"]!.FillWeight = 25; targets.Columns["status"]!.FillWeight = 70;
        targets.AccessibleName = "记录会话";
        targetMenu.Items.Add("读取记录", null, async (_, _) => { if (targetMenu.Tag is string id) await ReadConversation(id); });
        targetMenu.Items.Add("搜索", null, (_, _) => { if (targetMenu.Tag is string id) SearchConversation(id); });
        targetMenu.Items.Add("移除对话", null, (_, _) => { if (targetMenu.Tag is string id) CloseConversation(id); });
        targets.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowTargetMenu(e.Location); };
        layout.Controls.Add(targets, 0, 2);
        var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        filter.Controls.Add(search);
        filter.Controls.Add(new Label { Text = "原始消息文字 · 保留发送人、发送时间与消息编号", AutoSize = true, Padding = new Padding(8, 7, 0, 0), ForeColor = Color.DimGray });
        layout.Controls.Add(filter, 0, 3);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var recordsTab = new TabPage("采集记录");
        var recordsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        recordsLayout.RowStyles.Add(new(SizeType.Absolute, 44)); recordsLayout.RowStyles.Add(new(SizeType.Percent, 100));
        recordsLayout.RowStyles.Add(exportProgressRow);
        recordsLayout.Controls.Add(exportProgress, 0, 2);
        var recordsToolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 3, 0, 0) };
        exportCurrent = Button("导出当前聊天记录", Export); exportCurrent.Enabled = false;
        recordsToolbar.Controls.Add(exportCurrent); recordsLayout.Controls.Add(recordsToolbar, 0, 0);
        recordsToolbar.Controls.Add(transcribeVoices);
        recordsTab.Controls.Add(recordsLayout);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1200, 500), SplitterDistance = 700, Panel1MinSize = 350, Panel2MinSize = 300 };
        split.Panel1.Controls.Add(captures);
        var previewTabs = new TabControl { Dock = DockStyle.Fill };
        var textTab = new TabPage("消息原文 / 来源"); textTab.Padding = new Padding(12);
        var previewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        previewLayout.RowStyles.Add(new(SizeType.Absolute, 42)); previewLayout.RowStyles.Add(new(SizeType.Absolute, 44)); previewLayout.RowStyles.Add(new(SizeType.Percent, 100));
        var fileToolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        locateFile = Button("定位文件", LocateFile); locateFile.Enabled = false;
        fileToolbar.Controls.Add(locateFile); previewLayout.Controls.Add(fileToolbar, 0, 0);
        previewLayout.Controls.Add(fileStatus, 0, 1); previewLayout.Controls.Add(detail, 0, 2);
        textTab.Controls.Add(previewLayout); previewTabs.TabPages.Add(textTab);
        split.Panel2.Controls.Add(previewTabs); recordsLayout.Controls.Add(split, 0, 1);
        tabs.TabPages.Add(recordsTab); layout.Controls.Add(tabs, 0, 4);
        layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        MainMenuStrip = new AppMenu(
            () => { using var model = new SpeechModelDialog(store.Root); model.ShowDialog(this); },
            () => { using var about = new AboutDialog(); about.ShowDialog(this); }) { Font = Font, BackColor = BackColor };
        Controls.Add(MainMenuStrip);
        interval.ValueChanged += (_, _) => timer.Interval = (int)interval.Value * 1000;
        timer.Interval = (int)interval.Value * 1000; timer.Tick += async (_, _) => await Poll();
        search.TextChanged += (_, _) => captures.Search(search.Text);
        captures.SelectionChanged += () =>
        {
            ShowCapture(); UpdateFooter();
            UpdateAccountProfile();
            UpdateExportButton();
        };
        captures.CloseRequested += CloseConversation;
        recorder.Status += (id, message) =>
        {
            foreach (DataGridViewRow row in targets.Rows) if ((string?)row.Tag == id) row.Cells["status"].Value = message;
        };
        recorder.Recorded += RefreshRecords;
        FormClosing += ClosingAsync;
        FormClosed += (_, _) => { targetMenu.Dispose(); timer.Dispose(); cancellation.Dispose(); recorder.Dispose(); };
        Shown += (_, _) => split.SplitterDistance = Math.Clamp((int)(split.ClientSize.Width * .58), split.Panel1MinSize, split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth);
        Shown += (_, _) => UpdateAccountProfile();
        RefreshTargets(); RefreshRecords();
    }

    internal static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        AutoGenerateColumns = false, RowTemplate = { Height = 32 }, AllowUserToOrderColumns = false,
        ColumnHeadersHeight = 36, EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(232, 238, 243), ForeColor = Color.FromArgb(45, 62, 79) },
        DefaultCellStyle = new DataGridViewCellStyle { SelectionBackColor = Color.FromArgb(219, 242, 233), SelectionForeColor = Color.FromArgb(20, 67, 51), ForeColor = Color.FromArgb(40, 52, 65), Padding = new Padding(5) }
    };
    private static Button Button(string text, EventHandler handler, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(17, 127, 91) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(45, 62, 79), Padding = new Padding(7, 0, 7, 0) };
        button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(212, 220, 227); button.Click += handler; return button;
    }
    private void RefreshTargets()
    {
        targets.Rows.Clear(); foreach (var item in store.Targets())
        {
            int row = targets.Rows.Add(targets.Rows.Count + 1, item.Name, item.SourceRoot == null ? "旧版画面绑定" : item.FromUnix == 0 ? "全部本地历史" : DateTimeOffset.FromUnixTimeSeconds(item.FromUnix).ToLocalTime().ToString("MM-dd HH:mm"), item.SourceRoot == null ? "历史绑定 · 请添加原文会话" : "待开始 · 保持微信登录"); targets.Rows[row].Tag = item.Id;
        }
    }
    internal ContextMenuStrip? ShowTargetMenu(Point location)
    {
        targetMenu.Close(); targetMenu.Tag = null;
        if (closing || closingConversation || readingConversation) return null;
        var hit = targets.HitTest(location.X, location.Y);
        if (hit.Type != DataGridViewHitTestType.Cell || hit.RowIndex < 0 || targets.Rows[hit.RowIndex].Tag is not string id) return null;
        targets.CurrentCell = targets.Rows[hit.RowIndex].Cells[hit.ColumnIndex];
        targets.Rows[hit.RowIndex].Selected = true;
        targetMenu.Tag = id; targetMenu.Show(targets, location); return targetMenu;
    }
    private void SearchConversation(string id)
    {
        if (closing || closingConversation || readingConversation || !captures.SelectConversation(id)) return;
        targetMenu.Close();
        search.Focus(); search.SelectAll();
    }
    private async Task ReadConversation(string id)
    {
        if (closing || closingConversation || readingConversation || !store.Targets().Any(t => t.Id == id)) return;
        bool wasRecording = timer.Enabled;
        readingConversation = true; Enabled = false;
        try
        {
            await PauseAndDrain();
            if (IsDisposed || closing) return;
            search.Clear(); RefreshRecords(); captures.SelectConversation(id);
            cancellation.Dispose(); cancellation = new();
            badge.Text = "正在读取所选会话…";
            await recorder.PollAsync(cancellation.Token, id);
            if (IsDisposed || closing) return;
            RefreshRecords();
            badge.Text = "所选会话读取结束 · 详情见采集状态";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed && !closing) MessageBox.Show(this, ex.Message, "无法读取记录"); }
        finally
        {
            readingConversation = false;
            if (!IsDisposed && !closing)
            {
                if (wasRecording && store.Targets().Count > 0) ResumeRecording();
                Enabled = true;
            }
        }
    }
    private void RefreshRecords()
    {
        captures.RefreshRecords();
        UpdateFooter();
    }
    private void UpdateAccountProfile()
    {
        if (!IsHandleCreated || closing || IsDisposed) return;
        string? id = captures.SelectedConversationId;
        string? root = id == null ? null : store.Targets().FirstOrDefault(t => t.Id == id)?.SourceRoot ?? store.TargetSources().GetValueOrDefault(id);
        _ = accountProfile.ShowAccountAsync(root);
    }
    private void UpdateFooter() => footer.Text = $"当前标签共 {captures.TotalCount:N0} 条消息 · 本页 {captures.VisibleCount} 条 · 本机数据：{store.Root}";
    private void ShowCapture()
    {
        var item = captures.SelectedRecord;
        detail.Text = item == null ? "添加会话并开始记录后，这里显示原始消息。\r\n选择一条消息即可查看原文和来源。" : item.Message is { } message ?
            $"{item.TargetName}\r\n发送人：{message.DisplaySender}\r\n群昵称：{message.GroupNickname ?? "—（未设置或本机未同步）"}\r\n发送于 {message.SentAt:yyyy-MM-dd HH:mm:ss zzz}\r\n记录于 {item.ObservedAt:yyyy-MM-dd HH:mm:ss zzz}\r\n消息类型：{RawChatSource.TypeName(message.RawType)}\r\n消息编号：{message.ServerId} / 本地 {message.LocalId}\r\n来源：{message.SourceDatabase}\r\n\r\n【消息原文】\r\n{item.Text}\r\n\r\n{item.Note}".ReplaceLineEndings(Environment.NewLine) :
            $"{item.TargetName}\r\n旧版 OCR 记录，采集于 {item.ObservedAt:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n{item.Note}\r\n\r\n{item.Text}".ReplaceLineEndings(Environment.NewLine);
        bool hasFile = item != null && MessageContent.Read(item).File != null;
        locateFile.Enabled = hasFile && !locating;
        fileStatus.Text = hasFile ? "定位此消息在本机已下载的文件附件。" : "选择文件消息后，可定位本机已下载的附件。";
    }
    private async void AddTarget(object? sender, EventArgs e)
    {
        await PauseAndDrain();
        try
        {
            using var dialog = new RawBindDialog(recorder);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var existing = store.Targets().Select(t => t.Id).ToHashSet();
                foreach (var target in dialog.Results.Where(t => !existing.Contains(t.Id))) store.SaveTarget(target);
                RefreshTargets(); RefreshRecords();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法添加会话"); }
    }
    private async void Toggle(object? sender, EventArgs e)
    {
        if (timer.Enabled) { await PauseAndDrain(); return; }
        if (store.Targets().Count == 0) { MessageBox.Show(this, "请先选择需要记录的群或联系人。", "尚未绑定会话"); return; }
        ResumeRecording();
        await Poll();
    }

    private void ResumeRecording()
    {
        cancellation.Dispose(); cancellation = new(); recorder.Resume();
        timer.Start(); start.Text = "暂停记录"; once.Enabled = false; badge.Text = "正在记录"; badge.ForeColor = Color.FromArgb(17, 127, 91);
    }

    private async void CloseConversation(string id)
    {
        if (closing || closingConversation || readingConversation) return;
        bool wasRecording = timer.Enabled;
        closingConversation = true; Enabled = false;
        try
        {
            await PauseAndDrain();
            if (IsDisposed || closing) return;
            store.CloseConversation(id); RefreshTargets(); RefreshRecords();
            if (wasRecording && store.Targets().Count > 0) ResumeRecording();
        }
        catch (Exception ex) { if (!IsDisposed && !closing) MessageBox.Show(this, ex.Message, "无法关闭会话"); }
        finally { closingConversation = false; if (!IsDisposed && !closing) Enabled = true; }
    }
    private async Task Poll()
    {
        if (closing || closingConversation || readingConversation || recorder.IsBusy) return;
        if (store.Targets().Count == 0) { badge.Text = "请先添加会话"; return; }
        if (cancellation.IsCancellationRequested) { cancellation.Dispose(); cancellation = new(); recorder.Resume(); }
        try { await recorder.PollAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { timer.Stop(); start.Text = "开始记录"; once.Enabled = true; badge.Text = "记录已暂停：" + ex.Message; }
    }
    private async Task PauseAndDrain()
    {
        timer.Stop(); cancellation.Cancel(); start.Text = "开始记录"; once.Enabled = true; badge.Text = "已暂停"; badge.ForeColor = Color.DimGray;
        while (recorder.IsBusy) await Task.Delay(50);
        foreach (DataGridViewRow row in targets.Rows) row.Cells["status"].Value = "已暂停 · 点击开始记录可恢复";
    }
    private async void LocateFile(object? sender, EventArgs e)
    {
        if (locating || captures.SelectedRecord is not { } record || MessageContent.Read(record).File is not { } file) return;
        locating = true; locateFile.Enabled = false; locateFile.Text = "正在定位…"; fileStatus.Text = "正在查找本地文件…";
        try
        {
            var finder = new AttachmentExporter(store, store.Root);
            var result = await Task.Run(() => finder.Locate(record, file));
            if (IsDisposed || closing || captures.SelectedRecord?.Id != record.Id) return;
            fileStatus.Text = result.Status;
            if (result.FullPath is { } path && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                fileStatus.Text = "已在资源管理器中定位本地文件。";
            }
            else
            {
                if (result.FullPath != null) fileStatus.Text = "文件已移动或删除。";
                MessageBox.Show(this, fileStatus.Text + "\n请先在微信下载文件后重试。", "无法定位文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !closing && captures.SelectedRecord?.Id == record.Id)
            { fileStatus.Text = "定位文件失败"; MessageBox.Show(this, ex.Message, "无法定位文件"); }
        }
        finally
        {
            locating = false;
            if (!IsDisposed)
            { locateFile.Text = "定位文件"; locateFile.Enabled = captures.SelectedRecord is { } selected && MessageContent.Read(selected).File != null; }
        }
    }
    private async void Export(object? sender, EventArgs e)
    {
        if (exporting || preparingExport || closing) return;
        string? targetId = captures.SelectedConversationId;
        if (targetId == null) { MessageBox.Show(this, "请先切换到需要导出的群或联系人标签。", "选择当前会话"); return; }
        string conversationName = ConversationName(targetId);
        preparingExport = true; UpdateExportButton();
        try
        {
            string root = store.Root;
            bool transcribe = transcribeVoices.Checked;
            if (transcribe && !SpeechModel.Exists(root))
            {
                bool hasVoice = await Task.Run(() => { using var reader = new Store(root, readOnly: true); return reader.HasVoiceRecords(targetId); });
                if (IsDisposed || closing) return;
                if (hasVoice)
                {
                    using var model = new SpeechModelDialog(root);
                    if (model.ShowDialog(this) != DialogResult.OK) return;
                }
            }
            var transcription = transcribe ? SpeechSettings.Load(root).Options(root) : null;
            using var dialog = new FolderBrowserDialog { Description = "选择导出位置，将在其中新建以聊天窗口名称命名的文件夹；同名时自动编号", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string folder = ExportDirectory.Create(dialog.SelectedPath, conversationName);
            await StartExportAsync(targetId, folder, transcription);
        }
        catch (Exception ex) { if (!IsDisposed && !closing) MessageBox.Show(this, ex.Message, "无法开始导出"); }
        finally { preparingExport = false; if (!IsDisposed) UpdateExportButton(); }
    }

    private void UpdateExportButton() => exportCurrent.Enabled = captures.SelectedConversationId != null && captures.TotalCount > 0 && !exporting && !preparingExport && !closing;

    private string ConversationName(string targetId) => store.Targets().FirstOrDefault(t => t.Id == targetId)?.Name
        ?? store.CaptureConversations().FirstOrDefault(c => c.Id == targetId).Name ?? "聊天记录";

    internal Task<bool> StartExportAsync(string targetId, string folder, VoiceTranscriptionOptions? transcription = null,
        Action<IProgress<ExportProgress>>? export = null)
    {
        if (exporting || closing) return Task.FromResult(false);
        string root = store.Root;
        string name = ConversationName(targetId);
        exporting = true;
        UpdateExportButton(); exportProgressRow.Height = 78;
        exportProgress.Begin(name, folder);
        var progress = new Progress<ExportProgress>(exportProgress.SetProgress);
        var task = Run(); activeExport = task; return task;

        async Task<bool> Run()
        {
            Exception? failure = null;
            try
            {
                await Task.Run(() =>
                {
                    if (export != null) export(progress);
                    else
                    {
                        // This connection belongs only to the export worker. UI reads and recording keep their own connection.
                        using var reader = new Store(root, readOnly: true);
                        Exporter.Export(reader, folder, targetId, progress, transcription);
                    }
                });
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                exporting = false;
                if (!IsDisposed) { exportProgress.Complete(failure); UpdateExportButton(); }
            }
            return failure == null;
        }
    }
    private static void OpenFolder(string folder) => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return;
        closing = true; Enabled = false;
        if (exporting) footer.Text = "正在等待后台导出完成，随后退出…";
        await PauseAndDrain();
        if (activeExport != null) await activeExport;
        allowClose = true; Close();
    }
}
