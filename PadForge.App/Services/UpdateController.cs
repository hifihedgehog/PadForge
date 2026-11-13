using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Runs the Updates card in Settings (#457): the automatic checks, Check
    /// Now, the background download for Install Updates Automatically, and
    /// Install and Restart. UI thread only. The network and file work lives
    /// in <see cref="UpdateService"/>.
    ///
    /// <para>The automatic check runs 20 seconds after launch, so it never
    /// competes with startup, and every 12 hours after that. The check
    /// interval and the startup delay follow HandheldCompanion's
    /// UpdateManager (a check at start plus a timer) at a slower cadence,
    /// since GitHub allows 60 unauthenticated API calls an hour per
    /// address.</para>
    ///
    /// <para>One operation runs at a time: a check, which also stages what
    /// it finds while Install Updates Automatically is on, a background
    /// staging, or an install. It owns the busy state from start to end, and
    /// what it writes to the card after an await lands only while it is
    /// still the running operation and its offer is still the offer.
    /// Changing channel cancels it. An install commits its helper on this
    /// thread, so a channel change lands either before the commit, which it
    /// then stops, or after it, when PadForge is already on its way out.</para>
    /// </summary>
    internal sealed class UpdateController : IDisposable
    {
        private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

        private readonly MainViewModel _vm;
        private readonly Action _exitForUpdate;
        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private readonly CancellationTokenSource _cts = new();
        private DispatcherTimer _timer;
        private bool _started;
        private bool _disposed;
        /// <summary>The check, staging or install running now.</summary>
        private Operation _op;
        /// <summary>An automatic check asked for while busy, run once idle.</summary>
        private bool _checkWhenIdle;
        /// <summary>Install Updates Automatically turned on while busy: stage
        /// the offer once idle.</summary>
        private bool _stageWhenIdle;
        /// <summary>A helper has the swap, and this process is on its way
        /// out. Nothing more starts, whether or not closing works.</summary>
        private bool _committed;
        /// <summary>The first tick asked for the staging cleanup, which waits
        /// until no operation runs, and <see cref="_cleanedUp"/> once it has.</summary>
        private bool _cleanupDue, _cleanedUp;
        private Task _cleanupTask = Task.CompletedTask;
        private UpdateOffer _offer;
        /// <summary>The downloaded exe for <see cref="_offer"/>, verified
        /// this session. Install and Restart reuses it.</summary>
        private StagedUpdate _staged;
        /// <summary>The status-bar line that announced <see cref="_offer"/>,
        /// exactly as posted.</summary>
        private string _announcement;
        private EventHandler _onCheckNow, _onInstall, _onReleaseNotes;

        // The network, the pending record, the helper and the cleanup,
        // replaceable in tests.
        internal Func<bool, CancellationToken, Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>> Check =
            UpdateService.CheckAsync;
        internal Func<UpdateOffer, IProgress<int>, CancellationToken, Task<StagedUpdate>> Download =
            UpdateService.DownloadAsync;
        internal Func<PendingUpdate> ReadPending = UpdateService.ReadPending;
        internal Action<UpdateOffer, StagedUpdate, bool> WritePending = UpdateService.WritePending;
        internal Action DeletePending = UpdateService.DeletePending;
        internal Func<StagedUpdate, string, IReadOnlyList<string>, CancellationToken, UpdateService.IHelperHandle> StartHelper =
            UpdateService.StartHelper;
        internal Func<Task> Cleanup = UpdateService.CleanupStagingAsync;
        internal Func<IReadOnlyList<string>> LaunchArgs = () => App.StartupArgs;

        private sealed class Operation
        {
            public readonly CancellationTokenSource Cts;
            public Operation(CancellationToken parent) => Cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        }

        public UpdateController(MainViewModel vm, Action exitForUpdate)
        {
            _vm = vm ?? throw new ArgumentNullException(nameof(vm));
            _exitForUpdate = exitForUpdate ?? throw new ArgumentNullException(nameof(exitForUpdate));
        }

        private SettingsViewModel Settings => _vm.Settings;

        public void Start()
        {
            if (_started || _disposed) return;
            _started = true;
            Subscribe();

            // A helper this launch could not stop outranks the news that the
            // last update worked.
            if (UpdateService.LastPendingError is UpdateHelperException { Failure: HelperFailure.NotStopped } stuck)
                _vm.SetStatus(FailureText(stuck)(), persist: true);
            else if (UpdateService.HasUpdatedMarker(LaunchArgs()))
                _vm.SetStatus(string.Format(Strings.Instance.Update_Updated_Format, BuildIdentity.Display));
            else if (UpdateService.LastPendingFailure != null)
                _vm.SetStatus(string.Format(Strings.Instance.Update_PendingFailed_Format,
                    UpdateService.LastPendingFailure), persist: true);

            _timer = new DispatcherTimer { Interval = FirstCheckDelay };
            _timer.Tick += (s, e) => OnTimerTick();
            _timer.Start();
        }

        /// <summary>The card's three commands and its settings. Start calls
        /// it, and tests call it alone, without the timer.</summary>
        internal void Subscribe()
        {
            if (_onCheckNow != null || _disposed) return;
            _onCheckNow = (s, e) => _ = CheckAsync(userInitiated: true);
            _onInstall = (s, e) => _ = InstallAsync();
            _onReleaseNotes = (s, e) => OpenReleaseNotes();
            Settings.CheckForUpdatesNowRequested += _onCheckNow;
            Settings.InstallUpdateRequested += _onInstall;
            Settings.OpenReleaseNotesRequested += _onReleaseNotes;
            Settings.PropertyChanged += OnSettingsChanged;
        }

        /// <summary>
        /// The first tick comes 20 seconds after launch, and a PadForge that
        /// ran that long from this exe shows the exe works. So it also asks
        /// for the cleanup of earlier downloads and of the copy the last
        /// update kept of the previous version.
        /// </summary>
        internal void OnTimerTick()
        {
            if (_disposed) return;
            if (_timer != null) _timer.Interval = CheckInterval;
            if (!_cleanedUp) _cleanupDue = true;
            CleanUpWhenIdle();
            if (Settings.CheckForUpdatesAutomatically)
                _ = CheckAsync(userInitiated: false);
        }

        /// <summary>The cleanup deletes download folders it does not
        /// recognize, so it never starts under a running operation, and every
        /// download, or reuse of one, that starts after it waits for it
        /// (DownloadCore).</summary>
        private void CleanUpWhenIdle()
        {
            if (!_cleanupDue || _op != null || _disposed || _committed) return;
            _cleanupDue = false;
            _cleanedUp = true;
            _cleanupTask = Cleanup();
        }

        private void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            // A committed helper installs what it was given once this process
            // exits. The switches still save, and the card keeps saying what
            // is happening.
            if (_committed) return;
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.IncludePreReleaseUpdates):
                    // An offer, a download or a staged build from the other
                    // channel no longer answers the question the card asks.
                    _op?.Cts.Cancel();
                    ClearOffer();
                    TryDeletePending(out _);
                    Settings.SetUpdateStatus(null);
                    RequestAutomaticCheck();
                    break;
                case nameof(SettingsViewModel.InstallUpdatesAutomatically):
                    if (!Settings.InstallUpdatesAutomatically)
                        RevokeStaging();
                    else if (_offer != null && Settings.CheckForUpdatesAutomatically)
                        _ = StageAsync(_offer);
                    break;
                case nameof(SettingsViewModel.CheckForUpdatesAutomatically):
                    if (!Settings.CheckForUpdatesAutomatically)
                    {
                        _checkWhenIdle = false;
                        RevokeStaging();
                    }
                    else
                    {
                        RequestAutomaticCheck();
                    }
                    break;
            }
        }

        /// <summary>Nothing installs at the next launch any more. A card that
        /// said the offer would goes back to saying it is available. A running
        /// operation writes its own line, and asks both switches again when
        /// its download ends.</summary>
        private void RevokeStaging()
        {
            _stageWhenIdle = false;
            TryDeletePending(out _);
            var offer = _offer;
            if (_op == null && offer != null && !_disposed)
                Settings.SetUpdateStatus(() => AvailableText(offer));
        }

        private void RequestAutomaticCheck()
        {
            if (_disposed || _committed || !Settings.CheckForUpdatesAutomatically) return;
            if (_op != null) _checkWhenIdle = true;
            else _ = CheckAsync(userInitiated: false);
        }

        private Operation Begin()
        {
            if (_disposed || _committed || _op != null) return null;
            _op = new Operation(_cts.Token);
            SetBusy(true);
            return _op;
        }

        private void End(Operation op)
        {
            if (!ReferenceEquals(_op, op)) return;
            _op = null;
            op.Cts.Dispose();
            SetBusy(_committed);
            CleanUpWhenIdle();
            if ((_checkWhenIdle || _stageWhenIdle) && !_disposed && !_committed)
                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RunQueuedWork));
        }

        /// <summary>A check, or a staging, asked for while another operation
        /// ran. Each runs only while the settings that asked for it are still
        /// on. A check stages what it finds itself, so it goes first.</summary>
        private void RunQueuedWork()
        {
            if (_disposed || _committed || _op != null) return;
            if (_checkWhenIdle)
            {
                _checkWhenIdle = false;
                if (Settings.CheckForUpdatesAutomatically)
                {
                    // A queued staging stays queued: this check stages what it
                    // finds, and when it ends without that, End runs this again.
                    _ = CheckAsync(userInitiated: false);
                    return;
                }
            }
            if (_stageWhenIdle)
            {
                _stageWhenIdle = false;
                if (_offer != null && AutoInstallOn())
                    _ = StageAsync(_offer);
            }
        }

        private bool IsCurrent(Operation op, UpdateOffer offer) =>
            !_disposed && ReferenceEquals(_op, op) && !op.Cts.IsCancellationRequested
            && (offer == null || ReferenceEquals(_offer, offer));

        /// <summary>Writes the card's status line for <paramref name="op"/>,
        /// unless another operation or another offer has taken over.
        /// <paramref name="upToDate"/> says the line reports this build the
        /// newest, which the About page shows in its own words.</summary>
        private void Status(Operation op, UpdateOffer offer, Func<string> text, bool upToDate = false)
        {
            if (IsCurrent(op, offer)) Settings.SetUpdateStatus(text, upToDate);
        }

        /// <summary>
        /// The card's line for a failed download or install, worded when it
        /// is shown. A reason PadForge words itself follows a language change.
        /// A reason from Windows or the network is kept as it came.
        /// </summary>
        internal static Func<string> FailureText(Exception ex)
        {
            switch (ex)
            {
                case UpdateDownloadStalledException:
                    return () => string.Format(Strings.Instance.Update_InstallFailed_Format,
                        Strings.Instance.Update_DownloadStalled);
                case UpdateHelperException helper:
                    return () => string.Format(Strings.Instance.Update_InstallFailed_Format, helper.Reason);
                default:
                    string reason = ex.Message;
                    return () => string.Format(Strings.Instance.Update_InstallFailed_Format, reason);
            }
        }

        private void ClearOffer()
        {
            _offer = null;
            _staged = null;
            Settings.IsUpdateAvailable = false;
            _stageWhenIdle = false;
            WithdrawAnnouncement();
        }

        /// <summary>Takes back the status-bar line that announced the offer,
        /// while it still shows. Another message there is left alone.</summary>
        private void WithdrawAnnouncement()
        {
            if (_announcement != null && _vm.StatusText == _announcement)
                _vm.StatusText = string.Empty;
            _announcement = null;
        }

        /// <summary>Deletes this copy's pending record. The launch honors the
        /// saved preferences whether or not this works, so a failure matters
        /// only where the record would otherwise install something unwanted,
        /// and the caller reports it there.</summary>
        private bool TryDeletePending(out string error)
        {
            try
            {
                DeletePending();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public async Task CheckAsync(bool userInitiated)
        {
            // Every automatic check asks the switch, whichever path started it.
            if (!userInitiated && (_disposed || !Settings.CheckForUpdatesAutomatically)) return;
            var op = Begin();
            if (op == null)
            {
                // Queued only behind a running operation. After a commit, or
                // once disposed, nothing runs again.
                if (!userInitiated && _op != null) _checkWhenIdle = true;
                return;
            }
            _checkWhenIdle = false;
            try
            {
                if (userInitiated)
                    Status(op, null, () => Strings.Instance.Update_Checking);
                bool preReleases = Settings.IncludePreReleaseUpdates;
                var (outcome, offer, error) = await Check(preReleases, op.Cts.Token);
                if (!IsCurrent(op, null) || preReleases != Settings.IncludePreReleaseUpdates)
                    return;
                switch (outcome)
                {
                    case UpdateCheckOutcome.Available:
                        await OnAvailableAsync(op, offer, userInitiated);
                        break;
                    case UpdateCheckOutcome.UpToDate:
                        // Anything staged is for a build that is no longer the
                        // newest, a pulled release among them.
                        ClearOffer();
                        TryDeletePending(out _);
                        Status(op, null, () => string.Format(Strings.Instance.Update_UpToDate_Format, BuildIdentity.Display), upToDate: true);
                        break;
                    case UpdateCheckOutcome.NoBuildForThisPc:
                        // The card says the newest build has none for this PC,
                        // so an older one staged earlier does not install
                        // behind it.
                        ClearOffer();
                        TryDeletePending(out _);
                        Status(op, null, () => Strings.Instance.Update_NoBuildForThisPc);
                        break;
                    case UpdateCheckOutcome.RateLimited:
                        Status(op, null, () => Strings.Instance.Update_RateLimited);
                        break;
                    default:
                        string reason = error ?? string.Empty;
                        Status(op, null, () => string.Format(Strings.Instance.Update_CheckFailed_Format, reason));
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                End(op);
            }
        }

        private async Task OnAvailableAsync(Operation op, UpdateOffer offer, bool userInitiated)
        {
            bool isNew = _offer == null || !_offer.IsSameFile(offer);
            if (isNew)
            {
                // Another file than the one staged, a rebuilt dev build or a
                // replaced release: what was staged for the old file must not
                // install at the next launch.
                var pending = ReadPending();
                if (pending != null && !string.Equals(pending.AssetSha256, offer.Sha256, StringComparison.Ordinal)
                    && !TryDeletePending(out string error))
                {
                    ClearOffer();
                    Status(op, null, () => string.Format(Strings.Instance.Update_InstallFailed_Format, error));
                    return;
                }
                // The line that announced the old offer names a build this
                // one replaces.
                WithdrawAnnouncement();
                _offer = offer;
                _staged = null;
            }
            var shown = _offer;
            Settings.IsUpdateAvailable = true;
            Status(op, shown, () => AvailableText(shown));
            if (!userInitiated && isNew)
            {
                _announcement = string.Format(Strings.Instance.Update_AvailableStatusBar_Format, shown.DisplayVersion);
                _vm.SetStatus(_announcement, persist: true);
            }
            if (AutoInstallOn())
                await StageCore(op, shown);
        }

        private static string AvailableText(UpdateOffer offer) =>
            string.Format(offer.IsPreRelease
                    ? Strings.Instance.Update_PreReleaseAvailable_Format
                    : Strings.Instance.Update_Available_Format,
                offer.DisplayVersion);

        private bool AutoInstallOn() => Settings.InstallUpdatesAutomatically && Settings.CheckForUpdatesAutomatically;

        /// <summary>A record staged for this very file, not yet tried, with
        /// its exe still on disk.</summary>
        private static bool IsReadyPending(PendingUpdate pending, UpdateOffer offer) =>
            pending != null && !pending.Attempted && offer != null
            && string.Equals(pending.AssetSha256, offer.Sha256, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(pending.ExePath) && File.Exists(pending.ExePath);

        private async Task StageAsync(UpdateOffer offer)
        {
            var op = Begin();
            if (op == null)
            {
                // Staged once the running operation ends.
                if (!_disposed && !_committed) _stageWhenIdle = true;
                return;
            }
            try
            {
                await StageCore(op, offer);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                End(op);
            }
        }

        /// <summary>Install Updates Automatically: fetch and verify now,
        /// install at the next launch (App.OnStartup). Both switches are asked
        /// before the download and again after it.</summary>
        private async Task StageCore(Operation op, UpdateOffer offer)
        {
            // Staging starts here, so a queued request is spent, and a failed
            // download does not come back as an automatic retry.
            _stageWhenIdle = false;
            if (!AutoInstallOn() || !IsCurrent(op, offer)) return;
            bool preReleases = Settings.IncludePreReleaseUpdates;
            var pending = ReadPending();
            // Staged already, for the channel the launch will check it against.
            if (IsReadyPending(pending, offer) && pending.RequestedPreReleases == preReleases)
            {
                Status(op, offer, () => string.Format(Strings.Instance.Update_ReadyNextStart_Format, offer.DisplayVersion));
                return;
            }
            var staged = await DownloadCore(op, offer);
            if (staged == null || !IsCurrent(op, offer)) return;
            if (!AutoInstallOn())
            {
                // Switched off while it downloaded. Install and Restart still
                // has the file, and nothing installs at the next launch.
                Status(op, offer, () => AvailableText(offer));
                return;
            }
            try
            {
                WritePending(offer, staged, preReleases);
                Status(op, offer, () => string.Format(Strings.Instance.Update_ReadyNextStart_Format, offer.DisplayVersion));
            }
            catch (Exception ex)
            {
                Status(op, offer, FailureText(ex));
            }
        }

        /// <summary>Downloads with progress on the card. Null when it failed,
        /// in which case the card says why, or when the operation was
        /// canceled.</summary>
        private async Task<StagedUpdate> DownloadCore(Operation op, UpdateOffer offer)
        {
            // The cleanup deletes download folders it does not recognize, the
            // one holding a file this session kept among them. Nothing is
            // reused, or downloaded, until it is done, however it ended.
            try { await _cleanupTask; } catch { }
            if (!IsCurrent(op, offer)) return null;
            if (_staged != null && ReferenceEquals(offer, _offer) && File.Exists(_staged.ExePath))
                return _staged;
            // Staged for this same file at an earlier launch.
            var pending = ReadPending();
            if (IsReadyPending(pending, offer))
            {
                var ready = new StagedUpdate(pending.ExePath, pending.ExeSha256);
                if (IsCurrent(op, offer)) _staged = ready;
                return ready;
            }
            Settings.UpdateProgress = 0;
            Settings.IsUpdateDownloading = true;
            Status(op, offer, () => string.Format(Strings.Instance.Update_Downloading_Format, 0));
            try
            {
                var progress = new Progress<int>(p =>
                {
                    if (!IsCurrent(op, offer)) return;
                    Settings.UpdateProgress = p;
                    Settings.SetUpdateStatus(() => string.Format(Strings.Instance.Update_Downloading_Format, p));
                });
                var staged = await Download(offer, progress, op.Cts.Token);
                if (IsCurrent(op, offer)) _staged = staged;
                return staged;
            }
            catch (UpdateVerificationException)
            {
                Status(op, offer, () => Strings.Instance.Update_VerifyFailed);
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Status(op, offer, FailureText(ex));
                return null;
            }
            finally
            {
                Settings.IsUpdateDownloading = false;
            }
        }

        public async Task InstallAsync()
        {
            var offer = _offer;
            if (offer == null) return;
            var op = Begin();
            if (op == null) return;
            UpdateService.IHelperHandle helper = null;
            StagedUpdate staged = null;
            bool committed = false;
            try
            {
                staged = await DownloadCore(op, offer);
                if (staged == null || !IsCurrent(op, offer)) return;
                // A manual install replaces whatever the automatic path
                // staged. A record left behind is judged again at the next
                // launch against the updated build, so a failure here is not
                // reported.
                TryDeletePending(out _);
                var args = LaunchArgs();
                var token = op.Cts.Token;
                // Off the UI thread: the exe is hashed again (about 290 MB),
                // and the helper gets up to a minute to report ready.
                helper = await Task.Run(() => StartHelper(staged, offer.Commit, args, token));
                // Back on the UI thread, where a channel change cancels this
                // operation, so the commit and the change cannot interleave.
                if (!IsCurrent(op, offer))
                {
                    // Stopping it can take ten seconds, which the UI thread
                    // does not wait out.
                    var stale = helper;
                    await Task.Run(stale.Abort);
                    return;
                }
                helper.Commit();
                committed = _committed = true;
            }
            catch (UpdateVerificationException)
            {
                // Changed on disk since it was written. Drop it, so the next
                // Install and Restart downloads it again.
                UpdateService.DiscardStaged(staged);
                if (ReferenceEquals(_staged, staged)) _staged = null;
                Status(op, offer, () => Strings.Instance.Update_VerifyFailed);
            }
            catch (OperationCanceledException)
            {
            }
            catch (UpdateHelperException ex) when (ex.Failure == HelperFailure.NotStopped)
            {
                // Said even when the card has moved on: a helper that may
                // still be running is news whatever the card shows.
                if (!_disposed) Settings.SetUpdateStatus(FailureText(ex));
            }
            catch (Exception ex)
            {
                if (ex is UpdateHelperException { Failure: HelperFailure.WrongBuild })
                {
                    // Not the build the offer named. The next Install and
                    // Restart downloads it again.
                    UpdateService.DiscardStaged(staged);
                    if (ReferenceEquals(_staged, staged)) _staged = null;
                }
                Status(op, offer, FailureText(ex));
            }
            finally
            {
                try
                {
                    // A committed helper runs on after this process exits. One
                    // not committed is stopped, off the UI thread.
                    if (helper != null)
                    {
                        if (committed) helper.Dispose();
                        else await Task.Run(helper.Dispose);
                    }
                }
                catch (UpdateHelperException ex) when (ex.Failure == HelperFailure.NotStopped)
                {
                    // Said even when the card has moved on, as above.
                    if (!_disposed) Settings.SetUpdateStatus(FailureText(ex));
                }
                catch
                {
                    // Disposing a handle is not the update. The operation ends
                    // and a committed helper still gets its exit.
                }
                finally
                {
                    End(op);
                }
            }
            if (!committed) return;
            // The helper is committed and waits for this process to exit.
            try
            {
                _exitForUpdate();
            }
            catch (Exception ex)
            {
                // PadForge runs on, and the helper waits for it to close, then
                // installs. Nothing else starts meanwhile (_committed).
                string reason = ex.Message;
                if (!_disposed)
                    Settings.SetUpdateStatus(() => string.Format(Strings.Instance.Update_InstallFailed_Format, reason));
            }
        }

        /// <summary>The page Release Notes opens: the offered update's, or
        /// the releases page while no update is offered (#574), since the
        /// About page shows the button before any check. Null for an
        /// address outside PadForge's repository.</summary>
        internal static string ReleaseNotesUrl(UpdateOffer offer)
        {
            string url = offer == null ? "https://github.com/" + UpdateService.Repo + "/releases" : offer.ReleasePageUrl;
            return !string.IsNullOrEmpty(url) && url.StartsWith("https://github.com/" + UpdateService.Repo + "/", StringComparison.OrdinalIgnoreCase)
                ? url
                : null;
        }

        private void OpenReleaseNotes()
        {
            string url = ReleaseNotesUrl(_offer);
            if (url == null) return;
            // Through the update code's one process start, which the test host
            // refuses.
            try { UpdateService.StartProcess(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser registered */ }
        }

        private void SetBusy(bool busy) => Settings.IsUpdateBusy = busy;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _checkWhenIdle = false;
            _stageWhenIdle = false;
            _cleanupDue = false;
            _timer?.Stop();
            Settings.PropertyChanged -= OnSettingsChanged;
            if (_onCheckNow != null) Settings.CheckForUpdatesNowRequested -= _onCheckNow;
            if (_onInstall != null) Settings.InstallUpdateRequested -= _onInstall;
            if (_onReleaseNotes != null) Settings.OpenReleaseNotesRequested -= _onReleaseNotes;
            // Each operation disposes its own linked source when it ends. A
            // helper already committed runs on its own and is not touched.
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
