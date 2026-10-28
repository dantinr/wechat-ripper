using System.Text.Json;

namespace WeChatNotes;

internal static class FocusSelfTest
{
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        string data = Path.Combine(root, "focus-tests");
        var target = new Target("test-group", "测试群", "", 0, 0, 0, 0, 0, 0, Path.Combine(data, "db_storage"), "group@chatroom");
        CaptureRecord Record(string id, string sender, string[]? mentions, string? otherTarget = null) => new(id, otherTarget ?? target.Id, target.Name,
            DateTimeOffset.UtcNow, "@同名昵称", "", "", "", new("message_0.db", 7, 0, sender, "同名昵称", DateTimeOffset.FromUnixTimeSeconds(100), 1, "正文", null, mentions));
        check("提及元数据按账号解析并去重", MentionMetadata.Read("<msgsource><atuserlist><![CDATA[wxid_me, wxid_other,wxid_me]]></atuserlist></msgsource>")!.SequenceEqual(new[] { "wxid_me", "wxid_other" }));
        check("无提及与损坏的提及元数据区分", MentionMetadata.Read("") is { Length: 0 } && MentionMetadata.Read("<msgsource><other>@本人</other></msgsource>") is { Length: 0 } &&
            MentionMetadata.Read("<broken>") == null && MentionMetadata.Read("<message><atuserlist>me</atuserlist></message>") == null);
        check("提及元数据拒绝DTD及超限内容", MentionMetadata.Read("<!DOCTYPE msgsource [<!ENTITY a SYSTEM 'file:///missing'>]><msgsource>&a;</msgsource>") == null && MentionMetadata.Read(new string('x', 1024 * 1024 + 1)) == null);
        check("损坏UTF8提及不影响正文读取", MentionMetadata.Decode(new byte[] { 0xff }) == null);
        var old = new FocusRuleState(target, "wxid_me", [Record("old", "other", ["wxid_me"])]);
        old.Observe([Record("old", "other", ["wxid_me"]), Record("new", "other", [])]);
        check("启动前旧@不触发且正文昵称不触发", !old.Released && !old.Blocked);
        old.Observe([Record("different", "other", ["wxid_someone"]), Record("own", "wxid_me", ["notify@all"]), Record("cross", "other", ["wxid_me"], "another-target")]);
        check("仅本群他人@本人或所有人触发", !old.Released && !old.Blocked);
        old.Observe([Record("mention", "other", ["wxid_me"])]); old.Observe([Record("ordinary", "other", [])]);
        check("新@本人放行并保持直到重新开始", old.Released && !old.Blocked);
        var all = new FocusRuleState(target, "wxid_me", []); all.Observe([Record("all", "other", ["notify@all"])]);
        var unknown = new FocusRuleState(target, "wxid_me", []); unknown.Observe([Record("unknown", "other", null)]);
        check("@所有人放行且未知元数据暂停隐藏", all.Released && unknown.Blocked && !unknown.Released);
        unknown.Retry(); unknown.Observe([Record("unknown", "other", null)]);
        check("重试暂停不会跳过尚未解析的提及消息", unknown.Blocked && !unknown.Released);
        unknown.Retry(); unknown.Observe([Record("unknown", "other", ["wxid_me"])]);
        check("重试后补齐元数据的@仍会放行", unknown.Released && !unknown.Blocked);
        all.Retry(); all.Observe([Record("later", "other", [])]);
        check("重试暂停保留已经放行的群", all.Released && !all.Blocked);
        var retry = new FocusRuleState(target, "wxid_me", [Record("old", "other", ["wxid_me"])]);
        retry.Block("temporary"); retry.Retry(); retry.Observe([Record("old", "other", ["wxid_me"])]);
        check("重试暂停保留旧消息基线且允许再次执行", !retry.Blocked && !retry.Released && retry.Stamp == null);
        retry.Defer("timeout");
        check("短暂读取失败稍后重试而非永久暂停", !retry.Blocked && retry.RetryAfter > DateTimeOffset.UtcNow && retry.Status.Contains("自动重试"));
        var attempted = DateTimeOffset.UtcNow; retry.AttemptedStamp = "stamp"; retry.AttemptedAt = attempted;
        check("隐藏落库延迟给予五秒确认时间", retry.Confirming(attempted.AddSeconds(2)) && !retry.Confirming(attempted.AddSeconds(6)));
        var state = new FocusSnapshot(false, "stamp", 100, 7, "other");
        check("落库尚未覆盖会话最新消息时禁止隐藏", state.CoveredBy([Record("latest", "other", [])]) && !state.CoveredBy([Record("wrong", "else", [])]) && !state.CoveredBy([]));
        var calibration = new FocusCalibration(60, 80, 240, 65, 1100, 905);
        check("校准限制左侧单行区域并保留名称带", calibration.Valid && !(calibration with { Height = 400 }).Valid && !(calibration with { X = 600 }).Valid && FocusCalibration.NameBand(calibration.Row(0)).Bottom < calibration.Row(0).Bottom);
        using (var scrolled = new Bitmap(calibration.WindowWidth, calibration.WindowHeight))
        using (var graphics = Graphics.FromImage(scrolled))
        using (var separator = new Pen(Color.FromArgb(221, 221, 222)))
        {
            graphics.Clear(Color.FromArgb(230, 230, 232));
            // First row is clipped by scrolling 37 pixels; its old name band now
            // points at a summary. Selected rows may lack their own separators.
            for (int top = 108; top < 890; top += 65) graphics.DrawLine(separator, 139, top - 1, 287, top - 1);
            graphics.FillRectangle(Brushes.SeaGreen, 60, 497, 240, 66);
            var rows = ConversationRows.Locate(scrolled, calibration);
            check("列表滚动半行后按画面重新定位完整会话行", rows.Length > 5 && rows[0].Top == 108 && rows.All(r => (r.Top - 108) % 65 == 0));
            check("选中行遮住分隔线仍保留该群标题区域", rows.Contains(new Rectangle(60, 498, 240, 65)) && FocusCalibration.NameBand(rows.Single(r => r.Top == 498)).Top > 498);
            check("校准行高少量偏差由重复分隔线修正", ConversationRows.Locate(scrolled, calibration with { Height = 63 }).SequenceEqual(rows));
            graphics.Clear(Color.FromArgb(230, 230, 232));
            using (var halo = new Pen(Color.FromArgb(227, 227, 229)))
            {
                for (int top = 108; top < 890; top += 65)
                {
                    graphics.DrawLine(separator, 139, top - 1, 287, top - 1);
                    foreach (int offset in new[] { -5, -3, 3, 5 }) graphics.DrawLine(halo, 139, top - 1 + offset, 287, top - 1 + offset);
                }
                check("同一分隔线的多重细边缘不干扰会话行定位", ConversationRows.Locate(scrolled, calibration).SequenceEqual(rows));
                check("边缘合并后仍能校正少量校准行高误差", ConversationRows.Locate(scrolled, calibration with { Height = 63 }).SequenceEqual(rows));
                graphics.Clear(Color.FromArgb(230, 230, 232));
                foreach (int top in new[] { 108, 173 })
                {
                    graphics.DrawLine(separator, 139, top - 1, 287, top - 1);
                    foreach (int offset in new[] { -5, -3, 3, 5 }) graphics.DrawLine(halo, 139, top - 1 + offset, 287, top - 1 + offset);
                }
                check("重复边缘不能把两条分隔线伪装成足够的行证据", ConversationRows.Locate(scrolled, calibration).Length == 0);
            }
            graphics.Clear(Color.FromArgb(230, 230, 232));
            graphics.DrawLine(separator, 139, 107, 287, 107); graphics.DrawLine(separator, 139, 172, 287, 172);
            check("不足三条分隔线不猜测会话行或误用消息摘要", ConversationRows.Locate(scrolled, calibration).Length == 0);
            for (int top = 238; top < 890; top += 65) graphics.DrawLine(separator, 139, top - 1, 287, top - 1);
            for (int top = 120; top < 890; top += 65) graphics.DrawLine(separator, 139, top - 1, 287, top - 1);
            check("两套行位置证据冲突时不点击", ConversationRows.Locate(scrolled, calibration).Length == 0);
        }
        LocalOcr.Line Line(string text, int y, int x = 170) => new(text, new(x, y, 100, 20));
        var menu = new[] { Line("独立窗口显示", 350), Line("不显示", 385), Line("删除", 425) };
        check("隐藏菜单必须同时核对上下邻项", WeChatListAutomation.MenuTarget(menu, new(150, 220)) is { Y: 395 } && WeChatListAutomation.MenuTarget(menu.Take(2).ToArray(), new(150, 220)) == null &&
            WeChatListAutomation.MenuTarget([..menu, Line("不显示", 389)], new(150, 220)) == null);
        check("仅菜单邻项允许实测口囗OCR差异", WeChatListAutomation.MenuTarget([Line("独立窗囗显示", 350), Line("不显示", 385), Line("删除", 425)], new(150, 220)) is { Y: 395 } &&
            !WeChatListAutomation.SameName("窗口", "窗囗"));
        var results = new[] { Line("测试群", 50, 100), Line("群聊", 85, 80), Line("测试群", 130, 130), Line("聊天记录", 180, 80), Line("测试群", 220, 130) };
        check("恢复仅点击群聊分组下唯一完整名称", WeChatListAutomation.SearchResult(results, target.Name, calibration) is { Y: 140 } &&
            WeChatListAutomation.SearchResult([..results, Line("测试群", 155, 130)], target.Name, calibration) == null && WeChatListAutomation.SearchResult(results, "测试", calibration) == null);
        check("恢复兼容微信最常使用分组并拒绝多重匹配", WeChatListAutomation.SearchResult([Line("最常使用", 85, 80), Line("测试群", 130, 130), Line("聊天记录", 180, 80)], target.Name, calibration) is { Y: 140 } &&
            WeChatListAutomation.SearchResult([Line("最常使用", 85, 80), Line("测试群", 130, 130), Line("群聊", 170, 80), Line("测试群", 210, 130)], target.Name, calibration) == null);
        check("实测OCR分组字形差异仅应用于搜索标题", WeChatListAutomation.SearchResult([Line("最帛使用", 85, 80), Line("测试群", 130, 130)], target.Name, calibration) is { Y: 140 } &&
            !WeChatListAutomation.SameName("最常使用", "最帛使用"));
        using (var bitmap = new Bitmap(400, 180))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(Color.FromArgb(50, 190, 120), 2))
        {
            graphics.Clear(Color.White); graphics.DrawRectangle(pen, 75, 40, 175, 30);
            check("恢复搜索必须看到搜索区域焦点边框", WeChatListAutomation.SearchFocused(bitmap, new(70, 35, 190, 45)) && !WeChatListAutomation.SearchFocused(bitmap, new(70, 95, 190, 45)));
        }
        using (var selected = new Bitmap(40, 20))
        using (var graphics = Graphics.FromImage(selected))
        {
            graphics.Clear(Color.FromArgb(7, 160, 95)); graphics.FillRectangle(Brushes.White, 5, 4, 3, 10);
            using var normalized = WeChatListAutomation.PrepareName(selected);
            check("绿底白字的会话名规范为有留白的黑字白底", normalized.Size == new Size(64, 40) && normalized.GetPixel(17, 15).ToArgb() == Color.Black.ToArgb() && normalized.GetPixel(12, 10).ToArgb() == Color.White.ToArgb());
        }
        using (var bitmap = new Bitmap(1100, 905))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(225, 225, 231)); graphics.FillRectangle(Brushes.White, 75, 43, 178, 28);
            check("恢复定位唯一顶部搜索框不依赖灰色占位文字OCR", WeChatListAutomation.EmptySearchField(bitmap, calibration) == new Rectangle(75, 43, 178, 28));
            graphics.FillRectangle(Brushes.Gray, 233, 52, 8, 8);
            check("已有搜索内容的清除按钮阻止覆盖输入", WeChatListAutomation.EmptySearchField(bitmap, calibration) == null);
            graphics.Clear(Color.White);
            check("无法识别独立搜索框时禁止输入", WeChatListAutomation.EmptySearchField(bitmap, calibration) == null);
        }
        using (var bitmap = new Bitmap(40, 20))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White); graphics.FillRectangle(Brushes.Black, 5, 5, 4, 10); graphics.FillRectangle(Brushes.SeaGreen, 25, 3, 1, 14);
            using var query = WeChatListAutomation.PrepareQuery(bitmap);
            check("查询核对移除绿色光标并保留查询文字", query.GetPixel(37, 15).ToArgb() == Color.White.ToArgb() && query.GetPixel(17, 15).ToArgb() == Color.Black.ToArgb());
        }
        new FocusSettings([target], calibration).Save(data);
        new FocusSettings([target], calibration).Save(data, "focus-restore.json");
        using (var controller = new FocusController(data))
        using (var store = new Store(data))
        using (var dialog = new FocusDialog(store, controller) { Opacity = 0, ShowInTaskbar = false })
        {
            check("规则跨启动保留但默认不自动隐藏且恢复清单独立保留", controller.Settings.Targets.Length == 1 && !controller.Running && controller.PendingCount == 1);
            bool rejected = false; try { controller.Configure(new([], null)); } catch (InvalidOperationException) { rejected = true; }
            check("待恢复会话存在时禁止覆盖规则", rejected && FocusSettings.Load(data, "focus-restore.json").Targets.Length == 1);
            dialog.Show(); Application.DoEvents();
            using var image = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(image, new(Point.Empty, dialog.Size));
            image.Save(Path.ChangeExtension(report, ".focus.png")); dialog.Hide();
        }
        File.WriteAllText(Path.Combine(data, "focus-settings.json"), "invalid");
        check("损坏的隐藏设置回退未启动配置", FocusSettings.Load(data).Targets.Length == 0);
        File.WriteAllText(Path.Combine(data, "focus-settings.json"), "{\"Targets\":[null],\"Calibration\":null}");
        check("空规则条目不会导致启动失败", FocusSettings.Load(data).Targets.Length == 0);
        int clicks = 0; using var appMenu = new AppMenu(() => { }, () => { }, () => clicks++);
        ((ToolStripMenuItem)appMenu.Items[0]).DropDownItems[1].PerformClick();
        check("设置菜单提供会话隐藏入口", clicks == 1);
        var legacy = JsonSerializer.Deserialize<RawMessageInfo>("{\"SourceDatabase\":\"old.db\",\"LocalId\":1,\"ServerId\":0,\"SenderId\":\"me\",\"Sender\":\"昵称\",\"SentAt\":\"2026-10-28T00:00:00Z\",\"RawType\":1,\"RawContent\":\"old\"}");
        check("历史消息JSON兼容没有提及字段", legacy is { MentionedUsers: null, RawContent: "old" });
    }
}
