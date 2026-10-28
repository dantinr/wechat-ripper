using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace WeChatNotes;

internal static class RawSelfTest
{
    internal static void Run(string report)
    {
        var results = new List<object>();
        string root = Path.Combine(Path.GetTempPath(), "WeChatNotesRawTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(string name, bool pass)
        {
            results.Add(new { name, passed = pass });
            if (!pass) throw new InvalidOperationException(name);
        }
        try
        {
            byte[] key = RandomNumberGenerator.GetBytes(32), salt = RandomNumberGenerator.GetBytes(16);
            byte[] mac = CipherPages.MacKey(key, salt);
            byte[] MakePage(byte value, uint number, uint databasePages = 2, bool validSize = true)
            {
                byte[] result = new byte[4096], plain = Enumerable.Repeat(value, 4016).ToArray();
                int start = number == 1 ? 16 : 0;
                if (number == 1)
                {
                    salt.CopyTo(result, 0);
                    BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(28), databasePages);
                    BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(24), 1);
                    BinaryPrimitives.WriteUInt32BigEndian(plain.AsSpan(92), validSize ? 1u : 0u);
                }
                byte[] iv = RandomNumberGenerator.GetBytes(16); iv.CopyTo(result, 4016);
                using var aes = Aes.Create(); aes.Key = key;
                aes.EncryptCbc(plain.AsSpan(start), iv, result.AsSpan(start), PaddingMode.None);
                byte[] signed = new byte[4032 - start + 4]; result.AsSpan(start, 4032 - start).CopyTo(signed);
                BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(signed.Length - 4), number);
                HMACSHA512.HashData(mac, signed).CopyTo(result, 4032); return result;
            }
            byte[] first = MakePage(10, 1), second = MakePage(20, 2);
            byte[] main = [..first, ..second];
            Check("正确密钥通过第一页校验", CipherPages.VerifyKey(key, first));
            Check("错误密钥被拒绝", !CipherPages.VerifyKey(RandomNumberGenerator.GetBytes(32), first));
            byte[] decoded = CipherPages.Decode(main, [], key, CancellationToken.None);
            Check("多页 AES 解密保留正文和保留区", decoded.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8) && decoded[16] == 10 && decoded[4096] == 20 && decoded[4095] == 0);
            byte[] damaged = main.ToArray(); damaged[4200] ^= 1;
            bool rejected = false; try { CipherPages.Decode(damaged, [], key, CancellationToken.None); } catch (CryptographicException) { rejected = true; }
            Check("损坏的非首页被拒绝", rejected);
            byte[] Wal(params (uint Page, uint Commit, byte[] Body)[] frames)
            {
                byte[] bytes = new byte[32 + frames.Length * 4120];
                void BE(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
                BE(0, 0x377f0682); BE(4, 3007000); BE(8, 4096); BE(16, 100); BE(20, 200);
                uint a = 0, b = 0;
                void Sum(ReadOnlySpan<byte> data)
                {
                    for (int i = 0; i < data.Length; i += 8)
                    {
                        a = unchecked(a + BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) + b);
                        b = unchecked(b + BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]) + a);
                    }
                }
                Sum(bytes.AsSpan(0, 24)); BE(24, a); BE(28, b);
                for (int i = 0; i < frames.Length; i++)
                {
                    int offset = 32 + i * 4120; var frame = frames[i];
                    BE(offset, frame.Page); BE(offset + 4, frame.Commit); BE(offset + 8, 100); BE(offset + 12, 200);
                    frame.Body.CopyTo(bytes, offset + 24);
                    Sum(bytes.AsSpan(offset, 8)); Sum(bytes.AsSpan(offset + 24, 4096)); BE(offset + 16, a); BE(offset + 20, b);
                }
                return bytes;
            }
            byte[] committed = MakePage(30, 2), pending = MakePage(40, 2);
            byte[] wal = Wal((2, 2, committed), (2, 0, pending));
            Check("WAL 只读取已提交消息", CipherPages.Decode(main, wal, key, CancellationToken.None)[4096] == 30);
            Check("无提交的 WAL 不覆盖数据库", CipherPages.Decode(main, Wal((2, 0, pending)), key, CancellationToken.None)[4096] == 20);
            wal[^100] ^= 1;
            Check("撕裂的后续 WAL 不覆盖有效提交", CipherPages.Decode(main, wal, key, CancellationToken.None)[4096] == 30);
            byte[] resized = CipherPages.Decode(main, Wal((1, 1, first)), key, CancellationToken.None);
            Check("WAL 提交大小控制数据库截断", resized.Length == 4096);
            byte[] untrusted = MakePage(50, 2); untrusted[100] ^= 1;
            rejected = false; try { CipherPages.Decode(main, Wal((2, 2, untrusted)), key, CancellationToken.None); } catch (CryptographicException) { rejected = true; }
            Check("WAL 页也进行密码校验", rejected);
            bool Rejects(Action action)
            {
                try { action(); return false; }
                catch (Exception ex) when (ex is IOException or CryptographicException) { return true; }
            }
            byte[] padded = [..main, ..new byte[4096 * 4]];
            Check("预分配零页按有效数据库长度排除", CipherPages.Decode(padded, [], key, CancellationToken.None).SequenceEqual(decoded));
            Check("有效长度之外的旧页不参与快照", CipherPages.Decode([..main, ..damaged], [], key, CancellationToken.None).SequenceEqual(decoded));
            Check("有效范围内的空白页仍被拒绝", Rejects(() => CipherPages.Decode([..first, ..new byte[4096]], [], key, CancellationToken.None)));
            byte[] legacy = MakePage(10, 1, 1, validSize: false);
            Check("页头版本计数不匹配时回退物理长度", CipherPages.Decode([..legacy, ..second], [], key, CancellationToken.None).Length == 8192 && Rejects(() => CipherPages.Decode([..legacy, ..new byte[4096]], [], key, CancellationToken.None)));
            Check("页头长度为零时回退物理长度", CipherPages.Decode([..MakePage(10, 1, 0), ..second], [], key, CancellationToken.None).Length == 8192);
            byte[] grown = CipherPages.Decode(padded, Wal((3, 3, MakePage(50, 3))), key, CancellationToken.None);
            Check("WAL 已提交扩容覆盖预分配页", grown.Length == 12288 && grown[8192] == 50);
            Check("WAL 未提交扩容不改变有效长度", CipherPages.Decode(padded, Wal((3, 0, MakePage(50, 3))), key, CancellationToken.None).SequenceEqual(decoded));
            Check("WAL 已提交截断不读取已移除坏页", CipherPages.Decode(damaged, Wal((1, 1, MakePage(10, 1, 1))), key, CancellationToken.None).Length == 4096);
            Check("WAL 最新已提交页替代主库旧页", CipherPages.Decode(damaged, Wal((2, 2, committed)), key, CancellationToken.None)[4096] == 30 && CipherPages.Decode(main, Wal((2, 2, untrusted), (2, 2, committed)), key, CancellationToken.None)[4096] == 30);
            Check("WAL 缺页扩容与超限长度被拒绝", Rejects(() => CipherPages.Decode(main, Wal((4, 4, MakePage(50, 4))), key, CancellationToken.None)) && Rejects(() => CipherPages.Decode(main, Wal((2, uint.MaxValue, second)), key, CancellationToken.None)));
            Check("页头声明缺失页时不生成不完整快照", Rejects(() => CipherPages.Decode([..MakePage(10, 1, 3), ..second], [], key, CancellationToken.None)));
            string badSnapshot = Path.Combine(root, "bad-page-fixture.db");
            File.WriteAllBytes(badSnapshot, damaged);
            bool preciseFailure = false;
            try { CipherPages.Snapshot(badSnapshot, key, CancellationToken.None); }
            catch (IOException ex) { preciseFailure = ex.Message.Contains("bad-page-fixture.db") && ex.Message.Contains("3 次") && ex.Message.Contains("第 2 页"); }
            Check("快照重试用尽后报告库名页号与最终失败", preciseFailure);
            string original = "  原始文字 &amp; <需求>\n第二行🙂\n";
            Check("原文空格换行表情不被转换", RawChatSource.DecodeContent(original, DBNull.Value) == original);
            using (var compressor = new Compressor(3))
            {
                byte[] compressed = compressor.Wrap(Encoding.UTF8.GetBytes(original)).ToArray();
                Check("压缩正文无损还原", RawChatSource.DecodeContent(DBNull.Value, compressed) == original);
            }
            string fixture = Path.Combine(root, "fixture.sqlite3");
            using (var db = new SqliteConnection($"Data Source={fixture};Pooling=False"))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE test(content TEXT); INSERT INTO test VALUES('原始文字🙂');"; command.ExecuteNonQuery();
            }
            using (var memory = MemoryDatabase.Open(File.ReadAllBytes(fixture)))
            {
                using var command = memory.CreateCommand(); command.CommandText = "SELECT content FROM test";
                Check("真实 SQLite 数据库在内存中读取", (string?)command.ExecuteScalar() == "原始文字🙂");
                rejected = false; try { command.CommandText = "DELETE FROM test"; command.ExecuteNonQuery(); } catch (SqliteException) { rejected = true; }
                Check("内存数据库拒绝写入", rejected);
            }
            var info = new RawMessageInfo("message_0.db", 1, 0, "test-sender", "测试发送人", DateTimeOffset.Now, 1, original);
            var record = new CaptureRecord("raw-test-1", "selected", "合成需求群", DateTimeOffset.Now, original, original, "", "合成测试", info);
            string legacyPath = Path.Combine(root, "store", "records.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            const string legacyRequirement = """{"Id":"legacy-requirement","Title":"历史合成需求","Notes":"保留原数据","Status":"待确认","CaptureId":"raw-test-1","CreatedAt":"2026-10-23T09:00:00+08:00"}""";
            using (var legacyStoreDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath }.ToString()))
            {
                legacyStoreDb.Open(); using var command = legacyStoreDb.CreateCommand();
                command.CommandText = "CREATE TABLE requirements(id TEXT PRIMARY KEY,json TEXT NOT NULL); INSERT INTO requirements VALUES('legacy-requirement',$json)";
                command.Parameters.AddWithValue("$json", legacyRequirement); command.ExecuteNonQuery();
            }
            using (var store = new Store(Path.Combine(root, "store")))
            {
                Check("首次采集逐条保存", store.AddMessages([record]) == 1);
                Check("重复轮询不重复保存", store.AddMessages([record with { ObservedAt = DateTimeOffset.Now.AddSeconds(10) }]) == 0 && store.Captures().Count == 1);
                Check("同文不同消息均保留", store.AddMessages([record with { Id = "raw-test-2", Message = info with { LocalId = 2 } }]) == 1 && store.Captures().Count == 2);
                Check("发送完成后更新同条消息编号", store.AddMessages([record with { Message = info with { ServerId = 500 } }]) == 1 && store.Captures().Count == 2 && store.Captures()[0].Message!.ServerId == 500);
                string export = Path.Combine(root, "export"); Exporter.Export(store, export);
                var records = File.ReadAllLines(Path.Combine(export, "聊天记录.jsonl")).Select(line => JsonSerializer.Deserialize<CaptureRecord>(line)!).ToList();
                Check("JSONL 保留原文和来源元数据", records[0].Text == original && records[0].Message!.RawContent == original && records[0].Message!.ServerId == 500);
                Check("Markdown 保留消息来源锚点", File.ReadAllText(Path.Combine(export, "聊天记录.md")).Contains("id=\"record-raw-test-1\""));
                Check("CSV 公式按文本导出", Exporter.Csv(" =1+1").StartsWith("\"'"));
            }
            using (var reopened = new Store(Path.Combine(root, "store")))
            {
                using var reopenedLegacyDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath }.ToString());
                reopenedLegacyDb.Open(); using var command = reopenedLegacyDb.CreateCommand();
                command.CommandText = "SELECT json FROM requirements WHERE id='legacy-requirement'";
                Check("重启后去重正常且旧版需求数据原样保留", reopened.Captures().Count == 2 && (string?)command.ExecuteScalar() == legacyRequirement && reopened.AddMessages([record with { Message = info with { ServerId = 500 } }]) == 0);
            }
            DirectorySelfTest.Run(root, Check);
            ConversationSelfTest.Run(root, report, Check);
            PaginationSelfTest.Run(root, report, Check);
            ConversationOptionsSelfTest.Run(root, Check);
            ConversationSelectionSelfTest.Run(root, report, Check);
            AccountProfileSelfTest.Run(root, report, Check);
            ExportProgressSelfTest.Run(root, report, Check);
            ExportDirectorySelfTest.Run(root, Check);
            AttachmentSelfTest.Run(root, Check);
            HtmlExportSelfTest.Run(root, report, Check);
            ExportFileLocatorSelfTest.Run(root, Check);
            VoiceSelfTest.Run(root, Check);
            TranscriptionSelfTest.Run(root, Check);
            SpeechBackendSelfTest.Run(root, Check);
            MenuSettingsSelfTest.Run(root, report, Check);
            FocusSelfTest.Run(root, report, Check);
            WindowMessageSelfTest.Run(Check);
            if (Environment.GetEnvironmentVariable("RIPPER_TEST_SPEECH_MODEL") is { Length: > 0 } speechModel &&
                Environment.GetEnvironmentVariable("RIPPER_TEST_SPEECH_WAV") is { Length: > 0 } speechWave)
            {
                TranscriptionSelfTest.RunModel(root, speechModel, speechWave, report, Check);
                if (Environment.GetEnvironmentVariable("RIPPER_TEST_GPU") == "1")
                    SpeechBackendSelfTest.RunModel(root, speechModel, speechWave, report, Check);
            }
            SilkSelfTest.Run(root, Check);
        }
        catch (Exception ex) { results.Add(new { name = "异常", passed = false, detail = ex.ToString() }); Environment.ExitCode = 1; }
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
}
