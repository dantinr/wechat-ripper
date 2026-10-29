// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace WeChatNotes;

internal sealed record ChatSession(string Username, string DisplayName, long LastTime)
{
    public override string ToString() => DisplayName;
}

internal sealed class RawChatSource(string root) : IDisposable
{
    public string Root { get; } = Path.GetFullPath(root);
    private Dictionary<string, byte[]> keys = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> names = new(StringComparer.Ordinal);
    private Dictionary<(string Room, string Sender), string> groupNicknames = [];
    internal static string[] Accounts(IEnumerable<string>? knownRoots = null, CancellationToken token = default) => WeChatDirectories.Discover(knownRoots, token);
    internal static string TargetIdFor(string root, string conversation) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant() + "|" + conversation))).ToLowerInvariant();
    private string[] MessageFiles() => Directory.GetFiles(Path.Combine(Root, "message"), "message_*.db")
        .Where(p => Regex.IsMatch(Path.GetFileName(p), @"^message_\d+\.db$", RegexOptions.CultureInvariant)).Order().ToArray();
    internal void Connect(Action<string>? progress, CancellationToken token)
    {
        string[] files = [Path.Combine(Root, "session", "session.db"), Path.Combine(Root, "contact", "contact.db"), .. MessageFiles()];
        if (files.Any(p => !File.Exists(p))) throw new IOException("请选择包含 session、contact、message 的 db_storage 文件夹。");
        if (files.All(f => keys.TryGetValue(f, out var key) && CipherPages.VerifyKey(key, RawKeys.SharedRead(f, 4096)))) return;
        Dispose(); keys = RawKeys.Find(files, progress, token);
    }
    private SqliteConnection Open(string path, CancellationToken token)
    {
        if (!keys.TryGetValue(path, out var key)) throw new InvalidOperationException("出现新的消息数据库，请重新连接微信。");
        byte[] bytes = CipherPages.Snapshot(path, key, token);
        try { return MemoryDatabase.Open(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal List<ChatSession> Sessions(Action<string>? progress, CancellationToken token)
    {
        Connect(progress, token); LoadNames(token);
        using var db = Open(Path.Combine(Root, "session", "session.db"), token);
        using var command = db.CreateCommand(); command.CommandText = "SELECT username,last_timestamp FROM SessionTable ORDER BY last_timestamp DESC";
        using var reader = command.ExecuteReader(); var result = new List<ChatSession>();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            if (!string.IsNullOrEmpty(id)) result.Add(new(id, NameOf(id), reader.IsDBNull(1) ? 0 : reader.GetInt64(1)));
        }
        return result;
    }
    private void LoadNames(CancellationToken token, IEnumerable<string>? rooms = null)
    {
        using var db = Open(Path.Combine(Root, "contact", "contact.db"), token);
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string table in new[] { "contact", "stranger" })
        {
            using var command = db.CreateCommand(); command.CommandText = $"SELECT username,remark,nick_name FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string id = reader.GetString(0); string remark = reader.IsDBNull(1) ? "" : reader.GetString(1); string nick = reader.IsDBNull(2) ? "" : reader.GetString(2);
                next.TryAdd(id, !string.IsNullOrWhiteSpace(remark) ? remark : !string.IsNullOrWhiteSpace(nick) ? nick : id);
            }
        }
        names = next;
        groupNicknames = GroupNicknames.Load(db, rooms ?? []);
    }
    private string NameOf(string id) => names.GetValueOrDefault(id, id);
    internal List<CaptureRecord> Read(IReadOnlyList<Target> targets, CancellationToken token)
    {
        Connect(null, token);
        LoadNames(token, targets.Select(t => t.ConversationId!).Where(id => id.EndsWith("@chatroom", StringComparison.Ordinal)));
        var result = new List<CaptureRecord>();
        foreach (string path in MessageFiles())
        {
            token.ThrowIfCancellationRequested();
            using var db = Open(path, token);
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
            }
            var senders = new Dictionary<long, string>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT rowid,user_name FROM Name2Id";
                using var reader = command.ExecuteReader(); while (reader.Read()) senders[reader.GetInt64(0)] = reader.GetString(1);
            }
            foreach (var target in targets)
            {
                string table = "Msg_" + Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(target.ConversationId!))).ToLowerInvariant();
                if (!tables.Contains(table)) continue;
                using var command = db.CreateCommand();
                command.CommandText = $"SELECT local_id,server_id,local_type,real_sender_id,create_time,message_content,compress_content FROM \"{table}\" WHERE create_time >= $from ORDER BY create_time,local_id";
                command.Parameters.AddWithValue("$from", target.FromUnix);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    long local = reader.GetInt64(0), server = reader.GetInt64(1), type = reader.GetInt64(2);
                    string sender = senders.GetValueOrDefault(reader.GetInt64(3), "");
                    var time = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)).ToOffset(TimeSpan.FromHours(8));
                    string raw = DecodeContent(reader.GetValue(5), reader.GetValue(6));
                    string content = raw;
                    // A group sender prefix is an envelope, kept intact in RawContent for auditing.
                    int newline = content.IndexOf('\n');
                    if (target.ConversationId!.EndsWith("@chatroom") && newline is > 1 and < 100 && content[newline - 1] == ':')
                    {
                        string prefix = content[..(newline - 1)];
                        if (prefix == sender || senders.Values.Contains(prefix)) { if (sender.Length == 0) sender = prefix; content = content[(newline + 1)..]; }
                    }
                    string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{target.Id}|{Path.GetFileName(path)}|{local}"))).ToLowerInvariant();
                    string kind = TypeName(type);
                    var message = new RawMessageInfo(Path.GetFileName(path), local, server, sender, sender.Length > 0 ? NameOf(sender) : "未知发送人 / 系统", time, type, raw,
                        groupNicknames.GetValueOrDefault((target.ConversationId!, sender)));
                    string text = content.Length > 0 ? content : $"[{kind}，无文字正文]";
                    result.Add(new(id, target.Id, target.Name, DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)), text, text, "",
                        $"直接读取本地消息 · {kind}。导出时一并保存可匹配的本地文件附件和语音；图片和视频不提取原文件。", message));
                }
            }
        }
        return result.OrderBy(r => r.Message!.SentAt).ThenBy(r => r.Message!.SourceDatabase).ThenBy(r => r.Message!.LocalId).ToList();
    }
    internal static string DecodeContent(object content, object compressed)
    {
        // Prefer an actual compressed frame; never HTML-decode or trim original plain messages.
        foreach (var value in new[] { content, compressed })
            if (value is byte[] bytes && bytes.AsSpan().StartsWith(new byte[] { 0x28, 0xb5, 0x2f, 0xfd }))
            {
                using var decompressor = new Decompressor();
                return new UTF8Encoding(false, true).GetString(decompressor.Unwrap(bytes, 16 * 1024 * 1024).ToArray());
            }
        foreach (var value in new[] { content, compressed })
        {
            if (value is string text && text.Length > 0) return text;
            if (value is byte[] bytes && bytes.Length > 0) return new UTF8Encoding(false, true).GetString(bytes);
        }
        return "";
    }
    internal static string TypeName(long type) => (type & 0xffffffff) switch
    {
        1 => "文字", 3 => "图片", 34 => "语音", 43 => "视频", 47 => "表情", 49 => "引用 / 链接 / 文件", 10000 => "系统消息", _ => "消息类型 " + type
    };
    public void Dispose()
    {
        foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
        keys.Clear(); names.Clear(); groupNicknames.Clear();
    }
}

internal static class MemoryDatabase
{
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_malloc64(ulong size);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern void sqlite3_free(nint address);
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_deserialize(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string schema, nint bytes, long length, long capacity, uint flags);
    internal static SqliteConnection Open(byte[] bytes)
    {
        var db = new SqliteConnection("Data Source=:memory:;Pooling=False"); db.Open();
        // A detached read-only in-memory image; no decrypted full-chat database is written to disk.
        // The detached image has no external WAL, so use rollback mode in its private header.
        bytes[18] = bytes[19] = 1;
        nint buffer = sqlite3_malloc64((ulong)bytes.Length);
        if (buffer == 0) { db.Dispose(); throw new OutOfMemoryException(); }
        Marshal.Copy(bytes, 0, buffer, bytes.Length);
        int code = sqlite3_deserialize(db.Handle!.DangerousGetHandle(), "main", buffer, bytes.Length, bytes.Length, 1 | 4);
        if (code != 0) { sqlite3_free(buffer); db.Dispose(); throw new IOException("无法打开内存数据库：" + code); }
        return db;
    }
}
