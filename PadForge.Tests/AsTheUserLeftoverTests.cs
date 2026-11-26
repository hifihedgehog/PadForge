using System;
using System.IO;
using System.Linq;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SteamVR's path registry and the two data folders are written as the
    /// user. The file sits in the user's own LocalAppData, where any program
    /// of the user may put a link in place of a folder, and PadForge runs
    /// with rights. A runner that refuses leaves the file and the folders as
    /// they were. One that runs gives the rewrite.
    /// </summary>
    public class SteamVrContainmentAsUserTests
    {
        private static (string Owned, string Reg, string Root) Bench()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge_VrContain_" + Guid.NewGuid().ToString("N"));
            string owned = Path.Combine(root, "SteamVR");
            Directory.CreateDirectory(owned);
            string reg = Path.Combine(root, "openvr", "openvrpaths.vrpath");
            Directory.CreateDirectory(Path.GetDirectoryName(reg));
            File.WriteAllText(reg, System.Text.Json.JsonSerializer.Serialize(new
            {
                runtime = new[] { owned },
                config = new[] { owned + "-config" },
                log = new[] { owned + "-logs" },
                external_drivers = new[] { @"C:\ProgramData\HIDMaestro\openvr\hidmaestro" },
            }));
            return (owned, reg, root);
        }

        [Fact]
        public void ARunnerThatRefuses_LeavesTheFileAndTheFolders()
        {
            var (owned, reg, root) = Bench();
            try
            {
                string before = File.ReadAllText(reg);
                Assert.False(DriverInstaller.ContainSteamVrDataPaths(owned, reg, _ => false));
                Assert.Equal(before, File.ReadAllText(reg));
                Assert.False(Directory.Exists(Path.Combine(owned, "config")));
                Assert.False(Directory.Exists(Path.Combine(owned, "logs")));
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void ARunnerThatRuns_GivesTheRewrite()
        {
            var (owned, reg, root) = Bench();
            try
            {
                int runs = 0;
                Assert.True(DriverInstaller.ContainSteamVrDataPaths(owned, reg, work => { runs++; work(); return true; }));
                Assert.Equal(1, runs);
                Assert.True(Directory.Exists(Path.Combine(owned, "config")));
                Assert.True(Directory.Exists(Path.Combine(owned, "logs")));
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reg));
                Assert.Equal(Path.Combine(owned, "config"), doc.RootElement.GetProperty("config")[0].GetString());
                Assert.Equal(Path.Combine(owned, "logs"), doc.RootElement.GetProperty("log")[0].GetString());
                Assert.Equal(1, doc.RootElement.GetProperty("external_drivers").GetArrayLength());
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }

    /// <summary>The legacy vJoy removal takes only vJoy's own root nodes:
    /// those whose hardware ID is one vJoy's INF names,
    /// root\VID_1234&amp;PID_0FFB, PID_1FFB or PID_BEAD with any REV
    /// (driver/sys/vjoy.inx of vJoy-shauleiz, vJoy-njz3, vJoy-jshafer817 and
    /// vJoy-Brunner, and VENDOR_N_ID 0x1234 and PRODUCT_N_ID in inc/public.h).
    /// Another driver's generated ROOT\HIDCLASS node stays.</summary>
    public class VJoyNodeTests
    {
        [Fact]
        public void OnlyVJoysOwnNodes_AreRemoved()
        {
            var nodes = new[]
            {
                ("ROOT\\HIDCLASS\\0000", new[] { "root\\VID_1234&PID_BEAD&REV_0222" }),
                ("ROOT\\HIDCLASS\\0001", new[] { "root\\SomeOtherDriver" }),
                ("ROOT\\HIDCLASS\\0002", new[] { "ROOT\\VID_1234&PID_0FFB&REV_0218" }),
                ("ROOT\\HIDCLASS\\0003", new[] { "root\\VID_1234&PID_1FFB&REV_0219", "root\\VID_1234" }),
                ("ROOT\\HIDCLASS\\0004", new[] { "root\\VID_1234&PID_ABCD&REV_0222" }),
                ("ROOT\\HIDCLASS\\0005", Array.Empty<string>()),
                ("ROOT\\HIDCLASS\\0006", (string[])null),
            };
            Assert.Equal(new[] { "ROOT\\HIDCLASS\\0000", "ROOT\\HIDCLASS\\0002", "ROOT\\HIDCLASS\\0003" },
                DriverInstaller.VJoyNodeIds(nodes).ToArray());
        }
    }
}
