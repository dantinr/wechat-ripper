using System.Diagnostics;

namespace WeChatNotes;

internal sealed class ExportProgressView : UserControl
{
    private readonly Label stage = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "导出状态" };
    private readonly Label counts = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "导出完成条数" };
    private readonly Label elapsed = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Margin = new(0, 5, 12, 5), MarqueeAnimationSpeed = 30, AccessibleName = "导出进度" };
    private readonly Button open = new() { Text = "打开目录", AutoSize = true, Dock = DockStyle.Fill, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Stopwatch watch = new();
    private bool active;
    private string conversation = "";
    private string folder = "";

    internal ExportProgressView()
    {
        Dock = DockStyle.Fill; BackColor = Color.FromArgb(239, 246, 241);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(12, 5, 12, 5), ColumnCount = 3, RowCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.Absolute, 240)); layout.ColumnStyles.Add(new(SizeType.Absolute, 120));
        layout.RowStyles.Add(new(SizeType.Percent, 50)); layout.RowStyles.Add(new(SizeType.Percent, 50));
        layout.Controls.Add(stage, 0, 0); layout.SetColumnSpan(stage, 2); layout.Controls.Add(elapsed, 2, 0);
        layout.Controls.Add(bar, 0, 1); layout.Controls.Add(counts, 1, 1); layout.Controls.Add(open, 2, 1); Controls.Add(layout);
        timer.Tick += (_, _) => UpdateElapsed();
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
            catch (Exception ex) { counts.Text = "无法打开目录：" + ex.Message; }
        };
    }

    internal void Begin(string name, string path)
    {
        conversation = name; folder = path; active = true;
        stage.ForeColor = Color.FromArgb(24, 67, 51); open.Enabled = false;
        watch.Restart(); timer.Start(); SetProgress(new("准备导出"));
    }

    internal void SetProgress(ExportProgress progress)
    {
        if (!active || IsDisposed) return;
        stage.Text = $"正在导出「{progress.Conversation ?? conversation}」· {progress.Stage}" + (string.IsNullOrEmpty(progress.Detail) ? "" : " · " + progress.Detail);
        if (progress.Percent is int percent)
        {
            // Recognition may take minutes before any recording finishes; keep activity visible
            // without turning elapsed time into an invented completion percentage.
            bool recognizing = progress.Stage == "离线语音转文字" && progress.Completed < progress.Total;
            bar.Style = recognizing ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            bar.Value = percent;
            counts.Text = recognizing ? $"已完成 {progress.Completed:N0} / {progress.Total:N0} · 识别中…"
                : $"本步骤 {progress.Completed:N0} / {progress.Total:N0}（{percent}%）";
        }
        else
        {
            bar.Style = ProgressBarStyle.Marquee;
            counts.Text = "后台处理中，可继续其他操作";
        }
        UpdateElapsed();
    }

    internal void Complete(Exception? failure, BatchExportResult? batch = null)
    {
        active = false; timer.Stop(); watch.Stop();
        bar.Style = ProgressBarStyle.Continuous; bar.Value = failure == null ? 100 : 0;
        stage.Text = failure == null ? $"「{conversation}」导出完成" : $"「{conversation}」导出失败：{failure.Message}";
        stage.ForeColor = failure == null ? Color.FromArgb(24, 100, 65) : Color.Firebrick;
        counts.Text = failure == null ? "点击“打开目录”查看导出文件" : "可重试导出；目录可能包含未完成的文件";
        open.Enabled = Directory.Exists(folder); UpdateElapsed();
        if (failure == null && batch != null)
        {
            stage.Text = $"「{conversation}」导出结束 · {batch.Summary}";
            stage.ForeColor = batch.Failed == 0 ? Color.FromArgb(24, 100, 65) : Color.Firebrick;
            counts.Text = "打开目录查看“导出结果.txt”";
        }
    }

    private void UpdateElapsed() => elapsed.Text = $"用时 {(int)watch.Elapsed.TotalMinutes:00}:{watch.Elapsed.Seconds:00}";
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
