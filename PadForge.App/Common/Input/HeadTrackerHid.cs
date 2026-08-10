using System;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Pure HID report-descriptor value decoding for the Android Head
    /// Tracker sensor collection (issue #188). Direct port of the
    /// reference implementation's hid_descriptor.cpp
    /// (NicholasSlattery/sony-head-tracker, MIT): logical-to-physical
    /// scaling, HID unit-exponent nibble decode, and LSB-first packed
    /// value-array extraction with sign extension. No Windows calls, so
    /// every path is unit-testable against the reference's own vectors.
    /// </summary>
    internal static class HeadTrackerHid
    {
        // HID sensor-page usages for the Android Head Tracker profile
        // (reference hid_usages.hpp).
        internal const ushort SensorPage = 0x20;
        internal const ushort OtherCustom = 0xE1;
        internal const ushort SensorDescription = 0x0308;
        internal const ushort ReportInterval = 0x030E;
        internal const ushort ReportingAllEvents = 0x0841;
        internal const ushort PowerFull = 0x0851;
        internal const ushort TransportAcl = 0xF800;
        internal const ushort Rotation = 0x0544;          // orientation rotation vector
        internal const ushort AngularVelocity = 0x0545;   // gyroscope (rad/s), vector form
        internal const ushort ResetCounter = 0x0546;
        internal const ushort AccelerationVector = 0x0452;
        internal const ushort AccelerationX = 0x0453;
        internal const ushort AccelerationY = 0x0454;
        internal const ushort AccelerationZ = 0x0455;
        internal const ushort AngularVelocityVector = 0x0456;
        internal const ushort AngularVelocityX = 0x0457;
        internal const ushort AngularVelocityY = 0x0458;
        internal const ushort AngularVelocityZ = 0x0459;

        /// <summary>Sensor-description marker that qualifies a candidate
        /// collection as an Android Head Tracker (reference hid_usages.hpp).</summary>
        internal const string Marker = "#AndroidHeadTracker#";

        /// <summary>The scaling facts of one descriptor value field, the
        /// subset of HIDP_VALUE_CAPS the pure decode needs.</summary>
        internal struct FieldScale
        {
            public ushort BitSize;
            public ushort ReportCount;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public sbyte UnitExponent;
        }

        /// <summary>Logical-to-physical scaling honoring the unit exponent
        /// (reference descriptorScale). A degenerate logical or physical
        /// range passes the raw value through.</summary>
        internal static double Scale(long raw, int lmin, int lmax, int pmin, int pmax, sbyte exponent)
        {
            if (lmax == lmin || (pmax == 0 && pmin == 0)) return raw;
            double fraction = (raw - (double)lmin) / ((double)lmax - lmin);
            return (pmin + fraction * ((double)pmax - pmin)) * Math.Pow(10.0, exponent);
        }

        /// <summary>HID unit exponents are a signed 4-bit nibble
        /// (reference decodeHidUnitExponent): 0xF is -1, 0x8 is -8.</summary>
        internal static sbyte DecodeUnitExponent(uint exponent)
        {
            int nibble = (int)(exponent & 0x0F);
            return (sbyte)(nibble >= 8 ? nibble - 16 : nibble);
        }

        /// <summary>
        /// Decodes a packed HidP_GetUsageValueArray buffer: ReportCount
        /// values of BitSize bits each, LSB-first, sign-extended when the
        /// logical range is signed, then scaled (reference
        /// decodePackedDescriptorValues). Bits past the end of the buffer
        /// read as zero; a zero or &gt;63 bit size yields an empty result.
        /// </summary>
        internal static double[] DecodePackedValues(ReadOnlySpan<byte> packed, in FieldScale field)
        {
            if (field.BitSize == 0 || field.BitSize > 63) return Array.Empty<double>();
            var result = new double[field.ReportCount];
            for (int valueIndex = 0; valueIndex < field.ReportCount; valueIndex++)
            {
                ulong raw = 0;
                long offset = (long)valueIndex * field.BitSize;
                for (int bitIndex = 0; bitIndex < field.BitSize; bitIndex++)
                {
                    long bit = offset + bitIndex;
                    if (bit / 8 < packed.Length && (packed[(int)(bit / 8)] & (1 << (int)(bit % 8))) != 0)
                        raw |= 1UL << bitIndex;
                }
                long value = (long)raw;
                if (field.LogicalMin < 0)
                {
                    ulong sign = 1UL << (field.BitSize - 1);
                    ulong mask = (1UL << field.BitSize) - 1;
                    value = (long)(((raw & mask) ^ sign) - sign);
                }
                result[valueIndex] = Scale(value, field.LogicalMin, field.LogicalMax,
                    field.PhysicalMin, field.PhysicalMax, field.UnitExponent);
            }
            return result;
        }

        /// <summary>Sign extension of a raw HID item value of 1/2/4 bytes
        /// (reference hidSigned). Zero bytes decodes to zero.</summary>
        internal static long HidSigned(uint value, int bytes)
        {
            if (bytes == 0) return 0;
            int bits = bytes * 8;
            if (bits >= 32) return (int)value;
            uint sign = 1u << (bits - 1);
            uint mask = (1u << bits) - 1;
            value &= mask;
            return (int)((value ^ sign) - sign);
        }

        /// <summary>
        /// Computes the report-interval value to encode, in the device's
        /// own units, from the field's physical range and unit exponent
        /// (reference configureHeadTrackerFeatures interval branch). The
        /// protocol targets 10-20 ms; a device whose advertised range
        /// cannot reach that window gets its fastest advertised interval
        /// instead (the WH-1000XM5 advertises a 40 ms floor).
        /// </summary>
        internal static long ComputeIntervalTarget(int physicalMin, int physicalMax, sbyte unitExponent)
        {
            int low = Math.Min(physicalMin, physicalMax);
            int high = Math.Max(physicalMin, physicalMax);
            double scale = Math.Pow(10.0, unitExponent);
            double supportedLow = low * scale;
            double supportedHigh = high * scale;
            double targetSeconds = Math.Max(0.010, supportedLow);
            if (targetSeconds > 0.020 || supportedHigh < 0.010)
                targetSeconds = supportedLow;
            long target = (long)Math.Round(targetSeconds / scale, MidpointRounding.AwayFromZero);
            return Math.Clamp(target, low, high);
        }

        /// <summary>
        /// Trims trailing 0x00 / 0xFF padding from a feature-report window
        /// and decodes it as the sensor-description string (the tail
        /// handling both marker fallbacks in the reference's
        /// extractDescription share).
        /// </summary>
        internal static string TrimDescription(ReadOnlySpan<byte> bytes)
        {
            int end = bytes.Length;
            while (end > 0 && (bytes[end - 1] == 0x00 || bytes[end - 1] == 0xFF)) end--;
            return System.Text.Encoding.ASCII.GetString(bytes.Slice(0, end));
        }
    }
}
