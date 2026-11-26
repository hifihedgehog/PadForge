using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using static PadForge.Tests.ControllerDatabaseFixture;

namespace PadForge.Tests
{
    /// <summary>
    /// The Controller Database row's two commands (#597): Update Now and
    /// the reset, with what the row says after each. The network is a
    /// stand-in, the baseline is a small one of the test's own, and the
    /// section is the real one in SettingsManager, put back afterward.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class ControllerDatabaseControllerTests : IDisposable
    {
        private readonly ControllerDatabaseSection _saved = SettingsManager.ControllerDatabase;
        private readonly string _savedFrom = SettingsManager.ControllerDatabaseFrom;

        public ControllerDatabaseControllerTests()
        {
            SettingsManager.ControllerDatabase = null;
            SettingsManager.ControllerDatabaseFrom = null;
        }

        public void Dispose()
        {
            SettingsManager.ControllerDatabase = _saved;
            SettingsManager.ControllerDatabaseFrom = _savedFrom;
        }

        private static readonly string KeptRow = Row(0x1000, 0x0001, "Kept Pad");
        private static readonly string SdlRow = Row(0x1000, 0x0002, "SDL Pad");
        private static readonly string ElsewhereRow = Row(0x1000, 0x0003, "Mac Pad");
        private static readonly string LeftOutRow = Row(0x1000, 0x0004, "Driver Pad");
        private static readonly string ChangedKept = KeptRow.Replace("a:b0", "a:b7");
        private static readonly string NewOne = Row(0x2000, 0x0001, "New One");
        private static readonly string NewTwo = Row(0x2000, 0x0002, "New Two");

        private static readonly string[] Own =
        {
            "0300d8234c0500006802000000000000,Sony DualShock 3 (DsHidMini SDF and SXS),a:b2,b:b1,platform:Windows,",
            "03000000373500002210000000000000,GameSir G7 Pro,a:b0,b:b1,platform:Windows,",
        };

        private static readonly string SdlRowTwo = Row(0x1000, 0x0006, "SDL Pad Two");

        private static ControllerDatabaseBaseline Pinned() =>
            Baseline(('K', KeptRow), ('S', SdlRow), ('O', ElsewhereRow), ('N', LeftOutRow), ('S', SdlRowTwo));

        /// <summary>A community file of the pinned rows that keep nothing as
        /// written, with the ones given in place of, or beside, them. The
        /// row SDL maps elsewhere only is kept as written, so a test that
        /// wants it names it.</summary>
        private static byte[] FileWith(params string[] rows)
        {
            var all = new List<string>(rows);
            foreach (string pinned in new[] { KeptRow, SdlRow, SdlRowTwo, LeftOutRow })
                if (!all.Any(r => r.StartsWith(pinned.Substring(0, 33), StringComparison.Ordinal))) all.Add(pinned);
            return CommunityFile(all);
        }

        private static ControllerDatabaseFetch Fetched(byte[] file, string etag = "W/\"tag-1\"", DateTime? serverTime = null) =>
            new(ControllerDatabaseFetchOutcome.Fetched, file, etag, serverTime);

        private sealed class Harness : IDisposable
        {
            public readonly MainViewModel Vm = new();
            public readonly ControllerDatabaseController Controller;
            public readonly List<string> AskedTags = new();
            public readonly List<string> AskedAddresses = new();
            public readonly List<string> Log = new();
            public int Dirty;
            public Func<string, CancellationToken, Task<ControllerDatabaseFetch>> Answer =
                (_, _) => throw new InvalidOperationException("the test set no answer");
            public DateTime Now = new(2026, 11, 23, 14, 17, 35, DateTimeKind.Utc);

            public Harness()
            {
                var baseline = Pinned().WithOwnRows(Own);
                Controller = new ControllerDatabaseController(Vm, () => Dirty++)
                {
                    Fetch = (address, etag, ct) =>
                    {
                        AskedAddresses.Add(address);
                        AskedTags.Add(etag);
                        return Answer(etag, ct);
                    },
                    Baseline = () => baseline,
                    UtcNow = () => Now,
                    AppVersion = "6.0.1 (test)",
                    OwnRows = Own,
                    Zone = TimeZoneInfo.Utc,
                    Log = Log.Add,
                };
                Controller.Start();
            }

            public SettingsViewModel Settings => Vm.Settings;

            public void Answers(ControllerDatabaseFetch fetch) => Answer = (_, _) => Task.FromResult(fetch);

            public void Dispose() => Controller.Dispose();
        }

        private static string BuiltIn(int count) =>
            string.Format(CultureInfo.CurrentCulture, Strings.Instance.ControllerDatabase_BuiltIn_Format, count, "2026-09-24");

        private static string Updated(int count, string day, int added, int changed) =>
            string.Format(CultureInfo.CurrentCulture, Strings.Instance.ControllerDatabase_BuiltInUpdated_Format,
                count, "2026-09-24", day, added, changed);

        // ── The status line ──

        /// <summary>Before any update: SDL's 313 rows, the one community
        /// row this baseline's fork kept and PadForge's own two, and the
        /// pinned copy's day.</summary>
        [Fact]
        public void BeforeAnyUpdate_TheRowSaysWhatIsBuiltIn()
        {
            using var h = new Harness();
            Assert.Equal(BuiltIn(316), h.Settings.ControllerDatabaseStatus);
            Assert.Equal(string.Empty, h.Settings.ControllerDatabaseResult);
            Assert.False(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
            Assert.True(h.Settings.UpdateControllerDatabaseCommand.CanExecute(null));
            Assert.Equal(0, h.Dirty);
        }

        [Fact]
        public async Task UpdateNow_StoresWhatIsNewAndChanged_AndTheRowCountsIt()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(ChangedKept, NewOne, NewTwo), serverTime: new DateTime(2026, 11, 22, 23, 59, 59, DateTimeKind.Utc)));
            var raised = new List<string>();
            h.Settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            await h.Controller.UpdateAsync();

            var section = SettingsManager.ControllerDatabase;
            Assert.NotNull(section);
            Assert.Equal(2, section.NewRows);
            Assert.Equal(1, section.ChangedRows);
            Assert.Equal("W/\"tag-1\"", section.ETag);
            Assert.Equal("6.0.1 (test)", section.AppVersion);
            Assert.Equal(Pin, section.BaselinePin);
            // GitHub's own time is the fetch time when it gives one.
            Assert.Equal(new DateTime(2026, 11, 22, 23, 59, 59, DateTimeKind.Utc), section.FetchedUtc);
            Assert.Equal(1, h.Dirty);
            Assert.Equal(new string[] { null }, h.AskedTags);

            Assert.Equal(Updated(316, "2026-11-22", 2, 1), h.Settings.ControllerDatabaseStatus);
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseStatus), raised);
            // Rows were stored, and they are in use at once (#600).
            Assert.Equal(Strings.Instance.ControllerDatabase_InUseAtOnce, h.Settings.ControllerDatabaseResult);
            Assert.False(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
            Assert.Contains(h.Log, l => l.Contains("stored: 2 new, 1 changed", StringComparison.Ordinal));
        }

        /// <summary>With no time from GitHub, the PC's clock is the fetch
        /// time, and the row shows its day in the user's own zone.</summary>
        [Fact]
        public async Task TheUpdatesDay_IsTheUsersOwn()
        {
            using var h = new Harness();
            h.Controller.Zone = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(-10), "test", "test");
            h.Now = new DateTime(2026, 11, 24, 3, 0, 0, DateTimeKind.Utc);
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            Assert.Equal(h.Now, SettingsManager.ControllerDatabase.FetchedUtc);
            Assert.Equal(Updated(316, "2026-11-23", 1, 0), h.Settings.ControllerDatabaseStatus);
            Assert.Equal(Strings.Instance.ControllerDatabase_InUseAtOnce, h.Settings.ControllerDatabaseResult);
        }

        [Fact]
        public async Task AnUpdateThatFindsNothingNew_IsStillStored_AndSaysSoInItsCounts()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith()));
            await h.Controller.UpdateAsync();
            Assert.NotNull(SettingsManager.ControllerDatabase);
            Assert.Empty(SettingsManager.ControllerDatabase.Rows);
            Assert.Equal(1, h.Dirty);
            Assert.Equal(Updated(316, "2026-11-23", 0, 0), h.Settings.ControllerDatabaseStatus);
            Assert.Equal(string.Empty, h.Settings.ControllerDatabaseResult);
        }

        /// <summary>The button says a download runs, and takes no second
        /// press while it does.</summary>
        [Fact]
        public async Task WhileADownloadRuns_TheButtonSaysSo_AndASecondPressDoesNothing()
        {
            using var h = new Harness();
            var gate = new TaskCompletionSource<ControllerDatabaseFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Answer = (_, _) => gate.Task;
            var canExecute = new List<bool>();
            h.Settings.UpdateControllerDatabaseCommand.CanExecuteChanged += (s, e) => canExecute.Add(h.Settings.UpdateControllerDatabaseCommand.CanExecute(null));

            // The press goes through the row's own command.
            h.Settings.UpdateControllerDatabaseCommand.Execute(null);
            Assert.True(h.Settings.IsControllerDatabaseUpdating);
            Assert.False(h.Settings.UpdateControllerDatabaseCommand.CanExecute(null));
            Assert.Single(h.AskedTags);

            await h.Controller.UpdateAsync();
            Assert.Single(h.AskedTags);

            gate.SetResult(Fetched(FileWith(NewOne)));
            await WaitFor(() => !h.Settings.IsControllerDatabaseUpdating);
            Assert.True(h.Settings.UpdateControllerDatabaseCommand.CanExecute(null));
            Assert.Equal(new[] { false, true }, canExecute);
            Assert.Equal(1, SettingsManager.ControllerDatabase.NewRows);
        }

        private static async Task WaitFor(Func<bool> done)
        {
            for (int i = 0; i < 500 && !done(); i++) await Task.Delay(10);
            Assert.True(done(), "the download did not end");
        }

        // ── Nothing changed, no network, a refused file ──

        /// <summary>A 304 keeps the section as it is: the same rows, the
        /// same tag, the same day, and nothing to save.</summary>
        [Fact]
        public async Task AnswerNotModified_KeepsTheSection()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne, ChangedKept)));
            await h.Controller.UpdateAsync();
            var stored = SettingsManager.ControllerDatabase;
            string status = h.Settings.ControllerDatabaseStatus;

            h.Now = h.Now.AddDays(3);
            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.NotModified));
            await h.Controller.UpdateAsync();

            Assert.Same(stored, SettingsManager.ControllerDatabase);
            Assert.Equal(1, h.Dirty);
            // The second request named the stored tag.
            Assert.Equal(new[] { null, "W/\"tag-1\"" }, h.AskedTags);
            Assert.Equal(status, h.Settings.ControllerDatabaseStatus);
            Assert.Equal(Strings.Instance.ControllerDatabase_NothingChanged, h.Settings.ControllerDatabaseResult);
            Assert.False(h.Settings.ControllerDatabaseResultIsWarning);
        }

        /// <summary>A file under a new tag replaces the section: its rows,
        /// its tag and its day.</summary>
        [Fact]
        public async Task AFileUnderANewTag_ReplacesTheSection()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne, ChangedKept)));
            await h.Controller.UpdateAsync();
            var first = SettingsManager.ControllerDatabase;

            h.Now = h.Now.AddDays(3);
            h.Answers(Fetched(FileWith(NewTwo), "W/\"tag-2\""));
            await h.Controller.UpdateAsync();

            var second = SettingsManager.ControllerDatabase;
            Assert.NotSame(first, second);
            Assert.Equal("W/\"tag-2\"", second.ETag);
            Assert.Equal(h.Now, second.FetchedUtc);
            Assert.Equal(NewTwo, Assert.Single(second.Rows).Row.Text);
            Assert.Equal(2, h.Dirty);
            Assert.Equal(new[] { null, "W/\"tag-1\"" }, h.AskedTags);
            Assert.Equal(Updated(316, "2026-11-26", 1, 0), h.Settings.ControllerDatabaseStatus);
        }

        [Fact]
        public async Task WithNoNetwork_TheSectionStays_AndTheRowSaysWhy()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            var stored = SettingsManager.ControllerDatabase;

            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable, Reason: "No such host is known."));
            await h.Controller.UpdateAsync();
            Assert.Same(stored, SettingsManager.ControllerDatabase);
            Assert.Equal(1, h.Dirty);
            Assert.Equal(string.Format(Strings.Instance.ControllerDatabase_NoNetwork_Format, "No such host is known."),
                h.Settings.ControllerDatabaseResult);
            Assert.True(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.DoesNotContain("\n", h.Settings.ControllerDatabaseResult);

            // A stall is worded by PadForge, in the language in use.
            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable, Stalled: true));
            await h.Controller.UpdateAsync();
            Assert.Equal(string.Format(Strings.Instance.ControllerDatabase_NoNetwork_Format, Strings.Instance.Update_DownloadStalled),
                h.Settings.ControllerDatabaseResult);
            Assert.True(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
        }

        /// <summary>A file refused by the checks, and an answer the request
        /// itself refused, change nothing. The row says so in one line, and
        /// the log has the reason.</summary>
        [Fact]
        public async Task ARefusedFile_ChangesNothing_AndTheRowSaysSo()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            var stored = SettingsManager.ControllerDatabase;

            // Too small to be the database.
            h.Answers(Fetched(System.Text.Encoding.UTF8.GetBytes("<html>404: Not Found</html>"), "W/\"tag-9\""));
            await h.Controller.UpdateAsync();
            Assert.Same(stored, SettingsManager.ControllerDatabase);
            Assert.Equal(Strings.Instance.ControllerDatabase_Refused, h.Settings.ControllerDatabaseResult);
            Assert.True(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.Contains(h.Log, l => l.Contains("refused: TooSmall", StringComparison.Ordinal));

            // Most of the rows this release knows are gone.
            h.Answers(Fetched(CommunityFile(new[] { KeptRow, NewOne }), "W/\"tag-9\""));
            await h.Controller.UpdateAsync();
            Assert.Same(stored, SettingsManager.ControllerDatabase);
            Assert.Equal(Strings.Instance.ControllerDatabase_Refused, h.Settings.ControllerDatabaseResult);
            Assert.Contains(h.Log, l => l.Contains("refused: TooFewKnownRows", StringComparison.Ordinal));

            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Refused, Reason: "an address off raw.githubusercontent.com"));
            await h.Controller.UpdateAsync();
            Assert.Same(stored, SettingsManager.ControllerDatabase);
            Assert.Equal(Strings.Instance.ControllerDatabase_Refused, h.Settings.ControllerDatabaseResult);
            Assert.Contains(h.Log, l => l.Contains("an address off raw.githubusercontent.com", StringComparison.Ordinal));
            Assert.Equal(1, h.Dirty);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
        }

        [Fact]
        public async Task AFailureNobodyForesaw_EndsTheUpdate_AndLeavesTheRowUsable()
        {
            using var h = new Harness();
            h.Answer = (_, _) => throw new InvalidOperationException("an odd failure");
            await h.Controller.UpdateAsync();
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.True(h.Settings.ControllerDatabaseResultIsWarning);
            Assert.Contains("an odd failure", h.Settings.ControllerDatabaseResult, StringComparison.Ordinal);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
            Assert.True(h.Settings.UpdateControllerDatabaseCommand.CanExecute(null));
        }

        // ── A row in use ──

        /// <summary>The poll thread marks a row while the UI thread stores
        /// a download. The mark made on the old section is in the new
        /// one.</summary>
        [Fact]
        public async Task ARowMarkedInUse_KeepsItsMark_ThroughTheNextUpdate()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne, NewTwo)));
            await h.Controller.UpdateAsync();
            Assert.True(SettingsManager.ChangeControllerDatabase(s => s.WithRowInUse(Id(0x2000, 0x0001))));
            Assert.False(SettingsManager.ChangeControllerDatabase(s => s.WithRowInUse(Id(0x2000, 0x0001))));

            h.Answers(Fetched(FileWith(NewTwo), "W/\"tag-2\""));
            await h.Controller.UpdateAsync();
            var section = SettingsManager.ControllerDatabase;
            Assert.Equal(2, section.NewRows);
            Assert.True(section.TryFindNew(Id(0x2000, 0x0001), out var used) && used.InUse);
            Assert.True(section.TryFindNew(Id(0x2000, 0x0002), out var unused) && !unused.InUse);
        }

        // ── The reset ──

        /// <summary>The row's reset empties the section, through the page's
        /// own reset command.</summary>
        [Fact]
        public async Task TheReset_EmptiesTheSection()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne, ChangedKept)));
            await h.Controller.UpdateAsync();
            Assert.NotNull(SettingsManager.ControllerDatabase);
            Assert.NotEqual(string.Empty, h.Settings.ControllerDatabaseResult);
            var raised = new List<string>();
            h.Settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            Assert.True(h.Settings.ResetSettingCommand.CanExecute(nameof(SettingsViewModel.ControllerDatabaseStatus)));
            h.Settings.ResetSettingCommand.Execute(nameof(SettingsViewModel.ControllerDatabaseStatus));

            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Equal(2, h.Dirty);
            Assert.Equal(BuiltIn(316), h.Settings.ControllerDatabaseStatus);
            Assert.Equal(string.Empty, h.Settings.ControllerDatabaseResult);
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseStatus), raised);
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseResult), raised);

            // The next update names no tag: there is no copy to compare with.
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            Assert.Equal(new string[] { null, null }, h.AskedTags);
        }

        /// <summary>The Updates card's Reset All puts the row back with the
        /// card's switches.</summary>
        [Fact]
        public async Task TheCardsResetAll_EmptiesTheSectionToo()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseStatus), SettingsViewModel.CardSettings("Updates"));
            h.Settings.ResetCardCommand.Execute("Updates");
            Assert.Null(SettingsManager.ControllerDatabase);
        }

        [Fact]
        public void AResetWithNothingStored_SavesNothing()
        {
            using var h = new Harness();
            h.Controller.Reset();
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Equal(0, h.Dirty);
        }

        /// <summary>A reset during a download ends the download: what it
        /// brings is not stored after the user emptied the section.</summary>
        [Fact]
        public async Task AResetDuringADownload_EndsIt_AndNothingIsStored()
        {
            using var h = new Harness();
            var gate = new TaskCompletionSource<ControllerDatabaseFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken seen = default;
            h.Answer = (_, ct) =>
            {
                seen = ct;
                return gate.Task;
            };
            var running = h.Controller.UpdateAsync();
            Assert.True(h.Settings.IsControllerDatabaseUpdating);

            h.Controller.Reset();
            Assert.True(seen.IsCancellationRequested);
            gate.SetResult(Fetched(FileWith(NewOne)));
            await running;

            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Equal(0, h.Dirty);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
            Assert.Equal(string.Empty, h.Settings.ControllerDatabaseResult);
        }

        /// <summary>Closing PadForge cancels a running download, and it
        /// writes nothing afterward.</summary>
        [Fact]
        public async Task ClosingPadForge_CancelsTheDownload()
        {
            var h = new Harness();
            var gate = new TaskCompletionSource<ControllerDatabaseFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken seen = default;
            h.Answer = (_, ct) =>
            {
                seen = ct;
                ct.Register(() => gate.TrySetCanceled(ct));
                return gate.Task;
            };
            var running = h.Controller.UpdateAsync();
            h.Dispose();
            Assert.True(seen.IsCancellationRequested);
            await running;
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.Equal(0, h.Dirty);

            // Nothing starts once it is closed, and the row's commands are let go.
            await h.Controller.UpdateAsync();
            h.Settings.UpdateControllerDatabaseCommand.Execute(null);
            h.Settings.ResetSettingCommand.Execute(nameof(SettingsViewModel.ControllerDatabaseStatus));
            Assert.Single(h.AskedTags);
        }

        // ── The words ──

        /// <summary>Both lines are built when they are read, so a language
        /// change reaches them.</summary>
        [Fact]
        public void TheRowsLines_AreRaisedOnALanguageChange()
        {
            string viewModel = OffLimitsSource.Read("PadForge.App/ViewModels/SettingsViewModel.cs");
            string onCulture = FenceSource.Body(viewModel, "protected override void OnCultureChanged()");
            Assert.Contains("OnPropertyChanged(nameof(ControllerDatabaseStatus));", onCulture, StringComparison.Ordinal);
            Assert.Contains("OnPropertyChanged(nameof(ControllerDatabaseResult));", onCulture, StringComparison.Ordinal);
        }

        // ── Download From (#600 item 49) ──

        private const string Mirror = "https://mirror.example.net/sdl/gamecontrollerdb.txt";

        /// <summary>Update Now asks the address the user set, stores where
        /// the rows came from, keeps new rows only from there, and the row's
        /// line names the host.</summary>
        [Fact]
        public async Task UpdateNow_AsksTheChosenAddress_KeepsNewRowsOnly_AndTheLineNamesItsHost()
        {
            using var h = new Harness();
            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            Assert.Equal(Mirror, SettingsManager.ControllerDatabaseFrom);
            Assert.Equal(Mirror, h.Settings.ControllerDatabaseAddress);
            Assert.True(h.Settings.HasOwnControllerDatabaseAddress);
            Assert.Equal(1, h.Dirty);

            h.Answers(Fetched(FileWith(ChangedKept, NewOne), serverTime: new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc)));
            await h.Controller.UpdateAsync();

            Assert.Equal(new[] { Mirror }, h.AskedAddresses);
            var section = SettingsManager.ControllerDatabase;
            Assert.Equal(Mirror, section.Source);
            Assert.Equal(1, section.NewRows);
            Assert.Equal(0, section.ChangedRows);
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.CountFormat(nameof(Strings.ControllerDatabase_BuiltInUpdatedFrom_Format), 1),
                1, 316, "2026-09-24", "2026-11-22", "mirror.example.net"), h.Settings.ControllerDatabaseStatus);
            Assert.Contains("mirror.example.net", h.Settings.ControllerDatabaseStatus, StringComparison.Ordinal);
        }

        /// <summary>The stored tag goes only to the address it came from:
        /// another address is asked for its whole file.</summary>
        [Fact]
        public async Task TheStoredTag_GoesOnlyToTheAddressItCameFrom()
        {
            using var h = new Harness();
            h.Answers(Fetched(FileWith(NewOne)));
            await h.Controller.UpdateAsync();
            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            h.Answers(Fetched(FileWith(NewOne), etag: "\"mirror-tag\""));
            await h.Controller.UpdateAsync();
            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.NotModified));
            await h.Controller.UpdateAsync();
            h.Settings.UseCommunityDatabaseCommand.Execute(null);
            await h.Controller.UpdateAsync();

            Assert.Equal(new[] { ControllerDatabaseAddress.Community, Mirror, Mirror, ControllerDatabaseAddress.Community }, h.AskedAddresses);
            Assert.Equal(new[] { null, null, "\"mirror-tag\"", null }, h.AskedTags);
        }

        /// <summary>Use the Community's File, the row's reset and the card's
        /// Reset All put the community's address back. An address PadForge
        /// does not take changes nothing.</summary>
        [Fact]
        public void TheCommunitysAddress_ComesBack_ByItsButton_AndByTheResets()
        {
            using var h = new Harness();
            h.Settings.ChooseControllerDatabaseAddress("http://mirror.example.net/plain.txt");
            Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);
            Assert.False(h.Settings.HasOwnControllerDatabaseAddress);
            Assert.Equal(0, h.Dirty);

            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            var raised = new List<string>();
            h.Settings.PropertyChanged += (s, e) => raised.Add(e.PropertyName);
            h.Settings.UseCommunityDatabaseCommand.Execute(null);
            Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);
            Assert.Contains(nameof(SettingsViewModel.ControllerDatabaseAddress), raised);
            Assert.Contains(nameof(SettingsViewModel.HasOwnControllerDatabaseAddress), raised);
            Assert.Equal(2, h.Dirty);

            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            h.Settings.ResetSettingCommand.Execute(nameof(SettingsViewModel.ControllerDatabaseStatus));
            Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);
            Assert.Equal(4, h.Dirty);

            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            h.Settings.ResetCardCommand.Execute("Updates");
            Assert.Equal(ControllerDatabaseAddress.Community, SettingsManager.ControllerDatabaseFrom);
        }

        /// <summary>A download running for the old address ends when the
        /// address changes, and nothing it brings is stored.</summary>
        [Fact]
        public async Task ChangingTheAddress_EndsARunningDownload()
        {
            using var h = new Harness();
            var gate = new TaskCompletionSource<ControllerDatabaseFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken seen = default;
            h.Answer = (_, ct) =>
            {
                seen = ct;
                return gate.Task;
            };
            var running = h.Controller.UpdateAsync();
            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            Assert.True(seen.IsCancellationRequested);
            gate.SetResult(Fetched(FileWith(NewOne)));
            await running;
            Assert.Null(SettingsManager.ControllerDatabase);
            Assert.False(h.Settings.IsControllerDatabaseUpdating);
        }

        /// <summary>No answer from the user's address names that host, not
        /// GitHub.</summary>
        [Fact]
        public async Task NoAnswerFromTheChosenAddress_NamesItsHost()
        {
            using var h = new Harness();
            h.Settings.ChooseControllerDatabaseAddress(Mirror);
            h.Answers(new ControllerDatabaseFetch(ControllerDatabaseFetchOutcome.Unreachable, Reason: "No such host is known."));
            await h.Controller.UpdateAsync();
            Assert.Equal(string.Format(Strings.Instance.ControllerDatabase_NoNetworkAt_Format, "mirror.example.net", "No such host is known."),
                h.Settings.ControllerDatabaseResult);
        }
    }

    /// <summary>The status line's numbers and words, in English.</summary>
    [Collection("CultureSwitching")]
    public class ControllerDatabaseStatusTests
    {
        private static readonly string KeptRow = Row(0x1000, 0x0001, "Kept Pad");
        private static readonly string SdlRow = Row(0x1000, 0x0002, "SDL Pad");

        private static T InEnglish<T>(Func<T> read)
        {
            var before = CultureInfo.CurrentUICulture;
            try
            {
                Strings.ChangeCulture(CultureInfo.GetCultureInfo("en"));
                return read();
            }
            finally
            {
                Strings.ChangeCulture(before);
            }
        }

        [Fact]
        public void TheStatusLine_ReadsAsTheIssueHasIt()
        {
            // A row SDL maps elsewhere only adds no built-in mapping, and its
            // pinned text counts as new once downloaded.
            string elsewhere = Row(0x1000, 0x0003, "Mac Pad");
            var baseline = Baseline(('K', KeptRow), ('S', SdlRow), ('O', elsewhere));
            var own = new[] { "030000004c050000b20b000000000000,Sony PS VR Aim Controller,a:b1,platform:Windows," };
            Assert.Equal("Built in: 315 mappings, community copy of 2026-09-24.",
                InEnglish(() => ControllerDatabaseStatus.Facts(baseline, own, null, TimeZoneInfo.Utc)));

            var download = ControllerDatabaseDownload.Read(CommunityFile(new[]
            {
                KeptRow.Replace("a:b0", "a:b7"), SdlRow, elsewhere, Row(0x2000, 0x0001), Row(0x2000, 0x0002), Row(0x2000, 0x0003),
            }), baseline);
            var section = ControllerDatabaseSection.FromDownload(download, null, new DateTime(2026, 11, 23, 14, 17, 35, DateTimeKind.Utc),
                baseline, "6.0.1", null);
            Assert.Equal("Built in: 315 mappings, community copy of 2026-09-24. Downloaded 2026-11-23: 4 new rows, 1 changed.",
                InEnglish(() => ControllerDatabaseStatus.Facts(baseline, own, section, TimeZoneInfo.Utc)));
            Assert.Equal(string.Empty, ControllerDatabaseStatus.Facts(null, own, section, TimeZoneInfo.Utc));
        }

        /// <summary>A row of PadForge's own for a controller the fork or
        /// SDL already maps takes that mapping's place and adds none.</summary>
        [Fact]
        public void ARowOfPadForgesOwnForACarriedId_AddsNoMapping()
        {
            var baseline = Baseline(('K', KeptRow), ('S', SdlRow), ('O', Row(0x1000, 0x0003)), ('N', Row(0x1000, 0x0004)));
            Assert.Equal(314, ControllerDatabaseStatus.BuiltInCount(baseline, Array.Empty<string>()));
            Assert.Equal(314, ControllerDatabaseStatus.BuiltInCount(baseline, new[] { KeptRow, SdlRow }));
            // One for a controller SDL maps elsewhere only, one the fork
            // left out, one the pinned copy does not have, and one whose ID
            // names a name hash: each is a mapping of its own.
            Assert.Equal(318, ControllerDatabaseStatus.BuiltInCount(baseline, new[]
            {
                Row(0x1000, 0x0003), Row(0x1000, 0x0004), Row(0x2000, 0x0001),
                "0300d823001000000100000000000000,Kept Pad Under Another Name,a:b0,platform:Windows,",
            }));
        }

        [Fact]
        public void TheRowsWords_AreThere_AndTheCardNamesEveryHostItContacts()
        {
            var (label, updateNow, updating, description, card) = InEnglish(() => (Strings.Instance.Settings_ControllerDatabase,
                Strings.Instance.Settings_ControllerDatabaseUpdateNow, Strings.Instance.Settings_ControllerDatabaseUpdating,
                Strings.Instance.Settings_ControllerDatabaseDesc, Strings.Instance.Settings_UpdatesDesc));
            Assert.Equal("Controller Database", label);
            Assert.Equal("Update Now", updateNow);
            // The one-character ellipsis, as every waiting word has it.
            Assert.Equal("Updating" + (char)0x2026, updating);
            Assert.Contains("GitHub", description, StringComparison.Ordinal);
            Assert.Contains("settings file", description, StringComparison.Ordinal);
            // The card's sentence on what PadForge contacts stays true: the
            // updater's hosts and the database's one.
            foreach (string host in new[] { "api.github.com", "github.com", ControllerDatabaseUpdater.Host })
                Assert.Contains(host, card, StringComparison.Ordinal);
            Assert.Equal("raw.githubusercontent.com", ControllerDatabaseUpdater.Host);
            Assert.Equal(ControllerDatabaseUpdater.Host, new Uri(ControllerDatabaseAddress.Community).Host);
            // And on what it sends: its version, and the stored tag.
            Assert.Contains("version number", card, StringComparison.Ordinal);
            Assert.Contains("the tag the server gave the last download", card, StringComparison.Ordinal);
            // An address the user sets is the one other place it contacts,
            // and the card says so (#600).
            Assert.Contains("Download From", card, StringComparison.Ordinal);
        }
    }
}
