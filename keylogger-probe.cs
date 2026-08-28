// Diagnostic only. Answers three questions in one run:
//   1. Do the brightness keys reach a low-level keyboard hook (so we can block them)?
//   2. Do they show up as raw HID consumer-control reports instead?
//   3. Can we dim below hardware zero with a gamma ramp, or will Windows clamp it?
// Every brightness change is logged too, so a change with no preceding key event
// proves the keys bypass user-mode entirely.
using System;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

class Probe
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_INPUT = 0x00FF;
    const int RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public int Flags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string lpszMenuName; public string lpszClassName;
    }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    static HookProc _hook;
    static WndProc _wndProc;
    static StreamWriter _log;
    static readonly object _lock = new object();

    static void Log(string s)
    {
        lock (_lock)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
            Console.WriteLine(line);
            _log.WriteLine(line);
            _log.Flush();
        }
    }

    static IntPtr HookCb(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            int msg = wParam.ToInt32();
            string kind = (msg == 0x0100 || msg == 0x0104) ? "DOWN" : "UP  ";
            Log(string.Format("HOOK  {0}  vk=0x{1:X2} ({1,3})  sc=0x{2:X2}  flags=0x{3:X2}", kind, k.vkCode, k.scanCode, k.flags));
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static IntPtr WndCb(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            uint hdrSize = (uint)(sizeof(uint) * 2 + IntPtr.Size * 2);
            uint size = 0;
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdrSize);
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdrSize) == size)
                {
                    int type = Marshal.ReadInt32(buf, 0);
                    var sb = new StringBuilder();
                    for (int i = 0; i < (int)size && i < 64; i++) sb.AppendFormat("{0:X2} ", Marshal.ReadByte(buf, i));
                    Log(string.Format("RAW   type={0} size={1}  bytes: {2}", type, size, sb.ToString().Trim()));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static void GammaTest()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            var orig = new ushort[768];
            if (!GetDeviceGammaRamp(hdc, orig)) { Log("GAMMA unsupported (GetDeviceGammaRamp failed)"); return; }

            var dim = new ushort[768];
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 256; i++)
                    dim[c * 256 + i] = (ushort)Math.Min(65535.0, orig[c * 256 + i] * 0.40);

            bool set = SetDeviceGammaRamp(hdc, dim);
            var back = new ushort[768];
            GetDeviceGammaRamp(hdc, back);
            SetDeviceGammaRamp(hdc, orig);   // restore immediately

            // If Windows clamped us, the read-back mid-tone won't match what we asked for.
            int want = dim[128], got = back[128], was = orig[128];
            Log(string.Format("GAMMA set={0}  orig[128]={1} requested={2} readback={3}  => {4}",
                set, was, want, got,
                (set && Math.Abs(got - want) < 2000) ? "USABLE for sub-zero dimming" : "CLAMPED by Windows"));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    static void Main()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe-log.txt");
        _log = new StreamWriter(path, false);
        Log("=== probe v2 started ===");

        GammaTest();

        _wndProc = WndCb;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "BrightProbeWnd2"
        };
        ushort atom = RegisterClassW(ref wc);
        Log("RegisterClassW atom=" + atom + (atom == 0 ? " err=" + Marshal.GetLastWin32Error() : ""));
        IntPtr hwnd = CreateWindowExW(0, "BrightProbeWnd2", "probe", 0, 0, 0, 0, 0, (IntPtr)(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        Log("message window: 0x" + hwnd.ToInt64().ToString("X") + (hwnd == IntPtr.Zero ? " err=" + Marshal.GetLastWin32Error() : ""));

        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 0x0C, Usage = 0x01, Flags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        };
        bool rr = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        Log("RegisterRawInputDevices: " + rr + (rr ? "" : " err=" + Marshal.GetLastWin32Error()));

        try
        {
            var w = new ManagementEventWatcher(new ManagementScope("root\\wmi"),
                        new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            w.EventArrived += (s, e) =>
                Log(">>>> BRIGHTNESS CHANGED to " + e.NewEvent.Properties["Brightness"].Value + " <<<<");
            w.Start();
            Log("WMI brightness watcher: started");
        }
        catch (Exception ex) { Log("WMI brightness watcher FAILED: " + ex.Message); }

        _hook = HookCb;
        IntPtr hh = SetWindowsHookEx(WH_KEYBOARD_LL, _hook, GetModuleHandle(null), 0);
        Log("SetWindowsHookEx: 0x" + hh.ToInt64().ToString("X") + (hh == IntPtr.Zero ? " FAILED err=" + Marshal.GetLastWin32Error() : ""));
        Log("=== ready; press ONLY the brightness keys now ===");

        MSG m;
        while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
    }
}
