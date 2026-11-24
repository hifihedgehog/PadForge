using System;
using System.Diagnostics;
using System.Threading;
using PadForge.Engine;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The Test Controller's pattern (#597): one control at a time, so each
    /// mapping row can be seen working. Every button in turn, each stick
    /// around its full circle, each trigger from rest to full and back, the
    /// hat through its eight directions, then again. A pure function of the
    /// pattern's time, so the same speed gives the same sequence.
    /// </summary>
    internal static class TestControllerPattern
    {
        /// <summary>The standard gamepad slots it has, in SDL's layout as
        /// PadForge reads it (SettingsManager.CreateDefaultPadSetting): the
        /// eleven buttons, the six axes and the D-pad.</summary>
        internal static readonly string[] ButtonNames =
            { "A", "B", "X", "Y", "Left Shoulder", "Right Shoulder", "Back", "Start", "Left Stick Button", "Right Stick Button", "Guide" };
        internal static readonly string[] AxisNames =
            { "Left X", "Left Y", "Left Trigger", "Right X", "Right Y", "Right Trigger" };

        internal const int Center = 32768;
        internal const int LeftTrigger = 2, RightTrigger = 5;

        // The parts of one pass at Normal speed, in milliseconds.
        internal const int ButtonDownMs = 250, ButtonUpMs = 150;
        internal const int CircleMs = 2000;
        internal const int TriggerMs = 2000;
        internal const int HatStepMs = 400;

        private static readonly int ButtonsMs = ButtonNames.Length * (ButtonDownMs + ButtonUpMs);
        private const int SticksMs = 2 * CircleMs;
        private const int TriggersMs = 2 * TriggerMs;
        private const int HatMs = 9 * HatStepMs;

        /// <summary>One pass at Normal speed: 16 seconds.</summary>
        internal static readonly int PassMs = ButtonsMs + SticksMs + TriggersMs + HatMs;

        /// <summary>The speeds the page offers, by their place in the list:
        /// Slow, Normal, Fast and Fastest.</summary>
        internal static readonly double[] Speeds = { 0.5, 1.0, 2.0, 4.0 };
        internal const int NormalSpeed = 1;

        internal static double Factor(int speed) => Speeds[Math.Clamp(speed, 0, Speeds.Length - 1)];

        /// <summary>The device's shape: a standard gamepad's, reported as a
        /// gamepad so it is read and auto-mapped one to one like a real
        /// pad.</summary>
        internal static ProgramDeviceShape Shape()
        {
            var buttons = new (int, string)[ButtonNames.Length];
            for (int i = 0; i < buttons.Length; i++) buttons[i] = (i, ButtonNames[i]);
            var axes = new ProgramAxis[AxisNames.Length];
            for (int i = 0; i < axes.Length; i++)
                axes[i] = new ProgramAxis(i, AxisNames[i], i is LeftTrigger or RightTrigger ? 0 : Center);
            return new ProgramDeviceShape
            {
                Buttons = buttons,
                Axes = axes,
                Hats = new[] { "D-Pad" },
                DeviceType = InputDeviceType.Gamepad,
            };
        }

        /// <summary>The state at <paramref name="patternMs"/> into the
        /// pattern, at Normal speed's clock.</summary>
        internal static CustomInputState At(double patternMs)
        {
            var s = Rest();
            double t = patternMs % PassMs;
            if (t < 0) t += PassMs;

            if (t < ButtonsMs)
            {
                int button = (int)(t / (ButtonDownMs + ButtonUpMs));
                if (t - button * (ButtonDownMs + ButtonUpMs) < ButtonDownMs) s.Buttons[button] = true;
                return s;
            }
            t -= ButtonsMs;

            if (t < SticksMs)
            {
                int stick = t < CircleMs ? 0 : 1;
                double angle = (t - stick * CircleMs) / CircleMs * 2 * Math.PI;
                int x = stick == 0 ? 0 : 3;
                s.Axis[x] = Scale(Math.Cos(angle));
                s.Axis[x + 1] = Scale(Math.Sin(angle));
                return s;
            }
            t -= SticksMs;

            if (t < TriggersMs)
            {
                int trigger = t < TriggerMs ? LeftTrigger : RightTrigger;
                double into = (t % TriggerMs) / TriggerMs;
                double pull = into < 0.5 ? into * 2 : (1 - into) * 2;
                s.Axis[trigger] = (int)Math.Round(pull * 65535);
                return s;
            }
            t -= TriggersMs;

            int step = (int)(t / HatStepMs);
            s.Povs[0] = step < 8 ? step * 4500 : -1;
            return s;
        }

        /// <summary>Everything at rest: sticks centered, triggers out,
        /// buttons up, the hat centered.</summary>
        internal static CustomInputState Rest()
        {
            var s = new CustomInputState();
            s.Axis[0] = s.Axis[1] = s.Axis[3] = s.Axis[4] = Center;
            for (int i = 0; i < s.Povs.Length; i++) s.Povs[i] = -1;
            return s;
        }

        /// <summary>A stick's -1 to 1 in the axis range, 1 to 65535 about
        /// the center.</summary>
        private static int Scale(double unit) => Math.Clamp((int)Math.Round(Center + unit * 32767), 0, 65535);
    }

    /// <summary>
    /// The thread that moves the Test Controller: it advances the pattern
    /// by the time gone by times the speed and gives the device each new
    /// state, a hundred times a second. Started when the switch goes on and
    /// stopped when it goes off.
    /// </summary>
    internal sealed class TestControllerFeeder : IDisposable
    {
        internal const int StepMs = 10;

        private readonly ProgramDevice _device;
        private Thread _thread;
        private volatile bool _stop;
        private volatile int _speed = TestControllerPattern.NormalSpeed;

        public TestControllerFeeder(ProgramDevice device) => _device = device;

        /// <summary>The speed's place in <see cref="TestControllerPattern.Speeds"/>.
        /// A change takes effect from the next step, with no jump.</summary>
        public int Speed
        {
            get => _speed;
            set => _speed = Math.Clamp(value, 0, TestControllerPattern.Speeds.Length - 1);
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "PadForge.TestController", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Run()
        {
            using var clock = new FeederClock();
            var watch = Stopwatch.StartNew();
            double pattern = 0;
            long last = 0;
            long step = Stopwatch.Frequency * StepMs / 1000;
            while (!_stop)
            {
                long now = watch.ElapsedTicks;
                pattern += (now - last) * 1000.0 / Stopwatch.Frequency * TestControllerPattern.Factor(_speed);
                last = now;
                try { _device.Publish(TestControllerPattern.At(pattern)); }
                catch { /* the device went away with the switch */ }
                clock.Sleep(step);
            }
            _device.Rest();
        }

        public void Dispose()
        {
            _stop = true;
            var thread = Interlocked.Exchange(ref _thread, null);
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
            _device.Rest();
        }
    }
}
