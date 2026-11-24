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
    /// Runs the Controller Database row of the Updates card (#597): Update
    /// Now, the row's reset, the status line and the line under it. UI
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
    internal sealed class ControllerDatabaseController : IDisposable
    {
        private readonly MainViewModel _vm;
        private readonly Action _markDirty;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        /// <summary>The download running now.</summary>
        private CancellationTokenSource _op;
        private EventHandler _onUpdate, _onReset;

        // The network, the baseline, the clock and the build's name,
        // replaceable in tests.
        internal Func<string, CancellationToken, Task<ControllerDatabaseFetch>> Fetch = ControllerDatabaseUpdater.FetchAsync;
        internal Func<ControllerDatabaseBaseline> Baseline = () => ControllerDatabaseBaseline.Embedded;
        internal Func<DateTime> UtcNow = () => DateTime.UtcNow;
        internal string AppVersion = BuildIdentity.Display;
        internal IReadOnlyList<string> OwnRows = ControllerDatabaseUse.OwnRows;
        internal TimeZoneInfo Zone = TimeZoneInfo.Local;
        internal Action<string> Log = line => PadForge.Engine.SdlDiagLog.WriteLine(line);

        public ControllerDatabaseController(MainViewModel vm, Action markDirty)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
        }

        private SettingsViewModel Settings => _vm.Settings;

        /// <summary>Takes the row's two commands and gives it its status
        /// line, which is built each time it is read.</summary>
        public void Start()
        {
            if (_onUpdate != null || _disposed) return;
            _onUpdate = (s, e) => _ = UpdateAsync();
            _onReset = (s, e) => Reset();
            Settings.UpdateControllerDatabaseRequested += _onUpdate;
            Settings.ControllerDatabaseResetRequested += _onReset;
            Settings.SetControllerDatabaseStatus(StatusText);
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

        /// <summary>Update Now: fetches the community's current copy, checks
        /// it, and stores what is new or changed against this release.</summary>
        public async Task UpdateAsync()
        {
            if (_disposed || _op != null) return;
            var op = _op = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            Settings.IsControllerDatabaseUpdating = true;
            Settings.SetControllerDatabaseResult(null);
            try
            {
                var baseline = Baseline();
                var fetch = await Fetch(SettingsManager.ControllerDatabase?.ETag, op.Token);
                if (!IsCurrent(op)) return;
                switch (fetch.Outcome)
                {
                    case ControllerDatabaseFetchOutcome.NotModified:
                        Settings.SetControllerDatabaseResult(() => Strings.Instance.ControllerDatabase_NothingChanged);
                        break;
                    case ControllerDatabaseFetchOutcome.Unreachable:
                        Log("MAPPINGS database update: no answer (" + (fetch.Stalled ? "stalled" : fetch.Reason) + ")");
                        string reason = fetch.Reason;
                        bool stalled = fetch.Stalled;
                        Settings.SetControllerDatabaseResult(() => string.Format(Strings.Instance.ControllerDatabase_NoNetwork_Format,
                            stalled ? Strings.Instance.Update_DownloadStalled : reason), warning: true);
                        break;
                    case ControllerDatabaseFetchOutcome.Refused:
                        Refuse(fetch.Reason);
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
                        Store(download, fetch.ETag, fetch.ServerTimeUtc ?? UtcNow(), baseline);
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
                    Settings.SetControllerDatabaseResult(() => string.Format(Strings.Instance.ControllerDatabase_NoNetwork_Format, reason),
                        warning: true);
                }
            }
            finally
            {
                if (ReferenceEquals(_op, op)) _op = null;
                op.Dispose();
                if (!_disposed && _op == null) Settings.IsControllerDatabaseUpdating = false;
            }
        }

        private void Refuse(string what)
        {
            Log("MAPPINGS database update refused: " + what);
            Settings.SetControllerDatabaseResult(() => Strings.Instance.ControllerDatabase_Refused, warning: true);
        }

        private void Store(ControllerDatabaseDownload download, string etag, DateTime fetchedUtc, ControllerDatabaseBaseline baseline)
        {
            ControllerDatabaseSection stored = null;
            SettingsManager.ChangeControllerDatabase(current =>
                stored = ControllerDatabaseSection.FromDownload(download, etag, fetchedUtc, baseline, AppVersion, current));
            _markDirty();
            Log("MAPPINGS database update stored: " + stored.NewRows + " new, " + stored.ChangedRows + " changed, of "
                + download.Rows.Count + " accepted rows, SHA-256 " + download.Sha256);
            Settings.RaiseControllerDatabaseStatus();
            bool anyNew = stored.NewRows > 0, anyChanged = stored.ChangedRows > 0;
            if (anyNew || anyChanged)
                Settings.SetControllerDatabaseResult(() => WhenItApplies(anyNew, anyChanged));
        }

        /// <summary>When the stored rows take effect, a sentence a line for
        /// each kind stored.</summary>
        private static string WhenItApplies(bool anyNew, bool anyChanged)
        {
            var lines = new List<string>(2);
            if (anyNew) lines.Add(Strings.Instance.ControllerDatabase_NewApplies);
            if (anyChanged) lines.Add(Strings.Instance.ControllerDatabase_ChangedApplies);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>The row's reset: empties the section, and ends a
        /// download that would fill it again.</summary>
        public void Reset()
        {
            if (_disposed) return;
            _op?.Cancel();
            if (SettingsManager.ChangeControllerDatabase(_ => null)) _markDirty();
            Settings.SetControllerDatabaseResult(null);
            Settings.RaiseControllerDatabaseStatus();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_onUpdate != null) Settings.UpdateControllerDatabaseRequested -= _onUpdate;
            if (_onReset != null) Settings.ControllerDatabaseResetRequested -= _onReset;
            // A running download disposes its own linked source when it ends.
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
