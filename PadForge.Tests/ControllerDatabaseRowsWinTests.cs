using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The user's own rows win for their devices over every other source
    /// (#600 items 47 and 49). PadForge's engine runs only with rights, and a
    /// start with rights keeps the allow list's variables alone
    /// (ProtectedStartAllowListTests), so the SDL_GAMECONTROLLERCONFIG
    /// variable never reaches SDL and is no source at all.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ControllerDatabaseRowsWinTests : IDisposable
    {
        /// <summary>A DualSense over Bluetooth read by SDL's HIDAPI driver:
        /// bus 0005, name checksum 0x5657, signature 'h'.</summary>
        private const string DualSenseBt = "050057564c050000e60c000000006800";

        /// <summary>The same pad in the community's form: checksum and
        /// version zeroed.</summary>
        private const string DualSenseBtModel = "050000004c050000e60c000000006800";

        private readonly ControllerDatabaseMine _savedMine = ControllerDatabaseMine.Current;

        public void Dispose()
        {
            ControllerDatabaseMine.Current = _savedMine;
            ControllerDatabaseEngine.SdlStarting();
            ControllerDatabaseEngine.SetReadsRaw(DualSenseBt, false);
        }

        // ── A paste keeps the row SDL read before the user's ──

        private static ControllerDatabaseMineRow Kept(string id, string text, string old)
        {
            Assert.True(SdlMappingRow.TryParse(text, out var row, out var why), why.ToString());
            return new ControllerDatabaseMineRow(id, row, DateTime.UnixEpoch, old, Guid.Empty, Array.Empty<ControllerDatabaseBefore>());
        }

        /// <summary>
        /// Paste keeps, as the row it takes the place of, the mapping SDL read
        /// the device through before any row of the user's. Under a copy of
        /// the user's row for its model, that is what SDL held before the
        /// first copy. Through that row itself, it is what SDL held for the
        /// row's ID before it, null while not known. A device SDL reads
        /// through no row of the user's keeps SDL's mapping. Paste asks it
        /// with the copy in force for the device.
        /// </summary>
        [Fact]
        public void APaste_KeepsTheRowSdlReadBeforeAnyOfTheUsers()
        {
            string crcRow = DualSenseBt + ",SDL's own,a:b2,b:b3,crc:5657,platform:Windows,";
            string community = DualSenseBtModel + ",Community,a:b0,b:b1,platform:Windows,";
            var model = Kept(DualSenseBtModel, DualSenseBtModel + ",Yours,a:b1,b:b0,platform:Windows,", community);
            string throughModel = DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,";

            Assert.Equal(crcRow, ControllerDatabaseUse.ReadBefore(throughModel, model, true, crcRow, false));
            Assert.Equal(string.Empty, ControllerDatabaseUse.ReadBefore(throughModel, model, true, null, false));
            Assert.Equal(community, ControllerDatabaseUse.ReadBefore(throughModel, model, false, null, false));
            Assert.Equal(string.Empty, ControllerDatabaseUse.ReadBefore(throughModel, model with { OldRow = string.Empty }, false, null, false));
            Assert.Null(ControllerDatabaseUse.ReadBefore(throughModel, model with { OldRow = null }, false, null, false));
            Assert.Equal(crcRow, ControllerDatabaseUse.ReadBefore(crcRow, model, false, null, false));
            Assert.Equal(crcRow, ControllerDatabaseUse.ReadBefore(crcRow, null, false, null, false));
            Assert.Equal(string.Empty, ControllerDatabaseUse.ReadBefore(string.Empty, null, false, null, false));

            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            string apply = FenceSource.Body(devices, "internal async Task<SdlApplyResult> ApplyMineAsync(Guid instance, SdlMappingRow row)");
            Assert.Contains("ControllerDatabaseUse.ReadBefore(now, earlierRow, ControllerDatabaseEngine.HeldBeforeCopy(id, out string beforeCopy), beforeCopy,",
                apply, StringComparison.Ordinal);
            Assert.Contains("ControllerDatabaseEngine.HadNone(id));", apply, StringComparison.Ordinal);
            Assert.DoesNotContain("facts != null ? facts.Mapping ?? string.Empty\n                : await", apply, StringComparison.Ordinal);
        }

        /// <summary>
        /// The model row going and then the paste gives the pad back the row
        /// SDL had: here SDL's own row naming the pad's checksum, which SDL
        /// read the pad through before the model row's copy. While the paste
        /// stays, the pad is read through it, though the model row had nothing
        /// to give back and the rest of its devices are read raw. Once the
        /// paste goes, the pad is read through SDL's row again, not raw.
        /// </summary>
        [Fact]
        public void TheModelRowThenThePasteGoing_GivesBackTheRowSdlHad()
        {
            const string otherName = "050034124c050000e60c000000006800";
            ControllerDatabaseEngine.SdlStarting();
            string crcRow = DualSenseBt + ",SDL's own,a:b2,b:b3,crc:5657,platform:Windows,";
            var model = Kept(DualSenseBtModel, DualSenseBtModel + ",Yours,a:b1,b:b0,platform:Windows,", string.Empty);
            ControllerDatabaseEngine.NoteCopy(DualSenseBt, model.Id, crcRow);
            string old = ControllerDatabaseUse.ReadBefore(DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,", model,
                ControllerDatabaseEngine.HeldBeforeCopy(DualSenseBt, out string beforeCopy), beforeCopy, ControllerDatabaseEngine.HadNone(DualSenseBt));
            var paste = Kept(DualSenseBt, DualSenseBt + ",Pasted,a:b5,platform:Windows,", old);
            Assert.Equal(crcRow, paste.OldRow);

            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty.With(paste);
            Assert.Empty(ControllerDatabaseEngine.GiveBack(model));
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(model.Id, ControllerDatabaseMine.Current));
            Assert.False(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            Assert.True(ControllerDatabaseEngine.ReadsRaw(otherName));

            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            Assert.Equal(new[] { ControllerDatabaseUse.Under(DualSenseBt, crcRow) }, ControllerDatabaseEngine.GiveBack(paste));
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(paste.Id, ControllerDatabaseMine.Current));
            Assert.False(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            Assert.True(ControllerDatabaseEngine.ReadsRaw(otherName));
        }

        /// <summary>
        /// The paste going first leaves the pad on the model row, never raw
        /// while that row stays, and the model row going then gives back what
        /// SDL had before either: here nothing, so the pad is read raw until
        /// SDL starts again. The model row's copy, given again as the pad
        /// opens, keeps what SDL had before the first one.
        /// </summary>
        [Fact]
        public void ThePasteGoingFirst_LeavesThePadOnTheModelRow()
        {
            ControllerDatabaseEngine.SdlStarting();
            string community = DualSenseBtModel + ",Community,a:b0,b:b1,platform:Windows,";
            var model = Kept(DualSenseBtModel, DualSenseBtModel + ",Yours,a:b1,b:b0,platform:Windows,", community);
            ControllerDatabaseEngine.NoteCopy(DualSenseBt, model.Id, null);
            string old = ControllerDatabaseUse.ReadBefore(DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,", model,
                ControllerDatabaseEngine.HeldBeforeCopy(DualSenseBt, out string beforeCopy), beforeCopy, ControllerDatabaseEngine.HadNone(DualSenseBt));
            var paste = Kept(DualSenseBt, DualSenseBt + ",Pasted,a:b5,platform:Windows,", old);
            Assert.Equal(string.Empty, paste.OldRow);

            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty.With(model);
            Assert.Empty(ControllerDatabaseEngine.GiveBack(paste));
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(paste.Id, ControllerDatabaseMine.Current));
            Assert.False(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            ControllerDatabaseEngine.NoteCopy(DualSenseBt, model.Id, DualSenseBt + ",Pasted,a:b5,platform:Windows,");

            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            Assert.Equal(new[] { ControllerDatabaseUse.Under(model.Id, community) }, ControllerDatabaseEngine.GiveBack(model));
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(model.Id, ControllerDatabaseMine.Current));
            Assert.True(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));

            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            Assert.Contains("var texts = ControllerDatabaseEngine.GiveBack(row);",
                FenceSource.Body(devices, "private static Task GiveBackAsync(ControllerDatabaseMineRow row,"), StringComparison.Ordinal);
        }

        /// <summary>
        /// SDL keeps a removed row of the user's until it quits, so it answers
        /// that row's bindings for the row's ID, though it had nothing there
        /// before. A row kept for such an ID afterwards, by Paste or by Add
        /// Rows, and a copy given for a device of it, replace nothing: the ID
        /// was marked as the row went, and that mark stays for the start after
        /// a new row lifts the raw read. So a second removal reads the pad raw
        /// again instead of giving the first row back. A device given its own
        /// mapping back under its ID is past a mark under its model's.
        /// </summary>
        [Fact]
        public void ARowKeptAfterARemovedOne_ReplacesNothingWhereSdlHadNothing()
        {
            const string otherName = "050034124c050000e60c000000006800";
            ControllerDatabaseEngine.SdlStarting();
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            string firstText = DualSenseBt + ",First,a:b5,platform:Windows,";
            Assert.Empty(ControllerDatabaseEngine.GiveBack(Kept(DualSenseBt, firstText, string.Empty)));
            Assert.True(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            Assert.True(ControllerDatabaseEngine.HadNone(DualSenseBt));

            Assert.Equal(string.Empty, ControllerDatabaseUse.ReadBefore(firstText, null, false, null, ControllerDatabaseEngine.HadNone(DualSenseBt)));
            ControllerDatabaseEngine.SetReadsRaw(DualSenseBt, false);
            Assert.False(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            Assert.True(ControllerDatabaseEngine.HadNone(DualSenseBt));
            var second = Kept(DualSenseBt, DualSenseBt + ",Second,a:b6,platform:Windows,", string.Empty);
            Assert.Empty(ControllerDatabaseEngine.GiveBack(second));
            Assert.True(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));

            ControllerDatabaseEngine.NoteCopy(DualSenseBt, DualSenseBtModel, firstText);
            Assert.True(ControllerDatabaseEngine.HeldBeforeCopy(DualSenseBt, out string beforeCopy));
            Assert.Null(beforeCopy);

            Assert.False(ControllerDatabaseEngine.HadNone(otherName));
            Assert.Empty(ControllerDatabaseEngine.GiveBack(Kept(DualSenseBtModel, DualSenseBtModel + ",Model,a:b1,platform:Windows,", string.Empty)));
            Assert.True(ControllerDatabaseEngine.HadNone(otherName));
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(DualSenseBtModel, ControllerDatabaseMine.Empty));
            Assert.Single(ControllerDatabaseEngine.GiveBack(Kept(otherName, otherName + ",Third,a:b7,platform:Windows,", otherName + ",SDL's,a:b0,platform:Windows,")));
            Assert.False(ControllerDatabaseEngine.HadNone(otherName));

            ControllerDatabaseEngine.SdlStarting();
            Assert.False(ControllerDatabaseEngine.HadNone(DualSenseBt));

            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            Assert.Contains("if (old != null && ControllerDatabaseEngine.HadNone(id)) old = string.Empty;",
                FenceSource.Body(devices, "internal async Task<int> AddRowsAsync(IReadOnlyList<SdlMappingRow> rows)"), StringComparison.Ordinal);
        }

        // ── No SDL_GAMECONTROLLERCONFIG ──

        /// <summary>PadForge hands SDL no hint of its own: SDL reads the
        /// SDL_GAMECONTROLLERCONFIG variable PadForge started with itself, at
        /// start. The card names the variable as a device's source, and no
        /// answer of a paste says the variable kept its row.</summary>
        [Fact]
        public void TheVariable_IsReadBySdlItself_AndNamedAsASource()
        {
            string manager = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.cs");
            string engine = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.ControllerDatabase.cs");
            foreach (string text in new[] { manager, engine })
            {
                Assert.DoesNotContain("SDL_SetHintWithPriority", text, StringComparison.Ordinal);
                Assert.DoesNotContain("GAMECONTROLLERCONFIG", text, StringComparison.Ordinal);
            }
            Assert.Contains("Variable", Enum.GetNames(typeof(SdlMappingSource)));
            Assert.DoesNotContain("VariableWins", Enum.GetNames(typeof(SdlApplyResult)));

            var doc = System.Xml.Linq.XDocument.Load(System.IO.Path.Combine(OffLimitsSource.RepoRoot(), "PadForge.App", "Resources", "Strings", "Strings.resx"));
            var keys = doc.Root.Elements("data").Select(d => (string)d.Attribute("name")).ToList();
            Assert.Contains("SdlMapping_From_Variable", keys);
            Assert.DoesNotContain("SdlMapping_VariableWins", keys);
        }

        /// <summary>
        /// As the engine starts, before SDL does, the joysticks read raw since
        /// a row of the user's went are read as SDL maps them again: SDL keeps
        /// a mapping once given until it quits, and starts with only the rows
        /// PadForge has then.
        /// </summary>
        [Fact]
        public void EachEngineStart_ForgetsTheRawReads()
        {
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            ControllerDatabaseEngine.SetReadsRaw(DualSenseBt, true);
            Assert.True(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));
            ControllerDatabaseEngine.SdlStarting();
            Assert.False(ControllerDatabaseEngine.ReadsRaw(DualSenseBt));

            string manager = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.cs");
            int method = manager.IndexOf("private bool InitializeSdl()", StringComparison.Ordinal);
            int guard = manager.IndexOf("if (DirectDeviceAccess.RefusesAll)", method, StringComparison.Ordinal);
            int starting = manager.IndexOf("ControllerDatabaseEngine.SdlStarting();", method, StringComparison.Ordinal);
            int init = manager.IndexOf("if (!SDL_Init(", method, StringComparison.Ordinal);
            Assert.True(method > 0 && guard > method && starting > guard && init > starting, "the raw reads are not forgotten before SDL starts");
        }

        // ── Whose device a row is for (brief part 5) ──

        /// <summary>A row whose ID names a checksum maps only devices of that
        /// name: SDL writes the ID's checksum into the row as it adds it
        /// (SDL_gamepad.c:2493) and passes over a row whose checksum is not
        /// the device's (:1586). A row with none maps every name of its model,
        /// so the card says "yours" only for a device SDL reads through the
        /// row.</summary>
        [Fact]
        public void ARowNamingAChecksum_MapsOnlyDevicesOfThatName()
        {
            const string clone = "050034124c050000e60c000000006800";
            Assert.True(SdlMappingRow.Maps(DualSenseBt, DualSenseBt));
            Assert.False(SdlMappingRow.Maps(DualSenseBt, clone));
            Assert.True(SdlMappingRow.Maps(DualSenseBtModel, clone));
            Assert.True(SdlMappingRow.Maps(DualSenseBtModel, DualSenseBt));
            Assert.Equal(0x5657, SdlMappingRow.ChecksumOf(DualSenseBt));
            Assert.Equal(0, SdlMappingRow.ChecksumOf("short"));

            Assert.True(SdlMappingRow.TryParse(DualSenseBt + ",DualSense,a:b1,platform:Windows,", out var row, out _));
            var mine = ControllerDatabaseMine.Empty.With(new ControllerDatabaseMineRow(DualSenseBt, row, DateTime.UnixEpoch, null, Guid.Empty,
                Array.Empty<ControllerDatabaseBefore>()));
            Assert.NotNull(mine.ForSdlDevice(DualSenseBt));
            Assert.Null(mine.ForSdlDevice(clone));
        }

        /// <summary>
        /// A row reaches a device at the step of SDL's lookup that would read
        /// it (SDL_PrivateGetGamepadMappingForGUID): the device's version with
        /// its checksum (SDL_gamepad.c:1618), its version with a row naming no
        /// checksum (:1623), then any version, with the checksum and then
        /// without (:1630, :1635), for a device whose ID names a vendor and a
        /// product in SDL's standard form and is no MFi controller's
        /// (SDL_JoystickGUIDUsesVersion). A row naming another checksum is
        /// never read. So a row of another firmware maps the device, at SDL's
        /// last tries. The standard form's bus is below 0x20 or the virtual
        /// bus, 0xFF (SDL_GetJoystickGUIDInfo).
        /// </summary>
        [Fact]
        public void ARowReachesADevice_AtTheStepOfSdlsLookup()
        {
            const string device = "03001a2b4c050000e60c000001010000";
            Assert.Equal(1, SdlMappingRow.Reach(device, device));
            Assert.Equal(2, SdlMappingRow.Reach("030000004c050000e60c000001010000", device));
            Assert.Equal(3, SdlMappingRow.Reach("03001a2b4c050000e60c000002010000", device));
            Assert.Equal(4, SdlMappingRow.Reach("030000004c050000e60c000002010000", device));
            Assert.Equal(4, SdlMappingRow.Reach("030000004c050000e60c000000000000", device));
            Assert.Equal(0, SdlMappingRow.Reach("030034124c050000e60c000001010000", device));
            Assert.Equal(0, SdlMappingRow.Reach("030000004c050000e70c000001010000", device));
            Assert.True(SdlMappingRow.Maps("030000004c050000e60c000002010000", device));

            const string unnamed = "030000004c050000e60c000001010000";
            Assert.Equal(1, SdlMappingRow.Reach(unnamed, unnamed));
            Assert.Equal(3, SdlMappingRow.Reach("030000004c050000e60c000002010000", unnamed));
            Assert.Equal(0, SdlMappingRow.Reach(device, unnamed));

            const string mfi = "050000004c050000e60c000001006d00";
            Assert.Equal(1, SdlMappingRow.Reach(mfi, mfi));
            Assert.Equal(0, SdlMappingRow.Reach("050000004c050000e60c000002006d00", mfi));
            Assert.Equal(3, SdlMappingRow.Reach("ff0000004c050000e60c000002010000", "ff0000004c050000e60c000001010000"));
            Assert.Equal(0, SdlMappingRow.Reach("760000004c050000e60c000002010000", "760000004c050000e60c000001010000"));
        }

        /// <summary>
        /// A paste for a pad of one firmware is what SDL reads a pad of
        /// another, of the same name, through when SDL holds nothing nearer
        /// for it: the card names it the user's, and no copy is given. Where
        /// SDL reads that pad through a mapping of its own, its HIDAPI one
        /// made at the pad's first open, the paste goes to SDL under the pad's
        /// own ID. Of the user's rows, the one at SDL's nearest step stands
        /// for a device, whatever their order, and so for Find's pick among a
        /// text's rows.
        /// </summary>
        [Fact]
        public void TheCardAndTheCopy_FollowTheRowSdlReads_ForAnotherFirmware()
        {
            const string first = "050057564c050000e60c000001016800";
            const string second = "050057564c050000e60c000002016800";
            var mine = MineOf(first + ",Pasted,a:b5,platform:Windows,");
            string throughPaste = second + ",Pasted,a:b5,platform:Windows,crc:5657,";
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(second, throughPaste, mine, null, null, null));
            Assert.Null(ControllerDatabaseUse.CopyFor(second, mine, throughPaste));
            Assert.Equal(second + ",Pasted,a:b5,platform:Windows,",
                ControllerDatabaseUse.CopyFor(second, mine, second + ",*,a:b0,b:b1,crc:5657,platform:Windows,"));

            var both = MineOf(DualSenseBtModel + ",Model,a:b1,platform:Windows,", "050000004c050000e60c000002016800,Firmware,a:b2,platform:Windows,");
            Assert.Equal("Firmware", both.ForSdlDevice(second).Row.Name);
            Assert.Equal("Model", both.ForSdlDevice(DualSenseBt).Row.Name);

            var rows = new List<SdlMappingRow>();
            foreach (string text in new[] { DualSenseBtModel + ",Model,a:b1,platform:Windows,", "050000004c050000e60c000002016800,Firmware,a:b2,platform:Windows," })
            {
                Assert.True(SdlMappingRow.TryParse(text, out var row, out _));
                rows.Add(row);
            }
            Assert.Equal("Firmware", SdlMappingRow.PickFor(rows, second, out var fit).Name);
            Assert.Equal(SdlMappingRow.Fit.ItsId, fit);
        }

        /// <summary>SDL reads a vendor and product only from an ID of its
        /// standard form, whose bus is below 0x20 or the virtual bus, 0xFF
        /// (SDL_GetJoystickGUIDInfo, SDL_HARDWARE_BUS_VIRTUAL). So a row for a
        /// GameCube adapter on the virtual bus gets the GameCube hint as SDL
        /// adds it, and one on bus 0x76 does not.</summary>
        [Fact]
        public void AGameCubeAdapterOnTheVirtualBus_ReadsAsSdlStoresIt()
        {
            Assert.Equal("a:b0,b:b2,x:b1,y:b3,", ControllerDatabaseSource.StoredLayoutOf("ff0000007e0500003703000000000000,GameCube,a:b0,b:b1,x:b2,y:b3,"));
            Assert.Equal("a:b0,b:b1,x:b2,y:b3,", ControllerDatabaseSource.StoredLayoutOf("760000007e0500003703000000000000,GameCube,a:b0,b:b1,x:b2,y:b3,"));
        }

        // ── A row in the community's form, under each device's own ID (brief part 3) ──

        private static ControllerDatabaseMine MineOf(params string[] rows)
        {
            var mine = ControllerDatabaseMine.Empty;
            foreach (string text in rows)
            {
                Assert.True(SdlMappingRow.TryParse(text, out var row, out var why), why.ToString());
                mine = mine.With(new ControllerDatabaseMineRow(row.Guid, row, DateTime.UnixEpoch, null, Guid.Empty,
                    Array.Empty<ControllerDatabaseBefore>()));
            }
            return mine;
        }

        /// <summary>
        /// SDL tries a device's exact version and checksum first
        /// (SDL_gamepad.c:1618), so a row of SDL's that names them stands
        /// over a row of the model with neither, as Add Rows keeps. Where SDL
        /// reads such a device through another row, the user's row is given
        /// to it once more under the device's own ID, as Paste gives one. A
        /// device SDL reads through the user's row already, one the user has
        /// a row of its own ID for, and one no row of the user's is for get
        /// nothing.
        /// </summary>
        [Fact]
        public void ARowInTheCommunitysForm_IsGivenUnderTheDevicesOwnId_WhereSdlReadsAnother()
        {
            var mine = MineOf(DualSenseBtModel + ",Yours,a:b1,b:b0,platform:Windows,");
            string sdls = DualSenseBt + ",DualSense,a:b0,b:b1,crc:5657,platform:Windows,";
            Assert.Equal(DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,", ControllerDatabaseUse.CopyFor(DualSenseBt, mine, sdls));
            Assert.Equal(DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,", ControllerDatabaseUse.CopyFor(DualSenseBt, mine, null));
            Assert.Null(ControllerDatabaseUse.CopyFor(DualSenseBt, mine, DualSenseBt + ",Yours,a:b1,b:b0,platform:Windows,"));
            Assert.Null(ControllerDatabaseUse.CopyFor("030057564c050000e60c000000006800", mine, sdls));
            Assert.Null(ControllerDatabaseUse.CopyFor(DualSenseBt, MineOf(DualSenseBt + ",Pasted,a:b2,platform:Windows,"), sdls));
            Assert.Null(ControllerDatabaseUse.CopyFor(DualSenseBt, ControllerDatabaseMine.Empty, sdls));
        }

        /// <summary>
        /// A row's copies go back with the row: each device SDL was given a
        /// copy for gets back the mapping SDL held under its ID before, or is
        /// read raw until SDL starts again when SDL held none. A device with a
        /// row of the user's under its own ID keeps that row's entry, and
        /// another row's copies stay. SDL starts again with no copy.
        /// </summary>
        [Fact]
        public void ARowsCopies_GoBackWithTheRow()
        {
            const string other = "050012344c050000e60c000000006800";
            const string third = "050056784c050000e60c000000006800";
            ControllerDatabaseEngine.SdlStarting();
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            ControllerDatabaseEngine.NoteCopy(DualSenseBt, DualSenseBtModel, DualSenseBt + ",DualSense,a:b0,crc:5657,");
            ControllerDatabaseEngine.NoteCopy(other, DualSenseBtModel, null);
            ControllerDatabaseEngine.NoteCopy(third, "030000004c050000e60c000000000000", third + ",Usb,a:b0,");
            // A second copy for a device keeps what SDL held before the first.
            ControllerDatabaseEngine.NoteCopy(DualSenseBt, DualSenseBtModel, DualSenseBt + ",Yours,a:b1,");

            var back = ControllerDatabaseEngine.TakeCopies(DualSenseBtModel, ControllerDatabaseMine.Empty);
            Assert.Equal(new[] { DualSenseBt + ",DualSense,a:b0,crc:5657," }, back);
            Assert.True(ControllerDatabaseEngine.ReadsRaw(other));
            Assert.Single(ControllerDatabaseEngine.TakeCopies("030000004c050000e60c000000000000", ControllerDatabaseMine.Empty));

            ControllerDatabaseEngine.NoteCopy(DualSenseBt, DualSenseBtModel, DualSenseBt + ",DualSense,a:b0,");
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(DualSenseBtModel, MineOf(DualSenseBt + ",Pasted,a:b2,platform:Windows,")));

            ControllerDatabaseEngine.NoteCopy(DualSenseBt, DualSenseBtModel, DualSenseBt + ",DualSense,a:b0,");
            ControllerDatabaseEngine.SdlStarting();
            Assert.Empty(ControllerDatabaseEngine.TakeCopies(DualSenseBtModel, ControllerDatabaseMine.Empty));
            ControllerDatabaseEngine.SetReadsRaw(other, false);
        }

        /// <summary>The copy is given before Step 1 opens a joystick, since
        /// the open reads SDL's mapping, and after rows are given while the
        /// engine runs, before a device is opened again. Remove Mine takes the
        /// row's copies back with it.</summary>
        [Fact]
        public void TheCopies_AreGivenBeforeEachOpen_AndTakenBackWithTheRow()
        {
            string engine = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.ControllerDatabase.cs");
            string offer = FenceSource.Body(engine, "private string OfferControllerDatabaseRow(uint instanceId)");
            int copy = offer.IndexOf("GiveCopy(instanceId);", StringComparison.Ordinal);
            int section = offer.IndexOf("if (section == null || section.NewRows == 0) return null;", StringComparison.Ordinal);
            Assert.True(copy >= 0 && section > copy, "the copy is not given before the joystick opens");

            string apply = FenceSource.Body(engine, "internal ControllerDatabaseApplied ApplySdlRows(IReadOnlyList<string> rows, bool offerNew)");
            int given = apply.IndexOf("GiveCopy(w.SdlInstanceId);", StringComparison.Ordinal);
            int reopen = apply.IndexOf("var reopened = new List<Guid>();", StringComparison.Ordinal);
            Assert.True(given > 0 && reopen > given, "the copies are not given before a device is opened again");

            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            Assert.Contains("ControllerDatabaseEngine.TakeCopies(row.Id, ControllerDatabaseMine.Current)",
                FenceSource.Body(devices, "private static Task GiveBackAsync(ControllerDatabaseMineRow row,"), StringComparison.Ordinal);
        }

        // ── A row compared the way SDL stores it (brief part 4) ──

        private const string Pad = "030000004c050000e60c000000000000";

        /// <summary>The Wii U GameCube adapter, 057E:0337, on SDL's GameCube
        /// list (SDL_joystick.c:538-549).</summary>
        private const string GameCubeAdapter = "030000007e0500003703000000000000";

        /// <summary>A row whose first hint is the button-label hint is stored
        /// with a and b, and x and y, swapped, and the hint marked "!"
        /// (SDL_gamepad.c:2857, :2996). The GameCube hint swaps b and x
        /// (:2784, :2936). A hint already marked "!" leaves the row as it is.
        /// SDL's own text for the mapping is the stored form, so the two read
        /// the same.</summary>
        [Fact]
        public void ARowWithALabelHint_ReadsAsSdlStoresIt()
        {
            string labels = Pad + ",Pad,a:b0,b:b1,x:b2,y:b3,hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,";
            Assert.Equal(Pad + ",Pad,b:b0,a:b1,y:b2,x:b3,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,",
                ControllerDatabaseSource.AsSdlStores(labels));
            string sdlsText = Pad + ",Pad,a:b1,b:b0,x:b3,y:b2,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,";
            Assert.Equal(ControllerDatabaseSource.LayoutOf(sdlsText), ControllerDatabaseSource.StoredLayoutOf(labels));
            Assert.Equal("a:b1,b:b0,x:b3,y:b2,", ControllerDatabaseSource.StoredLayoutOf(labels));

            string gameCube = Pad + ",Pad,a:b0,b:b1,x:b2,y:b3,hint:SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1,";
            Assert.Equal("a:b0,b:b2,x:b1,y:b3,", ControllerDatabaseSource.StoredLayoutOf(gameCube));
            string positional = Pad + ",Pad,a:b0,b:b1,x:b2,y:b3,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,";
            Assert.Equal(positional, ControllerDatabaseSource.AsSdlStores(positional));
            Assert.Equal("a:b0,b:b1,x:b2,y:b3,", ControllerDatabaseSource.StoredLayoutOf(positional));
        }

        /// <summary>A row for a controller on SDL's GameCube list that names
        /// no GameCube hint gets it as SDL adds the row (SDL_gamepad.c:2939),
        /// and is stored with b and x swapped. One that names it marked "!"
        /// stays, and a row of another controller gets nothing.</summary>
        [Fact]
        public void ARowForAGameCubeController_ReadsAsSdlStoresIt()
        {
            string row = GameCubeAdapter + ",GameCube,a:b0,b:b1,x:b2,y:b3,platform:Windows,";
            Assert.Equal("a:b0,b:b2,x:b1,y:b3,", ControllerDatabaseSource.StoredLayoutOf(row));
            string marked = GameCubeAdapter + ",GameCube,a:b0,b:b1,x:b2,y:b3,hint:!SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1,";
            Assert.Equal("a:b0,b:b1,x:b2,y:b3,", ControllerDatabaseSource.StoredLayoutOf(marked));
            Assert.Equal("a:b0,b:b1,x:b2,y:b3,", ControllerDatabaseSource.StoredLayoutOf(Pad + ",Pad,a:b0,b:b1,x:b2,y:b3,"));
        }

        /// <summary>
        /// The text SDL hands back for a mapping it holds is read as it is.
        /// It is the stored form already, and a mapping SDL builds itself
        /// never passes the conversion: a driver's automatic mapping goes
        /// straight into SDL's table with no hint
        /// (SDL_PrivateGenerateAutomaticGamepadMapping, SDL_gamepad.c:2700),
        /// for a controller on the GameCube list too. A row of the user's in
        /// the same bindings, which SDL stores with b and x swapped, is then
        /// not the mapping SDL reads the controller through: the card does not
        /// call it the user's, and the row goes to SDL under the controller's
        /// own ID.
        /// </summary>
        [Fact]
        public void SdlsOwnText_ReadsAsItIs()
        {
            const string device = "030000007e0500003703000000010000";
            string sdlsText = device + ",GameCube,a:b0,b:b1,x:b2,y:b3,platform:Windows,";
            Assert.Equal("a:b0,b:b1,x:b2,y:b3,", ControllerDatabaseSource.LayoutOf(sdlsText));

            var mine = MineOf(GameCubeAdapter + ",Mine,a:b0,b:b1,x:b2,y:b3,platform:Windows,");
            Assert.Equal(SdlMappingSource.Sdl, ControllerDatabaseSource.Of(device, sdlsText, mine, null, null, null));
            Assert.Equal(device + ",Mine,a:b0,b:b1,x:b2,y:b3,platform:Windows,", ControllerDatabaseUse.CopyFor(device, mine, sdlsText));
        }

        /// <summary>The card names a row of the user's with a label hint as
        /// the source of SDL's mapping, and every check of whether SDL reads a
        /// device through a row of the user's compares the stored form.</summary>
        [Fact]
        public void AUsersRowWithALabelHint_IsTheSourceOfWhatSdlStores()
        {
            Assert.True(SdlMappingRow.TryParse(Pad + ",Mine,a:b0,b:b1,x:b2,y:b3,hint:SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,",
                out var row, out var why), why.ToString());
            var mine = ControllerDatabaseMine.Empty.With(new ControllerDatabaseMineRow(Pad, row, DateTime.UnixEpoch, null, Guid.Empty,
                Array.Empty<ControllerDatabaseBefore>()));
            string sdlsText = Pad + ",Mine,a:b1,b:b0,x:b3,y:b2,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,platform:Windows,";
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(Pad, sdlsText, mine, null, null, null));
            Assert.Equal(ControllerDatabaseSource.LayoutOf(sdlsText), ControllerDatabaseSource.StoredLayout(mine.Rows[0]));

            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            string engine = OffLimitsSource.Read("PadForge.App/Common/Input/InputManager.ControllerDatabase.cs");
            Assert.DoesNotContain("Row.Layout", devices, StringComparison.Ordinal);
            Assert.DoesNotContain("Row.Layout", engine, StringComparison.Ordinal);
            // Each comparison goes through the one that reads the stored form.
            Assert.Contains("ControllerDatabaseSource.ReadsThrough(Sanitized(after.Mapping), keptRow)", devices, StringComparison.Ordinal);
            Assert.Contains("ControllerDatabaseSource.ReadsThrough(ControllerDatabaseEngine.MappingForId(wrapper.SdlInstanceId), row)", engine, StringComparison.Ordinal);
            string source = OffLimitsSource.Read("PadForge.App/Common/Input/ControllerDatabaseSource.cs");
            Assert.Contains("string stored = StoredLayout(row);", FenceSource.Body(source, "internal static bool ReadsThrough("), StringComparison.Ordinal);
        }

        /// <summary>The wrapper's word for the raw list says how long it
        /// holds: until SDL starts again, when it is forgotten.</summary>
        [Fact]
        public void TheRawList_IsSaidToHoldUntilSdlStartsAgain()
        {
            string wrapper = OffLimitsSource.Read("PadForge.Engine/Common/SdlDeviceWrapper.cs");
            int field = wrapper.IndexOf("public static Func<uint, bool> ReadsRawThisSession;", StringComparison.Ordinal);
            int doc = wrapper.LastIndexOf("/// <summary>", field, StringComparison.Ordinal);
            string words = wrapper.Substring(doc, field - doc);
            Assert.Contains("until SDL starts again", words, StringComparison.Ordinal);
            Assert.DoesNotContain("rest of the session", words, StringComparison.Ordinal);
        }

        /// <summary>A mapping is given back under the ID of the row it takes
        /// the place of, so SDL puts it on the same entry. Under an ID with no
        /// checksum it keeps no checksum field: SDL reads a field's checksum
        /// into such an ID, which would put the mapping on another entry
        /// (SDL_PrivateAddMappingForGUID).</summary>
        [Fact]
        public void AMappingGivenBack_GoesUnderTheRowsId()
        {
            Assert.Equal(DualSenseBtModel + ",DualSense,a:b3,platform:Windows",
                ControllerDatabaseUse.Under(DualSenseBtModel, DualSenseBt + ",DualSense,a:b3,crc:5657,platform:Windows"));
            Assert.Equal(DualSenseBt + ",DualSense,a:b3,crc:5657,", ControllerDatabaseUse.Under(DualSenseBt, "x,DualSense,a:b3,crc:5657,"));
            Assert.Equal("notarow", ControllerDatabaseUse.Under(DualSenseBt, "notarow"));
        }

        /// <summary>
        /// A mapping given back is SDL's own text, and SDL takes it through
        /// the turn again (SDL_PrivateAddGamepadMapping). Under an ID on the
        /// GameCube list, a text with no hint field would get the GameCube
        /// hint unmarked (SDL_gamepad.c:2939) and have b and x swapped
        /// (:2936), so it goes back with the hint marked "!", which SDL keeps
        /// as it is. SDL stores that hint for such an ID whatever it is given,
        /// so only the swap is avoided. A text with a hint field, or under
        /// another ID, goes back as it is.
        /// </summary>
        [Fact]
        public void AMappingGivenBack_KeepsItsBindings()
        {
            const string device = "030000007e0500003703000000010000";
            const string marked = "hint:!SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1,";
            string sdlsText = device + ",GameCube,a:b0,b:b1,x:b2,y:b3,platform:Windows,";
            string back = ControllerDatabaseUse.Under(device, sdlsText);
            Assert.Equal(sdlsText + marked, back);
            Assert.Equal(back, ControllerDatabaseSource.AsSdlStores(back));
            Assert.Equal(ControllerDatabaseSource.LayoutOf(sdlsText), ControllerDatabaseSource.StoredLayoutOf(back));
            Assert.Equal(device + ",GameCube,a:b0,b:b1,x:b2,y:b3," + marked,
                ControllerDatabaseUse.Under(device, device + ",GameCube,a:b0,b:b1,x:b2,y:b3"));

            string hidapi = device + ",GameCube,a:b0,b:b2,x:b1,y:b3," + marked + "platform:Windows,";
            Assert.Equal(hidapi, ControllerDatabaseUse.Under(device, hidapi));
            string pad = DualSenseBt + ",Pad,a:b0,b:b1,x:b2,y:b3,platform:Windows,";
            Assert.Equal(pad, ControllerDatabaseUse.Under(DualSenseBt, pad));
        }

        // ── What the SDL3.dll PadForge ships stored ──

        /// <summary>
        /// The texts the SDL3.dll PadForge shipped before SDL 68f6b71aef (x64,
        /// SHA-256 82f3e34ed43d900aec40c2b373bf3895818246ae01dd9e79a4ea2ca7690964c9,
        /// revision SDL-3.5.0-release-3.4.0-2012-g51470ad786) handed back on
        /// 2026-11-25 to a console probe run by hand with every joystick
        /// driver off, since the test host starts no SDL
        /// (TestHostFences.DeviceOutputOffLimits), read the way PadForge reads
        /// them. Each first answer, given back as Under writes it, was
        /// answered again as it was. D: SDL read a device of firmware 0x0101
        /// through a row of version 0x0102 while it held nothing nearer, and
        /// through a row of the device's own version once there was one. A: SDL read a DualSense past the model
        /// row through its own HIDAPI mapping, so CopyFor gives the copy in the
        /// form the probe gave, and the copy SDL then held, with the checksum
        /// SDL added, reads as the user's row. B: SDL stored a text with no
        /// hint field under the GameCube adapter's ID as AsSdlStores says, and
        /// Under's text as given. C: SDL read a GameSir past a row in the
        /// community's form through its built-in row naming the checksum, and
        /// a row under the device's own ID replaced that row.
        /// </summary>
        [Fact]
        public void WhatTheShippedSdlStored_IsWhatPadForgeReads()
        {
            const string ds = "050057564c050000e60c000000006800";
            string sdlsOwn = ds + ",*,a:b0,b:b1,back:b4,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b5,leftshoulder:b9,leftstick:b7,"
                + "lefttrigger:a4,leftx:a0,lefty:a1,rightshoulder:b10,rightstick:b8,righttrigger:a5,rightx:a2,righty:a3,start:b6,x:b2,y:b3,"
                + "touchpad:b11,misc1:b12,crc:5657,platform:Windows,";
            var model = MineOf("050000004c050000e60c000000006800,Probe Model,a:b1,b:b0,x:b3,y:b2,platform:Windows,");
            Assert.Equal(ds + ",Probe Model,a:b1,b:b0,x:b3,y:b2,platform:Windows,", ControllerDatabaseUse.CopyFor(ds, model, sdlsOwn));
            string copyHeld = ds + ",Probe Model,a:b1,b:b0,x:b3,y:b2,platform:Windows,crc:5657,";
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(ds, copyHeld, model, null, null, null));
            Assert.Null(ControllerDatabaseUse.CopyFor(ds, model, copyHeld));
            Assert.Equal(sdlsOwn, ControllerDatabaseUse.Under(ds, sdlsOwn));
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of("050034124c050000e60c000000006800",
                "050034124c050000e60c000000006800,Probe Model,a:b1,b:b0,x:b3,y:b2,platform:Windows,", model, null, null, null));

            const string gc = "030000007e0500003703000000010000";
            const string marked = "hint:!SDL_GAMECONTROLLER_USE_GAMECUBE_LABELS:=1,";
            string bare = gc + ",GameCube Probe,a:b0,b:b1,x:b2,y:b3,platform:Windows,";
            Assert.Equal(gc + ",GameCube Probe,a:b0,x:b1,b:b2,y:b3,platform:Windows," + marked, ControllerDatabaseSource.AsSdlStores(bare));
            string givenBack = ControllerDatabaseUse.Under(gc, bare);
            Assert.Equal(gc + ",GameCube Probe,a:b0,b:b1,x:b2,y:b3,platform:Windows," + marked, givenBack);
            Assert.Equal(givenBack, ControllerDatabaseSource.AsSdlStores(givenBack));
            Assert.Equal(ControllerDatabaseSource.LayoutOf(bare), ControllerDatabaseSource.LayoutOf(givenBack));

            const string gs = "0300a41a491900000204000000000000";
            string builtIn = gs + ",GameSir T4 Pro,crc:1aa4,a:b0,b:b1,back:b10,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,leftshoulder:b6,"
                + "leftstick:b13,lefttrigger:b8,leftx:a0,lefty:a1,rightshoulder:b7,rightstick:b14,righttrigger:b9,rightx:a3,righty:a4,start:b11,"
                + "x:b3,y:b4,platform:Windows,";
            var community = MineOf("03000000491900000204000000000000,Probe Community,a:b1,b:b0,platform:Windows,");
            Assert.Equal(SdlMappingSource.Sdl, ControllerDatabaseSource.Of(gs, builtIn, community, null, null, null));
            Assert.Equal(gs + ",Probe Community,a:b1,b:b0,platform:Windows,", ControllerDatabaseUse.CopyFor(gs, community, builtIn));
            Assert.Equal(builtIn, ControllerDatabaseUse.Under(gs, builtIn));
            var own = MineOf(gs + ",Probe Yours,a:b2,b:b3,platform:Windows,");
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(gs, gs + ",Probe Yours,a:b2,b:b3,platform:Windows,crc:1aa4,", own, null, null, null));
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of("03003412491900000204000000000000",
                "03003412491900000204000000000000,Probe Community,a:b1,b:b0,platform:Windows,", community, null, null, null));

            const string firmware = "03001a2b341200007856000001010000";
            var otherFirmware = MineOf("03000000341200007856000002010000,Probe Other Firmware,a:b1,b:b0,platform:Windows,");
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(firmware,
                firmware + ",Probe Other Firmware,a:b1,b:b0,platform:Windows,", otherFirmware, null, null, null));
            var bothFirmwares = MineOf("03000000341200007856000002010000,Probe Other Firmware,a:b1,b:b0,platform:Windows,",
                "03000000341200007856000001010000,Probe Own Firmware,a:b2,b:b3,platform:Windows,");
            Assert.Equal("Probe Own Firmware", bothFirmwares.ForSdlDevice(firmware).Row.Name);
            Assert.Equal(SdlMappingSource.Yours, ControllerDatabaseSource.Of(firmware,
                firmware + ",Probe Own Firmware,a:b2,b:b3,platform:Windows,", bothFirmwares, null, null, null));
        }
    }
}
