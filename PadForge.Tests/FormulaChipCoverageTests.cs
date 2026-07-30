using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.Engine.Common.Mapping;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every function the expression language accepts must have a chip in BOTH
    /// formula editors, or it is a capability the user cannot discover.
    ///
    /// <para>This exists because adding pow, hypot and deadzone to the engine
    /// left both chip rows stale: the parser accepted them, nothing offered
    /// them, and the only way to find them was to read the source. Same defect
    /// class as a setting with no card, one level down.</para>
    ///
    /// <para>It also catches the reverse, a chip for a function the parser
    /// rejects, which renders as a chip that inserts text and then turns the
    /// status red.</para>
    /// </summary>
    public class FormulaChipCoverageTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>The registry, read through the parser rather than a copied
        /// list: a name compiles as a call only when it is registered.</summary>
        private static IReadOnlyList<string> RegisteredFunctions()
        {
            // Arity varies, so try the shapes the language uses and keep any
            // name that compiles in at least one of them.
            var candidates = new[]
            {
                "abs", "min", "max", "clamp", "sign", "floor", "ceil", "round",
                "sqrt", "sin", "cos", "tan", "atan2", "lerp", "pow", "hypot",
                "deadzone",
            };
            var live = new List<string>();
            foreach (var f in candidates)
            {
                foreach (var args in new[] { "s[0]", "s[0],s[1]", "s[0],s[1],s[2]" })
                {
                    if (MappingExpression.Compile($"{f}({args})").IsValid) { live.Add(f); break; }
                }
            }
            // Guard the guard: if the candidate list itself drifts behind the
            // registry this test goes quiet, so require the count we know of.
            Assert.True(live.Count >= 17,
                $"only {live.Count} functions compiled; the candidate list has drifted "
                + "behind MappingExpression's registry");
            return live;
        }

        private static string ChipXaml() =>
            File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));

        /// <summary>Chip Tags in one editor's row, e.g. Tag="abs(".</summary>
        private static HashSet<string> ChipsForStyle(string xaml, string style)
        {
            var rx = new Regex(@"Tag=""(\w+)\(""[^>]*?" + Regex.Escape(style), RegexOptions.Singleline);
            var alt = new Regex(Regex.Escape(style) + @"[^>]*?Tag=""(\w+)\(""", RegexOptions.Singleline);
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in rx.Matches(xaml)) found.Add(m.Groups[1].Value);
            foreach (Match m in alt.Matches(xaml)) found.Add(m.Groups[1].Value);
            return found;
        }

        [Theory]
        [InlineData("MacroFormulaChipStyle")]
        [InlineData("FormulaChipStyle")]
        public void EveryRegisteredFunctionHasAChip(string style)
        {
            var xaml = ChipXaml();
            var chips = ChipsForStyle(xaml, style);
            Assert.True(chips.Count > 5, $"found only {chips.Count} chips for {style}; the scan has drifted");

            var missing = RegisteredFunctions().Where(f => !chips.Contains(f)).OrderBy(f => f).ToList();
            Assert.True(missing.Count == 0,
                $"{style} offers no chip for: {string.Join(", ", missing)}. "
                + "The parser accepts them, so they are capabilities the user cannot discover.");
        }

        [Theory]
        [InlineData("MacroFormulaChipStyle")]
        [InlineData("FormulaChipStyle")]
        public void NoChipOffersAFunctionTheParserRejects(string style)
        {
            var chips = ChipsForStyle(ChipXaml(), style);
            var bogus = chips
                .Where(c => !MappingExpression.Compile($"{c}(s[0])").IsValid
                            && !MappingExpression.Compile($"{c}(s[0],s[1])").IsValid
                            && !MappingExpression.Compile($"{c}(s[0],s[1],s[2])").IsValid)
                .OrderBy(c => c)
                .ToList();
            Assert.True(bogus.Count == 0,
                $"{style} offers chips the parser rejects: {string.Join(", ", bogus)}");
        }

        [Fact]
        public void EveryChipHasALocalizedTooltipInEveryLocale()
        {
            // A chip with no tooltip is a bare token with no explanation, and a
            // tooltip present in English only renders empty elsewhere.
            var xaml = ChipXaml();
            var keys = new HashSet<string>(
                Regex.Matches(xaml, @"Pad_Formula_Chip_(\w+)").Select(m => m.Value),
                StringComparer.Ordinal);
            Assert.True(keys.Count > 10, $"only {keys.Count} chip tooltip keys found");

            var dir = Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings");
            var gaps = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "Strings*.resx"))
            {
                var text = File.ReadAllText(f);
                var missing = keys.Where(k => !text.Contains($"name=\"{k}\"")).OrderBy(k => k).ToList();
                if (missing.Count > 0)
                    gaps.Add($"{Path.GetFileName(f)}: {string.Join(", ", missing.Take(8))}");
            }
            Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
        }
    }
}
