namespace WeChatNotes;

internal sealed class FocusDialog : Form
{
    private readonly DataGridView rules = MainForm.Grid();
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button choose = new() { Text = "选择微信群", AutoSize = true };
    private readonly Button calibrate = new() { Text = "校准会话列表", AutoSize = true };
    private readonly Button start = new() { Text = "开始隐藏", AutoSize = true };
    private readonly Button stop = new() { Text = "停止并恢复", AutoSize = true };
    private readonly Button retry = new() { Text = "重试暂停", AutoSize = true };
    private bool busy;

    internal FocusDialog(Store store, FocusController controller)
    {
        Text = "微信会话隐藏（实验）"; Size = new(940, 600); MinimumSize = new(820, 520);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 5 };
        foreach (var row in new RowStyle[] { new(SizeType.Absolute, 110), new(SizeType.Absolute, 45), new(SizeType.Percent, 100), new(SizeType.Absolute, 40), new(SizeType.Absolute, 55) }) layout.RowStyles.Add(row);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "普通消息出现时自动再次隐藏；收到新的 @本人 或 @所有人 后放行，直到你重新开始。\n支持微信在后台隐藏，不抢焦点、不移动鼠标；最小化、名称截断或校准失效时暂缓。\n先选群，再校准列表；窗口大小和行高需保持不变。关闭本设置窗口会继续运行。\n停止或正常退出 Ripper 时，通过微信搜索打开本次隐藏的群来恢复显示，可能清除该群未读数。" }, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        tools.Controls.AddRange([choose, calibrate, start, retry, stop]); layout.Controls.Add(tools, 0, 1);
        rules.AccessibleName = "会话隐藏规则";
        rules.Columns.Add("name", "微信群"); rules.Columns.Add("status", "隐藏状态"); rules.Columns["status"]!.FillWeight = 180;
        layout.Controls.Add(rules, 0, 2); layout.Controls.Add(status, 0, 3);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, Text = "实验限制：隐藏时不自动滚动或搜索。请让完整群名位于微信列表显示区域；菜单可能短暂出现。\n临时变化会自动重试；暂停的规则可点“重试暂停”。停止恢复会将微信置于前台并使用鼠标。" }, 0, 4);
        Controls.Add(layout);
        void RefreshState()
        {
            if (IsDisposed || Disposing) return;
            rules.Rows.Clear();
            foreach (var target in controller.Settings.Targets.Concat(controller.PendingTargets).DistinctBy(t => t.Id))
                rules.Rows.Add(target.Name, controller.Rules.FirstOrDefault(r => r.Target.Id == target.Id)?.Status ??
                    controller.RecoveryStatus.GetValueOrDefault(target.Id) ?? (controller.PendingTargets.Any(t => t.Id == target.Id) ? "待恢复（上次未完成），请点击停止并恢复" : "未启动"));
            status.Text = (controller.Running ? "正在运行 · " : "已停止 · ") + (controller.Settings.Calibration?.Valid == true ? "已有校准" : "尚未校准") + $" · 待恢复 {controller.PendingCount} 个群";
            choose.Enabled = calibrate.Enabled = start.Enabled = !busy && !controller.Running && controller.PendingCount == 0;
            stop.Enabled = !busy && (controller.Running || controller.PendingCount > 0);
            retry.Enabled = !busy && controller.Running && !controller.Working && controller.Rules.Any(r => r.Blocked);
        }
        controller.Changed += RefreshState;
        FormClosed += (_, _) => controller.Changed -= RefreshState;
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        async Task Execute(Func<Task> action)
        {
            busy = true; RefreshState();
            try { await action(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "会话隐藏设置", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            finally { busy = false; RefreshState(); }
        }
        choose.Click += async (_, _) => await Execute(() =>
        {
            using var reader = new RawRecorder(store);
            using var dialog = new RawBindDialog(reader, focusMode: true);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (dialog.Results.Count > 20) throw new InvalidOperationException("实验版最多选择 20 个群。");
                controller.Configure(controller.Settings with { Targets = dialog.Results.ToArray() });
            }
            return Task.CompletedTask;
        });
        calibrate.Click += async (_, _) => await Execute(async () =>
        {
            using var image = await controller.CaptureAsync(CancellationToken.None);
            using var dialog = new FocusCalibrationDialog(image);
            if (dialog.ShowDialog(this) == DialogResult.OK)
                controller.Configure(controller.Settings with { Calibration = dialog.Result });
        });
        start.Click += async (_, _) => await Execute(controller.StartAsync);
        retry.Click += (_, _) => controller.RetryPaused();
        stop.Click += async (_, _) => await Execute(async () =>
        {
            await controller.StopAsync();
            if (controller.PendingCount > 0) MessageBox.Show(this, $"还有 {controller.PendingCount} 个群待恢复。请保持微信登录，清空微信搜索框后重试；也可手动搜索打开这些群，再点击停止并恢复。待恢复清单已保存在本机。", "会话尚未恢复");
        });
        RefreshState();
    }
}

internal sealed class FocusCalibrationDialog : Form
{
    internal FocusCalibration? Result { get; private set; }
    internal FocusCalibrationDialog(Bitmap image)
    {
        Text = "校准微信会话列表"; Size = new(1100, 880); MinimumSize = new(800, 650); StartPosition = FormStartPosition.CenterParent;
        Font = new("Microsoft YaHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.Absolute, 82)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "在下方微信画面中，拖框选中左侧第一条完整会话的整行：\n左边从列表边缘开始，右边到列表右边缘，包含头像、会话名和下方摘要，底边到下一行开始处。\n请先把微信会话列表滚动到顶部，再来校准；不要框整个列表、搜索框或右侧聊天内容。" }, 0, 0);
        var preview = new CropPreview { Dock = DockStyle.Fill, Image = image }; layout.Controls.Add(preview, 0, 1);
        var save = new Button { Text = "保存校准", AutoSize = true, Enabled = false };
        var message = new Label { AutoSize = true, Padding = new(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill }; buttons.Controls.AddRange([save, message]); layout.Controls.Add(buttons, 0, 2);
        preview.SelectionChanged += () =>
        {
            var r = preview.Selection; Result = new(r.X, r.Y, r.Width, r.Height, image.Width, image.Height);
            save.Enabled = Result.Valid; message.Text = save.Enabled ? $"单行区域：{r.Width} × {r.Height}" : "请框选左侧一条完整会话行";
        };
        save.Click += (_, _) => DialogResult = DialogResult.OK; Controls.Add(layout);
    }
}
