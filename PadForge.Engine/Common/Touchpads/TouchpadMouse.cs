using Microsoft.Win32;

namespace PadForge.Engine.Touchpads;

/// <summary>The Windows touchpad settings the mouse row of a touchpad
/// PadForge reads alone follows, from the user's precision touchpad key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad).
/// A value that is missing reads as Windows' default: on, and standard
/// scrolling.</summary>
public sealed record TouchpadSettings(bool Taps, bool TwoFingerTap, bool RightClickZone, bool TapAndDrag,
    bool Scroll, bool ScrollReversed)
{
    /// <summary>Every setting on, standard scrolling: Windows' defaults.</summary>
    public static readonly TouchpadSettings Default = new(true, true, true, true, true, false);

    /// <summary>The current user's settings. ScrollDirection 0 is standard
    /// ("Down motion scrolls up") and any other value reverse (Microsoft,
    /// Legacy Touchpad PC Settings Opt-In).</summary>
    public static TouchpadSettings Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad");
            if (key == null) return Default;
            bool On(string name) => key.GetValue(name) is not int value || value != 0;
            return new TouchpadSettings(On("TapsEnabled"), On("TwoFingerTapEnabled"), On("RightClickZoneEnabled"),
                On("TapAndDrag"), On("PanEnabled"), key.GetValue("ScrollDirection") is int direction && direction != 0);
        }
        catch
        {
            return Default;
        }
    }
}

/// <summary>
/// The clicks and the wheel of a touchpad PadForge reads alone, worked out
/// from its frames the way libinput does, with Windows' own tap timing as
/// measured on a precision touchpad. Its pointer motion is
/// <see cref="TouchpadPointer.FrameMotion"/>.
///
/// <para>A press of a click-pad clicks right with two fingers down
/// (libinput's clickfinger, evdev-mt-touchpad-buttons.c:1126-1180) or
/// with its finger in the bottom right area (libinput's software
/// buttons, evdev-mt-touchpad-buttons.c:599-609), middle with three
/// fingers, and left otherwise. A touch that ends within 180 ms and
/// moved under 1.3 mm is a tap (evdev-mt-touchpad-tap.c:32-36): one
/// finger clicks left, two fingers click right. Two fingers moving scroll
/// by their average motion (evdev-mt-touchpad-gestures.c:1706-1735).</para>
/// </summary>
public sealed class TouchpadMouse
{
    /// <summary>The longest touch libinput takes for a tap, and the most it
    /// may move (evdev-mt-touchpad-tap.c:32 and 36).</summary>
    public const int TapTimeoutMs = 180;
    public const double TapMoveThresholdMm = 1.3;

    /// <summary>How long Windows held the left click of a one-finger tap
    /// with tap-and-drag on, measured on a Goodix precision touchpad
    /// (216 to 236 ms over nine taps): the time a second touch has to turn
    /// the tap into a drag.</summary>
    public const int TapDragHoldMs = 230;

    /// <summary>How long Windows held the right click of a two-finger tap,
    /// measured on the same touchpad (78 ms): a click no drag can
    /// follow.</summary>
    public const int ClickHoldMs = 78;

    /// <summary>Wheel units per millimeter of two-finger travel, 120 to a
    /// wheel notch per 10 mm. Windows delivers a touchpad's scrolling to
    /// windows as messages and not as mouse input (measured: no Raw Input
    /// wheel event over thirty two-finger scrolls), so it has no scale to
    /// copy. This one reads a scroll at a moderate pace like a wheel
    /// turned at one.</summary>
    public const double WheelUnitsPerMm = 12;

    /// <summary>The mouse row's buttons: left, right and middle.</summary>
    public const int Left = 1, Right = 2, Middle = 4;

    private TouchpadSettings settings;
    private readonly double widthMm, heightMm;
    private readonly Dictionary<int, (double X, double Y)> start = new();
    private Dictionary<int, (double X, double Y)> last = new();
    private bool touching, sessionPressed, sessionMoved, dragging;
    private long sessionStart;
    private int sessionFingers;
    private int pressButton;
    private int tapButton;
    private long tapUntil;
    private bool tapMayDrag;

    public TouchpadMouse(TouchpadSettings settings, double widthMm, double heightMm)
    {
        this.settings = settings ?? TouchpadSettings.Default;
        this.widthMm = widthMm;
        this.heightMm = heightMm;
    }

    /// <summary>The Windows touchpad settings as they read now. A touch under
    /// way ends by them.</summary>
    public void UseSettings(TouchpadSettings settings) => this.settings = settings ?? TouchpadSettings.Default;

    /// <summary>The wheel the frames left since the last call to
    /// <see cref="TakeWheel"/>, in wheel units, 120 to a notch.</summary>
    public double Wheel { get; private set; }

    /// <summary>One committed frame: the contacts down, by contact ID, in
    /// millimeters from the top left, and whether the click-pad's own
    /// button is pressed.</summary>
    public void Frame(ReadOnlySpan<(int Id, double X, double Y)> contacts, bool pressed, long nowMs)
    {
        if (contacts.Length > 0 && !touching)
        {
            touching = true;
            sessionStart = nowMs;
            sessionFingers = 0;
            sessionPressed = false;
            sessionMoved = false;
            start.Clear();
            // A touch that begins while a tap's left click is held drags
            // with it (Windows' tap-and-drag).
            dragging = tapMayDrag && tapButton == Left && nowMs < tapUntil;
        }

        // Each finger's travel from where it landed.
        foreach (var c in contacts)
        {
            if (!start.TryGetValue(c.Id, out var at)) start[c.Id] = (c.X, c.Y);
            else if (Math.Sqrt((c.X - at.X) * (c.X - at.X) + (c.Y - at.Y) * (c.Y - at.Y)) >= TapMoveThresholdMm)
                sessionMoved = true;
        }
        sessionFingers = Math.Max(sessionFingers, contacts.Length);

        // The click-pad's press: which button it is, read as it goes down.
        if (pressed && pressButton == 0)
        {
            sessionPressed = true;
            pressButton = PressButton(contacts);
        }
        else if (!pressed)
        {
            pressButton = 0;
        }

        // Two fingers moving, not pressed: the wheel, by their average
        // motion. Down is wheel up under standard scrolling.
        if (settings.Scroll && contacts.Length == 2 && !pressed)
        {
            double dy = 0;
            int moving = 0;
            foreach (var c in contacts)
            {
                if (!last.TryGetValue(c.Id, out var before)) continue;
                dy += c.Y - before.Y;
                moving++;
            }
            if (moving == 2) Wheel += dy / 2 * WheelUnitsPerMm * (settings.ScrollReversed ? -1 : 1);
        }

        var now = new Dictionary<int, (double X, double Y)>(contacts.Length);
        foreach (var c in contacts) now[c.Id] = (c.X, c.Y);
        last = now;

        if (contacts.Length == 0 && touching) End(nowMs);
    }

    /// <summary>Ends a touch the touchpad stopped reporting, as a lift at
    /// <paramref name="liftMs"/>.</summary>
    public void Silence(long liftMs)
    {
        pressButton = 0;
        if (!touching) return;
        last = new Dictionary<int, (double X, double Y)>();
        End(liftMs);
    }

    private void End(long nowMs)
    {
        touching = false;
        bool wasDragging = dragging;
        dragging = false;
        bool tap = !sessionPressed && !sessionMoved && nowMs - sessionStart <= TapTimeoutMs;
        if (wasDragging)
        {
            // The drag's button lifts with the touch. A second tap is a
            // double click: its click follows the first one's release.
            tapButton = 0;
            tapUntil = 0;
            if (tap && sessionFingers == 1 && settings.Taps) Click(Left, nowMs, ClickHoldMs, mayDrag: false);
            return;
        }
        if (!tap) return;
        if (sessionFingers == 1 && settings.Taps)
            Click(Left, nowMs, settings.TapAndDrag ? TapDragHoldMs : ClickHoldMs, settings.TapAndDrag);
        else if (sessionFingers == 2 && settings.TwoFingerTap)
            Click(Right, nowMs, ClickHoldMs, mayDrag: false);
    }

    private void Click(int button, long nowMs, int holdMs, bool mayDrag)
    {
        tapButton = button;
        tapUntil = nowMs + holdMs;
        tapMayDrag = mayDrag;
    }

    /// <summary>The buttons down now: a press's, a tap's while its click is
    /// held, and a drag's left while it lasts.</summary>
    public int Buttons(long nowMs)
    {
        int buttons = pressButton;
        if (dragging) buttons |= Left;
        else if (tapButton != 0 && nowMs < tapUntil) buttons |= tapButton;
        return buttons;
    }

    /// <summary>The whole wheel units gathered, the fraction kept for the
    /// next call.</summary>
    public int TakeWheel()
    {
        int whole = (int)Wheel;
        Wheel -= whole;
        return whole;
    }

    /// <summary>The button a press is: right with two fingers together,
    /// middle with three, right with one finger in the bottom right area
    /// while Windows' right-click zone is on, left otherwise.</summary>
    private int PressButton(ReadOnlySpan<(int Id, double X, double Y)> contacts)
    {
        int fingers = contacts.Length;
        if (fingers == 2 && !Together(contacts[0], contacts[1])) fingers = 1;
        if (fingers == 2) return Right;
        if (fingers >= 3) return Middle;
        if (fingers == 1 && settings.RightClickZone && InRightButtonArea(contacts[0].X, contacts[0].Y)) return Right;
        return Left;
    }

    /// <summary>Whether two fingers are one two-finger click: within 40 mm
    /// across and 30 mm down, and, past 20 mm down on a touchpad 50 mm tall
    /// or more, not one in the bottom 20 mm and the other above it, which
    /// is a thumb resting (evdev-mt-touchpad-buttons.c:1071-1123).</summary>
    internal bool Together((int Id, double X, double Y) a, (int Id, double X, double Y) b)
    {
        double x = Math.Abs(a.X - b.X), y = Math.Abs(a.Y - b.Y);
        if (x > 40 || y > 30) return false;
        if (y <= 20 || heightMm < 50) return true;
        double bottom = heightMm - 20;
        return (a.Y > bottom) == (b.Y > bottom);
    }

    /// <summary>libinput's right software button: the bottom 10 mm or 15
    /// percent of the height, whichever is smaller, right of the middle
    /// (evdev-mt-touchpad-buttons.c:599-609).</summary>
    internal bool InRightButtonArea(double xMm, double yMm)
    {
        double top = heightMm * 0.15 > 10 ? heightMm - 10 : heightMm * 0.85;
        return yMm >= top && xMm > widthMm * 0.5;
    }
}
