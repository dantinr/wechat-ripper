using System.Runtime.InteropServices;

namespace WeChatNotes;

internal static class WindowMessageSelfTest
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    // Real HWNDs with a message queue, offscreen and never activated. Tests exercise
    // the native transport without touching a running WeChat or injecting desktop input.
    private sealed class Receiver : NativeWindow, IDisposable
    {
        internal readonly List<(int Message, nuint Buttons, Point Client)> Clicks = [];
        internal readonly List<int> EscapeMessages = [];
        internal Receiver(nint owner = 0, int x = -20000, int y = -20000)
        {
            CreateHandle(new CreateParams { Caption = "Ripper synthetic message receiver", Parent = owner,
                X = x, Y = y, Width = 300, Height = 180, Style = unchecked((int)0x90000000), ExStyle = 0x08000080 });
        }
        internal Point Screen(int x, int y)
        {
            var point = new Point(x, y);
            if (!ClientToScreen(Handle, ref point)) throw new IOException("Synthetic coordinate conversion failed.");
            return point;
        }
        protected override void WndProc(ref Message m)
        {
            // Keep WinForms' dialog preprocessing from consuming the posted Escape.
            if (m.Msg == 0x0087) { m.Result = 4; return; } // DLGC_WANTALLKEYS
            if (m.Msg is 0x0100 or 0x0101 && m.WParam == 0x1B) { EscapeMessages.Add(m.Msg); m.Result = 0; return; }
            if (m.Msg is 0x0201 or 0x0202 or 0x0204 or 0x0205)
            {
                long packed = m.LParam.ToInt64();
                Clicks.Add((m.Msg, unchecked((nuint)m.WParam), new(unchecked((short)packed), unchecked((short)(packed >> 16)))));
                m.Result = 0; return;
            }
            if (m.Msg == 0x0021) { m.Result = 3; return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        public void Dispose() { if (Handle != 0) DestroyHandle(); }
    }

    internal static void Run(Action<string, bool> check)
    {
        using var main = new Receiver();
        using var menu = new Receiver(main.Handle, -19960, -19960);
        using var unrelated = new Receiver();
        Application.DoEvents(); // Finish prior synthetic dialogs' focus/close notifications first.
        nint foreground = GetForegroundWindow(); GetCursorPos(out var cursor);
        var mainTarget = WindowMessageTarget.Observe(main.Handle, main.Handle)!;
        bool right = mainTarget.TryClick(main.Screen(42, 31), true);
        Application.DoEvents();
        check("后台定向右键只投递目标窗口且转换负屏幕坐标", right && main.Clicks.SequenceEqual(new[]
        {
            (0x0204, (nuint)2, new Point(42, 31)), (0x0205, (nuint)0, new Point(42, 31))
        }) && menu.Clicks.Count == 0 && unrelated.Clicks.Count == 0);

        var menuTarget = WindowMessageTarget.Observe(main.Handle, menu.Handle)!;
        bool left = menuTarget.TryClick(menu.Screen(25, 65), false);
        Application.DoEvents();
        check("后台隐藏点击发往已观察的所属菜单窗口", left && menu.Clicks.SequenceEqual(new[]
        {
            (0x0201, (nuint)1, new Point(25, 65)), (0x0202, (nuint)0, new Point(25, 65))
        }) && main.Clicks.Count == 2);
        GetCursorPos(out var after);
        check("后台窗口消息保持前台窗口与鼠标位置", GetForegroundWindow() == foreground && cursor == after);
        check("后台点击拒绝其他窗口和客户区外坐标", WindowMessageTarget.Observe(main.Handle, unrelated.Handle) == null &&
            !mainTarget.TryClick(main.Screen(-1, 30), true) && !mainTarget.TryClick(main.Screen(300, 30), true));
        bool dismissed = menuTarget.TryDismiss(); Application.DoEvents();
        check("放弃隐藏仅向本次菜单发送关闭按键", dismissed && !mainTarget.TryDismiss() && menu.EscapeMessages.SequenceEqual(new[] { 0x0100, 0x0101 }) &&
            main.EscapeMessages.Count == 0 && unrelated.EscapeMessages.Count == 0);

        SetWindowPos(menu.Handle, 0, -19950, -19960, 300, 180, 0x0014);
        check("菜单移动后旧点击目的地失效", !menuTarget.Valid && !menuTarget.TryClick(menu.Screen(25, 65), false));
        menuTarget = WindowMessageTarget.Observe(main.Handle, menu.Handle)!;
        SetWindowPos(main.Handle, 0, -19990, -20000, 300, 180, 0x0014);
        check("主窗口移动后菜单和列表的旧定位同时失效", !mainTarget.Valid && !menuTarget.Valid && !mainTarget.TryClick(main.Screen(42, 31), true));
        menuTarget = WindowMessageTarget.Observe(main.Handle, menu.Handle)!;
        menu.Dispose();
        check("菜单关闭后不会把点击发送给桌面或其他窗口", !menuTarget.Valid && !menuTarget.TryClick(new(-19925, -19895), false) && !menuTarget.TryDismiss());
        Application.DoEvents();
        check("失效窗口和越界点击不会产生额外鼠标消息", main.Clicks.Count == 2 && menu.Clicks.Count == 2 && unrelated.Clicks.Count == 0);
    }
}
