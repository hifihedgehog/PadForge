using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The controller database section of PadForge.xml (#597): which rows
    /// of a download are kept and as what, how the section is written and
    /// read back, and what a load under another release drops. All of it is
    /// plain logic over a baseline, so no test here touches the settings
    /// statics or a file.
    /// </summary>
    public class ControllerDatabaseSectionTests
    {
        private const string Version = "6.0.1 (r5568@7992e39)";
        private static readonly DateTime Fetched = new(2026, 11, 23, 14, 17, 35, DateTimeKind.Utc);

        // The pinned rows: one the fork kept, one SDL maps in its Windows
        // section, one SDL maps elsewhere only, one the fork left out.
        private static readonly string KeptRow = Row(0x1000, 0x0001, "Kept Pad");
        private static readonly string SdlRow = Row(0x1000, 0x0002, "SDL Pad");
        private static readonly string ElsewhereRow = Row(0x1000, 0x0003, "Mac Pad");
        private static readonly string LeftOutRow = Row(0x1000, 0x0004, "Driver Pad");
        private static readonly string SdlOnlyId = Id(0x1000, 0x0005);
        private static readonly string SdlRowTwo = Row(0x1000, 0x0006, "SDL Pad Two");

        private static ControllerDatabaseBaseline Pinned() => Baseline(
            ('K', KeptRow), ('S', SdlRow), ('O', ElsewhereRow), ('N', LeftOutRow), ('S', SdlOnlyId), ('S', SdlRowTwo));

        private static string Changed(string row) => row.Replace("a:b0", "a:b7");

        private static ControllerDatabaseDownload Download(ControllerDatabaseBaseline baseline, params string[] rows)
        {
            // The pinned rows that keep nothing as written are always there,
            // as written or changed, so the file passes the known-row count.
            // The row SDL maps elsewhere only is kept as written, so a test
            // that wants it names it.
            var all = new List<string>(rows);
            foreach (string pinned in new[] { KeptRow, SdlRow, SdlRowTwo, LeftOutRow })
                if (!all.Any(r => r.StartsWith(pinned.Substring(0, 33), StringComparison.Ordinal))) all.Add(pinned);
            var read = ControllerDatabaseDownload.Read(CommunityFile(all), baseline);
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            return read;
        }

        private static ControllerDatabaseSection Stored(ControllerDatabaseBaseline baseline, ControllerDatabaseSection previous, params string[] rows) =>
            ControllerDatabaseSection.FromDownload(Download(baseline, rows), "W/\"tag-1\"", Fetched, baseline, Version, previous);

        // ── The delta ──

        /// <summary>The first outcome: a row the same as the pinned one is
        /// carried already when the fork kept it, or when SDL maps the
        /// controller on Windows or the fork leaves it out, and nothing is
        /// kept. A row SDL maps on other platforms only is the exception:
        /// the fork leaves it out, so this release has no Windows row for the
        /// controller, and the row is New as it was pinned.</summary>
        [Fact]
        public void ARowTheSameAsThePinnedOne_IsNotKept_UnlessSdlMapsItElsewhereOnly()
        {
            Assert.Empty(Stored(Pinned(), null).Rows);
            var section = Stored(Pinned(), null, ElsewhereRow);
            var row = Assert.Single(section.Rows);
            Assert.Equal(ElsewhereRow, row.Row.Text);
            Assert.Equal(ControllerDatabaseRowKind.New, row.Kind);
            Assert.Equal(1, section.NewRows);
            Assert.Equal(0, section.ChangedRows);
            foreach (string pinned in new[] { KeptRow, SdlRow, LeftOutRow })
                Assert.Null(ControllerDatabaseSection.Classify(Pinned(), Parsed(pinned)));
            Assert.Equal(ControllerDatabaseRowKind.New, ControllerDatabaseSection.Classify(Pinned(), Parsed(ElsewhereRow)));
        }

        /// <summary>A controller PadForge's own file has a row for takes no
        /// downloaded row, whatever its verdict: the release carries it. The
        /// GameSir G7 Pro is the case today, a controller SDL maps elsewhere
        /// only whose Windows row PadForge carries itself.</summary>
        [Fact]
        public void ARowForAControllerOfPadForgesOwnFile_IsNotKept()
        {
            string unknown = Row(0x2000, 0x0001, "Brand New Pad");
            var own = new[]
            {
                ElsewhereRow.Replace("Mac Pad", "PadForge's Own Pad"),
                // An own row with a name hash maps one variant on purpose,
                // and no downloaded row is kept for the controller.
                "0300d823" + unknown.Substring(8, 24) + ",One Variant,a:b0,platform:Windows,",
                Changed(KeptRow),
            };
            var baseline = Pinned().WithOwnRows(own);
            Assert.True(baseline.HasOwnRow(Id(0x1000, 0x0003)));
            Assert.True(baseline.HasOwnRow(Id(0x2000, 0x0001)));
            Assert.False(baseline.HasOwnRow(Id(0x1000, 0x0002)));
            Assert.False(baseline.HasOwnRow(null));
            Assert.Empty(Stored(baseline, null, Changed(ElsewhereRow), unknown, Changed(KeptRow)).Rows);
            // The list itself is the same list.
            Assert.Equal(Pinned().Count, baseline.Count);
            Assert.Equal(Pin, baseline.PinCommit);
            Assert.False(Pinned().HasOwnRow(Id(0x1000, 0x0003)));

            // The list this build carries knows PadForge's own file: the PS
            // VR Aim Controller takes no row. The GameSir G7 Pro, which SDL
            // maps elsewhere only, is a row the fork carries since SDL#53, and
            // PadForge's file has none for it any more.
            const string aim = "030000004c050000b20b000000000000", g7 = "03000000373500002210000000000000";
            var embedded = ControllerDatabaseBaseline.Embedded;
            Assert.True(embedded.HasOwnRow(aim));
            Assert.Null(ControllerDatabaseSection.Classify(embedded, Parsed(aim + ",Sony PS VR Aim Controller,a:b1,b:b2,platform:Windows,")));
            Assert.True(embedded.TryFind(g7, out var entry) && entry.Verdict == ControllerDatabaseVerdict.Kept);
            Assert.False(embedded.HasOwnRow(g7));
        }

        /// <summary>The second: a changed row the fork kept is Changed.</summary>
        [Fact]
        public void AChangedRowTheForkKept_IsKeptAsChanged()
        {
            var section = Stored(Pinned(), null, Changed(KeptRow));
            var row = Assert.Single(section.Rows);
            Assert.Equal(ControllerDatabaseRowKind.Changed, row.Kind);
            Assert.Equal(Changed(KeptRow), row.Row.Text);
            Assert.False(row.InUse);
            Assert.Equal(1, section.ChangedRows);
            Assert.False(section.TryFindNew(row.Row.Guid, out _));
        }

        /// <summary>The third: a row SDL maps on other platforms only,
        /// changed or not, and a row for a controller the pinned copy does
        /// not have, are New.</summary>
        [Fact]
        public void ARowSdlMapsElsewhere_AndARowForAnUnknownId_AreKeptAsNew()
        {
            string unknown = Row(0x2000, 0x0001, "Brand New Pad");
            var section = Stored(Pinned(), null, Changed(ElsewhereRow), unknown);
            Assert.Equal(2, section.Rows.Count);
            Assert.All(section.Rows, r => Assert.Equal(ControllerDatabaseRowKind.New, r.Kind));
            Assert.Equal(2, section.NewRows);
            Assert.Equal(0, section.ChangedRows);
            Assert.True(section.TryFindNew(Id(0x1000, 0x0003), out var elsewhere));
            Assert.Equal(Changed(ElsewhereRow), elsewhere.Row.Text);
            Assert.True(section.TryFindNew(Id(0x2000, 0x0001), out var added));
            Assert.Equal(unknown, added.Row.Text);
        }

        /// <summary>The fourth: a controller SDL maps in its own Windows
        /// section, or one the fork leaves out, takes no row, changed or
        /// not. That holds for an ID of SDL's section the pinned copy has no
        /// row for, which is how the community's later row for the Flydigi
        /// Vader 5S Pro arrives.</summary>
        [Fact]
        public void ARowForAControllerSdlMapsOrTheForkLeavesOut_IsNotKept()
        {
            string sdlOnly = SdlOnlyId + ",Flydigi Vader 5S Pro,a:b11,b:b10,platform:Windows,";
            var section = Stored(Pinned(), null, Changed(SdlRow), Changed(LeftOutRow), sdlOnly);
            Assert.Empty(section.Rows);
            Assert.Null(ControllerDatabaseSection.Classify(Pinned(), Parsed(Changed(SdlRow))));
            Assert.Null(ControllerDatabaseSection.Classify(Pinned(), Parsed(Changed(LeftOutRow))));
            Assert.Null(ControllerDatabaseSection.Classify(Pinned(), Parsed(sdlOnly)));
        }

        [Fact]
        public void TheSection_HoldsWhereAndWhenItCameFrom_AndItsRowsById()
        {
            var baseline = Pinned();
            var section = Stored(baseline, null, Row(0x3000, 0x0001), Changed(KeptRow), Row(0x2000, 0x0001));
            Assert.Equal(Fetched, section.FetchedUtc);
            Assert.Equal(DateTimeKind.Utc, section.FetchedUtc.Kind);
            Assert.Equal("W/\"tag-1\"", section.ETag);
            Assert.Matches("^[0-9a-f]{64}$", section.Sha256);
            Assert.Equal(6, section.WindowsRows);
            Assert.Equal(Pin, section.BaselinePin);
            Assert.Equal(Version, section.AppVersion);
            Assert.Equal(new[] { Id(0x1000, 0x0001), Id(0x2000, 0x0001), Id(0x3000, 0x0001) }, section.Rows.Select(r => r.Row.Guid));
        }

        /// <summary>The time is kept to the second, as the file writes it.</summary>
        [Fact]
        public void TheFetchTime_IsKeptToTheSecond()
        {
            var baseline = Pinned();
            var section = ControllerDatabaseSection.FromDownload(Download(baseline), null, Fetched.AddMilliseconds(987), baseline, Version, null);
            Assert.Equal(Fetched, section.FetchedUtc);
            Assert.Null(section.ETag);
        }

        // ── A row in use ──

        private static ControllerDatabaseSection InUse(ControllerDatabaseSection section, string id)
        {
            var marked = section.WithRowInUse(id);
            Assert.NotSame(section, marked);
            return marked;
        }

        [Fact]
        public void ARowInUse_KeepsItsMark_ThroughTheNextDownload()
        {
            string unknown = Row(0x2000, 0x0001, "Brand New Pad");
            var first = InUse(Stored(Pinned(), null, unknown, Row(0x2000, 0x0002)), Id(0x2000, 0x0001));
            Assert.True(first.TryFindNew(Id(0x2000, 0x0001), out var marked) && marked.InUse);
            Assert.True(first.TryFindNew(Id(0x2000, 0x0002), out var other) && !other.InUse);

            // The community changed the row since. The controller is read
            // through the new text, and the mark stays.
            string improved = unknown.Replace("a:b0", "a:b2");
            var second = Stored(Pinned(), first, improved, Row(0x2000, 0x0002));
            Assert.True(second.TryFindNew(Id(0x2000, 0x0001), out var again));
            Assert.True(again.InUse);
            Assert.Equal(improved, again.Row.Text);
            Assert.True(second.TryFindNew(Id(0x2000, 0x0002), out other) && !other.InUse);
        }

        /// <summary>A row a controller is read through stays when the
        /// community's copy drops it. A row nothing uses goes with it.</summary>
        [Fact]
        public void ARowInUse_StaysWhenTheCommunityDropsIt_AndAnUnusedOneGoes()
        {
            var first = InUse(Stored(Pinned(), null, Row(0x2000, 0x0001, "Used"), Row(0x2000, 0x0002, "Unused")), Id(0x2000, 0x0001));
            var second = Stored(Pinned(), first);
            var kept = Assert.Single(second.Rows);
            Assert.Equal(Id(0x2000, 0x0001), kept.Row.Guid);
            Assert.True(kept.InUse);
            Assert.Equal(ControllerDatabaseRowKind.New, kept.Kind);
        }

        /// <summary>The community went back to the pinned text of a row SDL
        /// maps elsewhere only. The fork left that row out, so this release
        /// still does not carry it: the pinned text is kept as New, and the
        /// controller read through the row keeps its mark.</summary>
        [Fact]
        public void ARowInUse_StaysWhenTheCommunityGoesBackToThePinnedTextTheForkLeftOut()
        {
            var first = InUse(Stored(Pinned(), null, Changed(ElsewhereRow)), Id(0x1000, 0x0003));
            var second = Stored(Pinned(), first, ElsewhereRow);
            var kept = Assert.Single(second.Rows);
            Assert.Equal(ElsewhereRow, kept.Row.Text);
            Assert.True(kept.InUse);
            // With no controller on it, the pinned text is kept unmarked.
            var unused = Assert.Single(Stored(Pinned(), Stored(Pinned(), null, Changed(ElsewhereRow)), ElsewhereRow).Rows);
            Assert.Equal(ElsewhereRow, unused.Row.Text);
            Assert.False(unused.InUse);
        }

        [Fact]
        public void MarkingARow_ChangesNothingForAChangedRow_AnUnknownId_OrARowMarkedAlready()
        {
            var section = Stored(Pinned(), null, Changed(KeptRow), Row(0x2000, 0x0001));
            Assert.Same(section, section.WithRowInUse(Id(0x1000, 0x0001)));
            Assert.Same(section, section.WithRowInUse(Id(0x9999, 0x0001)));
            Assert.Same(section, section.WithRowInUse(null));
            var marked = InUse(section, Id(0x2000, 0x0001));
            Assert.Same(marked, marked.WithRowInUse(Id(0x2000, 0x0001)));
            // The section it was made from is as it was.
            Assert.True(section.TryFindNew(Id(0x2000, 0x0001), out var before) && !before.InUse);
            Assert.Equal(section.ETag, marked.ETag);
            Assert.Equal(section.FetchedUtc, marked.FetchedUtc);
            Assert.Equal(2, marked.Rows.Count);
        }

        // ── The file ──

        private static readonly XmlSerializer Serializer = new(typeof(SettingsFileData));

        private static string Xml(SettingsFileData data) =>
            Encoding.UTF8.GetString(PadForge.Engine.Common.XmlSafeText.Serialize(Serializer, data));

        private static SettingsFileData FromXml(string xml)
        {
            using var reader = new StringReader(xml);
            return (SettingsFileData)Serializer.Deserialize(reader);
        }

        [Fact]
        public void TheSection_IsOneElementUnderTheRoot_WithAnAttributeEachAndAChildPerRow()
        {
            var baseline = Pinned();
            var section = InUse(Stored(baseline, null, Changed(KeptRow), Row(0x2000, 0x0001, "Tom & Jerry <Pad>"), Row(0x2000, 0x0002)),
                Id(0x2000, 0x0001));
            string xml = Xml(new SettingsFileData { ControllerDatabase = section.ToData() });

            var element = Assert.Single(XDocument.Parse(xml).Root.Elements(), e => e.Name.LocalName == "ControllerDatabase");
            Assert.Equal(ControllerDatabaseAddress.Community, (string)element.Attribute("Source"));
            Assert.Null(element.Attribute("DownloadFrom"));
            Assert.Equal("2026-11-23T14:17:35Z", (string)element.Attribute("FetchedUtc"));
            Assert.Equal("W/\"tag-1\"", (string)element.Attribute("ETag"));
            Assert.Equal(section.Sha256, (string)element.Attribute("Sha256"));
            Assert.Equal("6", (string)element.Attribute("WindowsRows"));
            Assert.Equal(Pin, (string)element.Attribute("BaselinePin"));
            Assert.Equal(Version, (string)element.Attribute("AppVersion"));
            var rows = element.Elements().ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal("Row", r.Name.LocalName));
            Assert.Equal(new[] { "Changed", "New", "New" }, rows.Select(r => (string)r.Attribute("Kind")));
            // The mark is written only where it is set.
            Assert.Equal(new[] { null, "true", null }, rows.Select(r => (string)r.Attribute("InUse")));
            // Each row's text is the community's, character for character.
            Assert.Equal(section.Rows.Select(r => r.Row.Text), rows.Select(r => r.Value));

            var back = ControllerDatabaseSection.FromData(FromXml(xml).ControllerDatabase, baseline, Version, out bool changed);
            Assert.False(changed);
            Assert.Equal(section.FetchedUtc, back.FetchedUtc);
            Assert.Equal(section.ETag, back.ETag);
            Assert.Equal(section.Sha256, back.Sha256);
            Assert.Equal(section.WindowsRows, back.WindowsRows);
            Assert.Equal(section.BaselinePin, back.BaselinePin);
            Assert.Equal(section.AppVersion, back.AppVersion);
            Assert.Equal(section.Rows.Select(r => (r.Row.Text, r.Kind, r.InUse)), back.Rows.Select(r => (r.Row.Text, r.Kind, r.InUse)));
        }

        /// <summary>No section, no element: the file of a PadForge that
        /// never updated the database has nothing of the feature in it.</summary>
        [Fact]
        public void WithNoSection_TheFileHasNoElement()
        {
            string xml = Xml(new SettingsFileData());
            Assert.DoesNotContain("ControllerDatabase", xml, StringComparison.Ordinal);
            Assert.Null(FromXml(xml).ControllerDatabase);
            Assert.Null(ControllerDatabaseSection.FromData(null, Pinned(), Version, out bool changed));
            Assert.False(changed);
        }

        /// <summary>A file from before the feature loads with no section.</summary>
        [Fact]
        public void AnOldFile_LoadsWithNoSection()
        {
            const string old = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<PadForgeSettings>\n  <Devices />\n  <UserSettings />\n"
                + "  <AppSettings>\n    <PollingRateHz>1000</PollingRateHz>\n  </AppSettings>\n</PadForgeSettings>";
            var data = FromXml(old);
            Assert.NotNull(data.AppSettings);
            Assert.Null(data.ControllerDatabase);
        }

        /// <summary>A stored download that kept no row is still a section:
        /// it says when the database was last fetched, and its tag saves the
        /// next download.</summary>
        [Fact]
        public void ASectionWithNoRows_IsStillWritten()
        {
            var baseline = Pinned();
            var section = Stored(baseline, null);
            Assert.Empty(section.Rows);
            string xml = Xml(new SettingsFileData { ControllerDatabase = section.ToData() });
            var element = Assert.Single(XDocument.Parse(xml).Root.Elements(), e => e.Name.LocalName == "ControllerDatabase");
            Assert.Empty(element.Elements());
            var back = ControllerDatabaseSection.FromData(FromXml(xml).ControllerDatabase, baseline, Version, out bool changed);
            Assert.False(changed);
            Assert.Empty(back.Rows);
            Assert.Equal("W/\"tag-1\"", back.ETag);
        }

        // ── A load under another release ──

        private static ControllerDatabaseData Data(ControllerDatabaseSection section) => FromXml(
            Xml(new SettingsFileData { ControllerDatabase = section.ToData() })).ControllerDatabase;

        /// <summary>The prune a new version owes. Its SDL carries the row
        /// that was Changed, maps the controller that was New in its own
        /// Windows section, and leaves another out: all three go. A row it
        /// still lacks stays.</summary>
        [Fact]
        public void ALoadUnderANewerRelease_DropsTheRowsItNowCarriesOrSdlNowReads()
        {
            string newPad = Row(0x2000, 0x0001, "Now SDL's"), driverPad = Row(0x2000, 0x0002, "Now A Driver's"), still = Row(0x2000, 0x0003, "Still New");
            var stored = Stored(Pinned(), null, Changed(KeptRow), newPad, driverPad, still);
            Assert.Equal(4, stored.Rows.Count);

            const string newPin = "3333333333333333333333333333333333333333";
            var newer = Baseline(newPin, "2026-12-15T09:00:00Z",
                ('K', Changed(KeptRow)), ('S', SdlRow), ('O', ElsewhereRow), ('N', LeftOutRow),
                ('S', Id(0x2000, 0x0001)), ('N', driverPad));
            var loaded = ControllerDatabaseSection.FromData(Data(stored), newer, "6.1.0 (r5700@abcdef0)", out bool changed);
            Assert.True(changed);
            var kept = Assert.Single(loaded.Rows);
            Assert.Equal(still, kept.Row.Text);
            Assert.Equal(ControllerDatabaseRowKind.New, kept.Kind);
            Assert.Equal(newPin, loaded.BaselinePin);
            Assert.Equal("6.1.0 (r5700@abcdef0)", loaded.AppVersion);
            // The tag named a file judged against the old pin.
            Assert.Null(loaded.ETag);
            Assert.Equal(stored.FetchedUtc, loaded.FetchedUtc);
        }

        /// <summary>A row for a controller the new release's fork carries a
        /// community row for is no longer new: it is that row's changed
        /// form, and SDL is given it as it starts.</summary>
        [Fact]
        public void ALoadUnderANewerRelease_ReadsANewRowTheForkNowCarriesAsChanged()
        {
            string pad = Row(0x2000, 0x0001, "Pad");
            var stored = Stored(Pinned(), null, pad).WithRowInUse(Id(0x2000, 0x0001));
            // The new pin is older than the download, and has another text.
            var newer = Baseline("3333333333333333333333333333333333333333", "2026-11-01T00:00:00Z",
                ('K', KeptRow), ('K', pad.Replace("a:b0", "a:b4")));
            var loaded = ControllerDatabaseSection.FromData(Data(stored), newer, Version, out bool changed);
            Assert.True(changed);
            var row = Assert.Single(loaded.Rows);
            Assert.Equal(ControllerDatabaseRowKind.Changed, row.Kind);
            Assert.False(row.InUse);
            Assert.Equal(pad, row.Row.Text);
        }

        /// <summary>A changed row is newer than the fork's copy only while
        /// it was fetched after the pin. Under a pin made after the
        /// download, the fork's copy is the newer one, and the stored row
        /// goes. Under the pin it was chosen against, it stays whatever the
        /// clocks say.</summary>
        [Fact]
        public void AChangedRowFetchedBeforeTheNewPin_Goes_AndOneFetchedAfterItStays()
        {
            var stored = Stored(Pinned(), null, Changed(KeptRow));
            const string newPin = "3333333333333333333333333333333333333333";
            string newerText = KeptRow.Replace("a:b0", "a:b9");

            var pinnedLater = Baseline(newPin, "2026-12-15T09:00:00Z", ('K', newerText));
            Assert.Empty(ControllerDatabaseSection.FromData(Data(stored), pinnedLater, Version, out bool changed).Rows);
            Assert.True(changed);

            var pinnedEarlier = Baseline(newPin, "2026-10-01T09:00:00Z", ('K', newerText));
            var kept = Assert.Single(ControllerDatabaseSection.FromData(Data(stored), pinnedEarlier, Version, out changed).Rows);
            Assert.Equal(ControllerDatabaseRowKind.Changed, kept.Kind);
            Assert.True(changed);

            // The same pin, and a pin time after the download: a PC whose
            // clock ran behind when it fetched keeps its row.
            var samePinLaterTime = Baseline(Pin, "2027-01-01T00:00:00Z",
                ('K', KeptRow), ('S', SdlRow), ('O', ElsewhereRow), ('N', LeftOutRow), ('S', SdlOnlyId));
            Assert.Single(ControllerDatabaseSection.FromData(Data(stored), samePinLaterTime, Version, out changed).Rows);
            Assert.False(changed);
        }

        [Fact]
        public void ALoadUnderAnotherBuild_KeepsTheRows_AndSaysTheFileChanged()
        {
            var stored = Stored(Pinned(), null, Changed(KeptRow), Row(0x2000, 0x0001));
            var loaded = ControllerDatabaseSection.FromData(Data(stored), Pinned(), "6.0.2 (r5600@1234567)", out bool changed);
            Assert.True(changed);
            Assert.Equal(2, loaded.Rows.Count);
            Assert.Equal("6.0.2 (r5600@1234567)", loaded.AppVersion);
            // The pin is the same, so the tag still names the file.
            Assert.Equal(stored.ETag, loaded.ETag);
        }

        /// <summary>Nothing in the file is taken on trust. A row that no
        /// longer passes the grammar goes, a second row for an ID goes, a
        /// kind the file claims is read again from the baseline, and a mark
        /// on a changed row is dropped.</summary>
        [Fact]
        public void AFileEditedByHand_IsReadAgainstTheBaseline()
        {
            var data = Data(Stored(Pinned(), null, Changed(KeptRow), Row(0x2000, 0x0001)));
            var rows = data.Rows.ToList();
            // A row for SDL's own controller, claimed as Changed.
            rows.Add(new ControllerDatabaseRowData { Kind = "Changed", Text = Changed(SdlRow) });
            // A row that is no row.
            rows.Add(new ControllerDatabaseRowData { Kind = "New", Text = "xinput,XInput Controller,a:b0,platform:Windows," });
            rows.Add(new ControllerDatabaseRowData { Kind = "New", Text = null });
            rows.Add(null);
            // A second row for an ID already there.
            rows.Add(new ControllerDatabaseRowData { Kind = "New", Text = Row(0x2000, 0x0001, "Second") });
            // The fork's row claimed as New and in use.
            rows[0] = new ControllerDatabaseRowData { Kind = "New", InUse = "true", Text = Changed(KeptRow) };
            data.Rows = rows.ToArray();

            var loaded = ControllerDatabaseSection.FromData(data, Pinned(), Version, out bool changed);
            Assert.True(changed);
            Assert.Equal(2, loaded.Rows.Count);
            var fork = loaded.Rows.Single(r => r.Row.Guid == Id(0x1000, 0x0001));
            Assert.Equal(ControllerDatabaseRowKind.Changed, fork.Kind);
            Assert.False(fork.InUse);
            Assert.Equal(ControllerDatabaseRowKind.New, loaded.Rows.Single(r => r.Row.Guid == Id(0x2000, 0x0001)).Kind);
            Assert.Equal("Test Pad", loaded.Rows.Single(r => r.Row.Guid == Id(0x2000, 0x0001)).Row.Name);
        }

        /// <summary>Every value of the section is text in the file, so one
        /// the reader cannot make sense of costs that value alone. A typed
        /// number or mark would fail the whole settings file at the
        /// serializer, and PadForge would start on defaults.</summary>
        [Fact]
        public void AValueTheReaderCannotMakeSenseOf_CostsThatValue_NotTheFile()
        {
            var stored = Stored(Pinned(), null, Row(0x2000, 0x0001)).WithRowInUse(Id(0x2000, 0x0001));
            string xml = Xml(new SettingsFileData { ControllerDatabase = stored.ToData() });
            Assert.Contains("WindowsRows=\"5\"", xml, StringComparison.Ordinal);
            Assert.Contains("InUse=\"true\"", xml, StringComparison.Ordinal);
            xml = xml.Replace("WindowsRows=\"5\"", "WindowsRows=\"many\"").Replace("InUse=\"true\"", "InUse=\"maybe\"")
                .Replace("Kind=\"New\"", "Kind=\"Sideways\"");

            var data = FromXml(xml).ControllerDatabase;
            Assert.NotNull(data);
            var loaded = ControllerDatabaseSection.FromData(data, Pinned(), Version, out bool changed);
            Assert.True(changed);
            Assert.Equal(0, loaded.WindowsRows);
            var row = Assert.Single(loaded.Rows);
            Assert.Equal(ControllerDatabaseRowKind.New, row.Kind);
            Assert.False(row.InUse);
            // What it writes back is whole again.
            var again = loaded.ToData();
            Assert.Equal("0", again.WindowsRows);
            Assert.Null(again.Rows[0].InUse);
            Assert.Equal("New", again.Rows[0].Kind);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("yesterday")]
        [InlineData("2026-11-23")]
        [InlineData("23/11/2026 14:17:35")]
        public void ASectionWithNoReadableTime_IsNoSection(string time)
        {
            var data = Data(Stored(Pinned(), null, Row(0x2000, 0x0001)));
            data.FetchedUtc = time;
            Assert.Null(ControllerDatabaseSection.FromData(data, Pinned(), Version, out bool changed));
            Assert.True(changed);
        }

        /// <summary>The tag goes back to GitHub in a header. One that is
        /// not visible ASCII, or is too long, or came from another address,
        /// is not sent.</summary>
        [Fact]
        public void ATagThatIsNoTag_OrFromAnotherAddress_IsDropped()
        {
            var stored = Stored(Pinned(), null, Row(0x2000, 0x0001));
            foreach (string bad in new[] { "W/\"a b\"", "\"line\nbreak\"", "\"" + new string('x', 300) + "\"", "\"caf" + (char)0xE9 + "\"" })
            {
                var data = Data(stored);
                data.ETag = bad;
                var loaded = ControllerDatabaseSection.FromData(data, Pinned(), Version, out bool changed);
                Assert.Null(loaded.ETag);
                Assert.True(changed);
                Assert.Single(loaded.Rows);
            }

            // A section from another address keeps its tag, for that
            // address alone (ControllerDatabaseController sends it nowhere
            // else). A source the file does not hold in PadForge's own form
            // is no address, and its tag goes.
            var elsewhere = Data(stored);
            elsewhere.Source = "https://example.com/gamecontrollerdb.txt";
            var read = ControllerDatabaseSection.FromData(elsewhere, Pinned(), Version, out bool moved);
            Assert.Equal("W/\"tag-1\"", read.ETag);
            Assert.False(moved);
            Assert.Equal("https://example.com/gamecontrollerdb.txt", read.Source);
            Assert.Equal("https://example.com/gamecontrollerdb.txt", read.ToData().Source);
            foreach (string unread in new[] { null, "http://example.com/gamecontrollerdb.txt", "https://EXAMPLE.com/gamecontrollerdb.txt" })
            {
                var odd = Data(stored);
                odd.Source = unread;
                var back = ControllerDatabaseSection.FromData(odd, Pinned(), Version, out bool oddChanged);
                Assert.Null(back.ETag);
                Assert.Null(back.Source);
                Assert.True(oddChanged);
            }

            Assert.Equal("\"abc\"", ControllerDatabaseSection.CleanETag("\"abc\""));
            Assert.Null(ControllerDatabaseSection.CleanETag(""));
            Assert.Null(ControllerDatabaseSection.CleanETag(null));
        }

        /// <summary>The address is the community repository's own file, on
        /// GitHub's raw host, over HTTPS, at its master branch.</summary>
        [Fact]
        public void TheSource_IsTheCommunitysFileOnGitHubsRawHost()
        {
            Assert.Equal("https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt",
                ControllerDatabaseAddress.Community);
            var community = new Uri(ControllerDatabaseAddress.Community);
            Assert.True(ControllerDatabaseUpdater.IsTrusted(community, community));
            Assert.Equal(ControllerDatabaseUpdater.Host, community.Host);
            Assert.True(ControllerDatabaseAddress.TryRead(ControllerDatabaseAddress.Community, out string read));
            Assert.Equal(ControllerDatabaseAddress.Community, read);
        }

        // ── Download From (#600 item 49) ──

        private const string Mirror = "https://mirror.example.net/sdl/gamecontrollerdb.txt";

        /// <summary>An address is taken over HTTPS on the default port, with
        /// a host and no user name, password or fragment, and kept in one
        /// form, so the community's is known by its text.</summary>
        [Theory]
        [InlineData(" https://Mirror.Example.NET/sdl/gamecontrollerdb.txt ", "https://mirror.example.net/sdl/gamecontrollerdb.txt")]
        [InlineData("https://mirror.example.net:443/sdl/gamecontrollerdb.txt", "https://mirror.example.net/sdl/gamecontrollerdb.txt")]
        [InlineData("https://mirror.example.net", "https://mirror.example.net/")]
        [InlineData("https://RAW.githubusercontent.com/mdqinc/SDL_GameControllerDB/master/gamecontrollerdb.txt", ControllerDatabaseAddress.Community)]
        [InlineData("http://mirror.example.net/sdl/gamecontrollerdb.txt", null)]
        [InlineData("https://mirror.example.net:8443/sdl/gamecontrollerdb.txt", null)]
        [InlineData("https://someone:secret@mirror.example.net/sdl/gamecontrollerdb.txt", null)]
        [InlineData("https://mirror.example.net/sdl/gamecontrollerdb.txt#rows", null)]
        [InlineData("ftp://mirror.example.net/sdl/gamecontrollerdb.txt", null)]
        [InlineData("file:///C:/rows.txt", null)]
        [InlineData("mirror.example.net/sdl/gamecontrollerdb.txt", null)]
        [InlineData("/sdl/gamecontrollerdb.txt", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void AnAddress_IsTakenOnlyOverHttpsOnTheDefaultPort(string typed, string taken)
        {
            Assert.Equal(taken != null, ControllerDatabaseAddress.TryRead(typed, out string read));
            Assert.Equal(taken, read);
            Assert.Equal(taken == ControllerDatabaseAddress.Community, ControllerDatabaseAddress.IsCommunity(read));
        }

        [Fact]
        public void AnAddressPastTheLongestTaken_IsRefused()
        {
            string longest = "https://mirror.example.net/" + new string('a', ControllerDatabaseAddress.MaxLength - 27);
            Assert.Equal(ControllerDatabaseAddress.MaxLength, longest.Length);
            Assert.True(ControllerDatabaseAddress.TryRead(longest, out _));
            Assert.False(ControllerDatabaseAddress.TryRead(longest + "a", out _));
        }

        /// <summary>From an address that is not the community's, a download
        /// keeps new rows only: a changed row could be older than the row
        /// this release carries. The section says where its rows came from.</summary>
        [Fact]
        public void ADownloadFromAnotherAddress_KeepsNewRowsOnly()
        {
            var baseline = Pinned();
            var section = ControllerDatabaseSection.FromDownload(Download(baseline, Changed(KeptRow), Row(0x2000, 0x0001)), "\"m\"", Fetched,
                baseline, Version, null, Mirror);
            var row = Assert.Single(section.Rows);
            Assert.Equal(Id(0x2000, 0x0001), row.Row.Guid);
            Assert.Equal(ControllerDatabaseRowKind.New, row.Kind);
            Assert.Equal(0, section.ChangedRows);
            Assert.Equal(Mirror, section.Source);
            Assert.Equal(Mirror, section.ToData().Source);

            // The community's own file keeps both, as before.
            var community = Stored(baseline, null, Changed(KeptRow), Row(0x2000, 0x0001));
            Assert.Equal(1, community.ChangedRows);
            Assert.Equal(1, community.NewRows);
            Assert.Equal(ControllerDatabaseAddress.Community, community.Source);
        }

        /// <summary>A file whose rows came from another address and holds a
        /// changed row, as a hand edit could make it, loses that row on
        /// load.</summary>
        [Fact]
        public void ALoadOfRowsFromAnotherAddress_DropsAChangedRow()
        {
            var baseline = Pinned();
            var data = Data(Stored(baseline, null, Changed(KeptRow), Row(0x2000, 0x0001)));
            data.Source = Mirror;
            var read = ControllerDatabaseSection.FromData(data, baseline, Version, out bool changed);
            Assert.True(changed);
            var row = Assert.Single(read.Rows);
            Assert.Equal(ControllerDatabaseRowKind.New, row.Kind);
            Assert.Equal(Mirror, read.Source);
        }

        /// <summary>The address the user set is kept in the element whether
        /// or not a download or a row of the user's is, and left out while it
        /// is the community's.</summary>
        [Fact]
        public void TheChosenAddress_IsKept_WithNothingElseInTheElement()
        {
            var data = ControllerDatabaseSection.Compose(null, ControllerDatabaseMine.Empty, Mirror);
            string xml = Xml(new SettingsFileData { ControllerDatabase = data });
            var element = Assert.Single(XDocument.Parse(xml).Root.Elements(), e => e.Name.LocalName == "ControllerDatabase");
            Assert.Equal(Mirror, (string)element.Attribute("DownloadFrom"));
            Assert.Empty(element.Elements());
            var back = FromXml(xml).ControllerDatabase;
            Assert.Equal(Mirror, back.DownloadFrom);
            Assert.Null(ControllerDatabaseSection.FromData(back, Pinned(), Version, out bool changed));
            Assert.False(changed);

            Assert.Null(ControllerDatabaseSection.Compose(null, ControllerDatabaseMine.Empty, ControllerDatabaseAddress.Community));
            Assert.Null(ControllerDatabaseSection.Compose(null, ControllerDatabaseMine.Empty, null));
            var withRows = ControllerDatabaseSection.Compose(Stored(Pinned(), null, Row(0x2000, 0x0001)), ControllerDatabaseMine.Empty, ControllerDatabaseAddress.Community);
            Assert.Null(withRows.DownloadFrom);
        }
    }
}
