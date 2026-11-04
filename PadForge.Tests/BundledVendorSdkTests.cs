using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The vendor SDK libraries PadForge ships inside its executable for
    /// the iCUE and Mystic Light routes (#557), as it ships the Interhaptics
    /// engine: each is a Content item of the x64 build under the name its
    /// loader asks for, the file is an x64 image, a publish without it is
    /// refused, and no ARM64 build carries one, since neither vendor builds
    /// one for ARM64.
    /// </summary>
    public class BundledVendorSdkTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        public static TheoryData<string, string> Libraries() => new()
        {
            { "Corsair", IcueSdkNative.LibraryName },
            { "MysticLight", MysticLightNative.BundledName },
        };

        [Theory]
        [MemberData(nameof(Libraries))]
        public void EachLibrary_ShipsInTheX64BuildUnderItsLoadersName(string folder, string name)
        {
            string app = Path.Combine(RepoRoot(), "PadForge.App");
            var project = XDocument.Load(Path.Combine(app, "PadForge.App.csproj"));
            string include = $@"Resources\{folder}\$(NativeArch)\{name}";
            var item = Assert.Single(project.Descendants("Content"), e => (string)e.Attribute("Include") == include);
            Assert.Equal(name, (string)item.Attribute("Link"));
            Assert.Equal($"Exists('{include}')", (string)item.Attribute("Condition"));
            Assert.Equal("PreserveNewest", (string)item.Element("CopyToOutputDirectory"));

            string file = Path.Combine(app, "Resources", folder, "x64", name);
            Assert.True(MysticLightNative.IsX64Library(file), file);
            Assert.False(Directory.Exists(Path.Combine(app, "Resources", folder, "arm64")));

            var guard = project.Descendants("Target").Single(t => (string)t.Attribute("Name") == "RequireBundledNatives");
            Assert.Single(guard.Elements("Error"),
                e => (string)e.Attribute("Condition") == $@"'$(NativeArch)' == 'x64' And !Exists('Resources\{folder}\x64\{name}')");
        }

        /// <summary>The bundled Mystic Light library carries MSI's own name
        /// for the 64-bit library, the first name the service-folder fallback
        /// looks for too.</summary>
        [Fact]
        public void TheMysticLightCopy_CarriesMsisOwnName()
            => Assert.Equal(MysticLightNative.BundledName, MysticLightNative.LibraryNames[0]);

        /// <summary>iCUE counts as installed by iCUE 5's or iCUE 4's iCUE.exe
        /// under Program Files, or by iCUE 5's Run value, as Aurora reads it
        /// (IcueInstallationUtils.cs:11-17).</summary>
        [Fact]
        public void ICue_IsInstalledByItsProgramOrItsRunValue()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert.Equal(new[]
            {
                Path.Combine(programFiles, "Corsair", "Corsair iCUE5 Software", "iCUE.exe"),
                Path.Combine(programFiles, "Corsair", "CORSAIR iCUE 4 Software", "iCUE.exe"),
            }, IcueSdkNative.ProgramPaths());
            Assert.Equal("Corsair iCUE5 Software", IcueSdkNative.RunValueName);
        }
    }
}
