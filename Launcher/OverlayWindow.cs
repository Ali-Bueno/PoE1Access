using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PoE1AccessLauncher;

/// <summary>
/// Exile API draws on a transparent window of its own ("ExileApi") over the game: its menu, its
/// notifications and its log lines, which the mod does not need (its plugins keep running with
/// the window hidden; checked 20 Nov 2026). Windows lists that window in Alt+Tab and the
/// taskbar, so a player switching windows by ear meets an extra, silent window, and it can take
/// the focus from the game, which then ignores the controller. While the game runs, the window
/// is kept hidden, and a tool window (left out of Alt+Tab and the taskbar) that does not take
/// the focus; Exile API sets its own window state again at times, so this is checked twice a
/// second. If the window has the focus anyway, the game is given it back.
/// "PoE1Access.exe --show-exileapi" leaves the window shown, for Exile API's own menu (F12).
/// </summary>
internal static class OverlayWindow
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    private static bool _logged, _show;
    private const int SW_HIDE = 0;

    /// <summary>Waits for the game to close, keeping Exile API's window out of the way meanwhile.</summary>
    public static void KeepQuietUntilExit(Process game, Process loader, bool show, Action<string> log)
    {
        _show = show;
        while (true)
        {
            try { if (game.WaitForExit(500)) return; }
            catch { return; }
            try { Tame(game, loader, log); } catch { }
        }
    }

    private static void Tame(Process game, Process loader, Action<string> log)
    {
        if (loader == null || loader.HasExited) return;
        uint loaderId = (uint)loader.Id;
        IntPtr overlay = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != loaderId) return true;
            var title = new System.Text.StringBuilder(64);
            GetWindowText(h, title, title.Capacity);
            if (title.ToString() != "ExileApi") return true;
            overlay = h;
            return false;
        }, IntPtr.Zero);
        if (overlay == IntPtr.Zero) return;

        long ex = GetWindowLongPtr(overlay, GWL_EXSTYLE).ToInt64();
        long want = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_APPWINDOW;
        if (want != ex)
        {
            SetWindowLongPtr(overlay, GWL_EXSTYLE, new IntPtr(want));
            SetWindowPos(overlay, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            if (!_logged) { _logged = true; log("Exile API's window kept " + (_show ? "" : "hidden, ") + "out of Alt+Tab and the focus"); }
        }

        if (!_show && IsWindowVisible(overlay)) ShowWindow(overlay, SW_HIDE);

        if (GetForegroundWindow() == overlay)
        {
            game.Refresh();
            IntPtr gameWindow = game.MainWindowHandle;
            if (gameWindow != IntPtr.Zero) SetForegroundWindow(gameWindow);
        }
    }

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
}
