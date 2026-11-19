using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PoE1Access;

public interface ISpeech
{
    // interrupt = true cuts off whatever is being spoken first.
    void Speak(string text, bool interrupt = false);

    // Read out once at start: "Using <Name>."
    string Name { get; }
}

/// <summary>Picks the speech output: Prism if prism.dll is next to us, else NVDA / the Windows voice.</summary>
public static class SpeechFactory
{
    public static ISpeech Create()
    {
        try
        {
            var prism = PrismSpeech.TryCreate();
            if (prism != null) return prism;
        }
        catch { }
        return new AutoSpeech();
    }
}

/// <summary>
/// Screen reader output through Prism (https://github.com/ethindp/prism, MPL-2.0): NVDA, JAWS
/// and other screen readers, braille displays, and the Windows voice when no screen reader runs.
/// Text goes to speech and braille together (prism_backend_output). Every ten seconds at most it
/// checks whether a better output became available (a screen reader started after the game).
/// prism.dll is loaded from the folder this program or plugin runs from.
/// </summary>
public sealed class PrismSpeech : ISpeech
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InitFn(IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateBestFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NameFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutputFn(IntPtr backend, byte[] utf8, [MarshalAs(UnmanagedType.U1)] bool interrupt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetRateFn(IntPtr backend, float rate);

    private SetRateFn _setRate;
    private float _rate = -1f;

    /// <summary>Speech speed, 0 (slowest) to 1 (fastest). False when the output keeps its own
    /// speed (NVDA, JAWS and other screen readers).</summary>
    public bool SetRate(float rate)
    {
        lock (_lock)
        {
            _rate = rate;
            if (_setRate == null || _backend == IntPtr.Zero) return false;
            try { return _setRate(_backend, rate) == 0; }
            catch { return false; }
        }
    }

    private readonly object _lock = new object();
    private readonly InitFn _init;
    private readonly CreateBestFn _createBest;
    private readonly NameFn _name;
    private readonly OutputFn _output;
    private readonly FreeFn _free;
    private readonly IntPtr _ctx;
    private IntPtr _backend;
    private string _backendName = "";
    private long _nextCheckMs;

    public string Name { get { lock (_lock) return _backendName.Length > 0 ? _backendName : "Prism"; } }

    private PrismSpeech(IntPtr lib)
    {
        T Fn<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

        _init = Fn<InitFn>("prism_init");
        _createBest = Fn<CreateBestFn>("prism_registry_create_best");
        _name = Fn<NameFn>("prism_backend_name");
        _output = Fn<OutputFn>("prism_backend_output");
        _free = Fn<FreeFn>("prism_backend_free");
        if (NativeLibrary.TryGetExport(lib, "prism_backend_set_rate", out IntPtr pRate))
            _setRate = Marshal.GetDelegateForFunctionPointer<SetRateFn>(pRate);

        _ctx = _init(IntPtr.Zero);
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("prism_init failed");
        PickBest(true);
        if (_backend == IntPtr.Zero) throw new InvalidOperationException("Prism found no speech output");
    }

    /// <summary>Null when prism.dll is not next to us or cannot start.</summary>
    public static PrismSpeech TryCreate()
    {
        foreach (string dir in CandidateDirs())
        {
            string path = Path.Combine(dir, "prism.dll");
            if (!File.Exists(path)) continue;
            if (!NativeLibrary.TryLoad(path, out IntPtr lib)) continue;
            try { return new PrismSpeech(lib); }
            catch { return null; }
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        string asm = null;
        try { asm = Path.GetDirectoryName(typeof(PrismSpeech).Assembly.Location); } catch { }
        if (!string.IsNullOrEmpty(asm)) yield return asm;   // the plugin's own folder
        yield return AppContext.BaseDirectory;               // the launcher's folder
    }

    // Use the best output available now; keep the current one if it is still the best.
    private void PickBest(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextCheckMs) return;
        _nextCheckMs = now + 10000;

        IntPtr best = _createBest(_ctx);
        if (best == IntPtr.Zero) return;
        string name = Marshal.PtrToStringUTF8(_name(best)) ?? "";
        if (_backend != IntPtr.Zero && name == _backendName)
        {
            _free(best);
            return;
        }
        if (_backend != IntPtr.Zero) _free(_backend);
        _backend = best;
        _backendName = name;
        if (_rate >= 0 && _setRate != null) { try { _setRate(_backend, _rate); } catch { } }   // keep the chosen speed
    }

    public void Speak(string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_lock)
            {
                PickBest(false);
                if (_backend == IntPtr.Zero) return;
                byte[] utf8 = Encoding.UTF8.GetBytes(text + "\0");
                int err = _output(_backend, utf8, interrupt);
                if (err != 0)
                {
                    // The output went away (screen reader closed): pick again and retry once.
                    PickBest(true);
                    if (_backend != IntPtr.Zero) _output(_backend, utf8, interrupt);
                }
            }
        }
        catch
        {
            // never let speech crash the HUD
        }
    }
}

/// <summary>
/// Fallback when prism.dll is not available.
/// Speaks through NVDA when it is running, otherwise through the Windows voice (SAPI).
/// It re-checks every couple of seconds, so starting or closing NVDA while the game is
/// running just works. Speech problems are swallowed on purpose: they must never crash the HUD.
/// </summary>
public sealed class AutoSpeech : ISpeech
{
    private readonly object _lock = new object();
    private NvdaClient _nvda;
    private SapiVoice _sapi;
    private bool _sapiTried;
    private bool _nvdaRunning;
    private long _nextProbeMs;
    private long _nextSearchMs;

    public AutoSpeech()
    {
        lock (_lock) Probe(true);
    }

    public string Name
    {
        get { lock (_lock) return _nvdaRunning ? "NVDA" : "the Windows voice"; }
    }

    public void Speak(string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            lock (_lock)
            {
                Probe(false);
                if (_nvdaRunning && _nvda != null)
                {
                    if (_nvda.Speak(text, interrupt)) return;
                    _nvdaRunning = false; // the call failed: fall back until the next probe
                }
                SpeakSapi(text, interrupt);
            }
        }
        catch
        {
            // never let speech crash the HUD
        }
    }

    private void Probe(bool force)
    {
        long now = Environment.TickCount64;
        if (!force && now < _nextProbeMs) return;
        _nextProbeMs = now + 2000;

        // The client DLL may not be findable yet (NVDA started after us), so look again
        // now and then, but not every probe: listing processes is not free.
        if (_nvda == null && (force || now >= _nextSearchMs))
        {
            _nextSearchMs = now + 10000;
            _nvda = NvdaClient.TryLoad();
        }

        _nvdaRunning = _nvda != null && _nvda.IsRunning();
    }

    private void SpeakSapi(string text, bool interrupt)
    {
        if (!_sapiTried)
        {
            _sapiTried = true;
            _sapi = SapiVoice.TryCreate();
        }
        _sapi?.Speak(text, interrupt);
    }
}

/// <summary>
/// NVDA's controller client, loaded by full path so it is found wherever NVDA keeps it.
/// Search order: the folder of the running NVDA, the usual Program Files folders, then the
/// folder next to Loader.exe (the documented place to copy the DLL if all else fails).
/// </summary>
internal sealed class NvdaClient
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int TestIfRunningFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SpeakTextFn([MarshalAs(UnmanagedType.LPWStr)] string text);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CancelSpeechFn();

    private static readonly string[] DllNames = { "nvdaControllerClient64.dll", "nvdaControllerClient.dll" };

    private readonly TestIfRunningFn _test;
    private readonly SpeakTextFn _speak;
    private readonly CancelSpeechFn _cancel;

    private NvdaClient(TestIfRunningFn test, SpeakTextFn speak, CancelSpeechFn cancel)
    {
        _test = test;
        _speak = speak;
        _cancel = cancel;
    }

    public static NvdaClient TryLoad()
    {
        foreach (string dir in CandidateDirectories())
        {
            foreach (string name in DllNames)
            {
                try
                {
                    string path = Path.Combine(dir, name);
                    if (!File.Exists(path)) continue;
                    if (!NativeLibrary.TryLoad(path, out IntPtr lib)) continue; // e.g. a 32-bit DLL

                    if (NativeLibrary.TryGetExport(lib, "nvdaController_testIfRunning", out IntPtr pTest) &&
                        NativeLibrary.TryGetExport(lib, "nvdaController_speakText", out IntPtr pSpeak) &&
                        NativeLibrary.TryGetExport(lib, "nvdaController_cancelSpeech", out IntPtr pCancel))
                    {
                        return new NvdaClient(
                            Marshal.GetDelegateForFunctionPointer<TestIfRunningFn>(pTest),
                            Marshal.GetDelegateForFunctionPointer<SpeakTextFn>(pSpeak),
                            Marshal.GetDelegateForFunctionPointer<CancelSpeechFn>(pCancel));
                    }
                }
                catch
                {
                    // try the next candidate
                }
            }
        }
        return null;
    }

    private static List<string> CandidateDirectories()
    {
        var dirs = new List<string>();

        try
        {
            foreach (Process p in Process.GetProcessesByName("nvda"))
            {
                try
                {
                    string file = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(file)) dirs.Add(Path.GetDirectoryName(file));
                }
                catch { /* NVDA may run at a higher privilege level: skip */ }
                finally { p.Dispose(); }
            }
        }
        catch { }

        AddIfSet(dirs, Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "NVDA");
        AddIfSet(dirs, Environment.GetEnvironmentVariable("ProgramFiles"), "NVDA");
        dirs.Add(AppContext.BaseDirectory);
        return dirs;
    }

    private static void AddIfSet(List<string> dirs, string root, string sub)
    {
        if (!string.IsNullOrEmpty(root)) dirs.Add(Path.Combine(root, sub));
    }

    public bool IsRunning()
    {
        try { return _test() == 0; }
        catch { return false; }
    }

    public bool Speak(string text, bool interrupt)
    {
        try
        {
            if (interrupt) _cancel();
            return _speak(text) == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The built-in Windows voice, used through late-bound COM so no extra package is needed.
/// </summary>
internal sealed class SapiVoice
{
    // SpeechVoiceSpeakFlags: 1 = asynchronous, 2 = purge pending speech first, 16 = plain text (not XML).
    private const int Async = 1;
    private const int PurgeBeforeSpeak = 2;
    private const int IsNotXml = 16;

    private readonly dynamic _voice;

    private SapiVoice(object voice)
    {
        _voice = voice;
    }

    public static SapiVoice TryCreate()
    {
        try
        {
            Type type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null) return null;
            return new SapiVoice(Activator.CreateInstance(type));
        }
        catch
        {
            return null;
        }
    }

    public void Speak(string text, bool interrupt)
    {
        try
        {
            int flags = Async | IsNotXml | (interrupt ? PurgeBeforeSpeak : 0);
            _voice.Speak(text, flags);
        }
        catch
        {
            // ignore: speech must never crash the HUD
        }
    }
}
