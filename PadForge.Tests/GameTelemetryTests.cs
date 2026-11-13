using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.Telemetry;
using PadForge.Common.Telemetry;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Game telemetry (#531, discussion #525): every built-in format decoded
    /// from packets built at the offsets the cloned references give,
    /// Auto-Detect and its hold, custom formats, the Game Telemetry row's axes
    /// and buttons, the forward port over loopback, and the values Custom
    /// Expression variables read.
    /// </summary>
    // GameTelemetryRuntime's values and settings are statics the macro
    // evaluator reads, so this class runs with the other macro classes.
    [Collection("SettingsManagerStatics")]
    public sealed class GameTelemetryTests : IDisposable
    {
        public GameTelemetryTests() => GameTelemetryRuntime.ResetForTest();
        public void Dispose() => GameTelemetryRuntime.ResetForTest();

        // ── Packet builders ──

        private static void F32(byte[] p, int at, float v) => BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(at), v);
        private static void F64(byte[] p, int at, double v) => BinaryPrimitives.WriteDoubleLittleEndian(p.AsSpan(at), v);
        private static void I32(byte[] p, int at, int v) => BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(at), v);
        private static void U16(byte[] p, int at, int v) => BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(at), (ushort)v);
        private static void U32(byte[] p, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(at), v);

        /// <summary>A Forza Dash packet (forza-telemetry FMData.cs offsets),
        /// shifted by 12 from byte 232 on for Horizon.</summary>
        internal static byte[] Forza(int length, float speedMs = 30f, byte gear = 3, sbyte steer = 64, bool raceOn = true)
        {
            var p = new byte[length];
            int o = length is 323 or 324 ? 12 : 0;
            I32(p, 0, raceOn ? 1 : 0);
            F32(p, 8, 8000f);
            F32(p, 16, 6000f);
            F32(p, 244 + o, speedMs);
            p[303 + o] = 255;   // accel
            p[304 + o] = 51;    // brake 0.2
            p[305 + o] = 0;     // clutch
            p[306 + o] = 255;   // handbrake
            p[307 + o] = gear;
            p[308 + o] = (byte)steer;
            return p;
        }

        internal static byte[] F1(int year, int id, int length, int car)
        {
            var p = new byte[length];
            U16(p, 0, year);
            if (year == 2022) { p[5] = (byte)id; p[22] = (byte)car; }
            else { p[2] = (byte)(year - 2000); p[6] = (byte)id; p[27] = (byte)car; }
            return p;
        }

        internal static byte[] F1Telemetry(int year, int car)
        {
            int header = year == 2022 ? 24 : 29;
            var p = F1(year, 6, year == 2022 ? 1347 : 1352, car);
            int e = header + car * 60;
            U16(p, e, 180);         // km/h
            F32(p, e + 2, 0.75f);   // throttle
            F32(p, e + 6, -0.5f);   // steer
            F32(p, e + 10, 0.25f);  // brake
            p[e + 14] = 40;         // clutch, percent
            p[e + 15] = unchecked((byte)(sbyte)-1); // reverse
            U16(p, e + 16, 11000);  // rpm
            return p;
        }

        internal static byte[] F1Status(int year, int car, int maxRpm)
        {
            int header = year == 2022 ? 24 : 29;
            int entry = year == 2022 ? 47 : 55;
            var p = F1(year, 7, year == 2022 ? 1058 : 1239, car);
            U16(p, header + car * entry + 17, maxRpm);
            return p;
        }

        internal static byte[] Codemasters(float gear = 10f)
        {
            var p = new byte[264];
            F32(p, 28, 20f);
            F32(p, 116, 0.5f);
            F32(p, 120, 0.25f);
            F32(p, 124, 0.1f);
            F32(p, 128, 0f);
            F32(p, 132, gear);
            F32(p, 148, 650f);
            F32(p, 252, 750f);
            return p;
        }

        /// <summary>EA SPORTS WRC's default session_update, tight (237),
        /// padded after shiftlights_rpm_valid (240), and with the 4-byte
        /// fourcc in front.</summary>
        internal static byte[] Wrc(bool padded, bool fourcc)
        {
            int pad = padded ? 3 : 0;
            int start = fourcc ? 4 : 0;
            var p = new byte[start + 237 + pad];
            if (fourcc) Encoding.ASCII.GetBytes("SESU").CopyTo(p, 0);
            p[start + 37 + pad] = 3;   // gear index
            p[start + 38 + pad] = 0;   // neutral index
            p[start + 39 + pad] = 10;  // reverse index
            F32(p, start + 41 + pad, 30f);
            F32(p, start + 185 + pad, 8000f);
            F32(p, start + 193 + pad, 6000f);
            F32(p, start + 197 + pad, 0.9f);
            F32(p, start + 201 + pad, 0.1f);
            F32(p, start + 205 + pad, 0f);
            F32(p, start + 209 + pad, 0.5f);
            F32(p, start + 213 + pad, 1f);
            F64(p, start + 221 + pad, 1234.5);
            return p;
        }

        internal static byte[] OutGauge(string car, byte gear, int length = 92)
        {
            var p = new byte[length];
            Encoding.ASCII.GetBytes(car).CopyTo(p, 4);
            p[10] = gear;
            F32(p, 12, 25f);
            F32(p, 16, 4000f);
            U32(p, 40, 0x7FF);   // available
            U32(p, 44, 4);       // handbrake on
            F32(p, 48, 0.6f);
            F32(p, 52, 0.2f);
            F32(p, 56, 0f);
            return p;
        }

        internal static byte[] ProjectCars2GameState(int state)
        {
            var p = new byte[24];
            p[10] = 4;
            p[14] = (byte)(state | (3 << 3));
            return p;
        }

        internal static byte[] ProjectCars2Telemetry()
        {
            var p = new byte[559];
            p[10] = 0;
            p[29] = 51;      // brake
            p[30] = 255;     // throttle
            p[31] = 0;       // clutch
            F32(p, 36, 40f);
            U16(p, 40, 7000);
            U16(p, 42, 8000);
            p[44] = unchecked((byte)(sbyte)-127);
            p[45] = 0x6F;    // six gears, reverse
            p[370] = 255;    // handbrake
            return p;
        }

        internal static (byte[] Physics, byte[] Statics, byte[] Graphics) Kunos(string smVersion, int status)
        {
            var physics = new byte[800];
            F32(physics, 4, 0.8f);
            F32(physics, 8, 0.3f);
            I32(physics, 16, 4);
            I32(physics, 20, 7200);
            F32(physics, 24, 0.1f);
            F32(physics, 28, 144f);
            F32(physics, 364, 1f);
            var statics = new byte[688];
            Encoding.Unicode.GetBytes(smVersion).CopyTo(statics, 0);
            I32(statics, 412, 8500);
            var graphics = new byte[296];
            I32(graphics, 4, status);
            return (physics, statics, graphics);
        }

        internal const string TruckJson =
            "{\"speed\":-5.000,\"rpm\":900.0,\"gear\":-1,\"dgear\":-1,\"steer\":0.250,\"throttle\":0.000,\"brake\":0.500,\"clutch\":1.000,\"cruise\":0.000}\n";

        private static TelemetryFrame Decode(Func<TelemetryFrame, bool> decoder)
        {
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.True(decoder(f));
            return f;
        }

        // ── Forza ──

        [Theory]
        [InlineData(311, TelemetrySource.ForzaMotorsport7)]
        [InlineData(331, TelemetrySource.ForzaMotorsport)]
        [InlineData(324, TelemetrySource.ForzaHorizon)]
        [InlineData(323, TelemetrySource.ForzaHorizon)]
        public void Forza_ReadsTheDashAtItsGamesOffsets(int length, TelemetrySource source)
        {
            var f = Decode(x => TelemetryDecoders.TryForza(Forza(length), x));
            Assert.Equal(source, f.Source);
            Assert.Equal(30f, f.SpeedMs);
            Assert.Equal(6000f, f.Rpm);
            Assert.Equal(8000f, f.MaxRpm);
            Assert.Equal(1f, f.Throttle);
            Assert.Equal(0.2f, f.Brake, 3);
            Assert.Equal(1f, f.Handbrake);
            Assert.Equal(3, f.Gear);
            Assert.Equal(64 / 127f, f.Steering, 4);
            Assert.True(f.DrivingReported);
            Assert.True(f.Driving);
        }

        [Fact]
        public void ForzaSled_TakesItsSpeedFromTheVelocity()
        {
            var p = new byte[232];
            I32(p, 0, 1);
            F32(p, 32, 3f);
            F32(p, 36, 4f);
            var f = Decode(x => TelemetryDecoders.TryForza(p, x));
            Assert.Equal(TelemetrySource.ForzaSled, f.Source);
            Assert.Equal(5f, f.SpeedMs, 4);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(11, 0)]
        [InlineData(1, 1)]
        [InlineData(10, 10)]
        [InlineData(12, 0)]
        public void ForzaGear_ZeroIsReverse_ElevenIsNeutral(byte raw, int gear)
            => Assert.Equal(gear, TelemetryDecoders.ForzaGear(raw));

        [Fact]
        public void Forza_InTheMenus_ReportsNotDriving()
        {
            var f = Decode(x => TelemetryDecoders.TryForza(Forza(324, raceOn: false), x));
            Assert.True(f.DrivingReported);
            Assert.False(f.Driving);
        }

        // ── F1 ──

        [Theory]
        [InlineData(2022)]
        [InlineData(2023)]
        [InlineData(2024)]
        [InlineData(2025)]
        public void F1_ReadsThePlayersCar_AndTakesMaxRpmFromCarStatus(int year)
        {
            var state = new TelemetryDecoders.F1State();
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Claimed, TelemetryDecoders.TryF1(F1Status(year, 3, 12500), state, f));
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryF1(F1Telemetry(year, 3), state, f));
            Assert.Equal(TelemetrySource.F1, f.Source);
            Assert.Equal(year, f.F1Year);
            Assert.Equal(50f, f.SpeedMs, 3);
            Assert.Equal(0.75f, f.Throttle);
            Assert.Equal(-0.5f, f.Steering);
            Assert.Equal(0.25f, f.Brake);
            Assert.Equal(0.4f, f.Clutch, 4);
            Assert.Equal(-1, f.Gear);
            Assert.Equal(11000f, f.Rpm);
            Assert.Equal(12500f, f.MaxRpm);
        }

        /// <summary>F1 24 and F1 25 also send the two formats before their
        /// own (f1-24-udp README "Will my old app still work"), and the year
        /// byte then names the game, or reads 0, never the format's year.</summary>
        [Theory]
        [InlineData(2023, 24)]
        [InlineData(2023, 25)]
        [InlineData(2023, 0)]
        [InlineData(2024, 25)]
        [InlineData(2024, 0)]
        public void F1_AnOlderFormat_DecodesWhateverTheGameYearByteSays(int year, int gameYear)
        {
            var state = new TelemetryDecoders.F1State();
            var status = F1Status(year, 0, 12000);
            status[2] = (byte)gameYear;
            var telemetry = F1Telemetry(year, 0);
            telemetry[2] = (byte)gameYear;
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Claimed, TelemetryDecoders.TryF1(status, state, f));
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryF1(telemetry, state, f));
            Assert.Equal(TelemetrySource.F1, f.Source);
            Assert.Equal(gameYear != 0 ? 2000 + gameYear : year, f.F1Year);
            Assert.Equal(11000f, f.Rpm);
            Assert.Equal(12000f, f.MaxRpm);
        }

        /// <summary>The status names the game by the year byte formats 2023
        /// and later carry, and by the format when the byte reads 0 or the
        /// format has none (F1 22).</summary>
        [Theory]
        [InlineData(2024, 25, "F1 25")]
        [InlineData(2024, 0, "F1 24")]
        [InlineData(2023, 24, "F1 24")]
        [InlineData(2025, 25, "F1 25")]
        [InlineData(2022, -1, "F1 22")]
        public void F1_TheStatusNamesTheGame_NotTheFormat(int year, int gameYear, string name)
        {
            var telemetry = F1Telemetry(year, 0);
            if (gameYear >= 0) telemetry[2] = (byte)gameYear;
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryF1(telemetry, new TelemetryDecoders.F1State(), f));
            Assert.Equal(name, InputService.SourceName(
                new TelemetryStatus(TelemetryListenState.Listening, 20777, f.Source, f.F1Year, "", "", 0)));
        }

        /// <summary>Every packet size of formats 2023 to 2025, by packet id:
        /// vAzhure F12023.cs, F12024.cs and F12025.cs (Marshal.SizeOf of each
        /// Packet...Data struct), and the f1-24-udp README's "Size:" lines
        /// for 2024.</summary>
        public static IEnumerable<object[]> F1PacketSizes()
        {
            int[] f1_23 = { 1349, 644, 1131, 45, 1306, 1107, 1352, 1239, 1020, 1218, 953, 1460, 231, 217 };
            int[] f1_24 = { 1349, 753, 1285, 45, 1350, 1133, 1352, 1239, 1020, 1306, 953, 1460, 231, 237, 101 };
            int[] f1_25 = { 1349, 753, 1285, 45, 1284, 1133, 1352, 1239, 1042, 954, 1041, 1460, 231, 273, 101, 1131 };
            for (int id = 0; id < f1_23.Length; id++) yield return new object[] { 2023, id, f1_23[id] };
            for (int id = 0; id < f1_24.Length; id++) yield return new object[] { 2024, id, f1_24[id] };
            for (int id = 0; id < f1_25.Length; id++) yield return new object[] { 2025, id, f1_25[id] };
        }

        [Theory]
        [MemberData(nameof(F1PacketSizes))]
        public void F1_EveryPacketOfItsFormat_IsClaimed(int year, int id, int length)
        {
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.NotEqual(TelemetryDecode.No, TelemetryDecoders.TryF1(F1(year, id, length, 0), new TelemetryDecoders.F1State(), f));
        }

        [Theory]
        [InlineData(2023, 1351)]
        [InlineData(2024, 559)]
        [InlineData(2025, 237)]
        public void F1_ALengthNotInItsFormat_IsNotF1(int year, int length)
        {
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.No, TelemetryDecoders.TryF1(F1(year, 6, length, 0), new TelemetryDecoders.F1State(), f));
        }

        [Theory]
        [InlineData(24)]
        [InlineData(25)]
        [InlineData(0)]
        public void F1_24MotionEx_IsClaimed_AndNeverReadAsEaSportsWrc(int gameYear)
        {
            // Format 2024's Motion Ex is 237 bytes, EA SPORTS WRC's length,
            // and F1 25 sends it with its own year byte or 0.
            var motionEx = F1(2024, 13, 237, 0);
            motionEx[2] = (byte)gameYear;
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null, null, 0);
            Assert.False(reader.Read(motionEx, 1000));
            Assert.Equal(TelemetrySource.None, reader.Frame.Source);
        }

        [Fact]
        public void F1_22_NeedsOneOfItsOwnSizes()
        {
            var p = F1(2022, 6, 559, 0);
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.No, TelemetryDecoders.TryF1(p, new TelemetryDecoders.F1State(), f));
        }

        // ── The status line ──

        /// <summary>Automobilista 2 sends Project CARS 2's packets, and
        /// American Truck Simulator the ETS2 plugin's, with nothing that
        /// tells the two games apart, so the status names both, as the
        /// picker does.</summary>
        [Fact]
        public void TheStatus_NamesBothGamesOfAFormatTwoGamesSend()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal(s.Telemetry_Game_ProjectCars2, InputService.SourceName(
                new TelemetryStatus(TelemetryListenState.Listening, 5606, TelemetrySource.ProjectCars2, 0, "", "", 0)));
            Assert.Equal(s.Telemetry_Game_TruckSimulator, InputService.SourceName(
                new TelemetryStatus(TelemetryListenState.Listening, 49001, TelemetrySource.TruckSimulator, 0, "", "", 0)));
        }

        // ── Codemasters ──

        [Theory]
        [InlineData(10f, -1)]
        [InlineData(-1f, -1)]
        [InlineData(0f, 0)]
        [InlineData(4f, 4)]
        public void Codemasters_ReadsSixtySixFloats_RpmInTenths(float gear, int expected)
        {
            var f = Decode(x => TelemetryDecoders.TryCodemasters(Codemasters(gear), x));
            Assert.Equal(TelemetrySource.Codemasters, f.Source);
            Assert.Equal(20f, f.SpeedMs);
            Assert.Equal(0.5f, f.Throttle);
            Assert.Equal(0.25f, f.Steering);
            Assert.Equal(0.1f, f.Brake);
            Assert.Equal(expected, f.Gear);
            Assert.Equal(6500f, f.Rpm);
            Assert.Equal(7500f, f.MaxRpm);
        }

        // ── EA SPORTS WRC ──

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void EaSportsWrc_ReadsTheDefaultLayout_PaddedOrWithItsFourCC(bool padded, bool fourcc)
        {
            var f = Decode(x => TelemetryDecoders.TryEaSportsWrc(Wrc(padded, fourcc), x));
            Assert.Equal(TelemetrySource.EaSportsWrc, f.Source);
            Assert.Equal(3, f.Gear);
            Assert.Equal(30f, f.SpeedMs);
            Assert.Equal(8000f, f.MaxRpm);
            Assert.Equal(6000f, f.Rpm);
            Assert.Equal(0.9f, f.Throttle);
            Assert.Equal(0.1f, f.Brake);
            Assert.Equal(0.5f, f.Steering);
            Assert.Equal(1f, f.Handbrake);
        }

        [Theory]
        [InlineData(0, 0, 10, 0)]
        [InlineData(10, 0, 10, -1)]
        [InlineData(5, 1, 9, 4)]
        [InlineData(2, 0, 9, 2)]
        public void EaSportsWrcGear_FollowsTheGamesOwnIndexes(int index, int neutral, int reverse, int gear)
            => Assert.Equal(gear, TelemetryDecoders.EaSportsWrcGear(index, neutral, reverse));

        // ── OutGauge ──

        [Fact]
        public void OutGauge_FromBeamNg()
        {
            var f = Decode(x => TelemetryDecoders.TryOutGauge(OutGauge("beam", 3), x));
            Assert.Equal(TelemetrySource.BeamNg, f.Source);
            Assert.Equal(2, f.Gear);
            Assert.Equal(25f, f.SpeedMs);
            Assert.Equal(4000f, f.Rpm);
            Assert.Equal(1f, f.Handbrake);
            Assert.Equal(0.6f, f.Throttle);
            Assert.Equal(0.2f, f.Brake);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        public void OutGauge_FromLiveForSpeed_WithAnId(byte gear, int expected)
        {
            var f = Decode(x => TelemetryDecoders.TryOutGauge(OutGauge("XFG\0", gear, 96), x));
            Assert.Equal(TelemetrySource.LiveForSpeed, f.Source);
            Assert.Equal(expected, f.Gear);
        }

        // ── Project CARS 2 ──

        [Fact]
        public void ProjectCars2_ReadsTelemetry_AndTheGameStateSaysDriving()
        {
            var state = new TelemetryDecoders.ProjectCars2State();
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Claimed, TelemetryDecoders.TryProjectCars2(ProjectCars2GameState(2), state, f));
            f.Reset(0);
            Assert.Equal(TelemetryDecode.Frame, TelemetryDecoders.TryProjectCars2(ProjectCars2Telemetry(), state, f));
            Assert.Equal(TelemetrySource.ProjectCars2, f.Source);
            Assert.Equal(0.2f, f.Brake, 3);
            Assert.Equal(1f, f.Throttle);
            Assert.Equal(40f, f.SpeedMs);
            Assert.Equal(7000f, f.Rpm);
            Assert.Equal(8000f, f.MaxRpm);
            Assert.Equal(-1f, f.Steering);
            Assert.Equal(-1, f.Gear);
            Assert.Equal(1f, f.Handbrake);
            Assert.True(f.DrivingReported);
            Assert.True(f.Driving);

            TelemetryDecoders.TryProjectCars2(ProjectCars2GameState(3), state, f);
            f.Reset(0);
            TelemetryDecoders.TryProjectCars2(ProjectCars2Telemetry(), state, f);
            Assert.False(f.Driving);
        }

        // ── Assetto Corsa ──

        [Theory]
        [InlineData("1.7", TelemetrySource.AssettoCorsa)]
        [InlineData("1.9", TelemetrySource.AssettoCorsaCompetizione)]
        public void Kunos_ReadsThePages_AndTellsAcFromAcc(string smVersion, TelemetrySource source)
        {
            var (physics, statics, graphics) = Kunos(smVersion, 2);
            var f = Decode(x => TelemetryDecoders.TryKunos(physics, statics, graphics, x));
            Assert.Equal(source, f.Source);
            Assert.Equal(0.8f, f.Throttle);
            Assert.Equal(0.3f, f.Brake);
            Assert.Equal(3, f.Gear);
            Assert.Equal(7200f, f.Rpm);
            Assert.Equal(0.1f, f.Steering);
            Assert.Equal(40f, f.SpeedMs, 4);
            // The page reads 1 with the pedal up (vAzhure GameListener.cs 221).
            Assert.Equal(0f, f.Clutch);
            Assert.Equal(8500f, f.MaxRpm);
            Assert.True(f.Driving);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        public void KunosGear_ZeroIsReverse_OneIsNeutral(int raw, int gear)
        {
            var (physics, statics, graphics) = Kunos("1.9", 2);
            I32(physics, 16, raw);
            var f = Decode(x => TelemetryDecoders.TryKunos(physics, statics, graphics, x));
            Assert.Equal(gear, f.Gear);
        }

        // ── ETS2 and ATS ──

        [Fact]
        public void TruckSimulator_ReadsThePluginsJson_SpeedUnsigned_SteeringFlipped()
        {
            var f = Decode(x => TelemetryDecoders.TryTruckSimulator(Encoding.UTF8.GetBytes(TruckJson), x));
            Assert.Equal(TelemetrySource.TruckSimulator, f.Source);
            Assert.Equal(5f, f.SpeedMs);
            Assert.Equal(900f, f.Rpm);
            Assert.Equal(-1, f.Gear);
            Assert.Equal(-0.25f, f.Steering);
            Assert.Equal(0.5f, f.Brake);
            Assert.Equal(1f, f.Clutch);
        }

        [Fact]
        public void TruckSimulator_NeedsSpeedRpmAndGear()
        {
            var f = new TelemetryFrame();
            f.Reset(0);
            Assert.False(TelemetryDecoders.TryTruckSimulator(Encoding.UTF8.GetBytes("{\"speed\":1.0,\"rpm\":2.0}"), f));
            Assert.False(TelemetryDecoders.TryTruckSimulator(Encoding.UTF8.GetBytes("not json"), f));
        }

        // ── Auto-Detect ──

        public static IEnumerable<object[]> EveryUdpFormat() => new[]
        {
            new object[] { Forza(324), TelemetrySource.ForzaHorizon },
            new object[] { F1Telemetry(2025, 0), TelemetrySource.F1 },
            new object[] { Codemasters(), TelemetrySource.Codemasters },
            new object[] { Wrc(false, false), TelemetrySource.EaSportsWrc },
            new object[] { OutGauge("beam", 2), TelemetrySource.BeamNg },
            new object[] { ProjectCars2Telemetry(), TelemetrySource.ProjectCars2 },
            new object[] { Encoding.UTF8.GetBytes(TruckJson), TelemetrySource.TruckSimulator },
        };

        [Theory]
        [MemberData(nameof(EveryUdpFormat))]
        public void AutoDetect_FindsEachFormat(byte[] packet, TelemetrySource source)
        {
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null, null, 0);
            Assert.True(reader.Read(packet, 1000));
            Assert.Equal(source, reader.Frame.Source);
        }

        [Fact]
        public void AutoDetect_HoldsThroughAStrayPacket_AndSwitchesAfterThree()
        {
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null, null, 0);
            Assert.True(reader.Read(Forza(324), 1000));
            Assert.False(reader.Read(Codemasters(), 1010));
            Assert.True(reader.Read(Forza(324), 1020));
            Assert.Equal(TelemetrySource.ForzaHorizon, reader.Frame.Source);

            Assert.False(reader.Read(Codemasters(), 1030));
            Assert.False(reader.Read(Codemasters(), 1040));
            Assert.True(reader.Read(Codemasters(), 1050));
            Assert.Equal(TelemetrySource.Codemasters, reader.Frame.Source);
        }

        [Fact]
        public void AutoDetect_LetsGoAfterASecondOfSilence()
        {
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null, null, 0);
            Assert.True(reader.Read(Forza(324), 1000));
            Assert.True(reader.Read(Codemasters(), 1000 + TelemetryReader.HoldReleaseMs));
            Assert.Equal(TelemetrySource.Codemasters, reader.Frame.Source);
        }

        [Fact]
        public void APickedFormat_ReadsItsOwnPacketsAlone()
        {
            var reader = new TelemetryReader(GameTelemetryFormat.Codemasters, null, null, 0);
            Assert.False(reader.Read(Forza(324), 1000));
            Assert.True(reader.Read(Codemasters(), 1001));
        }

        [Fact]
        public void AGameWithNoMaxRpm_GetsTheHighestRpmSeen()
        {
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null, null, 0);
            var low = OutGauge("beam", 2);
            var high = OutGauge("beam", 2);
            F32(high, 16, 7000f);
            reader.Read(high, 1000);
            reader.Read(low, 1010);
            Assert.Equal(4000f, reader.Frame.Rpm);
            Assert.Equal(7000f, reader.Frame.MaxRpm);
            Assert.True(reader.Frame.MaxRpmLearned);

            // A game's own max RPM is not learned.
            Assert.True(reader.Read(Forza(324), 1020 + TelemetryReader.HoldReleaseMs));
            Assert.Equal(8000f, reader.Frame.MaxRpm);
            Assert.False(reader.Frame.MaxRpmLearned);
        }

        [Fact]
        public void AssettoCorsaPicked_ReadsNoUdp()
        {
            var reader = new TelemetryReader(GameTelemetryFormat.AssettoCorsa, null, null, 0);
            Assert.False(reader.ReadsUdp);
            Assert.False(reader.Read(Forza(324), 1000));
            var (physics, statics, graphics) = Kunos("1.9", 2);
            Assert.True(reader.ReadKunos(physics, statics, graphics, 1000));
        }

        // ── Custom formats ──

        private static CustomTelemetryFormat BinaryFormat(bool bigEndian = false) => new()
        {
            Name = "Rig",
            Kind = CustomTelemetryKind.Binary,
            Length = 40,
            BigEndian = bigEndian,
            UseGearValues = true,
            GearReverse = 0,
            GearNeutral = 11,
            Fields =
            {
                new CustomTelemetryField { Target = CustomTelemetryTarget.Speed, Offset = 0, Type = CustomTelemetryType.F32, Scale = 3.6 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Rpm, Offset = 4, Type = CustomTelemetryType.U16 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Gear, Offset = 6, Type = CustomTelemetryType.U8 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Brake, Offset = 7, Type = CustomTelemetryType.U8, Scale = 1 / 255.0 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Steering, Offset = 8, Type = CustomTelemetryType.S8, Scale = 1 / 127.0 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Boost", Offset = 12, Type = CustomTelemetryType.F64, Min = 0, Max = 2 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Temp", Offset = 20, Type = CustomTelemetryType.S16, Scale = 0.1, Min = 0, Max = 120 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Driving, Offset = 22, Type = CustomTelemetryType.U32 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.MaxRpm, Offset = 26, Type = CustomTelemetryType.S32 },
                new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Far", Offset = 39, Type = CustomTelemetryType.U32 },
            },
        };

        private static byte[] RigPacket(bool bigEndian, byte gear = 3)
        {
            var p = new byte[40];
            if (bigEndian)
            {
                BinaryPrimitives.WriteSingleBigEndian(p.AsSpan(0), 25f);
                BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), 5000);
                BinaryPrimitives.WriteDoubleBigEndian(p.AsSpan(12), 1.5);
                BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(20), 905);
                BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(22), 1);
                BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(26), 7000);
            }
            else
            {
                F32(p, 0, 25f);
                U16(p, 4, 5000);
                F64(p, 12, 1.5);
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(20), 905);
                U32(p, 22, 1);
                I32(p, 26, 7000);
            }
            p[6] = gear;
            p[7] = 255;
            p[8] = unchecked((byte)(sbyte)-127);
            return p;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ACustomBinaryFormat_ReadsEveryFieldInItsOrder(bool bigEndian)
        {
            var format = BinaryFormat(bigEndian);
            var variables = GameTelemetryRuntime.BuildVariables(new[] { format });
            var compiled = new CompiledTelemetryFormat(format, variables);
            var f = new TelemetryFrame();
            f.Reset(variables.Length);
            Assert.True(compiled.TryRead(RigPacket(bigEndian), f, pinned: false));
            Assert.Equal(TelemetrySource.Custom, f.Source);
            Assert.Equal("Rig", f.CustomName);
            Assert.Equal(25f, f.SpeedMs, 3);   // 25 * 3.6 km/h back to m/s
            Assert.Equal(5000f, f.Rpm);
            Assert.Equal(3, f.Gear);
            Assert.Equal(1f, f.Brake, 4);
            Assert.Equal(-1f, f.Steering, 4);
            Assert.Equal(new[] { "Boost", "Temp", "Far" }, variables.Select(v => v.Name));
            Assert.Equal(1.5f, f.Variables[0]);
            Assert.Equal(90.5f, f.Variables[1], 3);
            // A field past the packet's end reads nothing.
            Assert.Equal(0f, f.Variables[2]);
            Assert.True(f.DrivingReported);
            Assert.True(f.Driving);
            Assert.Equal(7000f, f.MaxRpm);
        }

        [Theory]
        [InlineData(0, -1)]
        [InlineData(11, 0)]
        [InlineData(4, 4)]
        public void ACustomFormat_NamesItsOwnReverseAndNeutral(byte gear, int expected)
        {
            var format = BinaryFormat();
            var compiled = new CompiledTelemetryFormat(format, GameTelemetryRuntime.BuildVariables(new[] { format }));
            var f = new TelemetryFrame();
            f.Reset(3);
            compiled.TryRead(RigPacket(false, gear), f, pinned: false);
            Assert.Equal(expected, f.Gear);
        }

        [Fact]
        public void ACustomBinaryFormat_MatchesItsLengthAlone()
        {
            var format = BinaryFormat();
            var compiled = new CompiledTelemetryFormat(format, GameTelemetryRuntime.BuildVariables(new[] { format }));
            var f = new TelemetryFrame();
            f.Reset(3);
            Assert.False(compiled.TryRead(new byte[41], f, pinned: false));
        }

        [Fact]
        public void ACustomFormatWithNoLengthOrKey_ReadsOnlyWhenPicked()
        {
            var format = BinaryFormat();
            format.Length = 0;
            var compiled = new CompiledTelemetryFormat(format, GameTelemetryRuntime.BuildVariables(new[] { format }));
            Assert.False(compiled.Detectable);
            var f = new TelemetryFrame();
            f.Reset(3);
            Assert.False(compiled.TryRead(RigPacket(false), f, pinned: false));
            Assert.True(compiled.TryRead(RigPacket(false), f, pinned: true));
        }

        [Fact]
        public void ACustomJsonFormat_ReadsNestedKeys_AndNeedsItsKeyToBeFound()
        {
            var format = new CustomTelemetryFormat
            {
                Name = "Web",
                Kind = CustomTelemetryKind.Json,
                Key = "car.speed",
                Fields =
                {
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Speed, Key = "car.speed" },
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Gear, Key = "car.gear" },
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Driving, Key = "live" },
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Fuel", Key = "fuel", Scale = 100 },
                },
            };
            var variables = GameTelemetryRuntime.BuildVariables(new[] { format });
            var compiled = new CompiledTelemetryFormat(format, variables);
            var f = new TelemetryFrame();
            f.Reset(variables.Length);
            Assert.True(compiled.TryRead(Encoding.UTF8.GetBytes("{\"car\":{\"speed\":72,\"gear\":\"2\"},\"live\":true,\"fuel\":0.25}"), f, pinned: false));
            Assert.Equal(20f, f.SpeedMs, 3);
            Assert.Equal(2, f.Gear);
            Assert.True(f.Driving);
            Assert.Equal(25f, f.Variables[0]);
            f.Reset(variables.Length);
            Assert.False(compiled.TryRead(Encoding.UTF8.GetBytes("{\"fuel\":0.25}"), f, pinned: false));
        }

        /// <summary>An offset so large that adding the field's size overflows
        /// reads nothing, as any offset past the packet's end does.</summary>
        [Fact]
        public void AFieldOffsetNearIntMax_ReadsNothing()
        {
            var format = BinaryFormat();
            format.Fields.Add(new CustomTelemetryField { Target = CustomTelemetryTarget.Throttle, Offset = int.MaxValue - 1, Type = CustomTelemetryType.F32 });
            var compiled = new CompiledTelemetryFormat(format, GameTelemetryRuntime.BuildVariables(new[] { format }));
            var f = new TelemetryFrame();
            f.Reset(3);
            Assert.True(compiled.TryRead(RigPacket(false), f, pinned: true));
            Assert.Equal(5000f, f.Rpm);
            Assert.Equal(0f, f.Throttle);
        }

        [Fact]
        public void AutoDetect_TriesACustomFormatFirst()
        {
            var format = BinaryFormat();
            format.Length = 264;   // Codemasters' length
            var variables = GameTelemetryRuntime.BuildVariables(new[] { format });
            var reader = new TelemetryReader(GameTelemetryFormat.Auto, null,
                new[] { new CompiledTelemetryFormat(format, variables) }, variables.Length);
            Assert.True(reader.Read(Codemasters(), 1000));
            Assert.Equal(TelemetrySource.Custom, reader.Frame.Source);
        }

        [Fact]
        public void TheVariableList_HasEachNameOnce_InTheOrderFirstUsed()
        {
            var a = BinaryFormat();
            var b = new CustomTelemetryFormat
            {
                Name = "Other",
                Fields =
                {
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "temp" },
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Oil" },
                    new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Bad:Name" },
                },
            };
            Assert.Equal(new[] { "Boost", "Temp", "Far", "Oil" },
                GameTelemetryRuntime.BuildVariables(new[] { a, b }).Select(v => v.Name));
        }

        [Fact]
        public void CustomFormats_RoundTripThroughTheSettingsFile()
        {
            var settings = new AppSettingsData
            {
                EnableGameTelemetry = true,
                GameTelemetryPort = 30000,
                GameTelemetryFormat = GameTelemetryFormat.Custom,
                GameTelemetryCustomFormat = "Rig",
                GameTelemetryForwardPort = 30001,
                GameTelemetryFullSpeed = 250,
                GameTelemetryFormats = new List<CustomTelemetryFormat> { BinaryFormat(true) },
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, settings);
            using var reader = new StringReader(writer.ToString());
            var back = (AppSettingsData)serializer.Deserialize(reader);

            Assert.True(back.EnableGameTelemetry);
            Assert.Equal(30000, back.GameTelemetryPort);
            Assert.Equal(GameTelemetryFormat.Custom, back.GameTelemetryFormat);
            Assert.Equal("Rig", back.GameTelemetryCustomFormat);
            Assert.Equal(30001, back.GameTelemetryForwardPort);
            Assert.Equal(250, back.GameTelemetryFullSpeed);
            var format = Assert.Single(back.GameTelemetryFormats);
            var original = BinaryFormat(true);
            Assert.Equal(original.Name, format.Name);
            Assert.True(format.BigEndian);
            Assert.True(format.UseGearValues);
            Assert.Equal(11, format.GearNeutral);
            Assert.Equal(original.Fields.Count, format.Fields.Count);
            for (int i = 0; i < original.Fields.Count; i++)
            {
                Assert.Equal(original.Fields[i].Target, format.Fields[i].Target);
                Assert.Equal(original.Fields[i].Name, format.Fields[i].Name);
                Assert.Equal(original.Fields[i].Offset, format.Fields[i].Offset);
                Assert.Equal(original.Fields[i].Type, format.Fields[i].Type);
                Assert.Equal(original.Fields[i].Scale, format.Fields[i].Scale);
                Assert.Equal(original.Fields[i].Max, format.Fields[i].Max);
            }
        }

        [Fact]
        public void ProfileData_CarriesTheServiceSettings_ButNotTheFormats()
        {
            var profile = new ProfileData
            {
                EnableGameTelemetry = true,
                GameTelemetryPort = 5606,
                GameTelemetryFormat = GameTelemetryFormat.ProjectCars2,
                GameTelemetryForwardPort = 20778,
                GameTelemetryFullSpeed = 200,
            };
            var serializer = new XmlSerializer(typeof(ProfileData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, profile);
            string xml = writer.ToString();
            Assert.DoesNotContain("GameTelemetryFormats", xml);
            using var reader = new StringReader(xml);
            var back = (ProfileData)serializer.Deserialize(reader);
            Assert.True(back.EnableGameTelemetry);
            Assert.Equal(5606, back.GameTelemetryPort);
            Assert.Equal(GameTelemetryFormat.ProjectCars2, back.GameTelemetryFormat);
            Assert.Equal(20778, back.GameTelemetryForwardPort);
            Assert.Equal(200, back.GameTelemetryFullSpeed);
        }

        // ── The row ──

        private sealed class Clock
        {
            public long Now = 1000;
        }

        private static GameTelemetryDevice Row(GameTelemetryFormat format, Clock clock,
            TelemetryVariable[] variables = null, CompiledTelemetryFormat pinned = null, int port = 0)
        {
            variables ??= Array.Empty<TelemetryVariable>();
            var reader = new TelemetryReader(format, pinned, null, variables.Length);
            var row = new GameTelemetryDevice(port, reader, variables, 0, () => clock.Now, _ => { },
                () => new KunosSharedMemory("pf531-test-physics", "pf531-test-static", "pf531-test-graphics"))
            {
                BindAddress = IPAddress.Loopback,
            };
            row.AttachForTest();
            return row;
        }

        [Fact]
        public void TheRow_ShowsSpeedRpmPedalsSteeringAndGear()
        {
            var clock = new Clock();
            GameTelemetryRuntime.FullSpeedKmh = 216;   // 60 m/s
            using var row = Row(GameTelemetryFormat.Auto, clock);
            row.InjectUdp(Forza(324, speedMs: 30f, gear: 3, steer: 127), "127.0.0.1");
            var state = row.GetCurrentState();

            Assert.Equal(GameTelemetryDevice.Unipolar(0.5f), state.Axis[GameTelemetryDevice.SpeedAxis]);
            Assert.Equal(GameTelemetryDevice.Unipolar(0.75f), state.Axis[GameTelemetryDevice.RpmAxis]);
            Assert.Equal(65535, state.Axis[GameTelemetryDevice.ThrottleAxis]);
            Assert.Equal(65535, state.Axis[GameTelemetryDevice.HandbrakeAxis]);
            Assert.Equal(65535, state.Axis[GameTelemetryDevice.SteeringAxis]);
            Assert.True(state.Buttons[GameTelemetryDevice.DrivingButton]);
            Assert.True(state.Buttons[GameTelemetryDevice.FirstGearButton + 2]);
            Assert.Equal(2, state.Buttons.Take(GameTelemetryDevice.ButtonCount).Count(b => b));
        }

        [Theory]
        [InlineData(0, GameTelemetryDevice.ReverseButton)]
        [InlineData(11, GameTelemetryDevice.NeutralButton)]
        [InlineData(10, GameTelemetryDevice.FirstGearButton + 9)]
        public void TheRow_PressesOneGearButton(byte forzaGear, int button)
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Forza, clock);
            row.InjectUdp(Forza(324, gear: forzaGear), "127.0.0.1");
            var state = row.GetCurrentState();
            for (int b = GameTelemetryDevice.ReverseButton; b < GameTelemetryDevice.ButtonCount; b++)
                Assert.Equal(b == button, state.Buttons[b]);
        }

        [Fact]
        public void TheRow_PressesNoGearAboveEighteen()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.TruckSimulator, clock);
            row.InjectUdp(Encoding.UTF8.GetBytes(TruckJson.Replace("\"gear\":-1", "\"gear\":19")), "127.0.0.1");
            var state = row.GetCurrentState();
            for (int b = GameTelemetryDevice.ReverseButton; b < GameTelemetryDevice.ButtonCount; b++)
                Assert.False(state.Buttons[b]);
        }

        [Fact]
        public void TheRow_RestsAfterASecondOfSilence()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock);
            row.InjectUdp(Forza(324), "127.0.0.1");
            clock.Now += GameTelemetryDevice.SilenceMs;
            Assert.True(row.GetCurrentState().Buttons[GameTelemetryDevice.DrivingButton]);
            clock.Now += 1;
            var state = row.GetCurrentState();
            Assert.Equal(0, state.Axis[GameTelemetryDevice.SpeedAxis]);
            Assert.Equal(HeadPose.AxisCenter, state.Axis[GameTelemetryDevice.SteeringAxis]);
            Assert.DoesNotContain(true, state.Buttons.Take(GameTelemetryDevice.ButtonCount));
            Assert.Equal(TelemetrySource.None, row.Status.Source);
        }

        [Fact]
        public void TheRow_HoldsTheTruckPluginsValuesForTenSeconds()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.TruckSimulator, clock);
            row.InjectUdp(Encoding.UTF8.GetBytes(TruckJson), "127.0.0.1");
            clock.Now += GameTelemetryDevice.TruckSimulatorHoldMs;
            Assert.True(row.GetCurrentState().Buttons[GameTelemetryDevice.ReverseButton]);
            clock.Now += 1;
            Assert.False(row.GetCurrentState().Buttons[GameTelemetryDevice.ReverseButton]);
        }

        [Fact]
        public void TheRow_DrivingFollowsTheGamesFlag_WhenItSendsOne()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Forza, clock);
            row.InjectUdp(Forza(324, raceOn: false), "127.0.0.1");
            Assert.False(row.GetCurrentState().Buttons[GameTelemetryDevice.DrivingButton]);
        }

        [Fact]
        public void TheRow_ScalesAVariableAxisFromItsMinToItsMax()
        {
            var format = BinaryFormat();
            var variables = GameTelemetryRuntime.BuildVariables(new[] { format });
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Custom, clock, variables, new CompiledTelemetryFormat(format, variables));
            Assert.Equal(GameTelemetryRuntime.StandardAxisCount + 3, row.NumAxes);
            row.InjectUdp(RigPacket(false), "127.0.0.1");
            var state = row.GetCurrentState();
            Assert.Equal(GameTelemetryDevice.Unipolar(0.75f), state.Axis[7]);              // Boost 1.5 of 2
            Assert.Equal(GameTelemetryDevice.Unipolar(90.5f / 120f), state.Axis[8]);       // Temp
        }

        [Fact]
        public void TheRow_NamesEveryAxisAndButton()
        {
            var variables = new[] { new TelemetryVariable("Boost", 0, 2, 0) };
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock, variables);
            var objects = row.GetDeviceObjects();
            Assert.Equal(new[] { "Speed", "RPM", "Throttle", "Brake", "Clutch", "Handbrake", "Steering", "Boost" },
                objects.Where(o => !o.IsButton).Select(o => o.Name));
            var buttons = objects.Where(o => o.IsButton).Select(o => o.Name).ToArray();
            Assert.Equal(21, buttons.Length);
            Assert.Equal("Driving", buttons[0]);
            Assert.Equal("Reverse", buttons[1]);
            Assert.Equal("Neutral", buttons[2]);
            Assert.Equal("Gear 1", buttons[3]);
            Assert.Equal("Gear 18", buttons[20]);
            Assert.Equal(InputDeviceType.GameTelemetry, row.GetInputDeviceType());
            Assert.Equal("telemetry://game", row.DevicePath);
        }

        /// <summary>The row writes every axis, Steering and the variables
        /// included, into the state's Axis array, so the picker must offer
        /// each as "Axis N". A Slider GUID made it "Slider N", which reads
        /// the Sliders array the row never fills (the #193 rule in
        /// SdlDeviceWrapper.GetDeviceObjects).</summary>
        [Fact]
        public void ThePicker_OffersEveryAxisAsAnAxis_TheArrayTheRowWrites()
        {
            var variables = new[] { new TelemetryVariable("Boost", 0, 2, 0) };
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock, variables);
            var ud = new PadForge.Engine.Data.UserDevice { CapType = InputDeviceType.GameTelemetry, DeviceObjects = row.GetDeviceObjects() };
            var choices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(ud);
            Assert.DoesNotContain(choices, c => c.Descriptor.StartsWith("Slider", StringComparison.Ordinal));
            Assert.Contains(choices, c => c.Descriptor == "Axis 6" && c.DisplayName == "Steering");
            Assert.Contains(choices, c => c.Descriptor == "Axis 7" && c.DisplayName == "Boost");
        }

        // ── Each variable keeps its axis ──

        private static CustomTelemetryFormat VariablesFormat(string name, params string[] variables)
        {
            var format = new CustomTelemetryFormat { Name = name, Length = 4 * Math.Max(1, variables.Length) };
            for (int i = 0; i < variables.Length; i++)
                format.Fields.Add(new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = variables[i], Offset = 4 * i, Max = 100 });
            return format;
        }

        private static int SlotOf(string name) => GameTelemetryRuntime.Variables.Single(v => v.Name == name).Slot;

        /// <summary>A saved mapping names an axis by number, so a variable
        /// keeps the axis it was first given while its name is in use, the
        /// voice phrase and NFC tag registries' rule for their buttons.
        /// Deleting or reordering a variable moves no other variable's axis,
        /// and a name that leaves frees its axis for the next new one.</summary>
        [Fact]
        public void AVariable_KeepsItsAxis_WhenAnotherIsDeletedOrMoved()
        {
            GameTelemetryRuntime.Formats = new[] { VariablesFormat("Rig", "Boost", "Temp", "Far") };
            Assert.Equal(new[] { 0, 1, 2 }, new[] { SlotOf("Boost"), SlotOf("Temp"), SlotOf("Far") });

            GameTelemetryRuntime.Formats = new[] { VariablesFormat("Rig", "Far", "Temp") };   // Boost deleted, the rest swapped
            Assert.Equal(1, SlotOf("Temp"));
            Assert.Equal(2, SlotOf("Far"));

            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Custom, clock, GameTelemetryRuntime.Variables, GameTelemetryRuntime.CompiledFormats[0]);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 8, 9 }, row.SupportedAxisIndices);
            Assert.Equal(10, row.NumAxes);
            var objects = row.GetDeviceObjects().Where(o => !o.IsButton).ToArray();
            Assert.Equal("Temp", objects.Single(o => o.InputIndex == 8).Name);
            Assert.Equal("Far", objects.Single(o => o.InputIndex == 9).Name);
            var packet = new byte[8];
            F32(packet, 0, 25f);    // Far
            F32(packet, 4, 75f);    // Temp
            row.InjectUdp(packet, "127.0.0.1");
            var state = row.GetCurrentState();
            Assert.Equal(GameTelemetryDevice.Unipolar(0.75f), state.Axis[8]);
            Assert.Equal(GameTelemetryDevice.Unipolar(0.25f), state.Axis[9]);
            Assert.Equal(0, state.Axis[7]);

            // The freed axis goes to the next new name.
            GameTelemetryRuntime.Formats = new[] { VariablesFormat("Rig", "Far", "Temp", "Oil") };
            Assert.Equal(0, SlotOf("Oil"));
            Assert.Equal(1, SlotOf("Temp"));
        }

        /// <summary>The axes ride the formats into the settings file, so a
        /// restart keeps them. A file from before has none, and its variables
        /// take the axes in first-use order, where they were.</summary>
        [Fact]
        public void TheAxes_SurviveARestart_AndAnOlderFileKeepsItsOrder()
        {
            var vm = new DashboardViewModel();
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { VariablesFormat("Rig", "Boost", "Temp", "Far") };
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { VariablesFormat("Rig", "Far", "Temp") };
            var saved = vm.GameTelemetryFormats;
            Assert.Equal(1, saved[0].Fields.Single(f => f.Name == "Temp").Axis);
            Assert.Equal(2, saved[0].Fields.Single(f => f.Name == "Far").Axis);

            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, new AppSettingsData { GameTelemetryFormats = saved });
            Assert.Contains("Axis=\"1\"", writer.ToString());
            var back = (AppSettingsData)serializer.Deserialize(new StringReader(writer.ToString()));

            GameTelemetryRuntime.ResetForTest();            // a new process
            new DashboardViewModel().GameTelemetryFormats = back.GameTelemetryFormats;
            Assert.Equal(1, SlotOf("Temp"));
            Assert.Equal(2, SlotOf("Far"));

            // An older file: no Axis anywhere.
            GameTelemetryRuntime.ResetForTest();
            var older = VariablesFormat("Rig", "Far", "Temp");
            Assert.All(older.Fields, f => Assert.Equal(-1, f.Axis));
            GameTelemetryRuntime.Formats = new[] { older };
            Assert.Equal(0, SlotOf("Far"));
            Assert.Equal(1, SlotOf("Temp"));
        }

        /// <summary>The row has 24 axes, the input state's, 17 after the
        /// standard seven. A variable past them gets none and still reads as
        /// an expression channel, and it takes the first axis a deleted
        /// variable frees.</summary>
        [Fact]
        public void AVariablePastTheLastAxis_GetsNone_UntilOneIsFreed()
        {
            int max = GameTelemetryRuntime.MaxVariableAxes;
            Assert.Equal(PadForge.Engine.CustomInputState.MaxAxis - GameTelemetryRuntime.StandardAxisCount, max);
            var names = Enumerable.Range(0, max + 1).Select(i => "V" + i).ToArray();
            GameTelemetryRuntime.Formats = new[] { VariablesFormat("Many", names) };
            Assert.Equal(-1, SlotOf("V" + max));
            Assert.Equal(max - 1, SlotOf("V" + (max - 1)));
            var clock = new Clock();
            using (var row = Row(GameTelemetryFormat.Custom, clock, GameTelemetryRuntime.Variables, GameTelemetryRuntime.CompiledFormats[0]))
                Assert.Equal(PadForge.Engine.CustomInputState.MaxAxis, row.NumAxes);

            GameTelemetryRuntime.Formats = new[] { VariablesFormat("Many", names.Where(n => n != "V3").ToArray()) };
            Assert.Equal(3, SlotOf("V" + max));
        }

        /// <summary>The dialog edits copies, so it carries each field's axis
        /// through, and a renamed variable keeps the axis its old name had
        /// once that name is gone.</summary>
        [Fact]
        public void TheDialog_CarriesTheAxis_AndARenameKeepsIt()
        {
            var vm = new DashboardViewModel();
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { VariablesFormat("Rig", "Boost", "Temp", "Far") };
            var edit = TelemetryFormatEdit.From(vm.GameTelemetryFormats[0]);
            edit.Fields[2].Name = "Oil";                     // Far renamed, axis 2
            edit.Fields.RemoveAt(0);                         // Boost deleted, axis 0 free
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { edit.ToModel() };
            Assert.Equal(1, SlotOf("Temp"));
            Assert.Equal(2, SlotOf("Oil"));
        }

        [Fact]
        public void TheRowsIdentity_NeverChanges()
        {
            var clock = new Clock();
            using var a = Row(GameTelemetryFormat.Auto, clock);
            using var b = Row(GameTelemetryFormat.F1, clock, new[] { new TelemetryVariable("X", 0, 1, 0) });
            Assert.Equal(a.InstanceGuid, b.InstanceGuid);
            Assert.Equal(a.ProductGuid, b.ProductGuid);
        }

        [Fact]
        public void TheRow_OnAPortGamesShare_BindsNothingInTheTestHost()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock, port: GameTelemetryRuntime.DefaultPort);
            Assert.True(row.Open());
            Assert.Equal(TelemetryListenState.PortInUse, row.Status.State);
            Assert.Equal(0, row.BoundPort);
        }

        [Fact]
        public void TheRow_ReadsAPacketOverLoopback_AndForwardsIt()
        {
            int port = FreeLoopbackPort();
            using var forwardTarget = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            forwardTarget.Client.ReceiveTimeout = 3000;
            GameTelemetryRuntime.ForwardPort = ((IPEndPoint)forwardTarget.Client.LocalEndPoint).Port;

            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock, port: port);
            Assert.True(row.Open());
            Assert.Equal(TelemetryListenState.Listening, row.Status.State);
            Assert.Equal(IPAddress.Loopback, row.BoundAddress);

            var packet = Forza(324, speedMs: 12f);
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            game.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));

            IPEndPoint from = null;
            Assert.Equal(packet, forwardTarget.Receive(ref from));
            long until = Environment.TickCount64 + 3000;
            while (row.Status.Source == TelemetrySource.None && Environment.TickCount64 < until)
                System.Threading.Thread.Sleep(5);
            Assert.Equal(TelemetrySource.ForzaHorizon, row.Status.Source);
            Assert.Equal("127.0.0.1", row.Status.Peer);
        }

        [Fact]
        public void AForwardPortEqualToTheListeningPort_ForwardsNothing()
        {
            int port = FreeLoopbackPort();
            GameTelemetryRuntime.ForwardPort = port;
            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock, port: port);
            Assert.True(row.Open());
            var packet = Forza(324);
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            for (int i = 0; i < 3; i++)
                game.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
            System.Threading.Thread.Sleep(200);
            // Three packets in, three frames read: none looped back.
            Assert.Equal(TelemetrySource.ForzaHorizon, row.Status.Source);
        }

        private static int FreeLoopbackPort()
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint).Port;
        }

        [Fact]
        public void TheKunosReader_OpensNothing_WhereNoGameWrites()
        {
            using var memory = new KunosSharedMemory("pf531-absent-physics", "pf531-absent-static", "pf531-absent-graphics");
            Assert.False(memory.TryRead(5000));
            Assert.False(memory.IsOpen);
        }

        [Fact]
        public void TheKunosReader_OnTheGamesNames_OpensNothingInTheTestHost()
        {
            using var memory = new KunosSharedMemory();
            Assert.False(memory.TryRead(5000));
            Assert.False(memory.IsOpen);
        }

        [Fact]
        public void TheKunosReader_ReadsAPageThatMoves_AfterABaseline()
        {
            string tag = Guid.NewGuid().ToString("N");
            using var physics = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("pf531-" + tag + "-p", 800);
            using var statics = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("pf531-" + tag + "-s", 688);
            using var graphics = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew("pf531-" + tag + "-g", 296);
            using var memory = new KunosSharedMemory("pf531-" + tag + "-p", "pf531-" + tag + "-s", "pf531-" + tag + "-g");
            using (var view = physics.CreateViewAccessor()) view.Write(0, 1);
            Assert.False(memory.TryRead(1000));   // the baseline
            Assert.False(memory.TryRead(1001));   // nothing moved
            using (var view = physics.CreateViewAccessor()) view.Write(0, 2);
            Assert.True(memory.TryRead(1002));
            Assert.False(memory.TryRead(1002 + KunosSharedMemory.StaleMs));
            Assert.False(memory.IsOpen);          // a still page lets go
        }

        // ── The values Custom Expression variables read ──

        [Fact]
        public void TheExpressionValues_AreInTheirOwnUnits()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock);
            row.InjectUdp(F1Status(2025, 0, 12000), "127.0.0.1");
            row.InjectUdp(F1Telemetry(2025, 0), "127.0.0.1");
            row.GetCurrentState();

            Assert.Equal(180f, GameTelemetryRuntime.Read(TelemetryChannel.SpeedKmh, null), 2);
            Assert.Equal(111.85f, GameTelemetryRuntime.Read(TelemetryChannel.SpeedMph, null), 2);
            Assert.Equal(11000f, GameTelemetryRuntime.Read(TelemetryChannel.Rpm, null));
            Assert.Equal(12000f, GameTelemetryRuntime.Read(TelemetryChannel.MaxRpm, null));
            Assert.Equal(11000f / 12000f * 100f, GameTelemetryRuntime.Read(TelemetryChannel.RpmPercent, null), 3);
            Assert.Equal(-1f, GameTelemetryRuntime.Read(TelemetryChannel.Gear, null));
            Assert.Equal(75f, GameTelemetryRuntime.Read(TelemetryChannel.ThrottlePercent, null));
            Assert.Equal(25f, GameTelemetryRuntime.Read(TelemetryChannel.BrakePercent, null));
            Assert.Equal(40f, GameTelemetryRuntime.Read(TelemetryChannel.ClutchPercent, null), 3);
            Assert.Equal(-50f, GameTelemetryRuntime.Read(TelemetryChannel.SteeringPercent, null));
            Assert.Equal(1f, GameTelemetryRuntime.Read(TelemetryChannel.Driving, null));

            clock.Now += GameTelemetryDevice.SilenceMs + 1;
            row.GetCurrentState();
            Assert.Equal(0f, GameTelemetryRuntime.Read(TelemetryChannel.SpeedKmh, null));
            Assert.Equal(0f, GameTelemetryRuntime.Read(TelemetryChannel.Driving, null));
        }

        [Fact]
        public void TheExpressionValues_GoToRest_WhenTheRowCloses()
        {
            var clock = new Clock();
            var row = Row(GameTelemetryFormat.Auto, clock);
            row.InjectUdp(Forza(324), "127.0.0.1");
            row.GetCurrentState();
            Assert.True(GameTelemetryRuntime.Live);
            row.Dispose();
            Assert.False(GameTelemetryRuntime.Live);
            Assert.Equal(0f, GameTelemetryRuntime.Read(TelemetryChannel.Rpm, null));
        }

        [Fact]
        public void AVariable_ReadsByName()
        {
            var format = BinaryFormat();
            GameTelemetryRuntime.Formats = new[] { format };
            var variables = GameTelemetryRuntime.Variables;
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Custom, clock, variables, GameTelemetryRuntime.CompiledFormats[0]);
            row.InjectUdp(RigPacket(false), "127.0.0.1");
            row.GetCurrentState();
            Assert.Equal(1.5f, GameTelemetryRuntime.Read(TelemetryChannel.Variable, "boost"));
            Assert.Equal(0f, GameTelemetryRuntime.Read(TelemetryChannel.Variable, "Missing"));
        }

        /// <summary>Saving the formats swaps the variable list at once, and
        /// the row keeps publishing frames laid out by the list it opened
        /// with until the sweep reopens it. A name reads its own value
        /// through that window, never the value at its new position.</summary>
        [Fact]
        public void AVariable_ReadsItsOwnValue_BeforeTheRowReopensOnNewFormats()
        {
            GameTelemetryRuntime.Formats = new[] { BinaryFormat() };   // Boost, Temp, Far
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Custom, clock, GameTelemetryRuntime.Variables, GameTelemetryRuntime.CompiledFormats[0]);
            row.InjectUdp(RigPacket(false), "127.0.0.1");
            row.GetCurrentState();
            Assert.Equal(90.5f, GameTelemetryRuntime.Read(TelemetryChannel.Variable, "Temp"), 3);

            var edited = BinaryFormat();
            edited.Fields.RemoveAll(field => field.Name == "Boost");
            GameTelemetryRuntime.Formats = new[] { edited };              // Temp, Far
            Assert.Equal("Temp", GameTelemetryRuntime.Variables[0].Name);
            row.GetCurrentState();
            Assert.Equal(90.5f, GameTelemetryRuntime.Read(TelemetryChannel.Variable, "Temp"), 3);
        }

        [Theory]
        [InlineData("tel:SpeedKmh", TelemetryChannel.SpeedKmh, "")]
        [InlineData("tel:Gear", TelemetryChannel.Gear, "")]
        [InlineData("tel:var:Boost", TelemetryChannel.Variable, "Boost")]
        public void AVariablesSpec_RoundTrips(string spec, TelemetryChannel channel, string name)
        {
            var v = new MacroExpressionVariable { Spec = spec };
            Assert.Equal(MacroTriggerSource.GameTelemetry, v.Source);
            Assert.Equal(channel, v.TelemetryChannel);
            Assert.Equal(name, v.TelemetryVariable);
            Assert.True(v.IsBound);
            Assert.Equal(spec, v.Spec);
            Assert.Equal(TelemetryChannelNames.DisplayName(channel, name), v.DisplaySummary);
        }

        [Theory]
        [InlineData("tel:")]
        [InlineData("tel:Nonsense")]
        [InlineData("tel:var:")]
        [InlineData("tel:Variable")]
        [InlineData("tel:99")]
        public void ABrokenTelemetrySpec_IsUnbound(string spec)
        {
            var v = new MacroExpressionVariable { Spec = spec };
            Assert.False(v.IsBound);
            Assert.Equal("", v.Spec);
        }

        [Fact]
        public void PickingTheTelemetrySource_StartsAtSpeed_AndClearUnbinds()
        {
            var v = new MacroExpressionVariable { Source = MacroTriggerSource.GameTelemetry };
            Assert.Equal(TelemetryChannel.SpeedKmh, v.TelemetryChannel);
            Assert.Equal("tel:SpeedKmh", v.Spec);
            v.ClearBindingCommand.Execute(null);
            Assert.False(v.IsBound);
        }

        [Fact]
        public void ACustomExpressionMacro_FiresOnTelemetry()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock);
            var macro = new MacroItem
            {
                Name = "Fast in third",
                IsEnabled = true,
                PadIndex = 0,
                TriggerMode = MacroTriggerMode.CustomExpression,
                RepeatMode = MacroRepeatMode.UntilRelease,
                TriggerExpression = "a > 100 && b == 3",
            };
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Spec = "tel:SpeedKmh" });
            macro.TriggerExpressionVariables.Add(new MacroExpressionVariable { Spec = "tel:Gear" });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = Gamepad.A, DurationMs = 60000 });
            var im = new InputManager();

            row.InjectUdp(Forza(324, speedMs: 25f, gear: 3), "127.0.0.1");    // 90 km/h
            row.GetCurrentState();
            var gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { macro });
            Assert.Equal(0, gp.Buttons & Gamepad.A);

            row.InjectUdp(Forza(324, speedMs: 30f, gear: 3), "127.0.0.1");    // 108 km/h
            row.GetCurrentState();
            gp = new Gamepad();
            im.EvaluateSlotMacros(ref gp, new[] { macro });
            Assert.Equal(Gamepad.A, gp.Buttons & Gamepad.A);
        }

        // ── The sibling lists ──

        [Fact]
        public void TheTypeNumbers_ArePinned()
        {
            Assert.Equal(44, InputDeviceType.GameTelemetry);
            Assert.Equal(2, (int)MacroTriggerSource.GameTelemetry);
            Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, Enum.GetValues<GameTelemetryFormat>().Select(v => (int)v));
            Assert.Equal(Enumerable.Range(0, 14), Enum.GetValues<TelemetryChannel>().Select(v => (int)v));
            Assert.Equal(Enumerable.Range(0, 11), Enum.GetValues<CustomTelemetryTarget>().Select(v => (int)v));
            Assert.Equal(Enumerable.Range(0, 8), Enum.GetValues<CustomTelemetryType>().Select(v => (int)v));
            Assert.Equal(new[] { 0, 1 }, Enum.GetValues<CustomTelemetryKind>().Select(v => (int)v));
        }

        [Fact]
        public void TheRow_AnswersNoAnyDeviceSource_AndHasNoInputHiding()
        {
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.GameTelemetry));
            var row = new DeviceRowViewModel { DeviceTypeKey = "GameTelemetry", DevicePath = "telemetry://game" };
            Assert.True(row.IsInternalVirtual);
            Assert.False(row.ShowSubmitMapping);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.DeviceType_GameTelemetry, row.DeviceType);
            Assert.Equal("\uEC4A", PadForge.Common.DeviceTypeGlyph.For(InputDeviceType.GameTelemetry));
        }

        [Fact]
        public void TheRow_IsNamedOnTheMappingsTab()
        {
            var ud = new PadForge.Engine.Data.UserDevice { CapType = InputDeviceType.GameTelemetry };
            Assert.False(PadForge.Common.MappingDisplayResolver.UseRawNumberedNaming(ud));
        }

        // ── The wheel RPM lights ──

        [Fact]
        public void TheRow_TakesItsPortFromTheLightsHub_AndGivesItBack()
        {
            int port = FreeLoopbackPort();
            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock, port: port);
            Assert.False(TelemetryHub.IsPortYielded(port));
            Assert.True(row.Open());
            Assert.Equal(TelemetryListenState.Listening, row.Status.State);
            Assert.True(TelemetryHub.IsPortYielded(port));
            row.Dispose();
            Assert.False(TelemetryHub.IsPortYielded(port));
        }

        [Fact]
        public void ARowThatCannotBind_GivesThePortBackAtOnce()
        {
            using var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.LocalEndPoint).Port;
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock, port: port);
            Assert.True(row.Open());
            Assert.Equal(TelemetryListenState.PortInUse, row.Status.State);
            Assert.False(TelemetryHub.IsPortYielded(port));
        }

        /// <summary>A port another program held when the row opened is tried
        /// again, so the row listens once that program lets go, with no
        /// toggle and no restart.</summary>
        [Fact]
        public void ARowWhosePortWasInUse_ListensOnceItIsFree()
        {
            var holder = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            holder.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int port = ((IPEndPoint)holder.LocalEndPoint).Port;
            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock, port: port);
            Assert.True(row.Open());
            Assert.Equal(TelemetryListenState.PortInUse, row.Status.State);

            Assert.False(row.RetryBind());
            Assert.Equal(TelemetryListenState.PortInUse, row.Status.State);
            Assert.Equal(0, row.BoundPort);
            Assert.False(TelemetryHub.IsPortYielded(port));

            holder.Dispose();
            int version = row.StatusVersion;
            Assert.True(row.RetryBind());
            Assert.Equal(TelemetryListenState.Listening, row.Status.State);
            Assert.Equal(port, row.BoundPort);
            Assert.NotEqual(version, row.StatusVersion);

            var packet = Forza(324);
            using var game = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            game.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
            long until = Environment.TickCount64 + 3000;
            while (row.Status.Source == TelemetrySource.None && Environment.TickCount64 < until)
                System.Threading.Thread.Sleep(5);
            Assert.Equal(TelemetrySource.ForzaHorizon, row.Status.Source);
            Assert.False(row.RetryBind());   // listening: nothing to retry
        }

        /// <summary>The device sweep (every 2 s) is what retries the port,
        /// on the row it already has. The test host's gate refuses the
        /// every-address bind the app's own row makes, so the port stays in
        /// use here and each sweep is one more attempt.</summary>
        [Fact]
        public void TheSweep_RetriesAPortInUse_OnTheSameRow()
        {
            var saved = SettingsManager.UserDevices;
            var im = new InputManager();
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                GameTelemetryRuntime.Port = FreeLoopbackPort();
                GameTelemetryRuntime.Enabled = true;
                var sweep = typeof(InputManager).GetMethod("UpdateGameTelemetryDevice",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                sweep.Invoke(im, null);
                var row = im.GameTelemetry;
                Assert.NotNull(row);
                Assert.Equal(TelemetryListenState.PortInUse, row.Status.State);
                int attempts = row.BindAttempts;
                sweep.Invoke(im, null);
                sweep.Invoke(im, null);
                Assert.Same(row, im.GameTelemetry);
                Assert.Equal(attempts + 2, row.BindAttempts);
            }
            finally
            {
                im.ShutdownGameTelemetryInputs();
                SettingsManager.UserDevices = saved;
            }
        }

        [Fact]
        public void ARowOnAPortGamesShare_TakesNothingFromTheHubInTheTestHost()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock, port: GameTelemetryRuntime.DefaultPort);
            Assert.True(row.Open());
            Assert.False(TelemetryHub.IsPortYielded(GameTelemetryRuntime.DefaultPort));
        }

        [Fact]
        public void TheHub_KeepsAPortClosed_UntilItIsGivenBack()
        {
            const int port = 61531;
            TelemetryHub.YieldPort(port);
            TelemetryHub.YieldPort(port);
            Assert.True(TelemetryHub.IsPortYielded(port));
            TelemetryHub.ReclaimPort(port);
            Assert.False(TelemetryHub.IsPortYielded(port));
            TelemetryHub.ReclaimPort(port);
            Assert.False(TelemetryHub.IsPortYielded(port));
        }

        [Fact]
        public void TheLights_ReadTheRowsEngine_WhileTheGameDrives()
        {
            var clock = new Clock();
            using var row = Row(GameTelemetryFormat.Auto, clock);
            row.InjectUdp(Forza(324), "127.0.0.1");
            row.GetCurrentState();
            Assert.True(GameTelemetryRuntime.TryGetEngine(clock.Now, out float rpm, out float maxRpm, out bool learned));
            Assert.Equal(6000f, rpm);
            Assert.Equal(8000f, maxRpm);
            Assert.False(learned);
            // A row the engine stopped reading is not read.
            Assert.False(GameTelemetryRuntime.TryGetEngine(clock.Now + GameTelemetryRuntime.EngineStaleMs + 1, out _, out _, out _));

            // In a menu: Forza's IsRaceOn is 0.
            clock.Now += 10;
            row.InjectUdp(Forza(324, raceOn: false), "127.0.0.1");
            row.GetCurrentState();
            Assert.False(GameTelemetryRuntime.TryGetEngine(clock.Now, out _, out _, out _));

            // Silence past a second.
            clock.Now += 10;
            row.InjectUdp(Forza(324), "127.0.0.1");
            row.GetCurrentState();
            Assert.True(GameTelemetryRuntime.TryGetEngine(clock.Now, out _, out _, out _));
            clock.Now += GameTelemetryDevice.SilenceMs + 1;
            row.GetCurrentState();
            Assert.False(GameTelemetryRuntime.TryGetEngine(clock.Now, out _, out _, out _));

            // The row closing.
            row.InjectUdp(Forza(324), "127.0.0.1");
            row.GetCurrentState();
            Assert.True(GameTelemetryRuntime.TryGetEngine(clock.Now, out _, out _, out _));
            row.Dispose();
            Assert.False(GameTelemetryRuntime.TryGetEngine(clock.Now, out _, out _, out _));
        }

        [Fact]
        public void TheHubsRowSource_HandsTheLightsTheRowsRpm()
        {
            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock);
            var source = new GameTelemetryRowSource(learnedMax: false);
            var learnedSource = new GameTelemetryRowSource(learnedMax: true);
            Assert.False(source.TryGetSnapshot(out _));
            row.InjectUdp(Forza(324), "127.0.0.1");
            clock.Now = Environment.TickCount64;
            row.GetCurrentState();
            Assert.True(source.TryGetSnapshot(out var snap));
            Assert.Equal(6000f, snap.Rpm);
            Assert.Equal(8000f, snap.MaxRpm);
            Assert.Equal(0.75f, snap.RpmFraction);
            Assert.Equal("Game Telemetry", snap.Source);
            // Forza sends its max RPM, so the source last in the order is quiet.
            Assert.False(learnedSource.TryGetSnapshot(out _));
        }

        [Fact]
        public void ALearnedMaxRpm_ReachesTheLightsFromTheLastSource_BelowTheShiftCue()
        {
            var clock = new Clock { Now = Environment.TickCount64 };
            using var row = Row(GameTelemetryFormat.Auto, clock);
            var source = new GameTelemetryRowSource(learnedMax: false);
            var learnedSource = new GameTelemetryRowSource(learnedMax: true);

            // OutGauge sends no max RPM. Its first frame is its own peak.
            row.InjectUdp(OutGauge("beam", 3), "127.0.0.1");
            clock.Now = Environment.TickCount64;
            row.GetCurrentState();
            Assert.True(GameTelemetryRuntime.TryGetEngine(clock.Now, out float rpm, out float maxRpm, out bool learned));
            Assert.Equal(4000f, rpm);
            Assert.Equal(4000f, maxRpm);
            Assert.True(learned);
            Assert.False(source.TryGetSnapshot(out _));
            Assert.True(learnedSource.TryGetSnapshot(out var snap));
            Assert.Equal(4000f * GameTelemetryRowSource.LearnedHeadroom, snap.MaxRpm);
            // Every wheel's strip lights and holds still, where the bare peak
            // (a fraction of 1) blinks the shift cue.
            float frac = snap.RpmFraction;
            Assert.NotEqual(0, (int)RpmLedMap.Logitech(frac, blinkOn: false));
            Assert.Equal(RpmLedMap.Logitech(frac, blinkOn: true), RpmLedMap.Logitech(frac, blinkOn: false));
            Assert.Equal(RpmLedMap.Fanatec(frac, blinkOn: true), RpmLedMap.Fanatec(frac, blinkOn: false));
            Assert.Equal(RpmLedMap.Thrustmaster(frac, blinkOn: true), RpmLedMap.Thrustmaster(frac, blinkOn: false));
            Assert.NotEqual(RpmLedMap.Logitech(1f, blinkOn: true), RpmLedMap.Logitech(1f, blinkOn: false));

            // Revved out, then back down: the peak sets the max.
            var high = OutGauge("beam", 3);
            F32(high, 16, 7000f);
            clock.Now += 10;
            row.InjectUdp(high, "127.0.0.1");
            row.GetCurrentState();
            clock.Now += 10;
            row.InjectUdp(OutGauge("beam", 3), "127.0.0.1");
            row.GetCurrentState();
            Assert.True(learnedSource.TryGetSnapshot(out snap));
            Assert.Equal(4000f, snap.Rpm);
            Assert.Equal(7000f, snap.MaxRpm);
        }

        /// <summary>The RPM lights' own F1 listener (the hub's, while the Game
        /// Telemetry row is off) reads the four formats the row reads, each
        /// by its own layout, and only a packet of its id's size: F1 22's
        /// 24-byte header and 47-byte Car Status entries, the 29-byte header
        /// and 55-byte entries after (vAzhure F12022.cs to F12025.cs).</summary>
        [Theory]
        [InlineData(2022)]
        [InlineData(2023)]
        [InlineData(2024)]
        [InlineData(2025)]
        public void TheLightsF1Listener_ReadsEveryFormatTheRowReads(int year)
        {
            int header = year == 2022 ? 24 : 29;
            int entry = year == 2022 ? 47 : 55;
            var status = F1Status(year, 3, 12500);
            U16(status, header + 3 * entry + 19, 4000);
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.MaxAndIdle,
                CodemastersUdpTelemetrySource.ReadF1(status, out int format, out _, out float max, out float idle));
            Assert.Equal(year, format);
            Assert.Equal(12500f, max);
            Assert.Equal(4000f, idle);
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.Rpm,
                CodemastersUdpTelemetrySource.ReadF1(F1Telemetry(year, 3), out format, out float rpm, out _, out _));
            Assert.Equal(11000f, rpm);
        }

        [Fact]
        public void TheLightsF1Listener_ReadsNoOtherFormat_AndNoPacketOfAnotherSize()
        {
            var old = F1Telemetry(2023, 0);
            U16(old, 0, 2021);
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.None, CodemastersUdpTelemetrySource.ReadF1(old, out _, out _, out _, out _));
            // Car Telemetry's id at Car Status's size.
            var foreign = F1(2024, 6, 1239, 0);
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.None, CodemastersUdpTelemetrySource.ReadF1(foreign, out _, out _, out _, out _));
            var shortStatus = F1(2025, 7, 237, 0);
            Assert.Equal(CodemastersUdpTelemetrySource.F1Read.None, CodemastersUdpTelemetrySource.ReadF1(shortStatus, out _, out _, out _, out _));
        }

        [Fact]
        public void TheHub_ReadsTheRowFirst_AndOpensNoSourceOnAPortTheRowHolds()
        {
            foreach (var type in new[] { typeof(CodemastersUdpTelemetrySource), typeof(ForzaUdpTelemetrySource), typeof(OutGaugeTelemetrySource) })
                Assert.True(typeof(IUdpTelemetrySource).IsAssignableFrom(type), type.Name);
            Assert.False(typeof(IUdpTelemetrySource).IsAssignableFrom(typeof(GameTelemetryRowSource)));

            string Source(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Telemetry", name));
            string hub = Source("TelemetryHub.cs");
            int rowSource = hub.IndexOf("new GameTelemetryRowSource(learnedMax: false),", StringComparison.Ordinal);
            // Named without their "new", which the listener scan reads in strings too.
            int forza = hub.IndexOf("ForzaUdpTelemetrySource(_forzaPort),", StringComparison.Ordinal);
            int outGauge = hub.IndexOf("OutGaugeTelemetrySource(),", StringComparison.Ordinal);
            int learnedRow = hub.IndexOf("new GameTelemetryRowSource(learnedMax: true),", StringComparison.Ordinal);
            int end = hub.IndexOf("};", learnedRow < 0 ? 0 : learnedRow, StringComparison.Ordinal);
            Assert.True(rowSource > 0 && forza > rowSource);
            // A learned max comes last, behind every source with the game's own.
            Assert.True(outGauge > forza && learnedRow > outGauge && end > learnedRow);
            Assert.Single(hub.Substring(learnedRow, end - learnedRow).Split("Source(").Skip(1));
            Assert.Contains("if (s is IUdpTelemetrySource udp && _yieldedPorts.Contains(udp.Port)) continue;", hub);
            Assert.Contains("public int Port => ListenPort;", Source("CodemastersUdpTelemetrySource.cs"));
            Assert.Contains("private const int ListenPort = 20777;", Source("CodemastersUdpTelemetrySource.cs"));
            Assert.Contains("public int Port => _port;", Source("ForzaUdpTelemetrySource.cs"));
            Assert.Contains("public int Port => _port;", Source("OutGaugeTelemetrySource.cs"));

            // The row takes the port after the gate and before its socket.
            string device = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "Telemetry", "GameTelemetryDevice.cs"));
            int gate = device.IndexOf("NetworkListenerGate.Enter(BindAddress);", StringComparison.Ordinal);
            int yield = device.IndexOf("TelemetryHub.YieldPort(_port);", StringComparison.Ordinal);
            int socket = device.IndexOf("var s = new Socket(", StringComparison.Ordinal);
            Assert.True(gate > 0 && yield > gate && socket > yield);
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "PadForge.sln"))) return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("PadForge.sln not found");
        }

        // ── The Dashboard section ──

        [Fact]
        public void PickingProjectCars2OrTheTrucks_MovesThePortToTheirs()
        {
            var vm = new DashboardViewModel();
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.Format == GameTelemetryFormat.ProjectCars2);
            Assert.Equal(GameTelemetryRuntime.ProjectCars2Port, vm.GameTelemetryPort);
            Assert.Equal(GameTelemetryRuntime.ProjectCars2Port, GameTelemetryRuntime.Port);
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.Format == GameTelemetryFormat.TruckSimulator);
            Assert.Equal(GameTelemetryRuntime.TruckSimulatorPort, vm.GameTelemetryPort);
            // A game whose port the player sets keeps the port typed.
            vm.GameTelemetryPort = 30000;
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.Format == GameTelemetryFormat.Forza);
            Assert.Equal(30000, vm.GameTelemetryPort);
            Assert.Equal(GameTelemetryFormat.Forza, GameTelemetryRuntime.Format);
        }

        /// <summary>Leaving Project CARS 2 or the ETS2 plugin, whose ports are
        /// fixed, puts the port back to the default the other games send to,
        /// unless the player typed another port since.</summary>
        [Fact]
        public void LeavingAFixedPortSender_PutsTheDefaultPortBack()
        {
            var vm = new DashboardViewModel();
            GameTelemetryOption Pick(GameTelemetryFormat format) => vm.GameTelemetryOptions.First(o => o.Format == format);

            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.ProjectCars2);
            Assert.Equal(GameTelemetryRuntime.ProjectCars2Port, vm.GameTelemetryPort);
            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.F1);
            Assert.Equal(GameTelemetryRuntime.DefaultPort, vm.GameTelemetryPort);
            Assert.Equal(GameTelemetryRuntime.DefaultPort, GameTelemetryRuntime.Port);

            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.TruckSimulator);
            vm.ResetGameTelemetryGameCommand.Execute(null);
            Assert.Equal(GameTelemetryFormat.Auto, vm.GameTelemetryFormat);
            Assert.Equal(GameTelemetryRuntime.DefaultPort, vm.GameTelemetryPort);

            // One fixed-port sender to the other takes the new one's port.
            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.TruckSimulator);
            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.ProjectCars2);
            Assert.Equal(GameTelemetryRuntime.ProjectCars2Port, vm.GameTelemetryPort);

            // A port typed after the pick is the player's, and stays.
            vm.GameTelemetryPort = 30000;
            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.Forza);
            Assert.Equal(30000, vm.GameTelemetryPort);

            // A saved profile's port is applied as saved.
            vm.SelectedGameTelemetryOption = Pick(GameTelemetryFormat.ProjectCars2);
            vm.ApplyGameTelemetrySettings(GameTelemetryFormat.Forza, "", GameTelemetryRuntime.ProjectCars2Port, 0, 300);
            Assert.Equal(GameTelemetryRuntime.ProjectCars2Port, vm.GameTelemetryPort);
        }

        /// <summary>Assetto Corsa is read from shared memory alone, so the
        /// row opens no socket and copies nothing on, and the port and the
        /// forward port are off while it is picked.</summary>
        [Fact]
        public void ThePorts_AreOffWhileAssettoCorsaIsPicked()
        {
            var vm = new DashboardViewModel();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.True(vm.GameTelemetryReadsUdp);
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.Format == GameTelemetryFormat.AssettoCorsa);
            Assert.False(vm.GameTelemetryReadsUdp);
            Assert.Contains(nameof(DashboardViewModel.GameTelemetryReadsUdp), changed);
            Assert.False(new TelemetryReader(GameTelemetryFormat.AssettoCorsa, null, null, 0).ReadsUdp);
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.Format == GameTelemetryFormat.Auto);
            Assert.True(vm.GameTelemetryReadsUdp);

            // Both rows, each box with its reset button. They are the first
            // two rows under More on the Game Telemetry page (#574), each a
            // row that rests while the game is read from shared memory.
            string page = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "ExtrasPage.xaml"));
            const string Row = "<controls:SettingRow MinHeight=\"44\" IsEnabled=\"{Binding GameTelemetryReadsUdp}\">";
            var rows = new List<string>();
            for (int at = page.IndexOf(Row, StringComparison.Ordinal); at >= 0; at = page.IndexOf(Row, at + 1, StringComparison.Ordinal))
                rows.Add(page.Substring(at, page.IndexOf("</controls:SettingRow>", at, StringComparison.Ordinal) - at));
            Assert.Equal(2, rows.Count);
            Assert.Contains("{Binding GameTelemetryPort,", rows[0]);
            Assert.Contains("{Binding GameTelemetryForwardPort,", rows[1]);
            Assert.All(rows, body => Assert.Contains("reset:SettingResetButton", body));
        }

        /// <summary>The picker, the profiles and the reader name a format by
        /// its name, case aside, so a hand-edited file with two formats of
        /// one name loads the first alone, as a name that cannot be read is
        /// dropped.</summary>
        [Fact]
        public void ASecondFormatOfTheSameName_IsDropped_FirstWins()
        {
            var first = BinaryFormat();               // "Rig", Boost, Temp, Far
            var second = new CustomTelemetryFormat
            {
                Name = "RIG",
                Length = 12,
                Fields = { new CustomTelemetryField { Target = CustomTelemetryTarget.Variable, Name = "Oil", Offset = 0 } },
            };
            GameTelemetryRuntime.Formats = new[] { first, second };
            var compiled = Assert.Single(GameTelemetryRuntime.CompiledFormats);
            Assert.Equal("Rig", compiled.Name);
            Assert.Equal(new[] { "Boost", "Temp", "Far" }, GameTelemetryRuntime.Variables.Select(v => v.Name));

            var vm = new DashboardViewModel();
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { first, second };
            var custom = Assert.Single(vm.GameTelemetryOptions, o => o.Format == GameTelemetryFormat.Custom);
            Assert.Equal("Rig", custom.CustomName);
        }

        [Fact]
        public void ASavedCustomGameWhoseFormatIsGone_ReadsAsAutoDetect()
        {
            var vm = new DashboardViewModel();
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { new() { Name = "Rig", Length = 16 } };
            vm.ApplyGameTelemetrySettings(GameTelemetryFormat.Custom, "Rig", 30000, 30001, 250);
            Assert.Equal(GameTelemetryFormat.Custom, GameTelemetryRuntime.Format);
            Assert.Equal("Rig", GameTelemetryRuntime.CustomFormatName);
            Assert.Equal(30000, GameTelemetryRuntime.Port);
            Assert.Equal(30001, GameTelemetryRuntime.ForwardPort);
            Assert.Equal(250, GameTelemetryRuntime.FullSpeedKmh);

            // Numbers out of range keep the values the section holds.
            vm.ApplyGameTelemetrySettings(GameTelemetryFormat.Custom, "Gone", 80, 70000, 5);
            Assert.Equal(GameTelemetryFormat.Auto, vm.GameTelemetryFormat);
            Assert.Equal(string.Empty, vm.GameTelemetryCustomFormat);
            Assert.Equal(30000, vm.GameTelemetryPort);
            Assert.Equal(30001, vm.GameTelemetryForwardPort);
            Assert.Equal(250, vm.GameTelemetryFullSpeed);
            vm.ApplyGameTelemetrySettings((GameTelemetryFormat)99, null, 30000, 0, 300);
            Assert.Equal(GameTelemetryFormat.Auto, vm.GameTelemetryFormat);
        }

        [Fact]
        public void DeletingThePickedFormat_GoesBackToAutoDetect()
        {
            var vm = new DashboardViewModel();
            vm.GameTelemetryFormats = new List<CustomTelemetryFormat>
            {
                new() { Name = "Rig", Length = 16 },
                new() { Name = "Dash", Kind = CustomTelemetryKind.Json, Key = "rpm" },
            };
            vm.SelectedGameTelemetryOption = vm.GameTelemetryOptions.First(o => o.CustomName == "Dash");
            Assert.Equal(GameTelemetryFormat.Custom, GameTelemetryRuntime.Format);
            Assert.Equal("Dash", GameTelemetryRuntime.CustomFormatName);

            vm.GameTelemetryFormats = new List<CustomTelemetryFormat> { new() { Name = "Rig", Length = 16 } };
            Assert.Equal(GameTelemetryFormat.Auto, vm.GameTelemetryFormat);
            Assert.Equal(GameTelemetryFormat.Auto, GameTelemetryRuntime.Format);
            Assert.Equal(new[] { "Rig" },
                vm.GameTelemetryOptions.Where(o => o.Format == GameTelemetryFormat.Custom).Select(o => o.CustomName));
            Assert.Same(vm.GameTelemetryOptions[0], vm.SelectedGameTelemetryOption);
        }

        // ── The custom format dialog ──

        [Fact]
        public void ANewFormat_TakesTheFirstFreeNumber()
        {
            string stem = PadForge.Resources.Strings.Strings.Instance.Telemetry_Formats_NewName_Format;
            string one = string.Format(CultureInfo.CurrentCulture, stem, 1);
            string two = string.Format(CultureInfo.CurrentCulture, stem, 2);
            Assert.Equal(one, CustomTelemetryFormatsDialog.UniqueName(new[] { "Rig" }, stem));
            Assert.Equal(two, CustomTelemetryFormatsDialog.UniqueName(new[] { " " + one.ToUpperInvariant() + " ", null }, stem));
        }

        [Fact]
        public void TheDialog_RefusesAFormatThatCouldNotRead()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var culture = CultureInfo.CurrentCulture;
            var rig = new TelemetryFormatEdit { Name = "Rig", Length = 8 };
            rig.Fields.Add(new TelemetryFieldEdit { Target = CustomTelemetryTarget.Rpm, Offset = 4, Type = CustomTelemetryType.F32 });
            Assert.Null(CustomTelemetryFormatsDialog.Problem(new[] { rig }));

            rig.Fields[0].Offset = 6;
            Assert.Equal(string.Format(culture, s.Telemetry_Formats_ErrorOffset_Format, "Rig", 8),
                CustomTelemetryFormatsDialog.Problem(new[] { rig }));
            rig.Fields[0].Offset = 4;

            var twin = new TelemetryFormatEdit { Name = " rig " };
            Assert.Equal(string.Format(culture, s.Telemetry_Formats_ErrorDuplicate_Format, "rig"),
                CustomTelemetryFormatsDialog.Problem(new[] { rig, twin }));
            twin.Name = "a:b";
            Assert.Equal(string.Format(culture, s.Telemetry_Formats_ErrorName_Format, 2),
                CustomTelemetryFormatsDialog.Problem(new[] { rig, twin }));

            var dash = new TelemetryFormatEdit { Name = "Dash", Kind = CustomTelemetryKind.Json };
            dash.Fields.Add(new TelemetryFieldEdit { Target = CustomTelemetryTarget.Speed });
            Assert.Equal(string.Format(culture, s.Telemetry_Formats_ErrorKey_Format, "Dash"),
                CustomTelemetryFormatsDialog.Problem(new[] { dash }));
            dash.Fields[0].Key = "speed";
            dash.Fields.Add(new TelemetryFieldEdit { Target = CustomTelemetryTarget.Variable, Key = "boost" });
            Assert.Equal(string.Format(culture, s.Telemetry_Formats_ErrorVariable_Format, "Dash"),
                CustomTelemetryFormatsDialog.Problem(new[] { dash }));
            dash.Fields[1].Name = "Boost";
            Assert.Null(CustomTelemetryFormatsDialog.Problem(new[] { rig, dash }));
        }

        [Fact]
        public void TheDialogsCopies_LeaveTheSavedFormatsAlone()
        {
            var saved = BinaryFormat(false);
            var edit = TelemetryFormatEdit.From(saved);
            Assert.All(edit.Fields, f => Assert.Equal(CustomTelemetryKind.Binary, f.Kind));
            edit.Name = "Changed";
            edit.Fields[0].Scale = 9;
            Assert.NotEqual("Changed", saved.Name);
            Assert.NotEqual(9, saved.Fields[0].Scale);
            var copy = edit.Copy("Copy");
            Assert.Equal("Copy", copy.Name);
            Assert.Equal(edit.Fields.Count, copy.Fields.Count);
            Assert.Equal(9, copy.ToModel().Fields[0].Scale);
            edit.Kind = CustomTelemetryKind.Json;
            Assert.All(edit.Fields, f => Assert.True(f.IsJson));
        }

        [Fact]
        public void AFieldKeepsItsNumbers_WhenHandedNaNOrInfinity()
        {
            var row = new TelemetryFieldEdit { Scale = 0.5, Min = -1, Max = 2 };
            row.Scale = double.NaN;
            row.Min = double.PositiveInfinity;
            row.Max = double.NegativeInfinity;
            Assert.Equal(0.5, row.Scale);
            Assert.Equal(-1, row.Min);
            Assert.Equal(2, row.Max);

            var xml = new XmlSerializer(typeof(CustomTelemetryFormat));
            using var reader = new StringReader(
                "<CustomTelemetryFormat Name=\"Rig\"><Field Target=\"Rpm\" Scale=\"NaN\" Min=\"INF\" Max=\"2\" /></CustomTelemetryFormat>");
            var loaded = (CustomTelemetryFormat)xml.Deserialize(reader);
            Assert.Equal(1.0, loaded.Fields[0].Scale);
            Assert.Equal(0.0, loaded.Fields[0].Min);
            Assert.Equal(2.0, loaded.Fields[0].Max);
        }

        [Fact]
        public void AScaledValuePastADoublesRange_LeavesItsTargetAlone()
        {
            var format = new CustomTelemetryFormat { Name = "Big", Length = 8 };
            format.Fields.Add(new CustomTelemetryField
            {
                Target = CustomTelemetryTarget.Rpm, Offset = 0, Type = CustomTelemetryType.F64, Scale = 1e300,
            });
            var compiled = new CompiledTelemetryFormat(format, Array.Empty<TelemetryVariable>());
            var packet = new byte[8];
            F64(packet, 0, 1e300);
            var frame = new TelemetryFrame();
            frame.Reset(0);
            Assert.True(compiled.TryRead(packet, frame, pinned: true));
            Assert.Equal(0f, frame.Rpm);
        }
    }
}
