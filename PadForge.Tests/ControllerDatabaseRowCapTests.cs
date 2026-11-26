using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The user's own rows past the cap, and the rows of the user's own
    /// SDL_GAMECONTROLLERCONFIG variable (#600 item 49). The cap is 2,048,
    /// above the community's whole Windows file, and a kept row is never
    /// dropped to make room: a row past it is not added, and Add Rows says how
    /// many will and will not be. The variable is read from the signed-in
    /// user's profile, never from PadForge's own process, and only offered:
    /// its rows are kept when the user presses Add. The engine still never
    /// has the variable (ControllerDatabaseRowsWinTests.TheVariable_IsNoSource,
    /// ProtectedStartAllowListTests).
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ControllerDatabaseRowCapTests : IDisposable
    {
        private static readonly Guid Pad = new("66666666-6666-4666-8666-666666666666");
        private const string PadSdlGuid = "050057564c050000e60c000000006800";

        private readonly ControllerDatabaseMine _savedMine = ControllerDatabaseMine.Current;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly ControllerDatabaseSection _savedSection = SettingsManager.ControllerDatabase;

        public void Dispose()
        {
            ControllerDatabaseMine.Current = _savedMine;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.ControllerDatabase = _savedSection;
            ControllerDatabaseEngine.SdlStarting();
        }

        private static SdlMappingRow Read(string text)
        {
            Assert.True(SdlMappingRow.TryParse(text, out var row, out var why), why.ToString());
            return row;
        }

        /// <summary>A row of the user's for a made-up vendor and the
        /// product given, added now.</summary>
        private static ControllerDatabaseMineRow Mine(int product) =>
            Mine(Read(Row(0x3000 + product / 0x10000, product % 0x10000, "Kept " + product)));

        private static ControllerDatabaseMineRow Mine(SdlMappingRow row) =>
            new(row.Guid, row, new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc), string.Empty, Guid.Empty,
                Array.Empty<ControllerDatabaseBefore>());

        private static ControllerDatabaseMine Filled(int count)
        {
            var mine = ControllerDatabaseMine.Empty;
            for (int i = 0; i < count; i++) mine = mine.With(Mine(i));
            Assert.Equal(count, mine.Count);
            return mine;
        }

        private static List<SdlMappingRow> Rows(int first, int count) =>
            Enumerable.Range(first, count).Select(i => Read(Row(0x4000, i, "New " + i))).ToList();

        private sealed class Harness : IDisposable
        {
            public readonly MainViewModel Vm = new();
            public readonly ControllerDatabaseController Controller;
            public string Variable;

            public Harness()
            {
                Controller = new ControllerDatabaseController(Vm, () => { })
                {
                    Baseline = () => Baseline(),
                    OwnRows = Array.Empty<string>(),
                    Zone = TimeZoneInfo.Utc,
                    Log = _ => { },
                    SettleAfterSwitch = _ => { },
                    ProfileVariable = () => Variable,
                };
                Controller.Start();
            }

            public void Dispose() => Controller.Dispose();
        }

        private static void NoDevices()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.ControllerDatabase = null;
        }

        // ── The cap ──

        /// <summary>2,048, above the community's whole Windows file, which
        /// held 869 rows at the clone this was measured on.</summary>
        [Fact]
        public void TheCap_HoldsTheCommunitysWholeWindowsFile()
        {
            Assert.Equal(2048, ControllerDatabaseMine.MaxRows);
            Assert.True(ControllerDatabaseMine.MaxRows > 869);
        }

        /// <summary>At the cap a row for a new ID is not added and no kept
        /// row goes. A row under a kept ID still replaces it.</summary>
        [Fact]
        public void AtTheCap_ANewRowIsNotAdded_AndNoKeptRowIsDropped()
        {
            var full = Filled(ControllerDatabaseMine.MaxRows);
            var first = full.Rows[0];
            Assert.False(full.HasRoomFor(Mine(ControllerDatabaseMine.MaxRows)));
            Assert.Same(full, full.With(Mine(ControllerDatabaseMine.MaxRows)));
            Assert.Contains(first, full.Rows);

            var again = Mine(Read(Row(0x3000, 5, "Kept 5 again")));
            Assert.True(full.HasRoomFor(again));
            var replaced = full.With(again);
            Assert.Equal(ControllerDatabaseMine.MaxRows, replaced.Count);
            Assert.Contains(replaced.Rows, r => r.Row.Name == "Kept 5 again");
            Assert.Same(first, replaced.Rows[0]);
        }

        /// <summary>The count Add Rows shows: a new ID takes room while there
        /// is some, a kept ID or one earlier in the text takes none.</summary>
        [Fact]
        public void TheAddCount_IsWhatWillBeKept()
        {
            var mine = Filled(2040);
            Assert.Equal(8, mine.AddCount(Rows(0, 700)));

            var text = new List<SdlMappingRow> { mine.Rows[3].Row, mine.Rows[4].Row };
            text.AddRange(Rows(0, 10));
            text.Add(Rows(2, 1)[0]);
            // Two kept IDs and a repeat take no room: 2 + 8 + 1 of 13.
            Assert.Equal(11, mine.AddCount(text));
            Assert.Equal(700, ControllerDatabaseMine.Empty.AddCount(Rows(0, 700)));
        }

        /// <summary>Add Rows past the cap keeps what fits, drops no kept row
        /// and says how many it kept.</summary>
        [Fact]
        public async Task AddRows_PastTheCap_KeepsWhatFits_AndDropsNoKeptRow()
        {
            NoDevices();
            ControllerDatabaseMine.Current = Filled(ControllerDatabaseMine.MaxRows - 2);
            var first = ControllerDatabaseMine.Current.Rows[0];
            var keptBefore = ControllerDatabaseMine.Current.Rows.Select(r => r.Id).ToList();
            using var h = new Harness();
            var rows = Rows(0, 5);
            rows.Add(first.Row);
            Assert.Equal(3, await h.Vm.Settings.AddControllerDatabaseRows(rows));
            Assert.Equal(ControllerDatabaseMine.MaxRows, ControllerDatabaseMine.Current.Count);
            // The kept row the text named was replaced, and no kept row went.
            var ids = ControllerDatabaseMine.Current.Rows.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(keptBefore, id => Assert.Contains(id, ids));
            Assert.NotNull(ControllerDatabaseMine.Current.ForSdlDevice(rows[1].Guid));
            Assert.Null(ControllerDatabaseMine.Current.ForSdlDevice(rows[2].Guid));
        }

        /// <summary>Paste at the cap for a device with no row of the user's
        /// keeps nothing, gives SDL nothing, and says why.</summary>
        [Fact]
        public async Task Paste_AtTheCap_KeepsNothing_AndSaysWhy()
        {
            NoDevices();
            var ud = new UserDevice { InstanceGuid = Pad, ProductName = "T.16000M", SdlGuid = PadSdlGuid, CapType = InputDeviceType.Joystick, IsOnline = false };
            lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            var full = Filled(ControllerDatabaseMine.MaxRows);
            ControllerDatabaseMine.Current = full;
            using var h = new Harness();
            var result = await h.Controller.ApplyMineAsync(Pad, Read("050000004c050000e60c000000006800,Pasted,a:b1,platform:Windows,"));
            Assert.Equal(SdlApplyResult.Full, result);
            Assert.Same(full, ControllerDatabaseMine.Current);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.SdlMapping_Full_Format, ControllerDatabaseMine.MaxRows),
                SdlMappingCard.Outcome(result));
        }

        /// <summary>The dialog's count in both states, as the Database board
        /// draws them.</summary>
        [Fact]
        public void TheAddDialogsCount_SaysWhatWillAndWillNotBeAdded()
        {
            var culture = CultureInfo.CurrentCulture;
            Assert.Equal(string.Format(culture, Strings.Instance.SdlMappingDialog_AddAll_Format, 700, 2048),
                SdlMappingDialog.AddCountWords(700, 700, 3));
            Assert.Equal(string.Format(culture, Strings.Instance.SdlMappingDialog_AddSome_Format, 2040, 8, 692),
                SdlMappingDialog.AddCountWords(700, 8, 2040));
            Assert.Equal(string.Format(culture, Strings.Instance.SdlMappingDialog_AddAll_One_Format, 1, 2048),
                SdlMappingDialog.AddCountWords(1, 1, 0));
        }

        // ── The variable ──

        private static readonly string VarRowOne = Row(0x5000, 0x0001, "Profile One");
        private static readonly string VarRowTwo = Row(0x5000, 0x0002, "Profile Two");

        /// <summary>The value is found by its name in any case, among the
        /// lines Windows builds.</summary>
        [Fact]
        public void TheVariablesValue_IsFoundByItsName()
        {
            var lines = new[] { "Path=C:\\Windows", "sdl_gamecontrollerconfig=" + VarRowOne, "SDL_GAMECONTROLLERCONFIG_FILE=C:\\x.txt" };
            Assert.Equal(VarRowOne, ControllerDatabaseProfileRows.ValueOf(lines, ControllerDatabaseProfileRows.Variable));
            Assert.Null(ControllerDatabaseProfileRows.ValueOf(new[] { "SDL_GAMECONTROLLERCONFIG_FILE=C:\\x.txt" }, ControllerDatabaseProfileRows.Variable));
            Assert.Null(ControllerDatabaseProfileRows.ValueOf(null, ControllerDatabaseProfileRows.Variable));
        }

        /// <summary>Only rows that pass the paste's check and that the user
        /// does not keep as written are offered.</summary>
        [Fact]
        public void OnlyRowsThatPassAndAreNotKept_AreOffered()
        {
            string text = VarRowOne + "\n" + VarRowTwo + "\n"
                + "030000008f0e00000300000009010000,Mac Pad,a:b2,platform:Mac OS X,\n"
                + "not a row\n"
                + Row(0x5000, 0x0003, "Hinted").Replace("platform:Windows,", "hint:SDL_X:=1,platform:Windows,");
            var mine = ControllerDatabaseMine.Empty.With(Mine(Read(VarRowOne)));
            var offered = ControllerDatabaseProfileRows.NotKept(text, mine);
            Assert.Equal(new[] { Id(0x5000, 0x0002) }, offered.Select(r => r.Guid));
            Assert.Empty(ControllerDatabaseProfileRows.NotKept(null, mine));
        }

        /// <summary>The Your Rows line says how many of the variable's rows
        /// PadForge does not keep, Add Rows opens with them when the
        /// clipboard holds no row, and the line goes quiet once they are
        /// added.</summary>
        [Fact]
        public async Task TheYourRowsLine_OffersTheVariablesRows_AndGoesQuietOnceAdded()
        {
            NoDevices();
            ControllerDatabaseMine.Current = ControllerDatabaseMine.Empty;
            using var h = new Harness { };
            h.Variable = VarRowOne + "\n" + VarRowTwo;
            h.Vm.Settings.RaiseControllerDatabaseMine();
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Instance.ControllerDatabase_ProfileRows_Format, 2),
                h.Vm.Settings.ControllerDatabaseProfileRowsStatus);
            string text = h.Vm.Settings.ControllerDatabaseProfileRowsText;
            Assert.Equal(Read(VarRowOne).Text + "\n" + Read(VarRowTwo).Text, text);

            // The clipboard's row comes first, then the variable's.
            Assert.Equal(text, SdlMappingDialog.AddRowsText("no row here", text));
            Assert.Equal(VarRowOne, SdlMappingDialog.AddRowsText(VarRowOne, text));
            Assert.Null(SdlMappingDialog.AddRowsText(null, null));

            var rows = SdlMappingRow.ReadAll(text, out _, out _, out _);
            Assert.Equal(2, await h.Vm.Settings.AddControllerDatabaseRows(rows));
            Assert.Equal(string.Empty, h.Vm.Settings.ControllerDatabaseProfileRowsStatus);
            Assert.Null(h.Vm.Settings.ControllerDatabaseProfileRowsText);

            h.Variable = null;
            Assert.Equal(string.Empty, h.Vm.Settings.ControllerDatabaseProfileRowsStatus);
        }

        /// <summary>The variable is read through the desktop shell's token
        /// from the signed-in user's profile, never from PadForge's own
        /// process, and the controller reads it that way unless a test gives
        /// another reader.</summary>
        [Fact]
        public void TheVariable_IsReadFromTheUsersProfile_NeverThisProcess()
        {
            string reader = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseProfileRows.cs");
            string read = FenceSource.Body(reader, "internal static string Read()");
            Assert.Contains("PluginLauncher.OpenShell()", read, StringComparison.Ordinal);
            Assert.Contains("PluginLauncher.ShellVariables(shell, out _)", read, StringComparison.Ordinal);
            Assert.DoesNotContain("GetEnvironmentVariable", reader, StringComparison.Ordinal);
            Assert.DoesNotContain("Environment.", reader, StringComparison.Ordinal);
            Assert.DoesNotContain("CurrentProcess", reader, StringComparison.Ordinal);
            string devices = OffLimitsSource.Read("PadForge.App/Services/ControllerDatabaseController.Devices.cs");
            Assert.Contains("internal Func<string> ProfileVariable = ControllerDatabaseProfileRows.Read;", devices, StringComparison.Ordinal);
        }
    }
}
