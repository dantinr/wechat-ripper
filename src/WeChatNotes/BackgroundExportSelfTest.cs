using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class BackgroundExportSelfTest
{
    private sealed class Observer(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress progress) => report(progress); }

    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "background-export-store"));
        CaptureRecord Record(string id, string target) => new(id, target, "合成会话 " + target, DateTimeOffset.Now,
            target + "正文", "", "", "合成后台导出测试");
        foreach (string id in new[] { "a", "b", "c" })
        {
            // Legacy targets never discover personal accounts or connect to WeChat.
            store.SaveTarget(new(id, "合成会话 " + id, "", 0, 0, 0, 0, 0, 0));
            store.AddMessages([Record(id + "-message", id)]);
        }
        store.AddMessages([Record("a-second", "a")]);
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        using var form = new MainForm(store) { Opacity = 0, ShowInTaskbar = false };
        var controls = Descendants(form).ToArray();
        var records = controls.OfType<ConversationRecords>().Single();
        var search = controls.OfType<TextBox>().Single(c => c.AccessibleName == "会话搜索");
        var targets = controls.OfType<DataGridView>().Single(c => c.AccessibleName == "记录会话");
        var exportButton = controls.OfType<Button>().Single(c => c.Text == "导出当前聊天记录");
        var start = controls.OfType<Button>().Single(c => c.Text == "开始记录");
        var once = controls.OfType<Button>().Single(c => c.Text == "立即采集");
        var view = controls.OfType<ExportProgressView>().Single();
        var stage = controls.OfType<Label>().Single(c => c.AccessibleName == "导出状态");
        var bar = controls.OfType<ProgressBar>().Single(c => c.AccessibleName == "导出进度");
        var open = controls.OfType<Button>().Single(c => c.Text == "打开目录");
        using var release = new ManualResetEventSlim();
        using var closeRelease = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        bool workerEnded = false, closedAfterWorker = false;
        form.FormClosed += (_, _) => closedAfterWorker = workerEnded;
        form.Shown += async (_, _) =>
        {
            try
            {
                records.SelectConversation("a"); start.PerformClick();
                int uiThread = Environment.CurrentManagedThreadId;
                bool background = false;
                string folder = Path.Combine(root, "background-export-output");
                var job = form.StartExportAsync("a", folder, export: progress =>
                {
                    background = Environment.CurrentManagedThreadId != uiThread;
                    using var reader = new Store(store.Root, readOnly: true);
                    Exporter.Export(reader, folder, "a", new Observer(update =>
                    {
                        progress.Report(update);
                        if (update.Stage == "整理消息内容" && update.Completed == 0)
                        {
                            started.TrySetResult();
                            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("合成导出未释放");
                        }
                    }));
                });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check("导出在后台执行且主界面保持可操作并延续原采集状态", background && !job.IsCompleted && form.Enabled && search.Enabled && !exportButton.Enabled && start.Text == "暂停记录" && form.OwnedForms.Length == 0);
                records.SelectConversation("b"); search.Text = "b正文";
                check("导出期间可切换会话搜索和查看消息来源", records.SelectedConversationId == "b" && records.VisibleCount == 1 && records.SelectedRecord?.TargetId == "b");
                var row = targets.Rows.Cast<DataGridViewRow>().Single(r => (string?)r.Tag == "c");
                var rectangle = targets.GetCellDisplayRectangle(1, row.Index, false);
                var menu = form.ShowTargetMenu(new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2));
                if (menu != null) { menu.Opacity = 0; menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "移除对话").PerformClick(); menu.Close(); }
                check("导出期间可使用会话右键菜单移除其他对话", menu != null && store.Targets().All(t => t.Id != "c") && !records.ConversationIds.Contains("c") && form.Enabled);
                start.PerformClick(); once.PerformClick();
                check("导出期间可暂停恢复及立即采集", start.Text == "开始记录" && once.Enabled && targets.Rows.Cast<DataGridViewRow>().All(r => r.Cells["status"].Value?.ToString()?.Contains("旧版画面绑定") == true));
                start.PerformClick();
                store.AddMessages([Record("late-message", "a")]);
                bool rejected = await Task.Run(() =>
                {
                    using var reader = new Store(store.Root, readOnly: true);
                    try { reader.RemoveTarget("b"); return false; }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 8) { return reader.Captures(targetId: "a").Count == 3; }
                });
                check("独立导出连接只读且主界面连接仍可写入和查询", rejected && store.Targets().Any(t => t.Id == "b") && store.Captures(targetId: "a").Count == 3);
                bool duplicateRan = false;
                bool duplicate = await form.StartExportAsync("b", Path.Combine(root, "must-not-export"), export: _ => duplicateRan = true);
                check("导出期间重复启动被拦截且切换会话不会重新启用导出按钮", !duplicate && !duplicateRan && !exportButton.Enabled);
                view.SetProgress(new("写入聊天记录", 25, 100));
                bool exact = bar.Style == ProgressBarStyle.Continuous && bar.Value == 25;
                view.SetProgress(new("读取本地语音库"));
                check("嵌入式进度显示真实百分比并支持未知数量步骤", exact && bar.Style == ProgressBarStyle.Marquee && stage.Text.Contains("合成会话 a"));
                var counts = controls.OfType<Label>().Single(c => c.AccessibleName == "导出完成条数");
                view.SetProgress(new("离线语音转文字", 0, 3));
                bool waiting = bar.Style == ProgressBarStyle.Marquee && bar.MarqueeAnimationSpeed > 0 && counts.Text.Contains("0 / 3") && counts.Text.Contains("识别中");
                view.SetProgress(new("离线语音转文字", 1, 3, "GPU（Vulkan）· 单路"));
                check("语音转写等待首条和后续结果时持续动画且保留真实条数", waiting && bar.Style == ProgressBarStyle.Marquee && counts.Text.Contains("1 / 3") && stage.Text.Contains("GPU（Vulkan）"));
                view.SetProgress(new("离线语音转文字", 3, 3));
                bool recognized = bar.Style == ProgressBarStyle.Continuous && bar.Value == 100 && counts.Text.Contains("3 / 3");
                view.SetProgress(new("写入聊天记录", 25, 100));
                check("语音转写结束及进入文档阶段恢复真实百分比", recognized && bar.Style == ProgressBarStyle.Continuous && bar.Value == 25);
                view.SetProgress(new("离线语音转文字", 1, 3, "GPU（Vulkan）· 单路"));
                form.PerformLayout();
                using (var preview = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(preview, new(Point.Empty, form.Size));
                    preview.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-background-export.png"));
                }
                release.Set();
                bool succeeded = await job;
                check("完成后保持当前会话和采集状态并提供目录入口且只导出开始时读取的消息", succeeded && ExportTestDocument.Records(folder).Length == 2 && File.Exists(Path.Combine(folder, "聊天记录.html")) && records.SelectedConversationId == "b" && start.Text == "暂停记录" && exportButton.Enabled && open.Enabled && stage.Text.Contains("导出完成") && form.OwnedForms.Length == 0);
                string blocked = Path.Combine(root, "background-export-failed"); File.WriteAllText(blocked, "合成文件占用导出目录");
                bool failed = await form.StartExportAsync("b", blocked);
                check("后台导出失败在主界面提示并恢复按钮且不弹出阻塞窗口", !failed && form.Enabled && exportButton.Enabled && stage.Text.Contains("导出失败") && bar is { Style: ProgressBarStyle.Continuous, Value: 0 } && form.OwnedForms.Length == 0);
                var closingJob = form.StartExportAsync("b", Path.Combine(root, "background-export-closing"), export: _ =>
                {
                    closeStarted.TrySetResult();
                    if (!closeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("合成关闭导出未释放");
                    workerEnded = true;
                });
                await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                form.Close(); await Task.Delay(50);
                check("关闭程序会等待正在运行的导出且不会提前释放主界面资源", !closingJob.IsCompleted && !form.IsDisposed && !closedAfterWorker);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                release.Set(); closeRelease.Set();
                if (failure != null) form.Close();
            }
        };
        form.ShowDialog();
        if (failure != null) throw new InvalidOperationException("后台导出界面验证失败", failure);
        check("导出结束后完成待处理的关闭请求", workerEnded && closedAfterWorker);
    }
}
