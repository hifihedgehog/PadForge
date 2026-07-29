using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every localized string a view binds must actually exist.
    ///
    /// <para>XamlResourceKeyTests covers <c>{StaticResource ...}</c>, which
    /// throws XamlParseException at page load and so announces itself. A
    /// localized binding fails SILENTLY: <c>{Binding Pad_Foo,
    /// Source={x:Static strings:Strings.Instance}}</c> against a property that
    /// does not exist just renders empty, so a mistyped key ships as a blank
    /// label or a tooltip that never appears.</para>
    ///
    /// <para>Written after five reset-button tooltips shipped bound to
    /// Pad_ResetTapMaxMotion when the key was Pad_ResetTouchpadTapMaxMotion.
    /// Nothing failed. The buttons simply had no tooltip.</para>
    /// </summary>
    public class StringsBindingKeyTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static HashSet<string> EnglishKeys(string root)
        {
            var path = Path.Combine(root, "PadForge.App", "Resources", "Strings", "Strings.resx");
            Assert.True(File.Exists(path), path);
            return XDocument.Load(path).Root.Elements("data")
                .Select(e => (string)e.Attribute("name"))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static HashSet<string> DesignerProperties(string root)
        {
            var path = Path.Combine(root, "PadForge.App", "Resources", "Strings", "Strings.Designer.cs");
            Assert.True(File.Exists(path), path);
            var text = File.ReadAllText(path);
            return Regex.Matches(text, @"public\s+string\s+(\w+)\s*=>")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Bindings whose Source is Strings.Instance, i.e. the
        /// localized-string surface, in every view.</summary>
        private static IEnumerable<(string File, string Key)> StringBindings(string root)
        {
            var views = Directory.GetFiles(Path.Combine(root, "PadForge.App"), "*.xaml",
                                           SearchOption.AllDirectories);
            var rx = new Regex(@"\{Binding\s+(\w+)\s*,\s*Source=\{x:Static\s+strings:Strings\.Instance\}\}");
            foreach (var f in views)
            {
                var text = File.ReadAllText(f);
                foreach (Match m in rx.Matches(text))
                    yield return (Path.GetFileName(f), m.Groups[1].Value);
            }
        }

        [Fact]
        public void EveryLocalizedBindingResolvesToARealString()
        {
            var root = RepoRoot();
            var keys = EnglishKeys(root);
            var props = DesignerProperties(root);

            var broken = new List<string>();
            foreach (var (file, key) in StringBindings(root).Distinct())
            {
                if (!props.Contains(key))
                    broken.Add($"{file}: binds {key}, which is not a Strings property");
                else if (!keys.Contains(key))
                    broken.Add($"{file}: binds {key}, which has no entry in Strings.resx");
            }

            Assert.True(broken.Count == 0,
                "Localized bindings that render empty instead of failing loudly:"
                + Environment.NewLine + string.Join(Environment.NewLine, broken));
        }

        [Fact]
        public void PositiveControl_TheScanActuallyFindsBindings()
        {
            // Without this the test above passes trivially if the regex stops
            // matching, e.g. after a formatting change to the binding syntax.
            var root = RepoRoot();
            var found = StringBindings(root).Distinct().Count();
            Assert.True(found > 500, $"only {found} localized bindings scanned, the regex has drifted");
        }

        [Fact]
        public void PositiveControl_AMissingKeyWouldBeCaught()
        {
            var root = RepoRoot();
            var props = DesignerProperties(root);
            Assert.DoesNotContain("Pad_ResetTapMaxMotion", props);      // the wrong spelling
            Assert.Contains("Pad_ResetTouchpadTapMaxMotion", props);    // the right one
        }

        [Fact]
        public void EveryLocaleCarriesEveryEnglishKey()
        {
            // A key present in English and missing elsewhere renders empty for
            // that locale only, which is the same silent failure one language
            // removed.
            var root = RepoRoot();
            var dir = Path.Combine(root, "PadForge.App", "Resources", "Strings");
            var english = EnglishKeys(root);

            var gaps = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "Strings.*.resx"))
            {
                var theirs = XDocument.Load(f).Root.Elements("data")
                    .Select(e => (string)e.Attribute("name"))
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.Ordinal);
                var missing = english.Where(k => !theirs.Contains(k)).OrderBy(k => k).ToList();
                if (missing.Count > 0)
                    gaps.Add($"{Path.GetFileName(f)} missing {missing.Count}: "
                             + string.Join(", ", missing.Take(12)));
            }
            Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
        }
    }
}
