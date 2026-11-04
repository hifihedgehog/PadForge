using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using Xunit;
using static PadForge.Tests.LaptopFamilies.LaptopFamilyTest;

namespace PadForge.Tests.LaptopFamilies
{
    /// <summary>The Logitech G600 against OpenRGB's LogitechG600Controller
    /// and libratbag's G600 driver (#557).</summary>
    public class LogitechG600LightingTests
    {
        /// <summary>OpenRGB's registration: 046D:C24A, interface 1, usage
        /// page 0xFF80 (LogitechControllerDetect.cpp:83 and 1547).</summary>
        private static HidCollectionInfo Collection(ushort feature = 154, int iface = 1, ushort page = 0xFF80,
            ushort productId = 0xC24A, ushort vendorId = 0x046D)
            => new()
            {
                Path = $@"\\?\hid#vid_{vendorId:x4}&pid_{productId:x4}&mi_{iface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = vendorId, ProductId = productId, UsagePage = page, Usage = 0x0080, InterfaceNumber = iface,
                FeatureReportLength = feature,
            };

        [Fact]
        public void TheRegistration_FindsTheModel()
        {
            var model = LogitechG600Lighting.Instance.Match(Collection());
            Assert.NotNull(model);
            Assert.Same(model, HidLightingFamilies.Match(Collection()));
            Assert.Contains(LogitechG600Lighting.Instance, HidLightingFamilies.All);
            // OpenRGB's registered name (LogitechControllerDetect.cpp:1547).
            Assert.Equal("Logitech G600 Gaming Mouse", model.Name);
            Assert.Equal(50, model.RepaintMs);
            Assert.Equal(0, model.HoldMs);
            Assert.Equal(new[] { VendorPrograms.LogitechGHub, VendorPrograms.LogitechGamingSoftware }, model.Programs);
            Assert.Equal("logitech:046D:C24A", model.Map.Product);
            var light = Assert.Single(model.Map.Lights);
            Assert.Equal((0, LightKind.Zone, 1), (light.Address, light.Kind, light.Zone));
            // The F1 report is 8 bytes. A shorter feature report, another
            // interface, page, product or vendor is not the collection.
            Assert.NotNull(LogitechG600Lighting.Instance.Match(Collection(feature: 8)));
            Assert.Null(LogitechG600Lighting.Instance.Match(Collection(feature: 7)));
            Assert.Null(LogitechG600Lighting.Instance.Match(Collection(iface: 0)));
            Assert.Null(LogitechG600Lighting.Instance.Match(Collection(page: 0xFF00)));
            Assert.Null(LogitechG600Lighting.Instance.Match(Collection(productId: 0xC24B)));
            Assert.Null(LogitechG600Lighting.Instance.Match(Collection(vendorId: 0x0738)));
        }

        /// <summary>The G HUB agent and both hosts of Logitech Gaming
        /// Software, the names the linker counts the Logitech program by
        /// (LogiLedEngineNative.HostProcesses), are on the presence's
        /// list.</summary>
        [Fact]
        public void TheLogitechPrograms_AreListed()
        {
            Assert.Contains(VendorPrograms.LogitechGHub, VendorPrograms.All);
            Assert.Contains(VendorPrograms.LogitechGamingSoftware, VendorPrograms.All);
            Assert.True(VendorPrograms.LogitechGHub.Owns("lghub_agent"));
            Assert.True(VendorPrograms.LogitechGamingSoftware.Owns("LCore"));
            Assert.True(VendorPrograms.LogitechGamingSoftware.Owns("lgs"));
            Assert.False(VendorPrograms.LogitechGHub.Owns("lghub"));
            var running = PeripheralPresence.WithRunning(PeripheralPresence.None, new[] { "lghub_agent.exe" });
            Assert.True(running.LogitechGHubRunning);
            Assert.Same(VendorPrograms.LogitechGHub, running.Programs.FirstOf(VendorPrograms.Logitech));
        }

        /// <summary>OpenRGB's Direct mode: F1, the color, mode 0, speed 0
        /// (LogitechG600Controller.cpp:56-67, RGBControllerInterface.h:339).</summary>
        [Fact]
        public void APicture_IsTheF1Report()
        {
            var io = new LoggingHidChannel { FeatureReportLength = 8 };
            var session = LogitechG600Lighting.Session.Begin(io);
            Assert.Empty(io.Log);
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, LogitechG600Lighting.Lights(), new[] { 0x123456 }));
            Assert.Equal(new[] { "F F1 12 34 56 00 00 00 00" }, io.Lines());
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new LoggingHidChannel { FailWrites = true }, io.Wait, LogitechG600Lighting.Lights(), new[] { 1 }));
        }

        /// <summary>The session reads F0, takes the profile from its byte
        /// 1's high four bits, reads that profile's report, F3 to F5, and
        /// hands back its red, green, blue, effect and duration as F1
        /// (driver-logitech-g600.c:61-87, 194-224 and 360-399).</summary>
        [Fact]
        public void TheHandBack_IsTheActiveProfilesLighting()
        {
            var io = new LoggingHidChannel { FeatureReportLength = 154 };
            // Profile 2, resolution 3 in bits 1 and 2.
            io.Answers.Enqueue(Bytes("F0 26 00 00"));
            io.Answers.Enqueue(Padded("F5 AA BB CC 01 05 00 00 00 00 00 03", 154));
            var session = LogitechG600Lighting.Session.Begin(io);
            Assert.Equal(new[] { "G F0", "G F5" }, io.Lines());
            var handBack = Assert.Single(session.CrashFrames);
            Assert.Equal("F1 AA BB CC 01 05 00 00", Hex(handBack));
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Equal("F F1 AA BB CC 01 05 00 00", io.Lines().Last());
            Assert.False(session.HandBack(new LoggingHidChannel { FailWrites = true }, io.Wait));
        }

        [Theory]
        // Profile 3 does not exist, nor does 15.
        [InlineData("F0 30 00 00", "F6")]
        [InlineData("F0 F0 00 00", "FF")]
        // A reply that is not the F0 report.
        [InlineData("F1 00 00 00", "F3")]
        // A profile report under another ID.
        [InlineData("F0 00 00 00", "F4")]
        public void AProfileThatCannotBeRead_LeavesNoHandBack(string active, string profileId)
        {
            var io = new LoggingHidChannel { FeatureReportLength = 154 };
            io.Answers.Enqueue(Bytes(active));
            io.Answers.Enqueue(Padded(profileId + " AA BB CC 01 05", 154));
            var session = LogitechG600Lighting.Session.Begin(io);
            Assert.Empty(session.CrashFrames);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.DoesNotContain(io.Log, e => e.Kind == "F");
        }

        /// <summary>A read that fails, and a collection too short for the
        /// profile report, read nothing more and hand back nothing.</summary>
        [Fact]
        public void AFailedRead_OrAShortCollection_LeavesNoHandBack()
        {
            var failing = new LoggingHidChannel { FeatureReportLength = 154 };
            Assert.Empty(LogitechG600Lighting.Session.Begin(failing).CrashFrames);
            Assert.Equal(new[] { "G F0" }, failing.Lines());

            var profileFails = new LoggingHidChannel { FeatureReportLength = 154 };
            profileFails.Answers.Enqueue(Bytes("F0 10 00 00"));
            Assert.Empty(LogitechG600Lighting.Session.Begin(profileFails).CrashFrames);
            Assert.Equal(new[] { "G F0", "G F4" }, profileFails.Lines());

            var shortOne = new LoggingHidChannel { FeatureReportLength = 153 };
            shortOne.Answers.Enqueue(Bytes("F0 00 00 00"));
            Assert.Empty(LogitechG600Lighting.Session.Begin(shortOne).CrashFrames);
            Assert.Empty(shortOne.Log);
        }

        [Theory]
        [InlineData("F0 00", 0)]
        [InlineData("F0 1E", 1)]
        [InlineData("F0 2F", 2)]
        [InlineData("F0 30", -1)]
        [InlineData("F0 80", -1)]
        [InlineData("F3 00", -1)]
        [InlineData("F0", -1)]
        public void TheActiveProfile_IsByte1sHighFourBits(string report, int profile)
            => Assert.Equal(profile, LogitechG600Lighting.ActiveProfile(Bytes(report)));
    }

    /// <summary>The Logitech X56 against OpenRGB's LogitechX56Controller
    /// (#557).</summary>
    public class LogitechX56LightingTests
    {
        private static HidCollectionInfo Collection(ushort productId, ushort usage = 3, int iface = 2, ushort page = 0xFF00,
            ushort feature = 64, ushort vendorId = 0x0738)
            => new()
            {
                Path = $@"\\?\hid#vid_{vendorId:x4}&pid_{productId:x4}&mi_{iface:x2}#7&1&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
                VendorId = vendorId, ProductId = productId, UsagePage = page, Usage = usage, InterfaceNumber = iface,
                FeatureReportLength = feature,
            };

        /// <summary>The four registrations: the joystick and the throttle,
        /// each on usage 1 and usage 3 of page 0xFF00 on interface 2
        /// (LogitechControllerDetect.cpp:135-137 and 1558-1561), each named
        /// as OpenRGB names it.</summary>
        [Fact]
        public void TheRegistrations_FindTheJoystickAndTheThrottle()
        {
            foreach (ushort usage in new ushort[] { 1, 3 })
            {
                var joystick = LogitechX56Lighting.Instance.Match(Collection(0x2221, usage));
                var throttle = LogitechX56Lighting.Instance.Match(Collection(0xA221, usage));
                Assert.Equal("Logitech X56 Rhino Hotas Joystick", joystick.Name);
                Assert.Equal("Logitech X56 Rhino Hotas Throttle", throttle.Name);
                Assert.Equal("logitech:0738:2221", joystick.Map.Product);
                Assert.Equal("logitech:0738:A221", throttle.Map.Product);
                Assert.Same(joystick, HidLightingFamilies.Match(Collection(0x2221, usage)));
            }
            var model = LogitechX56Lighting.Instance.Match(Collection(0x2221));
            Assert.Equal(50, model.RepaintMs);
            Assert.Empty(model.Programs);
            Assert.Single(model.Map.Lights);
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2221, usage: 2)));
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2221, iface: 0)));
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2221, page: 0xFF01)));
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2221, feature: 63)));
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2222)));
            Assert.Null(LogitechX56Lighting.Instance.Match(Collection(0x2221, vendorId: 0x046D)));
            Assert.Contains(LogitechX56Lighting.Instance, HidLightingFamilies.All);
        }

        /// <summary>SetColor's two 64-byte feature reports: 09 00 02 and
        /// the brightness, OpenRGB's default 0x64, then 09 00 03 and the
        /// color (LogitechX56Controller.cpp:51-84,
        /// RGBController_LogitechX56.cpp:40-42). The save report, 01 01, is
        /// never sent.</summary>
        [Fact]
        public void APicture_IsTheBrightnessThenTheColor()
        {
            var io = new LoggingHidChannel();
            var session = new LogitechX56Lighting.Session();
            Assert.Equal(HidLightingPaint.Sent, session.Paint(io, io.Wait, LogitechX56Lighting.Lights(), new[] { 0x123456 }));
            Assert.Equal(new[] { Padded("09 00 02 64", 64), Padded("09 00 03 12 34 56", 64) }, io.Sent("F"));
            Assert.DoesNotContain(io.Sent("F"), f => f[0] == 0x01);
            Assert.True(session.HandBack(io, io.Wait));
            Assert.Empty(session.CrashFrames);
            Assert.Equal(2, io.Log.Count);
            Assert.Equal(HidLightingPaint.Failed,
                session.Paint(new LoggingHidChannel { FailAfter = 1 }, io.Wait, LogitechX56Lighting.Lights(), new[] { 1 }));
        }
    }
}
