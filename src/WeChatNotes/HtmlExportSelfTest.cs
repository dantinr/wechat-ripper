using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WeChatNotes;

internal static class HtmlExportSelfTest
{
    private sealed class Observer(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress progress) => report(progress); }

    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "html-store"));
        string account = Path.Combine(root, "synthetic-周宁_123abc"), source = Path.Combine(account, "db_storage");
        string local = Path.Combine(account, "msg", "file"); Directory.CreateDirectory(local);
        store.SaveTarget(new("html", "产品讨论 · 合成演示", "", 0, 0, 0, 0, 0, 0, source, "synthetic@chatroom"));
        var time = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(8));
        CaptureRecord Record(string id, string text, int second, long type = 1, string sender = "林晓") =>
            new(id, "html", "产品讨论 · 合成演示", time.AddHours(1), text, "", "", "合成测试",
                new("message_0.db", second + 1, second + 100, "synthetic-" + sender, sender, time.AddSeconds(second), type, text));
        string FileXml(string name, int length) => new XElement("msg", new XElement("appmsg", new XElement("title", name),
            new XElement("type", 6), new XElement("appattach", new XElement("totallen", length)))).ToString(SaveOptions.DisableFormatting);
        string name = "计划 [v1] #1% & '最终'.txt";
        File.WriteAllText(Path.Combine(local, name), "这是一份用于验证本地文件链接的合成附件。", new UTF8Encoding(false));
        var records = new List<CaptureRecord>
        {
            Record("hello", "早上好，今天整理一下这周的讨论记录。\n文件和语音都放在同一份档案里。🙂", 0),
            Record("reply", "好的，已经确认。导出的聊天可以直接在浏览器里阅读。", 1, sender: "周宁"),
            Record("file", FileXml(name, (int)new FileInfo(Path.Combine(local, name)).Length), 2, 49),
            Record("voice", "<msg><voicemsg/></msg>", 3, 34, "周宁"),
            Record("missing", FileXml("尚未下载的附件.txt", 12345), 4, 49),
        };
        records.AddRange(Enumerable.Range(5, 51).Select(i => Record("text-" + i, $"合成消息 {i}：保留发送人、时间与来源，支持搜索和分页。", i)));
        string hostile = "</script><script>window.exportInjected=true</script><img src=x onerror=alert(1)> & \" '";
        records.Add(Record("hostile", hostile, 56, sender: hostile) with { Note = hostile, Message = Record("hostile", hostile, 56, sender: hostile).Message! with { SourceDatabase = hostile, GroupNickname = hostile } });
        records.Add(Record("excluded", "其他会话不应导出", 0) with { TargetId = "other" });
        store.AddMessages(records.AsEnumerable().Reverse());
        string folder = Path.Combine(root, "html-export");
        byte[] wave = new byte[44 + 3200];
        "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), 3200);
        var progress = new List<ExportProgress>();
        Exporter.Export(store, folder, "html", (_, selected) => selected.ToDictionary(r => r.Id, _ => new VoicePayload(wave.ToArray(), "合成音频")),
            new Observer(progress.Add), _ => new("明天下午三点开会，请把聊天记录整理好。", "已离线转写"));
        string html = File.ReadAllText(Path.Combine(folder, "聊天记录.html"));
        string decoded = WebUtility.HtmlDecode(html);
        check("HTML依据来源账号默认左右排列并预选自动识别且不依赖脚本初始化", Regex.Matches(html, "<article class=\"record self\"").Count == 2 && Regex.Matches(html, "data-own=\"true\"").Count == 2 && html.Contains("<option value=\"__auto\" selected>自动识别</option>"));
        check("HTML随聊天导出并按发送时间排列且隔离其他会话", Regex.Matches(html, "<article ").Count == 57 && decoded.IndexOf("早上好", StringComparison.Ordinal) < decoded.IndexOf("好的，已经确认", StringComparison.Ordinal) && !html.Contains("其他会话不应导出"));
        check("HTML将正文发送人来源与备注作为纯文字阻止脚本及属性注入", decoded.Contains(hostile) && !html.Contains("<img src=x") && !html.Contains("<script>window.exportInjected") && html.Contains("Content-Security-Policy"));
        string moved = Path.Combine(root, "移动后的中文目录 #100%"); Directory.Move(folder, moved);
        var links = Regex.Matches(html, "(?:href|src)=\"([^\"]+)\"").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Where(p => !p.StartsWith('#')).ToArray();
        check("HTML特殊字符文件和目录链接移动后仍有效且不依赖原机绝对路径", links.Length > 8 && links.All(p => File.Exists(Path.Combine(moved, Uri.UnescapeDataString(p))) || Directory.Exists(Path.Combine(moved, Uri.UnescapeDataString(p)))) && !html.Contains(root) && links.Any(p => p.Contains("%23") && p.Contains("%25")) && decoded.Contains(name));
        check("HTML提供文件定位相对路径及缺失附件说明", html.Contains("定位文件 ↗") && decoded.Contains("尚未下载的附件.txt") && html.Contains("附件未导出") && html.Contains("<code>附件/"));
        var fileIndex = JsonSerializer.Deserialize<ExportFileIndex>(File.ReadAllText(Path.Combine(moved, ExportFileLocator.IndexName)))!;
        check("HTML资源管理器链接使用可移动相对路径及导出索引并保留浏览目录", Guid.TryParseExact(fileIndex.Id, "N", out _) && fileIndex.Files.Length == 2 && fileIndex.Files.All(p => File.Exists(Path.Combine(moved, p))) && html.Contains("data-export-id=\"" + fileIndex.Id + "\"") && html.Contains("data-locate=\"") && html.Contains("ripper-locate://file/") && html.Contains("浏览目录 ↗") && !html.Contains(root));
        string voiceBubble = Regex.Match(html, "<article[^>]+data-kind=\"voice\"[\\s\\S]*?</article>").Value;
        check("HTML语音保留转写和播放器并移除文件卡片及转换说明", voiceBubble.Contains("<audio controls preload=none") && WebUtility.HtmlDecode(voiceBubble).Contains("明天下午三点开会") && voiceBubble.Contains("语音转写") && voiceBubble.Contains("原文与来源") && !voiceBubble.Contains("class=file") && !voiceBubble.Contains("class=hint") && store.Captures().All(c => c.VoiceTranscription == null));
        check("HTML全部写完后才报告导出完成并提供生成进度", progress.Any(p => p is { Stage: "生成 HTML 聊天目录", Completed: 57, Total: 57 }) && progress.Last().Stage == "导出完成" && html.EndsWith("</script></body></html>" + Environment.NewLine));
        string empty = Path.Combine(root, "html-empty"); Exporter.Export(store, empty, "absent");
        check("空会话仍生成可打开的HTML目录", File.ReadAllText(Path.Combine(empty, "聊天记录.html")).Contains("共 0 条记录") && !File.ReadAllText(Path.Combine(empty, "聊天记录.html")).Contains("<article "));
        string failed = Path.Combine(root, "html-failed"); Directory.CreateDirectory(Path.Combine(failed, "聊天记录.html"));
        progress.Clear(); bool rejected = false;
        try { Exporter.Export(store, failed, "absent", new Observer(progress.Add)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        check("HTML写入失败不会误报整体导出完成", rejected && progress.All(p => p.Stage != "导出完成"));
        var identityRecords = new[]
        {
            Record("identity-a", "合成", 0) with { TargetId = "a", Message = Record("identity-a", "合成", 0).Message! with { SenderId = "wxid_one", Sender = "同名用户" } },
            Record("identity-b", "合成", 0) with { TargetId = "b", Message = Record("identity-b", "合成", 0).Message! with { SenderId = "custom_account", Sender = "同名用户" } },
            Record("identity-legacy", "合成", 0) with { TargetId = "legacy" }
        };
        var identityRoots = new Dictionary<string, string>
        {
            ["a"] = Path.Combine(root, "wxid_one_123abc", "db_storage"),
            ["b"] = Path.Combine(root, "custom_account", "db_storage")
        };
        var owners = HtmlExporter.FindOwnSenders(identityRecords, identityRoots);
        check("HTML本人身份按会话隔离并兼容存储后缀和自定义账号且不使用昵称匹配", owners.Count == 2 && owners["a"] == "id:wxid_one" && owners["b"] == "id:custom_account");
        var ambiguous = identityRecords.Append(identityRecords[0] with { Id = "ambiguous", Message = identityRecords[0].Message! with { SenderId = "wxid_one_123abc" } }).ToArray();
        check("HTML来源账号不明或存在多个精确候选时保留手动选择而不猜测左右", !HtmlExporter.FindOwnSenders(ambiguous, identityRoots).ContainsKey("a") && !owners.ContainsKey("legacy") && HtmlExporter.FindOwnSenders(identityRecords, new Dictionary<string, string> { ["a"] = "\0" }).Count == 0);
        // Keep a self-contained, synthetic archive next to the report for file:// browser verification.
        string preview = Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-html");
        foreach (string file in Directory.EnumerateFiles(moved, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(preview, Path.GetRelativePath(moved, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
    }
}
