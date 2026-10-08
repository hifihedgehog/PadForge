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

        [Fact]
        public void OnlyAHidppUnitPathReachesASingleDevice()
        {
            foreach (OutputFamily family in Enum.GetValues(typeof(OutputFamily)))
                Assert.Equal(family != OutputFamily.HidppUnit, new OutputPath(family, "k").Shared);
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
            Assert.Null(PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x1824) },
                HidppSnapshot.Empty, gg).For(A));
            Assert.Null(PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Keyboard, PeripheralLinker.SteelSeriesVid, 0x170E) },
                HidppSnapshot.Empty, gg).For(A));
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

            using var md5 = MD5.Create();
            Guid Hash(string s) => new(md5.ComputeHash(Encoding.UTF8.GetBytes(s)));

            foreach (var (kind, tag, type) in new[]
            {
                (PeripheralRowKind.RazerChroma, "pfrazerchroma", InputDeviceType.PeripheralLighting),
                (PeripheralRowKind.LogitechLightsync, "pflogilightsync", InputDeviceType.PeripheralLighting),
                (PeripheralRowKind.RazerSensa, "pfrazersensa", InputDeviceType.PeripheralHaptics),
            })
            {
                var row = new PeripheralOutputRow(kind);
                Assert.Equal(Hash(tag), PeripheralOutputRow.IdentityFor(kind));
                Assert.Equal(Hash(tag), row.InstanceGuid);
                Assert.Equal(Hash(tag + "-product"), row.ProductGuid);
                Assert.Equal(type, row.GetInputDeviceType());
                Assert.Equal(0x5046, row.VendorId);
                Assert.Equal(0, row.NumAxes + row.NumButtons + row.NumHats);
                Assert.False(row.HasRumble || row.HasHaptic);
                Assert.NotNull(row.GetCurrentState());
                row.Dispose();
                Assert.False(row.IsAttached);
                Assert.Null(row.GetCurrentState());
            }
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

        [Fact]
        public void TheRecordedOutputsSurviveTheFile_AsTwoBits()
        {
            var ud = new UserDevice { PeripheralOutputs = 3 };
            Assert.True(ud.HasPeripheralHaptics);
            Assert.True(ud.HasPeripheralLighting);
            Assert.False(new UserDevice { PeripheralOutputs = 2 }.HasPeripheralHaptics);
            Assert.False(new UserDevice { PeripheralOutputs = 1 }.HasPeripheralLighting);

            var serializer = new XmlSerializer(typeof(UserDevice));
            using var writer = new StringWriter();
            serializer.Serialize(writer, ud);
            Assert.Contains("<PeripheralOutputs>3</PeripheralOutputs>", writer.ToString());
            var back = (UserDevice)serializer.Deserialize(new StringReader(writer.ToString()));
            Assert.Equal(3, back.PeripheralOutputs);
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

        [Fact]
        public void A0x8071DevicePrefersIt_AndListsNoZones()
        {
            var device = new Scripted { Name = "G915", Type = 0 };
            device.Features[0x8071] = 0x0C;
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };

            var unit = Describe(device, out bool incomplete);

            Assert.False(incomplete);
            Assert.Equal(HidppUnitProtocol.RgbEffects, unit.RgbFeatureId);
            Assert.Equal(0x0C, unit.RgbIndex);
            Assert.Empty(unit.Zones);
            Assert.False(unit.DirectRgb);
            Assert.True(unit.HasRgb);
            Assert.True(unit.IsKeyboardKind);
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

        [Fact]
        public void TheNameAndTypeAreExtras_AMissingAnswerOnlyLeavesThemUnknown()
        {
            var device = new Scripted();
            device.Features[0x8070] = 0x0E;
            device.Zones = new[] { new ushort[] { 0x0001 } };
            device.Silent = (f, fn, p) => f == 0x03;

            var unit = Describe(device, out bool incomplete);

            Assert.False(incomplete);
            Assert.Null(unit.Name);
            Assert.Equal(-1, unit.DeviceType);
            Assert.True(unit.DirectRgb);
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

        public PeripheralOutputsFacadeTests() => PeripheralOutputs.ResetForTest();
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
            Assert.Equal(B, ruler);
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
            Assert.Equal(A, ruler);
            Assert.Equal(0x112233, rgb);

            PeripheralOutputs.ReleaseSlot(2);
            Assert.False(PeripheralOutputs.TryResolveColor(path, out _, out _));
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
    }

    /// <summary>The GameSense worker binds while a tactile Rival is
    /// assigned and hands the mice back to GG when none is (#494).</summary>
    [Collection("PeripheralOutputStatics")]
    public class GameSenseBackendTests : IDisposable
    {
        public GameSenseBackendTests() => PeripheralOutputs.ResetForTest();
        public void Dispose() => PeripheralOutputs.ResetForTest();

        [Fact]
        public void TheRulingRivalsLevelPlays_AndTheMiceGoBackToGGWhenNoneIsAssigned()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var rival = Guid.NewGuid();
            int player = 1;
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = rival, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } },
            }));
            using var backend = new GameSenseBackend(() => new GameSenseTactile(coreProps, 1000),
                _ => Volatile.Read(ref player), 2);
            backend.Start();

            PeripheralOutputs.SetMotors(rival, 65535, 65535);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().LastOrDefault() > 0, 5000), "the level was posted");
            Assert.Equal(new[] { "/game_metadata", "/bind_game_event" }, server.Paths().Take(2));
            Assert.Equal(BackendState.Connected, PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile));

            // The Rival leaves its controller: the motor stops at once, and
            // GG takes the mice back only after the release delay.
            Volatile.Write(ref player, 0);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().Last() == 0, 1000), "the motor stopped at once");
            Assert.DoesNotContain("/stop_game", server.Paths());
            Assert.True(SpinWait.SpinUntil(() => server.Paths().Contains("/stop_game"),
                GameSenseBackend.ReleaseMs + 3000), "the mice went back to GG");
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile) == BackendState.Idle, 1000));
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
            using var backend = new GameSenseBackend(() => new GameSenseTactile(coreProps, 1000), _ => 1, 2);
            backend.Start();
            PeripheralOutputs.SetMotors(rival, 65535, 0);
            Assert.True(SpinWait.SpinUntil(() => server.EventValues().LastOrDefault() > 0, 5000), "the level was posted");

            // The engine's quiesce zeroes the level, then the panic path waits.
            PeripheralOutputs.SilenceHaptics();
            backend.WaitForSilence(1000);
            Assert.Equal(0, server.EventValues().Last());
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
                new List<ProfileData> { on, off, silent }, null, null, () => -1, Fresh));

            Assert.Single(on.Entries);
            Assert.Null(off.Entries);
            Assert.Null(silent.Entries);
            Assert.All(new[] { on, off, silent }, p => Assert.Null(p.EnableSensaHaptics));
            Assert.Empty(SettingsManager.UserSettings.Items);

            // A global switch that was on reaches the profile with no opinion.
            silent.EnableSensaHaptics = null;
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData> { silent }, null, null, () => -1, Fresh));
            Assert.Single(silent.Entries);
        }

        [Fact]
        public void TheLiveSettingsFollowTheActiveProfilesOwnValue()
        {
            // On globally, off in the active profile: the live slot stays bare.
            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), Profile(false, "active"), null, () => 1, Fresh));
            Assert.Empty(SettingsManager.UserSettings.Items);

            // Off globally, on in the active profile: the live slot takes the row.
            var active = Profile(true, "active");
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: false) },
                new List<ProfileData>(), active, null, () => 1, Fresh));
            var us = Assert.Single(SettingsManager.UserSettings.Items);
            Assert.Equal(Sensa, us.InstanceGuid);
            Assert.Equal(1, us.MapTo);
            Assert.NotNull(us.GetPadSetting());
        }

        [Fact]
        public void TheDefaultTopologyTakesTheGlobalValue_OnceAndOffline()
        {
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, () => 0, Fresh));

            var record = Assert.Single(SettingsManager.UserDevices.Items);
            Assert.Equal(Sensa, record.InstanceGuid);
            Assert.False(record.IsOnline);
            Assert.Equal(InputDeviceType.PeripheralHaptics, record.CapType);
            Assert.True(record.HasPeripheralHaptics);
            Assert.Equal(0, Assert.Single(SettingsManager.UserSettings.Items).MapTo);

            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, () => 0, Fresh));
            Assert.Single(SettingsManager.UserSettings.Items);

            // No controller: nothing to assign.
            SettingsManager.UserSettings = new SettingsCollection();
            Assert.False(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), null, null, () => -1, Fresh));
            Assert.Empty(SettingsManager.UserSettings.Items);
        }

        [Fact]
        public void ARowThisPcCannotOpenIsNeverAssigned_AndTheSwitchStillClears()
        {
            var on = Profile(true, "on");
            var unavailable = new PeripheralSwitchMigration.LegacySwitch(PeripheralRowKind.RazerSensa, true,
                p => p.EnableSensaHaptics, p => p.EnableSensaHaptics = null, Available: false);

            Assert.True(PeripheralSwitchMigration.Run(new[] { unavailable }, new List<ProfileData> { on },
                Profile(true, "active"), Profile(true, "default"), () => 1, Fresh));

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

        [Fact]
        public void TheDefaultsStoredStateFollowsItsOwnValue()
        {
            var defaultSnapshot = Profile(null, "default");
            Assert.True(PeripheralSwitchMigration.Run(new[] { Switch(global: true) },
                new List<ProfileData>(), Profile(false, "active"), defaultSnapshot, () => 1, Fresh));
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
            // and keeps vendor rows to itself.
            Assert.Contains("HasRumble = dev.HasRumble || ud.HasPeripheralHaptics,", service);
            Assert.Contains("&& PadForge.Common.Input.Peripherals.PeripheralOutputs.TakesHaptics(ud))", service);
            Assert.Contains("if (dev is PadForge.Common.Input.Peripherals.PeripheralOutputRow) return false;", service);
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
            string link = MethodBody(host, "private void Link()");
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
            Assert.Contains("if (Check(channels, states))", backend);
            Assert.Contains("nextScan = Math.Min(nextScan, Environment.TickCount64 + FirstScanMs);", backend);

            string host = Text("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("try { _wake.WaitOne(LinkMs); }", host);
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
            Assert.Contains("PadForge.Common.Input.Peripherals.PeripheralRouteText.Haptics(peripheralRow)", page);
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
