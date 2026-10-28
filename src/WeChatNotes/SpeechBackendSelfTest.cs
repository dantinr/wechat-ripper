using System.Diagnostics;
using System.Text.Json;

namespace WeChatNotes;

internal static class SpeechBackendSelfTest
{
    private sealed class Worker(Func<string, VoiceTranscript> transcribe) : IGpuSpeechWorker
    {
        internal bool Disposed;
        public VoiceTranscript Transcribe(string path) => transcribe(path);
        public void Dispose() => Disposed = true;
    }
    private sealed class Observer(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => action(value); }

    internal static void Run(string root, Action<string, bool> check)
    {
        int gpuStarts = 0, cpuCalls = 0;
        using (var engine = new SpeechTranscriber(new("missing-model", Device: SpeechDevice.Cpu),
            () => { gpuStarts++; throw new IOException(); }, _ => { cpuCalls++; return new("CPU 结果", "已离线转写"); }))
        {
            var result = engine.Transcribe("synthetic-wave");
            check("选择CPU时跳过GPU探测且保留默认四路设置", gpuStarts == 0 && cpuCalls == 1 && result.Model.Contains("CPU") && engine.Description.Contains("4 路") && new VoiceTranscriptionOptions("model").Device == SpeechDevice.Auto);
        }

        int active = 0, peak = 0; var gate = new object();
        var worker = new Worker(path =>
        {
            lock (gate) peak = Math.Max(peak, ++active);
            try { Thread.Sleep(10); return new("GPU " + path, "已离线转写"); }
            finally { lock (gate) active--; }
        });
        var results = new VoiceTranscript[8]; gpuStarts = 0; cpuCalls = 0;
        using (var engine = new SpeechTranscriber(new("missing-model"), () => { gpuStarts++; return worker; }, _ => { cpuCalls++; return new(null, "不应调用"); }))
        {
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => results[i] = engine.Transcribe(i.ToString()));
            check("自动模式复用单个GPU进程并将并发限制为一路", gpuStarts == 1 && cpuCalls == 0 && peak == 1 && active == 0 && engine.Description.Contains("GPU"));
            check("GPU并发请求仍按原记录关联文字与设备信息", results.Select((r, i) => r.Text == "GPU " + i && r.Model.Contains("GPU Vulkan")).All(v => v));
        }
        check("导出结束释放GPU进程及模型", worker.Disposed);

        foreach (var device in new[] { SpeechDevice.Auto, SpeechDevice.Gpu })
        {
            gpuStarts = 0; cpuCalls = 0;
            using var engine = new SpeechTranscriber(new("missing-model", Device: device), () => { gpuStarts++; throw new IOException("synthetic-driver-private-error"); }, _ => { Interlocked.Increment(ref cpuCalls); return new("CPU 回退", "已离线转写"); });
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => results[i] = engine.Transcribe(i.ToString()));
            check(device == SpeechDevice.Auto ? "自动检测GPU失败后只探测一次并回退CPU" : "指定GPU但不可用时继续用CPU完成全部语音",
                gpuStarts == 1 && cpuCalls == 8 && results.All(r => r.Text == "CPU 回退" && r.Model.Contains("CPU")) && engine.Description.Contains("已回退 CPU") && !engine.Description.Contains("synthetic-driver"));
        }

        int calls = 0; cpuCalls = 0;
        var crashing = new Worker(_ => ++calls == 1 ? new("首条GPU结果", "已离线转写") : throw new IOException("synthetic-native-crash"));
        using (var engine = new SpeechTranscriber(new("missing-model", Device: SpeechDevice.Gpu), () => crashing, path => { cpuCalls++; return new("重试 " + path, "已离线转写"); }))
        {
            var first = engine.Transcribe("first"); var second = engine.Transcribe("second"); var third = engine.Transcribe("third");
            check("GPU中途失败释放设备并重试当前语音且保留之前结果", first.Text == "首条GPU结果" && second.Text == "重试 second" && third.Text == "重试 third" && calls == 2 && cpuCalls == 2 && crashing.Disposed);
        }

        var progress = new List<ExportProgress>(); var updates = new ExportProgressReporter(new Observer(progress.Add));
        updates.Report(new("离线语音转文字", 0, 3, "检测 GPU"));
        updates.Report(new("离线语音转文字", 0, 3, "GPU · 单路"));
        updates.Report(new("离线语音转文字", 0, 3, "GPU 不可用，已回退 CPU"));
        check("设备变化立即报告且不会伪增完成条数", progress.Count == 3 && progress.All(p => p.Completed == 0) && progress.Last().Detail!.Contains("回退 CPU"));
        var start = GpuSpeechWorker.CreateStartInfo();
        check("GPU使用隐藏独立进程和重定向管道而不经Shell传递语音", start.CreateNoWindow && !start.UseShellExecute && start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError && start.ArgumentList.Last() == "--speech-worker" && !start.Arguments.Contains("missing-model"));

    }

    internal static void RunModel(string root, string modelPath, string wave, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "gpu-model-integration"));
        store.SaveTarget(new("gpu", "GPU合成测试群", "", 0, 0, 0, 0, 0, 0, Path.Combine(root, "gpu-account", "db_storage"), "gpu@chatroom"));
        var time = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(8));
        store.AddMessages(Enumerable.Range(1, 4).Select(i => new CaptureRecord("gpu" + i, "gpu", "GPU合成测试群", time, "<msg><voicemsg/></msg>", "", "", "合成TTS",
            new("message_0.db", i, i, "sender", "合成发送人", time.AddSeconds(i), 34, "合成原始语音XML"))));
        Dictionary<string, VoicePayload> Read(string _, IReadOnlyList<CaptureRecord> records) => records.ToDictionary(r => r.Id, _ => new VoicePayload(File.ReadAllBytes(wave), "合成TTS"));
        var progress = new List<ExportProgress>();
        var watch = Stopwatch.StartNew();
        string gpuFolder = Path.Combine(root, "gpu-export");
        using (var engine = new SpeechTranscriber(new(modelPath, Device: SpeechDevice.Gpu)))
        {
            Exporter.Export(store, gpuFolder, "gpu", Read, new Observer(progress.Add), engine.Transcribe, 4, engine);
            check("发布版实际使用GPU模型及单路工作进程导出合成语音", engine.Description == "GPU（Vulkan）· 单路");
        }
        double gpuSeconds = watch.Elapsed.TotalSeconds;
        var gpuRecords = File.ReadAllLines(Path.Combine(gpuFolder, "聊天记录.jsonl")).Select(line => JsonSerializer.Deserialize<CaptureRecord>(line)!).ToArray();
        check("GPU转写随HTML和聊天文档导出且不改原文或顺序", gpuRecords.Length == 4 && gpuRecords.All(r => r.VoiceTranscription?.Text?.Contains("明天下午三点开会") == true && r.VoiceTranscription.Model.Contains("GPU Vulkan") && r.Message!.RawContent == "合成原始语音XML") && gpuRecords.Select(r => r.Id).SequenceEqual(store.Captures().Select(r => r.Id)));
        check("GPU初始化完成后立即显示实际设备并保留零完成条数", progress.Any(p => p.Stage == "离线语音转文字" && p.Completed == 0 && p.Detail == "GPU（Vulkan）· 单路"));
        watch.Restart();
        string cpuFolder = Path.Combine(root, "cpu-device-export");
        using (var engine = new SpeechTranscriber(new(modelPath, Device: SpeechDevice.Cpu)))
            Exporter.Export(store, cpuFolder, "gpu", Read, null, engine.Transcribe, 4, engine);
        double cpuSeconds = watch.Elapsed.TotalSeconds;
        var cpuRecords = File.ReadAllLines(Path.Combine(cpuFolder, "聊天记录.jsonl")).Select(line => JsonSerializer.Deserialize<CaptureRecord>(line)!).ToArray();
        check("同一主进程中可从GPU切换CPU并正确识别合成语音", cpuRecords.All(r => r.VoiceTranscription?.Text?.Contains("明天下午三点开会") == true && r.VoiceTranscription.Model.Contains("CPU")));
        File.WriteAllText(Path.ChangeExtension(report, ".speech-benchmark.json"), JsonSerializer.Serialize(new { Voices = 4, GPU = "Vulkan", GPUSeconds = gpuSeconds, CPUParallelism = 4, CPUSeconds = cpuSeconds }, new JsonSerializerOptions { WriteIndented = true }));
        var worker = new GpuSpeechWorker(modelPath);
        using var process = Process.GetProcessById(worker.ProcessId);
        process.Kill(entireProcessTree: true); process.WaitForExit();
        using (var engine = new SpeechTranscriber(new(modelPath, Device: SpeechDevice.Gpu), () => worker, _ => new("合成CPU回退结果", "已离线转写")))
        {
            var recovered = engine.Transcribe(wave);
            check("真实GPU工作进程异常退出后主进程仍可回退CPU", recovered.Text == "合成CPU回退结果" && recovered.Model.Contains("CPU") && engine.Description.Contains("回退 CPU"));
        }
    }
}
