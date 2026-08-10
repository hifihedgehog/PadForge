using System;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Sony headset head-tracker source (issue #188). The decode tests are
    /// ports of the reference implementation's own vectors
    /// (NicholasSlattery/sony-head-tracker tests/descriptor_tests.cpp), so
    /// our pure decoder is locked to the behavior the reference proved on
    /// hardware. The wrapper tests lock the stale-window contract and the
    /// single ingest-time frame remap.
    /// </summary>
    public class HeadsetMotionTests
    {
        private static HeadTrackerHid.FieldScale Field(ushort bitSize, ushort count,
            int lmin, int lmax, int pmin = 0, int pmax = 0, sbyte exp = 0)
            => new HeadTrackerHid.FieldScale
            {
                BitSize = bitSize,
                ReportCount = count,
                LogicalMin = lmin,
                LogicalMax = lmax,
                PhysicalMin = pmin,
                PhysicalMax = pmax,
                UnitExponent = exp
            };

        // ── Reference vectors: unit-exponent decode ──

        [Fact]
        public void UnitExponent_SignedNibble()
        {
            Assert.Equal(0, HeadTrackerHid.DecodeUnitExponent(0x00));
            Assert.Equal(-1, HeadTrackerHid.DecodeUnitExponent(0x0F));
            Assert.Equal(-8, HeadTrackerHid.DecodeUnitExponent(0x08));
            Assert.Equal(7, HeadTrackerHid.DecodeUnitExponent(0x07));
        }

        // ── Reference vectors: logical→physical scaling ──

        [Fact]
        public void Scale_LinearAndExponent()
        {
            Assert.Equal(1.0, HeadTrackerHid.Scale(100, 0, 1000, 0, 10, 0), 12);
            Assert.Equal(0.01, HeadTrackerHid.Scale(100, 0, 1000, 0, 10, -2), 12);
        }

        [Fact]
        public void Scale_PassthroughWithoutPhysicalRange()
        {
            Assert.Equal(42.0, HeadTrackerHid.Scale(42, 0, 0, 0, 0, 0));
            Assert.Equal(-7.0, HeadTrackerHid.Scale(-7, -100, 100, 0, 0, 0));
        }

        // ── Reference vectors: packed value extraction ──

        [Fact]
        public void Packed_SignedBitfieldExtraction()
        {
            var allOnes = new byte[] { 0xFF, 0xFF };
            var r = HeadTrackerHid.DecodePackedValues(allOnes, Field(16, 1, -32768, 32767));
            Assert.Single(r);
            Assert.Equal(-1.0, r[0]);

            var minVal = new byte[] { 0x00, 0x80 };
            r = HeadTrackerHid.DecodePackedValues(minVal, Field(16, 1, -32768, 32767));
            Assert.Equal(-32768.0, r[0]);
        }

        [Fact]
        public void Packed_UnsignedMultiValue()
        {
            var bytes = new byte[] { 0x01, 0x02 };
            var r = HeadTrackerHid.DecodePackedValues(bytes, Field(8, 2, 0, 255));
            Assert.Equal(2, r.Length);
            Assert.Equal(1.0, r[0]);
            Assert.Equal(2.0, r[1]);
        }

        [Fact]
        public void Packed_TruncatedBufferReadsZeroHighBits()
        {
            var oneByte = new byte[] { 0xFF };
            var r = HeadTrackerHid.DecodePackedValues(oneByte, Field(16, 1, -32768, 32767));
            Assert.Single(r);
            Assert.Equal(255.0, r[0]); // sign bit 15 absent → positive
        }

        [Fact]
        public void Packed_EmptyBufferYieldsZeroes()
        {
            var r = HeadTrackerHid.DecodePackedValues(ReadOnlySpan<byte>.Empty, Field(16, 3, 0, 65535));
            Assert.Equal(3, r.Length);
            foreach (var v in r) Assert.Equal(0.0, v);
        }

        [Fact]
        public void Packed_DegenerateBitSizeReturnsEmpty()
        {
            var bytes = new byte[8];
            Assert.Empty(HeadTrackerHid.DecodePackedValues(bytes, Field(0, 1, 0, 1)));
            Assert.Empty(HeadTrackerHid.DecodePackedValues(bytes, Field(64, 1, 0, 1)));
        }

        [Fact]
        public void HidSigned_VariableWidth()
        {
            Assert.Equal(-1, HeadTrackerHid.HidSigned(0xFF, 1));
            Assert.Equal(-128, HeadTrackerHid.HidSigned(0x80, 1));
            Assert.Equal(127, HeadTrackerHid.HidSigned(0x7F, 1));
            Assert.Equal(-1, HeadTrackerHid.HidSigned(0xFFFF, 2));
            Assert.Equal(0x1234, HeadTrackerHid.HidSigned(0x1234, 4));
            Assert.Equal(0, HeadTrackerHid.HidSigned(0, 0));
        }

        // ── Enable sequence: interval target computation ──

        [Fact]
        public void Interval_ProtocolWindowReachable_TargetsTenMs()
        {
            // Device advertises 5..100 ms in 10^-3 s units: the protocol's
            // 10 ms target is reachable, so encode 10 (units).
            Assert.Equal(10, HeadTrackerHid.ComputeIntervalTarget(5, 100, -3));
        }

        [Fact]
        public void Interval_Xm5FloorWins()
        {
            // The WH-1000XM5 advertises a 40 ms floor: 40 ms > 20 ms target
            // window, so the fastest advertised interval is used verbatim
            // (reference warning branch).
            Assert.Equal(40, HeadTrackerHid.ComputeIntervalTarget(40, 1000, -3));
        }

        [Fact]
        public void Interval_ClampedToAdvertisedRange()
        {
            // Range entirely below 10 ms (1..5 ms): supportedHigh < 0.010
            // takes the fastest advertised interval, clamped in range.
            Assert.Equal(1, HeadTrackerHid.ComputeIntervalTarget(1, 5, -3));
        }

        [Fact]
        public void Interval_ReversedPhysicalRange()
        {
            // Descriptors may order the physical range max-first; the
            // computation normalizes before clamping.
            Assert.Equal(40, HeadTrackerHid.ComputeIntervalTarget(1000, 40, -3));
        }

        // ── Description trim ──

        [Fact]
        public void TrimDescription_StripsTrailingPadding()
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(HeadTrackerHid.Marker + "v1");
            var padded = new byte[bytes.Length + 4];
            Array.Copy(bytes, padded, bytes.Length);
            padded[^1] = 0xFF; padded[^2] = 0x00; padded[^3] = 0xFF; padded[^4] = 0x00;
            Assert.Equal(HeadTrackerHid.Marker + "v1", HeadTrackerHid.TrimDescription(padded));
        }

        // ── Rotation-only fallback math ──

        [Fact]
        public void AngularRate_PureAxisRotation()
        {
            // 0.1 rad about X between samples 40 ms apart → 2.5 rad/s about X.
            var rate = new double[3];
            Assert.True(HeadTrackerMath.AngularRateFromRotationVectors(
                0.2, 0, 0, 0.3, 0, 0, 0.040, rate));
            Assert.Equal(2.5, rate[0], 9);
            Assert.Equal(0.0, rate[1], 9);
            Assert.Equal(0.0, rate[2], 9);
        }

        [Fact]
        public void AngularRate_IdenticalSamplesAreZero()
        {
            var rate = new double[] { 9, 9, 9 };
            Assert.True(HeadTrackerMath.AngularRateFromRotationVectors(
                0.1, 0.2, 0.3, 0.1, 0.2, 0.3, 0.040, rate));
            Assert.Equal(0.0, rate[0], 9);
            Assert.Equal(0.0, rate[1], 9);
            Assert.Equal(0.0, rate[2], 9);
        }

        [Fact]
        public void AngularRate_NonPositiveDtRefused()
        {
            var rate = new double[3];
            Assert.False(HeadTrackerMath.AngularRateFromRotationVectors(
                0, 0, 0, 0.1, 0, 0, 0, rate));
        }

        [Fact]
        public void SynthesizedGyro_UsesThePreviousSampleNotItself()
        {
            // Regression: the fallback once overwrote its stored previous
            // rotation before computing the delta (the snapshot aliased the
            // storage array), so every synthesized rate was zero.
            long now = 0;
            using var dev = MakeDevice(() => now);
            var rate = new double[3];
            long freq = System.Diagnostics.Stopwatch.Frequency;

            // First sample only primes the storage.
            Assert.False(dev.SynthesizeGyroFromRotation(0.2, 0, 0, now, rate));

            // Second sample 40 ms later: 0.1 rad about X → 2.5 rad/s.
            now += freq * 40 / 1000;
            Assert.True(dev.SynthesizeGyroFromRotation(0.3, 0, 0, now, rate));
            Assert.Equal(2.5, rate[0], 6);
            Assert.Equal(0.0, rate[1], 6);
            Assert.Equal(0.0, rate[2], 6);
        }

        // ── Wrapper: identity and capability surface ──

        private static SonyHeadsetMotionDevice MakeDevice(Func<long> ticks = null, bool hasAccel = true)
            => new SonyHeadsetMotionDevice(new SonyHeadsetMotionRuntime.Candidate
            {
                Path = @"\\?\hid#vid_054c&pid_0df0#7&test#{4d1e55b2}",
                Name = "WH-1000XM5",
                VendorId = 0x054C,
                ProductId = 0x0DF0,
                HasAccel = hasAccel
            }, ticks);

        [Fact]
        public void Wrapper_IdentityAndCapType()
        {
            using var dev = MakeDevice();
            Assert.Equal(InputDeviceType.HeadsetMotion, dev.GetInputDeviceType());
            Assert.True(dev.HasGyro);
            Assert.True(dev.HasAccel);
            Assert.Equal(0, dev.NumAxes);
            Assert.Equal(0, dev.NumButtons);
            Assert.NotEqual(Guid.Empty, dev.InstanceGuid);
            // Identity is derived from the HID path, so the same path
            // always reconnects to the same slot assignments.
            using var again = MakeDevice();
            Assert.Equal(dev.InstanceGuid, again.InstanceGuid);
        }

        [Fact]
        public void Wrapper_AccelAdvertisedOnlyWhenDescriptorHasIt()
        {
            using var dev = MakeDevice(hasAccel: false);
            Assert.False(dev.HasAccel);
            Assert.True(dev.HasGyro);
        }

        // ── Wrapper: stale-window contract ──

        [Fact]
        public void StaleWindow_BaselineBeforeFirstSample_NullAfterSilence()
        {
            long now = 0;
            using var dev = MakeDevice(() => now);
            dev.AttachForTest();

            // Before any sample: centered baseline, not offline.
            Assert.NotNull(dev.GetCurrentState());

            dev.InjectSample(new double[] { 1, 2, 3 }, null);
            Assert.NotNull(dev.GetCurrentState());

            // Inside the window the last sample holds.
            now += (SonyHeadsetMotionDevice.StaleWindowMs - 500)
                * System.Diagnostics.Stopwatch.Frequency / 1000;
            Assert.NotNull(dev.GetCurrentState());

            // Past the window: offline (null) so held gyro aim releases.
            now += System.Diagnostics.Stopwatch.Frequency; // +1 s
            Assert.Null(dev.GetCurrentState());

            // A fresh sample revives the device.
            dev.InjectSample(new double[] { 0, 0, 0 }, null);
            Assert.NotNull(dev.GetCurrentState());
        }

        [Fact]
        public void GetCurrentState_NullWhenNeverOpened()
        {
            using var dev = MakeDevice();
            // Not attached (Open never ran): offline.
            Assert.Null(dev.GetCurrentState());
        }

        // ── Wrapper: single-seam frame remap ──

        [Fact]
        public void FrameRemap_ReferenceMapAppliedAtIngest()
        {
            // Reference types.hpp: source {1,0,2}, sign {-1,+1,-1}, where
            // out[i] = in[source[i]] * sign[i] (math.cpp remap), applied to
            // gyro and accel alike (orientation.cpp:18-20).
            long now = 0;
            using var dev = MakeDevice(() => now);
            dev.AttachForTest();
            dev.InjectSample(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 });
            var state = dev.GetCurrentState();
            Assert.NotNull(state);
            Assert.Equal(-2f, state.Gyro[0], 5);
            Assert.Equal(1f, state.Gyro[1], 5);
            Assert.Equal(-3f, state.Gyro[2], 5);
            Assert.Equal(-5f, state.Accel[0], 5);
            Assert.Equal(4f, state.Accel[1], 5);
            Assert.Equal(-6f, state.Accel[2], 5);
        }

        // ── Native layout guards ──

        [Fact]
        public void BluetoothStructs_MarshalToNativeSizes()
        {
            // BLUETOOTH_DEVICE_INFO is 560 bytes native (dwSize 4 + pad 4 +
            // 8-aligned address union + ...). A 2-aligned address struct
            // shrank it to 556, BluetoothFindFirstDevice rejected dwSize
            // with ERROR_REVISION_MISMATCH (1306), and every repair path
            // read "no paired devices" (hardware-diagnosed 2026-08-07).
            Assert.Equal(560, PadForge.Services.HeadsetTrackerRepair.DeviceInfoMarshalSize);
            Assert.Equal(40, PadForge.Services.HeadsetTrackerRepair.SearchParamsMarshalSize);
        }

        // ── BTHENUM address extraction (repair name resolution) ──

        [Fact]
        public void BthenumAddress_DevPrefix()
        {
            Assert.True(PadForge.Services.HeadsetTrackerRepair.TryParseBthenumAddress(
                @"BTHENUM\DEV_581862893796\9&1479B2EE&0&BLUETOOTHDEVICE_581862893796", out ulong a));
            Assert.Equal(0x581862893796UL, a);
        }

        [Fact]
        public void BthenumAddress_ServiceChildDelimiter()
        {
            // Service children end with ...&0&<address>_C00000000; the GUID's
            // own 12-hex runs must not be mistaken for the address.
            Assert.True(PadForge.Services.HeadsetTrackerRepair.TryParseBthenumAddress(
                @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0DF0\9&1479B2EE&0&581862893796_C00000000", out ulong a));
            Assert.Equal(0x581862893796UL, a);
        }

        [Fact]
        public void BthenumAddress_RejectsZeroAndNonBthenumRuns()
        {
            // All-zero address (an unpaired service child) is not an identity.
            Assert.False(PadForge.Services.HeadsetTrackerRepair.TryParseBthenumAddress(
                @"BTHENUM\{1CB831EA-79CD-4508-B0FC-85F7C85AE8E0}_LOCALMFG&0000\9&3F90950&0&000000000000_00000002", out _));
            Assert.False(PadForge.Services.HeadsetTrackerRepair.TryParseBthenumAddress("", out _));
            Assert.False(PadForge.Services.HeadsetTrackerRepair.TryParseBthenumAddress(null, out _));
        }

        // ── Field classification ──

        [Fact]
        public void BuildParsedFields_ClassifiesProtocolUsages()
        {
            SonyHeadsetHid.HIDP_VALUE_CAPS Cap(ushort usage, ushort page = 0x20) =>
                new SonyHeadsetHid.HIDP_VALUE_CAPS
                {
                    UsagePage = page,
                    UsageMin = usage,
                    ReportID = 1,
                    BitSize = 16,
                    ReportCount = usage == HeadTrackerHid.Rotation
                        || usage == HeadTrackerHid.AngularVelocity ? (ushort)3 : (ushort)1,
                    LogicalMin = -32768,
                    LogicalMax = 32767
                };

            var fields = SonyHeadsetHid.BuildParsedFields(new[]
            {
                Cap(HeadTrackerHid.Rotation),
                Cap(HeadTrackerHid.AngularVelocity),
                Cap(HeadTrackerHid.AccelerationY),
                Cap(HeadTrackerHid.AngularVelocityZ),
                Cap(HeadTrackerHid.ResetCounter),
                Cap(0x0300),          // unrelated sensor usage → dropped
                Cap(0x0544, 0x01)     // wrong page → dropped
            });

            Assert.Equal(5, fields.Length);
            Assert.Equal(SonyHeadsetHid.FieldKind.Rotation, fields[0].Kind);
            Assert.Equal(SonyHeadsetHid.FieldKind.GyroVector, fields[1].Kind);
            Assert.Equal(SonyHeadsetHid.FieldKind.AccelScalar, fields[2].Kind);
            Assert.Equal(1, fields[2].Axis); // Y
            Assert.Equal(SonyHeadsetHid.FieldKind.GyroScalar, fields[3].Kind);
            Assert.Equal(2, fields[3].Axis); // Z
            Assert.Equal(SonyHeadsetHid.FieldKind.ResetCounter, fields[4].Kind);
        }
    }
}
