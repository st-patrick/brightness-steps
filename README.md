# BrightnessSteps

Finer brightness steps for the keyboard brightness keys on a Surface Book 3.

Windows moves brightness in jumps of 10, so the bottom of the range is unusable:
you can have 0 or 10, never 5. This replaces each key press with a step along a
hand-tuned ladder, and adds a few rungs *below* hardware zero.

## The ladder

26 rungs, darkest to brightest:

| region | rungs |
| --- | --- |
| below hardware zero (black overlay) | black (fully dark) / dim 91% / 80% / 67% / 47% / 24% |
| hardware brightness | `0 1 2 3 4 5 6 8 10 13 16 20 25 30 37 45 55 67 82 100` |

The darkest rung is a fully opaque black sheet — the screen goes completely
dark. Brightness-up still works from there, the level popup draws *above* the
sheet so there is always something visible, and the sheet belongs to this
process, so killing it (or Ctrl+Alt+Del, which switches to the secure desktop)
always gets the screen back.

1-point steps up to 6, then gradually wider, so every press is about the same
*relative* change rather than the same absolute one.

To change it, edit the `Ladder` array in `BrightnessSteps.cs` and run `build.ps1`.

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
loop polls the backlight every millisecond for 220 ms afterwards and puts the
value straight back.

External brightness changes with no preceding key press — the slider, battery
saver, adaptive brightness — are followed rather than fought, and clear the
overlay. The guard window is excluded from that, since during it we are
deliberately overriding Windows.

## Why it goes through the display driver, not WMI

Windows' step cannot be blocked, so it is always briefly visible. The only thing
left to control is *how long*. Measured on this machine:

| operation | cost |
| --- | --- |
| `WmiSetBrightness` (the obvious API) | 10.1 ms median |
| `IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS` | **0.16 ms** median |

So brightness is read and written straight through the monitor device
(`\\?\display#...`, opened unelevated), which is the layer underneath WMI. That
is what makes a 1 ms poll loop affordable, and it cuts the window during which
Windows' wrong value is on screen from ~10 ms to **1.8 ms measured**. WMI is
kept only as a fallback if the device cannot be opened.

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
