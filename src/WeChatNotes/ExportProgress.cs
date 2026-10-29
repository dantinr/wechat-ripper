namespace WeChatNotes;

public sealed record ExportProgress(string Stage, int Completed = 0, int Total = 0, string? Detail = null, string? Conversation = null)
{
    public int? Percent => Total > 0 ? (int)Math.Clamp((long)Completed * 100 / Total, 0, 100) : null;
}

internal sealed class ExportProgressReporter(IProgress<ExportProgress>? progress) : IProgress<ExportProgress>
{
    private string? stage;
    private string? detail;
    private long lastReport;
    public void Report(ExportProgress value)
    {
        if (progress == null) return;
        long now = Environment.TickCount64;
        // Keep large exports from flooding the UI thread with one callback per message.
        if (value.Stage == stage && value.Detail == detail && value.Completed < value.Total && now - lastReport < 100) return;
        stage = value.Stage; detail = value.Detail; lastReport = now; progress.Report(value);
    }
}
