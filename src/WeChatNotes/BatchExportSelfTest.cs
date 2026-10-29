using System.Text.Json;

namespace WeChatNotes;

internal static class BatchExportSelfTest
{
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "batch-export-store"));
        Target Target(string id, string name) => new(id, name, "", 0, 0, 0, 0, 0, 0);
        CaptureRecord Record(string id, string target, string name) => new(id, target, name, DateTimeOffset.Now, id + "正文", id + "正文", "", "合成批量导出");
        store.SaveTarget(Target("a", "产品/讨论")); store.SaveTarget(Target("b", "产品/讨论")); store.SaveTarget(Target("empty", "空会话"));
        store.AddMessages(Enumerable.Range(0, 105).Select(i => Record("a-" + i, "a", "产品/讨论")));
        store.AddMessages([Record("b-1", "b", "产品/讨论"), Record("history-1", "history", "历史会话"), Record("closed-1", "closed", "已关闭会话")]);
        store.CloseConversation("closed");
        using var form = new MainForm(store) { Opacity = 0, ShowInTaskbar = false };
        var controls = Descendants(form).ToArray();
        var records = controls.OfType<ConversationRecords>().Single();
        var search = controls.OfType<TextBox>().Single(c => c.AccessibleName == "会话搜索");
        var all = controls.OfType<Button>().Single(c => c.Text == "导出全部会话");
        var current = controls.OfType<Button>().Single(c => c.Text == "导出当前聊天记录");
        var stage = controls.OfType<Label>().Single(c => c.AccessibleName == "导出状态");
        var open = controls.OfType<Button>().Single(c => c.Text == "打开目录");
        var view = controls.OfType<ExportProgressView>().Single();
        using var release = new ManualResetEventSlim();
        using var closeRelease = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? failure = null;
        bool closedAfterBatch = false, batchFinished = false;
        form.FormClosed += (_, _) => closedAfterBatch = batchFinished;
        form.Shown += async (_, _) =>
        {
            try
            {
                records.SelectConversation("empty"); search.Text = "不匹配任何消息";
                check("当前空会话或搜索无结果时仍可导出其他全部会话", !current.Enabled && all.Enabled);
                int uiThread = Environment.CurrentManagedThreadId;
                bool background = false, readOnly = false;
                string folder = Path.Combine(root, "batch-export-output");
                var job = form.StartAllExportAsync(folder, export: (reader, destination, id, progress) =>
                {
                    background = Environment.CurrentManagedThreadId != uiThread;
                    if (id == "a")
                    {
                        try { reader.RemoveTarget("empty"); }
                        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 8) { readOnly = true; }
                        progress.Report(new("离线语音转文字", 0, 3)); started.TrySetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("合成批量导出未释放");
                    }
                    Exporter.Export(reader, destination, id, progress);
                });
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check("全部会话后台导出使用独立只读连接且两个导出入口互斥", background && readOnly && !job.IsCompleted && form.Enabled && !all.Enabled && !current.Enabled);
                view.SetProgress(new("离线语音转文字", 0, 3, "CPU · 4 路", "1/3 · 产品/讨论"));
                check("批量进度显示当前会话序号且语音识别保持动画", stage.Text.Contains("1/3 · 产品/讨论") && controls.OfType<ProgressBar>().Single(c => c.AccessibleName == "导出进度").Style == ProgressBarStyle.Marquee);
                records.SelectConversation("b"); search.Text = "b-1";
                store.CloseConversation("b");
                store.SaveTarget(Target("late", "新添加会话")); store.AddMessages([Record("late-1", "late", "新添加会话")]);
                records.RefreshRecords();
                bool duplicateBatch = await form.StartAllExportAsync(Path.Combine(root, "batch-duplicate"));
                bool duplicateSingle = await form.StartExportAsync("a", Path.Combine(root, "single-duplicate"));
                check("批量期间可搜索管理会话且拒绝重复批量和单会话导出", form.Enabled && search.Enabled && !duplicateBatch && !duplicateSingle && !all.Enabled && !current.Enabled);
                form.Width = 1060;
                using (var preview = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(preview, new(Point.Empty, form.Size));
                    preview.Save(Path.ChangeExtension(report, ".batch-export.png"));
                    var voice = controls.OfType<CheckBox>().Single(c => c.Text == "语音转文字（离线）");
                    check("最小窗口宽度完整显示两个导出按钮及语音选项", voice.Right <= voice.Parent!.ClientSize.Width && all.Right <= all.Parent!.ClientSize.Width);
                }
                release.Set(); bool succeeded = await job;
                string first = Path.Combine(folder, "产品_讨论"), second = Path.Combine(folder, "产品_讨论 (2)");
                CaptureRecord[] Read(string path) => File.ReadAllLines(Path.Combine(path, "聊天记录.jsonl")).Select(line => JsonSerializer.Deserialize<CaptureRecord>(line)!).ToArray();
                check("全部导出不受搜索分页影响且同名会话独立保存", succeeded && Read(first).Length == 105 && Read(first).All(c => c.TargetId == "a") && Read(second).Single().TargetId == "b" && Directory.GetDirectories(folder).Length == 3);
                check("导出范围保留开始时会话列表并排除空会话和此前关闭会话", File.Exists(Path.Combine(folder, "历史会话", "聊天记录.html")) && !Directory.Exists(Path.Combine(folder, "新添加会话")) && !Directory.Exists(Path.Combine(folder, "空会话")) && !Directory.Exists(Path.Combine(folder, "已关闭会话")));
                check("批量完成汇总成功数并恢复按钮及目录入口", stage.Text.Contains("成功 3 个会话，失败 0") && all.Enabled && open.Enabled && File.ReadAllText(Path.Combine(folder, "导出结果.txt")).Contains("成功 3 个会话，失败 0"));

                string partialFolder = Path.Combine(root, "batch-partial-output");
                bool partial = await form.StartAllExportAsync(partialFolder, export: (reader, destination, id, progress) =>
                {
                    if (id == "a") throw new IOException("合成单会话失败");
                    Exporter.Export(reader, destination, id, progress);
                });
                check("单个会话失败继续其余导出并准确提示部分完成", !partial && File.Exists(Path.Combine(partialFolder, "新添加会话", "聊天记录.html")) && File.Exists(Path.Combine(partialFolder, "历史会话", "聊天记录.html")) && stage.Text.Contains("成功 2 个会话，失败 1") && all.Enabled && open.Enabled);
                check("导出结果清单记录失败会话原因及成功目录", File.ReadAllText(Path.Combine(partialFolder, "导出结果.txt")).Contains("合成单会话失败") && File.ReadAllText(Path.Combine(partialFolder, "导出结果.txt")).Contains("目录：历史会话"));
                string blocked = Path.Combine(root, "batch-blocked"); File.WriteAllText(blocked, "合成同名文件");
                bool failed = await form.StartAllExportAsync(blocked);
                check("批量目录无法创建时恢复操作并显示失败", !failed && all.Enabled && stage.Text.Contains("导出失败"));

                var closing = form.StartAllExportAsync(Path.Combine(root, "batch-closing"), export: (_, _, _, _) =>
                {
                    closeStarted.TrySetResult();
                    if (!closeRelease.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("合成关闭未释放");
                    batchFinished = true;
                });
                await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                form.Close(); await Task.Delay(50);
                check("退出等待整个批量导出完成", !closing.IsCompleted && !form.IsDisposed && !closedAfterBatch);
            }
            catch (Exception ex) { failure = ex; }
            finally { release.Set(); closeRelease.Set(); if (failure != null) form.Close(); }
        };
        form.ShowDialog();
        if (failure != null) throw new InvalidOperationException("全部会话导出验证失败", failure);
        check("批量结束后完成待处理退出", closedAfterBatch);

        using var empty = new Store(Path.Combine(root, "batch-empty-store"));
        using var emptyForm = new MainForm(empty);
        check("没有已采集消息时禁用全部会话导出", !Descendants(emptyForm).OfType<Button>().Single(c => c.Text == "导出全部会话").Enabled && !emptyForm.StartAllExportAsync(Path.Combine(root, "batch-must-not-exist")).Result && !Directory.Exists(Path.Combine(root, "batch-must-not-exist")));
    }
}
