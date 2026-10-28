namespace WeChatNotes;

internal sealed class AppMenu : MenuStrip
{
    internal const string GitHub = "https://github.com/dantinr/wechat-ripper";
    internal const string Slogan = "Free your conversations. Own your records.";

    internal AppMenu(Action configureModel, Action showAbout, Action? configureFocus = null)
    {
        AccessibleName = "主菜单";
        var settings = new ToolStripMenuItem("设置");
        settings.DropDownItems.Add("模型设置", null, (_, _) => configureModel());
        settings.DropDownItems.Add(new ToolStripMenuItem("会话隐藏（实验）", null, (_, _) => configureFocus?.Invoke()) { Enabled = configureFocus != null });
        var about = new ToolStripMenuItem("关于");
        about.Click += (_, _) => showAbout();
        Items.AddRange([settings, about]);
    }
}
