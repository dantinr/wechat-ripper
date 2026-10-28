using System.Text.Json;

namespace WeChatNotes;

internal sealed record SpeechSettings(bool GpuEnabled = true, int CpuParallelism = 4)
{
    internal static SpeechSettings Load(string root)
    {
        try
        {
            string path = Path.Combine(root, "speech-settings.json");
            if (File.Exists(path) && new FileInfo(path).Length <= 16384 && JsonSerializer.Deserialize<SpeechSettings>(File.ReadAllText(path)) is { } settings)
                return settings with { CpuParallelism = Math.Clamp(settings.CpuParallelism, 1, 8) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal void Save(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "speech-settings.json"), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this with { CpuParallelism = Math.Clamp(CpuParallelism, 1, 8) }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal VoiceTranscriptionOptions Options(string root) => new(SpeechModel.PathFor(root), CpuParallelism, GpuEnabled ? SpeechDevice.Auto : SpeechDevice.Cpu);
}
