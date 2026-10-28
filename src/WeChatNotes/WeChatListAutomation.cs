using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WeChatNotes;

internal sealed record FocusCalibration(int X, int Y, int Width, int Height, int WindowWidth, int WindowHeight)
{
    internal bool Valid => WindowWidth >= 500 && WindowHeight >= 400 && X >= 0 && Y >= 40 && Width >= 160 && Height is >= 36 and <= 150 &&
        X + Width <= WindowWidth / 2 && Y + Height * 2 <= WindowHeight;
    internal Rectangle Row(int index) => new(X, Y + Height * index, Width, Height);
    internal static Rectangle NameBand(Rectangle row) => Rectangle.FromLTRB(row.Left + (int)(row.Height * .9), row.Top + (int)(row.Height * .12), row.Right - (int)(row.Height * .8), row.Top + (int)(row.Height * .53));
}

internal sealed record FocusActionResult(string Status, bool Acted = false, bool Blocked = false);

// Only right-click an observed, complete conversation name; then click a separately observed menu label.
// Hiding never activates or types. Restoring explicitly opens a verified local group search result.
// No message submission, conversation deletion, or source database writes.
internal sealed class WeChatListAutomation : IDisposable
{
    private WindowCapture? capture;
    private LocalOcr? ocr;
    private delegate bool EnumProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref InputTime info);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, MouseInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeWindows.NativeRect bounds, uint size);
    [DllImport("user32.dll", EntryPoint = "SendInput")] private static extern uint SendKeys(uint count, KeyInput[] inputs, int size);
    private const nuint InputMarker = 0x52505052;
    private delegate nint InputHook(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookExW(int kind, InputHook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    private sealed class InputWatch : IDisposable
    {
        private readonly InputHook keyboard, mouse;
        private nint keyboardHook, mouseHook;
        internal bool Interrupted { get; private set; }
        private readonly nint watchedWindow;
        internal InputWatch(nint watchedWindow = 0)
        {
            this.watchedWindow = watchedWindow;
            // Restore watches all input. Background hiding watches only input directed
            // at WeChat, allowing the user to work elsewhere. No input is suppressed/stored.
            keyboard = (code, message, data) => Observe(code, message, data, 16, false);
            mouse = (code, message, data) => Observe(code, message, data, 24, true);
            keyboardHook = SetWindowsHookExW(13, keyboard, GetModuleHandle(null), 0);
            mouseHook = SetWindowsHookExW(14, mouse, GetModuleHandle(null), 0);
            if (keyboardHook == 0 || mouseHook == 0) { Dispose(); throw new IOException("无法区分程序操作与用户输入，已停止界面操作。"); }
        }
        private nint Observe(int code, nint message, nint data, int extraOffset, bool mouse)
        {
            if (code >= 0 && unchecked((nuint)Marshal.ReadIntPtr(data, extraOffset)) != InputMarker)
            {
                nint receiving = mouse ? GetAncestor(WindowFromPoint(new(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4))), 2) : GetForegroundWindow();
                if (watchedWindow == 0 || OwnedBy(receiving, watchedWindow)) Interrupted = true;
            }
            return CallNextHookEx(0, code, message, data);
        }
        public void Dispose()
        {
            if (keyboardHook != 0) { UnhookWindowsHookEx(keyboardHook); keyboardHook = 0; }
            if (mouseHook != 0) { UnhookWindowsHookEx(mouseHook); mouseHook = 0; }
            GC.KeepAlive(keyboard); GC.KeepAlive(mouse);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputTime { internal uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { internal uint Type; internal Mouse Mouse; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct KeyInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
        [FieldOffset(16)] internal uint Time;
        [FieldOffset(24)] internal nuint Extra;
    }

    internal static nint MainWindow()
    {
        var ids = new HashSet<int>();
        foreach (string name in new[] { "Weixin", "WeChat" })
            foreach (var process in Process.GetProcessesByName(name)) using (process) ids.Add(process.Id);
        var windows = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (ids.Contains((int)process) && IsWindowVisible(window) && NativeWindows.Title(window) is "微信" or "Weixin" or "WeChat" &&
                NativeWindows.GetWindowRect(window, out var bounds) && (NativeWindows.IsIconic(window) || bounds.Right - bounds.Left >= 500 && bounds.Bottom - bounds.Top >= 400)) windows.Add(window);
            return true;
        }, 0);
        return windows.Count == 1 ? windows[0] : 0;
    }
    private static uint LastInput()
    {
        var info = new InputTime { Size = (uint)Marshal.SizeOf<InputTime>() };
        if (!GetLastInputInfo(ref info)) throw new IOException("无法确认鼠标键盘状态。");
        return info.Time;
    }
    internal async Task<Bitmap> CaptureAsync(CancellationToken token)
    {
        nint window = MainWindow();
        if (window == 0 || NativeWindows.IsIconic(window)) throw new InvalidOperationException("请打开一个微信主窗口，并取消最小化。");
        ocr ??= new(); capture ??= new();
        return await capture.CaptureAsync(window, token);
    }
    internal static string Compact(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
    internal static bool SameName(string first, string second) => string.Equals(Compact(first), Compact(second), StringComparison.OrdinalIgnoreCase);
    internal static Bitmap PrepareName(Bitmap source)
    {
        // Windows OCR misses short white names on WeChat's selected green row. Normalize
        // contrast and leave a margin, while preserving the exact text for matching.
        static int Light(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
        int background = new[] { Light(source.GetPixel(0, 0)), Light(source.GetPixel(source.Width - 1, 0)), Light(source.GetPixel(source.Width - 1, source.Height - 1)) }.Order().ElementAt(1);
        var result = new Bitmap(source.Width + 24, source.Height + 20);
        using (var graphics = Graphics.FromImage(result)) graphics.Clear(Color.White);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                int light = Light(source.GetPixel(x, y));
                bool ink = background < 170 ? light > background + 45 : light < background - 50;
                result.SetPixel(x + 12, y + 10, ink ? Color.Black : Color.White);
            }
        return result;
    }
    internal static Point? MenuTarget(IReadOnlyList<LocalOcr.Line> lines, Point openedAt)
    {
        // Windows Chinese OCR reads 口 as 囗 in the neighboring menu label on WeChat 4.x.
        // Keep conversation names and the actionable 不显示/删除 labels exact.
        bool Label(LocalOcr.Line line, string text) => Compact(line.Text) == text || text == "独立窗口显示" && Compact(line.Text) == "独立窗囗显示";
        var matches = lines.Where(h => Label(h, "不显示") && h.Bounds.Left - openedAt.X is >= 0 and < 60 && h.Bounds.Top - openedAt.Y is > 40 and < 300 &&
            lines.Any(d => Label(d, "删除") && Math.Abs(d.Bounds.Left - h.Bounds.Left) < 20 && d.Bounds.Top - h.Bounds.Bottom is > 3 and < 80) &&
            lines.Any(i => Label(i, "独立窗口显示") && Math.Abs(i.Bounds.Left - h.Bounds.Left) < 20 && h.Bounds.Top - i.Bounds.Bottom is > 3 and < 70)).ToArray();
        return matches.Length == 1 ? new(matches[0].Bounds.Left + matches[0].Bounds.Width / 2, matches[0].Bounds.Top + matches[0].Bounds.Height / 2) : null;
    }
    private static bool OwnedBy(nint child, nint window)
    {
        for (int i = 0; i < 8 && child != 0; i++, child = GetWindow(child, 4)) if (child == window) return true;
        return false;
    }
    private static nint[] Popups(nint window, int maximumHeight = 700)
    {
        GetWindowThreadProcessId(window, out uint process);
        var result = new List<nint>();
        EnumWindows((candidate, _) =>
        {
            GetWindowThreadProcessId(candidate, out uint owner);
            if (candidate != window && owner == process && IsWindowVisible(candidate) && OwnedBy(candidate, window) && NativeWindows.GetWindowRect(candidate, out var rect) &&
                rect.Right - rect.Left is >= 80 and <= 450 && rect.Bottom - rect.Top >= 60 && rect.Bottom - rect.Top <= maximumHeight) result.Add(candidate);
            return true;
        }, 0);
        return result.ToArray();
    }
    private static bool Ready(nint window, InputWatch input, Point screen)
        => NativeWindows.IsWindow(window) && !NativeWindows.IsIconic(window) && OwnedBy(GetForegroundWindow(), window) && !input.Interrupted && OwnedBy(GetAncestor(WindowFromPoint(screen), 2), window);
    private static bool CaptureBounds(nint window, Size image, out NativeWindows.NativeRect bounds)
    {
        // WGC excludes the invisible resize border included by GetWindowRect.
        if (DwmGetWindowAttribute(window, 9, out bounds, (uint)Marshal.SizeOf<NativeWindows.NativeRect>()) == 0 && bounds.Right - bounds.Left == image.Width && bounds.Bottom - bounds.Top == image.Height) return true;
        return NativeWindows.GetWindowRect(window, out bounds) && bounds.Right - bounds.Left == image.Width && bounds.Bottom - bounds.Top == image.Height;
    }
    private async Task<Bitmap> CaptureWithPopups(nint window, CancellationToken token)
    {
        var image = await CaptureAsync(token);
        try
        {
            if (!CaptureBounds(window, image.Size, out var main)) return image;
            foreach (var popup in Popups(window, image.Height))
            {
                using var part = await capture!.CaptureAsync(popup, token);
                if (!CaptureBounds(popup, part.Size, out var bounds)) continue;
                using var graphics = Graphics.FromImage(image);
                graphics.DrawImageUnscaled(part, bounds.Left - main.Left, bounds.Top - main.Top);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private static void Click(Point screen, bool right)
    {
        var desktop = SystemInformation.VirtualScreen;
        var mouse = new Mouse { X = (int)((screen.X - desktop.Left) * 65535L / Math.Max(1, desktop.Width - 1)), Y = (int)((screen.Y - desktop.Top) * 65535L / Math.Max(1, desktop.Height - 1)), Flags = 0x8000 | 0x4000 | 1, Extra = InputMarker };
        var down = mouse; down.Flags = right ? 8u : 2u;
        var up = mouse; up.Flags = right ? 16u : 4u;
        if (SendInput(3, [new() { Mouse = mouse }, new() { Mouse = down }, new() { Mouse = up }], Marshal.SizeOf<MouseInput>()) != 3) throw new IOException("微信未接受鼠标操作，已停止本次隐藏。");
    }
    internal async Task<FocusActionResult> HideAsync(string name, FocusCalibration calibration, Func<Task<bool>> unchanged, Action beforeHide, CancellationToken token, Action<string>? progress = null)
    {
        nint window = MainWindow();
        if (window == 0) return new("未找到唯一微信主窗口，请关闭多余主窗口");
        if (NativeWindows.IsIconic(window)) return new("微信已最小化，等待还原后后台隐藏");
        bool foreground = OwnedBy(GetForegroundWindow(), window);
        if (foreground && unchecked((uint)Environment.TickCount - LastInput()) < 1500) return new("等待微信操作空闲");
        if (Popups(window).Length != 0) return new("等待微信现有菜单关闭");
        using var input = new InputWatch(window);
        var mainTarget = WindowMessageTarget.Observe(window, window);
        bool ReadyToPost() => mainTarget?.Valid == true && !input.Interrupted && OwnedBy(GetForegroundWindow(), window) == foreground;
        if (!ReadyToPost()) return new("微信窗口已变化，稍后重试");
        capture ??= new(); ocr ??= new();
        progress?.Invoke("正在读取微信列表画面");
        using var image = await capture.CaptureAsync(window, token);
        if (!calibration.Valid || image.Width != calibration.WindowWidth || image.Height != calibration.WindowHeight) return new("微信大小已改变，请重新校准");
        if (!CaptureBounds(window, image.Size, out var bounds)) return new("窗口缩放不匹配，请重新校准");
        async Task<bool> NameMatches(Bitmap frame, Rectangle row)
        {
            var band = FocusCalibration.NameBand(row);
            if (band.Width <= 0 || !new Rectangle(Point.Empty, frame.Size).Contains(band)) return false;
            using var cropped = frame.Clone(band, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var normalized = PrepareName(cropped);
            return SameName(await ocr.ReadAsync(normalized), name);
        }
        var rows = ConversationRows.Locate(image, calibration);
        if (rows.Length == 0) return new("未识别到会话行，请关闭微信搜索或弹窗后重试");
        var matches = new List<Rectangle>();
        for (int index = 0; index < rows.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            progress?.Invoke($"正在识别第 {index + 1} 行会话名");
            var row = rows[index];
            if (await NameMatches(image, row)) matches.Add(row);
        }
        if (matches.Count != 1) return matches.Count == 0 ? new("等待列表中完整显示会话名（可滚动至顶部）") : new("画面有同名会话，已暂停", Blocked: true);
        var selected = matches[0];
        Point at = new(selected.Left + selected.Width / 2, selected.Top + selected.Height / 3), screen = new(bounds.Left + at.X, bounds.Top + at.Y);
        token.ThrowIfCancellationRequested();
        if (!await unchanged() || !ReadyToPost()) return new("消息或界面已变化，稍后重试");
        // Other conversations may reorder the list while database/OCR work is pending.
        using (var fresh = await capture.CaptureAsync(window, token))
            if (fresh.Size != image.Size || !ConversationRows.Locate(fresh, calibration).Contains(selected) || !await NameMatches(fresh, selected)) return new("会话位置已变化，稍后重试");
        if (!ReadyToPost() || Popups(window).Length != 0) return new("微信界面已变化，稍后重试");
        progress?.Invoke("正在后台打开会话菜单");
        var existing = Popups(window).ToHashSet();
        token.ThrowIfCancellationRequested();
        if (!mainTarget!.TryClick(screen, true)) return new("微信未接受定向右键消息，已暂停", Blocked: true);
        var openedMenus = new List<WindowMessageTarget>();
        bool hidePosted = false;
        try
        {
            // Discover the popup even if Stop arrives during this brief delay, so
            // cancellation can dismiss our own menu in finally.
            await Task.Delay(250);
            var menuTargets = new List<(nint Window, WindowMessageTarget Destination, Point Screen, Point OpenedAt, Size ImageSize)>();
            foreach (var popup in Popups(window).Where(p => !existing.Contains(p)))
            {
                var destination = WindowMessageTarget.Observe(window, popup);
                if (destination == null) continue;
                openedMenus.Add(destination);
                using var menu = await capture.CaptureAsync(popup, token);
                if (!CaptureBounds(popup, menu.Size, out var menuBounds)) continue;
                var target = MenuTarget(await ocr!.ReadLinesAsync(menu), Point.Empty);
                if (target != null && destination.Valid) menuTargets.Add((popup, destination, new(menuBounds.Left + target.Value.X, menuBounds.Top + target.Value.Y), Point.Empty, menu.Size));
            }
            if (menuTargets.Count == 0)
            {
                using var menu = await capture.CaptureAsync(window, token);
                var target = MenuTarget(await ocr!.ReadLinesAsync(menu), at);
                if (menu.Size == image.Size && target != null) menuTargets.Add((window, mainTarget, new(bounds.Left + target.Value.X, bounds.Top + target.Value.Y), at, menu.Size));
            }
            if (menuTargets.Count != 1) return new("本次未识别到唯一“不显示”菜单，将自动重试");
            var chosen = menuTargets[0]; screen = chosen.Screen;
            token.ThrowIfCancellationRequested();
            if (!await unchanged()) return new("收到新消息，将重新检查后再隐藏");
            if (!ReadyToPost() || !chosen.Destination.Valid) return new("微信操作或菜单已变化，稍后重试");
            // Reconfirm the actionable label after the asynchronous database check. Never
            // reuse a menu handle/coordinate that disappeared or moved in the meantime.
            using (var verified = await capture.CaptureAsync(chosen.Window, token))
            {
                if (verified.Size != chosen.ImageSize || !CaptureBounds(chosen.Window, verified.Size, out var menuBounds)) return new("菜单尺寸已变化，稍后重试");
                var target = MenuTarget(await ocr.ReadLinesAsync(verified), chosen.OpenedAt);
                if (target == null || new Point(menuBounds.Left + target.Value.X, menuBounds.Top + target.Value.Y) != screen) return new("菜单内容已变化，将重新识别");
            }
            token.ThrowIfCancellationRequested();
            if (!ReadyToPost() || !chosen.Destination.Valid) return new("微信操作或菜单已变化，稍后重试");
            beforeHide(); // Persist recovery intent before an input can hide the conversation.
            if (!await unchanged() || !ReadyToPost() || !chosen.Destination.Valid) return new("消息或界面已变化，稍后重试");
            token.ThrowIfCancellationRequested();
            if (!chosen.Destination.TryClick(screen, false)) return new("微信未接受定向隐藏消息，已暂停", Blocked: true);
            hidePosted = true;
            return new("已后台请求隐藏，等待微信状态确认", Acted: true);
        }
        finally
        {
            // If the hide request succeeded, its popup is already closing. Do not race
            // the queued click with Escape; only clean up an abandoned attempt.
            // Cleanup is skipped after user input in WeChat, to preserve their menu.
            if (!input.Interrupted && !hidePosted)
                foreach (var menu in openedMenus) menu.TryDismiss();
        }
    }

    internal static Point? SearchResult(IReadOnlyList<LocalOcr.Line> lines, string name, FocusCalibration calibration)
    {
        // WeChat promotes an often-opened group to 最常使用. Callers have already checked
        // that the exact name belongs to this group alone across all local contacts.
        var groups = lines.Where(l => Compact(l.Text) is "群聊" or "最常使用" or "常使用" or "最帛使用" && l.Bounds.Left >= calibration.X && l.Bounds.Left < calibration.X + calibration.Width &&
            l.Bounds.Top >= 35 && l.Bounds.Top < calibration.Y + calibration.Height).ToArray();
        if (groups.Length != 1) return null;
        var header = groups[0];
        int end = lines.Where(l => new[] { "聊天记录", "聊天文件", "联系人", "更多", "全部" }.Contains(Compact(l.Text)) && l.Bounds.Left < calibration.X + calibration.Width && l.Bounds.Top > header.Bounds.Bottom)
            .Select(l => l.Bounds.Top).DefaultIfEmpty(header.Bounds.Bottom + calibration.Height * 4).Min();
        var matches = lines.Where(l => SameName(l.Text, name) && l.Bounds.Left >= calibration.X + calibration.Height * .7 &&
            l.Bounds.Left < calibration.X + calibration.Width && l.Bounds.Top > header.Bounds.Bottom && l.Bounds.Bottom < end).ToArray();
        return matches.Length == 1 ? new(matches[0].Bounds.Left + matches[0].Bounds.Width / 2, matches[0].Bounds.Top + matches[0].Bounds.Height / 2) : null;
    }
    internal static bool SearchFocused(Bitmap image, Rectangle area)
    {
        area.Intersect(new(Point.Empty, image.Size));
        int green = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                var color = image.GetPixel(x, y);
                if (color.G > color.R + 18 && color.G > color.B + 8) green++;
            }
        return green >= area.Width; // Focus border around the observed search placeholder, above the conversation rows.
    }
    internal static Rectangle? EmptySearchField(Bitmap image, FocusCalibration calibration)
    {
        // The light gray two-character placeholder is not reliably recognized by Windows OCR.
        // Locate the single light input surface above the calibrated list instead, then verify
        // its focus border before typing. A clear button or text at its right edge rejects an existing query.
        var area = Rectangle.FromLTRB(calibration.X + 4, Math.Max(30, calibration.Y - calibration.Height),
            calibration.X + calibration.Width - 25, calibration.Y - 5);
        if (!new Rectangle(Point.Empty, image.Size).Contains(area) || area.Width <= 0 || area.Height <= 0) return null;
        var light = new HashSet<Point>();
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                var c = image.GetPixel(x, y);
                if (c.R >= 240 && c.G >= 240 && c.B >= 240) light.Add(new(x, y));
            }
        var candidates = new List<Rectangle>();
        while (light.Count > 0)
        {
            var first = light.First(); light.Remove(first);
            var queue = new Queue<Point>(); queue.Enqueue(first);
            int left = first.X, right = first.X, top = first.Y, bottom = first.Y, count = 0;
            while (queue.TryDequeue(out var p))
            {
                count++; left = Math.Min(left, p.X); right = Math.Max(right, p.X); top = Math.Min(top, p.Y); bottom = Math.Max(bottom, p.Y);
                foreach (var next in new[] { new Point(p.X - 1, p.Y), new Point(p.X + 1, p.Y), new Point(p.X, p.Y - 1), new Point(p.X, p.Y + 1) })
                    if (light.Remove(next)) queue.Enqueue(next);
            }
            var box = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            if (box.Width < calibration.Width * .55 || box.Width > calibration.Width * .9 || box.Height is < 20 or > 48 ||
                box.Left <= area.Left || box.Right >= area.Right || box.Top <= area.Top || box.Bottom >= area.Bottom || count < box.Width * box.Height * .7) continue;
            bool clear = true;
            for (int y = box.Top + 6; y < box.Bottom - 6; y++)
                for (int x = box.Right - 30; x < box.Right - 7; x++)
                {
                    var c = image.GetPixel(x, y);
                    if (Math.Min(c.R, Math.Min(c.G, c.B)) < 210) clear = false;
                }
            if (clear) candidates.Add(box);
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }
    internal static Bitmap PrepareQuery(Bitmap source)
    {
        using var copy = (Bitmap)source.Clone();
        // The green insertion caret must not become a trailing OCR letter in the query.
        for (int y = 0; y < copy.Height; y++)
            for (int x = 0; x < copy.Width; x++)
            {
                var c = copy.GetPixel(x, y);
                if (c.G > c.R + 18 && c.G > c.B + 8) copy.SetPixel(x, y, Color.White);
            }
        return PrepareName(copy);
    }
    private async Task<LocalOcr.Line[]> SearchLines(Bitmap image, FocusCalibration calibration)
    {
        // Read the first search result separately from its avatar and info icon. Full-window
        // OCR merges the avatar into short group names. Other sections are never clicked.
        var header = new Rectangle(calibration.X + (int)(calibration.Height * .3), calibration.Y - 4,
            calibration.Width - (int)(calibration.Height * .6), (int)(calibration.Height * .4));
        var name = new Rectangle(calibration.X + calibration.Height, calibration.Y + (int)(calibration.Height * .48),
            calibration.Width - calibration.Height - 14, (int)(calibration.Height * .77));
        var result = new List<LocalOcr.Line>();
        foreach (var area in new[] { header, name })
        {
            if (area.Width <= 0 || area.Height <= 0 || !new Rectangle(Point.Empty, image.Size).Contains(area)) return [];
            using var part = image.Clone(area, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var normalized = PrepareName(part);
            foreach (var line in await ocr!.ReadLinesAsync(normalized))
            {
                var bounds = line.Bounds; bounds.Offset(area.Left - 12, area.Top - 10);
                result.Add(line with { Bounds = bounds });
            }
        }
        return result.ToArray();
    }
    internal async Task<FocusActionResult> RestoreAsync(string name, FocusCalibration calibration, bool activate, CancellationToken token)
    {
        if (name.Length > 128 || name.Any(char.IsControl)) return new("会话名不适合自动搜索，请手动恢复");
        nint window = MainWindow();
        if (window == 0) return new("请登录并打开微信后重试恢复");
        if (activate)
        {
            if (NativeWindows.IsIconic(window)) ShowWindow(window, 9);
            SetForegroundWindow(window); await Task.Delay(250, token);
        }
        if (GetForegroundWindow() != window || NativeWindows.IsIconic(window)) return new("请将微信置于前台后重试恢复");
        if (!activate && unchecked((uint)Environment.TickCount - LastInput()) < 1500) return new("收到 @，等待空闲后恢复显示");
        using var input = new InputWatch();
        using var image = await CaptureAsync(token);
        if (!calibration.Valid || image.Size != new Size(calibration.WindowWidth, calibration.WindowHeight) || !CaptureBounds(window, image.Size, out var bounds)) return new("窗口大小或缩放改变，请手动恢复或恢复原窗口大小");
        var field = EmptySearchField(image, calibration);
        if (field == null) return new("请清空微信顶部搜索框并关闭菜单后重试恢复");
        var box = field.Value;
        Point local = new(box.Left + box.Width / 2, box.Top + box.Height / 2), screen = new(bounds.Left + local.X, bounds.Top + local.Y);
        if (!Ready(window, input, screen)) return new("界面或输入状态改变，请重试恢复");
        Click(screen, false); await Task.Delay(200, token);
        using var focused = await CaptureWithPopups(window, token);
        var searchArea = Rectangle.Inflate(box, 3, 3);
        if (focused.Size != image.Size || !SearchFocused(focused, searchArea) || !Ready(window, input, screen)) return new("未确认微信搜索框焦点，已取消输入");
        var keys = name.SelectMany(c => new[] { new KeyInput { Type = 1, Scan = c, Flags = 4, Extra = InputMarker }, new KeyInput { Type = 1, Scan = c, Flags = 6, Extra = InputMarker } }).ToArray();
        if (SendKeys((uint)keys.Length, keys, Marshal.SizeOf<KeyInput>()) != keys.Length) return new("搜索输入未完成，请清空搜索后重试");
        for (int attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(250, token); using var results = await CaptureWithPopups(window, token);
            if (results.Size != image.Size) return new("微信窗口尺寸变化，已停止自动恢复");
            if (input.Interrupted) return new("检测到新的鼠标键盘输入，已停止自动恢复");
            if (!OwnedBy(GetForegroundWindow(), window)) return new("微信不再位于前台，已停止自动恢复");
            if (!Ready(window, input, screen)) return new("搜索框被其他窗口遮挡，已停止自动恢复");
            // The typed query and a unique exact result under 群聊 must both be visible.
            var lines = await SearchLines(results, calibration);
            var queryArea = Rectangle.FromLTRB(box.Left + 25, box.Top + 3, box.Right - 26, box.Bottom - 3);
            if (queryArea.Width <= 0 || queryArea.Height <= 0) return new("搜索框区域无法确认，请手动恢复");
            using var queryImage = results.Clone(queryArea, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var normalized = PrepareQuery(queryImage);
            bool query = SameName(await ocr!.ReadAsync(normalized), name);
            var target = query ? SearchResult(lines, name, calibration) : null;
            if (target == null) continue;
            screen = new(bounds.Left + target.Value.X, bounds.Top + target.Value.Y);
            if (!Ready(window, input, screen)) return new("界面状态改变，已取消恢复点击");
            Click(screen, false);
            return new("已打开群会话，等待恢复确认", Acted: true);
        }
        return new("未找到唯一完整群名，请清空搜索框后重试或手动打开该群");
    }
    public void Dispose() { capture?.Dispose(); capture = null; }
}
