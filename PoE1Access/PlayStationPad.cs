using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PoE1Access;

/// <summary>
/// Reads a PlayStation controller (DualSense, DualSense Edge, DualShock 4; USB or Bluetooth)
/// straight from its HID input reports. These are not XInput controllers: Steam Input turns
/// them into one only for the game it runs, never for Exile API, so without this the mod's
/// gestures (L3, L3 + right stick) did nothing with them. Read-only and shared: the game and
/// Steam keep reading the controller as before.
///
/// Report layouts (byte offsets from the report id):
///   DualSense USB 0x01 (64 bytes): sticks LX LY RX RY at 1..4, buttons at 8 and 9.
///   DualSense Bluetooth 0x31: sticks at 2..5, buttons at 9 and 10.
///   DualSense/DualShock 4 basic 0x01 (Bluetooth before the full mode, and DualShock 4 USB):
///     sticks at 1..4, buttons at 5 and 6.
///   DualShock 4 Bluetooth 0x11: sticks at 3..6, buttons at 7 and 8.
/// In the second button byte L3 is 0x40 and R3 0x80. Sticks are 0..255, 128 centred, Y down.
/// </summary>
internal sealed class PlayStationPad : IDisposable
{
    private const ushort Sony = 0x054C;
    private static readonly HashSet<ushort> DualSenses = new HashSet<ushort> { 0x0CE6, 0x0DF2 };
    private static readonly HashSet<ushort> DualShocks = new HashSet<ushort> { 0x05C4, 0x09CC, 0x0BA0 };

    private SafeFileHandle _handle;
    private Thread _reader;
    private volatile bool _stop;
    private bool _dualSense;
    private int _reportLength;

    // The latest state, written by the reader thread.
    private volatile int _buttons1, _buttons2;
    private volatile int _lx = 128, _ly = 128, _rx = 128, _ry = 128;
    private long _lastReportMs;

    public string Name { get; private set; } = "";
    public bool Connected => _handle != null && !_handle.IsInvalid && !_handle.IsClosed &&
                             Environment.TickCount64 - Interlocked.Read(ref _lastReportMs) < 3000;

    public bool L3 => (_buttons2 & 0x40) != 0;
    /// <summary>Circle (B on an Xbox pad): the high bits of the hat byte are square 0x10, cross 0x20, circle 0x40, triangle 0x80.</summary>
    public bool Circle => (_buttons1 & 0x40) != 0;
    public bool FaceButton => (_buttons1 & 0xD0) != 0;   // circle, square, triangle (cross is A)
    private volatile int _hat = 8;
    /// <summary>The D-pad as XInput's bits: up 1, down 2, left 4, right 8 (diagonals set two).</summary>
    public int DPad => _hat switch
    {
        0 => 1, 1 => 1 | 8, 2 => 8, 3 => 2 | 8, 4 => 2, 5 => 2 | 4, 6 => 4, 7 => 1 | 4, _ => 0,
    };
    public Vector2 LeftStick => Stick(_lx, _ly);
    public Vector2 RightStick => Stick(_rx, _ry);

    /// <summary>Opens the first PlayStation controller found; false if there is none.</summary>
    public bool Open()
    {
        Dispose();
        foreach (string path in HidPaths())
        {
            // First without any access, only to ask what device it is (no other device is opened
            // for reading); then a PlayStation controller for reading only, shared, never for
            // writing: the game and Steam keep it as before (testers' DualSense, 23 Nov 2026).
            var attr = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            using (var probe = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (probe.IsInvalid || !HidD_GetAttributes(probe, ref attr) || attr.VendorID != Sony ||
                    !(DualSenses.Contains(attr.ProductID) || DualShocks.Contains(attr.ProductID)))
                    continue;
            }
            var h = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            int length = 64;
            if (HidD_GetPreparsedData(h, out IntPtr pre))
            {
                if (HidP_GetCaps(pre, out HidpCaps caps) == HidpStatusSuccess && caps.InputReportByteLength > 0) length = caps.InputReportByteLength;
                HidD_FreePreparsedData(pre);
            }
            _handle = h;
            _dualSense = DualSenses.Contains(attr.ProductID);
            _reportLength = length;
            Name = (_dualSense ? "DualSense" : "DualShock 4") + " (" + attr.ProductID.ToString("X4") + ", reports of " + length + " bytes)";
            Interlocked.Exchange(ref _lastReportMs, Environment.TickCount64);
            _stop = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PoE1Access PlayStation pad" };
            _reader.Start();
            return true;
        }
        return false;
    }

    private void ReadLoop()
    {
        var buf = new byte[Math.Max(_reportLength, 78)];
        try
        {
            using var stream = new FileStream(_handle, FileAccess.Read, 0, false);
            while (!_stop)
            {
                int n = stream.Read(buf, 0, _reportLength);
                if (n <= 0) break;
                if (Parse(buf, n)) Interlocked.Exchange(ref _lastReportMs, Environment.TickCount64);
            }
        }
        catch { }   // unplugged or closed: Connected turns false and the pad is looked for again
    }

    private bool Parse(byte[] b, int n)
    {
        int stick, buttons;
        if (b[0] == 0x31 && n >= 11) { stick = 2; buttons = 9; }                 // DualSense, Bluetooth
        else if (b[0] == 0x11 && n >= 9) { stick = 3; buttons = 7; }             // DualShock 4, Bluetooth
        else if (b[0] == 0x01 && _dualSense && n >= 64) { stick = 1; buttons = 8; }  // DualSense, USB
        else if (b[0] == 0x01 && n >= 7) { stick = 1; buttons = 5; }             // basic report, DualShock 4 USB
        else return false;
        _lx = b[stick]; _ly = b[stick + 1]; _rx = b[stick + 2]; _ry = b[stick + 3];
        _buttons2 = b[buttons + 1];
        _buttons1 = b[buttons];
        _hat = b[buttons] & 0x0F;   // 0 up, then clockwise in eighths to 7 up left; 8 none
        return true;
    }

    // XInput's direction: x right, y up, -1..1, with the same dead zone as ControllerInput.
    private static Vector2 Stick(int x, int y)
    {
        var v = new Vector2((x - 128) / 127f, (128 - y) / 127f);
        v = Vector2.Clamp(v, new Vector2(-1, -1), new Vector2(1, 1));
        return v.Length() < 0.2f ? Vector2.Zero : v;
    }

    public void Dispose()
    {
        _stop = true;
        try { _handle?.Dispose(); } catch { }
        _handle = null;
        _reader = null;
    }

    // ---- Windows HID device list ----

    private static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out Guid hid);
        IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;
        try
        {
            var data = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                if (size <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4);
                    if (!string.IsNullOrEmpty(path)) yield return path;
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint GenericRead = 0x80000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;
    private const int DigcfPresent = 0x2, DigcfDeviceInterface = 0x10, HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps,
            NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int CbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HiddAttributes attributes);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidpCaps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, int index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
