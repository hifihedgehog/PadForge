using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Discussion #395: a Vader 5 Pro re-enumerated in a loop while Flydigi's
    /// service could reach it. PadForge cannot see the service's traffic, so
    /// it names the service on the Devices page, records the environment on
    /// every Flydigi arrival, labels each SDL view by backend, and offers a
    /// switch for SDL's Flydigi driver. These pin the emitted records and the
    /// state the runtime replays, not the production source text. Rides the
    /// switch's collection: ApplyFlydigiEnhancedProtocol is one static.
    /// </summary>
    [Collection("FlydigiSwitchStatics")]
    public class FlydigiServiceConflictTests
    {
        [Theory]
        [InlineData(0x37D7, 0x2401, true)]   // Vader 5 Pro
        [InlineData(0x37D7, 0x0001, true)]   // any Flydigi V2 product
        [InlineData(0x04B4, 0x2412, true)]   // first-generation gamepad on the Cypress vendor id
        [InlineData(0x04B4, 0x0001, false)]  // another Cypress device
        [InlineData(0x045E, 0x028E, false)]  // Xbox 360
        [InlineData(0x054C, 0x0CE6, false)]  // DualSense
        public void IsFlydigiDevice_MatchesSdlsOwnTable(int vid, int pid, bool expected)
            => Assert.Equal(expected, FlydigiServiceWatch.IsFlydigiDevice((ushort)vid, (ushort)pid));

        [Fact]
        public void Refresh_ReportsAChangeOnlyWhenTheRunningSetChanged()
        {
            FlydigiServiceWatch.Refresh();
            Assert.NotNull(FlydigiServiceWatch.Running);
            Assert.False(string.IsNullOrEmpty(FlydigiServiceWatch.Detail), "Detail says 'none' when nothing runs");
            // The flag and the value agree: a second scan reports a change
            // exactly when Running differs from the first scan's.
            string first = FlydigiServiceWatch.Running;
            bool changed = FlydigiServiceWatch.Refresh();
            Assert.Equal(!string.Equals(first, FlydigiServiceWatch.Running, System.StringComparison.Ordinal), changed);
            // Names are canonical: every entry is spelled as the watch lists it.
            foreach (var n in FlydigiServiceWatch.Running.Split(", ", System.StringSplitOptions.RemoveEmptyEntries))
                Assert.Contains(n, new[] { "SpaceStationService", "GameControllerService", "Flydigi Space Station" });
        }

        [Fact]
        public void DescribeArrival_CarriesTheIdentityTheViewAndTheSnapshot()
        {
            // The detail is passed in, so the line is one snapshot. The earlier
            // shape read the static twice, and a second read differing from the
            // first is the one way this test failed (seen once on 09-04, not
            // reproduced in three full runs).
            string line = FlydigiServiceWatch.DescribeArrival(0x37D7, 0x2401, 7, "hidapi",
                @"\\?\HID#VID_37D7&PID_2401&MI_01#7&2e838171&0&0000",
                @"SpaceStationService.exe pid=4242 v=4.1.2 path=C:\x\SpaceStationService.exe",
                "hidhide=ok active=true whitelist=2 SpaceStationService.exe=name-listed");
            Assert.StartsWith("FLYDIGI arrival 37D7:2401 sdl=7 backend=hidapi path=", line);
            Assert.Contains(@"MI_01#7&2e838171", line);
            Assert.Contains("service=[SpaceStationService.exe pid=4242 v=4.1.2 path=", line);
            Assert.EndsWith("SpaceStationService.exe=name-listed", line);
        }

        [Fact]
        public void DescribeReach_NamesEveryImageAndItsOutcome_WhateverTheDriverState()
        {
            string s = HidHideController.DescribeReach("SpaceStationService.exe", "GameControllerService.exe");
            Assert.StartsWith("hidhide=", s);
            Assert.Contains(" SpaceStationService.exe=", s);
            Assert.Contains(" GameControllerService.exe=", s);
            var verdicts = s.Split(' ').Where(t => t.Contains(".exe=")).Select(t => t.Substring(t.IndexOf('=') + 1)).ToList();
            Assert.Equal(2, verdicts.Count);
            Assert.All(verdicts, v => Assert.Contains(v, new[] { "name-listed", "not-listed", "unknown" }));
            // With the driver unavailable every image reads unknown, never a verdict.
            if (s.StartsWith("hidhide=unavailable"))
                Assert.All(verdicts, v => Assert.Equal("unknown", v));
            // A driver that answered reports each read's own outcome, never a
            // false value standing in for a failed read.
            if (s.StartsWith("hidhide=ok"))
            {
                Assert.Matches(@"active=(true|false|read-failed|err:\w+)", s);
                Assert.Matches(@"whitelist=(\d+|read-failed|err:\w+)", s);
                if (s.Contains("whitelist=read-failed"))
                    Assert.All(verdicts, v => Assert.Equal("unknown", v));
            }
        }

        [Fact]
        public void ApplyFlydigiEnhancedProtocol_RecordsTheStateInitReplays()
        {
            bool before = InputManager.FlydigiEnhancedProtocolDesired;
            try
            {
                InputManager.ApplyFlydigiEnhancedProtocol(false);
                Assert.False(InputManager.FlydigiEnhancedProtocolDesired);
                InputManager.ApplyFlydigiEnhancedProtocol(true);
                Assert.True(InputManager.FlydigiEnhancedProtocolDesired);
            }
            finally
            {
                InputManager.ApplyFlydigiEnhancedProtocol(before);
            }
        }

        [Fact]
        public void InitializeSdl_ReplaysTheSwitch_AndTheSweepRepublishesOnAServiceChange()
        {
            // Wiring pins with comment lines rejected, so a commented-out
            // call does not satisfy them.
            string im = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.cs"));
            Assert.Contains(im.Split('\n'), l => l.Trim() == "ApplyFlydigiEnhancedProtocol(_flydigiEnhancedDesired);");
            string step1 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            Assert.Contains(step1.Split('\n'), l => l.Trim() == "if (FlydigiServiceWatch.Refresh())");
            Assert.Contains(step1.Split('\n'), l => l.Trim().StartsWith("MarkChanged(ref changed, \"flydigi\", "));
            Assert.Contains(step1.Split('\n'), l => l.Trim().StartsWith("Engine.SdlDiagLog.WriteLine(FlydigiServiceWatch.DescribeArrival("));
        }

        // SDL_CreateJoystickGUID: bus(2) crc(2) vendor(2) 0(2) product(2) 0(2)
        // version(2) signature(1) data(1) = 16 bytes = 32 hex chars, and the
        // signature is data[14], hex chars 28-29.
        private static string Guid(string sig) => "03000000d7370000012400000000" + sig + "00";

        [Theory]
        [InlineData("78", "xinput")]
        [InlineData("68", "hidapi")]
        [InlineData("72", "rawinput")]
        [InlineData("77", "wgi")]
        [InlineData("00", "dinput")]
        [InlineData("ab", "0xAB")]
        public void BackendFromGuid_DecodesTheDriverSignatureByte(string sig, string expected)
        {
            string guid = Guid(sig);
            Assert.Equal(32, guid.Length);
            Assert.Equal(expected, SdlDeviceWrapper.BackendFromGuid(guid));
        }

        [Theory]
        [InlineData("")]
        [InlineData("short")]
        [InlineData("03000000d7370000012400000000zz00")]
        public void BackendFromGuid_UnreadableGuidsAreUnknown(string guid)
            => Assert.Equal("unknown", SdlDeviceWrapper.BackendFromGuid(guid));

        [Fact]
        public void DeviceRow_WarnsOnlyForAFlydigiRow_AndOnlyWhileTheServiceRuns()
        {
            var vader = new DeviceRowViewModel { VendorId = 0x37D7, ProductId = 0x2401 };
            Assert.True(vader.IsFlydigiDevice);
            Assert.False(vader.HasFlydigiServiceWarning);
            Assert.Equal(string.Empty, vader.FlydigiServiceWarning);

            vader.FlydigiServiceRunning = "SpaceStationService";
            Assert.True(vader.HasFlydigiServiceWarning);
            Assert.Equal(string.Format(Strings.Instance.Flydigi_ServiceWarning_Format, "SpaceStationService"),
                vader.FlydigiServiceWarning);
            Assert.Contains("SpaceStationService", vader.FlydigiServiceWarning);

            vader.FlydigiServiceRunning = "";
            Assert.False(vader.HasFlydigiServiceWarning);

            var xbox = new DeviceRowViewModel { VendorId = 0x045E, ProductId = 0x028E };
            xbox.FlydigiServiceRunning = "SpaceStationService";
            Assert.False(xbox.IsFlydigiDevice);
            Assert.False(xbox.HasFlydigiServiceWarning, "a non-Flydigi row never carries the notice");
        }

        [Fact]
        public void DeviceRow_RaisesTheWarningDependents_OnServiceAndOnIdentity()
        {
            var row = new DeviceRowViewModel();
            var raised = new List<string>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            row.FlydigiServiceRunning = "GameControllerService";
            Assert.Contains("HasFlydigiServiceWarning", raised);
            Assert.Contains("FlydigiServiceWarning", raised);

            raised.Clear();
            row.VendorId = 0x37D7;                       // identity arrives after the string
            Assert.Contains("IsFlydigiDevice", raised);
            Assert.Contains("HasFlydigiServiceWarning", raised);
            raised.Clear();
            row.ProductId = 0x2401;
            Assert.Contains("FlydigiServiceWarning", raised);
            Assert.True(row.HasFlydigiServiceWarning);
        }

        [Fact]
        public void FlydigiEnhancedProtocol_DefaultsOn_RoundTrips_AndOldFilesReadOn()
        {
            var fresh = new AppSettingsData();
            Assert.True(fresh.FlydigiEnhancedProtocol);

            var ser = new XmlSerializer(typeof(AppSettingsData));
            var off = new AppSettingsData { FlydigiEnhancedProtocol = false };
            string xml;
            using (var w = new StringWriter()) { ser.Serialize(w, off); xml = w.ToString(); }
            Assert.Contains("<FlydigiEnhancedProtocol>false</FlydigiEnhancedProtocol>", xml);
            using (var r = new StringReader(xml))
                Assert.False(((AppSettingsData)ser.Deserialize(r)).FlydigiEnhancedProtocol);

            // A file written before the element existed keeps the shipped behavior.
            string legacy = xml.Replace("<FlydigiEnhancedProtocol>false</FlydigiEnhancedProtocol>", "");
            Assert.DoesNotContain("FlydigiEnhancedProtocol", legacy);
            using (var r = new StringReader(legacy))
                Assert.True(((AppSettingsData)ser.Deserialize(r)).FlydigiEnhancedProtocol);
        }

        [Fact]
        public void SettingsViewModel_CarriesTheSwitch_DefaultOn()
        {
            var vm = new SettingsViewModel();
            Assert.True(vm.FlydigiEnhancedProtocol);
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.FlydigiEnhancedProtocol = false;
            Assert.Contains("FlydigiEnhancedProtocol", raised);
        }

        [Fact]
        public void HintConstant_IsSdlsName()
            => Assert.Equal("SDL_JOYSTICK_HIDAPI_FLYDIGI", SDL3.SDL.SDL_HINT_JOYSTICK_HIDAPI_FLYDIGI);

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
