using System;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// Controllers on COM ports (hifihedgehog/SDL#33): the protocols PadForge
    /// offers, the SDL_JOYSTICK_SERIAL value it writes, and the ports it
    /// lists. The fork's hint parser (SDL_serial_engine.c,
    /// SDL_Serial_ParseHint and Serial_ValidKey) is the contract.
    /// </summary>
    public class SerialControllerTests
    {
        private readonly ITestOutputHelper _output;
        public SerialControllerTests(ITestOutputHelper output) => _output = output;

        /// <summary>The fork's serial module tokens (SDL_serialjoystick.c,
        /// serial_modules), bio2 included, which PadForge does not offer
        /// because its cabinet comes from another hint.</summary>
        private static readonly string[] ForkTokens =
        {
            "spaceball", "spaceorb", "magellan", "stinger", "warrior", "cyberman", "zhenhua", "ibus",
            "jvs", "vrinsight", "kettler", "iforce", "mastercontroller", "dji", "djimavicmini",
            "djiphantom3", "djiphantom2", "bio2", "bio2iidx", "bio2sdvx", "kfca", "panb", "rvol", "mdxf",
        };

        [Fact]
        public void EveryOfferedProtocol_IsAForkToken_AndOnlyBio2IsLeftOut()
        {
            var offered = SerialControllers.Protocols.Select(p => p.Token).ToArray();
            Assert.Equal(offered.Length, offered.Distinct().Count());
            Assert.All(offered, t => Assert.Contains(t, ForkTokens));
            Assert.Equal(new[] { "bio2" }, ForkTokens.Except(offered).ToArray());
            Assert.Equal(offered.Length, SerialControllers.Protocols.Select(p => p.Name).Distinct().Count());
        }

        [Theory]
        [InlineData("COM1")]
        [InlineData("com3")]
        [InlineData("COM9999")]
        [InlineData(@"FTDIBUS\VID_0403+PID_6001+A1B2C3D4A\0000")]
        [InlineData(@"USB\VID_2341&PID_0043\75237333536351F0B2E1")]
        [InlineData(@"ACPI\PNP0501\1")]
        public void APortSdlTakes_IsValid(string port) => Assert.True(SerialControllers.IsValidPort(port));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("COM0")]
        [InlineData("COM01")]
        [InlineData("COM10000")]
        [InlineData("FTDIBUS")]                        // no backslash: an enumerator alone
        [InlineData(@"USB\VID_2341 PID_0043\1")]      // a space
        [InlineData(@"USB\VID_2341,PID_0043\1")]      // the hint's entry separator
        [InlineData(@"USB\VID_2341=PID_0043\1")]      // the entry's port-protocol separator
        public void APortSdlRefuses_IsInvalid(string port) => Assert.False(SerialControllers.IsValidPort(port));

        [Fact]
        public void AnInstanceIdLongerThanADeviceIdAllows_IsInvalid()
        {
            Assert.True(SerialControllers.IsValidPort(@"USB\" + new string('A', 196)));
            Assert.False(SerialControllers.IsValidPort(@"USB\" + new string('A', 197)));
        }

        [Fact]
        public void APort_IsNamedByItsInstanceId_OrByItsComNameWhenTheIdWillNotFit()
        {
            Assert.Equal(@"FTDIBUS\VID_0403+PID_6001+A\0000",
                SerialControllers.PortKey(new ComPort("COM3", "USB Serial Port (COM3)", @"FTDIBUS\VID_0403+PID_6001+A\0000")));
            Assert.Equal("COM4", SerialControllers.PortKey(new ComPort("COM4", "Odd (COM4)", @"ROOT\PORTS=X\0000")));
            Assert.Null(SerialControllers.PortKey(new ComPort("COM10000", "Odd", "")));
            Assert.Null(SerialControllers.PortKey(null));
        }

        [Fact]
        public void TheHint_ListsEachUsableEntryAsPortEqualsProtocol()
        {
            var entries = new[]
            {
                new SerialControllerEntry { Port = @"FTDIBUS\VID_0403+PID_6001+A\0000", PortName = "COM3", Protocol = "spaceball" },
                new SerialControllerEntry { Port = "COM5", PortName = "COM5", Protocol = "DJIPHANTOM3" },
                new SerialControllerEntry { Port = "COM6", PortName = "COM6", Protocol = "bio2" },       // not offered
                new SerialControllerEntry { Port = "COM 7", PortName = "COM 7", Protocol = "jvs" },      // not a port
                null,
            };
            Assert.Equal(@"FTDIBUS\VID_0403+PID_6001+A\0000=spaceball,COM5=djiphantom3",
                SerialControllers.HintValue(entries));
            Assert.Equal(string.Empty, SerialControllers.HintValue(null));
            Assert.Equal(string.Empty, SerialControllers.HintValue(Array.Empty<SerialControllerEntry>()));
        }

        /// <summary>Konami's ACIO boards run only while the list names one
        /// (SDL_JOYSTICK_KONAMI_ACIO), so the fork leaves the port of a BIO2
        /// the user has not added closed, free for a game.</summary>
        [Fact]
        public void TheAcioHint_IsOnOnlyWhileTheListNamesAKonamiBoard()
        {
            Assert.Equal("0", SerialControllers.AcioHintValue(null));
            Assert.Equal("0", SerialControllers.AcioHintValue(string.Empty));
            Assert.Equal("0", SerialControllers.AcioHintValue("COM3=spaceball,COM5=dji"));
            foreach (string token in SerialControllers.AcioProtocols)
                Assert.Equal("1", SerialControllers.AcioHintValue("COM3=spaceball,COM9=" + token));
            Assert.Equal("1", SerialControllers.AcioHintValue("COM4=BIO2SDVX"));

            // From the dialog's entries through the value SDL gets: an entry
            // the serial hint leaves out does not turn ACIO on either.
            var added = new[] { new SerialControllerEntry { Port = "COM4", PortName = "COM4", Protocol = "bio2iidx" } };
            Assert.Equal("1", SerialControllers.AcioHintValue(SerialControllers.HintValue(added)));
            var unusable = new[] { new SerialControllerEntry { Port = "COM 5", PortName = "COM 5", Protocol = "kfca" } };
            Assert.Equal("0", SerialControllers.AcioHintValue(SerialControllers.HintValue(unusable)));
        }

        /// <summary>What SDL receives: ApplySerialControllers writes both hints
        /// together. Hints need no SDL_Init, and the joystick lock is a no-op
        /// before it (SDL_LockJoysticks takes SDL_event_lock, null until
        /// then), so this runs against the shipped SDL3.dll without starting
        /// a joystick driver.</summary>
        [Fact]
        public void ApplyingTheList_WritesTheSerialAndAcioHintsTogether()
        {
            try
            {
                InputManager.ApplySerialControllers(new[]
                {
                    new SerialControllerEntry { Port = "COM4", PortName = "COM4", Protocol = "bio2sdvx" },
                    new SerialControllerEntry { Port = "COM7", PortName = "COM7", Protocol = "stinger" },
                });
                Assert.Equal("COM4=bio2sdvx,COM7=stinger", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_SERIAL));
                Assert.Equal("1", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_KONAMI_ACIO));

                InputManager.ApplySerialControllers(new[]
                {
                    new SerialControllerEntry { Port = "COM7", PortName = "COM7", Protocol = "stinger" },
                });
                Assert.Equal("0", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_KONAMI_ACIO));
            }
            finally
            {
                InputManager.ApplySerialControllers(Array.Empty<SerialControllerEntry>());
            }
            Assert.Equal("0", SDL3.SDL.SDL_GetHint(SDL3.SDL.SDL_HINT_JOYSTICK_KONAMI_ACIO));
        }

        /// <summary>The ACIO set is exactly the offered Konami boards, the
        /// fork's serial_acio_modules without bio2.</summary>
        [Fact]
        public void TheAcioProtocols_AreTheOfferedKonamiBoards()
        {
            Assert.Equal(new[] { "bio2iidx", "bio2sdvx", "kfca", "panb", "rvol", "mdxf" }, SerialControllers.AcioProtocols);
            var konami = SerialControllers.Protocols
                .Where(p => p.Name.StartsWith("Konami ", StringComparison.Ordinal))
                .Select(p => p.Token);
            Assert.Equal(konami.OrderBy(t => t), SerialControllers.AcioProtocols.OrderBy(t => t));
        }

        [Fact]
        public void TheHint_StopsAtTheSixteenPortsSdlHolds()
        {
            var entries = Enumerable.Range(1, 20)
                .Select(n => new SerialControllerEntry { Port = "COM" + n, PortName = "COM" + n, Protocol = "stinger" });
            string hint = SerialControllers.HintValue(entries);
            Assert.Equal(SerialControllers.MaxEntries, hint.Split(',').Length);
            Assert.EndsWith("COM16=stinger", hint);
        }

        [Fact]
        public void AnEntry_ShowsItsControllerAndPort()
        {
            var e = new SerialControllerEntry { Port = "COM3", PortName = "COM3", Protocol = "stinger" };
            Assert.Equal("Gravis Stinger (COM3)", e.Display);
        }

        [Fact]
        public void TheEntries_RideTheSettingsFileAsAttributes()
        {
            var data = new AppSettingsData
            {
                SerialControllers = new[]
                {
                    new SerialControllerEntry { Port = @"FTDIBUS\VID_0403+PID_6001+A\0000", PortName = "COM3", Protocol = "magellan" },
                },
            };
            var serializer = new XmlSerializer(typeof(AppSettingsData));
            using var writer = new StringWriter();
            serializer.Serialize(writer, data);
            string xml = writer.ToString();
            Assert.Contains("<SerialControllers>", xml);
            Assert.Contains("<Controller Port=\"FTDIBUS\\VID_0403+PID_6001+A\\0000\" PortName=\"COM3\" Protocol=\"magellan\" />", xml);
            Assert.DoesNotContain("ControllerName", xml);
            Assert.DoesNotContain("Display", xml);

            using var reader = new StringReader(xml);
            var back = (AppSettingsData)serializer.Deserialize(reader);
            var e = Assert.Single(back.SerialControllers);
            Assert.Equal("magellan", e.Protocol);
            Assert.Equal("COM3", e.PortName);

            // A file from before the list has none.
            using var old = new StringReader("<AppSettingsData><EnableWebController>true</EnableWebController></AppSettingsData>");
            Assert.Null(((AppSettingsData)serializer.Deserialize(old)).SerialControllers);
        }

        /// <summary>The ports on this machine, read only, as the dialog
        /// lists them.</summary>
        [Fact]
        public void TheComPortList_ReadsThisMachine()
        {
            var ports = SerialControllers.ListComPorts();
            _output.WriteLine($"{ports.Count} COM ports");
            foreach (var p in ports)
            {
                _output.WriteLine($"{p.PortName} | {p.FriendlyName} | {p.InstanceId} -> {SerialControllers.PortKey(p) ?? "(unusable)"}");
                Assert.Matches(@"^COM[1-9][0-9]{0,3}$", p.PortName);
                Assert.False(string.IsNullOrWhiteSpace(p.FriendlyName));
            }
        }
    }

    public partial class ProfileServiceToggleTests
    {
        /// <summary>The IDs moved to PadForge's WinUSB driver ride the
        /// machine settings (#33 Part 15, rule 4).</summary>
        [Fact]
        public void WinUsbOptInsSurviveTheActualSaveAndLoad()
        {
            var (vm, settings) = Arrange();
            vm.Settings.WinUsbOptIns.Add(@"USB\VID_045E&PID_028E&REV_0114");
            var built = BuildApp(settings);
            Assert.Equal(new[] { @"USB\VID_045E&PID_028E&REV_0114" }, built.WinUsbOptIns);

            var (vm2, settings2) = Arrange();
            LoadApp(settings2, RoundTripApp(built));
            Assert.Equal(new[] { @"USB\VID_045E&PID_028E&REV_0114" }, vm2.Settings.WinUsbOptIns);

            vm2.Settings.WinUsbOptIns.Clear();
            Assert.Null(BuildApp(settings2).WinUsbOptIns);
        }

        /// <summary>The list is a machine setting: it rides PadForge.xml
        /// through the real save and load, and the load hands it to SDL
        /// without marking anything changed.</summary>
        [Fact]
        public void SerialControllersSurviveTheActualSaveAndLoad()
        {
            var (vm, settings) = Arrange();
            try
            {
                vm.Settings.SerialControllers.Add(new SerialControllerEntry { Port = "COM250", PortName = "COM250", Protocol = "spaceorb" });
                var built = BuildApp(settings);
                var e = Assert.Single(built.SerialControllers);
                Assert.Equal("spaceorb", e.Protocol);

                int raised = 0;
                var (vm2, settings2) = Arrange();
                vm2.Settings.SerialControllersChanged += (_, _) => raised++;
                LoadApp(settings2, RoundTripApp(built));
                var back = Assert.Single(vm2.Settings.SerialControllers);
                Assert.Equal("COM250", back.Port);
                Assert.Equal(0, raised);

                // None added: nothing written.
                vm2.Settings.SerialControllers.Clear();
                Assert.Null(BuildApp(settings2).SerialControllers);
            }
            finally
            {
                // The load hands the list to SDL. Leave SDL's hint empty.
                InputManager.ApplySerialControllers(Array.Empty<SerialControllerEntry>());
            }
        }
    }
}
