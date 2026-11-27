using System;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using PadForge.Services.Automation;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Each Windows reading #597 needs is made in one place, which every part
    /// calls: the parts built apart each had a copy of some, and the merge
    /// kept one of each.
    /// </summary>
    public class OneOfEachTests
    {
        private static int Count(string pattern) => FenceSource.Census(pattern).Values.Sum();

        private static void OnlyIn(string pattern, params string[] files)
            => Assert.Equal(files.OrderBy(f => f, StringComparer.Ordinal).ToArray(),
                FenceSource.Census(pattern).Keys.ToArray());

        [Fact]
        public void EachReadingIsMadeInOnePlace()
        {
            // Text from a program or a vendor library, made fit to show. The
            // settings file's own cleaner is another rule: it keeps format
            // characters and drops what XML 1.0 cannot hold.
            OnlyIn(@"\bstatic string Clean\(string text\)", "PadForge.Engine/Common/ShownText.cs", "PadForge.Engine/Common/XmlSafeText.cs");
            Assert.Empty(FenceSource.Census(@"\bclass (HostText|DisplayText)\b"));
            // Signatures: one reader, and the driver installer's own check of
            // Microsoft's packages, which predates #597.
            OnlyIn(@"\bextern\b.*\bWinVerifyTrust\(", "PadForge.App/Common/DriverInstaller.cs", "PadForge.Engine/Common/AuthenticodeSigner.cs");
            // A pipe client's process.
            OnlyIn(@"\bextern\b.*\bGetNamedPipeClientProcessId\(", "PadForge.App/Services/Automation/AutomationClientIdentity.cs");
            // Variables built from a profile or the machine.
            OnlyIn(@"""CreateEnvironmentBlock""|\bextern\b.*\bCreateEnvironmentBlock\(", "PadForge.App/Common/System32Calls.cs");
            // Privileges held for a while, and the DS3 driver installer's
            // own, which predates #597.
            OnlyIn(@"\bextern\b.*\bAdjustTokenPrivileges\(", "PadForge.App/Common/Privileges.cs", "PadForge.App/Services/Ds3DriverInstaller.cs");
            // A token's user.
            Assert.Equal(1, Count(@"\bGetTokenInformation\(token, TokenUser, buffer"));
        }

        [Fact]
        public void TheLogonStart_HoldsItsPrivilegesAndReadsTheUsersToken()
        {
            string logon = OffLimitsSource.Read("PadForge.App/Services/LogonStart.cs");
            Assert.Contains("using var held = Privileges.Hold(\"SeTcbPrivilege\", \"SeAssignPrimaryTokenPrivilege\", \"SeIncreaseQuotaPrivilege\");", logon, StringComparison.Ordinal);
            Assert.Contains("AutomationClientIdentity.ReadUser(t)", logon, StringComparison.Ordinal);
            Assert.Contains("System32Calls.VariablesOf(token)", logon, StringComparison.Ordinal);
        }

        /// <summary>The shared readers, live on this PC.</summary>
        [Fact]
        public async Task TheSharedReaders_ReadThisProcess()
        {
            string name = "PadForge.Tests.OneOfEach." + Guid.NewGuid().ToString("N");
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waited = server.WaitForConnectionAsync();
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(5000);
            await waited;
            Assert.Equal(Environment.ProcessId, AutomationClientProcess.IdOf(server));
            Assert.Equal(0, AutomationClientProcess.IdOf(null));

            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.User.Value, AutomationClientIdentity.ReadUser(identity.Token));
            const int TokenElevationType = 18;
            Assert.InRange(AutomationClientIdentity.ReadInt(identity.Token, TokenElevationType) ?? 0, 1, 3);
            Assert.Null(AutomationClientIdentity.ReadUser(IntPtr.Zero));

            // The user's own variables carry the profile's folder, and the
            // machine's alone do not name this user.
            string[] mine = PadForge.Common.System32Calls.VariablesOf(identity.Token);
            Assert.Contains(mine, l => l.StartsWith("USERPROFILE=", StringComparison.OrdinalIgnoreCase));
            string[] machine = PadForge.Common.System32Calls.MachineVariables();
            Assert.DoesNotContain(machine, l => l.Equals("USERNAME=" + Environment.UserName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
