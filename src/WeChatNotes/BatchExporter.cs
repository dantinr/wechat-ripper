using System.Text;

namespace WeChatNotes;

internal sealed record BatchExportResult(int Succeeded, int Failed)
{
    internal string Summary => $"成功 {Succeeded} 个会话，失败 {Failed} 个会话";
}

internal static class BatchExporter
{
    private sealed class Observer(Action<ExportProgress> report) : IProgress<ExportProgress>
    { public void Report(ExportProgress progress) => report(progress); }

    internal static BatchExportResult Export(Store store, IReadOnlyList<(string Id, string Name)> conversations, string folder,
        IProgress<ExportProgress> progress, VoiceTranscriptionOptions? transcription = null,
        Action<Store, string, string, IProgress<ExportProgress>>? export = null)
    {
        Directory.CreateDirectory(folder);
        using var engine = transcription == null || export != null ? null : new SpeechTranscriber(transcription);
        // Write results as each conversation finishes so completed exports remain
        // identifiable even if a later directory or report write fails.
        using var report = new StreamWriter(Path.Combine(folder, "导出结果.txt"), false, new UTF8Encoding(true)) { AutoFlush = true };
        report.WriteLine($"Ripper · 全部会话导出\n共 {conversations.Count} 个会话；范围为开始导出时采集记录中的有消息会话。\n");
        int succeeded = 0, failed = 0;
        for (int i = 0; i < conversations.Count; i++)
        {
            var conversation = conversations[i];
            string? destination = null;
            Exception? failure = null;
            var current = new Observer(p => progress.Report(p with { Conversation = $"{i + 1}/{conversations.Count} · {conversation.Name}" }));
            try
            {
                current.Report(new("准备导出"));
                destination = ExportDirectory.Create(folder, conversation.Name);
                if (export != null) export(store, destination, conversation.Id, current);
                else Exporter.Export(store, destination, conversation.Id, null, current, engine == null ? null : engine.Transcribe,
                    transcription?.Parallelism ?? 4, engine);
            }
            catch (Exception ex) { failure = ex; }
            if (failure == null)
            {
                succeeded++;
                report.WriteLine($"成功：{conversation.Name}\n目录：{Path.GetFileName(destination)}\n");
            }
            else
            {
                failed++;
                report.WriteLine($"失败：{conversation.Name}\n目录：{(destination == null ? "未创建" : Path.GetFileName(destination))}\n原因：{failure.Message}\n");
            }
            progress.Report(new("导出全部会话", i + 1, conversations.Count, $"成功 {succeeded} · 失败 {failed}"));
        }
        var result = new BatchExportResult(succeeded, failed);
        report.WriteLine(result.Summary);
        return result;
    }
}
