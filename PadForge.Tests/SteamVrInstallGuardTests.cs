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
    /// "C:\" would arm Directory.Delete on the whole drive.</summary>
    public class SteamVrInstallGuardTests
    {
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
