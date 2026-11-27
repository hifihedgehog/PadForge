using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PadForge.Common.Input;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// What a downloaded copy of the community controller database has to
    /// be before any row of it is kept (#597): the caps on the whole file,
    /// and the grammar and limits on each Windows row. No publisher digest
    /// exists for the file, so these checks are what stands between the
    /// network and SDL.
    /// </summary>
    public class ControllerDatabaseDownloadTests
    {
        // ── A row ──

        [Fact]
        public void ARowInTheDatabaseForm_IsAccepted_WithItsIdNameAndIds()
        {
            string text = Row(0x054C, 0x0BB2, "Sony PS VR Aim Controller");
            Assert.True(ControllerDatabaseRow.TryParse(text, out var row));
            Assert.Equal("030000004c050000b20b000000000000", row.Guid);
            Assert.Equal("Sony PS VR Aim Controller", row.Name);
            Assert.Equal(0x054C, row.VendorId);
            Assert.Equal(0x0BB2, row.ProductId);
            Assert.Same(text, row.Text);
        }

        /// <summary>PadForge's own rows pass the grammar they are the model
        /// for. One whose ID carries a name hash is no USB-form ID, and is
        /// the only kind the grammar turns away.</summary>
        [Fact]
        public void PadForgesOwnRows_PassTheSameGrammar()
        {
            var own = ControllerDatabaseUse.OwnRows;
            Assert.NotEmpty(own);
            int passed = 0;
            foreach (string row in own)
            {
                bool usbForm = ControllerDatabaseRow.TryReadUsbGuid(row.Substring(0, 32), out _, out _);
                Assert.True(ControllerDatabaseRow.TryParse(row, out _) == usbForm, row);
                if (usbForm) passed++;
            }
            Assert.True(passed > 0, "no row of PadForge's own was held to the grammar");
            Assert.Contains(own, r => r.StartsWith("0300d8234c0500006802000000000000,", StringComparison.Ordinal));
        }

        public static IEnumerable<object[]> RefusedRows() => new[]
        {
            // Another platform's row, and a row with no platform.
            new object[] { "another platform", Id(0x1234, 0x0001) + ",Pad,a:b0,platform:Linux," },
            new object[] { "no platform", Id(0x1234, 0x0001) + ",Pad,a:b0," },
            new object[] { "platform not last", Id(0x1234, 0x0001) + ",Pad,platform:Windows,a:b0," },
            // A bad ID: too short, not hex, upper case, the name form, a
            // name hash, a version, the Bluetooth bus, and the xinput row.
            new object[] { "short ID", "0300000034120000010000000000000,Pad,a:b0,platform:Windows," },
            new object[] { "not hex", "0300000034120000010000000000000g,Pad,a:b0,platform:Windows," },
            new object[] { "upper case", "03000000AB1200000100000000000000,Pad,a:b0,platform:Windows," },
            new object[] { "name form", "03000000526574726f20506164000000,Pad,a:b0,platform:Windows," },
            new object[] { "name hash", "0300d8234c0500006802000000000000,Pad,a:b0,platform:Windows," },
            new object[] { "version", "030000004c0500006802000011010000,Pad,a:b0,platform:Windows," },
            new object[] { "Bluetooth bus", "050000004c0500006802000000000000,Pad,a:b0,platform:Windows," },
            new object[] { "xinput", "xinput,XInput Controller,a:b0,b:b1,platform:Windows," },
            // No vendor, no product.
            new object[] { "zero vendor", Id(0, 0x0001) + ",Pad,a:b0,platform:Windows," },
            new object[] { "zero product", Id(0x1234, 0) + ",Pad,a:b0,platform:Windows," },
            // vJoy's shared ID.
            new object[] { "vJoy", Row(0x1234, 0xBEAD, "vJoy Device") },
            // The name: none, a comma's worth of nothing, too long, a
            // control character, an invisible one, another kind of space.
            new object[] { "no name", Id(0x1234, 0x0001) + ",,a:b0,platform:Windows," },
            new object[] { "long name", Row(0x1234, 0x0001, new string('n', 129)) },
            new object[] { "tab in the name", Row(0x1234, 0x0001, "Pad\tOne") },
            new object[] { "zero-width space in the name", Row(0x1234, 0x0001, "Pad" + (char)0x200B + "One") },
            new object[] { "no-break space in the name", Row(0x1234, 0x0001, "Pad" + (char)0x00A0 + "One") },
            new object[] { "right-to-left override in the name", Row(0x1234, 0x0001, "Pad" + (char)0x202E + "One") },
            // A binding SDL's parser would not take as written.
            new object[] { "hint field", Row(0x1234, 0x0001, bindings: "a:b0,hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1") },
            new object[] { "crc field", Row(0x1234, 0x0001, bindings: "crc:1a2b,a:b0") },
            new object[] { "type field", Row(0x1234, 0x0001, bindings: "a:b0,type:ps4") },
            new object[] { "no binding at all", Id(0x1234, 0x0001) + ",Pad,platform:Windows," },
            new object[] { "empty fields alone", Id(0x1234, 0x0001) + ",Pad,,,,platform:Windows," },
            new object[] { "no value", Row(0x1234, 0x0001, bindings: "a:") },
            new object[] { "no control", Row(0x1234, 0x0001, bindings: ":b0") },
            new object[] { "a sign for a control", Row(0x1234, 0x0001, bindings: "+:b0") },
            new object[] { "a sign after the control", Row(0x1234, 0x0001, bindings: "leftx+:a0") },
            new object[] { "a control that starts with a digit", Row(0x1234, 0x0001, bindings: "1a:b0") },
            new object[] { "a space", Row(0x1234, 0x0001, bindings: "a:b0, b:b1") },
            new object[] { "upper case control", Row(0x1234, 0x0001, bindings: "A:b0") },
            new object[] { "a hat with two digits", Row(0x1234, 0x0001, bindings: "dpup:h10.1") },
            new object[] { "a button with four digits", Row(0x1234, 0x0001, bindings: "a:b1000") },
            new object[] { "another script's digit", Row(0x1234, 0x0001, bindings: "a:b" + (char)0x0663) },
            new object[] { "a control name past SDL's buffer", Row(0x1234, 0x0001, bindings: new string('x', 17) + ":b0") },
            new object[] { "a second platform", Row(0x1234, 0x0001, bindings: "a:b0,platform:Windows") },
            // SDL finds its own fields by their text anywhere in a row, so a
            // control whose name ends in one would be read as that field,
            // and a name that holds a hint would keep the row from being added.
            new object[] { "a control read as a name hash", Row(0x1234, 0x0001, bindings: "a:b0,xcrc:b1") },
            new object[] { "a control read as a type", Row(0x1234, 0x0001, bindings: "a:b0,subtype:b1") },
            new object[] { "a control read as a face style", Row(0x1234, 0x0001, bindings: "a:b0,face:b1") },
            new object[] { "a control read as a hint", Row(0x1234, 0x0001, bindings: "a:b0,hint:b1") },
            new object[] { "a control read as a platform", Row(0x1234, 0x0001, bindings: "a:b0,platform:b1") },
            new object[] { "a hint in the name", Row(0x1234, 0x0001, "Pad hint:SDL_X") },
            // Too long in all.
            new object[] { "long row", Row(0x1234, 0x0001, bindings: string.Join(",", Enumerable.Range(0, 200).Select(i => "misc1:b" + (i % 100)))) },
        };

        [Theory]
        [MemberData(nameof(RefusedRows))]
        public void ARowOutsideTheForm_IsRefused(string what, string text)
        {
            Assert.False(ControllerDatabaseRow.TryParse(text, out var row), what);
            Assert.Null(row);
        }

        /// <summary>The limits themselves pass: a name of 128, a control
        /// name of 16, a row of 1,024, and every kind of binding SDL
        /// reads.</summary>
        [Fact]
        public void ARowAtTheLimits_IsAccepted()
        {
            Assert.True(ControllerDatabaseRow.TryParse(Row(0x1234, 0x0001, new string('n', 128)), out _));
            Assert.True(ControllerDatabaseRow.TryParse(Row(0x1234, 0x0001, bindings: new string('x', 16) + ":b0"), out _));
            Assert.True(ControllerDatabaseRow.TryParse(Row(0x1234, 0x0001, "Pokémon Pad 手柄"), out _));
            Assert.True(ControllerDatabaseRow.TryParse(Row(0x1234, 0x0001,
                bindings: "+rightx:b10,-rightx:b0,a:b999,dpup:-a1,dpdown:+a1,leftx:a0~,lefttrigger:a999,dpleft:h0.8,dpright:h9.12,,x:b3"), out _));

            // A row of exactly 1,024 characters, made of bindings and of the
            // empty fields the grammar passes over. One more character and
            // it is refused.
            const string tail = ",platform:Windows,";
            string head = Id(0x1234, 0x0001) + ",Pad";
            while (head.Length + ",misc1:b15".Length + tail.Length <= ControllerDatabaseRow.MaxRowLength) head += ",misc1:b15";
            head += new string(',', ControllerDatabaseRow.MaxRowLength - head.Length - tail.Length);
            string longest = head + tail;
            Assert.Equal(ControllerDatabaseRow.MaxRowLength, longest.Length);
            Assert.True(ControllerDatabaseRow.TryParse(longest, out _));
            Assert.False(ControllerDatabaseRow.TryParse(head + "," + tail, out _));
        }

        /// <summary>The hash is the one the baseline's script writes: the
        /// first 16 hex digits of the SHA-256 of the row's UTF-8 bytes.</summary>
        [Fact]
        public void ARowsHash_IsTheFirstSixteenDigitsOfItsSha256()
        {
            string text = Row(0x054C, 0x0BB2, "Sony PS VR Aim Controller");
            string whole = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
            Assert.Equal(whole.Substring(0, 16), ControllerDatabaseRow.HashOf(text));
            Assert.Equal(whole.Substring(0, 16), Parsed(text).Hash);
            // A known vector, so the digest is SHA-256 and no other.
            Assert.Equal("ba7816bf8f01cfea", ControllerDatabaseRow.HashOf("abc"));
        }

        /// <summary>SDL reaches a row from a joystick's GUID with its name
        /// hash cleared, and then its version too.</summary>
        [Theory]
        [InlineData("030000004c0500006802000000000000", "030000004c0500006802000000000000")]
        [InlineData("0300d8234c0500006802000000000000", "030000004c0500006802000000000000")]
        [InlineData("0300D8234C0500006802000011010000", "030000004c0500006802000000000000")]
        [InlineData("030000004c0500006802000011017801", "030000004c0500006802000000007801")]
        [InlineData("050000004c0500006802000000000000", "050000004c0500006802000000000000")]
        [InlineData("030000004c050000680200000000000", null)]
        [InlineData("xinput", null)]
        [InlineData("030000004c05000068020000000000zz", null)]
        [InlineData(null, null)]
        public void AJoysticksGuid_GivesTheRowIdSdlLooksUp(string guid, string expected)
            => Assert.Equal(expected, ControllerDatabaseRow.LookupId(guid));

        /// <summary>DirectInput puts a Bluetooth pad on the Bluetooth bus in
        /// its GUID since upstream c37e0e81ca, and SDL reads it through the
        /// row of the same GUID on the USB bus when its own has none
        /// (SDL_gamepad.c:1652-1667). A GUID with a driver mark, or on another
        /// bus, has no such second try.</summary>
        [Fact]
        public void ABluetoothPadsGuid_FallsBackToItsUsbRow_AsSdlDoes()
        {
            const string usb = "030000004c0500006802000000000000", bt = "050000004c0500006802000000000000";
            const string device = "0500d8234c0500006802000011010000";
            Assert.Equal(usb, ControllerDatabaseRow.LookupId(device, id => id == usb));
            Assert.Equal(bt, ControllerDatabaseRow.LookupId(device, id => id == usb || id == bt));
            Assert.Equal(bt, ControllerDatabaseRow.LookupId(device, id => false));
            Assert.Null(ControllerDatabaseRow.UsbBusFallback("050000004c0500006802000000006800"));
            Assert.Null(ControllerDatabaseRow.UsbBusFallback(usb));
            Assert.True(SdlMappingRow.Maps(usb, device));
            Assert.True(SdlMappingRow.Reach(bt, device) < SdlMappingRow.Reach(usb, device));
            Assert.False(SdlMappingRow.Maps(usb, "050000004c0500006802000000006800"));
        }

        // ── The whole file ──

        private static readonly List<string> Kept = KeptRows(10);

        private static ControllerDatabaseBaseline TenKept() => Baseline(Kept.Select(r => ('K', r)).ToArray());

        [Fact]
        public void AFileOfWindowsRows_IsRead_AndCountsWhatItHolds()
        {
            var rows = Kept.Concat(new[]
            {
                Row(0x2000, 0x0001, "New Pad"),
                "xinput,XInput Controller,a:b0,b:b1,platform:Windows,",
                "0300d8234c0500006802000000000000,Hashed,a:b0,platform:Windows,",
            }).ToList();
            byte[] file = CommunityFile(rows);
            var read = ControllerDatabaseDownload.Read(file, TenKept());
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            // Thirteen lines carry the Windows field, and eleven are rows.
            Assert.Equal(13, read.WindowsRows);
            Assert.Equal(11, read.Rows.Count);
            Assert.Equal(10, read.KnownRows);
            Assert.Equal(rows.Take(11), read.Rows.Select(r => r.Text));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), read.Sha256);
        }

        [Fact]
        public void WindowsLineEnds_AreNoPartOfARow()
        {
            var read = ControllerDatabaseDownload.Read(CommunityFile(Kept, "\r\n"), TenKept());
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            Assert.Equal(Kept, read.Rows.Select(r => r.Text));
        }

        [Fact]
        public void AFileOverFourMegabytes_IsRefused()
        {
            byte[] file = new byte[ControllerDatabaseDownload.MaxBytes + 1];
            Array.Fill(file, (byte)'#');
            var read = ControllerDatabaseDownload.Read(file, TenKept());
            Assert.Equal(ControllerDatabaseRefusal.TooLarge, read.Refusal);
            Assert.Empty(read.Rows);
            // At the cap it is read: it is all comment, and holds no row.
            Assert.NotEqual(ControllerDatabaseRefusal.TooLarge,
                ControllerDatabaseDownload.Read(file.AsSpan(0, ControllerDatabaseDownload.MaxBytes).ToArray(), TenKept()).Refusal);
        }

        [Fact]
        public void AFileUnderAHundredKilobytes_IsRefused()
        {
            byte[] whole = CommunityFile(Kept);
            Assert.True(whole.Length >= ControllerDatabaseDownload.MinBytes);
            var read = ControllerDatabaseDownload.Read(whole.AsSpan(0, ControllerDatabaseDownload.MinBytes - 1).ToArray(), TenKept());
            Assert.Equal(ControllerDatabaseRefusal.TooSmall, read.Refusal);
            Assert.Empty(read.Rows);
            // An error page is small too.
            Assert.Equal(ControllerDatabaseRefusal.TooSmall,
                ControllerDatabaseDownload.Read(Encoding.UTF8.GetBytes("<html>404: Not Found</html>"), TenKept()).Refusal);
        }

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x07)]
        [InlineData(0x1B)]
        [InlineData(0x7F)]
        public void AFileWithAControlCharacter_IsRefused(int control)
        {
            byte[] file = CommunityFile(Kept);
            file[file.Length / 2] = (byte)control;
            Assert.Equal(ControllerDatabaseRefusal.NotText, ControllerDatabaseDownload.Read(file, TenKept()).Refusal);
        }

        [Fact]
        public void AFileThatIsNotUtf8_IsRefused()
        {
            byte[] file = CommunityFile(Kept);
            file[file.Length / 2] = 0xFF;
            Assert.Equal(ControllerDatabaseRefusal.NotText, ControllerDatabaseDownload.Read(file, TenKept()).Refusal);
        }

        /// <summary>Tabs and both line ends are text.</summary>
        [Fact]
        public void TabsAndLineEnds_AreText()
        {
            var rows = new List<string>(Kept) { "#\ta comment with a tab" };
            Assert.Equal(ControllerDatabaseRefusal.None, ControllerDatabaseDownload.Read(CommunityFile(rows, "\r\n"), TenKept()).Refusal);
        }

        /// <summary>Two rows for one controller: SDL would keep the later,
        /// and neither is taken. A third changes nothing.</summary>
        [Fact]
        public void ARepeatedId_TakesNoRowOfThatId()
        {
            var rows = new List<string>(Kept)
            {
                Row(0x2000, 0x0001, "First"),
                Row(0x2000, 0x0002, "Alone"),
                Row(0x2000, 0x0001, "Second"),
                Row(0x2000, 0x0001, "Third"),
            };
            var read = ControllerDatabaseDownload.Read(CommunityFile(rows), TenKept());
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            Assert.Equal(14, read.WindowsRows);
            Assert.Equal(11, read.Rows.Count);
            Assert.DoesNotContain(read.Rows, r => r.Guid == Id(0x2000, 0x0001));
            Assert.Contains(read.Rows, r => r.Name == "Alone");
        }

        /// <summary>Four in five of the controllers this release knows have
        /// to be there. Eight of ten pass, and seven do not.</summary>
        [Theory]
        [InlineData(10, false)]
        [InlineData(8, false)]
        [InlineData(7, true)]
        [InlineData(0, true)]
        public void AFileMissingMoreThanAFifthOfTheKnownRows_IsRefused(int kept, bool refused)
        {
            // Rows of controllers the release does not know do not make up
            // for the ones that are gone.
            var rows = Kept.Take(kept).Concat(Enumerable.Range(0, 40).Select(i => Row(0x3000 + i, 0x0001))).ToList();
            var read = ControllerDatabaseDownload.Read(CommunityFile(rows), TenKept());
            Assert.Equal(refused ? ControllerDatabaseRefusal.TooFewKnownRows : ControllerDatabaseRefusal.None, read.Refusal);
            Assert.Equal(kept, read.KnownRows);
            Assert.Equal(refused ? 0 : rows.Count, read.Rows.Count);
        }

        /// <summary>A known row that changed is still there: the count is of
        /// controllers, whatever their rows now say.</summary>
        [Fact]
        public void AChangedRow_StillCountsAsKnown()
        {
            var rows = Kept.Select(r => r.Replace("a:b0", "a:b5")).ToList();
            var read = ControllerDatabaseDownload.Read(CommunityFile(rows), TenKept());
            Assert.Equal(ControllerDatabaseRefusal.None, read.Refusal);
            Assert.Equal(10, read.KnownRows);
        }

        /// <summary>An ID of SDL's own Windows section that the pinned copy
        /// has no row for is no row to miss.</summary>
        [Fact]
        public void AnIdWithNoPinnedRow_IsNotCountedAsKnown()
        {
            var baseline = Baseline(Kept.Select(r => ('K', r)).Append(('S', Id(0x37D7, 0x2816))).ToArray());
            Assert.Equal(10, baseline.PinnedIds.Count);
            Assert.Equal(11, baseline.Count);
            Assert.Equal(ControllerDatabaseRefusal.None, ControllerDatabaseDownload.Read(CommunityFile(Kept.Take(8)), baseline).Refusal);
        }
    }
}
