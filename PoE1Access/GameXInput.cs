using System;
using System.Diagnostics;
using System.Linq;
using ExileCore.Shared.Interfaces;

namespace PoE1Access;

/// <summary>
/// The controller as the game itself sees it, read from the game's memory. In the Xbox full
/// screen experience (ROG Xbox Ally) Windows gives controller input only to the app in front:
/// XInput, Windows.Gaming.Input, GameInput and raw HID all read nothing in the mod while the game
/// plays. The game reads the pad with xinput1_4.dll, which keeps each controller's last state in
/// a record of its own inside the game's process, so the mod reads that record instead.
///
/// Found 21 Nov 2026 (xinput1_4 10.0.26100.8875): xinput1_4's .data holds a pointer to a table of
/// device records ([xinput+0x17CE8] + 8 -> record). In a record:
///   +0x10  -1 (8 bytes)
///   +0x3C  packet number (changes with every new state)
///   +0x40  XINPUT_GAMEPAD: buttons, triggers, LX, LY, RX, RY (as XInputGetState gives them)
///   +0x74  vendor id, product id (0B05:1B4C on the Ally)
/// Other Windows builds may move the table, so every pointer in xinput1_4's .data is tried with
/// the same record checks when the known place does not hold one.
/// </summary>
internal sealed class GameXInput
{
    private const int KnownTable = 0x17CE8, RecordSize = 0x80, StateAt = 0x40, IdsAt = 0x74;

    private readonly Func<IMemory> _mem;
    private long _record, _nextSearchMs;
    private int _pid;
    private bool _logged;

    public GameXInput(Func<IMemory> mem) { _mem = mem; }

    public bool Found => _record != 0;

    /// <summary>The game's current controller state: buttons as XInput gives them, sticks raw.</summary>
    public bool TryRead(long now, out ushort buttons, out short lx, out short ly, out short rx, out short ry)
    {
        buttons = 0; lx = ly = rx = ry = 0;
        IMemory m = null;
        try { m = _mem(); } catch { }
        if (m == null) return false;
        try
        {
            int pid = m.Process?.Id ?? 0;
            if (pid != _pid) { _pid = pid; _record = 0; _nextSearchMs = 0; }
            if (_record != 0)
            {
                byte[] r = m.ReadBytes(_record, RecordSize);
                if (!IsRecord(r)) _record = 0;
                else
                {
                    buttons = BitConverter.ToUInt16(r, StateAt);
                    lx = BitConverter.ToInt16(r, StateAt + 4); ly = BitConverter.ToInt16(r, StateAt + 6);
                    rx = BitConverter.ToInt16(r, StateAt + 8); ry = BitConverter.ToInt16(r, StateAt + 10);
                    return true;
                }
            }
            if (now < _nextSearchMs) return false;
            _nextSearchMs = now + 5000;
            _record = Search(m);
            if (_record != 0 && !_logged)
            {
                _logged = true;
                byte[] r = m.ReadBytes(_record, RecordSize);
                PluginLog.Write($"Controller: reading the game's own controller state ({BitConverter.ToUInt16(r, IdsAt):X4}:{BitConverter.ToUInt16(r, IdsAt + 2):X4})");
            }
        }
        catch { _record = 0; }
        return false;
    }

    private static bool IsRecord(byte[] r)
    {
        if (r == null || r.Length < RecordSize) return false;
        ushort vid = BitConverter.ToUInt16(r, IdsAt), pid = BitConverter.ToUInt16(r, IdsAt + 2);
        return BitConverter.ToInt64(r, 0x10) == -1 && vid != 0 && vid != 0xFFFF && pid != 0 && pid != 0xFFFF;
    }

    private static long Search(IMemory m)
    {
        var proc = Process.GetProcessById(m.Process.Id);
        var xi = proc.Modules.Cast<ProcessModule>().FirstOrDefault(p => p.ModuleName.Equals("xinput1_4.dll", StringComparison.OrdinalIgnoreCase));
        if (xi == null) return 0;
        long b = (long)xi.BaseAddress, end = b + xi.ModuleMemorySize;

        long Table(long table)
        {
            long t = Ptr(m, table);
            if (t == 0 || t >= b && t < end) return 0;
            byte[] slots = m.ReadBytes(t, 0x40);
            for (int k = 0; k + 8 <= slots.Length; k += 8)
            {
                long rec = BitConverter.ToInt64(slots, k);
                if (rec > 0x10000 && rec < 0x7FFFFFFFFFFF && IsRecord(m.ReadBytes(rec, RecordSize))) return rec;
            }
            return 0;
        }

        if (KnownTable < xi.ModuleMemorySize && Table(b + KnownTable) is long known && known != 0) return known;

        // Elsewhere in .data on other Windows builds.
        byte[] hdr = m.ReadBytes(b, 0x400);
        int pe = BitConverter.ToInt32(hdr, 0x3C);
        int sections = BitConverter.ToUInt16(hdr, pe + 6), opt = BitConverter.ToUInt16(hdr, pe + 20);
        for (int i = 0; i < sections; i++)
        {
            int s = pe + 24 + opt + i * 40;
            if (s + 40 > hdr.Length) break;
            if (System.Text.Encoding.ASCII.GetString(hdr, s, 8).TrimEnd('\0') != ".data") continue;
            long rva = BitConverter.ToUInt32(hdr, s + 12), size = BitConverter.ToUInt32(hdr, s + 8);
            for (long a = b + rva; a + 8 <= b + rva + size; a += 8)
                if (Table(a) is long rec && rec != 0) return rec;
        }
        return 0;
    }

    private static long Ptr(IMemory m, long a)
    {
        long v = BitConverter.ToInt64(m.ReadBytes(a, 8), 0);
        return v > 0x10000 && v < 0x7FFFFFFFFFFF ? v : 0;
    }
}
