using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class ConversationSelfTest
{
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        byte[] Field(int number, byte[] value)
        {
            var bytes = new List<byte>();
            void Varint(int n) { while (n >= 128) { bytes.Add((byte)((n & 127) | 128)); n >>= 7; } bytes.Add((byte)n); }
            Varint((number << 3) | 2); Varint(value.Length); bytes.AddRange(value); return bytes.ToArray();
        }
        byte[] Member(string id, string nick) => Field(1, [..Field(1, Encoding.UTF8.GetBytes(id)), ..Field(2, Encoding.UTF8.GetBytes(nick)), 24, 1]);
        byte[] firstRoom = [..Member("alice", "产品·小林🙂"), ..Member("bob", ""), ..Field(9, "unknown"u8.ToArray())];
        check("群成员资料解析昵称并跳过未设置项和扩展字段", GroupNicknames.TryParse(firstRoom, out var parsed) && parsed.Count == 1 && parsed["alice"] == "产品·小林🙂");
        check("截断的群资料不生成部分或错误昵称", !GroupNicknames.TryParse([..firstRoom, 10, 127], out parsed) && parsed.Count == 0);
        check("非法 UTF-8 和超长整数被拒绝", !GroupNicknames.TryParse(Field(1, Field(2, [255])), out _) && !GroupNicknames.TryParse(Enumerable.Repeat((byte)255, 11).ToArray(), out _));
        using (var db = new SqliteConnection("Data Source=:memory:"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE chat_room(username TEXT,ext_buffer BLOB); INSERT INTO chat_room VALUES('a@chatroom',$a),('b@chatroom',$b)";
            command.Parameters.AddWithValue("$a", firstRoom); command.Parameters.AddWithValue("$b", Member("alice", "研发·小林")); command.ExecuteNonQuery();
            var names = GroupNicknames.Load(db, ["a@chatroom", "b@chatroom"]);
            check("同一发送人在不同群的昵称分别保存", names[("a@chatroom", "alice")] == "产品·小林🙂" && names[("b@chatroom", "alice")] == "研发·小林" && !names.ContainsKey(("a@chatroom", "bob")));
            check("仅读取指定群的成员昵称", GroupNicknames.Load(db, ["b@chatroom"]).Count == 1);
        }
        var old = JsonSerializer.Deserialize<RawMessageInfo>("""{"SourceDatabase":"message_0.db","LocalId":1,"ServerId":1,"SenderId":"alice","Sender":"联系人备注","SentAt":"2026-10-23T09:00:00+08:00","RawType":1,"RawContent":"旧版正文"}""")!;
        check("旧记录兼容缺失的群昵称字段", old.GroupNickname == null && old.Sender == "联系人备注");

        using var store = new Store(Path.Combine(root, "conversation-store"));
        Target Target(string id, string name) => new(id, name, "", 0, 0, 0, 0, 0, 0);
        store.SaveTarget(Target("a", "产品需求群")); store.SaveTarget(Target("b", "研发讨论群")); store.SaveTarget(Target("empty", "待采集会话"));
        CaptureRecord Record(string id, string target, string name, string text, string? nick) =>
            new(id, target, name, DateTimeOffset.Now, text, text, "", "合成演示数据", old with { GroupNickname = nick });
        var a = Record("a1", "a", "产品需求群", "导出文件需要包含需求来源。", "产品·小林🙂");
        var b = Record("b1", "b", "研发讨论群", "周五提供预览版本。", "研发·小林");
        store.AddMessages([a with { Message = old }, b]);
        check("再次同步补全群昵称且不复制原消息", store.AddMessages([a]) == 1 && store.Captures().Count == 2 && store.Captures(500, "a").Single().Message?.GroupNickname == "产品·小林🙂");
        store.AddMessages(Enumerable.Range(0, 505).Select(i => b with { Id = "burst-" + i }));
        check("活跃群超过 500 条时其他会话仍能独立查询", store.Captures(500, "a").Single().Id == "a1" && store.Captures(500, "b").Count == 500);

        using (var host = new Form { Width = 1000, Height = 600, Opacity = 0, ShowInTaskbar = false })
        using (var view = new ConversationRecords(store))
        {
            host.Controls.Add(view); host.Show();
            view.RefreshRecords();
            check("仅保留会话标签且自动选中首个会话", view.ConversationIds.ToHashSet().SetEquals(["a", "b", "empty"]) && view.SelectedConversationId == "a" && !view.SelectConversation(null));
            view.SelectConversation("a");
            check("群标签仅展示本群消息并显示发送人列", view.VisibleCount == 1 && view.SelectedRecord?.Id == "a1" && view.CurrentGrid!.Columns["sender"]!.HeaderText == "发送人" && (string?)view.CurrentGrid.Rows[0].Cells["sender"].Value == old.Sender);
            view.SelectConversation("b"); view.CurrentGrid!.CurrentCell = view.CurrentGrid.Rows[3].Cells[0];
            string? selected = view.SelectedRecord?.Id;
            view.SelectConversation("a"); view.SelectConversation("b");
            check("切换标签保留各自选中消息", selected != null && view.SelectedRecord?.Id == selected);
            store.AddMessages([b with { Id = "b-new" }]); view.RefreshRecords();
            check("采集刷新保留当前标签与选中消息来源", view.SelectedConversationId == "b" && view.SelectedRecord?.Id == selected);
            view.Search("产品·小林"); check("昵称搜索不混入其他会话", view.VisibleCount == 0 && view.SelectedRecord == null);
            view.SelectConversation("a"); check("群昵称可用于搜索", view.VisibleCount == 1 && view.SelectedRecord?.Id == "a1");
            view.Search(""); view.SelectConversation("empty"); check("空会话清空预览来源", view.VisibleCount == 0 && view.SelectedRecord == null);
            store.AddMessages([
                Record("sender-old", "empty", "待采集会话", "无群昵称的已有记录", null),
                Record("sender-id", "empty", "待采集会话", "无联系人名称的消息", null) with { Message = old with { Sender = " " } },
                Record("sender-system", "empty", "待采集会话", "未提供发送人资料的消息", null) with { Message = old with { Sender = "", SenderId = "" } }
            ]);
            view.RefreshRecords();
            var senderCells = view.CurrentGrid!.Rows.Cast<DataGridViewRow>().ToDictionary(row => ((CaptureRecord)row.Tag!).Id, row => (string?)row.Cells["sender"].Value);
            check("缺少群昵称的旧记录直接显示已保存发送人", senderCells["sender-old"] == old.Sender);
            check("发送人名称缺失时显示微信标识", senderCells["sender-id"] == old.SenderId);
            check("发送人资料均缺失时明确标注系统或未知", senderCells["sender-system"] == "未知发送人 / 系统");
            view.SelectConversation("a"); store.RemoveTarget("a"); view.RefreshRecords();
            check("移除绑定后保留历史标签及消息", view.SelectedConversationId == "a" && view.SelectedRecord?.Id == "a1");
        }
        store.SaveTarget(Target("a", "产品需求群"));
        string export = Path.Combine(root, "nickname-export"); Exporter.Export(store, export);
        string exported = ExportTestDocument.Html(export);
        check("HTML 和对话保留群昵称", System.Net.WebUtility.HtmlDecode(exported).Contains("产品·小林🙂") && File.ReadAllText(Path.Combine(export, "对话内容.md")).Contains(a.Message!.DisplaySender));
        store.AddMessages([b with { Id = "same-name-other-chat", TargetName = a.TargetName, Text = "同名但不同会话的消息" }]);
        string onlyA = Path.Combine(root, "current-a-export"); Exporter.Export(store, onlyA, "a");
        var aRecords = ExportTestDocument.Records(onlyA);
        check("当前会话导出按标识隔离同名会话及其他消息", aRecords.Length == 1 && aRecords[0].Contains("<dd>" + a.Id + "</dd>") && !aRecords[0].Contains("同名但不同会话的消息") && !aRecords[0].Contains("record-b1"));
        string onlyB = Path.Combine(root, "current-b-export"); Exporter.Export(store, onlyB, "b");
        var bRecords = ExportTestDocument.Records(onlyB);
        check("当前会话导出包含超过 500 条的全部已采集历史", bRecords.Length > 500 && bRecords.Length == store.Captures(targetId: "b").Count);
        check("每个会话导出根目录严格只保留指定四项", new[] { onlyA, onlyB }.All(folder => Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).ToHashSet().SetEquals(["聊天记录.html", "对话内容.md", "语音", "附件"]) && !ExportTestDocument.Html(folder).Contains("href=\"聊天记录.csv\"")));
        string emptyExport = Path.Combine(root, "empty-chat-export"); Exporter.Export(store, emptyExport, "no-messages");
        check("空会话导出不会回退到全部聊天或生成需求文档", ExportTestDocument.Records(emptyExport).Length == 0 && !ExportTestDocument.Html(emptyExport).Contains("record-a1") && !File.Exists(Path.Combine(emptyExport, "需求整理.md")));
        // Render only synthetic data, without opening or interacting with personal WeChat.
        using var form = new MainForm(store) { Opacity = 0, ShowInTaskbar = false };
        form.Show(); form.PerformLayout();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.ChangeExtension(report, ".png"), System.Drawing.Imaging.ImageFormat.Png);
        form.Close();
    }
}
