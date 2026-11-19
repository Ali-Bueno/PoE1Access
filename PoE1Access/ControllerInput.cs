using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PoE1Access;

/// <summary>
/// Reads the controller (XInput) and turns the mod button gestures into commands, following
/// the Poe2Access layout. Read-only: it cannot hide a press from the game, which also sees it.
///
///   L3 tap                 -> Tap        (where the scanner's choice is / repeat in menus)
///   L3 double tap          -> DoubleTap  (life, mana, energy shield)
///   L3 hold + right stick  -> ScanUp/Down/Left/Right (category / entry)
///   L3 hold + R3           -> Guide      (guide beacon on / off)
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

    private const ushort L3 = 0x0040, R3 = 0x0080;
    private const long TapMaxMs = 350, DoubleTapGapMs = 300;

    private bool _useOld;
    private int _pad = -1;
    private long _nextPadSearchMs;

    private bool _l3Down, _r3Down, _l3Used;
    private long _l3DownAtMs, _lastTapMs = -1;
    private bool _pendingTap;
    private bool _flickArmed = true;

    /// <summary>While the mod's settings menu is open: right stick flicks move in it without
    /// holding L3 (they come out as the Scan commands).</summary>
    public bool MenuMode;

    public Vector2 LeftStick { get; private set; }
    public Vector2 RightStick { get; private set; }
    public bool Connected => _pad >= 0;

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
        if (_pad < 0)
        {
            if (now < _nextPadSearchMs) return Command.None;
            _nextPadSearchMs = now + 2000;
            for (int i = 0; i < 4; i++)
                if (Read(i, out _)) { _pad = i; break; }
            if (_pad < 0) return Command.None;
        }

        if (!Read(_pad, out XInputState st)) { _pad = -1; return Command.None; }

        var g = st.Gamepad;
        LeftStick = Stick(g.LX, g.LY);
        RightStick = Stick(g.RX, g.RY);
        bool l3 = (g.Buttons & L3) != 0, r3 = (g.Buttons & R3) != 0;

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
            if (r3 && !_r3Down) { _l3Used = true; cmd = Command.Guide; }

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
            // A release after a short press: a tap, or the second of a double tap.
            if (_pendingTap && now - _lastTapMs <= DoubleTapGapMs)
            {
                _pendingTap = false;
                cmd = Command.DoubleTap;
            }
            else
            {
                _pendingTap = true;
                _lastTapMs = now;
            }
        }

        // A single tap fires once the double-tap window has passed.
        if (cmd == Command.None && _pendingTap && !l3 && now - _lastTapMs > DoubleTapGapMs)
        {
            _pendingTap = false;
            cmd = Command.Tap;
        }

        _l3Down = l3;
        _r3Down = r3;
        return cmd;
    }

    private static Vector2 Stick(short x, short y)
    {
        var v = new Vector2(x / 32767f, y / 32767f);
        return v.Length() < 0.2f ? Vector2.Zero : v;
    }
}
