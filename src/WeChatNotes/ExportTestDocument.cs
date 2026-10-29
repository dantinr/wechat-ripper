using System.Net;
using System.Text.RegularExpressions;

namespace WeChatNotes;

internal static class ExportTestDocument
{
    internal static string Html(string folder) => File.ReadAllText(Path.Combine(folder, "聊天记录.html"));
    internal static string[] Records(string folder) => Regex.Matches(Html(folder), "<article class=\"record[\\s\\S]*?</article>")
        .Select(m => WebUtility.HtmlDecode(m.Value)).ToArray();
}
