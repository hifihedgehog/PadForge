using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// BthPS3 counts as installed only while its packages are in the driver
    /// database. BthPS3's own removal deletes the three packages and leaves
    /// the two services and their binaries (BthPS3Installer/CustomActions.cs
    /// UninstallDrivers at 1bdde12, DriverStore.RemoveDriver in
    /// Nefarius.Utilities.DeviceManagement v6.1.0 src/Drivers/DriverStore.cs:267-275),
    /// so a service alone is no install. Under HKCU, so nothing system-wide
    /// is read or touched.
    /// </summary>
    public sealed class BthPs3PackageTests : IDisposable
    {
        private const string TestRoot = @"Software\PadForgeTests\BthPs3Packages";
        private readonly RegistryKey _services;
        private readonly RegistryKey _packages;

        public BthPs3PackageTests()
        {
            Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
            _services = Registry.CurrentUser.CreateSubKey(TestRoot + @"\Services", writable: true);
            _packages = Registry.CurrentUser.CreateSubKey(TestRoot + @"\DriverPackages", writable: true);
        }

        public void Dispose()
        {
            _services?.Dispose();
            _packages?.Dispose();
            try { Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false); }
            catch { }
        }

        private void Service(string name)
        {
            using var k = _services.CreateSubKey(name, writable: true);
            k.SetValue("ImagePath", @"\SystemRoot\System32\drivers\" + name + ".sys", RegistryValueKind.ExpandString);
        }

        private void Package(string folder) => _packages.CreateSubKey(folder, writable: true).Dispose();

        /// <summary>The services BthPS3's removal leaves, with no package:
        /// a pairing takes the full install, whose last step advertises the
        /// profile service again, and the Settings row reads not
        /// installed.</summary>
        [Fact]
        public void AServiceLeftWithoutItsPackage_IsNoInstall()
        {
            Service("BthPS3");
            Service("BthPS3PSM");
            Package("dshidmini.inf_amd64_0123456789abcdef");
            Assert.False(Ds3DriverInstaller.IsProfileDriverInstalled(_services, _packages));
            Assert.False(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(_services, _packages));
        }

        [Fact]
        public void AServiceWithItsPackage_IsInstalled()
        {
            Service("BthPS3");
            Package("bthps3.inf_amd64_0123456789abcdef");
            Assert.True(Ds3DriverInstaller.IsProfileDriverInstalled(_services, _packages));
            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(_services, _packages));
        }

        [Fact]
        public void ThePsmFilterWithItsPackage_IsAnInstalledBthPs3Driver()
        {
            Service("BthPS3PSM");
            Package("bthps3psm.inf_amd64_0123456789abcdef");
            Assert.False(Ds3DriverInstaller.IsProfileDriverInstalled(_services, _packages));
            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(_services, _packages));
        }
    }

    /// <summary>A start with no PadForge.xml is a fresh install, which has
    /// no legacy driver of PadForge v2 to clean up, so the cleanup offer is
    /// marked shown. A vJoy the user installed for another program was
    /// offered for removal on a first start.</summary>
    [Collection("SettingsManagerStatics")]
    public class FreshInstallCleanupOfferTests
    {
        [Fact]
        public void AStartWithNoSettingsFile_MarksTheLegacyCleanupOffered()
        {
            var profiles = SettingsManager.Profiles;
            var active = SettingsManager.ActiveProfileId;
            var pending = SettingsManager.PendingDefaultSnapshot;
            try
            {
                SettingsManager.Profiles = new List<ProfileData>();
                var vm = new MainViewModel();
                var settings = new SettingsService(vm);
                string none = Path.Combine(Path.GetTempPath(), "PadForge_NoSettings_" + Guid.NewGuid().ToString("N"), "PadForge.xml");
                settings.LoadOrStartFresh(none);
                Assert.True(vm.Settings.LegacyDriverCleanupOffered);
            }
            finally
            {
                SettingsManager.Profiles = profiles;
                SettingsManager.ActiveProfileId = active;
                SettingsManager.PendingDefaultSnapshot = pending;
            }
        }
    }

    /// <summary>The legacy vJoy removal deletes vJoy's own Uninstall entry
    /// (Inno's {AppId}_is1 of vJoy's scripts, or the AppName "vJoy Device
    /// Driver") and no other program's whose name holds vJoy.</summary>
    public class VJoyUninstallEntryTests
    {
        [Theory]
        [InlineData("{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1", null, true)]
        [InlineData("{8e31f76f-74c3-47f1-9550-e041eedc5fbb}_is1", "anything", true)]
        [InlineData("{01234567-0000-0000-0000-000000000000}_is1", "vJoy Device Driver", true)]
        [InlineData("{01234567-0000-0000-0000-000000000000}_is1", "vJoy Device Driver 2.2.2.0", true)]
        [InlineData("vJoySerialFeeder", "vJoySerialFeeder", false)]
        [InlineData("{11111111-0000-0000-0000-000000000000}", "MouseToVJoy", false)]
        [InlineData("{22222222-0000-0000-0000-000000000000}", "vJoy Device Driverless Tool", false)]
        public void OnlyVJoysOwnEntry_IsDeleted(string key, string name, bool own)
            => Assert.Equal(own, DriverInstaller.IsVJoysOwnUninstallEntry(key, name));
    }

    /// <summary>Microsoft's MIDI bundle is found by the UpgradeCode Burn
    /// writes to its entry (BundleUpgradeCode, 7afb7a7b-86d4-42d7-8cb5-08f65ddcb1a9
    /// at every tag of Bundle.wxs in microsoft/MIDI), not by its name: the
    /// bundle was "Windows MIDI Services Runtime" from preview-11 to rc-1
    /// and "Windows MIDI Services Runtime and Tools" from rc-2.</summary>
    public class MidiBundleLookupTests
    {
        [Theory]
        [InlineData("Windows MIDI Services Runtime")]
        [InlineData("Windows MIDI Services Runtime and Tools")]
        public void TheBundle_IsFoundByItsUpgradeCode_UnderEitherName(string name)
        {
            var entries = new[]
            {
                new UninstallRegistry.Entry("{5CC7ACEA-BD18-4CD6-A6C8-510848601B9F}", name, "1.0.15",
                    "\"C:\\ProgramData\\Package Cache\\{5CC7ACEA-BD18-4CD6-A6C8-510848601B9F}\\WindowsMidiServicesRuntime.exe\" /uninstall",
                    null, "{7AFB7A7B-86D4-42D7-8CB5-08F65DDCB1A9}"),
            };
            var found = DriverInstaller.FindMidiRuntime(entries,
                code => code == DriverInstaller.MicrosoftMidiRuntimeUpgradeCode ? "{11111111-2222-3333-4444-555555555555}" : null);
            Assert.Equal(DriverInstaller.MidiRuntimeOwner.MicrosoftBundle, found.Owner);
        }

        [Fact]
        public void AnEntryNamedLikeTheBundle_WithAnotherUpgradeCode_IsNotTheBundle()
        {
            var entries = new[]
            {
                new UninstallRegistry.Entry("{00000000-0000-0000-0000-000000000001}", "Windows MIDI Services Runtime and Tools", "9",
                    "x.exe /uninstall", null, "{00000000-0000-0000-0000-000000000002}"),
            };
            var found = DriverInstaller.FindMidiRuntime(entries, _ => null);
            Assert.Equal(DriverInstaller.MidiRuntimeOwner.None, found.Owner);
        }
    }

    /// <summary>The Arx line keeps a library that was found and did not load
    /// apart from one that is missing, as the Strike7 line does.</summary>
    public class ArxStatusLineTests
    {
        [Theory]
        [InlineData("load")]
        [InlineData("exports")]
        public void AFoundLibraryThatDidNotLoad_IsNotReadAsMissing(string reason)
        {
            var status = KeyboardScreenHost.ArxStatusFrom(Architecture.X64, reason, active: false, onPhone: false);
            Assert.NotEqual(KeyboardScreenRoute.ArxNoLibrary, status.Route);
        }

        [Theory]
        [InlineData("missing")]
        [InlineData("gate")]
        public void NoLibrary_IsReadAsMissing(string reason)
            => Assert.Equal(KeyboardScreenRoute.ArxNoLibrary,
                KeyboardScreenHost.ArxStatusFrom(Architecture.X64, reason, active: false, onPhone: false).Route);
    }
}
