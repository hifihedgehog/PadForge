using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using Xunit;
using static PadForge.Tests.KeyboardFamilyTestKit;

namespace PadForge.Tests
{
    /// <summary>The one list the presence read finds programs in, and the
    /// programs of the vendors whose rows the linker closes by vendor (#557).</summary>
    public class VendorProgramListTests
    {
        /// <summary>A program the list does not hold never reads as running,
        /// so its model writes beside it.</summary>
        [Fact]
        public void EveryProgramDeclared_IsInTheOneList()
        {
            var declared = typeof(VendorPrograms).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(VendorProgram))
                .Select(f => (VendorProgram)f.GetValue(null));
            Assert.All(declared, p => Assert.Contains(p, VendorPrograms.All));
        }

        [Fact]
        public void EveryProgramARowNames_IsInTheOneList()
        {
            foreach (var row in PluginDeviceLighting.Devices)
                Assert.All(row.Programs, p => Assert.Contains(p, VendorPrograms.All));
        }

        /// <summary>The Razer rows of the plugin table and the six Razer
        /// families of the shared worker name Synapse, or the worker hands
        /// them back on top of it when it starts.</summary>
        [Fact]
        public void EveryRazerRowAndModel_NamesSynapse()
        {
            Assert.All(PluginDeviceLighting.Devices.Where(d => d.VendorId == PeripheralLinker.RazerVid),
                row => Assert.Contains(VendorPrograms.RazerSynapse, row.Programs));
            Assert.Contains(VendorPrograms.Logitech[0], PluginDeviceLighting.Devices.Single(d => d.Name == "Logitech G19").Programs);

            HidLightingModel[] models =
            {
                RazerKrakenLighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0510), output: 37)),
                RazerKrakenV3Lighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0549), output: 15)),
                RazerKrakenV4Lighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x056B), output: 64)),
                RazerChargingPadLighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F26), feature: 91)),
                RazerArgbLighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F1F), feature: 91)),
                RazerHanboLighting.Instance.Match(CollectionFor(RazerHidTable.Registrations.Single(r => r.ProductId == 0x0F35), output: 65)),
            };
            Assert.All(models, m => Assert.Contains(VendorPrograms.RazerSynapse, m.Programs));
        }

        /// <summary>The Logitech and Razer programs are whole names, as the
        /// linker counts them, and a prefix program still takes what starts
        /// with it.</summary>
        [Fact]
        public void TheLogitechAndRazerPrograms_AreWholeNames()
        {
            Assert.True(VendorPrograms.LogitechGamingSoftware.Owns("LGS"));
            Assert.True(VendorPrograms.LogitechGamingSoftware.Owns("lcore"));
            Assert.False(VendorPrograms.LogitechGamingSoftware.Owns("lgsvc"));
            Assert.True(VendorPrograms.LogitechGHub.Owns("lghub_agent"));
            Assert.False(VendorPrograms.LogitechGHub.Owns("lghub_agent2"));
            Assert.True(VendorPrograms.RazerSynapse.Owns("RazerAppEngine"));
            Assert.False(VendorPrograms.RazerSynapse.Owns("RazerAppEngineHelper"));
            Assert.True(VendorPrograms.SteelSeriesGG.Owns("SteelSeriesGGClient"));
        }

        /// <summary>The presence read names Synapse from the same snapshot,
        /// so a Razer model's worker stands down.</summary>
        [Fact]
        public void SynapseRunning_ReadsAsTheProgramRazerModelsName()
        {
            var presence = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "RazerAppEngine.exe" });
            Assert.True(presence.RazerSoftwareRunning);
            Assert.Equal(VendorPrograms.RazerSynapse, presence.Programs.FirstOf(VendorPrograms.Razer));
        }
    }

    /// <summary>An OpenRGB controller and a HID++ unit on one device, and a
    /// device OpenRGB holds whose program runs too (#557).</summary>
    public class OpenRgbHoldLinkRepairTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000f1");
        private static readonly Guid Box = new("22222222-3333-4444-5555-666666666666");

        private static HidppUnit Headset() => PeripheralLinkingTests.Unit(1, 8, Box) with
        {
            RgbFeatureId = HidppUnitProtocol.ColorLedEffects,
            Zones = new[] { new HidppZone(0, 1) },
        };

        private static DirectUnit Held() => new(OutputFamily.OpenRgbDevice, "HID: g733", Box, PeripheralLinker.LogitechVid, 0x0AB5);

        /// <summary>One device, one row, one writer: the HID++ unit's row is
        /// lit through OpenRGB and its own path closes, and the controller
        /// gets no row beside it.</summary>
        [Fact]
        public void AHidppUnitOpenRgbHolds_IsLitThroughOpenRgb_OnOneRow()
        {
            var unit = Headset();
            var held = Held();
            var inputs = new LinkInputs(new HidppSnapshot(new[] { unit }, Array.Empty<Guid>()), PeripheralPresence.None)
            {
                Units = new[] { held },
            };
            var orphans = PeripheralLinker.Orphans(Array.Empty<LinkRow>(), inputs);
            var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
            Assert.Equal(new[] { path }, orphans.Select(u => u.Path));

            var row = new LinkRow(A, InputDeviceType.PeripheralLighting, 0x5046, 0x4C55, Guid.Empty, null) { Unit = path };
            var links = PeripheralLinker.Build(new[] { row }, inputs).For(A);
            Assert.Equal(new[] { held.Path }, links.Lighting);
            Assert.Equal(new[] { path }, links.TakenByOpenRgb);
        }

        /// <summary>A controller in another container is another device.</summary>
        [Fact]
        public void AHidppUnitInAnotherContainer_IsNotHeld()
        {
            var unit = Headset();
            var other = Held() with { ContainerId = Guid.NewGuid() };
            Assert.False(PeripheralLinker.OpenRgbHoldsHidpp(other, unit));
            Assert.True(PeripheralLinker.OpenRgbHoldsHidpp(Held(), unit));
            Assert.False(PeripheralLinker.OpenRgbHoldsHidpp(Held() with { VendorId = PeripheralLinker.RazerVid }, unit));
        }

        /// <summary>G HUB and OpenRGB side by side: the row stays, lit through
        /// OpenRGB, where it had none.</summary>
        [Fact]
        public void AHidppUnitOpenRgbHolds_KeepsItsRow_WhileGHubRuns()
        {
            var unit = Headset();
            var hub = new PeripheralPresence(false, false, false, false, false) { LogitechGHubRunning = true };
            var inputs = new LinkInputs(new HidppSnapshot(new[] { unit }, Array.Empty<Guid>()), hub) { Units = new[] { Held() } };
            Assert.Single(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), inputs));

            var alone = new LinkInputs(new HidppSnapshot(new[] { unit }, Array.Empty<Guid>()), hub);
            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), alone));
        }

        /// <summary>A mousepad with Synapse and OpenRGB both running keeps its
        /// row, lit through OpenRGB, whatever the program. Without OpenRGB it
        /// is Synapse's, and it has none.</summary>
        [Fact]
        public void AUnitOpenRgbHolds_KeepsItsRow_WhileItsProgramRuns()
        {
            var pad = new DirectUnit(OutputFamily.LampArrayUnit, "pad", Box, PeripheralLinker.RazerVid, 0x0C02);
            var held = new DirectUnit(OutputFamily.OpenRgbDevice, "HID: pad", Box, PeripheralLinker.RazerVid, 0x0C02);
            var synapse = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            var inputs = new LinkInputs(HidppSnapshot.Empty, synapse) { Units = new[] { pad, held } };
            var orphans = PeripheralLinker.Orphans(Array.Empty<LinkRow>(), inputs);
            Assert.Equal(new[] { pad.Path }, orphans.Select(u => u.Path));

            var row = new LinkRow(A, InputDeviceType.PeripheralLighting, 0x5046, 0x4C55, Guid.Empty, null) { Unit = pad.Path };
            Assert.Equal(new[] { held.Path }, PeripheralLinker.Build(new[] { row }, inputs).For(A).Lighting);

            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), new LinkInputs(HidppSnapshot.Empty, synapse) { Units = new[] { pad } }));
        }

        /// <summary>An ASUS row is watched for Armoury Crate wherever the Aura
        /// SDK is installed, whichever path lights it.</summary>
        [Fact]
        public void AnAsusRow_IsWatchedForArmouryCrate_WhereTheAuraSdkIsInstalled()
        {
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, PeripheralLinker.AsusVid, 0x19AF) { Kind = 1 };
            var keyboard = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.AsusVid, 0x19AF, Box, null);
            var aura = new PeripheralPresence(false, false, false, false, false) { AsusAuraSdk = true };
            var with = PeripheralLinker.Build(new[] { keyboard }, new LinkInputs(HidppSnapshot.Empty, aura) { Units = new[] { lamp } }).For(A);
            Assert.True(with.HasVendorProgram);
            var without = PeripheralLinker.Build(new[] { keyboard },
                new LinkInputs(HidppSnapshot.Empty, PeripheralPresence.None) { Units = new[] { lamp } }).For(A);
            Assert.False(without.HasVendorProgram);
        }
    }

    /// <summary>The Lighting tab's lines for a refused session and a device
    /// that stores its pictures (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class RefusedAndStoredRouteTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000f2");
        private static readonly Guid Box = new("22222222-3333-4444-5555-666666666667");
        private static readonly Guid OtherBox = new("22222222-3333-4444-5555-666666666668");

        public RefusedAndStoredRouteTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralOutputs.SetRunning(true);
        }

        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static UserDevice Row(ushort vendor, ushort product) => new()
        {
            InstanceGuid = A, IsOnline = true, VendorId = vendor, ProdId = product, CapType = InputDeviceType.Keyboard,
            PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
        };

        private static DirectSnapshot Found(params DirectUnit[] units)
            => new(units, new[] { OutputFamily.HidLightingUnit });

        /// <summary>The link pass found the row in the units' container.</summary>
        private static void OnBox(Guid device) => PeripheralOutputs.PublishLinks(PeripheralLinker.Build(
            new[] { new LinkRow(device, InputDeviceType.Keyboard, 0x3434, 0x0123, Box, null) }, new LinkInputs(null, null)));

        /// <summary>A device that opened and took no session is not told to
        /// wake up. Another device that sleeps still is.</summary>
        [Fact]
        public void ARefusedDevice_SaysSo_AndASleepingOneStillSaysItSleeps()
        {
            var s = Strings.Instance;
            var refused = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x3434, 0x0123) { Asleep = true, Refused = true };
            OnBox(A);
            PeripheralOutputs.Direct = Found(refused);
            Assert.Equal(s.Pad_Lighting_RouteRefused, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(refused with { Refused = false });
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(refused with { WindowsHolds = true });
            Assert.Equal(s.Pad_Lighting_RouteWindowsHolds, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
        }

        /// <summary>A device Windows holds, or another program has open, says so
        /// whichever way it came to sleep, where a line about waking it would
        /// send the user to wake a device that is awake.</summary>
        [Fact]
        public void AHeldDevice_SaysWhoHoldsIt_WithOrWithoutARefusal()
        {
            var s = Strings.Instance;
            var asleep = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x3434, 0x0123) { Asleep = true };
            OnBox(A);
            PeripheralOutputs.Direct = Found(asleep with { HeldByProgram = true });
            Assert.Equal(s.Pad_Lighting_RouteHeldByProgram, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(asleep with { Refused = true, HeldByProgram = true });
            Assert.Equal(s.Pad_Lighting_RouteHeldByProgram, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(asleep with { WindowsHolds = true });
            Assert.Equal(s.Pad_Lighting_RouteWindowsHolds, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(asleep);
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
        }

        /// <summary>Two identical devices: the row of the one in another container
        /// reads as asleep, and the refusal belongs to the row of the device in the
        /// unit's own. A row whose container is not known yet claims nothing.</summary>
        [Fact]
        public void ARefusedDevice_DoesNotSpeakForItsTwin()
        {
            var s = Strings.Instance;
            var refused = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x3434, 0x0123) { Asleep = true, Refused = true };
            PeripheralOutputs.Direct = Found(refused);

            PeripheralOutputs.PublishLinks(PeripheralLinker.Build(
                new[] { new LinkRow(A, InputDeviceType.Keyboard, 0x3434, 0x0123, OtherBox, null) },
                new LinkInputs(null, null) { Units = new[] { refused } }));
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));

            PeripheralOutputs.PublishLinks(PeripheralLinker.Build(
                new[] { new LinkRow(A, InputDeviceType.Keyboard, 0x3434, 0x0123, Guid.Empty, null) },
                new LinkInputs(null, null) { Units = new[] { refused } }));
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));

            PeripheralOutputs.PublishLinks(PeripheralLinker.Build(
                new[] { new LinkRow(A, InputDeviceType.Keyboard, 0x3434, 0x0123, Box, null) },
                new LinkInputs(null, null) { Units = new[] { refused } }));
            Assert.Equal(s.Pad_Lighting_RouteRefused, PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
        }

        /// <summary>A row that learns its container with no path changed makes a
        /// new table, or its line would stay as it was.</summary>
        [Fact]
        public void ARowThatLearnsItsContainer_MakesANewTable()
        {
            LinkTable Of(Guid container) => PeripheralLinker.Build(
                new[] { new LinkRow(A, InputDeviceType.Keyboard, 0x3434, 0x0123, container, null) }, new LinkInputs(null, null));
            Assert.False(Of(Guid.Empty).SameAs(Of(Box)));
            Assert.True(Of(Box).SameAs(Of(Box)));
        }

        /// <summary>A device that stores each picture says a changing color
        /// does not show. One that does not keeps the plain line.</summary>
        [Fact]
        public void ADeviceThatStoresItsPictures_SaysChangingColorsStayStill()
        {
            var s = Strings.Instance;
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x3434, 0x0123) { Name = "Board", StoresPictures = true };
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = A, Lighting = new[] { unit.Path } } }));
            PeripheralOutputs.Direct = Found(unit);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectStored, "Board"), PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
            PeripheralOutputs.Direct = Found(unit with { StoresPictures = false });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "Board"), PeripheralRouteText.Lighting(Row(0x3434, 0x0123), 0));
        }

        /// <summary>A device whose pictures write over the effects stored on it
        /// says those are lost, where the user turns the row on.</summary>
        [Fact]
        public void ADeviceThatLosesItsStoredEffects_SaysSo()
        {
            var s = Strings.Instance;
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x05AC, 0x024F)
            {
                Name = "Board", StoresPictures = true, ReplacesStoredEffects = true,
            };
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = A, Lighting = new[] { unit.Path } } }));
            PeripheralOutputs.Direct = Found(unit);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectStoredEffects, "Board"), PeripheralRouteText.Lighting(Row(0x05AC, 0x024F), 0));
            Assert.Contains("effects", s.Pad_Lighting_RouteDirectStoredEffects);
            // Both models whose pictures rewrite the effect table say so.
            Assert.True(KeychronKeyboardLighting.Instance.Match(KeyboardFamilyTestKit.CollectionFor(
                OpenRgbRegistrations.Of(KeychronKeyboardLighting.OpenRgbFamily).Single(), feature: 65)).ReplacesStoredEffects);
            Assert.True(MintakaKeyboardLighting.Instance.Match(KeyboardFamilyTestKit.CollectionFor(
                OpenRgbRegistrations.Of(MintakaKeyboardLighting.OpenRgbFamily).Single(), feature: 65)).ReplacesStoredEffects);
        }

        /// <summary>A keyboard that shows changing colors through its LampArray
        /// has no such path right now. That says nothing of the BIOS option behind
        /// it, so the line states the condition and not that they do not show.</summary>
        [Fact]
        public void AKeyboardWithNoLampArrayPathRightNow_StillStatesTheCondition()
        {
            var s = Strings.Instance;
            var unit = new DirectUnit(OutputFamily.HidLightingUnit, "unit", Box, 0x048D, 0xC197)
            {
                Name = "Spectrum", LiveThroughLampArray = true, StoresPictures = true,
            };
            PeripheralOutputs.PublishLinks(new LinkTable(new[] { new DeviceLinks { Device = A, Lighting = new[] { unit.Path } } }));
            PeripheralOutputs.Direct = Found(unit);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectChangingNeedsBios, "Spectrum"), PeripheralRouteText.Lighting(Row(0x048D, 0xC197), 0));
        }
    }

    /// <summary>The Dell G series' pace, and a look through the worker's walk
    /// (#557).</summary>
    public class SlowFamilyBoundTests
    {
        /// <summary>A picture takes 0.6 to 2 s of reports on the one worker,
        /// so a changing color leaves the other units their turn.</summary>
        [Fact]
        public void TheDellGSeries_LeavesTheWorkerFreeBetweenPictures()
            => Assert.True(AlienwareLighting.RepaintMs >= 2000);

        /// <summary>The Sinowealth look reads the walk it is handed and makes
        /// none of its own.</summary>
        [Fact]
        public void TheSinowealthLook_ReadsTheWalkItIsHanded()
        {
            int walks = 0;
            var family = new SinowealthMouseLighting(() =>
            {
                walks++;
                return Array.Empty<HidCollectionInfo>();
            }, _ => Guid.Empty, null, null);
            Assert.NotNull(family.Look(Array.Empty<HidCollectionInfo>()));
            Assert.Equal(0, walks);
            Assert.NotNull(family.Look());
            Assert.Equal(1, walks);
        }

        /// <summary>The HID walk's probe opens nothing in the test host.</summary>
        [Fact]
        public void TheWalksProbe_OpensNothingInTheTestHost()
            => Assert.Empty(HidCollectionRuntime.Enumerate());
    }
}
