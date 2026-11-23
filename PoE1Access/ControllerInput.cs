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

    public const int DPadUp = 1, DPadDown = 2, DPadLeft = 4, DPadRight = 8;
    /// <summary>D-pad directions newly pressed on the last poll (DPadUp | DPadDown...).</summary>
    public int DPadPressed { get; private set; }
    /// <summary>When B, X or Y was last pressed: it opens an item's action menu or picks one of its actions.</summary>
    public long FacePressedMs { get; private set; } = -100000;
    private bool _bDown;
    private int _dpadDown;

    public Vector2 LeftStick { get; private set; }
    public Vector2 RightStick { get; private set; }
    public bool Connected => _pad >= 0 || _ps.Connected || (Game?.Found ?? false);

    /// <summary>The game's own copy of the controller state, from its memory. In the Xbox full
    /// screen experience Windows gives the pad only to the game, so XInput reads nothing here;
    /// this is merged with what XInput gives.</summary>
    public GameXInput Game;

    // A PlayStation controller, used when no XInput controller is connected (and the setting
    // allows it: off, the mod leaves the controller alone).
    private readonly PlayStationPad _ps = new PlayStationPad();
    public bool AllowPlayStation = true;

    /// <summary>Lets go of a PlayStation controller the mod had open.</summary>
    public void ClosePlayStation() { if (_ps.Connected) { try { _ps.Dispose(); } catch { } } }
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
        ushort gb = 0; short glx = 0, gly = 0, grx = 0, gry = 0;
        bool fromGame = Game != null && Game.TryRead(now, out gb, out glx, out gly, out grx, out gry);
        if (_pad < 0 && !_ps.Connected && !fromGame)
        {
            if (now < _nextPadSearchMs) return Command.None;
            _nextPadSearchMs = now + 2000;
            for (int i = 0; i < 4; i++)
                if (Read(i, out _)) { _pad = i; break; }
            if (_pad < 0)
            {
                bool found = false;
                if (AllowPlayStation) { try { found = _ps.Open(); } catch { } }
                if (found != _psLogged)
                {
                    _psLogged = found;
                    PluginLog.Write(found ? "Controller: " + _ps.Name : "Controller: none found (XInput or PlayStation)");
                }
                if (!found) return Command.None;
            }
        }

        bool l3, bButton;
        int dpad;
        if (_pad >= 0)
        {
            if (!Read(_pad, out XInputState st)) { _pad = -1; return Command.None; }
            var g = st.Gamepad;
            LeftStick = Stick(g.LX, g.LY);
            RightStick = Stick(g.RX, g.RY);
            l3 = (g.Buttons & L3) != 0;
            dpad = g.Buttons & 0x0F;   // XInput: up 1, down 2, left 4, right 8
            bButton = (g.Buttons & 0xE000) != 0;   // B, X or Y (A picks items up all the time)
        }
        else if (!_ps.Connected)
        {
            if (!fromGame) return Command.None;
            LeftStick = RightStick = Vector2.Zero;
            l3 = bButton = false;
            dpad = 0;
        }
        else
        {
            LeftStick = _ps.LeftStick;
            RightStick = _ps.RightStick;
            l3 = _ps.L3;
            dpad = _ps.DPad;
            bButton = _ps.FaceButton;
        }
        if (fromGame)
        {
            // Whichever source sees more: XInput is neutral to background apps in full screen mode.
            Vector2 gl = Stick(glx, gly), gr = Stick(grx, gry);
            if (gl.Length() > LeftStick.Length()) LeftStick = gl;
            if (gr.Length() > RightStick.Length()) RightStick = gr;
            l3 |= (gb & L3) != 0;
            dpad |= gb & 0x0F;
            bButton |= (gb & 0xE000) != 0;
        }
        // D-pad directions pressed since the last poll (DPadUp, DPadDown, DPadLeft, DPadRight).
        DPadPressed = dpad & ~_dpadDown;
        if (bButton && !_bDown) FacePressedMs = now;
        _bDown = bButton;
        _dpadDown = dpad;

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
