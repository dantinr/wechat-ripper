using System.Reflection;
using System.Runtime.InteropServices;

namespace WeChatNotes;

internal static class ConversationSelectionSelfTest
{
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint buttons, nint point);

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    private static void ClickItem(RangeCheckedListBox list, int index, bool shift = false)
    {
        var rect = list.GetItemRectangle(index);
        nint point = (nint)(((rect.Top + rect.Height / 2) << 16) | 10);
        SendMessage(list.Handle, 0x0201, shift ? 5 : 1, point);
        SendMessage(list.Handle, 0x0202, shift ? 4 : 0, point);
    }
    private static void Key(RangeCheckedListBox list, Keys keys) => typeof(RangeCheckedListBox).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(list, [new KeyEventArgs(keys)]);
    private static int[] Checked(RangeCheckedListBox list) => list.CheckedIndices.Cast<int>().ToArray();

    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using (var host = new Form { Width = 500, Height = 400, Opacity = 0, ShowInTaskbar = false })
        using (var list = new RangeCheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true })
        {
            list.Items.AddRange(["合成会话 0", "合成会话 1", "合成会话 2", "合成会话 3", "合成会话 4", "合成会话 5"]);
            host.Controls.Add(list); host.Show();
            ClickItem(list, 1); ClickItem(list, 4, true);
            check("添加会话Shift点击包含首尾且不会再次切换末项", Checked(list).SequenceEqual([1, 2, 3, 4]));
            ClickItem(list, 5, true);
            check("连续Shift点击保留初始锚点并扩展勾选范围", Checked(list).SequenceEqual([1, 2, 3, 4, 5]));
            list.CheckAll(false); ClickItem(list, 4); ClickItem(list, 1, true);
            check("向上Shift连选按当前显示顺序勾选", Checked(list).SequenceEqual([1, 2, 3, 4]));
            ClickItem(list, 4); ClickItem(list, 2, true);
            check("取消一项后Shift点击可批量取消勾选", Checked(list).SequenceEqual([1]));
            ClickItem(list, 2);
            check("Shift操作后普通单击仍仅切换当前会话", Checked(list).SequenceEqual([1, 2]));
            list.CheckAll(false); ClickItem(list, 3, true);
            check("没有选择锚点时Shift点击只勾选当前项", Checked(list).SequenceEqual([3]));
            Key(list, Keys.Control | Keys.A);
            check("会话列表Ctrl加A勾选全部显示项", Checked(list).SequenceEqual([0, 1, 2, 3, 4, 5]));
            Key(list, Keys.Control | Keys.Shift | Keys.A);
            check("会话列表Ctrl加Shift加A取消全部显示项", list.CheckedItems.Count == 0);
            host.Close();
        }

        using var store = new Store(Path.Combine(root, "conversation-selection-store"));
        using var recorder = new RawRecorder(store);
        using var dialog = new RawBindDialog(recorder);
        var controls = Descendants(dialog).ToArray();
        var sessions = controls.OfType<RangeCheckedListBox>().Single();
        var filter = controls.OfType<TextBox>().Single();
        var account = controls.OfType<ComboBox>().Single(c => c.AccessibleName != "首次导入范围");
        var selectAll = controls.OfType<Button>().Single(c => c.Text == "全选");
        var clearAll = controls.OfType<Button>().Single(c => c.Text == "取消全选");
        var save = controls.OfType<Button>().Single(c => c.Text == "添加选中会话");
        var count = controls.OfType<Label>().Single(c => c.AccessibleName == "会话选择数量");
        account.Text = Path.Combine(root, "synthetic-selection-account", "db_storage");
        ChatSession[] loaded = [new("a@chatroom", "项目 A", 1), new("b@chatroom", "项目 B", 2), new("c@chatroom", "项目 C", 3), new("friend-d", "联系人 D", 4)];
        dialog.SetSessions(loaded);
        // Keep the dialog hidden: Shown would discover the user's actual WeChat accounts.
        _ = dialog.Handle;
        using (var initial = new Bitmap(dialog.Width, dialog.Height)) dialog.DrawToBitmap(initial, new(Point.Empty, dialog.Size));
        check("会话读取后无默认勾选且全选和取消按钮可用", sessions.Items.Count == 4 && sessions.CheckedItems.Count == 0 && selectAll.Enabled && clearAll.Enabled && !save.Enabled);
        ClickItem(sessions, 0);
        filter.Text = "联系人";
        Click(selectAll);
        check("搜索后全选只添加当前结果并保留其他已选会话", sessions.CheckedItems.Count == 1 && count.Text.StartsWith("已选 2 / 共 4") && save.Enabled);
        Click(clearAll);
        filter.Text = "";
        check("取消搜索结果勾选不会清空其他已选会话", Checked(sessions).SequenceEqual([0]) && count.Text.StartsWith("已选 1 / 共 4"));
        filter.Text = "项目";
        ClickItem(sessions, 2, true);
        check("修改搜索条件后Shift不会使用过滤前的锚点", Checked(sessions).SequenceEqual([0, 2]));
        filter.Text = "没有匹配项";
        check("搜索无结果禁用批量按钮并保留可提交的已选会话", sessions.Items.Count == 0 && !selectAll.Enabled && !clearAll.Enabled && save.Enabled && count.Text.Contains("已选 2 / 共 4"));
        filter.Text = "";
        Click(clearAll); Click(selectAll);
        check("清空筛选后全选可勾选全部会话并刷新数量", Checked(sessions).SequenceEqual([0, 1, 2, 3]) && count.Text.StartsWith("已选 4 / 共 4"));

        // Render the real content in a synthetic host without firing account discovery.
        var content = dialog.Controls[0];
        using (var previewHost = new Form { Text = dialog.Text, Font = dialog.Font, Opacity = 0, ShowInTaskbar = false })
        {
            previewHost.Controls.Add(content); previewHost.Show();
            foreach (var size in new[] { new Size(820, 660), new Size(680, 520) })
            {
                previewHost.Size = size;
                using var preview = new Bitmap(previewHost.Width, previewHost.Height);
                previewHost.DrawToBitmap(preview, new(Point.Empty, previewHost.Size));
                preview.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + $"-conversation-selection-{size.Width}.png"));
                check($"添加会话{size.Width}宽度批量操作与计数布局完整", count.Right <= count.Parent!.ClientSize.Width && count.Bottom <= count.Parent.ClientSize.Height && sessions.Height >= sessions.ItemHeight * 3);
            }
            dialog.Controls.Add(content); previewHost.Close();
        }

        account.Text = Path.Combine(root, "synthetic-other-account", "db_storage");
        check("切换账号清空旧选择和列表并禁用提交", sessions.Items.Count == 0 && !save.Enabled && !selectAll.Enabled && count.Text.StartsWith("已选 0 / 共 0"));
        dialog.SetSessions(loaded);
        ClickItem(sessions, 2, true);
        check("重新读取后Shift首击不会沿用旧账号锚点", Checked(sessions).SequenceEqual([2]));
        filter.Text = "联系人"; Click(selectAll);
        Click(save);
        check("提交包含当前筛选外的全部已选会话且不重复", dialog.DialogResult == DialogResult.OK && dialog.Results.Select(t => t.ConversationId).SequenceEqual(["c@chatroom", "friend-d"]) && dialog.Results.All(t => t.SourceRoot == account.Text));

    }
}
