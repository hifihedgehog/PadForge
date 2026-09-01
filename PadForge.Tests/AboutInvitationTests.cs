using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The About page's invitation panel, the padforge.org arrangement
    /// in WPF: the verse with its citation split onto its own line, the
    /// testimony, and the invitation sentence whose ComeUntoChrist.org
    /// marker becomes the only linked, ember-colored span. The resx
    /// contract matters most: every locale must carry the split ref key
    /// and the marker the code-behind splits on, or a locale ships a
    /// verse with a duplicated citation or an invitation with no link
    /// target to color.
    /// </summary>
    public class AboutInvitationTests
    {
        private static readonly string[] Locales =
            { "", ".de", ".es", ".fr", ".it", ".ja", ".ko", ".nl", ".pt-BR", ".zh-Hans" };

        private static string ResxValue(string resx, string key)
        {
            var m = Regex.Match(resx,
                "<data name=\"" + Regex.Escape(key) + "\" xml:space=\"preserve\"><value>(.*?)</value></data>");
            Assert.True(m.Success, key + " missing");
            return m.Groups[1].Value;
        }

        [Fact]
        public void EveryLocale_SplitsTheCitationAndCarriesTheMarker()
        {
            foreach (var loc in Locales)
            {
                string resx = RepoText("PadForge.App", "Resources", "Strings", "Strings" + loc + ".resx");

                string verse = ResxValue(resx, "About_TestimonyScripture");
                string reference = ResxValue(resx, "About_TestimonyRef");
                string invite = ResxValue(resx, "About_TestimonyInvite");

                Assert.False(string.IsNullOrWhiteSpace(reference), loc + ": empty ref");
                Assert.Contains("25:26", reference);
                // The citation moved out of the verse; leaving it in both
                // renders it twice on the panel.
                Assert.DoesNotContain(reference, verse);
                // The code-behind splits the sentence on this marker to
                // color only the link.
                Assert.Contains("ComeUntoChrist.org", invite);
            }
        }

        [Fact]
        public void EnglishStrings_CarryTheSiteCopy()
        {
            string resx = RepoText("PadForge.App", "Resources", "Strings", "Strings.resx");
            Assert.Equal("An Invitation", ResxValue(resx, "About_Testimony"));
            Assert.Equal("2 Nephi 25:26", ResxValue(resx, "About_TestimonyRef"));
            Assert.Contains("about Him and The Church of Jesus Christ of Latter-day Saints",
                ResxValue(resx, "About_TestimonyInvite"));
            Assert.Contains("the source of all truth and salvation",
                ResxValue(resx, "About_TestimonyDoxology"));
        }

        /// <summary>The page contracts: the panel binds the split ref,
        /// the invitation TextBlock is the named code-behind target with
        /// no XAML hyperlink left behind, and the code-behind split
        /// rebuilds on culture change with the ember resource on the
        /// link.</summary>
        [Fact]
        public void PageAndCodeBehind_BuildTheSplitLink()
        {
            string page = RepoText("PadForge.App", "Views", "AboutPage.xaml");
            Assert.Contains("Binding About_TestimonyRef", page);
            Assert.Contains("x:Name=\"InviteLine\"", page);
            Assert.DoesNotContain("<Hyperlink", page);

            string code = RepoText("PadForge.App", "Views", "AboutPage.xaml.cs");
            Assert.Contains("const string Marker = \"ComeUntoChrist.org\";", code);
            Assert.Contains("Strings.CultureChanged += BuildInviteLine;", code);
            Assert.Contains("link.SetResourceReference(TextElement.ForegroundProperty, \"EmberBrush\");", code);
            Assert.Contains("NavigateUri = new Uri(\"https://www.comeuntochrist.org\")", code);
        }

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }
    }
}
