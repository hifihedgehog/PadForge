using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Common.Logitech;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The engine's shared types as the audit of 2026-11-26 found them: the
    /// diagnostics log with rights, a declaration a program sends, an access
    /// list's alarm entry, a disconnected device's own state, and the
    /// Logitech registrations.
    /// </summary>
    public class AuditDelta20261126EngineTests
    {
        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        // ── The diagnostics log with rights ──

        /// <summary>A program without rights that sets PADFORGE_DIAG to a
        /// file PadForge keeps beside the exe never has PadForge with rights
        /// append to it, rotate it or move it away.</summary>
        [Theory]
        [InlineData(@"C:\PadForge\PadForge.xml")]
        [InlineData(@"C:\PadForge\crash.log")]
        [InlineData(@"C:\PadForge\Settings.xml")]
        [InlineData(@"C:\PadForge\PadForge.exe")]
        [InlineData(@"C:\PadForge\new.dll")]
        public void TheDiagnosticsLog_WithRights_TakesNoOtherFileBesideTheExe(string path)
            => Assert.Null(SdlDiagLog.MirrorPathWithRights(path, @"C:\PadForge\PadForge.exe"));

        // ── A declaration a program sends ──

        /// <summary>A number where JSON holds a string, a fraction or a
        /// boolean is refused with words, never thrown out of the session's
        /// line.</summary>
        [Theory]
        [InlineData("{\"touchpads\":[{\"fingers\":\"two\"}]}")]
        [InlineData("{\"touchpads\":[{\"fingers\":2.5}]}")]
        [InlineData("{\"touchpads\":[{\"fingers\":true}]}")]
        [InlineData("{\"buttons\":[{\"index\":\"1\"}]}")]
        [InlineData("{\"axes\":[{\"index\":1.5}]}")]
        [InlineData("{\"axes\":[{\"rest\":true}]}")]
        [InlineData("{\"sliders\":[{\"rest\":null}]}")]
        public void ADeclarationWithAWrongKindOfNumber_IsRefusedAndThrowsNothing(string json)
        {
            ProgramDeviceShape shape = null;
            string error = null;
            var thrown = Record.Exception(() => shape = ProgramDeviceDeclaration.Parse(json, out error));
            Assert.Null(thrown);
            Assert.Null(shape);
            Assert.False(string.IsNullOrEmpty(error));
        }

        // ── An access list's alarm entry ──

        // ── A disconnected device's own state ──

        /// <summary>A device that disconnects keeps no state of its last
        /// connection for a reader to take as its own.</summary>
        [Fact]
        public void ADisconnectedDevice_KeepsNoOwnStateOfItsLastConnection()
        {
            var ud = new UserDevice { InputState = new CustomInputState(), ReadInputState = new CustomInputState() };
            ud.ClearRuntimeState();
            Assert.Null(ud.InputState);
            Assert.Null(ud.ReadInputState);
        }

        /// <summary>A settings reload hands the device's own state to the
        /// record the file replaced it with, beside the state mappings
        /// read.</summary>
        [Fact]
        public void ASettingsReload_CarriesTheDevicesOwnState()
        {
            var carry = typeof(SettingsService).GetMethod("CarryRuntimeState", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(carry);
            var own = new CustomInputState();
            var from = new UserDevice { InputState = new CustomInputState(), ReadInputState = own };
            var to = new UserDevice();
            carry.Invoke(null, new object[] { from, to });
            Assert.Same(own, to.ReadInputState);
        }

        // ── The Logitech registrations ──

        /// <summary>RegistryKey.GetValue without options fills a
        /// REG_EXPAND_SZ value's variables in from this process's own
        /// environment (dotnet runtime, RegistryKey.cs InternalGetValue),
        /// which the user's
        /// variables make. Both catalogs read the value as written.</summary>
        [Fact]
        public void BothLogitechCatalogs_ReadTheRegistrationAsWritten()
        {
            string server = RepoFile("PadForge.Engine", "Common", "Logitech", "LogitechServerCatalog.cs");
            Assert.Contains("RegistryValueOptions.DoNotExpandEnvironmentNames", server, StringComparison.Ordinal);
            Assert.DoesNotContain("GetValue(string.Empty) as string", server, StringComparison.Ordinal);
            string gkey = RepoFile("PadForge.Engine", "Common", "Logitech", "LogitechGKeyCatalog.cs");
            Assert.Contains("LogitechServerCatalog.ReadPath(subKey)", gkey, StringComparison.Ordinal);
            Assert.DoesNotContain("GetValue(", gkey, StringComparison.Ordinal);
        }
    }

    /// <summary>The value rule both Logitech catalogs read a registration
    /// by, with the machine's variables from the test's own seam.</summary>
    [Collection("SettingsManagerStatics")]
    public class LogitechRegistrationValueTests : IDisposable
    {
        public LogitechRegistrationValueTests()
            => MachineEnvironment.GetSeam = name => name switch
            {
                "ProgramFiles" => @"C:\Program Files",
                _ => null,
            };

        public void Dispose() => MachineEnvironment.GetSeam = null;

        [Fact]
        public void ARegisteredValue_LosesItsQuotesAndTakesTheMachinesVariables()
        {
            Assert.Equal(@"C:\Program Files\Logitech Gaming Software\SDK\G-key\x64\LogitechGkey.dll",
                LogitechServerCatalog.PathOf("  \"%ProgramFiles%\\Logitech Gaming Software\\SDK\\G-key\\x64\\LogitechGkey.dll\" "));
            // A variable the machine does not have stays as written, so no
            // user variable fills it in.
            Assert.Equal(@"%USERPROFILE%\x.dll", LogitechServerCatalog.PathOf(@"%USERPROFILE%\x.dll"));
            Assert.Null(LogitechServerCatalog.PathOf("  "));
            Assert.Null(LogitechServerCatalog.PathOf("\"\""));
            Assert.Null(LogitechServerCatalog.PathOf(null));
        }

        /// <summary>The folders Windows sets for every process and keeps in
        /// no machine variable fill in from Windows' own folder paths, so a
        /// registration that names one is not taken for a file that is
        /// gone.</summary>
        [Fact]
        public void TheFoldersWindowsSetsForEveryProcess_FillIn()
        {
            MachineEnvironment.GetSeam = null;
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles) + @"\Logitech\x.dll",
                MachineEnvironment.Expand(@"%CommonProgramFiles%\Logitech\x.dll"));
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + @"\Logitech\x.dll",
                MachineEnvironment.Expand(@"%ProgramData%\Logitech\x.dll"));
            Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\x.dll",
                MachineEnvironment.Expand(@"%ProgramW6432%\x.dll"));
        }
    }
}
