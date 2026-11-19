using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Cache;

namespace PoE1Access;

/// <summary>
/// Runs the plugin's pre-game reading (login, character selection, character creation) inside
/// the reader helper (app\reader\PoE1AccessReader.exe), before ExileApi has started. ExileApi
/// only starts once a character is in the game, so without this the login screen is silent.
///
/// It uses ExileApi's own library the way ExileApi does: its memory reader opens the game
/// read-only, its pattern search finds the game's data, and TheGame reads the game states.
/// TheGame reads the list of game states when it is created, so one created while the game is
/// still starting sees none and stays empty: it is created only once states exist, and created
/// again whenever its list goes empty or stale for two seconds.
///
/// While ExileApi runs the plugin, it holds the "PoE1Access.InExileApi" mutex and this host
/// stays quiet; if ExileApi closes, it takes over again.
///
/// Called by reflection: StandaloneHost.Create(int gameProcessId), then Tick() about twenty
/// times a second, and Dispose() at the end. Read-only: it never writes to the game.
/// </summary>
public sealed class StandaloneHost : IDisposable
{
    private readonly Memory _memory;
    private TheGame _game;
    private readonly PoE1AccessCore _plugin;
    private bool _paused;
    private long _staleSinceMs = -1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private StandaloneHost(Memory memory, TheGame game, PoE1AccessCore plugin)
    {
        _memory = memory;
        _game = game;
        _plugin = plugin;
    }

    /// <summary>Attaches to the game. Throws while the game is not ready yet (call again later).</summary>
    public static StandaloneHost Create(int gameProcessId)
    {
        Process proc = Process.GetProcessById(gameProcessId);

        // ExileApi's Memory(Process, Mutex, CoreSettings) constructor is not public.
        ConstructorInfo ctor = typeof(Memory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 3);
        var memory = (Memory)ctor.Invoke(new object[] { proc, new Mutex(), new CoreSettings() });

        TheGame game = NewGame(memory);
        if (!HasStates(game))
        {
            memory.Dispose();
            throw new InvalidOperationException("the game has no screens yet (still starting)");
        }

        var plugin = new PoE1AccessCore();
        plugin.StartStandalone(game, memory);
        PluginLog.Write("Standalone host attached to " + proc.ProcessName + " (" + proc.Id + "), states: " + StateNames(game));
        return new StandaloneHost(memory, game, plugin);
    }

    private static TheGame NewGame(Memory memory) => new TheGame(memory, new Cache(), new CoreSettings(), null);

    private static bool HasStates(TheGame game)
    {
        try
        {
            return game.AllGameStates != null && game.AllGameStates.Count > 0 &&
                   game.CurrentGameStates != null && game.CurrentGameStates.Count > 0;
        }
        catch { return false; }
    }

    private static string StateNames(TheGame game)
    {
        try
        {
            var names = game.AllGameStates.ToDictionary(k => k.Value, k => k.Key.ToString());
            return string.Join(", ", game.CurrentGameStates.Select(s => names.TryGetValue(s.Address, out var n) ? n : "?"));
        }
        catch { return "?"; }
    }

    // A current state that is not one of the known states means the list is stale.
    private static bool LooksStale(TheGame game)
    {
        try
        {
            if (!HasStates(game)) return true;
            var known = game.AllGameStates.Values.ToHashSet();
            return game.CurrentGameStates.Any(s => s == null || !known.Contains(s.Address));
        }
        catch { return true; }
    }

    /// <summary>True while ExileApi runs the plugin itself.</summary>
    public static bool ExileApiHasPlugin()
    {
        if (Mutex.TryOpenExisting(PoE1AccessCore.InExileApiMutexName, out Mutex m))
        {
            m.Dispose();
            return true;
        }
        return false;
    }

    public void Tick()
    {
        if (ExileApiHasPlugin())
        {
            if (!_paused)
            {
                _paused = true;
                _plugin.StandalonePause();
                PluginLog.Write("Standalone host paused: ExileApi runs the plugin");
            }
            return;
        }
        if (_paused)
        {
            _paused = false;
            PluginLog.Write("Standalone host resumed");
        }

        // Keep the game object fresh: a new one when its state list has been empty or stale
        // for two seconds.
        long now = _clock.ElapsedMilliseconds;
        if (LooksStale(_game))
        {
            if (_staleSinceMs < 0) _staleSinceMs = now;
            if (now - _staleSinceMs >= 2000)
            {
                _staleSinceMs = -1;
                try
                {
                    TheGame fresh = NewGame(_memory);
                    if (HasStates(fresh))
                    {
                        _game = fresh;
                        _plugin.SetStandaloneGame(fresh);
                        PluginLog.Write("Standalone host: game data refreshed, states: " + StateNames(fresh));
                    }
                }
                catch (Exception ex) { PluginLog.Write("Standalone host: refresh failed: " + ex.Message); }
            }
        }
        else _staleSinceMs = -1;

        _plugin.StandaloneTick();
    }

    public void Dispose()
    {
        try { _memory?.Dispose(); } catch { }
    }
}
