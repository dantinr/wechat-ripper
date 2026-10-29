using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class VoiceSelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string source = Path.Combine(root, "voice-account", "db_storage"), directory = Path.Combine(source, "message");
        Directory.CreateDirectory(directory);
        string first = Path.Combine(directory, "media_0.db"), second = Path.Combine(directory, "media_1.db");
        string chat = "synthetic-voice@chatroom", otherChat = "other-voice@chatroom";
        string target = RawChatSource.TargetIdFor(source, chat), otherTarget = RawChatSource.TargetIdFor(source, otherChat);
        byte[] audio = [2, .. "#!SILK_V3"u8.ToArray(), 3, 0, 10, 20, 30];
        byte[] otherAudio = [2, .. "#!SILK_V3"u8.ToArray(), 2, 0, 40, 50];
        var time = new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(8));
        string dataRoot = Path.Combine(root, "voice-store");
        using var store = new Store(dataRoot);
        store.SaveTarget(new(target, "语音测试群", "", 0, 0, 0, 0, 0, 0, source, chat));
        store.SaveTarget(new(otherTarget, "其他语音群", "", 0, 0, 0, 0, 0, 0, source, otherChat));
        CaptureRecord Voice(string id, long local, long server, int seconds = 0, string? selected = null) =>
            new(id, selected ?? target, selected == otherTarget ? "其他语音群" : "语音测试群", time, "<msg><voicemsg voicelength=\"1500\"/></msg>", "", "", "合成语音",
                new("message_0.db", local, server, "synthetic-sender", "合成发送人", time.AddSeconds(seconds), 34, "<msg><voicemsg/></msg>"));

        void Create(string path, long selectedId, long otherId)
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE Name2Id(user_name TEXT); CREATE TABLE VoiceInfo(chat_name_id INTEGER,create_time INTEGER,local_id INTEGER,svr_id INTEGER,voice_data BLOB,data_index TEXT); INSERT INTO Name2Id(rowid,user_name) VALUES($selected,$chat),($other,$otherChat)";
            command.Parameters.AddWithValue("$selected", selectedId); command.Parameters.AddWithValue("$chat", chat);
            command.Parameters.AddWithValue("$other", otherId); command.Parameters.AddWithValue("$otherChat", otherChat);
            command.ExecuteNonQuery();
        }
        void Insert(string path, long chatId, long local, long server, int seconds, byte[]? bytes)
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False"); db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO VoiceInfo VALUES($chat,$time,$local,$server,$bytes,'synthetic')";
            command.Parameters.AddWithValue("$chat", chatId); command.Parameters.AddWithValue("$time", time.AddSeconds(seconds).ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$local", local); command.Parameters.AddWithValue("$server", server);
            command.Parameters.AddWithValue("$bytes", (object?)bytes ?? DBNull.Value); command.ExecuteNonQuery();
        }
        SqliteConnection Open(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            try { return MemoryDatabase.Open(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        Dictionary<string, VoicePayload> Read(string account, IReadOnlyList<CaptureRecord> records) => RawVoiceSource.Read(account, records, [first, second], Open);
        void Export(string folder) => Exporter.Export(store, folder, target, Read);
        Create(first, 7, 8); Create(second, 90, 91);
        Insert(first, 8, 1, 101, 0, otherAudio); // A collision in another chat must not win.
        Insert(first, 7, 1, 101, 0, audio); Insert(first, 7, 2, 102, 0, otherAudio);
        Insert(first, 7, 30, 103, 3, audio); Insert(first, 7, 3, 999, 3, audio); Insert(first, 7, 3, 103, 99, audio);
        Insert(first, 7, 4, 104, 4, []);
        Insert(first, 7, 5, 105, 5, audio); Insert(second, 90, 5, 105, 5, otherAudio);
        Insert(first, 7, 6, 106, 6, audio); Insert(first, 7, 6, 106, 6, otherAudio);
        Insert(second, 90, 7, 107, 7, audio); Insert(second, 90, 8, 108, 8, [1, 2, 3]);
        Insert(second, 91, 20, 120, 20, otherAudio);
        var records = new[] { Voice("voice-1", 1, 101), Voice("voice-2", 2, 102), Voice("missing", 3, 103, 3), Voice("empty", 4, 104, 4),
            Voice("cross-shard-duplicate", 5, 105, 5), Voice("same-shard-duplicate", 6, 106, 6), Voice("second-shard", 7, 107, 7), Voice("unknown-format", 8, 108, 8),
            Voice("other-voice", 20, 120, 20, otherTarget) };
        store.AddMessages(records);
        var text = records[0] with { Id = "plain-voice-chat", Text = "语音附件随这条消息一起导出。", Message = records[0].Message! with { RawType = 1 } };
        store.AddMessages([text]);
        string[] before = [Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first))), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second)))];
        var found = Read(source, records);
        check("语音按会话及完整消息标识匹配，忽略其他会话的同号音频", found["voice-1"].Bytes!.SequenceEqual(audio));
        check("语音匹配同时核对本地编号服务器编号和发送时间", found["missing"].Bytes == null);
        check("各语音分库独立解析会话行号", found["second-shard"].Bytes!.SequenceEqual(audio));
        check("同库和跨库重复音频不猜测拼接或误导出", found["cross-shard-duplicate"].Bytes == null && found["same-shard-duplicate"].Bytes == null && found["cross-shard-duplicate"].Status.Contains("多条"));
        check("空语音保留未导出原因", found["empty"].Bytes == null && found["empty"].Status.Contains("为空"));
        foreach (var payload in found.Values) RawVoiceSource.Clear(payload);

        string folder = Path.Combine(root, "voice-export"); Export(folder);
        string dialogue = File.ReadAllText(Path.Combine(folder, "对话内容.md")), raw = ExportTestDocument.Html(folder);
        var links = Regex.Matches(dialogue, @"\]\(([^)]+)\)").Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).ToArray();
        string[] exported = links.Select(p => Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar))).ToArray();
        check("语音随当前聊天记录导出且 Markdown 链接有效", links.Length == 4 && links.All(p => p.StartsWith("语音/", StringComparison.Ordinal)) && exported.All(File.Exists));
        check("原始 SILK 语音完整保留微信前缀和音频字节", exported.Count(p => Path.GetExtension(p) == ".silk") == 3 && exported.Any(p => File.ReadAllBytes(p).SequenceEqual(audio)));
        check("同时发送的语音分目录保存互不覆盖", links[0] != links[1] && Path.GetFileName(links[0]) == Path.GetFileName(links[1]) && !File.ReadAllBytes(exported[0]).SequenceEqual(File.ReadAllBytes(exported[1])));
        check("未知语音格式以 bin 原样保存", exported.Any(p => Path.GetExtension(p) == ".bin" && File.ReadAllBytes(p).SequenceEqual(new byte[] { 1, 2, 3 })));
        check("未找到空音频和重复音频在对话中明确标注", dialogue.Contains("语音未导出") && dialogue.Contains("本地音频为空") && dialogue.Contains("匹配到多条音频") && !dialogue.Contains("<voicemsg"));
        string html = System.Net.WebUtility.HtmlDecode(raw);
        check("HTML 保留语音状态及消息原文", html.Contains("<voicemsg") && html.Contains("voice-1") && html.Contains("语音未导出"));
        check("当前聊天语音导出不混入其他会话", !dialogue.Contains("其他语音群") && !html.Contains("other-voice") && Directory.GetFiles(Path.Combine(folder, "语音"), "*", SearchOption.AllDirectories).Length == 4);
        check("语音导出不改变源媒体数据库", before[0] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first))) && before[1] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second))));

        byte[] temporary = [.. audio];
        var cleaner = new VoiceExporter(store, Path.Combine(root, "voice-cleanup"), (_, selected) => selected.ToDictionary(c => c.Id, _ => new VoicePayload(temporary, "已读取")));
        cleaner.Copy([records[0]]);
        check("语音写出后清理临时音频缓冲", temporary.All(b => b == 0));
        var collision = new VoiceExporter(store, folder, Read).Copy([records[0]]);
        check("已有同名导出文件不会被覆盖或误删除", collision["voice-1"].RelativePath == null && File.ReadAllBytes(exported[0]).SequenceEqual(audio));
        string failedFolder = Path.Combine(root, "voice-source-failure");
        Exporter.Export(store, failedFolder, target, (_, _) => throw new InvalidOperationException("synthetic-sensitive-diagnostic"));
        string failedDialogue = File.ReadAllText(Path.Combine(failedFolder, "对话内容.md"));
        check("语音库不可读时仍完成文字导出并提供不含私有诊断的说明", failedDialogue.Contains(text.Text) && failedDialogue.Contains("无法读取本地语音库") && !failedDialogue.Contains("synthetic-sensitive-diagnostic"));
        bool searched = false;
        var orphan = records[0] with { Id = "no-voice-source", TargetId = "unknown-account" };
        var orphanResult = new VoiceExporter(store, Path.Combine(root, "voice-orphan"), (_, _) => { searched = true; return []; }).Copy([orphan]);
        check("缺少语音来源时不会搜索其他账号或猜测匹配", !searched && orphanResult[orphan.Id].RelativePath == null && orphanResult[orphan.Id].Status.Contains("来源目录"));

        Insert(second, 90, 3, 103, 3, audio);
        string retry = Path.Combine(root, "voice-loaded-retry"); Export(retry);
        check("微信加载语音后重新导出可补上音频无需重新采集", Directory.GetFiles(Path.Combine(retry, "语音"), "*", SearchOption.AllDirectories).Length == 5);
        store.RemoveTarget(target);
        using var reopened = new Store(dataRoot);
        string historyFolder = Path.Combine(root, "voice-removed-history");
        Exporter.Export(reopened, historyFolder, target, Read);
        check("移除会话和重启后仍能从历史来源匹配语音", reopened.Targets().All(t => t.Id != target) && Directory.GetFiles(Path.Combine(historyFolder, "语音"), "*", SearchOption.AllDirectories).Length == 5);
        string malformed = Path.Combine(directory, "unsupported.db");
        using (var db = new SqliteConnection($"Data Source={malformed};Pooling=False")) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE Name2Id(user_name TEXT); INSERT INTO Name2Id VALUES($chat)"; command.Parameters.AddWithValue("$chat", chat); command.ExecuteNonQuery(); }
        var unsupported = new VoiceExporter(store, Path.Combine(root, "voice-unsupported"), (account, selected) => RawVoiceSource.Read(account, selected, [first, malformed], Open)).Copy([records[0]]);
        check("部分语音分库结构不兼容时停止该组语音导出", unsupported["voice-1"].RelativePath == null);
    }
}
