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
// then hold it. Windows still stomps on it ~20ms later, so a guard loop watches
// the backlight for a short window afterwards and puts it straight back. Both
// the read and the write go through the display driver directly
// (IOCTL_VIDEO_*_DISPLAY_BRIGHTNESS, ~0.16ms) rather than WMI (~10ms).
//
// The driver will not always take the first correction: a write issued while an
// earlier brightness change is still settling is often dropped outright, and a
// change takes 3-14ms to settle. So the guard polls (paced, ~100us) and re-issues
// (rate limited, ~3.3ms) until a read confirms the value. Re-issuing on every
// poll instead is what saturates the driver and guarantees it never converges.
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // A previous run killed while the screen was gamma-dimmed leaves the
        // ramp dark. Put it back before anything else can look at it.
        GammaDimmer.RecoverIfCrashed();

        // Written by the elevated copy the setup wizard launches.
        if (Array.IndexOf(args, "--enable-gamma") >= 0)
        {
            Environment.ExitCode = GammaSupport.WriteValue() ? 0 : 1;
            return;
        }

        // --selftest drives the real key path with a simulated Windows stomp, so
        // the press/stomp race can be reproduced without a physical keyboard.
        // It deliberately skips the singleton; stop the running instance first.
        if (Array.IndexOf(args, "--selftest") >= 0)
        {
            Application.EnableVisualStyles();
            SelfTest.Run();
            return;
        }

        // Lets the wizard be launched on its own, so keys can be re-taught
        // without hunting through the tray menu.
        if (Array.IndexOf(args, "--setup") >= 0)
        {
            Application.EnableVisualStyles();
            KeyConfig.Load();
            try
            {
                using (var dlg = new SetupForm())
                    if (dlg.ShowDialog() == DialogResult.OK) KeyConfig.Save(dlg.Down, dlg.Up);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                MessageBox.Show("Setup could not start: " + ex.Message, "BrightnessSteps",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            // Tell an already-running instance to pick the new keys up, then
            // fall through: if none is running, this process becomes the tray
            // app, so finishing setup always leaves an icon behind.
            SignalReload();
        }

        bool fresh;
        using (var mutex = new Mutex(true, "BrightnessStepsSingleton", out fresh))
        {
            if (!fresh) return;                 // already running
            Application.EnableVisualStyles();
            Application.Run(new TrayApp());
        }
    }

    public const string ReloadEventName = "BrightnessStepsReloadKeys";

    static void SignalReload()
    {
        try
        {
            EventWaitHandle ev;
            if (EventWaitHandle.TryOpenExisting(ReloadEventName, out ev))
                using (ev) ev.Set();
        }
        catch { }
    }
}

/// <summary>
/// What this machine actually looks like to the app. Exists so someone whose
/// laptop it does not work on can send back something useful in one click,
/// rather than "it does nothing". Purely local - nothing is transmitted.
/// </summary>
static class Diagnostics
{
    public static int HidDevicesSeen;
    public static int HidDevicesWithoutDescriptor;
    public static int KeyPressesSeen;
    public static int DecodedByDescriptor;
    public static int DecodedByFallbackLayout;
    public static bool RawInputRegistered;
    public static string BacklightMethod = "none";
    public static string DimMethod = "overlay";
    public static string BacklightDevice = "";
    public static int SupportedLevels = -1;

    public static string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BrightnessSteps - compatibility report");
        sb.AppendLine("generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();

        sb.AppendLine("[system]");
        sb.AppendLine("  windows       : " + Environment.OSVersion.Version + " (" + (IntPtr.Size == 8 ? "x64" : "x86") + ")");
        try
        {
            foreach (ManagementObject o in new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem").Get())
            { sb.AppendLine("  machine       : " + o["Manufacturer"] + " " + o["Model"]); break; }
        }
        catch { sb.AppendLine("  machine       : (unavailable)"); }
        sb.AppendLine();

        sb.AppendLine("[brightness control]");
        sb.AppendLine("  method        : " + BacklightMethod);
        sb.AppendLine("  device        : " + (BacklightDevice == "" ? "(none)" : BacklightDevice));
        sb.AppendLine("  levels        : " + (SupportedLevels < 0 ? "unknown" : SupportedLevels.ToString()));
        sb.AppendLine("  below zero    : " + DimMethod);
        sb.AppendLine();

        sb.AppendLine("[brightness keys]");
        sb.AppendLine("  raw input     : " + (RawInputRegistered ? "registered" : "NOT REGISTERED"));
        sb.AppendLine("  key presses   : " + KeyPressesSeen);
        sb.AppendLine("  hid devices   : " + HidDevicesSeen + " (" + HidDevicesWithoutDescriptor + " without a usable descriptor)");
        sb.AppendLine("  decoded via   : " + DecodedByDescriptor + " descriptor, " + DecodedByFallbackLayout + " fallback layout");
        sb.AppendLine();

        sb.AppendLine("[what this means]");
        if (BacklightMethod == "none")
            sb.AppendLine("  No panel accepted brightness commands. Likely a desktop, or an external");
        else if (KeyPressesSeen == 0)
            sb.AppendLine("  Brightness control works, but no brightness key was ever seen. The keys");
        else
            sb.AppendLine("  Both halves are working on this machine.");
        if (BacklightMethod == "none")
            sb.AppendLine("  monitor only - this tool drives built-in laptop panels.");
        else if (KeyPressesSeen == 0)
            sb.AppendLine("  may be handled in firmware and never reach Windows on this model.");

        return sb.ToString();
    }
}

/// <summary>
/// In-memory trace, off unless asked for. Records only interesting moments -
/// key presses, guard corrections, driver failures - never the poll loop
/// itself, which runs thousands of times per press.
/// </summary>
static class Trace
{
    public static volatile bool On;

    struct Rec { public long T; public string Msg; }

    static readonly System.Collections.Generic.Queue<Rec> Buf = new System.Collections.Generic.Queue<Rec>();
    static readonly object Lock = new object();
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    const int MaxRecords = 40000;

    public static void Log(string msg)
    {
        if (!On) return;
        lock (Lock)
        {
            if (Buf.Count >= MaxRecords) Buf.Dequeue();
            Buf.Enqueue(new Rec { T = Clock.ElapsedTicks, Msg = msg });
        }
    }

    public static int Count { get { lock (Lock) return Buf.Count; } }

    public static void Dump(string path)
    {
        Rec[] recs;
        lock (Lock) { recs = Buf.ToArray(); Buf.Clear(); }
        using (var w = new StreamWriter(path, false))
        {
            double freq = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            double t0 = recs.Length > 0 ? recs[0].T / freq : 0;
            foreach (var r in recs) w.WriteLine("{0,9:F3} ms  {1}", r.T / freq - t0, r.Msg);
        }
    }
}

/// <summary>
/// Reproduces the press/stomp race without a keyboard. Raw HID reports cannot
/// be injected, but the part that matters can be: it drives the app's real key
/// path and, on a separate device handle, plays Windows' role by adding its
/// +/-10 about 20ms after each press - the timing measured from the HID stream.
/// </summary>
static class SelfTest
{
    static void Say(string s)
    {
        Console.WriteLine("[{0:HH:mm:ss.fff}] {1}", DateTime.Now, s);
        Console.Out.Flush();
    }

    public static void Run()
    {
        // Anything that parks forever should still produce a report.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(45000);
            Say("WATCHDOG: still running after 45s, dumping and aborting");
            try { Trace.Dump(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "selftest-trace.txt")); } catch { }
            Environment.Exit(2);
        });
        watchdog.IsBackground = true;
        watchdog.Start();

        Say("constructing TrayApp");
        var app = new TrayApp();
        Say("TrayApp constructed");
        var windows = new Backlight();          // stands in for Windows' own writes
        Say("second backlight handle open");
        string dir = Path.GetDirectoryName(Application.ExecutablePath);

        // The UI thread must keep pumping - presentation is marshalled to it.
        var worker = new Thread(() =>
        {
            try
            {
                Say("worker started");
                foreach (int gap in new[] { 120, 35 })
                {
                    Sweep(app, windows, +1, 26, gap, "up");
                    Sweep(app, windows, -1, 26, gap, "down");
                }
            }
            catch (Exception ex) { Say("worker threw: " + ex); }
            finally
            {
                try { Trace.Dump(Path.Combine(dir, "selftest-trace.txt")); } catch { }
                Application.Exit();
            }
        });
        worker.IsBackground = true;
        worker.Start();

        Application.Run();
        windows.Dispose();
    }

    static void Sweep(TrayApp app, Backlight windows, int direction, int presses, int gapMs, string label)
    {
        // Start from a known end of the ladder.
        Say(string.Format("sweep {0} @{1}ms: resetting", label, gapMs));
        for (int i = 0; i < 30; i++) { app.StepForTest(-direction); Thread.Sleep(4); }
        Say("  reset done, settling");
        Thread.Sleep(400);
        Say("  pressing");

        Trace.On = true;
        Trace.Log(string.Format("=== sweep {0}, {1} presses {2}ms apart ===", label, presses, gapMs));

        int worst = 0;
        for (int i = 0; i < presses; i++)
        {
            app.StepForTest(direction);

            // Windows applies its step ~20ms later, from whatever it finds.
            int stompDelay = Math.Min(20, gapMs / 2);
            Thread.Sleep(stompDelay);
            int cur = windows.Get();
            int stomped = Math.Max(0, Math.Min(100, cur + direction * 10));
            windows.Set(stomped);

            Thread.Sleep(Math.Max(1, gapMs - stompDelay));

            int settled = windows.Get();
            int want = app.CurrentHwForTest;
            if (settled != want)
            {
                int drift = Math.Abs(settled - want);
                if (drift > worst) worst = drift;
                Trace.Log(string.Format("DRIFT  after press {0}: hardware {1}, rung wants {2}", i + 1, settled, want));
            }
        }

        Trace.Log(string.Format("=== sweep {0} @{1}ms done, worst drift {2} points ===", label, gapMs, worst));
        Console.WriteLine("sweep {0,-5} gap={1,3}ms   worst drift = {2} points", label, gapMs, worst);
        Thread.Sleep(300);
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
    readonly Dimmer _dimmer = Dimmer.Create();
    readonly Osd _osd = new Osd();
    readonly Control _sync = new Control();      // marshals WMI callbacks onto the UI thread
    readonly Backlight _backlight = new Backlight();
    readonly Guard _guard;
    RawInputListener _raw;
    KeyboardWatch _keys;
    volatile bool _stopping;

    // Key presses arrive on the raw-input thread; the menu and the slider
    // watcher run on the UI thread. Everything shared between them is either
    // under _ladderLock or interlocked.
    readonly object _ladderLock = new object();
    int _index;
    volatile int _desiredHw = -1;       // the hardware value the current rung asks for
    readonly bool _panelAvailable;
    long _guardEndsAtTicks;
    volatile bool _showOsd = true;

    public TrayApp()
    {
        _sync.CreateControl();
        var _ = _sync.Handle;

        _guard = new Guard();
        int hw = _backlight.Get();
        _panelAvailable = hw >= 0;
        if (!_panelAvailable) hw = 50;
        _desiredHw = hw;
        _index = IndexForHardware(hw);

        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = _panelAvailable ? "BrightnessSteps" : "BrightnessSteps - no adjustable display found",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        if (!_panelAvailable)
            _tray.ShowBalloonTip(10000, "BrightnessSteps",
                "No adjustable display found. This tool drives built-in laptop panels; " +
                "external monitors are not supported. Use \"Copy compatibility report\" to report this.",
                ToolTipIcon.Warning);

        KeyConfig.Load();
        RebuildListeners();
        WatchForReload();

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
        m.Items.Add("Set up brightness keys...", null, (s, e) => RunSetup());
        m.Items.Add("Reset keys to defaults", null, (s, e) => ResetKeys());
        m.Items.Add("Copy compatibility report", null, (s, e) => ShowReport());
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

    /// <summary>
    /// One click to get something reportable. Written locally and opened for the
    /// user to read first - nothing leaves the machine on its own.
    /// </summary>
    void ShowReport()
    {
        string text = Diagnostics.Report();
        try { Clipboard.SetText(text); } catch { }
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "brightnesssteps-report.txt");
            File.WriteAllText(path, text);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { MessageBox.Show(text, "BrightnessSteps"); }
    }

    void RunSetup()
    {
        // The running listener keeps its own registration; the wizard opens a
        // second one that listens to everything, then narrows it down.
        using (var dlg = new SetupForm())
        {
            if (dlg.ShowDialog() != DialogResult.OK) return;
            KeyConfig.Save(dlg.Down, dlg.Up);
        }

        RebuildListeners();
        _tray.ShowBalloonTip(5000, "BrightnessSteps",
            "Keys set: " + KeyConfig.Down.Describe() + " / " + KeyConfig.Up.Describe(), ToolTipIcon.Info);
    }

    /// <summary>
    /// Undo for a bad setup run. Without this, teaching it the wrong key leaves
    /// the brightness keys dead with no way back except deleting a file.
    /// </summary>
    void ResetKeys()
    {
        KeyConfig.Reset();
        RebuildListeners();
        _tray.ShowBalloonTip(5000, "BrightnessSteps",
            "Back to the standard brightness keys.", ToolTipIcon.Info);
    }

    /// <summary>Re-reads the key config and re-attaches input. Safe to call repeatedly.</summary>
    void RebuildListeners()
    {
        if (_raw != null) { _raw.Dispose(); _raw = null; }
        if (_keys != null) { _keys.Dispose(); _keys = null; }

        _raw = new RawInputListener(KeyConfig.Collections(), KeyConfig.Pages());
        _raw.UsageSeen += OnUsage;
        if (KeyConfig.NeedsKeyboardHook)
        {
            _keys = new KeyboardWatch();
            _keys.KeyDown += OnVirtualKey;
        }
    }

    /// <summary>
    /// A separate `--setup` run saves new keys and signals this. Reload rather
    /// than making the user restart the tray app to see the change.
    /// </summary>
    void WatchForReload()
    {
        var t = new Thread(() =>
        {
            using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ReloadEventName))
                while (!_stopping)
                {
                    if (!ev.WaitOne(1000)) continue;
                    try
                    {
                        _sync.BeginInvoke((Action)(() =>
                        {
                            KeyConfig.Load();
                            RebuildListeners();
                            _tray.ShowBalloonTip(5000, "BrightnessSteps",
                                "Keys set: " + KeyConfig.Down.Describe() + " / " + KeyConfig.Up.Describe(),
                                ToolTipIcon.Info);
                        }));
                    }
                    catch { }
                }
        });
        t.IsBackground = true;
        t.Name = "brightness-reload";
        t.Start();
    }

    static Icon LoadAppIcon()
    {
        try
        {
            string ico = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "app.ico");
            if (File.Exists(ico)) return new Icon(ico);
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    void Shutdown()
    {
        _stopping = true;
        if (_raw != null) { _raw.Dispose(); _raw = null; }
        if (_keys != null) { _keys.Dispose(); _keys = null; }
        _guard.Dispose();
        _dimmer.Dispose();
        _backlight.Dispose();
        _tray.Visible = false;
        ExitThread();
    }

    // ---------- key handling ----------

    void OnUsage(ushort page, ushort usage)
    {
        var sig = KeySignal.Hid(page, usage);
        if (sig.Matches(KeyConfig.Down)) { Diagnostics.KeyPressesSeen++; Move(-1); }
        else if (sig.Matches(KeyConfig.Up)) { Diagnostics.KeyPressesSeen++; Move(+1); }
    }

    void OnVirtualKey(int vk)
    {
        var sig = KeySignal.Key(vk);
        if (sig.Matches(KeyConfig.Down)) { Diagnostics.KeyPressesSeen++; Move(-1); }
        else if (sig.Matches(KeyConfig.Up)) { Diagnostics.KeyPressesSeen++; Move(+1); }
    }

    /// <summary>Same entry point a key press takes. Used by --selftest.</summary>
    public void StepForTest(int direction) { Move(direction); }

    public int CurrentHwForTest { get { lock (_ladderLock) return Ladder[_index].Hw; } }

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
        Trace.Log(string.Format("PRESS  rung {0}  hw={1} alpha={2}", i, s.Hw, s.Alpha));
        Interlocked.Exchange(ref _guardEndsAtTicks, DateTime.UtcNow.AddMilliseconds(GuardMs).Ticks);
        _guard.Hold(s.Hw, GuardMs);
        SetHardware(s.Hw);

        // Everything below is presentation and must not sit in the key path, so
        // it is handed to the UI thread. Ordering between hardware and overlay
        // does not matter here because every overlay rung sits at hardware 0,
        // and so does its neighbour - the two never change in the same step.
        Action present = () =>
        {
            _dimmer.Apply(s.Alpha);
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
        _desiredHw = level;
        _backlight.Set(level);
    }

    // ---------- following the slider ----------

    void StartBrightnessWatcher()
    {
        try
        {
            var w = new ManagementEventWatcher(new ManagementScope(@"root\WMI"),
                        new WqlEventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            // The event is only a nudge that *something* changed; its value is
            // read fresh from the hardware in the handler.
            w.EventArrived += (s, e) =>
            {
                try { _sync.BeginInvoke((Action)OnExternalBrightness); } catch { }
            };
            w.Start();
        }
        catch { }
    }

    void OnExternalBrightness()
    {
        long now = DateTime.UtcNow.Ticks;

        // While the guard is running we own the backlight; the events arriving
        // are Windows' stomp and our corrections racing each other.
        if (now < Interlocked.Read(ref _guardEndsAtTicks) + 250 * TimeSpan.TicksPerMillisecond) return;

        // Deliberately ignore the value the event carries. It reports brightness
        // as it was at the moment of the change, and delivery can lag by
        // hundreds of milliseconds - long enough for the guard to have already
        // put the value back. Acting on that stale number resynced the ladder to
        // Windows' stomp and cleared the overlay, which in the dim zone reads as
        // a jump up to the 0 rung. Ask the hardware what is actually true now.
        int actual = _backlight.Get();

        // Still where we put it, so nothing external really happened. Compared
        // against the rung's own value rather than a recency window: sitting in
        // the dim zone longer than the old 2.5s timeout let any unrelated
        // brightness event clear the overlay out from under us.
        if (actual == _desiredHw) return;

        // A real external change - the slider, battery saver, adaptive
        // brightness. Follow it rather than fight it.
        _desiredHw = actual;
        lock (_ladderLock) { _index = IndexForHardware(actual); }
        if (_dimmer.Alpha != 0) _dimmer.Apply(0);
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
    string _devicePath = "";
    byte _policy = DISPLAYPOLICY_AC;
    ManagementObject _wmiMethods;

    // Preallocated: the guard polls these thousands of times per key press, and
    // an AllocHGlobal per poll would be pure churn.
    readonly IntPtr _queryBuf = Marshal.AllocHGlobal(3);
    readonly IntPtr _setBuf = Marshal.AllocHGlobal(3);

    public Backlight()
    {
        Open();
        if (Diagnostics.BacklightMethod == "none") RecordDiagnostics();
    }

    public bool UsingFastPath { get { return _device != (IntPtr)(-1); } }

    /// <summary>True if this machine has a panel we can actually drive.</summary>
    public bool Available { get { return UsingFastPath || WmiGet() >= 0; } }

    void RecordDiagnostics()
    {
        if (UsingFastPath)
        {
            Diagnostics.BacklightMethod = "display driver ioctl";
            Diagnostics.BacklightDevice = _devicePath;
        }
        else if (WmiGet() >= 0)
        {
            Diagnostics.BacklightMethod = "wmi";
            Diagnostics.BacklightDevice = "(wmi)";
        }

        try
        {
            foreach (ManagementObject o in new ManagementObjectSearcher(
                new ManagementScope(@"root\WMI"), new SelectQuery("WmiMonitorBrightness")).Get())
            {
                var levels = o["Level"] as byte[];
                if (levels != null) Diagnostics.SupportedLevels = levels.Length;
                break;
            }
        }
        catch { }
    }

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
                    if (TryQuery(out probe)) { _devicePath = path; return; }   // usable panel, keep it
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
        // The WMI path is ~60x slower; if this ever fires mid-press it would
        // stall the guard long enough for stomps to accumulate.
        Trace.Log("BACKLIGHT query fell back to WMI");
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
                Trace.Log("BACKLIGHT set ioctl failed err=" + Marshal.GetLastWin32Error());
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
        return -1;                  // no panel answered; callers treat <0 as unavailable
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

    // Poll every ~100us rather than as fast as the CPU allows: detection stays
    // well under a millisecond, with ~14x less driver traffic.
    static readonly long PollIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 10000;

    // Minimum gap between corrective writes, so a set in flight gets a chance to
    // land before the next one is queued behind it. ~3.3ms; measured against
    // 1.4ms (identical) and 5.9ms (worse), so this sits on a flat optimum.
    static readonly long SetBackoffTicks = System.Diagnostics.Stopwatch.Frequency / 300;

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
            long lastSetAt = 0;
            long nextPoll = System.Diagnostics.Stopwatch.GetTimestamp();

            timeBeginPeriod(1);                 // Sleep(1) is ~15ms otherwise
            try
            {
                while (!_stop)
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (now >= Interlocked.Read(ref _untilTicks)) break;

                    int cur = _backlight.Get();
                    if (cur >= 0 && cur != _target)
                    {
                        // Rate limited. The driver applies brightness serially,
                        // so re-issuing on every poll (this used to fire every
                        // ~7us) keeps it permanently busy and the correction
                        // never lands - the guard was starving the very thing it
                        // was driving, and Windows' steps piled up behind it.
                        long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (stamp - lastSetAt >= SetBackoffTicks)
                        {
                            Trace.Log(string.Format("guard  saw {0}, restoring {1}", cur, _target));
                            _backlight.Set(_target);
                            lastSetAt = stamp;
                        }
                    }
                    else lastSetAt = 0;         // settled; next stomp gets an immediate answer

                    bool spinBudgetLeft = (now - startedAt) < MaxContinuousSpinMs * TimeSpan.TicksPerMillisecond;
                    if (now < Interlocked.Read(ref _spinUntilTicks) && spinBudgetLeft)
                    {
                        // Pace the reads too. Detection stays well under a
                        // millisecond without flooding the driver with queries.
                        nextPoll += PollIntervalTicks;
                        long t = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (nextPoll < t) nextPoll = t;             // fell behind; don't burst
                        while (System.Diagnostics.Stopwatch.GetTimestamp() < nextPoll) Thread.SpinWait(20);
                    }
                    else
                    {
                        Thread.Sleep(1);
                        nextPoll = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
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

/// <summary>A brightness key, as this machine actually emits it.</summary>
struct KeySignal
{
    public bool IsHid;
    public ushort Page, Usage;
    public int Vk;

    public static KeySignal Hid(ushort page, ushort usage) { return new KeySignal { IsHid = true, Page = page, Usage = usage }; }
    public static KeySignal Key(int vk) { return new KeySignal { IsHid = false, Vk = vk }; }

    public bool IsSet { get { return IsHid || Vk != 0; } }

    public bool Matches(KeySignal o)
    {
        if (IsHid != o.IsHid) return false;
        return IsHid ? (Page == o.Page && Usage == o.Usage) : Vk == o.Vk;
    }

    public override string ToString()
    {
        return IsHid ? string.Format("hid:{0:X2}/{1:X2}", Page, Usage) : string.Format("vk:{0:X2}", Vk);
    }

    public string Describe()
    {
        if (!IsHid) return "keyboard key 0x" + Vk.ToString("X2");
        if (Page == 0x0C && Usage == 0x6F) return "standard brightness-up";
        if (Page == 0x0C && Usage == 0x70) return "standard brightness-down";
        return string.Format("HID page 0x{0:X2}, usage 0x{1:X2}", Page, Usage);
    }

    public static bool TryParse(string s, out KeySignal k)
    {
        k = new KeySignal();
        if (string.IsNullOrEmpty(s)) return false;
        try
        {
            if (s.StartsWith("hid:"))
            {
                var parts = s.Substring(4).Split('/');
                k = Hid(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16));
                return true;
            }
            if (s.StartsWith("vk:")) { k = Key(Convert.ToInt32(s.Substring(3), 16)); return true; }
        }
        catch { }
        return false;
    }
}

/// <summary>
/// Which signals mean brighter and darker on this machine. Defaults to the
/// standard HID Consumer Page usages, which is right on most laptops; the setup
/// wizard overwrites them for vendors that use something else.
/// </summary>
static class KeyConfig
{
    public static readonly KeySignal DefaultDown = KeySignal.Hid(0x0C, 0x70);
    public static readonly KeySignal DefaultUp = KeySignal.Hid(0x0C, 0x6F);

    public static KeySignal Down = DefaultDown;
    public static KeySignal Up = DefaultUp;
    public static bool Learned;

    public static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightnessSteps"); }
    }
    public static string ConfigFile { get { return Path.Combine(Dir, "keys.cfg"); } }

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            KeySignal d = new KeySignal(), u = new KeySignal();
            foreach (var line in File.ReadAllLines(ConfigFile))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.TrimStart().StartsWith("#")) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (k == "down") KeySignal.TryParse(v, out d);
                else if (k == "up") KeySignal.TryParse(v, out u);
            }
            if (d.IsSet && u.IsSet) { Down = d; Up = u; Learned = true; }
        }
        catch { }
    }

    public static void Save(KeySignal down, KeySignal up)
    {
        Down = down; Up = up; Learned = true;
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigFile,
                "# Learned by the setup wizard. Delete this file to return to the defaults." + Environment.NewLine +
                "down=" + down + Environment.NewLine +
                "up=" + up + Environment.NewLine);
        }
        catch { }
    }

    public static void Reset()
    {
        Down = DefaultDown; Up = DefaultUp; Learned = false;
        try { if (File.Exists(ConfigFile)) File.Delete(ConfigFile); } catch { }
    }

    /// <summary>Collections raw input must listen to for the configured keys.</summary>
    public static ushort[][] Collections()
    {
        var list = new System.Collections.Generic.List<ushort[]>();
        list.Add(new ushort[] { 0x0C, 0x01 });                 // consumer control, the usual home
        foreach (var k in new[] { Down, Up })
            if (k.IsHid && k.Page != 0x0C) list.Add(new ushort[] { k.Page, 0x01 });
        return list.ToArray();
    }

    public static ushort[] Pages()
    {
        var list = new System.Collections.Generic.List<ushort>();
        list.Add(0x0C);
        foreach (var k in new[] { Down, Up })
            if (k.IsHid && !list.Contains(k.Page)) list.Add(k.Page);
        return list.ToArray();
    }

    public static bool NeedsKeyboardHook { get { return !Down.IsHid || !Up.IsHid; } }
}

/// <summary>Every HID top-level collection present, so the wizard can listen to all of them.</summary>
static class HidScan
{
    const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid g; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr w, uint f);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA a);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA a, IntPtr d, uint s, out uint r, IntPtr i);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint acc, uint share, IntPtr sec, uint disp, uint flags, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static ushort[][] Collections()
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        var result = new System.Collections.Generic.List<ushort[]>();

        Guid hid;
        HidD_GetHidGuid(out hid);
        IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == (IntPtr)(-1)) return result.ToArray();

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref did); i++)
            {
                uint need;
                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out need, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, detail, need, out need, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni((IntPtr)(detail.ToInt64() + 4));

                    IntPtr h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (h == (IntPtr)(-1)) continue;
                    try
                    {
                        IntPtr pp;
                        if (!HidD_GetPreparsedData(h, out pp)) continue;
                        HIDP_CAPS caps;
                        HidP_GetCaps(pp, out caps);
                        HidD_FreePreparsedData(pp);

                        // Mouse and keyboard top-level collections belong to the
                        // system; listening to them would pull in ordinary typing.
                        if (caps.UsagePage == 0x01 && (caps.Usage == 0x02 || caps.Usage == 0x06)) continue;

                        int key = (caps.UsagePage << 16) | caps.Usage;
                        if (seen.Add(key)) result.Add(new ushort[] { caps.UsagePage, caps.Usage });
                    }
                    finally { CloseHandle(h); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }

        return result.ToArray();
    }
}

/// <summary>
/// Listens for raw HID reports and reports the usages seen. Raw input can
/// observe brightness keys but cannot block them, which is fine - we only need
/// to know direction and timing.
///
/// It runs on its own thread with its own message loop. On the UI thread,
/// WM_INPUT would queue behind the overlay's animation ticks and the popup's
/// painting - exactly what is busiest when keys are held or pressed quickly in
/// the dim region, so the guard got armed late when it mattered most.
/// </summary>
class RawInputListener : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const int WM_QUIT = 0x0012;
    const int RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;
    const uint RIDI_PREPARSEDDATA = 0x20000005;
    const int HIDP_INPUT = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public int Flags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint cmd, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [In, Out] ushort[] usageList, ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLength);
    [DllImport("hid.dll")] static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>Raised on the listener's own thread for every usage seen.</summary>
    public event Action<ushort, ushort> UsageSeen;

    readonly ushort[][] _collections;
    readonly ushort[] _pages;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
    readonly System.Collections.Generic.Dictionary<IntPtr, IntPtr> _preparsed =
        new System.Collections.Generic.Dictionary<IntPtr, IntPtr>();
    Sink _sink;
    uint _threadId;

    public RawInputListener(ushort[][] collections, ushort[] pages)
    {
        _collections = collections;
        _pages = pages;
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

    void Raise(ushort page, ushort usage)
    {
        var h = UsageSeen;
        if (h != null) h(page, usage);
    }

    class Sink : NativeWindow
    {
        readonly RawInputListener _owner;

        public Sink(RawInputListener owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Parent = (IntPtr)(-3) });   // HWND_MESSAGE

            var devs = new RAWINPUTDEVICE[owner._collections.Length];
            for (int i = 0; i < devs.Length; i++)
                devs[i] = new RAWINPUTDEVICE
                {
                    UsagePage = owner._collections[i][0],
                    Usage = owner._collections[i][1],
                    Flags = RIDEV_INPUTSINK,        // deliver even when we have no focus
                    hwndTarget = Handle,
                };

            bool ok = RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (!Diagnostics.RawInputRegistered) Diagnostics.RawInputRegistered = ok;
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
                if (sizeHid < 2) return;

                IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);             // RAWINPUTHEADER.hDevice
                IntPtr preparsed = _owner.PreparsedFor(hDevice);

                for (int r = 0; r < count; r++)
                {
                    int off = data + r * sizeHid;
                    if (off + sizeHid > size) break;
                    IntPtr report = (IntPtr)(buf.ToInt64() + off);

                    if (preparsed != IntPtr.Zero)
                    {
                        foreach (ushort page in _owner._pages)
                        {
                            ushort[] usages = UsagesOn(preparsed, page, report, (uint)sizeHid);
                            for (int u = 0; u < usages.Length; u++)
                            {
                                Diagnostics.DecodedByDescriptor++;
                                _owner.Raise(page, usages[u]);
                            }
                        }
                    }
                    else
                    {
                        ushort one = ByLayout(buf, off, sizeHid, (int)size);
                        if (one != 0) { Diagnostics.DecodedByFallbackLayout++; _owner.Raise(0x0C, one); }
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Decodes a report the way the device's own descriptor says to, rather
        /// than assuming a byte layout. Consumer-control reports differ between
        /// vendors - some send a 16-bit usage, some a bitmap, with varying report
        /// ids and padding - so this is what lets it work on hardware other than
        /// the machine it was written on.
        /// </summary>
        static ushort[] UsagesOn(IntPtr preparsed, ushort page, IntPtr report, uint reportLength)
        {
            int max = HidP_MaxUsageListLength(HIDP_INPUT, page, preparsed);
            if (max <= 0) return EmptyUsages;

            var usages = new ushort[max];
            uint len = (uint)max;
            if (HidP_GetUsages(HIDP_INPUT, page, 0, usages, ref len, preparsed, report, reportLength) != HIDP_STATUS_SUCCESS)
                return EmptyUsages;

            if (len == usages.Length) return usages;
            var trimmed = new ushort[len];
            Array.Copy(usages, trimmed, (int)len);
            return trimmed;
        }

        static readonly ushort[] EmptyUsages = new ushort[0];

        /// <summary>Fallback for the common "report id then 16-bit usage" layout.</summary>
        static ushort ByLayout(IntPtr buf, int off, int sizeHid, int size)
        {
            if (sizeHid < 3 || off + 3 > size) return 0;
            return (ushort)(Marshal.ReadByte(buf, off + 1) | (Marshal.ReadByte(buf, off + 2) << 8));
        }
    }

    // One preparsed descriptor per device, kept for the life of the listener.
    internal IntPtr PreparsedFor(IntPtr hDevice)
    {
        IntPtr pp;
        if (_preparsed.TryGetValue(hDevice, out pp)) return pp;

        pp = IntPtr.Zero;
        uint size = 0;
        if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) == 0 && size > 0)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfoW(hDevice, RIDI_PREPARSEDDATA, buf, ref size) != unchecked((uint)-1)) pp = buf;
            else Marshal.FreeHGlobal(buf);
        }

        _preparsed[hDevice] = pp;       // cache the failure too; do not retry per report
        Diagnostics.HidDevicesSeen++;
        if (pp == IntPtr.Zero) Diagnostics.HidDevicesWithoutDescriptor++;
        return pp;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        foreach (var pp in _preparsed.Values) if (pp != IntPtr.Zero) Marshal.FreeHGlobal(pp);
        _preparsed.Clear();
    }
}

/// <summary>
/// Low-level keyboard hook, installed only when a learned key turns out to be
/// an ordinary virtual key rather than a HID usage. Most laptops never need it.
/// </summary>
class KeyboardWatch : IDisposable
{
    const int WH_KEYBOARD_LL = 13;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    readonly HookProc _proc;        // must outlive the hook
    IntPtr _handle;

    public event Action<int> KeyDown;

    public KeyboardWatch()
    {
        _proc = Callback;
        _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == 0x0100 || msg == 0x0104)     // key down
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                var h = KeyDown;
                if (h != null) h((int)k.vkCode);
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose() { if (_handle != IntPtr.Zero) { UnhookWindowsHookEx(_handle); _handle = IntPtr.Zero; } }
}

/// <summary>
/// Whether Windows will accept a gamma ramp dark enough to be useful. By
/// default it refuses anything below about 60%, which is why the overlay
/// exists; the machine-wide GdiIcmGammaRange value lifts that.
/// </summary>
static class GammaSupport
{
    const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM";
    const string ValueName = "GdiIcmGammaRange";

    public static bool Unlocked
    {
        get
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(KeyPath))
                    return k != null && Convert.ToInt32(k.GetValue(ValueName, 0)) >= 256;
            }
            catch { return false; }
        }
    }

    /// <summary>Writes the value. Needs elevation, hence its own process.</summary>
    public static bool WriteValue()
    {
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(KeyPath))
            {
                if (k == null) return false;
                k.SetValue(ValueName, 256, RegistryValueKind.DWord);
                return true;
            }
        }
        catch { return false; }
    }

    /// <summary>Re-runs this exe elevated to write the value. Returns true if it took.</summary>
    public static bool RequestEnable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--enable-gamma")
            {
                UseShellExecute = true,
                Verb = "runas",                 // the UAC prompt the user just agreed to
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(60000);
            return Unlocked;
        }
        catch { return false; }                 // cancelled the prompt
    }
}

/// <summary>
/// Darkens the screen below what the backlight alone can manage. Two
/// implementations: a gamma ramp where Windows allows one, and a black overlay
/// window everywhere else. Both fade on the same curve as the panel's own
/// brightness ramp, so the two halves of the ladder feel like one control.
/// </summary>
abstract class Dimmer : IDisposable
{
    const int FadeMs = 170;

    readonly System.Windows.Forms.Timer _anim;
    readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
    double _from, _current;

    /// <summary>Where it is headed, not where the fade currently is.</summary>
    public int Alpha { get; private set; }

    protected Dimmer()
    {
        _anim = new System.Windows.Forms.Timer { Interval = 10 };
        _anim.Tick += Tick;
    }

    public void Apply(int alpha)
    {
        if (alpha == Alpha && !_anim.Enabled) return;

        Alpha = alpha;
        _from = _current;                       // retarget mid-fade rather than jumping
        _clock.Restart();
        if (alpha > 0) OnBeforeShow(_current);
        _anim.Start();
    }

    void Tick(object sender, EventArgs e)
    {
        double t = _clock.Elapsed.TotalMilliseconds / FadeMs;
        bool done = t >= 1;
        if (done) t = 1;

        _current = _from + (Alpha - _from) * Ease(t);
        OnLevel(_current);

        if (!done) return;
        _anim.Stop();
        _clock.Stop();
        if (Alpha <= 0) OnCleared();
    }

    /// <summary>Ease in/out cubic - eases off at both ends like the panel's own ramp.</summary>
    static double Ease(double t)
    {
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    protected virtual void OnBeforeShow(double current) { }
    protected abstract void OnLevel(double alpha);      // 0..255
    protected virtual void OnCleared() { }

    public virtual void Dispose() { _anim.Dispose(); }

    /// <summary>Gamma where it is allowed, overlay otherwise.</summary>
    public static Dimmer Create()
    {
        if (GammaSupport.Unlocked)
        {
            var g = new GammaDimmer();
            if (g.Usable) { Diagnostics.DimMethod = "gamma ramp"; return g; }
            g.Dispose();        // key is set but the driver still refuses
        }
        Diagnostics.DimMethod = GammaSupport.Unlocked ? "overlay (gamma refused)" : "overlay";
        return new DimOverlay();
    }
}

/// <summary>
/// Dims by scaling the display's gamma ramp. Nothing to composite, so it never
/// appears in screenshots or screen shares and never sits on top of fullscreen
/// apps - unlike the overlay.
///
/// The catch: a gamma ramp outlives the process. If this is killed while dark,
/// the screen stays dark, so the original ramp is saved to disk and a marker
/// file lets the next start put it back.
/// </summary>
class GammaDimmer : Dimmer
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    static string SavedRampPath { get { return Path.Combine(KeyConfig.Dir, "gamma-original.bin"); } }
    static string MarkerPath { get { return Path.Combine(KeyConfig.Dir, "gamma-active"); } }

    readonly IntPtr _dc;
    readonly ushort[] _original = new ushort[768];
    readonly ushort[] _scratch = new ushort[768];
    bool _marked;

    public bool Usable { get; private set; }

    public GammaDimmer()
    {
        _dc = GetDC(IntPtr.Zero);
        if (_dc == IntPtr.Zero) return;
        if (!GetDeviceGammaRamp(_dc, _original)) return;

        // Prove a deep ramp is actually accepted before committing to this
        // path; the registry value can be set while a driver still says no.
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                _scratch[c * 256 + i] = (ushort)(_original[c * 256 + i] * 0.30);

        bool ok = SetDeviceGammaRamp(_dc, _scratch);
        SetDeviceGammaRamp(_dc, _original);
        Usable = ok;

        if (Usable) SaveOriginal();
    }

    void SaveOriginal()
    {
        try
        {
            Directory.CreateDirectory(KeyConfig.Dir);
            var bytes = new byte[_original.Length * 2];
            Buffer.BlockCopy(_original, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(SavedRampPath, bytes);
        }
        catch { }
    }

    /// <summary>
    /// If a previous run was killed while the screen was dimmed, the ramp is
    /// still dark. Put it back before doing anything else.
    /// </summary>
    public static void RecoverIfCrashed()
    {
        try
        {
            if (!File.Exists(MarkerPath)) return;

            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                var ramp = new ushort[768];
                if (File.Exists(SavedRampPath))
                {
                    var bytes = File.ReadAllBytes(SavedRampPath);
                    if (bytes.Length == ramp.Length * 2) Buffer.BlockCopy(bytes, 0, ramp, 0, bytes.Length);
                    else ramp = Identity();
                }
                else ramp = Identity();

                SetDeviceGammaRamp(dc, ramp);
                ReleaseDC(IntPtr.Zero, dc);
            }
            File.Delete(MarkerPath);
        }
        catch { }
    }

    static ushort[] Identity()
    {
        var r = new ushort[768];
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                r[c * 256 + i] = (ushort)Math.Min(65535, i * 257);
        return r;
    }

    protected override void OnLevel(double alpha)
    {
        if (!Usable) return;

        double keep = 1.0 - Math.Max(0, Math.Min(255, alpha)) / 255.0;
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < 256; i++)
                _scratch[c * 256 + i] = (ushort)(_original[c * 256 + i] * keep);

        SetDeviceGammaRamp(_dc, _scratch);
        Mark(alpha > 0.5);
    }

    protected override void OnCleared()
    {
        if (Usable) SetDeviceGammaRamp(_dc, _original);
        Mark(false);
    }

    void Mark(bool dimmed)
    {
        if (dimmed == _marked) return;
        _marked = dimmed;
        try
        {
            if (dimmed) { Directory.CreateDirectory(KeyConfig.Dir); File.WriteAllText(MarkerPath, "1"); }
            else if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch { }
    }

    public override void Dispose()
    {
        base.Dispose();
        if (_dc != IntPtr.Zero)
        {
            if (Usable) SetDeviceGammaRamp(_dc, _original);
            ReleaseDC(IntPtr.Zero, _dc);
        }
        Mark(false);
    }
}

/// <summary>Click-through black sheet over every monitor. The fallback when gamma is clamped.</summary>
class DimOverlay : Dimmer
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

        // Build the window up front. Created lazily on the first Show(), the
        // layered alpha is applied a frame *after* the window first appears, so
        // stepping into the dim region flashed fully-opaque black for a frame.
        var warm = _form.Handle;
        GC.KeepAlive(warm);
    }

    protected override void OnBeforeShow(double current)
    {
        if (_form.Visible) return;
        // Alpha first, then reveal - never the other way round.
        _form.Opacity = Math.Max(0, Math.Min(1, current / 255.0));
        if (_form.Bounds != SystemInformation.VirtualScreen) _form.Bounds = SystemInformation.VirtualScreen;
        _form.Show();
        _form.TopMost = true;
    }

    protected override void OnLevel(double alpha)
    {
        _form.Opacity = Math.Max(0, Math.Min(1, alpha / 255.0));
    }

    protected override void OnCleared()
    {
        if (_form.Visible) _form.Hide();
    }

    public override void Dispose()
    {
        base.Dispose();
        _form.Dispose();
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




/// <summary>
/// Asks the user to press their own brightness keys and learns whatever this
/// machine actually emits. This is what makes the app work on vendors that do
/// not use the standard HID brightness usages: rather than guessing every
/// vendor's encoding, listen to every HID collection on the box and take the
/// signal that shows up for "darker" but not for "brighter", and vice versa.
/// </summary>
class SetupForm : Form
{
    readonly Label _step = new Label();
    readonly Label _hint = new Label();
    readonly Label _status = new Label();
    readonly Button _next = new Button();
    readonly Button _cancel = new Button();
    readonly CheckBox _gamma = new CheckBox();

    RawInputListener _listen;
    KeyboardWatch _keys;

    readonly System.Collections.Generic.Dictionary<string, int> _phase1 = new System.Collections.Generic.Dictionary<string, int>();
    readonly System.Collections.Generic.Dictionary<string, int> _phase2 = new System.Collections.Generic.Dictionary<string, int>();
    readonly System.Collections.Generic.Dictionary<string, KeySignal> _signals = new System.Collections.Generic.Dictionary<string, KeySignal>();

    // Touchscreens, pens and sensors emit continuously. Whatever is already
    // chattering before the user presses anything is recorded here and then
    // ignored - without this, a Surface's digitizer wins on sheer volume and
    // gets learned as the brightness key.
    readonly System.Collections.Generic.HashSet<string> _ambient = new System.Collections.Generic.HashSet<string>();
    readonly System.Windows.Forms.Timer _calibrate = new System.Windows.Forms.Timer();
    bool _calibrating;

    int _phase;                 // 0 = darker, 1 = brighter, 2 = result
    public KeySignal Down, Up;

    public SetupForm()
    {
        Text = "BrightnessSteps - set up your keys";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(470, 286);
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.White;
        try { Icon = new Icon(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "app.ico")); } catch { }

        _step.SetBounds(24, 22, 420, 26);
        _step.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _step.ForeColor = Color.FromArgb(240, 201, 135);

        _hint.SetBounds(24, 56, 420, 74);
        _hint.Font = new Font("Segoe UI", 9.75f);
        _hint.ForeColor = Color.FromArgb(200, 208, 222);

        _status.SetBounds(24, 132, 420, 46);
        _status.Font = new Font("Consolas", 9f);
        _status.ForeColor = Color.FromArgb(150, 160, 178);

        _gamma.SetBounds(24, 176, 424, 46);
        _gamma.Text = "Also enable deeper dimming (recommended)\r\nNeeds administrator once, and a restart to take effect.";
        _gamma.Checked = true;
        _gamma.Visible = false;
        _gamma.ForeColor = Color.FromArgb(200, 208, 222);
        _gamma.Font = new Font("Segoe UI", 8.75f);

        _next.SetBounds(330, 240, 112, 30);
        _next.Text = "Continue";
        _next.Enabled = false;
        _next.FlatStyle = FlatStyle.System;
        _next.Click += (s, e) => Advance();

        _cancel.SetBounds(210, 240, 108, 30);
        _cancel.Text = "Cancel";
        _cancel.FlatStyle = FlatStyle.System;
        _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { _step, _hint, _status, _gamma, _next, _cancel });

        _calibrate.Interval = 1500;
        _calibrate.Tick += (s, e) => { _calibrate.Stop(); _calibrating = false; ShowStatus(); };

        Load += (s, e) => Begin();
        FormClosed += (s, e) => { _calibrate.Stop(); StopListening(); };
    }

    void Begin()
    {
        // Listen to every HID collection present, not just the standard one -
        // that is the whole point of asking rather than assuming.
        var collections = HidScan.Collections();
        if (collections.Length == 0) collections = new[] { new ushort[] { 0x0C, 0x01 } };

        var pageList = new System.Collections.Generic.List<ushort>();
        foreach (var c in collections) if (!pageList.Contains(c[0])) pageList.Add(c[0]);
        if (!pageList.Contains(0x0C)) pageList.Add(0x0C);

        _listen = new RawInputListener(collections, pageList.ToArray());
        _listen.UsageSeen += (page, usage) => Record(KeySignal.Hid(page, usage));

        _keys = new KeyboardWatch();
        _keys.KeyDown += vk => Record(KeySignal.Key(vk));

        ShowPhase();
    }

    void StopListening()
    {
        if (_listen != null) { _listen.Dispose(); _listen = null; }
        if (_keys != null) { _keys.Dispose(); _keys = null; }
    }

    void Record(KeySignal sig)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => RecordOnUi(sig))); } catch { }
    }

    void RecordOnUi(KeySignal sig)
    {
        if (_phase > 1) return;

        if (sig.IsHid)
        {
            if (sig.Usage == 0) return;                     // a release, not a press
            // A brightness key is never on the digitizer or sensor pages, and
            // those are exactly the ones that stream constantly.
            if (sig.Page == 0x0D || sig.Page == 0x20) return;
        }

        string key = sig.ToString();
        _signals[key] = sig;

        if (_calibrating) { _ambient.Add(key); return; }
        if (_ambient.Contains(key)) return;                 // was already chattering

        var bucket = _phase == 0 ? _phase1 : _phase2;
        int n;
        bucket[key] = bucket.TryGetValue(key, out n) ? n + 1 : 1;

        _next.Enabled = Candidates().Length > 0;
        ShowStatus();
    }

    /// <summary>Signals seen in this phase that the other phase has not claimed.</summary>
    string[] Candidates()
    {
        var mine = _phase == 0 ? _phase1 : _phase2;
        var other = _phase == 0 ? _phase2 : _phase1;

        var list = new System.Collections.Generic.List<string>();
        foreach (var kv in mine) if (!other.ContainsKey(kv.Key)) list.Add(kv.Key);
        list.Sort((a, b) =>
        {
            bool ca = _signals[a].IsHid && _signals[a].Page == 0x0C;
            bool cb = _signals[b].IsHid && _signals[b].Page == 0x0C;
            if (ca != cb) return ca ? -1 : 1;                // the standard page wins ties
            return mine[b].CompareTo(mine[a]);
        });
        return list.ToArray();
    }

    void ShowPhase()
    {
        if (_phase == 0)
        {
            _step.Text = "1 of 2 - press your DARKER key";
            _hint.Text = "Press the key you use to make the screen dimmer, three or four times.\r\n\r\n"
                       + "Wait for the prompt below, then press nothing else. If your keys need Fn, "
                       + "hold Fn as you normally would.";
            _next.Text = "Continue";
        }
        else if (_phase == 1)
        {
            _step.Text = "2 of 2 - press your BRIGHTER key";
            _hint.Text = "Now press the key you use to make the screen brighter, three or four times.\r\n\r\n"
                       + "Wait for the prompt below, then press nothing else.";
            _next.Text = "Continue";
        }
        _next.Enabled = false;

        // Listen first, press second: sample what is already streaming so it can
        // be discounted.
        _calibrating = true;
        _calibrate.Stop();
        _calibrate.Start();
        ShowStatus();
    }

    void ShowStatus()
    {
        if (_phase > 1) return;
        if (_calibrating)
        {
            _status.Text = "checking what this machine sends on its own -\r\ndon't touch the screen or keyboard for a moment...";
            return;
        }
        var c = Candidates();
        if (c.Length == 0) { _status.Text = "ready - press your key now"; return; }

        var bucket = _phase == 0 ? _phase1 : _phase2;
        _status.Text = "detected: " + _signals[c[0]].Describe() + "   (" + bucket[c[0]] + " presses)";
        if (c.Length > 1) _status.Text += "\r\n+ " + (c.Length - 1) + " other signal(s); the most frequent wins";
    }

    void Advance()
    {
        if (_phase == 0)
        {
            var c = Candidates();
            if (c.Length == 0) return;
            Down = _signals[c[0]];
            _phase = 1;
            ShowPhase();
            return;
        }

        if (_phase == 1)
        {
            var c = Candidates();
            if (c.Length == 0)
            {
                MessageBox.Show(this,
                    "No separate signal was seen for the brighter key.\r\n\r\n"
                    + "Either it emits the same thing as the darker key, or it never reaches "
                    + "Windows on this machine. BrightnessSteps cannot drive it in that case.",
                    "BrightnessSteps", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Up = _signals[c[0]];

            _phase = 2;
            StopListening();

            _step.Text = "Done";
            _hint.Text = "Darker:   " + Down.Describe() + "\r\n"
                       + "Brighter: " + Up.Describe() + "\r\n\r\n"
                       + "Saved to your profile. Choose \"Set up brightness keys\" again to redo it.";

            // Below hardware zero this can either scale the display's gamma or
            // lay a black sheet over everything. Gamma is better - invisible to
            // screenshots and to fullscreen apps - but Windows clamps it until
            // a machine-wide value is set, which needs administrator once.
            _gamma.Visible = !GammaSupport.Unlocked;
            _status.Text = Down.ToString() + "   /   " + Up.ToString();
            _next.Text = "Finish";
            _next.Enabled = true;
            _cancel.Text = "Discard";
            return;
        }

        if (_gamma.Visible && _gamma.Checked)
        {
            _next.Enabled = false;
            _step.Text = "Enabling deeper dimming...";
            Application.DoEvents();

            if (GammaSupport.RequestEnable())
                MessageBox.Show(this,
                    "Deeper dimming is enabled.\r\n\r\n"
                    + "It takes effect after a restart. Until then the darkest steps "
                    + "use a black overlay instead, which works just as well - it is "
                    + "only visible to screenshots and fullscreen apps.",
                    "BrightnessSteps", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(this,
                    "Deeper dimming was not enabled.\r\n\r\n"
                    + "The darkest steps will keep using a black overlay, which works. "
                    + "You can try again from the tray menu at any time.",
                    "BrightnessSteps", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}

