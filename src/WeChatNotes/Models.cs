using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WeChatNotes;

public sealed record Target(string Id, string Name, string WindowTitle, int Width, int Height, int X, int Y, int CropWidth, int CropHeight,
    string? SourceRoot = null, string? ConversationId = null, long FromUnix = 0)
{
    public Rectangle Region => new(X, Y, CropWidth, CropHeight);
}

public sealed record CaptureRecord(string Id, string TargetId, string TargetName, DateTimeOffset ObservedAt,
    string Text, string AddedText, string ImagePath, string Note, RawMessageInfo? Message = null, VoiceTranscript? VoiceTranscription = null);
public sealed record RawMessageInfo(string SourceDatabase, long LocalId, long ServerId, string SenderId,
    string Sender, DateTimeOffset SentAt, long RawType, string RawContent, string? GroupNickname = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplaySender => !string.IsNullOrWhiteSpace(Sender) ? Sender
        : !string.IsNullOrWhiteSpace(SenderId) ? SenderId : "未知发送人 / 系统";
}

public static partial class TextDiff
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    public static string[] Lines(string text) => text.Replace("\r", "").Split('\n')
        .Select(x => Spaces().Replace(x.Trim(), " ")).Where(x => x.Length > 0).ToArray();
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Lines(text)))));

    // Retain order and multiplicity. A global text hash would incorrectly erase repeated messages.
    public static (string Text, string Note) Added(string? previous, string current)
    {
        var next = Lines(current);
        if (previous == null) return (string.Join('\n', next), "本次启动的首屏；采集时间不是消息发送时间。");
        var before = Lines(previous);
        if (before.SequenceEqual(next)) return ("", "");
        for (int overlap = Math.Min(before.Length, next.Length); overlap >= 1; overlap--)
        {
            if (before.AsSpan(before.Length - overlap).SequenceEqual(next.AsSpan(0, overlap)))
                return (string.Join('\n', next.Skip(overlap)), "根据相邻画面的行重叠推测新增内容；请结合原始画面核对。");
        }
        // Never silently discard text when scrolling, OCR variations or a large burst break alignment.
        return (string.Join('\n', next), "与上一画面无法对齐：保留整屏，可能存在重复或遗漏；请核对截图。");
    }
}
