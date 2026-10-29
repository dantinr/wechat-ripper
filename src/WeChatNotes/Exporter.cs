using System.Text;

namespace WeChatNotes;

public static class Exporter
{
    public static void Export(Store store, string folder, string? targetId = null, IProgress<ExportProgress>? progress = null, VoiceTranscriptionOptions? transcription = null)
    {
        using var engine = transcription == null ? null : new SpeechTranscriber(transcription);
        Export(store, folder, targetId, null, progress, engine == null ? null : engine.Transcribe, transcription?.Parallelism ?? 4, engine);
    }

    internal static void Export(Store store, string folder, string? targetId,
        Func<string, IReadOnlyList<CaptureRecord>, Dictionary<string, VoicePayload>>? readVoices, IProgress<ExportProgress>? progress = null,
        Func<string, VoiceTranscript>? transcribe = null, int transcriptionParallelism = 4, SpeechTranscriber? transcriptionEngine = null)
    {
        var updates = new ExportProgressReporter(progress);
        updates.Report(new("准备导出记录"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "语音"));
        Directory.CreateDirectory(Path.Combine(folder, "附件"));
        var captures = store.Captures(targetId: targetId);
        var content = new Dictionary<string, ReadableMessage>(StringComparer.Ordinal);
        int completed = 0;
        updates.Report(new("整理消息内容", 0, captures.Count));
        foreach (var capture in captures)
        {
            content[capture.Id] = MessageContent.Read(capture);
            updates.Report(new("整理消息内容", ++completed, captures.Count));
        }
        var copier = new AttachmentExporter(store, folder);
        var attachments = new Dictionary<string, ExportedAttachment>(StringComparer.Ordinal);
        var files = captures.Where(c => content[c.Id].File != null).ToList();
        completed = 0;
        if (files.Count > 0) updates.Report(new("导出文件附件", 0, files.Count));
        foreach (var capture in files)
        {
            attachments[capture.Id] = copier.Copy(capture, content[capture.Id].File!);
            updates.Report(new("导出文件附件", ++completed, files.Count));
        }
        var voices = new VoiceExporter(store, folder, readVoices).Copy(captures, updates);
        var transcripts = new Dictionary<string, VoiceTranscript>(StringComparer.Ordinal);
        if (transcribe != null)
        {
            completed = 0; updates.Report(new("离线语音转文字", 0, voices.Count, transcriptionEngine?.Description));
            var entries = voices.ToArray();
            var results = new VoiceTranscript[entries.Length];
            var progressLock = new object();
            void BackendChanged(string description)
            {
                lock (progressLock) updates.Report(new("离线语音转文字", completed, voices.Count, description));
            }
            if (transcriptionEngine != null) transcriptionEngine.StatusChanged += BackendChanged;
            try
            {
                Parallel.For(0, entries.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(transcriptionParallelism, 1, 8) }, i =>
                {
                    var voice = entries[i].Value;
                    if (voice.RelativePath is not { } path) results[i] = new(null, "本地音频未导出，无法转写");
                    else if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) results[i] = new(null, "当前音频未转换为 WAV，请收听原音");
                    else
                    {
                        try { results[i] = transcribe(Path.Combine(folder, path)); }
                        catch (Exception) { results[i] = new(null, "语音识别失败，请收听原音后重试"); }
                    }
                    lock (progressLock) updates.Report(new("离线语音转文字", ++completed, voices.Count, transcriptionEngine?.Description));
                });
            }
            finally { if (transcriptionEngine != null) transcriptionEngine.StatusChanged -= BackendChanged; }
            for (int i = 0; i < entries.Length; i++) transcripts[entries[i].Key] = results[i];
        }
        var utf8 = new UTF8Encoding(true);
        completed = 0; updates.Report(new("生成对话文档", 0, captures.Count));
        using (var dialogue = new StreamWriter(Path.Combine(folder, "对话内容.md"), false, utf8))
        {
            dialogue.WriteLine("# 对话内容\n");
            bool multiple = captures.Select(c => c.TargetId).Distinct(StringComparer.Ordinal).Skip(1).Any();
            foreach (var capture in captures.OrderBy(c => c.Message?.SentAt ?? c.ObservedAt))
            {
                string sender = capture.Message?.DisplaySender ?? "旧版 OCR（发送人待核对）";
                string prefix = multiple ? Md(capture.TargetName) + " · " : "";
                string body = attachments.TryGetValue(capture.Id, out var attachment) ? AttachmentText(attachment)
                    : voices.TryGetValue(capture.Id, out var voice) ? TranscriptText(transcripts.GetValueOrDefault(capture.Id)) + VoiceText(voice) : Md(content[capture.Id].Text);
                var time = (capture.Message?.SentAt ?? capture.ObservedAt).ToOffset(TimeSpan.FromHours(8));
                dialogue.WriteLine($"{prefix}{Md(sender)}：{body} （{(capture.Message == null ? "记录于 " : "")}{time:yyyy-MM-dd HH:mm:ss zzz}）\n".ReplaceLineEndings(Environment.NewLine));
                updates.Report(new("生成对话文档", ++completed, captures.Count));
            }
        }
        HtmlExporter.Write(folder, captures, content, attachments, voices, transcripts, store.TargetSources(), updates);
        updates.Report(new("导出完成", 1, 1));
    }

    private static string AttachmentText(ExportedAttachment file) => file.RelativePath is { } path
        ? $"文件 [{Md(file.Name)}]({string.Join('/', path.Split('/').Select(Uri.EscapeDataString))})"
        : $"文件：{Md(file.Name.Length == 0 ? "未提供文件名" : file.Name)}（附件未导出：{Md(file.Status)}）";
    private static string TranscriptText(VoiceTranscript? transcript) => transcript == null ? ""
        : transcript.Text is { Length: > 0 } text ? $"{Md(text)}〔语音转写〕  \n"
        : $"〔语音未转写：{Md(transcript.Status)}〕  \n";
    private static string VoiceText(ExportedVoice voice) => voice.RelativePath is { } path
        ? $"语音 [{Md(voice.Name)}]({string.Join('/', path.Split('/').Select(Uri.EscapeDataString))})" + (voice.OriginalPath is { } original
            ? $"（[原始 SILK]({string.Join('/', original.Split('/').Select(Uri.EscapeDataString))})）" : $"（{Md(voice.Status)}）")
        : $"[语音]（语音未导出：{Md(voice.Status)}）";
    private static string Md(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").ReplaceLineEndings("  " + Environment.NewLine);
}
