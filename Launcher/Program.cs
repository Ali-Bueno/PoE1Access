using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using PoE1Access;
using static PoE1Access.Loc;

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
///     Set as the game's launch option in Steam ("...\PoE1Access.exe" %command%), it starts
///     whenever the game is started from Steam, and starts the game itself (see _gameCommand).
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
        // Started by Steam as the game's launch option: the game's own command comes after the
        // launcher's switches. The switches are the launcher's; the rest is passed to the game.
        int gameAt = Array.FindIndex(args, a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        if (gameAt >= 0)
        {
            _gameCommand = args.Skip(gameAt).ToArray();
            args = args.Take(gameAt).ToArray();
        }
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
        if (!first && _gameCommand != null)
        {
            // The launcher asked Steam to start the game, and Steam started it through this
            // launch option: start the game and stay until it closes, so Steam knows it runs.
            Directory.CreateDirectory(AppDir);
            Log("Steam started the game while the launcher runs: " + _gameCommand[0]);
            Process g = StartGameCommand();
            try { g?.WaitForExit(); } catch { }
            return 0;
        }
        if (!first)
        {
            Box(T("Path of Exile access is already running.", "Path of Exile access ya se está ejecutando."), "Path of Exile access", MB_ICONINFO);
            return 0;
        }

        Directory.CreateDirectory(AppDir);
        try { Loc.UseGameLanguage(Log); } catch { }
        try { _speech = SpeechFactory.Create(); Log("Speech: " + _speech.Name); } catch { }
        Say(T("Path of Exile access starting.", "Path of Exile access se está iniciando."));

        try
        {
            // The mod's own updates: a newer release on GitHub is installed, then this starts again.
            // (--setup-only updates too; the "-test" switches do not.)
            if (!args.Any(a => a.EndsWith("-test", StringComparison.OrdinalIgnoreCase)) &&
                !args.Any(a => a.Equals("--no-update", StringComparison.OrdinalIgnoreCase)) &&
                SelfUpdate(args.Where(a => !a.Equals("--updated", StringComparison.OrdinalIgnoreCase)).ToArray()))
                return 0;
            CleanUpOldLauncher();
            // Just updated: say what changed (once).
            SayWhatsNew(args.Any(a => a.Equals("--updated", StringComparison.OrdinalIgnoreCase)));

            if (!CheckDotNet()) return 1;
            if (!EnsureExileApi()) return 1;
            InstallPlugin();

            // "PoE1Access.exe --setup-only": check and install, start nothing (for testing).
            if (args.Any(a => a.Equals("--setup-only", StringComparison.OrdinalIgnoreCase)))
            {
                Log("Setup only: done");
                Say(T("Setup finished.", "Configuración terminada."));
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

            if (_gameCommand != null) NoteSteamLaunch();
            else OfferSteamLaunch(args.Any(a => a.Equals("--steam-setup", StringComparison.OrdinalIgnoreCase)));

            if (!StartGame(out Process game)) return 1;

            // Read the login and character screens until ExileApi takes over.
            Process reader = StartReader(game);
            Process loader = StartLoader();
            if (loader == null) { StopReader(reader); return 1; }

            Say(T("Ready.", "Listo."));
            WaitForGameToClose(game, loader, args.Any(a => a.Equals("--show-exileapi", StringComparison.OrdinalIgnoreCase)));
            StopReader(reader);
            return 0;
        }
        catch (Exception ex)
        {
            Log("Failed: " + ex);
            Say(T($"Path of Exile access could not start. {ex.Message}", $"Path of Exile access no pudo iniciarse. {ex.Message}"));
            Box(T($"Path of Exile access could not start:\n\n{ex.Message}\n\nDetails are in {LogFile}", $"Path of Exile access no pudo iniciarse:\n\n{ex.Message}\n\nLos detalles están en {LogFile}"), "Path of Exile access", MB_ICONERROR);
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

    // The release notes as plain words, kept in app\whats-new.txt (see SayWhatsNew).
    private static readonly string WhatsNewFile = Path.Combine(AppDir, "whats-new.txt");

    /// <summary>The notes in plain words, newest version first: markdown, links and the
    /// "Test version" line taken out.</summary>
    private static string WhatsNewText(System.Collections.Generic.List<(Version Version, string Body)> notes)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (v, body) in notes.OrderByDescending(n => n.Version))
        {
            sb.AppendLine(T($"Version {v}.", $"Versión {v}."));
            foreach (string raw in (body ?? "").Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("Test version", StringComparison.OrdinalIgnoreCase)) continue;
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");   // [text](link) -> text
                line = line.Replace("**", "").Replace("`", "").Replace("@", "").TrimStart('#', '-', '*', ' ');
                if (line.Length == 0) continue;
                if (!".:!?".Contains(line[line.Length - 1])) line += ".";
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    // What changed: on the first start of a new version (after an update, which the old launcher
    // did, so the new one fetches the notes itself), the release notes of every version since the
    // one last announced are read once and kept in app\whats-new.txt.
    private static readonly string WhatsNewSeenFile = Path.Combine(AppDir, "whats-new-seen.txt");

    private static void SayWhatsNew(bool justUpdated)
    {
        try
        {
            Version current = File.Exists(VersionFile) ? ParseVersion(File.ReadAllText(VersionFile)) : null;
            if (current == null) return;
            Version seen = File.Exists(WhatsNewSeenFile) ? ParseVersion(File.ReadAllText(WhatsNewSeenFile)) : null;
            File.WriteAllText(WhatsNewSeenFile, current.ToString());
            // A first install says nothing; an update, or a version newer than the last announced, does.
            if (seen == null ? !justUpdated : current <= seen) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PoE1Access-launcher");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var notes = new System.Collections.Generic.List<(Version Version, string Body)>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("draft").GetBoolean()) continue;
                Version v = ParseVersion(r.GetProperty("tag_name").GetString());
                // Every version since the last one announced; only this one when that is not known.
                if (v == null || v > current || (seen != null ? v <= seen : v != current)) continue;
                string body = r.TryGetProperty("body", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String ? b.GetString() : "";
                notes.Add((v, body ?? ""));
            }
            if (notes.Count == 0) return;
            string text = WhatsNewText(notes);
            File.WriteAllText(WhatsNewFile, text);
            Log("What's new read for " + string.Join(", ", notes.Select(n => n.Version)));
            string spoken = text.Replace(Environment.NewLine, " ").Replace("\n", " ");
            Say(T($"What's new. {spoken}", $"Novedades. {spoken}"));
            Say(T("This is kept in app, whats-new.txt.", "Esto se guarda en app, whats-new.txt."));
        }
        catch (Exception ex) { Log("What's new not read: " + ex.Message); }
    }

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
        Say(T($"Updating Path of Exile access to version {latest}. Please wait.", $"Actualizando Path of Exile access a la versión {latest}. Espera, por favor."));
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
            Say(T($"Updated to version {latest}. Starting.", $"Actualizado a la versión {latest}. Iniciando."));

            // Start the new launcher (it removes PoE1Access.old.exe) and close this one.
            var psi = new ProcessStartInfo(Path.Combine(Root, "PoE1Access.exe")) { UseShellExecute = false, WorkingDirectory = Root };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--no-update");
            psi.ArgumentList.Add("--updated");
            // The game's command from Steam goes last, as Steam gave it.
            if (_gameCommand != null) foreach (string a in _gameCommand) psi.ArgumentList.Add(a);
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
            Say(T("Path of Exile access could not be updated. Starting the version you have.", "Path of Exile access no se pudo actualizar. Se inicia la versión que tienes."));
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

        string msg = T("Exile API needs the Microsoft .NET 10 Desktop Runtime, which is not installed. Open the download page now? Choose the Desktop Runtime for Windows x64, install it, then start Path of Exile access again.",
                       "Exile API necesita el Microsoft .NET 10 Desktop Runtime, que no está instalado. ¿Abrir la página de descarga ahora? Elige el Desktop Runtime para Windows x64, instálalo y vuelve a iniciar Path of Exile access.");
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
        Say(T("Updating Exile API to its newest version. Please wait.", "Actualizando Exile API a su versión más reciente. Espera, por favor."));
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
            Say(T("Exile API updated.", "Exile API actualizado."));
        }
        catch (Exception ex)
        {
            Log("ExileApi update failed: " + ex);
            Say(T("Exile API could not be updated. Starting with the version you have.", "Exile API no se pudo actualizar. Se inicia con la versión que tienes."));
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

        string msg = T($"Path of Exile access runs on Exile API, a separate program that reads the game. It is not included in this download. Download it now from {ExileApiPage}? About 25 megabytes. Like any tool that reads the game, Grinding Gear Games has not approved it.",
                       $"Path of Exile access funciona sobre Exile API, un programa aparte que lee el juego. No está incluido en esta descarga. ¿Descargarlo ahora desde {ExileApiPage}? Unos 25 megabytes. Como cualquier herramienta que lee el juego, Grinding Gear Games no la ha aprobado.");
        Say(msg);
        if (Box(msg, T("Download Exile API", "Descargar Exile API"), MB_YESNO | MB_ICONQUESTION) != IDYES)
        {
            Say(T("Exile API was not downloaded. Path of Exile access cannot start without it.", "Exile API no se descargó. Path of Exile access no puede iniciarse sin él."));
            return false;
        }

        Say(T("Downloading Exile API. Please wait.", "Descargando Exile API. Espera, por favor."));
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

        Say(T("Exile API downloaded.", "Exile API descargado."));
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

        if (_gameCommand != null)
        {
            // Started by Steam: start the game as Steam asked.
            Say(T("Starting Path of Exile.", "Iniciando Path of Exile."));
            StartGameCommand();
        }
        else
        {
            Say(T("Starting Path of Exile through Steam.", "Iniciando Path of Exile a través de Steam."));
            try { Open(SteamPoE1); }
            catch (Exception ex) { Log("Steam start failed: " + ex.Message); }
        }

        for (int i = 0; i < 180 && game == null; i++)   // up to 3 minutes
        {
            Thread.Sleep(1000);
            game = FindGame();
        }
        if (game != null) { Log("Game started: " + game.ProcessName); return true; }

        string msg = T("Path of Exile did not start. Start the game yourself, then start Path of Exile access again.", "Path of Exile no se inició. Inicia el juego tú mismo y luego vuelve a iniciar Path of Exile access.");
        Say(msg);
        Box(msg, "Path of Exile access", MB_ICONWARNING);
        return false;
    }

    // ---- Starting with the game from Steam ----
    //
    // Steam's launch option for Path of Exile, "<folder>\PoE1Access.exe" %command%, makes Steam
    // start this launcher in place of the game, with the game's command after it (its exe and
    // arguments). The launcher then starts the game from that command and everything else as
    // usual, and stays until the game closes, so Steam shows the game as running. Steam keeps
    // launch options in a file it rewrites as it closes, so the launcher does not set it itself:
    // it copies it to the clipboard for you to paste into the game's Properties.
    private static string[] _gameCommand;
    private static readonly string SteamLaunchFile = Path.Combine(AppDir, "steam-launch-option.txt");
    private static readonly string SteamLaunchAskedFile = Path.Combine(AppDir, "steam-launch-asked.txt");
    private static readonly string SteamLaunchSetFile = Path.Combine(AppDir, "steam-launch-set.txt");

    private static string SteamLaunchOption => "\"" + Path.Combine(Root, "PoE1Access.exe") + "\" %command%";

    private static Process StartGameCommand()
    {
        try
        {
            string exe = _gameCommand[0];
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            foreach (string a in _gameCommand.Skip(1)) psi.ArgumentList.Add(a);
            Process p = Process.Start(psi);
            Log("Started the game from Steam's command: " + exe + ", process " + p?.Id);
            return p;
        }
        catch (Exception ex)
        {
            Log("The game could not be started from Steam's command: " + ex.Message);
            return null;
        }
    }

    private static void NoteSteamLaunch()
    {
        try
        {
            if (File.Exists(SteamLaunchSetFile)) return;
            File.WriteAllText(SteamLaunchSetFile, "Started by Steam's launch option on " + DateTime.Now);
            Log("Started by Steam's launch option");
        }
        catch { }
    }

    private static bool SteamInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && path.Length > 0;
        }
        catch { return false; }
    }

    // Asked once, with Steam installed and the launch option not used yet; "PoE1Access.exe
    // --steam-setup" asks again.
    private static void OfferSteamLaunch(bool again)
    {
        try
        {
            File.WriteAllText(SteamLaunchFile, SteamLaunchOption + Environment.NewLine);
            if (!again && (File.Exists(SteamLaunchAskedFile) || File.Exists(SteamLaunchSetFile) || !SteamInstalled())) return;
            File.WriteAllText(SteamLaunchAskedFile, DateTime.Now.ToString());
            string msg = T("Path of Exile access can start by itself whenever you start Path of Exile from Steam. Copy Steam's launch option to the clipboard now? Then, in your Steam library, open Path of Exile's Properties, and on the General page paste it into Launch Options. It is also kept in app, steam-launch-option.txt. This is asked only once; start PoE1Access.exe with --steam-setup to be asked again.",
                           "Path of Exile access puede iniciarse solo cada vez que inicias Path of Exile desde Steam. ¿Copiar ahora la opción de lanzamiento de Steam al portapapeles? Después, en tu biblioteca de Steam, abre las Propiedades de Path of Exile y, en la página General, pégala en Opciones de lanzamiento. También se guarda en app, steam-launch-option.txt. Solo se pregunta una vez; inicia PoE1Access.exe con --steam-setup para que se pregunte otra vez.");
            Say(msg);
            if (Box(msg, T("Start with Steam", "Iniciar con Steam"), MB_YESNO | MB_ICONQUESTION) != IDYES) { Log("Steam launch option: declined"); return; }
            bool copied = CopyToClipboard(SteamLaunchOption);
            Log("Steam launch option " + (copied ? "copied: " : "not copied: ") + SteamLaunchOption);
            Say(copied ? T("Copied. Paste it into Path of Exile's Launch Options in Steam.", "Copiado. Pégalo en las Opciones de lanzamiento de Path of Exile en Steam.")
                       : T("It could not be copied. It is in app, steam-launch-option.txt.", "No se pudo copiar. Está en app, steam-launch-option.txt."));
        }
        catch (Exception ex) { Log("Steam launch option not offered: " + ex.Message); }
    }

    private static bool CopyToClipboard(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    byte[] bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                    IntPtr mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                    if (mem == IntPtr.Zero) return false;
                    IntPtr ptr = GlobalLock(mem);
                    Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    GlobalUnlock(mem);
                    return SetClipboardData(CF_UNICODETEXT, mem) != IntPtr.Zero;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(100);
        }
        return false;
    }

    private const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x2;
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);

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

    private static void WaitForGameToClose(Process game, Process loader, bool showExileApi)
    {
        OverlayWindow.KeepQuietUntilExit(game, loader, showExileApi, Log);
        Log("Game closed");
        try { if (!loader.HasExited) loader.Kill(true); } catch { }
        Say(T("Path of Exile closed. Path of Exile access stopped.", "Path of Exile se cerró. Path of Exile access se detuvo."));
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
