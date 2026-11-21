using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.SteamWorkshop.Translation;
using Xunit;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>Every reason a report can name is one an importer gives. A
    /// report lives only as long as the import that made it: the profile
    /// keeps <see cref="TranslationReport.ToSummaryString"/>, which names no
    /// reason, and nothing stores an entry. So a reason nothing gives can
    /// never be shown, and its constant and its string in every language are
    /// dead. Eleven such reasons were kept "for reports serialized by older
    /// translator versions" until 6.0.0, when they were deleted.</summary>
    public class ReasonVocabularyTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        [Fact]
        public void EveryReason_IsGivenBySomeImporter()
        {
            var reasons = typeof(TranslationReasons).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (f.Name, Key: (string)f.GetRawConstantValue()))
                .ToList();
            Assert.True(reasons.Count > 40, $"only {reasons.Count} reasons were read");

            // The translator library and the app's importers give reasons.
            // The vocabulary itself, the two lists that show a report and the
            // strings table only name them.
            string[] namesOnly =
            {
                "TranslationReport.cs", "CommunityConfigsPanel.xaml.cs", "ImportConfigDialog.xaml.cs", "Strings.Designer.cs",
            };
            char sep = Path.DirectorySeparatorChar;
            string given = string.Join("\n", new[] { "PadForge.SteamWorkshop", "PadForge.App" }
                .SelectMany(d => Directory.EnumerateFiles(Path.Combine(Root(), d), "*.cs", SearchOption.AllDirectories))
                .Where(p => !p.Contains(sep + "bin" + sep) && !p.Contains(sep + "obj" + sep)
                    && !namesOnly.Contains(Path.GetFileName(p)))
                .Select(File.ReadAllText));
            // Positive control: a reason the translator gives is found.
            Assert.Matches(@"\bTranslationReasons\.MenuEmitted\b", given);

            var unused = reasons
                .Where(r => !Regex.IsMatch(given, @"\bTranslationReasons\." + r.Name + @"\b")
                    && !given.Contains("\"" + r.Key + "\"", StringComparison.Ordinal))
                .Select(r => r.Name)
                .ToList();
            Assert.True(unused.Count == 0,
                "nothing gives these reasons, so no report can name them. Delete each with its strings: "
                + string.Join(", ", unused));
        }
    }
}
