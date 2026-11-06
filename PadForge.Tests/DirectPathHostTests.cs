using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    // ─────────────────────────────────────────────
    //  The facade, the tab's lines and the dispatcher: shared state
    // ─────────────────────────────────────────────

    /// <summary>What the direct paths add at the meeting point between the
    /// engine and the backends (#494): the hold a claim carries, the units
    /// the tabs name, and the lines that name them.</summary>
    [Collection("PeripheralOutputStatics")]
    public class DirectPathFacadeTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000b2");
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");

        public DirectPathFacadeTests()
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

        private static UserDevice Device(Guid id, ushort vid, int capType = InputDeviceType.Mouse)
            => new()
            {
                InstanceGuid = id, IsOnline = true, CapType = capType, VendorId = vid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            };

        private static DirectSnapshot Found(params DirectUnit[] units)
            => new(units, units.Select(u => u.Family).Distinct().ToArray());

        // ── The claim's hold ──

        /// <summary>A claim carries how firmly its slot may hold the device,
        /// and the ruling claim's is the one a backend reads. A hold that
        /// moves wakes the workers and redraws the tab, as a rank does.</summary>
        [Fact]
        public void AClaimCarriesItsHold_AndTheRulingClaimsIsTheOneABackendReads()
        {
            var unit = new OutputPath(OutputFamily.HidppUnit, "rx|01");
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit }, MaxClaimLevel = 2 });
            int claims = 0, wakes = 0;
            void OnClaims() => claims++;
            void OnLighting() => wakes++;
            PeripheralOutputs.ClaimsChanged += OnClaims;
            PeripheralOutputs.LightingChanged += OnLighting;
            try
            {
                // Every caller that names no hold asks for colors alone.
                PeripheralOutputs.SetLighting(A, slot: 3, player: 2, 1, 2, 3);
                Assert.True(PeripheralOutputs.TryResolveColor(unit, out _, out var ruler));
                Assert.Equal(0, ruler.Level);
                Assert.Equal((1, 1), (claims, wakes));

                PeripheralOutputs.SetLighting(A, slot: 3, player: 2, 1, 2, 3, level: 2);
                Assert.True(PeripheralOutputs.TryResolveColor(unit, out int rgb, out ruler));
                Assert.Equal(2, ruler.Level);
                Assert.Equal(0x010203, rgb);
                Assert.Equal((2, 2), (claims, wakes));

                // The same hold again is no change, and a color alone wakes
                // the workers without redrawing the tab.
                PeripheralOutputs.SetLighting(A, slot: 3, player: 2, 1, 2, 3, level: 2);
                Assert.Equal((2, 2), (claims, wakes));
                PeripheralOutputs.SetLighting(A, slot: 3, player: 2, 9, 9, 9, level: 2);
                Assert.Equal((2, 3), (claims, wakes));

                // Another controller with the smaller number rules, and its
                // hold is the device's, never the larger of the two.
                PeripheralOutputs.SetLighting(A, slot: 7, player: 1, 4, 5, 6);
                Assert.True(PeripheralOutputs.TryResolveColor(unit, out _, out ruler));
                Assert.Equal((7, 0), (ruler.Slot, ruler.Level));
                PeripheralOutputs.ReleaseLighting(A, 7);
                Assert.True(PeripheralOutputs.TryResolveColor(unit, out _, out ruler));
                Assert.Equal((3, 2), (ruler.Slot, ruler.Level));
            }
            finally
            {
                PeripheralOutputs.ClaimsChanged -= OnClaims;
                PeripheralOutputs.LightingChanged -= OnLighting;
            }

            // The hold never ranks a claim.
            var low = new PeripheralOutputs.Claimant(A, false, 1, 0, 0);
            var high = new PeripheralOutputs.Claimant(B, false, 2, 0, 0) { Level = 2 };
            Assert.True(PeripheralOutputs.Rules(low, high));
            Assert.False(PeripheralOutputs.Rules(high, low));
        }

        /// <summary>The link pass looks for a starting vendor program on
        /// every pass only while a claim holds a path that program would
        /// take: a unit of the device's own protocol, or a LampArray unit
        /// on a row of a vendor whose program drives its devices. A unit
        /// Windows lights is not painted, and a device of any other vendor
        /// has no program to yield to, so neither is watched for.</summary>
        [Fact]
        public void AClaimAVendorProgramWouldTake_IsOneOnTheDevicesOwnUnit_OrOnALampArrayUnitOfThatVendorsRow()
        {
            var category = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            var rumble = new OutputPath(OutputFamily.HidppUnit, "rx|01");
            var buzz = new OutputPath(OutputFamily.TouchSenseUnit, "winusb");
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());

            // A claim on a shared path, and units linked for rumble alone.
            Link(new DeviceLinks { Device = A, Lighting = new[] { category }, Haptics = new[] { rumble, buzz }, HasVendorProgram = true });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());

            // A unit of the device's own protocol, in each of the three.
            foreach (var family in new[] { OutputFamily.HidppUnit, OutputFamily.RazerUnit, OutputFamily.SteelSeriesUnit })
            {
                var unit = new OutputPath(family, "unit");
                Link(new DeviceLinks { Device = A, Lighting = new[] { unit }, HasVendorProgram = true });
                Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes(), family.ToString());
                // No claim on it: nothing to take.
                PeripheralOutputs.ReleaseLighting(A, 0);
                Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes(), family.ToString());
                PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            }

            // A LampArray unit counts only on a row of a vendor whose
            // program drives its devices.
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x1532, 0x0084);
            PeripheralOutputs.Direct = Found(lamp);
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path }, HasVendorProgram = true });
            Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path } });
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path }, UnitRow = true });
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            // The row of a unit that is such a vendor's device is one its
            // program takes too.
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path }, UnitRow = true, HasVendorProgram = true });
            Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            // Held by two rows, one of them such a vendor's.
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path } },
                 new DeviceLinks { Device = B, Lighting = new[] { lamp.Path }, HasVendorProgram = true });
            Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());

            // Windows lights the unit: its backend paints nothing, so there
            // is no write for a program to meet.
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path }, HasVendorProgram = true });
            PeripheralOutputs.Direct = Found(lamp with { WindowsLit = true });
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            PeripheralOutputs.Direct = Found(lamp);
            Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());

            // A claim whose row lost the path lights nothing on its own.
            Link(new DeviceLinks { Device = A, Lighting = new[] { category }, HasVendorProgram = true });
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            Assert.False(PeripheralOutputs.WatchesVendorPrograms(Environment.TickCount64));
        }

        /// <summary>Set Chroma Color paints the Razer devices on its slot
        /// whatever their lighting switch says. A live macro on a slot that
        /// holds a row lit through a Razer unit of its own is therefore a
        /// write a starting Synapse would meet, claim or no claim, and it
        /// is watched for like one.</summary>
        [Fact]
        public void ALiveChromaMacro_OnASlotWhoseRowHoldsARazerUnit_IsWatchedForLikeAClaim()
        {
            var oldSettings = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = A, MapTo = 3 });
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = B, MapTo = 5 });
                var unit = new OutputPath(OutputFamily.RazerUnit, "razer");
                var category = new OutputPath(OutputFamily.ChromaCategory, "mouse");
                Link(new DeviceLinks { Device = A, Lighting = new[] { unit }, HasVendorProgram = true },
                     new DeviceLinks { Device = B, Lighting = new[] { category }, HasVendorProgram = true });
                const long now = 50_000;
                Assert.False(PeripheralOutputs.AnyChromaMacroOnARazerUnit(now));
                Assert.False(PeripheralOutputs.WatchesVendorPrograms(now));

                // On the slot of a row lit through Synapse, which the Chroma
                // backend paints, and on a slot that holds no device.
                PeripheralOutputs.AssertChromaMacro(5, 1, 2, 3, now);
                PeripheralOutputs.AssertChromaMacro(7, 1, 2, 3, now);
                Assert.False(PeripheralOutputs.AnyChromaMacroOnARazerUnit(now));

                // On the slot of the row that holds the unit: watched for,
                // with no claim anywhere.
                PeripheralOutputs.AssertChromaMacro(3, 1, 2, 3, now);
                Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
                Assert.True(PeripheralOutputs.AnyChromaMacroOnARazerUnit(now));
                Assert.True(PeripheralOutputs.WatchesVendorPrograms(now));
                // For as long as the macro is live, and no longer.
                long last = now + PeripheralOutputs.MacroAssertWindowMs;
                Assert.True(PeripheralOutputs.AnyChromaMacroOnARazerUnit(last));
                Assert.False(PeripheralOutputs.AnyChromaMacroOnARazerUnit(last + 1));
                Assert.False(PeripheralOutputs.WatchesVendorPrograms(last + 1));

                // The row moved to Synapse's category: no unit of its own
                // is written to.
                Link(new DeviceLinks { Device = A, Lighting = new[] { category }, HasVendorProgram = true });
                Assert.False(PeripheralOutputs.AnyChromaMacroOnARazerUnit(now));
            }
            finally
            {
                SettingsManager.UserSettings = oldSettings;
            }
        }

        // ── What the tabs read ──

        [Fact]
        public void TheDirectUnits_RaiseStatusOnlyWhenTheyDiffer()
        {
            int raised = 0;
            void OnStatus() => raised++;
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x048D, 0xC193) { Name = "Lenovo Lighting", Kind = 1 };
            PeripheralOutputs.StatusChanged += OnStatus;
            try
            {
                PeripheralOutputs.Direct = Found(lamp);
                Assert.Equal(1, raised);
                // The link pass builds a new snapshot on every pass.
                PeripheralOutputs.Direct = Found(lamp with { });
                Assert.Equal(1, raised);
                Assert.Equal(lamp, PeripheralOutputs.Direct.Find(lamp.Path));
                Assert.Null(PeripheralOutputs.Direct.Find(new OutputPath(OutputFamily.RazerUnit, "lamp")));

                PeripheralOutputs.Direct = Found(lamp with { WindowsLit = true });
                Assert.Equal(2, raised);
                PeripheralOutputs.Direct = new DirectSnapshot(new[] { lamp with { WindowsLit = true } },
                    new[] { OutputFamily.LampArrayUnit }, scanned: false);
                Assert.Equal(3, raised);
                PeripheralOutputs.Direct = new DirectSnapshot(new[] { lamp with { WindowsLit = true } },
                    new[] { OutputFamily.LampArrayUnit, OutputFamily.RazerUnit }, scanned: false);
                Assert.Equal(4, raised);
                PeripheralOutputs.Direct = null;
                Assert.Equal(5, raised);
                Assert.Same(DirectSnapshot.Empty, PeripheralOutputs.Direct);
            }
            finally { PeripheralOutputs.StatusChanged -= OnStatus; }

            Assert.True(DirectSnapshot.Empty.Scanned);
            Assert.Empty(DirectSnapshot.Empty.Families);
            Assert.False(DirectSnapshot.Empty.SameAs(null));
        }

        /// <summary>The poll thread opens and retires rows when the list is
        /// a new one, so a list that holds the same units stays in place.</summary>
        [Fact]
        public void TheUnitsThatGetRows_StayInPlace_WhileTheyAreTheSameUnits()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, 0x1234, 1);
            var strip = new DirectUnit(OutputFamily.LampArrayUnit, "strip", Box, 0x1234, 2);
            Assert.Empty(PeripheralOutputs.UnitRows);

            var first = new[] { headset };
            PeripheralOutputs.UnitRows = first;
            Assert.Same(first, PeripheralOutputs.UnitRows);
            PeripheralOutputs.UnitRows = new[] { headset with { } };
            Assert.Same(first, PeripheralOutputs.UnitRows);

            var second = new[] { headset, strip };
            PeripheralOutputs.UnitRows = second;
            Assert.Same(second, PeripheralOutputs.UnitRows);
            PeripheralOutputs.UnitRows = null;
            Assert.Empty(PeripheralOutputs.UnitRows);

            // The test seam forgets both.
            PeripheralOutputs.UnitRows = first;
            PeripheralOutputs.Direct = Found(headset);
            PeripheralOutputs.ResetForTest();
            Assert.Empty(PeripheralOutputs.UnitRows);
            Assert.Same(DirectSnapshot.Empty, PeripheralOutputs.Direct);
        }

        // ── The Lighting tab's line ──

        /// <summary>A device with lighting on record and no path now is one
        /// asleep, unplugged or not scanned for yet. Outside Logitech that
        /// can only be while a direct backend runs that could find it, and a
        /// vendor row stands for software, which never sleeps.</summary>
        [Fact]
        public void ADeviceWithNoPathNow_ReadsAsAsleep_OnlyWhileSomethingCouldFindIt()
        {
            var s = Strings.Instance;
            var razer = Device(A, PeripheralLinker.RazerVid);
            var logitech = Device(B, PeripheralLinker.LogitechVid);
            var chroma = Device(PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerChroma), 0x5046, InputDeviceType.PeripheralLighting);
            var unitRow = Device(PeripheralUnitRow.IdentityFor(OutputFamily.LampArrayUnit, "headset"), 0x5046, InputDeviceType.PeripheralLighting);

            // No direct backend: as it was.
            Assert.Null(PeripheralRouteText.Lighting(razer, 0));
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(logitech, 0));
            Assert.Null(PeripheralRouteText.Lighting(chroma, 0));
            Assert.Null(PeripheralRouteText.Lighting(unitRow, 0));

            PeripheralOutputs.Direct = new DirectSnapshot(Array.Empty<DirectUnit>(), new[] { OutputFamily.LampArrayUnit }, scanned: false);
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(razer, 0));
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(logitech, 0));
            Assert.Equal(s.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(unitRow, 0));
            Assert.Null(PeripheralRouteText.Lighting(chroma, 0));
            // A controller that leaves the device alone names no wait for it.
            Assert.Null(PeripheralRouteText.Lighting(razer, 0, lightsHere: false));
            // Nothing on record, nothing to wait for.
            Assert.Null(PeripheralRouteText.Lighting(new UserDevice { InstanceGuid = A, VendorId = PeripheralLinker.RazerVid }, 0));
        }

        [Fact]
        public void AUnitWindowsLights_NamesWindowsAndTheSwitch_AndALaptopsOwnKeyboardWhatItCosts()
        {
            var s = Strings.Instance;
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x0B05, 0x1A2B) { Name = "ROG Falchion", WindowsLit = true };
            var keyboard = Device(A, 0x0B05, InputDeviceType.Keyboard);
            Link(new DeviceLinks { Device = A, Lighting = new[] { lamp.Path } });
            PeripheralOutputs.Direct = Found(lamp);

            Assert.Equal(s.Pad_Lighting_RouteWindowsLighting, PeripheralRouteText.Lighting(keyboard, 0));
            // Nothing shows from any controller, so a controller that leaves
            // the device alone has nothing to name.
            Assert.Null(PeripheralRouteText.Lighting(keyboard, 0, lightsHere: false));
            // Windows holds it whoever claims it, so no other controller's
            // color is named either.
            PeripheralOutputs.SetLighting(A, slot: 5, player: 1, 1, 1, 1);
            Assert.Equal(s.Pad_Lighting_RouteWindowsLighting, PeripheralRouteText.Lighting(keyboard, 2));
            PeripheralOutputs.ReleaseDevice(A);

            // Built into the PC: the keyboard's line says what the switch
            // costs, and any other row there gets the plain line.
            var builtIn = lamp with { ContainerId = PeripheralLinker.LocalMachineContainer };
            PeripheralOutputs.Direct = Found(builtIn);
            Assert.Equal(s.Pad_Lighting_RouteWindowsLightingBuiltIn, PeripheralRouteText.Lighting(keyboard, 0));
            Assert.Equal(s.Pad_Lighting_RouteWindowsLightingBuiltIn,
                PeripheralRouteText.Lighting(Device(A, 0x0B05, InputDeviceType.AnalogKeyboard), 0));
            Assert.Equal(s.Pad_Lighting_RouteWindowsLighting, PeripheralRouteText.Lighting(Device(A, 0x0B05, InputDeviceType.Mouse), 0));
            Assert.Equal(s.Pad_Lighting_RouteWindowsLighting,
                PeripheralRouteText.Lighting(Device(A, 0x5046, InputDeviceType.PeripheralLighting), 0));

            // The gate on a Logitech device reads the same way: its row holds
            // the LampArray path while Windows lights it.
            Assert.Equal(s.Pad_Lighting_RouteWindowsLightingBuiltIn,
                PeripheralRouteText.Lighting(Device(A, PeripheralLinker.LogitechVid, InputDeviceType.Keyboard), 0));

            // Dynamic Lighting off for it: PadForge lights it.
            PeripheralOutputs.Direct = Found(builtIn with { WindowsLit = false });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "ROG Falchion"), PeripheralRouteText.Lighting(keyboard, 0));
        }

        /// <summary>A keyboard whose own controller keeps still pictures
        /// only shows changing colors through a LampArray interface of its
        /// device, on any brand. Its line says so and how to turn that on, with
        /// the interface linked or not, since PadForge reads no such setting,
        /// and with Windows holding the interface, what Windows takes and how to
        /// hand it back. With no such interface it says changing colors do not
        /// show. A controller that shows a stream keeps the plain line, beside a
        /// LampArray interface or not.</summary>
        [Fact]
        public void AControllerThatKeepsStillPicturesOnly_SaysWhatChangingColorsNeed()
        {
            var s = Strings.Instance;
            var pc = PeripheralLinker.LocalMachineContainer;
            var spectrum = new DirectUnit(OutputFamily.HidLightingUnit, "spectrum", pc, 0x048D, 0xC197)
            {
                Name = "ITE Device(8258)", LitBesideWindows = true, StoresPictures = true,
            };
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", pc, 0x048D, 0xC193) { Kind = 1 };
            var keyboard = Device(A, 0x048D, InputDeviceType.Keyboard);

            Link(new DeviceLinks { Device = A, Lighting = new[] { spectrum.Path, lamp.Path }, HasLampArray = true });
            PeripheralOutputs.Direct = Found(spectrum, lamp);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectChangingThroughLampArray, "ITE Device(8258)"),
                PeripheralRouteText.Lighting(keyboard, 0));
            Assert.Null(PeripheralRouteText.Lighting(keyboard, 0, lightsHere: false));

            Link(new DeviceLinks { Device = A, Lighting = new[] { spectrum.Path }, WindowsLightsLampArray = true, HasLampArray = true });
            PeripheralOutputs.Direct = Found(spectrum, lamp with { WindowsLit = true });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectBesideWindows, "ITE Device(8258)"),
                PeripheralRouteText.Lighting(keyboard, 0));

            // The interface asleep for a moment: the path is down and the firmware
            // is as unknown as before, so the line stays the same.
            Link(new DeviceLinks { Device = A, Lighting = new[] { spectrum.Path }, HasLampArray = true });
            PeripheralOutputs.Direct = Found(spectrum, lamp with { Asleep = true });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectChangingThroughLampArray, "ITE Device(8258)"),
                PeripheralRouteText.Lighting(keyboard, 0));

            // No such interface on the device: changing colors have no way there.
            Link(new DeviceLinks { Device = A, Lighting = new[] { spectrum.Path } });
            PeripheralOutputs.Direct = Found(spectrum);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirectStored, "ITE Device(8258)"),
                PeripheralRouteText.Lighting(keyboard, 0));

            // A controller that shows a stream reads as any unit does, with
            // a LampArray interface beside it, as on an ASUS laptop, or not.
            var besideALamp = spectrum with { StoresPictures = false, LiveThroughLampArray = true };
            Link(new DeviceLinks { Device = A, Lighting = new[] { besideALamp.Path, lamp.Path }, HasLampArray = true });
            PeripheralOutputs.Direct = Found(besideALamp, lamp);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "ITE Device(8258)"), PeripheralRouteText.Lighting(keyboard, 0));
            var streams = spectrum with { StoresPictures = false };
            Link(new DeviceLinks { Device = A, Lighting = new[] { streams.Path, lamp.Path }, HasLampArray = true });
            PeripheralOutputs.Direct = Found(streams, lamp);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "ITE Device(8258)"), PeripheralRouteText.Lighting(keyboard, 0));
        }

        /// <summary>A single light's color is set by its address on the
        /// lights the row's Key Colors card shows, the first of its paths
        /// that knows its lights one by one. Every other path of the row
        /// takes the same frame without those colors, its keys, groups and
        /// background kept, since the address is not its own. A frame with
        /// no single light's color goes to every path as it is.</summary>
        [Fact]
        public void ASingleLightsColor_GoesOnlyToThePathWhoseLightsTheCardShows()
        {
            var native = new OutputPath(OutputFamily.HidLightingUnit, "spectrum");
            var lamp = new OutputPath(OutputFamily.LampArrayUnit, "lamp");
            LightMap Map(int first) => new(new[]
            {
                new LightInfo(first, LightKind.Key, 0x04, LightKeySource.Generated),
                new LightInfo(first + 1, LightKind.Other, 0, LightKeySource.None),
            }, "test");
            PeripheralLightMaps.ResetForTest();
            try
            {
                PeripheralLightMaps.Publish(native, Map(0x61));
                PeripheralLightMaps.Publish(lamp, Map(0x61));
                Link(new DeviceLinks { Device = A, Lighting = new[] { native, lamp } });
                var under = new KeyFrame(null, null, new Dictionary<int, int> { [0x62] = 0x00FF00 }, null);
                var frame = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0x0000FF },
                    new Dictionary<string, int> { [LightGroups.Edge] = 0x123456 },
                    new Dictionary<int, int> { [0x62] = 0xFF0000 }, 0x111111, under);
                PeripheralOutputs.SetLighting(A, slot: 0, player: 1, 1, 2, 3, keys: frame);

                Assert.True(PeripheralOutputs.TryResolveColor(native, out int rgb, out var ruler));
                Assert.Same(frame, ruler.Keys);
                Assert.Equal(0x010203, rgb);

                Assert.True(PeripheralOutputs.TryResolveColor(lamp, out rgb, out ruler));
                Assert.Equal(0x010203, rgb);
                Assert.False(ruler.Keys.HasLights);
                Assert.Empty(ruler.Keys.Lights);
                Assert.Null(ruler.Keys.Under);
                Assert.Equal(0x0000FF, ruler.Keys.Keys[0x04]);
                Assert.Equal(0x123456, ruler.Keys.Groups[LightGroups.Edge]);
                Assert.Equal(0x111111, ruler.Keys.Background);
                // The light the address names on that path keeps the
                // background, never the other unit's single-light color.
                Assert.Equal(new[] { 0x0000FF, 0x111111 },
                    LightRenderer.Render(PeripheralLightMaps.For(lamp), rgb, ruler.Keys));

                // The card shows the first path that knows its lights one by
                // one: with the LampArray first, it keeps them instead.
                Link(new DeviceLinks { Device = A, Lighting = new[] { lamp, native } });
                Assert.True(PeripheralOutputs.TryResolveColor(lamp, out _, out ruler));
                Assert.Same(frame, ruler.Keys);
                Assert.True(PeripheralOutputs.TryResolveColor(native, out _, out ruler));
                Assert.False(ruler.Keys.HasLights);

                // A frame of keys and groups alone reaches both as it is.
                var keysOnly = new KeyFrame(new Dictionary<ushort, int> { [0x04] = 0x0000FF }, null, null, null);
                PeripheralOutputs.SetLighting(A, slot: 0, player: 1, 1, 2, 3, keys: keysOnly);
                Assert.True(PeripheralOutputs.TryResolveColor(native, out _, out ruler));
                Assert.Same(keysOnly, ruler.Keys);
                Assert.True(PeripheralOutputs.TryResolveColor(lamp, out _, out ruler));
                Assert.Same(keysOnly, ruler.Keys);
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        }

        /// <summary>A unit of any direct family reads as a HID++ unit does:
        /// by the name its backend read, else the row's, and with the
        /// controller whose color shows when another claim rules it.</summary>
        [Theory]
        [InlineData((int)OutputFamily.RazerUnit)]
        [InlineData((int)OutputFamily.SteelSeriesUnit)]
        [InlineData((int)OutputFamily.LampArrayUnit)]
        public void AUnitThisControllerLights_IsNamedAsItsBackendReadIt(int familyNumber)
        {
            var family = (OutputFamily)familyNumber;
            var s = Strings.Instance;
            var unit = new DirectUnit(family, "unit", Box, 0x1532, 0x0084) { Name = "DeathAdder V2" };
            var mouse = Device(A, 0x1532);
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit.Path } },
                 new DeviceLinks { Device = B, Lighting = new[] { unit.Path } });
            PeripheralOutputs.Direct = Found(unit);

            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "DeathAdder V2"), PeripheralRouteText.Lighting(mouse, 4));
            Assert.Null(PeripheralRouteText.Lighting(mouse, 4, lightsHere: false));
            // These families offer no hold, whatever the slot asks for.
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, "DeathAdder V2"), PeripheralRouteText.Lighting(mouse, 4, true, claimLevel: 2));

            // The backend read no name, or the unit is not in the snapshot.
            PeripheralOutputs.Direct = Found(unit with { Name = " " });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, mouse.ResolvedName), PeripheralRouteText.Lighting(mouse, 4));
            PeripheralOutputs.Direct = DirectSnapshot.Empty;
            Assert.Equal(string.Format(s.Pad_Lighting_RouteDirect, mouse.ResolvedName), PeripheralRouteText.Lighting(mouse, 4));

            // The same device on another controller, then another row of it.
            PeripheralOutputs.SetLighting(A, slot: 4, player: 2, 1, 1, 1);
            PeripheralOutputs.SetLighting(A, slot: 7, player: 1, 2, 2, 2);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherController, 1), PeripheralRouteText.Lighting(mouse, 4));
            Assert.Equal(string.Format(s.Pad_Lighting_RouteOtherControllerOnly, 1), PeripheralRouteText.Lighting(mouse, 9, lightsHere: false));
            PeripheralOutputs.ReleaseLighting(A, 7);
            PeripheralOutputs.ReleaseLighting(A, 4);
            PeripheralOutputs.SetLighting(B, slot: 4, player: 2, 3, 3, 3);
            Assert.Equal(s.Pad_Lighting_RouteOtherRow, PeripheralRouteText.Lighting(mouse, 4));
            Assert.Null(PeripheralRouteText.LightingApp(family));

            // A stopped engine reaches no device, so the line says nothing.
            PeripheralOutputs.SetRunning(false);
            Assert.Null(PeripheralRouteText.Lighting(mouse, 4));
            Assert.Null(PeripheralRouteText.Lighting(null, 4));
        }

        [Fact]
        public void TheLineNamesWhatIsHeld_AtTheSmallerOfTheSlotsHoldAndTheDevices()
        {
            var s = Strings.Instance;
            var unit = new HidppUnit { ChannelPath = "rx", DeviceIndex = 1, Name = "G915 X" };
            var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
            var keyboard = Device(A, PeripheralLinker.LogitechVid, InputDeviceType.Keyboard);
            PeripheralOutputs.Hidpp = new HidppSnapshot(new[] { unit }, Array.Empty<Guid>());

            string plain = string.Format(s.Pad_Lighting_RouteDirect, "G915 X");
            string power = string.Format(s.Pad_Lighting_RouteDirectPower, "G915 X");
            string hostMode = string.Format(s.Pad_Lighting_RouteDirectHostMode, "G915 X");
            Assert.NotEqual(plain, power);
            Assert.NotEqual(power, hostMode);

            Link(new DeviceLinks { Device = A, Lighting = new[] { path }, MaxClaimLevel = 2 });
            Assert.Equal(plain, PeripheralRouteText.Lighting(keyboard, 0));
            Assert.Equal(plain, PeripheralRouteText.Lighting(keyboard, 0, true, claimLevel: 0));
            Assert.Equal(power, PeripheralRouteText.Lighting(keyboard, 0, true, claimLevel: 1));
            Assert.Equal(hostMode, PeripheralRouteText.Lighting(keyboard, 0, true, claimLevel: 2));

            // A board that must never leave its on-board memory mode.
            Link(new DeviceLinks { Device = A, Lighting = new[] { path }, MaxClaimLevel = 1 });
            Assert.Equal(power, PeripheralRouteText.Lighting(keyboard, 0, true, claimLevel: 2));
            // The zone-only class offers no hold.
            Link(new DeviceLinks { Device = A, Lighting = new[] { path } });
            Assert.Equal(plain, PeripheralRouteText.Lighting(keyboard, 0, true, claimLevel: 2));
        }

        [Fact]
        public void ASharedPathWithADirectOneBehindIt_SaysWhatHappensWhenTheProgramStops()
        {
            var s = Strings.Instance;
            var category = new OutputPath(OutputFamily.ChromaCategory, "mouse");
            var mouse = Device(A, PeripheralLinker.RazerVid);

            Link(new DeviceLinks { Device = A, Lighting = new[] { category }, DirectWhenClosed = true });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThroughOrDirect, "Razer Synapse"), PeripheralRouteText.Lighting(mouse, 0));
            Assert.Null(PeripheralRouteText.Lighting(mouse, 0, lightsHere: false));
            Link(new DeviceLinks { Device = A, Lighting = new[] { category } });
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThrough, "Razer Synapse"), PeripheralRouteText.Lighting(mouse, 0));

            // The program is named more than once, and its name fills
            // every place.
            int named = s.Pad_Lighting_RouteThroughOrDirect.Split("{0}").Length - 1;
            Assert.True(named >= 2);
            var steelSeries = new OutputPath(OutputFamily.GameSenseColor, "mouse");
            Link(new DeviceLinks { Device = A, Lighting = new[] { steelSeries }, DirectWhenClosed = true });
            string line = PeripheralRouteText.Lighting(Device(A, PeripheralLinker.SteelSeriesVid), 0);
            Assert.Equal(named, line.Split("SteelSeries GG").Length - 1);
            Assert.DoesNotContain("{0}", line);
        }

        [Fact]
        public void TheForceFeedbackLine_NamesATouchSenseMouse()
        {
            var s = Strings.Instance;
            var unit = new DirectUnit(OutputFamily.TouchSenseUnit, "winusb", Box, PeripheralLinker.LogitechVid, 0xC030) { Name = "iFeel Mouse" };
            var mouse = new UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Mouse, VendorId = PeripheralLinker.LogitechVid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            Link(new DeviceLinks { Device = A, Haptics = new[] { unit.Path } });
            PeripheralOutputs.Direct = Found(unit);
            Assert.Equal(string.Format(s.Pad_ForceFeedback_RouteTouchSense, "iFeel Mouse"), PeripheralRouteText.Haptics(mouse));
            Assert.Null(PeripheralRouteText.Haptics(mouse, slotLive: false));
            PeripheralOutputs.Direct = DirectSnapshot.Empty;
            Assert.Equal(string.Format(s.Pad_ForceFeedback_RouteTouchSense, mouse.ResolvedName), PeripheralRouteText.Haptics(mouse));
            PeripheralOutputs.SetRunning(false);
            Assert.Null(PeripheralRouteText.Haptics(mouse));
        }

        /// <summary>A row with rumble on record and no path now is a device
        /// that has not answered yet. Outside Logitech that can only be
        /// while a direct backend that rumbles a device runs, and the Razer
        /// Sensa row keeps its own lines.</summary>
        [Fact]
        public void ARumbleRowWithNoPathNow_ReadsAsAsleep_OnlyWhileSomethingCouldFindIt()
        {
            var s = Strings.Instance;
            static UserDevice Rumbler(Guid id, ushort vid) => new()
            {
                InstanceGuid = id, IsOnline = true, CapType = InputDeviceType.Mouse, VendorId = vid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Haptics,
            };
            var other = Rumbler(A, 0x06A3);
            var logitech = Rumbler(B, PeripheralLinker.LogitechVid);
            var sensa = Rumbler(PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa), 0x5046);

            // No direct backend: as it was.
            Assert.Null(PeripheralRouteText.Haptics(other));
            Assert.Equal(s.Pad_ForceFeedback_RouteHidppAsleep, PeripheralRouteText.Haptics(logitech));
            string sensaLine = PeripheralRouteText.Haptics(sensa);

            // A backend that lights devices finds no rumble.
            PeripheralOutputs.Direct = new DirectSnapshot(Array.Empty<DirectUnit>(), new[] { OutputFamily.LampArrayUnit });
            Assert.Null(PeripheralRouteText.Haptics(other));

            PeripheralOutputs.Direct = new DirectSnapshot(Array.Empty<DirectUnit>(),
                new[] { OutputFamily.LampArrayUnit, OutputFamily.TouchSenseUnit });
            Assert.Equal(s.Pad_ForceFeedback_RouteHidppAsleep, PeripheralRouteText.Haptics(other));
            Assert.Equal(s.Pad_ForceFeedback_RouteHidppAsleep, PeripheralRouteText.Haptics(logitech));
            // The Sensa row stands for software, which never sleeps.
            Assert.Equal(sensaLine, PeripheralRouteText.Haptics(sensa));
            Assert.NotEqual(s.Pad_ForceFeedback_RouteHidppAsleep, sensaLine);
            // Nothing on record, or a controller that rumbles nothing.
            Assert.Null(PeripheralRouteText.Haptics(new UserDevice { InstanceGuid = A, VendorId = 0x06A3 }));
            Assert.Null(PeripheralRouteText.Haptics(other, slotLive: false));
        }

        // ── The strings ──

        /// <summary>The eleven strings read as written, and every locale's
        /// copy keeps the placeholders and names its own options.</summary>
        [Fact]
        public void TheDirectPathStrings_AreInEveryLocale_WithTheirPlaceholdersAndTheirOwnOptionNames()
        {
            string En(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture);
            Assert.Equal("Windows Dynamic Lighting lights this device. Turn Dynamic Lighting off for it in Windows Settings and PadForge lights it.",
                En("Pad_Lighting_RouteWindowsLighting"));
            Assert.Equal("Windows Dynamic Lighting lights this keyboard. Turn Dynamic Lighting off for it in Windows Settings and PadForge lights it. On some laptops the keyboard then stays dark while PadForge is not lighting it.",
                En("Pad_Lighting_RouteWindowsLightingBuiltIn"));
            Assert.Equal("Colors go straight to {0}. It does not dim its lights by itself while it is lit.", En("Pad_Lighting_RouteDirectPower"));
            Assert.Equal("Colors go straight to {0}. Its on-board memory mode is off while it is lit.", En("Pad_Lighting_RouteDirectHostMode"));
            Assert.Equal("Colors go through {0}, which lights every device of this kind at once. While {0} is not running, PadForge lights this device on its own.",
                En("Pad_Lighting_RouteThroughOrDirect"));
            Assert.Equal("Lighting Hold", En("Pad_Lighting_ClaimLevel"));
            Assert.Equal("Colors Only", En("Pad_Lighting_ClaimLevel_Colors"));
            Assert.Equal("Colors and Lighting Power", En("Pad_Lighting_ClaimLevel_Power"));
            Assert.Equal("Colors, Lighting Power, and On-Board Memory Mode Off", En("Pad_Lighting_ClaimLevel_HostMode"));
            Assert.Equal("Colors Only leaves the device\u2019s power saving and its saved profile alone. Choose Colors and Lighting Power when the device\u2019s own idle effect takes the color away. The device then stops dimming its lights by itself. Choose the last option when the device shows its own profile\u2019s lighting over the color. Its on-board memory mode then goes off while it is lit and back on when it stops, and while it is off the profile saved on the device (its buttons, DPI, and report rate) does not apply.",
                En("Pad_Lighting_ClaimLevel_Tooltip"));
            Assert.Equal("Rumble plays on {0} as a buzz, sent straight to the device.", En("Pad_ForceFeedback_RouteTouchSense"));

            // The designer names each one.
            var s = Strings.Instance;
            Assert.Equal(En("Pad_Lighting_ClaimLevel"), Strings.ResourceManager.GetString(nameof(s.Pad_Lighting_ClaimLevel), CultureInfo.InvariantCulture));
            foreach (string key in new[]
            {
                nameof(s.Pad_Lighting_RouteWindowsLighting), nameof(s.Pad_Lighting_RouteWindowsLightingBuiltIn),
                nameof(s.Pad_Lighting_RouteDirectPower), nameof(s.Pad_Lighting_RouteDirectHostMode),
                nameof(s.Pad_Lighting_RouteThroughOrDirect), nameof(s.Pad_Lighting_ClaimLevel),
                nameof(s.Pad_Lighting_ClaimLevel_Colors), nameof(s.Pad_Lighting_ClaimLevel_Power),
                nameof(s.Pad_Lighting_ClaimLevel_HostMode), nameof(s.Pad_Lighting_ClaimLevel_Tooltip),
                nameof(s.Pad_ForceFeedback_RouteTouchSense),
            })
            {
                Assert.False(string.IsNullOrEmpty(En(key)), key);
                foreach (string locale in LocaleFiles.CompleteTranslations)
                {
                    var culture = new CultureInfo(locale);
                    string value = Strings.ResourceManager.GetResourceSet(culture, true, false)?.GetString(key);
                    Assert.False(string.IsNullOrEmpty(value), $"{locale} {key}");
                    Assert.Equal(En(key).Contains("{0}"), value.Contains("{0}"));
                    Assert.DoesNotContain("{1}", value);
                    // A translation, never the English text left in place.
                    Assert.NotEqual(En(key), value);
                }
            }

            foreach (string locale in LocaleFiles.CompleteTranslations)
            {
                var set = Strings.ResourceManager.GetResourceSet(new CultureInfo(locale), true, false);
                string tooltip = set.GetString("Pad_Lighting_ClaimLevel_Tooltip");
                Assert.Contains(set.GetString("Pad_Lighting_ClaimLevel_Colors"), tooltip);
                Assert.Contains(set.GetString("Pad_Lighting_ClaimLevel_Power"), tooltip);
                // The second option's name starts the third, so the list
                // reads as one that grows.
                string power = set.GetString("Pad_Lighting_ClaimLevel_Power");
                string colors = set.GetString("Pad_Lighting_ClaimLevel_Colors");
                Assert.NotEqual(colors, power);
                // The laptop line is the plain line and one sentence more.
                Assert.True(set.GetString("Pad_Lighting_RouteWindowsLightingBuiltIn").Length
                            > set.GetString("Pad_Lighting_RouteWindowsLighting").Length, locale);
                // The same route sentence opens all three direct lines.
                string direct = set.GetString("Pad_Lighting_RouteDirect");
                Assert.StartsWith(direct, set.GetString("Pad_Lighting_RouteDirectPower"));
                Assert.StartsWith(direct, set.GetString("Pad_Lighting_RouteDirectHostMode"));
            }
        }

        /// <summary>The lines for a device left to a program and for the
        /// OpenRGB route (#557) read as written, and every complete locale
        /// carries each with the same placeholders.</summary>
        [Fact]
        public void TheProgramAndOpenRgbLines_ReadAsWritten_InEveryLocale()
        {
            string En(string key) => Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture);
            var s = Strings.Instance;
            var expected = new (string Key, string English)[]
            {
                (nameof(s.Pad_Lighting_RouteLeftToProgram),
                    "{0} lights this device while it runs. While {0} is not running, PadForge lights it on its own."),
                (nameof(s.Pad_Lighting_RouteThroughOpenRgb),
                    "Colors go through OpenRGB's SDK Server. While OpenRGB is not running, PadForge lights this device on its own."),
                (nameof(s.Pad_Lighting_RouteThroughOpenRgbOnly), "Colors go through OpenRGB's SDK Server."),
                (nameof(s.Pad_Lighting_RouteLeftToOpenRgb),
                    "OpenRGB holds this device and has no Direct mode for it, so its lighting stays with OpenRGB."),
                (nameof(s.Pad_Lighting_VendorRowExamples_Aura), "motherboards, graphics cards, and memory"),
                (nameof(s.Pad_Lighting_VendorRowExamples_MysticLight), "motherboards and graphics cards"),
            };
            foreach (var (key, english) in expected)
            {
                Assert.Equal(english, En(key));
                foreach (string locale in LocaleFiles.CompleteTranslations)
                {
                    string value = Strings.ResourceManager.GetResourceSet(new CultureInfo(locale), true, false)?.GetString(key);
                    Assert.False(string.IsNullOrEmpty(value), $"{locale} {key}");
                    Assert.Equal(english.Split("{0}").Length, value.Split("{0}").Length);
                    Assert.DoesNotContain("{1}", value);
                }
            }
            // The OpenRGB lines name the server by its tab in OpenRGB's
            // window (qt/OpenRGBDialog/OpenRGBDialog.cpp:967), and the
            // with-a-promise line is the plain one and a sentence more.
            Assert.StartsWith(s.Pad_Lighting_RouteThroughOpenRgbOnly, s.Pad_Lighting_RouteThroughOpenRgb);
            Assert.Equal(
                "OpenRGB is running and its SDK Server does not answer on port {0}, so PadForge leaves this device to OpenRGB. Turn on the SDK Server in OpenRGB and PadForge lights the device through it.",
                En(nameof(s.Pad_Lighting_RouteOpenRgbServerNoAnswer)));
            Assert.Contains("Direct", En(nameof(s.Pad_Lighting_RouteLeftToOpenRgb)));
            Assert.Equal("Direct", OpenRgbDevices.DirectModeName);
        }

        // ── The dispatcher ──

        /// <summary>A unit row has no Control This Device's Lighting switch,
        /// so it lights from its own config as a vendor row does, and its
        /// animated mode keeps the timer running.</summary>
        [Fact]
        public void AUnitRow_IsLitWithNoSwitch_LikeAVendorRow()
        {
            var oldDevices = SettingsManager.UserDevices;
            var oldSettings = SettingsManager.UserSettings;
            var oldCreated = SettingsManager.SlotCreated;
            var oldEnabled = SettingsManager.SlotEnabled;
            var oldOrder = SettingsManager.XboxSlotOrder;
            var oldConfigs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
            UserEffectsDispatcher dispatcher = null;
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, 0x1234, 1) { Name = "Headset" };
            var row = new PeripheralUnitRow(headset);
            try
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.SlotCreated = new bool[16];
                SettingsManager.SlotEnabled = new bool[16];
                SettingsManager.SlotCreated[15] = SettingsManager.SlotEnabled[15] = true;
                SettingsManager.XboxSlotOrder = new List<int> { 15 };

                var ud = new UserDevice();
                ud.LoadFromExternalDevice(row);
                ud.IsOnline = true;
                ud.PeripheralOutputs = (int)PeripheralOutputKinds.Lighting;
                SettingsManager.UserDevices.Items.Add(ud);
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = row.InstanceGuid, MapTo = 15 });
                Link(new DeviceLinks { Device = row.InstanceGuid, UnitRow = true, Lighting = new[] { headset.Path } });

                var cfg = new DeviceSlotConfig
                {
                    LightbarMode = LightbarMode.Static, LightbarRed = 16, LightbarGreen = 20, LightbarBlue = 24,
                };
                Assert.False(cfg.PeripheralLightingEnabled);
                var configs = new Dictionary<Guid, DeviceSlotConfig> { [row.InstanceGuid] = cfg };
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => configs;
                dispatcher = new UserEffectsDispatcher(15, cfg, startTimer: false);

                dispatcher.ApplyOnce();
                Assert.True(PeripheralOutputs.TryResolveColor(headset.Path, out int rgb, out var ruler));
                Assert.Equal(0x101418, rgb);
                Assert.Equal(15, ruler.Slot);
                // It ranks as a device, and its claim asks for colors alone.
                Assert.False(ruler.CatchAll);
                Assert.Equal(0, ruler.Level);

                // Its mode drives the slot's timer with the switch off.
                Assert.True(UserEffectsDispatcher.LightingDriven(row.InstanceGuid, new DeviceSlotConfig()));
                // With no path now, it drives nothing: the last pass saw a
                // row with no input of its own.
                Link();
                var seen = typeof(UserEffectsDispatcher).GetField("_peripheralKinds", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(seen);
                var kinds = (IReadOnlyDictionary<Guid, bool>)seen.GetValue(dispatcher);
                Assert.False(kinds[row.InstanceGuid]);
                Assert.False(UserEffectsDispatcher.LightingDriven(row.InstanceGuid, new DeviceSlotConfig { PeripheralLightingEnabled = true }, kinds));
            }
            finally
            {
                dispatcher?.Dispose();
                row.Dispose();
                UserEffectsDispatcher.SlotPerDeviceConfigsProvider = oldConfigs;
                SettingsManager.UserDevices = oldDevices;
                SettingsManager.UserSettings = oldSettings;
                SettingsManager.SlotCreated = oldCreated;
                SettingsManager.SlotEnabled = oldEnabled;
                SettingsManager.XboxSlotOrder = oldOrder;
            }
        }

        /// <summary>The question "is this a row with no input of its own"
        /// has one home, and every site that asked it of a vendor row asks
        /// that home, so a unit row answers the same.</summary>
        [Fact]
        public void EverySiteThatAskedForAVendorRow_AsksForARowWithNoInputOfItsOwn()
        {
            string dispatcher = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "UserEffectsDispatcher.cs");
            Assert.Contains("bool outputOnly = ud.Device is Common.Input.Peripherals.IOutputOnlyRow;", dispatcher);
            Assert.Contains("ud.IsOnline && ud.Device is not Common.Input.Peripherals.IOutputOnlyRow;", dispatcher);
            Assert.Equal(2, dispatcher.Split("ud.Device is not Common.Input.Peripherals.IOutputOnlyRow;").Length - 1);
            Assert.Contains("return links.OutputOnly || cfg?.PeripheralLightingEnabled == true;", dispatcher);
            Assert.DoesNotContain("PeripheralOutputRow", dispatcher);

            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("if (dev is PadForge.Common.Input.Peripherals.IOutputOnlyRow) return false;", service);
            // A device that came or went also asks for the walk the direct
            // backends scan from.
            Assert.Contains("_peripheralHost?.DevicesChanged();", service);
            Assert.DoesNotContain("_peripheralHost?.Nudge();", service);

            // The Pad page reads a row's type, which both kinds of row share.
            string page = AuditDelta20261002Tests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");
            Assert.Contains("vendorLightingRow = ud.CapType == InputDeviceType.PeripheralLighting;", page);

            // The host that runs with the engine starts every direct backend.
            string host = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            Assert.Contains("new ChromaBackend(), new LedSdkBackend(), direct: DirectBackends.Create()) { }", host);
        }

        /// <summary>Windows reports a HID interface arriving to the main
        /// window, which already hides devices off that notice. The same
        /// notice asks the peripheral host for a walk, so a lit device with
        /// no PadForge row, a light strip or a headset, is found when it is
        /// plugged in and not at the next slow walk.</summary>
        [Fact]
        public void AHidInterfaceThatArrives_AsksTheHostForAWalk()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            int handler = window.IndexOf("private IntPtr OnDeviceChangeMessage(", StringComparison.Ordinal);
            Assert.True(handler > 0, "the handler is there");
            int arrival = window.IndexOf("if (msg == WM_DEVICECHANGE && (int)wParam == DBT_DEVICEARRIVAL)", handler, StringComparison.Ordinal);
            int ask = window.IndexOf("try { _inputService?.RequestPeripheralRescan(); }", handler, StringComparison.Ordinal);
            int end = window.IndexOf("return IntPtr.Zero;", handler, StringComparison.Ordinal);
            Assert.True(arrival > handler && ask > arrival && end > ask, $"{handler} {arrival} {ask} {end}");

            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("public void RequestPeripheralRescan() => _peripheralHost?.RequestRescan();", service);
        }
    }

    // ─────────────────────────────────────────────
    //  The host and the rows: shared state
    // ─────────────────────────────────────────────

    /// <summary>A direct backend that leaves the silence wait to the
    /// interface, as a lighting backend does.</summary>
    internal sealed class LightingOnlyBackend : IDirectUnitBackend
    {
        public OutputFamily Family => OutputFamily.SteelSeriesUnit;
        public DirectBackendSnapshot Snapshot { get; } = new(Array.Empty<DirectUnit>());
        public event Action SnapshotChanged { add { } remove { } }
        public event Action RescanRequested { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Rescan(IReadOnlyList<HidCollectionInfo> present) { }
        public void SignalStop() { }
        public void ReleaseClaimedNow(long deadline) { }
        public void Dispose() { }
    }

    /// <summary>The host's side of the direct paths against scripted
    /// backends and a clock the test sets (#494): the shared walk, the pass
    /// that links from the backends' units, the read of the running vendor
    /// programs, the crash path, and the rows of lit units no row reaches.
    /// Every interval is measured on the test's clock, and a test waits for
    /// link passes by count, never for a length of time.</summary>
    [Collection("PeripheralOutputStatics")]
    public class DirectPathHostTests : IDisposable
    {
        private static readonly Guid Box = new("11111111-2222-3333-4444-555555555555");
        private static readonly Guid OtherBox = new("99999999-2222-3333-4444-555555555555");
        private const string BoxPath = @"\\?\hid#vid_1532&pid_0084&mi_00#box";
        private const string OtherPath = @"\\?\hid#vid_0b05&pid_1a2b&mi_00#other";

        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private long _tick = 1_000_000;

        public DirectPathHostTests()
        {
            PeripheralOutputs.ResetForTest();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
        }

        public void Dispose()
        {
            PeripheralOutputHost.BeforeGate = null;
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            PeripheralOutputs.ResetForTest();
        }

        private long Tick() => Interlocked.Read(ref _tick);
        private void Advance(long ms) => Interlocked.Add(ref _tick, ms);

        private static Guid ContainerOf(string path)
            => path == BoxPath ? Box : path == OtherPath ? OtherBox : Guid.Empty;

        /// <summary>A host on backends that find nothing and reach no
        /// vendor, with the given direct backends and this test's clock.</summary>
        private PeripheralOutputHost Host(IReadOnlyList<IDirectUnitBackend> direct,
            Func<PeripheralPresence> readPresence = null,
            Func<PeripheralPresence, PeripheralPresence> readRunning = null,
            Func<IReadOnlyList<HidCollectionInfo>> enumerate = null,
            Func<string, Guid> containerOf = null,
            int stopJoinMs = PeripheralOutputHost.StopJoinMs)
            => new(new HidppBackend(() => Array.Empty<VendorHidCollection>(), (c, s) => null, _ => Guid.Empty, 1, 5),
                new GameSenseBackend(() => new GameSenseClient(HidppHapticsTests.MissingCoreProps(), 100), _ => 0, 20),
                () => new SensaHapticsService(30000, 16, 1000, () => 0f) { PlatformCanLoadEngine = () => false },
                containerOf: containerOf ?? ContainerOf,
                readPresence: readPresence ?? (() => PeripheralPresence.None),
                stopJoinMs: stopJoinMs,
                direct: direct,
                enumerate: enumerate ?? (() => Array.Empty<HidCollectionInfo>()),
                readRunning: readRunning,
                ticks: Tick);

        /// <summary>Waits for link passes to end. The first may have begun
        /// before the caller's last change, so two are asked for by default:
        /// the second began after the first ended, and so after the change.</summary>
        private static void Passes(PeripheralOutputHost host, int count = 2)
        {
            for (int i = 0; i < count; i++)
            {
                int before = host.PassCount;
                host.Nudge();
                Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "a link pass ran");
            }
        }

        private static UserDevice AddRow(ushort vid, ushort pid, int capType, string path, int record = 0)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), IsOnline = true, CapType = capType,
                VendorId = vid, ProdId = pid, DevicePath = path, PeripheralOutputs = record,
            };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            return ud;
        }

        private static void Assign(UserDevice ud, int slot)
        {
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = slot });
        }

        // ── Start, stop and the walk ──

        [Fact]
        public void TheHostStartsStopsAndDisposesEveryBackend_AndFollowsEachOnesChanges()
        {
            var journal = new ConcurrentQueue<string>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit, "lamp", journal);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit, "razer", journal);
            var host = Host(new IDirectUnitBackend[] { lamp, razer });
            var subscribers = typeof(FakeDirectBackend).GetField("SnapshotChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            var askers = typeof(FakeDirectBackend).GetField("RescanRequested", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(subscribers);
            Assert.NotNull(askers);
            Delegate Follower(FakeDirectBackend backend) => (Delegate)subscribers.GetValue(backend);
            Delegate Walker(FakeDirectBackend backend) => (Delegate)askers.GetValue(backend);

            Assert.Null(Follower(lamp));
            Assert.Null(Walker(lamp));
            host.Start();
            Assert.Equal(new[] { "start lamp", "start razer" }, journal.Take(2));
            // The host's nudge follows each backend, so a unit found brings
            // the next pass forward, and each backend's ask for a walk
            // reaches the host's own.
            foreach (var backend in new[] { lamp, razer })
            {
                var follower = Assert.Single(Follower(backend).GetInvocationList());
                Assert.Same(host, follower.Target);
                Assert.Equal(nameof(PeripheralOutputHost.Nudge), follower.Method.Name);
                var walker = Assert.Single(Walker(backend).GetInvocationList());
                Assert.Same(host, walker.Target);
                Assert.Equal(nameof(PeripheralOutputHost.RequestRescan), walker.Method.Name);
            }
            Passes(host);
            Assert.Equal(new[] { OutputFamily.LampArrayUnit, OutputFamily.RazerUnit }, PeripheralOutputs.Direct.Families);
            Assert.False(PeripheralOutputs.Direct.Scanned);

            host.Stop();
            // Last started, first stopped, and nothing published outlives it.
            Assert.Equal(new[] { "stop razer", "stop lamp" }, journal.Where(e => e.StartsWith("stop ")));
            Assert.Null(Follower(lamp));
            Assert.Null(Follower(razer));
            Assert.Null(Walker(lamp));
            Assert.Null(Walker(razer));
            Assert.Same(DirectSnapshot.Empty, PeripheralOutputs.Direct);
            Assert.Empty(PeripheralOutputs.UnitRows);

            host.Dispose();
            Assert.Equal(new[] { "dispose razer", "dispose lamp" }, journal.Where(e => e.StartsWith("dispose ")));
            // The dispose stopped them once more, as it stops every backend.
            Assert.Equal(4, journal.Count(e => e.StartsWith("stop ")));
        }

        /// <summary>A backend whose program runs and has not said which
        /// devices it holds, OpenRGB with its server off (#557): the pass
        /// leaves every device it would light on its own to OpenRGB, and
        /// names its paths taken. Once the backend says otherwise, the
        /// device is lit again.</summary>
        [Fact]
        public void AnUnansweredOpenRgb_TakesEveryDevicesOwnPaths()
        {
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit, "razer");
            var openRgb = new FakeDirectBackend(OutputFamily.OpenRgbDevice, "openrgb");
            using var host = Host(new IDirectUnitBackend[] { razer, openRgb });
            var row = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            var unit = new DirectUnit(OutputFamily.RazerUnit, BoxPath, Box, PeripheralLinker.RazerVid, 0x0084);
            host.Start();
            razer.Found(unit);
            openRgb.Unanswered();
            Passes(host);
            var links = PeripheralOutputs.Links.For(row.InstanceGuid);
            Assert.Empty(links.Lighting);
            Assert.True(links.WaitsForOpenRgbServer);
            Assert.Equal(new[] { unit.Path }, links.TakenByOpenRgb);
            Assert.True(PeripheralOutputs.TakenByOpenRgb(unit.Path));

            openRgb.Found();
            Passes(host);
            links = PeripheralOutputs.Links.For(row.InstanceGuid);
            Assert.Equal(new[] { unit.Path }, links.Lighting);
            Assert.False(PeripheralOutputs.TakenByOpenRgb(unit.Path));
            host.Stop();
        }

        /// <summary>One family that cannot start keeps neither the others
        /// nor the link pass from running. It never scans, so a device row
        /// keeps what it had on record.</summary>
        [Fact]
        public void ABackendThatCannotStart_KeepsNeitherTheOthersNorThePassFromRunning()
        {
            var journal = new ConcurrentQueue<string>();
            var broken = new FakeDirectBackend(OutputFamily.RazerUnit, "razer", journal) { FailStart = true };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit, "lamp", journal);
            var lampUnit = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", OtherBox, 0x0B05, 0x1A2B) { Kind = 1 };
            var keyboard = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath, record: (int)PeripheralOutputKinds.Lighting);
            using var host = Host(new IDirectUnitBackend[] { broken, lamp });
            host.Start();
            Assert.Equal(new[] { "start razer", "start lamp" }, journal.Take(2));

            lamp.Found(lampUnit);
            Passes(host);
            Assert.Equal(new[] { lampUnit.Path }, PeripheralOutputs.Links.For(keyboard.InstanceGuid).Lighting);
            Assert.Equal(1, lamp.Rescans);
            Assert.Equal(1, broken.Rescans);
            Assert.False(PeripheralOutputs.Direct.Scanned);
            Assert.True(mouse.HasPeripheralLighting);
        }

        /// <summary>What a scripted backend throws while it is made or asked
        /// for its family, named for these tests so its log lines are no
        /// other test's.</summary>
        private sealed class ScriptedConstructorFault : Exception { }

        /// <summary>A backend that cannot say which family it serves, and
        /// counts every other call made to it.</summary>
        private sealed class NamelessBackend : IDirectUnitBackend
        {
            public int Calls;
            public OutputFamily Family => throw new ScriptedConstructorFault();
            public DirectBackendSnapshot Snapshot { get { Interlocked.Increment(ref Calls); return DirectBackendSnapshot.NotScanned; } }
            public event Action SnapshotChanged { add { Interlocked.Increment(ref Calls); } remove { } }
            public event Action RescanRequested { add { Interlocked.Increment(ref Calls); } remove { } }
            public void Start() => Interlocked.Increment(ref Calls);
            public void Stop() => Interlocked.Increment(ref Calls);
            public void Rescan(IReadOnlyList<HidCollectionInfo> present) => Interlocked.Increment(ref Calls);
            public void SignalStop() => Interlocked.Increment(ref Calls);
            public void ReleaseClaimedNow(long deadline) => Interlocked.Increment(ref Calls);
            public void Dispose() => Interlocked.Increment(ref Calls);
        }

        /// <summary>The engine's backends are made one by one, each inside
        /// a guard. One whose constructor throws is left out and named
        /// once, and the others are made all the same, so one family that
        /// cannot be made never costs the host or the families beside it.</summary>
        [Fact]
        public void ABackendThatCannotBeMade_IsLeftOut_AndTheOthersAreMade()
        {
            var backends = new List<IDirectUnitBackend>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            string fault = "PERIPHERAL SteelSeriesUnit backend not made: " + nameof(ScriptedConstructorFault);
            int before = LogLines(fault);

            DirectBackends.Add(backends, OutputFamily.LampArrayUnit, () => lamp);
            DirectBackends.Add(backends, OutputFamily.SteelSeriesUnit, () => throw new ScriptedConstructorFault());
            DirectBackends.Add(backends, OutputFamily.TouchSenseUnit, () => null);
            DirectBackends.Add(backends, OutputFamily.RazerUnit, () => razer);

            Assert.Equal(new IDirectUnitBackend[] { lamp, razer }, backends);
            Assert.Equal(before + 1, LogLines(fault));

            // Every line of the list the engine's host starts from goes
            // through that guard.
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "DirectBackends.cs");
            Assert.DoesNotContain("backends.Add(new", source);
            Assert.Contains("try { made = make(); }", source);
            Assert.NotNull(DirectBackends.Create());
        }

        /// <summary>The host reads each backend's family once, when it is
        /// made, inside a guard. A backend that cannot name its family is
        /// left out of everything, and the host and the other backends run.</summary>
        [Fact]
        public void ABackendThatCannotNameItsFamily_IsLeftOut_AndTheHostRuns()
        {
            var nameless = new NamelessBackend();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            string fault = "PERIPHERAL " + nameof(NamelessBackend) + " left out";
            int before = LogLines(fault);
            using (var host = Host(new IDirectUnitBackend[] { nameless, null, lamp }))
            {
                Assert.Equal(before + 1, LogLines(fault));
                host.Start();
                lamp.Found();
                Passes(host);
                Assert.Equal(new[] { OutputFamily.LampArrayUnit }, PeripheralOutputs.Direct.Families);
                Assert.True(PeripheralOutputs.Direct.Scanned);
                Assert.Equal(1, lamp.Rescans);
                host.ReleaseLightingNow();
                host.WaitForSilence(0);
            }
            Assert.Equal(0, Volatile.Read(ref nameless.Calls));
        }

        /// <summary>With no direct backend, an ask for a walk is dropped
        /// where it is made: no flag kept and no pass brought forward, so a
        /// HID interface that arrives changes nothing for a host with
        /// nothing to scan. With a backend, the first ask brings the next
        /// pass forward and the asks after it, while the walk is still
        /// owed, bring nothing forward. Neither host is started, so no
        /// pass takes the wake before the test reads it.</summary>
        [Fact]
        public void AnAskForAWalk_IsDroppedWithNoBackend_AndBringsOnePassForwardWithOne()
        {
            var asked = typeof(PeripheralOutputHost).GetField("_rescanAsked", BindingFlags.NonPublic | BindingFlags.Instance);
            var wake = typeof(PeripheralOutputHost).GetField("_wake", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(asked);
            Assert.NotNull(wake);
            bool Woken(PeripheralOutputHost host) => ((AutoResetEvent)wake.GetValue(host)).WaitOne(0);

            using var bare = Host(Array.Empty<IDirectUnitBackend>());
            bare.RequestRescan();
            Assert.Equal(0, (int)asked.GetValue(bare));
            Assert.False(Woken(bare));
            // A device row that came or went still brings its pass forward.
            bare.DevicesChanged();
            Assert.True(Woken(bare));

            using var one = Host(new IDirectUnitBackend[] { new FakeDirectBackend(OutputFamily.LampArrayUnit) });
            one.RequestRescan();
            Assert.Equal(1, (int)asked.GetValue(one));
            Assert.True(Woken(one));
            one.RequestRescan();
            one.RequestRescan();
            Assert.Equal(1, (int)asked.GetValue(one));
            Assert.False(Woken(one));
            // The owner's own notice always brings a pass forward: its rows
            // changed, whatever is owed.
            one.DevicesChanged();
            Assert.True(Woken(one));
        }

        /// <summary>What a scripted backend throws from its snapshot read,
        /// named for these tests so its log lines are no other test's.</summary>
        private sealed class ScriptedSnapshotFault : Exception { }

        private static int LogLines(string text)
            => PadForge.Engine.SdlDiagLog.Snapshot().Split('\n').Count(line => line.Contains(text));

        /// <summary>A backend's snapshot is read inside a guard. A read
        /// that throws, or gives nothing, counts as not scanned with no
        /// units: the pass goes on for every other family, every device row
        /// keeps what it had on record, and the fault is named once.</summary>
        [Fact]
        public void ASnapshotReadThatThrowsOrGivesNothing_CountsAsNotScannedWithNoUnits_AndIsNamedOnce()
        {
            var lampUnit = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", OtherBox, 0x0B05, 0x1A2B) { Kind = 1 };
            var razerUnit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084);
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var keyboard = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            using var host = Host(new IDirectUnitBackend[] { razer, lamp });
            host.Start();
            lamp.Found(lampUnit);
            razer.Found(razerUnit);
            Passes(host);
            Assert.True(PeripheralOutputs.Direct.Scanned);
            Assert.Equal(new[] { razerUnit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.True(mouse.HasPeripheralLighting);

            // The first backend's read starts to throw.
            string fault = "PERIPHERAL RazerUnit snapshot fault: " + nameof(ScriptedSnapshotFault);
            int named = LogLines(fault);
            int everywhere = LogLines(nameof(ScriptedSnapshotFault));
            razer.SnapshotFault = () => new ScriptedSnapshotFault();
            Passes(host, 4);
            // The fault outlasts the hold a row gets after its unit left.
            Advance(PeripheralLinker.UnitGoneHoldMs);
            Passes(host);
            // The pass went on past it: the keyboard still has its unit.
            Assert.Equal(new[] { lampUnit.Path }, PeripheralOutputs.Links.For(keyboard.InstanceGuid).Lighting);
            // Not scanned and no units: the mouse has no path and keeps its
            // record for as long as the read fails, where a scanned empty
            // list would have cleared it once that hold was over.
            Assert.False(PeripheralOutputs.Direct.Scanned);
            Assert.Equal(new[] { lampUnit }, PeripheralOutputs.Direct.Units);
            Assert.Null(PeripheralOutputs.Links.For(mouse.InstanceGuid));
            Assert.True(mouse.HasPeripheralLighting);
            // Named once, however many passes met it, and no pass failed.
            Assert.Equal(named + 1, LogLines(fault));
            Assert.Equal(everywhere + 1, LogLines(nameof(ScriptedSnapshotFault)));

            // It reads again: what it found is linked again.
            razer.SnapshotFault = null;
            Passes(host);
            Assert.True(PeripheralOutputs.Direct.Scanned);
            Assert.Equal(new[] { razerUnit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);

            // A backend that gives no snapshot at all reads the same way.
            const string silent = "PERIPHERAL LampArrayUnit gave no snapshot";
            int before = LogLines(silent);
            lamp.NoSnapshot = true;
            Passes(host, 4);
            Advance(PeripheralLinker.UnitGoneHoldMs);
            Passes(host);
            Assert.False(PeripheralOutputs.Direct.Scanned);
            Assert.Null(PeripheralOutputs.Links.For(keyboard.InstanceGuid));
            Assert.True(keyboard.HasPeripheralLighting);
            Assert.Equal(new[] { razerUnit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.Equal(before + 1, LogLines(silent));
        }

        /// <summary>The units and the scan state a pass links from are one
        /// read of one object: no pass reads a backend twice.</summary>
        [Fact]
        public void EachPassReadsABackendsSnapshotOnce()
        {
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var host = Host(new IDirectUnitBackend[] { lamp });
            // The pass count moves once, at the end of a pass, so two reads
            // inside one pass would note the same count.
            var readAt = new ConcurrentQueue<int>();
            lamp.SnapshotRead = () => readAt.Enqueue(host.PassCount);
            using (host)
            {
                host.Start();
                lamp.Found(new DirectUnit(OutputFamily.LampArrayUnit, "lamp", OtherBox, 0x0B05, 0x1A2B) { Kind = 1 });
                Passes(host, 6);
                host.Stop();
            }
            Assert.True(readAt.Count >= 6, "each pass read the snapshot");
            Assert.Equal(readAt.Count, readAt.Distinct().Count());
            Assert.Equal(readAt.Count, lamp.SnapshotReads);
        }

        /// <summary>A host with no direct backend walks nothing and reads
        /// the rows it read before one existed.</summary>
        [Fact]
        public void WithNoDirectBackend_NothingIsWalked_AndOnlyTheThreeVendorsRowsAreRead()
        {
            int walks = 0;
            var lookups = new ConcurrentQueue<string>();
            AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            using var host = Host(Array.Empty<IDirectUnitBackend>(),
                enumerate: () => { Interlocked.Increment(ref walks); return Array.Empty<HidCollectionInfo>(); },
                containerOf: path => { lookups.Enqueue(path); return ContainerOf(path); });
            host.Start();
            Passes(host, 3);
            host.DevicesChanged();
            Advance(PeripheralOutputHost.RescanMs * 3);
            Passes(host, 3);

            Assert.Equal(0, Volatile.Read(ref walks));
            Assert.Equal(new[] { BoxPath }, lookups);
            Assert.Empty(PeripheralOutputs.Direct.Families);
            Assert.True(PeripheralOutputs.Direct.Scanned);
        }

        [Fact]
        public void TheCollectionsAreWalked_AtStart_WhenDevicesChange_AndEveryThirtySeconds_ForEveryBackendAtOnce()
        {
            int walks = 0;
            var present = new[] { new HidCollectionInfo { Path = "lamp", UsagePage = 0x59, Usage = 1, FeatureReportLength = 51 } };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp, razer },
                enumerate: () => { Interlocked.Increment(ref walks); return present; });
            host.Start();
            Passes(host);
            Assert.Equal(1, Volatile.Read(ref walks));
            Assert.Equal((1, 1), (lamp.Rescans, razer.Rescans));
            // One walk, one list, for both.
            Assert.Same(present, lamp.Present);
            Assert.Same(present, razer.Present);

            // A pass alone walks nothing, and neither does a nudge.
            Passes(host, 4);
            Assert.Equal(1, Volatile.Read(ref walks));

            // A device row came or went, past the floor between two walks.
            Advance(PeripheralOutputHost.RescanFloorMs);
            host.DevicesChanged();
            Passes(host);
            Assert.Equal(2, Volatile.Read(ref walks));
            Assert.Equal((2, 2), (lamp.Rescans, razer.Rescans));

            Advance(PeripheralOutputHost.RescanMs - 1);
            Passes(host);
            Assert.Equal(2, Volatile.Read(ref walks));
            Advance(1);
            Passes(host);
            Assert.Equal(3, Volatile.Read(ref walks));
            Passes(host, 3);
            Assert.Equal(3, Volatile.Read(ref walks));
            Assert.Equal(30000, PeripheralOutputHost.RescanMs);
        }

        /// <summary>A backend that asks for a walk gets one, for every
        /// backend, no sooner than the floor after the walk before, and one
        /// walk answers every ask made in between: a device whose
        /// collections arrive one by one is walked once more, not once per
        /// collection.</summary>
        [Fact]
        public void AWalkThatIsAskedFor_IsMadeNoSoonerThanTheFloorAfterTheLastOne_AndOnceForEveryAsk()
        {
            int walks = 0;
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp, razer },
                enumerate: () => { Interlocked.Increment(ref walks); return Array.Empty<HidCollectionInfo>(); });
            host.Start();
            Passes(host);
            Assert.Equal(1, Volatile.Read(ref walks));

            // Asked for inside the floor: it waits, and stays asked for.
            lamp.AskForRescan();
            Passes(host, 3);
            Assert.Equal(1, Volatile.Read(ref walks));
            Advance(PeripheralOutputHost.RescanFloorMs - 1);
            Passes(host);
            Assert.Equal(1, Volatile.Read(ref walks));
            Advance(1);
            Passes(host);
            Assert.Equal(2, Volatile.Read(ref walks));
            // One walk, handed to every backend, whoever asked.
            Assert.Equal((2, 2), (lamp.Rescans, razer.Rescans));
            // The ask is answered: no walk follows it.
            Advance(PeripheralOutputHost.RescanFloorMs);
            Passes(host, 3);
            Assert.Equal(2, Volatile.Read(ref walks));

            // Past the floor an ask is answered by the next pass.
            razer.AskForRescan();
            Passes(host);
            Assert.Equal(3, Volatile.Read(ref walks));
            // A burst of asks, from any of them, is one walk.
            lamp.AskForRescan();
            razer.AskForRescan();
            host.RequestRescan();
            host.DevicesChanged();
            Passes(host, 3);
            Assert.Equal(3, Volatile.Read(ref walks));
            Advance(PeripheralOutputHost.RescanFloorMs);
            Passes(host, 3);
            Assert.Equal(4, Volatile.Read(ref walks));
            Assert.Equal((4, 4), (lamp.Rescans, razer.Rescans));
            Assert.Equal(1000, PeripheralOutputHost.RescanFloorMs);
        }

        /// <summary>A pass that Stop's join gave up on, held in its read of
        /// the vendor programs, comes back to a host that has stopped and
        /// disposed its backends. It walks nothing, hands nothing over and
        /// reads no backend, although a walk was due.</summary>
        [Fact]
        public void APassThatOutlivesStop_HeldBeforeItsWalk_WalksNothingAndCallsNoBackend()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int reads = 0, walks = 0;
            var journal = new ConcurrentQueue<string>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit, "lamp", journal);
            var host = Host(new IDirectUnitBackend[] { lamp },
                readPresence: () =>
                {
                    // The first read is Start's own. The second is a pass's,
                    // and what it answers is as old as the wait it was held
                    // for.
                    if (Interlocked.Increment(ref reads) != 2) return PeripheralPresence.None;
                    held.Set();
                    release.Wait(30000);
                    return PeripheralPresence.None with { RazerSoftwareRunning = true };
                },
                enumerate: () => { Interlocked.Increment(ref walks); return Array.Empty<HidCollectionInfo>(); },
                stopJoinMs: 50);
            try
            {
                host.Start();
                Passes(host);
                Assert.Equal(1, Volatile.Read(ref walks));
                Assert.Equal(1, lamp.Rescans);

                // The next pass reads the vendor programs and is held there,
                // with the slow walk due as well.
                Advance(PeripheralOutputHost.PresenceMs + PeripheralOutputHost.RescanMs);
                host.Nudge();
                Assert.True(held.Wait(30000), "a pass reached its read of the vendor programs");
                int before = host.PassCount;
                host.Stop();
                host.Dispose();
                int snapshotReads = lamp.SnapshotReads;
                Assert.Equal("dispose lamp", journal.Last());

                release.Set();
                Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "the held pass ended");
                Assert.Equal(1, Volatile.Read(ref walks));
                Assert.Equal(1, lamp.Rescans);
                Assert.Equal(snapshotReads, lamp.SnapshotReads);
                Assert.Equal("dispose lamp", journal.Last());
                // Nor does it publish the read it was held in: a later
                // start's own read is never replaced by an older one.
                Assert.False(PeripheralOutputs.Presence.RazerSoftwareRunning);
            }
            finally
            {
                release.Set();
                host.Dispose();
            }
        }

        /// <summary>The same pass held inside its walk: the walk comes back
        /// to a stopped host, and what it found is handed to no backend.</summary>
        [Fact]
        public void APassThatOutlivesStop_HeldInItsWalk_HandsNothingOver_AndReadsNoBackend()
        {
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            int walks = 0;
            var journal = new ConcurrentQueue<string>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit, "lamp", journal);
            var host = Host(new IDirectUnitBackend[] { lamp },
                enumerate: () =>
                {
                    // The first walk goes through. The second is held.
                    if (Interlocked.Increment(ref walks) == 2)
                    {
                        held.Set();
                        release.Wait(30000);
                    }
                    return Array.Empty<HidCollectionInfo>();
                },
                stopJoinMs: 50);
            try
            {
                host.Start();
                Passes(host);
                Assert.Equal(1, lamp.Rescans);

                Advance(PeripheralOutputHost.RescanMs);
                host.Nudge();
                Assert.True(held.Wait(30000), "a pass reached its walk");
                int before = host.PassCount;
                host.Stop();
                host.Dispose();
                int snapshotReads = lamp.SnapshotReads;
                Assert.Equal("dispose lamp", journal.Last());

                release.Set();
                Assert.True(SpinWait.SpinUntil(() => host.PassCount > before, 30000), "the held pass ended");
                Assert.Equal(1, lamp.Rescans);
                Assert.Equal(snapshotReads, lamp.SnapshotReads);
                Assert.Equal("dispose lamp", journal.Last());
            }
            finally
            {
                release.Set();
                host.Dispose();
            }
        }

        /// <summary>A walk that could not be made is no empty list: nothing
        /// is handed over, so no backend drops its devices, and the walk is
        /// made again on the next pass.</summary>
        [Fact]
        public void AWalkThatFails_HandsOverNothing_AndIsMadeAgainOnTheNextPass()
        {
            int walks = 0, mode = 0;
            var present = Array.Empty<HidCollectionInfo>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp }, enumerate: () =>
            {
                Interlocked.Increment(ref walks);
                return Volatile.Read(ref mode) switch
                {
                    0 => null,
                    1 => throw new InvalidOperationException(),
                    _ => present,
                };
            });
            host.Start();
            Passes(host, 3);
            Assert.True(Volatile.Read(ref walks) >= 2, "each pass tried again");
            Assert.Equal(0, lamp.Rescans);

            Volatile.Write(ref mode, 1);
            Passes(host, 3);
            Assert.Equal(0, lamp.Rescans);

            Volatile.Write(ref mode, 2);
            Passes(host);
            Assert.Equal(1, lamp.Rescans);
            int settled = Volatile.Read(ref walks);
            Passes(host, 3);
            Assert.Equal(settled, Volatile.Read(ref walks));
        }

        // ── The pass ──

        [Fact]
        public void ThePassLinksFromEveryBackendsUnits_PublishesThemForTheTabs_AndKeepsARecordUntilEachHasScanned()
        {
            var lampUnit = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", OtherBox, 0x0B05, 0x1A2B) { Name = "ROG Falchion", Kind = 1 };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var keyboard = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            // A mouse whose record says it was lit, found by nothing yet.
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath, record: (int)PeripheralOutputKinds.Lighting);
            int capabilityChanges = 0;
            using var host = Host(new IDirectUnitBackend[] { lamp, razer });
            host.CapabilitiesChanged += () => Interlocked.Increment(ref capabilityChanges);
            host.Start();

            lamp.Found(lampUnit);
            Passes(host);
            Assert.Equal(new[] { lampUnit.Path }, PeripheralOutputs.Links.For(keyboard.InstanceGuid).Lighting);
            Assert.True(keyboard.HasPeripheralLighting);
            Assert.True(Volatile.Read(ref capabilityChanges) >= 1);
            Assert.Equal(new[] { lampUnit }, PeripheralOutputs.Direct.Units);
            // One backend has not scanned, so the mouse keeps its record.
            Assert.False(PeripheralOutputs.Direct.Scanned);
            Assert.Null(PeripheralOutputs.Links.For(mouse.InstanceGuid));
            Assert.True(mouse.HasPeripheralLighting);

            var razerUnit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084) { Name = "DeathAdder V2" };
            razer.Found(razerUnit);
            Passes(host);
            Assert.True(PeripheralOutputs.Direct.Scanned);
            Assert.Equal(new[] { lampUnit, razerUnit }, PeripheralOutputs.Direct.Units);
            Assert.Equal(new[] { razerUnit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);

            // A write to the mouse fails, or it stops answering: its backend
            // keeps it listed, asleep. No path leads to it, and its record
            // and its tab stay, with the line for a device that has not
            // answered yet.
            int changesAwake = Volatile.Read(ref capabilityChanges);
            razer.Found(razerUnit with { Asleep = true });
            Passes(host);
            Assert.Equal(new[] { lampUnit, razerUnit with { Asleep = true } }, PeripheralOutputs.Direct.Units);
            Assert.Null(PeripheralOutputs.Links.For(mouse.InstanceGuid));
            Assert.True(mouse.HasPeripheralLighting);
            Assert.Equal(changesAwake, Volatile.Read(ref capabilityChanges));
            Assert.Equal(Strings.Instance.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(mouse, 0));

            // It answers again: linked again, on the record it kept.
            razer.Found(razerUnit);
            Passes(host);
            Assert.Equal(new[] { razerUnit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.Equal(changesAwake, Volatile.Read(ref capabilityChanges));

            // Its collection leaves the walk, so the unit leaves the list for
            // good. The row is still online, so it keeps its record for the
            // hold a row gets after its last unit left, and loses it then:
            // every backend has scanned, and nothing is waited for.
            razer.Found();
            Passes(host);
            Assert.Null(PeripheralOutputs.Links.For(mouse.InstanceGuid));
            Assert.True(mouse.HasPeripheralLighting);
            Advance(PeripheralLinker.UnitGoneHoldMs);
            Passes(host);
            Assert.False(mouse.HasPeripheralLighting);
            Assert.Null(PeripheralRouteText.Lighting(mouse, 0));
        }

        [Fact]
        public void AKeyboardOfAnyVendorIsRead_WhileADirectBackendRuns_AndAForwardedOneNever()
        {
            var lookups = new ConcurrentQueue<string>();
            AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            AddRow(0x0B05, 0x1A2B, InputDeviceType.Gamepad, @"\\?\hid#vid_0b05&pid_1a2b&ig_00#pad");
            var forwarded = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, "peer://owner/0001");
            Assert.True(RemoteLinkOutputRouter.IsPeerPath(forwarded.DevicePath));
            using var host = Host(new IDirectUnitBackend[] { new FakeDirectBackend(OutputFamily.LampArrayUnit) },
                containerOf: path => { lookups.Enqueue(path); return ContainerOf(path); });
            host.Start();
            Passes(host, 3);
            // One cached lookup for the keyboard, none for the others.
            Assert.Equal(new[] { OtherPath }, lookups);
        }

        /// <summary>A TouchSense mouse bound to WinUSB is no Raw Input mouse
        /// any more, so its backend gives it a row of its own. That row
        /// carries the WinUSB interface path as its device path: the host
        /// looks the container up from it and matches the row to the unit.
        /// Under a path of PadForge's own the row names no device, has no
        /// container and gets no rumble.</summary>
        [Fact]
        public void ABoundTouchSenseMousesRow_IsMatchedToItsUnit_ByTheWinUsbInterfacePathItCarries()
        {
            const string winUsb = @"\\?\usb#vid_046d&pid_c030#6&1c2d3e4f&0&2#{a5dcbf10-6530-11d2-901f-00c04fb951ed}";
            var unit = new DirectUnit(OutputFamily.TouchSenseUnit, winUsb, Box, PeripheralLinker.LogitechVid, 0xC030) { Name = "iFeel Mouse" };
            var touchSense = new FakeDirectBackend(OutputFamily.TouchSenseUnit);
            var bound = AddRow(PeripheralLinker.LogitechVid, 0xC030, InputDeviceType.Mouse, winUsb);
            var synthetic = AddRow(PeripheralLinker.LogitechVid, 0xC030, InputDeviceType.Mouse, "touchsense://ifeel");
            var lookups = new ConcurrentQueue<string>();
            using var host = Host(new IDirectUnitBackend[] { touchSense },
                containerOf: path => { lookups.Enqueue(path); return path == winUsb ? Box : Guid.Empty; });
            host.Start();
            touchSense.Found(unit);
            Passes(host);

            Assert.Equal(winUsb, PeripheralOutputHost.ContainerPath(bound));
            Assert.Contains(winUsb, lookups);
            var links = PeripheralOutputs.Links.For(bound.InstanceGuid);
            Assert.Equal(new[] { unit.Path }, links.Haptics);
            Assert.Empty(links.Lighting);
            Assert.True(bound.HasPeripheralHaptics);
            Assert.Null(PeripheralOutputs.Links.For(synthetic.InstanceGuid));
            Assert.False(synthetic.HasPeripheralHaptics);
        }

        /// <summary>A unit row is linked to its own unit by the pass, with
        /// no container to look up, and keeps its record after its unit
        /// left for the hold any row gets, and no longer.</summary>
        [Fact]
        public void AUnitRowIsLinkedToItsUnit_ByThePass()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", OtherBox, 0x1234, 1) { Name = "Headset" };
            using var row = new PeripheralUnitRow(headset);
            var ud = new UserDevice();
            ud.LoadFromExternalDevice(row);
            ud.IsOnline = true;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            var lookups = new ConcurrentQueue<string>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp },
                containerOf: path => { lookups.Enqueue(path); return Guid.Empty; });
            host.Start();
            lamp.Found(headset);
            Passes(host);

            var links = PeripheralOutputs.Links.For(row.InstanceGuid);
            Assert.Equal(new[] { headset.Path }, links.Lighting);
            Assert.True(links.UnitRow && links.OutputOnly);
            Assert.False(links.CatchAll);
            Assert.True(ud.HasPeripheralLighting);
            Assert.Empty(lookups);

            // The unit sleeps: no path, and the row keeps its record and its
            // line for a device that has not answered yet.
            lamp.Found(headset with { Asleep = true });
            Passes(host);
            Assert.Null(PeripheralOutputs.Links.For(row.InstanceGuid));
            Assert.True(ud.HasPeripheralLighting);
            Assert.Equal(Strings.Instance.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(ud, 0));

            // The unit left the list, its collection gone from the walk. The
            // input manager retires the row at its next scan, and the row
            // keeps its record until the hold is over.
            lamp.Found();
            Passes(host);
            Assert.Null(PeripheralOutputs.Links.For(row.InstanceGuid));
            Assert.True(ud.HasPeripheralLighting);
            Advance(PeripheralLinker.UnitGoneHoldMs);
            Passes(host);
            Assert.False(ud.HasPeripheralLighting);
        }

        // ── A device that is unplugged ──

        /// <summary>The walk and the input manager's scan see an unplug in
        /// either order. Scan first: the row is offline when its unit
        /// leaves, and an offline row is not read. Walk first: the unit
        /// leaves under a row still online, and the row keeps its record
        /// for the hold, inside which the scan takes it offline. Either
        /// way the record, and so the tab, outlives the unplug.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnUnpluggedDevicesRow_KeepsItsRecord_WhicheverOfTheWalkAndTheScanSeesItFirst(bool walkFirst)
        {
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            int changes = 0;
            using var host = Host(new IDirectUnitBackend[] { razer });
            host.CapabilitiesChanged += () => Interlocked.Increment(ref changes);
            host.Start();
            razer.Found(unit);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);
            int linked = Volatile.Read(ref changes);

            if (walkFirst)
            {
                // The unit is gone and the row is still online: no path, the
                // record kept, and the line for a device that has not
                // answered yet, to the last tick of the hold.
                razer.Found();
                Passes(host);
                Assert.Null(PeripheralOutputs.Links.For(mouse.InstanceGuid));
                Assert.True(mouse.HasPeripheralLighting);
                Assert.Equal(Strings.Instance.Pad_Lighting_RouteAsleep, PeripheralRouteText.Lighting(mouse, 0));
                Advance(PeripheralLinker.UnitGoneHoldMs - 1);
                Passes(host);
                Assert.True(mouse.HasPeripheralLighting);
                // The scan takes the row offline inside the hold.
                lock (SettingsManager.UserDevices.SyncRoot) mouse.IsOnline = false;
            }
            else
            {
                lock (SettingsManager.UserDevices.SyncRoot) mouse.IsOnline = false;
                Passes(host);
                razer.Found();
            }

            // Nothing reads the row from here on, however long it is away.
            Passes(host);
            Advance(60000);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);
            Assert.Equal(linked, Volatile.Read(ref changes));
        }

        /// <summary>A row that stays online after its unit left for good
        /// loses its record when the hold is over, on the host's clock, and
        /// gets it back with its unit.</summary>
        [Fact]
        public void ARowThatStaysOnline_LosesItsRecord_OnceTheHoldAfterItsUnitLeftIsOver()
        {
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            using var host = Host(new IDirectUnitBackend[] { razer });
            host.Start();
            razer.Found(unit);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);

            // The unit sleeps for longer than the hold: it is still listed,
            // so the hold has not begun.
            razer.Found(unit with { Asleep = true });
            Passes(host);
            Advance(3 * PeripheralLinker.UnitGoneHoldMs);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);

            // It leaves the list: held from the last pass that listed it.
            razer.Found();
            Passes(host);
            Advance(PeripheralLinker.UnitGoneHoldMs - 1);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);
            Advance(1);
            Passes(host);
            Assert.False(mouse.HasPeripheralLighting);
            Assert.Null(PeripheralRouteText.Lighting(mouse, 0));

            razer.Found(unit);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);
        }

        /// <summary>The hold belongs to one engine run. A host started
        /// again holds no row for a unit its earlier run last saw: its
        /// backends scan anew, and a row they find nothing for keeps
        /// nothing.</summary>
        [Fact]
        public void AHostStartedAgain_HoldsNoRowForAUnitItsEarlierRunSaw()
        {
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            using var host = Host(new IDirectUnitBackend[] { razer });
            host.Start();
            razer.Found(unit);
            Passes(host);
            Assert.True(mouse.HasPeripheralLighting);
            host.Stop();
            Assert.True(mouse.HasPeripheralLighting);

            // The next run's scan finds nothing, on the same tick.
            razer.Found();
            host.Start();
            Passes(host);
            Assert.False(mouse.HasPeripheralLighting);
        }

        // ── The running vendor programs ──

        /// <summary>The Razer mouse of these tests: installed Synapse, a
        /// unit of the mouse's own, and a controller on slot 2 that may
        /// claim it.</summary>
        private (PeripheralOutputHost Host, FakeDirectBackend Razer, UserDevice Mouse, DirectUnit Unit) RazerBench(
            Func<PeripheralPresence> readPresence, Func<PeripheralPresence, PeripheralPresence> readRunning)
        {
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084) { Name = "DeathAdder V2" };
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            Assign(mouse, 2);
            var host = Host(new IDirectUnitBackend[] { razer }, readPresence, readRunning);
            host.SlotHasController = slot => slot == 2;
            host.Start();
            razer.Found(unit);
            Passes(host);
            return (host, razer, mouse, unit);
        }

        [Fact]
        public void WhileADeviceIsLitOnItsOwn_TheRunningProgramsAreReadOnEveryPass_AndOtherwiseEveryFiveSeconds()
        {
            int presenceReads = 0, runningReads = 0;
            var installed = new PeripheralPresence(false, false, true, false, false);
            var scripted = installed;
            var (host, _, mouse, unit) = RazerBench(
                () => { Interlocked.Increment(ref presenceReads); return Volatile.Read(ref scripted); },
                current =>
                {
                    Interlocked.Increment(ref runningReads);
                    return PeripheralPresence.CopyRunning(current, Volatile.Read(ref scripted));
                });
            using (host)
            {
                Assert.Equal(new[] { unit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);

                // Nothing lit on its own: one read at start, and no other
                // until five seconds have passed on the host's clock.
                Passes(host, 4);
                Assert.Equal(1, Volatile.Read(ref presenceReads));
                Assert.Equal(0, Volatile.Read(ref runningReads));
                Advance(PeripheralOutputHost.PresenceMs - 1);
                Passes(host);
                Assert.Equal(1, Volatile.Read(ref presenceReads));
                Advance(1);
                Passes(host);
                Assert.Equal(2, Volatile.Read(ref presenceReads));
                Passes(host, 3);
                Assert.Equal(2, Volatile.Read(ref presenceReads));
                Assert.Equal(0, Volatile.Read(ref runningReads));

                // The mouse is lit on its own: every pass reads who runs.
                PeripheralOutputs.SetLighting(mouse.InstanceGuid, slot: 2, player: 1, 0x11, 0x22, 0x33);
                Passes(host);
                int before = Volatile.Read(ref runningReads);
                Assert.True(before >= 1, "the first pass after the claim read the running programs");
                Passes(host, 4);
                Assert.True(Volatile.Read(ref runningReads) >= before + 3, "each pass read them");
                Assert.Equal(2, Volatile.Read(ref presenceReads));

                // Synapse starts: seen within a pass, and the path withdrawn.
                Volatile.Write(ref scripted, installed with { RazerSoftwareRunning = true });
                Passes(host);
                Assert.True(PeripheralOutputs.Presence.RazerSoftwareRunning);
                var links = PeripheralOutputs.Links.For(mouse.InstanceGuid);
                Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "mouse") }, links.Lighting);
                Assert.True(links.DirectWhenClosed);
                Assert.Equal(2, Volatile.Read(ref presenceReads));

                // Nothing is lit on its own any more, so the reads stop.
                int settled = Volatile.Read(ref runningReads);
                Passes(host, 4);
                Assert.Equal(settled, Volatile.Read(ref runningReads));
            }
        }

        /// <summary>A device no vendor program can take costs no extra
        /// read: a keyboard of another vendor lit through its LampArray
        /// unit, and a unit Windows lights, which PadForge leaves
        /// unpainted. The running programs are read on every pass only for
        /// a claim one of them would end.</summary>
        [Fact]
        public void ADeviceNoVendorProgramCanTake_IsLitWithNoExtraReadOfTheRunningPrograms()
        {
            int runningReads = 0;
            var lampUnit = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", OtherBox, 0x0B05, 0x1A2B) { Kind = 1 };
            var razerLamp = new DirectUnit(OutputFamily.LampArrayUnit, "razerlamp", Box, PeripheralLinker.RazerVid, 0x0084)
            {
                Kind = 2, WindowsLit = true,
            };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            var keyboard = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            Assign(keyboard, 2);
            Assign(mouse, 2);
            using var host = Host(new IDirectUnitBackend[] { lamp },
                readRunning: current => { Interlocked.Increment(ref runningReads); return current; });
            host.SlotHasController = slot => slot == 2;
            host.Start();
            lamp.Found(lampUnit, razerLamp);
            Passes(host);
            Assert.Equal(new[] { lampUnit.Path }, PeripheralOutputs.Links.For(keyboard.InstanceGuid).Lighting);
            Assert.Equal(new[] { razerLamp.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
            Assert.False(PeripheralOutputs.Links.For(keyboard.InstanceGuid).HasVendorProgram);
            Assert.True(PeripheralOutputs.Links.For(mouse.InstanceGuid).HasVendorProgram);

            // Both are lit: one of a vendor with no program to yield to,
            // and one Windows lights.
            PeripheralOutputs.SetLighting(keyboard.InstanceGuid, slot: 2, player: 1, 1, 2, 3);
            PeripheralOutputs.SetLighting(mouse.InstanceGuid, slot: 2, player: 1, 1, 2, 3);
            Passes(host, 4);
            Assert.True(PeripheralOutputs.TryResolveColor(lampUnit.Path, out _));
            Assert.True(PeripheralOutputs.TryResolveColor(razerLamp.Path, out _));
            Assert.Equal(0, Volatile.Read(ref runningReads));

            // Dynamic Lighting goes off for the Razer mouse. PadForge paints
            // its unit now, and Synapse starting would take it.
            lamp.Found(lampUnit, razerLamp with { WindowsLit = false });
            Passes(host, 3);
            Assert.True(Volatile.Read(ref runningReads) >= 1, "the Razer mouse's unit is watched for");
        }

        /// <summary>Set Chroma Color with the lighting switch off: no claim
        /// is held, and the macro paints the Razer unit for its window all
        /// the same. Synapse starting under it must be seen within a pass
        /// too, so the macro alone brings the read on every pass.</summary>
        [Fact]
        public void ALiveChromaMacroOnARazerUnitsSlot_ReadsTheRunningProgramsOnEveryPass_WithNoClaim()
        {
            int runningReads = 0;
            var installed = new PeripheralPresence(false, false, true, false, false);
            var (host, _, mouse, unit) = RazerBench(() => installed,
                current => { Interlocked.Increment(ref runningReads); return current; });
            using (host)
            {
                Assert.Equal(new[] { unit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
                Passes(host, 3);
                Assert.Equal(0, Volatile.Read(ref runningReads));

                // The macro is asserted on the mouse's slot, on the host's
                // clock, with no claim behind it.
                PeripheralOutputs.AssertChromaMacro(2, 0x10, 0x20, 0x30, Tick());
                Passes(host);
                int before = Volatile.Read(ref runningReads);
                Assert.True(before >= 1, "a pass under the macro read the running programs");
                Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
                Passes(host, 3);
                Assert.True(Volatile.Read(ref runningReads) >= before + 2, "each pass under it read them");

                // The macro lapses, and the reads stop with it.
                Advance(PeripheralOutputs.MacroAssertWindowMs + 1);
                Passes(host);
                int settled = Volatile.Read(ref runningReads);
                Passes(host, 4);
                Assert.Equal(settled, Volatile.Read(ref runningReads));
            }
        }

        /// <summary>A claim belongs to its device and slot, never to a
        /// path. When Synapse stops, the row moves to its own unit and the
        /// standing claim resolves there with nothing set again.</summary>
        [Fact]
        public void ARelinkFromASharedPathToADirectOne_KeepsTheStandingClaim_AndSetsNothingAgain()
        {
            var installed = new PeripheralPresence(false, false, true, false, false);
            var scripted = installed with { RazerSoftwareRunning = true };
            var (host, _, mouse, unit) = RazerBench(() => Volatile.Read(ref scripted), null);
            using (host)
            {
                var category = new OutputPath(OutputFamily.ChromaCategory, "mouse");
                Assert.Equal(new[] { category }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
                PeripheralOutputs.SetLighting(mouse.InstanceGuid, slot: 2, player: 1, 0x11, 0x22, 0x33, level: 0);
                Passes(host);
                Assert.True(PeripheralOutputs.TryResolveColor(category, out int shared));
                Assert.Equal(0x112233, shared);
                Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());

                int version = PeripheralOutputs.LightingVersion;
                Volatile.Write(ref scripted, installed);
                Advance(PeripheralOutputHost.PresenceMs);
                Passes(host);

                var links = PeripheralOutputs.Links.For(mouse.InstanceGuid);
                Assert.Equal(new[] { unit.Path }, links.Lighting);
                Assert.Equal(new[] { category }, links.Shadows);
                Assert.True(PeripheralOutputs.TryResolveColor(unit.Path, out int direct, out var ruler));
                Assert.Equal(0x112233, direct);
                Assert.Equal((mouse.InstanceGuid, 2), (ruler.Device, ruler.Slot));
                Assert.False(PeripheralOutputs.TryResolveColor(category, out _));
                // No claim was made, changed or dropped on the way.
                Assert.Equal(version, PeripheralOutputs.LightingVersion);
                Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            }
        }

        // ── The crash path ──

        [Fact]
        public void TheCrashPath_TellsEveryBackendToClaimNothingMore_BeforeItWaitsForAny_AndGivesAllOneDeadline()
        {
            var journal = new ConcurrentQueue<string>();
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit, "lamp", journal);
            var steelSeries = new FakeDirectBackend(OutputFamily.SteelSeriesUnit, "steelseries", journal);
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit, "razer", journal);
            using var host = Host(new IDirectUnitBackend[] { lamp, steelSeries, razer });

            host.ReleaseLightingNow();

            long deadline = Tick() + PeripheralOutputHost.PanicBudgetMs;
            Assert.Equal(new[]
            {
                "signal lamp", "signal steelseries", "signal razer",
                $"release lamp {deadline}", $"release steelseries {deadline}", $"release razer {deadline}",
            }, journal);
            Assert.Equal(750, PeripheralOutputHost.PanicBudgetMs);

            // The HID++ units go back between the two, ahead of every
            // direct backend.
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "Peripherals", "PeripheralOutputHost.cs");
            int method = source.IndexOf("public void ReleaseLightingNow()", StringComparison.Ordinal);
            int signal = source.IndexOf("backend.SignalStop();", method, StringComparison.Ordinal);
            int hidpp = source.IndexOf("_hidpp.ReleaseClaimedNow();", method, StringComparison.Ordinal);
            int release = source.IndexOf("backend.ReleaseClaimedNow(deadline);", method, StringComparison.Ordinal);
            Assert.True(method > 0 && signal > method && hidpp > signal && release > hidpp, $"{method} {signal} {hidpp} {release}");
        }

        [Fact]
        public void TheSilenceWait_ReachesEachBackend_InsideWhatIsLeftOfIt()
        {
            var journal = new ConcurrentQueue<string>();
            var first = new FakeDirectBackend(OutputFamily.TouchSenseUnit, "first", journal) { Waiting = _ => Advance(100) };
            var second = new FakeDirectBackend(OutputFamily.TouchSenseUnit, "second", journal) { Waiting = _ => Advance(400) };
            var third = new FakeDirectBackend(OutputFamily.TouchSenseUnit, "third", journal);
            using var host = Host(new IDirectUnitBackend[] { first, new LightingOnlyBackend(), second, third });

            host.WaitForSilence(250);

            // 250 ms in all: the second is left 150, and the third nothing.
            Assert.Equal(new[] { "silence first 250", "silence second 150", "silence third 0" }, journal);
        }

        // ── Lit units no row reaches ──

        [Fact]
        public void ALitUnitNoRowReaches_IsListedForARowOfItsOwn_OnlyAfterTheHold_OnTheHostsClock()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", OtherBox, 0x0B05, 0x1A2B) { Name = "Headset", Kind = 4 };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp });
            host.Start();
            lamp.Found(headset);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);

            Advance(PeripheralLinker.UnitRowHoldMs - 1);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Advance(1);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);
            var listed = PeripheralOutputs.UnitRows;
            Passes(host, 3);
            Assert.Same(listed, PeripheralOutputs.UnitRows);

            // A keyboard of the device comes online: its own row reaches the
            // unit, so the unit is listed no more.
            var keyboard = AddRow(0x0B05, 0x1A2B, InputDeviceType.Keyboard, OtherPath);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Assert.Equal(new[] { headset.Path }, PeripheralOutputs.Links.For(keyboard.InstanceGuid).Lighting);

            // The keyboard leaves: the wait starts over.
            keyboard.IsOnline = false;
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Advance(PeripheralLinker.UnitRowHoldMs - 1);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Advance(1);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);

            host.Stop();
            Assert.Empty(PeripheralOutputs.UnitRows);
        }

        /// <summary>A Razer unit no row reaches is listed for a row of its
        /// own after the hold, as a LampArray unit is (#557), and a mouse
        /// row of the device that comes online takes it back in the next
        /// pass. While Synapse runs it is listed for none.</summary>
        [Fact]
        public void ARazerUnitNoRowReaches_IsListedAfterTheHold_AndItsMouseRowTakesItBack()
        {
            var unit = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x0084)
            {
                Name = "Razer DeathAdder V2",
            };
            var razer = new FakeDirectBackend(OutputFamily.RazerUnit);
            var presence = PeripheralPresence.None;
            using var host = Host(new IDirectUnitBackend[] { razer }, readPresence: () => Volatile.Read(ref presence),
                readRunning: _ => Volatile.Read(ref presence));
            host.Start();
            razer.Found(unit);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Advance(PeripheralLinker.UnitRowHoldMs);
            Passes(host);
            Assert.Equal(new[] { unit }, PeripheralOutputs.UnitRows);
            Assert.Equal("Razer DeathAdder V2", PeripheralUnitRow.NameFor(PeripheralOutputs.UnitRows[0]));

            // Synapse starts, seen at the next read of the programs: listed
            // no more. It stops: listed again after the whole hold.
            Volatile.Write(ref presence, PeripheralPresence.None with { RazerSoftwareRunning = true });
            Advance(PeripheralOutputHost.PresenceMs);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Volatile.Write(ref presence, PeripheralPresence.None);
            Advance(PeripheralOutputHost.PresenceMs);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Advance(PeripheralLinker.UnitRowHoldMs);
            Passes(host);
            Assert.Equal(new[] { unit }, PeripheralOutputs.UnitRows);

            // The mouse comes online: its own row lights the device.
            var mouse = AddRow(PeripheralLinker.RazerVid, 0x0084, InputDeviceType.Mouse, BoxPath);
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            Assert.Equal(new[] { unit.Path }, PeripheralOutputs.Links.For(mouse.InstanceGuid).Lighting);
        }

        /// <summary>A unit with a row of its own that falls asleep stays
        /// listed, so its row stays open: a failed write never closes the
        /// row and makes the device wait out the hold again. A unit asleep
        /// with no row yet is listed for none.</summary>
        [Fact]
        public void AUnitWithARowOfItsOwn_StaysListedWhileItSleeps_SoItsRowStaysOpen()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", OtherBox, 0x0B05, 0x1A2B) { Name = "Headset", Kind = 4 };
            var lamp = new FakeDirectBackend(OutputFamily.LampArrayUnit);
            using var host = Host(new IDirectUnitBackend[] { lamp });
            host.Start();
            lamp.Found(headset);
            Passes(host);
            Advance(PeripheralLinker.UnitRowHoldMs);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);

            // Asleep before the input manager opened its row: no row for a
            // device that does not answer.
            lamp.Found(headset with { Asleep = true });
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
            lamp.Found(headset);
            Passes(host);
            Advance(PeripheralLinker.UnitRowHoldMs);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);

            // The input manager opens the row.
            using var row = new PeripheralUnitRow(headset);
            var record = new UserDevice();
            record.LoadFromExternalDevice(row);
            record.IsOnline = true;
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(record);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);

            // Asleep: listed still, in the very next pass, with no new wait.
            var asleep = headset with { Asleep = true };
            lamp.Found(asleep);
            Passes(host);
            Assert.Equal(new[] { asleep }, PeripheralOutputs.UnitRows);
            Assert.Equal(new[] { asleep }, PeripheralOutputs.Direct.Units);

            lamp.Found(headset);
            Passes(host);
            Assert.Equal(new[] { headset }, PeripheralOutputs.UnitRows);

            // Its collection left the walk: listed no more.
            lamp.Found();
            Passes(host);
            Assert.Empty(PeripheralOutputs.UnitRows);
        }

        /// <summary>A unit that learns its name after its row opened: the same
        /// row and record take the name, and the device list hears of it
        /// once.</summary>
        [Fact]
        public void ARow_TakesTheNameItsUnitLearned()
        {
            var port = new DirectUnit(OutputFamily.HidLightingUnit, @"\\.\COM5", Box, 0x1A86, 0x7523) { Name = "Serial device on COM5" };
            Guid id = PeripheralUnitRow.IdentityFor(port.Family, port.Key);
            var manager = new InputManager();
            var phase = typeof(InputManager).GetMethod("UpdatePeripheralRows", BindingFlags.NonPublic | BindingFlags.Instance);
            bool Update() => (bool)phase.Invoke(manager, null);
            try
            {
                PeripheralOutputs.UnitRows = new[] { port };
                Assert.True(Update());
                UserDevice record;
                lock (SettingsManager.UserDevices.SyncRoot)
                    record = SettingsManager.UserDevices.Items.Single(d => d.InstanceGuid == id);
                var row = Assert.IsType<PeripheralUnitRow>(record.Device);
                Assert.Equal("Serial device on COM5", record.ResolvedName);

                PeripheralOutputs.UnitRows = new[] { port with { Name = "Skydimo SK0402" } };
                Assert.True(Update());
                Assert.Same(row, record.Device);
                Assert.Equal("Skydimo SK0402", row.Name);
                Assert.Equal("Skydimo SK0402", record.ResolvedName);
                Assert.True(record.IsOnline);
                Assert.False(Update());
            }
            finally
            {
                manager.ShutdownPeripheralRows();
            }
        }

        /// <summary>The poll thread's phase: a row opens for each listed
        /// unit, retires when its unit leaves the list, and comes back in
        /// the same pass when the user removed it from the Devices page, the
        /// vendor rows' rules.</summary>
        [Fact]
        public void ARowOpensForEachListedUnit_RetiresWhenItLeaves_AndComesBackAfterTheUserRemovedIt()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", OtherBox, 0x0B05, 0x1A2B) { Name = "Headset", Kind = 4 };
            var strip = new DirectUnit(OutputFamily.LampArrayUnit, "strip", Box, 0x1234, 0x0002);
            Guid headsetId = PeripheralUnitRow.IdentityFor(headset.Family, headset.Key);
            Guid stripId = PeripheralUnitRow.IdentityFor(strip.Family, strip.Key);
            var manager = new InputManager();
            var phase = typeof(InputManager).GetMethod("UpdatePeripheralRows", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(phase);
            bool Update() => (bool)phase.Invoke(manager, null);
            UserDevice Record(Guid id)
            {
                lock (SettingsManager.UserDevices.SyncRoot)
                    return SettingsManager.UserDevices.Items.SingleOrDefault(d => d.InstanceGuid == id);
            }

            try
            {
                Assert.False(Update());
                PeripheralOutputs.UnitRows = new[] { headset, strip };
                Assert.True(Update());
                var record = Record(headsetId);
                Assert.True(record.IsOnline);
                var row = Assert.IsType<PeripheralUnitRow>(record.Device);
                Assert.Equal(headset.Path, row.Unit);
                Assert.Equal("Headset", record.ResolvedName);
                Assert.Equal(InputDeviceType.PeripheralLighting, record.CapType);
                Assert.Equal("LampArray 1234:0002", Record(stripId).ResolvedName);
                Assert.False(Update());

                // The unit sleeps and is listed still: its row stays open,
                // the same row, and nothing changes for the device list.
                PeripheralOutputs.UnitRows = new[] { headset with { Asleep = true }, strip };
                Assert.False(Update());
                Assert.True(record.IsOnline);
                Assert.Same(row, record.Device);
                PeripheralOutputs.UnitRows = new[] { headset, strip };
                Assert.False(Update());

                // The unit leaves the list: the row retires and lets its
                // lighting go.
                PeripheralOutputs.SetLighting(headsetId, slot: 0, player: 1, 1, 2, 3);
                PeripheralOutputs.UnitRows = new[] { strip };
                Assert.True(Update());
                Assert.False(record.IsOnline);
                Assert.Null(record.Device);
                Assert.False(row.IsAttached);
                Assert.False(PeripheralOutputs.IsLit(headsetId));
                Assert.True(Record(stripId).IsOnline);

                // It comes back on the same record, which is what its
                // assignment names.
                PeripheralOutputs.UnitRows = new[] { strip, headset };
                Assert.True(Update());
                Assert.Same(record, Record(headsetId));
                Assert.True(record.IsOnline);

                // Removed from the Devices page while its unit is listed:
                // back on the same pass.
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Remove(record);
                Assert.True(Update());
                var again = Record(headsetId);
                Assert.NotNull(again);
                Assert.NotSame(record, again);
                Assert.True(again.IsOnline);

                // The engine stops: every row retires, and none opens after.
                manager.ShutdownPeripheralRows();
                Assert.False(again.IsOnline);
                Assert.False(Record(stripId).IsOnline);
                Assert.False(Update());
                Assert.False(again.IsOnline);
            }
            finally
            {
                // Whatever the test left open retires here, on this test's
                // device list, and never later from the finalizer.
                manager.ShutdownPeripheralRows();
            }
        }
    }
}
