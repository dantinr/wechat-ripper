namespace WeChatNotes;

internal static class PaginationSelfTest
{
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "pagination-store"));
        foreach (var (id, name) in new[] { ("a", "产品需求群"), ("b", "研发讨论群"), ("empty", "空会话") })
            store.SaveTarget(new(id, name, "", 0, 0, 0, 0, 0, 0));
        CaptureRecord Record(int number, string target = "a")
        {
            string text = number switch { 0 => "历史独有关键词", 1 => "100%_ ' OR 1=1 --", _ => $"消息 {number:D4}" };
            var time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(8)).AddSeconds(number);
            var message = new RawMessageInfo("message_0.db", number, number, number == 3 ? "sender-unique" : "sender", number == 2 ? "Änne" : "ALICE", time, 1, text, number == 1 ? "产品·小林" : null);
            return new($"{target}-{number}", target, target == "a" ? "产品需求群" : "研发讨论群", time, text, text, "", "合成分页演示", message);
        }
        store.AddMessages(Enumerable.Range(0, 1203).Select(i => Record(i)));
        store.AddMessages(Enumerable.Range(0, 100).Select(i => Record(i, "b")));
        var allIds = new List<string>();
        for (int page = 1; page <= 13; page++) allIds.AddRange(store.CapturePage(100, page, "a").Records.Select(r => r.Id));
        check("分页覆盖超过 500 条的全部历史且无重复遗漏", allIds.Count == 1203 && allIds.Distinct().Count() == 1203 && allIds.First() == "a-1202" && allIds.Last() == "a-0");
        var last = store.CapturePage(100, long.MaxValue, "a");
        check("末页余数和越界页码正确", last.TotalCount == 1203 && last.PageCount == 13 && last.PageNumber == 13 && last.Records.Count == 3 && store.CapturePage(100, -1, "a").PageNumber == 1);
        check("总览与各会话计数隔离", store.CapturePage(100).TotalCount == 1303 && store.CapturePage(50, 2, "b").Records.All(r => r.TargetId == "b"));
        check("整页倍数不产生多余空页", store.CapturePage(50, 99, "b") is { PageCount: 2, PageNumber: 2, Records.Count: 50 });
        check("全量搜索找到 500 条之前的历史消息", store.CapturePage(100, 1, "a", "历史独有关键词") is { TotalCount: 1203, MatchCount: 1 } historical && historical.Records.Single().Id == "a-0");
        check("分页搜索覆盖会话发送人群昵称标识及 Unicode 大小写", store.CapturePage(100, 1, "a", "产品需求群").MatchCount == 1203 && store.CapturePage(100, 1, "a", "alice").MatchCount == 1202 && store.CapturePage(100, 1, "a", "产品·小林").MatchCount == 1 && store.CapturePage(100, 1, "a", "sender-unique").MatchCount == 1 && store.CapturePage(100, 1, "a", "änne").MatchCount == 1);
        check("搜索将通配符与引号按普通文字处理", store.CapturePage(100, 1, "a", "%_").MatchCount == 1 && store.CapturePage(100, 1, "a", "' OR 1=1 --").MatchCount == 1);
        var filtered = store.CapturePage(100, 13, "a", "消息");
        check("过滤结果独立计数并分页", filtered.TotalCount == 1203 && filtered.MatchCount == 1201 && filtered.Records.Count == 1 && filtered.Records.Single().Id == "a-2");

        using var host = new Form { Width = 780, Height = 600, Opacity = 0, ShowInTaskbar = false, Font = new("Microsoft YaHei UI", 10) };
        using var view = new ConversationRecords(store);
        host.Controls.Add(view); host.Show(); view.RefreshRecords(); view.SelectConversation("a");
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        Button Button(string text) => Descendants(view).OfType<Button>().Single(b => b.Text == text);
        check("默认每页 100 条且显示完整总数与页数", view.PageSize == 100 && view.TotalCount == 1203 && view.PageCount == 13 && view.VisibleCount == 100);
        long Number(int row) => (long)view.CurrentGrid!.Rows[row].Cells["number"].Value!;
        bool numbered = Number(0) == 1 && Number(99) == 100;
        view.GoToPage(2); numbered &= Number(0) == 101 && Number(99) == 200;
        view.SetPageSize(50); view.GoToPage(3); numbered &= Number(0) == 101 && Number(49) == 150;
        view.SelectConversation("b"); numbered &= Number(0) == 1 && Number(49) == 50;
        view.SelectConversation("a"); view.SetPageSize(100);
        check("会话序号跨页连续且随每页条数和会话正确变化", numbered);
        bool sizes = true;
        foreach (int size in new[] { 50, 200, 500, 100 }) { view.SetPageSize(size); sizes &= view.VisibleCount == size && view.PageNumber == 1; }
        check("每页条数可切换 50 100 200 500", sizes);
        Button("下一页").PerformClick(); bool next = view.PageNumber == 2;
        Button("末页").PerformClick(); bool end = view.PageNumber == 13 && view.VisibleCount == 3 && !Button("下一页").Enabled;
        Button("上一页").PerformClick(); bool previous = view.PageNumber == 12;
        Button("首页").PerformClick();
        check("首页上页下页末页按钮及边界可用", next && end && previous && view.PageNumber == 1 && !Button("上一页").Enabled);
        Descendants(view).OfType<NumericUpDown>().Single().Value = 4;
        check("输入页码可跳转历史页", view.PageNumber == 4 && view.SelectedRecord?.Id == "a-902");
        view.GoToPage(2); view.CurrentGrid!.CurrentCell = view.CurrentGrid.Rows[99].Cells[0];
        string? selected = view.SelectedRecord?.Id;
        view.SelectConversation("b"); view.GoToPage(1); view.SelectConversation("a");
        check("切换会话记住各自页码与来源消息", view.PageNumber == 2 && view.SelectedRecord?.Id == selected);
        store.AddMessages(Enumerable.Range(1203, 101).Select(i => Record(i))); view.RefreshRecords();
        check("新增消息跨页后刷新保留正在查看的来源", view.TotalCount == 1304 && view.PageNumber == 4 && view.SelectedRecord?.Id == selected);
        view.Search("历史独有关键词"); bool found = view.PageNumber == 1 && view.MatchCount == 1 && view.SelectedRecord?.Id == "a-0" && Number(0) == 1;
        view.Search("没有匹配的消息");
        check("搜索重置页码且无结果清空来源禁用翻页", found && view.TotalCount == 1304 && view.MatchCount == 0 && view.PageNumber == 1 && view.PageCount == 1 && view.SelectedRecord == null && !Button("下一页").Enabled && !Button("末页").Enabled);
        view.Search(""); view.SelectConversation("empty");
        check("空会话显示零条且不会回退其他会话", view.TotalCount == 0 && view.VisibleCount == 0 && view.PageNumber == 1 && view.SelectedRecord == null);
        view.SelectConversation("a"); view.GoToPage(2); view.Search("历史独有关键词");
        string export = Path.Combine(root, "pagination-export"); Exporter.Export(store, export, "a");
        check("导出仍包含搜索与分页之外的全部会话消息", ExportTestDocument.Records(export).Length == 1304);

        view.Search(""); view.GoToPage(2); host.PerformLayout();
        void Screenshot(string suffix)
        {
            using var bitmap = new Bitmap(host.Width, host.Height); host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + suffix + ".png"));
        }
        Screenshot("-pagination"); host.Width = 420; host.PerformLayout();
        check("窄窗口分页按钮换行后保持完整可见", Descendants(view).OfType<Button>().All(b => b.Bottom <= b.Parent!.ClientSize.Height && b.Right <= b.Parent.ClientSize.Width));
        Screenshot("-pagination-narrow"); host.Close();

        using var freshStore = new Store(Path.Combine(root, "no-conversation-store"));
        using var freshHost = new Form { Width = 780, Height = 600, Opacity = 0, ShowInTaskbar = false };
        using var fresh = new ConversationRecords(freshStore);
        freshHost.Controls.Add(fresh); freshHost.Show();
        fresh.RefreshRecords(); fresh.Search("合成搜索"); fresh.GoToPage(99);
        check("无会话时没有总览标签且搜索翻页保持空状态", fresh.ConversationIds.Length == 0 && fresh.SelectedConversationId == null && fresh.CurrentGrid == null && fresh.TotalCount == 0 && fresh.PageNumber == 1 && !Descendants(fresh).OfType<Button>().Any(b => b.Enabled));
        foreach (string id in new[] { "first", "second" }) freshStore.SaveTarget(new(id, "合成会话", "", 0, 0, 0, 0, 0, 0));
        fresh.RefreshRecords(); bool firstSelected = fresh.SelectedConversationId == "first";
        fresh.SelectConversation("second"); freshStore.RemoveTarget("second"); fresh.RefreshRecords();
        bool fallback = fresh.SelectedConversationId == "first";
        freshStore.RemoveTarget("first"); fresh.RefreshRecords();
        check("新增会话自动选择且移除当前会话正确回退或清空", firstSelected && fallback && fresh.SelectedConversationId == null && fresh.ConversationIds.Length == 0 && fresh.TotalCount == 0 && fresh.SelectedRecord == null);
        freshHost.Close();
    }
}
