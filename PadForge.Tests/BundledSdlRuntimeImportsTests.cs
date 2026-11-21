using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SDL3.dll comes from the fork as a binary, and which Visual C++ runtime
    /// DLLs it imports depends on how the fork was built. The x64 one imports
    /// msvcp140.dll and vcruntime140_1.dll for the Elite paddle reader, which
    /// is C++. The ARM64 one delivered for 4.5.1 had that reader switched off
    /// and imported neither, so the ARM64 build stopped carrying them.
    ///
    /// <para>The fork then built the reader for ARM64 (011dd342aa), and the
    /// ARM64 SDL3.dll imports msvcp140.dll again. Taken without the DLL
    /// beside it, SDL3.dll fails to load on a clean ARM64 machine and every
    /// controller is dead, with nothing at build time to say why. So each
    /// SDL3.dll is read for the runtime DLLs it names, and each one named
    /// has to be in the same architecture's VisualCpp folder. This test
    /// was written ahead of that delivery and failed on it, which is how
    /// the ARM64 msvcp140.dll came to be bundled in the same commit.</para>
    /// </summary>
    public class BundledSdlRuntimeImportsTests
    {
        private static readonly string[] RuntimeDlls = { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" };

        public static IEnumerable<object[]> Architectures()
        {
            string sdl = Path.Combine(ResourcesRoot(), "SDL3");
            foreach (string dir in Directory.GetDirectories(sdl))
                if (File.Exists(Path.Combine(dir, "SDL3.dll")))
                    yield return new object[] { Path.GetFileName(dir) };
        }

        [Theory]
        [MemberData(nameof(Architectures))]
        public void EveryRuntimeDllSdlImportsIsBundledForThatArchitecture(string arch)
        {
            var named = RuntimeDllsNamedBy(File.ReadAllBytes(Path.Combine(ResourcesRoot(), "SDL3", arch, "SDL3.dll")));
            // vcruntime140.dll is imported by every MSVC build, so an empty
            // answer means the scan is broken, not that SDL needs nothing.
            Assert.Contains("vcruntime140.dll", named);

            var missing = named.Where(dll => !File.Exists(Path.Combine(ResourcesRoot(), "VisualCpp", arch, dll))).ToList();
            Assert.True(missing.Count == 0,
                "Resources/SDL3/" + arch + "/SDL3.dll imports " + string.Join(", ", missing)
                + ", and Resources/VisualCpp/" + arch + " does not carry it.");
        }

        [Fact]
        public void BothArchitecturesAreChecked()
        {
            var archs = Architectures().Select(a => (string)a[0]).ToList();
            Assert.Contains("x64", archs);
            Assert.Contains("arm64", archs);
        }

        /// <summary>The scan has to be able to see a name, or the theory
        /// above passes on anything. The x64 SDL3.dll is known to import all
        /// three (dumpbin /dependents).</summary>
        [Fact]
        public void TheScanSeesTheThreeNamesTheX64SdlImports()
        {
            var named = RuntimeDllsNamedBy(File.ReadAllBytes(Path.Combine(ResourcesRoot(), "SDL3", "x64", "SDL3.dll")));
            Assert.Equal(RuntimeDlls.OrderBy(n => n), named.OrderBy(n => n));
        }

        /// <summary>The two runtime DLLs both builds need are bundled by
        /// items with no Exists condition. With one, a file that went missing
        /// was left out in silence and the publish went through with an
        /// SDL3.dll that cannot load on a clean machine. Without one, the
        /// build stops and names the file. The theory above cannot see this
        /// while the files are there.</summary>
        [Theory]
        [InlineData("msvcp140.dll")]
        [InlineData("vcruntime140.dll")]
        public void ARuntimeDllBothBuildsNeed_IsBundledUnconditionally(string dll)
        {
            string project = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "PadForge.App.csproj")));
            string path = @"Resources\VisualCpp\$(NativeArch)\" + dll;
            var item = System.Text.RegularExpressions.Regex.Match(project,
                "<Content Include=\"" + System.Text.RegularExpressions.Regex.Escape(path) + "\"[^>]*>");
            Assert.True(item.Success, "no Content item bundles " + dll + " by $(NativeArch)");
            Assert.DoesNotContain("Condition=", item.Value);
        }

        /// <summary>Import names are plain ASCII in the image, in whatever
        /// case the linker wrote them (MSVCP140.dll, VCRUNTIME140.dll).</summary>
        private static List<string> RuntimeDllsNamedBy(byte[] image)
        {
            string text = Encoding.Latin1.GetString(image);
            return RuntimeDlls.Where(dll => text.IndexOf(dll, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private static string ResourcesRoot()
        {
            string sdl = AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Resources", "SDL3", "x64", "SDL3.dll"));
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sdl), "..", ".."));
        }
    }
}
