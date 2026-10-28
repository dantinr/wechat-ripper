namespace WeChatNotes;

internal sealed class SpeechModelDialog : Form
{
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox modelPath = new() { Dock = DockStyle.Fill, AccessibleName = "语音模型路径" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill };
    private readonly Button download = new() { Text = "自动下载（约 488 MB）", AutoSize = true };
    private readonly Button browse = new() { Text = "选择文件…", AutoSize = true };
    private readonly Button use = new() { Text = "保存并使用", AutoSize = true };
    private readonly Button cancel = new() { Text = "关闭", AutoSize = true };
    private readonly CancellationTokenSource cancellation = new();
    private bool busy;
    private bool closeRequested;
    internal Exception? Failure { get; private set; }

    internal SpeechModelDialog(string root)
    {
        Text = "模型设置"; ClientSize = new(720, 470); Font = new("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 1, RowCount = 7 };
        foreach (int height in new[] { 100, 110, 28, 40, 30, 55, 46 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "发布包不含识别模型，可选择提前下载的文件，或由程序自动下载。\nWhisper Small 多语言模型 · 支持中文 · 约 488 MB（466 MiB）\n设置路径后直接使用原文件，无需复制；识别全程离线，语音不上传。\n离线识别需要 Windows 11 或更新版本及 VC++ 2022 x64 运行库。" }, 0, 0);
        var settings = SpeechSettings.Load(root);
        var gpu = new CheckBox { Text = "启用 GPU 加速", Checked = settings.GpuEnabled, AutoSize = true, AccessibleName = "启用GPU加速", Enabled = SpeechModel.Supported };
        var parallel = new NumericUpDown { Minimum = 1, Maximum = 8, Value = settings.CpuParallelism, Width = 55, AccessibleName = "语音转写并发数", Enabled = SpeechModel.Supported };
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.Add(gpu); row.Controls.Add(new Label { Text = "CPU并发", AutoSize = true, Padding = new(16, 3, 0, 0) }); row.Controls.Add(parallel);
        options.Controls.Add(row);
        var optionStatus = new Label { AutoSize = true, MaximumSize = new(660, 0), ForeColor = Color.DimGray, Text = "启用时自动尝试 GPU（单路），不可用则回退 CPU；关闭时仅使用 CPU。\nGPU 和并发设置自动保存，对下次导出生效。" };
        options.Controls.Add(optionStatus); layout.Controls.Add(options, 0, 1);
        bool restoring = false;
        void SaveOptions()
        {
            if (restoring) return;
            try
            {
                var changed = new SpeechSettings(gpu.Checked, (int)parallel.Value);
                changed.Save(root); settings = changed;
                optionStatus.Text = "已自动保存，对下次导出生效。" + (settings.GpuEnabled ? "GPU 单路，不可用时回退 CPU。" : "GPU 已关闭，仅使用 CPU。");
                optionStatus.ForeColor = Color.DimGray;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                restoring = true; gpu.Checked = settings.GpuEnabled; parallel.Value = settings.CpuParallelism; restoring = false;
                optionStatus.Text = "设置未保存，已恢复原值。请检查数据目录权限或磁盘空间。";
                optionStatus.ForeColor = Color.Firebrick;
            }
        }
        gpu.CheckedChanged += (_, _) => SaveOptions(); parallel.ValueChanged += (_, _) => SaveOptions();
        layout.Controls.Add(new Label { Text = "模型文件路径", Dock = DockStyle.Fill }, 0, 2);
        var location = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        location.ColumnStyles.Add(new(SizeType.Percent, 100)); location.ColumnStyles.Add(new(SizeType.AutoSize));
        modelPath.Text = SpeechModel.PathFor(root);
        location.Controls.Add(modelPath, 0, 0); location.Controls.Add(browse, 1, 0); layout.Controls.Add(location, 0, 3);
        layout.Controls.Add(progress, 0, 4); layout.Controls.Add(status, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange([download, use, cancel]); layout.Controls.Add(buttons, 0, 6); Controls.Add(layout);
        status.Text = !SpeechModel.Supported ? "当前系统不支持离线识别，可继续导出文字与音频。" : SpeechModel.Exists(root) ? "已找到模型；保存并使用时会校验文件。" : "请选择已下载的 ggml-small.bin，或点击自动下载。";
        download.Enabled = browse.Enabled = modelPath.Enabled = use.Enabled = SpeechModel.Supported;
        use.Click += async (_, _) => await Prepare(false);
        cancel.Click += (_, _) => Close();
        download.Click += async (_, _) => await Prepare(true);
        browse.Click += (_, _) =>
        {
            using var file = new OpenFileDialog { Title = "选择已下载的 Whisper Small 多语言模型", Filter = "Whisper模型 (*.bin)|*.bin|所有文件 (*.*)|*.*", FileName = "ggml-small.bin" };
            if (file.ShowDialog(this) == DialogResult.OK) modelPath.Text = file.FileName;
        };
        modelPath.TextChanged += (_, _) => { if (!busy) status.Text = "点击“保存并使用”校验并记住路径，不会复制或移动模型文件。"; };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; closeRequested = true; cancellation.Cancel(); status.Text = "正在取消…"; } };
        FormClosed += (_, _) => cancellation.Dispose();

        async Task Prepare(bool automatic)
        {
            if (busy) return;
            busy = true; download.Enabled = browse.Enabled = use.Enabled = modelPath.Enabled = false; cancel.Text = "取消";
            Failure = null;
            var updates = new Progress<ExportProgress>(p =>
            {
                if (IsDisposed) return;
                progress.Style = p.Percent == null ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
                if (p.Percent is int percent) progress.Value = percent;
                status.Text = p.Total > 0 ? $"{p.Stage}：{p.Completed / 1048576.0:N1} / {p.Total / 1048576.0:N1} MiB（{p.Percent}%）" : p.Stage;
            });
            try
            {
                if (automatic)
                {
                    await SpeechModel.DownloadAsync(root, new ExportProgressReporter(updates), cancellation.Token);
                    modelPath.Text = SpeechModel.PathFor(root);
                }
                else
                {
                    string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(modelPath.Text.Trim().Trim('"')));
                    progress.Style = ProgressBarStyle.Marquee; status.Text = "正在校验模型文件…";
                    await SpeechModel.ValidateAsync(path, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    SpeechModel.SavePath(root, path);
                }
                DialogResult = DialogResult.OK;
            }
            catch (OperationCanceledException) { status.Text = "已取消，原有模型路径保持不变。"; }
            catch (Exception ex)
            {
                Failure = ex;
                progress.Style = ProgressBarStyle.Blocks; progress.Value = 0;
                status.Text = ex switch
                {
                    FileNotFoundException or DirectoryNotFoundException => "找不到模型文件，请点击“选择文件”重新指定 ggml-small.bin。",
                    InvalidDataException => "模型大小或 SHA256 校验不符，请选择完整的 Whisper Small 多语言模型。",
                    UnauthorizedAccessException => "没有权限读取模型或保存路径设置，请选择可读取的文件并检查数据目录权限。",
                    ArgumentException or NotSupportedException => "模型路径无效，请点击“选择文件”重新选择。",
                    HttpRequestException => "模型下载连接失败；已有模型可直接点击“保存并使用”，无需重复下载。",
                    IOException => "模型文件无法读取，或路径设置无法写入。请检查文件占用、磁盘空间和目录权限。",
                    _ => "模型设置未完成：" + ex.Message
                };
            }
            finally
            {
                busy = false; cancel.Text = "关闭";
                download.Enabled = browse.Enabled = modelPath.Enabled = use.Enabled = !cancellation.IsCancellationRequested && SpeechModel.Supported;
                if (closeRequested || DialogResult == DialogResult.OK) Close();
            }
        }
    }
}
