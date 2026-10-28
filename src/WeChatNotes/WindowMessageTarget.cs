using System.Runtime.InteropServices;
using System.Text;

namespace WeChatNotes;

// A frozen, observed window destination. Coordinates are converted to its client area;
// no desktop input, foreground activation or cursor movement is involved.
internal sealed class WindowMessageTarget
{
    private readonly nint root, target;
    private readonly Snapshot main, destination;
    private sealed record Snapshot(uint Process, uint Thread, string Class, Rectangle Bounds, Rectangle Client, Point Origin);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeWindows.NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint window, ref Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int length);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nuint wparam, nint lparam);

    private WindowMessageTarget(nint root, nint target, Snapshot main, Snapshot destination)
        => (this.root, this.target, this.main, this.destination) = (root, target, main, destination);

    private static Snapshot? Read(nint window)
    {
        if (!NativeWindows.IsWindow(window) || !IsWindowVisible(window) || NativeWindows.IsIconic(window) ||
            !NativeWindows.GetWindowRect(window, out var bounds) || !GetClientRect(window, out var client)) return null;
        var origin = Point.Empty;
        if (!ClientToScreen(window, ref origin)) return null;
        uint thread = GetWindowThreadProcessId(window, out uint process);
        var name = new StringBuilder(256);
        if (thread == 0 || GetClassName(window, name, name.Capacity) == 0) return null;
        return new(process, thread, name.ToString(), Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            Rectangle.FromLTRB(client.Left, client.Top, client.Right, client.Bottom), origin);
    }
    private static bool OwnedBy(nint target, nint root)
    {
        for (int i = 0; i < 8 && target != 0; i++, target = GetWindow(target, 4)) if (target == root) return true;
        return false;
    }
    internal static WindowMessageTarget? Observe(nint root, nint target)
    {
        var main = Read(root); var destination = Read(target);
        return main != null && destination != null && main.Process == destination.Process && OwnedBy(target, root)
            ? new(root, target, main, destination) : null;
    }
    internal bool Valid => Read(root) == main && Read(target) == destination && OwnedBy(target, root);
    internal bool TryDismiss()
    {
        // Only dismiss the still-valid popup we opened, never the main conversation.
        if (target == root || !Valid) return false;
        return PostMessageW(target, 0x0100, 0x1B, 0x00010001) &&
            PostMessageW(target, 0x0101, 0x1B, unchecked((nint)0xC0010001u));
    }
    internal bool TryClick(Point screen, bool right)
    {
        if (!Valid || !ScreenToClient(target, ref screen) || !destination.Client.Contains(screen) ||
            screen.X is < short.MinValue or > short.MaxValue || screen.Y is < short.MinValue or > short.MaxValue) return false;
        // LPARAM contains signed 16-bit client coordinates, even on a 64-bit process.
        nint position = unchecked((nint)((uint)(ushort)screen.X | ((uint)(ushort)screen.Y << 16)));
        if (!PostMessageW(target, right ? 0x0204u : 0x0201u, right ? 2u : 1u, position)) return false;
        return PostMessageW(target, right ? 0x0205u : 0x0202u, 0, position);
    }
}
