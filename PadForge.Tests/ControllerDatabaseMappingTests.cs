using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.Views;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// A person's own SDL rows (#600 items 46 to 48): the grammar a pasted
    /// row is held to, the ID Copy writes, which row of a text is picked for
    /// a device, and the round trip of a Bluetooth pad's row. Plain logic:
    /// no SDL starts.
    /// </summary>
    public class ControllerDatabaseMappingTests
    {
        /// <summary>A DualSense read by SDL's own driver over Bluetooth on
        /// this PC: bus 0005, checksum 0x5657 of "DualSense Wireless
        /// Controller", Sony 054C, product 0CE6, version 0, signature 'h'.</summary>
        private const string DualSenseBt = "050057564c050000e60c000000006800";

        private const string DualSenseRow =
            "050057564c050000e60c000000006800,DualSense Wireless Controller,a:b1,b:b2,back:b8,crc:5657,dpdown:h0.4,dpleft:h0.8,"
            + "dpright:h0.2,dpup:h0.1,guide:b12,leftshoulder:b4,leftstick:b10,lefttrigger:a3,leftx:a0,lefty:a1,misc1:b14,"
            + "rightshoulder:b5,rightstick:b11,righttrigger:a4,rightx:a2,righty:a5,start:b9,touchpad:b13,x:b0,y:b3,type:ps5,platform:Windows,";

        private static SdlMappingRow Read(string text)
        {
            Assert.True(SdlMappingRow.TryParse(text, out var row, out var why), why.ToString());
            return row;
        }

        // ── Copy's ID and the round trip ──

        /// <summary>Copy writes the device's own ID with its checksum and
        /// version zeroed and its bus and driver mark kept, as SDL compares
        /// them (SDL_PrivateMatchGamepadMappingForGUID): a Bluetooth pad
        /// keeps bus 0005.</summary>
        [Fact]
        public void CopysId_ZeroesTheChecksumAndVersion_AndKeepsTheBus()
        {
            Assert.Equal("050000004c050000e60c000000006800", SdlMappingRow.CommunityId(DualSenseBt));
            Assert.Equal("030000004c050000e60c000000000000", SdlMappingRow.CommunityId("03001a2b4c050000e60c000001010000"));
        }

        /// <summary>The round trip: SDL's mapping of the pad, copied in the
        /// community's form, pasted back, written again, reads the same row,
        /// maps the same pad and no USB pad of the same model.</summary>
        [Fact]
        public void ABluetoothPadsRow_RoundTrips_AndMapsThatPadOnly()
        {
            var held = Read(DualSenseRow);
            string copied = held.With(guid: SdlMappingRow.CommunityId(DualSenseBt)).Text;
            Assert.StartsWith("050000004c050000e60c000000006800,DualSense Wireless Controller,", copied);
            Assert.EndsWith(",type:ps5,platform:Windows,", copied);
            Assert.DoesNotContain("crc:", copied);

            var pasted = Read(copied);
            Assert.Equal(copied, pasted.Text);
            Assert.Equal(held.Layout, pasted.Layout);
            Assert.True(SdlMappingRow.Maps(pasted.Guid, DualSenseBt));
            Assert.False(SdlMappingRow.Maps(pasted.Guid, "030057564c050000e60c000000006800"));
            // A version the row names must match. A zero matches any.
            Assert.True(SdlMappingRow.Maps("030000004c050000e60c000000000000", "03001a2b4c050000e60c000001010000"));
            Assert.False(SdlMappingRow.Maps("030000004c050000e60c000002010000", "03001a2b4c050000e60c000001010000"));
            Assert.True(SdlMappingRow.Maps("030000004c050000e60c000001010000", "03001a2b4c050000e60c000001010000"));
        }

        [Fact]
        public void TheRowsText_SortsTheBindings_ThenTypeFaceHint_AndEndsWithWindows()
        {
            var row = Read("03000000aa0b00001100000000000000,Pad,y:b3,a:b0,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,face:abxy,-leftx:a0,platform:Windows,");
            Assert.Equal("03000000aa0b00001100000000000000,Pad,-leftx:a0,a:b0,y:b3,face:abxy,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,",
                row.Text);
            Assert.Equal(5, row.FieldCount);
            Assert.Equal("b3", row.InputOf("y"));
        }

        [Fact]
        public void ARowWithNoPlatform_IsTaken_AsForWindows()
        {
            var row = Read(Id(0x1234, 0x0001) + ",Pad,a:b0,");
            Assert.EndsWith(",a:b0,platform:Windows,", row.Text);
        }

        // ── What a paste checks (DB-15) ──

        [Theory]
        [InlineData("", nameof(SdlRowRefusal.Empty))]
        [InlineData("not a row at all", nameof(SdlRowRefusal.NotARow))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,platform:Linux,", nameof(SdlRowRefusal.OtherPlatform))]
        [InlineData("xinput,XInput Controller,a:b0,", nameof(SdlRowRefusal.BadId))]
        [InlineData("00000000000000000000000000000000,Pad,a:b0,", nameof(SdlRowRefusal.BadId))]
        [InlineData("03000000aa0b0000110000000000000g,Pad,a:b0,", nameof(SdlRowRefusal.BadId))]
        [InlineData("03000000aa0b00001100000000000000,,a:b0,", nameof(SdlRowRefusal.BadName))]
        [InlineData("03000000aa0b00001100000000000000,Pad hint:x,a:b0,", nameof(SdlRowRefusal.BadName))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,hint:SDL_JOYSTICK_HIDAPI:=1,", nameof(SdlRowRefusal.Hint))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,sdk>=:21,", nameof(SdlRowRefusal.Hint))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,xcrc:b1,", nameof(SdlRowRefusal.UnknownField))]
        [InlineData("03000000aa0b00001100000000000000,Pad,+a:b0,", nameof(SdlRowRefusal.UnknownField))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,type:gizmo,", nameof(SdlRowRefusal.UnknownField))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:x0,", nameof(SdlRowRefusal.BadBinding))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:h10.1,", nameof(SdlRowRefusal.BadBinding))]
        [InlineData("03000000aa0b00001100000000000000,Pad,a:b0,a:b1,", nameof(SdlRowRefusal.RepeatedField))]
        [InlineData("03000000aa0b00001100000000000000,Pad,crc:1234,platform:Windows,", nameof(SdlRowRefusal.NoBindings))]
        public void APaste_IsRefused_WithItsReason(string text, string reason)
        {
            var expected = Enum.Parse<SdlRowRefusal>(reason);
            Assert.False(SdlMappingRow.TryParse(text, out _, out var why));
            Assert.Equal(expected, why);
            Assert.False(string.IsNullOrWhiteSpace(SdlMappingDialog.RefusalWords(why)));
        }

        [Fact]
        public void ARowOver1024Characters_IsRefused()
        {
            string text = Id(0x1234, 0x0001) + ",Pad," + string.Concat(Enumerable.Repeat("a:b0,", 220));
            Assert.False(SdlMappingRow.TryParse(text, out _, out var why));
            Assert.Equal(SdlRowRefusal.TooLong, why);
        }

        // ── Which row of a text is for the device ──

        [Fact]
        public void AWholeFile_GivesTheDevicesOwnRow_ThenOneOfItsModel_ThenTheFirst()
        {
            string text = "# Windows\n" + Row(0x1000, 0x0001, "First") + "\n" + Row(0x054C, 0x0CE6, "USB DualSense") + "\n"
                          + "050000004c050000e60c000000006800,BT DualSense,a:b1,platform:Windows,\n"
                          + "050000004c050000e60c000000006800,Mac,a:b1,platform:Mac OS X,\n";
            var rows = SdlMappingRow.ReadAll(text, out int other, out _, out int refused);
            Assert.Equal(3, rows.Count);
            Assert.Equal(1, other);
            Assert.Equal(0, refused);

            Assert.Equal("BT DualSense", SdlMappingRow.PickFor(rows, DualSenseBt, out var fit).Name);
            Assert.Equal(SdlMappingRow.Fit.ItsId, fit);
            Assert.Equal("USB DualSense", SdlMappingRow.PickFor(rows.Where(r => r.Name != "BT DualSense").ToList(), DualSenseBt, out fit).Name);
            Assert.Equal(SdlMappingRow.Fit.SameProduct, fit);
            Assert.Equal("First", SdlMappingRow.PickFor(rows, Id(0x9999, 0x9999), out fit).Name);
            Assert.Equal(SdlMappingRow.Fit.Other, fit);
        }

        /// <summary>The dialog's check: a row for the device says so, Add
        /// Rows counts what it keeps and what other systems' rows it passes
        /// over, and a text of no row refuses with its reason.</summary>
        [Fact]
        public void TheDialogsCheck_SaysWhatTheTextIs()
        {
            var ok = SdlMappingDialog.Verdict(SdlMappingDialog.Mode.Paste, Row(0x1000, 0x0001), Id(0x1000, 0x0001), out var row, out _);
            Assert.True(ok.Ok);
            Assert.Equal(Strings.Instance.SdlMappingDialog_ForThisDevice, ok.Lead);
            Assert.Contains("8", ok.Detail);
            Assert.NotNull(row);

            var add = SdlMappingDialog.Verdict(SdlMappingDialog.Mode.AddRows,
                Row(0x1000, 0x0001) + "\n" + Row(0x1000, 0x0002) + "\n03000000aa0b00001100000000000000,Mac,a:b0,platform:Mac OS X,\nbad row",
                null, out _, out var rows);
            Assert.True(add.Ok);
            Assert.Equal(2, rows.Count);
            Assert.Equal(string.Format(Strings.Instance.SdlMappingDialog_AddFound_Format, 2, 1), add.Lead);
            Assert.NotNull(add.Detail);

            var none = SdlMappingDialog.Verdict(SdlMappingDialog.Mode.Paste, "03000000aa0b00001100000000000000,Mac,a:b0,platform:Mac OS X,",
                Id(0x1000, 0x0001), out row, out _);
            Assert.False(none.Ok);
            Assert.Equal(Strings.Instance.SdlMappingDialog_Refused_OtherPlatform, none.Lead);
            Assert.Null(row);
        }

        /// <summary>What applying a row changes, named for each controller
        /// the device is on, or that none changes.</summary>
        [Fact]
        public void TheDialog_NamesTheControllersMappedAgain()
        {
            Assert.Equal(string.Format(Strings.Instance.SdlMappingDialog_Remaps_Format, "T.16000M", "Xbox #1, PlayStation #1"),
                SdlMappingDialog.ChangeWords("T.16000M", new[] { "Xbox #1", "PlayStation #1" }));
            Assert.Equal(string.Format(Strings.Instance.SdlMappingDialog_NoController_Format, "T.16000M"),
                SdlMappingDialog.ChangeWords("T.16000M", Array.Empty<string>()));
        }

        // ── Find's filter (DB-18, DB-19) ──

        [Fact]
        public void Find_FiltersByName_Id_OrVidPid()
        {
            var rows = new[]
            {
                new SdlFindRow(Read(Row(0x0F0D, 0x0092, "HORI Fighting Stick mini 4")), SdlMappingSource.Community, false),
                new SdlFindRow(Read(Row(0x0F0D, 0x0085, "HORI Fighting Commander")), SdlMappingSource.Sdl, false),
                new SdlFindRow(Read(Row(0x045E, 0x028E, "Xbox 360 Controller")), SdlMappingSource.Sdl, false),
            };
            Assert.Equal(2, SdlMappingDialog.Matching(rows, "hori fighting").Count);
            Assert.Single(SdlMappingDialog.Matching(rows, "045e:028e"));
            Assert.Single(SdlMappingDialog.Matching(rows, Id(0x0F0D, 0x0085)));
            Assert.Equal(3, SdlMappingDialog.Matching(rows, "  ").Count);
            Assert.Empty(SdlMappingDialog.Matching(rows, "nothing"));
        }

        // ── The user's rows in PadForge.xml ──

        private static ControllerDatabaseMineRow MineRow(string id, string name = "Mine", Guid device = default, string old = null,
            IReadOnlyList<ControllerDatabaseBefore> before = null)
        {
            var row = Read(id.Length == 32 ? id + "," + name + ",a:b0,b:b1,leftx:a0,platform:Windows," : id);
            return new ControllerDatabaseMineRow(row.Guid, row, new DateTime(2026, 11, 24, 14, 17, 35, DateTimeKind.Utc), old, device,
                before ?? Array.Empty<ControllerDatabaseBefore>());
        }

        /// <summary>The user's rows round-trip through the settings file's
        /// own serializer with what Remove Mine needs: the mapping SDL held
        /// before, and each controller's rows of the device with their
        /// sources and its settings.</summary>
        [Fact]
        public void TheUsersRows_RoundTripThroughTheSettingsFile()
        {
            var instance = Guid.Parse("8f0c1d2e-0000-4000-8000-000000000001");
            var rows = new List<MappingRow>
            {
                new() { Target = "ButtonA", Sources = new List<MappingSource> { new() { DeviceGuid = instance.ToString(), Descriptor = "Button 2" } } },
            };
            var before = new[] { new ControllerDatabaseBefore(instance, 1, new PadSetting { ButtonA = "Button 2" }, rows) };
            var mine = ControllerDatabaseMine.Empty
                .With(MineRow(DualSenseBt, "DualSense", old: DualSenseBt + ",DualSense,a:b1,", before: before) with { Mapped = new[] { instance.ToString() } })
                .With(MineRow(Id(0x1000, 1), "Plugin pad", device: instance));

            var data = ControllerDatabaseSection.Compose(null, mine);
            var serializer = new XmlSerializer(typeof(SettingsFileData));
            var writer = new StringWriter();
            serializer.Serialize(writer, new SettingsFileData { ControllerDatabase = data });
            var back = (SettingsFileData)serializer.Deserialize(new StringReader(writer.ToString()));

            Assert.Null(ControllerDatabaseSection.FromData(back.ControllerDatabase, Baseline(), "6.0.1", out bool sectionChanged));
            Assert.False(sectionChanged);
            var read = ControllerDatabaseMine.FromData(back.ControllerDatabase.Mine, out bool changed);
            Assert.False(changed);
            Assert.Equal(2, read.Count);
            var ds = read.ForSdlDevice(DualSenseBt);
            Assert.Equal(DualSenseBt, ds.Id);
            Assert.Equal(DualSenseBt + ",DualSense,a:b1,", ds.OldRow);
            Assert.Equal(new DateTime(2026, 11, 24, 14, 17, 35, DateTimeKind.Utc), ds.AddedUtc);
            Assert.Single(ds.Before);
            Assert.Equal(1, ds.Before[0].Slot);
            Assert.Equal(new[] { instance.ToString() }, ds.Mapped);
            Assert.Equal("Button 2", ds.Before[0].PadSetting.ButtonA);
            Assert.Equal("Button 2", ds.Before[0].Rows[0].Sources[0].Descriptor);
            Assert.Equal(instance, read.ForPadForgeDevice(instance).Device);
            // A device SDL does not read is never given to SDL.
            Assert.Single(read.SdlRows());
        }

        [Fact]
        public void ARowTheFileHoldsThatNoLongerPasses_IsDropped_AndTheFileIsSavedAgain()
        {
            var data = new[]
            {
                new ControllerDatabaseMineData { Added = "2026-11-24T14:17:35Z", Text = Row(0x1000, 0x0001) },
                new ControllerDatabaseMineData { Added = "2026-11-24T14:17:35Z", Text = Row(0x1000, 0x0002).Replace("a:b0", "a:zz") },
            };
            var read = ControllerDatabaseMine.FromData(data, out bool changed);
            Assert.True(changed);
            Assert.Equal(1, read.Count);
        }

        /// <summary>A device's own row is found before a row of its model,
        /// and adding a row for the same ID replaces the one there.</summary>
        [Fact]
        public void TheUsersRowForADevice_IsItsOwnBeforeItsModels()
        {
            var model = MineRow("050000004c050000e60c000000006800", "Model");
            var own = MineRow(DualSenseBt, "Own");
            var mine = ControllerDatabaseMine.Empty.With(model);
            Assert.Same(model, mine.ForSdlDevice(DualSenseBt));
            mine = mine.With(own);
            Assert.Same(own, mine.ForSdlDevice(DualSenseBt));
            var replaced = mine.With(MineRow(DualSenseBt, "Own Again"));
            Assert.Equal(2, replaced.Count);
            Assert.Equal("Own Again", replaced.ForSdlDevice(DualSenseBt).Row.Name);
            Assert.Same(model, replaced.Without(replaced.ForSdlDevice(DualSenseBt)).ForSdlDevice(DualSenseBt));
            Assert.Same(ControllerDatabaseMine.Empty, ControllerDatabaseMine.Empty.With(own).Without(own));
        }

        // ── Where a mapping came from (DB-07) ──

        [Fact]
        public void TheSource_IsTheOneWhoseBindingsSdlHolds()
        {
            string kept = Row(0x1000, 0x0001, "Kept Pad");
            string sdl = Row(0x1000, 0x0002, "SDL Pad");
            var baseline = Baseline(('K', kept), ('S', sdl));
            string own = Row(0x2000, 0x0001, "Own Pad");
            string device = "0300aabb" + Id(0x1000, 0x0001).Substring(8);

            Assert.Equal(SdlMappingSource.None, ControllerDatabaseSource.Of(device, null, null, null, null, baseline, null));
            Assert.Equal(SdlMappingSource.Community, ControllerDatabaseSource.Of(device, kept, null, null, null, baseline, null));
            Assert.Equal(SdlMappingSource.Sdl, ControllerDatabaseSource.Of("0300aabb" + Id(0x1000, 0x0002).Substring(8), sdl, null, null, null, baseline, null));
            Assert.Equal(SdlMappingSource.PadForge,
                ControllerDatabaseSource.Of("0300aabb" + Id(0x2000, 0x0001).Substring(8), own, null, null, new[] { own }, baseline, null));

            string yoursText = device + ",Mine,a:b5,platform:Windows,";
            var mine = ControllerDatabaseMine.Empty.With(MineRow(yoursText));
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(device, device + ",Mine,a:b5,crc:aabb,", mine, null, null, baseline, null));
            // SDL holds the variable's row over the user's.
            string variable = Id(0x1000, 0x0001) + ",Steam,a:b6,";
            Assert.Equal(SdlMappingSource.Variable, ControllerDatabaseSource.Of(device, device + ",Steam,a:b6,", mine, null, null, baseline, variable));
        }

        [Fact]
        public void TheSourceOfADownloadedRow_IsTheDownload()
        {
            string kept = Row(0x1000, 0x0001, "Kept Pad");
            string changed = kept.Replace("a:b0", "a:b7");
            var baseline = Baseline(('K', kept));
            var download = ControllerDatabaseDownload.Read(CommunityFile(new[] { changed }), baseline);
            var section = ControllerDatabaseSection.FromDownload(download, null, new DateTime(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc), baseline, "6.0.1", null);
            Assert.Equal(SdlMappingSource.Downloaded, ControllerDatabaseSource.Of(Id(0x1000, 0x0001), changed, null, section, null, baseline, null));
            Assert.Equal(SdlMappingSource.Downloaded, ControllerDatabaseSource.OfRow(Id(0x1000, 0x0001), null, section, null, baseline));
            Assert.Equal(SdlMappingSource.Community, ControllerDatabaseSource.OfRow(Id(0x1000, 0x0001), null, null, null, baseline));
        }
    }
}
