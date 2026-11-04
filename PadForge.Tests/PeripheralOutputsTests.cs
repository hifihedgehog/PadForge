using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Resources.Strings;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The peripheral facade's levels, claims and links are
    /// process-wide (#494), and the engine's silence edges zero every level,
    /// so a test that sets one runs with nothing else running.</summary>
    [CollectionDefinition("PeripheralOutputStatics", DisableParallelization = true)]
    public sealed class PeripheralOutputStaticsCollection { }

    // ─────────────────────────────────────────────
    //  Linking: pure rules, no shared state
    // ─────────────────────────────────────────────

    /// <summary>How rows reach output paths, and which claim rules a
    /// shared path (#494).</summary>
    public class PeripheralLinkingTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
        private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
        private static readonly Guid Container = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherContainer = new("99999999-2222-3333-4444-555555555555");

        private static PeripheralOutputs.Claimant C(Guid device, int player, int slot = 0, bool catchAll = false)
            => new(device, catchAll, player, slot, 0);

        internal static HidppUnit Unit(byte index, int type, Guid container, bool haptics = true, ushort rgb = 0)
            => new()
            {
                ChannelPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#fake",
                ContainerId = container,
                DeviceIndex = index,
                DeviceType = type,
                HapticIndex = haptics ? (byte)0x0B : (byte)0,
                WaveformMask = haptics ? 0x7FFFu : 0u,
                RgbFeatureId = rgb,
            };

        private static LinkRow Row(Guid device, int capType, ushort vid, ushort pid = 0, Guid container = default)
            => new(device, capType, vid, pid, container, null);

        [Fact]
        public void ADeviceOnItsOwnBeatsAVendorRow_ThenTheSmallerPlayer_ThenTheSlot_ThenTheId()
        {
            Assert.True(PeripheralOutputs.Rules(C(A, 4), C(B, 1, catchAll: true)));
            Assert.False(PeripheralOutputs.Rules(C(B, 1, catchAll: true), C(A, 4)));

            Assert.True(PeripheralOutputs.Rules(C(B, 1), C(A, 2)));
            Assert.False(PeripheralOutputs.Rules(C(A, 2), C(B, 1)));

            Assert.True(PeripheralOutputs.Rules(C(B, 3, slot: 1), C(A, 3, slot: 3)));
            Assert.False(PeripheralOutputs.Rules(C(A, 3, slot: 3), C(B, 3, slot: 1)));

            // Two devices on one slot: the id decides, the same way both times.
            Assert.True(PeripheralOutputs.Rules(C(A, 3, slot: 2), C(B, 3, slot: 2)));
            Assert.False(PeripheralOutputs.Rules(C(B, 3, slot: 2), C(A, 3, slot: 2)));
        }

        [Fact]
        public void AClaimWithNoPlayerNumberNeverBeatsANumberedOne()
        {
            Assert.True(PeripheralOutputs.Rules(C(B, 7), C(A, 0)));
            Assert.False(PeripheralOutputs.Rules(C(A, 0), C(B, 7)));
            Assert.False(PeripheralOutputs.Rules(C(A, -1), C(B, 7)));
        }

        /// <summary>The five unit families reach one device each, the five
        /// others a device class their vendor's software resolves, and
        /// every member is sorted: one appended later is refused until it
        /// is.</summary>
        [Fact]
        public void AUnitPathReachesASingleDevice_AndEveryOtherFamilyIsShared()
        {
            var direct = new[]
            {
                OutputFamily.HidppUnit, OutputFamily.RazerUnit, OutputFamily.SteelSeriesUnit,
                OutputFamily.LampArrayUnit, OutputFamily.TouchSenseUnit, OutputFamily.HidLightingUnit,
            };
            var shared = new[]
            {
                OutputFamily.ChromaCategory, OutputFamily.LedSdkType, OutputFamily.GameSenseColor,
                OutputFamily.GameSenseTactile, OutputFamily.Interhaptics,
            };
            Assert.All(direct, family => Assert.False(new OutputPath(family, "k").Shared));
            Assert.All(shared, family => Assert.True(new OutputPath(family, "k").Shared));
            Assert.Equal(Enum.GetValues(typeof(OutputFamily)).Cast<OutputFamily>().OrderBy(f => f),
                direct.Concat(shared).OrderBy(f => f));
            Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new OutputPath((OutputFamily)11, "k").Shared; });
        }

        [Fact]
        public void TheTableIndexesEachPathOnce_AndComparesWholeRows()
        {
            var unit = new OutputPath(OutputFamily.HidppUnit, "x|01");
            var tactile = PeripheralLinker.GameSenseTactilePath;
            var rowA = new DeviceLinks { Device = A, Haptics = new[] { unit, tactile } };
            var rowB = new DeviceLinks { Device = B, Haptics = new[] { tactile }, CatchAll = true };
            var table = new LinkTable(new[]
            {
                rowA, rowB,
                new DeviceLinks { Device = Guid.Empty, Haptics = new[] { unit } },
                null,
            });

            Assert.Equal(2, table.ByDevice.Count);
            Assert.Equal(new[] { A }, table.ByPath[unit]);
            Assert.Equal(new[] { A, B }, table.ByPath[tactile]);
            Assert.Equal(PeripheralOutputKinds.Haptics, table.For(A).Kinds);
            Assert.Equal(PeripheralOutputKinds.Haptics | PeripheralOutputKinds.Lighting,
                new DeviceLinks { Device = A, Haptics = new[] { unit }, Lighting = new[] { unit } }.Kinds);
            Assert.Null(table.For(Guid.NewGuid()));

            Assert.True(table.SameAs(new LinkTable(new[] { rowA, rowB })));
            Assert.False(table.SameAs(new LinkTable(new[] { rowA })));
            Assert.False(table.SameAs(new LinkTable(new[]
            {
                rowA, new DeviceLinks { Device = B, Haptics = new[] { tactile } },
            })));
            Assert.False(table.SameAs(new LinkTable(new[]
            {
                rowA, new DeviceLinks { Device = B, Haptics = new[] { unit }, CatchAll = true },
            })));
            Assert.False(table.SameAs(null));
            Assert.True(LinkTable.Empty.SameAs(new LinkTable(Array.Empty<DeviceLinks>())));
        }

        [Fact]
        public void AUnitGoesToTheRowOfItsKindInItsContainer()
        {
            var mouseRow = Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, container: Container);
            var keyboardRow = Row(B, InputDeviceType.Keyboard, PeripheralLinker.LogitechVid, container: Container);

            foreach (int mouseKind in new[] { 3, 4, 5 })
            {
                Assert.True(PeripheralLinker.Matches(Unit(1, mouseKind, Container), mouseRow));
                Assert.False(PeripheralLinker.Matches(Unit(1, mouseKind, Container), keyboardRow));
            }
            foreach (int keyboardKind in new[] { 0, 2 })
            {
                Assert.True(PeripheralLinker.Matches(Unit(1, keyboardKind, Container), keyboardRow));
                Assert.False(PeripheralLinker.Matches(Unit(1, keyboardKind, Container), mouseRow));
            }

            // A unit that gave no type goes to both rows of its container.
            Assert.True(PeripheralLinker.Matches(Unit(1, -1, Container), mouseRow));
            Assert.True(PeripheralLinker.Matches(Unit(1, -1, Container), keyboardRow));

            // A remote (1) or a presenter (6) is neither kind.
            Assert.False(PeripheralLinker.Matches(Unit(1, 1, Container), mouseRow));
            Assert.False(PeripheralLinker.Matches(Unit(1, 6, Container), keyboardRow));

            // Another container, or a row whose container is unknown, never.
            Assert.False(PeripheralLinker.Matches(Unit(1, 3, OtherContainer), mouseRow));
            Assert.False(PeripheralLinker.Matches(Unit(1, -1, Guid.Empty),
                Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid)));
        }

        [Fact]
        public void ALogitechRowTakesTheHapticUnitsItMatches()
        {
            var units = new[]
            {
                Unit(1, 3, Container),
                Unit(2, 0, Container, haptics: false, rgb: HidppUnitProtocol.ColorLedEffects),
                Unit(3, 3, OtherContainer),
            };
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, container: Container),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.LogitechVid, container: Container),
            };
            var table = PeripheralLinker.Build(rows, new HidppSnapshot(units, Array.Empty<Guid>()), PeripheralPresence.None);

            var mouse = table.For(A);
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, units[0].Key) }, mouse.Haptics);
            Assert.False(mouse.CatchAll);
            // The keyboard's unit has no haptics, and a zone-less 0x8070
            // device cannot be lit directly.
            Assert.Null(table.For(B));
        }

        [Fact]
        public void ARivalTakesTheTactilePath_OnlyWithGGAndOnlyByProductId()
        {
            var gg = PeripheralPresence.None with { SteelSeriesGG = true };
            foreach (ushort pid in PeripheralLinker.RivalTactilePids)
            {
                var table = PeripheralLinker.Build(
                    new[] { Row(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, pid) }, HidppSnapshot.Empty, gg);
                Assert.Equal(new[] { PeripheralLinker.GameSenseTactilePath }, table.For(A).Haptics);
            }

            Assert.Null(PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x170E) },
                HidppSnapshot.Empty, PeripheralPresence.None).For(A));
            // Any other SteelSeries mouse, and a keyboard, take their GameSense
            // color type (#494) and no rumble.
            var other = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x1824) },
                HidppSnapshot.Empty, gg).For(A);
            Assert.Empty(other.Haptics);
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") }, other.Lighting);
            var keyboard = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Keyboard, PeripheralLinker.SteelSeriesVid, 0x1612) },
                HidppSnapshot.Empty, gg).For(A);
            Assert.Empty(keyboard.Haptics);
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "keyboard") }, keyboard.Lighting);
            // A Rival's keyboard collection is the Rival: its color type and
            // its rumble.
            var rivalKeys = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Keyboard, PeripheralLinker.SteelSeriesVid, 0x170E) },
                HidppSnapshot.Empty, gg).For(A);
            Assert.Equal(new[] { PeripheralLinker.GameSenseTactilePath }, rivalKeys.Haptics);
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") }, rivalKeys.Lighting);
        }

        [Fact]
        public void TheSensaRowTakesInterhapticsOnlyWhereTheEngineRuns()
        {
            var sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            var row = new LinkRow(sensa, InputDeviceType.PeripheralHaptics, 0x5046, 0x5345, Guid.Empty, PeripheralRowKind.RazerSensa);

            var on = PeripheralLinker.Build(new[] { row }, HidppSnapshot.Empty, PeripheralPresence.None with { SensaPlatform = true });
            Assert.Equal(new[] { PeripheralLinker.SensaPath }, on.For(sensa).Haptics);
            Assert.True(on.For(sensa).CatchAll);

            Assert.Null(PeripheralLinker.Build(new[] { row }, HidppSnapshot.Empty, PeripheralPresence.None).For(sensa));
        }

        [Fact]
        public void ARowKeepsItsOutputsOnlyWhileItsDeviceIsStillBeingLookedFor()
        {
            var links = new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } };
            Assert.Equal(1, PeripheralLinker.Capabilities(0, links, stillLooking: false));
            Assert.Equal(1, PeripheralLinker.Capabilities(3, links, stillLooking: false));
            Assert.Equal(0, PeripheralLinker.Capabilities(3, null, stillLooking: false));
            Assert.Equal(3, PeripheralLinker.Capabilities(3, null, stillLooking: true));
            Assert.Equal(3, PeripheralLinker.Capabilities(2, links, stillLooking: true));
        }

        [Fact]
        public void TheVendorRowsKeepTheirIdentities_AndTheirTypeFollowsTheirOutput()
        {
            Assert.Equal(0, (int)PeripheralRowKind.RazerChroma);
            Assert.Equal(1, (int)PeripheralRowKind.LogitechLightsync);
            Assert.Equal(2, (int)PeripheralRowKind.RazerSensa);
            Assert.Equal(3, (int)PeripheralRowKind.SteelSeriesGG);
            Assert.Equal(4, Enum.GetValues(typeof(PeripheralRowKind)).Length);

            using var md5 = MD5.Create();
            Guid Hash(string s) => new(md5.ComputeHash(Encoding.UTF8.GetBytes(s)));

            foreach (var (kind, tag, type, name, path, product) in new[]
            {
                (PeripheralRowKind.RazerChroma, "pfrazerchroma", InputDeviceType.PeripheralLighting,
                    "Razer Chroma", "razerchroma://local", (ushort)0x4348),
                (PeripheralRowKind.LogitechLightsync, "pflogilightsync", InputDeviceType.PeripheralLighting,
                    "Logitech LIGHTSYNC", "logilightsync://local", (ushort)0x4C53),
                (PeripheralRowKind.RazerSensa, "pfrazersensa", InputDeviceType.PeripheralHaptics,
                    "Razer Sensa", "razersensa://local", (ushort)0x5345),
                (PeripheralRowKind.SteelSeriesGG, "pfsteelseriesgg", InputDeviceType.PeripheralLighting,
                    "SteelSeries GG", "steelseriesgg://local", (ushort)0x5347),
            })
            {
                var row = new PeripheralOutputRow(kind);
                Assert.Equal(Hash(tag), PeripheralOutputRow.IdentityFor(kind));
                Assert.Equal(Hash(tag), row.InstanceGuid);
                Assert.Equal(Hash(tag + "-product"), row.ProductGuid);
                Assert.Equal(type, row.GetInputDeviceType());
                Assert.Equal(type == InputDeviceType.PeripheralLighting, PeripheralOutputRow.IsLightingRow(kind));
                Assert.Equal(name, PeripheralOutputRow.NameFor(kind));
                Assert.Equal(name, row.Name);
                Assert.Equal(path, row.DevicePath);
                Assert.Equal(product, row.ProductId);
                Assert.Equal(kind, PeripheralOutputRow.KindOf(row.InstanceGuid));
                Assert.Equal(0x5046, row.VendorId);
                Assert.Equal(0, row.NumAxes + row.NumButtons + row.NumHats);
                Assert.False(row.HasRumble || row.HasHaptic);
                Assert.NotNull(row.GetCurrentState());
                row.Dispose();
                Assert.False(row.IsAttached);
                Assert.Null(row.GetCurrentState());
            }
        }

        /// <summary>A kind appended to the enum without its own arms is
        /// refused everywhere a row is described, rather than answered with
        /// the Razer Sensa row's path, name and id, which a new row would then
        /// overwrite in the device list and in every saved assignment.</summary>
        [Fact]
        public void AnUnlistedRowKindIsRefused()
        {
            var unlisted = (PeripheralRowKind)Enum.GetValues(typeof(PeripheralRowKind)).Length;
            Assert.Throws<ArgumentOutOfRangeException>(() => PeripheralOutputRow.IdentityFor(unlisted));
            Assert.Throws<ArgumentOutOfRangeException>(() => PeripheralOutputRow.NameFor(unlisted));
            Assert.Throws<ArgumentOutOfRangeException>(() => PeripheralOutputRow.IsLightingRow(unlisted));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PeripheralOutputRow(unlisted));
            Assert.Null(PeripheralOutputRow.KindOf(Guid.NewGuid()));
        }

        [Fact]
        public void TheSensaRowOpensOnlyWithSynapseAndTheEngine()
        {
            var both = new PeripheralPresence(false, false, true, false, true);
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, both));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, both with { RazerSynapse = false }));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, both with { SensaPlatform = false }));
        }

        [Fact]
        public void TheOutputRowsAnswerNoAnyDeviceSource_AndKeepTheirNumbers()
        {
            Assert.Equal(40, InputDeviceType.PeripheralLighting);
            Assert.Equal(41, InputDeviceType.PeripheralHaptics);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.PeripheralLighting));
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.PeripheralHaptics));
        }

        /// <summary>Haptics, lighting and the two lighting holds a device
        /// offers above colors alone, each its own bit, and all four come
        /// back from the file.</summary>
        [Fact]
        public void TheRecordedOutputsSurviveTheFile_AsFourBits()
        {
            var ud = new UserDevice { PeripheralOutputs = 15 };
            Assert.True(ud.HasPeripheralHaptics);
            Assert.True(ud.HasPeripheralLighting);
            Assert.True(ud.HasPeripheralLightingLevel1);
            Assert.True(ud.HasPeripheralLightingLevel2);
            Assert.False(new UserDevice { PeripheralOutputs = 2 }.HasPeripheralHaptics);
            Assert.False(new UserDevice { PeripheralOutputs = 1 }.HasPeripheralLighting);
            Assert.False(new UserDevice { PeripheralOutputs = 3 }.HasPeripheralLightingLevel1);
            Assert.False(new UserDevice { PeripheralOutputs = 7 }.HasPeripheralLightingLevel2);
            Assert.Equal(15, (int)(PeripheralOutputKinds.Haptics | PeripheralOutputKinds.Lighting
                                   | PeripheralOutputKinds.LightingLevel1 | PeripheralOutputKinds.LightingLevel2));

            var serializer = new XmlSerializer(typeof(UserDevice));
            using var writer = new StringWriter();
            serializer.Serialize(writer, ud);
            Assert.Contains("<PeripheralOutputs>15</PeripheralOutputs>", writer.ToString());
            var back = (UserDevice)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal(15, back.PeripheralOutputs);
            Assert.True(back.HasPeripheralLightingLevel1 && back.HasPeripheralLightingLevel2);
        }
    }

    // ─────────────────────────────────────────────
    //  HID++ units: scripted channels, no shared state
    // ─────────────────────────────────────────────

    /// <summary>What the probe finds on a HID++ 2.0 device beyond haptics
    /// (#494), against scripted answers shaped the way Solaar's decoders
    /// read them.</summary>
    public class HidppUnitProbeTests
    {
        /// <summary>A scripted device: its features at fixed indexes, its
        /// 0x8070 zones as lists of effect IDs, its battery, and any request
        /// that goes unanswered.</summary>
        private sealed class Scripted
        {
            public byte Index = HidppHapticProtocol.DirectDeviceIndex;
            public string Name = "G502 X";
            public byte Type = 3;
            public readonly Dictionary<ushort, byte> Features = new() { [0x0005] = 0x03 };
            public ushort[][] Zones = Array.Empty<ushort[]>();

            /// <summary>Its 0x8071 clusters, as lists of effect IDs.</summary>
            public ushort[][] Clusters = Array.Empty<ushort[]>();
            public byte BatteryPercent, BatteryLevel;
            public Func<byte, byte, byte[], bool> Silent = (f, fn, p) => false;

            public HidppReply Answer(byte d, byte f, byte fn, byte[] p)
            {
                if (d != Index || Silent(f, fn, p)) return HidppHapticsTests.NoAnswer();
                if (f == 0 && fn == 0)
                {
                    ushort id = (ushort)((p[0] << 8) | p[1]);
                    return HidppHapticsTests.Ans(d, 0, 0, Features.TryGetValue(id, out byte at) ? at : (byte)0);
                }
                if (Features.TryGetValue(0x0005, out byte nameAt) && f == nameAt)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Name);
                    return fn switch
                    {
                        0 => HidppHapticsTests.Ans(d, f, 0, (byte)bytes.Length),
                        1 => HidppHapticsTests.Ans(d, f, 1, bytes.Skip(p[0]).Take(16).ToArray()),
                        2 => HidppHapticsTests.Ans(d, f, 2, Type),
                        _ => HidppHapticsTests.NoAnswer(),
                    };
                }
                if (Features.TryGetValue(0x8070, out byte rgbAt) && f == rgbAt)
                {
                    switch (fn)
                    {
                        case 0:
                            return HidppHapticsTests.Ans(d, f, 0, (byte)Zones.Length);
                        case 1:
                            // GetZoneInfo [z, FF, 00]: location in 1-2, the
                            // effect count in 3 (hidpp20.py:1356-1357).
                            return HidppHapticsTests.Ans(d, f, 1, p[0], 0, 1, (byte)Zones[p[0]].Length);
                        case 2:
                            // GetZoneEffectInfo [z, e]: the ID big-endian in 2-3
                            // (hidpp20.py:1332-1333).
                            ushort effect = Zones[p[0]][p[1]];
                            return HidppHapticsTests.Ans(d, f, 2, p[0], p[1], (byte)(effect >> 8), (byte)effect);
                    }
                }
                if (Features.TryGetValue(0x8071, out byte clustersAt) && f == clustersAt && fn == 0)
                {
                    // getInfo: [FF FF] the device with its cluster count in
                    // 2, [c FF] a cluster with its location in 2-3 and its
                    // effect count in 4, [c e] an effect with its ID in 2-3
                    // (Logitech's x8071 document, p.4-7).
                    if (p[0] == 0xFF) return HidppHapticsTests.Ans(d, f, 0, 0xFF, 0xFF, (byte)Clusters.Length);
                    if (p[1] == 0xFF) return HidppHapticsTests.Ans(d, f, 0, p[0], 0xFF, 0x00, 0x01, (byte)Clusters[p[0]].Length);
                    ushort fixedOrNot = Clusters[p[0]][p[1]];
                    return HidppHapticsTests.Ans(d, f, 0, p[0], p[1], (byte)(fixedOrNot >> 8), (byte)fixedOrNot);
                }
                if (Features.TryGetValue(0x1004, out byte unifiedAt) && f == unifiedAt && fn == 1)
                    return HidppHapticsTests.Ans(d, f, 1, BatteryPercent, BatteryLevel);
                if (Features.TryGetValue(0x1000, out byte statusAt) && f == statusAt && fn == 0)
                    return HidppHapticsTests.Ans(d, f, 0, BatteryPercent);
                return HidppHapticsTests.NoAnswer();
            }

            public HidppHapticsTests.FakeChannel Channel() => new() { Answer = Answer };
        }

        private static HidppUnit Describe(Scripted device, out bool incomplete)
            => HidppUnitProbe.Describe(device.Channel(), device.Index, device.Features[0x0005], Guid.Empty, 300, out incomplete);

        [Fact]
        public void AZoneDeviceListsEachZoneWithItsStaticEffect_FoundByScanning()
        {
            var device = new Scripted { Name = "G203", Type = 3 };
            device.Features[0x8070] = 0x0E;
            device.Zones = new[]
            {
                new ushort[] { 0x0000, 0x0001, 0x0003 },   // static second
                new ushort[] { 0x0001 },                   // static first
                new ushort[] { 0x0000, 0x000A },           // no static: not listed
            };

            var unit = Describe(device, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal("G203", unit.Name);
            Assert.Equal(3, unit.DeviceType);
            Assert.Equal(HidppUnitProtocol.ColorLedEffects, unit.RgbFeatureId);
            Assert.Equal(0x0E, unit.RgbIndex);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 0) }, unit.Zones);
            Assert.True(unit.DirectRgb);
            Assert.True(unit.HasRgb);
            Assert.False(unit.HasHaptics);
            Assert.True(unit.IsMouseKind);
        }

        /// <summary>A device with 0x8071 is painted through it, whatever
        /// else it lists: the first pass stops there, its 0x8070 is never
        /// looked up, and the walk after the batteries lists its clusters,
        /// each with the Fixed effect found by its ID. It is not the
        /// zone-only class, and with no mode feature it offers its lighting
        /// power and nothing above it.</summary>
        [Fact]
        public void A0x8071DevicePrefersIt_AndListsItsClusters()
        {
            var device = new Scripted { Name = "G915", Type = 0 };
            device.Features[0x8071] = 0x0C;
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            device.Clusters = new[] { new ushort[] { 0x0000, 0x0001 }, new ushort[] { 0x0001 } };
            var channel = device.Channel();

            var unit = HidppUnitProbe.Describe(channel, device.Index, device.Features[0x0005], Guid.Empty, 300, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(HidppUnitProtocol.RgbEffects, unit.RgbFeatureId);
            Assert.Equal(0x0C, unit.RgbIndex);
            Assert.Equal((HidppUnitProtocol.RgbEffects, (byte)0x0C), (unit.EffectFeatureId, unit.EffectIndex));
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 0) }, unit.Zones);
            Assert.False(unit.DirectRgb);
            Assert.True(unit.HasRgb);
            Assert.True(unit.HasLightPlan);
            Assert.True(unit.IsKeyboardKind);
            Assert.Equal(1, unit.MaxClaimLevel);
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x1004, 0x1000, 0x8081, 0x8101, 0x8100 }, channel.RootLookups);
        }

        [Fact]
        public void TheUnifiedBatteryComesFirst()
        {
            var device = new Scripted();
            device.Features[0x1000] = 0x06;
            device.Features[0x1004] = 0x08;
            var unit = Describe(device, out _);
            Assert.Equal(HidppUnitProtocol.UnifiedBattery, unit.BatteryFeatureId);
            Assert.Equal(0x08, unit.BatteryIndex);

            device.Features.Remove(0x1004);
            unit = Describe(device, out _);
            Assert.Equal(HidppUnitProtocol.BatteryStatus, unit.BatteryFeatureId);
        }

        [Fact]
        public void AZoneThatGoesUnansweredLeavesTheUnitIncomplete()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 }, new ushort[] { 0x0001 } };
            device.Silent = (f, fn, p) => f == 0x0E && fn == 1 && p[0] == 1;

            var unit = Describe(device, out bool incomplete);

            // The walk stops at the silent zone and the unit comes back
            // without zones, for the caller to drop and ask again in full.
            Assert.True(incomplete);
            Assert.Empty(unit.Zones);
        }

        [Fact]
        public void AFeatureLookupThatGoesUnansweredLeavesTheUnitIncomplete()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Silent = (f, fn, p) => f == 0 && fn == 0 && p[0] == 0x10 && p[1] == 0x04;

            Describe(device, out bool incomplete);
            Assert.True(incomplete);
        }

        /// <summary>The name is an extra a missing answer only leaves
        /// unknown. The type decides which rows a receiver's unit lights and
        /// rumbles (#494), so a missing type answer makes the unit incomplete
        /// and it is asked again in full.</summary>
        [Fact]
        public void TheNameIsAnExtra_AMissingTypeIsAskedAgain()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            device.Silent = (f, fn, p) => f == 0x03 && fn != 2;

            var unit = Describe(device, out bool incomplete);
            Assert.False(incomplete);
            Assert.Null(unit.Name);
            Assert.Equal(3, unit.DeviceType);
            Assert.True(unit.DirectRgb);

            device.Silent = (f, fn, p) => f == 0x03;
            unit = Describe(device, out incomplete);
            Assert.True(incomplete);
            Assert.Equal(-1, unit.DeviceType);
            Assert.False(unit.DirectRgb);
        }

        [Fact]
        public void TheProbeKeepsALitDevice_AndSettlesOneWithNothingToOffer()
        {
            var lit = new Scripted();
            lit.Features[0x8070] = 0x0E;
            lit.Zones = new[] { new ushort[] { 0x0001 } };
            var state = new HidppPathState();
            var found = HidppUnitProbe.Probe(lit.Channel(), state, Guid.Empty, 0);
            Assert.Single(found);
            Assert.True(state.Direct);
            Assert.True(state.Settled[0]);

            var bare = new Scripted();
            state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(bare.Channel(), state, Guid.Empty, 0));
            Assert.True(state.Settled[0]);
            Assert.False(HidppUnitProbe.Pending(state, bluetooth: false));
        }

        [Fact]
        public void AnIncompleteUnitBacksOff_AndIsAskedAgainInFull()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            bool asleep = true;
            device.Silent = (f, fn, p) => asleep && f == 0x0E;
            var channel = device.Channel();
            var state = new HidppPathState();

            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, 0));
            Assert.False(state.Settled[0]);
            Assert.Equal(HidppHapticProbe.FirstRetryMs, state.RetryAt[0]);
            Assert.True(HidppUnitProbe.Pending(state, bluetooth: false));

            // Inside the backoff nothing is asked.
            int asked = channel.Asked.Count;
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, 1000));
            Assert.Equal(asked, channel.Asked.Count);

            asleep = false;
            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, HidppHapticProbe.FirstRetryMs);
            Assert.Single(found);
            Assert.True(found[0].DirectRgb);
            Assert.True(state.Settled[0]);
        }

        [Fact]
        public void TheStaticColorFrameIsOpenRgbs_WithBlackPassedThrough()
        {
            byte[] frame = HidppUnitProtocol.SetStaticColor(0x01, 0x0E, new HidppZone(1, 3), 0x10, 0x20, 0x30);
            var expected = new byte[20];
            new byte[] { 0x11, 0x01, 0x0E, 0x3C, 0x01, 0x03, 0x10, 0x20, 0x30, 0x02 }.CopyTo(expected, 0);
            Assert.Equal(expected, frame);

            frame = HidppUnitProtocol.SetStaticColor(0x01, 0x0E, new HidppZone(0, 1), 0, 0, 0);
            Assert.Equal(0x00, frame[9]);
            Assert.All(frame.Skip(10), b => Assert.Equal(0, b));
        }

        [Fact]
        public void SoftwareControlIsClaimedWithOneOneAndHandedBackWithZeroZero()
        {
            var expected = new byte[20];
            new byte[] { 0x11, 0xFF, 0x0E, 0x8C, 0x01, 0x01 }.CopyTo(expected, 0);
            Assert.Equal(expected, HidppUnitProtocol.SetSwControl(0xFF, 0x0E, claim: true));

            expected[4] = 0;
            expected[5] = 0;
            Assert.Equal(expected, HidppUnitProtocol.SetSwControl(0xFF, 0x0E, claim: false));
        }

        [Theory]
        [InlineData(0x1004, 57, 0, 57)]
        [InlineData(0x1004, 150, 0, 100)]
        [InlineData(0x1004, 0, 8, 90)]
        [InlineData(0x1004, 0, 4, 50)]
        [InlineData(0x1004, 0, 2, 20)]
        [InlineData(0x1004, 0, 1, 5)]
        [InlineData(0x1004, 0, 0, 0)]
        [InlineData(0x1004, 0, 12, 0)]     // Solaar compares the level, never its bits
        [InlineData(0x1000, 80, 0, 80)]
        [InlineData(0x1000, 0, 8, -1)]     // 0x1000 has no level: zero is unknown
        public void TheBatteryReadsAsSolaarDecodesIt(int feature, int percent, int level, int expected)
        {
            var reply = HidppHapticsTests.Ans(0x01, 0x08, 1, (byte)percent, (byte)level);
            Assert.Equal(expected, HidppUnitProtocol.BatteryPercent((ushort)feature, reply));
        }

        [Fact]
        public void TheBatteryIsAskedWithEachFeaturesOwnFunction()
        {
            Assert.Equal(1, HidppUnitProtocol.BatteryFunction(HidppUnitProtocol.UnifiedBattery));
            Assert.Equal(0, HidppUnitProtocol.BatteryFunction(HidppUnitProtocol.BatteryStatus));

            var device = new Scripted { BatteryPercent = 0, BatteryLevel = 4 };
            device.Features[0x1004] = 0x08;
            var unit = new HidppUnit { DeviceIndex = device.Index, BatteryFeatureId = 0x1004, BatteryIndex = 0x08 };
            Assert.Equal(50, HidppUnitProbe.ReadBattery(device.Channel(), unit));
            Assert.Equal(-1, HidppUnitProbe.ReadBattery(device.Channel(), unit with { BatteryFeatureId = 0 }));
            Assert.Equal(-1, HidppUnitProbe.ReadBattery(new HidppHapticsTests.FakeChannel(), unit));
        }

        private static HidppHapticsTests.FakeChannel BoltReceiver(Func<byte, HidppReply> record,
            Func<byte, byte, byte, byte[], HidppReply> devices = null)
            => new()
            {
                Answer = devices ?? ((d, f, fn, p) => HidppHapticsTests.NoAnswer()),
                Register = (r, s) => r == 0xB5 ? record((byte)(s - 0x50)) : HidppHapticsTests.NoAnswer(),
            };

        [Fact]
        public void TheReceiversTableSettlesEmptySlots_AndKeepsAnAsleepOnePending()
        {
            var channel = BoltReceiver(
                slot => slot <= 2 ? new HidppReply(HidppReplyKind.Answer) : new HidppReply(HidppReplyKind.Error, 0x03),
                HidppHapticsTests.Mouse(1));
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };

            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, 0);

            Assert.Single(found);
            Assert.True(state.Receiver);
            Assert.False(state.Direct);
            Assert.True(state.Settled[1]);
            Assert.False(state.Unpaired[2]);   // paired and asleep
            Assert.All(new[] { 3, 4, 5, 6 }, slot => Assert.True(state.Unpaired[slot]));
            Assert.True(HidppUnitProbe.Pending(state, bluetooth: false));
            Assert.True(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
            // 0xFF and the empty slots are never asked.
            Assert.All(channel.Asked, a => Assert.True(a.Device == 1 || a.Device == 2, $"asked 0x{a.Device:X2}"));

            // The device in slot 2 is unpaired: nothing waits any more, and
            // nothing holds a row's outputs.
            channel.Register = (r, s) => s == 0x51 ? new HidppReply(HidppReplyKind.Answer) : new HidppReply(HidppReplyKind.Error, 0x03);
            HidppUnitProbe.Probe(channel, state, Guid.Empty, HidppHapticProbe.MaxRetryMs);
            Assert.True(state.Unpaired[2]);
            Assert.False(HidppUnitProbe.Pending(state, bluetooth: false));
            Assert.False(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
        }

        [Fact]
        public void OneStrayErrorNeverSettlesASlot()
        {
            int reads = 0;
            var channel = BoltReceiver(slot => slot == 1 && reads++ == 0
                ? new HidppReply(HidppReplyKind.Error, 0x03)
                : new HidppReply(HidppReplyKind.Answer));
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };

            HidppUnitProbe.ReadPairing(channel, state, 300);

            Assert.False(state.Unpaired[1]);
        }

        [Fact]
        public void APairedSlotLaterIsAskedAgain()
        {
            bool paired = false;
            var channel = BoltReceiver(
                slot => slot == 3 && paired ? new HidppReply(HidppReplyKind.Answer) : new HidppReply(HidppReplyKind.Error, 0x03),
                HidppHapticsTests.Mouse(3));
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };

            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, 0));
            Assert.True(state.Unpaired[3]);

            paired = true;
            Assert.Single(HidppUnitProbe.Probe(channel, state, Guid.Empty, 0));
            Assert.False(state.Unpaired[3]);
        }

        [Fact]
        public void AReceiverThatNeverAnswersItsTableIsNotAskedForItAgain()
        {
            var channel = BoltReceiver(slot => HidppHapticsTests.NoAnswer());
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };
            for (int i = 0; i < HidppUnitProbe.PairingGiveUp + 2; i++)
                HidppUnitProbe.ReadPairing(channel, state, 300);

            // One unanswered read per pass, and none after the give-up.
            Assert.Equal(HidppUnitProbe.PairingGiveUp, channel.RegistersAsked.Count);
            Assert.All(Enumerable.Range(1, 6), slot => Assert.False(state.Unpaired[slot]));
        }

        /// <summary>A pairing read that never went out, on a channel without
        /// the receiver's short collection or one whose short write failed,
        /// counts toward nothing: the receiver was never asked, so the table
        /// is still read once it can be.</summary>
        [Fact]
        public void AReadThatNeverWentOutNeverGivesUpTheTable()
        {
            foreach (var never in new[] { HidppReplyKind.None, HidppReplyKind.WriteFailed })
            {
                bool reachable = false;
                var channel = BoltReceiver(slot => !reachable ? new HidppReply(never)
                    : slot == 1 ? new HidppReply(HidppReplyKind.Answer) : new HidppReply(HidppReplyKind.Error, 0x03));
                var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };
                for (int i = 0; i <= HidppUnitProbe.PairingGiveUp; i++)
                    HidppUnitProbe.ReadPairing(channel, state, 300);
                Assert.Equal(0, state.PairingMisses);

                reachable = true;
                HidppUnitProbe.ReadPairing(channel, state, 300);
                Assert.False(state.Unpaired[1]);
                Assert.All(new[] { 2, 3, 4, 5, 6 }, slot => Assert.True(state.Unpaired[slot], $"{never}: slot {slot}"));
            }

            // The channel answers None when it has no short collection to ask
            // through.
            string haptics = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "HidppHaptics.cs");
            int read = haptics.IndexOf("public HidppReply ReadReceiverRegister(", StringComparison.Ordinal);
            Assert.True(read >= 0, "ReadReceiverRegister moved");
            Assert.Contains("return new HidppReply(HidppReplyKind.None);", haptics.Substring(read, 600));
        }

        [Fact]
        public void TheNameIsReadThroughTheIndexAlreadyFound()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            var channel = device.Channel();

            var unit = HidppUnitProbe.Describe(channel, device.Index, 0x03, Guid.Empty, 300, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal("G502 X", unit.Name);
            // No second 0x0005 lookup, whose late answer would be taken for
            // the 0x19B0 lookup after it.
            Assert.DoesNotContain((ushort)0x0005, channel.RootLookups);
            Assert.Equal((ushort)0x19B0, channel.RootLookups.First());
        }

        [Fact]
        public void ABusyAnswerIsAMissToAskAgain_WhereverItComes()
        {
            // A Root lookup that stays busy.
            foreach (ushort feature in new ushort[] { 0x19B0, 0x8071, 0x1004 })
            {
                var device = new Scripted();
                device.Features[0x8070] = 0x0E;
                device.Zones = new[] { new ushort[] { 0x0001 } };
                device.Features[0x1004] = 0x08;
                Func<byte, byte, byte, byte[], HidppReply> inner = device.Answer;
                var channel = new HidppHapticsTests.FakeChannel
                {
                    Answer = (d, f, fn, p) => f == 0 && fn == 0 && ((p[0] << 8) | p[1]) == feature
                        ? new HidppReply(HidppReplyKind.Error, HidppHapticProtocol.ErrorBusy)
                        : inner(d, f, fn, p),
                };
                HidppUnitProbe.Describe(channel, device.Index, 0x03, Guid.Empty, 300, out bool incomplete);
                Assert.True(incomplete, $"busy 0x{feature:X4} settled the unit");
            }

            // The zone walk.
            var zoned = new Scripted();
            zoned.Features[0x8070] = 0x0E;
            zoned.Zones = new[] { new ushort[] { 0x0001 } };
            var zonedChannel = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => f == 0x0E && fn == 1
                    ? new HidppReply(HidppReplyKind.Error, HidppHapticProtocol.ErrorBusy)
                    : zoned.Answer(d, f, fn, p),
            };
            HidppUnitProbe.Describe(zonedChannel, zoned.Index, 0x03, Guid.Empty, 300, out bool zonesIncomplete);
            Assert.True(zonesIncomplete);

            // The slot's first lookup: backed off, never settled.
            var first = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => new HidppReply(HidppReplyKind.Error, HidppHapticProtocol.ErrorBusy),
            };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(first, state, Guid.Empty, 0));
            Assert.False(state.Settled[0]);
            Assert.True(state.RetryAt[0] > 0);

            // Any other refusal settles it: nothing there to offer.
            var refused = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => new HidppReply(HidppReplyKind.Error, 0x02),
            };
            state = new HidppPathState();
            HidppUnitProbe.Probe(refused, state, Guid.Empty, 0);
            Assert.True(state.Settled[0]);
        }

        [Fact]
        public void AGivenUpTableHandsItsSlotsBackToTheWalk()
        {
            bool answering = true;
            var channel = BoltReceiver(slot => !answering ? HidppHapticsTests.NoAnswer()
                : slot == 1 ? new HidppReply(HidppReplyKind.Answer) : new HidppReply(HidppReplyKind.Error, 0x03));
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };

            HidppUnitProbe.ReadPairing(channel, state, 300);
            Assert.False(state.Unpaired[1]);
            Assert.True(state.Unpaired[4]);

            answering = false;
            for (int i = 0; i < HidppUnitProbe.PairingGiveUp; i++)
                HidppUnitProbe.ReadPairing(channel, state, 300);

            Assert.All(Enumerable.Range(1, 6), slot => Assert.False(state.Unpaired[slot]));
            Assert.True(state.Open(4));
        }

        [Fact]
        public void TheFastScanFollowsWaitingSlots_AndARowsOutputsFollowOpenOnes()
        {
            var state = new HidppPathState { Receiver = true };
            for (int slot = 1; slot <= 6; slot++) state.Settled[slot] = slot != 3;

            // A slot that never answered keeps the scan fast through its
            // first walk only, and stays open: a row in its container keeps
            // what it has, since a mouse asleep since launch looks the same.
            Assert.True(state.Waiting(3));
            Assert.True(HidppUnitProbe.Pending(state, bluetooth: false));
            state.Misses[3] = HidppPathState.FirstWalkMisses;
            Assert.False(state.Waiting(3));
            Assert.False(HidppUnitProbe.Pending(state, bluetooth: false));
            Assert.True(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));

            // A slot that held a device before it slept keeps the scan fast.
            state.Held[3] = true;
            Assert.True(state.Waiting(3));

            // Reported empty: nothing waits and nothing holds the outputs.
            state.Unpaired[3] = true;
            Assert.False(state.Waiting(3));
            Assert.False(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
        }

        private static HidppReply Hidpp10Error(byte device, byte code)
            => new(HidppReplyKind.Error, code,
                new byte[] { 0x10, device, 0x8F, 0x00, (byte)HidppHapticProtocol.SoftwareId, code, 0x00 });

        [Fact]
        public void AReceiversHidpp10ErrorSettlesOnlyAHidpp10Device()
        {
            var state = new HidppPathState { Receiver = true };
            var channel = new HidppHapticsTests.FakeChannel
            {
                // Slot 1 holds a HID++ 1.0 device, slot 2 one that is asleep.
                Answer = (d, f, fn, p) => d == 1 ? Hidpp10Error(1, HidppHapticProtocol.Hidpp10InvalidSubId)
                    : d == 2 ? Hidpp10Error(2, 0x08)
                    : HidppHapticsTests.NoAnswer(),
            };

            HidppUnitProbe.Probe(channel, state, Guid.Empty, 0);

            Assert.True(state.Settled[1]);
            Assert.False(state.Settled[2]);
            Assert.True(state.RetryAt[2] > 0);

            // At 0xFF a receiver's error never makes the path a device.
            var direct = new HidppPathState();
            var receiver = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => d == 0xFF ? Hidpp10Error(0xFF, HidppHapticProtocol.Hidpp10InvalidSubId)
                    : HidppHapticsTests.NoAnswer(),
            };
            HidppUnitProbe.Probe(receiver, direct, Guid.Empty, 0);
            Assert.False(direct.Direct);
            Assert.Contains(receiver.Asked, a => a.Device == 1);
        }

        [Fact]
        public void AReceiversHidpp10ErrorInTheSleepCheckReadsAsAsleep()
        {
            // The receiver says the device cannot be reached: no answer.
            var asleep = new HidppHapticsTests.FakeChannel { Answer = (d, f, fn, p) => Hidpp10Error(d, 0x09) };
            Assert.Null(HidppHapticProbe.ReadFeedbackEnabled(asleep, 0x01, 0x0B));
            // A HID++ 2.0 error comes from the device itself, which is awake.
            var refused = new HidppHapticsTests.FakeChannel { Answer = (d, f, fn, p) => new HidppReply(HidppReplyKind.Error, 0x05) };
            Assert.True(HidppHapticProbe.ReadFeedbackEnabled(refused, 0x01, 0x0B));
        }

        [Fact]
        public void AHidpp10ErrorMidwayIsAMissToAskAgain()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            var channel = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => f == 0x0E ? Hidpp10Error(d, 0x09) : device.Answer(d, f, fn, p),
            };
            HidppUnitProbe.Describe(channel, device.Index, 0x03, Guid.Empty, 300, out bool incomplete);
            Assert.True(incomplete);
        }

        [Fact]
        public void TheHidpp10ErrorMatchesOnlyItsOwnRequest()
        {
            byte[] error = { 0x10, 0x02, 0x8F, 0x00, (byte)HidppHapticProtocol.SoftwareId, 0x09, 0x00 };
            Assert.Equal(HidppReplyKind.Error, HidppHapticProtocol.MatchHidpp10Error(error, 0x02, 0x00, 0, out byte code));
            Assert.Equal(0x09, code);
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.MatchHidpp10Error(error, 0x03, 0x00, 0, out _));
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.MatchHidpp10Error(error, 0x02, 0x0B, 0, out _));
            // Another program's request carries its own software ID.
            byte[] theirs = { 0x10, 0x02, 0x8F, 0x00, 0x01, 0x09, 0x00 };
            Assert.Equal(HidppReplyKind.None, HidppHapticProtocol.MatchHidpp10Error(theirs, 0x02, 0x00, 0, out _));
            // The long answer path stays HID++ 2.0 only.
            Assert.False(new HidppReply(HidppReplyKind.Error, 0x08).Hidpp10);
            Assert.True(Hidpp10Error(0x02, 0x08).Hidpp10);
        }

        [Fact]
        public void APathThatCannotBeOpenedTakesItsMisses()
        {
            var state = new HidppPathState();
            HidppUnitProbe.MissOpenSlots(state, 0, bluetooth: false);
            Assert.All(Enumerable.Range(0, 7), slot => Assert.Equal(1, state.Misses[slot]));
            // Inside the backoff nothing more is counted.
            HidppUnitProbe.MissOpenSlots(state, 1000, bluetooth: false);
            Assert.All(Enumerable.Range(0, 7), slot => Assert.Equal(1, state.Misses[slot]));
            HidppUnitProbe.MissOpenSlots(state, HidppHapticProbe.FirstRetryMs, bluetooth: false);
            HidppUnitProbe.MissOpenSlots(state, HidppHapticProbe.FirstRetryMs * 4, bluetooth: false);
            Assert.False(HidppUnitProbe.Pending(state, bluetooth: false));
            Assert.True(HidppUnitProbe.HasOpenSlot(state, bluetooth: false));
        }

        [Fact]
        public void TheReceiverProtocolFollowsSolaar()
        {
            Assert.Equal(HidppReceiverKind.Bolt, HidppReceiverProtocol.KindOf(0xC548));
            foreach (ushort pid in new ushort[] { 0xC52B, 0xC532, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D })
                Assert.Equal(HidppReceiverKind.Unifying, HidppReceiverProtocol.KindOf(pid));
            foreach (ushort pid in new ushort[] { 0xC52F, 0xC534, 0xC517, 0xC08B })
                Assert.Equal(HidppReceiverKind.None, HidppReceiverProtocol.KindOf(pid));

            Assert.Equal(0x51, HidppReceiverProtocol.PairingSub(HidppReceiverKind.Bolt, 1));
            Assert.Equal(0x56, HidppReceiverProtocol.PairingSub(HidppReceiverKind.Bolt, 6));
            Assert.Equal(0x20, HidppReceiverProtocol.PairingSub(HidppReceiverKind.Unifying, 1));
            Assert.Equal(0x25, HidppReceiverProtocol.PairingSub(HidppReceiverKind.Unifying, 6));

            Assert.Equal(new byte[] { 0x10, 0xFF, 0x83, 0xB5, 0x52, 0x00, 0x00 }, HidppReceiverProtocol.ReadRegister(0xB5, 0x52));

            var record = new byte[20];
            new byte[] { 0x11, 0xFF, 0x83, 0xB5, 0x52, 0x01, 0x02 }.CopyTo(record, 0);
            Assert.Equal(HidppReplyKind.Answer, HidppReceiverProtocol.Match(record, 0xB5, 0x52, out _));
            Assert.Equal(HidppReplyKind.None, HidppReceiverProtocol.Match(record, 0xB5, 0x53, out _));

            byte[] error = { 0x10, 0xFF, 0x8F, 0x83, 0xB5, 0x03, 0x00 };
            Assert.Equal(HidppReplyKind.Error, HidppReceiverProtocol.Match(error, 0xB5, 0x52, out byte code));
            Assert.Equal(0x03, code);
            Assert.Equal(HidppReplyKind.None, HidppReceiverProtocol.Match(error, 0xB2, 0x52, out _));
        }

        [Fact]
        public void TheShortCollectionIsFoundByOpenRgbsPathKey()
        {
            const string guid = "#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string longPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#8&b41415e&0&0001" + guid;
            string shortPath = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#8&b41415e&0&0000" + guid;
            string otherShort = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#8&c51515e&0&0000" + guid;

            Assert.Equal(@"\\?\hid#vid_046d&pid_c548&mi_02#8&b41415e&0" + guid, HidppReceiverProtocol.PathKey(longPath));
            Assert.Equal(HidppReceiverProtocol.PathKey(longPath), HidppReceiverProtocol.PathKey(shortPath));
            Assert.Equal("no-collection-here", HidppReceiverProtocol.PathKey("no-collection-here"));

            VendorHidCollection C(string path, ushort usage) => new()
            {
                Path = path, VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = usage,
            };
            var longCollection = C(longPath, 2);
            var mine = C(shortPath, 1);
            // A second receiver of the same model has its own instance.
            Assert.Same(mine, HidppReceiverProtocol.ShortSibling(longCollection, new[] { C(otherShort, 1), longCollection, mine }));
            Assert.Null(HidppReceiverProtocol.ShortSibling(longCollection, new[] { C(otherShort, 1) }));
        }

        [Fact]
        public void AnyMissAtTheCapabilitiesIsAskedAgain()
        {
            var busy = HidppHapticsTests.Mouse(0xFF);
            var channel = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => f == 0x0B && fn == 0 ? new HidppReply(HidppReplyKind.Error, 0x08) : busy(d, f, fn, p),
            };
            var state = new HidppPathState();
            Assert.Empty(HidppUnitProbe.Probe(channel, state, Guid.Empty, 0));
            Assert.False(state.Settled[0]);
            Assert.True(state.RetryAt[0] > 0);
        }

        [Fact]
        public void AProbeStopsAtTheFirstDecisiveMiss()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Features[0x1004] = 0x08;
            device.Zones = Enumerable.Range(0, 8).Select(_ => new ushort[] { 0x0000, 0x0001 }).ToArray();
            // Zone 2 stops answering.
            device.Silent = (f, fn, p) => f == 0x0E && fn == 1 && p[0] == 2;
            var channel = device.Channel();

            HidppUnitProbe.Describe(channel, device.Index, 0x03, Guid.Empty, 300, out bool incomplete);

            Assert.True(incomplete);
            // Nothing past the first miss: zones 0 and 1, then the silent
            // zone 2, and no later one.
            Assert.Equal(3, channel.Asked.Count(a => a.Feature == 0x0E && a.Function == 1));
            // Root lookups: the haptics and four RGB features. The battery,
            // which comes after the zones, is never looked up.
            Assert.Equal(new ushort[] { 0x19B0, 0x8071, 0x8081, 0x8080, 0x8070 }, channel.RootLookups);
        }

        /// <summary>A Bolt receiver with a haptic mouse in slot 1, a zone-only
        /// 0x8070 mouse with a battery in slot 2, a paired device asleep in
        /// slot 3 and nothing in slots 4 to 6.</summary>
        internal static HidppHapticsTests.FakeChannel QuietReceiver(string path = null)
        {
            var haptic = HidppHapticsTests.Mouse(1);
            var lit = new Scripted { Index = 2, BatteryPercent = 55 };
            lit.Features[0x8070] = 0x0E;
            lit.Features[0x1004] = 0x08;
            lit.Zones = new[] { new ushort[] { 0x0000, 0x0001 }, new ushort[] { 0x0001 } };
            var channel = new HidppHapticsTests.FakeChannel
            {
                Answer = (d, f, fn, p) => d == 1 ? haptic(d, f, fn, p) : d == 2 ? lit.Answer(d, f, fn, p) : HidppHapticsTests.NoAnswer(),
                // Bolt keeps slot n's pairing record at 0x50 + n.
                Register = (r, s) => r == 0xB5 && s <= 0x53
                    ? new HidppReply(HidppReplyKind.Answer)
                    : new HidppReply(HidppReplyKind.Error, 0x03),
            };
            return path == null ? channel : new HidppHapticsTests.FakeChannel
            {
                Path = path, Answer = channel.Answer, Register = channel.Register,
            };
        }

        /// <summary>What a probe of <see cref="QuietReceiver"/> asks, frame
        /// for frame and in order, with no rumble arriving: the pairing
        /// table first, an error read twice before it counts, then each
        /// paired slot. The bytes are the ones the probe sent before a pulse
        /// could go out beside it.</summary>
        internal static readonly string[] QuietProbe =
        {
            // The pairing records of slots 1 to 6.
            "reg 10 FF 83 B5 51", "reg 10 FF 83 B5 52", "reg 10 FF 83 B5 53",
            "reg 10 FF 83 B5 54", "reg 10 FF 83 B5 54",
            "reg 10 FF 83 B5 55", "reg 10 FF 83 B5 55",
            "reg 10 FF 83 B5 56", "reg 10 FF 83 B5 56",
            // Slot 1: 0x0005, its type, its name, 0x19B0 with its mask and
            // its configuration, the four RGB features, the two batteries.
            "ask 11 01 00 0C 00 05",
            "ask 11 01 03 2C", "ask 11 01 03 0C", "ask 11 01 03 1C",
            "ask 11 01 00 0C 19 B0", "ask 11 01 0B 0C", "ask 11 01 0B 1C",
            "ask 11 01 00 0C 80 71", "ask 11 01 00 0C 80 81", "ask 11 01 00 0C 80 80", "ask 11 01 00 0C 80 70",
            "ask 11 01 00 0C 10 04", "ask 11 01 00 0C 10",
            // Slot 2: no haptics, 0x8070 found last of the four, its two
            // zones walked for the static effect, the first battery found.
            "ask 11 02 00 0C 00 05",
            "ask 11 02 03 2C", "ask 11 02 03 0C", "ask 11 02 03 1C",
            "ask 11 02 00 0C 19 B0",
            "ask 11 02 00 0C 80 71", "ask 11 02 00 0C 80 81", "ask 11 02 00 0C 80 80", "ask 11 02 00 0C 80 70",
            "ask 11 02 0E 0C",
            "ask 11 02 0E 1C 00 FF", "ask 11 02 0E 2C", "ask 11 02 0E 2C 00 01",
            "ask 11 02 0E 1C 01 FF", "ask 11 02 0E 2C 01",
            "ask 11 02 00 0C 10 04",
            // Slot 3 does not answer. Slots 4 to 6 are never asked.
            "ask 11 03 00 0C 00 05",
        };

        [Fact]
        public void AQuietProbe_AsksWhatItHasAlwaysAsked_FrameForFrame()
        {
            var channel = QuietReceiver();
            var state = new HidppPathState { ReceiverKind = HidppReceiverKind.Bolt };

            var found = HidppUnitProbe.Probe(channel, state, Guid.Empty, 0);

            Assert.Equal(QuietProbe, HidppHapticsTests.Lines(channel.Traffic));
            Assert.Equal(new byte[] { 1, 2 }, found.Select(u => u.DeviceIndex).ToArray());
            // What the probe concluded, which a pulse beside it must not move.
            Assert.True(found[0].HasHaptics && !found[0].HasRgb);
            Assert.Equal(0x7FFFu, found[0].WaveformMask);
            Assert.True(found[1].DirectRgb && !found[1].HasHaptics);
            Assert.Equal(new[] { new HidppZone(0, 1), new HidppZone(1, 0) }, found[1].Zones);
            Assert.Equal((HidppUnitProtocol.UnifiedBattery, (byte)0x08), (found[1].BatteryFeatureId, found[1].BatteryIndex));
            // Index 7 is a Bolt's no slot: never asked, never read (#557).
            Assert.Equal(new[] { true, true, false, false, false, false, false }, state.Settled.Skip(1).ToArray());
            Assert.Equal(new[] { false, false, false, true, true, true, false }, state.Unpaired.Skip(1).ToArray());
            Assert.Equal(HidppHapticProbe.FirstRetryMs, state.RetryAt[3]);
            Assert.Equal(1, state.Misses[3]);
        }

        [Fact]
        public void TheWorkerOpensMiceReceiversAndKeyboardsOnTheirLongCollections()
        {
            static VendorHidCollection Collection(ushort page, ushort usage, ushort length = 20, ushort vid = 0x046D)
                => new() { VendorId = vid, UsagePage = page, Usage = usage, InputReportLength = length };

            Assert.True(HidppBackend.IsHidppLong(Collection(0xFF00, 0x0002)));
            Assert.True(HidppBackend.IsHidppLong(Collection(0xFF43, 0x0602)));
            Assert.False(HidppBackend.IsHidppLong(Collection(0xFF00, 0x0001, 7)));
            Assert.False(HidppBackend.IsHidppLong(Collection(0xFF43, 0x0604, 64)));
            Assert.False(HidppBackend.IsHidppLong(Collection(0xFF00, 0x0002, vid: 0x1532)));
            // The Bluetooth LE devices OpenLogi lists and the headsets OpenRGB
            // drives on 0xFF43 usage 0x0202, with the 20-byte report only.
            Assert.True(HidppBackend.IsHidppLong(Collection(0xFF43, 0x0202)));
            Assert.False(HidppBackend.IsHidppLong(Collection(0xFF43, 0x0202, 64)));
            // The Centurion page carries frames the channel does not speak.
            Assert.False(HidppBackend.IsHidppLong(Collection(0xFFA0, 0x0001)));
        }
    }

    // ─────────────────────────────────────────────
    //  The facade and its workers: shared state
    // ─────────────────────────────────────────────

    /// <summary>The levels, claims and links the engine and the backends
    /// meet at (#494).</summary>
    [Collection("PeripheralOutputStatics")]
    public class PeripheralOutputsFacadeTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Vendor = new("00000000-0000-0000-0000-0000000000c3");

        public PeripheralOutputsFacadeTests()
        {
            PeripheralOutputs.ResetForTest();
            // The route lines say nothing while the engine is stopped.
            PeripheralOutputs.SetRunning(true);
        }

        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        [Fact]
        public void ALevelIsKeptWithoutAPath_SoAWokenDevicePlaysTheCurrentOne()
        {
            PeripheralOutputs.SetMotors(A, 65535, 65535);
            Assert.Equal(1f, PeripheralOutputs.AmplitudeOf(A), 5);

            // The stop lands whatever the links say: a device that wakes
            // after the game stopped plays nothing.
            PeripheralOutputs.SetMotors(A, 0, 0);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));

            PeripheralOutputs.SetMotors(B, 0, 0);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(B));
        }

        [Fact]
        public void ARowTakesHapticsByItsPathOrItsRecord()
        {
            Assert.False(PeripheralOutputs.TakesHaptics(null));
            Assert.False(PeripheralOutputs.TakesHaptics(new UserDevice { InstanceGuid = A }));
            Assert.True(PeripheralOutputs.TakesHaptics(new UserDevice { InstanceGuid = A, PeripheralOutputs = 1 }));
            Assert.False(PeripheralOutputs.TakesHaptics(new UserDevice { InstanceGuid = A, PeripheralOutputs = 2 }));

            Link(new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } });
            Assert.True(PeripheralOutputs.TakesHaptics(new UserDevice { InstanceGuid = A }));
        }

        [Fact]
        public void AForwardedMouseWithRumbleShowsItsTab_AndTakesNoLevelHere()
        {
            RemotePeerDevice Peer(bool rumble, int type) => new(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "owner", PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
                HasRumble = rumble, VendorId = 0x046D, ProductId = 0xB042,
                NumAxes = 2, NumButtons = 5, InputDeviceType = type,
            });

            var mouse = new UserDevice { InstanceGuid = A, CapType = InputDeviceType.Mouse, Device = Peer(true, InputDeviceType.Mouse) };
            Assert.True(PeripheralOutputs.IsHapticPeripheral(mouse));
            // Its level ships back to the PC it came from, never to a backend here.
            Assert.False(PeripheralOutputs.TakesHaptics(mouse));

            var silent = new UserDevice { InstanceGuid = A, CapType = InputDeviceType.Mouse, Device = Peer(false, InputDeviceType.Mouse) };
            Assert.False(PeripheralOutputs.IsHapticPeripheral(silent));

            // A forwarded gamepad rumbles through the gamepad gate, not this one.
            var pad = new UserDevice { InstanceGuid = A, CapType = InputDeviceType.Gamepad, Device = Peer(true, InputDeviceType.Gamepad) };
            Assert.False(PeripheralOutputs.IsHapticPeripheral(pad));
        }

        [Fact]
        public void TheStrongerMotorIsTheLevel_AndOnlyAStartWakesTheWorkers()
        {
            Link(new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } });
            int wakes = 0;
            void Wake() => wakes++;
            PeripheralOutputs.HapticsChanged += Wake;
            try
            {
                PeripheralOutputs.SetMotors(A, 16384, 49151);
                Assert.Equal(49151 / 65535f, PeripheralOutputs.AmplitudeOf(A), 5);
                Assert.Equal(1, wakes);

                PeripheralOutputs.SetMotors(A, 65535, 0);
                Assert.Equal(1f, PeripheralOutputs.AmplitudeOf(A), 5);
                Assert.Equal(1, wakes);

                PeripheralOutputs.SetMotors(A, 0, 0);
                Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));
                PeripheralOutputs.SetMotors(A, 1000, 0);
                Assert.Equal(2, wakes);
            }
            finally { PeripheralOutputs.HapticsChanged -= Wake; }
        }

        [Fact]
        public void TheSilenceEdgeZeroesEveryLevel_AndOwesEachPlayingRowOneResend()
        {
            Link(new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
                 new DeviceLinks { Device = B, Haptics = new[] { PeripheralLinker.SensaPath } });
            PeripheralOutputs.SetMotors(A, 30000, 0);
            PeripheralOutputs.SetMotors(B, 0, 0);

            PeripheralOutputs.SilenceHaptics();

            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));
            Assert.True(PeripheralOutputs.ConsumeResend(A));
            Assert.False(PeripheralOutputs.ConsumeResend(A));
            Assert.False(PeripheralOutputs.ConsumeResend(B));   // it was silent already
        }

        [Fact]
        public void TheFocusEdgeKeepsARelayedLevel_AndAStopEndsIt()
        {
            PeripheralOutputs.SetMotors(A, 30000, 0, relayed: true);
            PeripheralOutputs.SetMotors(B, 30000, 0);

            PeripheralOutputs.SilenceHaptics(keepRelayed: true);
            Assert.Equal(30000 / 65535f, PeripheralOutputs.AmplitudeOf(A), 5);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(B));
            Assert.False(PeripheralOutputs.ConsumeResend(A));

            // A local write takes the level back from the relay.
            PeripheralOutputs.SetMotors(A, 20000, 0);
            PeripheralOutputs.SilenceHaptics(keepRelayed: true);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));

            // An engine stop ends the relay as well.
            PeripheralOutputs.SetMotors(A, 30000, 0, relayed: true);
            PeripheralOutputs.SilenceHaptics();
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));
            Assert.True(PeripheralOutputs.ConsumeResend(A));
        }

        [Fact]
        public void AStopZeroesTheLevelWithoutAResend()
        {
            Link(new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } });
            PeripheralOutputs.SetMotors(A, 30000, 0);
            PeripheralOutputs.StopHaptics(A);
            Assert.Equal(0f, PeripheralOutputs.AmplitudeOf(A));
            Assert.False(PeripheralOutputs.ConsumeResend(A));
            PeripheralOutputs.StopHaptics(Guid.NewGuid());   // a row with no level: nothing to do
        }

        [Fact]
        public void TheSmallestAssignedPlayerRulesASharedHapticPath_AndADeviceBeatsItsVendorRow()
        {
            var path = PeripheralLinker.GameSenseTactilePath;
            Link(new DeviceLinks { Device = A, Haptics = new[] { path } },
                 new DeviceLinks { Device = B, Haptics = new[] { path } },
                 new DeviceLinks { Device = Vendor, Haptics = new[] { path }, CatchAll = true });
            var players = new Dictionary<Guid, int> { [A] = 2, [B] = 1, [Vendor] = 0 };
            int PlayerOf(Guid g) => players.TryGetValue(g, out int p) ? p : 0;

            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out var ruler));
            Assert.Equal(B, ruler);

            players[B] = 0;   // B left its controller
            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
            Assert.Equal(A, ruler);

            players[Vendor] = 1;   // the vendor row yields to a device on its own
            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
            Assert.Equal(A, ruler);

            players[A] = 0;
            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
            Assert.Equal(Vendor, ruler);

            players[Vendor] = 0;
            Assert.False(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out _));
            Assert.False(PeripheralOutputs.TryResolveHapticRuler(PeripheralLinker.SensaPath, PlayerOf, out _));

            // A Rival a linked PC drives ranks after every controller here.
            players[A] = PeripheralOutputs.PeerDrivenPlayer;
            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
            Assert.Equal(A, ruler);
            players[B] = 16;
            Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
            Assert.Equal(B, ruler);
        }

        [Fact]
        public void ALightingClaimRulesItsPathByPlayer_AndOnlyItsOwnSlotReleasesIt()
        {
            var path = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { path } },
                 new DeviceLinks { Device = B, Lighting = new[] { path } });

            Assert.False(PeripheralOutputs.TryResolveColor(path, out _, out _));

            int version = PeripheralOutputs.LightingVersion;
            PeripheralOutputs.SetLighting(A, slot: 2, player: 3, 0x11, 0x22, 0x33);
            PeripheralOutputs.SetLighting(B, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(PeripheralOutputs.LightingVersion > version);
            Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb, out var ruler));
            Assert.Equal(B, ruler.Device);
            Assert.Equal(0x445566, rgb);

            // The same color again is no change.
            version = PeripheralOutputs.LightingVersion;
            PeripheralOutputs.SetLighting(B, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.Equal(version, PeripheralOutputs.LightingVersion);

            // Another slot cannot release B's claim.
            PeripheralOutputs.ReleaseLighting(B, slot: 5);
            Assert.True(PeripheralOutputs.IsLit(B));

            PeripheralOutputs.ReleaseLighting(B, slot: 0);
            Assert.False(PeripheralOutputs.IsLit(B));
            Assert.True(PeripheralOutputs.TryResolveColor(path, out rgb, out ruler));
            Assert.Equal(A, ruler.Device);
            Assert.Equal(0x112233, rgb);

            // A slot that lights nothing any more, its controller gone, loses
            // its claims at the link pass's prune, its device still assigned.
            PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)> { (A, 2) }, slot => slot != 2);
            Assert.False(PeripheralOutputs.TryResolveColor(path, out _, out _));
        }

        /// <summary>Identify on a row takes its shared haptic path for the
        /// train, ahead of every rank, so an unassigned Rival plays its pulses
        /// and a Rival another controller's Rival rules does too. The path
        /// goes back to the ranking when the train ends, and a claim never
        /// ended lapses on its own (#494).</summary>
        [Fact]
        public void AnIdentifiedRowRulesASharedHapticPath_ForItsTrainOnly()
        {
            var path = PeripheralLinker.GameSenseTactilePath;
            Link(new DeviceLinks { Device = A, Haptics = new[] { path } },
                 new DeviceLinks { Device = B, Haptics = new[] { path } });
            var players = new Dictionary<Guid, int> { [A] = 0, [B] = 1 };
            int PlayerOf(Guid g) => players.TryGetValue(g, out int p) ? p : 0;
            int changes = 0;
            void OnChange() => changes++;
            PeripheralOutputs.IdentifyChanged += OnChange;
            try
            {
                // Unassigned, A ranks nowhere, and B on controller 1 rules.
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out var ruler));
                Assert.Equal(B, ruler);

                PeripheralOutputs.BeginIdentify(A, 60000);
                Assert.Equal(1, changes);
                Assert.True(PeripheralOutputs.IsIdentifying(A));
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
                Assert.Equal(A, ruler);

                PeripheralOutputs.EndIdentify(A);
                Assert.Equal(2, changes);
                Assert.False(PeripheralOutputs.IsIdentifying(A));
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
                Assert.Equal(B, ruler);

                // Nothing assigned: only Identify rules.
                players[B] = 0;
                Assert.False(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out _));
                PeripheralOutputs.BeginIdentify(A, 60000);
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
                Assert.Equal(A, ruler);
                PeripheralOutputs.EndIdentify(A);

                // A claim no train ended lapses at its bound.
                PeripheralOutputs.BeginIdentify(A, 1000, Environment.TickCount64 - 5000);
                Assert.False(PeripheralOutputs.IsIdentifying(A));
                Assert.False(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out _));

                // Two rows identified at once: the claim that lapses last
                // rules, whichever row the table lists first.
                long now = Environment.TickCount64;
                PeripheralOutputs.BeginIdentify(A, 60000, now);
                PeripheralOutputs.BeginIdentify(B, 30000, now);
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
                Assert.Equal(A, ruler);
                PeripheralOutputs.BeginIdentify(B, 90000, now);
                Assert.True(PeripheralOutputs.TryResolveHapticRuler(path, PlayerOf, out ruler));
                Assert.Equal(B, ruler);
                PeripheralOutputs.EndIdentify(A);
                PeripheralOutputs.EndIdentify(B);
            }
            finally { PeripheralOutputs.IdentifyChanged -= OnChange; }
        }

        /// <summary>The Razer Sensa worker reads its row's level on every
        /// tick, so the read allocates nothing: the row's id is hashed
        /// once.</summary>
        [Fact]
        public void TheSensaRowsLevelIsReadWithoutAllocating()
        {
            var sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            PeripheralOutputs.SetMotors(sensa, 0, 32768);
            Assert.Equal(32768 / 65535f, SensaHapticsService.RowAmplitude(), 5);
            // Counted over windows the runtime left alone: the thread's
            // count moves by itself in a full run (AllocationWindow).
            foreach (long bytes in AllocationWindow.Clean(() => SensaHapticsService.RowAmplitude(), 1000))
                Assert.Equal(0, bytes);
        }

        /// <summary>The HID++ worker asks every unit whether it plays a
        /// waveform on every pass, so the answer allocates nothing.</summary>
        [Fact]
        public void TheWaveformChoiceAllocatesNothing()
        {
            const uint all = (1u << 2) | (1u << 3) | (1u << 4);
            Assert.Equal((byte)0x04, HapticRumbleShaper.Waveform(1, all));
            int n = 0;
            foreach (long bytes in AllocationWindow.Clean(() => HapticRumbleShaper.Waveform(1 + n++ % 3, all), 1000))
                Assert.Equal(0, bytes);
        }

        [Fact]
        public void AClaimOnARowNotLinkedToThePathIsIgnored()
        {
            var path = new OutputPath(OutputFamily.LedSdkType, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "keyboard") } });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 2, 3);
            Assert.False(PeripheralOutputs.TryResolveColor(path, out _, out _));
        }

        [Fact]
        public void PublishingLinksRaisesTheEventAndBumpsTheVersion()
        {
            int raised = 0;
            void OnLinks() => raised++;
            PeripheralOutputs.LinksChanged += OnLinks;
            try
            {
                int version = PeripheralOutputs.LinkVersion;
                Link(new DeviceLinks { Device = A, Haptics = new[] { PeripheralLinker.SensaPath } });
                Assert.Equal(1, raised);
                Assert.Equal(version + 1, PeripheralOutputs.LinkVersion);
                Assert.True(PeripheralOutputs.HasHaptics(A));
                Assert.False(PeripheralOutputs.HasLighting(A));
            }
            finally { PeripheralOutputs.LinksChanged -= OnLinks; }
        }

        [Fact]
        public void ABackendStateRaisesStatusOnlyWhenItChanges()
        {
            int raised = 0;
            void OnStatus() => raised++;
            PeripheralOutputs.StatusChanged += OnStatus;
            try
            {
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
                Assert.Equal(1, raised);
                Assert.Equal(BackendState.Waiting, PeripheralOutputs.StateOf(OutputFamily.Interhaptics));
                Assert.Equal(BackendState.Idle, PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile));
            }
            finally { PeripheralOutputs.StatusChanged -= OnStatus; }
        }

        /// <summary>The HID++ worker builds a new snapshot on every scan and
        /// liveness check, most of them the same as the last. A status change
        /// raised for a new object alone refreshed every pad page and rewrote
        /// PadForge.xml every 30 s on a PC with a Logitech receiver (audit of
        /// 2026-10-27), so only a snapshot a tab would read differently
        /// raises one, as the direct backends' snapshot already did.</summary>
        [Fact]
        public void AnHidppSnapshotLikeTheLastRaisesNoStatus()
        {
            var unit = PeripheralLinkingTests.Unit(1, 3, Vendor) with { Name = "MX Master 4", BatteryPercent = 80 };
            HidppSnapshot Snapshot(int battery, params Guid[] pending) => new(
                new[] { unit with { Zones = new[] { new HidppZone(0, 1) }, BatteryPercent = battery } },
                new HashSet<Guid>(pending));

            PeripheralOutputs.Hidpp = Snapshot(80, Vendor);
            int raised = 0;
            void OnStatus() => raised++;
            PeripheralOutputs.StatusChanged += OnStatus;
            try
            {
                // A rescan that found the same unit builds new objects, zones included.
                PeripheralOutputs.Hidpp = Snapshot(80, Vendor);
                Assert.Equal(0, raised);
                // A battery reading that moved is news.
                PeripheralOutputs.Hidpp = Snapshot(40, Vendor);
                Assert.Equal(1, raised);
                // So is a container that is no longer waiting on a slot.
                PeripheralOutputs.Hidpp = Snapshot(40);
                Assert.Equal(2, raised);
            }
            finally { PeripheralOutputs.StatusChanged -= OnStatus; }
        }

        [Fact]
        public void TheForceFeedbackLineNamesTheDeviceItsRowReaches()
        {
            var s = Strings.Instance;
            var unit = PeripheralLinkingTests.Unit(1, 3, Guid.Empty) with { Name = "MX Master 4", FeedbackEnabled = false };
            PeripheralOutputs.Hidpp = new HidppSnapshot(new[] { unit }, Array.Empty<Guid>());
            Link(new DeviceLinks { Device = A, Haptics = new[] { new OutputPath(OutputFamily.HidppUnit, unit.Key) } });

            var mouse = new UserDevice { InstanceGuid = A, VendorId = PeripheralLinker.LogitechVid, PeripheralOutputs = 1 };
            Assert.Equal(string.Format(s.Pad_ForceFeedback_RouteHidppFeedbackOff, "MX Master 4"),
                PeripheralRouteText.Haptics(mouse));

            PeripheralOutputs.Hidpp = new HidppSnapshot(new[] { unit with { FeedbackEnabled = true } }, Array.Empty<Guid>());
            Assert.Equal(string.Format(s.Pad_ForceFeedback_RouteHidpp, "MX Master 4"), PeripheralRouteText.Haptics(mouse));

            // Asleep: no path, and the record still says it has haptics.
            Link();
            Assert.Equal(s.Pad_ForceFeedback_RouteHidppAsleep, PeripheralRouteText.Haptics(mouse));
            Assert.Null(PeripheralRouteText.Haptics(new UserDevice { InstanceGuid = B }));
            // Only a Logitech device sleeps behind its HID++ path.
            Assert.Null(PeripheralRouteText.Haptics(
                new UserDevice { InstanceGuid = B, VendorId = PeripheralLinker.SteelSeriesVid, PeripheralOutputs = 1 }));

            var sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            var sensaRow = new UserDevice { InstanceGuid = sensa, PeripheralOutputs = 1 };
            // No path: the engine cannot load here, or Synapse is missing.
            Assert.Equal(PlatformSupport.SensaAvailable ? s.Pad_ForceFeedback_RouteSensaWaiting : s.Common_NotAvailableOnArm64,
                PeripheralRouteText.Haptics(sensaRow));

            Link(new DeviceLinks { Device = sensa, Haptics = new[] { PeripheralLinker.SensaPath }, CatchAll = true });
            Assert.Equal(s.Pad_ForceFeedback_RouteSensa, PeripheralRouteText.Haptics(sensaRow));
            PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
            Assert.Equal(s.Pad_ForceFeedback_RouteSensaWaiting, PeripheralRouteText.Haptics(sensaRow));
        }

        [Fact]
        public void TheSensaWorkerStreamsItsRowsLevel()
        {
            var sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            Link(new DeviceLinks { Device = sensa, Haptics = new[] { PeripheralLinker.SensaPath }, CatchAll = true });
            Assert.Equal(0f, SensaHapticsService.RowAmplitude());
            PeripheralOutputs.SetMotors(sensa, 0, 32768);
            Assert.Equal(32768 / 65535f, SensaHapticsService.RowAmplitude(), 5);
        }
    }

    /// <summary>Reads a worker's cadence without timing it: the wait the
    /// worker asked for, where a count of passes in a second would fall
    /// short on a busy bench with nothing wrong in the worker.</summary>
    internal static class WorkerCadence
    {
        /// <summary>The wait a worker asked for once a whole pass has run
        /// after this call. A worker counts a pass as it starts and records
        /// its wait as the pass ends, so once two more passes have started,
        /// one of them began after the call and has ended, and the wait read
        /// then was chosen from what the worker could see after the call.
        /// The limit only catches a worker that makes no pass at all.</summary>
        internal static int WaitAfterAFullPass(Func<int> passes, Func<int> waitMs)
        {
            int seen = passes();
            Assert.True(SpinWait.SpinUntil(() => passes() >= seen + 2, 10000), "the worker made no pass");
            return waitMs();
        }
    }

    /// <summary>The HID++ worker plays a unit only for a row linked to it
    /// (#494), against a scripted receiver.</summary>
    [Collection("PeripheralOutputStatics")]
    public class HidppBackendTests : IDisposable
    {
        public HidppBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static readonly VendorHidCollection Receiver = new()
        {
            Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#bench",
            VendorId = 0x046D,
            ProductId = 0xC548,
            UsagePage = 0xFF00,
            Usage = 0x0002,
            InputReportLength = 20,
        };

        [Fact]
        public void TheWorkerPlaysOnlyTheUnitALinkedRowAsksFor()
        {
            var container = Guid.NewGuid();
            var first = HidppHapticsTests.Mouse(1);
            var second = HidppHapticsTests.Mouse(2, "MX Anywhere 3S");
            var channel = new HidppHapticsTests.FakeChannel
            {
                Path = Receiver.Path,
                Answer = (d, f, fn, p) => d == 1 ? first(d, f, fn, p) : d == 2 ? second(d, f, fn, p) : HidppHapticsTests.NoAnswer(),
            };
            using var backend = new HidppBackend(() => new[] { Receiver }, (c, s) => channel, _ => container, 1, 5);
            Assert.False(backend.Snapshot.Scanned);
            backend.Start();

            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 2, 5000), "the scan found both mice");
            Assert.True(backend.Snapshot.Scanned);
            // The pairing table goes unanswered here, so slots 3 to 6 may
            // hold a sleeping device and the container is still looked at.
            Assert.Contains(container, backend.Snapshot.PendingContainers);
            Assert.All(backend.Snapshot.Units, u => Assert.Equal(container, u.ContainerId));

            var unit = backend.Snapshot.Units.Single(u => u.DeviceIndex == 1);
            var device = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = device, Haptics = new[] { new OutputPath(OutputFamily.HidppUnit, unit.Key) } },
            }));
            while (channel.Writes.TryDequeue(out _)) { }

            PeripheralOutputs.SetMotors(device, 65535, 0);
            Assert.True(SpinWait.SpinUntil(() => channel.Writes.Count >= 2, 3000), "pulses played");
            Assert.All(channel.Writes.Select(w => w.Frame),
                frame => Assert.Equal(HidppHapticProtocol.Play(1, 0x0B, HidppHapticProtocol.SharpCollision), frame));

            // The pulses stop: a quiet 300 ms comes within three seconds.
            PeripheralOutputs.SetMotors(device, 0, 0);
            Assert.True(QuietFor(channel, 300, 3000), "pulses kept coming after the stop");

            // A write that fails drops the collection and what it found.
            channel.FailWrites = true;
            PeripheralOutputs.SetMotors(device, 65535, 0);
            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 0, 3000), "the failed path was dropped");
            Assert.True(channel.Disposed);

            // The worker's finally empties the snapshot.
            backend.Dispose();
            Assert.False(backend.Snapshot.Scanned);
        }

        /// <summary>True once no write landed for <paramref name="quietMs"/>
        /// inside <paramref name="timeoutMs"/>.</summary>
        private static bool QuietFor(HidppHapticsTests.FakeChannel channel, int quietMs, int timeoutMs)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int count = channel.Writes.Count;
            long since = 0;
            while (clock.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(20);
                int now = channel.Writes.Count;
                if (now != count)
                {
                    count = now;
                    since = clock.ElapsedMilliseconds;
                }
                else if (clock.ElapsedMilliseconds - since >= quietMs)
                {
                    return true;
                }
            }
            return false;
        }

        [Fact]
        public void TheWorkerReadsTheReceiversPairingTable_AndAsksOnlyPairedSlots()
        {
            var container = Guid.NewGuid();
            var shortCollection = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#bench",
                VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = 0x0001, InputReportLength = 7,
            };
            VendorHidCollection openedWith = null;
            var mouse = HidppHapticsTests.Mouse(1);
            var channel = new HidppHapticsTests.FakeChannel
            {
                Path = Receiver.Path,
                Answer = (d, f, fn, p) => d == 1 ? mouse(d, f, fn, p) : HidppHapticsTests.NoAnswer(),
                // Bolt: slot n's record at 0x50 + n. Only slot 1 is paired.
                Register = (r, s) => r == 0xB5 && s == 0x51
                    ? new HidppReply(HidppReplyKind.Answer)
                    : new HidppReply(HidppReplyKind.Error, 0x03),
            };
            using var backend = new HidppBackend(() => new[] { Receiver, shortCollection },
                (c, s) => { openedWith = s; return channel; }, _ => container, 1, 5);
            backend.Start();

            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
            Assert.Same(shortCollection, openedWith);
            // Every other slot is empty, so nothing keeps the container open.
            Assert.DoesNotContain(container, backend.Snapshot.PendingContainers);
            // Neither 0xFF nor an empty slot was ever asked.
            Assert.All(channel.Asked, a => Assert.Equal(1, a.Device));
        }

        /// <summary>The worker's first scan of a quiet receiver sends what
        /// the probe sends (<see cref="HidppUnitProbeTests.QuietProbe"/>),
        /// then reads the one battery it found, and nothing else: no frame
        /// is written, and nothing is asked twice. The traffic is read once
        /// the scan has published its units, which is after its last
        /// request and five seconds before its next one.</summary>
        [Fact]
        public void AQuietScan_SendsTheProbesRequestsAndOneBatteryRead_AndWritesNothing()
        {
            var shortCollection = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#bench",
                VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = 0x0001, InputReportLength = 7,
            };
            var channel = HidppUnitProbeTests.QuietReceiver(Receiver.Path);
            using var backend = new HidppBackend(() => new[] { Receiver, shortCollection }, (c, s) => channel,
                _ => Guid.NewGuid(), 1, 5);
            backend.Start();

            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 2, 30000), "the scan found both mice");
            var traffic = HidppHapticsTests.Lines(channel.Traffic);
            Assert.Equal(HidppUnitProbeTests.QuietProbe.Append("ask 11 02 08 1C").ToArray(), traffic);
            Assert.Empty(channel.Writes);
            Assert.Equal(55, backend.Snapshot.Units.Single(u => u.DeviceIndex == 2).BatteryPercent);
        }

        /// <summary>A receiver with a haptic mouse in slot 1 and no pairing
        /// table, so slots 2 to 6 keep a scan due every five seconds.</summary>
        private static (HidppBackend Backend, HidppHapticsTests.FakeChannel Channel) MouseBench(
            int activeTickMs = 1, int idleTickMs = 5, Func<string, Guid> containerOf = null,
            Func<IReadOnlyList<VendorHidCollection>> enumerate = null,
            Func<byte, byte, byte, byte[], HidppReply> answer = null,
            int predecessorJoinMs = HidppBackend.PredecessorJoinMs)
        {
            var mouse = HidppHapticsTests.Mouse(1);
            var channel = new HidppHapticsTests.FakeChannel
            {
                Path = Receiver.Path,
                Answer = answer ?? ((d, f, fn, p) => d == 1 ? mouse(d, f, fn, p) : HidppHapticsTests.NoAnswer()),
            };
            var container = Guid.NewGuid();
            var backend = new HidppBackend(enumerate ?? (() => new[] { Receiver }), (c, s) => channel,
                containerOf ?? (_ => container), activeTickMs, idleTickMs, predecessorJoinMs: predecessorJoinMs);
            return (backend, channel);
        }

        /// <summary>While a unit is linked for rumble, a scan that comes due
        /// during play waits for a second with nothing playing, so a probe
        /// does not hold up rumble that comes in bursts. The first build's
        /// gate, which a scan on the first silent tick had replaced. The
        /// test reads how long the worker had seen nothing play when the
        /// scan started, where a window timed from here would close early
        /// on a busy bench.</summary>
        [Fact]
        public void AScanWaitsForAQuietSecond_WhileAUnitIsLinked()
        {
            var (backend, _) = MouseBench();
            using (backend)
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
                var unit = backend.Snapshot.Units[0];
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = device, Haptics = new[] { new OutputPath(OutputFamily.HidppUnit, unit.Key) } },
                }));

                // Rumble holds past the next scan's due time, five seconds
                // after the first scan, and no scan runs while it plays.
                int scans = backend.ScanCount;
                PeripheralOutputs.SetMotors(device, 65535, 0);
                Thread.Sleep(HidppBackend.FirstScanMs + 500);
                Assert.Equal(scans, backend.ScanCount);

                // The due scan runs once the rumble stops, and only with a
                // quiet second behind it.
                PeripheralOutputs.SetMotors(device, 0, 0);
                Assert.True(SpinWait.SpinUntil(() => backend.ScanCount > scans, 10000),
                    "the due scan never ran after the rumble stopped");
                Assert.True(backend.ScanQuietMs >= HidppBackend.QuietMs,
                    $"the due scan ran {backend.ScanQuietMs} ms after the last pass that played");
            }
        }

        /// <summary>A unit linked for rumble with no level waits the idle
        /// tick, since a level that starts wakes the worker, and the fast
        /// tick comes back with the rumble. The test reads the wait the
        /// worker asks for and times nothing, so a busy bench cannot fail
        /// it.</summary>
        [Fact]
        public void ASilentLinkedUnitWaitsTheIdleTick_AndRumbleBringsTheFastTickBack()
        {
            const int activeTickMs = 10, idleTickMs = 200;
            var (backend, channel) = MouseBench(activeTickMs, idleTickMs);
            using (backend)
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
                var unit = backend.Snapshot.Units[0];
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks { Device = device, Haptics = new[] { new OutputPath(OutputFamily.HidppUnit, unit.Key) } },
                }));
                Assert.Equal(idleTickMs, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));

                while (channel.Writes.TryDequeue(out _)) { }
                PeripheralOutputs.SetMotors(device, 65535, 0);
                Assert.Equal(activeTickMs, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
                // The pulse shows the row is linked to the unit, so the idle
                // wait above was a linked unit's and not an unlinked one's.
                Assert.False(channel.Writes.IsEmpty, "a full pass with the level up played no pulse");

                PeripheralOutputs.SetMotors(device, 0, 0);
                Assert.Equal(idleTickMs, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
            }
        }

        /// <summary>A Stop whose join times out leaves the worker inside a
        /// probe. A Start after it runs one worker: the old one reads its own
        /// stop when it comes back and ends, and the new one waits for it
        /// before it opens the collections.</summary>
        [Fact]
        public void ARestartAfterATimedOutStop_RunsOneWorker()
        {
            using var gate = new ManualResetEventSlim(false);
            Thread first = null;
            int requests = 0, overlapped = 0;
            var mouse = HidppHapticsTests.Mouse(1);
            var (backend, _) = MouseBench(
                enumerate: () =>
                {
                    // The first scan names its worker. A scan another thread
                    // makes while that worker lives shares its collections.
                    var me = Thread.CurrentThread;
                    var one = Interlocked.CompareExchange(ref first, me, null) ?? me;
                    if (one != me && one.IsAlive) Volatile.Write(ref overlapped, 1);
                    return new[] { Receiver };
                },
                answer: (d, f, fn, p) =>
                {
                    if (Interlocked.Increment(ref requests) == 1) gate.Wait(10000);
                    return d == 1 ? mouse(d, f, fn, p) : HidppHapticsTests.NoAnswer();
                });
            try
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref requests) >= 1, 3000), "the first request is held");
                Assert.NotNull(first);
                backend.Stop();
                Assert.True(first.IsAlive, "the Stop's join gave up with the worker inside its probe");
                backend.Start();
                // The new worker scans nothing while the old one is held.
                Thread.Sleep(300);
                Assert.Equal(0, Volatile.Read(ref overlapped));
                gate.Set();

                Assert.True(first.Join(3000), "the first worker came back to life after the restart");
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the new worker found the mouse");
                Assert.Equal(0, Volatile.Read(ref overlapped));
            }
            finally
            {
                gate.Set();
                backend.Dispose();
            }
        }

        /// <summary>A predecessor wedged inside a probe holds the next worker
        /// only for its bounded wait: the new worker then says so and goes
        /// on, instead of waiting for a thread that may never come back.</summary>
        [Fact]
        public void AWedgedPredecessor_HoldsTheNextWorkerOnlyForItsBoundedWait()
        {
            using var gate = new ManualResetEventSlim(false);
            int requests = 0;
            var (wedged, _) = MouseBench(answer: (d, f, fn, p) =>
            {
                if (Interlocked.Increment(ref requests) == 1) gate.Wait(10000);
                return HidppHapticsTests.NoAnswer();
            });
            var (next, _) = MouseBench(predecessorJoinMs: 200);
            static int GaveUp() => SdlDiagLog.Snapshot().Split('\n')
                .Count(l => l.Contains("PERIPHERAL HID++ previous worker still running after 200 ms"));
            try
            {
                wedged.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref requests) >= 1, 3000), "the first worker is held in its probe");
                int before = GaveUp();
                next.Start();
                Assert.True(SpinWait.SpinUntil(() => next.Snapshot.Units.Count == 1, 5000), "the new worker went on and found the mouse");
                Assert.True(wedged.WorkerAlive, "the predecessor was still inside its probe");
                Assert.Equal(before + 1, GaveUp());
            }
            finally
            {
                gate.Set();
                next.Dispose();
                wedged.Dispose();
            }
        }

        /// <summary>A Dispose whose Stop gave up its join releases the wake
        /// under a worker still inside a probe. The worker ends quietly when
        /// it comes back, without logging the fault a real one would.</summary>
        [Fact]
        public void ADisposeThatOutlastsTheJoin_LogsNoFault()
        {
            using var gate = new ManualResetEventSlim(false);
            int requests = 0;
            var (backend, channel) = MouseBench(answer: (d, f, fn, p) =>
            {
                if (Interlocked.Increment(ref requests) == 1) gate.Wait(10000);
                return HidppHapticsTests.NoAnswer();
            });
            static int Faults() => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains("PERIPHERAL HID++ worker fault"));
            try
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref requests) >= 1, 3000), "the first request is held");
                int faults = Faults();
                backend.Dispose();
                gate.Set();
                Assert.True(SpinWait.SpinUntil(() => channel.Disposed, 5000), "the worker ended and closed its channel");
                Assert.Equal(faults, Faults());
            }
            finally
            {
                gate.Set();
                backend.Dispose();
            }
        }

        /// <summary>A container lookup that fails for a present collection is
        /// asked again at the next scan, and the units already found there
        /// take the container, rather than standing unmatched for as long as
        /// the collection stays.</summary>
        [Fact]
        public void AFailedContainerLookup_IsAskedAgainAtTheNextScan()
        {
            var container = Guid.NewGuid();
            int lookups = 0;
            var (backend, _) = MouseBench(containerOf: _ => Interlocked.Increment(ref lookups) == 1 ? Guid.Empty : container);
            using (backend)
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
                Assert.Equal(Guid.Empty, backend.Snapshot.Units[0].ContainerId);
                Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1
                    && backend.Snapshot.Units[0].ContainerId == container, HidppBackend.FirstScanMs + 3000),
                    "the next scan asked again and the unit took its container");
                Assert.Contains(container, backend.Snapshot.PendingContainers);
            }
        }

        /// <summary>A Bluetooth LE Logitech device has no receiver, and its
        /// HID++ collection sits on usage page 0xFF43 usage 0x0202. The scan
        /// finds it there, as the device itself.</summary>
        [Fact]
        public void ABluetoothLeDeviceOnPage0xFF43_IsFoundAsTheDeviceItself()
        {
            var ble = new VendorHidCollection
            {
                Path = @"\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&02046d_pid&b042&col03#9&1&0002#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                VendorId = 0x046D, ProductId = 0xB042, UsagePage = 0xFF43, Usage = 0x0202, InputReportLength = 20,
            };
            var channel = new HidppHapticsTests.FakeChannel { Path = ble.Path, Bluetooth = true, Answer = HidppHapticsTests.Mouse(0xFF) };
            using var backend = new HidppBackend(() => new[] { ble }, (c, s) => channel, _ => Guid.NewGuid(), 1, 5);
            backend.Start();

            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
            Assert.Equal(HidppHapticProtocol.DirectDeviceIndex, backend.Snapshot.Units[0].DeviceIndex);
            Assert.All(channel.Asked, a => Assert.Equal(HidppHapticProtocol.DirectDeviceIndex, a.Device));
        }
    }

    /// <summary>The GameSense worker binds while a tactile Rival is
    /// assigned and hands the mice back to GG when none is (#494).</summary>
    [Collection("PeripheralOutputStatics")]
    public class GameSenseBackendTests : IDisposable
    {
        public GameSenseBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        /// <summary>The worker reads this test's clock and works the ruling
        /// Rival out at every look, so the release delay runs out when the
        /// test says so and nothing here is timed. "At once" was a second by
        /// the wall clock, and the check that GG still held the mice passed
        /// only while this thread reached it inside the release delay.</summary>
        [Fact]
        public void TheRulingRivalsLevelPlays_AndTheMiceGoBackToGGWhenNoneIsAssigned()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var rival = Guid.NewGuid();
            int player = 1;
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 10000),
                _ => Volatile.Read(ref player), 2, rulerMs: 0, clock: () => Volatile.Read(ref now));
            backend.Start();

            PeripheralOutputs.SetMotors(rival, 65535, 65535);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().LastOrDefault() > 0, 5000), "the level was posted");
            Assert.Equal(new[] { "/game_metadata", "/bind_game_event" }, server.Paths().Take(2));
            // The worker reports the state once the level's post returns, and
            // the server records the post before it answers.
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile) == BackendState.Connected, 5000));

            // The Rival leaves its controller: the motor stops at once, with
            // none of the release delay gone.
            Volatile.Write(ref player, 0);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().Last() == 0, 5000), "the motor stopped at once");
            // GG takes the mice back only after the release delay: one tick
            // short of it a whole look has passed and GG still holds them.
            Volatile.Write(ref now, now + GameSenseBackend.ReleaseMs - 1);
            WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
            Assert.DoesNotContain("/stop_game", server.Paths());
            Volatile.Write(ref now, now + 1);
            Assert.True(SpinWait.SpinUntil(() => server.Paths().Contains("/stop_game"), 5000), "the mice went back to GG");
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile) == BackendState.Idle, 5000));
        }

        [Fact]
        public void AnAbnormalExitWaitsForGGToHearTheZero()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var rival = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 10000), _ => 1, 2);
            backend.Start();
            PeripheralOutputs.SetMotors(rival, 65535, 0);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().LastOrDefault() > 0, 5000), "the level was posted");

            // The engine's quiesce zeroes the level, then the panic path waits.
            PeripheralOutputs.SilenceHaptics();
            Assert.True(backend.WaitForSilence(5000), "GG never heard the zero");
            Assert.Equal(0, server.EventValues().Last());
        }

        /// <summary>The crash wait never passes a level on its way to GG. A
        /// look that read its level before the engine's quiesce zeroed it
        /// still posts that level, so the wait holds until that look has
        /// ended and a later one has posted the zero. The look here is held
        /// with the level in hand, read and not yet posted, which is where a
        /// wait that looked only at the last posted level took the silence
        /// before it for GG's.</summary>
        [Fact]
        public void AnAbnormalExitHoldsForALookThatReadItsLevelBeforeTheQuiesce()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var rival = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            using var read = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int held = 0;
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 10000), _ => 1, 2,
                levelOf: device =>
                {
                    float level = PeripheralOutputs.AmplitudeOf(device);
                    if (level != 0f && Interlocked.Exchange(ref held, 1) == 0)
                    {
                        read.Set();
                        release.Wait(10000);
                    }
                    return level;
                });
            try
            {
                backend.Start();
                // GG is bound and has been told no level.
                Assert.True(SpinWait.SpinUntil(
                    () => PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile) == BackendState.Connected, 5000),
                    "GG was bound");
                Assert.Empty(server.EventValues());

                PeripheralOutputs.SetMotors(rival, 65535, 0);
                Assert.True(read.Wait(5000), "a look read the level");
                PeripheralOutputs.SilenceHaptics();
                Assert.False(backend.WaitForSilence(200), "the wait passed a level on its way to GG");

                release.Set();
                Assert.True(backend.WaitForSilence(5000), "GG never heard the zero");
                // The held look posted its level, and a later one the zero.
                Assert.Equal(new[] { GameSenseClient.LevelValue(3), 0 }, server.EventValues());
            }
            finally { release.Set(); }
        }

        /// <summary>A look that reads no level holds no crash wait. With only
        /// a color claimed, the wait is over at once while the look sits
        /// inside a call to GG, since nothing it posts can start a motor, so
        /// a GG slow to answer a color costs the crash path nothing.</summary>
        [Fact]
        public void AnAbnormalExitDoesNotWaitOnALookThatReadsNoLevel()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int posts = 0;
            server.BeforeAnswer = _ =>
            {
                if (Interlocked.Increment(ref posts) != 1) return;
                held.Set();
                release.Wait(10000);
            };
            string coreProps = server.WriteCoreProps();
            var device = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = device, Lighting = new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") } },
            }));
            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 1, 2, 3);
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 15000), _ => 0, 2);
            try
            {
                backend.Start();
                Assert.True(held.Wait(5000), "the look is held inside its first call to GG");
                Assert.True(backend.WaitForSilence(0), "the wait sat on a look that holds no level");
            }
            finally { release.Set(); }
        }

        /// <summary>Identify on a Rival no controller holds plays its pulses
        /// through GG, and GG gets the mice back after the train. The ruler
        /// is worked out again the moment Identify starts: with the cadence
        /// pushed out to a minute here, only that look can find it.</summary>
        [Fact]
        public void AnUnassignedRivalBuzzesForIdentify_AndGoesBackToGGAfter()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var rival = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            // Every row ranks 0, the rank of a Rival on no controller.
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 10000), _ => 0, 2, rulerMs: 60000);
            backend.Start();
            Assert.True(SpinWait.SpinUntil(() => backend.PassCount > 2, 2000), "the worker made its first looks");

            PeripheralOutputs.SetMotors(rival, 65535, 65535);
            PeripheralOutputs.BeginIdentify(rival, 60000);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().LastOrDefault() > 0, 5000), "Identify's level was posted");

            PeripheralOutputs.SetMotors(rival, 0, 0);
            PeripheralOutputs.EndIdentify(rival);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().Last() == 0, 2000), "the motor stopped");
            Assert.True(SpinWait.SpinUntil(() => server.Paths().Contains("/stop_game"),
                GameSenseBackend.ReleaseMs + 3000), "the mice went back to GG");
        }

        /// <summary>With nothing claimed and GG let go the worker waits the
        /// idle tick, and a claimed color brings its fast tick back. The
        /// test reads the wait the worker asks for and times nothing, so a
        /// busy bench cannot fail it.</summary>
        [Fact]
        public void WithNothingClaimed_TheWorkerWaitsTheIdleTick()
        {
            const int tickMs = 20;
            using var backend = new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, tickMs);
            backend.Start();
            Assert.Equal(GameSenseBackend.IdleTickMs,
                WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));

            // A claimed color with GG absent keeps the fast tick while the
            // worker waits to connect.
            var device = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = device, Lighting = new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") } },
            }));
            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 1, 2, 3);
            Assert.Equal(tickMs, WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));

            PeripheralOutputs.ReleaseLighting(device, 0);
            Assert.Equal(GameSenseBackend.IdleTickMs,
                WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs));
        }

        /// <summary>A Stop whose join times out leaves the worker inside its
        /// client factory. A Start after it runs one worker: the old one reads
        /// its own stop when it comes back and never connects.</summary>
        [Fact]
        public void ARestartAfterATimedOutStop_ConnectsOnce()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            using var gate = new ManualResetEventSlim(false);
            int clients = 0;
            var rival = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            PeripheralOutputs.SetMotors(rival, 65535, 0);
            var backend = new GameSenseBackend(() =>
            {
                if (Interlocked.Increment(ref clients) == 1) gate.Wait(10000);
                return new GameSenseClient(coreProps, 10000);
            }, _ => 1, 2);
            try
            {
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref clients) >= 1, 3000), "the first worker is held");
                backend.Stop();
                backend.Start();
                Assert.True(SpinWait.SpinUntil(() => server.Paths().Count(p => p == "/game_metadata") >= 1, 5000),
                    "the new worker connected");
                gate.Set();
                Thread.Sleep(1000);
                Assert.Equal(1, server.Paths().Count(p => p == "/game_metadata"));
            }
            finally
            {
                gate.Set();
                backend.Dispose();
                PeripheralOutputs.SetMotors(rival, 0, 0);
            }
        }

        /// <summary>A Dispose whose Stop gave up its join releases the wake
        /// under a worker still inside a look. The worker ends quietly when
        /// it comes back, without logging the fault a real one would.</summary>
        [Fact]
        public void ADisposeThatOutlastsTheJoin_LogsNoFault()
        {
            using var gate = new ManualResetEventSlim(false);
            using var held = new ManualResetEventSlim(false);
            var rival = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            Thread worker = null;
            int asked = 0;
            var backend = new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100),
                _ =>
                {
                    // The worker's first look at the ranks is held.
                    if (Interlocked.Increment(ref asked) == 1)
                    {
                        worker = Thread.CurrentThread;
                        held.Set();
                        gate.Wait(10000);
                    }
                    return 0;
                }, 2);
            static int Faults() => SdlDiagLog.Snapshot().Split('\n').Count(l => l.Contains("PERIPHERAL GameSense worker fault"));
            try
            {
                backend.Start();
                Assert.True(held.Wait(3000), "the worker is held inside its look");
                int faults = Faults();
                backend.Dispose();
                gate.Set();
                Assert.True(worker.Join(3000), "the worker ended");
                Assert.Equal(faults, Faults());
            }
            finally
            {
                gate.Set();
                backend.Dispose();
            }
        }
    }

    /// <summary>The host's link pass and its Razer Sensa worker against
    /// scripted backends and a scripted device list (#494).</summary>
    [Collection("PeripheralOutputStatics")]
    public class PeripheralOutputHostTests : IDisposable
    {
        private static readonly Guid Sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly List<PeripheralOutputRow> _rows = new();

        public PeripheralOutputHostTests()
        {
            PeripheralOutputs.ResetForTest();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            PeripheralOutputHost.BeforeGate = null;
            foreach (var row in _rows) row.Dispose();
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
        }

        /// <summary>The Razer Sensa row online, on no controller.</summary>
        private void AddSensaRow()
        {
            var row = new PeripheralOutputRow(PeripheralRowKind.RazerSensa);
            _rows.Add(row);
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(row);
            ud.IsOnline = true;
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
        }

        /// <summary>A host on backends that find nothing and reach no vendor,
        /// with the Sensa engine's platform present and a factory that counts
        /// the workers it makes, none of which loads the engine.</summary>
        private static PeripheralOutputHost Host(Func<SensaHapticsService> sensa, int stopJoinMs = PeripheralOutputHost.StopJoinMs,
            Func<string, Guid> containerOf = null, HidppBackend hidpp = null, int linkMs = PeripheralOutputHost.LinkMs)
            => new(hidpp ?? new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                sensa, containerOf: containerOf ?? (_ => Guid.Empty),
                readPresence: () => PeripheralPresence.None with { SensaPlatform = true },
                stopJoinMs: stopJoinMs, linkMs: linkMs);

        private static Func<SensaHapticsService> CountingSensa(Action made)
            => () =>
            {
                made();
                return new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false };
            };

        /// <summary>An HID++ worker handed no enumeration, which never
        /// publishes. It neither nudges the host nor changes what the tabs
        /// read, so a pass or a refresh in a test that uses it has the
        /// test's own step for its only cause.</summary>
        private static HidppBackend SilentHidpp()
            => new(() => null, (c, s) => null, _ => Guid.Empty, 1, 5);

        /// <summary>Waits until a whole link pass has run after this call.
        /// The host counts a pass as it ends, so the second count from here
        /// belongs to a pass that also began after the call. The limit only
        /// catches a loop that makes no pass at all.</summary>
        private static void AWholePass(PeripheralOutputHost host)
        {
            int seen = host.PassCount;
            Assert.True(SpinWait.SpinUntil(() => host.PassCount >= seen + 2, 10000), "the link loop made no pass");
        }

        /// <summary>Identify on the Razer Sensa row, assigned to no
        /// controller, starts its worker for the train, and the row with no
        /// Identify starts none.</summary>
        [Fact]
        public void IdentifyRunsTheSensaWorker_ForARowNoControllerHolds()
        {
            AddSensaRow();
            int made = 0;
            using var host = Host(CountingSensa(() => Interlocked.Increment(ref made)));
            host.Start();
            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.HasHaptics(Sensa), 3000), "the Sensa row linked");
            // A whole pass with the row linked and no Identify.
            AWholePass(host);
            Assert.Equal(0, Volatile.Read(ref made));

            PeripheralOutputs.BeginIdentify(Sensa, 60000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref made) >= 1, 10000), "Identify started the Sensa worker");
            PeripheralOutputs.EndIdentify(Sensa);
        }

        /// <summary>Identify starts the Sensa worker at once: it nudges the
        /// link pass instead of leaving the worker to the next timed pass,
        /// which would spend the opening pulses of a train 1.3 seconds long.
        /// Here the loop is at rest after its first pass with the next timed
        /// one ten minutes away, and the HID++ worker, given no enumeration,
        /// never publishes, so only Identify's nudge can bring the pass that
        /// starts the worker. Nothing is timed against the half second
        /// between passes.</summary>
        [Fact]
        public void IdentifyStartsTheSensaWorkerAtOnce_NotAtTheNextTimedPass()
        {
            AddSensaRow();
            int made = 0;
            using var host = Host(CountingSensa(() => Interlocked.Increment(ref made)), hidpp: SilentHidpp(), linkMs: 600000);
            host.Start();
            Assert.True(SpinWait.SpinUntil(() => host.PassCount >= 1, 5000), "the first pass ended");
            Assert.True(PeripheralOutputs.HasHaptics(Sensa), "the Sensa row linked");
            Assert.Equal(0, Volatile.Read(ref made));

            PeripheralOutputs.BeginIdentify(Sensa, 60000);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref made) >= 1, 10000),
                "Identify waited for the next timed pass");
            PeripheralOutputs.EndIdentify(Sensa);
        }

        /// <summary>A link pass Stop's join gave up on publishes nothing and
        /// starts no Sensa worker when it comes back, whether it was held
        /// before its publish or before the worker's start, so Stop's own
        /// teardown, an empty table and no worker, has the last word.</summary>
        [Theory]
        [InlineData("publish")]
        [InlineData("sensa")]
        public void AStopThatOutlivesALinkPass_PublishesNothingAndStartsNoWorker(string holdAt)
        {
            AddSensaRow();
            PeripheralOutputs.BeginIdentify(Sensa, 60000);
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int holds = 0;
            PeripheralOutputHost.BeforeGate = point =>
            {
                if (point != holdAt || Interlocked.Increment(ref holds) != 1) return;
                held.Set();
                release.Wait(10000);
            };
            int made = 0;
            using var host = Host(CountingSensa(() => Interlocked.Increment(ref made)), stopJoinMs: 200);
            try
            {
                host.Start();
                Assert.True(held.Wait(3000), "the first pass reached " + holdAt);
                host.Stop();
                int ended = host.PassCount;
                release.Set();
                // What the held pass left is read only once it has come back
                // and ended. Read sooner, a pass still on its way would show
                // nothing whatever it was about to do.
                Assert.True(SpinWait.SpinUntil(() => host.PassCount > ended, 10000), "the held pass never came back");

                Assert.Empty(PeripheralOutputs.Links.ByDevice);
                Assert.Equal(0, Volatile.Read(ref made));
            }
            finally
            {
                release.Set();
                PeripheralOutputs.EndIdentify(Sensa);
            }
        }

        /// <summary>A row's container lookup that fails is asked again on the
        /// next pass, so the row links to its HID++ unit once the lookup
        /// answers, instead of standing unmatched for the rest of the engine
        /// run.</summary>
        [Fact]
        public void AFailedContainerLookup_IsAskedAgain_AndTheRowLinks()
        {
            var container = Guid.NewGuid();
            var receiver = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#bench",
                VendorId = 0x046D, ProductId = 0xC548, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
            };
            var channel = new HidppHapticsTests.FakeChannel { Path = receiver.Path, Answer = HidppHapticsTests.Mouse(1) };
            var hidpp = new HidppBackend(() => new[] { receiver }, (c, s) => channel, _ => container, 1, 5);
            var mouse = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), IsOnline = true, CapType = InputDeviceType.Mouse,
                VendorId = PeripheralLinker.LogitechVid, ProdId = 0xC548, DevicePath = @"\\?\hid#vid_046d&pid_c548&mi_01#row",
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(mouse);
            int lookups = 0;
            using var host = Host(CountingSensa(() => { }), hidpp: hidpp,
                containerOf: _ => Interlocked.Increment(ref lookups) <= 2 ? Guid.Empty : container);
            host.Start();

            Assert.True(SpinWait.SpinUntil(() => PeripheralOutputs.HasHaptics(mouse.InstanceGuid), 5000),
                $"the row linked to its unit after {Volatile.Read(ref lookups)} lookups");
            Assert.True(Volatile.Read(ref lookups) >= 3);
        }

        /// <summary>A row that goes offline leaves the cache, so when it comes
        /// back its container is looked up again, the way the HID++ worker
        /// forgets a collection that leaves its enumeration.</summary>
        [Fact]
        public void ARowThatLeavesAndComesBack_IsLookedUpAgain()
        {
            var mouse = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), IsOnline = true, CapType = InputDeviceType.Mouse,
                VendorId = PeripheralLinker.LogitechVid, ProdId = 0xC548, DevicePath = @"\\?\hid#vid_046d&pid_c548&mi_01#row",
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(mouse);
            int lookups = 0;
            var container = Guid.NewGuid();
            using var host = Host(CountingSensa(() => { }), containerOf: _ => { Interlocked.Increment(ref lookups); return container; });
            host.Start();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref lookups) == 1, 3000), "the row was looked up");
            // A whole pass with the row still there asks nothing more.
            AWholePass(host);
            Assert.Equal(1, Volatile.Read(ref lookups));

            // A whole pass with the row away forgets its path.
            mouse.IsOnline = false;
            AWholePass(host);
            mouse.IsOnline = true;
            host.Nudge();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref lookups) == 2, 10000), "the returning row was looked up again");
        }

        /// <summary>A Stop whose join gives up on a held pass, then a Start,
        /// runs one link loop: the old one reads its own stop when it comes
        /// back and makes no pass after it.</summary>
        [Fact]
        public void ARestartAfterATimedOutStop_RunsOneLinkLoop()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var passes = new System.Collections.Concurrent.ConcurrentQueue<int>();
            int holds = 0;
            int first = -1;
            Thread firstLoop = null;
            PeripheralOutputHost.BeforeGate = point =>
            {
                if (point != "publish") return;
                passes.Enqueue(Environment.CurrentManagedThreadId);
                if (Interlocked.Increment(ref holds) != 1) return;
                first = Environment.CurrentManagedThreadId;
                firstLoop = Thread.CurrentThread;
                held.Set();
                release.Wait(10000);
            };
            using var host = Host(CountingSensa(() => { }), stopJoinMs: 200);
            try
            {
                host.Start();
                Assert.True(held.Wait(3000), "the first pass reached its publish");
                host.Stop();
                host.Start();
                release.Set();
                // The old loop is gone before its passes are counted, so
                // none can still be on its way, and the new one has passed.
                Assert.True(firstLoop.Join(10000), "the old loop never ended");
                Assert.True(SpinWait.SpinUntil(() => passes.Any(id => id != first), 10000), "the new loop made no pass");
                Assert.Equal(1, passes.Count(id => id == first));
            }
            finally { release.Set(); }
        }

        /// <summary>The cache keeps a container once found, keeps no failed
        /// lookup, and forgets a path no row used.</summary>
        [Fact]
        public void TheContainerCache_KeepsWhatAnswered_AndForgetsWhatLeft()
        {
            var cache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var found = Guid.NewGuid();
            Guid answer = Guid.Empty;
            int asked = 0;
            Guid Lookup(string p) { asked++; return answer; }

            Assert.Equal(Guid.Empty, PeripheralOutputHost.ResolveContainer(cache, "row", Lookup));
            answer = found;
            Assert.Equal(found, PeripheralOutputHost.ResolveContainer(cache, "row", Lookup));
            Assert.Equal(found, PeripheralOutputHost.ResolveContainer(cache, "ROW", Lookup));
            Assert.Equal(2, asked);
            Assert.Equal(Guid.Empty, PeripheralOutputHost.ResolveContainer(cache, "other", _ => throw new InvalidOperationException()));
            Assert.False(cache.ContainsKey("other"));

            PeripheralOutputHost.ForgetContainers(cache, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "row" });
            Assert.True(cache.ContainsKey("row"));
            PeripheralOutputHost.ForgetContainers(cache, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Assert.Empty(cache);
        }

        /// <summary>A slot whose controller comes or goes changes what the
        /// tabs say even where no claim was pruned, so the pass refreshes them
        /// when a slot's liveness flips, and the tabs read the same rule the
        /// prune does.</summary>
        [Fact]
        public void ASlotThatComesOrGoes_RefreshesTheTabs()
        {
            var live = new bool[InputManager.MaxPads];
            using var host = Host(CountingSensa(() => { }), hidpp: SilentHidpp());
            host.SlotHasController = slot => Volatile.Read(ref live[slot]);
            Assert.False(PeripheralOutputs.Running);
            host.Start();
            Assert.True(PeripheralOutputs.Running, "a started host says the engine runs, so the route lines show");
            Assert.False(PeripheralOutputs.SlotLive(3));
            // A whole pass has recorded which slots are live.
            AWholePass(host);

            int raised = 0;
            void OnStatus() => Interlocked.Increment(ref raised);
            PeripheralOutputs.StatusChanged += OnStatus;
            try
            {
                // A whole pass with nothing changed refreshes nothing.
                AWholePass(host);
                Assert.Equal(0, Volatile.Read(ref raised));

                Volatile.Write(ref live[3], true);
                Assert.True(PeripheralOutputs.SlotLive(3));
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref raised) >= 1, 10000),
                    "a slot that came refreshed the tabs");
            }
            finally { PeripheralOutputs.StatusChanged -= OnStatus; }

            host.Stop();
            Assert.False(PeripheralOutputs.Running);
            Assert.False(PeripheralOutputs.SlotLive(3));
        }
    }

    // ─────────────────────────────────────────────
    //  The shipped switch becomes an assignment
    // ─────────────────────────────────────────────

    /// <summary>The Razer Sensa switch (#374) becomes an assignment of the
    /// Razer Sensa row wherever it was on (#494).</summary>
    [Collection("SettingsManagerStatics")]
    public class PeripheralSwitchMigrationTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private static readonly Guid Sensa = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);

        public PeripheralSwitchMigrationTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static PeripheralSwitchMigration.LegacySwitch Switch(bool global)
            => new(PeripheralRowKind.RazerSensa, global, p => p.EnableSensaHaptics, p => p.EnableSensaHaptics = null);

        /// <summary>A profile with two created slots, 1 an Xbox controller
        /// and 3 a PlayStation one, so the Xbox group shows first.</summary>
        private static ProfileData Profile(bool? opinion, string name = "p") => new()
        {
            Name = name,
            EnableSensaHaptics = opinion,
            SlotCreated = new[] { false, true, false, true, false, false, false, false },
            SlotControllerTypes = new[] { 0, (int)VirtualControllerType.Xbox, 0, (int)VirtualControllerType.PlayStation, 0, 0, 0, 0 },
            XboxSlotOrder = new[] { 1 },
            PlayStationSlotOrder = new[] { 3 },
        };

        private static PadSetting Fresh(UserDevice ud, int slot) => new();

        [Fact]
        public void TheRulingSlotIsTheFirstCreatedOneInDisplayOrder()
        {
            var created = new[] { true, true, true, false };
            IReadOnlyList<int> Order(VirtualControllerType type) => type switch
            {
                VirtualControllerType.Xbox => new[] { 3, 2 },
                VirtualControllerType.PlayStation => new[] { 0 },
                _ => null,
            };
            // Slot 3 is not created, so the Xbox group's first is slot 2,
            // and the Xbox group shows before the PlayStation one.
            Assert.Equal(2, PeripheralSwitchMigration.FirstDisplayedSlot(created, Order));
            Assert.Equal(-1, PeripheralSwitchMigration.FirstDisplayedSlot(new[] { false, false }, Order));
            Assert.Equal(-1, PeripheralSwitchMigration.FirstDisplayedSlot(null, Order));
            Assert.Equal(1, PeripheralSwitchMigration.FirstDisplayedSlot(Profile(null)));
        }

        [Fact]
        public void AProfileGainsOneEntryOnItsRulingSlot()
        {
            var profile = Profile(true);
            Assert.True(PeripheralSwitchMigration.AddToProfile(profile, PeripheralRowKind.RazerSensa));
            var entry = Assert.Single(profile.Entries);
            Assert.Equal(Sensa, entry.InstanceGuid);
            Assert.Equal(1, entry.MapTo);
            Assert.Contains(profile.PadSettings, ps => ps.PadSettingChecksum == entry.PadSettingChecksum);

            Assert.False(PeripheralSwitchMigration.AddToProfile(profile, PeripheralRowKind.RazerSensa));
            Assert.Single(profile.Entries);

            Assert.False(PeripheralSwitchMigration.AddToProfile(new ProfileData { SlotCreated = new bool[8] },
                PeripheralRowKind.RazerSensa));
        }

        [Fact]
        public void EachStoredProfileFollowsItsOwnValue_AndEveryOpinionIsCleared()
        {
            var on = Profile(true, "on");
            var off = Profile(false, "off");
            var silent = Profile(null, "silent");

            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: false) },
                new List<ProfileData> { on, off, silent }, null, null, _ => -1, Fresh));

            Assert.Single(on.Entries);
            Assert.Null(off.Entries);
            Assert.Null(silent.Entries);
            Assert.All(new[] { on, off, silent }, p => Assert.Null(p.EnableSensaHaptics));
            Assert.Empty(SettingsManager.UserSettings.Items);

            // A global switch that was on reaches the profile with no opinion.
            silent.EnableSensaHaptics = null;
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData> { silent }, null, null, _ => -1, Fresh));
            Assert.Single(silent.Entries);
        }

        [Fact]
        public void TheLiveSettingsFollowTheActiveProfilesOwnValue()
        {
            // On globally, off in the active profile: the live slot stays bare.
            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), Profile(false, "active"), null, _ => 1, Fresh));
            Assert.Empty(SettingsManager.UserSettings.Items);

            // Off globally, on in the active profile: the live slot takes the row.
            var active = Profile(true, "active");
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: false) },
                new List<ProfileData>(), active, null, _ => 1, Fresh));
            var us = Assert.Single(SettingsManager.UserSettings.Items);
            Assert.Equal(Sensa, us.InstanceGuid);
            Assert.Equal(1, us.MapTo);
            Assert.NotNull(us.GetPadSetting());
        }

        [Fact]
        public void TheDefaultTopologyTakesTheGlobalValue_OnceAndOffline()
        {
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, _ => 0, Fresh));

            var record = Assert.Single(SettingsManager.UserDevices.Items);
            Assert.Equal(Sensa, record.InstanceGuid);
            Assert.False(record.IsOnline);
            Assert.Equal(InputDeviceType.PeripheralHaptics, record.CapType);
            Assert.True(record.HasPeripheralHaptics);
            Assert.Equal(0, Assert.Single(SettingsManager.UserSettings.Items).MapTo);

            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, _ => 0, Fresh));
            Assert.Single(SettingsManager.UserSettings.Items);

            // No controller: nothing to assign.
            SettingsManager.UserSettings = new SettingsCollection();
            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, _ => -1, Fresh));
            Assert.Empty(SettingsManager.UserSettings.Items);
        }

        [Fact]
        public void ARowThisPcCannotOpenIsNeverAssigned_AndTheSwitchStillClears()
        {
            var on = Profile(true, "on");
            var unavailable = new PeripheralSwitchMigration.LegacySwitch(PeripheralRowKind.RazerSensa, true,
                p => p.EnableSensaHaptics, p => p.EnableSensaHaptics = null, Available: false);

            Assert.True(PeripheralSwitchMigration.Run(new[] { unavailable }, new List<ProfileData> { on },
                Profile(true, "active"), Profile(true, "default"), _ => 1, Fresh));

            Assert.Null(on.Entries);
            Assert.Null(on.EnableSensaHaptics);
            Assert.Empty(SettingsManager.UserSettings.Items);
            Assert.Empty(SettingsManager.UserDevices.Items);
        }

        [Fact]
        public void AnImportedProfileTurnsItsOwnOpinionOnly()
        {
            var on = Profile(true, "imported");
            PeripheralSwitchMigration.MigrateImported(on);
            Assert.Null(on.EnableSensaHaptics);
            Assert.Equal(PlatformSupport.SensaAvailable ? 1 : 0, on.Entries?.Length ?? 0);

            var silent = Profile(null, "silent");
            PeripheralSwitchMigration.MigrateImported(silent);
            Assert.Null(silent.Entries);
            Assert.Empty(SettingsManager.UserSettings.Items);
        }

        private static PeripheralSwitchMigration.LegacySwitch ChromaSwitch(bool global)
            => new(PeripheralRowKind.RazerChroma, global, p => p.EnableChromaLightbar, p => p.EnableChromaLightbar = null);

        /// <summary>A PlayStation controller carries the game's lightbar when
        /// it is a DualSense or a DualShock 4, and no profile is the
        /// PlayStation default, a DualSense.</summary>
        [Fact]
        public void TheLightbarProfiles_AreTheDualSenseAndTheDualShock4()
        {
            Assert.True(PeripheralSwitchMigration.DecodesLightbar("dualsense"));
            Assert.True(PeripheralSwitchMigration.DecodesLightbar("dualsense-edge-bt"));
            Assert.True(PeripheralSwitchMigration.DecodesLightbar("dualshock-4-v2-composite"));
            Assert.True(PeripheralSwitchMigration.DecodesLightbar(null));
            Assert.True(PeripheralSwitchMigration.DecodesLightbar(""));
            Assert.False(PeripheralSwitchMigration.DecodesLightbar("dualshock-3"));

            var created = new[] { true, true, true, true };
            var ids = new[] { null, "dualshock-3", null, "dualsense" };
            // Slot 1 shows first among the PlayStation slots but has no
            // lightbar, slot 0 is not a PlayStation slot at all.
            Assert.Equal(3, PeripheralSwitchMigration.FirstLightbarSlot(created, new[] { 1, 3, 2 }, i => ids[i]));
            Assert.Equal(-1, PeripheralSwitchMigration.FirstLightbarSlot(created, new[] { 1 }, i => ids[i]));
            Assert.Equal(-1, PeripheralSwitchMigration.FirstLightbarSlot(created, null, i => ids[i]));
        }

        /// <summary>A mirror switch's row goes to the first DualSense or
        /// DualShock 4 in display order, not to the Xbox controller that
        /// shows first, since the mirror showed only a game's lightbar. A
        /// topology without one gets nothing.</summary>
        [Fact]
        public void AMirrorRow_GoesToTheFirstLightbarSlot_OrNowhere()
        {
            var profile = Profile(null);
            Assert.True(PeripheralSwitchMigration.AddToProfile(profile, PeripheralRowKind.RazerChroma));
            var entry = Assert.Single(profile.Entries);
            Assert.Equal(PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma), entry.InstanceGuid);
            Assert.Equal(3, entry.MapTo);

            var ds3 = Profile(null);
            ds3.SlotProfileIds = new[] { null, null, null, "dualshock-3", null, null, null, null };
            Assert.False(PeripheralSwitchMigration.AddToProfile(ds3, PeripheralRowKind.LogitechLightsync));
            Assert.Null(ds3.Entries);

            // The live topology gets the slot the kind asks for.
            var asked = new List<PeripheralRowKind>();
            Assert.True(PeripheralSwitchMigration.Run(new[] { ChromaSwitch(global: true) }, new List<ProfileData>(),
                null, null, kind => { asked.Add(kind); return kind == PeripheralRowKind.RazerChroma ? 3 : -1; }, Fresh));
            Assert.Equal(new[] { PeripheralRowKind.RazerChroma }, asked);
            Assert.Equal(3, Assert.Single(SettingsManager.UserSettings.Items).MapTo);
        }

        /// <summary>A stored profile's display order is rebuilt the way the
        /// live one is when the profile is applied: a profile saved before
        /// slot orders existed reads in ascending index, and a saved order
        /// that leaves a created slot out gets it appended.</summary>
        [Fact]
        public void AStoredProfilesOrder_IsRebuiltTheWayApplyingItRebuildsIt()
        {
            var legacy = new ProfileData
            {
                Name = "legacy",
                SlotCreated = new[] { false, true, true, false, false, false, false, false },
                SlotControllerTypes = new[] { 0, (int)VirtualControllerType.Xbox, (int)VirtualControllerType.PlayStation, 0, 0, 0, 0, 0 },
            };
            Assert.Equal(new[] { 2 }, PeripheralSwitchMigration.DisplayOrder(legacy, VirtualControllerType.PlayStation));
            Assert.Equal(1, PeripheralSwitchMigration.FirstDisplayedSlot(legacy));
            Assert.Equal(2, PeripheralSwitchMigration.RulingSlot(PeripheralRowKind.RazerChroma, legacy));
            Assert.Equal(1, PeripheralSwitchMigration.RulingSlot(PeripheralRowKind.RazerSensa, legacy));

            var partial = Profile(null);
            partial.SlotCreated[5] = true;
            partial.SlotControllerTypes[5] = (int)VirtualControllerType.PlayStation;
            partial.PlayStationSlotOrder = new[] { 9, 5, 5 };
            Assert.Equal(new[] { 5, 3 }, PeripheralSwitchMigration.DisplayOrder(partial, VirtualControllerType.PlayStation));
        }

        [Fact]
        public void TheDefaultsStoredStateFollowsItsOwnValue()
        {
            var defaultSnapshot = Profile(null, "default");
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), Profile(false, "active"), defaultSnapshot, _ => 1, Fresh));
            Assert.Single(defaultSnapshot.Entries);
            Assert.Empty(SettingsManager.UserSettings.Items);
        }
    }

    // ─────────────────────────────────────────────
    //  Wiring no unit test reaches, pinned by source
    // ─────────────────────────────────────────────

    /// <summary>The engine and service sites that hand peripheral rows their
    /// rumble (#494).</summary>
    public class PeripheralWiringTests
    {
        private static string Text(params string[] parts) => AuditDelta20261002Tests.RepoText(parts);

        private static string MethodBody(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " moved");
            return src[at..src.IndexOf("\n        }", at, StringComparison.Ordinal)];
        }

        [Fact]
        public void Step2HandsAHapticRowItsLevel_TheSoleWriterWay()
        {
            string body = MethodBody(Text("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"),
                "private void ApplyForceFeedback(UserDevice ud)");

            // The cache a row with no SDL rumble never got at load, born
            // with the stored level zeroed.
            int stop = body.IndexOf("PeripheralOutputs.StopHaptics(ud.InstanceGuid);", StringComparison.Ordinal);
            int born = body.IndexOf("ud.ForceFeedbackState = new ForceFeedbackState();", StringComparison.Ordinal);
            Assert.True(stop >= 0 && stop < born, "the stored level is not zeroed before the new cache");
            Assert.Contains("!PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(ud)", body);
            Assert.Contains("bool isPeripheralHaptic = PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(ud);", body);
            Assert.Contains("if (!isXboxImpulse && !isVendorFfb && !isPadixConverter && !isBlissBox && !isPeripheralHaptic)", body);
            Assert.Contains("else if ((isXboxImpulse || isPadixConverter || isBlissBox || isPeripheralHaptic) && ud.Device == null)", body);

            // The last slot leaving sends a zero.
            int zero = body.IndexOf("else if (isPeripheralHaptic)", StringComparison.Ordinal);
            Assert.True(zero >= 0, "no zero for a row that left its last slot");
            Assert.Contains("PeripheralOutputs.SetMotors(ud.InstanceGuid, 0, 0);", body[zero..]);

            // The branch: fold, resend after a silence edge, record, hand over.
            int branch = body.IndexOf("\n            if (isPeripheralHaptic)", StringComparison.Ordinal);
            Assert.True(branch >= 0, "the peripheral branch is gone");
            string tail = body[branch..];
            int fold = tail.IndexOf("FoldTriggersForDirectWriter(firstPadSetting, combinedLT, combinedRT, ref hapticL, ref hapticR)", StringComparison.Ordinal);
            int resend = tail.IndexOf("PeripheralOutputs.ConsumeResend(ud.InstanceGuid)", StringComparison.Ordinal);
            int record = tail.IndexOf("ud.ForceFeedbackState.TryRecordMotorSnapshot(hapticL, hapticR)", StringComparison.Ordinal);
            int hand = tail.IndexOf("PeripheralOutputs.SetMotors(ud.InstanceGuid, hapticL, hapticR)", StringComparison.Ordinal);
            Assert.True(fold >= 0 && fold < resend && resend < record && record < hand, $"{fold} {resend} {record} {hand}");
            Assert.Contains("MarkDirectWriteFailed();", tail[resend..record]);
            // The level is kept whether or not the path is up, so nothing is sent twice.
            Assert.DoesNotContain("MarkDirectWriteFailed();", tail[hand..tail.IndexOf("return;", hand, StringComparison.Ordinal)]);
        }

        [Fact]
        public void EverySilenceEdgeSilencesThePeripheralsToo()
        {
            string src = Text("PadForge.App", "Common", "Input", "InputManager.cs").Replace("\r\n", "\n");
            // Stop and focus suspend, where Step 2 does not run.
            var edges = Regex.Matches(src, @"RumbleAudioService\.SilenceAll\(\);");
            Assert.Equal(3, edges.Count);
            int idle = src.IndexOf("if (BeginIdlePoll())", StringComparison.Ordinal);
            foreach (Match edge in edges)
            {
                string after = src.Substring(edge.Index, Math.Min(400, src.Length - edge.Index));
                bool inIdle = idle >= 0 && edge.Index > idle && edge.Index - idle < 1200;
                // Idle is no edge: Step 2 runs there and writes the level.
                if (inIdle) Assert.DoesNotContain("PeripheralOutputs.SilenceHaptics(", after);
                else Assert.Contains("PeripheralOutputs.SilenceHaptics(", after);
            }
            // Focus suspend keeps a level a Remote Link peer drives.
            Assert.Contains("PeripheralOutputs.SilenceHaptics(keepRelayed: true);", src);

            // The crash quiesce's sweep: once before the gates, and per row
            // under each row's gate, so a level decided under it cannot land after.
            string stopAll = MethodBody(src, "private void StopAllForceFeedback()");
            Assert.Contains("PeripheralOutputs.SilenceHaptics();", stopAll);
            int gate = stopAll.IndexOf("System.Threading.Monitor.TryEnter(ud.OutputSync, 50, ref gated);", StringComparison.Ordinal);
            int rowStop = stopAll.IndexOf("PeripheralOutputs.StopHaptics(ud.InstanceGuid);", StringComparison.Ordinal);
            int release = stopAll.IndexOf("System.Threading.Monitor.Exit(ud.OutputSync);", StringComparison.Ordinal);
            Assert.True(gate >= 0 && gate < rowStop && rowStop < release, $"{gate} {rowStop} {release}");
            Assert.Contains("ShutdownPeripheralRows();", src);
            Assert.DoesNotContain("UpdateSensaLane", src);
        }

        [Fact]
        public void TheOtherWritersReachTheRowsLevel()
        {
            string step1 = Text("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs");
            Assert.Contains("changed |= UpdatePeripheralRows();", step1);
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralOutputs.StopHaptics(ud.InstanceGuid);", step1);

            Assert.DoesNotContain("UpdateSensaLane",
                Text("PadForge.App", "Common", "Input", "InputManager.Step5.VirtualDevices.cs"));

            Assert.Contains("|| PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(device);",
                Text("PadForge.App", "Common", "Input", "InputManager.SteeringAngleRumble.cs"));

            string service = Text("PadForge.App", "Services", "InputService.cs");
            // Identify's pulse train, under the row's gate with the quiesce re-checked.
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(ud);", service);
            int peripheral = service.IndexOf("else if (peripheral)", StringComparison.Ordinal);
            Assert.True(peripheral >= 0, "Identify's peripheral lane is gone");
            string lane = service.Substring(peripheral, 1400);
            int locked = lane.IndexOf("lock (ud.OutputSync)", StringComparison.Ordinal);
            int recheck = lane.IndexOf("_inputManager?.OutputsQuiesced == true", StringComparison.Ordinal);
            int write = lane.IndexOf("PeripheralOutputs.SetMotors(ud.InstanceGuid, left, right,", StringComparison.Ordinal);
            Assert.True(locked >= 0 && locked < recheck && recheck < write, $"{locked} {recheck} {write}");
            // Remote Link: the owner advertises the rumble and applies it,
            // and keeps the rows with no input of their own to itself, a
            // vendor row and a unit row alike.
            Assert.Contains("HasRumble = dev.HasRumble || ud.HasPeripheralHaptics", service);
            Assert.Contains("&& PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(ud))", service);
            Assert.Contains("if (dev is PadForge.Common.Input.Peripherals.IOutputOnlyRow) return false;", service);
            // The Devices page's rumble chip, which is Identify's button.
            Assert.Contains("|| PadForge.Common.Input.Peripherals.PeripheralOutputs.IsHapticPeripheral(ud);", service);
            Assert.DoesNotContain("StartSensaIfEnabled", service);
        }

        [Fact]
        public void ARemovedOrRetiredRowStopsItsLevel()
        {
            string remove = MethodBody(Text("PadForge.App", "Common", "SettingsManager.cs"),
                "public static bool RemoveDevice(Guid instanceGuid)");
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralOutputs.StopHaptics(instanceGuid);", remove);

            string retire = MethodBody(Text("PadForge.App", "Common", "Input", "InputManager.PeripheralRows.cs"),
                "private void RetirePeripheralRow(PeripheralRowKind kind)");
            Assert.Contains("PeripheralOutputs.StopHaptics(row.InstanceGuid);", retire);
            Assert.DoesNotContain("SetMotors", retire);
        }

        [Fact]
        public void OnlyThisPcsOwnDevicesLinkToItsVendorChannels()
        {
            string host = Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath)) continue;", host);

            // The records land before the table that goes with them.
            string link = MethodBody(host, "private void Link(WorkerRun run)");
            int records = link.IndexOf("ud.PeripheralOutputs = next;", StringComparison.Ordinal);
            int publish = link.IndexOf("PeripheralOutputs.PublishLinks(table);", StringComparison.Ordinal);
            Assert.True(records >= 0 && records < publish, $"{records} {publish}");
            Assert.Contains("PeripheralOutputs.NotifyStatusChanged();", link);

            string service = Text("PadForge.App", "Services", "InputService.cs");
            string changed = MethodBody(service, "private void OnPeripheralCapabilitiesChanged()");
            Assert.Contains("SyncDevicesList();", changed);
        }

        [Fact]
        public void AReopenedSlotBringsTheScanForward_AndTheHostWaitSurvivesDispose()
        {
            string backend = Text("PadForge.App", "Common", "Input", "Peripherals", "HidppBackend.cs");
            Assert.Contains("if (Check(run, channels, states))", backend);
            Assert.Contains("nextScan = Math.Min(nextScan, _ticks() + FirstScanMs);", backend);

            string host = Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("try { _wake.WaitOne(_linkMs); }", host);
            Assert.Contains("catch (ObjectDisposedException) { return; }", host);

            string service = Text("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("ud.InstanceGuid, hvib.LeftMotorSpeed, hvib.RightMotorSpeed, relayed: true);", service);
            // The relay pays back a resend a silence edge owes, before its
            // snapshot compares.
            int relay = service.IndexOf("var hvib = effect.Vibration;", StringComparison.Ordinal);
            string lane = service.Substring(relay, 1400);
            int resend = lane.IndexOf("PeripheralOutputs.ConsumeResend(ud.InstanceGuid))", StringComparison.Ordinal);
            int record = lane.IndexOf("TryRecordMotorSnapshot(", StringComparison.Ordinal);
            Assert.True(resend >= 0 && resend < record, $"{resend} {record}");
            // Identify keeps a peer-driven level marked as the peer's.
            Assert.Contains("relayed: PadForge.Common.Input.RemoteLinkOutputRouter.PeerWroteLast(ud.DevicePath));", service);
        }

        [Fact]
        public void AReceiversHidpp10ErrorNeverRetriesAsBusyOrPassesForAlive()
        {
            // HID++ 1.0's 0x08 is UNKNOWN_DEVICE, not HID++ 2.0's BUSY.
            string haptics = Text("PadForge.App", "Common", "Input", "HidppHaptics.cs");
            Assert.Contains("if (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10", haptics);
            // The sleep check of a unit with no haptics reads it as out of reach.
            string backend = Text("PadForge.App", "Common", "Input", "Peripherals", "HidppBackend.cs");
            Assert.Contains("|| (reply.Kind == HidppReplyKind.Error && !reply.Hidpp10);", backend);
        }

        [Fact]
        public void AnImportedProfileFileMigratesItsSwitch()
        {
            string import = MethodBody(Text("PadForge.App", "Common", "ProfileTransfer.cs"),
                "public static ProfileData Import(string srcPath, out List<string> registeredPackages)");
            Assert.Contains("PeripheralSwitchMigration.MigrateImported(profile);", import);
        }

        [Fact]
        public void AConsumerRegistersADeviceAgainWhenItsRumbleChanges()
        {
            string reconcile = MethodBody(Text("PadForge.Engine", "RemoteLink", "LinkServer.cs"),
                "private void ReconcileRemoteDevices(");
            int changed = reconcile.IndexOf("bool rumbleChanged = existing.Info.HasRumble != info.HasRumble", StringComparison.Ordinal);
            int refresh = reconcile.IndexOf("existing.Info.HasRumble = info.HasRumble;", StringComparison.Ordinal);
            Assert.True(changed >= 0 && changed < refresh, "the change is read before the refresh overwrites it");
            Assert.Contains("|| existing.Info.HasHaptic != info.HasHaptic;", reconcile);
            Assert.Contains("if (slotChanged || typeChanged || touchCapabilitiesChanged || axesChanged || rumbleChanged)", reconcile);
        }

        [Fact]
        public void TheForceFeedbackTabOpensForAHapticRow()
        {
            string page = Text("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("hasPeripheralHaptics = PadForge.Common.Input.Peripherals.PeripheralOutputs.IsHapticPeripheral(ud);", page);
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralRouteText.Haptics(peripheralRow, slotLive)", page);
            Assert.Contains("x:Name=\"PeripheralHapticsRoute\"", Text("PadForge.App", "Views", "PadPage.xaml"));
        }

        [Fact]
        public void ThePanicPathWaitsForTheGameSenseZero()
        {
            string panic = MethodBody(Text("PadForge.App", "Services", "InputService.cs"),
                "public void PanicQuiesceOutputs()");
            int quiesce = panic.IndexOf("_inputManager?.QuiesceOutputs();", StringComparison.Ordinal);
            int wait = panic.IndexOf("_peripheralHost?.WaitForSilence(250);", StringComparison.Ordinal);
            Assert.True(quiesce >= 0 && wait > quiesce, "the wait follows the quiesce that zeroes the levels");
        }

        /// <summary>The crash wait reaches the Razer Sensa worker too, inside
        /// the one budget, after GameSense's.</summary>
        [Fact]
        public void ThePanicPathWaitsForTheSensaZero()
        {
            string wait = MethodBody(Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs"),
                "public void WaitForSilence(int waitMs)");
            int gameSense = wait.IndexOf("_gameSense.WaitForSilence(waitMs);", StringComparison.Ordinal);
            int sensa = wait.IndexOf("Volatile.Read(ref _sensa)?.WaitForSilence(", StringComparison.Ordinal);
            Assert.True(gameSense >= 0 && sensa > gameSense, $"{gameSense} {sensa}");
        }

        /// <summary>A haptic mouse, keyboard or the Razer Sensa row plays the
        /// stronger motor whichever side it is on, so swapping the two changes
        /// nothing, and the Force Feedback tab leaves the swap row out for
        /// those rows and keeps it for every other device.</summary>
        [Fact]
        public void TheSwapRowHidesForAHapticRow_WhosePlayIsTheStrongerMotor()
        {
            var device = Guid.NewGuid();
            try
            {
                PeripheralOutputs.SetMotors(device, 0x1000, 0x8000);
                float one = PeripheralOutputs.AmplitudeOf(device);
                PeripheralOutputs.SetMotors(device, 0x8000, 0x1000);
                Assert.Equal(one, PeripheralOutputs.AmplitudeOf(device));
            }
            finally { PeripheralOutputs.StopHaptics(device); }

            string xaml = Text("PadForge.App", "Views", "PadPage.xaml");
            int row = xaml.IndexOf("x:Name=\"FfbSwapMotorsRow\"", StringComparison.Ordinal);
            int swap = xaml.IndexOf("CommandParameter=\"SwapMotors\"", StringComparison.Ordinal);
            Assert.True(row >= 0 && swap > row && swap - row < 800, "the swap row is named");
            Assert.Contains("FfbSwapMotorsRow.Visibility = hasPeripheralHaptics ? Visibility.Collapsed : Visibility.Visible;",
                Text("PadForge.App", "Views", "PadPage.xaml.cs"));
        }

        /// <summary>The link pass prunes under the settings lock its snapshot
        /// was read under, so an assignment made between the two can never
        /// lose the claim its first pass made.</summary>
        [Fact]
        public void TheLinkPassPrunesUnderTheLockItsSnapshotWasReadUnder()
        {
            string link = MethodBody(Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs"),
                "private void Link(WorkerRun run)");
            int locked = link.IndexOf("lock (settings.SyncRoot)", StringComparison.Ordinal);
            Assert.True(locked >= 0, "the snapshot's lock moved");
            int open = link.IndexOf('{', locked), depth = 0, close = -1;
            for (int i = open; i < link.Length; i++)
            {
                if (link[i] == '{') depth++;
                else if (link[i] == '}' && --depth == 0) { close = i; break; }
            }
            int prune = link.IndexOf("PeripheralOutputs.PruneClaims(assigned, PeripheralOutputs.SlotLive);", StringComparison.Ordinal);
            Assert.True(prune > open && prune < close, $"{open} {prune} {close}");

            // The pass keeps no failed container lookup, and forgets the
            // paths no row used.
            Assert.Contains("rows[i] = rows[i] with { Container = ResolveContainer(_containers, path, _containerOf) };", link);
            Assert.Contains("ForgetContainers(_containers, used);", link);
        }

        /// <summary>The facade keeps no release no production code makes, and
        /// no summary that describes a consumer that does not exist.</summary>
        [Fact]
        public void TheFacadeDescribesOnlyItsRealConsumers()
        {
            string facade = Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputs.cs");
            Assert.DoesNotContain("public static void ReleaseSlot(", facade);
            // A summary wraps, so its prose is read with each line break and
            // comment marker folded into one space.
            string prose = Regex.Replace(facade, @"\s*\r?\n\s*///\s*", " ");
            Assert.Contains("<summary>Bumped on every claim, release and color change.</summary>", prose);
            Assert.DoesNotContain("so a backend re-resolves only when something moved", prose);
        }

        /// <summary>Every GameSense body names the game and the rumble event
        /// through one constant each, and the receiver's register read is
        /// sized by the short report's own length.</summary>
        [Fact]
        public void TheGameSenseNamesAndTheShortFrame_AreNamedOnce()
        {
            string Code(string text) => string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            string client = Code(Text("PadForge.App", "Services", "GameSenseClient.cs"));
            // The bare words: a body spelled out by hand holds each name
            // between escaped quotes, which a quoted pattern never matches.
            Assert.Single(Regex.Matches(client, @"\bPADFORGE\b"));
            Assert.Single(Regex.Matches(client, @"\bRUMBLE\b"));
            Assert.Contains("internal const string Game = \"PADFORGE\";", client);
            Assert.Contains("internal const string Event = \"RUMBLE\";", client);

            string units = Code(Text("PadForge.App", "Common", "Input", "Peripherals", "HidppUnits.cs"));
            Assert.True(Regex.Matches(units, @"\bShortReportLength\b").Count >= 2, "ShortReportLength is used");
            Assert.Equal(new byte[] { 0x10, 0xFF, 0x83, 0xB5, 0x52, 0x00, 0x00 }, HidppReceiverProtocol.ReadRegister(0xB5, 0x52));
        }

        /// <summary>Identify takes a haptic row's shared path for its train in
        /// both lanes, and ends the claim however the train ends.</summary>
        [Fact]
        public void IdentifyClaimsTheHapticPathForBothTrains()
        {
            string body = MethodBody(Text("PadForge.App", "Services", "InputService.cs"),
                "public void IdentifyDevice(Guid instanceGuid)");
            Assert.Equal(2, Regex.Matches(body, @"PeripheralOutputs\.BeginIdentify\(instanceGuid,").Count);
            int sole = body.IndexOf("if (pad >= 0)", StringComparison.Ordinal);
            int direct = body.IndexOf("bool peripheral = ", StringComparison.Ordinal);
            int first = body.IndexOf("PeripheralOutputs.BeginIdentify(instanceGuid,", StringComparison.Ordinal);
            int second = body.IndexOf("PeripheralOutputs.BeginIdentify(instanceGuid,", first + 1, StringComparison.Ordinal);
            Assert.True(sole < first && first < direct && direct < second, $"{sole} {first} {direct} {second}");
            int end = body.IndexOf("if (identifying) PadForge.Common.Input.Peripherals.PeripheralOutputs.EndIdentify(instanceGuid);",
                StringComparison.Ordinal);
            int final = body.LastIndexOf("finally", StringComparison.Ordinal);
            Assert.True(final >= 0 && end > final, "the claim ends in the train's finally");
        }

        [Fact]
        public void TheLoadSavesAMigrationAfterItsCallersClearTheDirtyFlag()
        {
            string src = Text("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("_peripheralSwitchesMigratedOnLoad = true;", src);
            Assert.Equal(2, Regex.Matches(src,
                @"if \(_peripheralSwitchesMigratedOnLoad\) \{ _peripheralSwitchesMigratedOnLoad = false; MarkDirty\(\); \}").Count);
            Assert.Contains("activeProfile != null ? SettingsManager.PendingDefaultSnapshot : null", src);
        }
    }
}
