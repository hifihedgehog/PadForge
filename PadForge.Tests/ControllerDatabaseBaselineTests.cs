using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PadForge.Common.Input;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The list of what this release carries of the community controller
    /// database (gamecontrollerdb_baseline.txt, embedded, #597). It is
    /// written from the SDL fork's sources by
    /// tools/gen_controller_database_baseline.py, and SDL3.dll comes from
    /// the fork as a binary, so a take that brings new libraries and skips
    /// the script would ship a list that says one thing while SDL does
    /// another. Each line is held here to both SDL3.dll files: SDL's rows
    /// are plain text inside the library.
    /// </summary>
    public class ControllerDatabaseBaselineTests
    {
        private static string ResourcesRoot() =>
            Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "Resources");

        public static IEnumerable<object[]> Architectures()
        {
            string sdl = Path.Combine(ResourcesRoot(), "SDL3");
            foreach (string dir in Directory.GetDirectories(sdl))
                if (System.IO.File.Exists(Path.Combine(dir, "SDL3.dll")))
                    yield return new object[] { Path.GetFileName(dir) };
        }

        /// <summary>A mapping row as the library holds it: 32 hex digits, a
        /// comma, a name and a comma.</summary>
        private static readonly Regex LibraryRow = new(@"(?<![0-9a-f])([0-9a-f]{32}),[^,\x00]{1,128},", RegexOptions.Compiled);

        /// <summary>What SDL_GetJoystickGUIDInfo reads from an ID in the
        /// standard form, or (0, 0).</summary>
        private static (int Vendor, int Product) VendorProduct(string id)
        {
            byte[] data = Convert.FromHexString(id);
            int Word(int i) => data[i * 2] | (data[i * 2 + 1] << 8);
            return (Word(0) < 0x20 || Word(0) == 0xFF) && Word(3) == 0 && Word(5) == 0 ? (Word(2), Word(4)) : (0, 0);
        }

        [Fact]
        public void TheEmbeddedBaseline_IsTheFileInTheRepo_AndReadsWhole()
        {
            var embedded = ControllerDatabaseBaseline.Embedded;
            var onDisk = ControllerDatabaseBaseline.Parse(
                System.IO.File.ReadAllText(Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "gamecontrollerdb_baseline.txt")));
            Assert.Equal(onDisk.Count, embedded.Count);
            Assert.Equal(onDisk.PinCommit, embedded.PinCommit);
            Assert.Equal(onDisk.ForkCommit, embedded.ForkCommit);

            Assert.Matches("^[0-9a-f]{40}$", embedded.PinCommit);
            Assert.Matches("^[0-9a-f]{40}$", embedded.ForkCommit);
            Assert.Matches("^[0-9a-f]{64}$", embedded.PinnedSha256);
            Assert.Equal(DateTimeKind.Utc, embedded.PinTimeUtc.Kind);
            Assert.True(embedded.PinTimeUtc > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.True(embedded.KeptRows > 0, "the fork carries no community row");
            Assert.True(embedded.SdlWindowsRows > 0, "SDL's Windows section has no row");
            // The pinned copy has one Windows row with no ID, the xinput row.
            Assert.InRange(embedded.PinnedIds.Count, embedded.KeptRows, embedded.WindowsRows);
            Assert.Equal(embedded.Entries.Count(e => e.Value.Verdict == ControllerDatabaseVerdict.Kept), embedded.KeptRows);
            // A row the fork kept is a pinned row, so it has a hash.
            Assert.All(embedded.Entries.Where(e => e.Value.Verdict == ControllerDatabaseVerdict.Kept),
                e => Assert.NotNull(e.Value.RowHash));
        }

        /// <summary>
        /// Every line against the library. An ID the fork kept is in it. An
        /// ID of SDL's Windows section is in it, or another row for its
        /// vendor and product is. An ID SDL maps on other platforms only, or
        /// one the fork left out, is not, and no row of the library is for
        /// its vendor and product. And the library holds as many rows as the
        /// list counts: SDL's own Windows section and the community rows the
        /// fork kept.
        /// </summary>
        [Theory]
        [MemberData(nameof(Architectures))]
        public void EveryLine_IsWhatThatSdlLibraryCarries(string arch)
        {
            var baseline = ControllerDatabaseBaseline.Embedded;
            string library = Encoding.Latin1.GetString(System.IO.File.ReadAllBytes(Path.Combine(ResourcesRoot(), "SDL3", arch, "SDL3.dll")));
            var rows = LibraryRow.Matches(library).Select(m => m.Groups[1].Value).ToList();
            var ids = new HashSet<string>(rows, StringComparer.Ordinal);
            var pairs = new HashSet<(int, int)>(ids.Select(VendorProduct).Where(p => p.Vendor != 0 && p.Product != 0));
            // Positive control: the scan reads the library's rows.
            Assert.True(rows.Count > 100, "the scan found " + rows.Count + " rows in " + arch + "/SDL3.dll");
            Assert.DoesNotContain(Id(0x1234, 0xBEAD), ids);

            Assert.True(rows.Count == baseline.SdlWindowsRows + baseline.KeptRows,
                arch + "/SDL3.dll holds " + rows.Count + " rows, and the baseline counts " + baseline.SdlWindowsRows + " of SDL's and "
                + baseline.KeptRows + " the fork kept. Run tools/gen_controller_database_baseline.py against the fork commit these libraries carry.");

            var wrong = new List<string>();
            foreach (var (id, entry) in baseline.Entries)
            {
                var pair = VendorProduct(id);
                bool rowForPair = pair.Vendor != 0 && pair.Product != 0 && pairs.Contains(pair);
                bool holds = entry.Verdict switch
                {
                    ControllerDatabaseVerdict.Kept => ids.Contains(id),
                    ControllerDatabaseVerdict.SdlWindows => ids.Contains(id) || rowForPair,
                    _ => !ids.Contains(id) && !rowForPair,
                };
                if (!holds) wrong.Add(entry.Verdict + " " + id);
            }
            Assert.True(wrong.Count == 0, arch + "/SDL3.dll does not carry what the baseline says for " + wrong.Count + " IDs: "
                + string.Join(", ", wrong.Take(8)) + ". Run tools/gen_controller_database_baseline.py against the fork commit these libraries carry.");
        }

        [Fact]
        public void BothLibrariesAreChecked()
        {
            var archs = Architectures().Select(a => (string)a[0]).ToList();
            Assert.Contains("x64", archs);
            Assert.Contains("arm64", archs);
        }

        /// <summary>What PadForge reads out of the box, as the issue counts
        /// it: SDL's 313 Windows rows, the 286 community rows the fork kept
        /// at this pin, and PadForge's own 3. The numbers move with an SDL
        /// take or a row of PadForge's own, and this is where they are
        /// read.</summary>
        [Fact]
        public void TheBuiltInCount_IsSdlsRowsTheKeptRowsAndPadForgesOwn()
        {
            var baseline = ControllerDatabaseBaseline.Embedded;
            var own = ControllerDatabaseUse.OwnRows;
            int count = ControllerDatabaseStatus.BuiltInCount(baseline, own);
            Assert.True(count == 602, "PadForge reads " + count + " mappings out of the box: " + baseline.SdlWindowsRows + " of SDL's, "
                + baseline.KeptRows + " community rows the fork kept and " + own.Count + " of its own. Issue #597 counts 602, and the README names the kept share.");
            Assert.Equal(869, baseline.WindowsRows);
        }

        [Fact]
        public void ABaseline_ReadsItsHeaderAndItsLines()
        {
            string kept = Row(0x1000, 0x0001), sdl = Row(0x1000, 0x0002), elsewhere = Row(0x1000, 0x0003), leftOut = Row(0x1000, 0x0004);
            var baseline = Baseline(('K', kept), ('S', sdl), ('O', elsewhere), ('N', leftOut), ('S', Id(0x1000, 0x0005)));
            Assert.Equal(Fork, baseline.ForkCommit);
            Assert.Equal(Pin, baseline.PinCommit);
            Assert.Equal(new DateTime(2026, 9, 24, 16, 11, 31, DateTimeKind.Utc), baseline.PinTimeUtc);
            Assert.Equal(DateTimeKind.Utc, baseline.PinTimeUtc.Kind);
            Assert.Equal(313, baseline.SdlWindowsRows);
            Assert.Equal(1, baseline.KeptRows);
            Assert.Equal(5, baseline.Count);
            Assert.Equal(4, baseline.PinnedIds.Count);

            Assert.True(baseline.TryFind(Id(0x1000, 0x0001), out var k));
            Assert.Equal(new ControllerDatabaseBaselineEntry(ControllerDatabaseVerdict.Kept, ControllerDatabaseRow.HashOf(kept)), k);
            Assert.True(baseline.TryFind(Id(0x1000, 0x0002), out var s));
            Assert.Equal(ControllerDatabaseVerdict.SdlWindows, s.Verdict);
            Assert.True(baseline.TryFind(Id(0x1000, 0x0003), out var o));
            Assert.Equal(ControllerDatabaseVerdict.SdlElsewhere, o.Verdict);
            Assert.True(baseline.TryFind(Id(0x1000, 0x0004), out var n));
            Assert.Equal(ControllerDatabaseVerdict.LeftOut, n.Verdict);
            Assert.True(baseline.TryFind(Id(0x1000, 0x0005), out var bare));
            Assert.Equal(new ControllerDatabaseBaselineEntry(ControllerDatabaseVerdict.SdlWindows, null), bare);
            Assert.False(baseline.TryFind(Id(0x1000, 0x0006), out _));
            Assert.False(baseline.TryFind(null, out _));
        }

        /// <summary>The file is written by a script, so a line the reader
        /// does not know is a defect and not something to skip.</summary>
        [Theory]
        [InlineData("X 03000000001000000100000000000000 0123456789abcdef")]
        [InlineData("K 0300000000100000010000000000000 0123456789abcdef")]
        [InlineData("K 03000000001000000100000000000000 0123456789abcde")]
        [InlineData("K 03000000001000000100000000000000")]
        [InlineData("pin 2222222222222222222222222222222222222222 yesterday")]
        [InlineData("format one")]
        public void ALineTheReaderDoesNotKnow_IsAnError(string line)
        {
            const string header = "format 1\nfork 1111111111111111111111111111111111111111\n"
                + "pin 2222222222222222222222222222222222222222 2026-09-24T16:11:31Z\n"
                + "sha256 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nwindows-rows 1\nsdl-windows-rows 1\n";
            Assert.NotNull(ControllerDatabaseBaseline.Parse(header));
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseBaseline.Parse(header + line + "\n"));
        }

        [Fact]
        public void ABaselineWithNoHeader_OrARepeatedId_IsAnError()
        {
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseBaseline.Parse("# nothing\n"));
            Assert.Throws<InvalidDataException>(() => ControllerDatabaseBaseline.Parse("format 2\n"));
            string kept = Row(0x1000, 0x0001);
            Assert.Throws<InvalidDataException>(() => Baseline(('K', kept), ('S', kept)));
        }

        /// <summary>The routine that writes the list: the script is in the
        /// repo, says when to run it, and writes this file.</summary>
        [Fact]
        public void TheScript_NamesItsTwoRunsAndItsOutput()
        {
            string script = OffLimitsSource.Read("tools/gen_controller_database_baseline.py");
            Assert.Contains("At each SDL take", script, StringComparison.Ordinal);
            Assert.Contains("At each release prep", script, StringComparison.Ordinal);
            Assert.Contains("\"gamecontrollerdb_baseline.txt\"", script, StringComparison.Ordinal);
            Assert.Contains("--since-pin", script, StringComparison.Ordinal);
            // It changes neither clone: every git command goes through the
            // one call, which takes no lock, and each is one that reads.
            Assert.Single(Regex.Matches(script, @"subprocess\.run\("));
            Assert.Contains("[\"git\", \"--no-optional-locks\", \"-C\", clone, *args]", script, StringComparison.Ordinal);
            var commands = Regex.Matches(script, @"\bgit\(\s*\w+\s*,\s*""([a-z-]+)""").Select(m => m.Groups[1].Value).ToList();
            Assert.True(commands.Count >= 4, "the script's git commands were not found");
            Assert.All(commands, c => Assert.Contains(c, new[] { "rev-parse", "show" }));
            string project = OffLimitsSource.Read("PadForge.App/PadForge.App.csproj");
            Assert.Contains("<EmbeddedResource Include=\"gamecontrollerdb_baseline.txt\" />", project, StringComparison.Ordinal);
        }
    }
}
