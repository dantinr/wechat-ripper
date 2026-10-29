using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace WeChatNotes;

internal static class ExportFileLocatorSelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string folder = Path.Combine(root, "文件定位导出 #100%");
        string relative = "附件/" + new string('a', 64) + "/计划 [v1] #1% & '最终'.txt";
        string path = Path.Combine(folder, relative.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "合成附件");
        var index = ExportFileLocator.CreateIndex([relative]);
        void WritePage(string directory)
        {
            using var html = new StreamWriter(Path.Combine(directory, "聊天记录.html"));
            ExportFileLocator.WriteEmbeddedIndex(html, index);
        }
        WritePage(folder);
        string Request(string file, string? id = null) => $"{ExportFileLocator.Scheme}://file/{id ?? index.Id}?path=" + Uri.EscapeDataString(new Uri(file).AbsoluteUri);
        string? revealed = null;
        ExportFileLocator.Locate(Request(path), value => revealed = value);
        check("浏览器定位链接正确还原中文空格井号百分号和引号文件名", revealed == path && ExportFileLocator.Resolve(Request(path)) == path);
        var item = ExportFileLocator.ParseShellItem(path);
        try { check("定位文件可解析为原生资源管理器项目且不执行文件", item != IntPtr.Zero); }
        finally { Marshal.FreeCoTaskMem(item); }

        string moved = Path.Combine(root, "整体移动后的导出 中文 #%"); Directory.Move(folder, moved);
        path = Path.Combine(moved, relative.Replace('/', '\\'));
        check("导出目录移动后通过新页面路径和原索引继续定位", ExportFileLocator.Resolve(Request(path)) == path && !File.ReadAllText(Path.Combine(moved, "聊天记录.html")).Contains(root));
        bool Reject(string request)
        {
            bool opened = false;
            try { ExportFileLocator.Locate(request, _ => opened = true); return false; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or JsonException or NotSupportedException) { return !opened; }
        }
        check("无效外部协议动作参数及伪造导出标识不会调用资源管理器", Reject(Request(path, Guid.NewGuid().ToString("N"))) && Reject(Request(path).Replace("://file/", "://run/")) && Reject(Request(path) + "&run=cmd") && Reject("https://example.com/") && Reject("ripper-locate://file/invalid?path=anything"));
        check("网络路径网页路径和系统目录不作为文件定位目标", Reject($"ripper-locate://file/{index.Id}?path=" + Uri.EscapeDataString("file://server/share/file.txt")) && Reject($"ripper-locate://file/{index.Id}?path=" + Uri.EscapeDataString("https://example.com/file")) && Reject(Request(Environment.ProcessPath!)));
        string other = Path.Combine(Path.GetDirectoryName(path)!, "未导出.txt"); File.WriteAllText(other, "合成其他文件");
        check("不在导出清单中的文件及已删除附件不会打开目录", Reject(Request(other)) && Reject(Request(path + ".missing")));
        string indexPath = Path.Combine(moved, "聊天记录.html");
        File.WriteAllText(indexPath, "<script type=\"application/json\" id=\"ripper-file-index\">invalid json</script>\n");
        bool corrupt = Reject(Request(path)); File.Delete(indexPath);
        check("定位索引损坏或缺失时给出失败并保留普通目录访问", corrupt && Reject(Request(path)) && Directory.Exists(Path.GetDirectoryName(path)));
        WritePage(moved);
        check("新导出定位索引嵌入HTML无需额外JSON文件", !File.Exists(Path.Combine(moved, ExportFileLocator.IndexName)) && ExportFileLocator.Resolve(Request(path)) == path);
        File.WriteAllText(indexPath, "旧版页面");
        string legacyPath = Path.Combine(moved, ExportFileLocator.IndexName);
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(index));
        check("历史独立JSON索引的导出仍可定位", ExportFileLocator.Resolve(Request(path)) == path);
        File.Delete(legacyPath); WritePage(moved);
        string junction = Path.Combine(root, "定位目录联接");
        using (var link = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{moved}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            link.StandardOutput.ReadToEnd(); link.StandardError.ReadToEnd();
            if (!link.WaitForExit(5000) || link.ExitCode != 0) throw new IOException("无法创建合成目录联接。");
        }
        try { check("符号链接不能将定位请求重定向到其他位置", Reject(Request(Path.Combine(junction, relative.Replace('/', '\\'))))); }
        finally { Directory.Delete(junction); }

        string registryPath = @"Software\RipperTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            ExportFileLocator.WriteRegistration(key, @"C:\中文 程序\Ripper.exe");
            using var command = key.OpenSubKey(@"shell\open\command");
            check("当前用户协议注册命令正确引用含空格程序路径和单个请求参数", (string?)key.GetValue("URL Protocol") == "" && (string?)command?.GetValue("") == "\"C:\\中文 程序\\Ripper.exe\" --locate-export \"%1\"");
            ExportFileLocator.WriteRegistration(key, @"C:\Program Files\dotnet\dotnet.exe", @"D:\程序 目录\Ripper.dll");
            check("框架依赖版本的文件定位注册保留程序集路径", (string?)command?.GetValue("") == "\"C:\\Program Files\\dotnet\\dotnet.exe\" \"D:\\程序 目录\\Ripper.dll\" --locate-export \"%1\"");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(registryPath, false); }
    }
}
