using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

/// <summary>
/// The Updates card's operations (#457). One runs at a time and owns the busy
/// state, and nothing an old operation finishes lands after the channel
/// changed or a switch went off. The network, the pending record, the helper
/// and the cleanup are stand-ins, and every test runs on a WPF dispatcher
/// thread, where the card runs.
/// </summary>
public class UpdateControllerTests
{
    private static UpdateOffer Offer(char sha, string display = "4.9.0") =>
        new(display, false, 0, null, new Version(4, 9, 0),
            "https://github.com/hifihedgehog/PadForge/releases/tag/v4.9.0", "PadForge-v4.9.0-win-x64.zip",
            "https://github.com/hifihedgehog/PadForge/releases/download/v4.9.0/PadForge-v4.9.0-win-x64.zip", 1,
            new string(sha, 64));

    private static Task<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)> Answer(
        UpdateCheckOutcome outcome, UpdateOffer offer = null) =>
        Task.FromResult((outcome, offer, (string)null));

    /// <summary>A helper that reported ready, and what was asked of it.</summary>
    private sealed class FakeHelper : UpdateService.IHelperHandle
    {
        public bool Committed, Aborted, Disposed;
        public Exception AbortFails, DisposeFails, CommitFails;
        public int AbortThread;

        public void Commit()
        {
            if (CommitFails != null) throw CommitFails;
            Committed = true;
        }

        public void Abort()
        {
            AbortThread = Environment.CurrentManagedThreadId;
            if (AbortFails != null) throw AbortFails;
            Aborted = true;
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeFails != null) throw DisposeFails;
        }
    }

    private sealed class Harness : IDisposable
    {
        public readonly MainViewModel Vm = new();
        public readonly UpdateController Controller;
        public readonly List<string> Calls = new();
        /// <summary>The staged exe's folder: one of this harness's own in the
        /// test host's scratch folder, so a staged file the controller drops
        /// takes nothing with it but this folder (UpdateServiceOffLimits).</summary>
        public readonly string StagedFolder = Path.Combine(UpdateService.TestScratchRoot, "UpdateController",
            "Staged_" + Guid.NewGuid().ToString("N"));
        public readonly string StagedExe;
        public readonly FakeHelper Helper = new();
        public int Exits, Cleanups;
        public Exception ExitFails;
        public PendingUpdate Pending;
        public TaskCompletionSource<StagedUpdate> DownloadGate;
        public Func<Task> CleanupWork = () => Task.CompletedTask;
        public string ExpectedCommit;

        public Harness()
        {
            Directory.CreateDirectory(StagedFolder);
            StagedExe = Path.Combine(StagedFolder, "PadForge.exe");
            File.WriteAllText(StagedExe, "exe");
            Controller = new UpdateController(Vm, () =>
            {
                Exits++;
                if (ExitFails != null) throw ExitFails;
            });
            Controller.ReadPending = () => Pending;
            Controller.WritePending = (offer, staged, pre) =>
            {
                Calls.Add("write " + offer.Sha256[..4] + (pre ? " dev" : ""));
                Pending = new PendingUpdate
                {
                    AssetSha256 = offer.Sha256, ExePath = staged.ExePath, ExeSha256 = staged.ExeSha256,
                    RequestedPreReleases = pre,
                };
            };
            Controller.DeletePending = () =>
            {
                Calls.Add("delete");
                Pending = null;
            };
            Controller.Download = (offer, progress, ct) =>
            {
                Calls.Add("download " + offer.Sha256[..4]);
                var gate = new TaskCompletionSource<StagedUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
                DownloadGate = gate;
                ct.Register(() => gate.TrySetCanceled());
                return gate.Task;
            };
            Controller.StartHelper = (staged, expected, args, ct) =>
            {
                Calls.Add("install");
                ExpectedCommit = expected;
                return Helper;
            };
            Controller.Cleanup = () =>
            {
                Cleanups++;
                return CleanupWork();
            };
            Controller.LaunchArgs = () => Array.Empty<string>();
            Controller.Subscribe();
        }

        public StagedUpdate Staged(string sha = "x") => new(StagedExe, sha);

        public void Dispose()
        {
            Controller.Dispose();
            try { Directory.Delete(StagedFolder, recursive: true); } catch { }
        }
    }

    /// <summary>Runs <paramref name="body"/> on an STA thread with a WPF
    /// dispatcher, so awaits come back to that thread and the controller's
    /// dispatcher callbacks run.</summary>
    private static void OnDispatcher(Func<Task> body)
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await body(); }
                catch (Exception ex) { error = ex; }
                finally { frame.Continue = false; }
            }));
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the dispatcher test did not finish");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Lets every queued dispatcher callback above idle priority run.</summary>
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ContextIdle);

    [Fact]
    public void SwitchingAutoInstallOffDuringTheDownloadInstallsNothingLater() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        var check = h.Controller.CheckAsync(userInitiated: false);
        Assert.NotNull(h.DownloadGate);
        h.Vm.Settings.InstallUpdatesAutomatically = false;
        h.DownloadGate.SetResult(h.Staged());
        await check;
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("write", StringComparison.Ordinal));
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });

    [Fact]
    public void ChangingChannelDuringAnInstallStartsNoHelper() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        Assert.True(h.Vm.Settings.IsUpdateAvailable);
        var install = h.Controller.InstallAsync();
        Assert.NotNull(h.DownloadGate);
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        await install;
        Assert.DoesNotContain("install", h.Calls);
        Assert.Equal(0, h.Exits);
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });

    [Fact]
    public void ADownloadThatFinishesAsTheChannelChangesInstallsNothing() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        // Done before the switch, still on its way back when the switch lands.
        h.DownloadGate.SetResult(h.Staged());
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        await install;
        Assert.DoesNotContain("install", h.Calls);
        Assert.Equal(0, h.Exits);
    });

    /// <summary>The helper reports ready on a worker thread. A channel change
    /// that lands before the UI thread commits it stops it, and PadForge runs
    /// on.</summary>
    [Fact]
    public void AChannelChangeBeforeTheCommitStopsTheReadyHelper() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        var ui = Dispatcher.CurrentDispatcher;
        h.Controller.StartHelper = (staged, expected, args, ct) =>
        {
            // Ready, and the switch lands before this answer reaches the UI thread.
            ui.Invoke(() => h.Vm.Settings.IncludePreReleaseUpdates = true);
            return h.Helper;
        };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.True(h.Helper.Aborted);
        Assert.False(h.Helper.Committed);
        Assert.True(h.Helper.Disposed);
        // Stopping a helper can take ten seconds, which the UI thread does not wait out.
        Assert.NotEqual(ui.Thread.ManagedThreadId, h.Helper.AbortThread);
        Assert.Equal(0, h.Exits);
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });

    [Fact]
    public void ACheckThatFindsNothingNewerRetiresWhatWasStaged() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.UpToDate);
        await h.Controller.CheckAsync(userInitiated: false);
        Assert.Contains("delete", h.Calls);
        Assert.Null(h.Pending);
    });

    /// <summary>A check that answers after the card closed with PadForge
    /// changes nothing, the staged record included.</summary>
    [Fact]
    public void ACheckThatAnswersAfterDisposalChangesNothing() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        var late = new TaskCompletionSource<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Controller.Check = (pre, ct) => late.Task;
        var check = h.Controller.CheckAsync(userInitiated: true);
        h.Controller.Dispose();
        late.SetResult((UpdateCheckOutcome.UpToDate, null, null));
        await check;
        Assert.DoesNotContain("delete", h.Calls);
        Assert.NotNull(h.Pending);
    });

    [Fact]
    public void ADisposedControllerAnswersNoCommand() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        int checks = 0;
        h.Controller.Check = (pre, ct) => { checks++; return Answer(UpdateCheckOutcome.UpToDate); };
        h.Controller.Dispose();
        h.Vm.Settings.CheckForUpdatesNowCommand.Execute(null);
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        await h.Controller.CheckAsync(userInitiated: true);
        h.Controller.OnTimerTick();
        await Idle();
        Assert.Equal(0, checks);
        Assert.Equal(0, h.Cleanups);
    });

    [Fact]
    public void AnAutomaticCheckAsksTheSwitchWhicheverPathStartedIt() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        int checks = 0;
        h.Controller.Check = (pre, ct) => { checks++; return Answer(UpdateCheckOutcome.UpToDate); };
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        await h.Controller.CheckAsync(userInitiated: false);
        h.Controller.OnTimerTick();
        await Idle();
        Assert.Equal(0, checks);
        // Check Now is the user asking, whatever the switch says.
        await h.Controller.CheckAsync(userInitiated: true);
        Assert.Equal(1, checks);
    });

    [Fact]
    public void InstallTurnedOnWhileCheckingIsOffStagesNothing() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        await Idle();
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
    });

    [Fact]
    public void SwitchingChannelTakesBackTheAnnouncement() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        int checks = 0;
        h.Controller.Check = (pre, ct) => ++checks == 1
            ? Answer(UpdateCheckOutcome.Available, Offer('a', "4.9.0"))
            : Answer(UpdateCheckOutcome.UpToDate);
        await h.Controller.CheckAsync(userInitiated: false);
        Assert.Contains("4.9.0", h.Vm.StatusText);
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        await Idle();
        Assert.DoesNotContain("4.9.0", h.Vm.StatusText ?? string.Empty);
        Assert.False(h.Vm.Settings.IsUpdateAvailable);
    });

    [Fact]
    public void InstallHoldsBusyUntilTheHelperCommitsAndThenExits() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        bool busyWhileStarting = false;
        h.Controller.StartHelper = (staged, expected, args, ct) =>
        {
            busyWhileStarting = h.Vm.Settings.IsUpdateBusy;
            return h.Helper;
        };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.True(busyWhileStarting);
        Assert.True(h.Helper.Committed);
        // Its handle is let go, and the helper itself runs on.
        Assert.True(h.Helper.Disposed);
        Assert.False(h.Helper.Aborted);
        Assert.Equal(1, h.Exits);
        // Closing PadForge for the update leaves the committed helper alone.
        h.Controller.Dispose();
        Assert.False(h.Helper.Aborted);
    });

    [Fact]
    public void AHelperThatFailsLeavesPadForgeRunningAndSaysWhy() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Controller.StartHelper = (staged, expected, args, ct) => throw new InvalidOperationException("no answer from the helper");
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.Equal(0, h.Exits);
        Assert.Contains("no answer from the helper", h.Vm.Settings.UpdateStatusText);
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });

    /// <summary>A helper that may still be running is news whatever the card
    /// shows, so it is said even after a channel change cleared the card.</summary>
    [Fact]
    public void AHelperThatWouldNotStopIsReportedEvenAfterTheCardMovedOn() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        var ui = Dispatcher.CurrentDispatcher;
        h.Helper.AbortFails = new UpdateHelperException(HelperFailure.NotStopped);
        h.Controller.StartHelper = (staged, expected, args, ct) =>
        {
            ui.Invoke(() => h.Vm.Settings.IncludePreReleaseUpdates = true);
            return h.Helper;
        };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.Equal(0, h.Exits);
        Assert.False(h.Helper.Committed);
        Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Update_HelperNotStopped,
            h.Vm.Settings.UpdateStatusText);
    });

    [Fact]
    public void ANewFileRetiresWhatWasStagedForTheOldOneFirst() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('b'));
        var check = h.Controller.CheckAsync(userInitiated: false);
        Assert.Equal(new[] { "delete", "download bbbb" }, h.Calls);
        h.DownloadGate.SetResult(h.Staged("y"));
        await check;
        Assert.Equal("write bbbb", h.Calls[^1]);
    });

    [Fact]
    public void InstallUsesTheFileStagedAtAnEarlierLaunch() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        await h.Controller.InstallAsync();
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
        Assert.Contains("install", h.Calls);
        Assert.True(h.Helper.Committed);
    });

    /// <summary>The launch drops a record staged for the other channel, so a
    /// record for this very file, staged for the other channel, is written
    /// again for this one rather than called ready.</summary>
    [Fact]
    public void AFileStagedForTheOtherChannelIsStagedAgainForThisOne() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        h.Pending = new PendingUpdate
        {
            AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x", RequestedPreReleases = true,
        };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: false);
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
        Assert.Equal("write aaaa", h.Calls[^1]);
        Assert.False(h.Pending.RequestedPreReleases);
    });

    /// <summary>The cleanup waits for the first tick, which shows this exe
    /// runs, and for a moment when nothing downloads, since it deletes
    /// download folders it does not recognize. It runs once.</summary>
    [Fact]
    public void TheCleanupWaitsForTheFirstTickAndAnIdleCard() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        // An install downloads before the first tick: nothing is cleaned yet.
        var install = h.Controller.InstallAsync();
        Assert.Equal(0, h.Cleanups);
        h.Controller.OnTimerTick();
        Assert.Equal(0, h.Cleanups);
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        await install;
        await Idle();
        Assert.Equal(1, h.Cleanups);
        h.Controller.OnTimerTick();
        Assert.Equal(1, h.Cleanups);
    });

    /// <summary>After the commit PadForge is on its way out. Nothing more
    /// starts, even when closing did not work, and the card says why it is
    /// still open.</summary>
    [Fact]
    public void ACommittedInstallStartsNothingMoreEvenIfClosingFails() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.ExitFails = new InvalidOperationException("the window would not close");
        int checks = 0;
        h.Controller.Check = (pre, ct) => { checks++; return Answer(UpdateCheckOutcome.Available, Offer('a')); };
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.True(h.Helper.Committed);
        Assert.Equal(1, h.Exits);
        Assert.Contains("the window would not close", h.Vm.Settings.UpdateStatusText);
        // Every command the card offers would be refused now.
        Assert.True(h.Vm.Settings.IsUpdateBusy);
        await h.Controller.CheckAsync(userInitiated: true);
        await h.Controller.InstallAsync();
        // The automatic paths too, without queueing or calling themselves.
        h.Vm.Settings.CheckForUpdatesAutomatically = true;
        h.Controller.OnTimerTick();
        await h.Controller.CheckAsync(userInitiated: false);
        await Idle();
        Assert.Equal(1, checks);
        Assert.Single(h.Calls, "install");
        Assert.Equal(0, h.Cleanups);
        // The switches still change and save, and the updater leaves the
        // committed install, its record and the card alone.
        int deletes = h.Calls.FindAll(c => c == "delete").Count;
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        h.Vm.Settings.InstallUpdatesAutomatically = false;
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        await Idle();
        Assert.Equal(deletes, h.Calls.FindAll(c => c == "delete").Count);
        Assert.Contains("the window would not close", h.Vm.Settings.UpdateStatusText);
        Assert.True(h.Vm.Settings.IsUpdateAvailable);
        Assert.True(h.Vm.Settings.IsUpdateBusy);
    });

    /// <summary>A commit that throws leaves a helper to stop, and one that
    /// will not stop is what the card says.</summary>
    [Fact]
    public void AHelperThatWillNotStopAfterAFailedCommitIsReported() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Helper.CommitFails = new InvalidOperationException("the event would not set");
        h.Helper.DisposeFails = new UpdateHelperException(HelperFailure.NotStopped);
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.Equal(0, h.Exits);
        Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Update_HelperNotStopped, h.Vm.Settings.UpdateStatusText);
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });

    /// <summary>A handle that throws as it is disposed still lets the
    /// operation end, and a committed install still exits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AHandleThatThrowsOnDisposeStillEndsTheOperation(bool commit) => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.Helper.DisposeFails = new InvalidOperationException("dispose failed");
        var ui = Dispatcher.CurrentDispatcher;
        if (!commit)
        {
            h.Controller.StartHelper = (staged, expected, args, ct) =>
            {
                ui.Invoke(() => h.Vm.Settings.IncludePreReleaseUpdates = true);
                return h.Helper;
            };
        }
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.True(h.Helper.Disposed);
        Assert.Equal(commit ? 1 : 0, h.Exits);
        Assert.Equal(commit, h.Vm.Settings.IsUpdateBusy);
    });

    /// <summary>A cleanup that faulted does not stop the next download.</summary>
    [Fact]
    public void AFaultedCleanupDoesNotStopADownload() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        h.CleanupWork = () => Task.FromException(new IOException("cleanup failed"));
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        h.Controller.OnTimerTick();
        Assert.Equal(1, h.Cleanups);
        var install = h.Controller.InstallAsync();
        Assert.Contains("download aaaa", h.Calls);
        h.DownloadGate.SetCanceled();
        await install;
    });

    /// <summary>A queued check and a queued staging together: the check runs
    /// first, and when it ends without staging, the staging still runs.</summary>
    [Fact]
    public void AQueuedCheckDoesNotEraseAQueuedStaging() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        int checks = 0;
        var running = new TaskCompletionSource<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Controller.Check = (pre, ct) => ++checks switch
        {
            1 => Answer(UpdateCheckOutcome.Available, Offer('a')),
            2 => running.Task,
            _ => Answer(UpdateCheckOutcome.RateLimited),
        };
        await h.Controller.CheckAsync(userInitiated: true);
        var busy = h.Controller.CheckAsync(userInitiated: true);
        // Both queue behind the running check.
        h.Vm.Settings.CheckForUpdatesAutomatically = true;
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        running.SetResult((UpdateCheckOutcome.RateLimited, null, null));
        await busy;
        await Idle();
        await Idle();
        Assert.Equal(3, checks);
        Assert.Contains("download aaaa", h.Calls);
        h.DownloadGate.SetCanceled();
        await Idle();
    });

    /// <summary>The offer's commit reaches the helper, which refuses to be
    /// another build. A refusal drops the file, so the next try fetches it
    /// again.</summary>
    [Fact]
    public void AHelperThatIsAnotherBuildDropsTheFile() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        var offer = Offer('a') with { Commit = "b500441" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, offer);
        h.Controller.StartHelper = (staged, expected, args, ct) =>
        {
            h.ExpectedCommit = expected;
            throw new UpdateHelperException(HelperFailure.WrongBuild);
        };
        await h.Controller.CheckAsync(userInitiated: true);
        var install = h.Controller.InstallAsync();
        h.DownloadGate.SetResult(h.Staged());
        await install;
        Assert.Equal("b500441", h.ExpectedCommit);
        Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Update_HelperWrongBuild, h.Vm.Settings.UpdateStatusText);
        var again = h.Controller.InstallAsync();
        Assert.Equal(2, h.Calls.FindAll(c => c.StartsWith("download", StringComparison.Ordinal)).Count);
        h.DownloadGate.SetCanceled();
        await again;
    });

    /// <summary>A file kept from an earlier download is reused only after the
    /// cleanup, which could delete it, has finished.</summary>
    [Fact]
    public void AKeptFileIsReusedOnlyAfterTheCleanup() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        await h.Controller.CheckAsync(userInitiated: true);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.CleanupWork = () => cleanup.Task;
        h.Controller.OnTimerTick();
        Assert.Equal(1, h.Cleanups);
        var install = h.Controller.InstallAsync();
        await Idle();
        Assert.DoesNotContain("install", h.Calls);
        cleanup.SetResult();
        await install;
        Assert.Contains("install", h.Calls);
    });

    [Fact]
    public void NoBuildForThisPcRetiresWhatWasStaged() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Pending = new PendingUpdate { AssetSha256 = new string('a', 64), ExePath = h.StagedExe, ExeSha256 = "x" };
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.NoBuildForThisPc);
        await h.Controller.CheckAsync(userInitiated: true);
        Assert.Null(h.Pending);
        Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Update_NoBuildForThisPc, h.Vm.Settings.UpdateStatusText);
    });

    /// <summary>Switching installing off after the download finished takes
    /// back the card's promise along with the record.</summary>
    [Fact]
    public void SwitchingInstallOffTakesBackTheNextStartPromise() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        h.Controller.Check = (pre, ct) => Answer(UpdateCheckOutcome.Available, Offer('a'));
        var check = h.Controller.CheckAsync(userInitiated: false);
        h.DownloadGate.SetResult(h.Staged());
        await check;
        Assert.NotNull(h.Pending);
        string ready = h.Vm.Settings.UpdateStatusText;
        h.Vm.Settings.InstallUpdatesAutomatically = false;
        Assert.Null(h.Pending);
        Assert.NotEqual(ready, h.Vm.Settings.UpdateStatusText);
        Assert.Contains("4.9.0", h.Vm.Settings.UpdateStatusText);
    });

    /// <summary>Install Updates Automatically turned on while a check runs is
    /// not lost when that check ends without an offer of its own.</summary>
    [Fact]
    public void InstallTurnedOnWhileBusyStagesOnceIdle() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        int checks = 0;
        var second = new TaskCompletionSource<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Controller.Check = (pre, ct) => ++checks == 1 ? Answer(UpdateCheckOutcome.Available, Offer('a')) : second.Task;
        await h.Controller.CheckAsync(userInitiated: true);
        var running = h.Controller.CheckAsync(userInitiated: true);
        h.Vm.Settings.InstallUpdatesAutomatically = true;
        Assert.DoesNotContain(h.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
        second.SetResult((UpdateCheckOutcome.RateLimited, null, null));
        await running;
        await Idle();
        Assert.Contains("download aaaa", h.Calls);
        h.DownloadGate.SetCanceled();
        await Idle();
    });

    /// <summary>A manual check that finds a newer build than the one the
    /// message line announced takes that announcement back.</summary>
    [Fact]
    public void ANewerOfferTakesBackTheOldAnnouncement() => OnDispatcher(async () =>
    {
        using var h = new Harness();
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        int checks = 0;
        h.Controller.Check = (pre, ct) => ++checks == 1
            ? Answer(UpdateCheckOutcome.Available, Offer('a', "4.9.0"))
            : Answer(UpdateCheckOutcome.Available, Offer('b', "4.9.1"));
        h.Vm.Settings.CheckForUpdatesAutomatically = true;
        await Idle();
        Assert.Contains("4.9.0", h.Vm.StatusText);
        h.Vm.Settings.CheckForUpdatesAutomatically = false;
        await h.Controller.CheckAsync(userInitiated: true);
        Assert.DoesNotContain("4.9.0", h.Vm.StatusText ?? string.Empty);
    });

    /// <summary>A helper the launch could not stop is reported as such, not
    /// as an update that did not finish.</summary>
    [Fact]
    public void AHelperTheLaunchCouldNotStopIsSaidAtStart() => OnDispatcher(async () =>
    {
        UpdateService.LastPendingFailure = "4.9.0";
        UpdateService.LastPendingError = new UpdateHelperException(HelperFailure.NotStopped);
        try
        {
            using var h = new Harness();
            h.Vm.Settings.CheckForUpdatesAutomatically = false;
            // Even on a launch the last update marked as updated.
            h.Controller.LaunchArgs = () => new[] { UpdateService.UpdatedSwitch };
            h.Controller.Start();
            await Idle();
            Assert.Contains(PadForge.Resources.Strings.Strings.Instance.Update_HelperNotStopped, h.Vm.StatusText);
        }
        finally
        {
            UpdateService.LastPendingFailure = null;
            UpdateService.LastPendingError = null;
        }
    });

    /// <summary>The helper's wait question closes itself once the old copy
    /// has exited, as if OK had been chosen.</summary>
    [Fact]
    public void TheWaitQuestionAnswersItselfOnceTheOldCopyExits() => OnDispatcher(() =>
    {
        int asked = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool keep = App.AskToKeepWaiting("waiting", () => ++asked >= 3, TimeSpan.FromMilliseconds(40), w =>
        {
            w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            w.Left = -10000;
            w.Top = -10000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        });
        Assert.True(keep);
        Assert.True(asked >= 3);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void AQueuedCheckRunsOnlyWhileCheckingIsStillOn(bool turnCheckingOff, int expectedChecks) => OnDispatcher(async () =>
    {
        using var h = new Harness();
        int checks = 0;
        bool lastChannel = false;
        // A real request is canceled on another thread and comes back through
        // the dispatcher, so the check is still running while the handler
        // queues the next one.
        var first = new TaskCompletionSource<(UpdateCheckOutcome Outcome, UpdateOffer Offer, string Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Controller.Check = (pre, ct) =>
        {
            lastChannel = pre;
            if (++checks > 1) return Answer(UpdateCheckOutcome.UpToDate);
            ct.Register(() => first.TrySetCanceled());
            return first.Task;
        };
        var check = h.Controller.CheckAsync(userInitiated: false);
        // Cancels the running check and asks for one on the new channel.
        h.Vm.Settings.IncludePreReleaseUpdates = true;
        if (turnCheckingOff) h.Vm.Settings.CheckForUpdatesAutomatically = false;
        await check;
        await Idle();
        Assert.Equal(expectedChecks, checks);
        if (!turnCheckingOff) Assert.True(lastChannel);
        Assert.False(h.Vm.Settings.IsUpdateBusy);
    });
}
