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
            // The route lines say nothing while the engine is stopped.
            PeripheralOutputs.SetRunning(true);
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

            // A slot that lights nothing any more goes at the link pass's
            // prune, which is the release a dead slot gets.
            PeripheralOutputs.PruneClaims(new HashSet<(Guid, int)> { (A, 0), (A, 1), (B, 1) }, slot => slot != 1);
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
            Assert.Contains("PeripheralOutputs.PruneClaims(assigned, PeripheralOutputs.SlotLive);", host);
            string facade = RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputs.cs");
            Assert.Contains("=> UserEffectsDispatcher.HasLiveDispatcher(slot) || SlotHasController?.Invoke(slot) == true;", facade);
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

        /// <summary>A macro color wakes the two workers that paint it, the
        /// Chroma worker and the Razer direct worker, through an event of
        /// its own: on its start and on a new color, never on every
        /// assertion of a held one, and never the workers that do not
        /// paint it.</summary>
        [Fact]
        public void AChromaMacro_WakesOnlyTheWorkersThatPaintIt_OnStartsAndChanges()
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
            Assert.Contains("PeripheralOutputs.SlotHasController = SlotHasController;", host);
            string service = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("host.SlotHasController = pad => inputManager.HasVirtualControllerAt(pad);", service);
            PeripheralOutputs.SlotHasController = slot => slot == 6;
            Assert.True(PeripheralOutputs.SlotLive(6));
            Assert.False(PeripheralOutputs.SlotLive(7));
        }

        // ─────────────────────────────────────────────
        //  The linker
        // ─────────────────────────────────────────────

        private static LinkRow Row(Guid id, int capType, ushort vid, ushort pid, Guid container = default,
            PeripheralRowKind? kind = null) => new(id, capType, vid, pid, container, kind);

        private static PeripheralPresence Presence(bool ledSdk = false, bool gHub = false, bool synapse = false,
            bool gg = false, bool sensa = false) => new(ledSdk, gHub, synapse, gg, sensa);

        /// <summary>A Logitech device with a light plan takes its own unit
        /// while G HUB is not running, and its LED SDK type while it runs:
        /// the zone-only 0x8070 mouse and the 0x8071 keyboard alike, each
        /// with the claim level its unit offers. A 0x8071 device with no
        /// cluster to paint has no plan, so without G HUB nothing lights
        /// it.</summary>
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
                EffectFeatureId = HidppUnitProtocol.RgbEffects, EffectIndex = 0x0F,
                Zones = new[] { new HidppZone(0, 1), new HidppZone(1, 1) }, MaxClaimLevel = 2,
            };
            Assert.True(mouseUnit.HasLightPlan);
            Assert.True(keyboardUnit.HasLightPlan);
            Assert.False(keyboardUnit.DirectRgb);
            var hidpp = new HidppSnapshot(new[] { mouseUnit, keyboardUnit }, Array.Empty<Guid>());
            var rows = new[]
            {
                Row(A, InputDeviceType.Mouse, PeripheralLinker.LogitechVid, 0xC548, container),
                Row(B, InputDeviceType.Keyboard, PeripheralLinker.LogitechVid, 0xC548, container),
            };

            var direct = PeripheralLinker.Build(rows, hidpp, Presence(ledSdk: true));
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, mouseUnit.Key) }, direct.For(A).Lighting);
            Assert.Equal(new[] { new OutputPath(OutputFamily.HidppUnit, keyboardUnit.Key) }, direct.For(B).Lighting);
            Assert.Equal(0, direct.For(A).MaxClaimLevel);
            Assert.Equal(2, direct.For(B).MaxClaimLevel);

            // No cluster listed the Fixed effect: no plan, and no direct path.
            var bare = keyboardUnit with { Zones = Array.Empty<HidppZone>() };
            Assert.False(bare.HasLightPlan);
            Assert.Null(HidppLightPlan.For(bare));
            Assert.Null(PeripheralLinker.Build(rows, new HidppSnapshot(new[] { mouseUnit, bare }, Array.Empty<Guid>()),
                Presence(ledSdk: true)).For(B));

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

        /// <summary>An analog keyboard row's own path is synthetic, so its
        /// container comes from the HID collection it reads, and a Logitech
        /// analog keyboard finds its HID++ unit like any keyboard row.</summary>
        [Fact]
        public void AnAnalogKeyboardRow_FindsItsContainerThroughItsHidCollection()
        {
            var info = new PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardDeviceInfo
            {
                Path = @"\\?\hid#vid_31e3&pid_1232&mi_03#test",
                VendorId = 0x31E3,
                ProductId = 0x1232,
                UsagePage = 0xFF53,
                InputReportLength = 65,
                ProductString = "Wooting 60HE",
                SerialNumber = "A1B2C3",
            };
            using var analog = new AnalogKeyboardDevice(new AnalogKeyboardCandidate
            {
                Info = info,
                Routes = PadForge.Engine.Common.AnalogKeyboard.AnalogKeyboardRoutes.Candidates(info),
                Name = "Wooting 60HE",
                IdentityKey = AnalogKeyboardHidRuntime.IdentityKeyFor(info),
            });
            var row = new UserDevice { InstanceGuid = analog.InstanceGuid, DevicePath = analog.DevicePath, Device = analog };
            Assert.StartsWith("analogkb://", row.DevicePath);
            Assert.Equal(info.Path, PeripheralOutputHost.ContainerPath(row));

            var keyboard = new UserDevice { InstanceGuid = A, DevicePath = @"\\?\hid#vid_046d&pid_c33e&mi_00#kbd" };
            Assert.Equal(keyboard.DevicePath, PeripheralOutputHost.ContainerPath(keyboard));
        }

        /// <summary>The tab names the Logitech program that runs, and an engine
        /// that will not load reads as one that can light nothing, for a
        /// device and for the LIGHTSYNC row alike, never as a wait for
        /// software already running.</summary>
        [Fact]
        public void TheRouteLine_NamesTheLogitechProgramThatRuns_AndAnEngineThatWillNotLoad()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var logi = Device(A, PeripheralLinker.LogitechVid);
            Link(new DeviceLinks { Device = A, Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") } });
            PeripheralOutputs.SetLighting(A, slot: 1, player: 2, 3, 3, 3);

            PeripheralOutputs.Presence = Presence(ledSdk: true, gHub: true);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThrough, "Logitech G HUB"), PeripheralRouteText.Lighting(logi, 1));
            PeripheralOutputs.Presence = Presence(ledSdk: true, gHub: true) with { LogitechGamingSoftware = true };
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThrough, "Logitech Gaming Software"), PeripheralRouteText.Lighting(logi, 1));

            string cannot = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? s.Common_NotAvailableOnArm64 : s.Pad_Lighting_RouteCannotLight;
            PeripheralOutputs.SetBackendState(OutputFamily.LedSdkType, BackendState.Waiting);
            PeripheralOutputs.LedSdkPaintable = Array.Empty<string>();
            Assert.Equal(cannot, PeripheralRouteText.Lighting(logi, 1));
            Assert.Null(PeripheralRouteText.Lighting(logi, 1, lightsHere: false));

            var lightsync = PeripheralOutputRow.IdentityFor(PeripheralRowKind.LogitechLightsync);
            Link(new DeviceLinks
            {
                Device = lightsync, CatchAll = true,
                Lighting = PeripheralLinker.LedSdkTypes.Select(t => new OutputPath(OutputFamily.LedSdkType, t)).ToArray(),
            });
            PeripheralOutputs.SetLighting(lightsync, slot: 0, player: 1, 4, 4, 4);
            Assert.Equal(cannot, PeripheralRouteText.Lighting(Device(lightsync, PeripheralLinker.LogitechVid), 0));

            PeripheralOutputs.LedSdkPaintable = null;
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRowWaiting, "Logitech Gaming Software",
                s.Pad_Lighting_VendorRowExamples_LedSdk), PeripheralRouteText.Lighting(Device(lightsync, PeripheralLinker.LogitechVid), 0));
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

        /// <summary>A device assigned a moment before its own config exists
        /// on the slot is not lit with the selected device's config, whose
        /// Control This Device's Lighting switch and mode are another
        /// device's. Its own entry decides once it is made.</summary>
        [Fact]
        public void ADeviceWithNoConfigOfItsOwn_BorrowsNoOtherDevicesSwitch()
        {
            using var slot = new LitSlot();
            slot.Config.PeripheralLightingEnabled = true;
            slot.Add(B);
            var pathB = new OutputPath(OutputFamily.HidppUnit, "rx|02");
            Link(new DeviceLinks { Device = A, Lighting = new[] { slot.Path } },
                 new DeviceLinks { Device = B, Lighting = new[] { pathB } });
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);

            slot.Dispatcher.ApplyOnce();
            Assert.True(PeripheralOutputs.IsLit(A), "the selected mouse lights from its own config");
            Assert.False(PeripheralOutputs.IsLit(B), "a mouse with no config of its own took the selected mouse's");

            slot.Configs[B] = new DeviceSlotConfig { PeripheralLightingEnabled = true };
            slot.Dispatcher.ApplyOnce();
            Assert.True(PeripheralOutputs.IsLit(B), "its own config, switched on, lights it");
        }

        /// <summary>The claim a slot makes for a lit mouse carries the
        /// mouse's Lighting Hold as its level, and a change of the hold alone
        /// reaches the claim through the config's own change event, with no
        /// color change behind it. The tab's line names the same hold, from
        /// the number the claim carries.</summary>
        [Fact]
        public void TheClaimCarriesTheDevicesLightingHold_AndTheTabsLineNamesIt()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            using var slot = new LitSlot();
            slot.Config.PeripheralLightingEnabled = true;
            // A device that offers both holds, so the line can name each.
            Link(new DeviceLinks { Device = A, Lighting = new[] { slot.Path }, MaxClaimLevel = 2 });
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
            slot.Dispatcher.ApplyOnce();
            UserDevice mouse;
            lock (SettingsManager.UserDevices.SyncRoot)
                mouse = SettingsManager.UserDevices.Items.Single(d => d.InstanceGuid == A);

            var holds = new[]
            {
                (Hold: PeripheralClaimLevel.Colors, Level: 0, Line: s.Pad_Lighting_RouteDirect),
                (Hold: PeripheralClaimLevel.Power, Level: 1, Line: s.Pad_Lighting_RouteDirectPower),
                (Hold: PeripheralClaimLevel.HostMode, Level: 2, Line: s.Pad_Lighting_RouteDirectHostMode),
            };
            Assert.Equal(Enum.GetValues<PeripheralClaimLevel>(), holds.Select(h => h.Hold));
            foreach (var (hold, level, line) in holds)
            {
                // No pass is asked for: the config's change runs one.
                slot.Config.PeripheralClaimLevel = hold;
                Assert.True(PeripheralOutputs.TryResolveColor(slot.Path, out int rgb, out var ruler));
                Assert.Equal(level, ruler.Level);
                Assert.Equal(0x101418, rgb);
                Assert.Equal(string.Format(line, mouse.ResolvedName),
                    PeripheralRouteText.Lighting(mouse, 15, lightsHere: true, claimLevel: (int)hold));
            }

            // The reset beside the picker lowers the claim with it.
            slot.Config.ResetPeripheralClaimLevelCommand.Execute(null);
            Assert.True(PeripheralOutputs.TryResolveColor(slot.Path, out _, out var afterReset));
            Assert.Equal(0, afterReset.Level);

            // A hold on record with the switch off holds nothing: there is no
            // claim to carry it.
            slot.Config.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            slot.Config.PeripheralLightingEnabled = false;
            Assert.False(PeripheralOutputs.IsLit(A));
        }

        /// <summary>A vendor row and a unit row have no Lighting Hold on
        /// their tabs, so their claims ask for colors alone whatever their
        /// configs hold, a hold that came in a pasted or hand-edited entry
        /// included. The mouse beside them keeps its own.</summary>
        [Fact]
        public void AVendorRowAndAUnitRow_AskForColorsAlone_WhateverTheirConfigsHold()
        {
            using var slot = new LitSlot();
            var vendorRow = new PeripheralOutputRow(PeripheralRowKind.RazerChroma);
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Guid.NewGuid(), 0x1234, 1) { Name = "Headset" };
            var unitRow = new PeripheralUnitRow(headset);
            try
            {
                foreach (var row in new ISdlInputDevice[] { vendorRow, unitRow })
                {
                    var ud = new UserDevice();
                    ud.LoadFromExternalDevice(row);
                    ud.IsOnline = true;
                    ud.PeripheralOutputs = (int)PeripheralOutputKinds.Lighting;
                    lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
                    lock (SettingsManager.UserSettings.SyncRoot)
                        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = row.InstanceGuid, MapTo = 15 });
                    slot.Configs[row.InstanceGuid] = new DeviceSlotConfig { PeripheralClaimLevel = PeripheralClaimLevel.HostMode };
                }
                var category = new OutputPath(OutputFamily.ChromaCategory, "headset");
                // Every row's links say both holds are on offer, so only the
                // dispatcher's own rule keeps the two rows at colors alone.
                Link(new DeviceLinks { Device = A, Lighting = new[] { slot.Path }, MaxClaimLevel = 2 },
                     new DeviceLinks { Device = vendorRow.InstanceGuid, CatchAll = true, Lighting = new[] { category }, MaxClaimLevel = 2 },
                     new DeviceLinks { Device = unitRow.InstanceGuid, UnitRow = true, Lighting = new[] { headset.Path }, MaxClaimLevel = 2 });
                slot.Config.PeripheralLightingEnabled = true;
                slot.Config.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
                slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
                slot.Dispatcher.ApplyOnce();

                Assert.True(PeripheralOutputs.TryResolveColor(slot.Path, out _, out var mouse));
                Assert.Equal((A, 2), (mouse.Device, mouse.Level));
                Assert.True(PeripheralOutputs.TryResolveColor(category, out _, out var vendor));
                Assert.Equal((vendorRow.InstanceGuid, 0), (vendor.Device, vendor.Level));
                Assert.True(PeripheralOutputs.TryResolveColor(headset.Path, out _, out var unit));
                Assert.Equal((unitRow.InstanceGuid, 0), (unit.Device, unit.Level));
            }
            finally
            {
                vendorRow.Dispose();
                unitRow.Dispose();
            }
        }

        /// <summary>A load, a paste or a profile apply writes the switch and
        /// the hold one after the other, and each write runs a pass of its
        /// own that a lighting worker can wake behind. No claim is made in
        /// between at a hold neither the outgoing nor the incoming settings
        /// asked for: a device being taken has its hold in place before it
        /// is taken, and a device being let go is let go before the hold on
        /// its record changes.</summary>
        [Fact]
        public void ApplyingSettings_NeverClaimsADeviceAtAHoldNeitherSideAskedFor()
        {
            var build = typeof(SettingsService).GetMethod("BuildDeviceSlotConfigData",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(build);
            DeviceSlotConfigData Saved(bool lit, PeripheralClaimLevel hold) => (DeviceSlotConfigData)build.Invoke(null, new object[]
            {
                new DeviceSlotConfig
                {
                    LightbarMode = LightbarMode.Static, LightbarRed = 16, LightbarGreen = 20, LightbarBlue = 24,
                    PeripheralLightingEnabled = lit, PeripheralClaimLevel = hold,
                },
                15, A,
            });

            using var slot = new LitSlot();
            Link(new DeviceLinks { Device = A, Lighting = new[] { slot.Path }, MaxClaimLevel = 2 });
            slot.Dispatcher = new UserEffectsDispatcher(15, slot.Config, startTimer: false);
            // The level of the device's claim at every moment a worker is
            // woken, which is every moment it could read one.
            var levels = new List<int>();
            void OnLighting()
            {
                if (PeripheralOutputs.TryResolveColor(slot.Path, out _, out var ruler)) levels.Add(ruler.Level);
            }
            PeripheralOutputs.LightingChanged += OnLighting;
            try
            {
                // Left alone with the highest hold on record, then taken at
                // colors alone.
                SettingsService.ApplyDeviceSlotConfigData(slot.Config, Saved(lit: false, PeripheralClaimLevel.HostMode));
                Assert.Equal(PeripheralClaimLevel.HostMode, slot.Config.PeripheralClaimLevel);
                Assert.Empty(levels);
                SettingsService.ApplyDeviceSlotConfigData(slot.Config, Saved(lit: true, PeripheralClaimLevel.Colors));
                Assert.Equal(new[] { 0 }, levels.Distinct());

                // Lit at colors alone, then let go with the highest hold on
                // record.
                levels.Clear();
                SettingsService.ApplyDeviceSlotConfigData(slot.Config, Saved(lit: false, PeripheralClaimLevel.HostMode));
                Assert.Empty(levels);
                Assert.False(PeripheralOutputs.IsLit(A));
                Assert.Equal(PeripheralClaimLevel.HostMode, slot.Config.PeripheralClaimLevel);

                // Taken at the hold on its record, and then a change of the
                // hold alone while it is lit: straight to the new one.
                SettingsService.ApplyDeviceSlotConfigData(slot.Config, Saved(lit: true, PeripheralClaimLevel.HostMode));
                Assert.Equal(new[] { 2 }, levels.Distinct());
                levels.Clear();
                SettingsService.ApplyDeviceSlotConfigData(slot.Config, Saved(lit: true, PeripheralClaimLevel.Power));
                Assert.Equal(new[] { 1 }, levels.Distinct());
            }
            finally { PeripheralOutputs.LightingChanged -= OnLighting; }
        }

        /// <summary>The switch and the hold are read as one pair. Each
        /// setter publishes the pair before its change notice, since the
        /// notice runs a dispatcher pass that reads it, and a value the
        /// enum does not name changes neither.</summary>
        [Fact]
        public void TheSwitchAndTheHold_AreReadAsOnePair_CurrentAtEveryChangeNotice()
        {
            var cfg = new DeviceSlotConfig();
            Assert.False(cfg.PeripheralLightingClaim(out var level));
            Assert.Equal(PeripheralClaimLevel.Colors, level);
            var seen = new List<(bool Lit, PeripheralClaimLevel Hold, bool AsTheProperties)>();
            cfg.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is not (nameof(DeviceSlotConfig.PeripheralLightingEnabled)
                    or nameof(DeviceSlotConfig.PeripheralClaimLevel)))
                    return;
                bool lit = cfg.PeripheralLightingClaim(out var hold);
                seen.Add((lit, hold, lit == cfg.PeripheralLightingEnabled && hold == cfg.PeripheralClaimLevel));
            };
            cfg.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            cfg.PeripheralLightingEnabled = true;
            cfg.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            cfg.PeripheralLightingEnabled = false;
            Assert.Equal(new[]
            {
                (false, PeripheralClaimLevel.HostMode, true), (true, PeripheralClaimLevel.HostMode, true),
                (true, PeripheralClaimLevel.Power, true), (false, PeripheralClaimLevel.Power, true),
            }, seen);

            // A number with no name, and a value written again, change nothing.
            cfg.PeripheralClaimLevel = (PeripheralClaimLevel)7;
            cfg.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            cfg.PeripheralLightingEnabled = false;
            Assert.False(cfg.PeripheralLightingClaim(out level));
            Assert.Equal(PeripheralClaimLevel.Power, level);
            Assert.Equal(4, seen.Count);
        }

        /// <summary>A pass on another thread never reads the switch of one
        /// load with the hold of the next. One thread loads {on, colors
        /// alone} and {off, the highest hold} over and over, in the order a
        /// load writes them, while another reads the pair as the dispatcher
        /// does: it never finds the switch on at the highest hold, which
        /// neither load asked for.</summary>
        [Fact]
        public void APassOnAnotherThread_NeverPairsTheSwitchOfOneLoadWithTheHoldOfTheNext()
        {
            var cfg = new DeviceSlotConfig { PeripheralLightingEnabled = true };
            int wrong = 0, reads = 0, stop = 0;
            var reader = new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (cfg.PeripheralLightingClaim(out var level) && level == PeripheralClaimLevel.HostMode)
                        Interlocked.Increment(ref wrong);
                    Interlocked.Increment(ref reads);
                }
            }) { IsBackground = true };
            reader.Start();
            try
            {
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) > 0, 30000), "the reader runs");
                for (int i = 0; i < 200_000; i++)
                {
                    // Let go, then the hold: a load that turns the switch off.
                    cfg.PeripheralLightingEnabled = false;
                    cfg.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
                    // The hold, then taken: a load that turns it on.
                    cfg.PeripheralClaimLevel = PeripheralClaimLevel.Colors;
                    cfg.PeripheralLightingEnabled = true;
                }
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                Assert.True(reader.Join(30000), "the reader ended");
            }
            Assert.Equal(0, Volatile.Read(ref wrong));
        }

        /// <summary>While the engine is stopped nothing reaches any device,
        /// so every route line says nothing, the waiting and asleep lines
        /// included.</summary>
        [Fact]
        public void TheRouteLinesSayNothing_WhileTheEngineIsStopped()
        {
            var logi = new UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Mouse, VendorId = PeripheralLinker.LogitechVid,
                PeripheralOutputs = (int)(PeripheralOutputKinds.Lighting | PeripheralOutputKinds.Haptics),
            };
            var sensa = new UserDevice
            {
                InstanceGuid = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa),
                PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            // Running: the asleep and waiting lines.
            Assert.NotNull(PeripheralRouteText.Lighting(logi, 0));
            Assert.NotNull(PeripheralRouteText.Haptics(logi));
            Assert.NotNull(PeripheralRouteText.Haptics(sensa));

            PeripheralOutputs.SetRunning(false);
            Assert.Null(PeripheralRouteText.Lighting(logi, 0));
            Assert.Null(PeripheralRouteText.Haptics(logi));
            // The Sensa row's waiting line, where the engine loads. A build
            // that cannot load it reads its own line, which is no route.
            Assert.Null(PeripheralRouteText.Haptics(sensa, slotLive: true, sensaAvailable: true));
        }

        /// <summary>A Razer Sensa row in a build that cannot load the engine
        /// reads that platform's limit whatever the engine or the controller
        /// does: a stopped engine or a controller that is gone hides a
        /// route, and this line is no route. Where the engine loads, both
        /// still hide the row's waiting line.</summary>
        [Fact]
        public void ASensaRowWhereTheEngineCannotLoad_SaysSo_WhateverTheEngineOrTheControllerDoes()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var sensa = new UserDevice
            {
                InstanceGuid = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa),
                PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            string Line(bool slotLive, bool sensaAvailable) => PeripheralRouteText.Haptics(sensa, slotLive, sensaAvailable);

            // The engine running, on a live controller.
            Assert.Equal(s.Common_NotAvailableOnArm64, Line(slotLive: true, sensaAvailable: false));
            Assert.Equal(s.Pad_ForceFeedback_RouteSensaWaiting, Line(slotLive: true, sensaAvailable: true));
            // The controller gone.
            Assert.Equal(s.Common_NotAvailableOnArm64, Line(slotLive: false, sensaAvailable: false));
            Assert.Null(Line(slotLive: false, sensaAvailable: true));
            // The engine stopped.
            PeripheralOutputs.SetRunning(false);
            Assert.Equal(s.Common_NotAvailableOnArm64, Line(slotLive: true, sensaAvailable: false));
            Assert.Null(Line(slotLive: true, sensaAvailable: true));

            // The limit is the Sensa row's alone, and only on its record.
            var logi = new UserDevice
            {
                InstanceGuid = A, VendorId = PeripheralLinker.LogitechVid, PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            Assert.Null(PeripheralRouteText.Haptics(logi, slotLive: true, sensaAvailable: false));
            sensa.PeripheralOutputs = 0;
            Assert.Null(Line(slotLive: true, sensaAvailable: false));

            // The Pad page's form asks this build's own platform.
            sensa.PeripheralOutputs = (int)PeripheralOutputKinds.Haptics;
            Assert.Equal(Line(slotLive: true, sensaAvailable: PlatformSupport.SensaAvailable),
                PeripheralRouteText.Haptics(sensa));
        }

        /// <summary>An engine that cannot be loaded lights nothing from any
        /// controller, so the LIGHTSYNC row reads that on a controller that
        /// lights nothing as well, and never names another controller as
        /// the one lighting it.</summary>
        [Fact]
        public void AnEngineThatWillNotLoad_ReadsSoOnAControllerThatLightsNothing()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var lightsync = PeripheralOutputRow.IdentityFor(PeripheralRowKind.LogitechLightsync);
            var row = Device(lightsync, PeripheralLinker.LogitechVid);
            Link(new DeviceLinks
            {
                Device = lightsync, CatchAll = true,
                Lighting = PeripheralLinker.LedSdkTypes.Select(t => new OutputPath(OutputFamily.LedSdkType, t)).ToArray(),
            });
            PeripheralOutputs.Presence = Presence(ledSdk: true, gHub: true);
            // The same row on another controller holds every path.
            PeripheralOutputs.SetLighting(lightsync, slot: 0, player: 1, 4, 4, 4);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 1),
                PeripheralRouteText.Lighting(row, 5, lightsHere: false));

            string cannot = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64
                ? s.Common_NotAvailableOnArm64 : s.Pad_Lighting_RouteCannotLight;
            PeripheralOutputs.LedSdkPaintable = Array.Empty<string>();
            Assert.Equal(cannot, PeripheralRouteText.Lighting(row, 0));
            Assert.Equal(cannot, PeripheralRouteText.Lighting(row, 5, lightsHere: false));
        }

        /// <summary>A controller that lights or rumbles nothing, its own
        /// controller gone, names no route: a vendor row there names only the
        /// controller that lights it elsewhere, and a haptic row's line is
        /// empty. The Pad page asks the link pass's own rule.</summary>
        [Fact]
        public void AControllerThatLightsNothing_NamesNoRouteOfItsOwn()
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var chroma = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma);
            Link(new DeviceLinks
            {
                Device = chroma, CatchAll = true,
                Lighting = PeripheralLinker.ChromaCategories.Select(c => new OutputPath(OutputFamily.ChromaCategory, c)).ToArray(),
            });
            PeripheralOutputs.SetBackendState(OutputFamily.ChromaCategory, BackendState.Connected);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRow, "Razer Synapse", s.Pad_Lighting_VendorRowExamples_Chroma),
                PeripheralRouteText.Lighting(Device(chroma), 3));
            Assert.Null(PeripheralRouteText.Lighting(Device(chroma), 3, lightsHere: false));

            // The same row on another controller lights it.
            PeripheralOutputs.SetLighting(chroma, slot: 6, player: 2, 1, 2, 3);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 2),
                PeripheralRouteText.Lighting(Device(chroma), 3, lightsHere: false));

            var rival = new UserDevice
            {
                InstanceGuid = B, VendorId = PeripheralLinker.SteelSeriesVid, PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            Link(new DeviceLinks { Device = B, Haptics = new[] { PeripheralLinker.GameSenseTactilePath } });
            Assert.NotNull(PeripheralRouteText.Haptics(rival));
            Assert.Null(PeripheralRouteText.Haptics(rival, slotLive: false));

            string page = RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("bool slotLive = routeSlot >= 0 && PadForge.Common.Input.Peripherals.PeripheralOutputs.SlotLive(routeSlot);", page);
            Assert.Contains("PeripheralRouteText.Haptics(peripheralRow, slotLive)", page);
            Assert.Contains("slotLive && (vendorLightingRow || routeVm?.DeviceConfig?.PeripheralLightingEnabled == true)", page);
        }

        /// <summary>While a vendor's software is down nothing is lit from any
        /// controller, so on a controller that lights nothing the vendor row
        /// names no controller as the one lighting it, as a device's own row
        /// names none there. It read "Virtual Controller 2 lights this
        /// device." with nothing lit. With the software up it names that
        /// controller, and the controller that lights the row keeps its
        /// waiting line.</summary>
        [Theory]
        [InlineData("chroma")]
        [InlineData("gg")]
        [InlineData("lightsync")]
        public void AVendorRowWhoseSoftwareIsDown_NamesNoControllerOnOneThatLightsNothing(string vendor)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var (kind, family, keys, vid) = vendor switch
            {
                "chroma" => (PeripheralRowKind.RazerChroma, OutputFamily.ChromaCategory,
                    PeripheralLinker.ChromaCategories, PeripheralLinker.RazerVid),
                "gg" => (PeripheralRowKind.SteelSeriesGG, OutputFamily.GameSenseColor,
                    PeripheralLinker.GameSenseColorTypes, PeripheralLinker.SteelSeriesVid),
                _ => (PeripheralRowKind.LogitechLightsync, OutputFamily.LedSdkType,
                    PeripheralLinker.LedSdkTypes, PeripheralLinker.LogitechVid),
            };
            var id = PeripheralOutputRow.IdentityFor(kind);
            var row = Device(id, vid);
            Link(new DeviceLinks
            {
                Device = id, CatchAll = true,
                Lighting = keys.Select(k => new OutputPath(family, k)).ToArray(),
            });
            PeripheralOutputs.Presence = Presence(ledSdk: true, gHub: true);
            // The same row on another controller holds every path.
            PeripheralOutputs.SetLighting(id, slot: 6, player: 2, 1, 2, 3);

            PeripheralOutputs.SetBackendState(family, BackendState.Connected);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 2),
                PeripheralRouteText.Lighting(row, 3, lightsHere: false));

            PeripheralOutputs.SetBackendState(family, BackendState.Waiting);
            Assert.Null(PeripheralRouteText.Lighting(row, 3, lightsHere: false));
            // The controller that lights the row still says what it waits for.
            Assert.Equal(string.Format(s.Pad_Lighting_RouteVendorRowWaiting,
                    PeripheralRouteText.LightingApp(family), PeripheralRouteText.VendorRowExamples(family)),
                PeripheralRouteText.Lighting(row, 6));
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

        /// <summary>The whole frame sequence of a zone-only 0x8070 unit,
        /// byte for byte what the worker sent before it learned any other
        /// feature: the claim and the zone's color at the first color, the
        /// zone alone at a new color, 0x00 where the fixed-color marker goes
        /// for black, and [00 00] at the release. The bytes are written out
        /// here and built through no code under test, so a change to a
        /// builder or to the worker cannot move both sides at once. Nothing
        /// is timed: each step waits for its frame and then for two more
        /// passes, which would have carried any frame that had no business
        /// there.</summary>
        [Fact]
        public void AZoneOnlyUnit_SendsTheFramesItHasAlwaysSent_FromTheClaimToTheHandBack()
        {
            byte[] claim = HidppHapticsTests.Hex("11 01 0E 8C 01 01");
            byte[] red = HidppHapticsTests.Hex("11 01 0E 3C 00 01 FF 00 00 02");
            byte[] blue = HidppHapticsTests.Hex("11 01 0E 3C 00 01 00 00 FF 02");
            byte[] black = HidppHapticsTests.Hex("11 01 0E 3C 00 01 00 00 00 00");
            byte[] handBack = HidppHapticsTests.Hex("11 01 0E 8C 00 00");
            static string[] Sent(HidppHapticsTests.FakeChannel channel)
                => channel.Writes.Select(w => HidppHapticsTests.Text(w.Frame)).ToArray();
            static string[] Frames(params byte[][] frames) => frames.Select(HidppHapticsTests.Text).ToArray();
            static void Passes(HidppBackend backend)
            {
                for (int i = 0; i < 2; i++)
                {
                    int before = backend.PassCount;
                    Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
                }
            }
            static void Wait(HidppBackend backend, HidppHapticsTests.FakeChannel channel, int frames)
            {
                Assert.True(SpinWait.SpinUntil(() => channel.Writes.Count >= frames, 30000),
                    $"{channel.Writes.Count} of {frames} frames went out");
                Passes(backend);
            }

            // Every step of a claim's life, with no re-assert in between.
            var (backend, channel, _, path) = LitBench(reassertMs: int.MaxValue);
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                Passes(backend);
                Assert.Empty(channel.Writes);

                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Wait(backend, channel, 2);
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x00, 0x00, 0xFF);
                Wait(backend, channel, 3);
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x00, 0x00, 0x00);
                Wait(backend, channel, 4);
                PeripheralOutputs.ReleaseLighting(device, 0);
                Wait(backend, channel, 5);
                Assert.Equal(Frames(claim, red, blue, black, handBack), Sent(channel));
                // Nothing of it was a request: the worker waits on no answer.
                Assert.DoesNotContain(channel.Traffic, t => t.Kind == "ask" && t.Frame[2] == 0x0E && (t.Frame[3] >> 4) is 3 or 8);
            }

            // The re-assert: the claim and the color again, each interval.
            (backend, channel, _, path) = LitBench(reassertMs: 50);
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Assert.True(SpinWait.SpinUntil(() => channel.Writes.Count >= 6, 30000), "the claim went out again");
                Assert.Equal(Frames(claim, red, claim, red, claim, red), Sent(channel).Take(6).ToArray());
            }

            // The engine stops while the unit is lit: the worker hands it
            // back on its own channel before the channel closes.
            (backend, channel, _, path) = LitBench(reassertMs: int.MaxValue);
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Wait(backend, channel, 2);
                backend.Stop();
                Assert.Equal(Frames(claim, red, handBack), Sent(channel));
                Assert.True(channel.Disposed);
            }

            // The crash path: the same frame, on the worker's channel or
            // through the one-shot writer, and to the unit's own collection.
            var written = new System.Collections.Concurrent.ConcurrentQueue<(string Path, byte[] Frame)>();
            VendorHidCollection receiver;
            (backend, channel, receiver, path) = LitBench(reassertMs: int.MaxValue,
                writeOnce: (p, f, t) => { written.Enqueue((p, f)); return true; });
            using (backend)
            {
                var device = Guid.NewGuid();
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Wait(backend, channel, 2);
                backend.ReleaseClaimedNow();
                Assert.True(SpinWait.SpinUntil(() => !backend.WorkerAlive, 30000), "the worker stopped");
                var after = Sent(channel).Skip(2).Concat(written.Select(w => HidppHapticsTests.Text(w.Frame))).ToArray();
                Assert.NotEmpty(after);
                Assert.All(after, frame => Assert.Equal(HidppHapticsTests.Text(handBack), frame));
                Assert.All(written, w => Assert.Equal(receiver.Path, w.Path));
            }
        }

        /// <summary>A unit whose write failed is owed its hand-back, and the
        /// stopping worker pays it through a write of its own. Not where
        /// the linker closed the unit's path because OpenRGB holds the
        /// device (#557): the hand-back would land on OpenRGB's lighting.
        /// The same stop with no mark pays it.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHandBackOwedAtStop_IsNotPaid_WhereOpenRgbTookThePath(bool taken)
        {
            var written = new System.Collections.Concurrent.ConcurrentQueue<(string Path, byte[] Frame)>();
            var (backend, channel, _, path) = LitBench(reassertMs: int.MaxValue,
                writeOnce: (p, f, t) => { written.Enqueue((p, f)); return true; });
            using (backend)
            {
                var device = Guid.NewGuid();
                byte[] red = HidppUnitProtocol.SetStaticColor(1, 0x0E, new HidppZone(0, 1), 0xFF, 0x00, 0x00);
                byte[] handBack = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: false);
                PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } }));
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Assert.True(SpinWait.SpinUntil(() => channel.Writes.Any(w => w.Frame.SequenceEqual(red)), 30000), "lit");

                // The next write fails, and the unit is owed its hand-back.
                channel.FailWrites = true;
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x00, 0x00, 0xFF);
                var record = typeof(HidppBackend).GetField("_owed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.True(SpinWait.SpinUntil(() => ((System.Collections.ICollection)record.GetValue(backend)).Count > 0, 30000),
                    "the hand-back is owed");

                PeripheralOutputs.PublishLinks(new LinkTable(new[]
                {
                    new DeviceLinks
                    {
                        Device = device,
                        Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                        TakenByOpenRgb = taken ? new[] { path } : Array.Empty<OutputPath>(),
                    },
                }));
                backend.Stop();
                Assert.Equal(taken ? 0 : 1, written.Count(w => w.Frame.SequenceEqual(handBack)));
            }
        }

        /// <summary>The claim goes out again on its own interval while the
        /// color keeps changing, so an animated mode never holds back the
        /// re-claim a device that comes back from sleep needs. The color
        /// changes for as long as the third claim takes: a claim that waited
        /// on the colors would never follow the first, and a count over a
        /// fixed time would come up short on a busy bench.</summary>
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

                long until = Environment.TickCount64 + 10000;
                for (byte i = 1; Claims() < 3 && Environment.TickCount64 < until; i++)
                {
                    PeripheralOutputs.SetLighting(device, slot: 0, player: 1, i, 0x00, 0x00);
                    Thread.Sleep(20);
                }
                Assert.True(Claims() >= 3, $"the claim went out {Claims()} times while the color kept changing");
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

        /// <summary>G HUB starts while a unit is lit: the linker takes the
        /// unit's lighting path away, and the worker lets go of the unit
        /// without a frame, because a hand-back that landed after G HUB's
        /// own claim would clear that claim. A path that goes for any other
        /// reason, and a claim that ends while its path is still linked, are
        /// handed back with [00 00] as before. The worker's passes are
        /// counted, never timed.</summary>
        [Fact]
        public void WhenALogitechProgramStarts_ALitUnitIsLetGoWithNoFrame_AndEveryOtherReleaseHandsBack()
        {
            // No re-assert inside the test, so every frame below answers a
            // change the test made.
            var (backend, channel, _, path) = LitBench(reassertMs: int.MaxValue);
            using (backend)
            {
                var device = Guid.NewGuid();
                var lit = new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } });
                // What the linker gives the row while G HUB runs: its LED SDK
                // type, and the unit's path for rumble alone.
                var throughGHub = new LinkTable(new[]
                {
                    new DeviceLinks
                    {
                        Device = device, DirectWhenClosed = true, Haptics = new[] { path },
                        Lighting = new[] { new OutputPath(OutputFamily.LedSdkType, "mouse") },
                    },
                });
                var gHubRuns = new PeripheralPresence(true, true, false, false, false);
                byte[] claim = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: true);
                byte[] red = HidppUnitProtocol.SetStaticColor(1, 0x0E, new HidppZone(0, 1), 0xFF, 0x00, 0x00);
                byte[] handBack = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: false);
                int Count(byte[] frame) => channel.Writes.Count(w => w.Frame.SequenceEqual(frame));
                // The first pass to end may have begun before the change it
                // follows. The second began after the first ended.
                void Passes()
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int before = backend.PassCount;
                        Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
                    }
                }

                PeripheralOutputs.PublishLinks(lit);
                while (channel.Writes.TryDequeue(out _)) { }
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Passes();
                Assert.Equal((1, 1, 0), (Count(claim), Count(red), Count(handBack)));

                // G HUB starts. The link pass publishes the presence, then
                // the table without the unit's lighting path.
                PeripheralOutputs.Presence = gHubRuns;
                PeripheralOutputs.PublishLinks(throughGHub);
                Passes();
                Assert.Equal(2, channel.Writes.Count);
                Assert.Equal(0, Count(handBack));

                // The crash path has nothing of it on record either.
                var record = typeof(HidppBackend).GetField("_claimed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.NotNull(record);
                Assert.Empty((Array)record.GetValue(backend));

                // G HUB stops and the path comes back: the standing claim is
                // claimed and painted again.
                PeripheralOutputs.Presence = PeripheralPresence.None;
                PeripheralOutputs.PublishLinks(lit);
                Passes();
                Assert.Equal((2, 2, 0), (Count(claim), Count(red), Count(handBack)));
                Assert.Single((Array)record.GetValue(backend));

                // The path goes with no Logitech program running, a row that
                // went offline: handed back, as before.
                PeripheralOutputs.PublishLinks(LinkTable.Empty);
                Passes();
                Assert.Equal((2, 2, 1), (Count(claim), Count(red), Count(handBack)));

                // The claim ends while its path is still linked, a switch
                // turned off: handed back, whatever runs.
                PeripheralOutputs.PublishLinks(lit);
                Passes();
                Assert.Equal((3, 3, 1), (Count(claim), Count(red), Count(handBack)));
                PeripheralOutputs.Presence = gHubRuns;
                PeripheralOutputs.ReleaseLighting(device, 0);
                Passes();
                Assert.Equal((3, 3, 2), (Count(claim), Count(red), Count(handBack)));
            }
        }

        /// <summary>OpenRGB holds the mouse (#557): the linker closes the
        /// unit's lighting path and names it taken, and the worker lets go
        /// without a frame, as when G HUB starts. The same table without
        /// the mark hands the unit back.</summary>
        [Fact]
        public void APathOpenRgbTakes_IsLetGoWithNoFrame()
        {
            var (backend, channel, _, path) = LitBench(reassertMs: int.MaxValue);
            using (backend)
            {
                var device = Guid.NewGuid();
                var lit = new LinkTable(new[] { new DeviceLinks { Device = device, Lighting = new[] { path } } });
                LinkTable ThroughOpenRgb(bool taken) => new(new[]
                {
                    new DeviceLinks
                    {
                        Device = device, Haptics = new[] { path },
                        Lighting = new[] { new OutputPath(OutputFamily.OpenRgbDevice, "openrgb") },
                        TakenByOpenRgb = taken ? new[] { path } : Array.Empty<OutputPath>(),
                    },
                });
                byte[] claim = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: true);
                byte[] handBack = HidppUnitProtocol.SetSwControl(1, 0x0E, claim: false);
                int Count(byte[] frame) => channel.Writes.Count(w => w.Frame.SequenceEqual(frame));
                void Passes()
                {
                    for (int i = 0; i < 2; i++)
                    {
                        int before = backend.PassCount;
                        Assert.True(SpinWait.SpinUntil(() => backend.PassCount > before, 30000), "the worker made a pass");
                    }
                }

                PeripheralOutputs.PublishLinks(lit);
                while (channel.Writes.TryDequeue(out _)) { }
                PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0xFF, 0x00, 0x00);
                Passes();
                Assert.Equal((1, 0), (Count(claim), Count(handBack)));

                PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: true));
                Passes();
                Assert.Equal(2, channel.Writes.Count);
                Assert.Equal(0, Count(handBack));
                var record = typeof(HidppBackend).GetField("_claimed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                Assert.Empty((Array)record.GetValue(backend));

                // OpenRGB lets go: claimed again. The path then goes with no
                // mark: handed back.
                PeripheralOutputs.PublishLinks(lit);
                Passes();
                Assert.Equal(2, Count(claim));
                PeripheralOutputs.PublishLinks(ThroughOpenRgb(taken: false));
                Passes();
                Assert.Equal(1, Count(handBack));
            }
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
        /// once and hands the devices back to GG after the release delay.
        /// The worker reads this test's clock, so the color throttle and the
        /// release delay run out when the test says so and nothing here is
        /// timed.</summary>
        [Fact]
        public void AClaimedGameSenseType_IsBoundPaintedAndRemoved()
        {
            using var server = new HidppHapticsTests.FakeGameSense();
            string coreProps = server.WriteCoreProps();
            var device = Guid.NewGuid();
            long now = 1_000_000;
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks { Device = device, Lighting = new[] { new OutputPath(OutputFamily.GameSenseColor, "mouse") } },
            }));
            using var backend = new GameSenseBackend(() => new GameSenseClient(coreProps, 10000), _ => 0, 2,
                clock: () => Volatile.Read(ref now));
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
                () => PeripheralOutputs.StateOf(OutputFamily.GameSenseColor) == BackendState.Connected, 5000));
            Assert.Equal(BackendState.Idle, PeripheralOutputs.StateOf(OutputFamily.GameSenseTactile));

            // A held color is posted again no sooner than ColorMs after the
            // last: a whole look passes with the change unsent, and it goes
            // out once the clock reaches the throttle.
            PeripheralOutputs.SetLighting(device, slot: 0, player: 1, 0x44, 0x55, 0x66);
            WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
            Assert.DoesNotContain(0x445566, server.ColorValues("COLOR_MOUSE"));
            Volatile.Write(ref now, now + GameSenseBackend.ColorMs);
            Assert.True(SpinWait.SpinUntil(() => server.ColorValues("COLOR_MOUSE").Contains(0x445566), 5000));

            // The release removes the event with none of the release delay
            // gone, and the game ends on the delay's last tick, not one tick
            // short of it.
            int removesBefore = server.EventsAt("/remove_game_event").Count(e => e == "COLOR_MOUSE");
            PeripheralOutputs.ReleaseLighting(device, 0);
            Assert.True(SpinWait.SpinUntil(
                () => server.EventsAt("/remove_game_event").Count(e => e == "COLOR_MOUSE") > removesBefore, 5000),
                "the type went back to GG at once");
            Volatile.Write(ref now, now + GameSenseBackend.ReleaseMs - 1);
            WorkerCadence.WaitAfterAFullPass(() => backend.PassCount, () => backend.WaitMs);
            Assert.DoesNotContain("/stop_game", server.Paths());
            Volatile.Write(ref now, now + 1);
            Assert.True(SpinWait.SpinUntil(() => server.Paths().Contains("/stop_game"), 5000), "the game ended");
        }

        // ─────────────────────────────────────────────
        //  Persistence and migration
        // ─────────────────────────────────────────────

        /// <summary>The switch is saved per (slot, device) and reads off
        /// from a file written before it. It does not make a config count
        /// as configured: that check chooses a stand-in for another device,
        /// which never takes the switch.</summary>
        [Fact]
        public void TheControlSwitchRoundTrips_AndMakesNoConfigAStandIn()
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
            Assert.False(SettingsService.IsDeviceConfigConfigured(new DeviceSlotConfig { PeripheralLightingEnabled = true }));
            Assert.False(SettingsService.IsDeviceConfigConfigured(new DeviceSlotConfig()));
            var rev1 = new DeviceSlotConfigData
            {
                LightingRev = 1, LightbarMode = LightbarMode.PlayerNumber, PlayerLedMode = PlayerLedMode.PlayerNumber,
            };
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(rev1));
            rev1.PeripheralLightingEnabled = true;
            Assert.False(SettingsService.IsDeviceSlotConfigDataConfigured(rev1));

            var reset = new DeviceSlotConfig { PeripheralLightingEnabled = true };
            reset.ResetPeripheralLightingEnabledCommand.Execute(null);
            Assert.False(reset.PeripheralLightingEnabled);
        }

        /// <summary>The Lighting Hold is saved per (slot, device) by its
        /// name, comes back through the load that a paste and a profile
        /// apply share, and reads as colors alone from a file written
        /// before it, so an older file asks no device for more. A new
        /// config, and so a reset slot, starts at colors alone.</summary>
        [Fact]
        public void TheLightingHoldRoundTrips_AndAFileWithoutItReadsAsColorsAlone()
        {
            var build = typeof(SettingsService).GetMethod("BuildDeviceSlotConfigData",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(build);
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            Assert.Equal(PeripheralClaimLevel.Colors, new DeviceSlotConfig().PeripheralClaimLevel);
            Assert.Equal(PeripheralClaimLevel.Colors, new DeviceSlotConfigData().PeripheralClaimLevel);

            foreach (var hold in Enum.GetValues<PeripheralClaimLevel>())
            {
                var cfg = new DeviceSlotConfig { PeripheralClaimLevel = hold };
                var data = (DeviceSlotConfigData)build.Invoke(null, new object[] { cfg, 3, A });
                Assert.Equal(hold, data.PeripheralClaimLevel);

                // The file's own form, and back through the load.
                using var writer = new StringWriter();
                serializer.Serialize(writer, data);
                Assert.Contains($" PeripheralClaimLevel=\"{hold}\"", writer.ToString());
                var read = (DeviceSlotConfigData)serializer.Deserialize(new StringReader(writer.ToString()));
                foreach (var before in Enum.GetValues<PeripheralClaimLevel>())
                {
                    var back = new DeviceSlotConfig { PeripheralClaimLevel = before };
                    SettingsService.ApplyDeviceSlotConfigData(back, read);
                    Assert.Equal(hold, back.PeripheralClaimLevel);
                }
            }

            // An entry saved before the hold existed, the switch on.
            var older = (DeviceSlotConfigData)serializer.Deserialize(new StringReader(
                "<DeviceSlotConfigData SlotIndex=\"3\" LightingRev=\"1\" LightbarMode=\"Static\" PeripheralLightingEnabled=\"true\" />"));
            Assert.True(older.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Colors, older.PeripheralClaimLevel);
            // Applied over a device held more firmly, as a profile from
            // before the hold is, it returns the device to colors alone.
            var held = new DeviceSlotConfig { PeripheralClaimLevel = PeripheralClaimLevel.HostMode };
            SettingsService.ApplyDeviceSlotConfigData(held, older);
            Assert.True(held.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Colors, held.PeripheralClaimLevel);
        }

        /// <summary>The hold's number is the level its claim carries and the
        /// place of its option in the picker, and the clipboard carries it,
        /// so the three keep their numbers. A number the enum does not name
        /// leaves the hold as it was: the file writes the name and has none
        /// for it, so storing it would fail the next save, and a claim would
        /// carry it to a backend as a level nobody chose.</summary>
        [Fact]
        public void AHoldTheEnumDoesNotName_LeavesTheHoldAsItWas()
        {
            Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<PeripheralClaimLevel>().Select(hold => (int)hold));
            Assert.Equal((0, 1, 2),
                ((int)PeripheralClaimLevel.Colors, (int)PeripheralClaimLevel.Power, (int)PeripheralClaimLevel.HostMode));

            var cfg = new DeviceSlotConfig { PeripheralClaimLevel = PeripheralClaimLevel.Power };
            var raised = new List<string>();
            cfg.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            foreach (int unnamed in new[] { 3, 7, -1, int.MaxValue })
            {
                cfg.PeripheralClaimLevel = (PeripheralClaimLevel)unnamed;
                Assert.Equal(PeripheralClaimLevel.Power, cfg.PeripheralClaimLevel);
            }
            Assert.Empty(raised);

            // The clipboard's form is the number, so a pasted entry can
            // hold one with no name. The load refuses it.
            string json = System.Text.Json.JsonSerializer.Serialize(
                new DeviceSlotConfigData { LightingRev = 1, PeripheralClaimLevel = PeripheralClaimLevel.HostMode });
            Assert.Contains("\"PeripheralClaimLevel\":2", json);
            var pasted = System.Text.Json.JsonSerializer.Deserialize<DeviceSlotConfigData>(
                json.Replace("\"PeripheralClaimLevel\":2", "\"PeripheralClaimLevel\":7"));
            Assert.Equal(7, (int)pasted.PeripheralClaimLevel);
            SettingsService.ApplyDeviceSlotConfigData(cfg, pasted);
            Assert.Equal(PeripheralClaimLevel.Power, cfg.PeripheralClaimLevel);
            Assert.DoesNotContain(nameof(DeviceSlotConfig.PeripheralClaimLevel), raised);

            // What the file does with such a number, and with what the
            // config kept in its place.
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(DeviceSlotConfigData));
            Assert.Throws<InvalidOperationException>(() => serializer.Serialize(new StringWriter(), pasted));
            var build = typeof(SettingsService).GetMethod("BuildDeviceSlotConfigData",
                BindingFlags.NonPublic | BindingFlags.Static);
            using var writer = new StringWriter();
            serializer.Serialize(writer, build.Invoke(null, new object[] { cfg, 0, A }));
            Assert.Contains(" PeripheralClaimLevel=\"Power\"", writer.ToString());

            // A named hold still changes it, and says so once.
            raised.Clear();
            cfg.PeripheralClaimLevel = PeripheralClaimLevel.HostMode;
            Assert.Equal(PeripheralClaimLevel.HostMode, cfg.PeripheralClaimLevel);
            Assert.Equal(new[] { nameof(DeviceSlotConfig.PeripheralClaimLevel) }, raised);
        }

        /// <summary>The hold has a reset of its own beside its picker, which
        /// touches nothing else, and Reset All on the Lighting tab leaves
        /// it alone, as it leaves the switch.</summary>
        [Fact]
        public void TheLightingHoldHasAResetOfItsOwn_AndResetAllLeavesItAlone()
        {
            var cfg = new DeviceSlotConfig
            {
                PeripheralLightingEnabled = true, PeripheralClaimLevel = PeripheralClaimLevel.HostMode,
                LightbarMode = LightbarMode.Static,
            };
            cfg.ResetPeripheralClaimLevelCommand.Execute(null);
            Assert.Equal(PeripheralClaimLevel.Colors, cfg.PeripheralClaimLevel);
            Assert.True(cfg.PeripheralLightingEnabled);
            Assert.Equal(LightbarMode.Static, cfg.LightbarMode);
            // The switch's own reset leaves the hold as it is.
            cfg.PeripheralClaimLevel = PeripheralClaimLevel.Power;
            cfg.ResetPeripheralLightingEnabledCommand.Execute(null);
            Assert.False(cfg.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Power, cfg.PeripheralClaimLevel);

            cfg.PeripheralLightingEnabled = true;
            cfg.ResetLightbarAllCommand.Execute(null);
            Assert.Equal(LightbarMode.PlayerNumber, cfg.LightbarMode);
            Assert.True(cfg.PeripheralLightingEnabled);
            Assert.Equal(PeripheralClaimLevel.Power, cfg.PeripheralClaimLevel);
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
