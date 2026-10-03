using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// GunCon 2 calibration (hifihedgehog/SDL#33 Part 9). Every PC tool for the
    /// gun calibrates, because the beam counts at the picture's edges depend on
    /// the CRT, the video mode and the game. The screen shoots four targets set
    /// in from the corners, as beardypig's and psakhis's calibrate.py do, and
    /// the window is the line through the shots read at the edges.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class GunCon2CalibrationTests
    {
        private static readonly (double U, double V)[] Targets = PadForge.Views.GunCalibrationScreen.Targets;

        /// <summary>Shots a gun with this window would read at the targets.</summary>
        private static (int X, int Y)[] ShotsFor(int minX, int maxX, int minY, int maxY, int jitter = 0) =>
            Targets.Select((t, i) => (
                (int)Math.Round(minX + t.U * (maxX - minX)) + (i % 2 == 0 ? jitter : -jitter),
                (int)Math.Round(minY + t.V * (maxY - minY)) + (i < 2 ? -jitter : jitter))).ToArray();

        [Fact]
        public void TheDefault_IsThePcToolsStartingWindow()
        {
            var d = GunCon2Calibration.Default;
            Assert.Equal((175, 720, 20, 240), (d.MinX, d.MaxX, d.MinY, d.MaxY));
            Assert.True(d.IsDefault);
            Assert.True(d.IsUsable);
        }

        [Theory]
        [InlineData(175, 720, 20, 240)]
        [InlineData(210, 690, 32, 226)]
        [InlineData(90, 610, 14, 250)]
        public void ShotsAtTheTargets_FitTheWindowTheyCameFrom(int minX, int maxX, int minY, int maxY)
        {
            Assert.True(GunCon2Calibration.TryFit(Targets, ShotsFor(minX, maxX, minY, maxY), out var c));
            Assert.InRange(c.MinX, minX - 1, minX + 1);
            Assert.InRange(c.MaxX, maxX - 1, maxX + 1);
            Assert.InRange(c.MinY, minY - 1, minY + 1);
            Assert.InRange(c.MaxY, maxY - 1, maxY + 1);
        }

        /// <summary>A few counts of jitter per shot moves the edges by about
        /// that much times the stretch from the targets to the edges, not
        /// more: the least-squares line averages the two shots on each side.</summary>
        [Fact]
        public void JitteredShots_StayCloseToTheWindow()
        {
            Assert.True(GunCon2Calibration.TryFit(Targets, ShotsFor(200, 700, 30, 230, jitter: 2), out var c));
            Assert.InRange(c.MinX, 195, 205);
            Assert.InRange(c.MaxX, 695, 705);
            Assert.InRange(c.MinY, 25, 35);
            Assert.InRange(c.MaxY, 225, 235);
        }

        /// <summary>Four pulls at one spot measure nothing, and the screen
        /// starts over instead of storing a collapsed window.</summary>
        [Fact]
        public void ShotsAtOneSpot_DoNotFit()
        {
            var same = Enumerable.Repeat((400, 120), Targets.Length).ToArray();
            Assert.False(GunCon2Calibration.TryFit(Targets, same, out var c));
            Assert.Null(c);
        }

        [Fact]
        public void TargetsThatDoNotDiffer_DoNotFit()
        {
            var column = new (double U, double V)[] { (0.5, 0.1), (0.5, 0.9) };
            Assert.False(GunCon2Calibration.TryFit(column, new[] { (400, 40), (402, 210) }, out _));
            Assert.False(GunCon2Calibration.TryFit(Targets, ShotsFor(175, 720, 20, 240).Take(3).ToArray(), out _));
        }

        [Fact]
        public void TheStoredText_RoundTrips()
        {
            var c = new GunCon2Calibration(210, 690, 32, 226);
            Assert.Equal("210,690,32,226", c.ToString());
            Assert.True(GunCon2Calibration.TryParse(c.ToString(), out var back));
            Assert.Equal((210, 690, 32, 226), (back.MinX, back.MaxX, back.MinY, back.MaxY));
            Assert.False(back.IsDefault);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("175,720,20")]
        [InlineData("175,720,20,240,1")]
        [InlineData("a,720,20,240")]
        [InlineData("400,405,20,240")]
        [InlineData("175,720,100,100")]
        public void UnusableText_ReadsAsTheDefault(string text)
        {
            Assert.False(GunCon2Calibration.TryParse(text, out _));
            Assert.Same(GunCon2Calibration.Default, GunCon2Calibration.Parse(text));
        }

        [Fact]
        public void TheAim_ScalesByTheCalibration()
        {
            var c = new GunCon2Calibration(210, 690, 32, 226);
            var (x0, y0, on0) = SdlDeviceWrapper.GunCon2Aim(210, 32, c);
            Assert.True(on0);
            Assert.Equal(-1f, x0, precision: 4);
            Assert.Equal(-1f, y0, precision: 4);
            var (x1, y1, _) = SdlDeviceWrapper.GunCon2Aim(690, 226, c);
            Assert.Equal(1f, x1, precision: 4);
            Assert.Equal(1f, y1, precision: 4);
            var (xm, ym, _) = SdlDeviceWrapper.GunCon2Aim(450, 129, c);
            Assert.Equal(0f, xm, precision: 4);
            Assert.Equal(0f, ym, precision: 4);

            // The same counts over the default window land elsewhere.
            var (xd, _, _) = SdlDeviceWrapper.GunCon2Aim(210, 32);
            Assert.NotEqual(-1f, xd);
        }

        /// <summary>The poll thread latches each pull with the counts read in
        /// the same poll, so a pull shorter than the screen's sampling
        /// interval still arrives, and a held trigger is one pull.</summary>
        [Fact]
        public void ThePullLatch_TakesEachPressOnce()
        {
            using var gun = new SdlDeviceWrapper();
            Assert.False(gun.TryGetGunCon2Pull(out _, out _, out _));

            gun.RecordGunCon2Trigger(300, 100, false);
            Assert.False(gun.TryGetGunCon2Pull(out _, out _, out _));

            gun.RecordGunCon2Trigger(310, 110, true);
            Assert.True(gun.TryGetGunCon2Pull(out short x, out short y, out int first));
            Assert.Equal((310, 110), (x, y));

            gun.RecordGunCon2Trigger(500, 200, true);
            Assert.True(gun.TryGetGunCon2Pull(out x, out y, out int held));
            Assert.Equal(first, held);
            Assert.Equal((310, 110), (x, y));

            gun.RecordGunCon2Trigger(500, 200, false);
            gun.RecordGunCon2Trigger(620, 215, true);
            Assert.True(gun.TryGetGunCon2Pull(out x, out y, out int second));
            Assert.NotEqual(first, second);
            Assert.Equal((620, 215), (x, y));
        }

        [Fact]
        public void TheCalibration_PersistsPerDevice()
        {
            var ser = new XmlSerializer(typeof(UserDevice));
            var ud = new UserDevice { VendorId = 0x0B9A, ProdId = 0x016A, GunCalibration = "210,690,32,226" };
            using var w = new StringWriter();
            ser.Serialize(w, ud);
            Assert.Contains("<GunCalibration>210,690,32,226</GunCalibration>", w.ToString());
            using var r = new StringReader(w.ToString());
            Assert.Equal("210,690,32,226", ((UserDevice)ser.Deserialize(r)).GunCalibration);
            Assert.Equal(string.Empty, new UserDevice().GunCalibration);
        }

        [Fact]
        public void TheRow_ShowsTheWindowAndResetsToTheDefault()
        {
            var si = PadForge.Resources.Strings.Strings.Instance;
            var row = new DeviceRowViewModel { InstanceGuid = Guid.NewGuid(), ShowGunCalibration = true };
            Assert.Equal(string.Format(si.Devices_GunWindowDefault, 175, 720, 20, 240), row.GunCalibrationStatus);

            row.GunCalibration = "210,690,32,226";
            Assert.Equal(string.Format(si.Devices_GunWindowCalibrated, 210, 690, 32, 226), row.GunCalibrationStatus);

            Assert.True(DeviceRowViewModel.CanResetSetting(nameof(DeviceRowViewModel.GunCalibration)));
            row.ResetSettingCommand.Execute(nameof(DeviceRowViewModel.GunCalibration));
            Assert.Equal(string.Empty, row.GunCalibration);
        }

        [Fact]
        public void TheButton_NeedsTheGunConnectedHereAndOnline()
        {
            var row = new DeviceRowViewModel { ShowGunCalibration = true, GunConnectedHere = true };
            Assert.False(row.CanCalibrateGun);
            row.IsOnline = true;
            Assert.True(row.CanCalibrateGun);
            row.GunConnectedHere = false;
            Assert.False(row.CanCalibrateGun);
        }

        /// <summary>The Light Gun section sits between Power and Raw Input
        /// State. It always has exactly one rule above it (its own, or the
        /// one under the sections above when Power is absent) and one below
        /// it (the Raw Input State divider).</summary>
        [Theory]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void TheSection_HasOneRuleAboveAndOneBelow(bool gamepad, bool internalVirtual, bool power)
        {
            var row = new DeviceRowViewModel
            {
                DeviceTypeKey = gamepad ? "Gamepad" : "Keyboard",
                DevicePath = internalVirtual ? "web://controller/1" : @"\?\usb#vid_0b9a&pid_016a",
                ShowIdleDisconnect = power,
                ShowGunCalibration = true,
            };
            bool upperSections = row.ShowInputModeOrHidingSection;
            bool ruleUnderSections = upperSections;
            bool own = row.ShowGunCalibrationDivider;
            bool ruleDirectlyAbove = own || (ruleUnderSections && !row.ShowPowerSection);
            Assert.True(ruleDirectlyAbove);
            Assert.False(own && ruleUnderSections && !row.ShowPowerSection);
            Assert.True(row.ShowRawInputDivider);

            row.ShowGunCalibration = false;
            Assert.False(row.ShowGunCalibrationDivider);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "PadForge.App")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        /// <summary>The persistence sibling set, as Quick Charge pins its own:
        /// the row fill, the row flush that also hands the window to a
        /// connected gun, the connect-time hand-off, and the page.</summary>
        [Fact]
        public void SiblingLegs_FillFlushConnectAndPage()
        {
            string fill = RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("row.GunCalibration = ud.GunCalibration ?? string.Empty;", fill);
            // A Wii Remote shares the section (#485).
            Assert.Contains("row.ShowGunCalibration = ud.IsGunCon2 || ud.HasIrCamera;", fill);

            string flush = RepoText("PadForge.App", "Services", "DeviceService.cs");
            Assert.Contains("ud.GunCalibration = row.GunCalibration ?? string.Empty;", flush);
            Assert.Contains("gun.GunCon2Calibration = PadForge.Engine.GunCon2Calibration.Parse(ud.GunCalibration);", flush);

            string record = RepoText("PadForge.Engine", "Data", "UserDevice.cs");
            Assert.Contains("wrapper.GunCon2Calibration = GunCon2Calibration.Parse(GunCalibration);", record);

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.Contains("Binding SelectedDevice.ShowGunCalibration, Converter", page);
            Assert.Contains("Binding SelectedDevice.CanCalibrateGun", page);
            Assert.Contains("Click=\"GunCalibrate_Click\"", page);
            Assert.Contains("CommandParameter=\"GunCalibration\"", page);
        }
    }
}
