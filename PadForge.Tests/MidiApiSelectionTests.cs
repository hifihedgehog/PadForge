using System;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Which Windows MIDI Services API PadForge drives. Microsoft deleted the
    /// App SDK runtime installers on 2026-10-01, and the API moves into
    /// Windows as Windows.Devices.Midi2 for Windows 11 25H2 (build 26200)
    /// from the late-November 2026 update. PadForge tries the in-box API
    /// first and falls back to the older runtime only when the in-box classes
    /// are not registered (REGDB_E_CLASSNOTREG) and the runtime is installed.
    /// Activation is faked here: the rule takes the activation HRESULT.
    /// </summary>
    public class MidiApiSelectionTests
    {
        private const int S_OK = 0;
        private const int ClassNotRegistered = unchecked((int)0x80040154);
        private const int AccessDenied = unchecked((int)0x80070005);
        private const int Build25H2 = 26200;
        private const int Build24H2 = 26100;
        private const int Build23H2 = 22631;

        [Fact]
        public void TheInBoxApi_WinsOn25H2_EvenWithTheOlderRuntimeInstalled()
        {
            Assert.Equal(MidiApiKind.InBox,
                MidiApiSelection.Choose(Build25H2, () => S_OK, () => true));
            Assert.Equal(MidiApiKind.InBox,
                MidiApiSelection.Choose(Build25H2, () => S_OK, () => false));
        }

        [Fact]
        public void ClassNotRegistered_FallsBackToTheOlderRuntime_WhenItIsInstalled()
        {
            Assert.Equal(MidiApiKind.AppSdk,
                MidiApiSelection.Choose(Build25H2, () => ClassNotRegistered, () => true));
        }

        [Fact]
        public void ClassNotRegistered_WithNoOlderRuntime_LeavesNoApi()
        {
            Assert.Equal(MidiApiKind.None,
                MidiApiSelection.Choose(Build25H2, () => ClassNotRegistered, () => false));
        }

        /// <summary>Only an unregistered class means "not in Windows". A
        /// registered in-box API that fails to activate for another reason is
        /// not swapped for the older runtime behind the owner's back.</summary>
        [Fact]
        public void AnotherActivationFailure_DoesNotFallBack()
        {
            Assert.Equal(MidiApiKind.None,
                MidiApiSelection.Choose(Build25H2, () => AccessDenied, () => true));
        }

        /// <summary>24H2 is not covered by the in-box API, so it is never
        /// tried there, and the older runtime is the only path.</summary>
        [Fact]
        public void On24H2_TheInBoxApiIsNeverTried()
        {
            bool probed = false;
            Assert.Equal(MidiApiKind.AppSdk,
                MidiApiSelection.Choose(Build24H2, () => { probed = true; return S_OK; }, () => true));
            Assert.False(probed);
            Assert.Equal(MidiApiKind.None,
                MidiApiSelection.Choose(Build24H2, () => { probed = true; return S_OK; }, () => false));
            Assert.False(probed);
        }

        [Fact]
        public void BelowTheOlderRuntimesGate_NothingIsTried()
        {
            bool probed = false, asked = false;
            Assert.Equal(MidiApiKind.None,
                MidiApiSelection.Choose(Build23H2, () => { probed = true; return S_OK; }, () => { asked = true; return true; }));
            Assert.False(probed);
            Assert.False(asked);
        }

        /// <summary>The Settings card and the type pickers read registration
        /// from the registry on the UI thread, and must agree with the
        /// engine's activation rule case for case.</summary>
        [Theory]
        [InlineData(Build25H2, true, true, (int)MidiApiKind.InBox)]
        [InlineData(Build25H2, true, false, (int)MidiApiKind.InBox)]
        [InlineData(Build25H2, false, true, (int)MidiApiKind.AppSdk)]
        [InlineData(Build25H2, false, false, (int)MidiApiKind.None)]
        [InlineData(Build24H2, true, true, (int)MidiApiKind.AppSdk)]
        [InlineData(Build24H2, false, false, (int)MidiApiKind.None)]
        [InlineData(Build23H2, true, true, (int)MidiApiKind.None)]
        public void TheUiPrediction_FollowsTheSameRule(int build, bool inBoxRegistered, bool runtimeInstalled, int expected)
        {
            Assert.Equal((MidiApiKind)expected, MidiApiSelection.PredictForUi(build, inBoxRegistered, runtimeInstalled));
        }

        /// <summary>The real probe, through combase: a class every Windows
        /// registers activates (the positive control), and a class no
        /// Windows registers reads as REGDB_E_CLASSNOTREG, the one result
        /// that lets the older runtime take over.</summary>
        [Fact]
        public void TheActivationProbe_TellsARegisteredClassFromAMissingOne()
        {
            Assert.Equal(S_OK, MidiApiSelection.ProbeActivation("Windows.Foundation.Uri"));
            Assert.Equal(ClassNotRegistered,
                MidiApiSelection.ProbeActivation("Windows.Devices.Midi2.PadForgeTestClassThatDoesNotExist"));
        }

        // ── The Windows MIDI Services card ──

        private static SettingsViewModel Card(MidiApiKind api, bool runtimeInstalled, string version = "",
            bool midiSlots = false)
        {
            var vm = new SettingsViewModel
            {
                HasAnyMidiSlots = () => midiSlots,
            };
            vm.ActiveMidiApi = api;
            vm.IsMidiRuntimeInstalled = runtimeInstalled;
            vm.MidiRuntimeVersion = version;
            return vm;
        }

        [Fact]
        public void TheCard_NamesTheInBoxApi_AndOffersNoUninstall()
        {
            var vm = Card(MidiApiKind.InBox, runtimeInstalled: false);
            Assert.True(vm.IsMidiAvailable);
            Assert.Equal(Strings.Instance.Settings_MidiStatusInBox, vm.MidiServicesStatusText);
            Assert.Equal(string.Empty, vm.MidiServicesDetailText);
            Assert.False(vm.UninstallMidiServicesCommand.CanExecute(null));
        }

        [Fact]
        public void TheCard_NamesTheOlderRuntime_WithItsVersion_AndOffersUninstall()
        {
            var vm = Card(MidiApiKind.AppSdk, runtimeInstalled: true, version: "1.0.17-rc.4.25");
            Assert.True(vm.IsMidiAvailable);
            Assert.Equal(Strings.Instance.Settings_MidiStatusRuntime, vm.MidiServicesStatusText);
            Assert.Equal("1.0.17-rc.4.25", vm.MidiServicesDetailText);
            Assert.True(vm.UninstallMidiServicesCommand.CanExecute(null));
        }

        /// <summary>The uninstall guard: a MIDI slot running on the older
        /// runtime keeps it installed.</summary>
        [Fact]
        public void TheCard_KeepsTheOlderRuntime_WhileAMidiSlotRunsOnIt()
        {
            var vm = Card(MidiApiKind.AppSdk, runtimeInstalled: true, version: "1.0.17-rc.4.25", midiSlots: true);
            Assert.False(vm.UninstallMidiServicesCommand.CanExecute(null));
        }

        /// <summary>After the November update a PC can carry both. Windows
        /// runs the MIDI slots, so the older runtime can go even while they
        /// run, and the card says it is no longer needed.</summary>
        [Fact]
        public void TheCard_OffersToRemoveALeftoverRuntime_WhenWindowsHasTheApi()
        {
            var vm = Card(MidiApiKind.InBox, runtimeInstalled: true, version: "1.0.17-rc.4.25", midiSlots: true);
            Assert.Equal(Strings.Instance.Settings_MidiStatusInBox, vm.MidiServicesStatusText);
            Assert.Equal(string.Format(Strings.Instance.Settings_MidiOlderRuntime_Format, "1.0.17-rc.4.25"),
                vm.MidiServicesDetailText);
            Assert.True(vm.UninstallMidiServicesCommand.CanExecute(null));
        }

        [Fact]
        public void TheCard_SaysWhatThePcLacks_WhenNeitherApiIsThere()
        {
            var vm = Card(MidiApiKind.None, runtimeInstalled: false);
            Assert.False(vm.IsMidiAvailable);
            Assert.Equal(Strings.Instance.Settings_MidiStatusUnavailable, vm.MidiServicesStatusText);
            Assert.Equal(Strings.Instance.Settings_MidiNeedsUpdate, vm.MidiServicesDetailText);
            Assert.False(vm.UninstallMidiServicesCommand.CanExecute(null));
        }

        /// <summary>MidiApi.EnsureServiceAvailable returns false in Legacy
        /// API mode (Microsoft's MidiApi reference). A PC whose API is
        /// present but whose service did not start says so, instead of
        /// naming an API that nothing can use or asking for an update it
        /// already has.</summary>
        [Fact]
        public void TheCard_SaysTheServiceDidNotStart_WhenTheApiIsThereButStopped()
        {
            var vm = Card(MidiApiKind.None, runtimeInstalled: false);
            vm.MidiApiNotStarted = true;
            Assert.False(vm.IsMidiAvailable);
            Assert.Equal(Strings.Instance.Settings_MidiStatusNotRunning, vm.MidiServicesStatusText);
            Assert.Equal(Strings.Instance.Settings_MidiNotRunning, vm.MidiServicesDetailText);
        }

        /// <summary>The card shows the registry's prediction until the
        /// engine's probe has run, then the probe's answer. A failed probe
        /// for an API the registry says is present reads as not started. A
        /// failed probe with no API present still says what the PC
        /// lacks.</summary>
        [Theory]
        [InlineData((int)MidiApiKind.InBox, (int)MidiApiKind.None, false, (int)MidiApiKind.InBox, false)]
        [InlineData((int)MidiApiKind.AppSdk, (int)MidiApiKind.None, false, (int)MidiApiKind.AppSdk, false)]
        [InlineData((int)MidiApiKind.None, (int)MidiApiKind.None, false, (int)MidiApiKind.None, false)]
        [InlineData((int)MidiApiKind.InBox, (int)MidiApiKind.InBox, false, (int)MidiApiKind.InBox, false)]
        [InlineData((int)MidiApiKind.AppSdk, (int)MidiApiKind.AppSdk, false, (int)MidiApiKind.AppSdk, false)]
        [InlineData((int)MidiApiKind.InBox, (int)MidiApiKind.None, true, (int)MidiApiKind.None, true)]
        [InlineData((int)MidiApiKind.AppSdk, (int)MidiApiKind.None, true, (int)MidiApiKind.None, true)]
        [InlineData((int)MidiApiKind.None, (int)MidiApiKind.None, true, (int)MidiApiKind.None, false)]
        public void TheCard_TakesTheEnginesAnswer_OnceItHasProbed(int predicted, int engineActive, bool probeFailed,
            int expectedApi, bool expectedNotStarted)
        {
            var (api, notStarted) = MidiApiSelection.ForCard((MidiApiKind)predicted, (MidiApiKind)engineActive, probeFailed);
            Assert.Equal((MidiApiKind)expectedApi, api);
            Assert.Equal(expectedNotStarted, notStarted);
        }
    }
}
