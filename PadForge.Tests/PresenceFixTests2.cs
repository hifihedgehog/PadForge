using System;
using System.IO;
using System.Linq;
using PadForge.Common;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using PadForge.Services.Install;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SteelSeries GG is installed by its own marks: its Uninstall entry,
    /// named SteelSeries GG, or its install folder. coreProps.json is the
    /// file the engine creates when it starts, and "If this file is not
    /// present, then SteelSeries Engine 3 is not running"
    /// (gamesense-sdk doc/api/sending-game-events.md:19 and 33), and any
    /// program may write under ProgramData, so it says nothing of an
    /// install.
    /// </summary>
    public class SteelSeriesGGPresenceTests
    {
        private static readonly UninstallRegistry.Entry GgEntry =
            new("SteelSeries GG", "SteelSeries GG 75.0.0", "75.0.0", "\"C:\\Program Files\\SteelSeries\\GG\\uninst.exe\"", null);

        [Fact]
        public void ACorePropsFileAlone_IsNoInstall()
        {
            Assert.False(PeripheralPresence.SteelSeriesGGInstalled(
                Array.Empty<UninstallRegistry.Entry>(),
                p => string.Equals(p, GameSenseClient.DefaultCorePropsPath, StringComparison.OrdinalIgnoreCase)));
        }

        [Fact]
        public void TheUninstallEntry_IsAnInstall_WhereverItsFolderIs()
            => Assert.True(PeripheralPresence.SteelSeriesGGInstalled(new[] { GgEntry }, _ => false));

        [Fact]
        public void TheDefaultFolder_IsAnInstall()
            => Assert.True(PeripheralPresence.SteelSeriesGGInstalled(
                Array.Empty<UninstallRegistry.Entry>(),
                p => p.EndsWith(Path.Combine("SteelSeries", "GG"), StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The engine's address in coreProps.json is the engine's own
    /// host and port, "e.g. 127.0.0.1:51248" (sending-game-events.md:19-25),
    /// and PadForge with its rights posts to it. A file another program
    /// wrote may name any host, so only a loopback host and a port are
    /// taken.</summary>
    public class GameSenseAddressTests
    {
        [Theory]
        [InlineData("{\"address\":\"10.0.0.5:80\"}")]
        [InlineData("{\"address\":\"example.com:80/x\"}")]
        [InlineData("{\"address\":\"127.0.0.1:80/x\"}")]
        [InlineData("{\"address\":\"127.0.0.1:99999\"}")]
        [InlineData("{\"address\":\"user@127.0.0.1:80\"}")]
        public void AnAddressOffThisPC_IsRefused(string json)
            => Assert.False(GameSenseClient.TryReadAddress(json, out _));

        [Theory]
        [InlineData("{\"address\":\"127.0.0.1:51248\"}", "127.0.0.1:51248")]
        [InlineData("{\"address\":\"localhost:51248\"}", "localhost:51248")]
        [InlineData("{\"address\":\"[::1]:51248\"}", "[::1]:51248")]
        public void ALoopbackAddress_IsTaken(string json, string expected)
        {
            Assert.True(GameSenseClient.TryReadAddress(json, out string address));
            Assert.Equal(expected, address);
        }
    }

    /// <summary>The Razer Sensa row opens where Razer's Sensa runtime is,
    /// the library its path loads. The Chroma SDK runtime of Synapse says
    /// nothing of Sensa, and with it alone the row asked the user to set a
    /// Haptic Source that has no Sensa entry to pick.</summary>
    public class RazerSensaRowTests
    {
        [Fact]
        public void TheChromaRuntimeAlone_OpensNoSensaRow()
        {
            var presence = new PeripheralPresence(false, false, true, false, true) { RazerSensaRuntime = false };
            Assert.False(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, presence));
        }

        [Fact]
        public void TheSensaRuntime_OpensTheRow()
        {
            var presence = new PeripheralPresence(false, false, true, false, true) { RazerSensaRuntime = true };
            Assert.True(InputManager.PeripheralRowWanted(PeripheralRowKind.RazerSensa, presence));
        }
    }

    /// <summary>The folders of the user at the desktop, not of the account
    /// PadForge runs as: a standard user who typed an administrator's
    /// credentials at the rights prompt has another %APPDATA% and
    /// %LOCALAPPDATA% in this process.</summary>
    public class ShellUserFolderTests
    {
        [Fact]
        public void TheProfilePicker_OffersTheShellUsersDs4WindowsFolder()
        {
            string roaming = Path.Combine(Path.GetTempPath(), "PadForge_ShellRoaming_" + Guid.NewGuid().ToString("N"));
            try
            {
                string profiles = Path.Combine(roaming, "DS4Windows", "Profiles");
                Directory.CreateDirectory(profiles);
                var places = ForeignConfigImporter.ProfileFolders(f => f == UserFolder.RoamingData ? roaming : null).ToList();
                Assert.Contains(profiles, places);
            }
            finally
            {
                try { Directory.Delete(roaming, true); } catch { }
            }
        }

        [Fact]
        public void SteamVrsPathRegistry_IsReadFromTheShellUsersLocalAppData()
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE"))) return;
            string local = Path.Combine(Path.GetTempPath(), "PadForge_ShellLocal_" + Guid.NewGuid().ToString("N"));
            try
            {
                string runtime = Path.Combine(local, "SteamVR");
                string dll = Path.Combine(runtime, "bin", "win64", "openvr_api.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(dll));
                File.WriteAllBytes(dll, new byte[] { 0 });
                Directory.CreateDirectory(Path.Combine(local, "openvr"));
                File.WriteAllText(Path.Combine(local, "openvr", "openvrpaths.vrpath"),
                    System.Text.Json.JsonSerializer.Serialize(new { runtime = new[] { runtime } }));
                Assert.Equal(dll, OpenVrConsumerService.DiscoverRuntimeDll(() => local));
            }
            finally
            {
                try { Directory.Delete(local, true); } catch { }
            }
        }

        /// <summary>The SteamVR containment rewrites the same file the
        /// consumer reads, from the same user's LocalAppData.</summary>
        [Fact]
        public void TheContainment_ReadsTheSamePathRegistry()
        {
            string installer = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "DriverInstaller.cs")));
            Assert.Contains("string reg = Input.OpenVrConsumerService.PathRegistryFile(", installer);
            Assert.DoesNotContain("Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);\n                string reg", installer.Replace("\r\n", "\n"));
        }
    }
}
