using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Runs the Controller Database row of the Updates card (#600): Update
    /// Now, the row's reset, the status line and the line under it, and
    /// Download From under its More (#600). UI
    /// thread only, as <see cref="UpdateController"/> is. The request lives
    /// in <see cref="ControllerDatabaseUpdater"/>, the checks in
    /// <see cref="ControllerDatabaseDownload"/>, and what is kept in
    /// <see cref="ControllerDatabaseSection"/>.
    ///
    /// <para>The download starts only when the user presses Update Now. One
    /// runs at a time, the button says so while it does, and closing
    /// PadForge or pressing the reset cancels it. What a finished download
    /// writes lands only while it is still the running one.</para>
    ///
    /// <para>Nothing is written to disk here. A stored download replaces
    /// the section in memory and marks the settings changed, and the
    /// ordinary save puts it in PadForge.xml.</para>
    /// </summary>
    internal sealed partial class ControllerDatabaseController : IDisposable
    {
        private readonly MainViewModel _vm;
        private readonly Action _markDirty;
        private readonly Action _refreshDevices;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        /// <summary>The download running now.</summary>
        private CancellationTokenSource _op;
        private EventHandler _onUpdate, _onReset;
        private Action<string> _onAddress;

        // The network, the baseline, the clock and the build's name,
        // replaceable in tests.
        internal Func<string, string, CancellationToken, Task<ControllerDatabaseFetch>> Fetch = ControllerDatabaseUpdater.FetchAsync;
        internal Func<ControllerDatabaseBaseline> Baseline = () => ControllerDatabaseBaseline.Embedded;
        internal Func<DateTime> UtcNow = () => DateTime.UtcNow;
        internal string AppVersion = BuildIdentity.Display;
        internal IReadOnlyList<string> OwnRows = ControllerDatabaseUse.OwnRows;
        internal TimeZoneInfo Zone = TimeZoneInfo.Local;
        internal Action<string> Log = line => PadForge.Engine.SdlDiagLog.WriteLine(line);

        public ControllerDatabaseController(MainViewModel vm, Action markDirty, Action refreshDevices = null)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
            _refreshDevices = refreshDevices ?? (() => { });
        }

        private SettingsViewModel Settings => _vm.Settings;

        /// <summary>Takes the row's two commands and gives it its status
        /// line, which is built each time it is read.</summary>
        public void Start()
        {
            if (_onUpdate != null || _disposed) return;
            _onUpdate = (s, e) => _ = UpdateAsync();
            _onReset = (s, e) => Reset();
            _onAddress = ChooseAddress;
            Settings.UpdateControllerDatabaseRequested += _onUpdate;
            Settings.ControllerDatabaseResetRequested += _onReset;
            Settings.ControllerDatabaseAddressChosen += _onAddress;
            Settings.SetControllerDatabaseStatus(StatusText);
            Replaced += OnSettingsReplaced;
            StartDevices();
        }

        private static event Action<bool> Replaced;

        /// <summary>
        /// SettingsService took a new controller database section, Download
        /// From and rows of the user's: Reload, or Reset to Defaults
        /// (<paramref name="toDefaults"/>). A
        /// running download ends, since what it brought would be stored over
        /// the settings that replaced the ones it was asked for, and the
        /// lines on the user's rows are read again. Interface thread.
        /// </summary>
        internal static void SettingsReplaced(bool toDefaults) => Replaced?.Invoke(toDefaults);

        private void OnSettingsReplaced(bool toDefaults)
        {
            if (_disposed) return;
            _op?.Cancel();
            Settings.SetControllerDatabaseResult(null);
            Settings.RaiseControllerDatabaseMine();
            // Reset to Defaults empties the section, as the row's reset does,
            // and SDL is given back what the downloaded rows replaced.
            if (toDefaults) _ = GiveBackDownloadsAsync();
            else _vm.Devices.SdlMapping.Refresh();
        }

        private string StatusText()
        {
            try
            {
                return ControllerDatabaseStatus.Facts(Baseline(), OwnRows, SettingsManager.ControllerDatabase, Zone);
            }
            catch (Exception)
            {
                // A build with no readable baseline says nothing here, and
                // ControllerDatabaseBaselineTests fails for it.
                return string.Empty;
            }
        }

        private bool IsCurrent(CancellationTokenSource op) =>
            !_disposed && ReferenceEquals(_op, op) && !op.IsCancellationRequested;

        /// <summary>Update Now: fetches the current copy from the address
        /// Download From names, checks it, and stores what is new or changed
        /// against this release. The stored tag goes only to the address it
        /// came from.</summary>
        public async Task UpdateAsync()
        {
            if (_disposed || _op != null) return;
            var op = _op = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            Settings.IsControllerDatabaseUpdating = true;
            Settings.SetControllerDatabaseResult(null);
            string address = SettingsManager.ControllerDatabaseFrom;
            try
            {
                var baseline = Baseline();
                var held = SettingsManager.ControllerDatabase;
                string etag = held != null && string.Equals(held.Source, address, StringComparison.Ordinal) ? held.ETag : null;
                var fetch = await Fetch(address, etag, op.Token);
                if (!IsCurrent(op)) return;
                switch (fetch.Outcome)
                {
                    case ControllerDatabaseFetchOutcome.NotModified:
                        Settings.SetControllerDatabaseResult(() => NothingChanged(address));
                        break;
                    case ControllerDatabaseFetchOutcome.Unreachable:
                        Log("MAPPINGS database update: no answer (" + (fetch.Stalled ? "stalled" : fetch.Reason) + ")");
                        string reason = fetch.Reason;
                        bool stalled = fetch.Stalled;
                        Settings.SetControllerDatabaseResult(() => NoAnswer(address, stalled ? Strings.Instance.Update_DownloadStalled : reason),
                            warning: true);
                        break;
                    case ControllerDatabaseFetchOutcome.Refused:
                        RefuseAnswer(address, fetch.Reason);
                        break;
                    default:
                        byte[] content = fetch.Content;
                        var token = op.Token;
                        // Off the UI thread: the file is decoded, hashed and read row by row.
                        var download = await Task.Run(() => ControllerDatabaseDownload.Read(content, baseline), token);
                        if (!IsCurrent(op)) return;
                        if (download.Refusal != ControllerDatabaseRefusal.None)
                        {
                            Refuse(download.Refusal + ", " + content.Length + " bytes, " + download.KnownRows + " of "
                                   + baseline.PinnedIds.Count + " known rows, SHA-256 " + download.Sha256);
                            break;
                        }
                        // GitHub's own time for the community's file, which the
                        // pin test of a later release reads. The PC's clock for
                        // an address the user set: its Date header is the
                        // host's word, not the day of the download.
                        DateTime fetched = ControllerDatabaseAddress.IsCommunity(address) ? fetch.ServerTimeUtc ?? UtcNow() : UtcNow();
                        Store(download, fetch.ETag, fetched, baseline, address);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (IsCurrent(op))
                {
                    Log("MAPPINGS database update failed: " + ex.Message);
                    string reason = ex.Message;
                    Settings.SetControllerDatabaseResult(() => NoAnswer(address, reason), warning: true);
                }
            }
            finally
            {
                if (ReferenceEquals(_op, op)) _op = null;
                op.Dispose();
                if (!_disposed && _op == null) Settings.IsControllerDatabaseUpdating = false;
            }
        }

        /// <summary>The line for a download that got no answer: GitHub by
        /// name for the community's file, the host for an address the user
        /// set.</summary>
        private static string NoAnswer(string address, string reason) => ControllerDatabaseAddress.IsCommunity(address)
            ? string.Format(Strings.Instance.ControllerDatabase_NoNetwork_Format, reason)
            : string.Format(Strings.Instance.ControllerDatabase_NoNetworkAt_Format, ControllerDatabaseAddress.HostOf(address), reason);

        /// <summary>The line for a 304: the community's file by name, the
        /// host for an address the user set.</summary>
        private static string NothingChanged(string address) => ControllerDatabaseAddress.IsCommunity(address)
            ? Strings.Instance.ControllerDatabase_NothingChanged
            : string.Format(Strings.Instance.ControllerDatabase_NothingChangedAt_Format, ControllerDatabaseAddress.HostOf(address));

        /// <summary>A file that came and failed the database's checks
        /// (<see cref="ControllerDatabaseDownload.Read"/>).</summary>
        private void Refuse(string what)
        {
            Log("MAPPINGS database update refused: " + what);
            Settings.SetControllerDatabaseResult(() => Strings.Instance.ControllerDatabase_Refused, warning: true);
        }

        /// <summary>An answer the request itself refused before any file was
        /// read as the database (<see cref="ControllerDatabaseUpdater.FetchAsync"/>:
        /// a redirect off the host, with no address or past the limit, a size,
        /// encoding or body PadForge does not take): GitHub by name for the
        /// community's file, the host for an address the user set.</summary>
        private void RefuseAnswer(string address, string what)
        {
            Log("MAPPINGS database update refused: " + what);
            Settings.SetControllerDatabaseResult(() => ControllerDatabaseAddress.IsCommunity(address)
                ? Strings.Instance.ControllerDatabase_AnswerRefused
                : string.Format(Strings.Instance.ControllerDatabase_AnswerRefusedAt_Format, ControllerDatabaseAddress.HostOf(address)),
                warning: true);
        }

        private void Store(ControllerDatabaseDownload download, string etag, DateTime fetchedUtc, ControllerDatabaseBaseline baseline,
            string address)
        {
            ControllerDatabaseSection stored = null;
            SettingsManager.ChangeControllerDatabase(current =>
                stored = ControllerDatabaseSection.FromDownload(download, etag, fetchedUtc, baseline, AppVersion, current, address));
            _markDirty();
            Log("MAPPINGS database update stored: " + stored.NewRows + " new, " + stored.ChangedRows + " changed, of "
                + download.Rows.Count + " accepted rows, SHA-256 " + download.Sha256);
            Settings.RaiseControllerDatabaseStatus();
            if (stored.NewRows > 0 || stored.ChangedRows > 0)
            {
                Settings.SetControllerDatabaseResult(() => Strings.Instance.ControllerDatabase_InUseAtOnce);
                // In use at once (#600): the changed rows replace the
                // community copy's in SDL now, and the new rows are offered
                // to the joysticks SDL reads as joysticks now.
                _ = ApplyDownloadAsync(stored, baseline);
            }
        }

        /// <summary>The row's reset: empties the section, gives SDL back what
        /// the downloaded rows replaced, puts the community's address back
        /// under Download From, and ends a download that would fill it
        /// again.</summary>
        public void Reset()
        {
            if (_disposed) return;
            _op?.Cancel();
            bool emptied = SettingsManager.ChangeControllerDatabase(_ => null);
            bool moved = !ControllerDatabaseAddress.IsCommunity(SettingsManager.ControllerDatabaseFrom);
            SettingsManager.ControllerDatabaseFrom = null;
            if (emptied || moved) _markDirty();
            // SDL keeps a given mapping until it quits: it is given back what
            // each downloaded row replaced.
            _ = GiveBackDownloadsAsync();
            Settings.SetControllerDatabaseResult(null);
            Settings.RaiseControllerDatabaseStatus();
            Settings.RaiseControllerDatabaseAddress();
        }

        /// <summary>
        /// Download From (#600): the address the next Update Now asks, set by
        /// Change… or put back by Use the Community's File (null). Only
        /// the user reaches either, since the line is theirs alone. An
        /// address PadForge does not take changes nothing. A download running
        /// for the old address ends, since what it brought would be stored as
        /// the new address's. The rows already stored stay until the next
        /// download, and their tag goes only to the address they came from.
        /// </summary>
        public void ChooseAddress(string address)
        {
            if (_disposed) return;
            string chosen = ControllerDatabaseAddress.Community;
            if (address != null && !ControllerDatabaseAddress.TryRead(address, out chosen)) return;
            if (string.Equals(chosen, SettingsManager.ControllerDatabaseFrom, StringComparison.Ordinal)) return;
            _op?.Cancel();
            SettingsManager.ControllerDatabaseFrom = chosen;
            _markDirty();
            Settings.SetControllerDatabaseResult(null);
            Settings.RaiseControllerDatabaseAddress();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_onUpdate != null) Settings.UpdateControllerDatabaseRequested -= _onUpdate;
            if (_onReset != null) Settings.ControllerDatabaseResetRequested -= _onReset;
            if (_onAddress != null) Settings.ControllerDatabaseAddressChosen -= _onAddress;
            Replaced -= OnSettingsReplaced;
            StopDevices();
            // A running download disposes its own linked source when it ends.
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
