using System;
using System.IO;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The folders PadForge writes for its installs, downloads, voice model,
    /// DSX stand-in and give-back tasks are in the user's temp folder, as in
    /// 6.0.1, and none needs a folder under %ProgramData%.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WorkFolderTests
    {
        [Fact]
        public void TheWorkFolders_AreInTheTempFolder_WithNoProgramDataFolder()
        {
            {
                string temp = Path.GetTempPath();
                string staging = DriverInstaller.StagingFolder("HidHide");
                Assert.StartsWith(Path.Combine(temp, "PadForge_HidHide") + Path.DirectorySeparatorChar, staging, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Path.Combine(temp, "PadForge_DSX"), DsxStandIn.Folder);
                Assert.Equal(Path.Combine(temp, "PadForge_DSX", "DSX.exe"), DsxStandIn.ExePath);
                Assert.StartsWith(Path.Combine(temp, "PadForge", "strike7") + Path.DirectorySeparatorChar,
                    KeyboardScreenService.DownloadedStrike7Library, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Path.Combine(temp, "PadForge", "VendorUsb", "USB_VID_1234_PID_5678"),
                    VendorUsbDriverInstaller.StagingDirectory(@"USB\VID_1234&PID_5678"));
            }
        }

        [Theory]
        [InlineData("Common", "DriverInstaller.cs")]
        [InlineData("Common", "Input", "HMaestroProfileCatalog.cs")]
        [InlineData("Services", "Ds3DriverInstaller.cs")]
        [InlineData("Services", "DsxStandIn.cs")]
        [InlineData("Services", "GiveBackTask.cs")]
        [InlineData("Services", "KeyboardScreenService.cs")]
        [InlineData("Services", "VendorUsbDriverInstaller.cs")]
        [InlineData("Services", "VoskVoiceEngine.cs")]
        [InlineData("App.xaml.cs")]
        public void TheCaller_ReadsNoProtectedFolder(params string[] path)
        {
            var parts = new string[path.Length + 1];
            parts[0] = "PadForge.App";
            Array.Copy(path, 0, parts, 1, path.Length);
            string source = AuditDelta20261002Tests.RepoText(parts);
            Assert.DoesNotContain("ProtectedWork", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ProtectedFolder", source, StringComparison.Ordinal);
        }

        /// <summary>The update helper and PadForge after an update start with
        /// a plain Process.Start, as in 6.0.1, which hands on no
        /// DOTNET_BUNDLE_EXTRACT_BASE_DIR of PadForge's own: the program
        /// unpacks where .NET puts it by default.</summary>
        [Fact]
        public void TheUpdatersStart_HandsOnNoUnpackFolder()
        {
            string source = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "UpdateService.cs");
            string start = FenceSource.Body(source, "internal static Process StartProcess(ProcessStartInfo psi)");
            Assert.Contains("return Process.Start(psi);", start, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment", start, StringComparison.Ordinal);
            Assert.DoesNotContain("DOTNET_BUNDLE_EXTRACT_BASE_DIR", source, StringComparison.Ordinal);
            Assert.Null(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"));
        }
    }
}
