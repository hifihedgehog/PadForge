using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The port OpenRGB's SDK server listens on, read the way OpenRGB
    /// decides it (#557): the --server-port argument (cli.cpp:1558-1590),
    /// else OpenRGB.json's Server/default_port in the directory --config
    /// names or in %APPDATA%\OpenRGB (ResourceManager.cpp:1109-1153 and
    /// 1325-1352), else 6742.
    /// </summary>
    public class OpenRgbServerPortTests
    {
        [Fact]
        public void TheArgument_IsTheOneAfterTheOption_From1024To65535()
        {
            Assert.Equal(6800, OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--server", "--server-port", "6800" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--server-port", "80" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--server-port", "70000" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--server-port", "abc" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--server-port" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(new[] { "OpenRGB.exe", "--SERVER-PORT", "6800" }));
            Assert.Equal(1024, OpenRgbServerPort.PortFromArguments(new[] { "x", "--server-port", "1024" }));
            Assert.Equal(65535, OpenRgbServerPort.PortFromArguments(new[] { "x", "--server-port", "65535" }));
            Assert.Null(OpenRgbServerPort.PortFromArguments(null));
            Assert.Equal(@"D:\cfg", OpenRgbServerPort.ConfigFromArguments(new[] { "x", "--config", @"D:\cfg" }));
            Assert.Null(OpenRgbServerPort.ConfigFromArguments(new[] { "x", "--config" }));
        }

        /// <summary>The "Server" settings' "default_port"
        /// (ResourceManager.cpp:146-178 writes it, 305-309 bound it).</summary>
        [Fact]
        public void TheSettings_GiveTheServersDefaultPort()
        {
            Assert.Equal(6743, OpenRgbServerPort.PortFromSettings(
                "{\"Server\": {\"all_controllers\": false, \"default_host\": \"127.0.0.1\", \"default_port\": 6743}}"));
            Assert.Null(OpenRgbServerPort.PortFromSettings("{\"Server\": {\"default_port\": 0}}"));
            Assert.Null(OpenRgbServerPort.PortFromSettings("{\"Server\": {\"default_host\": \"127.0.0.1\"}}"));
            Assert.Null(OpenRgbServerPort.PortFromSettings("{\"Detectors\": {}}"));
            Assert.Null(OpenRgbServerPort.PortFromSettings("{not json"));
            Assert.Null(OpenRgbServerPort.PortFromSettings(null));
        }

        [Fact]
        public void ACommandLine_IsSplitByWindowsRules()
        {
            Assert.Equal(new[] { @"C:\Program Files\OpenRGB\OpenRGB.exe", "--server-port", "6800", "--config", @"C:\My Config" },
                OpenRgbServerPort.Split("\"C:\\Program Files\\OpenRGB\\OpenRGB.exe\" --server-port 6800 --config \"C:\\My Config\""));
            Assert.Empty(OpenRgbServerPort.Split(null));
            Assert.Empty(OpenRgbServerPort.Split("  "));
        }

        /// <summary>The argument wins over the settings, the settings in the
        /// --config directory over the ones under %APPDATA%, and the default
        /// stands where neither names a port.</summary>
        [Fact]
        public void ThePort_IsTheArgument_ThenTheSettings_ThenTheDefault()
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Users\u\AppData\Roaming\OpenRGB\OpenRGB.json"] = "{\"Server\":{\"default_port\":6750}}",
                [@"D:\cfg\OpenRGB.json"] = "{\"Server\":{\"default_port\":6760}}",
            };
            string Read(string path) => files.TryGetValue(path, out var text) ? text : null;
            const string appData = @"C:\Users\u\AppData\Roaming";
            Assert.Equal(6800, OpenRgbServerPort.Resolve("OpenRGB.exe --server-port 6800 --config D:\\cfg", appData, Read));
            Assert.Equal(6760, OpenRgbServerPort.Resolve("OpenRGB.exe --config D:\\cfg", appData, Read));
            Assert.Equal(6750, OpenRgbServerPort.Resolve("OpenRGB.exe --server", appData, Read));
            Assert.Equal(6750, OpenRgbServerPort.Resolve(null, appData, Read));
            Assert.Equal(6742, OpenRgbServerPort.Resolve("OpenRGB.exe", @"C:\Nowhere", Read));
            Assert.Equal(6742, OpenRgbServerPort.Resolve("OpenRGB.exe", null, Read));
            Assert.Equal(6742, OpenRgbServerPort.Resolve("OpenRGB.exe", appData, _ => throw new InvalidOperationException()));
        }

        [Fact]
        public void InTheTestHost_NothingOfOpenRgbsIsRead()
        {
            Assert.Equal(OpenRgbProtocol.DefaultPort, OpenRgbServerPort.Find());
            Assert.Null(OpenRgbServerPort.CommandLineOf((uint)Environment.ProcessId));
            Assert.Null(OpenRgbServerPort.ReadSettings(typeof(OpenRgbServerPortTests).Assembly.Location));
        }
    }

    /// <summary>
    /// OpenRGB runs and its SDK server has not said which devices it holds
    /// (#557): every device PadForge would write to on its own is left to
    /// OpenRGB, by the #494 rule that a direct path is open only while the
    /// program that drives the device does not run.
    /// </summary>
    public class OpenRgbUnansweredLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000f1");
        private static readonly Guid B = new("00000000-0000-0000-0000-0000000000f2");
        private static readonly Guid Box = new("44444444-3333-4444-5555-666666666666");

        private static LinkInputs Inputs(bool unanswered, PeripheralPresence presence, params DirectUnit[] units)
            => new(HidppSnapshot.Empty, presence ?? PeripheralPresence.None) { Units = units, OpenRgbUnanswered = unanswered };

        [Fact]
        public void ADevicesOwnPaths_AreLeftToOpenRgb_AndNamedTaken()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Box, null);
            var razer = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x024E);
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, null, razer)).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal(new[] { razer.Path }, links.TakenByOpenRgb);
            Assert.Equal("OpenRGB", links.LeftToProgram);
            Assert.True(links.WaitsForOpenRgbServer);
            Assert.Equal(PeripheralOutputKinds.Lighting, links.Kinds);

            // With the server answered, and holding nothing of it, the unit
            // lights the row again.
            links = PeripheralLinker.Build(new[] { row }, Inputs(false, null, razer)).For(A);
            Assert.Equal(new[] { razer.Path }, links.Lighting);
            Assert.False(links.WaitsForOpenRgbServer);
        }

        [Fact]
        public void ALampArrayAndANativeUnit_AreBothTaken()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x048D, 0xC197, Box, null);
            var own = new DirectUnit(OutputFamily.HidLightingUnit, "own", Box, 0x048D, 0xC197) { LiveThroughLampArray = true };
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x048D, 0xC193);
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, null, own, lamp)).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal(new[] { own.Path, lamp.Path }, links.TakenByOpenRgb);
        }

        /// <summary>A device Windows Dynamic Lighting lights keeps Windows'
        /// line: its backend writes nothing to it already.</summary>
        [Fact]
        public void AWindowsLitLampArray_StaysWithWindows()
        {
            var row = new LinkRow(A, InputDeviceType.Mouse, 0x1234, 0x0001, Box, null);
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x1234, 0x0001) { WindowsLit = true };
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, null, lamp)).For(A);
            Assert.Equal(new[] { lamp.Path }, links.Lighting);
            Assert.Empty(links.TakenByOpenRgb);
            Assert.False(links.WaitsForOpenRgbServer);
        }

        /// <summary>A vendor's program that runs keeps its shared path: the
        /// rule closes PadForge's own paths alone.</summary>
        [Fact]
        public void AVendorProgramsSharedPath_IsKept()
        {
            var row = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Box, null);
            var razer = new DirectUnit(OutputFamily.RazerUnit, "razer", Box, PeripheralLinker.RazerVid, 0x024E);
            var synapse = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            var links = PeripheralLinker.Build(new[] { row }, Inputs(true, synapse, razer)).For(A);
            Assert.Equal(new[] { new OutputPath(OutputFamily.ChromaCategory, "keyboard") }, links.Lighting);
            Assert.Empty(links.TakenByOpenRgb);
            Assert.False(links.WaitsForOpenRgbServer);
        }

        /// <summary>A unit with a row of its own holds no path, its path is
        /// taken, and no new unit row opens meanwhile.</summary>
        [Fact]
        public void AUnitRow_IsLeftToOpenRgb_AndNoNewOneOpens()
        {
            var headset = new DirectUnit(OutputFamily.LampArrayUnit, "headset", Box, 0x1532, 0x0527);
            var strip = new DirectUnit(OutputFamily.SteelSeriesUnit, "strip", Box, PeripheralLinker.SteelSeriesVid, 0x1600);
            var row = new LinkRow(A, InputDeviceType.PeripheralLighting, 0x5046, 0x4C55, Guid.Empty, null) { Unit = headset.Path };
            var inputs = Inputs(true, null, headset, strip);
            var links = PeripheralLinker.Build(new[] { row }, inputs).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal(new[] { headset.Path }, links.TakenByOpenRgb);
            Assert.True(links.WaitsForOpenRgbServer);

            var orphans = PeripheralLinker.Orphans(new[] { row }, inputs);
            Assert.Equal(new[] { headset.Path }, orphans.Select(u => u.Path));
            Assert.Equal(2, PeripheralLinker.Orphans(new[] { row }, inputs with { OpenRgbUnanswered = false }).Count);
        }

        [Fact]
        public void AHidppUnitRow_IsLeftToOpenRgb()
        {
            var unit = PeripheralLinkingTests.Unit(1, 8, Box) with
            {
                RgbFeatureId = HidppUnitProtocol.ColorLedEffects,
                Zones = new[] { new HidppZone(0, 1) },
            };
            var path = new OutputPath(OutputFamily.HidppUnit, unit.Key);
            var row = new LinkRow(A, InputDeviceType.PeripheralLighting, 0x5046, 0x4C55, Guid.Empty, null) { Unit = path };
            var inputs = new LinkInputs(new HidppSnapshot(new[] { unit }, Array.Empty<Guid>()), PeripheralPresence.None)
            {
                OpenRgbUnanswered = true,
            };
            var links = PeripheralLinker.Build(new[] { row }, inputs).For(A);
            Assert.Empty(links.Lighting);
            Assert.Equal(new[] { path }, links.TakenByOpenRgb);
            Assert.True(links.WaitsForOpenRgbServer);
            links = PeripheralLinker.Build(new[] { row }, inputs with { OpenRgbUnanswered = false }).For(A);
            Assert.Equal(new[] { path }, links.Lighting);
        }

        [Fact]
        public void LightsOnItsOwn_IsTheChoicesThatWrite()
        {
            var lit = new List<PeripheralLinker.Candidate> { new(new OutputPath(OutputFamily.LampArrayUnit, "l"), 0, true) };
            var dark = new List<PeripheralLinker.Candidate> { new(new OutputPath(OutputFamily.LampArrayUnit, "l"), 0, false) };
            Assert.True(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.Native, lit));
            Assert.True(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.NativeAndLampArray, lit));
            Assert.True(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.LampArray, dark));
            Assert.False(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.LampArray, lit));
            Assert.False(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.Shared, dark));
            Assert.False(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.None, dark));
            Assert.False(PeripheralLinker.LightsOnItsOwn(PeripheralLinker.LightingChoice.OpenRgb, dark));
        }

        [Fact]
        public void TheFlag_RepublishesTheTable()
        {
            var with = new LinkTable(new[] { new DeviceLinks { Device = A, WaitsForOpenRgbServer = true, LeftToProgram = "OpenRGB" } });
            var without = new LinkTable(new[] { new DeviceLinks { Device = A, LeftToProgram = "OpenRGB" } });
            Assert.False(with.SameAs(without));
            Assert.True(with.SameAs(with));
            _ = B;
        }
    }

    /// <summary>
    /// A device OpenRGB holds that no row reaches gets a row of its own
    /// (#557): memory, a graphics card, a mainboard, a fan hub, a strip.
    /// </summary>
    public class OpenRgbUnitRowTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000e7");
        private static readonly Guid Box = new("55555555-3333-4444-5555-666666666666");

        private static DirectUnit Ram(string key = "I2C: SMBus 0x51") => new(OutputFamily.OpenRgbDevice, key, Guid.Empty, 0, 0)
        {
            Name = "Corsair Vengeance RGB PRO",
        };

        private static LinkInputs Inputs(params DirectUnit[] units)
            => new(HidppSnapshot.Empty, PeripheralPresence.None) { Units = units };

        private static LinkRow UnitRow(DirectUnit unit) => new(A, InputDeviceType.PeripheralLighting, 0x5046, 0x4C55, Guid.Empty, null)
        {
            Unit = unit.Path,
        };

        [Fact]
        public void AControllerNoRowReaches_GetsARow_NamedByOpenRgb_EvenWithNoContainer()
        {
            var ram = Ram();
            var orphan = Assert.Single(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(ram)));
            Assert.Equal(ram.Path, orphan.Path);
            Assert.Equal("Corsair Vengeance RGB PRO", PeripheralUnitRow.NameFor(orphan));
            Assert.Equal("Corsair Vengeance RGB PRO", new PeripheralUnitRow(orphan).Name);
            Assert.Equal(PeripheralUnitRow.IdentityFor(OutputFamily.OpenRgbDevice, ram.Key), new PeripheralUnitRow(orphan).InstanceGuid);
        }

        [Fact]
        public void ItsRow_IsLitThroughOpenRgb_WhateverVendorProgramRuns()
        {
            var pad = new DirectUnit(OutputFamily.OpenRgbDevice, "HID: pad", Box, PeripheralLinker.RazerVid, 0x0C02);
            var synapse = new PeripheralPresence(false, false, true, false, false) { RazerSoftwareRunning = true };
            var links = PeripheralLinker.Build(new[] { UnitRow(pad) },
                new LinkInputs(HidppSnapshot.Empty, synapse) { Units = new[] { pad } }).For(A);
            Assert.Equal(new[] { pad.Path }, links.Lighting);
            Assert.Empty(links.TakenByOpenRgb);
            Assert.True(links.UnitRow);

            // Asleep, or with no Direct mode: no path.
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(pad) }, Inputs(pad with { Asleep = true })).For(A));
            Assert.Null(PeripheralLinker.Build(new[] { UnitRow(pad) }, Inputs(pad with { NoDirectMode = true })).For(A));
        }

        /// <summary>A controller of a device a row reaches gets no row: the
        /// device row, or the unit row of the device's other unit, lights it
        /// through OpenRGB already.</summary>
        [Fact]
        public void AControllerOfADeviceARowReaches_GetsNone()
        {
            var keyboard = new LinkRow(A, InputDeviceType.Keyboard, PeripheralLinker.RazerVid, 0x024E, Box, null);
            var held = new DirectUnit(OutputFamily.OpenRgbDevice, "HID: kb", Box, PeripheralLinker.RazerVid, 0x024E);
            Assert.Empty(PeripheralLinker.Orphans(new[] { keyboard }, Inputs(held)));

            // A headset with a LampArray unit and no input row: the LampArray
            // unit gets the row, lit through OpenRGB.
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", Box, 0x1532, 0x0527);
            var headset = new DirectUnit(OutputFamily.OpenRgbDevice, "HID: hs", Box, 0x1532, 0x0527);
            var orphans = PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(lamp, headset));
            Assert.Equal(new[] { lamp.Path }, orphans.Select(u => u.Path));

            // A device row that comes online takes the device from the
            // controller's own row in the same pass.
            var row = UnitRow(held) with { Device = new Guid("00000000-0000-0000-0000-0000000000e8") };
            var table = PeripheralLinker.Build(new[] { row, keyboard }, Inputs(held));
            Assert.Null(table.For(row.Device));
            Assert.Equal(new[] { held.Path }, table.For(A).Lighting);
        }

        [Fact]
        public void AControllerWithNoDirectMode_OrAsleepWithNoRow_GetsNone()
        {
            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Ram() with { NoDirectMode = true })));
            Assert.Empty(PeripheralLinker.Orphans(Array.Empty<LinkRow>(), Inputs(Ram() with { Asleep = true })));
            // An asleep one keeps the row it has.
            var ram = Ram() with { Asleep = true };
            Assert.Single(PeripheralLinker.Orphans(new[] { UnitRow(ram) }, Inputs(ram)));
        }
    }

    public partial class OpenRgbBackendTests
    {
        /// <summary>The worker connects on the port the server listens
        /// on.</summary>
        [Fact]
        public void TheWorker_ConnectsOnTheServersPort()
        {
            _port = 6800;
            using var backend = Started();
            Assert.Equal(2, backend.Snapshot.Units.Count);
        }

        /// <summary>While OpenRGB runs and its server does not answer, the
        /// snapshot says so. Once it answers, or OpenRGB stops, it does
        /// not.</summary>
        [Fact]
        public void TheSnapshot_SaysWhileOpenRgbsServerDoesNotAnswer()
        {
            _server.Down = true;
            using var backend = Started();
            Assert.True(backend.Snapshot.ProgramUnanswered);
            Assert.Empty(backend.Snapshot.Units);

            _server.Down = false;
            Advance(OpenRgbBackend.RetryMs);
            Passes(backend);
            Assert.False(backend.Snapshot.ProgramUnanswered);
            Assert.Equal(2, backend.Snapshot.Units.Count);

            _server.Down = true;
            Passes(backend);
            Assert.True(backend.Snapshot.ProgramUnanswered);
            Assert.All(backend.Snapshot.Units, u => Assert.True(u.Asleep));

            _runs = false;
            Passes(backend);
            Assert.False(backend.Snapshot.ProgramUnanswered);
            Assert.Empty(backend.Snapshot.Units);
        }
    }

    public partial class OpenRgbRouteLineTests
    {
        [Fact]
        public void ARowWaitingForOpenRgbsServer_SaysSo()
        {
            PeripheralOutputs.PublishLinks(new LinkTable(new[]
            {
                new DeviceLinks
                {
                    Device = A, LeftToProgram = "OpenRGB", WaitsForOpenRgbServer = true,
                    TakenByOpenRgb = new[] { new OutputPath(OutputFamily.RazerUnit, "razer") },
                },
            }));
            var ud = new PadForge.Engine.Data.UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Keyboard, VendorId = PeripheralLinker.RazerVid,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            };
            Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.Pad_Lighting_RouteWaiting, "OpenRGB SDK Server"),
                PeripheralRouteText.Lighting(ud, 0));
            Assert.Null(PeripheralRouteText.Lighting(ud, 0, lightsHere: false));
        }
    }
}
