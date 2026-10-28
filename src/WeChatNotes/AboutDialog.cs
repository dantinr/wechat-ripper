using System.Diagnostics;

namespace WeChatNotes;

internal sealed class AboutDialog : Form
{
    internal AboutDialog()
    {
        Text = "关于 Ripper"; ClientSize = new(560, 260); Font = new("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 54, 30, 36, 36, 42 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "Ripper", AutoSize = true, Font = new(Font.FontFamily, 24, FontStyle.Bold), ForeColor = Color.FromArgb(24, 41, 57) }, 0, 0);
        layout.Controls.Add(new Label { Text = $"版本号：v{Application.ProductVersion}", AutoSize = true }, 0, 1);
        layout.Controls.Add(new Label { Text = AppMenu.Slogan, AutoSize = true }, 0, 2);
        var github = new LinkLabel { Text = "GitHub：dantinr/wechat-ripper", AutoSize = true, AccessibleName = "GitHub" };
        github.Links.Add(0, github.Text.Length, AppMenu.GitHub);
        github.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppMenu.GitHub) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开 GitHub：" + ex.Message, "Ripper"); }
        };
        layout.Controls.Add(github, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.OK };
        buttons.Controls.Add(close); layout.Controls.Add(buttons, 0, 4);
        AcceptButton = CancelButton = close; Controls.Add(layout);
    }
}
