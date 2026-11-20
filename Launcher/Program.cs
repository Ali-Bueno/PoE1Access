using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using PoE1Access;

namespace PoE1AccessLauncher;

/// <summary>
/// PoE1Access.exe: starts everything with one double-click.
///
///  1. Checks the .NET 10 Desktop Runtime that ExileApi needs.
///  2. First run: asks, then downloads ExileApi from its official GitHub page into app\ExileApi.
///     (ExileApi carries no license, so it is not shipped inside this package.)
///  3. Installs the ready-built plugin into ExileApi\Plugins\Compiled\PoE1Access and moves
///     ExileApi's other bundled plugins (automation such as PickIt, flask routines, AutoQuit)
///     to Plugins\Disabled, once. They can be moved back by hand.
///  4. Starts Path of Exile through Steam if it is not running, then ExileApi's Loader.exe.
///  5. Closes ExileApi and itself when the game closes.
///
/// Everything it says is spoken (NVDA or the Windows voice) and also shown in a message box
/// when it needs an answer. Log: app\launcher.log.
/// </summary>
internal static class Program
{
    private const string ExileApiZipUrl = "https://github.com/exApiTools/ExileApi-Compiled/archive/refs/heads/master.zip";
    private const string ExileApiPage = "https://github.com/exApiTools/ExileApi-Compiled";
    private const string DotNetPage = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string SteamPoE1 = "steam://rungameid/238960";

    private static readonly string Root = AppContext.BaseDirectory;
    private static readonly string AppDir = Path.Combine(Root, "app");
    private static readonly string ExileApiDir = Path.Combine(AppDir, "ExileApi");
    private static readonly string PluginPayload = Path.Combine(AppDir, "plugin");
    private static readonly string LogFile = Path.Combine(AppDir, "launcher.log");

    private static ISpeech _speech;

    [STAThread]
    private static int Main(string[] args)
    {
        // The test switches may run next to a real launcher.
        bool test = args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a.EndsWith("-test", StringComparison.OrdinalIgnoreCase)) ||
                    args.Any(a => a.Equals("--setup-only", StringComparison.OrdinalIgnoreCase));
        using var single = new Mutex(true, test ? "PoE1Access.Launcher.Test" : "PoE1Access.Launcher", out bool first);
        // Started by the previous launcher after an update: wait for it to close.
        if (!first && args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase)))
        {
            try { first = single.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            Box("Path of Exile access is already running.", "Path of Exile access", MB_ICONINFO);
            return 0;
        }

        Directory.CreateDirectory(AppDir);
        try { _speech = SpeechFactory.Create(); Log("Speech: " + _speech.Name); } catch { }
        Say("Path of Exile access starting.");

        try
        {
            // The mod's own updates: a newer release on GitHub is installed, then this starts again.
            // (--setup-only updates too; the "-test" switches do not.)
            if (!args.Any(a => a.EndsWith("-test", StringComparison.OrdinalIgnoreCase)) &&
                !args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase)) &&
                SelfUpdate(args.Where(a => !a.Equals("--updated", StringComparison.OrdinalIgnoreCase)).ToArray()))
                return 0;
            CleanUpOldLauncher();

            if (!CheckDotNet()) return 1;
            if (!EnsureExileApi()) return 1;
            InstallPlugin();

            // "PoE1Access.exe --setup-only": check and install, start nothing (for testing).
            if (args.Any(a => a.Equals("--setup-only", StringComparison.OrdinalIgnoreCase)))
            {
                Log("Setup only: done");
                Say("Setup finished.");
                Thread.Sleep(1500);
                return 0;
            }

            // "PoE1Access.exe --standalone-test": attach to the running game for five seconds and
            // log what the pre-game reader sees (for testing).
            if (args.Any(a => a.Equals("--standalone-test", StringComparison.OrdinalIgnoreCase)))
            {
                Process running = FindGame();
                if (running == null) { Log("Standalone test: game not running"); return 1; }
                Process testReader = StartReader(running);
                Thread.Sleep(6000);
                StopReader(testReader);
                Log("Standalone test: done");
                return 0;
            }

            if (!StartGame(out Process game)) return 1;

            // Read the login and character screens until ExileApi takes over.
            Process reader = StartReader(game);
            Process loader = StartLoader();
            if (loader == null) { StopReader(reader); return 1; }

            Say("Ready.");
            WaitForGameToClose(game, loader);
            StopReader(reader);
            return 0;
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex);
            Say("Path of Exile access could not start. " + ex.Message);
            Box("Path of Exile access could not start:\n\n" + ex.Message + "\n\nDetails are in " + LogFile, "Path of Exile access", MB_ICONERROR);
            return 1;
        }
    }

    // ---- 0. The mod's own updates ----
    //
    // Each start asks GitHub for the mod's newest release (test versions included). When it is
    // newer than app\version.txt, its PoE1Access.zip is downloaded and unpacked over this folder:
    // the launcher, app\plugin, app\reader, the readme and licenses. Exile API (app\ExileApi, with
    // its settings) and the logs are left alone. The running launcher cannot overwrite itself, so
    // it renames itself to PoE1Access.old.exe first, then starts the new one and closes.
    private const string ReleasesApi = "https://api.github.com/repos/Random4358/PoE1Access/releases?per_page=10";
    private static readonly string VersionFile = Path.Combine(AppDir, "version.txt");

    private static Version ParseVersion(string s)
    {
        s = (s ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(s, out Version v) ? v : null;
    }

    private static bool SelfUpdate(string[] args)
    {
        Version have = File.Exists(VersionFile) ? ParseVersion(File.ReadAllText(VersionFile)) : null;
        string tag, zipUrl;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var newest = doc.RootElement.EnumerateArray()
                .Where(r => !r.GetProperty("draft").GetBoolean())
                .Select(r => (Tag: r.GetProperty("tag_name").GetString(), Release: r))
                .Where(r => ParseVersion(r.Tag) != null)
                .OrderByDescending(r => ParseVersion(r.Tag))
                .FirstOrDefault();
            if (newest.Tag == null) return false;
            tag = newest.Tag;
            zipUrl = newest.Release.GetProperty("assets").EnumerateArray()
                .Where(a => a.GetProperty("name").GetString() == "PoE1Access.zip")
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault();
            if (zipUrl == null || !zipUrl.StartsWith("https://github.com/Random4358/PoE1Access/releases/download/", StringComparison.Ordinal))
                return false;
        }
        catch (Exception ex)
        {
            Log("Update check failed (offline?): " + ex.Message);
            return false;
        }

        Version latest = ParseVersion(tag);
        if (have != null && latest <= have) { Log("Path of Exile access is up to date: " + have); return false; }

        Log("Mod update: " + (have?.ToString() ?? "unknown") + " -> " + latest);
        Say("Updating Path of Exile access to version " + latest + ". Please wait.");
        string zip = Path.Combine(AppDir, "update-download.zip");
        string temp = Path.Combine(AppDir, "update-extract");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
                File.WriteAllBytes(zip, http.GetByteArrayAsync(zipUrl).GetAwaiter().GetResult());
            }
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            ZipFile.ExtractToDirectory(zip, temp);
            // The zip holds one folder (PoE1Access) with PoE1Access.exe in it.
            string top = Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "PoE1Access.exe")))
                         ?? throw new InvalidOperationException("The update does not contain PoE1Access.exe.");

            string self = Environment.ProcessPath ?? Path.Combine(Root, "PoE1Access.exe");
            string old = Path.Combine(Root, "PoE1Access.old.exe");
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);   // a running program may be renamed, not overwritten

            int files = 0;
            foreach (string src in Directory.GetFiles(top, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(top, src);
                if (rel.StartsWith(Path.Combine("app", "ExileApi"), StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(Root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                try { File.Copy(src, dest, true); }
                catch (IOException)
                {
                    // In use by this launcher (prism.dll, its speech): move it aside, then copy.
                    string aside = dest + ".old";
                    if (File.Exists(aside)) File.Delete(aside);
                    File.Move(dest, aside);
                    File.Copy(src, dest, true);
                }
                files++;
            }
            File.WriteAllText(VersionFile, latest.ToString());   // also for releases that did not ship one
            Log("Mod updated to " + latest + ": " + files + " files");
            Say("Updated to version " + latest + ". Starting.");

            // Start the new launcher (it removes PoE1Access.old.exe) and close this one.
            var psi = new ProcessStartInfo(Path.Combine(Root, "PoE1Access.exe")) { UseShellExecute = false, WorkingDirectory = Root };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--no-update");
            psi.ArgumentList.Add("--updated");
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log("Mod update failed: " + ex);
            // Put the launcher back if it was moved and nothing replaced it.
            try
            {
                string self = Path.Combine(Root, "PoE1Access.exe"), old = Path.Combine(Root, "PoE1Access.old.exe");
                if (!File.Exists(self) && File.Exists(old)) File.Move(old, self);
            }
            catch { }
            Say("Path of Exile access could not be updated. Starting the version you have.");
            return false;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
        }
    }

    // The previous launcher, left after an update; it can go once it has closed.
    private static void CleanUpOldLauncher()
    {
        var leftovers = new[] { Path.Combine(Root, "PoE1Access.old.exe") }
            .Concat(Directory.EnumerateFiles(Root, "*.old", SearchOption.AllDirectories)
                .Where(f => !f.StartsWith(ExileApiDir, StringComparison.OrdinalIgnoreCase)))
            .Where(File.Exists).ToList();
        foreach (string old in leftovers)
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(250); }
            }
    }

    // ---- 1. .NET 10 Desktop Runtime ----

    private static bool CheckDotNet()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string shared = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
        bool ok = Directory.Exists(shared) &&
                  Directory.GetDirectories(shared).Any(d => Path.GetFileName(d).StartsWith("10."));
        Log(".NET 10 Desktop Runtime: " + (ok ? "found" : "missing"));
        if (ok) return true;

        const string msg = "Exile API needs the Microsoft .NET 10 Desktop Runtime, which is not installed. " +
                           "Open the download page now? Choose the Desktop Runtime for Windows x64, install it, " +
                           "then start Path of Exile access again.";
        Say(msg);
        if (Box(msg, "Path of Exile access", MB_YESNO | MB_ICONWARNING) == IDYES) Open(DotNetPage);
        return false;
    }

    // ---- 2. ExileApi, downloaded on first run ----

    // ExileApi's version: the latest commit of its GitHub page, recorded after each download.
    private const string ExileApiCommitApi = "https://api.github.com/repos/exApiTools/ExileApi-Compiled/commits/master";
    private static readonly string ExileApiVersionFile = Path.Combine(AppDir, "exileapi-version.txt");

    private static string LatestExileApiVersion()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.sha");
            string sha = http.GetStringAsync(ExileApiCommitApi).GetAwaiter().GetResult().Trim();
            return sha.Length >= 7 && sha.Length <= 64 && sha.All(Uri.IsHexDigit) ? sha : null;
        }
        catch (Exception ex)
        {
            Log("ExileApi version check failed (offline?): " + ex.Message);
            return null;
        }
    }

    // ExileApi is updated after each game patch (the game's memory layout moves). Each start
    // checks its GitHub page and, when there is a newer version, downloads it over the old one,
    // keeping its settings (config) and the plugins it had put aside.
    private static void UpdateExileApi()
    {
        string latest = LatestExileApiVersion();
        if (latest == null) return;
        string have = File.Exists(ExileApiVersionFile) ? File.ReadAllText(ExileApiVersionFile).Trim() : "";
        if (have == latest) { Log("ExileApi is up to date: " + latest); return; }

        Log("ExileApi update: " + (have.Length > 0 ? have : "unknown") + " -> " + latest);
        Say("Updating Exile API to its newest version. Please wait.");
        string zip = Path.Combine(AppDir, "exileapi-download.zip");
        string temp = Path.Combine(AppDir, "exileapi-extract");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
                File.WriteAllBytes(zip, http.GetByteArrayAsync(ExileApiZipUrl).GetAwaiter().GetResult());
            }
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            ZipFile.ExtractToDirectory(zip, temp);
            string top = Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "Loader.exe")))
                         ?? throw new InvalidOperationException("The Exile API download does not contain Loader.exe.");

            string disabled = Path.Combine(ExileApiDir, "Plugins", "Disabled");
            int files = 0;
            foreach (string src in Directory.GetFiles(top, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(top, src);
                string dest = Path.Combine(ExileApiDir, rel);
                // Keep the user's settings.
                if (rel.StartsWith("config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
                // A bundled plugin that was put aside is updated where it is, not brought back.
                string compiledPrefix = Path.Combine("Plugins", "Compiled") + Path.DirectorySeparatorChar;
                if (rel.StartsWith(compiledPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string inner = rel.Substring(compiledPrefix.Length);
                    string pluginName = inner.Split(Path.DirectorySeparatorChar)[0];
                    if (Directory.Exists(Path.Combine(disabled, pluginName)) ||
                        !Directory.Exists(Path.Combine(ExileApiDir, "Plugins", "Compiled", pluginName)) && File.Exists(Path.Combine(AppDir, "other-plugins-disabled.txt")))
                        dest = Path.Combine(disabled, inner);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, true);
                files++;
            }
            File.WriteAllText(ExileApiVersionFile, latest);
            Log("ExileApi updated: " + files + " files");
            Say("Exile API updated.");
        }
        catch (Exception ex)
        {
            Log("ExileApi update failed: " + ex);
            Say("Exile API could not be updated. Starting with the version you have.");
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            try { if (File.Exists(zip)) File.Delete(zip); } catch { }
        }
    }

    private static bool EnsureExileApi()
    {
        if (File.Exists(Path.Combine(ExileApiDir, "Loader.exe")))
        {
            UpdateExileApi();
            return true;
        }

        string msg = "Path of Exile access runs on Exile API, a separate program that reads the game. " +
                     "It is not included in this download. Download it now from " + ExileApiPage + "? " +
                     "About 25 megabytes. Like any tool that reads the game, Grinding Gear Games has not approved it.";
        Say(msg);
        if (Box(msg, "Download Exile API", MB_YESNO | MB_ICONQUESTION) != IDYES)
        {
            Say("Exile API was not downloaded. Path of Exile access cannot start without it.");
            return false;
        }

        Say("Downloading Exile API. Please wait.");
        string zip = Path.Combine(AppDir, "exileapi-download.zip");
        string temp = Path.Combine(AppDir, "exileapi-extract");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
            byte[] data = http.GetByteArrayAsync(ExileApiZipUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(zip, data);
        }
        Log("Downloaded ExileApi: " + new FileInfo(zip).Length + " bytes");

        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        ZipFile.ExtractToDirectory(zip, temp);
        // The zip holds one top folder (ExileApi-Compiled-master); move its contents into place.
        string top = Directory.GetDirectories(temp).FirstOrDefault(d => File.Exists(Path.Combine(d, "Loader.exe")))
                     ?? throw new InvalidOperationException("The Exile API download does not contain Loader.exe.");
        if (Directory.Exists(ExileApiDir)) Directory.Delete(ExileApiDir, true);
        Directory.Move(top, ExileApiDir);
        Directory.Delete(temp, true);
        File.Delete(zip);
        string version = LatestExileApiVersion();
        if (version != null) File.WriteAllText(ExileApiVersionFile, version);

        Say("Exile API downloaded.");
        return true;
    }

    // ---- 3. The plugin ----

    private static void InstallPlugin()
    {
        if (!File.Exists(Path.Combine(PluginPayload, "PoE1Access.dll")))
            throw new FileNotFoundException("The plugin files are missing from app\\plugin. Unzip the whole package again.");

        string plugins = Path.Combine(ExileApiDir, "Plugins");
        string compiled = Path.Combine(plugins, "Compiled");
        string target = Path.Combine(compiled, "PoE1Access");
        Directory.CreateDirectory(target);
        foreach (string f in Directory.GetFiles(PluginPayload))
            File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);

        // A source copy would be compiled and loaded a second time.
        string source = Path.Combine(plugins, "Source", "PoE1Access");
        if (Directory.Exists(source)) Directory.Delete(source, true);

        // Once: put ExileApi's other bundled plugins aside (many press keys or click for you).
        string marker = Path.Combine(AppDir, "other-plugins-disabled.txt");
        if (!File.Exists(marker))
        {
            string disabled = Path.Combine(plugins, "Disabled");
            Directory.CreateDirectory(disabled);
            int moved = 0;
            foreach (string dir in Directory.GetDirectories(compiled))
            {
                if (Path.GetFileName(dir).Equals("PoE1Access", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(disabled, Path.GetFileName(dir));
                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                Directory.Move(dir, dest);
                moved++;
            }
            File.WriteAllText(marker, "Exile API's other plugins were moved to Plugins\\Disabled on " + DateTime.Now +
                                      ". Move a folder back to Plugins\\Compiled to use it again.");
            Log("Moved " + moved + " other plugins to Plugins\\Disabled");
        }
        Log("Plugin installed into " + target);
    }

    // ---- 4. Game and ExileApi ----

    private static Process FindGame()
    {
        return Process.GetProcesses().FirstOrDefault(p =>
        {
            string n = p.ProcessName;
            return n.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase) &&
                   !n.StartsWith("PathOfExile2", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool StartGame(out Process game)
    {
        game = FindGame();
        if (game != null) { Log("Game already running: " + game.ProcessName); return true; }

        Say("Starting Path of Exile through Steam.");
        try { Open(SteamPoE1); }
        catch (Exception ex) { Log("Steam start failed: " + ex.Message); }

        for (int i = 0; i < 180 && game == null; i++)   // up to 3 minutes
        {
            Thread.Sleep(1000);
            game = FindGame();
        }
        if (game != null) { Log("Game started: " + game.ProcessName); return true; }

        const string msg = "Path of Exile did not start. Start the game yourself, then start Path of Exile access again.";
        Say(msg);
        Box(msg, "Path of Exile access", MB_ICONWARNING);
        return false;
    }

    private static Process StartLoader()
    {
        Process running = Process.GetProcessesByName("Loader")
            .FirstOrDefault(p => { try { return p.MainModule?.FileName?.StartsWith(ExileApiDir, StringComparison.OrdinalIgnoreCase) == true; } catch { return false; } });
        if (running != null) { Log("Exile API already running"); return running; }

        var psi = new ProcessStartInfo(Path.Combine(ExileApiDir, "Loader.exe"))
        {
            WorkingDirectory = ExileApiDir,
            UseShellExecute = true,
        };
        Process p = Process.Start(psi);
        Log("Started Exile API, process " + p?.Id);
        return p;
    }

    private static void WaitForGameToClose(Process game, Process loader)
    {
        OverlayWindow.KeepQuietUntilExit(game, loader, Log);
        Log("Game closed");
        try { if (!loader.HasExited) loader.Kill(true); } catch { }
        Say("Path of Exile closed. Path of Exile access stopped.");
        Thread.Sleep(2500);   // let the last line be spoken
    }

    // ---- 5. Pre-game screens, before ExileApi has started ----

    // app\reader\PoE1AccessReader.exe reads the login and character screens with the plugin's
    // StandaloneHost. It is a separate, small program on the installed .NET 10 runtime: ExileApi's
    // library refuses to start inside this single-file launcher.
    private static Process StartReader(Process game)
    {
        string exe = Path.Combine(AppDir, "reader", "PoE1AccessReader.exe");
        if (!File.Exists(exe)) { Log("Reader missing: " + exe); return null; }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            psi.ArgumentList.Add(game.Id.ToString());
            psi.ArgumentList.Add(ExileApiDir);
            psi.ArgumentList.Add(PluginPayload);
            psi.ArgumentList.Add(Path.Combine(AppDir, "reader.log"));
            Process p = Process.Start(psi);
            Log("Started the pre-game reader, process " + p?.Id);
            return p;
        }
        catch (Exception ex)
        {
            Log("Pre-game reader could not start: " + ex.Message);
            return null;
        }
    }

    private static void StopReader(Process reader)
    {
        try { if (reader != null && !reader.HasExited) reader.Kill(); } catch { }
    }
    // ---- helpers ----

    private static void Say(string text)
    {
        Log("Say: " + text);
        try { _speech?.Speak(text, false); } catch { }
    }

    private static void Log(string line)
    {
        try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine); } catch { }
    }

    private static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private const uint MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30, MB_ICONINFO = 0x40;
    private const int IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static int Box(string text, string caption, uint type) => MessageBoxW(IntPtr.Zero, text, caption, type);
}
