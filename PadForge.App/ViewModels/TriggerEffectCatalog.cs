using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common.Input;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>Which trigger an Adaptive Trigger Effect action plays on
    /// (issue #531). Saved by name. APPEND-ONLY.</summary>
    public enum MacroTriggerEffectSide
    {
        Right = 0,
        Left = 1,
        Both = 2,
    }

    /// <summary>How long an Adaptive Trigger Effect action's effect holds.
    /// Saved by name. APPEND-ONLY.</summary>
    public enum MacroTriggerEffectHold
    {
        /// <summary>For <c>TriggerEffectMs</c> milliseconds.</summary>
        Duration = 0,
        /// <summary>Until the macro's trigger is no longer held.</summary>
        UntilRelease = 1,
        /// <summary>Until a Stop Adaptive Trigger Effect action.</summary>
        UntilStopped = 2,
    }

    /// <summary>One parameter of a trigger effect: its words, its range and
    /// the value a new action starts with. The ranges are the ones
    /// <see cref="GameModProtocol.TryEncodeTrigger"/> accepts, after Nielk1's
    /// TriggerEffectGenerator.</summary>
    public sealed class TriggerEffectParameter
    {
        public TriggerEffectParameter(Func<string> label, int min, int max, int value)
        {
            _label = label;
            Min = min;
            Max = max;
            Default = value;
        }

        private readonly Func<string> _label;
        public string Label => _label();
        public int Min { get; }
        public int Max { get; }
        public int Default { get; }
    }

    /// <summary>One effect in the action's picker: the DSX trigger mode the
    /// #529 encoder takes, its name, and its parameters in the encoder's
    /// order.</summary>
    public sealed class TriggerEffectKind : INotifyPropertyChanged
    {
        private readonly Func<string> _name;

        public TriggerEffectKind(int mode, Func<string> name, params TriggerEffectParameter[] parameters)
        {
            Mode = mode;
            _name = name;
            Parameters = parameters ?? Array.Empty<TriggerEffectParameter>();
        }

        public int Mode { get; }
        public string Display => _name();
        public TriggerEffectParameter[] Parameters { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        internal void RaiseDisplayChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        public override string ToString() => Display;
    }

    /// <summary>
    /// The effects an Adaptive Trigger Effect action can play (issue #531):
    /// the trigger modes game mods made for DSX send, encoded by the #529
    /// encoder, so a macro plays exactly what a mod can. DSX's modes that
    /// repeat another under a second name (19 to 23) and VibrateTrigger (8),
    /// which is Vibration with fixed zones, are left out of the picker.
    /// </summary>
    public static class TriggerEffectCatalog
    {
        public const int OffMode = 0;
        public const int ResistanceMode = 13;
        public const int WeaponMode = 16;
        public const int VibrationMode = 17;
        public const int BowMode = 14;
        public const int GallopingMode = 15;
        public const int MachineMode = 18;
        public const int SlopeFeedbackMode = 24;
        public const int MultiplePositionFeedbackMode = 25;
        public const int MultiplePositionVibrationMode = 26;

        private static TriggerEffectParameter P(Func<string> label, int min, int max, int value) => new(label, min, max, value);
        private static string StartZone() => Strings.Instance.MacroAction_TriggerEffect_StartZone;
        private static string EndZone() => Strings.Instance.MacroAction_TriggerEffect_EndZone;
        private static string Strength() => Strings.Instance.Pad_AT_Strength;
        private static string Frequency() => Strings.Instance.Pad_AT_Frequency;

        private static TriggerEffectParameter[] Zones(int[] values)
        {
            var list = new TriggerEffectParameter[10];
            for (int i = 0; i < 10; i++)
            {
                int zone = i + 1;
                list[i] = P(() => string.Format(CultureInfo.CurrentCulture, Strings.Instance.MacroAction_TriggerEffect_Zone_Format, zone), 0, 8, values[i]);
            }
            return list;
        }

        public static IReadOnlyList<TriggerEffectKind> Kinds { get; } = Build();

        private static IReadOnlyList<TriggerEffectKind> Build()
        {
            var vibrationZones = new List<TriggerEffectParameter> { P(Frequency, 1, 255, 20) };
            vibrationZones.AddRange(Zones(new[] { 0, 0, 0, 4, 4, 6, 6, 8, 8, 8 }));
            var kinds = new List<TriggerEffectKind>
            {
                new(OffMode, () => Strings.Instance.MacroAction_TriggerEffect_Off),
                new(ResistanceMode, () => Strings.Instance.Pad_AT_Feedback,
                    P(StartZone, 0, 9, 3), P(Strength, 1, 8, 6)),
                new(WeaponMode, () => Strings.Instance.Pad_AT_Weapon,
                    P(StartZone, 2, 7, 3), P(EndZone, 3, 8, 6), P(Strength, 1, 8, 8)),
                new(VibrationMode, () => Strings.Instance.Pad_AT_Vibration,
                    P(StartZone, 0, 9, 0), P(Strength, 1, 8, 8), P(Frequency, 1, 255, 30)),
                new(BowMode, () => Strings.Instance.MacroAction_TriggerEffect_Bow,
                    P(StartZone, 0, 7, 1), P(EndZone, 1, 8, 6), P(Strength, 1, 8, 6),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_SnapStrength, 1, 8, 8)),
                new(GallopingMode, () => Strings.Instance.MacroAction_TriggerEffect_Galloping,
                    P(StartZone, 0, 8, 0), P(EndZone, 1, 9, 9),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_FirstFoot, 0, 6, 2),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_SecondFoot, 1, 7, 5),
                    P(Frequency, 1, 255, 8)),
                new(MachineMode, () => Strings.Instance.MacroAction_TriggerEffect_Machine,
                    P(StartZone, 0, 8, 1), P(EndZone, 1, 9, 9),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_StrengthA, 0, 7, 2),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_StrengthB, 0, 7, 6),
                    P(Frequency, 1, 255, 10),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_Period, 0, 255, 4)),
                new(SlopeFeedbackMode, () => Strings.Instance.Pad_AT_Slope,
                    P(StartZone, 0, 8, 0), P(EndZone, 1, 9, 9),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_StartStrength, 1, 8, 1),
                    P(() => Strings.Instance.MacroAction_TriggerEffect_EndStrength, 1, 8, 8)),
                new(MultiplePositionFeedbackMode, () => Strings.Instance.Pad_AT_MultiPosFeedback,
                    Zones(new[] { 0, 0, 2, 3, 4, 5, 6, 7, 8, 8 })),
                new(MultiplePositionVibrationMode, () => Strings.Instance.Pad_AT_MultiPosVibration,
                    vibrationZones.ToArray()),
                // DSX's named presets (#529, DualSenseY's bytes). No parameters.
                new(1, () => "GameCube"),
                new(2, () => Strings.Instance.MacroAction_TriggerEffect_VerySoft),
                new(3, () => Strings.Instance.MacroAction_TriggerEffect_Soft),
                new(10, () => Strings.Instance.MacroAction_TriggerEffect_Medium),
                new(4, () => Strings.Instance.MacroAction_TriggerEffect_Hard),
                new(5, () => Strings.Instance.MacroAction_TriggerEffect_VeryHard),
                new(6, () => Strings.Instance.MacroAction_TriggerEffect_Hardest),
                new(7, () => Strings.Instance.MacroAction_TriggerEffect_Rigid),
                new(9, () => Strings.Instance.MacroAction_TriggerEffect_Choppy),
                new(11, () => Strings.Instance.MacroAction_TriggerEffect_VibratePulse),
            };
            Strings.Instance.PropertyChanged += (_, _) =>
            {
                foreach (var kind in kinds) kind.RaiseDisplayChanged();
            };
            return kinds;
        }

        public static TriggerEffectKind Find(int mode)
        {
            foreach (var kind in Kinds)
                if (kind.Mode == mode) return kind;
            return null;
        }

        /// <summary>A mode's starting values, in the encoder's order.</summary>
        public static int[] Defaults(int mode)
        {
            var kind = Find(mode);
            if (kind == null) return Array.Empty<int>();
            var values = new int[kind.Parameters.Length];
            for (int i = 0; i < values.Length; i++) values[i] = kind.Parameters[i].Default;
            return values;
        }

        /// <summary>The 11-byte block for a mode and its values, or null when
        /// the encoder refuses them (an end zone not past its start, a second
        /// foot not past the first).</summary>
        public static byte[] Encode(int mode, int[] values)
        {
            var block = new byte[11];
            return GameModProtocol.TryEncodeTrigger(mode, values ?? Array.Empty<int>(), block) ? block : null;
        }

        internal static string FormatValues(int[] values)
            => values == null || values.Length == 0 ? string.Empty
             : string.Join(",", Array.ConvertAll(values, v => v.ToString(CultureInfo.InvariantCulture)));

        internal static int[] ParseValues(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
            var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var values = new List<int>(parts.Length);
            foreach (var part in parts)
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    values.Add(v);
            return values.ToArray();
        }
    }

    /// <summary>One parameter row in the action's editor, writing back into
    /// the action's values.</summary>
    public sealed class TriggerEffectParameterValue : ObservableObject
    {
        private readonly MacroAction _owner;
        private readonly int _index;

        internal TriggerEffectParameterValue(MacroAction owner, int index, TriggerEffectParameter parameter)
        {
            _owner = owner;
            _index = index;
            Parameter = parameter;
        }

        public TriggerEffectParameter Parameter { get; }
        public string Label => Parameter.Label;
        public int Min => Parameter.Min;
        public int Max => Parameter.Max;

        public int Value
        {
            get => _owner.TriggerEffectValue(_index);
            set
            {
                _owner.SetTriggerEffectValue(_index, Math.Clamp(value, Parameter.Min, Parameter.Max));
                OnPropertyChanged();
            }
        }

        private CommunityToolkit.Mvvm.Input.RelayCommand _resetCommand;

        /// <summary>Puts the value back to the effect's own default for this
        /// parameter, as every other row of a step has its reset.</summary>
        public CommunityToolkit.Mvvm.Input.RelayCommand ResetValueCommand
            => _resetCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(() => Value = Parameter.Default);

        internal void RaiseLabelChanged() => OnPropertyChanged(nameof(Label));
        internal void RaisePropertyChangedForValue() => OnPropertyChanged(nameof(Value));
    }
}
