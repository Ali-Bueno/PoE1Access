using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PoE1Access;

/// <summary>
/// A plain text log in PoE1Access_dumps\PoE1Access_log.txt, next to Loader.exe.
/// It records start-up, the speech engine, state changes, hotkey presses, saved files and
/// errors, so a test run can be diagnosed from the file alone. Logging never throws.
/// </summary>
internal static class PluginLog
{
    private static readonly object Lock = new object();
    private const long MaxBytes = 2_000_000;

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "PoE1Access_dumps");
    public static string FilePath => Path.Combine(Folder, "PoE1Access_log.txt");

    // Email addresses (the login screen's account field) never go into the log, which players
    // send in bug reports.
    private static readonly System.Text.RegularExpressions.Regex Email =
        new System.Text.RegularExpressions.Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");

    public static void Write(string message)
    {
        try
        {
            message = Email.Replace(message ?? "", "[email]");
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Folder, "PoE1Access_log_old.txt"), true);
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never break the plugin
        }
    }
}

/// <summary>
/// Detects a key going down, once per press. Read-only: it asks Windows whether a key is
/// held (GetAsyncKeyState) and never sends or blocks any input.
/// </summary>
internal sealed class KeyWatcher
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly Dictionary<Keys, bool> _wasDown = new Dictionary<Keys, bool>();

    public bool IsDown(Keys key) =>
        key != Keys.None && (GetAsyncKeyState((int)(key & Keys.KeyCode)) & 0x8000) != 0;

    public bool PressedOnce(Keys key)
    {
        if (key == Keys.None) return false;

        bool down = (GetAsyncKeyState((int)(key & Keys.KeyCode)) & 0x8000) != 0;
        _wasDown.TryGetValue(key, out bool was);
        _wasDown[key] = down;
        return down && !was;
    }
}
