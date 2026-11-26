using System;
using System.IO;
using System.Linq;
using PadForge.Common.Recording;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The recording's XInput read-back says only what its read knows. It
    /// reads through the library SDL reads with, PadForge's OpenXInput fork
    /// in the app, which gives no virtual controller a number, by the numbers
    /// PadForge's own reader uses. The numbers a game reads are never read:
    /// PadForge loads no xinput1_4.dll of Windows' by path, since a library
    /// that numbers PadForge's pads writes their player lights.
    /// </summary>
    public class XInputReadBackLinesTests
    {
        private const string System32 = @"C:\Windows\System32";

        [Fact]
        public void TheFactsLine_NamesTheNumbering_AndSaysAGamesIsNotRead()
        {
            Assert.Equal("XInput read back with: xinput1_4.dll, PadForge's own, by the numbers PadForge's reader uses."
                + " It gives no virtual controller a number. The numbers a game reads are not read",
                XInputReadBack.LibraryLine(@"C:\ProgramData\PadForge\Natives\PadForge\abc\xinput1_4.dll", System32));
            Assert.Equal("XInput read back with: xinput1_4.dll, Windows' own, by the numbers PadForge's reader uses."
                + " The numbers a game reads are not read",
                XInputReadBack.LibraryLine(System32 + @"\xinput1_4.dll", System32));
            Assert.Equal("XInput read back with: xinput1_4.dll, not loaded yet. The numbers a game reads are not read",
                XInputReadBack.LibraryLine(null, System32));
        }

        /// <summary>No source line of the app or the engine names xinput1_4
        /// beside a way to load a library by path or the system folder.</summary>
        [Fact]
        public void TheApp_LoadsNoXInputLibraryByPath()
        {
            string root = RepoRoot();
            string[] loads = { "NativeLibrary", "LoadLibrary", "SystemDirectory", "SpecialFolder.System", "System32" };
            var found = new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line.Trim())))
                .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal)
                         && l.Text.Contains("xinput1_4", StringComparison.OrdinalIgnoreCase)
                         && loads.Any(w => l.Text.Contains(w, StringComparison.Ordinal)))
                .Select(l => $"{Path.GetFileName(l.File)}:{l.Line}: {l.Text}")
                .ToList();
            Assert.Empty(found);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
