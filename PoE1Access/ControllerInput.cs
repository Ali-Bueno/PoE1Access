using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PoE1Access;

/// <summary>
/// Reads the controller (XInput, or a PlayStation controller through PlayStationPad) and turns
/// the mod button gestures into commands, following the Poe2Access layout. Read-only: it cannot
/// hide a press from the game, which also sees it.
///
///   L3 tap                 -> Tap        (where the scanner's choice is / repeat in menus)
///   L3 double tap          -> DoubleTap  (life, mana, energy shield)
///   L3 triple tap          -> Guide      (guide beacon on / off)
///   L3 hold + right stick  -> ScanUp/Down/Left/Right (category / entry)
/// R3 is left to the game: it opens skill binding there, so a guide on hold L3 + R3 kept
/// opening it too.
/// </summary>
internal sealed class ControllerInput
{
    //   L3 hold (half a second)       -> Hold      (in menus: the whole tooltip)
    //   L3 hold (a second and a half) -> LongHold  (in the inventory: the whole grid)
    public enum Command { None, Tap, DoubleTap, ScanUp, ScanDown, ScanLeft, ScanRight, Guide, Hold, LongHold }

    private const long HoldMs = 500, LongHoldMs = 1500;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint Packet;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState14(int index, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState910(int index, out XInputState state);

    private const ushort L3 = 0x0040;
    private const long TapMaxMs = 350, DoubleTapGapMs = 300;

    private bool _useOld;
    private int _pad = -1;
    private long _nextPadSearchMs;

    private bool _l3Down, _l3Used;
    private long _l3DownAtMs, _lastTapMs = -1;
    private int _taps;   // short presses so far, each within DoubleTapGapMs of the last
    private bool _flickArmed = true;

    /// <summary>While the mod's settings menu is open: right stick flicks move in it without
    /// holding L3 (they come out as the Scan commands).</summary>
    public bool MenuMode;

    public Vector2 LeftStick { get; private set; }
    public Vector2 RightStick { get; private set; }
    public bool Connected => _pad >= 0 || _ps.Connected;

    // A PlayStation controller, used when no XInput controller is connected.
    private readonly PlayStationPad _ps = new PlayStationPad();
    private bool _psLogged;

    private bool Read(int index, out XInputState s)
    {
        try
        {
            int r = _useOld ? GetState910(index, out s) : GetState14(index, out s);
            return r == 0;
        }
        catch (DllNotFoundException)
        {
            _useOld = true;
            s = default;
            return false;
        }
        catch
        {
            s = default;
            return false;
        }
    }

    public Command Poll(long now)
    {
        if (_pad < 0 && !_ps.Connected)
        {
            if (now < _nextPadSearchMs) return Command.None;
            _nextPadSearchMs = now + 2000;
            for (int i = 0; i < 4; i++)
                if (Read(i, out _)) { _pad = i; break; }
            if (_pad < 0)
            {
                bool found = false;
                try { found = _ps.Open(); } catch { }
                if (found != _psLogged)
                {
                    _psLogged = found;
                    PluginLog.Write(found ? "Controller: " + _ps.Name : "Controller: none found (XInput or PlayStation)");
                }
                if (!found) return Command.None;
            }
        }

        bool l3;
        if (_pad >= 0)
        {
            if (!Read(_pad, out XInputState st)) { _pad = -1; return Command.None; }
            var g = st.Gamepad;
            LeftStick = Stick(g.LX, g.LY);
            RightStick = Stick(g.RX, g.RY);
            l3 = (g.Buttons & L3) != 0;
        }
        else
        {
            if (!_ps.Connected) return Command.None;
            LeftStick = _ps.LeftStick;
            RightStick = _ps.RightStick;
            l3 = _ps.L3;
        }

        Command cmd = Command.None;

        if (l3 && !_l3Down) { _l3DownAtMs = now; _l3Used = false; }

        if (MenuMode && !l3)
        {
            float mm = RightStick.Length();
            if (mm < 0.3f) _flickArmed = true;
            else if (mm > 0.65f && _flickArmed)
            {
                _flickArmed = false;
                Vector2 v = RightStick;
                cmd = Math.Abs(v.Y) >= Math.Abs(v.X)
                    ? (v.Y > 0 ? Command.ScanUp : Command.ScanDown)
                    : (v.X > 0 ? Command.ScanRight : Command.ScanLeft);
            }
        }

        if (l3)
        {
            // Hold + right stick flick (edge-triggered, re-armed when the stick returns).
            float m = RightStick.Length();
            if (m < 0.3f) _flickArmed = true;
            else if (m > 0.65f && _flickArmed)
            {
                _flickArmed = false;
                _l3Used = true;
                Vector2 v = RightStick;
                cmd = Math.Abs(v.Y) >= Math.Abs(v.X)
                    ? (v.Y > 0 ? Command.ScanUp : Command.ScanDown)
                    : (v.X > 0 ? Command.ScanRight : Command.ScanLeft);
            }

            // Long hold fires while still held.
            if (cmd == Command.None && !_l3Used && now - _l3DownAtMs >= LongHoldMs)
            {
                _l3Used = true;
                cmd = Command.LongHold;
            }
        }

        // A hold released between half a second and a second and a half.
        if (!l3 && _l3Down && !_l3Used && now - _l3DownAtMs >= HoldMs)
        {
            _l3Used = true;
            cmd = Command.Hold;
        }

        if (!l3 && _l3Down && !_l3Used && now - _l3DownAtMs <= TapMaxMs)
        {
            // A release after a short press: the first, second or third of a run of taps.
            _taps = _taps > 0 && now - _lastTapMs <= DoubleTapGapMs ? _taps + 1 : 1;
            _lastTapMs = now;
            if (_taps >= 3) { _taps = 0; cmd = Command.Guide; }
        }

        // One or two taps count once the gap for another has passed.
        if (cmd == Command.None && _taps > 0 && !l3 && now - _lastTapMs > DoubleTapGapMs)
        {
            cmd = _taps == 1 ? Command.Tap : Command.DoubleTap;
            _taps = 0;
        }

        _l3Down = l3;
        return cmd;
    }

    private static Vector2 Stick(short x, short y)
    {
        var v = new Vector2(x / 32767f, y / 32767f);
        return v.Length() < 0.2f ? Vector2.Zero : v;
    }
}
