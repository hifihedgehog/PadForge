using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using PadForge.Common.Input.Peripherals;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>The lighting backup registry is static. A reset in one test, or a settings
    /// file loaded by another, changes it under a picture a third is sending, and that
    /// picture then waits where the test expects it sent or failed. A test class that names
    /// the registry, or the rig that resets it, runs in the collection no other test runs
    /// beside. A class left outside it fails here by name.</summary>
    public class PeripheralOutputStaticsMembershipTests
    {
        private const string Collection = "PeripheralOutputStatics";

        private static readonly string[] SharedNames =
        {
            nameof(KeyboardProfileBackups),
            nameof(KeyboardFamilyRig),
        };

        private static string TestsFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "PadForge.Tests");
        }

        /// <summary>A line without its comment. A line comment starts at two slashes
        /// that no string is open across, which an even count of quotes before them
        /// shows.</summary>
        private static string WithoutComment(string line)
        {
            for (int at = line.IndexOf("//", StringComparison.Ordinal); at >= 0; at = line.IndexOf("//", at + 2, StringComparison.Ordinal))
            {
                int quotes = 0;
                for (int i = 0; i < at; i++)
                    if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) quotes++;
                if (quotes % 2 == 0) return line.Substring(0, at);
            }
            return line;
        }

        /// <summary>The top-level classes of a source file, each with its code. A class
        /// nested in another counts as code of the one around it, since a fake or a helper
        /// declared inside a test class runs in that class's tests.</summary>
        private static IEnumerable<(string Name, string Code)> TopLevelClasses(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            string[] lines = source.Replace("\r\n", "\n").Split('\n').Select(WithoutComment).ToArray();
            // A file-scoped namespace declares its classes at the margin, a block one four spaces in.
            string indent = lines.Any(l => Regex.IsMatch(l, @"^namespace\s+[\w.]+\s*;")) ? string.Empty : "    ";
            var declaration = new Regex("^" + indent + @"(?:\[[^\]]*\]\s*)*(?:public|internal)\s+(?:(?:static|sealed|abstract|partial)\s+)*class\s+(\w+)");
            string name = null;
            var code = new List<string>();
            foreach (string line in lines)
            {
                var match = declaration.Match(line);
                if (match.Success)
                {
                    if (name != null) yield return (name, string.Join("\n", code));
                    name = match.Groups[1].Value;
                    code.Clear();
                    continue;
                }
                if (name != null) code.Add(line);
            }
            if (name != null) yield return (name, string.Join("\n", code));
        }

        private static bool HasTests(Type type)
            => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(m => m.GetCustomAttributes(typeof(FactAttribute), true).Length > 0);

        private static string CollectionOf(Type type)
            => type.GetCustomAttributesData().FirstOrDefault(d => d.AttributeType == typeof(CollectionAttribute))
                ?.ConstructorArguments[0].Value as string;

        [Fact]
        public void TheCollectionRunsBesideNoOther()
        {
            var definition = typeof(PeripheralOutputStaticsCollection).GetCustomAttributesData()
                .Single(d => d.AttributeType == typeof(CollectionDefinitionAttribute));
            Assert.Equal(Collection, definition.ConstructorArguments[0].Value);
            Assert.True(typeof(PeripheralOutputStaticsCollection).GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization);
        }

        [Fact]
        public void EveryTestClassThatNamesTheBackupRegistryOrItsRig_IsInTheCollection()
        {
            var shared = new Regex(@"\b(?:" + string.Join("|", SharedNames) + @")\b");
            var naming = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string file in Directory.EnumerateFiles(TestsFolder(), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(TestsFolder(), file).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal)) continue;
                foreach (var (name, code) in TopLevelClasses(File.ReadAllText(file)))
                    if (shared.IsMatch(code)) naming.Add(name);
            }
            // This class names them to look for them.
            naming.Remove(nameof(PeripheralOutputStaticsMembershipTests));

            var types = typeof(PeripheralOutputStaticsMembershipTests).Assembly.GetTypes()
                .Where(t => !t.IsNested && naming.Contains(t.Name) && HasTests(t))
                .ToList();
            // The scan found the classes it is here for, by the registry's name and by
            // the rig's alone. A scan that finds none passes nothing.
            Assert.Contains(types, t => t == typeof(OtherProjectsLightingTests));
            Assert.Contains(types, t => t == typeof(AsusAuraUsbLightingTests));
            Assert.Contains(types, t => t == typeof(StoredPictureHandBackTests));
            Assert.Contains(types, t => t == typeof(HoltekLightingTests));

            var outside = types.Where(t => CollectionOf(t) != Collection).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(outside.Count == 0,
                "these test classes name the lighting backup registry or its rig and run beside other tests: " + string.Join(", ", outside));
        }

        [Fact]
        public void TheScan_ReadsCodeAndNotComments()
        {
            const string source = "namespace PadForge.Tests\n{\n"
                + "    /// <summary>Names KeyboardProfileBackups in a comment only.</summary>\n"
                + "    public class Quiet\n    {\n        // KeyboardProfileBackups.ResetForTest();\n        private const string Address = \"http://example\"; /* KeyboardProfileBackups */\n    }\n\n"
                + "    [Collection(\"X\")]\n    public sealed class Loud : IDisposable\n    {\n        private sealed class Inner\n        {\n            public void Go() => KeyboardProfileBackups.ResetForTest();\n        }\n    }\n}\n";
            var classes = TopLevelClasses(source).ToList();
            Assert.Equal(new[] { "Quiet", "Loud" }, classes.Select(c => c.Name));
            var shared = new Regex(@"\b" + nameof(KeyboardProfileBackups) + @"\b");
            Assert.DoesNotMatch(shared, classes[0].Code);
            Assert.Contains("http://example", classes[0].Code);
            Assert.Matches(shared, classes[1].Code);

            const string scoped = "namespace PadForge.Tests;\n\n[Collection(\"X\")]\npublic sealed class Margin\n{\n    void Go() => KeyboardFamilyRig.Equals(null, null);\n}\n";
            var margin = Assert.Single(TopLevelClasses(scoped));
            Assert.Equal("Margin", margin.Name);
            Assert.Contains(nameof(KeyboardFamilyRig), margin.Code);
        }
    }
}
