using System.Runtime.InteropServices;
using SharpDX.DirectInput;

namespace FfbTest;

class Program
{
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

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
        var hwnd = GetConsoleWindow();
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

        // Find FFB actuator axes.
        var axisObjects = objects
            .Where(o => o.ObjectId.Flags.HasFlag(DeviceObjectTypeFlags.ForceFeedbackActuator))
            .ToList();

        if (axisObjects.Count == 0)
        {
            Console.WriteLine("No FFB actuator axes found on this device.");
            joystick.Unacquire();
            return;
        }

        Console.WriteLine("FFB actuator axes:");
        foreach (var ax in axisObjects)
            Console.WriteLine($"  {ax.Name} (offset {ax.Offset})");
        Console.WriteLine();

        int[] axisOffsets = axisObjects.Select(a => a.Offset).Take(2).ToArray();
        int[] directions = new int[axisOffsets.Length];

        // Create constant force effect.
        Effect? constantEffect = null;
        try
        {
            constantEffect = new Effect(joystick, EffectGuid.ConstantForce, new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = axisOffsets,
                Directions = directions,
                Parameters = new ConstantForce { Magnitude = 0 }
            });
            Console.WriteLine("Constant force effect created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create constant force: {ex.Message}");
        }

        // Create sine wave effect.
        Effect? sineEffect = null;
        try
        {
            sineEffect = new Effect(joystick, EffectGuid.Sine, new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = -1,
                Gain = 10000,
                SamplePeriod = 0,
                StartDelay = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                Axes = axisOffsets,
                Directions = directions,
                Parameters = new PeriodicForce
                {
                    Magnitude = 5000,
                    Offset = 0,
                    Phase = 0,
                    Period = 200_000
                }
            });
            Console.WriteLine("Sine wave effect created.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create sine effect: {ex.Message}");
        }

        Console.WriteLine("\nCommands:");
        Console.WriteLine("  [1] Constant - light   (2500)");
        Console.WriteLine("  [2] Constant - medium  (5000)");
        Console.WriteLine("  [3] Constant - strong  (10000)");
        Console.WriteLine("  [4] Sine wave - gentle  (3000, 300ms)");
        Console.WriteLine("  [5] Sine wave - intense (8000, 100ms)");
        Console.WriteLine("  [0] Stop all");
        Console.WriteLine("  [Q] Quit\n");

        while (true)
        {
            Console.Write("> ");
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Q) break;

            try
            {
                switch (key.KeyChar)
                {
                    case '1':
                        SetConstantForce(constantEffect, 2500);
                        StopEffect(sineEffect);
                        Console.WriteLine("Constant: light (2500)");
                        break;
                    case '2':
                        SetConstantForce(constantEffect, 5000);
                        StopEffect(sineEffect);
                        Console.WriteLine("Constant: medium (5000)");
                        break;
                    case '3':
                        SetConstantForce(constantEffect, 10000);
                        StopEffect(sineEffect);
                        Console.WriteLine("Constant: strong (10000)");
                        break;
                    case '4':
                        StopEffect(constantEffect);
                        SetSineForce(sineEffect, 3000, 300_000);
                        Console.WriteLine("Sine: gentle (3000, 300ms)");
                        break;
                    case '5':
                        StopEffect(constantEffect);
                        SetSineForce(sineEffect, 8000, 100_000);
                        Console.WriteLine("Sine: intense (8000, 100ms)");
                        break;
                    case '0':
                        StopEffect(constantEffect);
                        StopEffect(sineEffect);
                        Console.WriteLine("Stopped");
                        break;
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
        }

        StopEffect(constantEffect);
        StopEffect(sineEffect);
        constantEffect?.Dispose();
        sineEffect?.Dispose();
        joystick.Unacquire();
        Console.WriteLine("\nDone.");
    }

    static void SetConstantForce(Effect? effect, int magnitude)
    {
        if (effect == null) return;
        effect.SetParameters(new EffectParameters
        {
            Parameters = new ConstantForce { Magnitude = magnitude }
        }, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
    }

    static void SetSineForce(Effect? effect, int magnitude, int periodMicroseconds)
    {
        if (effect == null) return;
        effect.SetParameters(new EffectParameters
        {
            Parameters = new PeriodicForce
            {
                Magnitude = magnitude,
                Offset = 0,
                Phase = 0,
                Period = periodMicroseconds
            }
        }, EffectParameterFlags.TypeSpecificParameters | EffectParameterFlags.Start);
    }

    static void StopEffect(Effect? effect)
    {
        try { effect?.Stop(); } catch { }
    }
}
