using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// The Source picker's Preview (#600): the demo of a mouse family, driven
    /// by the user's own mouse. Preview pins one family of the picker's mouse
    /// group, and its gauge then follows the user's mouse wherever the
    /// pointer goes, until a family is picked, Escape is pressed or the list
    /// closes. Nothing is mapped: the preview runs the engine's own steering
    /// (SourceKindRuntime's TickMouseTravel, TickMouseSpeed and
    /// TickMousePosition) on a runtime of its own, with the line's own
    /// settings where the line already reads that family and the defaults
    /// otherwise.
    /// <para>It reads the mouse through RawInputListener's running totals,
    /// which no reader consumes, so the engine's reads lose nothing, and the
    /// pointer with GetCursorPos. A preview of a family with a Steer input
    /// reads the mouse's own buttons, where the line's Steer input is, and
    /// says while it waits.</para>
    /// </summary>
    public sealed class MouseFamilyPreview : ObservableObject
    {
        public static MouseFamilyPreview Instance { get; } = new();

        /// <summary>What one frame of the preview reads: the mouse's running
        /// totals, its five buttons and where the pointer is on its
        /// monitor. A test stands in its own.</summary>
        internal readonly record struct Sample(long X, long Y, long Wheel, long WheelH, bool[] Buttons,
            (double x, double y, double centerX, double centerY, double unit)? Pointer);

        /// <summary>The reader of the mouse. Null reads the machine.</summary>
        internal static Func<Sample> ReadSeam { get; set; }

        /// <summary>The clock, in Stopwatch ticks. Null reads the
        /// machine's.</summary>
        internal static Func<long> ClockSeam { get; set; }

        private const string Target = "MousePreview";

        private InputChoice _pinned;
        private MappingSource _source;
        private string _gateWords = string.Empty;
        private SourceKindRuntime _runtime;
        private Sample _last;
        private long _lastTicks;
        private DispatcherTimer _timer;

        /// <summary>The entry Preview pins now, or null.</summary>
        public InputChoice Pinned => _pinned;

        private double _value;
        /// <summary>The family's answer from -1 to 1 for the last frame.</summary>
        public double Value
        {
            get => _value;
            private set
            {
                if (SetProperty(ref _value, value)) OnPropertyChanged(nameof(Arc));
            }
        }

        private string _caption = string.Empty;
        /// <summary>What the gauge shows in words: where the family holds the
        /// stick, or which input it waits for.</summary>
        public string Caption
        {
            get => _caption;
            private set => SetProperty(ref _caption, value ?? string.Empty);
        }

        /// <summary>The gauge's lit arc, from the top of a half circle to
        /// the value: a full turn of the stick either way is a quarter
        /// circle, as a wheel's lock is drawn.</summary>
        public Geometry Arc => ArcOf(_value);

        private RelayCommand<object> _pinCommand;

        /// <summary>Pins the family of the entry its parameter names, or
        /// unpins it when it is pinned already. The parameter is the entry,
        /// or an array of the entry and the source's line.</summary>
        public RelayCommand<object> PinCommand => _pinCommand ??= new RelayCommand<object>(p =>
        {
            InputChoice choice = p as InputChoice;
            object line = null;
            if (p is object[] pair && pair.Length >= 1)
            {
                choice = pair[0] as InputChoice;
                line = pair.Length > 1 ? pair[1] : null;
            }
            if (choice == null) return;
            if (ReferenceEquals(choice, _pinned)) Stop();
            else Pin(choice, line);
        });

        /// <summary>Starts the preview of the family of
        /// <paramref name="choice"/>, with the settings of
        /// <paramref name="line"/> (a MappingItem or a MappingSourceItem)
        /// where it reads the same descriptor, and stops any other.</summary>
        internal void Pin(InputChoice choice, object line)
        {
            if (choice?.PreviewFamily == null) return;
            Stop();
            _pinned = choice;
            choice.IsPreviewing = true;
            _source = SourceFor(choice, line, out _gateWords);
            _runtime = new SourceKindRuntime();
            _last = Read();
            _lastTicks = Now();
            Value = 0;
            Caption = Words(0, false);
            OnPropertyChanged(nameof(Pinned));
            if (ReadSeam == null && Application.Current?.Dispatcher is { } dispatcher)
            {
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input,
                    (_, _) => Step(), dispatcher);
                _timer.Start();
            }
        }

        /// <summary>Stops the preview. The list closing comes here, which a
        /// pick, Escape and a click away all do.</summary>
        public void Stop()
        {
            _timer?.Stop();
            _timer = null;
            if (_pinned != null) _pinned.IsPreviewing = false;
            _pinned = null;
            _source = null;
            _runtime = null;
            OnPropertyChanged(nameof(Pinned));
        }

        /// <summary>One frame: what the mouse did since the last one through
        /// the family's own steering.</summary>
        internal void Step()
        {
            if (_pinned == null || _source == null) return;
            var now = Read();
            long ticks = Now();
            double dt = Math.Max(0, (ticks - _lastTicks) / (double)Stopwatch.Frequency);
            _lastTicks = ticks;
            var state = new CustomInputState
            {
                MouseRawDX = (int)(now.X - _last.X),
                MouseRawDY = (int)(now.Y - _last.Y),
                MouseRawWheel = (int)(now.Wheel - _last.Wheel),
                MouseRawWheelH = (int)(now.WheelH - _last.WheelH),
            };
            if (now.Buttons != null)
                for (int i = 0; i < now.Buttons.Length && i < state.Buttons.Length; i++) state.Buttons[i] = now.Buttons[i];
            _last = now;

            bool held = SteerHeld(state);
            _runtime.FrameSeq++;
            string d = _source.Descriptor;
            double v = _pinned.PreviewFamily switch
            {
                InputChoice.PreviewSpeed => _runtime.TickMouseSpeed(0, Target, 0, _source, state, dt, held, d == "Axis 1"),
                InputChoice.PreviewPosition => _runtime.TickMousePosition(0, Target, 0, _source, dt, held, d.EndsWith(" Y", StringComparison.Ordinal), now.Pointer),
                _ => _runtime.TickMouseTravel(0, Target, 0, _source, state, dt, held),
            };
            Value = double.IsFinite(v) ? Math.Clamp(v, -1.0, 1.0) : 0;
            bool waits = !string.IsNullOrEmpty(_source.GateDescriptor) && !held
                && MappingSource.EffectiveMouseSteer(_source.ParamTravelSteer, _source.ParamTravelCentering, true,
                    _pinned.PreviewFamily is InputChoice.PreviewTravel or InputChoice.PreviewWheel) != MappingSource.TravelSteerToggle;
            Caption = Words(Value, waits);
        }

        /// <summary>The line's Steer input, read on the mouse's own buttons,
        /// where the line's picker takes it from.</summary>
        private bool SteerHeld(CustomInputState state)
        {
            string gate = _source?.GateDescriptor;
            if (string.IsNullOrEmpty(gate)) return false;
            var read = new MappingSource { Kind = "Direct", Descriptor = gate.Trim() };
            return SourceCoercion.EvaluateForButtonTarget(state, read, 50, -1, null);
        }

        /// <summary>The source the preview runs: the line's own settings
        /// where it reads the same descriptor as the entry and the same
        /// device, the family's defaults otherwise.</summary>
        private static MappingSource SourceFor(InputChoice choice, object line, out string gateWords)
        {
            gateWords = string.Empty;
            var src = new MappingSource { Kind = "Direct", Descriptor = choice.Descriptor, DeviceGuid = choice.DeviceGuid ?? string.Empty };
            switch (line)
            {
                case MappingItem row when string.Equals(row.LineDeviceGuid, src.DeviceGuid, StringComparison.OrdinalIgnoreCase)
                                          && row.SelectedInput?.Descriptor == choice.Descriptor:
                    row.StoreMouseTravel(src);
                    src.GateDescriptor = row.GateDescriptor ?? string.Empty;
                    src.MouseCursorSensitivity = row.MouseCursorSensitivity;
                    gateWords = row.GateText ?? string.Empty;
                    break;
                case MappingSourceItem item when string.Equals(item.LineDeviceGuid, src.DeviceGuid, StringComparison.OrdinalIgnoreCase)
                                                 && item.Descriptor == choice.Descriptor:
                    item.StoreMouseTravel(src);
                    src.GateDescriptor = item.GateDescriptor ?? string.Empty;
                    src.MouseCursorSensitivity = item.MouseCursorSensitivity;
                    gateWords = item.GateText ?? string.Empty;
                    break;
            }
            return src;
        }

        private string Words(double value, bool waits)
        {
            var s = Strings.Instance;
            if (waits)
            {
                return string.Format(s.Mapping_Preview_Waits_Format,
                    string.IsNullOrEmpty(_gateWords) ? _source.GateDescriptor : _gateWords);
            }
            int percent = (int)Math.Round(Math.Abs(value) * 100.0, MidpointRounding.AwayFromZero);
            string place;
            if (percent == 0) place = s.Mapping_Preview_Center;
            else
            {
                string d = _source?.Descriptor ?? string.Empty;
                bool upDown = d == SourceCoercion.WheelTravelDescriptor || d.EndsWith(" Y", StringComparison.Ordinal) || d == "Axis 1";
                // The wheel's positive side is up, and every other Y's is
                // toward the user, which reads as down.
                bool upPositive = d == SourceCoercion.WheelTravelDescriptor;
                string format = !upDown
                    ? (value > 0 ? s.Mapping_Preview_Right : s.Mapping_Preview_Left)
                    : (value > 0) == upPositive ? s.Mapping_Preview_Up : s.Mapping_Preview_Down;
                place = string.Format(CultureInfo.CurrentCulture, format, percent);
            }
            bool holds = _pinned?.PreviewFamily is InputChoice.PreviewTravel or InputChoice.PreviewWheel;
            return string.Format(holds ? s.Mapping_Preview_Held_Format : s.Mapping_Preview_At_Format, place);
        }

        /// <summary>The lit arc for a value: a half circle of radius 47
        /// centered at (57, 52), lit from its top toward the side the value
        /// leans, a quarter of the circle at full deflection.</summary>
        internal static Geometry ArcOf(double value)
        {
            const double cx = 57, cy = 52, r = 47;
            double v = double.IsFinite(value) ? Math.Clamp(value, -1.0, 1.0) : 0;
            if (Math.Abs(v) < 0.005) return Geometry.Empty;
            double angle = v * Math.PI / 2;
            var start = new Point(cx, cy - r);
            var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, false, v > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            g.Freeze();
            return g;
        }

        private static long Now() => ClockSeam?.Invoke() ?? Stopwatch.GetTimestamp();

        private static Sample Read()
        {
            var seam = ReadSeam;
            if (seam != null) return seam();
            var (x, y, wheel, wheelH) = RawInputListener.MouseTotals();
            var buttons = new bool[5];
            RawInputListener.GetMouseButtons(RawInputListener.AggregateMouseHandle, buttons);
            (double, double, double, double, double)? pointer = null;
            if (GetCursorPos(out var p))
            {
                var mon = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    var m = PadForge.Services.CursorControlService.Measure(p.X, p.Y,
                        mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
                    if (m.unit > 0) pointer = (p.X, p.Y, m.centerX, m.centerY, m.unit);
                }
            }
            return new Sample(x, y, wheel, wheelH, buttons, pointer);
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
