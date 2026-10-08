using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Mice, keyboards and the vendor rows take a virtual controller's
    /// lighting through their own Lighting tabs (#494): one claim per
    /// (device, slot), the smallest displayed player number ruling a device
    /// on two controllers or a path several devices share, the game's
    /// lightbar captured per controller behind the trust gate, and the
    /// backends painting what the claims resolve to.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class PeripheralLightingTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Vendor = new("00000000-0000-0000-0000-0000000000c3");

        public PeripheralLightingTests()
        {
            PeripheralOutputs.ResetForTest();
            GameLightbarCapture.ResetForTest();
        }

        public void Dispose()
        {
            PeripheralOutputs.ResetForTest();
            GameLightbarCapture.ResetForTest();
        }

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ─────────────────────────────────────────────
        //  Claims
        // ─────────────────────────────────────────────

        /// <summary>A device on two virtual controllers holds a claim from
        /// each. The smaller displayed player number rules, the other slot's
        /// repaint of its own claim never takes the device over, and each
        /// slot releases only its own claim.</summary>
        [Fact]
        public void ADeviceOnTwoControllers_TheSmallerPlayerNumberRules_AndEachSlotReleasesItsOwn()
        {
            var unit = new OutputPath(OutputFamily.HidppUnit, "rx|01");
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit } });

            PeripheralOutputs.SetLighting(A, slot: 4, player: 2, 0x10, 0x20, 0x30);
            PeripheralOutputs.SetLighting(A, slot: 7, player: 1, 0x40, 0x50, 0x60);
            Assert.True(PeripheralOutputs.TryResolveColor(unit, out int rgb, out var ruler));
            Assert.Equal(0x405060, rgb);
            Assert.Equal(7, ruler.Slot);
            Assert.Equal(1, ruler.Player);

            PeripheralOutputs.SetLighting(A, slot: 4, player: 2, 0x11, 0x21, 0x31);
            Assert.True(PeripheralOutputs.TryResolveColor(unit, out rgb));
            Assert.Equal(0x405060, rgb);

            PeripheralOutputs.ReleaseLighting(A, 7);
            Assert.True(PeripheralOutputs.TryResolveColor(unit, out rgb, out ruler));
            Assert.Equal(0x112131, rgb);
            Assert.Equal(4, ruler.Slot);

            PeripheralOutputs.ReleaseLighting(A, 4);
            Assert.False(PeripheralOutputs.TryResolveColor(unit, out _));
            Assert.False(PeripheralOutputs.IsLit(A));
        }

        /// <summary>A device that claims a shared path on its own rules it over
        /// the vendor row whatever the numbers, and the vendor row keeps every
        /// path no device claims.</summary>
        [Fact]
        public void ADevicesOwnClaimOutranksTheVendorRow_WhichKeepsTheRest()
        {
            var mouse = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            var headset = new OutputPath(OutputFamily.ChromaCategory, "headset");
            Link(new DeviceLinks { Device = Vendor, CatchAll = true, Lighting = new[] { mouse, headset } },
                 new DeviceLinks { Device = A, Lighting = new[] { mouse } });

            PeripheralOutputs.SetLighting(Vendor, slot: 0, player: 1, 0x01, 0x01, 0x01);
            PeripheralOutputs.SetLighting(A, slot: 3, player: 4, 0x02, 0x02, 0x02);
            Assert.True(PeripheralOutputs.TryResolveColor(mouse, out int rgb, out var ruler));
            Assert.Equal(0x020202, rgb);
            Assert.Equal(A, ruler.Device);
            Assert.True(PeripheralOutputs.TryResolveColor(headset, out rgb));
            Assert.Equal(0x010101, rgb);

            PeripheralOutputs.ReleaseLighting(A, 3);
            Assert.True(PeripheralOutputs.TryResolveColor(mouse, out rgb, out ruler));
            Assert.Equal(Vendor, ruler.Device);
        }

        [Fact]
        public void ReleasingASlotOrADevice_DropsOnlyTheirClaims()
        {
            var path = new OutputPath(OutputFamily.LedSdkType, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { path } },
                 new DeviceLinks { Device = B, Lighting = new[] { path } });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            PeripheralOutputs.SetLighting(A, 1, 2, 2, 2, 2);
            PeripheralOutputs.SetLighting(B, 1, 2, 3, 3, 3);

            PeripheralOutputs.ReleaseSlot(1);
            Assert.True(PeripheralOutputs.IsLit(A));
            Assert.False(PeripheralOutputs.IsLit(B));

            PeripheralOutputs.SetLighting(B, 2, 3, 3, 3, 3);
            PeripheralOutputs.ReleaseDevice(A);
            Assert.False(PeripheralOutputs.IsLit(A));
            Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb));
            Assert.Equal(0x030303, rgb);
        }

        /// <summary>A device unassigned from a slot loses that slot's claim at
        /// the next link pass, whose assignments the prune takes, and keeps
        /// the claims of the slots it is still on.</summary>
        [Fact]
        public void PruningDropsTheClaimsOfAssignmentsThatEnded()
        {
            var path = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { path } });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            PeripheralOutputs.SetLighting(A, 2, 2, 2, 2, 2);
            int claims = 0;
            void OnClaims() => claims++;
            PeripheralOutputs.ClaimsChanged += OnClaims;
            try
            {
                PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)> { (A, 0), (A, 2) });
                Assert.Equal(0, claims);

                PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)> { (A, 2), (B, 0) });
                Assert.Equal(1, claims);
                Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb, out var ruler));
                Assert.Equal(2, ruler.Slot);
                Assert.Equal(0x020202, rgb);

                PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)>());
                Assert.False(PeripheralOutputs.IsLit(A));
            }
            finally { PeripheralOutputs.ClaimsChanged -= OnClaims; }

            string host = RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("PeripheralOutputs.PruneClaims(assigned, slot => UserEffectsDispatcher.HasLiveDispatcher(slot)", host);
        }

        /// <summary>A slot whose virtual controller went away has no
        /// dispatcher to release its claims, so the prune drops them even
        /// while the device stays assigned there.</summary>
        [Fact]
        public void AClaimOnASlotWithNoLiveDispatcher_IsPruned()
        {
            var path = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { path } });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            PeripheralOutputs.SetLighting(A, 2, 2, 2, 2, 2);

            var assigned = new HashSet<(Guid, int)> { (A, 0), (A, 2) };
            PeripheralOutputs.PruneClaims(assigned, slot => true);
            Assert.True(PeripheralOutputs.TryResolveColor(path, out _, out var ruler));
            Assert.Equal(0, ruler.Slot);

            PeripheralOutputs.PruneClaims(assigned, slot => slot != 0);
            Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb, out ruler));
            Assert.Equal(2, ruler.Slot);
            Assert.Equal(0x020202, rgb);
        }

        /// <summary>The tab follows claims and ranks, never a color alone,
        /// so an animated mode does not redraw it thirty times a second.</summary>
        [Fact]
        public void ClaimsChangedFollowsClaimsAndRanks_NotColors()
        {
            int raised = 0;
            void OnClaims() => raised++;
            PeripheralOutputs.ClaimsChanged += OnClaims;
            try
            {
                PeripheralOutputs.SetLighting(A, 0, 1, 1, 2, 3);
                Assert.Equal(1, raised);
                int version = PeripheralOutputs.LightingVersion;
                PeripheralOutputs.SetLighting(A, 0, 1, 4, 5, 6);
                Assert.Equal(1, raised);
                Assert.True(PeripheralOutputs.LightingVersion > version);
                PeripheralOutputs.SetLighting(A, 0, 2, 4, 5, 6);
                Assert.Equal(2, raised);
                PeripheralOutputs.ReleaseLighting(A, 0);
                Assert.Equal(3, raised);
                PeripheralOutputs.ReleaseLighting(A, 0);
                Assert.Equal(3, raised);
            }
            finally { PeripheralOutputs.ClaimsChanged -= OnClaims; }
        }

        /// <summary>Set Chroma Color is asserted per slot and lives for its
        /// window after the last assertion.</summary>
        [Fact]
        public void AChromaMacroLivesForItsWindow_OnItsOwnSlot()
        {
            long now = Environment.TickCount64;
            PeripheralOutputs.AssertChromaMacro(2, 0x01, 0x02, 0x03, now);
            Assert.True(PeripheralOutputs.TryGetChromaMacro(2, now, out int rgb));
            Assert.Equal(0x010203, rgb);
            Assert.False(PeripheralOutputs.TryGetChromaMacro(1, now, out _));
            Assert.True(PeripheralOutputs.AnyChromaMacro(now));

            long later = now + PeripheralOutputs.MacroAssertWindowMs + 50;
            Assert.False(PeripheralOutputs.TryGetChromaMacro(2, later, out _));
            Assert.False(PeripheralOutputs.AnyChromaMacro(later));
            Assert.False(PeripheralOutputs.TryGetChromaMacro(99, now, out _));
        }

        /// <summary>A macro color wakes the Chroma worker alone, on its start
        /// and on a new color, never on every assertion of a held one, and
        /// never the workers that do not paint it.</summary>
        [Fact]
        public void AChromaMacro_WakesOnlyTheChromaWorker_OnStartsAndChanges()
        {
            int chroma = 0, lighting = 0;
            void OnChroma() => chroma++;
            void OnLighting() => lighting++;
            PeripheralOutputs.ChromaMacroChanged += OnChroma;
            PeripheralOutputs.LightingChanged += OnLighting;
            try
            {
                long now = 1_000_000;
                PeripheralOutputs.AssertChromaMacro(3, 1, 2, 3, now);
                PeripheralOutputs.AssertChromaMacro(3, 1, 2, 3, now + 1);
                PeripheralOutputs.AssertChromaMacro(3, 1, 2, 3, now + 2);
                Assert.Equal(1, chroma);
                PeripheralOutputs.AssertChromaMacro(3, 4, 5, 6, now + 3);
                Assert.Equal(2, chroma);
                PeripheralOutputs.AssertChromaMacro(3, 4, 5, 6, now + 3 + PeripheralOutputs.MacroAssertWindowMs + 1);
                Assert.Equal(3, chroma);
                Assert.Equal(0, lighting);
            }
            finally
            {
                PeripheralOutputs.ChromaMacroChanged -= OnChroma;
                PeripheralOutputs.LightingChanged -= OnLighting;
            }
        }

        /// <summary>Battery mode reads a Logitech device's charge from the
        /// HID++ units its row matches, whatever path lights it, G HUB's
        /// included. Any other row reads as unknown.</summary>
        [Fact]
        public void TheChargeComesFromTheMatchedHidppUnit_WhateverPathLightsIt()
        {
            var unit = new HidppUnit { ChannelPath = "rx", DeviceIndex = 1, BatteryFeatureId = 0x1004, BatteryIndex = 9, BatteryPercent = 42 };
            var silent = new HidppUnit { ChannelPath = "rx", DeviceIndex = 2 };
            PeripheralOutputs.Hidpp = new HidppSnapshot(new[] { silent, unit }, Array.Empty<Guid>());
            Link(new DeviceLinks
                 {
                     Device = A, Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") },
                     HidppUnits = new[] { silent.Key, unit.Key },
                 },
                 new DeviceLinks { Device = B, Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") } });
            Assert.Equal(42, PeripheralOutputs.BatteryOf(A));
            Assert.Equal(-1, PeripheralOutputs.BatteryOf(B));
            Assert.Equal(-1, PeripheralOutputs.BatteryOf(Guid.NewGuid()));
        }

        /// <summary>The link pass asks for a Battery-mode pass when a unit's
        /// charge changes, a unit found or gone included, and never for a
        /// snapshot that changed nothing else.</summary>
        [Fact]
        public void AChargeChange_IsSeenBetweenSnapshots()
        {
            var unit = new HidppUnit { ChannelPath = "rx", DeviceIndex = 1, BatteryPercent = 50 };
            var first = new HidppSnapshot(new[] { unit }, Array.Empty<Guid>());
            Assert.False(PeripheralOutputHost.ChargesChanged(first, first));
            Assert.False(PeripheralOutputHost.ChargesChanged(first, new HidppSnapshot(new[] { unit with { } }, Array.Empty<Guid>())));
            Assert.True(PeripheralOutputHost.ChargesChanged(first,
                new HidppSnapshot(new[] { unit with { BatteryPercent = 20 } }, Array.Empty<Guid>())));
            Assert.True(PeripheralOutputHost.ChargesChanged(first, HidppSnapshot.Empty));
            Assert.True(PeripheralOutputHost.ChargesChanged(HidppSnapshot.Empty, first));
            Assert.False(PeripheralOutputHost.ChargesChanged(null, HidppSnapshot.Empty));

            string host = RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            int charge = host.IndexOf("bool charge = ChargesChanged(PeripheralOutputs.Hidpp, hidpp);", StringComparison.Ordinal);
            int publish = host.IndexOf("PeripheralOutputs.Hidpp = hidpp;", StringComparison.Ordinal);
            int links = host.IndexOf("PeripheralOutputs.PublishLinks(table);", StringComparison.Ordinal);
            int relink = host.IndexOf("if (relinked) UserEffectsDispatcher.RequestPeripheralRefreshAll(evenUnlit: true);",
                StringComparison.Ordinal);
            int request = host.IndexOf("else if (charge) UserEffectsDispatcher.RequestPeripheralRefreshAll();", StringComparison.Ordinal);
            Assert.True(charge > 0 && publish > charge && links > publish && relink > links && request > relink,
                "the pass is asked for after the snapshot and the table are both published");

            // A slot keeps its claims while it holds a controller, which a
            // reorder moves before it rebuilds the slot's dispatcher.
            Assert.Contains("|| (hasController != null && hasController(slot))", host);
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("host.SlotHasController = pad => inputManager.HasVirtualControllerAt(pad);", service);
        }

        // ─────────────────────────────────────────────
        //  The linker
        // ─────────────────────────────────────────────

        private static LinkRow Row(Guid id, int capType, ushort vid, ushort pid, Guid container = default,
            PeripheralRowKind? kind = null) => new(id, capType, vid, pid, container, kind);

        private static PeripheralPresence Presence(bool ledSdk = false, bool gHub = false, bool synapse = false,
            bool gg = false, bool sensa = false) => new(ledSdk, gHub, synapse, gg, sensa);

        /// <summary>A Logitech device lit through feature 0x8070 takes its own
        /// unit while G HUB is not running, and its LED SDK type while it
        /// runs. The 0x8071 and per-key families have no direct path in this
        /// pass, so without G HUB nothing lights them.</summary>
        [Fact]
        public void ALogitechDevice_TakesItsUnitWithoutGHub_AndItsLedSdkTypeWithIt()
        {
            var container = Guid.NewGuid();
            var mouseUnit = new HidppUnit
            {
                ChannelPath = "rx", ContainerId = container, DeviceIndex = 1, DeviceType = 3,
                RgbFeatureId = HidppUnitProtocol.ColorLedEffects, RgbIndex = 0x0E, Zones = new[] { new HidppZone(0, 1) },
            };
            var keyboardUnit = new HidppUnit
            {
                ChannelPath = "rx", ContainerId = container, DeviceIndex = 2, DeviceType = 0,
                RgbFeatureId = HidppUnitProtocol.RgbEffects, RgbIndex = 0x0F,
            };
            var hidpp = new HidppSnapshot(new[] { mouseUnit, keyboardUnit }, Array.Empty<Guid>());
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, 0xC548, container),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.LogitechVid, 0xC548, container),
            };

            var direct = PeripheralLinker.Build(rows, hidpp, Presence(ledSdk: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, mouseUnit.Key) }, direct.For(A).Lighting);
            Assert.Null(direct.For(B));

            var throughGHub = PeripheralLinker.Build(rows, hidpp, Presence(ledSdk: true, gHub: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") }, throughGHub.For(A).Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.LedSdkType, "keyboard") }, throughGHub.For(B).Lighting);
            Assert.False(throughGHub.For(A).CatchAll);

            Assert.Null(PeripheralLinker.Build(rows, hidpp, Presence(gHub: true)).For(A));
        }

        [Fact]
        public void ARazerRowTakesItsChromaCategory_AKeypadIncluded()
        {
            var keypad = Guid.NewGuid();
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.RazerVid, 0x0084),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E),
                Row(keypad, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x0244),
            };
            var table = PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence(synapse: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") }, table.For(A).Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "keyboard") }, table.For(B).Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "keypad") }, table.For(keypad).Lighting);

            Assert.Empty(PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence()).ByDevice);
        }

        [Fact]
        public void ASteelSeriesRowTakesItsGameSenseType_AndATactileRivalItsRumbleToo()
        {
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.SteelSeriesVid, 0x1730),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.SteelSeriesVid, 0x1612),
            };
            var table = PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence(gg: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") }, table.For(A).Lighting);
            Assert.Equal(new[] { PeripheralLinker.GameSenseTactilePath }, table.For(A).Haptics);
            Assert.Equal(new[] { new OutputPath(OutputFamily.GameSenseColor, "keyboard") }, table.For(B).Lighting);
            Assert.Empty(table.For(B).Haptics);

            Assert.Empty(PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence()).ByDevice);
        }

        /// <summary>A Razer or SteelSeries row lights its device's kind by
        /// product ID, so a mouse's keyboard collection lights the mouse and a
        /// keyboard's mouse collection the keyboard. A device the lists lack
        /// goes by its row's kind, and an analog keyboard row is a
        /// keyboard.</summary>
        [Fact]
        public void EveryRowOfADevice_LightsThatDevicesKind()
        {
            string Razer(int capType, ushort pid) => PeripheralLinker.Build(
                new[] { Row(A, capType, PeripheralLinker.RazerVid, pid) }, HidppSnapshot.Empty, Presence(synapse: true))
                .For(A).Lighting.Single().Key;
            Assert.Equal("mouse", Razer(InputDeviceType.Keyboard, 0x0067));
            Assert.Equal("keyboard", Razer(InputDeviceType.Mouse, 0x024E));
            Assert.Equal("keypad", Razer(InputDeviceType.Mouse, 0x0244));
            Assert.Equal("keyboard", Razer(InputDeviceType.AnalogKeyboard, 0x0266));
            Assert.Equal("keyboard", Razer(InputDeviceType.Keyboard, 0x0FFF));
            Assert.Equal("mouse", Razer(InputDeviceType.Mouse, 0x0FFF));
            Assert.Empty(PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Gamepad, PeripheralLinker.RazerVid, 0x0067) },
                HidppSnapshot.Empty, Presence(synapse: true)).ByDevice);

            string SteelSeries(int capType, ushort pid) => PeripheralLinker.Build(
                new[] { Row(A, capType, PeripheralLinker.SteelSeriesVid, pid) }, HidppSnapshot.Empty, Presence(gg: true))
                .For(A).Lighting.Single().Key;
            Assert.Equal("keyboard", SteelSeries(InputDeviceType.Mouse, 0x1612));
            Assert.Equal("keyboard", SteelSeries(InputDeviceType.AnalogKeyboard, 0x1610));
            Assert.Equal("mouse", SteelSeries(InputDeviceType.Keyboard, 0x1824));

            Assert.All(PeripheralProductIds.RazerMice, pid => Assert.DoesNotContain(pid, PeripheralProductIds.RazerKeyboards));
            Assert.All(PeripheralProductIds.SteelSeriesMice,
                pid => Assert.DoesNotContain(pid, PeripheralProductIds.SteelSeriesKeyboards));
            Assert.All(PeripheralLinker.RazerKeypadPids, pid => Assert.Contains(pid, PeripheralProductIds.RazerKeyboards));
            Assert.All(PeripheralLinker.RivalTactilePids, pid => Assert.Contains(pid, PeripheralProductIds.SteelSeriesMice));

            string host = RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("if (!PeripheralLinker.IsLinkable(ud.CapType)) continue;", host);
        }

        /// <summary>The lighting rows take every path of their family while
        /// their vendor's software is installed, and open only then.</summary>
        [Fact]
        public void TheVendorLightingRows_TakeEveryPathOfTheirFamily_OnlyWithTheirSoftware()
        {
            var chroma = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma);
            var lightsync = PeripheralOutputRow.IdentityFor(PeripheralRowKind.LogitechLightsync);
            var gg = PeripheralOutputRow.IdentityFor(PeripheralRowKind.SteelSeriesGG);
            var rows = new[]
            {
                Row(chroma, InputDeviceType.PeripheralLighting, 0x5046, 0x4348, default, PeripheralRowKind.RazerChroma),
                Row(lightsync, InputDeviceType.PeripheralLighting, 0x5046, 0x4C53, default, PeripheralRowKind.LogitechLightsync),
                Row(gg, InputDeviceType.PeripheralLighting, 0x5046, 0x5347, default, PeripheralRowKind.SteelSeriesGG),
            };
            var all = PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence(ledSdk: true, synapse: true, gg: true));
            Assert.Equal(PeripheralLinker.ChromaCategories, all.For(chroma).Lighting.Select(p => p.Key));
            Assert.All(all.For(chroma).Lighting, p => Assert.Equal(OutputFamily.ChromaCategory, p.Family));
            Assert.Equal(PeripheralLinker.LedSdkTypes, all.For(lightsync).Lighting.Select(p => p.Key));
            Assert.Equal(PeripheralLinker.GameSenseColorTypes, all.For(gg).Lighting.Select(p => p.Key));
            Assert.True(all.For(chroma).CatchAll && all.For(lightsync).CatchAll && all.For(gg).CatchAll);
            Assert.Empty(PeripheralLinker.Build(rows, HidppSnapshot.Empty, Presence()).ByDevice);

            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerChroma, Presence(synapse: true)));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerChroma, Presence(gg: true)));
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.LogitechLightsync, Presence(ledSdk: true)));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.LogitechLightsync, Presence(gHub: true)));
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.SteelSeriesGG, Presence(gg: true)));
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, Presence(synapse: true)));
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, Presence(synapse: true, sensa: true)));
        }

        // ─────────────────────────────────────────────
        //  The tab's route line
        // ─────────────────────────────────────────────

        private static UserDevice Device(Guid id, ushort vid = PeripheralLinker.RazerVid)
            => new() { InstanceGuid = id, IsOnline = true, CapType = InputDeviceType.Mouse, VendorId = vid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting };

        /// <summary>A vendor row names what its own software reaches, and the
        /// waiting line is one sentence per locale, never two joined.</summary>
        [Fact]
        public void AVendorRowsLine_NamesItsOwnExamples()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var gg = PeripheralOutputRow.IdentityFor(PeripheralRowKind.SteelSeriesGG);
            var chroma = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma);
            Link(new DeviceLinks
                 {
                     Device = gg, CatchAll = true,
                     Lighting = PeripheralLinker.GameSenseColorTypes.Select(t => new OutputPath(OutputFamily.GameSenseColor, t)).ToArray(),
                 },
                 new DeviceLinks
                 {
                     Device = chroma, CatchAll = true,
                     Lighting = PeripheralLinker.ChromaCategories.Select(c => new OutputPath(OutputFamily.ChromaCategory, c)).ToArray(),
                 });

            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRow, "SteelSeries GG", s.Pad_Lighting_VendorRowExamples_GameSense),
                PeripheralRouteText.Lighting(Device(gg), 0));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRow, "Razer Synapse", s.Pad_Lighting_VendorRowExamples_Chroma),
                PeripheralRouteText.Lighting(Device(chroma), 0));

            PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Waiting);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRowWaiting, "Razer Synapse", s.Pad_Lighting_VendorRowExamples_Chroma),
                PeripheralRouteText.Lighting(Device(chroma), 0));
            Assert.Equal("headsets", PadForge.Resources.Strings.Strings.ResourceManager.GetString(
                "Pad_Lighting_VendorRowExamples_GameSense", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>A device type the Logitech LED engine on this PC cannot
        /// paint reads that instead of a route, on the tab that would light it
        /// only. A vendor row whose software is down reads the waiting line on
        /// every controller it is on, before any line naming the controller
        /// that rules.</summary>
        [Fact]
        public void TheRouteLine_NamesAnEngineThatCannotPaint_AndAWaitingVendorRowFirst()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var logi = Device(A, PeripheralLinker.LogitechVid);
            Link(new DeviceLinks { Device = A, Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") } });
            PeripheralOutputs.SetLighting(A, slot: 1, player: 2, 3, 3, 3);
            string through = string.Format(s.Pad_Lighting_RouteThrough, "Logitech G HUB");
            Assert.Equal(through, PeripheralRouteText.Lighting(logi, 1));

            PeripheralOutputs.LedSdkPaintable = new[] { PeripheralLinker.LedSdkWholeDevices, "keyboard" };
            Assert.Equal(s.Pad_Lighting_RouteCannotLight, PeripheralRouteText.Lighting(logi, 1));
            Assert.Null(PeripheralRouteText.Lighting(logi, 1, lightsHere: false));
            PeripheralOutputs.LedSdkPaintable = new[] { "mouse" };
            Assert.Equal(through, PeripheralRouteText.Lighting(logi, 1));

            var lightsync = PeripheralOutputRow.IdentityFor(PeripheralRowKind.LogitechLightsync);
            var row = Device(lightsync, PeripheralLinker.LogitechVid);
            Link(new DeviceLinks
            {
                Device = lightsync, CatchAll = true,
                Lighting = PeripheralLinker.LedSdkTypes.Select(t => new OutputPath(OutputFamily.LedSdkType, t)).ToArray(),
            });
            PeripheralOutputs.SetLighting(lightsync, slot: 0, player: 1, 4, 4, 4);
            PeripheralOutputs.SetLighting(lightsync, slot: 5, player: 3, 5, 5, 5);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherController, 1), PeripheralRouteText.Lighting(row, 5));
            PeripheralOutputs.SetBackendState(OutputFamily.LedSdkType, BackendState.Waiting);
            string waiting = string.Format(s.Pad_Lighting_RouteVendorRowWaiting, "Logitech G HUB",
                s.Pad_Lighting_VendorRowExamples_LedSdk);
            Assert.Equal(waiting, PeripheralRouteText.Lighting(row, 5));
            Assert.Equal(waiting, PeripheralRouteText.Lighting(row, 0));
        }

        /// <summary>The line names whose color shows when another claim rules
        /// the path: the same device on another controller, a vendor row by
        /// its own name, or another device sharing the path. The controller
        /// that rules reads its plain route, and a controller whose switch
        /// leaves the device alone shows only who lights it, never a route of
        /// its own. Every named number differs from its slot and from the slot
        /// plus one, so the line cannot pass by printing a slot.</summary>
        [Fact]
        public void TheRouteLine_NamesTheControllerWhoseColorShows()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var mouse = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            var chroma = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma);
            Link(new DeviceLinks { Device = A, Lighting = new[] { mouse } },
                 new DeviceLinks { Device = B, Lighting = new[] { mouse } });

            // The same mouse on two controllers.
            PeripheralOutputs.SetLighting(A, slot: 3, player: 1, 1, 1, 1);
            PeripheralOutputs.SetLighting(A, slot: 5, player: 2, 2, 2, 2);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThrough, "Razer Synapse"), PeripheralRouteText.Lighting(Device(A), 3));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherController, 1), PeripheralRouteText.Lighting(Device(A), 5));

            // Switched off on the controller that would rule it: the other one
            // lights it, and this tab says so without a route of its own.
            PeripheralOutputs.ReleaseLighting(A, 3);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 2),
                PeripheralRouteText.Lighting(Device(A), 3, lightsHere: false));
            PeripheralOutputs.ReleaseDevice(A);
            Assert.Null(PeripheralRouteText.Lighting(Device(A), 3, lightsHere: false));

            // Another mouse on the shared path, true with the switch off too.
            PeripheralOutputs.SetLighting(B, slot: 4, player: 1, 3, 3, 3);
            PeripheralOutputs.SetLighting(A, slot: 2, player: 3, 4, 4, 4);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteShared, "Razer Synapse", 1), PeripheralRouteText.Lighting(Device(A), 2));
            PeripheralOutputs.ReleaseLighting(A, 2);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteShared, "Razer Synapse", 1),
                PeripheralRouteText.Lighting(Device(A), 2, lightsHere: false));

            // The Razer Chroma row rules a mouse whose own switch is off.
            PeripheralOutputs.ReleaseDevice(B);
            Link(new DeviceLinks { Device = A, Lighting = new[] { mouse } },
                 new DeviceLinks { Device = chroma, CatchAll = true, Lighting = new[] { mouse } });
            PeripheralOutputs.SetLighting(chroma, slot: 6, player: 1, 5, 5, 5);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteSharedVendorRow, "Razer Synapse", "Razer Chroma", 1),
                PeripheralRouteText.Lighting(Device(A), 2, lightsHere: false));

            // The same vendor row on two controllers: the smaller number rules
            // every path, and the other controller's tab names it.
            PeripheralOutputs.SetLighting(chroma, slot: 9, player: 4, 6, 6, 6);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherController, 1), PeripheralRouteText.Lighting(Device(chroma), 9));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRow, "Razer Synapse", s.Pad_Lighting_VendorRowExamples_Chroma),
                PeripheralRouteText.Lighting(Device(chroma), 6));

            // A unit lit directly, on two controllers.
            PeripheralOutputs.ReleaseDevice(chroma);
            var unit = new OutputPath(OutputFamily.HidppUnit, "rx|01");
            var logi = Device(A, PeripheralLinker.LogitechVid);
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit } });
            PeripheralOutputs.SetLighting(A, slot: 4, player: 2, 6, 6, 6);
            PeripheralOutputs.SetLighting(A, slot: 7, player: 1, 7, 7, 7);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherController, 1), PeripheralRouteText.Lighting(logi, 4));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, logi.ResolvedName), PeripheralRouteText.Lighting(logi, 7));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 1),
                PeripheralRouteText.Lighting(logi, 9, lightsHere: false));

            // Two rows of one unit lit directly, on one controller: the lower
            // device id rules, and the other row's tab names it.
            PeripheralOutputs.ReleaseLighting(A, 7);
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit } },
                 new DeviceLinks { Device = B, Lighting = new[] { unit } });
            var logiB = Device(B, PeripheralLinker.LogitechVid);
            PeripheralOutputs.SetLighting(B, slot: 4, player: 2, 8, 8, 8);
            Assert.Equal(s.Pad_Lighting_RouteOtherRow, PeripheralRouteText.Lighting(logiB, 4));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, logi.ResolvedName), PeripheralRouteText.Lighting(logi, 4));
            PeripheralOutputs.ReleaseLighting(B, 4);
            Assert.Equal(s.Pad_Lighting_RouteOtherRowOnly, PeripheralRouteText.Lighting(logiB, 4, lightsHere: false));

            Assert.Equal("Razer Chroma", PeripheralOutputRow.NameFor(PeripheralRowKind.RazerChroma));
            Assert.Equal(PeripheralRowKind.SteelSeriesGG,
                PeripheralOutputRow.KindOf(PeripheralOutputRow.IdentityFor(PeripheralRowKind.SteelSeriesGG)));
            Assert.Null(PeripheralOutputRow.KindOf(A));
        }

        // ─────────────────────────────────────────────
        //  The game's lightbar
        // ─────────────────────────────────────────────

        /// <summary>The DualSense's rules per controller: a write is the
        /// game's for the grace window, then held while the game sends
        /// anything, then gone fifteen seconds after its last frame, and a
        /// frame after the lapse does not bring the color back.</summary>
        [Fact]
        public void AGamesColorIsFreshForTheGrace_HeldWhileTheGameRuns_ThenGone()
        {
            var bar = new GameLightbar();
            long t0 = 1_000_000;
            Assert.True(bar.Capture(1, 2, 3, t0));
            bar.Read(t0 + 10, out int? fresh, out int? last);
            Assert.Equal(0x010203, fresh);
            Assert.Equal(0x010203, last);
            Assert.Equal(GameLightbarPhase.Fresh, bar.Phase(t0 + 10));

            Assert.False(bar.Capture(1, 2, 3, t0 + 100));
            Assert.True(bar.Capture(4, 5, 6, t0 + 200));

            long t1 = t0 + 200 + GameLightbarCapture.GraceMs;
            bar.Read(t1, out fresh, out last);
            Assert.Null(fresh);
            Assert.Equal(0x040506, last);
            Assert.Equal(GameLightbarPhase.Held, bar.Phase(t1));
            Assert.True(bar.Capture(4, 5, 6, t1), "the same color after the grace window is a change");

            long frame = t1 + GameLightbarCapture.LapseMs - 1;
            bar.NoteFrame(frame);
            bar.Read(t1 + GameLightbarCapture.LapseMs + 100, out _, out last);
            Assert.Equal(0x040506, last);

            long gone = frame + GameLightbarCapture.LapseMs;
            Assert.Equal(GameLightbarPhase.None, bar.Phase(gone));
            bar.NoteFrame(gone + 10);
            bar.Read(gone + 20, out fresh, out last);
            Assert.Null(fresh);
            Assert.Null(last);
        }

        /// <summary>A reorder swap moves each controller's color with it,
        /// whichever controller moves first.</summary>
        [Fact]
        public void AReorderSwap_MovesEachControllersColorWithIt()
        {
            var first = new GameLightbar();
            var second = new GameLightbar();
            GameLightbarCapture.Publish(0, first);
            GameLightbarCapture.Publish(1, second);
            long now = Environment.TickCount64;
            first.Capture(0xAA, 0, 0, now);
            second.Capture(0, 0xBB, 0, now);

            GameLightbarCapture.Withdraw(0, first);
            GameLightbarCapture.Publish(1, first);
            GameLightbarCapture.Withdraw(1, second);
            GameLightbarCapture.Publish(0, second);

            GameLightbarCapture.Read(0, now, out int? fresh, out _);
            Assert.Equal(0x00BB00, fresh);
            GameLightbarCapture.Read(1, now, out fresh, out _);
            Assert.Equal(0xAA0000, fresh);

            GameLightbarCapture.WithdrawEverywhere(first);
            GameLightbarCapture.Read(1, now, out fresh, out _);
            Assert.Null(fresh);

            GameLightbarCapture.Forget(0);
            GameLightbarCapture.Read(0, now, out fresh, out int? last);
            Assert.Null(fresh);
            Assert.Null(last);
        }

        /// <summary>The capture reads the decoded lightbar only behind the
        /// trust gate, the global Chroma and LIGHTSYNC publishes are gone, the
        /// color moves and leaves with its controller, and an assignment
        /// change forgets it beside the DualSense mirror's record.</summary>
        [Fact]
        public void TheCaptureSitsBehindTheTrustGate_AndTheGlobalMirrorsAreGone()
        {
            string hm = RepoText("PadForge.App", "Common", "Input", "HMaestroVirtualController.cs");
            int gate = hm.IndexOf("&& SonyFrameValid(e.RawBytes.Length, declaredSize, e.CrcValid))", StringComparison.Ordinal);
            int capture = hm.IndexOf("_gameLightbar.Capture(lbRgb[0], lbRgb[1], lbRgb[2], lbNow)", StringComparison.Ordinal);
            int declared = hm.IndexOf("int declaredSize = _profile.ExtendedOutputReport?.Size ?? -1;", StringComparison.Ordinal);
            Assert.True(declared > 0 && gate > declared && capture > gate, "the capture follows the trust gate");
            // The gated block runs from the brace after the gate to the brace
            // that closes it.
            int open = hm.IndexOf('{', gate), depth = 0, blockEnd = -1;
            for (int i = open; open > 0 && i < hm.Length; i++)
            {
                if (hm[i] == '{') depth++;
                else if (hm[i] == '}' && --depth == 0) { blockEnd = i; break; }
            }
            Assert.True(open > gate && capture > open && blockEnd > capture, "the capture sits inside the gated block");
            Assert.DoesNotContain("source.Clear()",
                RepoText("PadForge.App", "Common", "Input", "Peripherals", "GameLightbarCapture.cs"));
            Assert.Contains("if (!lbValid) _gameLightbar.NoteFrame(lbNow);", hm);
            Assert.Contains("Peripherals.GameLightbarCapture.Withdraw(oldPadIndex, _gameLightbar);", hm);
            Assert.Contains("Peripherals.GameLightbarCapture.Publish(newPadIndex, _gameLightbar);", hm);
            Assert.Contains("Peripherals.GameLightbarCapture.Withdraw(pad, _gameLightbar);", hm);
            Assert.Contains("Peripherals.GameLightbarCapture.WithdrawEverywhere(_gameLightbar);", hm);
            Assert.DoesNotContain("ChromaLightbarService", hm);
            Assert.DoesNotContain("LightsyncLightbarService", hm);

            // Only the controller that wins its slot publishes its color.
            int prepare = hm.IndexOf("internal UserEffectsDispatcher PrepareDeviceEffectsForPublication(", StringComparison.Ordinal);
            int winner = hm.IndexOf("Peripherals.GameLightbarCapture.Publish(FeedbackPadIndex, _gameLightbar);", StringComparison.Ordinal);
            Assert.True(prepare > 0 && winner > prepare && winner - prepare < 600, "the winner publishes");

            string dispatcher = RepoText("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            int external = dispatcher.IndexOf("s_externalState.Remove(_padIndex);", StringComparison.Ordinal);
            int forget = dispatcher.IndexOf("if (!firstLook) Common.Input.Peripherals.GameLightbarCapture.Forget(_padIndex);",
                StringComparison.Ordinal);
            Assert.True(external > 0 && forget > external && forget - external < 500);
        }

        // ─────────────────────────────────────────────
        //  The color
        // ─────────────────────────────────────────────

        /// <summary>The DualSense's order: the game's fresh write wins over
        /// every setting, a device left at the player-number default keeps
        /// the game's last color, and anything configured comes back.</summary>
        [Fact]
        public void TheColorFollowsTheDualSensesOrder()
        {
            void Resolve(DeviceSlotConfig cfg, int? fresh, int? last, int player, out int rgb)
            {
                PeripheralLightingColor.Resolve(cfg, fresh, last, 0f, 0, 0, 0, 0f, 100, player,
                    out byte r, out byte g, out byte b);
                rgb = (r << 16) | (g << 8) | b;
            }
            int Pack((byte R, byte G, byte B) c) => (c.R << 16) | (c.G << 8) | c.B;

            var standard = new DeviceSlotConfig();
            Resolve(standard, 0x112233, 0x112233, 1, out int rgb);
            Assert.Equal(0x112233, rgb);
            Resolve(standard, null, 0x445566, 1, out rgb);
            Assert.Equal(0x445566, rgb);
            Resolve(standard, null, null, 2, out rgb);
            Assert.Equal(Pack(PlayerIdentityDefaults.ColorFor(2)), rgb);

            var fixedColor = new DeviceSlotConfig
            {
                LightbarMode = LightbarMode.Static, LightbarRed = 0x10, LightbarGreen = 0x20, LightbarBlue = 0x30,
            };
            Resolve(fixedColor, null, 0x445566, 1, out rgb);
            Assert.Equal(0x102030, rgb);
            Resolve(fixedColor, 0x778899, 0x778899, 1, out rgb);
            Assert.Equal(0x778899, rgb);

            var macro = new DeviceSlotConfig
            {
                MacroOverrideR = 9, MacroOverrideG = 8, MacroOverrideB = 7,
                MacroOverrideHoldMode = MacroLightbarHoldMode.Sticky,
                MacroOverrideExpiresAtUtc = DateTime.UtcNow.AddMinutes(1),
            };
            Resolve(macro, null, 0x445566, 1, out rgb);
            Assert.Equal(0x090807, rgb);
            Resolve(macro, 0x778899, 0x778899, 1, out rgb);
            Assert.Equal(0x778899, rgb);
        }

        // ─────────────────────────────────────────────
        //  The dispatcher's lane
        // ─────────────────────────────────────────────

        /// <summary>A lit mouse on a virtual controller takes that slot's
        /// color only while its Lighting tab controls it, ranked by the slot's
        /// displayed number, under the game's lightbar while the game writes
        /// one. It lets go when it leaves the slot, and the link pass drops the
        /// slot's claims once its dispatcher is gone. A vendor row needs no
        /// switch.</summary>
        [Fact]
        public void AnAssignedMouse_TakesItsSlotsColor_OnlyWhileItsTabControlsIt()
        {
            var oldDevices = SettingsManager.UserDevices;
            var oldSettings = SettingsManager.UserSettings;
            var oldCreated = SettingsManager.SlotCreated;
            var oldEnabled = SettingsManager.SlotEnabled;
            var oldOrder = SettingsManager.XboxSlotOrder;
            var oldConfigs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            UserEffectsDispatcher dispatcher = null;
            PeripheralOutputRow vendorRow = null;
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[16];
                SettingsManager.SlotEnabled = new bool[16];
                SettingsManager.SlotCreated[15] = SettingsManager.SlotEnabled[15] = true;
                SettingsManager.XboxSlotOrder = new List<int> { 15 };

                var path = new OutputPath(OutputFamily.HidppUnit, "rx|01");
                var mouse = new UserDevice
                {
                    InstanceGuid = A, IsOnline = true, IsEnabled = true, CapType = InputDeviceType.Mouse,
                    VendorId = PeripheralLinker.LogitechVid, ProdId = 0xC548,
                    PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
                };
                SettingsManager.UserDevices.Items.Add(mouse);
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = A, MapTo = 15 });
                Link(new DeviceLinks { Device = A, Lighting = new[] { path } });

                var cfg = new DeviceSlotConfig
                {
                    LightbarMode = LightbarMode.Static, LightbarRed = 16, LightbarGreen = 20, LightbarBlue = 24,
                };
                var configs = new Dictionary<Guid, DeviceSlotConfig> { [A] = cfg };
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => configs;
                dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);

                dispatcher.ApplyOnce();
                Assert.False(PeripheralOutputs.IsLit(A), "the device's own software keeps its lighting");

                cfg.PeripheralLightingEnabled = true;
                dispatcher.ApplyOnce();
                Assert.True(PeripheralOutputs.TryResolveColor(path, out int rgb, out var ruler));
                Assert.Equal(0x101418, rgb);
                Assert.Equal(15, ruler.Slot);
                Assert.Equal(1, ruler.Player);

                var bar = new GameLightbar();
                GameLightbarCapture.Publish(15, bar);
                bar.Capture(0xAB, 0xCD, 0xEF, Environment.TickCount64);
                dispatcher.ApplyOnce();
                Assert.True(PeripheralOutputs.TryResolveColor(path, out rgb));
                Assert.Equal(0xABCDEF, rgb);
                // The published color's watcher would queue passes of its own
                // into the rest of the test.
                GameLightbarCapture.ResetForTest();

                cfg.PeripheralLightingEnabled = false;
                dispatcher.ApplyOnce();
                Assert.False(PeripheralOutputs.IsLit(A), "switching control off hands the lighting back");

                // A vendor row is lit with no switch.
                vendorRow = new PeripheralOutputRow(PeripheralRowKind.RazerChroma);
                var vendor = new UserDevice();
                vendor.LoadFromExternalDevice(vendorRow);
                vendor.IsOnline = true;
                vendor.PeripheralOutputs = (int)PeripheralOutputKinds.Lighting;
                SettingsManager.UserDevices.Items.Add(vendor);
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = vendor.InstanceGuid, MapTo = 15 });
                configs[vendor.InstanceGuid] = new DeviceSlotConfig();
                Link(new DeviceLinks { Device = A, Lighting = new[] { path } },
                     new DeviceLinks
                     {
                         Device = vendor.InstanceGuid, CatchAll = true,
                         Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "headset") },
                     });
                dispatcher.ApplyOnce();
                Assert.True(PeripheralOutputs.IsLit(vendor.InstanceGuid));

                // Leaving the slot releases the claim, and the dispatcher's
                // end releases the slot's.
                cfg.PeripheralLightingEnabled = true;
                dispatcher.ApplyOnce();
                Assert.True(PeripheralOutputs.IsLit(A));
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.RemoveAll(s => s.InstanceGuid == A);
                dispatcher.ApplyOnce();
                Assert.False(PeripheralOutputs.IsLit(A));

                // A reorder disposes a slot's dispatcher and registers its
                // replacement a moment later, so the end of one leaves its
                // claims to the link pass, which drops a slot with no live
                // dispatcher.
                dispatcher.Dispose();
                dispatcher = null;
                Assert.True(PeripheralOutputs.IsLit(vendor.InstanceGuid));
                PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)> { (vendor.InstanceGuid, 15) },
                    UserEffectsDispatcher.HasLiveDispatcher);
                Assert.False(PeripheralOutputs.IsLit(vendor.InstanceGuid));
            }
            finally
            {
                dispatcher?.Dispose();
                vendorRow?.Dispose();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = oldConfigs;
                SettingsManager.UserDevices = oldDevices;
                SettingsManager.UserSettings = oldSettings;
                SettingsManager.SlotCreated = oldCreated;
                SettingsManager.SlotEnabled = oldEnabled;
                SettingsManager.XboxSlotOrder = oldOrder;
            }
        }

        /// <summary>A mouse whose tab leaves its lighting alone keeps no
        /// animation timer running, whatever mode its config carries.</summary>
        [Fact]
        public void AnUncontrolledPeripheralConfig_DrivesNoAnimation()
        {
            var animated = new DeviceSlotConfig { LightbarMode = LightbarMode.Rainbow };
            Link(new DeviceLinks { Device = A, Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") } },
                 new DeviceLinks
                 {
                     Device = Vendor, CatchAll = true,
                     Lighting = new[] { new OutputPath(OutputFamily.ChromaCategory, "headset") },
                 });
            Assert.False(UserEffectsDispatcher.LightingDriven(A, animated));
            animated.PeripheralLightingEnabled = true;
            Assert.True(UserEffectsDispatcher.LightingDriven(A, animated));
            Assert.True(UserEffectsDispatcher.LightingDriven(Vendor, new DeviceSlotConfig()));
            // Any device without a lighting path keeps the old rule.
            Assert.True(UserEffectsDispatcher.LightingDriven(B, new DeviceSlotConfig()));
        }

        /// <summary>A peripheral with no path now follows what the slot's last
        /// pass saw: an online mouse or keyboard follows its switch, and a
        /// vendor row or an offline device drives nothing. A device the pass
        /// never saw keeps the old rule.</summary>
        [Fact]
        public void APeripheralWithNoPathNow_FollowsWhatTheLastPassSaw()
        {
            var animated = new DeviceSlotConfig { LightbarMode = LightbarMode.Rainbow };
            var seen = new Dictionary<Guid, bool> { [A] = true, [Vendor] = false };
            Assert.False(UserEffectsDispatcher.LightingDriven(A, animated, seen));
            animated.PeripheralLightingEnabled = true;
            Assert.True(UserEffectsDispatcher.LightingDriven(A, animated, seen));
            Assert.False(UserEffectsDispatcher.LightingDriven(Vendor, animated, seen));
            Assert.True(UserEffectsDispatcher.LightingDriven(B, new DeviceSlotConfig(), seen));
        }

        /// <summary>A sleeping mouse, its path gone, keeps no animation timer
        /// running while its switch is off, and keeps it while the switch is
        /// on, so the animation resumes when it wakes.</summary>
        [Fact]
        public void ASleepingMouse_DrivesTheTimerOnlyWhileItsSwitchIsOn()
        {
            using var slot = new LitSlot();
            slot.Config.LightbarMode = LightbarMode.Rainbow;
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
            slot.Dispatcher.ApplyOnce();
            var active = typeof(UserEffectsDispatcher).GetField("_animTickActive", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(active);

            // The mouse sleeps: its path leaves the table.
            PeripheralOutputs.PublishLinks(new LinkTable(Array.Empty<DeviceLinks>()));
            slot.Config.LightbarPeriodMs += 1;
            Assert.False((bool)active.GetValue(slot.Dispatcher), "a sleeping mouse switched off drives no timer");

            slot.Config.PeripheralLightingEnabled = true;
            Assert.True((bool)active.GetValue(slot.Dispatcher), "a sleeping mouse switched on keeps its animation");
            slot.Config.PeripheralLightingEnabled = false;
        }

        /// <summary>A wired or Bluetooth Logitech device is the only unit in
        /// its container, so every row of it, its keyboard collection
        /// included, takes it, and under G HUB lights the unit's own kind.
        /// On a receiver a typed unit still goes to its own kind's row.</summary>
        [Fact]
        public void EveryRowOfADirectLogitechDevice_TakesItsUnit()
        {
            var container = Guid.NewGuid();
            var direct = new HidppUnit
            {
                ChannelPath = "usb", ContainerId = container,
                DeviceIndex = PadForge.Common.Input.HidppHapticProtocol.DirectDeviceIndex, DeviceType = 3,
                RgbFeatureId = HidppUnitProtocol.ColorLedEffects, RgbIndex = 0x0E, Zones = new[] { new HidppZone(0, 1) },
            };
            var hidpp = new HidppSnapshot(new[] { direct }, Array.Empty<Guid>());
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, 0xC08B, container),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.LogitechVid, 0xC08B, container),
            };
            var own = PeripheralLinker.Build(rows, hidpp, Presence(ledSdk: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, direct.Key) }, own.For(A).Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, direct.Key) }, own.For(B).Lighting);
            Assert.Equal(new[] { direct.Key }, own.For(B).HidppUnits);

            var gHub = PeripheralLinker.Build(rows, hidpp, Presence(ledSdk: true, gHub: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") }, gHub.For(B).Lighting);

            var paired = direct with { DeviceIndex = 1 };
            var receiver = PeripheralLinker.Build(rows, new HidppSnapshot(new[] { paired }, Array.Empty<Guid>()), Presence(ledSdk: true));
            Assert.NotNull(receiver.For(A));
            Assert.Null(receiver.For(B));
        }

        /// <summary>The Huntsman V3 boards the analog keyboard reader knows,
        /// openrazer's list lacking some, light as keyboards from any
        /// row.</summary>
        [Fact]
        public void EveryHuntsmanV3Row_LightsTheKeyboardCategory()
        {
            int found = 0;
            for (int pid = 0x0200; pid <= 0x02FF; pid++)
            {
                if (!PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardCatalog.IsRazerHuntsmanV3((ushort)pid)) continue;
                found++;
                var table = PeripheralLinker.Build(new[] { Row(A, InputDeviceType.Mouse, PeripheralLinker.RazerVid, (ushort)pid) },
                    HidppSnapshot.Empty, Presence(synapse: true));
                Assert.Equal("keyboard", table.For(A).Lighting.Single().Key);
            }
            Assert.True(found >= 11, "the catalog names the Huntsman V3 family");
        }

        /// <summary>A unit whose type answer went missing is asked again, so a
        /// receiver's mouse never lands on its keyboard row for a lost reply,
        /// while a device that refuses the request keeps an unknown
        /// type.</summary>
        [Fact]
        public void AMissedTypeAnswer_IsAskedAgain_WhileARefusalLeavesItUnknown()
        {
            int typeCalls = 0;
            var answer = LitMouse(1);
            var channel = new HidppHapticsTests.FakeChannel { Path = "rx" };
            channel.Answer = (d, f, fn, p) =>
            {
                if (d == 1 && f == 0x03 && fn == 2 && ++typeCalls == 1) return HidppHapticsTests.NoAnswer();
                return answer(d, f, fn, p);
            };
            var first = HidppUnitProbe.Describe(channel, 1, 0x03, Guid.Empty, 50, out bool incomplete);
            Assert.True(incomplete);
            Assert.Equal(-1, first.DeviceType);
            var second = HidppUnitProbe.Describe(channel, 1, 0x03, Guid.Empty, 50, out incomplete);
            Assert.False(incomplete);
            Assert.Equal(3, second.DeviceType);

            channel.Answer = (d, f, fn, p) => d == 1 && f == 0x03 && fn == 2
                ? new HidppReply(HidppReplyKind.Error, 0x02)
                : answer(d, f, fn, p);
            var refused = HidppUnitProbe.Describe(channel, 1, 0x03, Guid.Empty, 50, out incomplete);
            Assert.False(incomplete);
            Assert.Equal(-1, refused.DeviceType);
        }

        /// <summary>A slot whose dispatcher sits at the test's statics: an
        /// online Logitech mouse on slot 15, linked to one HID++ unit.</summary>
        private sealed class LitSlot : IDisposable
        {
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly bool[] _created = SettingsManager.SlotCreated;
            private readonly bool[] _enabled = SettingsManager.SlotEnabled;
            private readonly List<int> _order = SettingsManager.XboxSlotOrder;
            private readonly Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> _configs =
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            public readonly OutputPath Path = new(OutputFamily.HidppUnit, "rx|01");
            public readonly DeviceSlotConfig Config = new()
            {
                LightbarMode = LightbarMode.Static, LightbarRed = 16, LightbarGreen = 20, LightbarBlue = 24,
            };
            public readonly Dictionary<Guid, DeviceSlotConfig> Configs = new();
            public UserEffectsDispatcher Dispatcher;

            public LitSlot()
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[16];
                SettingsManager.SlotEnabled = new bool[16];
                SettingsManager.SlotCreated[15] = SettingsManager.SlotEnabled[15] = true;
                SettingsManager.XboxSlotOrder = new List<int> { 15 };
                Add(A);
                Configs[A] = Config;
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => Configs;
                Link(new DeviceLinks { Device = A, Lighting = new[] { Path } });
            }

            public void Add(Guid device)
            {
                SettingsManager.UserDevices.Items.Add(new UserDevice
                {
                    InstanceGuid = device, IsOnline = true, IsEnabled = true, CapType = InputDeviceType.Mouse,
                    VendorId = PeripheralLinker.LogitechVid, ProdId = 0xC548,
                    PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
                });
                lock (SettingsManager.UserSettings.SyncRoot)
                    SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = device, MapTo = 15 });
            }

            public void Dispose()
            {
                Dispatcher?.Dispose();
                GameLightbarCapture.ResetForTest();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _configs;
                SettingsManager.UserDevices = _devices;
                SettingsManager.UserSettings = _settings;
                SettingsManager.SlotCreated = _created;
                SettingsManager.SlotEnabled = _enabled;
                SettingsManager.XboxSlotOrder = _order;
            }
        }

        /// <summary>Turning on a mouse's lighting control with an animated
        /// mode starts the slot's animation timer, and turning it off stops
        /// it, through the config's own change events.</summary>
        [Fact]
        public void TheControlSwitch_StartsAndStopsTheAnimationTimer()
        {
            using var slot = new LitSlot();
            slot.Config.LightbarMode = LightbarMode.Rainbow;
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: true);
            var active = typeof(UserEffectsDispatcher).GetField("_animTickActive", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(active);
            Assert.False((bool)active.GetValue(slot.Dispatcher), "an uncontrolled mouse drives no timer");

            slot.Config.PeripheralLightingEnabled = true;
            Assert.True((bool)active.GetValue(slot.Dispatcher), "the switch started the timer");

            slot.Config.PeripheralLightingEnabled = false;
            Assert.False((bool)active.GetValue(slot.Dispatcher), "the switch stopped it");
        }

        /// <summary>A reorder moves a controller's game color to its new slot
        /// and rebuilds the slot's dispatcher, so the new dispatcher's first
        /// look keeps the color. A later change of the slot's devices forgets
        /// it, as it does the DualSense's own record.</summary>
        [Fact]
        public void ANewDispatchersFirstLook_KeepsTheGameColor_AndALaterChangeForgetsIt()
        {
            using var slot = new LitSlot();
            var bar = new GameLightbar();
            GameLightbarCapture.Publish(15, bar);
            bar.Capture(0x12, 0x34, 0x56, Environment.TickCount64);

            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
            slot.Dispatcher.ApplyOnce();
            GameLightbarCapture.Read(15, Environment.TickCount64, out _, out int? last);
            Assert.Equal(0x123456, last);

            slot.Add(B);
            slot.Dispatcher.ApplyOnce();
            GameLightbarCapture.Read(15, Environment.TickCount64, out int? fresh, out last);
            Assert.Null(fresh);
            Assert.Null(last);
        }

        /// <summary>A refresh asked for from outside the slot (the game's
        /// lightbar, a charge) repaints the slot's lit peripherals on the
        /// thread pool and settles back to idle. The pass is the peripherals'
        /// alone, so no Sony effect report goes out for it.</summary>
        [Fact]
        public void APeripheralRefresh_RepaintsTheLitDevices_AndSettles()
        {
            using var slot = new LitSlot();
            slot.Config.PeripheralLightingEnabled = true;
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
            slot.Dispatcher.ApplyOnce();
            Assert.True(PeripheralOutputs.TryResolveColor(slot.Path, out int rgb));
            Assert.Equal(0x101418, rgb);

            int visits = slot.Dispatcher.FullLaneVisits;
            Assert.True(visits > 0, "the full pass counts its visits");

            var bar = new GameLightbar();
            GameLightbarCapture.Publish(15, bar);
            bar.Capture(0x0A, 0x0B, 0x0C, Environment.TickCount64);
            UserEffectsDispatcher.RequestPeripheralRefresh(15);
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.TryResolveColor(slot.Path, out int c) && c == 0x0A0B0C, 3000),
                "the refresh painted the game's color");
            var state = typeof(UserEffectsDispatcher).GetField("_peripheralRefreshState",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(state);
            Assert.True(SpinWait.SpinUntil(() => (int)state.GetValue(slot.Dispatcher) == 0, 3000), "the refresh settled");
            Assert.Equal(visits, slot.Dispatcher.FullLaneVisits);

            string dispatcher = RepoText("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            Assert.Contains("DispatchSnapshot(peripheralsOnly: true);", dispatcher);
        }

        // ─────────────────────────────────────────────
        //  The Logitech worker's lighting
        // ─────────────────────────────────────────────

        /// <summary>A mouse with feature 0x8070 at index 0x0E, one zone whose
        /// second effect is the static one, and a 0x1004 battery at 0x08
        /// reporting 55 percent.</summary>
        private static Func<byte, byte, byte, byte[], HidppReply> LitMouse(byte deviceIndex)
        {
            const byte nameFeature = 0x03, rgb = 0x0E, battery = 0x08;
            return (d, f, fn, p) =>
            {
                if (d != deviceIndex) return HidppHapticsTests.NoAnswer();
                if (f == 0 && fn == 0)
                {
                    int id = (p[0] << 8) | p[1];
                    byte index = id switch
                    {
                        0x0005 => nameFeature,
                        HidppUnitProtocol.ColorLedEffects => rgb,
                        HidppUnitProtocol.UnifiedBattery => battery,
                        _ => 0,
                    };
                    return HidppHapticsTests.Ans(d, 0, 0, index);
                }
                if (f == nameFeature && fn == 2) return HidppHapticsTests.Ans(d, f, 2, 3);
                if (f == nameFeature && fn == 0) return HidppHapticsTests.Ans(d, f, 0, 4);
                if (f == nameFeature && fn == 1) return HidppHapticsTests.Ans(d, f, 1, (byte)'G', (byte)'5', (byte)'0', (byte)'2');
                if (f == rgb && fn == 0) return HidppHapticsTests.Ans(d, f, 0, 1);
                if (f == rgb && fn == 1) return HidppHapticsTests.Ans(d, f, 1, p[0], 0x00, 0x01, 2);
                if (f == rgb && fn == 2) return HidppHapticsTests.Ans(d, f, 2, p[0], p[1], 0x00, p[1] == 1 ? (byte)0x01 : (byte)0x00);
                if (f == battery && fn == 1) return HidppHapticsTests.Ans(d, f, 1, 55, 8);
                return HidppHapticsTests.NoAnswer();
            };
        }

        /// <summary>The first color claims the lighting with SetSWControl
        /// [01 01] and paints the zone's static effect, a new color paints
        /// again, and the release hands the lighting back with [00 00].</summary>
        [Fact]
        public void ADirectlyLitUnit_IsClaimedPaintedAndHandedBack()
        {
            var receiver = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#bench",
                VendorId = 0x046D, ProductId = 0xC539, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
            };
            var channel = new HidppHapticsTests.FakeChannel { Path = receiver.Path, Answer = LitMouse(1) };
            using var backend = new HidppBackend(() => new[] { receiver }, (c, s) => channel, _ => Guid.NewGuid(), 1, 5);
            backend.Start();

            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
            var unit = backend.Snapshot.Units[0];
            Assert.True(unit.DirectRgb);
            Assert.Equal(new[] { new HidppZone(0, 1) }, unit.Zones);
            Assert.Equal(55, unit.BatteryPercent);

            var device = Guid.NewGuid();
            var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
            while (channel.Writes.TryDequeue(out _)) { }

            byte[] claim = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: true);
            byte[] red = HidppUnitProtocol.SetStaticColor(1, 0x0E, new HidppZone(0, 1), 0xFF, 0x00, 0x00);
            byte[] blue = HidppUnitProtocol.SetStaticColor(1, 0x0E, new HidppZone(0, 1), 0x00, 0x00, 0xFF);
            byte[] release = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: false);
            bool Wrote(byte[] frame) => channel.Writes.Any(w => w.Frame.SequenceEqual(frame));

            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
            Assert.True(SpinWait.SpinUntil(() => Wrote(red), 3000), "the zone was painted");
            var frames = channel.Writes.Select(w => w.Frame).ToList();
            int claimAt = frames.FindIndex(f => f.SequenceEqual(claim));
            Assert.True(claimAt >= 0 && claimAt < frames.FindIndex(f => f.SequenceEqual(red)),
                "the claim came before the color");

            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x00, 0x00, 0xFF);
            Assert.True(SpinWait.SpinUntil(() => Wrote(blue), 3000), "the new color was painted");

            PeripheralOutputs.ReleaseLighting(device, 0);
            Assert.True(SpinWait.SpinUntil(() => Wrote(release), 3000), "the lighting was handed back");
        }

        private static (HidppBackend Backend, HidppHapticsTests.FakeChannel Channel, VendorHidCollection Receiver, OutputPath Path)
            LitBench(int reassertMs = HidppBackend.ReassertMs, Func<string, byte[], int, bool> writeOnce = null)
        {
            var receiver = new VendorHidCollection
            {
                Path = @"\\?\hid#vid_046d&pid_c539&mi_02&col02#bench",
                VendorId = 0x046D, ProductId = 0xC539, UsagePage = 0xFF00, Usage = 0x0002, InputReportLength = 20,
            };
            var channel = new HidppHapticsTests.FakeChannel { Path = receiver.Path, Answer = LitMouse(1) };
            var backend = new HidppBackend(() => new[] { receiver }, (c, s) => channel, _ => Guid.NewGuid(), 1, 5,
                reassertMs, writeOnce);
            backend.Start();
            Assert.True(SpinWait.SpinUntil(() => backend.Snapshot.Units.Count == 1, 5000), "the scan found the mouse");
            return (backend, channel, receiver, new OutputPath(OutputFamily.HidppUnit, backend.Snapshot.Units[0].Key));
        }

        /// <summary>The claim goes out again on its own interval while the
        /// color keeps changing, so an animated mode never holds back the
        /// re-claim a device that comes back from sleep needs.</summary>
        [Fact]
        public void TheClaimIsSentAgainOnItsInterval_WhileTheColorKeepsChanging()
        {
            var (backend, channel, _, path) = LitBench(reassertMs: 200);
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                byte[] claim = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: true);
                int Claims() => channel.Writes.Count(w => w.Frame.SequenceEqual(claim));

                long until = Environment.TickCount64 + 900;
                for (byte i = 1; Environment.TickCount64 < until; i++)
                {
                    PeripheralOutputs.SetLighting(device, slot: 0, player: 1, i, 0x00, 0x00);
                    Thread.Sleep(20);
                }
                Assert.True(Claims() >= 3, $"the claim went out {Claims()} times in 900 ms at a 200 ms interval");
            }
        }

        /// <summary>The crash path stops the worker before it hands back, the
        /// order OpenRGB's teardown keeps, so a worker a crash dialog keeps
        /// alive never claims the device again. The unit goes back on the
        /// worker's own channel or through the one-shot writer, and a second
        /// call sends nothing more.</summary>
        [Fact]
        public void TheCrashPath_StopsTheWorker_AndHandsTheUnitBackForGood()
        {
            var written = new System.Collections.Concurrent.ConcurrentQueue<(string Path, byte[] Frame)>();
            var (backend, channel, receiver, path) = LitBench(reassertMs: 50,
                writeOnce: (p, f, t) => { written.Enqueue((p, f)); return true; });
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                byte[] red = HidppUnitProtocol.SetStaticColor(1, 0x0E, new HidppZone(0, 1), 0xFF, 0x00, 0x00);
                byte[] claim = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: true);
                byte[] handBack = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: false);
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Assert.True(SpinWait.SpinUntil(() => channel.Writes.Any(w => w.Frame.SequenceEqual(red)), 3000));

                backend.ReleaseClaimedNow();
                Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 3000), "the worker stopped");
                Assert.True(channel.Writes.Any(w => w.Frame.SequenceEqual(handBack))
                            || written.Any(w => w.Path == receiver.Path && w.Frame.SequenceEqual(handBack)),
                    "the unit was handed back");
                int claims = channel.Writes.Count(w => w.Frame.SequenceEqual(claim));
                Thread.Sleep(300);
                Assert.Equal(claims, channel.Writes.Count(w => w.Frame.SequenceEqual(claim)));
                int once = written.Count;
                backend.ReleaseClaimedNow();
                Assert.Equal(once, written.Count);
            }

            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            int silence = service.IndexOf("try { _peripheralHost?.WaitForSilence(250); } catch { }", StringComparison.Ordinal);
            int release = service.IndexOf("try { _peripheralHost?.ReleaseLightingNow(); } catch { }", StringComparison.Ordinal);
            Assert.True(silence > 0 && release > silence, "the crash path hands the lighting back");
        }

        // ─────────────────────────────────────────────
        //  The SteelSeries worker's colors
        // ─────────────────────────────────────────────

        [Fact]
        public void AColorEventCarriesItsColorInTheFrame_OnEveryZoneOfItsType()
        {
            using var bind = System.Text.Json.JsonDocument.Parse(GameSenseClient.ColorBindBody("keyboard"));
            var root = bind.RootElement;
            Assert.Equal("COLOR_KEYBOARD", root.GetProperty("event").GetString());
            Assert.True(root.GetProperty("value_optional").GetBoolean());
            var handlers = root.GetProperty("handlers").EnumerateArray().ToList();
            Assert.Contains(handlers, h => h.GetProperty("device-type").GetString() == "rgb-per-key-zones"
                                           && h.GetProperty("zone").GetString() == "all");
            Assert.All(handlers, h =>
            {
                Assert.Equal("context-color", h.GetProperty("mode").GetString());
                Assert.Equal(GameSenseClient.ColorKey, h.GetProperty("context-frame-key").GetString());
            });
            Assert.Equal(new[] { "wheel", "logo", "base" },
                GameSenseClient.ColorZones("mouse").Select(z => z.Zone));
            Assert.Equal(new[] { ("headset", "earcups") }, GameSenseClient.ColorZones("headset"));
            // Each type's bind carries one handler per zone ColorZones names.
            foreach (var type in PeripheralLinker.GameSenseColorTypes)
            {
                using var typed = System.Text.Json.JsonDocument.Parse(GameSenseClient.ColorBindBody(type));
                var bound = typed.RootElement.GetProperty("handlers").EnumerateArray()
                    .Select(h => (h.GetProperty("device-type").GetString(), h.GetProperty("zone").GetString()))
                    .ToList();
                Assert.Equal(GameSenseClient.ColorZones(type).ToList(), bound);
            }

            using var evt = System.Text.Json.JsonDocument.Parse(GameSenseClient.ColorEventBody("mouse", 0x123456));
            var color = evt.RootElement.GetProperty("data").GetProperty("frame").GetProperty(GameSenseClient.ColorKey);
            Assert.Equal(0x12, color.GetProperty("red").GetInt32());
            Assert.Equal(0x34, color.GetProperty("green").GetInt32());
            Assert.Equal(0x56, color.GetProperty("blue").GetInt32());
        }

        /// <summary>A claimed mouse type binds its color event and posts its
        /// color, a change posts again, and a release removes the event at
        /// once and hands the devices back to GG after the release delay.</summary>
        [Fact]
        public void AClaimedGameSenseType_IsBoundPaintedAndRemoved()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var device = Guid.NewGuid();
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = device, Lighting = new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") } },
            }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 1000), _ => 0, 2);
            backend.Start();

            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x11, 0x22, 0x33);
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_MOUSE").Contains(0x112233), 5000), "the color was posted");
            Assert.Contains("COLOR_MOUSE", server.EventsAt("/bind_game_event"));
            // A session that dropped left its color events bound in GG, so the
            // connect removes every one before the first is bound again.
            var posts = server.Posts.ToList();
            int bindMouse = posts.FindIndex(p => p.Path == "/bind_game_event" && p.Body.Contains("\"COLOR_MOUSE\""));
            foreach (var type in PeripheralLinker.GameSenseColorTypes)
            {
                string evt = "\"COLOR_" + type.ToUpperInvariant() + "\"";
                int removed = posts.FindIndex(p => p.Path == "/remove_game_event" && p.Body.Contains(evt));
                Assert.True(removed >= 0 && removed < bindMouse, $"{evt} was removed before the first color bind");
            }
            Assert.True(SpinWait.SpinUntil(
                () => PeripheralOutputs.StateOf(OutputFamily.GameSenseColor) == BackendState.Connected, 1000));
            Assert.Equal(BackendState.Idle, PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile));

            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x44, 0x55, 0x66);
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_MOUSE").Contains(0x445566), 3000));

            int removesBefore = server.EventsAt("/remove_game_event").Count(e => e == "COLOR_MOUSE");
            long released = Environment.TickCount64;
            PeripheralOutputs.ReleaseLighting(device, 0);
            Assert.True(SpinWait.SpinUntil(
                () => server.EventsAt("/remove_game_event").Count(e => e == "COLOR_MOUSE") > removesBefore, 1000),
                "the type went back to GG at once");
            Assert.True(SpinWait.SpinUntil(() => server.Paths().Contains("/stop_game"),
                GameSenseBackend.ReleaseMs + 3000), "the game ended");
            long elapsed = Environment.TickCount64 - released;
            Assert.True(elapsed >= GameSenseBackend.ReleaseMs - 50, $"the game ended after {elapsed} ms, before the release delay");
        }

        // ─────────────────────────────────────────────
        //  Persistence and migration
        // ─────────────────────────────────────────────

        /// <summary>The switch is saved per (slot, device), reads off from a
        /// file written before it, and makes a config count as configured.</summary>
        [Fact]
        public void TheControlSwitchRoundTrips_AndCountsAsConfiguration()
        {
            var build = typeof(SettingsService).GetMethod("BuildDeviceSlotConfigData",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(build);
            var cfg = new DeviceSlotConfig { PeripheralLightingEnabled = true };
            var data = (DeviceSlotConfigData)build.Invoke(null, new object[] { cfg, 3, A });
            Assert.True(data.PeripheralLightingEnabled);

            var back = new DeviceSlotConfig();
            SettingsService.ApplyDeviceSlotConfigData(back, data);
            Assert.True(back.PeripheralLightingEnabled);
            data.PeripheralLightingEnabled = false;
            SettingsService.ApplyDeviceSlotConfigData(back, data);
            Assert.False(back.PeripheralLightingEnabled);

            Assert.False(new DeviceSlotConfigData().PeripheralLightingEnabled);
            Assert.True(SettingsService.IsDeviceConfigConfigured(new DeviceSlotConfig { PeripheralLightingEnabled = true }));
            Assert.False(SettingsService.IsDeviceConfigConfigured(new DeviceSlotConfig()));
            var rev1 = new DeviceSlotConfigData
            {
                LightingRev = 1, LightbarMode = LightbarMode.PlayerNumber, PlayerLedMode = PlayerLedMode.PlayerNumber,
            };
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(rev1));
            rev1.PeripheralLightingEnabled = true;
            Assert.True(SettingsService.IsDeviceSlotConfigDataConfigured(rev1));

            var reset = new DeviceSlotConfig { PeripheralLightingEnabled = true };
            reset.ResetPeripheralLightingEnabledCommand.Execute(null);
            Assert.False(reset.PeripheralLightingEnabled);
        }

        /// <summary>The shipped Chroma and LIGHTSYNC mirror switches migrate
        /// to their rows the way the Sensa switch does.</summary>
        [Fact]
        public void TheMirrorSwitches_MigrateToTheirLightingRows()
        {
            var app = new AppSettingsData { EnableChromaLightbar = true, EnableLightsyncLightbar = false };
            var switches = PeripheralSwitchMigration.Switches(app).ToList();
            var chroma = switches.Single(s => s.Kind == PeripheralRowKind.RazerChroma);
            var lightsync = switches.Single(s => s.Kind == PeripheralRowKind.LogitechLightsync);
            Assert.True(chroma.Global);
            Assert.False(lightsync.Global);
            Assert.True(chroma.Available && lightsync.Available);

            var profile = new ProfileData { EnableChromaLightbar = false, EnableLightsyncLightbar = true };
            Assert.False(chroma.Read(profile));
            Assert.True(lightsync.Read(profile));
            chroma.Clear(profile);
            lightsync.Clear(profile);
            Assert.Null(profile.EnableChromaLightbar);
            Assert.Null(profile.EnableLightsyncLightbar);

            var record = PeripheralSwitchMigration.NewRowRecord(PeripheralRowKind.RazerChroma);
            Assert.True(record.HasPeripheralLighting);
            Assert.Equal(PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma), record.InstanceGuid);
        }
    }
}
