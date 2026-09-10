// FnCtrl - use the Apple Magic Keyboard's Fn and Eject keys as ordinary Windows keys.
//
// Neither key reaches Windows as a keyboard scancode, which is why keyboard hooks
// (PowerToys, AutoHotkey) cannot see Fn at all. They arrive instead as HID reports
// on the keyboard's consumer-control collection (Bluetooth) or on Apple's vendor
// top-case page (USB). Raw Input reads those from user mode, so there is no kernel
// driver and Secure Boot is not involved.
//
//   FnCtrl.exe --probe     discover your keyboard's report ids and bits
//   FnCtrl.exe             run in the tray with the default mappings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class FnCtrl {
    const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE;
    const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_DEVNOTIFY = 0x00002000;
    const uint RID_INPUT = 0x10000003, RIDI_DEVICEINFO = 0x2000000b, RIDI_DEVICENAME = 0x20000007;
    const uint RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;
    const int GIDC_REMOVAL = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1, pad2; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint cb);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint hdr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW")]
    static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, IntPtr data, ref uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList(IntPtr p, ref uint n, uint cb);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int cb);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    // One physical key we watch for: a bit in a HID report, and the key to send.
    class Mapping {
        public byte ReportId, Byte, Mask;
        public ushort Vk, Scan;
        public bool Held;
        public string Name;
        public override string ToString() {
            return string.Format("{0}: report 0x{1:X2} byte {2} bit 0x{3:X2} -> vk 0x{4:X2}", Name, ReportId, Byte, Mask, Vk);
        }
    }

    static readonly List<Mapping> maps = new List<Mapping>();
    static bool probe = false, anyVendor = false;
    static string logPath = null;
    static readonly Dictionary<IntPtr, string> known = new Dictionary<IntPtr, string>();
    static NotifyIcon tray;

    // Must stay reachable for the lifetime of the process: NativeWindow's
    // finalizer destroys the window handle, and the Raw Input registration
    // lives on that handle. As a local it would be collectable the moment the
    // message loop started, silently ending input delivery.
    static Sink sink;

    class Sink : NativeWindow {
        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_INPUT) OnInput(m.LParam);
            else if (m.Msg == WM_INPUT_DEVICE_CHANGE) {
                known.Clear();
                if (m.WParam.ToInt32() == GIDC_REMOVAL) ReleaseAll();
            }
            base.WndProc(ref m);
        }
    }

    // Describes Apple HID devices; returns null for devices we ignore.
    static string Describe(IntPtr hDev) {
        string cached;
        if (known.TryGetValue(hDev, out cached)) return cached;
        string result = null;
        uint isz = 0;
        GetRawInputDeviceInfo(hDev, RIDI_DEVICEINFO, IntPtr.Zero, ref isz);
        if (isz >= 32) {
            IntPtr ib = Marshal.AllocHGlobal((int)isz + 8);
            try {
                Marshal.WriteInt32(ib, 0, (int)isz);
                if (GetRawInputDeviceInfo(hDev, RIDI_DEVICEINFO, ib, ref isz) != unchecked((uint)-1)) {
                    byte[] b = new byte[32]; Marshal.Copy(ib, b, 0, 32);
                    if (BitConverter.ToUInt32(b, 4) == RIM_TYPEHID) {
                        uint vid = BitConverter.ToUInt32(b, 8), pid = BitConverter.ToUInt32(b, 12);
                        ushort up = BitConverter.ToUInt16(b, 20), us = BitConverter.ToUInt16(b, 22);
                        if (vid == 0x05AC || anyVendor)
                            result = string.Format("VID_{0:X4} PID_{1:X4} UsagePage 0x{2:X4} Usage 0x{3:X2}", vid, pid, up, us);
                    }
                }
            } finally { Marshal.FreeHGlobal(ib); }
        }
        known[hDev] = result;
        return result;
    }

    static void OnInput(IntPtr hRawInput) {
        uint hdr = (uint)(IntPtr.Size == 8 ? 24 : 16);
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size == 0 || size > 4096) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, hdr) != size) return;
            byte[] b = new byte[size]; Marshal.Copy(buf, b, 0, (int)size);
            uint type = BitConverter.ToUInt32(b, 0);
            IntPtr hDev = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(b, 8) : (IntPtr)BitConverter.ToInt32(b, 8);

            if (type == RIM_TYPEKEYBOARD) {
                if (probe) {
                    int k = (int)hdr;
                    Console.WriteLine("KEYBOARD  make=0x{0:X2} flags=0x{1:X2} vkey=0x{2:X2}",
                        BitConverter.ToUInt16(b, k), BitConverter.ToUInt16(b, k + 2), BitConverter.ToUInt16(b, k + 6));
                }
                return;
            }
            if (type != RIM_TYPEHID) return;

            string who = Describe(hDev);
            if (who == null) return;

            int off = (int)hdr;
            uint sizeHid = BitConverter.ToUInt32(b, off), count = BitConverter.ToUInt32(b, off + 4);
            if (sizeHid < 2) return;
            int d = off + 8;
            for (uint c = 0; c < count && d + sizeHid <= size; c++, d += (int)sizeHid) {
                if (probe) {
                    var sb = new StringBuilder();
                    for (int i = 0; i < sizeHid; i++) sb.AppendFormat("{0:X2} ", b[d + i]);
                    Console.WriteLine("{0}  |  report: {1}", who, sb.ToString().Trim());
                    continue;
                }
                Trace("report {0:X2} {1:X2} from {2}", b[d], b[d + 1], who);
                foreach (Mapping m in maps) {
                    if (b[d] != m.ReportId || m.Byte >= sizeHid) continue;
                    if ((b[d + m.Byte] & m.Mask) != 0) Press(m); else Release(m);
                }
            }
        } finally { Marshal.FreeHGlobal(buf); }
    }

    static void Send(Mapping m, uint flags) {
        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = m.Vk;
        inputs[0].ki.wScan = m.Scan;
        inputs[0].ki.dwFlags = flags;
        inputs[0].ki.dwExtraInfo = (IntPtr)0x464E4331; // "FNC1" - marks our synthetic events
        uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
        if (sent != 1) Trace("SendInput FAILED for {0}: error {1}", m.Name, Marshal.GetLastWin32Error());
    }
    static void Press(Mapping m)   { if (!m.Held) { m.Held = true;  Send(m, 0); Trace("{0} down -> vk 0x{1:X2}", m.Name, m.Vk); } }
    static void Release(Mapping m) { if (m.Held)  { m.Held = false; Send(m, KEYEVENTF_KEYUP); Trace("{0} up", m.Name); } }
    static void ReleaseAll()       { foreach (Mapping m in maps) Release(m); }

    static void ListDevices() {
        uint n = 0, cb = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
        GetRawInputDeviceList(IntPtr.Zero, ref n, cb);
        IntPtr list = Marshal.AllocHGlobal((int)(n * cb));
        try {
            GetRawInputDeviceList(list, ref n, cb);
            Console.WriteLine("=== Apple HID collections (all vendors with --all) ===");
            int found = 0;
            for (int i = 0; i < n; i++) {
                var rd = (RAWINPUTDEVICELIST)Marshal.PtrToStructure((IntPtr)(list.ToInt64() + i * cb), typeof(RAWINPUTDEVICELIST));
                string desc = Describe(rd.hDevice);
                if (desc == null) continue;
                found++;
                uint sz = 0;
                GetRawInputDeviceInfo(rd.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref sz);
                IntPtr nb = Marshal.AllocHGlobal((int)sz * 2 + 2);
                GetRawInputDeviceInfo(rd.hDevice, RIDI_DEVICENAME, nb, ref sz);
                Console.WriteLine("{0}\n    {1}", desc, Marshal.PtrToStringUni(nb));
                Marshal.FreeHGlobal(nb);
            }
            if (found == 0) Console.WriteLine("(none found - is the keyboard connected?)");
        } finally { Marshal.FreeHGlobal(list); }
    }

    static void OpenConsole() {
        if (logPath != null) {
            var lw = new StreamWriter(logPath, false); lw.AutoFlush = true;
            Console.SetOut(lw);
            return;
        }
        if (!AttachConsole(-1)) AllocConsole();
        var w = new StreamWriter(Console.OpenStandardOutput()); w.AutoFlush = true;
        Console.SetOut(w);
    }

    static bool IsElevated() {
        try {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(
                System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch { return false; }
    }

    // Diagnostics: active whenever --log is given, in probe mode or not.
    static void Trace(string fmt, params object[] a) {
        if (logPath == null) return;
        Console.WriteLine("{0:HH:mm:ss.fff}  {1}", DateTime.Now, string.Format(fmt, a));
    }

    static ushort Hex(string s) {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return Convert.ToUInt16(s, 16);
    }

    // --map NAME:REPORTID:BYTE:MASK:VK[:SCAN]   all values hex except NAME
    static Mapping ParseMap(string spec) {
        string[] p = spec.Split(':');
        if (p.Length < 5) throw new ArgumentException("--map needs NAME:REPORTID:BYTE:MASK:VK[:SCAN]");
        return new Mapping {
            Name = p[0],
            ReportId = (byte)Hex(p[1]),
            Byte = (byte)Hex(p[2]),
            Mask = (byte)Hex(p[3]),
            Vk = Hex(p[4]),
            Scan = p.Length > 5 ? Hex(p[5]) : (ushort)0,
        };
    }

    static void Usage() {
        OpenConsole();
        Console.WriteLine("FnCtrl [--probe] [--all] [--map NAME:REPORTID:BYTE:MASK:VK[:SCAN]] ...");
        Console.WriteLine();
        Console.WriteLine("  --probe   print HID reports so you can find your keys' report id and bit");
        Console.WriteLine("  --all     in probe mode, show non-Apple devices too");
        Console.WriteLine("  --map     add a mapping; repeatable. Values are hex, NAME is a label.");
        Console.WriteLine("            Giving any --map replaces the defaults below.");
        Console.WriteLine();
        Console.WriteLine("Defaults (Bluetooth Magic Keyboard, VID 05AC PID 0256):");
        foreach (Mapping m in DefaultMaps()) Console.WriteLine("  " + m);
        Console.WriteLine();
        Console.WriteLine("Common VK codes: A2 LCtrl, A0 LShift, A4 LAlt, 5B LWin, 2C PrintScreen,");
        Console.WriteLine("                 1B Esc, 2E Delete, 91 ScrollLock, 13 Pause");
    }

    static List<Mapping> DefaultMaps() {
        return new List<Mapping> {
            new Mapping { Name = "Fn",    ReportId = 0x11, Byte = 1, Mask = 0x10, Vk = 0xA2, Scan = 0x1D }, // Left Ctrl
            new Mapping { Name = "Eject", ReportId = 0x11, Byte = 1, Mask = 0x08, Vk = 0x2C, Scan = 0x00 }, // Print Screen
        };
    }

    [STAThread]
    static void Main(string[] args) {
        try {
            for (int i = 0; i < args.Length; i++) {
                string a = args[i].ToLowerInvariant();
                bool hasNext = i + 1 < args.Length;
                if (a == "--probe") probe = true;
                else if (a == "--all") anyVendor = true;
                else if (a == "--map" && hasNext) maps.Add(ParseMap(args[++i]));
                else if (a == "--log" && hasNext) logPath = args[++i];
                else if (a == "--help" || a == "-h" || a == "/?") { Usage(); return; }
            }
        } catch (Exception ex) {
            OpenConsole();
            Console.WriteLine("Bad arguments: " + ex.Message);
            return;
        }
        if (maps.Count == 0) maps.AddRange(DefaultMaps());

        bool created;
        using (var mutex = new Mutex(true, "FnCtrl_SingleInstance_9f21", out created)) {
            if (!created && !probe) return; // already running

            if (probe || logPath != null) OpenConsole();
            Trace("starting; elevated={0}; mappings: {1}", IsElevated(), string.Join(" | ", maps.ConvertAll(x => x.ToString()).ToArray()));

            sink = new Sink();
            sink.CreateHandle(new CreateParams());

            if (probe) ListDevices();

            // 0x0C/0x01 consumer control carries Fn over Bluetooth;
            // 0xFF01/0x03 is Apple's vendor top-case page, used over USB.
            var regs = new RAWINPUTDEVICE[] {
                new RAWINPUTDEVICE { usUsagePage = 0x000C, usUsage = 0x01, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = sink.Handle },
                new RAWINPUTDEVICE { usUsagePage = 0xFF01, usUsage = 0x03, dwFlags = RIDEV_INPUTSINK | RIDEV_DEVNOTIFY, hwndTarget = sink.Handle },
            };
            if (!RegisterRawInputDevices(regs, (uint)regs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) {
                string msg = "Could not register for raw input: error " + Marshal.GetLastWin32Error();
                if (probe) Console.WriteLine(msg); else MessageBox.Show(msg, "FnCtrl");
                return;
            }

            if (probe) {
                Console.WriteLine();
                Console.WriteLine("=== Press a key a few times. Look for a report that appears on press");
                Console.WriteLine("=== and clears on release. Close this window to stop.");
                Console.WriteLine();
            } else {
                tray = new NotifyIcon();
                tray.Icon = SystemIcons.Application;
                tray.Text = "FnCtrl";
                var menu = new ContextMenuStrip();
                foreach (Mapping m in maps) menu.Items.Add(m.ToString()).Enabled = false;
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, delegate { ReleaseAll(); tray.Visible = false; Application.Exit(); });
                tray.ContextMenuStrip = menu;
                tray.Visible = true;
            }

            Application.ApplicationExit += delegate { ReleaseAll(); if (tray != null) tray.Visible = false; };
            Application.Run(new ApplicationContext());
            GC.KeepAlive(sink);
            GC.KeepAlive(mutex);
        }
    }
}
