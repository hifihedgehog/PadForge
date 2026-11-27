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
            Assert.Equal(0, Count(@"""CreateEnvironmentBlock""|\bextern\b.*\bCreateEnvironmentBlock\("));
            // The DS3 driver installer's own privileges, which predate #597.
            OnlyIn(@"\bextern\b.*\bAdjustTokenPrivileges\(", "PadForge.App/Services/Ds3DriverInstaller.cs");
            // A token's user.
            Assert.Equal(1, Count(@"\bGetTokenInformation\(token, TokenUser, buffer"));
        }

    }
}
