using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// ARM64. Three features have a native half, and which architecture
    /// decides each one is the part that is easy to get wrong.
    ///
    /// <para>A kernel driver follows the MACHINE, because a kernel driver
    /// cannot run emulated. An in-process library follows the PROCESS,
    /// because an x64 DLL loads into an emulated x64 process on an ARM64
    /// machine and cannot load into a native ARM64 one. HidHide has a driver
    /// for each machine, picked by IsArm64Machine, and Vosk a library for
    /// each process, so both are available everywhere PadForge runs. Sensa
    /// has an x64 engine only, so the ARM64 build loses it and the x64 build
    /// on an ARM64 machine keeps it. Each process rule is a pure function of
    /// the architecture it is handed, and the source pin below holds
    /// IsArm64Machine to the machine, so both halves are pinned here on a
    /// bench that only has x64.</para>
    /// </summary>
    public class PlatformSupportTests
    {
        // Each rule names the architectures that HAVE the native half. The
        // rules first read "anything but ARM64", which answered true for
        // x86, where none of it is usable.
        // libvosk is bundled for x64 (from the Vosk package) and for ARM64
        // (built from upstream's own recipe, tools/build-libvosk-arm64.sh).
        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.Arm64, true)]
        [InlineData(Architecture.X86, false)]
        [InlineData(Architecture.Arm, false)]
        public void VoskFollowsTheProcess(Architecture process, bool expected)
            => Assert.Equal(expected, PlatformSupport.VoskAvailableOn(process));

        [Theory]
        [InlineData(Architecture.X64, true)]
        [InlineData(Architecture.X86, false)]
        [InlineData(Architecture.Arm, false)]
        [InlineData(Architecture.Arm64, false)]
        public void SensaFollowsTheProcess(Architecture process, bool expected)
            => Assert.Equal(expected, PlatformSupport.SensaAvailableOn(process));

        /// <summary>The case the two-architecture split exists for: the x64
        /// build running emulated on ARM64 Windows. Its process is x64 and
        /// its machine is ARM64, so the in-process libraries still load, and
        /// the driver it installs has to be the ARM64 one. Collapsing the two
        /// questions into one "is this ARM64" would take voice and Sensa away
        /// from a build that can run them.</summary>
        [Fact]
        public void TheX64BuildOnAnArm64MachineKeepsEverything()
        {
            Architecture process = Architecture.X64;
            Assert.True(PlatformSupport.VoskAvailableOn(process));
            Assert.True(PlatformSupport.SensaAvailableOn(process));
        }

        /// <summary>The live properties answer for this process on this
        /// machine, and they have to agree with the pure rules they wrap.
        /// Nothing here assumes which architecture the suite runs on.</summary>
        [Fact]
        public void TheLivePropertiesAgreeWithTheRules()
        {
            Assert.Equal(PlatformSupport.VoskAvailableOn(RuntimeInformation.ProcessArchitecture),
                         PlatformSupport.VoskAvailable);
            Assert.Equal(PlatformSupport.SensaAvailableOn(RuntimeInformation.ProcessArchitecture),
                         PlatformSupport.SensaAvailable);
            Assert.Equal(RuntimeInformation.OSArchitecture == Architecture.Arm64,
                         PlatformSupport.IsArm64Machine);
        }

        /// <summary>The test above cannot see the mistake this class exists
        /// to prevent. On an x64 bench the machine and the process are both
        /// x64, so a live property wired to the WRONG one of the two returns
        /// the same answer and every assertion still holds. Only an ARM64
        /// machine running the x64 build tells them apart, and the bench has
        /// none. So the wiring is pinned in the source: each live property
        /// has to hand its rule the architecture that rule is about.</summary>
        [Theory]
        [InlineData("MachineArchitecture", "RuntimeInformation.OSArchitecture")]
        [InlineData("ProcessArchitecture", "RuntimeInformation.ProcessArchitecture")]
        [InlineData("IsArm64Machine", "MachineArchitecture == Architecture.Arm64")]
        [InlineData("VoskAvailable", "VoskAvailableOn(ProcessArchitecture)")]
        [InlineData("SensaAvailable", "SensaAvailableOn(ProcessArchitecture)")]
        public void EachLivePropertyAsksTheArchitectureItsRuleIsAbout(string property, string expression)
        {
            Assert.Equal(expression, LivePropertyBody(PlatformSupportSource(), property));
        }

        /// <summary>The pin is worthless unless it can tell the two
        /// architectures apart, so it is shown the swap it guards against.</summary>
        [Fact]
        public void ThePinSeesAMachineProcessSwap()
        {
            string swapped = PlatformSupportSource().Replace(
                "MachineArchitecture == Architecture.Arm64", "ProcessArchitecture == Architecture.Arm64");
            Assert.Equal("ProcessArchitecture == Architecture.Arm64", LivePropertyBody(swapped, "IsArm64Machine"));
        }

        private static string PlatformSupportSource()
            => File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.Engine", "Common", "PlatformSupport.cs")));

        /// <summary>The expression body of one static property, read from the
        /// declaration itself so a mention in a comment cannot satisfy it.</summary>
        private static string LivePropertyBody(string source, string property)
        {
            var m = Regex.Match(source,
                @"^\s*public static \w+ " + Regex.Escape(property) + @"\s*=>\s*(.+?);\s*$",
                RegexOptions.Multiline);
            Assert.True(m.Success, property + " is no longer an expression-bodied static property");
            return m.Groups[1].Value.Trim();
        }
    }
}
