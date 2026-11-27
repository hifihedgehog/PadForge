using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using PadForge.Common;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The logon task's program, read from its definition (#597).</summary>
    public class StartupTaskTests
    {
    }
}

namespace PadForge.Tests
{
    /// <summary>Give-back tasks left from before #597 that run PadForge.exe
    /// from a folder other accounts may change are made again without it, and
    /// the move points them at the new exe.</summary>
    [Collection("SettingsManagerStatics")]
    public class GiveBackTaskAuditTests : System.IDisposable
    {
        private const string Touchpad = @"HID\GXTP5100&Col02\4&c834cd3&0&0001";
        private const string Bind = @"USB\VID_046D&PID_C31C";
        private readonly System.Collections.Generic.List<string> _calls = new();
        private readonly string _definitions = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForgeTests", "audit-" + System.Guid.NewGuid().ToString("N"));
        private readonly System.Collections.Generic.Dictionary<string, string> _created = new();

        public GiveBackTaskAuditTests()
        {
            GiveBackTask.DefinitionFolderSeam = () => _definitions;
            GiveBackTask.FileExistsSeam = _ => false;
            GiveBackTask.ExeSizeSeam = _ => -1;
            GiveBackTask.ExtractBaseSeam = () => null;
            GiveBackTask.RunSeam = args =>
            {
                _calls.Add(args);
                int at = args.IndexOf("/XML \"", System.StringComparison.Ordinal);
                if (at >= 0)
                {
                    at += 6;
                    string path = args[at..args.IndexOf('"', at)];
                    _created[args] = System.IO.File.ReadAllText(path, System.Text.Encoding.Unicode);
                }
                return 0;
            };
        }

        public void Dispose()
        {
            GiveBackTask.OutputSeam = null;
            GiveBackTask.RunSeam = null;
            GiveBackTask.DefinitionFolderSeam = null;
            GiveBackTask.FileExistsSeam = null;
            GiveBackTask.ExeSizeSeam = null;
            GiveBackTask.ExtractBaseSeam = null;
            try { System.IO.Directory.Delete(_definitions, true); } catch { }
        }

        private static string Definition(string exe, string runSwitch, string id, string name)
            => GiveBackTask.TaskXml(GiveBackTask.Action(name, exe, runSwitch, id, null, @"C:\Windows\System32"), @"C:\Windows\System32", false);

        [Fact]
        public void TheTaskList_YieldsPadForgesGiveBackTasks()
        {
            string csv = "\"\\PadForge\",\"N/A\",\"Ready\"\r\n\"\\PadForge Give Back 0011223344556677\",\"N/A\",\"Ready\"\r\n"
                + "\"\\Microsoft\\Other\",\"N/A\",\"Ready\"\r\n\"\\PadForge Give Back 0011223344556677\",\"N/A\",\"Ready\"\r\n";
            Assert.Equal(new[] { "PadForge Give Back 0011223344556677" }, GiveBackTask.TaskNamesIn(csv));
            Assert.Empty(GiveBackTask.TaskNamesIn(null));
        }

        [Fact]
        public void ATasksRun_IsReadFromItsDefinition()
        {
            string name = GiveBackTask.NameOf(Touchpad);
            var run = GiveBackTask.RunIn(Definition(@"C:\Users\someone\Downloads\PadForge.exe", GiveBackRun.TouchpadSwitch, Touchpad, name));
            Assert.NotNull(run);
            Assert.Equal(@"C:\Users\someone\Downloads\PadForge.exe", run.Value.Exe);
            Assert.Equal(GiveBackRun.TouchpadSwitch, run.Value.Switch);
            Assert.Equal(Touchpad, run.Value.Id);
            Assert.Null(GiveBackTask.RunIn(Definition(null, GiveBackRun.TouchpadSwitch, Touchpad, name).Replace("--give-back", "--other")));
            Assert.Null(GiveBackTask.RunIn("garbage"));
        }

    }
}

namespace PadForge.Tests
{
    /// <summary>A vendor library's path is read from the machine's own
    /// registry and environment, never the user's (#597).</summary>
    [Collection("SettingsManagerStatics")]
    public class MachineEnvironmentTests : System.IDisposable
    {
        public MachineEnvironmentTests()
            => PadForge.Engine.Common.MachineEnvironment.GetSeam = name => name switch
            {
                "FDSDK" => @"%ProgramFiles%\Force Dimension\sdk-3.17.7",
                "ProgramFiles" => @"C:\Program Files",
                _ => null,
            };

        public void Dispose() => PadForge.Engine.Common.MachineEnvironment.GetSeam = null;

        [Fact]
        public void AVariable_IsFilledInFromTheMachinesValues()
        {
            Assert.Equal(@"C:\Program Files\Force Dimension\sdk-3.17.7\bin",
                PadForge.Engine.Common.MachineEnvironment.Expand(@"%FDSDK%\bin"));
            // A name the machine does not have stays as written.
            Assert.Equal(@"%USERPROFILE%\x", PadForge.Engine.Common.MachineEnvironment.Expand(@"%USERPROFILE%\x"));
            Assert.Equal("50% off", PadForge.Engine.Common.MachineEnvironment.Expand("50% off"));
            Assert.Null(PadForge.Engine.Common.MachineEnvironment.Expand(null));
        }

        [Fact]
        public void TheVendorCatalogs_ReadNoUserClassAndNoUserVariable()
        {
            foreach (string file in new[] { "LogitechGKeyCatalog.cs", "LogitechServerCatalog.cs" })
            {
                string source = AuditDelta20261002Tests.RepoText("PadForge.Engine", "Common", "Logitech", file);
                Assert.DoesNotContain("Registry.ClassesRoot", source, System.StringComparison.Ordinal);
                Assert.DoesNotContain("Registry.CurrentUser", source, System.StringComparison.Ordinal);
                Assert.DoesNotContain("Environment.ExpandEnvironmentVariables", source, System.StringComparison.Ordinal);
            }
            string falcon = AuditDelta20261002Tests.RepoText("PadForge.Engine", "Common", "ForceDimension", "ForceDimensionCatalog.cs");
            Assert.DoesNotContain("Environment.GetEnvironmentVariable(\"FDSDK\")", falcon, System.StringComparison.Ordinal);
            Assert.Contains("MachineEnvironment.Get(\"FDSDK\")", falcon, System.StringComparison.Ordinal);
        }
    }
}
