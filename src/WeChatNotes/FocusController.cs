using System.Text.Json;

namespace WeChatNotes;

internal sealed record FocusSettings(Target[] Targets, FocusCalibration? Calibration)
{
    internal static FocusSettings Load(string root, string file = "focus-settings.json")
    {
        try
        {
            string path = Path.Combine(root, file);
            if (File.Exists(path) && new FileInfo(path).Length <= 128 * 1024 && JsonSerializer.Deserialize<FocusSettings>(File.ReadAllText(path)) is { Targets: not null } value)
                return value with { Targets = value.Targets.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Name) && t.SourceRoot != null &&
                    t.ConversationId?.EndsWith("@chatroom", StringComparison.Ordinal) == true).DistinctBy(t => t.Id).Take(20).ToArray() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new([], null);
    }
    internal void Save(string root, string file = "focus-settings.json")
    {
        Directory.CreateDirectory(root); string path = Path.Combine(root, file), temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record FocusSnapshot(bool Hidden, string Stamp, long LastTime, long LastLocal, string Sender)
{
    internal bool CoveredBy(IReadOnlyList<CaptureRecord> records) => LastLocal == 0 || records.Any(r => r.Message is { } m &&
        m.LocalId == LastLocal && m.SentAt.ToUnixTimeSeconds() == LastTime && m.SenderId == Sender);
}

internal sealed class FocusRuleState(Target target, string owner, IEnumerable<CaptureRecord> baseline, bool restoreOnStop = true)
{
    internal Target Target { get; } = target;
    internal string Owner { get; } = owner;
    internal bool RestoreOnStop { get; } = restoreOnStop;
    private readonly HashSet<string> seen = baseline.Where(r => r.TargetId == target.Id).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
    internal bool Released { get; private set; }
    internal bool Blocked { get; private set; }
    internal string? Stamp, AttemptedStamp;
    internal DateTimeOffset RetryAfter, AttemptedAt;
    internal string Status = "等待隐藏";
    internal void Block(string status) { Blocked = true; Status = status; }
    internal void Defer(string status) { RetryAfter = DateTimeOffset.UtcNow.AddSeconds(5); Status = status + " · 5秒后自动重试"; }
    internal void Retry()
    {
        if (Released) return;
        Blocked = false; Stamp = null; AttemptedStamp = null; RetryAfter = default; Status = "等待重新检查并隐藏";
        // Preserve seen messages and the released state. Retrying must neither treat
        // old @ messages as new nor discard unresolved new mention metadata.
    }
    internal bool Confirming(DateTimeOffset now) => AttemptedStamp != null && now - AttemptedAt < TimeSpan.FromSeconds(5);
    internal void Observe(IReadOnlyList<CaptureRecord> records)
    {
        if (Released || Blocked) return;
        var fresh = records.Where(r => r.TargetId == Target.Id && !seen.Contains(r.Id)).ToArray();
        if (fresh.Any(r => r.Message?.MentionedUsers == null)) { Block("无法确认提及元数据，已暂停隐藏"); return; }
        if (fresh.Any(r => r.Message!.SenderId != Owner && r.Message.MentionedUsers!.Any(id => id == Owner || id == "notify@all")))
        { Released = true; Status = "收到 @本人/所有人，已放行（重新开始可再次隐藏）"; }
        foreach (var record in fresh) seen.Add(record.Id);
        if (seen.Count > 20000) Block("本次消息较多，请停止后重新开始");
    }
}

internal sealed class FocusController(string root) : IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly Dictionary<string, RawChatSource> sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly WeChatListAutomation automation = new();
    private CancellationTokenSource cancellation = new();
    private Task? active;
    private long since;
    private FocusSettings pending = FocusSettings.Load(root, "focus-restore.json");
    internal FocusSettings Settings { get; private set; } = FocusSettings.Load(root);
    internal List<FocusRuleState> Rules { get; } = [];
    internal Dictionary<string, string> RecoveryStatus { get; } = [];
    internal bool Running { get; private set; }
    internal int PendingCount => pending.Targets.Length;
    internal IReadOnlyList<Target> PendingTargets => pending.Targets;
    internal bool Working => active is { IsCompleted: false };
    internal event Action? Changed;
    private void Report()
    {
        Changed?.Invoke();
        if (!File.Exists(Path.Combine(root, ".focus-diagnostics"))) return;
        try { File.WriteAllText(Path.Combine(root, "focus-diagnostics.json"), JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Running, Rules = Rules.Select(r => new { r.Status, r.Blocked, r.Released }), Recovery = RecoveryStatus.Values, PendingCount })); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal void Configure(FocusSettings settings)
    {
        if (Running || Working || PendingCount > 0) throw new InvalidOperationException("请先停止并恢复已隐藏的会话，再修改设置。");
        settings.Save(root); Settings = settings; Rules.Clear(); RecoveryStatus.Clear(); Changed?.Invoke();
    }
    private void Remember(Target target)
    {
        if (pending.Targets.Any(t => t.Id == target.Id)) return;
        var next = new FocusSettings([..pending.Targets, target], Settings.Calibration);
        next.Save(root, "focus-restore.json"); pending = next;
    }
    private void Forget(Target target)
    {
        var next = pending with { Targets = pending.Targets.Where(t => t.Id != target.Id).ToArray() };
        next.Save(root, "focus-restore.json"); pending = next;
    }
    private RawChatSource Source(Target target, CancellationToken token)
    {
        if (!sources.TryGetValue(target.SourceRoot!, out var source))
        {
            source = new(target.SourceRoot!);
            try { source.Sessions(null, token); sources.Add(target.SourceRoot!, source); }
            catch { source.Dispose(); throw; }
        }
        return source;
    }
    internal async Task StartAsync()
    {
        await PauseAsync();
        if (PendingCount > 0) throw new InvalidOperationException("还有待恢复会话，请先点击“停止并恢复”。");
        if (Settings.Targets.Length == 0 || Settings.Calibration?.Valid != true) throw new InvalidOperationException("请先选择群会话并校准会话列表。");
        cancellation.Dispose(); cancellation = new(); var token = cancellation.Token;
        Rules.Clear(); since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
        Running = true; timer.Tick -= Tick; timer.Tick += Tick;
        active = Initialize();
        try { await active; if (!token.IsCancellationRequested) timer.Start(); }
        catch { Running = false; throw; }
        finally { Changed?.Invoke(); }
        async Task Initialize()
        {
            var initialized = await Task.Run(() =>
            {
                var result = new List<FocusRuleState>();
                foreach (var group in Settings.Targets.GroupBy(t => t.SourceRoot!, StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    var source = Source(group.First(), token); var sessions = source.Sessions(null, token);
                    string owner = source.OwnUsername() ?? throw new InvalidOperationException("无法唯一确认微信本人账号，已停止隐藏。");
                    foreach (var target in group)
                    {
                        if (!source.UniqueGroupName(target.ConversationId!, target.Name) || !sessions.Any(s => s.Username == target.ConversationId && s.DisplayName == target.Name))
                            throw new InvalidOperationException("会话已改名或存在同名会话，请重新选择。");
                        var before = source.FocusState(target.ConversationId!, token);
                        var baseline = source.Read([target with { FromUnix = since }], token);
                        var after = source.FocusState(target.ConversationId!, token);
                        if (before.Stamp != after.Stamp) throw new InvalidOperationException("启动期间收到新消息，请再次点击开始隐藏。");
                        result.Add(new(target, owner, baseline, !before.Hidden) { Stamp = before.Stamp });
                    }
                }
                return result;
            }, token);
            Rules.AddRange(initialized);
        }
    }
    internal void RetryPaused()
    {
        if (!Running || Working) return;
        foreach (var rule in Rules.Where(r => r.Blocked)) rule.Retry();
        Report();
    }
    private async void Tick(object? sender, EventArgs e)
    {
        if (!Running || Working) return;
        active = PollAsync();
        try { await active; }
        finally { active = null; Report(); }
    }
    private async Task PollAsync()
    {
        var token = cancellation.Token;
        foreach (var rule in Rules)
        {
            if (!Running || token.IsCancellationRequested) break;
            if (rule.Blocked || rule.RetryAfter > DateTimeOffset.UtcNow) continue;
            try
            {
                rule.Status = "正在读取会话状态"; Report();
                var source = sources[rule.Target.SourceRoot!];
                var current = await Task.Run(() => source.FocusState(rule.Target.ConversationId!, token), token);
                if (current.Stamp != rule.Stamp)
                {
                    var records = await Task.Run(() => source.Read([rule.Target with { FromUnix = since }], token), token);
                    if (!current.CoveredBy(records)) { rule.Status = "最新消息尚未完整读取，暂缓隐藏"; continue; }
                    rule.Observe(records); rule.Stamp = current.Stamp;
                }
                if (rule.Blocked) continue;
                if (rule.Released)
                {
                    rule.Status = "收到 @本人/所有人，已放行（重新开始可再次隐藏）";
                    if (current.Hidden && pending.Targets.Any(t => t.Id == rule.Target.Id))
                        rule.Status = (await automation.RestoreAsync(rule.Target.Name, Settings.Calibration!, false, token)).Status;
                    else if (!current.Hidden && pending.Targets.Any(t => t.Id == rule.Target.Id)) Forget(rule.Target);
                    continue;
                }
                if (current.Hidden) { rule.AttemptedStamp = null; rule.Status = "已隐藏 · 等待 @本人"; continue; }
                if (rule.AttemptedStamp == current.Stamp)
                {
                    if (rule.Confirming(DateTimeOffset.UtcNow)) rule.Status = "等待微信保存隐藏状态";
                    else rule.Block("微信尚未确认隐藏，请点击“重试暂停”");
                    continue;
                }
                var result = await automation.HideAsync(rule.Target.Name, Settings.Calibration!, async () =>
                {
                    var latest = await Task.Run(() => source.FocusState(rule.Target.ConversationId!, token), token);
                    return Running && !latest.Hidden && latest.Stamp == current.Stamp && !rule.Released && !rule.Blocked;
                }, () => { if (rule.RestoreOnStop) Remember(rule.Target); }, token, status => { rule.Status = status; Report(); });
                rule.Status = result.Status;
                if (result.Acted) { rule.AttemptedStamp = current.Stamp; rule.AttemptedAt = DateTimeOffset.UtcNow; }
                if (result.Blocked) rule.Block(result.Status);
                else if (!result.Acted) rule.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(3);
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { rule.Defer("读取微信画面超时"); }
            catch (IOException) { rule.Defer("微信数据正在更新或暂时无法读取"); }
            catch (InvalidOperationException) when (WeChatListAutomation.MainWindow() is var window && (window == 0 || NativeWindows.IsIconic(window)))
            { rule.Defer("微信窗口不可用，请登录或还原微信"); }
            catch (Exception ex) { rule.Block($"读取或界面验证失败（{ex.GetType().Name}），请点击“重试暂停”"); }
        }
        Report();
    }
    internal async Task PauseAsync()
    {
        Running = false; timer.Stop(); cancellation.Cancel();
        if (active != null) try { await active; } catch (Exception) { /* Caller already reports initialization/restore failures. */ }
        active = null;
        foreach (var source in sources.Values) source.Dispose(); sources.Clear(); Changed?.Invoke();
    }
    internal async Task StopAsync()
    {
        await PauseAsync();
        active = RestorePending();
        try { await active; }
        finally
        {
            foreach (var source in sources.Values) source.Dispose(); sources.Clear(); Report();
        }
    }
    private async Task RestorePending()
    {
        foreach (var target in pending.Targets.ToArray())
        {
            var rule = Rules.FirstOrDefault(r => r.Target.Id == target.Id);
            try
            {
                var source = await Task.Run(() => Source(target, CancellationToken.None));
                var state = await Task.Run(() => source.FocusState(target.ConversationId!, CancellationToken.None));
                if (state.Hidden)
                {
                    var sessions = await Task.Run(() => source.Sessions(null, CancellationToken.None));
                    if (!source.UniqueGroupName(target.ConversationId!, target.Name) || !sessions.Any(s => s.Username == target.ConversationId && s.DisplayName == target.Name))
                        throw new InvalidOperationException("会话改名或同名，请在微信中手动恢复。");
                    if (pending.Calibration?.Valid != true) throw new InvalidOperationException("缺少有效校准，请在微信中手动恢复。");
                    var result = await automation.RestoreAsync(target.Name, pending.Calibration, true, CancellationToken.None);
                    if (!result.Acted) throw new InvalidOperationException(result.Status);
                    // UI updates and the committed session WAL may lag the click.
                    for (int attempt = 0; attempt < 8 && state.Hidden; attempt++)
                    {
                        await Task.Delay(350);
                        state = await Task.Run(() => source.FocusState(target.ConversationId!, CancellationToken.None));
                    }
                    if (state.Hidden) throw new InvalidOperationException("微信未确认恢复，请重试或在微信中手动搜索打开该群。");
                }
                Forget(target); RecoveryStatus[target.Id] = "已恢复显示"; if (rule != null) rule.Status = "已恢复显示";
            }
            catch (Exception ex) { RecoveryStatus[target.Id] = "待恢复：" + ex.Message; if (rule != null) rule.Status = RecoveryStatus[target.Id]; }
        }
    }
    internal Task<Bitmap> CaptureAsync(CancellationToken token) => automation.CaptureAsync(token);
    public void Dispose() { timer.Dispose(); cancellation.Cancel(); cancellation.Dispose(); automation.Dispose(); foreach (var source in sources.Values) source.Dispose(); }
}
