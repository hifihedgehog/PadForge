using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PadForge.Common.Input.Telemetry;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>
    /// One custom telemetry format as the Custom Formats dialog edits it
    /// (issue #531). The dialog works on these copies and writes the list
    /// back only on Save, so Cancel leaves the saved formats as they were.
    /// </summary>
    public sealed class TelemetryFormatEdit : ObservableObject
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }

        private CustomTelemetryKind _kind = CustomTelemetryKind.Binary;
        public CustomTelemetryKind Kind
        {
            get => _kind;
            set
            {
                if (!SetProperty(ref _kind, value)) return;
                OnPropertyChanged(nameof(IsBinary));
                OnPropertyChanged(nameof(IsJson));
                foreach (var row in Fields) row.Kind = value;
            }
        }

        public bool IsBinary => _kind == CustomTelemetryKind.Binary;
        public bool IsJson => _kind == CustomTelemetryKind.Json;

        private int _length;
        public int Length
        {
            get => _length;
            set => SetProperty(ref _length, Math.Clamp(value, 0, 65507));
        }

        private string _key = string.Empty;
        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value ?? string.Empty);
        }

        private bool _bigEndian;
        public bool BigEndian
        {
            get => _bigEndian;
            set => SetProperty(ref _bigEndian, value);
        }

        private bool _useGearValues;
        public bool UseGearValues
        {
            get => _useGearValues;
            set => SetProperty(ref _useGearValues, value);
        }

        private int _gearReverse = -1;
        public int GearReverse
        {
            get => _gearReverse;
            set => SetProperty(ref _gearReverse, value);
        }

        private int _gearNeutral;
        public int GearNeutral
        {
            get => _gearNeutral;
            set => SetProperty(ref _gearNeutral, value);
        }

        public ObservableCollection<TelemetryFieldEdit> Fields { get; } = new();

        public static TelemetryFormatEdit From(CustomTelemetryFormat format)
        {
            var edit = new TelemetryFormatEdit
            {
                Name = format.Name,
                Kind = format.Kind,
                Length = format.Length,
                Key = format.Key,
                BigEndian = format.BigEndian,
                UseGearValues = format.UseGearValues,
                GearReverse = format.GearReverse,
                GearNeutral = format.GearNeutral,
            };
            foreach (var field in format.Fields)
                if (field != null) edit.Fields.Add(TelemetryFieldEdit.From(field, format.Kind));
            return edit;
        }

        public CustomTelemetryFormat ToModel()
        {
            var format = new CustomTelemetryFormat
            {
                Name = Name.Trim(),
                Kind = Kind,
                Length = Length,
                Key = Key.Trim(),
                BigEndian = BigEndian,
                UseGearValues = UseGearValues,
                GearReverse = GearReverse,
                GearNeutral = GearNeutral,
            };
            foreach (var field in Fields) format.Fields.Add(field.ToModel());
            return format;
        }

        public TelemetryFormatEdit Copy(string name)
        {
            var copy = From(ToModel());
            copy.Name = name;
            return copy;
        }

        /// <summary>The first problem that would keep this format from
        /// reading, or null. <paramref name="position"/> is its place in the
        /// list, for a format with no name.</summary>
        internal string Problem(int position)
        {
            var s = Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            string name = Name.Trim();
            if (!CustomTelemetryFormat.IsValidName(name))
                return string.Format(culture, s.Telemetry_Formats_ErrorName_Format, position);
            foreach (var field in Fields)
            {
                if (field.Target == CustomTelemetryTarget.Variable && !CustomTelemetryFormat.IsValidName(field.Name.Trim()))
                    return string.Format(culture, s.Telemetry_Formats_ErrorVariable_Format, name);
                if (IsJson)
                {
                    if (string.IsNullOrWhiteSpace(field.Key))
                        return string.Format(culture, s.Telemetry_Formats_ErrorKey_Format, name);
                }
                else if (Length > 0 && field.Offset + CustomTelemetryField.SizeOf(field.Type) > Length)
                    return string.Format(culture, s.Telemetry_Formats_ErrorOffset_Format, name, Length);
            }
            return null;
        }
    }

    /// <summary>One field row in the dialog. A number that is not finite
    /// leaves its box as it was: NaN by the base's rule, and infinity too,
    /// since no bound here turns it into a number.</summary>
    public sealed class TelemetryFieldEdit : FiniteObservableObject
    {
        private CustomTelemetryTarget _target = CustomTelemetryTarget.Variable;
        public CustomTelemetryTarget Target
        {
            get => _target;
            set
            {
                if (SetProperty(ref _target, value)) OnPropertyChanged(nameof(IsVariable));
            }
        }

        public bool IsVariable => _target == CustomTelemetryTarget.Variable;

        private CustomTelemetryKind _kind;
        /// <summary>The owning format's kind, which decides whether the row
        /// shows an offset and a type or a key.</summary>
        public CustomTelemetryKind Kind
        {
            get => _kind;
            set
            {
                if (!SetProperty(ref _kind, value)) return;
                OnPropertyChanged(nameof(IsBinary));
                OnPropertyChanged(nameof(IsJson));
            }
        }

        public bool IsBinary => _kind == CustomTelemetryKind.Binary;
        public bool IsJson => _kind == CustomTelemetryKind.Json;

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value ?? string.Empty);
        }

        private int _offset;
        public int Offset
        {
            get => _offset;
            set => SetProperty(ref _offset, Math.Clamp(value, 0, 65506));
        }

        private CustomTelemetryType _type = CustomTelemetryType.F32;
        public CustomTelemetryType Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }

        private string _key = string.Empty;
        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value ?? string.Empty);
        }

        private double _scale = 1.0;
        public double Scale
        {
            get => _scale;
            set { if (!double.IsInfinity(value)) SetProperty(ref _scale, value); }
        }

        private double _min;
        public double Min
        {
            get => _min;
            set { if (!double.IsInfinity(value)) SetProperty(ref _min, value); }
        }

        private double _max = 1.0;
        public double Max
        {
            get => _max;
            set { if (!double.IsInfinity(value)) SetProperty(ref _max, value); }
        }

        /// <summary>The axis the field's variable keeps
        /// (<see cref="CustomTelemetryField.Axis"/>), carried through the
        /// edit unseen, so a renamed variable keeps its old name's axis.</summary>
        public int Axis { get; set; } = -1;

        public static TelemetryFieldEdit From(CustomTelemetryField field, CustomTelemetryKind kind) => new()
        {
            Target = field.Target,
            Kind = kind,
            Name = field.Name,
            Axis = field.Axis,
            Offset = field.Offset,
            Type = field.Type,
            Key = field.Key,
            Scale = field.Scale,
            Min = field.Min,
            Max = field.Max,
        };

        public CustomTelemetryField ToModel() => new()
        {
            Target = Target,
            Name = Name.Trim(),
            Axis = Axis,
            Offset = Offset,
            Type = Type,
            Key = Key.Trim(),
            Scale = Scale,
            Min = Min,
            Max = Max,
        };
    }

    /// <summary>An item in one of the dialog's pickers: a value and its words.</summary>
    public sealed class TelemetryChoice<T> : INotifyPropertyChanged
    {
        private readonly Func<string> _display;

        public TelemetryChoice(T value, Func<string> display)
        {
            Value = value;
            _display = display;
        }

        public T Value { get; }
        public string Display => _display();

        public event PropertyChangedEventHandler PropertyChanged;
        internal void RaiseDisplayChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        public override string ToString() => Display;
    }

    /// <summary>The pickers' choices, shared by the dialog and the tests.</summary>
    public static class TelemetryChoices
    {
        public static IReadOnlyList<TelemetryChoice<CustomTelemetryKind>> Kinds { get; } = new[]
        {
            new TelemetryChoice<CustomTelemetryKind>(CustomTelemetryKind.Binary, () => Strings.Instance.Telemetry_Formats_KindBinary),
            new TelemetryChoice<CustomTelemetryKind>(CustomTelemetryKind.Json, () => Strings.Instance.Telemetry_Formats_KindJson),
        };

        public static IReadOnlyList<TelemetryChoice<CustomTelemetryTarget>> Targets { get; } = new[]
        {
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Variable, () => Strings.Instance.Telemetry_Target_Variable),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Speed, () => Strings.Instance.Telemetry_Target_Speed),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Rpm, () => Strings.Instance.Telemetry_Target_Rpm),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.MaxRpm, () => Strings.Instance.Telemetry_Target_MaxRpm),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Gear, () => Strings.Instance.Telemetry_Target_Gear),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Throttle, () => Strings.Instance.Telemetry_Target_Throttle),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Brake, () => Strings.Instance.Telemetry_Target_Brake),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Clutch, () => Strings.Instance.Telemetry_Target_Clutch),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Handbrake, () => Strings.Instance.Telemetry_Target_Handbrake),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Steering, () => Strings.Instance.Telemetry_Target_Steering),
            new TelemetryChoice<CustomTelemetryTarget>(CustomTelemetryTarget.Driving, () => Strings.Instance.Telemetry_Target_Driving),
        };

        /// <summary>The binary types by the names the C# and Rust references
        /// write them in, which no language translates.</summary>
        public static IReadOnlyList<TelemetryChoice<CustomTelemetryType>> Types { get; } = new[]
        {
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.U8, () => "u8"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.S8, () => "s8"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.U16, () => "u16"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.S16, () => "s16"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.U32, () => "u32"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.S32, () => "s32"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.F32, () => "f32"),
            new TelemetryChoice<CustomTelemetryType>(CustomTelemetryType.F64, () => "f64"),
        };

        /// <summary>The orders a binary format reads a number's bytes in, as
        /// the Byte Order picker offers them: Little-Endian first, the order
        /// nearly every game on a PC writes (#574). The value is
        /// <see cref="TelemetryFormatEdit.BigEndian"/>.</summary>
        public static IReadOnlyList<TelemetryChoice<bool>> ByteOrders { get; } = new[]
        {
            new TelemetryChoice<bool>(false, () => Strings.Instance.Telemetry_Formats_LittleEndian),
            new TelemetryChoice<bool>(true, () => Strings.Instance.Telemetry_Formats_BigEndian),
        };
    }
}
