using System;
using System.Collections.Generic;
using SharpDX;
using SharpDX.Multimedia;
using SharpDX.XAudio2;

namespace PoE1Access;

/// <summary>
/// Plays short synthesized stereo cues through XAudio2 (SharpDX.XAudio2 ships with ExileApi).
/// Every sound is generated here from a recipe; no recorded files are used. Generated buffers
/// are cached by recipe, so a cue costs one voice submit after its first use.
/// Failures are swallowed: sound must never crash the HUD.
/// </summary>
internal sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 44100;

    private XAudio2 _xa;
    private MasteringVoice _master;
    private readonly WaveFormat _format = new WaveFormat(SampleRate, 16, 2);
    private readonly List<(SourceVoice Voice, DataStream Data)> _voices = new List<(SourceVoice, DataStream)>();
    private readonly Dictionary<string, short[]> _cache = new Dictionary<string, short[]>();
    private const int MaxVoices = 24;

    public bool Available => _xa != null;

    /// <summary>Master volume for every cue (1 = as is), set from the settings menu.</summary>
    public static float Master = 1f;
    public string Error { get; private set; } = "";

    public AudioEngine()
    {
        try
        {
            _xa = new XAudio2();
            _master = new MasteringVoice(_xa, 2, SampleRate);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _xa = null;
        }
    }

    /// <summary>Plays a cached cue. `make` builds the samples (interleaved stereo, -1..1) once.</summary>
    public void Play(string key, Func<float[]> make, float volume)
    {
        volume *= Master;
        if (_xa == null || volume <= 0f) return;
        try
        {
            if (!_cache.TryGetValue(key, out short[] pcm))
            {
                pcm = ToPcm(make());
                _cache[key] = pcm;
            }

            SourceVoice voice = GetVoice(out int slot);
            if (voice == null) return;

            var data = DataStream.Create(pcm, true, false);
            var buffer = new AudioBuffer { Stream = data, AudioBytes = pcm.Length * 2, Flags = BufferFlags.EndOfStream };
            voice.SetVolume(Math.Min(4f, volume));
            voice.SubmitSourceBuffer(buffer, null);
            voice.Start();

            _voices[slot].Data?.Dispose();
            _voices[slot] = (voice, data);
        }
        catch
        {
            // never let sound break the plugin
        }
    }

    private SourceVoice GetVoice(out int slot)
    {
        for (int i = 0; i < _voices.Count; i++)
        {
            if (_voices[i].Voice.State.BuffersQueued == 0) { slot = i; return _voices[i].Voice; }
        }
        if (_voices.Count < MaxVoices)
        {
            var v = new SourceVoice(_xa, _format, true);
            _voices.Add((v, null));
            slot = _voices.Count - 1;
            return v;
        }
        slot = -1;
        return null;
    }

    private static short[] ToPcm(float[] s)
    {
        var pcm = new short[s.Length];
        for (int i = 0; i < s.Length; i++)
            pcm[i] = (short)(Math.Max(-1f, Math.Min(1f, s[i])) * 32000f);
        return pcm;
    }

    public void Dispose()
    {
        try
        {
            foreach (var (voice, data) in _voices)
            {
                voice.DestroyVoice();
                voice.Dispose();
                data?.Dispose();
            }
            _voices.Clear();
            _master?.Dispose();
            _xa?.Dispose();
        }
        catch { }
        _xa = null;
    }
}

/// <summary>
/// Sound recipes, modelled on the documented Poe2Access radar cues (re-synthesized, not copied):
/// a low wooden tock for a wall, a filtered-noise breath for open floor, a bright tin for an
/// exit, two short pips, a falling thud for walking into a wall, and an iron chime for the guide.
/// pan: -1 left .. +1 right. pitch: 1 = normal.
/// </summary>
internal static class Synth
{
    private const int SR = AudioEngine.SampleRate;

    private static float[] Buffer(double seconds) => new float[(int)(seconds * SR) * 2];

    // Constant-power pan.
    private static (float L, float R) Gains(float pan)
    {
        double a = (Math.Max(-1f, Math.Min(1f, pan)) + 1) * Math.PI / 4;
        return ((float)Math.Cos(a), (float)Math.Sin(a));
    }

    private static void Put(float[] b, int i, float v, (float L, float R) g)
    {
        b[2 * i] += v * g.L;
        b[2 * i + 1] += v * g.R;
    }

    // Wall: 260 Hz with a fast decay and a short noise click; 14 % lower when downward.
    public static float[] Tock(float pan, float pitch)
    {
        var b = Buffer(0.09);
        var g = Gains(pan);
        var rnd = new Random(1);
        double f = 260 * pitch;
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double env = Math.Exp(-t / 0.022);
            double v = Math.Sin(2 * Math.PI * f * t) * env * 0.7 +
                       Math.Sin(2 * Math.PI * f * 2.02 * t) * env * env * 0.2;
            if (i < 60) v += (rnd.NextDouble() * 2 - 1) * 0.25 * (1 - i / 60.0);
            Put(b, i, (float)v, g);
        }
        return b;
    }

    // Open floor: 180 ms of low-passed noise whose brightness rises (ahead), falls (behind) or
    // stays (sideways).
    public static float[] Breath(float pan, int contour)
    {
        var b = Buffer(0.18);
        var g = Gains(pan);
        var rnd = new Random(2);
        int n = b.Length / 2;
        double lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double cutoff = contour > 0 ? 600 + 1600 * x : contour < 0 ? 2200 - 1600 * x : 1200;
            double a = 1 - Math.Exp(-2 * Math.PI * cutoff / SR);
            double noise = rnd.NextDouble() * 2 - 1;
            lp += a * (noise - lp);
            lp2 += a * (lp - lp2);
            double env = Math.Sin(Math.PI * x);
            env *= env;
            Put(b, i, (float)(lp2 * env * 1.4), g);
        }
        return b;
    }

    // Exit: bright 1100 Hz tin with an inharmonic partial.
    public static float[] Tin(float pan, float pitch)
    {
        var b = Buffer(0.16);
        var g = Gains(pan);
        double f = 1100 * pitch;
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double env = Math.Exp(-t / 0.045);
            double v = (Math.Sin(2 * Math.PI * f * t) * 0.45 + Math.Sin(2 * Math.PI * f * 2.76 * t) * 0.15) * env;
            Put(b, i, (float)v, g);
        }
        return b;
    }

    // Two 900 Hz pips, 35 ms each, 30 ms apart (after a 30 ms gap).
    public static float[] Pips(float pan, float pitch, int count = 2)
    {
        var b = Buffer(0.03 + count * 0.065);
        var g = Gains(pan);
        double f = 900 * pitch;
        int pip = (int)(0.035 * SR), gap = (int)(0.03 * SR), start = (int)(0.03 * SR);
        for (int k = 0; k < count; k++)
        {
            int s0 = start + k * (pip + gap);
            for (int i = 0; i < pip && s0 + i < b.Length / 2; i++)
            {
                double env = Math.Sin(Math.PI * i / pip);
                Put(b, s0 + i, (float)(Math.Sin(2 * Math.PI * f * i / SR) * env * 0.35), g);
            }
        }
        return b;
    }

    // A cue followed by the unexplored mark: 30 ms gap, then `count` pips (0 = the cue alone).
    // gain compensates for the volume the cue is played at, so pips stay at one loudness.
    public static float[] WithPips(float[] cue, float pan, float pitch, int count, float gain)
    {
        if (count <= 0) return cue;
        float[] pips = Pips(pan, pitch, count);
        var b = new float[cue.Length + pips.Length];
        Array.Copy(cue, b, cue.Length);
        for (int i = 0; i < pips.Length; i++) b[cue.Length + i] = pips[i] * gain;
        return b;
    }

    // Wall bump: 90 ms sine sweep 150 -> 70 Hz over low-passed noise.
    public static float[] Thud(float pan)
    {
        var b = Buffer(0.09);
        var g = Gains(pan);
        var rnd = new Random(3);
        int n = b.Length / 2;
        double phase = 0, lp = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double f = 150 - 80 * x;
            phase += 2 * Math.PI * f / SR;
            lp += 0.05 * ((rnd.NextDouble() * 2 - 1) - lp);
            double env = Math.Exp(-x * 4);
            Put(b, i, (float)((Math.Sin(phase) * 0.8 + lp * 1.5) * env), g);
        }
        return b;
    }

    // ---- Enemies, after the Poe2Access recipe (its enemy-sounds notes; re-synthesized here):
    // every enemy a 90 ms low growl sweeping 175 -> 125 Hz with a 36 Hz flutter; magic and rare
    // add a bright 750 Hz buzz chattering at 62 Hz; uniques add a struck-metal ring. ----

    public static float[] Growl(ExileCore.Shared.Enums.MonsterRarity rarity, float pan, float pitch)
    {
        var b = Buffer(0.09);
        var g = Gains(pan);
        int n = b.Length / 2;
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n, t = (double)i / SR;
            double f = (175 - 50 * x) * pitch;                    // 175 -> 125 Hz
            phase += 2 * Math.PI * f / SR;
            // A buzzy voice: the fundamental with odd and even partials, softly clipped.
            double voice = Math.Tanh(1.8 * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.33 * Math.Sin(3 * phase)));
            double flutter = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 36 * t);
            double env = Math.Min(1, t / 0.006) * Math.Min(1, (1 - x) / 0.25);   // quick attack, short release
            Put(b, i, (float)(voice * flutter * env * 0.5), g);
        }
        switch (rarity)
        {
            case ExileCore.Shared.Enums.MonsterRarity.Magic:
            case ExileCore.Shared.Enums.MonsterRarity.Rare:
                return Mix(b, Buzz(pan, 750 * pitch, 0.09, rarity == ExileCore.Shared.Enums.MonsterRarity.Rare ? 0.2 : 0.13));
            case ExileCore.Shared.Enums.MonsterRarity.Unique:
                // Struck metal: inharmonic partials with a fast attack and a ringing tail.
                return Mix(b, Bell(pan, 620 * pitch, new[] { 1.0, 2.76, 5.4, 8.93 }, 0.08, 0.32, 0.0));
            default:
                return b;
        }
    }

    // Crosshair (Poe2Access): two short blips when the game's target lands on a living enemy.
    public static float[] Blips()
    {
        float[] one = Tone(0f, 1600, 0.025, 0.4);
        int gap = (int)(0.035 * SR) * 2;
        var b = new float[one.Length * 2 + gap];
        Array.Copy(one, b, one.Length);
        Array.Copy(one, 0, b, one.Length + gap, one.Length);
        return b;
    }

    // Reach ping (Poe2Access): two quick rising notes from the side of the enemy that came
    // within melee reach.
    public static float[] ReachPing(float pan)
    {
        float[] a = Tone(pan, 880, 0.045, 0.45), c = Tone(pan, 1320, 0.06, 0.45);
        int gap = (int)(0.015 * SR) * 2;
        var b = new float[a.Length + gap + c.Length];
        Array.Copy(a, b, a.Length);
        Array.Copy(c, 0, b, a.Length + gap, c.Length);
        return b;
    }

    // A clean sine note with a 3 ms attack and a smooth fall.
    private static float[] Tone(float pan, double f, double seconds, double amp)
    {
        var b = Buffer(seconds);
        var g = Gains(pan);
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n, t = (double)i / SR;
            double env = Math.Min(1, t / 0.003) * (1 - x * x);
            Put(b, i, (float)(Math.Sin(2 * Math.PI * f * t) * env * amp), g);
        }
        return b;
    }

    // NPCs and other players, after the Poe2Access recipe: a light wooden tap, 84 ms, a 700 Hz
    // knock; another player gets a quicker, higher double tap.
    public static float[] Tap(float pan, float pitch, bool player)
    {
        float[] one = Knock(pan, (player ? 950 : 700) * pitch, player ? 0.06 : 0.084);
        if (!player) return one;
        var b = new float[one.Length * 2];
        Array.Copy(one, b, one.Length);
        for (int i = 0; i < one.Length; i++) b[one.Length + i] += one[i] * 0.85f;
        return b;
    }

    // Wood: a short sine knock with an inharmonic overtone and a tiny noise click, decaying fast.
    private static float[] Knock(float pan, double f, double seconds)
    {
        var b = Buffer(seconds);
        var g = Gains(pan);
        var rnd = new Random(11);
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double env = Math.Exp(-t / 0.014) * Math.Min(1, t / 0.001);
            double v = Math.Sin(2 * Math.PI * f * t) * 0.6 + Math.Sin(2 * Math.PI * f * 2.3 * t) * 0.2 * Math.Exp(-t / 0.006);
            if (i < 40) v += (rnd.NextDouble() * 2 - 1) * 0.3 * (1 - i / 40.0);
            Put(b, i, (float)(v * env * 0.8), g);
        }
        return b;
    }

    // A bright buzz chattering on and off 62 times a second.
    private static float[] Buzz(float pan, double f, double seconds, double amp)
    {
        var b = Buffer(seconds);
        var g = Gains(pan);
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n, t = (double)i / SR;
            double sq = Math.Tanh(3 * Math.Sin(2 * Math.PI * f * t));
            double chatter = Math.Sin(2 * Math.PI * 62 * t) > 0 ? 1 : 0.15;
            double env = Math.Min(1, t / 0.004) * Math.Min(1, (1 - x) / 0.3);
            Put(b, i, (float)(sq * chatter * env * amp), g);
        }
        return b;
    }

    // ---- Loot on the ground: one cue per kind of item ----

    public enum Loot { Currency, Scroll, Gem, Flask, Quest, Map, Card, Jewel, Normal, Magic, Rare, Unique, Other }

    // Every loot cue starts with a short bright glint (so it cuts through enemy growls and the
    // radar) and is brought up to full loudness.
    public static float[] LootCue(Loot kind, float pan, float pitch)
    {
        float[] cue = Mix(Bell(pan, 3200 * pitch, new[] { 1.0, 1.5 }, 0.025, 0.35, 0), LootBody(kind, pan, pitch));
        return Normalize(cue, 0.92f);
    }

    private static float[] Normalize(float[] b, float peak)
    {
        float max = 0;
        foreach (float v in b) max = Math.Max(max, Math.Abs(v));
        if (max < 1e-4f) return b;
        float k = peak / max;
        for (int i = 0; i < b.Length; i++) b[i] *= k;
        return b;
    }

    private static float[] LootBody(Loot kind, float pan, float pitch)
    {
        switch (kind)
        {
            case Loot.Currency:   // a coin's clink: two bright inharmonic strikes
                return Mix(Bell(pan, 2400 * pitch, new[] { 1.0, 2.31, 3.9 }, 0.05, 0.35, 0),
                           Bell(pan, 2900 * pitch, new[] { 1.0, 2.31 }, 0.04, 0.25, 0.06));
            case Loot.Scroll:     // paper: a short bright rustle
                return Rustle(pan, 0.14, 3000 * pitch);
            case Loot.Gem:        // glass: a high fifth, rising
                return Mix(Bell(pan, 1568 * pitch, new[] { 1.0, 3.0 }, 0.09, 0.3, 0),
                           Bell(pan, 2349 * pitch, new[] { 1.0, 3.0 }, 0.12, 0.3, 0.07));
            case Loot.Flask:      // glass and liquid: a quick upward bubble
                return Bubble(pan, 500 * pitch, 1100 * pitch, 0.16);
            case Loot.Quest:      // a clear bell with a long ring
                return Bell(pan, 880 * pitch, new[] { 1.0, 2.0, 3.0, 4.2 }, 0.35, 0.35, 0);
            case Loot.Map:        // two low horn-like notes
                return Mix(Bell(pan, 294 * pitch, new[] { 1.0, 2.0, 3.0 }, 0.14, 0.3, 0),
                           Bell(pan, 440 * pitch, new[] { 1.0, 2.0, 3.0 }, 0.18, 0.3, 0.11));
            case Loot.Card:       // a card flicked: a very short rustle and a tick
                return Mix(Rustle(pan, 0.06, 4500 * pitch), Bell(pan, 1800 * pitch, new[] { 1.0 }, 0.02, 0.2, 0.05));
            case Loot.Jewel:      // a sparkle: three quick high notes
                return Mix(Mix(Bell(pan, 2093 * pitch, new[] { 1.0, 2.0 }, 0.05, 0.22, 0),
                               Bell(pan, 2637 * pitch, new[] { 1.0, 2.0 }, 0.05, 0.22, 0.05)),
                           Bell(pan, 3136 * pitch, new[] { 1.0, 2.0 }, 0.07, 0.22, 0.1));
            case Loot.Normal:     // equipment: a dull wooden thump
                return Bell(pan, 190 * pitch, new[] { 1.0, 2.7 }, 0.05, 0.6, 0);
            case Loot.Magic:      // the thump with a blue note above it
                return Mix(Bell(pan, 190 * pitch, new[] { 1.0, 2.7 }, 0.05, 0.5, 0),
                           Bell(pan, 784 * pitch, new[] { 1.0, 2.0 }, 0.12, 0.3, 0.03));
            case Loot.Rare:       // the thump with a bright rising triad
                return Mix(Mix(Bell(pan, 190 * pitch, new[] { 1.0, 2.7 }, 0.05, 0.5, 0),
                               Bell(pan, 659 * pitch, new[] { 1.0, 2.0 }, 0.12, 0.28, 0.03)),
                           Mix(Bell(pan, 784 * pitch, new[] { 1.0, 2.0 }, 0.12, 0.28, 0.09),
                               Bell(pan, 988 * pitch, new[] { 1.0, 2.0 }, 0.2, 0.28, 0.15)));
            case Loot.Unique:     // a full chord with a long shimmering tail
                return Mix(Mix(Bell(pan, 392 * pitch, new[] { 1.0, 2.0, 3.0 }, 0.5, 0.22, 0),
                               Bell(pan, 494 * pitch, new[] { 1.0, 2.0, 3.0 }, 0.5, 0.2, 0.04)),
                           Mix(Bell(pan, 587 * pitch, new[] { 1.0, 2.0, 3.0 }, 0.5, 0.2, 0.08),
                               Bell(pan, 1568 * pitch, new[] { 1.0, 2.01 }, 0.6, 0.12, 0.12)));
            default:              // anything else: a soft single tap
                return Bell(pan, 330 * pitch, new[] { 1.0, 2.0 }, 0.06, 0.4, 0);
        }
    }

    // A struck tone: partials at the given ratios, exponential decay, starting `delay` s in.
    private static float[] Bell(float pan, double f, double[] ratios, double decay, double amp, double delay)
    {
        var b = Buffer(delay + decay * 5 + 0.01);
        var g = Gains(pan);
        int start = (int)(delay * SR), n = b.Length / 2;
        for (int i = start; i < n; i++)
        {
            double t = (double)(i - start) / SR;
            double env = Math.Exp(-t / decay) * Math.Min(1, t / 0.002);
            double v = 0;
            for (int k = 0; k < ratios.Length; k++)
                v += Math.Sin(2 * Math.PI * f * ratios[k] * t) * Math.Pow(0.5, k) * (k == 0 ? 1 : Math.Exp(-t / (decay * 0.5)));
            Put(b, i, (float)(v * env * amp), g);
        }
        return b;
    }

    // Band-limited noise with a quick swell and fall.
    private static float[] Rustle(float pan, double seconds, double cutoff)
    {
        var b = Buffer(seconds);
        var g = Gains(pan);
        var rnd = new Random(5);
        int n = b.Length / 2;
        double a = 1 - Math.Exp(-2 * Math.PI * cutoff / SR), lp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double noise = rnd.NextDouble() * 2 - 1;
            lp += a * (noise - lp);
            double hp = lp - prev;   // remove the low rumble
            prev = lp;
            double env = Math.Sin(Math.PI * Math.Pow(x, 0.6)) * (0.6 + 0.4 * Math.Sin(x * 40));
            Put(b, i, (float)(hp * env * 2.2), g);
        }
        return b;
    }

    // A sine gliding upward with a fast wobble: a bubble.
    private static float[] Bubble(float pan, double f0, double f1, double seconds)
    {
        var b = Buffer(seconds);
        var g = Gains(pan);
        int n = b.Length / 2;
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / n;
            double f = f0 + (f1 - f0) * x * x + 40 * Math.Sin(2 * Math.PI * 30 * x * seconds);
            phase += 2 * Math.PI * f / SR;
            double env = Math.Sin(Math.PI * x) * Math.Exp(-x * 1.5);
            Put(b, i, (float)(Math.Sin(phase) * env * 0.4), g);
        }
        return b;
    }

    private static float[] Mix(float[] a, float[] b)
    {
        var r = new float[Math.Max(a.Length, b.Length)];
        for (int i = 0; i < a.Length; i++) r[i] += a[i];
        for (int i = 0; i < b.Length; i++) r[i] += b[i];
        return r;
    }

    // Guide: short iron chime.
    public static float[] Chime(float pan, float pitch)
    {
        var b = Buffer(0.22);
        var g = Gains(pan);
        double f = 660 * pitch;
        int n = b.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double env = Math.Exp(-t / 0.06) * Math.Min(1, t / 0.002);
            double v = (Math.Sin(2 * Math.PI * f * t) * 0.4 +
                        Math.Sin(2 * Math.PI * f * 2.41 * t) * 0.18 +
                        Math.Sin(2 * Math.PI * f * 3.98 * t) * 0.08) * env;
            Put(b, i, (float)v, g);
        }
        return b;
    }
}
