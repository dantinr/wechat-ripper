using System.Net;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WeChatNotes;

internal static class HtmlExporter
{
    internal static void Write(string folder, IReadOnlyList<CaptureRecord> captures,
        IReadOnlyDictionary<string, ReadableMessage> content, IReadOnlyDictionary<string, ExportedAttachment> attachments,
        IReadOnlyDictionary<string, ExportedVoice> voices, IReadOnlyDictionary<string, VoiceTranscript> transcripts,
        IReadOnlyDictionary<string, string> sourceRoots, IProgress<ExportProgress> progress)
    {
        var index = ExportFileLocator.WriteIndex(folder, attachments.Values.Select(a => a.RelativePath)
            .Concat(voices.Values.SelectMany(v => new[] { v.RelativePath, v.OriginalPath })));
        using var html = new StreamWriter(Path.Combine(folder, "聊天记录.html"), false, new UTF8Encoding(false));
        html.WriteLine(Head);
        string title = captures.Select(c => c.TargetName).Distinct(StringComparer.Ordinal).Take(2).ToArray() is [var name] ? name : "聊天记录";
        var senders = captures.OrderBy(c => c.Message?.SentAt ?? c.ObservedAt).Where(c => c.Message != null).Select(c => (Key: SenderKey(c), Name: c.Message!.DisplaySender)).DistinctBy(s => s.Key).ToArray();
        var avatars = senders.Select((s, i) => (s.Key, Color: i % 6)).ToDictionary(s => s.Key, s => s.Color, StringComparer.Ordinal);
        var ownSenders = FindOwnSenders(captures, sourceRoots);
        html.WriteLine($"<div class=window data-export-id=\"{index.Id}\">");
        html.WriteLine("""
            <aside class=rail aria-label="档案导航"><div class=brand title=Ripper>R</div>
            <a class=rail-chat href="#records" aria-label="聊天记录"><svg viewBox="0 0 24 24" aria-hidden=true><path d="M4 4h16v12H9l-5 4V4Z"/><path d="M8 9h8M8 12h5"/></svg></a>
            <a href="#export-files" aria-label="导出文件"><svg viewBox="0 0 24 24" aria-hidden=true><path d="M3 6h7l2 2h9v12H3V6Z"/></svg></a><span class=rail-bottom>本地</span></aside>
            <aside class=sidebar aria-label="会话信息"><div class=sidebar-title>聊天记录<span>离线档案</span></div>
            """);
        html.WriteLine($"<div class=conversation-item><span class=group-avatar aria-hidden=true>聊</span><div><strong>{H(title)}</strong><small>共 {captures.Count:N0} 条记录</small></div></div>");
        html.WriteLine("<div class=archive-facts><p class=section-label>档案信息</p><p>按消息发送时间排列<br>时区 UTC+08:00</p><p>附件和语音保存在此目录，<br>移动时请保留整个文件夹。</p></div><div class=sidebar-footer><strong>Ripper</strong><p>Free your conversations.<br>Own your records.</p><span>本地保存 · 离线阅读</span></div></aside><main class=chat>");
        html.WriteLine($"<header class=chat-header><div><h1>{H(title)}</h1><p>聊天记录 · 共 {captures.Count:N0} 条记录</p></div>");
        html.WriteLine("""
            <details class=export-menu id=export-files><summary>导出文件 <span aria-hidden=true>⌄</span></summary><nav class=downloads aria-label="导出文件"><a href="对话内容.md" target=_blank rel=noopener>对话 Markdown ↗</a><a href="聊天记录.md" target=_blank rel=noopener>原文 Markdown ↗</a><a href="聊天记录.csv" target=_blank rel=noopener>CSV 表格 ↗</a><a href="聊天记录.jsonl" target=_blank rel=noopener>JSONL 数据 ↗</a><a href="使用说明.txt" target=_blank rel=noopener>使用说明 ↗</a><p>“定位文件”通过 Ripper 打开 Windows 文件夹并选中文件。首次使用请先运行新版 Ripper，浏览器询问时选择打开；也可使用“浏览目录”或展开文件路径查找。请保留 ripper-files.json 文件定位索引。</p></nav></details></header>
            <section class=tools aria-label="查找记录"><label class=search><span class=sr-only>搜索记录</span><svg viewBox="0 0 24 24" aria-hidden=true><circle cx="10" cy="10" r="6"/><path d="m15 15 5 5"/></svg><input id=search type=search placeholder="搜索内容、发送人、文件名…" autocomplete=off></label><label><span class=sr-only>显示</span><select id=kind><option value=all>全部记录</option><option value=file>文件附件</option><option value=voice>语音消息</option></select></label><label class=self-label>本人<select id=self title="自动识别来源账号的本人消息，可手动更改右侧发送人"><option value="">未指定</option>
            """);
        if (ownSenders.Count > 0) html.WriteLine("<option value=\"__auto\" selected>自动识别</option>");
        foreach (var sender in senders) html.WriteLine($"<option value=\"{H(sender.Key)}\">{H(sender.Name)}</option>");
        html.WriteLine("""
            </select></label></section><p id=result role=status aria-live=polite></p><div class=thread>
            <noscript><p class=notice>浏览器未启用 JavaScript，显示全部记录；仍可查看原文和打开附件。</p></noscript><div id=records>
            """);
        int number = 0;
        progress.Report(new("生成 HTML 聊天目录", 0, captures.Count));
        foreach (var capture in captures.OrderBy(c => c.Message?.SentAt ?? c.ObservedAt))
        {
            number++;
            attachments.TryGetValue(capture.Id, out var attachment);
            voices.TryGetValue(capture.Id, out var voice);
            transcripts.TryGetValue(capture.Id, out var transcript);
            string kind = attachment != null ? "file" : voice != null ? "voice" : "text";
            var message = capture.Message;
            string sender = message?.DisplaySender ?? "旧版 OCR（发送人待核对）";
            var time = (message?.SentAt ?? capture.ObservedAt).ToOffset(TimeSpan.FromHours(8));
            string senderKey = SenderKey(capture);
            bool own = ownSenders.TryGetValue(capture.TargetId, out string? owner) && senderKey == owner;
            string initial = string.IsNullOrWhiteSpace(sender) ? "?" : StringInfo.GetNextTextElement(sender.Trim());
            // Chat text is always encoded markup, never executable HTML or JavaScript.
            html.WriteLine($"<article class=\"record{(own ? " self" : "")}\" id=\"record-{number}\" data-kind=\"{kind}\" data-sender=\"{H(senderKey)}\" data-own=\"{(own ? "true" : "false")}\" data-time=\"{time.ToUnixTimeSeconds()}\"><time class=message-time>{(message == null ? "记录于 " : "")}{time:yyyy-MM-dd HH:mm:ss}</time><div class=message-row><div class=\"avatar avatar-{avatars.GetValueOrDefault(senderKey)}\" aria-hidden=true>{H(initial)}</div><div class=message-main><div class=sender-row><strong title=\"{H(sender)}\">{H(sender)}</strong><span class=number>#{number:D4}</span></div><div class=bubble>");
            if (attachment == null && voice == null) html.WriteLine($"<div class=body>{H(content[capture.Id].Text)}</div>");
            if (attachment != null)
            {
                if (attachment.RelativePath is { } path) WriteFile(html, path, attachment.Name, "文件附件");
                else html.WriteLine($"<div class=body>文件：{H(attachment.Name)}</div><p class=notice>附件未导出：{H(attachment.Status)}</p>");
            }
            if (voice != null)
            {
                if (transcript != null)
                    html.WriteLine($"<div class=transcript><span class=tag>语音转写</span><div class=body>{H(transcript.Text ?? transcript.Status)}</div></div>");
                if (voice.RelativePath is { } path)
                {
                    if (Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".mp3" or ".ogg")
                        html.WriteLine($"<audio controls preload=none src=\"{Href(path)}\"><a href=\"{Href(path)}\" target=_blank rel=noopener>播放语音 ↗</a></audio>");
                    else html.WriteLine($"<a href=\"{Href(path)}\" target=_blank rel=noopener>打开语音 ↗</a><p class=notice>{H(voice.Status)}</p>");
                }
                else html.WriteLine($"<p class=notice>语音未导出：{H(voice.Status)}</p>");
            }
            html.WriteLine($"</div><details class=source><summary>原文与来源</summary><dl><dt>会话</dt><dd>{H(capture.TargetName)}</dd><dt>发送时间</dt><dd>{time:yyyy-MM-dd HH:mm:ss zzz}</dd><dt>记录 ID</dt><dd>{H(capture.Id)}</dd>");
            if (message != null)
                html.WriteLine($"<dt>消息编号</dt><dd>{message.ServerId}</dd><dt>本地编号</dt><dd>{message.LocalId}</dd><dt>来源数据库</dt><dd>{H(message.SourceDatabase)}</dd><dt>群昵称</dt><dd>{H(message.GroupNickname ?? "")}</dd>");
            html.WriteLine($"<dt>记录时间</dt><dd>{capture.ObservedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss zzz}</dd><dt>说明</dt><dd>{H(capture.Note)}</dd></dl><h3>消息原文</h3><pre>{H(capture.Text)}</pre>");
            if (message != null && message.RawContent != capture.Text) html.WriteLine($"<h3>数据库原始正文</h3><pre>{H(message.RawContent)}</pre>");
            html.WriteLine("</details></div></div></article>");
            progress.Report(new("生成 HTML 聊天目录", number, captures.Count));
        }
        html.WriteLine("""
            </div><p id=empty class=notice hidden>没有匹配的记录。</p></div><nav class=pager aria-label="记录分页" hidden><button id=prev type=button>← 上一页</button><span id=page aria-live=polite></span><button id=next type=button>下一页 →</button></nav>
            """);
        string alignment = ownSenders.Count > 0 ? "已识别本人：本人消息在右侧，其他消息在左侧" : "未识别本人，请通过上方“本人”选择右侧发送人";
        html.WriteLine($"<footer>{H(alignment)} · 语音转写请结合原音核对</footer></main></div>");
        html.WriteLine("""
            <script>
            (() => {
              'use strict';
              const exportId = document.querySelector('[data-export-id]').dataset.exportId;
              if (location.protocol === 'file:') document.querySelectorAll('a[data-locate]').forEach(link => {
                const file = new URL(link.dataset.locate, location.href);
                link.href = 'ripper-locate://file/' + exportId + '?path=' + encodeURIComponent(file.href);
                link.removeAttribute('target');
              });
              const records = Array.from(document.querySelectorAll('.record'));
              const search = document.getElementById('search'), kind = document.getElementById('kind'), self = document.getElementById('self');
              const prev = document.getElementById('prev'), next = document.getElementById('next');
              const page = document.getElementById('page'), result = document.getElementById('result');
              const pager = document.querySelector('.pager'), empty = document.getElementById('empty');
              const text = records.map(r => r.textContent.toLocaleLowerCase());
              const pageSize = 50;
              let matches = records, current = 0;
              function render() {
                const pages = Math.max(1, Math.ceil(matches.length / pageSize));
                current = Math.max(0, Math.min(current, pages - 1));
                records.forEach(r => { r.hidden = true; });
                let lastTime = null;
                matches.slice(current * pageSize, (current + 1) * pageSize).forEach(r => {
                  r.hidden = false;
                  const time = Number(r.dataset.time);
                  const showTime = lastTime === null || time - lastTime >= 300;
                  r.querySelector('.message-time').hidden = !showTime;
                  if (showTime) lastTime = time;
                });
                result.textContent = `显示 ${matches.length ? current * pageSize + 1 : 0}–${Math.min((current + 1) * pageSize, matches.length)} 条 / 匹配 ${matches.length} 条 / 共 ${records.length} 条`;
                page.textContent = `${current + 1} / ${pages} 页`;
                prev.disabled = current === 0; next.disabled = current + 1 === pages;
                empty.hidden = matches.length > 0; pager.hidden = matches.length === 0;
              }
              function filter() {
                const term = search.value.trim().toLocaleLowerCase();
                matches = records.filter((r, i) => (kind.value === 'all' || r.dataset.kind === kind.value) && text[i].includes(term));
                current = 0; render();
              }
              search.addEventListener('input', filter); kind.addEventListener('change', filter);
              self.addEventListener('change', () => records.forEach(r => r.classList.toggle('self', self.value === '__auto' ? r.dataset.own === 'true' : self.value !== '' && r.dataset.sender === self.value)));
              document.querySelector('.rail a[href="#export-files"]').addEventListener('click', () => { document.getElementById('export-files').open = true; });
              function move(delta) { current += delta; render(); document.querySelector('.thread').scrollTop = 0; }
              prev.addEventListener('click', () => move(-1)); next.addEventListener('click', () => move(1));
              render();
            })();
            </script></body></html>
            """);
    }

    private static void WriteFile(TextWriter html, string path, string name, string label)
    {
        string directory = path[..(path.LastIndexOf('/') + 1)];
        html.WriteLine($"<div class=file><div class=file-head><span class=file-icon aria-hidden=true>▤</span><div><strong>{H(name)}</strong><small>{H(label)} · 本地文件</small></div></div><div class=file-actions><a href=\"{Href(path)}\" target=_blank rel=noopener>打开文件 ↗</a><a data-locate=\"{Href(path)}\" href=\"{Href(directory)}\" target=_blank rel=noopener title=\"通过 Ripper 打开文件夹并选中文件\">定位文件 ↗</a><a href=\"{Href(directory)}\" target=_blank rel=noopener>浏览目录 ↗</a></div><details class=file-location><summary>文件路径</summary><code>{H(path)}</code></details></div>");
    }

    private static string SenderKey(CaptureRecord capture) => !string.IsNullOrWhiteSpace(capture.Message?.SenderId)
        ? "id:" + capture.Message.SenderId : "name:" + (capture.Message?.DisplaySender ?? "旧版 OCR");

    internal static Dictionary<string, string> FindOwnSenders(IReadOnlyList<CaptureRecord> captures, IReadOnlyDictionary<string, string> sourceRoots)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var conversation in captures.GroupBy(c => c.TargetId, StringComparer.Ordinal))
        {
            if (!sourceRoots.TryGetValue(conversation.Key, out string? root)) continue;
            try
            {
                string storage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (!Path.GetFileName(storage).Equals("db_storage", StringComparison.OrdinalIgnoreCase)) continue;
                string? account = Directory.GetParent(storage)?.Name;
                if (string.IsNullOrEmpty(account)) continue;
                var candidates = new HashSet<string>(StringComparer.Ordinal) { account };
                var suffix = Regex.Match(account, @"^(.+)_([a-zA-Z0-9]{4,8})$", RegexOptions.CultureInvariant);
                if (suffix.Success) candidates.Add(suffix.Groups[1].Value);
                var matches = conversation.Select(c => c.Message?.SenderId).Where(id => id != null && candidates.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
                // Identify by the account username only; display names can be shared by different people.
                if (matches.Length == 1) owners[conversation.Key] = "id:" + matches[0];
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        return owners;
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    // Only generated export paths reach this function. Encode each segment, including #, %, and spaces.
    private static string Href(string path) => H(string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    private const string Head = """
        <!doctype html><html lang=zh-CN><head><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1">
        <meta http-equiv=Content-Security-Policy content="default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; media-src 'self' file:; base-uri 'none'; form-action 'none'">
        <title>聊天记录 · Ripper</title><style>
        :root { color-scheme: light; --green: #07b75d; --ink: #222; --muted: #999; --line: #dedede; }
        * { box-sizing: border-box; }
        body { margin: 0; height: 100vh; height: 100dvh; padding: 24px; background: #d9dddb; color: var(--ink); font: 14px/1.6 "Segoe UI", "Microsoft YaHei", sans-serif; }
        a { color: #576b95; text-decoration: none; }
        a:hover { text-decoration: underline; }
        button, input, select { font: inherit; }
        a:focus-visible, button:focus-visible, select:focus-visible, summary:focus-visible { outline: 2px solid var(--green); outline-offset: 3px; }
        svg { width: 24px; height: 24px; fill: none; stroke: currentColor; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
        .window { display: grid; grid-template-columns: 62px 230px minmax(0, 1fr); width: 100%; max-width: 1480px; height: 100%; margin: auto; overflow: hidden; border: 1px solid #c3c8c5; border-radius: 7px; box-shadow: 0 14px 55px #253e3024; }
        .rail { background: #2d2e2f; color: #aaa; display: flex; align-items: center; flex-direction: column; gap: 21px; padding: 25px 0 18px; }
        .brand { width: 34px; height: 34px; display: grid; place-items: center; border-radius: 5px; background: #4e755c; color: #e4f3e8; font: 600 23px/1 "Segoe UI", sans-serif; margin-bottom: 16px; }
        .rail a { color: #aaa; display: grid; place-items: center; width: 42px; height: 36px; border-radius: 4px; }
        .rail a:hover { background: #393b3a; }
        .rail .rail-chat { color: #16cf6a; }
        .rail-bottom { margin-top: auto; font-size: 10px; color: #858887; letter-spacing: 2px; }
        .sidebar { min-width: 0; display: flex; flex-direction: column; background: #e8e8e7; border-right: 1px solid #d8d8d8; }
        .sidebar-title { padding: 26px 20px 22px; font-size: 16px; font-weight: 600; }
        .sidebar-title span { display: block; font-size: 11px; color: #999; font-weight: 400; margin-top: 2px; }
        .conversation-item { display: flex; gap: 12px; align-items: center; padding: 17px 17px; background: #d4d6d5; }
        .group-avatar { display: grid; place-items: center; width: 40px; height: 40px; flex-shrink: 0; border-radius: 4px; background: #52a97a; color: white; font-size: 20px; }
        .conversation-item>div { min-width: 0; }
        .conversation-item strong { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 500; font-size: 14px; }
        .conversation-item small { display: block; color: #858887; font-size: 11px; margin-top: 4px; }
        .archive-facts { padding: 26px 20px; color: #949795; font-size: 12px; }
        .section-label { font-size: 10px; letter-spacing: 1px; color: #929692; }
        .archive-facts p { margin: 0 0 15px; line-height: 1.9; }
        .sidebar-footer { margin-top: auto; padding: 20px; color: #a1a4a2; font-size: 10px; }
        .sidebar-footer strong { color: #808682; font-size: 13px; }
        .sidebar-footer p { margin: 8px 0 16px; }
        .chat { min-width: 0; min-height: 0; display: grid; grid-template-rows: 78px auto 30px minmax(0, 1fr) auto auto; background: #f3f3f3; }
        .chat-header { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding: 16px 28px; border-bottom: 1px solid #e1e1e1; }
        .chat-header>div { min-width: 0; }
        h1 { margin: 0; font-size: 19px; line-height: 1.4; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .chat-header p { margin: 4px 0 0; font-size: 11px; color: #a0a0a0; }
        .export-menu { position: relative; flex-shrink: 0; }
        .export-menu>summary { cursor: pointer; list-style: none; padding: 7px 12px; border-radius: 4px; background: #eaeaea; font-size: 12px; color: #666; }
        .export-menu>summary::-webkit-details-marker { display: none; }
        .export-menu>summary span { margin-left: 10px; }
        .downloads { position: absolute; z-index: 5; right: 0; top: 39px; width: 235px; background: #fff; border: 1px solid #ddd; box-shadow: 0 5px 24px #0002; border-radius: 5px; padding: 7px; }
        .downloads a { display: block; padding: 9px 12px; color: #333; font-size: 12px; border-radius: 3px; }
        .downloads a:hover { background: #f1f1f1; text-decoration: none; }
        .downloads p { border-top: 1px solid #eee; padding: 10px 12px 3px; font-size: 10px; color: #999; margin-top: 5px; line-height: 1.7; }
        .tools { display: flex; align-items: center; gap: 10px; padding: 14px 28px 2px; min-width: 0; }
        .tools label { min-width: 0; }
        .search { display: flex; align-items: center; flex: 1; gap: 6px; padding: 0 10px; background: #e7e7e7; border-radius: 4px; }
        .search:focus-within { outline: 1px solid #a0b8a7; background: white; }
        .search svg { width: 16px; height: 16px; color: #888; flex-shrink: 0; }
        input { border: 0; outline: 0; width: 100%; min-width: 0; padding: 8px 0; font-size: 12px; color: #444; background: transparent; }
        select { border: 1px solid #dfdfdf; border-radius: 4px; padding: 7px; font-size: 11px; background: #f8f8f8; color: #666; max-width: 125px; }
        .self-label { display: flex; align-items: center; gap: 6px; font-size: 11px; color: #999; flex-shrink: 0; }
        .self-label select { width: 91px; }
        #result { font-size: 10px; color: #aaa; margin: 0; padding: 6px 28px; }
        .thread { min-height: 0; overflow-y: auto; overflow-x: hidden; padding: 0 28px 18px; scrollbar-width: thin; scrollbar-color: #c7c7c7 transparent; }
        .record { margin: 0 0 18px; }
        .message-time { display: block; text-align: center; color: #aaa; font-size: 11px; margin: 14px 0 22px; }
        .message-row { display: flex; align-items: flex-start; gap: 11px; }
        .avatar { display: grid; place-items: center; flex: 0 0 37px; height: 37px; border-radius: 4px; font-size: 18px; color: #427566; background: #c9e4d9; border: 1px solid #00000005; }
        .avatar-1 { background: #cddde9; color: #526f84; }.avatar-2 { background: #ebdfc9; color: #998359; }.avatar-3 { background: #dbd5e7; color: #806f98; }.avatar-4 { background: #d9e6c6; color: #7e925b; }.avatar-5 { background: #e9d2d0; color: #9b7670; }
        .message-main { min-width: 0; max-width: min(620px, calc(100% - 48px)); }
        .sender-row { display: flex; align-items: center; gap: 8px; margin: -3px 0 5px; font-size: 11px; color: #999; min-width: 0; }
        .sender-row strong { font-weight: 400; max-width: 260px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .number { font-size: 9px; color: #b7b7b7; flex-shrink: 0; }
        .bubble { position: relative; width: fit-content; max-width: 100%; padding: 10px 13px; background: #fff; border: 1px solid #e7e7e7; border-radius: 4px; }
        .bubble:before { content: ""; position: absolute; left: -6px; top: 10px; width: 10px; height: 10px; border-left: 1px solid #e7e7e7; border-bottom: 1px solid #e7e7e7; transform: rotate(45deg); background: #fff; }
        .body, pre { white-space: pre-wrap; overflow-wrap: anywhere; }
        .body { position: relative; line-height: 1.8; }
        .self .message-row { flex-direction: row-reverse; }
        .self .message-main { display: flex; flex-direction: column; align-items: flex-end; }
        .self .sender-row { justify-content: flex-end; }
        .self .bubble { background: #95ec69; border-color: #8fe165; }
        .self .bubble:before { left: auto; right: -6px; border: 0; border-right: 1px solid #8fe165; border-top: 1px solid #8fe165; background: #95ec69; }
        .self[data-kind=file] .bubble { background: #fff; border-color: #e7e7e7; }
        .self[data-kind=file] .bubble:before { background: #fff; border-color: #e7e7e7; }
        .self .source { align-self: stretch; }
        .file { width: 300px; max-width: 100%; }
        .file-head { display: flex; align-items: center; gap: 12px; padding: 3px 0 12px; }
        .file-head>div { min-width: 0; }
        .file-head strong { display: block; font-weight: 500; font-size: 13px; overflow-wrap: anywhere; }
        .file-head small { display: block; font-size: 10px; color: #999; margin-top: 4px; }
        .file-icon { display: grid; place-items: center; flex: 0 0 38px; height: 42px; font-size: 28px; background: #f0a854; color: #fff; border-radius: 3px; }
        .file-actions { display: flex; flex-wrap: wrap; gap: 8px 22px; padding-top: 9px; border-top: 1px solid #0000000b; font-size: 11px; }
        .file-location { margin-top: 8px; font-size: 10px; color: #aaa; }
        summary { cursor: pointer; width: fit-content; }
        .file code { display: block; margin-top: 7px; font: 10px/1.7 Consolas, "Microsoft YaHei", monospace; color: #777; overflow-wrap: anywhere; white-space: pre-wrap; }
        audio { display: block; width: 290px; max-width: 100%; height: 35px; margin: 10px 0 15px; }
        .transcript { width: 300px; max-width: 100%; padding: 2px 0 12px; }
        .tag { display: inline-block; font-size: 10px; color: #6b9767; margin-bottom: 5px; }
        .hint { font-size: 10px; color: #999; margin: 8px 0 0; }
        .source { margin-top: 6px; font-size: 10px; color: #a3a3a3; }
        .source[open] { background: #ffffffb0; border: 1px solid #e5e5e5; padding: 10px 12px; border-radius: 4px; color: #888; }
        .source summary { font-size: 10px; }
        dl { display: grid; grid-template-columns: 78px minmax(0, 1fr); gap: 5px 10px; margin-top: 14px; }
        dt, dd { margin: 0; overflow-wrap: anywhere; }
        h3 { font-size: 11px; font-weight: 500; }
        pre { font: 11px/1.8 Consolas, "Microsoft YaHei", monospace; margin: 8px 0 0; color: #555; background: #f3f3f3; padding: 10px; border-radius: 3px; }
        .notice { color: #9d7b57; font-size: 11px; padding: 8px 0; margin: 0; max-width: 300px; }
        #empty { text-align: center; max-width: none; padding: 50px 20px; color: #aaa; }
        .pager { display: flex; justify-content: center; align-items: center; gap: 22px; border-top: 1px solid #e2e2e2; padding: 12px; background: #f7f7f7; font-size: 11px; color: #aaa; }
        button { border: 1px solid #ddd; border-radius: 3px; background: #fff; color: #666; padding: 5px 12px; cursor: pointer; font-size: 11px; }
        button:hover:not(:disabled) { background: #eee; }button:disabled { opacity: .4; cursor: default; }
        footer { text-align: center; padding: 7px 12px; font-size: 9px; color: #aaa; background: #f7f7f7; }
        .sr-only { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
        [hidden] { display: none !important; }
        @media(max-width: 1050px) { body { padding: 12px; }.window { grid-template-columns: 56px 190px minmax(0, 1fr); }.tools { gap: 7px; padding-left: 20px; padding-right: 20px; }.chat-header { padding-left: 20px; padding-right: 20px; }.thread { padding-left: 20px; padding-right: 20px; } }
        @media(max-width: 760px) { body { padding: 0; }.window { border: 0; border-radius: 0; grid-template-columns: minmax(0, 1fr); }.rail, .sidebar { display: none; }.chat { grid-template-rows: 70px auto 28px minmax(0, 1fr) auto auto; }.chat-header { padding: 12px 16px; }h1 { font-size: 17px; }.tools { display: grid; grid-template-columns: 1fr 1fr; padding: 12px 16px 0; gap: 8px; }.search { grid-column: 1 / -1; }.tools select { max-width: 100%; width: 100%; }.self-label { justify-self: end; }.self-label select { width: 125px; }#result { padding-left: 16px; }.thread { padding: 0 13px 14px; }.avatar { flex-basis: 32px; height: 32px; font-size: 16px; }.message-row { gap: 9px; }.message-main { max-width: calc(100% - 41px); }.sender-row strong { max-width: 190px; }.file, .transcript { width: 235px; }audio { width: 235px; }.pager { gap: 16px; padding: 9px; }footer { font-size: 9px; padding: 6px 10px; } }
        </style></head><body>
        """;
}
