// BrightnessSteps - finer brightness steps for the keyboard brightness keys.
//
// Windows' brightness keys move in jumps of 10, which makes the bottom of the
// range unusable: you can have 0 or 10, never 5. This walks a hand-tuned ladder
// instead, with 1-point steps at the bottom, plus a few "darker than hardware
// zero" rungs done with a black overlay (Windows clamps gamma ramps, so an
// overlay is the only way further down).
//
// How it hooks in: on this hardware the brightness keys never reach a low-level
// keyboard hook - firmware/Windows handles them below user mode - but they DO
// arrive as raw HID consumer-control reports (usage 0x6F up / 0x70 down) about
// 20ms before Windows applies its own step. So we can't swallow the key, but we
// always know a press happened and which way it went, which is enough to
// replace Windows' step with our own rung right after it lands.
using System;
using System.Drawing;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool fresh;
        using (var mutex = new System.Threading.Mutex(true, "BrightnessStepsSingleton", out fresh))
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
    // Darkest first. Below hardware 0 we stack overlay rungs; above it the rungs
    // are close together at the bottom and spread out toward full brightness so
    // every press feels like about the same relative change.
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

    // Windows applies its own step ~20ms after the HID report. If no brightness
    // event has arrived by this point, there wasn't going to be one (we're
    // already pinned at hardware 0 or 100) and we act on the key ourselves.
    const int KeySettleMs = 160;

    readonly NotifyIcon _tray;
    readonly DimOverlay _overlay = new DimOverlay();
    readonly Osd _osd = new Osd();
    readonly Control _sync = new Control();      // marshals WMI callbacks onto the UI thread
    readonly Timer _settle = new Timer { Interval = KeySettleMs };
    RawInputListener _raw;

    ManagementObject _brightnessMethods;
    int _index;
    int _pendingDir;
    DateTime _pendingAt = DateTime.MinValue;
    int _lastSelfSet = -1;
    DateTime _lastSelfSetAt = DateTime.MinValue;
    bool _showOsd = true;

    public TrayApp()
    {
        _sync.CreateControl();
        var _ = _sync.Handle;

        _index = IndexForHardware(ReadHardwareBrightness());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "BrightnessSteps",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _settle.Tick += (s, e) => { _settle.Stop(); FlushPendingKey(); };

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
        _overlay.Apply(0);
        _tray.Visible = false;
        ExitThread();
    }

    // ---------- key handling ----------

    void OnBrightnessKey(int direction)
    {
        // Arrives on the message-only window's thread, which is this one.
        _pendingDir = direction;
        _pendingAt = DateTime.UtcNow;
        _settle.Stop();
        _settle.Start();
    }

    void FlushPendingKey()
    {
        if (_pendingDir == 0) return;
        int dir = _pendingDir;
        _pendingDir = 0;
        Move(dir);
    }

    void Move(int direction)
    {
        ApplyIndex(Math.Max(0, Math.Min(Ladder.Length - 1, _index + direction)));
    }

    void ApplyIndex(int i)
    {
        _index = i;
        Step s = Ladder[i];

        // Raise hardware before lifting the overlay, and drop the overlay before
        // lowering hardware - otherwise the step flashes bright for a frame.
        if (s.Alpha == 0)
        {
            SetHardware(s.Hw);
            _overlay.Apply(0);
        }
        else
        {
            _overlay.Apply(s.Alpha);
            SetHardware(s.Hw);
        }

        if (_showOsd) _osd.Show(i, Ladder.Length, DescribeStep(s));
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

    // ---------- hardware brightness ----------

    int ReadHardwareBrightness()
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

    void SetHardware(int level)
    {
        try
        {
            if (_brightnessMethods == null)
            {
                var scope = new ManagementScope(@"root\WMI");
                foreach (ManagementObject o in new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightnessMethods")).Get())
                { _brightnessMethods = o; break; }
            }
            if (_brightnessMethods == null) return;
            _lastSelfSet = level;
            _lastSelfSetAt = DateTime.UtcNow;
            _brightnessMethods.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)level });
        }
        catch { _brightnessMethods = null; }
    }

    // ---------- reacting to Windows' own step ----------

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
        var now = DateTime.UtcNow;

        // Our own write echoing back.
        if (hw == _lastSelfSet && (now - _lastSelfSetAt).TotalMilliseconds < 2500) return;

        // Windows just applied its ±10 step for a key we saw on the HID stream.
        // Replace it with our rung immediately.
        if (_pendingDir != 0 && (now - _pendingAt).TotalMilliseconds < 900)
        {
            _settle.Stop();
            FlushPendingKey();
            return;
        }

        // Nobody pressed a key, so this is the slider (or something else) moving
        // brightness. Follow it rather than fight it.
        _index = IndexForHardware(hw);
        if (_overlay.Alpha != 0) _overlay.Apply(0);
    }
}

/// <summary>
/// Message-only window that listens for raw HID consumer-control reports and
/// reports brightness key presses. Raw input can observe these keys but cannot
/// block them, which is fine - we only need to know direction and timing.
/// </summary>
class RawInputListener : NativeWindow, IDisposable
{
    const int WM_INPUT = 0x00FF;
    const int RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const ushort USAGE_PAGE_CONSUMER = 0x0C;
    const ushort USAGE_CONSUMER_CONTROL = 0x01;
    const int USAGE_BRIGHTNESS_UP = 0x6F;
    const int USAGE_BRIGHTNESS_DOWN = 0x70;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public int Flags; public IntPtr hwndTarget; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint hdrSize);

    public event Action<int> BrightnessKey;

    public RawInputListener()
    {
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
                if (dir != 0)
                {
                    var h = BrightnessKey;
                    if (h != null) h(dir);
                }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
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

    readonly Sheet _form;
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
        _form.Bounds = SystemInformation.VirtualScreen;
        SystemEvents.DisplaySettingsChanged += (s, e) => _form.Bounds = SystemInformation.VirtualScreen;
    }

    public void Apply(int alpha)
    {
        Alpha = alpha;
        if (alpha <= 0) { if (_form.Visible) _form.Hide(); return; }
        _form.Bounds = SystemInformation.VirtualScreen;
        _form.Opacity = alpha / 255.0;
        if (!_form.Visible) _form.Show();
        _form.TopMost = true;
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
    readonly Timer _hide;
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
        _hide = new Timer { Interval = 1300 };
        _hide.Tick += (s, e) => { _hide.Stop(); _form.Hide(); };
    }

    void Draw(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var track = new Rectangle(20, 38, _form.Width - 40, 8);
        using (var b = new SolidBrush(Color.FromArgb(70, 70, 70))) g.FillRectangle(b, track);
        int w = (int)Math.Round(track.Width * (_step / (double)Math.Max(1, _total - 1)));
        using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, new Rectangle(track.X, track.Y, w, track.Height));

        using (var f = new Font("Segoe UI", 10f))
        using (var b = new SolidBrush(Color.White))
            g.DrawString("Brightness  " + _label, f, b, 20, 12);
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
