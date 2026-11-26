using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Settings row's BthPS3 answer on the driver status timer's 5 s
    /// tick. The driver database holds every driver package on the PC (719
    /// on the bench), so it is read once and again only after Windows
    /// reports a device change or PadForge installs or removes BthPS3. The
    /// two service reads run on every tick.
    /// </summary>
    public class BthPs3StatusReadTests
    {
        [Fact]
        public void ATickWithNoChange_ReadsNoDriverDatabase()
        {
            int reads = 0, changes = 0;
            var r = new Ds3DriverInstaller.BthPs3Reads
            {
                ServiceInstalled = name => name == "BthPS3",
                PackageFolders = () => { reads++; return new[] { "bthps3.inf_amd64_0123456789abcdef", "other.inf_amd64_0" }; },
                DeviceChanges = () => changes,
            };

            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
            Assert.Equal(1, reads);

            changes++;
            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
            Assert.Equal(2, reads);

            r.Forget();
            Assert.True(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
            Assert.Equal(3, reads);
        }

        /// <summary>A PC without a BthPS3 service reads no driver database
        /// at all.</summary>
        [Fact]
        public void NoService_ReadsNoDriverDatabase()
        {
            int reads = 0;
            var r = new Ds3DriverInstaller.BthPs3Reads
            {
                ServiceInstalled = _ => false,
                PackageFolders = () => { reads++; return new string[0]; },
                DeviceChanges = () => 0,
            };
            Assert.False(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
            Assert.Equal(0, reads);
        }

        /// <summary>A service left with no package is no install, through the cached read too.</summary>
        [Fact]
        public void AServiceWithoutItsPackage_IsNoInstall()
        {
            var r = new Ds3DriverInstaller.BthPs3Reads
            {
                ServiceInstalled = _ => true,
                PackageFolders = () => new[] { "dshidmini.inf_amd64_0" },
                DeviceChanges = () => 0,
            };
            Assert.False(Ds3DriverInstaller.IsAnyBthPs3DriverInstalled(r));
        }
    }
}
