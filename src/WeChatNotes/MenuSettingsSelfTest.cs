namespace WeChatNotes;

internal static class MenuSettingsSelfTest
{
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    internal static void Run(string root, string report, Action<string, bool> check)
    {
        int modelClicks = 0, aboutClicks = 0;
        using (var menu = new AppMenu(() => modelClicks++, () => aboutClicks++))
        using (var dialog = new AboutDialog() { Opacity = 0 })
        {
            var settings = (ToolStripMenuItem)menu.Items[0]; var about = (ToolStripMenuItem)menu.Items[1];
            settings.DropDownItems[0].PerformClick(); about.PerformClick();
            check("顶部设置菜单可进入模型设置", settings.Text == "设置" && settings.DropDownItems[0].Text == "模型设置" && modelClicks == 1);
            dialog.Show(); Application.DoEvents();
            var controls = Descendants(dialog).ToArray(); var github = controls.OfType<LinkLabel>().Single();
            check("点击关于统一展示版本GitHub及完整Slogan", about.Text == "关于" && about.DropDownItems.Count == 0 && aboutClicks == 1 &&
                controls.OfType<Label>().Any(c => c.Text == "版本号：v" + Application.ProductVersion) && controls.OfType<Label>().Any(c => c.Text == "Free your conversations. Own your records.") &&
                github.Visible && (string?)github.Links[0].LinkData == "https://github.com/dantinr/wechat-ripper");
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new(Point.Empty, dialog.Size));
            bitmap.Save(Path.ChangeExtension(report, ".about.png")); dialog.Hide();
        }
        string data = Path.Combine(root, "menu-settings");
        using var store = new Store(data);
        using (var main = new MainForm(store) { Opacity = 0, ShowInTaskbar = false })
        {
            main.Show(); Application.DoEvents();
            var content = main.Controls.OfType<TableLayoutPanel>().Single();
            check("主界面菜单位于内容上方且不遮挡采集操作", main.MainMenuStrip is AppMenu && main.MainMenuStrip.Top == 0 && content.Top >= main.MainMenuStrip.Bottom);
            main.Hide();
        }
        string model = Path.Combine(root, "自备模型", "ggml-small.bin");
        SpeechModel.SavePath(data, model);
        using (var dialog = new SpeechModelDialog(data) { Opacity = 0, ShowInTaskbar = false })
        {
            dialog.Show(); Application.DoEvents();
            var controls = Descendants(dialog).ToArray();
            var gpu = controls.OfType<CheckBox>().Single(c => c.AccessibleName == "启用GPU加速");
            var parallel = controls.OfType<NumericUpDown>().Single(c => c.AccessibleName == "语音转写并发数");
            check("模型设置默认启用GPU并保留CPU四路回退", gpu.Checked && parallel.Value == 4 && SpeechSettings.Load(data).Options(data).Device == SpeechDevice.Auto);
            gpu.Checked = false; parallel.Value = 2;
            var options = SpeechSettings.Load(data).Options(data);
            check("关闭GPU立即保存并映射到下次导出的CPU设备及并发", options.Device == SpeechDevice.Cpu && options.Parallelism == 2 && options.ModelPath == model && controls.OfType<Label>().Any(c => c.Text.Contains("已自动保存")));
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
            bitmap.Save(Path.ChangeExtension(report, ".model-settings.png"));
            dialog.Hide();
        }
        using (var reopened = new SpeechModelDialog(data))
        {
            var controls = Descendants(reopened).ToArray();
            var gpu = controls.OfType<CheckBox>().Single();
            check("重新打开设置保留GPU关闭及CPU并发且不改模型路径", !gpu.Checked && controls.OfType<NumericUpDown>().Single().Value == 2 && SpeechModel.PathFor(data) == model);
            gpu.Checked = true;
            check("重新启用GPU后恢复自动探测并保留CPU回退并发", SpeechSettings.Load(data).Options(data) is { Device: SpeechDevice.Auto, MaxParallelism: 2 });
        }
        string bad = Path.Combine(root, "bad-speech-settings"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "speech-settings.json"), "invalid json");
        check("损坏的模型运行设置回退默认值且限制异常并发", SpeechSettings.Load(bad) == new SpeechSettings() && new SpeechSettings(false, 999).Options(bad).Parallelism == 8);
        File.Delete(Path.Combine(bad, "speech-settings.json")); Directory.CreateDirectory(Path.Combine(bad, "speech-settings.json"));
        using var failed = new SpeechModelDialog(bad);
        var failedControls = Descendants(failed).ToArray(); var failedGpu = failedControls.OfType<CheckBox>().Single();
        failedGpu.Checked = false;
        check("设置无法保存时恢复原值并提示失败且清理临时文件", failedGpu.Checked && failedControls.OfType<Label>().Any(c => c.Text.Contains("设置未保存")) && !Directory.EnumerateFiles(bad, "*.tmp").Any());
    }
}
