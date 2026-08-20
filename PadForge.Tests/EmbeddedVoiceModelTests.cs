using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Vosk recognition model ships INSIDE the executable (#317). It used
    /// to download ~40 MB on first use, which made a feature advertised as
    /// offline unusable on a machine with no internet.
    ///
    /// <para>The failure mode this pins is silent by construction: if the
    /// resource name drifts, <c>GetManifestResourceStream</c> returns null,
    /// the unpack throws, and voice macros fall back to the Windows speech
    /// engine forever. Nothing crashes and nothing logs at a level anyone
    /// reads, so the feature just quietly gets worse. A renamed folder, a
    /// bumped model version, or a dropped csproj glob all land here.</para>
    /// </summary>
    public class EmbeddedVoiceModelTests
    {
        // The exact string VoskModelStore asks for.
        private const string ModelResource =
            "PadForge.VoiceModels.vosk-model-small-en-us-0.15.zip";

        private static Assembly AppAssembly =>
            Assembly.Load("PadForge");

        [Fact]
        public void TheModelIsEmbeddedUnderTheNameTheLoaderAsksFor()
        {
            var names = AppAssembly.GetManifestResourceNames();
            Assert.True(names.Contains(ModelResource),
                "embedded model missing. Present resources matching 'vosk': "
                + string.Join(", ", names.Where(n => n.Contains("vosk", System.StringComparison.OrdinalIgnoreCase)))
                + " (none, if blank)");
        }

        /// <summary>A resource of the right NAME that is not a readable
        /// archive fails just as silently, so open it.</summary>
        [Fact]
        public void TheEmbeddedModelIsAReadableArchiveCarryingTheModel()
        {
            using var s = AppAssembly.GetManifestResourceStream(ModelResource);
            Assert.NotNull(s);
            using var zip = new ZipArchive(s, ZipArchiveMode.Read);

            // Vosk loads a model from a directory, and the archive carries a
            // single top-level folder the unpack promotes. The acoustic model
            // is the file whose absence makes Model() throw.
            Assert.Contains(zip.Entries, e =>
                e.FullName.EndsWith("am/final.mdl", System.StringComparison.OrdinalIgnoreCase)
                || e.FullName.EndsWith("final.mdl", System.StringComparison.OrdinalIgnoreCase));

            var top = zip.Entries
                .Select(e => e.FullName.Split('/')[0])
                .Distinct()
                .ToArray();
            Assert.Single(top);
        }

        /// <summary>No network path may return. The whole point is that a
        /// machine which has never been online can still recognize speech.</summary>
        [Fact]
        public void TheLoaderCarriesNoDownloadUrl()
        {
            string src = Path.Combine(RepoRoot(), "PadForge.App", "Services", "VoskVoiceEngine.cs");
            Assert.True(File.Exists(src), src);
            string text = File.ReadAllText(src);
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
            Assert.DoesNotContain("HttpClient", text);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SharedVersion.cs")))
                d = d.Parent;
            return d?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
