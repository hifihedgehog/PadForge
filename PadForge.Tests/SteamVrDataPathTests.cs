using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Valve's tooling writes SteamVR's config and log directories as
    /// SIBLINGS of the runtime, appending "-config" and "-logs". At the
    /// default install that puts two folders at the root of the system
    /// drive, and every OpenVR process reads them from that one file, so a
    /// per-process environment override moves only PadForge's own copy.
    ///
    /// <para>PadForge therefore rewrites those two entries to sit inside the
    /// install. The REFUSALS are what these pin: an install PadForge did not
    /// make is never touched, because rewriting one would move a stranger's
    /// log directory out from under them.</para>
    /// </summary>
    public class SteamVrDataPathTests
    {
        private const string Owned = @"C:\SteamVR";

        /// <summary>THE CASE THAT MATTERS. No ownership marker means PadForge
        /// never installed this SteamVR, so it is left entirely alone.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void UnownedInstall_IsNeverTouched(string ownedDir)
        {
            Assert.False(DriverInstaller.ShouldContainDataPaths(
                ownedDir, Owned, @"C:\SteamVR-config", @"C:\SteamVR-logs",
                out _, out _));
        }

        /// <summary>A registry naming a DIFFERENT runtime (a Steam-client
        /// SteamVR, or a hand-made install elsewhere) is that runtime's
        /// business, even when PadForge owns one of its own.</summary>
        [Fact]
        public void ARegistryPointingElsewhere_IsNeverTouched()
        {
            Assert.False(DriverInstaller.ShouldContainDataPaths(
                Owned, @"D:\Steam\steamapps\common\SteamVR",
                @"D:\Steam\steamapps\common\SteamVR-config",
                @"D:\Steam\steamapps\common\SteamVR-logs",
                out _, out _));
        }

        /// <summary>The defect: root-level siblings on an owned install.</summary>
        [Fact]
        public void OwnedInstallWithRootSiblings_MovesThemInside()
        {
            Assert.True(DriverInstaller.ShouldContainDataPaths(
                Owned, Owned, @"C:\SteamVR-config", @"C:\SteamVR-logs",
                out string config, out string log));
            Assert.Equal(@"C:\SteamVR\config", config);
            Assert.Equal(@"C:\SteamVR\logs", log);
        }

        /// <summary>Idempotent: already contained means no rewrite, so this
        /// is free to run on every launch.</summary>
        [Fact]
        public void AlreadyContained_RewritesNothing()
        {
            Assert.False(DriverInstaller.ShouldContainDataPaths(
                Owned, Owned, @"C:\SteamVR\config", @"C:\SteamVR\logs",
                out _, out _));
        }

        /// <summary>Trailing separators and casing are the same directory.
        /// A path that differs only in spelling must not read as a stranger's
        /// runtime, or the guard refuses the very install it owns.</summary>
        [Fact]
        public void SpellingDifferences_StillCountAsTheSameInstall()
        {
            Assert.False(DriverInstaller.ShouldContainDataPaths(
                Owned, @"c:\steamvr\", @"C:\SteamVR\config", @"C:\SteamVR\logs",
                out _, out _));
        }

        /// <summary>One entry contained and the other not is still a rewrite:
        /// a half-moved pair leaves one folder at the drive root.</summary>
        [Fact]
        public void OnlyOneEntryContained_StillRewrites()
        {
            Assert.True(DriverInstaller.ShouldContainDataPaths(
                Owned, Owned, @"C:\SteamVR\config", @"C:\SteamVR-logs",
                out _, out _));
        }

        /// <summary>A registry with no runtime entry at all reads as null and
        /// must refuse rather than assume it means ours.</summary>
        [Fact]
        public void MissingRuntimeEntry_IsNeverTouched()
        {
            Assert.False(DriverInstaller.ShouldContainDataPaths(
                Owned, null, null, null, out _, out _));
        }
    }
}
