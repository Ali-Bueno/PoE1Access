using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Threading;

namespace PoE1AccessReader;

/// <summary>
/// PoE1AccessReader.exe gameProcessId exileApiDir pluginDir logFile
///
/// Started (hidden) by PoE1Access.exe. Loads the plugin's StandaloneHost from pluginDir, with
/// ExileApi's own library from exileApiDir, and runs it about twenty times a second until the
/// game closes. The host reads the login and character screens and goes quiet by itself while
/// ExileApi runs the plugin. Read-only.
/// </summary>
internal static class Program
{
    private static string _log;

    private static int Main(string[] args)
    {
        if (args.Length < 4) return 2;
        int pid = int.Parse(args[0]);
        string exileApiDir = args[1], pluginDir = args[2];
        _log = args[3];

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (string dir in new[] { pluginDir, exileApiDir })
            {
                string f = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(f)) return ctx.LoadFromAssemblyPath(f);
            }
            return null;
        };

        Process game;
        try { game = Process.GetProcessById(pid); }
        catch { Log("Game process " + pid + " not found"); return 1; }

        try
        {
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(pluginDir, "PoE1Access.dll"));
            Type hostType = asm.GetType("PoE1Access.StandaloneHost", true);
            var create = hostType.GetMethod("Create");
            var tick = hostType.GetMethod("Tick");

            object host = null;
            int failures = 0;
            while (host == null && !game.HasExited)
            {
                try
                {
                    host = create.Invoke(null, new object[] { pid });
                    Log("Attached to the game");
                }
                catch (Exception ex)
                {
                    // The first failure in full, then short lines (the game may still be loading).
                    Log("Not ready yet: " + (failures++ == 0 ? ex.ToString() : (ex.InnerException?.Message ?? ex.Message)));
                    Thread.Sleep(2000);
                }
            }

            int errors = 0;
            while (host != null && !game.HasExited)
            {
                try { tick.Invoke(host, null); errors = 0; }
                catch (Exception ex) { if (errors++ < 3) Log("Error: " + (ex.InnerException ?? ex)); }
                Thread.Sleep(50);
            }
            (host as IDisposable)?.Dispose();
            Log("Game closed");
            return 0;
        }
        catch (Exception ex)
        {
            Log("Could not start: " + ex);
            return 1;
        }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(_log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine); } catch { }
    }
}
