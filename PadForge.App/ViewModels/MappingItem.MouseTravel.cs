using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>A choice of a mouse picker on a source's line: the value
    /// MappingSource stores and its words.</summary>
    public sealed record MouseTravelChoice(string Value, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Says that a mouse's DPI changed on its device panel (#600),
    /// so a source's line reads its travel in centimeters again. A line
    /// listens through a weak event, so a line that is gone is not
    /// kept.</summary>
    internal sealed class MouseDpiNotice
    {
        internal static readonly MouseDpiNotice Instance = new();

        public event EventHandler<EventArgs> Changed;

        internal void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        internal static void Listen(EventHandler<EventArgs> handler)
            => WeakEventManager<MouseDpiNotice, EventArgs>.AddHandler(Instance, nameof(Changed), handler);
    }

    /// <summary>What a row's first source and an added source share for a
    /// mouse input (#597, #600): the pickers' choices, how a typed or
    /// dragged value lands, the Travel to Full Lock slider's scale, the
    /// Steer Input picker's list and what the source's device is.</summary>
    internal static class MouseTravelLine
    {
        /// <summary>The Centering picker's choices, in the culture in
        /// hand.</summary>
        internal static IReadOnlyList<MouseTravelChoice> CenteringChoices()
        {
            var s = Strings.Instance;
            return new[]
            {
                new MouseTravelChoice(MappingSource.TravelCenteringOff, s.Mapping_MouseTravelCentering_Off),
                new MouseTravelChoice(MappingSource.TravelCenteringSpring, s.Mapping_MouseTravelCentering_Spring),
                new MouseTravelChoice(MappingSource.TravelCenteringTelemetry, s.Mapping_MouseTravelCentering_Telemetry),
                new MouseTravelChoice(MappingSource.TravelCenteringGameForce, s.Mapping_MouseTravelCentering_GameForce),
            };
        }

        /// <summary>The Steer picker's choices: Always, While Held and
        /// Center, While Held and Stay, and Toggle (#600).</summary>
        internal static IReadOnlyList<MouseTravelChoice> SteerChoices()
        {
            var s = Strings.Instance;
            return new[]
            {
                new MouseTravelChoice(MappingSource.TravelSteerAlways, s.Mapping_MouseTravelSteer_Always),
                new MouseTravelChoice(MappingSource.TravelSteerHeldCenter, s.Mapping_MouseTravelSteer_Held),
                new MouseTravelChoice(MappingSource.TravelSteerHeldStay, s.Mapping_MouseTravelSteer_HeldStay),
                new MouseTravelChoice(MappingSource.TravelSteerToggle, s.Mapping_MouseTravelSteer_Toggle),
            };
        }

        /// <summary>A stored or picked Steer as the picker holds it: one of
        /// its four values. The While Held a build before #600 wrote is the
        /// mode the engine reads it in (MappingSource.EffectiveMouseSteer):
        /// Stay on a travel source with Centering Off, Center
        /// otherwise.</summary>
        internal static string SteerOf(string stored, string centering, bool holdsPosition)
        {
            string steer = MappingSource.EffectiveTravelSteer(stored);
            return steer == MappingSource.TravelSteerHeld
                ? MappingSource.EffectiveMouseSteer(steer, centering, hasInput: true, holdsPosition)
                : steer;
        }

        /// <summary>A value as a setting keeps it: inside its range and
        /// rounded to <paramref name="decimals"/> places, so the number
        /// stored is the number its box shows. No number at all (a typed
        /// "NaN") leaves the setting as it was.</summary>
        internal static double Within(double value, double min, double max, int decimals, double current)
            => double.IsFinite(value) ? Math.Clamp(Math.Round(value, decimals), min, max) : current;

        // The Travel to Full Lock slider runs from 0 to 100 along a scale
        // on which each step multiplies the counts. On a straight scale
        // from 100 to 50,000 the default of 2,000 sat a twenty-fifth of
        // the way along, and the low end, where a hundred counts change
        // the steering most, had no room at all.
        private static readonly double DialSpan =
            Math.Log(MappingSource.MaxTravelCounts / MappingSource.MinTravelCounts);

        /// <summary>Where the slider stands for a number of counts.</summary>
        internal static double DialOf(double counts)
            => 100.0 * Math.Log(Math.Clamp(counts, MappingSource.MinTravelCounts, MappingSource.MaxTravelCounts)
                / MappingSource.MinTravelCounts) / DialSpan;

        /// <summary>The counts at a place on the slider, on a round number:
        /// tens under 1,000, fifties under 10,000 and five hundreds past
        /// that, each about what one step of the slider is worth there.</summary>
        internal static double CountsAt(double dial)
        {
            if (!double.IsFinite(dial)) return MappingSource.DefaultTravelCounts;
            double counts = MappingSource.MinTravelCounts * Math.Exp(DialSpan * Math.Clamp(dial, 0.0, 100.0) / 100.0);
            double step = counts < 1000 ? 10 : counts < 10000 ? 50 : 500;
            return Math.Clamp(Math.Round(counts / step) * step, MappingSource.MinTravelCounts, MappingSource.MaxTravelCounts);
        }

        /// <summary>The device a line reads, from the device list.</summary>
        private static UserDevice DeviceOf(string deviceGuid)
            => !string.IsNullOrEmpty(deviceGuid) && Guid.TryParse(deviceGuid, out var g)
                ? PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(g) : null;

        /// <summary>True when the device is a mouse.</summary>
        internal static bool IsMouseDevice(string deviceGuid)
            => DeviceOf(deviceGuid)?.CapType == PadForge.Engine.InputDeviceType.Mouse;

        /// <summary>True when the device is known and is no mouse and no
        /// keyboard: its axes are the ones a user can mark as moving
        /// relatively.</summary>
        internal static bool IsAxisDevice(string deviceGuid)
            => DeviceOf(deviceGuid) is { } d
               && d.CapType != PadForge.Engine.InputDeviceType.Mouse
               && d.CapType != PadForge.Engine.InputDeviceType.Keyboard;

        /// <summary>True for "Axis 0" and "Axis 1", a mouse's Mouse Speed X
        /// and Y.</summary>
        internal static bool IsMouseSpeedDescriptor(string descriptor) => descriptor is "Axis 0" or "Axis 1";

        /// <summary>What follows Travel to Full Lock's box (#600): "counts",
        /// and once the mouse's DPI is set "counts, 3.2 cm", the centimeters
        /// in the user's own number format. Only a mouse's motion has a DPI:
        /// a wheel's units and a relative axis's counts are no fraction of an
        /// inch. The first read makes the line listen for the DPI to
        /// change.</summary>
        internal static string CountsUnit(double counts, string descriptor, string deviceGuid, object line)
        {
            if (line is IMouseDpiListener listener) listener.ListenForMouseDpi();
            string unit = Strings.Instance.Mapping_MouseTravelCounts_Unit;
            if (descriptor != SourceCoercion.MouseTravelXDescriptor && descriptor != SourceCoercion.MouseTravelYDescriptor)
                return unit;
            int dpi = DeviceOf(deviceGuid)?.MouseDpi ?? 0;
            if (dpi <= 0) return unit;
            double cm = counts / dpi * 2.54;
            return string.Format(Strings.Instance.Mapping_MouseTravelCounts_Centimeters, unit,
                cm.ToString(cm < 10 ? "0.0" : "0", CultureInfo.CurrentCulture));
        }

        /// <summary>Counts as their box shows them: whole, with the user's
        /// own digit grouping ("2,000", "2.000", "2 000").</summary>
        internal static string FormatCounts(double counts)
            => counts.ToString("N0", CultureInfo.CurrentCulture);

        /// <summary>Counts typed or shown in their box, read back. Counts are
        /// whole, so the user's own group separator and every space go first
        /// (a plain space typed where the format groups with a no-break one
        /// included), and what is left is read in the user's own format, then
        /// in the invariant one. Taking the separator out first also reads a
        /// format whose group and decimal separators are the same character,
        /// as one of .NET's cultures has. Null when the text is no
        /// number.</summary>
        internal static double? ParseCounts(string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (t.Length == 0) return null;
            var culture = CultureInfo.CurrentCulture;
            string group = culture.NumberFormat.NumberGroupSeparator ?? string.Empty;
            if (group.Length > 0) t = t.Replace(group, string.Empty, StringComparison.Ordinal);
            var compact = new System.Text.StringBuilder(t.Length);
            foreach (char c in t)
                if (!char.IsWhiteSpace(c) && c != (char)0x00A0 && c != (char)0x202F) compact.Append(c);
            string bare = compact.ToString();
            if (bare.Length == 0) return null;
            if (double.TryParse(bare, NumberStyles.Float, culture, out double v) && double.IsFinite(v)) return v;
            if (double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v)) return v;
            return null;
        }

        /// <summary>What the Steer Input picker offers: the on/off inputs of
        /// the device the source reads, out of the slot's list of them
        /// (<paramref name="keys"/>). The engine reads the input on that
        /// device and no other (SourceEvaluator.GateHeld), so an input of
        /// another device would never be seen held. The input in hand is
        /// listed too while the list has no entry for it (its device is not
        /// connected, or an import wrote a test no picker offers), under
        /// the words the line knows it by.</summary>
        internal static IReadOnlyList<InputChoice> SteerInputs(IEnumerable<InputChoice> keys, string deviceGuid,
            string deviceLabel, string input, string inputWords)
        {
            string guid = deviceGuid ?? string.Empty;
            var list = new List<InputChoice>();
            if (keys != null)
                foreach (var c in keys)
                    if (c != null && string.Equals(c.DeviceGuid ?? string.Empty, guid, StringComparison.OrdinalIgnoreCase))
                        list.Add(c);
            if (!string.IsNullOrEmpty(input)
                && PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(list, input, guid) == null)
            {
                list.Insert(0, new InputChoice
                {
                    Descriptor = input,
                    DisplayName = string.IsNullOrEmpty(inputWords) ? input : inputWords,
                    DeviceGuid = guid,
                    DeviceLabel = deviceLabel ?? string.Empty,
                });
            }
            return list;
        }
    }

    /// <summary>A source's line that reads its travel in centimeters and
    /// listens for its mouse's DPI to change.</summary>
    internal interface IMouseDpiListener
    {
        void ListenForMouseDpi();
    }

    public partial class MappingItem : IMouseDpiListener
    {
        // ── Mouse steering (#597, #600) ──
        // The settings of a row's first source when it reads a mouse
        // family, on its line where Mouse Position has its sensitivity: the
        // travel family's, Mouse Speed's and Mouse Position's. They are
        // MappingSource's ParamTravel* and ParamMouse* fields and
        // ParamAxisRelative, read by SourceKindRuntime. The Steer input is
        // the source's own gate (GateDescriptor), which the engine reads on
        // the source's device. MappingSourceItem carries the same members for
        // an added source, so one template draws either.

        private string LineInput => string.IsNullOrEmpty(_sourceDescriptor) ? string.Empty : StripLegacyPrefix(_sourceDescriptor);

        /// <summary>True when the first source reads the travel family: a
        /// travel input, or an axis marked as moving relatively. Gates the
        /// travel settings, as <see cref="IsMouseCursorSource"/> gates
        /// Mouse Position's.</summary>
        public bool IsMouseTravelSource => SourceCoercion.IsMouseTravelDescriptor(LineInput)
            || (_paramAxisRelative && SourceCoercion.TryParseRelativeAxis(LineInput, out _, out _));

        /// <summary>True when the first source reads a mouse's Axis 0 or 1,
        /// Mouse Speed X or Y. Gates Mouse Speed's settings.</summary>
        public bool IsMouseSpeedSource => MouseTravelLine.IsMouseSpeedDescriptor(LineInput)
            && MouseTravelLine.IsMouseDevice(PrimarySourceDeviceGuid);

        /// <summary>True when the first source reads Mouse Position X or
        /// Y.</summary>
        public bool IsMousePositionSource => IsMouseCursorSource;

        /// <summary>True for any mouse source that steers. Gates Steer, the
        /// Steer Input and Smoothing.</summary>
        public bool IsMouseSteerSource => IsMouseTravelSource || IsMouseSpeedSource || IsMousePositionSource;

        /// <summary>True when the first source reads a numbered axis or
        /// slider of a device that is no mouse and no keyboard, or one
        /// marked as moving relatively while its device is away.</summary>
        public bool IsAxisRelativeApplicable => SourceCoercion.TryParseRelativeAxis(LineInput, out _, out _)
            && (_paramAxisRelative || MouseTravelLine.IsAxisDevice(PrimarySourceDeviceGuid));

        private bool _paramAxisRelative;
        /// <summary>This axis moves relatively: the axis holds a position
        /// from how far it turned, as Mouse Travel does.</summary>
        public bool ParamAxisRelative
        {
            get => _paramAxisRelative;
            set
            {
                if (SetProperty(ref _paramAxisRelative, value)) RaiseMouseTravel();
            }
        }

        private double _paramTravelCounts = MappingSource.DefaultTravelCounts;
        /// <summary>Travel to Full Lock: the counts the mouse moves from
        /// center to a lock, 100 to 50,000.</summary>
        public double ParamTravelCounts
        {
            get => _paramTravelCounts;
            set
            {
                if (SetProperty(ref _paramTravelCounts, MouseTravelLine.Within(value,
                        MappingSource.MinTravelCounts, MappingSource.MaxTravelCounts, 0, _paramTravelCounts)))
                {
                    OnPropertyChanged(nameof(ParamTravelCountsDial));
                    OnPropertyChanged(nameof(ParamTravelCountsText));
                    OnPropertyChanged(nameof(TravelCountsUnit));
                }
            }
        }

        /// <summary><see cref="ParamTravelCounts"/> as its slider reads it,
        /// 0 to 100 (<see cref="MouseTravelLine.DialOf"/>).</summary>
        public double ParamTravelCountsDial
        {
            get => MouseTravelLine.DialOf(_paramTravelCounts);
            set => ParamTravelCounts = MouseTravelLine.CountsAt(value);
        }

        /// <summary>Travel to Full Lock in its box: the counts with the user's
        /// own digit grouping. A typed number is read in that format and
        /// lands inside the range, and text that is no number leaves the
        /// counts as they were and shows them again.</summary>
        public string ParamTravelCountsText
        {
            get => MouseTravelLine.FormatCounts(_paramTravelCounts);
            set
            {
                if (MouseTravelLine.ParseCounts(value) is double counts) ParamTravelCounts = counts;
                OnPropertyChanged(nameof(ParamTravelCountsText));
            }
        }

        /// <summary>What follows the counts' box: "counts", and "counts,
        /// 3.2 cm" once the mouse's DPI is set (#600).</summary>
        public string TravelCountsUnit => MouseTravelLine.CountsUnit(_paramTravelCounts,
            IsMouseTravelSource ? LineInput : string.Empty, PrimarySourceDeviceGuid, this);

        private bool _listensForMouseDpi;
        void IMouseDpiListener.ListenForMouseDpi()
        {
            if (_listensForMouseDpi) return;
            _listensForMouseDpi = true;
            MouseDpiNotice.Listen(OnMouseDpiChanged);
        }

        private void OnMouseDpiChanged(object sender, EventArgs e) => OnPropertyChanged(nameof(TravelCountsUnit));

        private string _paramTravelCentering = MappingSource.TravelCenteringOff;
        /// <summary>Centering: what brings the wheel back, one of
        /// MappingSource's TravelCentering values.</summary>
        public string ParamTravelCentering
        {
            get => _paramTravelCentering;
            set
            {
                // A picker whose list is being rebuilt writes null. That is
                // no choice (the lesson of #160).
                if (value == null) return;
                if (SetProperty(ref _paramTravelCentering, MappingSource.EffectiveTravelCentering(value)))
                    OnPropertyChanged(nameof(IsTravelReturnTimeShown));
            }
        }

        public IReadOnlyList<MouseTravelChoice> ParamTravelCenteringChoices => MouseTravelLine.CenteringChoices();

        private double _paramTravelReturnTime = MappingSource.DefaultTravelReturnTime;
        /// <summary>Return Time in seconds, 0 to 5. 0 centers at once.</summary>
        public double ParamTravelReturnTime
        {
            get => _paramTravelReturnTime;
            set => SetProperty(ref _paramTravelReturnTime, MouseTravelLine.Within(value,
                MappingSource.MinTravelReturnTime, MappingSource.MaxTravelReturnTime, 2, _paramTravelReturnTime));
        }

        /// <summary>Return Time shows for the two kinds of centering that
        /// take the wheel back over it.</summary>
        public bool IsTravelReturnTimeShown => IsMouseTravelSource
            && _paramTravelCentering is MappingSource.TravelCenteringSpring or MappingSource.TravelCenteringTelemetry;

        private string _paramTravelSteer = MappingSource.TravelSteerAlways;
        // The Steer input a change to Always took out of the gate, for a
        // change back to a mode that reads one. Kept for the session only.
        private string _travelSteerInputPutBy = string.Empty;

        /// <summary>Steer: when the mouse moves the source, one of Always,
        /// While Held and Center, While Held and Stay, and Toggle. Always has
        /// no input, so a change to it empties the gate, and the engine never
        /// holds the mouse on an input the line does not show. The input
        /// comes back with a change to a mode that reads one.</summary>
        public string ParamTravelSteer
        {
            get => _paramTravelSteer;
            set
            {
                if (value == null) return;
                string steer = MouseTravelLine.SteerOf(value, _paramTravelCentering, IsMouseTravelSource);
                if (!SetProperty(ref _paramTravelSteer, steer)) return;
                if (IsMouseSteerSource)
                {
                    if (steer == MappingSource.TravelSteerAlways)
                    {
                        if (HasGate)
                        {
                            _travelSteerInputPutBy = _gateDescriptor;
                            GateDescriptor = string.Empty;
                        }
                    }
                    else if (!HasGate && _travelSteerInputPutBy.Length > 0)
                    {
                        GateDescriptor = _travelSteerInputPutBy;
                        _travelSteerInputPutBy = string.Empty;
                    }
                }
                OnPropertyChanged(nameof(IsTravelSteerInputShown));
                OnPropertyChanged(nameof(IsMousePositionAnchorShown));
            }
        }

        public IReadOnlyList<MouseTravelChoice> ParamTravelSteerChoices => MouseTravelLine.SteerChoices();

        /// <summary>The Steer Input picker shows for the three modes that
        /// read an input.</summary>
        public bool IsTravelSteerInputShown => IsMouseSteerSource
            && _paramTravelSteer != MappingSource.TravelSteerAlways;

        private IReadOnlyList<InputChoice> _travelSteerInputs;
        /// <summary>The Steer Input picker's list
        /// (<see cref="MouseTravelLine.SteerInputs"/>), built when the
        /// picker first asks and again after what it is built from
        /// changes.</summary>
        public IReadOnlyList<InputChoice> TravelSteerInputs => _travelSteerInputs ??= MouseTravelLine.SteerInputs(
            KeyInputs, PrimarySourceDeviceGuid, PrimarySourceDeviceLabel, _gateDescriptor, GateText);

        /// <summary>The Steer input as its picker holds it: the entry for
        /// the gate on the source's device. A pick writes the gate.</summary>
        public InputChoice TravelSteerInputChoice
        {
            get => PadForge.Common.MappingDisplayResolver.FindChoiceOnDevice(
                TravelSteerInputs, _gateDescriptor, PrimarySourceDeviceGuid);
            set
            {
                // A null write is a picker that lost its entry to a rebuilt
                // list, never a choice: the input stays (#160).
                if (value == null || string.Equals(value.Descriptor ?? string.Empty, _gateDescriptor, StringComparison.Ordinal)) return;
                GateDescriptor = value.Descriptor ?? string.Empty;
            }
        }

        private double _paramTravelSmoothingMs;
        /// <summary>Smoothing in milliseconds, 0 to 100.</summary>
        public double ParamTravelSmoothingMs
        {
            get => _paramTravelSmoothingMs;
            set => SetProperty(ref _paramTravelSmoothingMs, MouseTravelLine.Within(value,
                0.0, MappingSource.MaxTravelSmoothingMs, 0, _paramTravelSmoothingMs));
        }

        private double _paramTravelLockAtSpeed = MappingSource.DefaultTravelLockAtSpeed;
        /// <summary>Lock at Speed in percent, 20 to 100.</summary>
        public double ParamTravelLockAtSpeed
        {
            get => _paramTravelLockAtSpeed;
            set => SetProperty(ref _paramTravelLockAtSpeed, MouseTravelLine.Within(value,
                MappingSource.MinTravelLockAtSpeed, MappingSource.MaxTravelLockAtSpeed, 0, _paramTravelLockAtSpeed));
        }

        /// <summary>Lock at Speed shows while the Game Telemetry extra is
        /// on, since only a game's telemetry gives it a speed to act on.</summary>
        public bool IsTravelLockAtSpeedShown => IsMouseTravelSource
            && PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Enabled;

        private double _paramMouseSpeedSensitivity = MappingSource.DefaultMouseSpeedSensitivity;
        /// <summary>Mouse Speed's Sensitivity, 0.1 to 10.</summary>
        public double ParamMouseSpeedSensitivity
        {
            get => _paramMouseSpeedSensitivity;
            set => SetProperty(ref _paramMouseSpeedSensitivity, MouseTravelLine.Within(value,
                MappingSource.MinMouseSpeedSensitivity, MappingSource.MaxMouseSpeedSensitivity, 2, _paramMouseSpeedSensitivity));
        }

        private double _paramMouseBackToCenter;
        /// <summary>Mouse Speed's Back to Center in seconds, 0 to 2.</summary>
        public double ParamMouseBackToCenter
        {
            get => _paramMouseBackToCenter;
            set => SetProperty(ref _paramMouseBackToCenter, MouseTravelLine.Within(value,
                0.0, MappingSource.MaxMouseBackToCenter, 2, _paramMouseBackToCenter));
        }

        private bool _paramMousePositionAnchor;
        /// <summary>Mouse Position's Start Where Steering Began.</summary>
        public bool ParamMousePositionAnchor
        {
            get => _paramMousePositionAnchor;
            set => SetProperty(ref _paramMousePositionAnchor, value);
        }

        /// <summary>Start Where Steering Began shows on Mouse Position under
        /// a mode that reads a Steer input, the only place it acts.</summary>
        public bool IsMousePositionAnchorShown => IsMousePositionSource
            && _paramTravelSteer != MappingSource.TravelSteerAlways;

        /// <summary>The gate shows as the input the source waits on, but on
        /// a mouse source, where it is the Steer input and the Steer Input
        /// picker shows it.</summary>
        public bool IsGateShown => HasGate && !IsMouseSteerSource;

        /// <summary>A mouse source that holds an input beside Always shows
        /// the mode the engine reads it in (MappingSource.EffectiveMouseSteer),
        /// so the line never hides an input the mouse waits on. A load and a
        /// change of the input to a mouse family both come here.</summary>
        private void SettleTravelSteer()
        {
            if (!IsMouseSteerSource || !HasGate || _paramTravelSteer != MappingSource.TravelSteerAlways) return;
            _paramTravelSteer = MappingSource.EffectiveMouseSteer(MappingSource.TravelSteerAlways,
                _paramTravelCentering, hasInput: true, holdsPosition: IsMouseTravelSource);
            OnPropertyChanged(nameof(ParamTravelSteer));
        }

        /// <summary>The source's input, its device or the row's list of
        /// inputs changed: what the settings show is read again.</summary>
        internal void RaiseMouseTravel()
        {
            SettleTravelSteer();
            OnPropertyChanged(nameof(IsMouseTravelSource));
            OnPropertyChanged(nameof(IsMouseSpeedSource));
            OnPropertyChanged(nameof(IsMousePositionSource));
            OnPropertyChanged(nameof(IsMouseSteerSource));
            OnPropertyChanged(nameof(IsAxisRelativeApplicable));
            OnPropertyChanged(nameof(IsTravelReturnTimeShown));
            OnPropertyChanged(nameof(IsTravelSteerInputShown));
            OnPropertyChanged(nameof(IsTravelLockAtSpeedShown));
            OnPropertyChanged(nameof(IsMousePositionAnchorShown));
            OnPropertyChanged(nameof(TravelCountsUnit));
            OnPropertyChanged(nameof(IsShapingShown));
            OnPropertyChanged(nameof(IsParamAccelApplicable));
            RaiseTravelSteerInput();
        }

        /// <summary>The Steer input changed, or what its picker lists did.
        /// A picker never asked for has nothing to build again.</summary>
        internal void RaiseTravelSteerInput()
        {
            OnPropertyChanged(nameof(IsGateShown));
            if (_travelSteerInputs == null) return;
            _travelSteerInputs = null;
            OnPropertyChanged(nameof(TravelSteerInputs));
            OnPropertyChanged(nameof(TravelSteerInputChoice));
        }

        /// <summary>The Game Telemetry extra went on or off.</summary>
        internal void RaiseMouseTravelRoad() => OnPropertyChanged(nameof(IsTravelLockAtSpeedShown));

        /// <summary>Takes the mouse settings of <paramref name="src"/>, or
        /// their defaults for none. Called after the source's input and its
        /// gates are in (<see cref="LoadShapingAndGates"/>).</summary>
        internal void LoadMouseTravel(MappingSource src)
        {
            _paramAxisRelative = src?.ParamAxisRelative ?? false;
            OnPropertyChanged(nameof(ParamAxisRelative));
            ParamTravelCounts = src != null
                ? MappingSource.EffectiveTravelCounts(src.ParamTravelCounts) : MappingSource.DefaultTravelCounts;
            ParamTravelCentering = MappingSource.EffectiveTravelCentering(src?.ParamTravelCentering);
            ParamTravelReturnTime = src != null
                ? MappingSource.EffectiveTravelReturnTime(src.ParamTravelReturnTime) : MappingSource.DefaultTravelReturnTime;
            // Past the setter: a load puts no input by and takes none out
            // of the gate it has just read.
            _travelSteerInputPutBy = string.Empty;
            if (SetProperty(ref _paramTravelSteer, MouseTravelLine.SteerOf(src?.ParamTravelSteer, _paramTravelCentering, IsMouseTravelSource),
                    nameof(ParamTravelSteer)))
                OnPropertyChanged(nameof(IsTravelSteerInputShown));
            ParamTravelSmoothingMs = src != null ? MappingSource.EffectiveTravelSmoothingMs(src.ParamTravelSmoothingMs) : 0.0;
            ParamTravelLockAtSpeed = src != null
                ? MappingSource.EffectiveTravelLockAtSpeed(src.ParamTravelLockAtSpeed) : MappingSource.DefaultTravelLockAtSpeed;
            ParamMouseSpeedSensitivity = src != null
                ? MappingSource.EffectiveMouseSpeedSensitivity(src.ParamMouseSpeedSensitivity) : MappingSource.DefaultMouseSpeedSensitivity;
            ParamMouseBackToCenter = src != null ? MappingSource.EffectiveMouseBackToCenter(src.ParamMouseBackToCenter) : 0.0;
            ParamMousePositionAnchor = src?.ParamMousePositionAnchor ?? false;
            RaiseMouseTravel();
        }

        /// <summary>Writes them onto <paramref name="dst"/>. The Steer
        /// input is the gate, which <see cref="StoreShapingAndGates"/>
        /// writes.</summary>
        internal void StoreMouseTravel(MappingSource dst)
        {
            if (dst == null) return;
            dst.ParamTravelCounts = _paramTravelCounts;
            dst.ParamTravelCentering = _paramTravelCentering;
            dst.ParamTravelReturnTime = _paramTravelReturnTime;
            dst.ParamTravelSteer = _paramTravelSteer;
            dst.ParamTravelSmoothingMs = _paramTravelSmoothingMs;
            dst.ParamTravelLockAtSpeed = _paramTravelLockAtSpeed;
            dst.ParamMouseSpeedSensitivity = _paramMouseSpeedSensitivity;
            dst.ParamMouseBackToCenter = _paramMouseBackToCenter;
            dst.ParamMousePositionAnchor = _paramMousePositionAnchor;
            // Kept while the device is away: only another input drops it.
            dst.ParamAxisRelative = _paramAxisRelative && SourceCoercion.TryParseRelativeAxis(LineInput, out _, out _);
        }
    }
}
