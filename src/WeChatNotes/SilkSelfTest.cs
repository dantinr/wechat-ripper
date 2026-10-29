using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WeChatNotes;

internal static class SilkSelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        byte[]? firstWave = null;
        foreach (var fixture in SilkFixture.All)
        {
            byte[] wave = SilkAudio.ToWave(fixture.Bytes);
            check($"SILK {fixture.SourceRate} Hz / {fixture.PacketMs} ms 转换与独立 SDK 参考 PCM 逐字节一致",
                wave.Length == 44 + fixture.PcmBytes && Convert.ToHexString(SHA256.HashData(wave.AsSpan(44))) == fixture.PcmHash);
            firstWave ??= wave;
        }
        byte[] original = SilkFixture.All[0].Bytes, expected = firstWave!;
        check("WAV 使用标准 RIFF 容器和 24kHz 单声道 16位 PCM",
            expected.AsSpan(0, 4).SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(4)) == expected.Length - 8 &&
            expected.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) && BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(20)) == 1 &&
            BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(22)) == 1 && BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(24)) == 24000 &&
            BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(28)) == 48000 && BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(32)) == 2 &&
            BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(34)) == 16 && expected.AsSpan(36, 4).SequenceEqual("data"u8) &&
            BinaryPrimitives.ReadInt32LittleEndian(expected.AsSpan(40)) == expected.Length - 44);
        using (var stream = new MemoryStream(expected))
        using (var player = new System.Media.SoundPlayer(stream))
        {
            player.Load();
            check("生成的 WAV 可由 Windows 音频组件读取且测试不播放声音", player.IsLoadCompleted);
        }
        check("标准及微信 SILK 文件头输出一致并接受规范终止标记",
            SilkAudio.ToWave(original[1..]).SequenceEqual(expected) && SilkAudio.ToWave([.. original[1..], 255, 255]).SequenceEqual(expected));
        int firstPacketLength = BinaryPrimitives.ReadInt16LittleEndian(original.AsSpan(10));
        byte[] shortWave = SilkAudio.ToWave(original[..(12 + firstPacketLength)]);
        check("只有一包的短语音也可转换且不会被旧缓冲逻辑截断", shortWave.Length == 44 + 24000 / 50 * 2);

        bool Rejected(byte[] bytes)
        {
            try { SilkAudio.ToWave(bytes); return false; }
            catch (InvalidDataException) { return true; }
        }
        check("空输入和只有文件头的 SILK 不生成空 WAV", Rejected([]) && Rejected("#!SILK_V3"u8.ToArray()));
        check("截断帧负长度和尾部残缺数据被拒绝", Rejected(original[..^1]) && Rejected([.. "#!SILK_V3"u8.ToArray(), 254, 255]) && Rejected([.. original, 1]));
        check("超长单帧和终止标记后多余内容被拒绝", Rejected([.. "#!SILK_V3"u8.ToArray(), 1, 4, .. new byte[1025]]) && Rejected([.. original, 255, 255, 0]));
        check("SDK 返回解码错误时不把损坏数据伪装成成功转换", Rejected([.. "#!SILK_V3"u8.ToArray(), 3, 0, 10, 20, 30]));
        int maximumPackets = SilkAudio.MaximumPcmBytes / 2 / (SilkAudio.SampleRate / 50 * 5);
        check("解码前限制最大输出容量避免无界 PCM 分配", Rejected([.. "#!SILK_V3"u8.ToArray(), .. new byte[(maximumPackets + 1) * 2]]));

        string source = Path.Combine(root, "silk-account", "db_storage"), target = RawChatSource.TargetIdFor(source, "silk-test@chatroom");
        using var store = new Store(Path.Combine(root, "silk-store"));
        store.SaveTarget(new(target, "转换测试群", "", 0, 0, 0, 0, 0, 0, source, "silk-test@chatroom"));
        var time = new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(8));
        CaptureRecord Record(string id, long local) => new(id, target, "转换测试群", time, "<msg><voicemsg/></msg>", "", "", "合成转换测试",
            new("message_0.db", local, local, "synthetic", "合成发送人", time, 34, "<msg><voicemsg/></msg>"));
        var first = Record("converted-first", 1); var second = Record("converted-second", 2); var broken = Record("broken-silk", 3);
        var text = Record("ordinary-text", 4) with { Text = "转换失败不影响文字导出", Message = first.Message! with { RawType = 1 } };
        store.AddMessages([first, second, broken, text]);
        byte[] invalid = [2, .. "#!SILK_V3"u8.ToArray(), 4, 0, 10];
        Dictionary<string, VoicePayload> Read(string account, IReadOnlyList<CaptureRecord> records) => records.ToDictionary(r => r.Id,
            r => new VoicePayload(r.Id == broken.Id ? [.. invalid] : [.. original], "已读取"));
        string folder = Path.Combine(root, "WAV 导出 #测试"); Exporter.Export(store, folder, target, Read);
        string dialogue = File.ReadAllText(Path.Combine(folder, "对话内容.md")), html = ExportTestDocument.Html(folder);
        string[] Links(string document) => Regex.Matches(document, @"\]\(([^)]+)\)").Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).ToArray();
        string[] links = Links(dialogue), waveLinks = links.Where(p => Path.GetExtension(p) == ".wav").ToArray(), silkLinks = links.Where(p => Path.GetExtension(p) == ".silk").ToArray();
        byte[] ReadExport(string path) => File.ReadAllBytes(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
        check("聊天导出自动提供可播放 WAV 和原始 SILK 的有效相对链接",
            waveLinks.Length == 2 && silkLinks.Length == 3 && links.All(p => File.Exists(Path.Combine(folder, p))) && waveLinks.All(p => ReadExport(p).SequenceEqual(expected)));
        check("WAV 转换保留原始 SILK 字节且中文空格目录正常工作", silkLinks.Count(p => ReadExport(p).SequenceEqual(original)) == 2 && silkLinks.Any(p => ReadExport(p).SequenceEqual(invalid)));
        check("同秒语音的 WAV 分目录保存互不覆盖", waveLinks.Distinct().Count() == 2 && Path.GetFileName(waveLinks[0]) == Path.GetFileName(waveLinks[1]));
        check("转换失败保留原音频并在对话中说明且文字仍完整", dialogue.Contains("WAV 转换失败，已保留原始 SILK") && dialogue.Contains(text.Text) && Directory.GetFiles(folder, "*.wav", SearchOption.AllDirectories).Length == 2);
        string[] htmlRecords = ExportTestDocument.Records(folder);
        check("HTML 提供可播放 WAV 并保留转换失败状态", waveLinks.All(p => html.Contains(string.Join('/', p.Split('/').Select(Uri.EscapeDataString)))) && htmlRecords.Any(r => r.Contains("WAV 转换失败")));

        string collisionFolder = Path.Combine(root, "wav-collision");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target + "|" + first.Id))).ToLowerInvariant();
        string protectedWave = Path.Combine(collisionFolder, "语音", key, "语音_20261023_093000.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(protectedWave)!); File.WriteAllText(protectedWave, "protected-existing-file");
        var collision = new VoiceExporter(store, collisionFolder, Read).Copy([first])[first.Id];
        check("WAV 目标已存在时不覆盖或删除并保留本次原始 SILK", collision.RelativePath?.EndsWith(".silk", StringComparison.Ordinal) == true && collision.Status.Contains("转换失败") && File.ReadAllText(protectedWave) == "protected-existing-file" && File.ReadAllBytes(Path.Combine(collisionFolder, collision.RelativePath)).SequenceEqual(original));
        var nativeWave = new VoiceExporter(store, Path.Combine(root, "native-wave"), (_, records) => records.ToDictionary(r => r.Id, _ => new VoicePayload([.. expected], "已读取"))).Copy([first])[first.Id];
        check("本来就是 WAV 的语音直接保存不重复转码", nativeWave.RelativePath?.EndsWith(".wav", StringComparison.Ordinal) == true && nativeWave.OriginalPath == null && nativeWave.Status == "已导出原始音频");
    }
}
