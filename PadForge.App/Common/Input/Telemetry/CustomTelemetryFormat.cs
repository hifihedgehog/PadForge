using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Xml.Serialization;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>How a custom format's packets are written (issue #531).
    /// Saved by name. APPEND-ONLY.</summary>
    public enum CustomTelemetryKind
    {
        Binary = 0,
        Json = 1,
    }

    /// <summary>What a custom field feeds. Saved by name. APPEND-ONLY.</summary>
    public enum CustomTelemetryTarget
    {
        /// <summary>A named variable: an axis on the row and a Custom
        /// Expression channel.</summary>
        Variable = 0,
        /// <summary>Speed in km/h after the scale.</summary>
        Speed = 1,
        Rpm = 2,
        MaxRpm = 3,
        /// <summary>A gear number after the scale: below 0 reverse, 0
        /// neutral, unless the format names its own two values.</summary>
        Gear = 4,
        /// <summary>The pedals, 0 to 1 after the scale.</summary>
        Throttle = 5,
        Brake = 6,
        Clutch = 7,
        Handbrake = 8,
        /// <summary>-1 full left to 1 full right after the scale.</summary>
        Steering = 9,
        /// <summary>Any value but 0 means driving.</summary>
        Driving = 10,
    }

    /// <summary>A binary field's type. Saved by name. APPEND-ONLY.</summary>
    public enum CustomTelemetryType
    {
        U8 = 0,
        S8 = 1,
        U16 = 2,
        S16 = 3,
        U32 = 4,
        S32 = 5,
        F32 = 6,
        F64 = 7,
    }

    /// <summary>
    /// A packet layout a user writes for a game PadForge does not know
    /// (issue #531, the "manually write variables" ask in discussion #525).
    /// Global, not per profile: the profile picks which format reads.
    /// </summary>
    public sealed class CustomTelemetryFormat
    {
        [XmlAttribute] public string Name { get; set; } = string.Empty;
        [XmlAttribute] public CustomTelemetryKind Kind { get; set; } = CustomTelemetryKind.Binary;

        /// <summary>A binary packet's exact length, or 0 for any. A format
        /// with no length and no key is read only when picked by name, so
        /// Auto-Detect never hands it every packet.</summary>
        [XmlAttribute] public int Length { get; set; }

        /// <summary>A JSON key every packet of this format carries, or empty.</summary>
        [XmlAttribute] public string Key { get; set; } = string.Empty;

        [XmlAttribute] public bool BigEndian { get; set; }

        /// <summary>When set, the gear field's value <see cref="GearReverse"/>
        /// means reverse and <see cref="GearNeutral"/> neutral, as in Forza's
        /// 0 and 11.</summary>
        [XmlAttribute] public bool UseGearValues { get; set; }
        [XmlAttribute] public int GearReverse { get; set; } = -1;
        [XmlAttribute] public int GearNeutral { get; set; }

        [XmlElement("Field")]
        public List<CustomTelemetryField> Fields { get; set; } = new();

        public CustomTelemetryFormat Clone()
        {
            var copy = (CustomTelemetryFormat)MemberwiseClone();
            copy.Fields = new List<CustomTelemetryField>(Fields.Count);
            foreach (var field in Fields) copy.Fields.Add(field?.Clone() ?? new CustomTelemetryField());
            return copy;
        }

        /// <summary>A name can carry neither of the two characters the
        /// expression variable's spec splits on.</summary>
        public static bool IsValidName(string name)
            => !string.IsNullOrWhiteSpace(name) && name.IndexOf(':') < 0 && name.IndexOf('|') < 0;
    }

    /// <summary>One field of a <see cref="CustomTelemetryFormat"/>.</summary>
    public sealed class CustomTelemetryField
    {
        [XmlAttribute] public CustomTelemetryTarget Target { get; set; } = CustomTelemetryTarget.Variable;

        /// <summary>The variable's name when <see cref="Target"/> is Variable.</summary>
        [XmlAttribute] public string Name { get; set; } = string.Empty;

        [XmlAttribute] public int Offset { get; set; }

        /// <summary>A variable's axis on the row, counted after the standard
        /// axes, or -1 before one is given. The runtime gives a name an axis
        /// once and keeps it while the name is in use, so deleting or
        /// reordering a variable never moves another one's axis and a saved
        /// mapping keeps reading its variable. Written here so the axis
        /// survives a restart. A file from before has none, and its
        /// variables take the axes in the order they are first used, which
        /// is where they were.</summary>
        [XmlAttribute] public int Axis { get; set; } = -1;
        public bool ShouldSerializeAxis() => Target == CustomTelemetryTarget.Variable && Axis >= 0;
        [XmlAttribute] public CustomTelemetryType Type { get; set; } = CustomTelemetryType.F32;

        /// <summary>A JSON format's key, dots for nested objects.</summary>
        [XmlAttribute] public string Key { get; set; } = string.Empty;

        // A value that is not finite leaves each number as it was, the
        // MappingSource rule, so a hand-edited "NaN" in the settings file
        // never reaches the reader.

        /// <summary>The raw value is multiplied by this.</summary>
        [XmlAttribute] public double Scale { get => _scale; set => _scale = Finite(value, _scale); }
        private double _scale = 1.0;

        /// <summary>A variable's axis runs from Min (at rest) to Max (full).</summary>
        [XmlAttribute] public double Min { get => _min; set => _min = Finite(value, _min); }
        private double _min;

        [XmlAttribute] public double Max { get => _max; set => _max = Finite(value, _max); }
        private double _max = 1.0;

        private static double Finite(double value, double current) => double.IsFinite(value) ? value : current;

        public CustomTelemetryField Clone() => (CustomTelemetryField)MemberwiseClone();

        internal static int SizeOf(CustomTelemetryType type) => type switch
        {
            CustomTelemetryType.U8 or CustomTelemetryType.S8 => 1,
            CustomTelemetryType.U16 or CustomTelemetryType.S16 => 2,
            CustomTelemetryType.U32 or CustomTelemetryType.S32 or CustomTelemetryType.F32 => 4,
            _ => 8,
        };
    }

    /// <summary>A custom variable as the row and the expression source see
    /// it: its name, the span its axis covers, and the axis it keeps on the
    /// row, counted after the standard axes (0 is the row's axis 7), or -1
    /// when every axis is taken and it reads as an expression channel
    /// alone.</summary>
    internal sealed record TelemetryVariable(string Name, double Min, double Max, int Slot);

    /// <summary>
    /// A custom format ready to read: each variable field already holds its
    /// index in the global variable list. Built by
    /// <see cref="GameTelemetryRuntime"/> whenever the list changes.
    /// </summary>
    internal sealed class CompiledTelemetryFormat
    {
        private readonly CustomTelemetryFormat _format;
        private readonly int[] _variableIndex;

        public string Name => _format.Name;

        /// <summary>True when Auto-Detect may try this format: it names a
        /// length or a key.</summary>
        public bool Detectable => _format.Kind == CustomTelemetryKind.Json
            ? !string.IsNullOrEmpty(_format.Key)
            : _format.Length > 0;

        public CompiledTelemetryFormat(CustomTelemetryFormat format, IReadOnlyList<TelemetryVariable> variables)
        {
            _format = format.Clone();
            _variableIndex = new int[_format.Fields.Count];
            for (int i = 0; i < _variableIndex.Length; i++)
            {
                _variableIndex[i] = -1;
                var field = _format.Fields[i];
                if (field.Target != CustomTelemetryTarget.Variable) continue;
                for (int v = 0; v < variables.Count; v++)
                {
                    if (string.Equals(variables[v].Name, field.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        _variableIndex[i] = v;
                        break;
                    }
                }
            }
        }

        /// <summary>Reads a packet. False when it does not match: the wrong
        /// length, not JSON, or missing the format's key. When
        /// <paramref name="pinned"/> is set the format was picked by name, so
        /// a format with no length or key reads any packet.</summary>
        public bool TryRead(ReadOnlySpan<byte> p, TelemetryFrame f, bool pinned)
        {
            if (!pinned && !Detectable) return false;
            bool ok = _format.Kind == CustomTelemetryKind.Json ? TryReadJson(p, f) : TryReadBinary(p, f);
            if (!ok) return false;
            f.Source = TelemetrySource.Custom;
            f.CustomName = _format.Name;
            return true;
        }

        private bool TryReadBinary(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            if (_format.Length > 0 && p.Length != _format.Length) return false;
            var fields = _format.Fields;
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field == null) continue;
                int size = CustomTelemetryField.SizeOf(field.Type);
                // Subtracted, so an offset near int.MaxValue cannot overflow
                // past the check.
                if (field.Offset < 0 || field.Offset > p.Length - size) continue;
                double raw = ReadBinary(p.Slice(field.Offset, size), field.Type, _format.BigEndian);
                Apply(field, i, raw, f);
            }
            return true;
        }

        private bool TryReadJson(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            if (!TelemetryDecoders.StartsWithObject(p)) return false;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(p.ToArray()); }
            catch (JsonException) { return false; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (!string.IsNullOrEmpty(_format.Key) && !TryFind(root, _format.Key, out _)) return false;
                var fields = _format.Fields;
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    if (field == null || string.IsNullOrEmpty(field.Key)) continue;
                    if (!TryFind(root, field.Key, out var element)) continue;
                    if (!TryNumber(element, out double raw)) continue;
                    Apply(field, i, raw, f);
                }
            }
            return true;
        }

        private void Apply(CustomTelemetryField field, int index, double raw, TelemetryFrame f)
        {
            if (!double.IsFinite(raw)) return;
            double v = raw * field.Scale;
            // A product past a double's range leaves the target as it was.
            if (!double.IsFinite(v)) return;
            switch (field.Target)
            {
                case CustomTelemetryTarget.Speed:
                    f.SpeedMs = (float)(v / 3.6);
                    f.SpeedReported = true;
                    break;
                case CustomTelemetryTarget.Rpm: f.Rpm = (float)v; break;
                case CustomTelemetryTarget.MaxRpm: f.MaxRpm = (float)v; break;
                case CustomTelemetryTarget.Gear: f.Gear = Gear(v); break;
                case CustomTelemetryTarget.Throttle: f.Throttle = (float)v; break;
                case CustomTelemetryTarget.Brake: f.Brake = (float)v; break;
                case CustomTelemetryTarget.Clutch: f.Clutch = (float)v; break;
                case CustomTelemetryTarget.Handbrake: f.Handbrake = (float)v; break;
                case CustomTelemetryTarget.Steering: f.Steering = (float)v; break;
                case CustomTelemetryTarget.Driving:
                    f.Driving = v != 0;
                    f.DrivingReported = true;
                    break;
                default:
                    int at = _variableIndex[index];
                    if (at >= 0 && at < f.Variables.Length) f.Variables[at] = (float)v;
                    break;
            }
        }

        private int Gear(double v)
        {
            int g = (int)Math.Round(v);
            if (_format.UseGearValues)
            {
                if (g == _format.GearReverse) return -1;
                if (g == _format.GearNeutral) return 0;
                return g < 0 ? -1 : g;
            }
            return g < 0 ? -1 : g;
        }

        internal static double ReadBinary(ReadOnlySpan<byte> b, CustomTelemetryType type, bool bigEndian)
        {
            switch (type)
            {
                case CustomTelemetryType.U8: return b[0];
                case CustomTelemetryType.S8: return (sbyte)b[0];
                case CustomTelemetryType.U16: return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
                case CustomTelemetryType.S16: return bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
                case CustomTelemetryType.U32: return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
                case CustomTelemetryType.S32: return bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
                case CustomTelemetryType.F32: return bigEndian ? BinaryPrimitives.ReadSingleBigEndian(b) : BinaryPrimitives.ReadSingleLittleEndian(b);
                default: return bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(b) : BinaryPrimitives.ReadDoubleLittleEndian(b);
            }
        }

        /// <summary>Walks a dotted key through nested objects.</summary>
        internal static bool TryFind(JsonElement root, string key, out JsonElement element)
        {
            element = root;
            foreach (string part in key.Split('.'))
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part.Trim(), out element))
                    return false;
            }
            return true;
        }

        /// <summary>A number, a boolean as 1 or 0, or a numeric string.</summary>
        internal static bool TryNumber(JsonElement element, out double value)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    value = element.GetDouble();
                    return true;
                case JsonValueKind.True:
                    value = 1;
                    return true;
                case JsonValueKind.False:
                    value = 0;
                    return true;
                case JsonValueKind.String:
                    return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                default:
                    value = 0;
                    return false;
            }
        }
    }
}
