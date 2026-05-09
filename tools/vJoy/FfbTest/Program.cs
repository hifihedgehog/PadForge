using System.Runtime.InteropServices;
using SharpDX.DirectInput;

namespace FfbTest;

class Program
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    static readonly IntPtr HWND_MESSAGE = new(-3);

    static IntPtr CreateMessageWindow()
    {
        // Message-only window — invisible, no taskbar entry, valid for DirectInput.
        var hwnd = CreateWindowExW(0, "Static", "FfbTest", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return hwnd;
    }

    static void Main()
    {
        Console.WriteLine("vJoy Force Feedback Test Tool");
        Console.WriteLine("=============================\n");

        using var di = new DirectInput();

        var allDevices = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AllDevices);
        var ffbDevices = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.ForceFeedback);

        if (allDevices.Count == 0)
        {
            Console.WriteLine("No game controllers found.");
            return;
        }

        Console.WriteLine($"Game controllers: {allDevices.Count} total, {ffbDevices.Count} with FFB\n");

        Console.WriteLine("All Devices:");
        for (int i = 0; i < allDevices.Count; i++)
        {
            var d = allDevices[i];
            bool hasFfb = ffbDevices.Any(f => f.InstanceGuid == d.InstanceGuid);
            string ffbTag = hasFfb ? " [FFB]" : "";
            Console.WriteLine($"  [{i}] {d.InstanceName}{ffbTag}");
        }
        Console.WriteLine();

        if (ffbDevices.Count == 0)
        {
            Console.WriteLine("No FFB-capable devices found.");
            Console.WriteLine("Check that:");
            Console.WriteLine("  1. vJoy uses PID_BEAD hardware ID (not PID_0FFB)");
            Console.WriteLine("  2. Device node was recreated after PID change");
            Console.WriteLine("  3. PadForge has created at least one vJoy controller");
            return;
        }

        // Select FFB device.
        int selection = 0;
        if (ffbDevices.Count > 1)
        {
            Console.Write("Select device number: ");
            if (!int.TryParse(Console.ReadLine(), out selection) || selection < 0 || selection >= allDevices.Count)
                selection = 0;

            var selectedGuid = allDevices[selection].InstanceGuid;
            int ffbIndex = -1;
            for (int i = 0; i < ffbDevices.Count; i++)
                if (ffbDevices[i].InstanceGuid == selectedGuid)
                { ffbIndex = i; break; }

            if (ffbIndex < 0)
            {
                Console.WriteLine("Selected device does not support FFB.");
                return;
            }
            selection = ffbIndex;
        }

        var target = ffbDevices[selection];
        Console.WriteLine($"Using: {target.InstanceName}\n");

        using var joystick = new Joystick(di, target.InstanceGuid);
        var hwnd = CreateMessageWindow();
        joystick.SetCooperativeLevel(hwnd,
            CooperativeLevel.Exclusive | CooperativeLevel.Background);

        var objects = joystick.GetObjects();
        Console.WriteLine("Device objects:");
        foreach (var obj in objects)
        {
            string flags = obj.ObjectId.Flags.ToString();
            Console.WriteLine($"  {obj.Name,-24} Offset={obj.Offset,4}  Type={flags}");
        }
        Console.WriteLine();

        joystick.Acquire();
        Console.WriteLine("Device acquired.\n");

        try { joystick.Properties.AutoCenter = false; Console.WriteLine("Auto-center disabled."); }
        catch { Console.WriteLine("Could not disable auto-center (non-fatal)."); }

        var supportedEffects = joystick.GetEffects();
        Console.WriteLine("\nSupported FFB effects:");
        foreach (var e in supportedEffects)
            Console.WriteLine($"  - {e.Name}");
        Console.WriteLine();

        // Find FFB actuator axes, fall back to regular axes.
        var axisObjects = objects
            .Where(o => o.ObjectId.Flags.HasFlag(DeviceObjectTypeFlags.ForceFeedbackActuator))
            .ToList();

        if (axisObjects.Count > 0)
        {
            Console.WriteLine("FFB actuator axes:");
            foreach (var ax in axisObjects)
                Console.WriteLine($"  {ax.Name} (offset {ax.Offset})");
        }
        else
        {
            Console.WriteLine("No dedicated FFB actuator axes found — using regular axes.");
            axisObjects = objects
                .Where(o => o.ObjectId.Flags.HasFlag(DeviceObjectTypeFlags.AbsoluteAxis))
                .ToList();
            if (axisObjects.Count == 0)
            {
                Console.WriteLine("No axes found at all.");
                joystick.Unacquire();
                if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
                return;
            }
            foreach (var ax in axisObjects.Take(2))
                Console.WriteLine($"  {ax.Name} (offset {ax.Offset})");
        }
        Console.WriteLine();

        int[] axisOffsets = axisObjects.Select(a => a.Offset).Take(2).ToArray();
        int[] directions = new int[axisOffsets.Length];

        // Exhaustive effect creation probing — DsHidMini's PID driver may be
        // picky about specific parameter combinations.
        Effect? constantEffect = null;
        Effect? sineEffect = null;

        var flagCombos = new (string name, EffectFlags flags, int[] axes, int[] dirs)[]
        {
            ("2ax Polar", EffectFlags.Polar | EffectFlags.ObjectOffsets, axisOffsets, new[] { 0, 0 }),
            ("2ax Cart",  EffectFlags.Cartesian | EffectFlags.ObjectOffsets, axisOffsets, directions),
            ("1ax Cart",  EffectFlags.Cartesian | EffectFlags.ObjectOffsets, new[] { axisOffsets[0] }, new[] { 0 }),
            ("1ax NoDir", EffectFlags.ObjectOffsets, new[] { axisOffsets[0] }, new[] { 0 }),
            ("2ax Spher", EffectFlags.Spherical | EffectFlags.ObjectOffsets, axisOffsets, new[] { 0, 0 }),
        };

        var durations = new (string name, int val)[] { ("inf", -1), ("1s", 1_000_000), ("5s", 5_000_000) };
        var gains = new (string name, int val)[] { ("g10000", 10000), ("g5000", 5000), ("g0", 0) };

        Console.WriteLine("\n--- Probing constant force effect creation ---");
        foreach (var (fn, flags, axes, dirs) in flagCombos)
        {
            foreach (var (dn, dur) in durations)
            {
                foreach (var (gn, gain) in gains)
                {
                    string tag = $"{fn} {dn} {gn}";
                    try
                    {
                        var ep = new EffectParameters
                        {
                            Flags = flags,
                            Duration = dur,
                            Gain = gain,
                            SamplePeriod = 0,
                            StartDelay = 0,
                            TriggerButton = -1,
                            TriggerRepeatInterval = 0,
                            Axes = axes,
                            Directions = dirs,
                            Parameters = new ConstantForce { Magnitude = 5000 }
                        };
                        constantEffect = new Effect(joystick, EffectGuid.ConstantForce, ep);
                        Console.WriteLine($"  SUCCESS: {tag}");
                        goto ConstDone;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  FAIL: {tag} — {ex.Message}");
                    }
                }
            }
        }
        Console.WriteLine("All constant force probes failed.");
        ConstDone:

        Console.WriteLine("\n--- Probing sine effect creation ---");
        foreach (var (fn, flags, axes, dirs) in flagCombos)
        {
            foreach (var (dn, dur) in durations)
            {
                string tag = $"{fn} {dn}";
                try
                {
                    var ep = new EffectParameters
                    {
                        Flags = flags,
                        Duration = dur,
                        Gain = 10000,
                        SamplePeriod = 0,
                        StartDelay = 0,
                        TriggerButton = -1,
                        TriggerRepeatInterval = 0,
                        Axes = axes,
                        Directions = dirs,
                        Parameters = new PeriodicForce
                        {
                            Magnitude = 5000,
                            Offset = 0,
                            Phase = 0,
                            Period = 200_000
                        }
                    };
                    sineEffect = new Effect(joystick, EffectGuid.Sine, ep);
                    Console.WriteLine($"  SUCCESS: {tag}");
                    goto SineDone;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  FAIL: {tag} - {ex.Message}");
                }
            }
        }
        Console.WriteLine("All sine probes failed.");
        SineDone:

        // Reuse the flags/axes/directions from the sine probe success for
        // the rest. Periodics share the PeriodicForce parameter shape, so
        // the same parameter set works for Square/Triangle/Saw{Up,Down}.
        // Conditions use ConditionSet (per-axis Coefficients/Saturation/etc.).
        // Ramp uses RampForce (Start/End magnitudes).
        Effect? squareEffect   = TryCreatePeriodic(joystick, EffectGuid.Square,        "Square",   axisOffsets, 5000, 200_000);
        Effect? triangleEffect = TryCreatePeriodic(joystick, EffectGuid.Triangle,      "Triangle", axisOffsets, 5000, 200_000);
        Effect? sawUpEffect    = TryCreatePeriodic(joystick, EffectGuid.SawtoothUp,    "SawUp",    axisOffsets, 5000, 200_000);
        Effect? sawDownEffect  = TryCreatePeriodic(joystick, EffectGuid.SawtoothDown,  "SawDown",  axisOffsets, 5000, 200_000);

        // Conditions push back when the stick moves (spring) or opposes
        // velocity (damper / friction / inertia). Coefficient 5000 = 50 %
        // of max return force.
        Effect? springEffect   = TryCreateCondition(joystick, EffectGuid.Spring,   "Spring",   axisOffsets, 5000);
        Effect? damperEffect   = TryCreateCondition(joystick, EffectGuid.Damper,   "Damper",   axisOffsets, 5000);
        Effect? inertiaEffect  = TryCreateCondition(joystick, EffectGuid.Inertia,  "Inertia",  axisOffsets, 5000);
        Effect? frictionEffect = TryCreateCondition(joystick, EffectGuid.Friction, "Friction", axisOffsets, 5000);

        // Ramp climbs from 0 to 10000 over the effect's 1-second duration.
        Effect? rampEffect = TryCreateRamp(joystick, axisOffsets, 0, 10000, 1_000_000);

        // Direction: Polar angle in hundredths of degrees.
        // DirectInput convention: direction = where force COMES FROM.
        // 9000 = from East = pushes stick left, 27000 = from West = pushes stick right.
        int currentDirection = 0; // default: pushes stick south (force from north)

        Console.WriteLine("\nCommands:");
        Console.WriteLine("  [1] Constant - light   (2500)");
        Console.WriteLine("  [2] Constant - medium  (5000)");
        Console.WriteLine("  [3] Constant - strong  (10000)");
        Console.WriteLine("  [4] Sine wave - gentle  (3000, 300ms)");
        Console.WriteLine("  [5] Sine wave - intense (8000, 100ms)");
        Console.WriteLine("  [6] Square   (5000, 200ms)");
        Console.WriteLine("  [7] Triangle (5000, 200ms)");
        Console.WriteLine("  [8] Sawtooth Up   (5000, 200ms)");
        Console.WriteLine("  [9] Sawtooth Down (5000, 200ms)");
        Console.WriteLine("  [A] Spring   (coeff 5000)");
        Console.WriteLine("  [S] Damper   (coeff 5000)");
        Console.WriteLine("  [D] Inertia  (coeff 5000)");
        Console.WriteLine("  [F] Friction (coeff 5000)");
        Console.WriteLine("  [P] Ramp (0 -> 10000 over 1s)");
        Console.WriteLine("  [L] Direction: Push left   (from East, 90 deg)");
        Console.WriteLine("  [R] Direction: Push right  (from West, 270 deg)");
        Console.WriteLine("  [B] Direction: Push south  (from North, 0 deg)");
        Console.WriteLine("  [0] Stop all");
        Console.WriteLine("  [Q] Quit\n");

        // Every effect we created lands in this list so direction switches,
        // a-key dispatches, and final cleanup don't drift out of sync as
        // we add more shapes. StopAll iterates this list.
        var allEffects = new List<Effect?>
        {
            constantEffect, sineEffect,
            squareEffect, triangleEffect, sawUpEffect, sawDownEffect,
            springEffect, damperEffect, inertiaEffect, frictionEffect,
            rampEffect,
        };

        while (true)
        {
            Console.Write("> ");
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Q) break;

            try
            {
                switch (char.ToLower(key.KeyChar))
                {
                    case '1':
                        StopAll(allEffects);
                        SetConstantForce(constantEffect, 2500, currentDirection, axisOffsets);
                        Console.WriteLine($"Constant: light (2500) dir={currentDirection}");
                        break;
                    case '2':
                        StopAll(allEffects);
                        SetConstantForce(constantEffect, 5000, currentDirection, axisOffsets);
                        Console.WriteLine($"Constant: medium (5000) dir={currentDirection}");
                        break;
                    case '3':
                        StopAll(allEffects);
                        SetConstantForce(constantEffect, 10000, currentDirection, axisOffsets);
                        Console.WriteLine($"Constant: strong (10000) dir={currentDirection}");
                        break;
                    case '4':
                        StopAll(allEffects);
                        SetPeriodic(sineEffect, 3000, 300_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Sine: gentle (3000, 300ms) dir={currentDirection}");
                        break;
                    case '5':
                        StopAll(allEffects);
                        SetPeriodic(sineEffect, 8000, 100_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Sine: intense (8000, 100ms) dir={currentDirection}");
                        break;
                    case '6':
                        StopAll(allEffects);
                        SetPeriodic(squareEffect, 5000, 200_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Square (5000, 200ms) dir={currentDirection}");
                        break;
                    case '7':
                        StopAll(allEffects);
                        SetPeriodic(triangleEffect, 5000, 200_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Triangle (5000, 200ms) dir={currentDirection}");
                        break;
                    case '8':
                        StopAll(allEffects);
                        SetPeriodic(sawUpEffect, 5000, 200_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Sawtooth Up (5000, 200ms) dir={currentDirection}");
                        break;
                    case '9':
                        StopAll(allEffects);
                        SetPeriodic(sawDownEffect, 5000, 200_000, currentDirection, axisOffsets);
                        Console.WriteLine($"Sawtooth Down (5000, 200ms) dir={currentDirection}");
                        break;
                    case 'a':
                        StopAll(allEffects);
                        SetCondition(springEffect, 5000, axisOffsets);
                        Console.WriteLine("Spring (coeff 5000) - move stick to feel it pull back");
                        break;
                    case 's':
                        StopAll(allEffects);
                        SetCondition(damperEffect, 5000, axisOffsets);
                        Console.WriteLine("Damper (coeff 5000) - oppose stick velocity");
                        break;
                    case 'd':
                        StopAll(allEffects);
                        SetCondition(inertiaEffect, 5000, axisOffsets);
                        Console.WriteLine("Inertia (coeff 5000) - oppose acceleration");
                        break;
                    case 'f':
                        StopAll(allEffects);
                        SetCondition(frictionEffect, 5000, axisOffsets);
                        Console.WriteLine("Friction (coeff 5000) - velocity-independent drag");
                        break;
                    case 'p':
                        StopAll(allEffects);
                        SetRamp(rampEffect, 0, 10000, currentDirection, axisOffsets);
                        Console.WriteLine($"Ramp (0 -> 10000, 1s) dir={currentDirection}");
                        break;
                    case 'l':
                        currentDirection = 9000;
                        Console.WriteLine("Direction: Push LEFT (from East, 9000 = 90 deg)");
                        break;
                    case 'r':
                        currentDirection = 27000;
                        Console.WriteLine("Direction: Push RIGHT (from West, 27000 = 270 deg)");
                        break;
                    case 'b':
                        currentDirection = 0;
                        Console.WriteLine("Direction: Push SOUTH (from North, 0 = 0 deg)");
                        break;
                    case '0':
                        StopAll(allEffects);
                        Console.WriteLine("Stopped");
                        break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
        }

        StopAll(allEffects);
        foreach (var e in allEffects) e?.Dispose();
        joystick.Unacquire();
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        Console.WriteLine("\nDone.");
    }

    static void SetConstantForce(Effect? effect, int magnitude, int direction, int[] axisOffsets)
    {
        if (effect == null) return;
        var ep = new EffectParameters
        {
            Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets,
            Axes = axisOffsets.Take(2).ToArray(),
            Directions = new[] { direction, 0 },
            Parameters = new ConstantForce { Magnitude = magnitude }
        };
        effect.SetParameters(ep,
            EffectParameterFlags.TypeSpecificParameters |
            EffectParameterFlags.Direction |
            EffectParameterFlags.Start);
    }

    static void SetPeriodic(Effect? effect, int magnitude, int periodMicroseconds, int direction, int[] axisOffsets)
    {
        if (effect == null) return;
        var ep = new EffectParameters
        {
            Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets,
            Axes = axisOffsets.Take(2).ToArray(),
            Directions = new[] { direction, 0 },
            Parameters = new PeriodicForce
            {
                Magnitude = magnitude,
                Offset = 0,
                Phase = 0,
                Period = periodMicroseconds
            }
        };
        effect.SetParameters(ep,
            EffectParameterFlags.TypeSpecificParameters |
            EffectParameterFlags.Direction |
            EffectParameterFlags.Start);
    }

    // Conditions don't carry a polar direction the way periodics/constants
    // do; they're per-axis and apply Cartesian coefficients. ConditionSet
    // wants one Condition per actuator axis, so duplicate the same
    // coefficient set across however many axes the device exposes.
    static void SetCondition(Effect? effect, int coefficient, int[] axisOffsets)
    {
        if (effect == null) return;
        int axes = Math.Min(axisOffsets.Length, 2);
        var conditions = new Condition[axes];
        for (int i = 0; i < axes; i++)
        {
            conditions[i] = new Condition
            {
                DeadBand = 0,
                Offset = 0,
                NegativeCoefficient = coefficient,
                PositiveCoefficient = coefficient,
                NegativeSaturation = 10000,
                PositiveSaturation = 10000,
            };
        }
        var ep = new EffectParameters
        {
            Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
            Axes = axisOffsets.Take(axes).ToArray(),
            Directions = new int[axes],
            Parameters = new ConditionSet { Conditions = conditions }
        };
        effect.SetParameters(ep,
            EffectParameterFlags.TypeSpecificParameters |
            EffectParameterFlags.Axes |
            EffectParameterFlags.Direction |
            EffectParameterFlags.Start);
    }

    static void SetRamp(Effect? effect, int startMag, int endMag, int direction, int[] axisOffsets)
    {
        if (effect == null) return;
        var ep = new EffectParameters
        {
            Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets,
            Axes = axisOffsets.Take(2).ToArray(),
            Directions = new[] { direction, 0 },
            Parameters = new RampForce { Start = startMag, End = endMag }
        };
        effect.SetParameters(ep,
            EffectParameterFlags.TypeSpecificParameters |
            EffectParameterFlags.Direction |
            EffectParameterFlags.Start);
    }

    static Effect? TryCreatePeriodic(Joystick joy, Guid guid, string name, int[] axisOffsets, int magnitude, int periodMicroseconds)
    {
        try
        {
            var ep = new EffectParameters
            {
                Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets,
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = axisOffsets.Take(2).ToArray(),
                Directions = new[] { 0, 0 },
                Parameters = new PeriodicForce
                {
                    Magnitude = magnitude,
                    Offset = 0,
                    Phase = 0,
                    Period = periodMicroseconds
                }
            };
            var eff = new Effect(joy, guid, ep);
            Console.WriteLine($"  SUCCESS: {name}");
            return eff;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {name} - {ex.Message}");
            return null;
        }
    }

    static Effect? TryCreateCondition(Joystick joy, Guid guid, string name, int[] axisOffsets, int coefficient)
    {
        try
        {
            int axes = Math.Min(axisOffsets.Length, 2);
            var conditions = new Condition[axes];
            for (int i = 0; i < axes; i++)
            {
                conditions[i] = new Condition
                {
                    DeadBand = 0,
                    Offset = 0,
                    NegativeCoefficient = coefficient,
                    PositiveCoefficient = coefficient,
                    NegativeSaturation = 10000,
                    PositiveSaturation = 10000,
                };
            }
            var ep = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = axisOffsets.Take(axes).ToArray(),
                Directions = new int[axes],
                Parameters = new ConditionSet { Conditions = conditions }
            };
            var eff = new Effect(joy, guid, ep);
            Console.WriteLine($"  SUCCESS: {name}");
            return eff;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: {name} - {ex.Message}");
            return null;
        }
    }

    static Effect? TryCreateRamp(Joystick joy, int[] axisOffsets, int startMag, int endMag, int durationMicroseconds)
    {
        try
        {
            var ep = new EffectParameters
            {
                Flags = EffectFlags.Polar | EffectFlags.ObjectOffsets,
                Duration = durationMicroseconds,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = axisOffsets.Take(2).ToArray(),
                Directions = new[] { 0, 0 },
                Parameters = new RampForce { Start = startMag, End = endMag }
            };
            var eff = new Effect(joy, EffectGuid.RampForce, ep);
            Console.WriteLine($"  SUCCESS: Ramp");
            return eff;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  FAIL: Ramp - {ex.Message}");
            return null;
        }
    }

    static void StopAll(IEnumerable<Effect?> effects)
    {
        foreach (var e in effects)
        {
            try { e?.Stop(); } catch { }
        }
    }
}
