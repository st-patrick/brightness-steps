<p align="center">
  <img src="docs/icon.png" width="96" height="96" alt="">
</p>

<h1 align="center">BrightnessSteps</h1>

<p align="center">
  <b>Your laptop's brightness keys, with usable steps at the dark end.</b><br>
  A single small tray app for Windows. No admin, no background service, no telemetry.
</p>

---

Windows moves screen brightness in jumps of 10. At the bottom of the range that
is the difference between "too dark to read" and "too bright for a dark room",
with nothing in between:

```
Windows      0 ─────────── 10 ─────────── 20 ─────────── 30 ...
             ^ nothing usable lives in here ^

BrightnessSteps
             0  1  2  3  4  5  6  8  10  13  16  20  25  30  37 ...
             ^ 1-point steps where your eyes need them ^
```

It also goes **below** hardware zero. Most laptop panels stop at a backlight
level that is still too bright in a properly dark room, so the last few steps
dim the screen further, down to fully black.

## Install

Download the installer from [Releases](../../releases), or grab
`BrightnessSteps.exe` and run it — it is a single self-contained executable.

- Installs per user, so there is **no UAC prompt**
- Needs .NET Framework 4.x, which ships with Windows 8 and later
- Optional "start when I sign in", toggleable later from the tray menu

Building from source needs nothing but Windows: `build.ps1` compiles with the
`csc.exe` already in `C:\Windows\Microsoft.NET`.

## Will it work on my laptop?

Probably, if it is a Windows laptop with brightness keys. Nothing in it is
specific to a vendor, model or keyboard layout — keys are matched on the
standard HID Consumer Page usages `0x6F`/`0x70` (Display Brightness
Increment/Decrement), decoded through the device's own HID report descriptor,
not by scancodes, key positions, or the `Fn` state.

Two things have to be true, and the app tells you if they are not:

| requirement | if it is missing |
| --- | --- |
| A built-in panel that accepts brightness commands | Tray icon says so on startup |
| Brightness keys that reach Windows as HID consumer usages | Keys do nothing; the ladder still works from the tray menu |

**It will not work with external monitors.** Those need DDC/CI over the video
cable, which is a different mechanism entirely; this drives built-in laptop
panels through the display driver.

If it does not work for you, right-click the tray icon and choose **Copy
compatibility report**. That collects your Windows version, machine model,
which brightness method was found, and whether any keys were seen — nothing
else, nothing personal — puts it on the clipboard and opens it so you can read
it before sending. Paste it into an
[issue](../../issues). Nothing is ever transmitted on its own.

Developed and measured on a Surface Book 3. Reports from other machines are
the most useful thing you can contribute.

## The ladder

26 rungs, darkest to brightest:

| region | rungs |
| --- | --- |
| below hardware zero (black overlay) | black (fully dark) / dim 91% / 80% / 67% / 47% / 24% |
| hardware brightness | `0 1 2 3 4 5 6 8 10 13 16 20 25 30 37 45 55 67 82 100` |

1-point steps up to 6, then gradually wider, so every press is about the same
*relative* change rather than the same absolute one. To change it, edit the
`Ladder` array in `BrightnessSteps.cs` and run `build.ps1`.

The darkest rung is a fully opaque black sheet — the screen goes completely
dark. Brightness-up still works from there, the level popup draws *above* the
sheet so there is always something visible, and the sheet belongs to this
process, so killing it (or Ctrl+Alt+Del, which switches to the secure desktop)
always gets the screen back.

---

# How it works, and what it cost to get there

The rest of this file is the engineering log: what was measured, what was
tried and rejected, and why the code looks the way it does. It is here because
almost every interesting decision in this app was forced by a measurement that
contradicted the obvious approach.

## How it works

Three things were measured on this machine before the design was fixed:

1. **The brightness keys are invisible to a low-level keyboard hook.** Firmware /
   Windows handles them below user mode, so they cannot be swallowed and
   replaced the usual way.
2. **They *are* visible to raw input**, as HID consumer-control reports — usage
   `0x6F` (up) and `0x70` (down) — arriving about 20 ms *before* Windows applies
   its own ±10 step. So the direction of every press is known unambiguously,
   which is what separates a key press from someone dragging the slider.
3. **Gamma-ramp dimming is clamped.** `SetDeviceGammaRamp` is refused outright
   (Windows restricts ramps unless `HKLM\...\ICM\GdiIcmGammaRange` is set, which
   needs admin plus a reboot). So "darker than hardware zero" uses a
   click-through black layered window instead — no admin, and it disappears with
   the process, so a crash can never leave the screen black.

Given that, each press works as: apply our rung the instant the HID report
arrives, then *hold* it. Windows still stomps on it ~20 ms later, so a guard
loop watches the backlight for 220 ms afterwards and puts the value straight
back (see the driver section below for how often it is safe to do that).

External brightness changes with no preceding key press — the slider, battery
saver, adaptive brightness — are followed rather than fought, and clear the
overlay. The guard window is excluded from that, since during it we are
deliberately overriding Windows.

**The brightness event's value is deliberately ignored.** It reports brightness
as it was at the moment of the change, and delivery can lag by hundreds of
milliseconds (224 ms observed) — long enough for the guard to have already put
the value back. Acting on that stale number resynced the ladder to Windows'
stomp and cleared the overlay, which in the dim zone shows up as a jump to the
`0` rung and back. The handler now treats the event purely as a nudge that
something changed and re-reads the hardware, which is cheap. It compares against
the current rung's own hardware value rather than a recency window, too: with a
2.5 s echo window, sitting in the dim zone longer than that let any unrelated
brightness event clear the overlay out from under it.

## Why it goes through the display driver, not WMI

Windows' step cannot be blocked, so it is always briefly visible. The only thing
left to control is *how long*. Measured on this machine:

| operation | cost |
| --- | --- |
| `WmiSetBrightness` (the obvious API) | 10.1 ms median |
| `IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS` | **0.16 ms** median |

So brightness is read and written straight through the monitor device
(`\\?\display#...`, opened unelevated), which is the layer underneath WMI. WMI
is kept only as a fallback if the device cannot be opened.

That makes tight polling affordable. Measured, with the stomp coming from an
independent device handle so it stands in for Windows rather than queueing
behind the guard's own lock:

| guard strategy | median | max |
| --- | --- | --- |
| `Sleep(1)` polling | 2.04 ms | 2.55 ms |
| **spin polling** | **0.27 ms** | 0.44 ms |
| WMI event driven | 3.29 ms | 224 ms |

So the guard spins for the first 70 ms after a press — the window Windows' stomp
actually lands in — then drops to `Sleep(1)` for the tail. The event-driven
option looks tempting and is the worst of the three: it is slower in the median
and occasionally hundreds of milliseconds late.

Two consequences worth knowing:

- The guard owns a **separate device handle** from the rest of the app. While
  spinning it takes that handle's lock every fraction of a millisecond, and
  sharing one handle would stall the UI thread's own writes behind the spin.
  (Measuring this wrong — with a shared lock — made spinning look *worse* than
  sleeping, which it is not.)
- Spinning costs one core for up to 70 ms per press, and a held key re-arms it,
  so it gives up after 3 s of continuous spinning. Idle cost is still zero: the
  guard thread blocks on an event between presses.

## Can Windows' own handling just be turned off?

No, not from user mode. `RegisterRawInputDevices` has flags for exactly this —
`RIDEV_NOHOTKEYS` and `RIDEV_NOLEGACY` — and they are accepted for the keyboard
collection but rejected for the consumer-control collection the brightness keys
actually arrive on:

| collection | `NOHOTKEYS` | `NOLEGACY \| NOHOTKEYS` |
| --- | --- | --- |
| consumer control `0C/01` (brightness keys) | rejected, `ERROR_INVALID_FLAGS` | rejected, `ERROR_INVALID_FLAGS` |
| keyboard `01/06` | accepted | accepted |

Disabling the HID device would stop Windows acting on the keys, but it would
stop us seeing them too. Short of a HID filter driver, the step can only be
raced, never prevented — so what is left is arming the race as early and as
reliably as possible:

- The guard is armed **before** any other work in a key press. It only needs the
  target number, so the overlay, DWM and the popup must not sit in front of it.
  Arming late is what let the occasional stomp slip through.
- The guard thread runs at `ThreadPriority.Highest`. It is blocked between
  presses, but must be scheduled *promptly* when woken.
- Raw input is pumped on **its own thread**, not the UI thread. `WM_INPUT`
  behind the overlay's 10 ms animation ticks and the popup's painting is late
  input, and those are busiest exactly when keys repeat in the dim region.
- The spin ceiling is a stuck-key backstop only. At its original 3 s, a normal
  hold from full brightness down to black ran past it: the guard loop never
  exits while repeats keep extending it, so `startedAt` stayed fixed and the
  guard quietly dropped to ~2 ms latency **mid-hold**. That is why the flicker
  came back specifically when holding or pressing fast, and specifically near
  zero where 2 ms is enough to see.

## The driver drops corrections, and the guard must not flood it

The single biggest cause of flicker under fast key presses turned out to be
self-inflicted. Measured on this panel:

- A brightness change takes **3-14 ms to settle**.
- A write issued while an earlier change is still settling is **often dropped
  outright** — a single correction sticks only about 5-8 times out of 8,
  regardless of how soon after the foreign write it is issued. There is no
  timing window that reliably wins, so the guard has to re-issue until a read
  confirms the value.
- Re-issuing on *every* poll, which is what the guard originally did, fires
  ~140,000 writes per second. That keeps the driver permanently busy, so the
  correction never lands at all and Windows' steps pile up on top of each other:
  `guard saw 92, restoring 82` repeating every 7 microseconds, forever.

So the guard now paces its reads (~100 µs) and rate-limits its writes (~3.3 ms,
measured flat against 1.4 ms and better than 5.9 ms). Under `--selftest`, worst
drift at 35 ms between presses went from **33 points to 10**; at 120 ms it is 0.

The remaining 10 points is one uncorrected step still settling at the moment of
sampling, and it is a property of the display driver rather than of this code.

## Would running as administrator help? No — measured, not assumed

Every limit here is an API restriction or driver behaviour, not an access
check. Tested elevated and unelevated:

| lever | result |
| --- | --- |
| `RegisterRawInputDevices` with `NOHOTKEYS` on `0C/01` | `ERROR_INVALID_FLAGS` either way — flag validation, not privilege |
| Exclusive `CreateFile` on the consumer-control HID collections | `ERROR_SHARING_VIOLATION` (32) either way, on all four collections |
| Stopping `DisplayEnhancementService` (needs admin) | Windows **still** stepped brightness: 151 key presses, 48 changes |

The HID result is the informative one. The failure is a *sharing violation*, not
`ACCESS_DENIED` — the kernel input stack already holds those collections open,
and sharing is enforced by the object manager against existing handles. No
privilege overrides that. Elevated output was byte-identical to unelevated.

Stopping the display service did change Windows' behaviour — its stepping
degraded to toggling between 0 and 13 — but it did not stop it, and it is not
where the hotkey is handled.

So the app deliberately does **not** request elevation: it would cost a UAC
prompt on every boot and buy nothing. The only remaining way to truly block the
key is a kernel-mode HID filter driver, which needs test-signing or an EV
certificate — far past what this is worth.

### The one admin-gated change worth making: lifting the gamma clamp

By default Windows only permits mild gamma dimming. Measured on this machine,
before any change:

| requested | result |
| --- | --- |
| 60 % | applied |
| 30 %, 10 %, 2 %, 0 % | `SetDeviceGammaRamp` returns false, ramp ignored |

That is why below-zero dimming uses a layered black window. Setting
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM\GdiIcmGammaRange = 256`
(admin, then a reboot) lifts the clamp, which would allow gamma dimming instead
— nothing in screenshots or screen shares, nothing sitting on top of fullscreen
apps, and none of the window-management bugs the overlay caused.

It would **not** reduce flicker, which is about backlight writes, and gamma can
be reset by other colour-management software, so the overlay stays as the
fallback either way.

**This key has been set on this machine.** To undo it, run elevated:

```powershell
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM' `
                    -Name GdiIcmGammaRange
```

then reboot. That restores the Windows default; nothing else depends on it.

## Testing without a keyboard

Raw HID reports cannot be injected, so `BrightnessSteps.exe --selftest` drives
the app's real key path and plays Windows' part on a second device handle,
adding ±10 about 20 ms after each press. It sweeps the ladder in both directions
at several press intervals and reports the worst drift between the hardware and
the rung that should be showing. It skips the singleton, so stop the running
instance first. `Trace` records presses, corrections and driver fallbacks to
`selftest-trace.txt`; it is off unless the self-test turns it on.

## Why the extremes flickered when the middle did not

A 10-point step is a ~10 % change at the top of the range and a ~10x change at
the bottom, so the same 2 ms is invisible mid-range and obvious near zero. Two
separate causes turned up there:

- **Stepping up out of the dim region.** Hardware sits at 0, Windows' step takes
  it to 10, and against a near-black screen that is a huge relative jump. Fixed
  by the spin guard above.
- **Stepping down into the dim region.** Hardware is already 0 and Windows'
  step is a no-op, so this one was entirely the overlay: the window was created
  lazily on its first `Show()`, and the layered alpha got applied a frame *after*
  the window first appeared — one frame of fully opaque black. The overlay
  window is now built up front, and alpha is always set before it is revealed.

One pleasant side effect of acting *before* Windows: Windows computes its step
from whatever brightness it finds, which by then is already our new rung. So a
press downward makes it aim 10 lower still and clamp toward 0 — the residual
blip goes *darker* rather than flashing bright, which is much easier on the eyes
at night. Presses upward still blip bright.

Below hardware zero there is no flicker at all going down: hardware is already
pinned at 0, Windows' step cannot go lower, and only the overlay changes.

## Tray menu

- **Start with Windows** — adds/removes the `HKCU\...\Run` entry
- **Show level popup** — the small readout of the true level (Windows' own popup
  shows the value it tried to set, which is not the one that ends up applied)
- **Darker / Brighter** — step the ladder without the keys
- **Exit**

## Files

| file | purpose |
| --- | --- |
| `BrightnessSteps.cs` | the app |
| `build.ps1` | rebuild + restart |
| `keylogger-probe.cs` | throwaway diagnostic that established the three findings above; not needed to run the app |
