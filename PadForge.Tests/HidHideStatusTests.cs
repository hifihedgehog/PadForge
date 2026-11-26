using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Whether HidHide is installed, and its version, as the Settings card,
    /// the assistant's drivers entry and a recording read them
    /// (DriverInstaller.TryGetHidHideStatus). One definition on both
    /// machines: the service, the HID class filter and the root\HidHide
    /// device node, which upstream's MSI installs on x64
    /// (HidHide-upstream Installer/Program.cs, OnAfterInstall: nefcon
    /// install of root\HidHide and the class filters) and its manual install
    /// leaves on ARM64. The Windows Installer entry gives the version alone.
    /// Every read is a seam here, so nothing on this PC is read.
    /// </summary>
    public class HidHideStatusTests
    {
        private sealed class Counted
        {
            public int Enumerations;
            public int Changes;
        }

        private static DriverInstaller.HidHideReads Reads(bool entry, string entryVersion,
            bool service, bool filter, bool node, string driverVersion, Counted counted = null)
        {
            counted ??= new Counted();
            return new DriverInstaller.HidHideReads
            {
                MsiEntry = () => (entry, entryVersion),
                ServiceRegistered = () => service,
                HidClassFiltered = () => filter,
                NodePresent = () => { counted.Enumerations++; return node; },
                DriverVersion = () => driverVersion,
                DeviceChanges = () => counted.Changes,
            };
        }

        /// <summary>A registration with no DisplayVersion over an installed
        /// driver whose file gives no version reads as installed with no
        /// version. The English word "Installed" stood in the version's
        /// place and reached the card in every language.</summary>
        [Fact]
        public void AnEntryWithNoVersion_GivesNoVersion()
        {
            Assert.True(DriverInstaller.TryGetHidHideStatus(
                Reads(entry: true, entryVersion: null, service: true, filter: true, node: true, driverVersion: null),
                out string version));
            Assert.Null(version);
        }

        /// <summary>An Uninstall entry over no driver service is no install,
        /// on x64 as on ARM64: the driver went by hand, or the MSI's removal
        /// hook failed and left the entry.</summary>
        [Fact]
        public void AnEntryWithNoService_IsNotInstalled()
        {
            Assert.False(DriverInstaller.TryGetHidHideStatus(
                Reads(entry: true, entryVersion: "1.5.230", service: false, filter: false, node: false, driverVersion: null),
                out string version));
            Assert.Null(version);
        }

        /// <summary>A driver with its service, filter and node and no
        /// Uninstall entry is installed, on x64 as on ARM64, with the driver
        /// file's version.</summary>
        [Fact]
        public void TheDriverWithNoEntry_IsInstalled_WithTheFilesVersion()
        {
            Assert.True(DriverInstaller.TryGetHidHideStatus(
                Reads(entry: false, entryVersion: null, service: true, filter: true, node: true, driverVersion: "1.5.230.0"),
                out string version));
            Assert.Equal("1.5.230.0", version);
        }

        /// <summary>The entry's version wins over the file's where there is
        /// one.</summary>
        [Fact]
        public void TheEntrysVersion_IsTheVersion()
        {
            Assert.True(DriverInstaller.TryGetHidHideStatus(
                Reads(entry: true, entryVersion: "1.5.230", service: true, filter: true, node: true, driverVersion: "1.5.230.0"),
                out string version));
            Assert.Equal("1.5.230", version);
        }

        /// <summary>The device node is read again only after Windows reports
        /// a device change or PadForge installs or removes HidHide. The
        /// 5 s status timer used to enumerate every present System-class
        /// device on each tick, on the interface thread.</summary>
        [Fact]
        public void TheDeviceNode_IsEnumeratedOnce_UntilADeviceChanges()
        {
            var counted = new Counted();
            var reads = Reads(entry: false, entryVersion: null, service: true, filter: true, node: true, driverVersion: "1.6.280.0", counted);

            Assert.True(DriverInstaller.TryGetHidHideStatus(reads, out _));
            Assert.True(DriverInstaller.TryGetHidHideStatus(reads, out _));
            Assert.Equal(1, counted.Enumerations);

            counted.Changes++;
            Assert.True(DriverInstaller.TryGetHidHideStatus(reads, out _));
            Assert.Equal(2, counted.Enumerations);

            reads.Forget();
            Assert.True(DriverInstaller.TryGetHidHideStatus(reads, out _));
            Assert.Equal(3, counted.Enumerations);
        }
    }
}
