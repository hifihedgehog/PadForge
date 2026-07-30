using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// <para>A <c>{StaticResource Foo}</c> naming a key that does not exist
    /// compiles cleanly and throws XamlParseException the moment the page
    /// loads. On a page the main window builds, that is a crash at launch
    /// with no window at all: which is exactly what shipped when a momentum
    /// slider was written against a style named DzValue when the page's
    /// styles are DzValueEdit and DzPercent.</para>
    /// <para>The build cannot see it and no unit test touching view models
    /// can either, because the failure lives in markup. This scans the
    /// markup.</para>
    /// </summary>
    public class XamlResourceKeyTests
    {
        private static readonly Regex KeyDef = new(@"x:Key=""([^""]+)""", RegexOptions.Compiled);
        private static readonly Regex StaticUse = new(@"StaticResource\s+([A-Za-z0-9_]+)", RegexOptions.Compiled);

        /// <summary>Keys that live in the Wpf.Ui control library rather than
        /// in this repo's markup, so scanning our own files cannot see them.
        /// The library names its implicit styles Default*Style uniformly;
        /// anything else has to be listed by hand, because an unrecognised
        /// key is a defect until someone decides otherwise.</summary>
        private static bool IsLibraryKey(string key)
            => (key.StartsWith("Default", StringComparison.Ordinal)
                && key.EndsWith("Style", StringComparison.Ordinal))
               || key == "TitleTextBlockStyle";

        /// <summary>Strips XML comments before scanning. Prose inside them
        /// mentions resources in passing ("the StaticResource font is only
        /// ..."), and matching those reports a defect in a sentence.</summary>
        private static string StripComments(string xaml)
            => Regex.Replace(xaml, "<!--.*?-->", " ", RegexOptions.Singleline);

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            return d?.FullName;
        }

        private static IEnumerable<string> XamlFiles(string root)
            => Directory.EnumerateFiles(Path.Combine(root, "PadForge.App"), "*.xaml",
                SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        [Fact]
        public void EveryStaticResourceKeyIsDefinedSomewhere()
        {
            string root = RepoRoot();
            Assert.False(string.IsNullOrEmpty(root), "could not locate the repo root from the test output dir");

            var files = XamlFiles(root).ToList();
            Assert.True(files.Count > 10, $"only {files.Count} xaml files found; the scan is not reaching them");

            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in files)
                foreach (Match m in KeyDef.Matches(StripComments(File.ReadAllText(f))))
                    defined.Add(m.Groups[1].Value);
            Assert.True(defined.Count > 50, $"only {defined.Count} keys defined; the scan is not parsing them");

            var offenders = new List<string>();
            foreach (var f in files)
            {
                var text = StripComments(File.ReadAllText(f));
                foreach (Match m in StaticUse.Matches(text))
                {
                    string key = m.Groups[1].Value;
                    if (defined.Contains(key) || IsLibraryKey(key)) continue;
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(f)}:{line} -> {key}");
                }
            }

            Assert.True(offenders.Count == 0,
                "StaticResource names a key defined nowhere. This compiles and throws "
                + "XamlParseException when the page loads, which on a startup page means no "
                + "window at all:\n  " + string.Join("\n  ", offenders.Take(20)));
        }

        [Fact]
        public void PositiveControl_TheScanWouldCatchAMissingKey()
        {
            // Without this the test above passes on a scan that found no
            // uses at all, or on a `defined` set that swallowed everything.
            string root = RepoRoot();
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in XamlFiles(root))
                foreach (Match m in KeyDef.Matches(StripComments(File.ReadAllText(f))))
                    defined.Add(m.Groups[1].Value);

            // The exact key that shipped broken, and one that is real.
            Assert.DoesNotContain("DzValue", defined);
            Assert.Contains("DzValueEdit", defined);

            // And the use-scanner actually matches the markup shape.
            var hits = StaticUse.Matches("Style=\"{StaticResource DzValueEdit}\"");
            Assert.Single(hits);
            Assert.Equal("DzValueEdit", hits[0].Groups[1].Value);
        }
    }
}
