using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The house style for what PadForge says. A semicolon is never a period:
    /// eight tooltips and lines joined two sentences with one ("1.00 covers
    /// the full width; 0.50 confines the cursor to half the screen") until
    /// 6.0.0. A file dialog's filter is the one place a semicolon belongs, as
    /// the separator Windows reads between patterns ("*.png;*.jpg").
    /// </summary>
    public sealed class EnglishStringsStyleTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        [Fact]
        public void NoEnglishString_UsesASemicolon_OutsideAFileFilter()
        {
            var english = XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings", "Strings.resx"))
                .Root.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => d.Element("value")?.Value ?? "");
            Assert.True(english.Count > 5000, $"only {english.Count} English strings were read");

            // Positive control: the filters are read and kept.
            var filters = english.Where(kv => kv.Value.Contains(';') && kv.Value.Contains('|')).ToList();
            Assert.Contains(filters, kv => kv.Key == "FileDialog_AudioFilter");

            var wrong = english.Where(kv => kv.Value.Contains(';') && !kv.Value.Contains('|'))
                .Select(kv => kv.Key + " = " + kv.Value).ToList();
            Assert.True(wrong.Count == 0,
                "these strings join two sentences with a semicolon. End the first with a period:\n" + string.Join("\n", wrong));
        }
    }
}
