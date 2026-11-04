using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Peripherals.HidFamilies;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>MSI laptops' SteelSeries controllers against OpenRGB's
    /// MSILaptopController (#557): the model table read by DMI name
    /// (MSILaptopControllerDetect.cpp:222-288), the lights
    /// (RGBController_MSILaptop.cpp:53-134) and SetLEDs' frames
    /// (MSILaptopController.cpp:62-147).</summary>
    public class MsiLaptopLightingTests
    {
        private const string Msi = "Micro-Star International Co., Ltd.";

        private static MsiLaptopLighting Family(string product = "Raider A18 HX A9WJG", string vendor = Msi)
            => new(() => vendor, () => product);

        private static HidCollectionInfo Of(OpenRgbRegistration r) => CollectionOf(r, feature: 525, output: 65);

        [Fact]
        public void EveryRegistration_FindsItsModel_OnALaptopOfTheTable()
        {
            var registrations = OpenRgbRegistrations.Of(MsiLaptopLighting.OpenRgbFamily).ToList();
            Assert.Equal(4, registrations.Count);
            foreach (var product in new[] { "Raider A18 HX A9WJG", "Raider 18 HX AI A2XWIG", "Raider GE68HX 13VF" })
            {
                var family = Family(product);
                foreach (var r in registrations)
                {
                    var model = family.Match(Of(r));
                    Assert.NotNull(model);
                    Assert.Equal(r.Name + " (" + product + ")", model.Name);
                    Assert.Same(model, family.Match(Of(r)));
                    Assert.Equal(new[] { VendorPrograms.SteelSeriesGG }, model.Programs);
                    Assert.Equal(50, model.RepaintMs);
                    Assert.Equal(0, model.HoldMs);
                }
            }
        }

        [Fact]
        public void ALaptopOutsideTheTable_IsNotLit_AndAFailedReadIsAskedAgain()
        {
            var r = OpenRgbRegistrations.Of(MsiLaptopLighting.OpenRgbFamily).First();
            Assert.Null(Family("Raider A18 HX A9WJF").Match(Of(r)));
            Assert.Null(Family(vendor: "MSI").Match(Of(r)));
            int reads = 0;
            string answer = null;
            var family = new MsiLaptopLighting(() => { reads++; return answer; }, () => "Raider A18 HX A9WJG");
            Assert.Null(family.Match(Of(r)));
            answer = Msi;
            Assert.NotNull(family.Match(Of(r)));
            Assert.NotNull(family.Match(Of(r)));
            Assert.Equal(2, reads);
        }

        [Fact]
        public void ACollectionOfOtherShape_IsNotTheFamilys()
        {
            var family = Family();
            var regs = OpenRgbRegistrations.Of(MsiLaptopLighting.OpenRgbFamily).ToList();
            var keyboard = regs.Single(r => r.ProductId == 0x1122);
            Assert.Null(family.Match(CollectionOf(keyboard, feature: 524, output: 65)));
            var bar = regs.Single(r => r.ProductId == 0x114B);
            Assert.Null(family.Match(CollectionOf(bar, feature: 525, output: 64)));
            Assert.NotNull(family.Match(CollectionOf(bar, feature: 525, output: 65)));
            // The light bar registrations name interface 0.
            var alc = regs.Single(r => r.ProductId == 0x1161);
            Assert.Null(family.Match(Of(alc) with { InterfaceNumber = 1, Path = Of(alc).Path.Replace("&mi_00", "&mi_01") }));
            Assert.Null(family.Match(null));
        }

        [Fact]
        public void TheDeviceKind_ComesFromTheProductId()
        {
            Assert.Equal(MsiLaptopLighting.DeviceKind.Keyboard, MsiLaptopLighting.KindOf(0x1122));
            Assert.Equal(MsiLaptopLighting.DeviceKind.Keyboard, MsiLaptopLighting.KindOf(0x113A));
            Assert.Equal(MsiLaptopLighting.DeviceKind.LightBar, MsiLaptopLighting.KindOf(0x1161));
            Assert.Equal(MsiLaptopLighting.DeviceKind.KeyboardLightBar, MsiLaptopLighting.KindOf(0x114B));
            Assert.Null(MsiLaptopLighting.KindOf(0x1123));
        }

        /// <summary>The keyboard numbers its LEDs by HID usage: every key the
        /// table names by an OpenRGB key name with a usage has that usage as
        /// its ID, save Right Windows, whose ID 0xE4 is Right Control's usage
        /// (MSILaptopControllerDetect.cpp:125).</summary>
        [Fact]
        public void TheKeyboard_IsNumberedByUsage_SoItsKeysAreNamed()
        {
            var table = MsiLaptopLighting.MsiRaiderA18KlcLeds;
            Assert.Equal(102, table.Length);
            var disagree = new List<string>();
            foreach (var (name, id) in table)
                if (OpenRgbKeys.TryGet(name, out ushort usage, out _) && usage != 0 && usage != id)
                    disagree.Add(name);
            Assert.Equal(new[] { "Key: Right Windows" }, disagree);
            var laptop = MsiLaptopLighting.Models[0];
            var lights = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.Keyboard, laptop);
            Assert.Equal(table.Select(t => (int)t.Value), lights.Select(l => l.Address));
            var home = lights[table.ToList().FindIndex(t => t.Name == "Home/Page Up")];
            Assert.Equal((ushort)0x4B, home.Usage);
            Assert.Equal(LightKeySource.Vendor, home.KeySource);
            var fn = lights[table.ToList().FindIndex(t => t.Name == "Key: Right Fn")];
            Assert.Equal(LightKind.Other, fn.Kind);
            Assert.Equal(LightLabel.Fn, fn.Label);
            var a = lights[0];
            Assert.Equal((ushort)0x04, a.Usage);
            // A sits at row 3, column 1 of msi_raider_a18_klc_matrix_map
            // (MSILaptopControllerDetect.cpp:140).
            Assert.True(a.HasPosition);
            Assert.Equal(1f, a.X);
            Assert.Equal(3f, a.Y);
            // Every LED has a cell, save the ISO backslash, LED 99, which the
            // grid leaves out (MSILaptopControllerDetect.cpp:135-143).
            Assert.Equal(new[] { 99 }, Enumerable.Range(0, lights.Count).Where(i => !lights[i].HasPosition));
        }

        [Fact]
        public void TheLightBars_AreEdgeLights_WithTheirLogo()
        {
            var raider = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.LightBar, MsiLaptopLighting.Models[0]);
            Assert.Equal(new[] { 0, 1, 2, 3 }, raider.Select(l => l.Address));
            Assert.Equal(new[] { LightKind.Edge, LightKind.Edge, LightKind.Edge, LightKind.Logo }, raider.Select(l => l.Kind));
            var ge68 = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.KeyboardLightBar, MsiLaptopLighting.Models[2]);
            Assert.Equal(53, ge68.Count);
            Assert.Equal(LightKind.Logo, ge68[0].Kind);
            Assert.Equal(Enumerable.Range(0, 53), ge68.Select(l => l.Address));
            Assert.All(ge68.Skip(1), l => Assert.Equal(LightKind.Edge, l.Kind));
        }

        /// <summary>Report ID 0, 0C, 00, 66 for the keyboard (06 for the light
        /// bar), 00, then ID R G B per LED and FF in every slot left.</summary>
        [Fact]
        public void TheKeyboardReport_IsSetLedsSlots()
        {
            var lights = new[]
            {
                new LightInfo(0x04, LightKind.Key, 4, LightKeySource.Generated),
                new LightInfo(0x29, LightKind.Key, 0x29, LightKeySource.Generated),
            };
            var frame = MsiLaptopLighting.SlotFrame(MsiLaptopLighting.DeviceKind.Keyboard, lights, new[] { 0x112233, 0x445566 });
            Assert.Equal(525, frame.Length);
            Assert.Equal("00 0C 00 66 00 04 11 22 33 29 44 55 66 FF 00 00 00 FF", Hex(frame, 18));
            for (int slot = 2; slot < 130; slot++)
            {
                Assert.Equal(0xFF, frame[5 + slot * 4]);
                Assert.Equal(0, frame[6 + slot * 4] | frame[7 + slot * 4] | frame[8 + slot * 4]);
            }
            var bar = MsiLaptopLighting.SlotFrame(MsiLaptopLighting.DeviceKind.LightBar, lights.Take(1).ToList(), new[] { 0xABCDEF });
            Assert.Equal("00 0C 00 06 00 04 AB CD EF FF", Hex(bar, 10));
        }

        /// <summary>The GE68HX light bar: 53 LEDs go in reports of 30 and 23,
        /// each LED R G B, six zeros, 01 00 and its ID, then the commit
        /// output report 00 0D 00 02.</summary>
        [Fact]
        public void TheGe68hxLightBar_GoesThirtyAtATime_ThenCommits()
        {
            var lights = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.KeyboardLightBar, MsiLaptopLighting.Models[2]);
            var colors = Colors(53, n => 0x010203 * (n + 1));
            var frames = MsiLaptopLighting.BarFrames(lights, colors);
            Assert.Equal(2, frames.Count);
            Assert.Equal("00 0E 00 1E 00 01 02 03 00 00 00 00 00 00 01 00 00 02 04 06 00 00 00 00 00 00 01 00 01",
                Hex(frames[0], 29));
            Assert.Equal("00 0E 00 17 00", Hex(frames[1], 5));
            // LED 30 opens the second report, its ID 0x1E.
            Assert.Equal(0x1E, frames[1][5 + 11]);
            Assert.Equal((byte)(colors[30] >> 16), frames[1][5]);
            Assert.All(frames, f => Assert.Equal(525, f.Length));
            Assert.Equal(Padded("00 0D 00 02", 65), MsiLaptopLighting.CommitFrame());

            var io = new LoggingHidChannel();
            var session = new MsiLaptopLighting.Session(MsiLaptopLighting.DeviceKind.KeyboardLightBar);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
            Assert.Equal(new[] { "F", "F", "O" }, io.Log.Select(e => e.Kind));
            Assert.Equal(MsiLaptopLighting.CommitFrame(), io.Log[2].Bytes);
        }

        [Fact]
        public void TheSession_SendsOneReport_AndHandsBackNothing()
        {
            var laptop = MsiLaptopLighting.Models[0];
            var lights = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.Keyboard, laptop);
            var colors = Colors(lights.Count, n => n);
            var io = new LoggingHidChannel();
            var session = new MsiLaptopLighting.Session(MsiLaptopLighting.DeviceKind.Keyboard);
            Assert.True(session.Feature);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, lights, colors));
            Assert.Equal(new[] { MsiLaptopLighting.SlotFrame(MsiLaptopLighting.DeviceKind.Keyboard, lights, colors) }, io.Sent("F"));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Single(io.Log);
            Assert.Equal(HidLightingPaint.Failed, session.Paint(new LoggingHidChannel { FailWrites = true }, io.Wait, lights, colors));
            var bar = new MsiLaptopLighting.Session(MsiLaptopLighting.DeviceKind.KeyboardLightBar);
            var ge = MsiLaptopLighting.Lights(MsiLaptopLighting.DeviceKind.KeyboardLightBar, MsiLaptopLighting.Models[2]);
            Assert.Equal(HidLightingPaint.Failed, bar.Paint(new LoggingHidChannel { FailAfter = 2 }, io.Wait, ge, Colors(53, n => n)));
        }

        [Fact]
        public void TheModelTable_IsOpenRgbs()
        {
            var models = MsiLaptopLighting.Models;
            Assert.Equal(new[] { "Raider A18 HX A9WJG", "Raider 18 HX AI A2XWIG", "Raider GE68HX 13VF" }, models.Select(m => m.Product));
            Assert.All(models, m => Assert.Equal(Msi, m.Manufacturer));
            Assert.All(models, m => Assert.Equal(6 * 23, m.Grid.Length));
            Assert.Equal(new[] { 3, 3, 52 }, models.Select(m => m.BarLength));
            Assert.Equal(new[] { 4, 4, 53 }, models.Select(m => m.BarLeds.Length));
            Assert.True(VendorPrograms.SteelSeriesGG.Owns("SteelSeriesGGClient"));
            Assert.Contains(VendorPrograms.SteelSeriesGG, VendorPrograms.All);
        }
    }
}
