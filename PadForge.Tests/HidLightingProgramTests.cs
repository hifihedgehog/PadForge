using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The programs a family of the shared HID lighting worker yields to
    /// (#557): the table, read from Lenovo Legion Toolkit's lists, and how
    /// the presence finds them in the one process snapshot it takes.
    /// </summary>
    public class VendorProgramTests
    {
        private static readonly PeripheralPresence Installed = new(true, false, true, true, true);

        /// <summary>The Toolkit's lists, written out from
        /// LenovoLegionToolkit-Team LenovoLegionToolkit.Lib/SoftwareDisabler:
        /// VantageDisabler.cs:26-34, LegionSpaceDisabler.cs:14-22 and
        /// LegionZoneDisabler.cs:17-35.</summary>
        [Fact]
        public void TheLenovoPrograms_AreTheToolkitsLists()
        {
            Assert.Equal("Lenovo Vantage", VendorPrograms.LenovoVantage.Name);
            Assert.Equal(new[] { "BGHelper", "Lenovo.Modern.ImController", "Lenovo.Vantage", "LenovoVantage", "QSHelper", "ScheduleEventAction" },
                VendorPrograms.LenovoVantage.Processes);
            Assert.Equal("Legion Space", VendorPrograms.LegionSpace.Name);
            Assert.Equal(new[] { "Bino3D", "LegionGameWidget", "LegionSpace", "LegionSpaceComponent", "LegionSpaceToast", "LSDaemon" },
                VendorPrograms.LegionSpace.Processes);
            Assert.Equal("Legion Zone", VendorPrograms.LegionZone.Name);
            Assert.Equal(new[]
            {
                "BorderlessSpace", "ContextMenuInstaller", "DoudouAI", "LegionZone", "LZAgent", "LZInstall", "LZMain",
                "lzolhelp64", "LZService", "LZStrategy", "LZTray", "LZUpdate", "NvOcScanner", "StreamingDiagnosis",
                "StreamingHost", "StreamingService",
            }, VendorPrograms.LegionZone.Processes);

            // The Spectrum page stands down for the three
            // (SpectrumKeyboardBacklightControl.xaml.cs:352-364), and the
            // presence looks for every program a family names.
            Assert.Equal(new[] { VendorPrograms.LenovoVantage, VendorPrograms.LegionSpace, VendorPrograms.LegionZone },
                VendorPrograms.LenovoSpectrum);
            Assert.All(VendorPrograms.LenovoSpectrum, p => Assert.Contains(p, VendorPrograms.All));
        }

        /// <summary>A process counts when its name starts with one of the
        /// program's names, in any case, the Toolkit's StartsWith
        /// (AbstractSoftwareDisabler.cs:210-230).</summary>
        [Fact]
        public void AProgramOwnsAProcess_WhoseNameStartsWithOneOfItsNames_InAnyCase()
        {
            var vantage = VendorPrograms.LenovoVantage;
            Assert.True(vantage.Owns("LenovoVantage"));
            Assert.True(vantage.Owns("lenovovantageservice"));
            Assert.True(vantage.Owns("Lenovo.Modern.ImController.PluginHost.Device"));
            Assert.True(VendorPrograms.LegionSpace.Owns("LSDAEMON"));
            Assert.True(VendorPrograms.LegionZone.Owns("LZTray"));
            // A name that holds the program's further in, or only part of it.
            Assert.False(vantage.Owns("MyLenovoVantage"));
            Assert.False(vantage.Owns("Lenovo"));
            Assert.False(vantage.Owns(""));
            Assert.False(vantage.Owns(null));
        }

        [Fact]
        public void ThePresence_NamesTheProgramsThatRun_FromTheSameImageNames()
        {
            var none = PeripheralPresence.WithRunning(Installed, new[] { "explorer.exe", "PadForge.exe" });
            Assert.Empty(none.Programs.Programs);

            var seen = PeripheralPresence.WithRunning(Installed,
                new[] { "explorer.exe", "LZTray.EXE", "Lenovo.Modern.ImController.exe" });
            // In the order of the table, whatever order they run in.
            Assert.Equal(new[] { VendorPrograms.LenovoVantage, VendorPrograms.LegionZone }, seen.Programs.Programs);
            Assert.False(seen.RazerSoftwareRunning || seen.SteelSeriesGGRunning || seen.LogitechGHubRunning);

            // Lenovo Legion Toolkit is no program the Gen 10 keyboard is left
            // to, and neither is a name the Toolkit lists for no program.
            Assert.Empty(PeripheralPresence.WithRunning(Installed,
                new[] { "Lenovo Legion Toolkit.exe", "LenovoUtilityService.exe" }).Programs.Programs);

            // Another family's table, as a family lane adds one.
            var other = new VendorProgram("Other", "OtherApp");
            var theirs = PeripheralPresence.WithRunning(Installed, new[] { "otherapp.exe", "LZTray.exe" }, new[] { other });
            Assert.Equal(new[] { other }, theirs.Programs.Programs);
        }

        /// <summary>Two reads that find the same programs are equal, so the
        /// published presence raises no status change.</summary>
        [Fact]
        public void TwoReadsOfTheSamePrograms_AreEqual()
        {
            var a = PeripheralPresence.WithRunning(Installed, new[] { "LSDaemon.exe" });
            var b = PeripheralPresence.WithRunning(Installed, new[] { "LegionSpace.exe", "lsdaemon.exe" });
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, PeripheralPresence.WithRunning(Installed, new[] { "LZTray.exe" }));
            Assert.NotEqual(a, PeripheralPresence.WithRunning(Installed, Array.Empty<string>()));
            Assert.Equal(Installed, PeripheralPresence.WithRunning(Installed, null));
        }

        /// <summary>A snapshot that failed keeps the programs read before
        /// it, as it keeps every running flag, and a scripted presence
        /// copies them.</summary>
        [Fact]
        public void AFailedSnapshot_KeepsTheProgramsLastRead_AndACopyCarriesThem()
        {
            var last = PeripheralPresence.WithRunning(Installed, new[] { "LenovoVantage.exe" });
            var installedNow = new PeripheralPresence(false, false, false, false, false);
            Assert.Equal(last.Programs, PeripheralPresence.WithSnapshot(installedNow, null, last).Programs);
            Assert.Empty(PeripheralPresence.WithSnapshot(installedNow, null, null).Programs.Programs);
            Assert.Empty(PeripheralPresence.WithSnapshot(installedNow, new[] { "explorer.exe" }, last).Programs.Programs);
            Assert.Equal(last.Programs, PeripheralPresence.CopyRunning(installedNow, last).Programs);
        }

        [Fact]
        public void TheFirstOfAListThatRuns_IsTheOneNamed()
        {
            var running = RunningPrograms.Of(new[] { "LZTray", "LSDaemon" }, VendorPrograms.All);
            Assert.Same(VendorPrograms.LegionSpace, running.FirstOf(VendorPrograms.LenovoSpectrum));
            Assert.Same(VendorPrograms.LegionZone, running.FirstOf(new[] { VendorPrograms.LegionZone, VendorPrograms.LegionSpace }));
            Assert.Null(running.FirstOf(new[] { VendorPrograms.LenovoVantage }));
            Assert.Null(running.FirstOf(null));
            Assert.Null(RunningPrograms.None.FirstOf(VendorPrograms.LenovoSpectrum));
        }

        [Fact]
        public void TheLegionGen10Model_NamesTheThreeLenovoPrograms()
        {
            var model = HidLightingFamilies.Match(new HidCollectionInfo
            {
                Path = @"\\?\hid#vid_048d&pid_c197&col02#8&11b458cb&0&0001",
                VendorId = 0x048D, ProductId = 0xC197, UsagePage = 0xFF89, Usage = 0x07, FeatureReportLength = 960,
            });
            Assert.Same(VendorPrograms.LenovoSpectrum, model.Programs);
        }
    }

    /// <summary>The linker leaves a shared-worker device to a program its
    /// model names, for as long as that program runs (#557).</summary>
    public class HidLightingProgramLinkTests
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");

        /// <summary>The owner's Legion as <see cref="HidLightingLinkTests"/>
        /// has it, with the model's programs on the controller's unit.</summary>
        private static (LinkRow Row, DirectUnit Spectrum, DirectUnit Lamp) Legion(bool spectrumAsleep = false)
        {
            var pc = PeripheralLinker.LocalMachineContainer;
            var row = new LinkRow(A, InputDeviceType.Keyboard, 0x048D, 0xC197, pc, null);
            var spectrum = new DirectUnit(OutputFamily.HidLightingUnit, "spectrum", pc, 0x048D, 0xC197)
            {
                LitBesideWindows = true, LiveThroughLampArray = true, Programs = VendorPrograms.LenovoSpectrum,
                Asleep = spectrumAsleep,
            };
            var lamp = new DirectUnit(OutputFamily.LampArrayUnit, "lamp", pc, 0x048D, 0xC193)
            {
                Kind = PeripheralLinker.LampArrayKindKeyboard,
            };
            return (row, spectrum, lamp);
        }

        private static LinkInputs Inputs(IEnumerable<string> running, params DirectUnit[] units)
            => new(HidppSnapshot.Empty,
                PeripheralPresence.WithRunning(new PeripheralPresence(false, false, false, false, false), running))
            {
                Units = units,
            };

        [Fact]
        public void WhileALenovoProgramRuns_TheRowHoldsNoPath_KeepsItsTab_AndNamesTheProgram()
        {
            var (row, spectrum, lamp) = Legion();
            var inputs = Inputs(new[] { "LegionSpace.exe" }, spectrum, lamp);
            var links = PeripheralLinker.Build(new[] { row }, inputs).For(A);
            Assert.NotNull(links);
            // Neither the controller nor the LampArray interface: the
            // program has the whole device.
            Assert.Empty(links.Lighting);
            Assert.Equal("Legion Space", links.LeftToProgram);
            Assert.Equal(PeripheralOutputKinds.Lighting, links.Kinds);
            Assert.True(links.HasVendorProgram);
            Assert.False(links.DirectWhenClosed);
            // The row keeps its Lighting tab on its record.
            Assert.Equal((int)PeripheralOutputKinds.Lighting, PeripheralLinker.Capabilities(0, links, stillLooking: false));
            // The LampArray interface is still reached by the keyboard row.
            Assert.Empty(PeripheralLinker.Orphans(new[] { row }, inputs));
        }

        [Fact]
        public void OnceTheProgramStops_TheRowIsLitAsBefore_AndStillWatchedFor()
        {
            var (row, spectrum, lamp) = Legion();
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "explorer.exe" }, spectrum, lamp)).For(A);
            Assert.Equal(new[] { spectrum.Path, lamp.Path }, links.Lighting);
            Assert.Null(links.LeftToProgram);
            // A program the model names can start, so a claim here is
            // watched for one, on a vendor ID no vendor rule knows.
            Assert.True(links.HasVendorProgram);
            Assert.False(PeripheralLinker.HasVendorProgram(0x048D));
        }

        [Fact]
        public void AProgramTheModelDoesNotName_ChangesNothing()
        {
            var (row, spectrum, lamp) = Legion();
            var other = new VendorProgram("Other", "OtherApp");
            var presence = new PeripheralPresence(false, false, false, false, false)
            {
                Programs = RunningPrograms.Of(new[] { "OtherApp" }, new[] { other }),
            };
            var links = PeripheralLinker.Build(new[] { row },
                new LinkInputs(HidppSnapshot.Empty, presence) { Units = new[] { spectrum, lamp } }).For(A);
            Assert.Equal(new[] { spectrum.Path, lamp.Path }, links.Lighting);
            Assert.Null(links.LeftToProgram);
        }

        /// <summary>The program has the device whether or not the device
        /// answers now.</summary>
        [Fact]
        public void AnAsleepUnit_IsLeftToTheProgramToo()
        {
            var (row, spectrum, lamp) = Legion(spectrumAsleep: true);
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LenovoVantage.exe" }, spectrum, lamp)).For(A);
            Assert.Equal("Lenovo Vantage", links.LeftToProgram);
            Assert.Empty(links.Lighting);
        }

        [Fact]
        public void AUnitOfAModelWithNoPrograms_IsNeverLeftToOne_NorWatchedFor()
        {
            var (row, spectrum, lamp) = Legion();
            var bare = spectrum with { Programs = Array.Empty<VendorProgram>() };
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LenovoVantage.exe" }, bare, lamp)).For(A);
            Assert.Equal(new[] { bare.Path, lamp.Path }, links.Lighting);
            Assert.Null(links.LeftToProgram);
            Assert.False(links.HasVendorProgram);
        }

        /// <summary>A unit of another device in the same container, the
        /// laptop's own, names its programs for its own rows alone.</summary>
        [Fact]
        public void AnotherDevicesUnit_DoesNotLeaveThisRowToItsProgram()
        {
            var (row, spectrum, lamp) = Legion();
            var neighbor = spectrum with { Key = "other", ProductId = 0xC195 };
            var bare = spectrum with { Programs = Array.Empty<VendorProgram>() };
            var links = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LenovoVantage.exe" }, bare, neighbor, lamp)).For(A);
            Assert.Null(links.LeftToProgram);
            Assert.Contains(bare.Path, links.Lighting);
        }

        [Fact]
        public void ATableThatDiffersOnlyInTheProgram_IsAChange()
        {
            var (row, spectrum, lamp) = Legion();
            var vantage = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LenovoVantage.exe" }, spectrum, lamp));
            var zone = PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LZTray.exe" }, spectrum, lamp));
            Assert.False(vantage.SameAs(zone));
            Assert.True(vantage.SameAs(PeripheralLinker.Build(new[] { row }, Inputs(new[] { "LenovoVantage.exe" }, spectrum, lamp))));
        }

        [Fact]
        public void TheUnitsSnapshot_CarriesTheModelsList_SoTwoSnapshotsStayEqual()
        {
            var (_, spectrum, _) = Legion();
            Assert.Equal(spectrum, spectrum with { Programs = VendorPrograms.LenovoSpectrum });
            Assert.Empty(new DirectUnit(OutputFamily.RazerUnit, "r", Guid.Empty, 1, 2).Programs);
        }
    }

    /// <summary>The program check where the claims and the tab's line meet
    /// it (#557).</summary>
    [Collection("PeripheralOutputStatics")]
    public class HidLightingProgramFacadeTests : IDisposable
    {
        private static readonly Guid A = new("00000000-0000-0000-0000-0000000000a1");

        public HidLightingProgramFacadeTests()
        {
            PeripheralOutputs.ResetForTest();
            PeripheralOutputs.SetRunning(true);
        }

        public void Dispose() => PeripheralOutputs.ResetForTest();

        private static void Link(params DeviceLinks[] rows) => PeripheralOutputs.PublishLinks(new LinkTable(rows));

        [Fact]
        public void AClaimOnASharedWorkerUnit_IsWatchedFor_WhereItsModelNamesAProgram()
        {
            var unit = new OutputPath(OutputFamily.HidLightingUnit, "spectrum");
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit }, HasVendorProgram = true });
            PeripheralOutputs.SetLighting(A, 0, 1, 1, 1, 1);
            Assert.True(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            Assert.True(PeripheralOutputs.WatchesVendorPrograms(Environment.TickCount64));
            // A model with no program: nothing can take the path.
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit } });
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
            // No claim on it.
            Link(new DeviceLinks { Device = A, Lighting = new[] { unit }, HasVendorProgram = true });
            PeripheralOutputs.ReleaseLighting(A, 0);
            Assert.False(PeripheralOutputs.AnyClaimAVendorProgramTakes());
        }

        [Fact]
        public void TheLine_NamesTheProgramTheDeviceIsLeftTo()
        {
            var s = Strings.Instance;
            var keyboard = new UserDevice
            {
                InstanceGuid = A, IsOnline = true, CapType = InputDeviceType.Keyboard, VendorId = 0x048D,
                PeripheralOutputs = (int)PeripheralOutputKinds.Lighting,
            };
            Link(new DeviceLinks { Device = A, LeftToProgram = "Lenovo Vantage", HasVendorProgram = true });
            string line = PeripheralRouteText.Lighting(keyboard, 0);
            Assert.Equal(string.Format(s.Pad_Lighting_RouteThroughOrDirect, "Lenovo Vantage"), line);
            Assert.DoesNotContain("{0}", line);
            // A controller that leaves the device alone names no route.
            Assert.Null(PeripheralRouteText.Lighting(keyboard, 0, lightsHere: false));
            // Nor does a stopped engine.
            PeripheralOutputs.SetRunning(false);
            Assert.Null(PeripheralRouteText.Lighting(keyboard, 0));
        }
    }
}
