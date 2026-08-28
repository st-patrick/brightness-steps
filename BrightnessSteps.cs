// BrightnessSteps - finer brightness steps for the keyboard brightness keys.
//
// Windows moves brightness in jumps of 10, which makes the bottom of the range
// unusable: you can have 0 or 10, never 5. This walks a hand-tuned ladder
// instead, with 1-point steps at the bottom, plus rungs *below* hardware zero
// down to a fully black screen (Windows clamps gamma ramps, so those are done
// with a black overlay).
//
// How it hooks in: on this hardware the brightness keys never reach a low-level
// keyboard hook - they are handled below user mode - but they DO arrive as raw
// HID consumer-control reports (usage 0x6F up / 0x70 down) about 20ms before
// Windows applies its own step. So the key cannot be swallowed, but every press
// is known immediately, along with its direction.
//
// Keeping the flicker down: we apply our rung the moment the HID report lands,
// then hold it. Windows still stomps on it ~20ms later, so a guard loop polls
// the backlight every millisecond for a short window afterwards and puts it
// straight back. Both the read and the write go through the display driver
// directly (IOCTL_VIDEO_*_DISPLAY_BRIGHTNESS, ~0.16ms) rather than WMI (~10ms),
// which is what makes a ~2ms correction possible at all.
using System;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool fresh;
        using (var mutex = new Mutex(true, "BrightnessStepsSingleton", out fresh))
        {
            if (!fresh) return;                 // already running
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }
}

/// <summary>One rung of the ladder: a hardware brightness plus overlay darkening.</summary>
struct Step
{
    public readonly int Hw;      // 0-100, sent to the panel
    public readonly int Alpha;   // 0-255 black overlay on top, for below-zero
    public Step(int hw, int alpha) { Hw = hw; Alpha = alpha; }
}

class TrayApp : ApplicationContext
{
    // Darkest first. Below hardware 0 we stack overlay rungs down to fully
    // black; above it the rungs are close together at the bottom and spread out
    // toward full brightness, so every press is about the same relative change.
    static readonly Step[] Ladder =
    {
        new Step(0, 255), new Step(0, 232), new Step(0, 205),
        new Step(0, 170), new Step(0, 120), new Step(0, 60),
        new Step(0, 0),
        new Step(1, 0),  new Step(2, 0),  new Step(3, 0),  new Step(4, 0),
        new Step(5, 0),  new Step(6, 0),  new Step(8, 0),  new Step(10, 0),
        new Step(13, 0), new Step(16, 0), new Step(20, 0), new Step(25, 0),
        new Step(30, 0), new Step(37, 0), new Step(45, 0), new Step(55, 0),
        new Step(67, 0), new Step(82, 0), new Step(100, 0),
    };

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "BrightnessSteps";

    // How long to keep putting the backlight back after a key press. Windows
    // lands its own step ~20ms in; the rest is slack for a slow moment.
    const int GuardMs = 220;

    readonly NotifyIcon _tray;
    readonly DimOverlay _overlay = new DimOverlay();
    readonly Osd _osd = new Osd();
    readonly Control _sync = new Control();      // marshals WMI callbacks onto the UI thread
    readonly Backlight _backlight = new Backlight();
    readonly Guard _guard;
    RawInputListener _raw;

    // Key presses arrive on the raw-input thread; the menu and the slider
    // watcher run on the UI thread. Everything shared between them is either
    // under _ladderLock or interlocked.
    readonly object _ladderLock = new object();
    int _index;
    volatile int _lastSelfSet = -1;
    long _lastSelfSetAtTicks;
    long _guardEndsAtTicks;
    volatile bool _showOsd = true;

    public TrayApp()
    {
        _sync.CreateControl();
        var _ = _sync.Handle;

        _guard = new Guard();
        _index = IndexForHardware(_backlight.Get());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "BrightnessSteps",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _raw = new RawInputListener();
        _raw.BrightnessKey += OnBrightnessKey;

        StartBrightnessWatcher();
    }

    // ---------- menu ----------

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsAutoStart(), CheckOnClick = true };
        startup.Click += (s, e) => SetAutoStart(startup.Checked);
        m.Items.Add(startup);

        var osd = new ToolStripMenuItem("Show level popup") { Checked = _showOsd, CheckOnClick = true };
        osd.Click += (s, e) => _showOsd = osd.Checked;
        m.Items.Add(osd);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Darker", null, (s, e) => Move(-1));
        m.Items.Add("Brighter", null, (s, e) => Move(+1));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Exit", null, (s, e) => Shutdown());
        return m;
    }

    static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunValue) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k == null) return;
            if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(RunValue, false);
        }
    }

    void Shutdown()
    {
        if (_raw != null) { _raw.Dispose(); _raw = null; }
        _guard.Dispose();
        _overlay.Apply(0);
        _backlight.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    // ---------- key handling ----------

    void OnBrightnessKey(int direction)
    {
        Move(direction);        // on the raw-input thread; act at once, don't wait for Windows
    }

    void Move(int direction)
    {
        int i;
        Step s;
        lock (_ladderLock)
        {
            i = Math.Max(0, Math.Min(Ladder.Length - 1, _index + direction));
            _index = i;
            s = Ladder[i];
        }
        ApplyStep(i, s);
    }

    void ApplyStep(int i, Step s)
    {
        // Arm the guard FIRST. Windows' stomp is coming in ~20ms and the guard
        // only needs the target number, so nothing that can block - showing the
        // overlay window, DWM, painting the popup - should sit in front of it.
        // Arming late is what let the occasional stomp through.
        Interlocked.Exchange(ref _guardEndsAtTicks, DateTime.UtcNow.AddMilliseconds(GuardMs).Ticks);
        _guard.Hold(s.Hw, GuardMs);
        SetHardware(s.Hw);

        // Everything below is presentation and must not sit in the key path, so
        // it is handed to the UI thread. Ordering between hardware and overlay
        // does not matter here because every overlay rung sits at hardware 0,
        // and so does its neighbour - the two never change in the same step.
        Action present = () =>
        {
            _overlay.Apply(s.Alpha);
            if (_showOsd) _osd.Show(i, Ladder.Length, DescribeStep(s));
        };

        if (_sync.InvokeRequired)
        {
            try { _sync.BeginInvoke(present); } catch { }
        }
        else present();
    }

    static string DescribeStep(Step s)
    {
        if (s.Alpha >= 255) return "black";
        return s.Alpha > 0
            ? "dim " + (int)Math.Round(s.Alpha / 255.0 * 100) + "%"
            : s.Hw.ToString(CultureInfo.InvariantCulture);
    }

    static int IndexForHardware(int hw)
    {
        int best = 0, bestDist = int.MaxValue;
        for (int i = 0; i < Ladder.Length; i++)
        {
            if (Ladder[i].Alpha != 0) continue;      // overlay rungs aren't reachable from hardware alone
            int d = Math.Abs(Ladder[i].Hw - hw);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    void SetHardware(int level)
    {
        _lastSelfSet = level;
        Interlocked.Exchange(ref _lastSelfSetAtTicks, DateTime.UtcNow.Ticks);
        _backlight.Set(level);
    }

    // ---------- following the slider ----------

    void StartBrightnessWatcher()
    {
        try
        {
            var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                        new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            w.EventArrived += (s, e) =>
            {
                int hw = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                try { _sync.BeginInvoke((Action)(() => OnExternalBrightness(hw))); } catch { }
            };
            w.Start();
        }
        catch { }
    }

    void OnExternalBrightness(int hw)
    {
        long now = DateTime.UtcNow.Ticks;

        // While the guard is running we own the backlight; the events arriving
        // are Windows' stomp and our corrections racing each other.
        if (now < Interlocked.Read(ref _guardEndsAtTicks) + 250 * TimeSpan.TicksPerMillisecond) return;

        // Our own write echoing back.
        if (hw == _lastSelfSet && now - Interlocked.Read(ref _lastSelfSetAtTicks) < 2500 * TimeSpan.TicksPerMillisecond) return;

        // Nobody pressed a key, so this is the slider (or battery saver, or
        // adaptive brightness) moving things. Follow it rather than fight it.
        lock (_ladderLock) { _index = IndexForHardware(hw); }
        if (_overlay.Alpha != 0) _overlay.Apply(0);
    }
}

/// <summary>
/// Reads and writes panel brightness straight through the display driver, which
/// is ~60x cheaper than the WMI route (0.16ms vs 10ms measured) and is what
/// makes the post-keypress guard loop viable. Falls back to WMI if the device
/// cannot be opened.
/// </summary>
class Backlight : IDisposable
{
    static Guid GUID_DEVINTERFACE_MONITOR = new Guid("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    // CTL_CODE(FILE_DEVICE_VIDEO = 0x23, func, METHOD_BUFFERED, FILE_ANY_ACCESS)
    const uint IOCTL_QUERY_BRIGHTNESS = (0x23 << 16) | (0x126 << 2);
    const uint IOCTL_SET_BRIGHTNESS = (0x23 << 16) | (0x127 << 2);

    const byte DISPLAYPOLICY_AC = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct DISPLAY_BRIGHTNESS { public byte ucDisplayPolicy, ucACBrightness, ucDCBrightness; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfoData);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    readonly object _lock = new object();
    IntPtr _device = (IntPtr)(-1);
    byte _policy = DISPLAYPOLICY_AC;
    ManagementObject _wmiMethods;

    // Preallocated: the guard polls these thousands of times per key press, and
    // an AllocHGlobal per poll would be pure churn.
    readonly IntPtr _queryBuf = Marshal.AllocHGlobal(3);
    readonly IntPtr _setBuf = Marshal.AllocHGlobal(3);

    public Backlight() { Open(); }

    public bool UsingFastPath { get { return _device != (IntPtr)(-1); } }

    void Open()
    {
        IntPtr set = SetupDiGetClassDevsW(ref GUID_DEVINTERFACE_MONITOR, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == (IntPtr)(-1)) return;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));

            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GUID_DEVINTERFACE_MONITOR, i, ref did); i++)
            {
                uint need;
                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, detail, need, out need, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni((IntPtr)(detail.ToInt64() + 4));

                    IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                           IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == (IntPtr)(-1)) continue;

                    int probe;
                    _device = h;
                    if (TryQuery(out probe)) return;      // usable panel, keep it
                    _device = (IntPtr)(-1);
                    CloseHandle(h);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    bool TryQuery(out int level)
    {
        level = 0;
        if (_device == (IntPtr)(-1)) return false;
        uint ret;
        if (!DeviceIoControl(_device, IOCTL_QUERY_BRIGHTNESS, IntPtr.Zero, 0, _queryBuf, 3, out ret, IntPtr.Zero)) return false;
        _policy = Marshal.ReadByte(_queryBuf, 0);       // keep writing to whichever policy is live
        level = _policy == DISPLAYPOLICY_AC ? Marshal.ReadByte(_queryBuf, 1) : Marshal.ReadByte(_queryBuf, 2);
        return true;
    }

    public int Get()
    {
        lock (_lock)
        {
            int level;
            if (TryQuery(out level)) return level;
        }
        return WmiGet();
    }

    public void Set(int level)
    {
        if (level < 0) level = 0; else if (level > 100) level = 100;
        lock (_lock)
        {
            if (_device != (IntPtr)(-1))
            {
                Marshal.WriteByte(_setBuf, 0, _policy);
                Marshal.WriteByte(_setBuf, 1, (byte)level);
                Marshal.WriteByte(_setBuf, 2, (byte)level);
                uint ret;
                if (DeviceIoControl(_device, IOCTL_SET_BRIGHTNESS, _setBuf, 3, IntPtr.Zero, 0, out ret, IntPtr.Zero)) return;
            }
        }
        WmiSet(level);
    }

    // ---- WMI fallback, for machines where the device cannot be opened ----

    int WmiGet()
    {
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            foreach (ManagementObject o in new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightness")).Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return 50;
    }

    void WmiSet(int level)
    {
        try
        {
            if (_wmiMethods == null)
            {
                var scope = new ManagementScope(@"root\WMI");
                foreach (ManagementObject o in new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightnessMethods")).Get())
                { _wmiMethods = o; break; }
            }
            if (_wmiMethods == null) return;
            _wmiMethods.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)level });
        }
        catch { _wmiMethods = null; }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_device != (IntPtr)(-1)) { CloseHandle(_device); _device = (IntPtr)(-1); }
            Marshal.FreeHGlobal(_queryBuf);
            Marshal.FreeHGlobal(_setBuf);
        }
    }
}

/// <summary>
/// Holds the backlight at a value for a short window after a key press, undoing
/// Windows' own +/-10 step within about a millisecond of it landing.
/// </summary>
class Guard : IDisposable
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);

    // Windows lands its stomp ~20ms after the key. Sleep(1) polling leaves the
    // wrong value up for ~1.8ms, which is invisible mid-range but not at the
    // bottom, where a 10-point step is a ~10x change in light. So poll without
    // sleeping across the window the stomp actually arrives in, then fall back
    // to cheap sleep-polling for the long tail.
    const int SpinMs = 70;

    // Purely a stuck-key backstop. This used to be 3s, which a normal hold from
    // full brightness down to black comfortably exceeds: the loop never exits
    // while repeats keep extending it, so the guard silently dropped to ~2ms
    // latency mid-hold and the flicker came back exactly where it hurts most.
    // Spinning costs one logical core (~12% of this machine) and only while a
    // key is actually down, so the ceiling belongs well outside real use.
    const int MaxContinuousSpinMs = 15000;

    // The guard gets its OWN device handle rather than sharing the app's. While
    // spinning it takes that handle's lock every fraction of a millisecond, and
    // sharing it would stall the UI thread's own writes behind the spin.
    readonly Backlight _backlight = new Backlight();
    readonly Thread _thread;
    readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
    volatile int _target;
    long _untilTicks;
    long _spinUntilTicks;
    volatile bool _stop;

    public Guard()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "brightness-guard",
            // It sleeps blocked between presses and spins for at most 70ms after
            // one, but it has to be scheduled *promptly* when woken - being put
            // behind other work is exactly what makes the stomp slip through.
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    public void Hold(int target, int ms)
    {
        _target = target;
        var now = DateTime.UtcNow;
        Interlocked.Exchange(ref _untilTicks, now.AddMilliseconds(ms).Ticks);
        Interlocked.Exchange(ref _spinUntilTicks, now.AddMilliseconds(SpinMs).Ticks);
        _wake.Set();
    }

    void Run()
    {
        while (!_stop)
        {
            _wake.Wait();
            if (_stop) return;

            long startedAt = DateTime.UtcNow.Ticks;
            timeBeginPeriod(1);                 // Sleep(1) is ~15ms otherwise
            try
            {
                while (!_stop)
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (now >= Interlocked.Read(ref _untilTicks)) break;

                    int cur = _backlight.Get();
                    if (cur != _target) _backlight.Set(_target);

                    bool spinBudgetLeft = (now - startedAt) < MaxContinuousSpinMs * TimeSpan.TicksPerMillisecond;
                    if (now < Interlocked.Read(ref _spinUntilTicks) && spinBudgetLeft) Thread.SpinWait(30);
                    else Thread.Sleep(1);
                }
            }
            finally { timeEndPeriod(1); }

            _wake.Reset();
            // A Hold that landed between loop exit and Reset would otherwise be lost.
            if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _untilTicks)) _wake.Set();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(500);
        _backlight.Dispose();
    }
}

/// <summary>
/// Listens for raw HID consumer-control reports and reports brightness key
/// presses. Raw input can observe these keys but cannot block them, which is
/// fine - we only need to know direction and timing.
///
/// It runs on its own thread with its own message loop. On the UI thread,
/// WM_INPUT would queue behind the overlay's animation ticks and the popup's
/// painting - which is precisely what is happening when keys are held or
/// pressed quickly in the dim region, so the guard got armed late exactly when
/// presses came fastest.
/// </summary>
class RawInputListener : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const int WM_QUIT = 0x0012;
    const int RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const ushort USAGE_PAGE_CONSUMER = 0x0C;
    const ushort USAGE_CONSUMER_CONTROL = 0x01;
    const int USAGE_BRIGHTNESS_UP = 0x6F;
    const int USAGE_BRIGHTNESS_DOWN = 0x70;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public int Flags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>Raised on the listener's own thread, not the UI thread.</summary>
    public event Action<int> BrightnessKey;

    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
    Sink _sink;
    uint _threadId;

    public RawInputListener()
    {
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "brightness-input",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    void Pump()
    {
        _threadId = GetCurrentThreadId();
        _sink = new Sink(this);
        _ready.Set();

        MSG m;
        while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
    }

    void Raise(int direction)
    {
        var h = BrightnessKey;
        if (h != null) h(direction);
    }

    /// <summary>The message-only window, created on and pumped by the listener thread.</summary>
    class Sink : NativeWindow
    {
        readonly RawInputListener _owner;

        public Sink(RawInputListener owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });   // HWND_MESSAGE

            var devs = new[]
            {
                new RAWINPUTDEVICE
                {
                    UsagePage = USAGE_PAGE_CONSUMER,
                    Usage = USAGE_CONSUMER_CONTROL,
                    Flags = RIDEV_INPUTSINK,        // deliver even when we have no focus
                    hwndTarget = Handle,
                },
            };
            RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) Handle_WM_INPUT(m.LParam);
            base.WndProc(ref m);
        }

        void Handle_WM_INPUT(IntPtr hRawInput)
        {
        uint hdrSize = (uint)(sizeof(uint) * 2 + IntPtr.Size * 2);   // RAWINPUTHEADER
        uint size = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, hdrSize) != 0 || size == 0) return;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, hdrSize) != size) return;
            if (Marshal.ReadInt32(buf, 0) != 2) return;              // RIM_TYPEHID

            int sizeHid = Marshal.ReadInt32(buf, (int)hdrSize);
            int count = Marshal.ReadInt32(buf, (int)hdrSize + 4);
            int data = (int)hdrSize + 8;
            if (sizeHid < 3) return;

            for (int r = 0; r < count; r++)
            {
                int off = data + r * sizeHid;
                if (off + 3 > size) break;
                // byte 0 is the report id; the pressed usage follows as 16-bit LE.
                int usage = Marshal.ReadByte(buf, off + 1) | (Marshal.ReadByte(buf, off + 2) << 8);

                int dir = usage == USAGE_BRIGHTNESS_UP ? +1 : usage == USAGE_BRIGHTNESS_DOWN ? -1 : 0;
                if (dir != 0) _owner.Raise(dir);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }
}

/// <summary>Click-through black sheet over every monitor, for the below-hardware-zero rungs.</summary>
class DimOverlay
{
    const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
              WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;

    class Sheet : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }

    // The panel ramps its own brightness on a curve, so a below-zero step that
    // snapped instantly felt like a different control. Match it.
    const int FadeMs = 170;

    readonly Sheet _form;
    readonly System.Windows.Forms.Timer _anim;
    readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
    double _from, _current;

    /// <summary>Where the overlay is headed, not where the fade currently is.</summary>
    public int Alpha { get; private set; }

    public DimOverlay()
    {
        _form = new Sheet
        {
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Color.Black,
            ShowInTaskbar = false,
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Opacity = 0,
        };
        _anim = new System.Windows.Forms.Timer { Interval = 10 };
        _anim.Tick += Tick;
        _form.Bounds = SystemInformation.VirtualScreen;
        SystemEvents.DisplaySettingsChanged += (s, e) => _form.Bounds = SystemInformation.VirtualScreen;

        // Build the window up front. Created lazily on the first Show(), the
        // layered alpha is applied a frame *after* the window first appears, so
        // stepping into the dim region flashed fully-opaque black for a frame.
        var warm = _form.Handle;
        GC.KeepAlive(warm);
    }

    public void Apply(int alpha)
    {
        if (alpha == Alpha && !_anim.Enabled) return;

        Alpha = alpha;
        _from = _current;                       // retarget mid-fade rather than jumping
        _clock.Restart();

        if (alpha > 0 && !_form.Visible)
        {
            // Alpha first, then reveal - never the other way round.
            _form.Opacity = Math.Max(0, Math.Min(1, _current / 255.0));
            if (_form.Bounds != SystemInformation.VirtualScreen) _form.Bounds = SystemInformation.VirtualScreen;
            _form.Show();
            _form.TopMost = true;
        }

        _anim.Start();
    }

    void Tick(object sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalMilliseconds / FadeMs;
        bool done = t >= 1;
        if (done) t = 1;

        _current = _from + (Alpha - _from) * Ease(t);
        _form.Opacity = Math.Max(0, Math.Min(1, _current / 255.0));

        if (!done) return;
        _anim.Stop();
        _clock.Stop();
        if (Alpha <= 0 && _form.Visible) _form.Hide();
    }

    /// <summary>Ease in/out cubic - eases off at both ends like the panel's own ramp.</summary>
    static double Ease(double t)
    {
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
}

/// <summary>Small readout of the true level, since Windows' own popup shows its (overridden) value.</summary>
class Osd
{
    const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020,
              WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;

    class Panel : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }

    readonly Panel _form;
    readonly System.Windows.Forms.Timer _hide;
    int _step, _total;
    string _label = "";

    public Osd()
    {
        _form = new Panel
        {
            FormBorderStyle = FormBorderStyle.None,
            BackColor = Color.FromArgb(20, 20, 20),
            ShowInTaskbar = false,
            TopMost = true,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(260, 64),
            Opacity = 0.92,
        };
        _form.Paint += Draw;
        _hide = new System.Windows.Forms.Timer { Interval = 1300 };
        _hide.Tick += (s, e) => { _hide.Stop(); _form.Hide(); };
    }

    // Cached: held keys repaint this repeatedly, and building a font per frame
    // is UI-thread work sitting in the way of nothing useful.
    static readonly Font TextFont = new Font("Segoe UI", 10f);
    static readonly SolidBrush TrackBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
    static readonly SolidBrush FillBrush = new SolidBrush(Color.White);

    void Draw(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var track = new Rectangle(20, 38, _form.Width - 40, 8);
        g.FillRectangle(TrackBrush, track);
        int w = (int)Math.Round(track.Width * (_step / (double)Math.Max(1, _total - 1)));
        g.FillRectangle(FillBrush, new Rectangle(track.X, track.Y, w, track.Height));

        g.DrawString("Brightness  " + _label, TextFont, FillBrush, 20, 12);
    }

    public void Show(int step, int total, string label)
    {
        _step = step; _total = total; _label = label;

        var wa = Screen.PrimaryScreen.WorkingArea;
        _form.Location = new Point(wa.Left + (wa.Width - _form.Width) / 2, wa.Bottom - _form.Height - 80);
        if (!_form.Visible) _form.Show();
        _form.TopMost = true;
        _form.BringToFront();
        _form.Invalidate();

        _hide.Stop();
        _hide.Start();
    }
}
