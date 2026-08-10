using System;
using System.Threading.Tasks;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Locks the SteamVR install-target refusals (audit round 43).
    /// Both throw BEFORE any download or process launch, so these run
    /// offline. The drive-root refusal exists because the uninstall side
    /// recursively deletes the recorded directory: an install allowed at
    /// "C:\" would arm Directory.Delete on the whole drive.
    /// The stop-after-guards seam keeps a REGRESSED guard inert: without
    /// it these tests execute the real installer when a guard is removed,
    /// which round 43's mutation run demonstrated by launching a live
    /// steamcmd toward C:\. With the seam a regression still reddens the
    /// test via the wrong exception type.</summary>
    public class SteamVrInstallGuardTests : IDisposable
    {
        public SteamVrInstallGuardTests()
            => DriverInstaller.SteamVrInstallStopAfterGuards = true;

        public void Dispose()
            => DriverInstaller.SteamVrInstallStopAfterGuards = false;

        [Theory]
        [InlineData(@"C:\")]
        [InlineData(@"C:")]
        [InlineData(@"D:\")]
        [InlineData(@"c:/")]
        public async Task Install_RefusesDriveRoot(string root)
            => await Assert.ThrowsAsync<ArgumentException>(
                () => DriverInstaller.InstallSteamVRAsync(root));

        [Theory]
        [InlineData("SteamVR")]
        [InlineData(@"relative\path")]
        public async Task Install_RefusesRelativePath(string relative)
            => await Assert.ThrowsAsync<ArgumentException>(
                () => DriverInstaller.InstallSteamVRAsync(relative));
    }
}
