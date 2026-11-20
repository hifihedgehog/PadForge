using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using PadForge.Common.Telemetry;
using PadForge.Engine.Steering;

namespace PadForge.Common.Input.Telemetry
{
    /// <summary>What a decoder made of a packet.</summary>
    internal enum TelemetryDecode
    {
        /// <summary>Not this format.</summary>
        No = 0,
        /// <summary>This format, with nothing for the frame: an F1 packet
        /// other than Car Telemetry, a Project CARS 2 game state.</summary>
        Claimed = 1,
        /// <summary>This format, and the frame holds a new reading.</summary>
        Frame = 2,
    }

    /// <summary>
    /// One decoder per built-in format (issue #531). Each reads only the
    /// fields PadForge uses, at the offsets the cloned references give, into
    /// a frame the caller has put at rest. Little-endian throughout, as every
    /// one of these games sends.
    /// </summary>
    internal static class TelemetryDecoders
    {
        // ─────────────────────────────────────────────
        //  Forza
        // ─────────────────────────────────────────────

        /// <summary>Forza's Data Out lengths: Sled (232), Motorsport 7 Dash
        /// (311), Horizon (324, and 323, the shortest packet fh6-tel's parser
        /// reads), Motorsport 2023 Dash (331). austinbaccus/forza-telemetry
        /// Program.cs 204-216 picks the layout by these lengths.</summary>
        internal static bool IsForzaLength(int length) => length is 232 or 311 or 323 or 324 or 331;

        /// <summary>
        /// Forza Data Out (forza-telemetry FMData.cs, fh6-tel parser.rs).
        /// IsRaceOn s32 at 0, EngineMaxRpm at 8, CurrentEngineRpm at 16. A
        /// Horizon packet carries 12 more bytes from 232 on, so its Dash
        /// fields sit 12 later. Gear 0 is reverse and 11 neutral. A Sled
        /// packet has no Dash fields, so its speed comes from the local
        /// velocity at 32. The front wheels' puddle depths at 132 and 136
        /// (FMData.cs WheelInPuddleFl, Fr) are in every layout, and are the
        /// game's one word on the road.
        /// </summary>
        internal static bool TryForza(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            int shift;
            switch (p.Length)
            {
                case 232: f.Source = TelemetrySource.ForzaSled; shift = -1; break;
                case 311: f.Source = TelemetrySource.ForzaMotorsport7; shift = 0; break;
                case 331: f.Source = TelemetrySource.ForzaMotorsport; shift = 0; break;
                case 323:
                case 324: f.Source = TelemetrySource.ForzaHorizon; shift = 12; break;
                default: return false;
            }
            f.Driving = I32(p, 0) != 0;
            f.DrivingReported = true;
            f.MaxRpm = F32(p, 8);
            f.Rpm = F32(p, 16);
            f.Grip = (float)RoadGrip.FrontAxle(SurfaceGrips.Forza(F32(p, 132)), SurfaceGrips.Forza(F32(p, 136)));
            if (shift < 0)
            {
                float vx = F32(p, 32), vy = F32(p, 36), vz = F32(p, 40);
                f.SpeedMs = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                f.SpeedReported = true;
                return true;
            }
            f.SpeedMs = F32(p, 244 + shift);
            f.SpeedReported = true;
            f.Throttle = p[303 + shift] / 255f;
            f.Brake = p[304 + shift] / 255f;
            f.Clutch = p[305 + shift] / 255f;
            f.Handbrake = p[306 + shift] / 255f;
            f.Gear = ForzaGear(p[307 + shift]);
            f.Steering = (sbyte)p[308 + shift] / 127f;
            return true;
        }

        /// <summary>0 is reverse and 11 neutral (fh6-tel CenterPanel.svelte
        /// 16-17, forza-telemetry Dashboard.tsx 499). Anything else past the
        /// tenth gear reads as neutral.</summary>
        internal static int ForzaGear(byte raw) => raw switch
        {
            0 => -1,
            >= 1 and <= 10 => raw,
            _ => 0,
        };

        // ─────────────────────────────────────────────
        //  F1 22 to F1 25
        // ─────────────────────────────────────────────

        /// <summary>What the F1 decoder keeps between packets: the year it
        /// last heard and the max RPM from the last Car Status packet.</summary>
        internal sealed class F1State
        {
            public int Year;
            public float MaxRpm;
        }

        /// <summary>Each format's packet sizes by id, so a packet must have
        /// one of its format's sizes. F1 22's from vAzhure F12022.cs, the
        /// others from F12023.cs, F12024.cs and F12025.cs, and 2024's match
        /// the f1-24-udp README's "Size:" lines.</summary>
        private static readonly int[] F1_22Sizes =
            { 1464, 632, 972, 40, 1257, 1102, 1347, 1058, 1015, 1191, 948, 1155 };
        private static readonly int[] F1_23Sizes =
            { 1349, 644, 1131, 45, 1306, 1107, 1352, 1239, 1020, 1218, 953, 1460, 231, 217 };
        private static readonly int[] F1_24Sizes =
            { 1349, 753, 1285, 45, 1350, 1133, 1352, 1239, 1020, 1306, 953, 1460, 231, 237, 101 };
        private static readonly int[] F1_25Sizes =
            { 1349, 753, 1285, 45, 1284, 1133, 1352, 1239, 1042, 954, 1041, 1460, 231, 273, 101, 1131 };

        /// <summary>The size of packet <paramref name="id"/> in F1 UDP format
        /// <paramref name="format"/> (2022 to 2025), or -1 for an id or a
        /// format there is none of. Shared with the RPM lights' own F1
        /// listener.</summary>
        internal static int F1PacketSize(int format, int id)
        {
            int[] sizes = F1Sizes(format);
            return sizes != null && id >= 0 && id < sizes.Length ? sizes[id] : -1;
        }

        private static int[] F1Sizes(int format) => format switch
        {
            2022 => F1_22Sizes,
            2023 => F1_23Sizes,
            2024 => F1_24Sizes,
            2025 => F1_25Sizes,
            _ => null,
        };

        /// <summary>
        /// F1's UDP (MacManley/f1-24-udp PHeader.h, PacketCarTelemetryData.h,
        /// PacketCarStatusData.h, and vAzhure F12022.cs to F12025.cs). The
        /// packet format at byte 0. F1 22's header is 24 bytes (packet id at
        /// 5, player car at 22), and later formats add the game year at 2
        /// for 29 bytes (packet id at 6, player car at 27). The game year is
        /// not checked: F1 24 and F1 25 also send the two formats before
        /// their own (f1-24-udp README, "Will my old app still work"), and
        /// then the byte names the game or reads 0. It names the game on the
        /// status line when it is not 0. Car Telemetry (6) holds
        /// 22 entries of 60 bytes, each closing with the surface under its
        /// four wheels at 56, rear left, rear right, front left, front right
        /// (PacketCarTelemetryData.h m_surfaceType, and the README's "What
        /// is the order of the wheel arrays?"). Car Status (7) entries are
        /// 47 bytes in F1 22 and 55 later, max RPM at 17. Every F1 packet is claimed, so
        /// a packet of another type never falls through to a rule that only
        /// knows lengths (format 2024's Motion Ex is 237 bytes, EA SPORTS
        /// WRC's length).
        /// </summary>
        internal static TelemetryDecode TryF1(ReadOnlySpan<byte> p, F1State state, TelemetryFrame f)
        {
            if (p.Length < 24) return TelemetryDecode.No;
            int year = BinaryPrimitives.ReadUInt16LittleEndian(p);
            int[] sizes = F1Sizes(year);
            if (sizes == null || Array.IndexOf(sizes, p.Length) < 0) return TelemetryDecode.No;
            int header, idAt, carAt, statusEntry;
            if (year == 2022) { header = 24; idAt = 5; carAt = 22; statusEntry = 47; }
            else { header = 29; idAt = 6; carAt = 27; statusEntry = 55; }
            int id = p[idAt];
            if (id > 15) return TelemetryDecode.No;
            if (state.Year != year)
            {
                state.Year = year;
                state.MaxRpm = 0f;
            }
            int car = p[carAt];
            if (car >= 22) return TelemetryDecode.Claimed;
            if (id == 7)
            {
                if (p.Length >= header + 22 * statusEntry)
                    state.MaxRpm = U16(p, header + car * statusEntry + 17);
                return TelemetryDecode.Claimed;
            }
            if (id != 6 || p.Length < header + 22 * 60) return TelemetryDecode.Claimed;
            int e = header + car * 60;
            f.Source = TelemetrySource.F1;
            // The game, for the status line: formats 2023 and later carry
            // its year at byte 2, which reads 0 from some games on an older
            // format. The layout above follows the format alone.
            f.F1Year = year >= 2023 && p[2] != 0 ? 2000 + p[2] : year;
            f.SpeedMs = U16(p, e) / 3.6f;
            f.SpeedReported = true;
            f.Throttle = F32(p, e + 2);
            f.Steering = F32(p, e + 6);
            f.Brake = F32(p, e + 10);
            f.Clutch = p[e + 14] / 100f;
            int gear = (sbyte)p[e + 15];
            f.Gear = gear < 0 ? -1 : gear;
            f.Rpm = U16(p, e + 16);
            f.MaxRpm = state.MaxRpm;
            f.Grip = (float)RoadGrip.FrontAxle(SurfaceGrips.F1(p[e + 58]), SurfaceGrips.F1(p[e + 59]));
            return TelemetryDecode.Frame;
        }

        // ─────────────────────────────────────────────
        //  Codemasters (extradata 3)
        // ─────────────────────────────────────────────

        /// <summary>
        /// The Codemasters packet DiRT Rally 2.0 sends with extradata 3: 66
        /// little-endian floats (CMTelemetry codemasters.h). CMTelemetry's
        /// struct carries four more floats, 280 bytes, with the same first
        /// 66. Speed in m/s at 28, throttle 116, steer 120, brake 124,
        /// clutch 128, gear 132 (-1 and 10 are reverse), engine rate 148 and
        /// max RPM 252, both in tenths of an RPM (tm-bt-led dirt.js 148-151,
        /// vAzhure UDPClient.cs 136-137).
        /// </summary>
        internal static bool TryCodemasters(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            if (p.Length != 264 && p.Length != 280) return false;
            f.Source = TelemetrySource.Codemasters;
            f.SpeedMs = F32(p, 28);
            f.SpeedReported = true;
            f.Throttle = F32(p, 116);
            f.Steering = F32(p, 120);
            f.Brake = F32(p, 124);
            f.Clutch = F32(p, 128);
            f.Gear = CodemastersGear(F32(p, 132));
            f.Rpm = F32(p, 148) * 10f;
            f.MaxRpm = F32(p, 252) * 10f;
            return true;
        }

        /// <summary>-1 and 10 are reverse, 0 neutral (dirt-rally-telemetry-dashboard
        /// Gear.java).</summary>
        internal static int CodemastersGear(float raw)
        {
            if (!float.IsFinite(raw)) return 0;
            int g = (int)MathF.Round(raw);
            return g < 0 || g == 10 ? -1 : g;
        }

        // ─────────────────────────────────────────────
        //  EA SPORTS WRC
        // ─────────────────────────────────────────────

        /// <summary>
        /// EA SPORTS WRC's default <c>session_update</c> layout, from the
        /// game's own readme/udp/wrc.json and readme/channels.json: packed
        /// little-endian channels, 237 bytes. Some builds pad 3 bytes after
        /// <c>shiftlights_rpm_valid</c> (240), and a layout that adds the
        /// <c>packet_4cc</c> header puts 4 bytes in front (241, 244), which
        /// tm-bt-led wrc.js reads the same way. Gear index at 37 with the
        /// neutral and reverse indexes at 38 and 39, speed in m/s at 41, max
        /// RPM 185, RPM 193, throttle 197, brake 201, clutch 205, steering
        /// 209, handbrake 213.
        /// </summary>
        internal static bool TryEaSportsWrc(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            int start = p.Length is 241 or 244 ? 4 : 0;
            int length = p.Length - start;
            int pad;
            if (length == 237) pad = 0;
            else if (length == 240) pad = 3;
            else return false;
            var b = p.Slice(start);
            f.Source = TelemetrySource.EaSportsWrc;
            f.Gear = EaSportsWrcGear(b[37 + pad], b[38 + pad], b[39 + pad]);
            f.SpeedMs = F32(b, 41 + pad);
            f.SpeedReported = true;
            f.MaxRpm = F32(b, 185 + pad);
            f.Rpm = F32(b, 193 + pad);
            f.Throttle = F32(b, 197 + pad);
            f.Brake = F32(b, 201 + pad);
            f.Clutch = F32(b, 205 + pad);
            f.Steering = F32(b, 209 + pad);
            f.Handbrake = F32(b, 213 + pad);
            return true;
        }

        /// <summary>tm-bt-led wrc.js's reading: the neutral index is neutral,
        /// the reverse index is reverse, and a gear past the neutral index
        /// counts from it.</summary>
        internal static int EaSportsWrcGear(int index, int neutral, int reverse)
        {
            if (index == neutral) return 0;
            if (index == reverse) return -1;
            return index > neutral ? index - neutral : index;
        }

        // ─────────────────────────────────────────────
        //  OutGauge (BeamNG.drive, Live for Speed)
        // ─────────────────────────────────────────────

        /// <summary>
        /// OutGauge (InSim.NET OutGaugePack.cs, BeamNG's protocol page): 92
        /// bytes, 96 with an OutGauge ID. Car name at 4 ("beam" from BeamNG),
        /// gear at 10 (0 reverse, 1 neutral, 2 first), speed in m/s at 12,
        /// RPM 16, the dash lights switched on at 44 (bit 2 is the
        /// handbrake), throttle, brake and clutch at 48, 52 and 56. No
        /// steering and no max RPM.
        /// </summary>
        internal static bool TryOutGauge(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            if (p.Length != 92 && p.Length != 96) return false;
            bool beam = p[4] == (byte)'b' && p[5] == (byte)'e' && p[6] == (byte)'a' && p[7] == (byte)'m';
            f.Source = beam ? TelemetrySource.BeamNg : TelemetrySource.LiveForSpeed;
            int gear = p[10];
            f.Gear = gear == 0 ? -1 : gear - 1;
            f.SpeedMs = F32(p, 12);
            f.SpeedReported = true;
            f.Rpm = F32(p, 16);
            f.Handbrake = (BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(44)) & 4u) != 0 ? 1f : 0f;
            f.Throttle = F32(p, 48);
            f.Brake = F32(p, 52);
            f.Clutch = F32(p, 56);
            return true;
        }

        // ─────────────────────────────────────────────
        //  Project CARS 2, Automobilista 2
        // ─────────────────────────────────────────────

        /// <summary>The last game state Project CARS 2 sent, -1 before any,
        /// and the rain density that came with it, from 0 to 1.</summary>
        internal sealed class ProjectCars2State
        {
            public int GameState = -1;
            public float Rain;
        }

        /// <summary>The packet sizes of Project CARS 2's UDP version 2
        /// (PCars2UdpNet PCars2UDPClient.cs MarshalPacket).</summary>
        private static readonly int[] ProjectCars2Sizes = { 559, 308, 1136, 1063, 24, 1040, 1164, 1452 };

        /// <summary>
        /// Project CARS 2's UDP version 2, which Automobilista 2 also sends
        /// (MacManley/project-cars-2-udp PBase.h, PacketTelemetryData.h,
        /// PacketGameStateData.h). A 12-byte header with the type at 10.
        /// Telemetry (0, 559 bytes): brake, throttle and clutch at 29 to 31,
        /// speed in m/s at 36, RPM and max RPM at 40 and 42, steering at 44,
        /// gear in the low nibble of 45 with 15 meaning reverse (tm-bt-led
        /// projectcars2.js 87-88), the terrain under each tire at 140 with
        /// the front left first (tm-bt-led SMS_UDP_Definitions.hpp
        /// sTerrain), handbrake at 370. Game State (4, 24 bytes): the low
        /// three bits of 14, 2 while playing, and the rain density at 17,
        /// 0 to 255 (PacketGameStateData.h sRainDensity).
        /// </summary>
        internal static TelemetryDecode TryProjectCars2(ReadOnlySpan<byte> p, ProjectCars2State state, TelemetryFrame f)
        {
            if (p.Length < 12 || Array.IndexOf(ProjectCars2Sizes, p.Length) < 0) return TelemetryDecode.No;
            int type = p[10];
            if (type > 8) return TelemetryDecode.No;
            if (type == 4 && p.Length == 24)
            {
                state.GameState = p[14] & 7;
                state.Rain = p[17] / 255f;
                return TelemetryDecode.Claimed;
            }
            if (type != 0 || p.Length != 559) return TelemetryDecode.Claimed;
            f.Source = TelemetrySource.ProjectCars2;
            f.Brake = p[29] / 255f;
            f.Throttle = p[30] / 255f;
            f.Clutch = p[31] / 255f;
            f.SpeedMs = F32(p, 36);
            f.SpeedReported = true;
            f.Rpm = U16(p, 40);
            f.MaxRpm = U16(p, 42);
            f.Steering = (sbyte)p[44] / 127f;
            int gear = p[45] & 15;
            f.Gear = gear == 15 ? -1 : gear;
            f.Handbrake = p[370] / 255f;
            f.Grip = (float)RoadGrip.FrontAxle(SurfaceGrips.Madness(p[140], state.Rain), SurfaceGrips.Madness(p[141], state.Rain));
            if (state.GameState >= 0)
            {
                f.DrivingReported = true;
                f.Driving = state.GameState == 2;
            }
            return TelemetryDecode.Frame;
        }

        // ─────────────────────────────────────────────
        //  Assetto Corsa, Assetto Corsa Competizione
        // ─────────────────────────────────────────────

        /// <summary>The bytes each page needs for the fields read below.</summary>
        internal const int KunosPhysicsBytes = 368;
        internal const int KunosStaticBytes = 416;
        /// <summary>What the reader copies of the graphics page: through
        /// Competizione's surface grip. The decoder needs the status alone,
        /// <see cref="KunosGraphicsLeastBytes"/>, and reads the grip from a
        /// page long enough to hold it.</summary>
        internal const int KunosGraphicsBytes = AssettoCorsaTelemetrySource.CompetizioneSurfaceGripOffset + 4;
        internal const int KunosGraphicsLeastBytes = 8;

        /// <summary>
        /// The three shared memory pages both games write (tm-bt-led
        /// AssettoCorsaSharedMemory/SharedFileOut.h, 4-byte packing). Physics:
        /// gas at 4, brake 8, gear 16 (0 reverse, 1 neutral, 2 first), RPM
        /// 20, steerAngle 24, speedKmh 28, clutch 364, which reads 1 at rest
        /// (vAzhure GameListener.cs 221 inverts it). Static: smVersion at 0,
        /// 1.8 and later for ACC, maxRpm at 412. Graphics: status at 4, 2
        /// while live, and surfaceGrip at 280 in Assetto Corsa and 1240 in
        /// Competizione, whose page holds sixty cars ahead of it
        /// (AssettoCorsaTelemetrySource).
        /// </summary>
        internal static bool TryKunos(ReadOnlySpan<byte> physics, ReadOnlySpan<byte> statics,
            ReadOnlySpan<byte> graphics, TelemetryFrame f)
        {
            if (physics.Length < KunosPhysicsBytes || statics.Length < KunosStaticBytes
                || graphics.Length < KunosGraphicsLeastBytes) return false;
            bool competizione = IsCompetizione(statics);
            f.Source = competizione ? TelemetrySource.AssettoCorsaCompetizione : TelemetrySource.AssettoCorsa;
            int gripAt = competizione
                ? AssettoCorsaTelemetrySource.CompetizioneSurfaceGripOffset
                : AssettoCorsaTelemetrySource.SurfaceGripOffset;
            f.Grip = graphics.Length >= gripAt + 4 ? (float)SurfaceGrips.AssettoCorsa(F32(graphics, gripAt)) : 0f;
            f.Throttle = F32(physics, 4);
            f.Brake = F32(physics, 8);
            int gear = I32(physics, 16);
            f.Gear = gear <= 0 ? -1 : gear - 1;
            f.Rpm = I32(physics, 20);
            f.Steering = F32(physics, 24);
            f.SpeedMs = F32(physics, 28) / 3.6f;
            f.SpeedReported = true;
            f.Clutch = 1f - F32(physics, 364);
            f.MaxRpm = I32(statics, 412);
            f.DrivingReported = true;
            f.Driving = I32(graphics, 4) == 2;
            return true;
        }

        /// <summary>ACC's pages report smVersion 1.8 or later (tm-bt-led
        /// assetto.js 89). The field is a UTF-16 string of up to 15 units.</summary>
        internal static bool IsCompetizione(ReadOnlySpan<byte> statics)
        {
            Span<char> text = stackalloc char[15];
            int n = 0;
            for (; n < 15; n++)
            {
                char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(statics.Slice(n * 2));
                if (c == '\0') break;
                text[n] = c;
            }
            return double.TryParse(text.Slice(0, n), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                && v >= 1.8;
        }

        // ─────────────────────────────────────────────
        //  Euro Truck Simulator 2, American Truck Simulator
        // ─────────────────────────────────────────────

        /// <summary>
        /// MrHokss's ets2-telemetry-udp plugin (src/telemetry.cpp,
        /// udp_send_json): one JSON object per changed channel,
        /// <c>{"speed","rpm","gear","dgear","steer","throttle","brake","clutch","cruise"}</c>.
        /// The SCS SDK's channels (scssdk_telemetry_truck_common_channels.h):
        /// speed in m/s and negative in reverse, gear negative in reverse,
        /// steering counterclockwise, so a right turn is negative and the
        /// sign is flipped here to match every other format.
        /// </summary>
        internal static bool TryTruckSimulator(ReadOnlySpan<byte> p, TelemetryFrame f)
        {
            if (!StartsWithObject(p)) return false;
            bool speed = false, rpm = false, gear = false;
            try
            {
                var reader = new Utf8JsonReader(p);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject) break;
                    if (reader.TokenType != JsonTokenType.PropertyName) return false;
                    bool isSpeed = reader.ValueTextEquals("speed"u8);
                    bool isRpm = !isSpeed && reader.ValueTextEquals("rpm"u8);
                    bool isGear = !isSpeed && !isRpm && reader.ValueTextEquals("gear"u8);
                    bool isSteer = reader.ValueTextEquals("steer"u8);
                    bool isThrottle = reader.ValueTextEquals("throttle"u8);
                    bool isBrake = reader.ValueTextEquals("brake"u8);
                    bool isClutch = reader.ValueTextEquals("clutch"u8);
                    if (!reader.Read()) return false;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                        continue;
                    }
                    if (reader.TokenType != JsonTokenType.Number) continue;
                    double v = reader.GetDouble();
                    if (isSpeed) { f.SpeedMs = (float)Math.Abs(v); f.SpeedReported = true; speed = true; }
                    else if (isRpm) { f.Rpm = (float)v; rpm = true; }
                    else if (isGear) { f.Gear = v < 0 ? -1 : (int)Math.Round(v); gear = true; }
                    else if (isSteer) f.Steering = (float)-v;
                    else if (isThrottle) f.Throttle = (float)v;
                    else if (isBrake) f.Brake = (float)v;
                    else if (isClutch) f.Clutch = (float)v;
                }
            }
            catch (JsonException)
            {
                return false;
            }
            if (!speed || !rpm || !gear) return false;
            f.Source = TelemetrySource.TruckSimulator;
            return true;
        }

        /// <summary>True when the first byte that is not white space opens a
        /// JSON object.</summary>
        internal static bool StartsWithObject(ReadOnlySpan<byte> p)
        {
            foreach (byte b in p)
            {
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
                return b == (byte)'{';
            }
            return false;
        }

        // ─────────────────────────────────────────────
        //  Reads
        // ─────────────────────────────────────────────

        internal static float F32(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p.Slice(at));
        internal static int I32(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadInt32LittleEndian(p.Slice(at));
        internal static int U16(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(at));
    }
}
