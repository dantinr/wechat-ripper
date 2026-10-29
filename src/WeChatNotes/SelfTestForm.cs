using System.Text.Json;

namespace WeChatNotes;

// End-to-end test against a synthetic chat window, never the user's conversations.
internal sealed class SelfTestForm : Form
{
    private readonly List<object> results = [];
    private readonly string root = Path.Combine(Path.GetTempPath(), "WeChatNotesTests", Guid.NewGuid().ToString("N"));
    public SelfTestForm()
    {
        Text = "Ripper · 自动验证窗口"; Size = new(820, 560); StartPosition = FormStartPosition.Manual; Location = new(40, 60);
        Font = new("Microsoft YaHei UI", 17); BackColor = Color.FromArgb(245, 245, 245);
        Controls.Add(new Label { Text = "项目需求讨论群", AutoSize = true, Location = new(30, 30) });
        Controls.Add(new Label { Text = "产品经理", AutoSize = true, Location = new(30, 95), ForeColor = Color.DimGray });
        Controls.Add(new Label { Text = "需要增加订单导出功能", AutoSize = true, Location = new(30, 140), BackColor = Color.White, Padding = new Padding(12) });
        Controls.Add(new Label { Text = "支持按日期筛选", AutoSize = true, Location = new(30, 215), BackColor = Color.White, Padding = new Padding(12) });
        Controls.Add(new Label { Text = "请确认字段和验收标准", AutoSize = true, Location = new(30, 290), BackColor = Color.FromArgb(169, 235, 133), Padding = new Padding(12) });
        Shown += async (_, _) => await Run();
    }
    private void Check(string name, bool passed, string detail = "")
    {
        results.Add(new { name, passed, detail });
        if (!passed) throw new InvalidOperationException("测试失败：" + name + ": " + detail);
    }

    private async Task Run()
    {
        Directory.CreateDirectory(root);
        bool success = false;
        try
        {
            await Task.Delay(500);
            using var capture = new WindowCapture();
            using var image = await capture.CaptureAsync(Handle);
            Check("原生窗口捕获", image.Width > 600 && image.Height > 400, $"{image.Width}x{image.Height}");
            var ocr = new LocalOcr();
            string text = await ocr.ReadAsync(image);
            Check("本地中文 OCR", text.Contains("订单") && text.Contains("日期") && text.Contains("验收"), text);
            // Cover the target completely with another real window: no screen-coordinate fallback is allowed.
            using (var cover = new Form { Text = "遮挡验证", Bounds = Bounds, BackColor = Color.DarkBlue, ShowInTaskbar = false })
            {
                cover.Show(); await Task.Delay(300);
                using var hidden = await capture.CaptureAsync(Handle);
                string coveredText = await ocr.ReadAsync(hidden);
                Check("被其他窗口遮挡仍可采集", coveredText.Contains("订单") && coveredText.Contains("日期"), coveredText);
                cover.Close();
            }
            Check("重复画面不增加文本", TextDiff.Added("甲\n乙", "甲\n乙").Text == "");
            Check("滚动重叠保留新增", TextDiff.Added("甲\n乙\n丙", "乙\n丙\n丁").Text == "丁");
            Check("重复真实消息不会全局去重", TextDiff.Added("甲\n收到", "甲\n收到\n收到").Text == "收到");
            var gap = TextDiff.Added("甲\n乙", "丙\n丁");
            Check("无法对齐时保留整屏并标记", gap.Text == "丙\n丁" && gap.Note.Contains("无法对齐"));
            string id = Guid.NewGuid().ToString("N");
            var record = new CaptureRecord(id, "test-target", "测试需求群", DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)), text, text, Path.Combine("screenshots", id + ".png"), "合成测试数据");
            using (var store = new Store(root))
            {
                store.Add(record, image);
                store.SaveTarget(new("test-target", "测试需求群", Text, image.Width, image.Height, 0, 0, image.Width, image.Height));
            }
            using (var reopened = new Store(root))
            {
                Check("重启后记录和会话可恢复", reopened.Captures().Count == 1 && reopened.Targets().Count == 1);
                string export = Path.Combine(root, "export"); Exporter.Export(reopened, export);
                string exported = ExportTestDocument.Records(export).Single();
                Check("旧版 OCR 文字可以导出为 HTML", exported.Contains(System.Net.WebUtility.HtmlEncode(text)) || System.Net.WebUtility.HtmlDecode(exported).Contains(text));
                Check("导出原文保留来源锚点且不生成需求文档", !File.Exists(Path.Combine(export, "需求整理.md"))
                    && exported.Contains("<dd>" + id + "</dd>"));
                using (var recorder = new Recorder(reopened, () => [new(Handle, Text)]))
                {
                    string status = "";
                    recorder.Status += (_, value) => status = value;
                    await recorder.PollAsync(CancellationToken.None);
                    int baseline = reopened.Captures().Count;
                    await recorder.PollAsync(CancellationToken.None);
                    Check("实际轮询不重复保存静止画面", baseline == 2 && reopened.Captures().Count == baseline, status);
                    recorder.Resume();
                    await recorder.PollAsync(CancellationToken.None);
                    Check("暂停恢复记录采集中断", reopened.Captures().Count == baseline + 1 && reopened.Captures()[^1].Note.Contains("中断"));
                    WindowState = FormWindowState.Minimized; await Task.Delay(200);
                    await recorder.PollAsync(CancellationToken.None);
                    Check("最小化时暂停并提示", status.Contains("最小化") && reopened.Captures().Count == baseline + 1, status);
                    WindowState = FormWindowState.Normal; await Task.Delay(200);
                    await recorder.PollAsync(CancellationToken.None);
                    Check("窗口恢复后保留中断说明", reopened.Captures()[^1].Note.Contains("中断") && reopened.Captures().Count == baseline + 2);
                    Width += 30; await Task.Delay(200);
                    await recorder.PollAsync(CancellationToken.None);
                    Check("窗口尺寸变化时停止此会话采集", status.Contains("尺寸") && reopened.Captures().Count == baseline + 2, status);
                }
                reopened.RemoveTarget("test-target");
                Check("移除绑定保留历史", reopened.Targets().Count == 0 && reopened.Captures().Count >= 1);
            }
            success = true;
        }
        catch (Exception ex) { results.Add(new { name = "异常", passed = false, detail = ex.ToString() }); }
        finally
        {
            string report = Path.Combine(AppContext.BaseDirectory, "self-test-report.json");
            File.WriteAllText(report, JsonSerializer.Serialize(new { success, data = root, results }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Environment.ExitCode = success ? 0 : 1;
            Close();
        }
    }
}
