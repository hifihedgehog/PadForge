using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Services;

namespace PadForge.Tests
{
    public class PsmPatchCoordinatorTests
    {
        private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
        private sealed class Rig
        {
            internal readonly List<bool> Enabled = new() { true, true };
            internal bool Present = true;
            internal bool DriversInstalled = true;
            internal bool PartialRead;
            internal bool ShortDisable;
            internal bool IgnoreDisable;
            internal bool WantEnabled = true;
            internal int DisableCalls;
            internal int EnableCalls;
            internal int RestoreCalls;
            internal Action OnRestore;
            internal readonly PsmPatchCoordinator Coordinator;

            internal Rig()
            {
                Coordinator = new(Read, Disable, () => !DriversInstalled, Restore);
            }

            private PsmPatchSnapshot Read()
            {
                if (!Present) return PsmPatchSnapshot.Unavailable(2);
                return PsmPatchSnapshot.Read(buffer =>
                {
                    int index = BitConverter.ToInt32(buffer);
                    if (PartialRead && index == 1) return new(false, 0, 5);
                    if (index >= Enabled.Count) return new(false, 0, 433);
                    BitConverter.GetBytes(Enabled[index] ? 1u : 0u).CopyTo(buffer, 4);
                    return new(true, 408, 0);
                });
            }

            private int Disable()
            {
                DisableCalls++;
                if (!IgnoreDisable)
                    for (int i = 0; i < Enabled.Count; i++) Enabled[i] = false;
                return ShortDisable ? Enabled.Count - 1 : Enabled.Count;
            }

            internal PsmPatchRequestResult Enable() => Coordinator.Apply(true, () =>
            {
                EnableCalls++;
                for (int i = 0; i < Enabled.Count; i++) Enabled[i] = true;
                return Enabled.Count;
            });

            private void Restore()
            {
                RestoreCalls++;
                bool desired = WantEnabled;
                OnRestore?.Invoke();
                if (desired) Enable();
                else Coordinator.Apply(false, Disable);
            }
        }

        [Fact]
        public void AScanDisablesEveryRadioAndDefersAllLocalEnableRequests()
        {
            var rig = new Rig();
            using (var scan = rig.Coordinator.Suspend())
            {
                Assert.True(scan.VerifyDisabled(out _));
                var request = rig.Enable();
                Assert.True(request.Deferred);
                Assert.Equal(0, request.AppliedRadios);
                Assert.Equal(0, rig.EnableCalls);
                Assert.All(rig.Enabled, enabled => Assert.False(enabled));
                Assert.True(scan.VerifyDisabled(out _));
            }
            Assert.Equal(1, rig.DisableCalls);
            Assert.Equal(1, rig.RestoreCalls);
            Assert.Equal(1, rig.EnableCalls);
        }

        [Fact]
        public void NestedScansRestoreOnlyAfterTheLastOwnerLeaves()
        {
            var rig = new Rig();
            var first = rig.Coordinator.Suspend();
            var second = rig.Coordinator.Suspend();
            Assert.True(first.VerifyDisabled(out _));
            first.Dispose();
            first.Dispose();
            Assert.Equal(0, rig.RestoreCalls);
            Assert.True(second.VerifyDisabled(out _));
            Assert.True(rig.Enable().Deferred);
            second.Dispose();
            Assert.Equal(1, rig.RestoreCalls);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void FinalReleaseUsesTheCurrentPolicy()
        {
            var rig = new Rig();
            using (var scan = rig.Coordinator.Suspend())
            {
                Assert.True(scan.VerifyDisabled(out _));
                rig.WantEnabled = false;
            }
            Assert.Equal(1, rig.RestoreCalls);
            Assert.Equal(0, rig.EnableCalls);
            Assert.All(rig.Enabled, enabled => Assert.False(enabled));
        }

        [Fact]
        public void ANewScanCannotBeRearmedByThePreviousScansDelayedRestore()
        {
            var rig = new Rig();
            PsmPatchCoordinator.Suspension next = null;
            var first = rig.Coordinator.Suspend();
            Assert.True(first.VerifyDisabled(out _));
            rig.OnRestore = () =>
            {
                rig.OnRestore = null;
                next = rig.Coordinator.Suspend();
            };
            first.Dispose();
            Assert.NotNull(next);
            Assert.Equal(0, rig.EnableCalls);
            Assert.True(next.VerifyDisabled(out _));
            next.Dispose();
            Assert.Equal(2, rig.RestoreCalls);
            Assert.Equal(1, rig.EnableCalls);
        }

        [Fact]
        public async Task AnOlderRestoreCannotOverwriteANewerCompletedScansPolicy()
        {
            var rig = new Rig();
            using var oldRestoreEntered = new ManualResetEventSlim();
            using var finishOldRestore = new ManualResetEventSlim();
            var first = rig.Coordinator.Suspend();
            Assert.True(first.VerifyDisabled(out _));
            rig.OnRestore = () =>
            {
                if (rig.RestoreCalls != 1) return;
                oldRestoreEntered.Set();
                if (!finishOldRestore.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            };
            var oldRelease = Task.Run(first.Dispose);
            var newRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread newOwner = null;
            try
            {
                Assert.True(oldRestoreEntered.Wait(TimeSpan.FromSeconds(5)));
                var second = rig.Coordinator.Suspend();
                Assert.True(second.VerifyDisabled(out _));
                rig.WantEnabled = false;
                newOwner = new Thread(() =>
                {
                    try { second.Dispose(); newRelease.SetResult(); }
                    catch (Exception ex) { newRelease.SetException(ex); }
                }) { IsBackground = true };
                newOwner.Start();
                Assert.True(SpinWait.SpinUntil(() => !rig.Coordinator.IsSuspended, TimeSpan.FromSeconds(5)));
                Assert.True(SpinWait.SpinUntil(() => newRelease.Task.IsCompleted
                    || (newOwner.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                Assert.False(newRelease.Task.IsCompleted);
                finishOldRestore.Set();
                await Task.WhenAll(oldRelease, newRelease.Task);
                Assert.Equal(2, rig.RestoreCalls);
                Assert.All(rig.Enabled, enabled => Assert.False(enabled));
            }
            finally
            {
                finishOldRestore.Set();
                await oldRelease;
                if (newOwner != null) await newRelease.Task;
            }
        }

        [Fact]
        public void PartialReadbackNeverStartsProtectedPairing()
        {
            var rig = new Rig { PartialRead = true };
            using var scan = rig.Coordinator.Suspend();
            Assert.False(scan.VerifyDisabled(out string error));
            Assert.Contains("read completely", error);
            Assert.Equal(0, rig.DisableCalls);
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void APartialOrIneffectiveDisableFailsAndStillRestores(bool shortDisable, bool ignoreDisable)
        {
            var rig = new Rig { ShortDisable = shortDisable, IgnoreDisable = ignoreDisable };
            using (var scan = rig.Coordinator.Suspend())
                Assert.False(scan.VerifyDisabled(out _));
            Assert.Equal(1, rig.RestoreCalls);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void ExternalRearmingInvalidatesEveryActiveScan()
        {
            var rig = new Rig();
            using var first = rig.Coordinator.Suspend();
            using var second = rig.Coordinator.Suspend();
            Assert.True(first.VerifyDisabled(out _));
            rig.Enabled[1] = true;
            Assert.False(second.VerifyDisabled(out string error));
            Assert.Contains("enabled again", error);
            rig.Enabled[1] = false;
            Assert.False(first.VerifyDisabled(out _));
            Assert.True(rig.Enable().Deferred);
        }

        [Fact]
        public void AChangedRadioCollectionInvalidatesTheScan()
        {
            var rig = new Rig();
            using var scan = rig.Coordinator.Suspend();
            Assert.True(scan.VerifyDisabled(out _));
            rig.Enabled.Add(false);
            Assert.False(scan.VerifyDisabled(out string error));
            Assert.Contains("instances changed", error);
        }

        [Fact]
        public void AnAbsentEndpointIsAcceptedOnlyWithoutAnInstalledStack()
        {
            var rig = new Rig { Present = false, DriversInstalled = false };
            using (var scan = rig.Coordinator.Suspend())
                Assert.True(scan.VerifyDisabled(out _));
            Assert.Equal(0, rig.DisableCalls);
            Assert.Equal(0, rig.RestoreCalls);

            rig.DriversInstalled = true;
            using var installedScan = rig.Coordinator.Suspend();
            Assert.False(installedScan.VerifyDisabled(out _));
        }

        [Fact]
        public void AFilterThatAppearsAfterAnAbsentEndpointRequiresANewScan()
        {
            var rig = new Rig { Present = false, DriversInstalled = false };
            using var scan = rig.Coordinator.Suspend();
            Assert.True(scan.VerifyDisabled(out _));
            rig.Present = true;
            Assert.False(scan.VerifyDisabled(out _));
        }

        [Fact]
        public void ADeferredEnableIsRestoredEvenWhenTheScanNeverReachesInquiry()
        {
            var rig = new Rig();
            using (rig.Coordinator.Suspend())
                Assert.True(rig.Enable().Deferred);
            Assert.Equal(1, rig.RestoreCalls);
            Assert.Equal(1, rig.EnableCalls);
        }

        [Fact]
        public void VerificationExceptionsDoNotLeakTheSuspension()
        {
            int restores = 0;
            var coordinator = new PsmPatchCoordinator(
                () => throw new InvalidOperationException("read failed"), () => 0, () => false, () => restores++);
            using (var scan = coordinator.Suspend())
            {
                Assert.False(scan.VerifyDisabled(out string error));
                Assert.Contains("read failed", error);
            }
            Assert.False(coordinator.IsSuspended);
            Assert.Equal(0, restores);
            Assert.False(coordinator.Apply(true, () => 1).Deferred);
        }

        [Fact]
        public void AnExplicitDisableDuringAnUnusedScopeStillReconcilesTheFinalPolicy()
        {
            var rig = new Rig();
            using (rig.Coordinator.Suspend())
                rig.Coordinator.Apply(false, () => 1);
            Assert.Equal(1, rig.RestoreCalls);
        }

        [Fact]
        public void AThrowingRestoreDoesNotRetainTheDisposedOwner()
        {
            var rig = new Rig();
            var scan = rig.Coordinator.Suspend();
            Assert.True(scan.VerifyDisabled(out _));
            rig.OnRestore = () => throw new InvalidOperationException("restore failed");
            Assert.Throws<InvalidOperationException>(scan.Dispose);
            Assert.False(rig.Coordinator.IsSuspended);
            rig.OnRestore = null;
            scan.Dispose();
            Assert.Equal(1, rig.RestoreCalls);
        }

        [Fact]
        public void WholeWiiScanHasNoEnableGapBetweenPasses()
        {
            var rig = new Rig();
            int begins = 0, passes = 0;
            PsmPatchCoordinator.Suspension first = null;
            var result = WiiPairingService.RunPairingScan(() =>
            {
                begins++;
                return rig.Coordinator.Suspend();
            }, scope =>
            {
                first ??= scope;
                Assert.Same(first, scope);
                Assert.True(scope.VerifyDisabled(out _));
                var pass = new WiiPairingService.PairPassResult();
                if (++passes == 3) pass.Paired.Add("Wii");
                return pass;
            }, CancellationToken.None, new ImmediateProgress<WiiPairingService.PairPassResult>(_ =>
                Assert.True(rig.Enable().Deferred)));

            Assert.Equal(1, begins);
            Assert.Equal(3, passes);
            Assert.Single(result.Paired);
            Assert.Equal(1, rig.DisableCalls);
            Assert.Equal(1, rig.RestoreCalls);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void WiiScanStopsOnFailedVerificationWithoutAnImmediateRetryLoop()
        {
            var rig = new Rig { PartialRead = true };
            int passes = 0;
            var result = WiiPairingService.RunPairingScan(rig.Coordinator.Suspend, scope =>
            {
                passes++;
                Assert.False(scope.VerifyDisabled(out _));
                return new WiiPairingService.PairPassResult { Error = WiiPairingService.PsmVerificationFailed };
            }, CancellationToken.None, null);
            Assert.Equal(WiiPairingService.PsmVerificationFailed, result.Error);
            Assert.Equal(1, passes);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void CancelingWiiScanBetweenPassesStillRestoresPolicy()
        {
            var rig = new Rig();
            using var cancellation = new CancellationTokenSource();
            int passes = 0;
            WiiPairingService.RunPairingScan(rig.Coordinator.Suspend, scope =>
            {
                passes++;
                Assert.True(scope.VerifyDisabled(out _));
                return new WiiPairingService.PairPassResult();
            }, cancellation.Token, new ImmediateProgress<WiiPairingService.PairPassResult>(_ => cancellation.Cancel()));
            Assert.Equal(1, passes);
            Assert.Equal(1, rig.RestoreCalls);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void AnAlreadyCanceledWiiScanDoesNotAcquireOrTouchTheFilter()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            WiiPairingService.RunPairingScan(
                () => throw new InvalidOperationException("unexpected acquisition"),
                _ => throw new InvalidOperationException("unexpected inquiry"), cancellation.Token, null);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void WiiScanRestoresPolicyWhenAPassOrProgressDeliveryThrows(bool progressThrows)
        {
            var rig = new Rig();
            Assert.Throws<InvalidOperationException>(() => WiiPairingService.RunPairingScan(
                rig.Coordinator.Suspend, scope =>
                {
                    Assert.True(scope.VerifyDisabled(out _));
                    if (!progressThrows) throw new InvalidOperationException("pass failed");
                    return new WiiPairingService.PairPassResult();
                }, CancellationToken.None, new ImmediateProgress<WiiPairingService.PairPassResult>(_ =>
                    throw new InvalidOperationException("progress failed"))));
            Assert.Equal(1, rig.RestoreCalls);
            Assert.False(rig.Coordinator.IsSuspended);
        }

        [Fact]
        public void FastEmptyWiiPassesArePacedWhileSuppressionStaysHeld()
        {
            var rig = new Rig();
            var starts = new List<long>();
            var result = WiiPairingService.RunPairingScan(rig.Coordinator.Suspend, scope =>
            {
                starts.Add(Stopwatch.GetTimestamp());
                Assert.True(scope.VerifyDisabled(out _));
                var pass = new WiiPairingService.PairPassResult();
                if (starts.Count == 3) pass.Paired.Add("Wii");
                return pass;
            }, CancellationToken.None, new ImmediateProgress<WiiPairingService.PairPassResult>(_ =>
                Assert.True(rig.Enable().Deferred)));

            Assert.Single(result.Paired);
            Assert.Equal(3, starts.Count);
            Assert.True(Stopwatch.GetElapsedTime(starts[0], starts[1]).TotalMilliseconds >= 90);
            Assert.True(Stopwatch.GetElapsedTime(starts[1], starts[2]).TotalMilliseconds >= 90);
            Assert.Equal(1, rig.RestoreCalls);
            Assert.Equal(1, rig.EnableCalls);
        }

        [Fact]
        public async Task CancelingDuringTheWiiRetryWaitRestoresWithoutAnotherPass()
        {
            var rig = new Rig();
            using var cancellation = new CancellationTokenSource();
            using var firstPass = new ManualResetEventSlim();
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int passes = 0;
            var worker = new Thread(() =>
            {
                try
                {
                    WiiPairingService.RunPairingScan(rig.Coordinator.Suspend, scope =>
                    {
                        Assert.True(scope.VerifyDisabled(out _));
                        if (Interlocked.Increment(ref passes) == 2) cancellation.Cancel();
                        firstPass.Set();
                        return new WiiPairingService.PairPassResult();
                    }, cancellation.Token, null);
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); }
            }) { IsBackground = true };
            worker.Start();
            try
            {
                Assert.True(firstPass.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(SpinWait.SpinUntil(() => finished.Task.IsCompleted
                    || (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2)));
                Assert.False(finished.Task.IsCompleted);
                Assert.Equal(1, Volatile.Read(ref passes));
                Assert.True(rig.Coordinator.IsSuspended);
                cancellation.Cancel();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(1, passes);
                Assert.Equal(1, rig.RestoreCalls);
                Assert.False(rig.Coordinator.IsSuspended);
            }
            finally
            {
                cancellation.Cancel();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public void ASlowWiiPassDoesNotProduceAnInvalidRetryDelay()
        {
            var rig = new Rig();
            int passes = 0;
            var result = WiiPairingService.RunPairingScan(rig.Coordinator.Suspend, scope =>
            {
                Assert.True(scope.VerifyDisabled(out _));
                Thread.Sleep(120);
                return new WiiPairingService.PairPassResult { Error = ++passes == 2 ? "finished" : null };
            }, CancellationToken.None, null);
            Assert.Equal("finished", result.Error);
            Assert.Equal(2, passes);
            Assert.Equal(1, rig.RestoreCalls);
        }

        [Fact]
        public async Task SuspensionWaitsForAnAlreadyRunningEnableToFinish()
        {
            var rig = new Rig();
            using var enteredWrite = new ManualResetEventSlim();
            using var finishWrite = new ManualResetEventSlim();
            using var acquiredScope = new ManualResetEventSlim();
            bool attemptingScope = false;
            PsmPatchCoordinator.Suspension scan = null;
            var writer = Task.Run(() => rig.Coordinator.Apply(true, () =>
            {
                enteredWrite.Set();
                if (!finishWrite.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                rig.Enabled[0] = true;
                return 1;
            }));
            Thread entrant = new(() =>
            {
                Volatile.Write(ref attemptingScope, true);
                scan = rig.Coordinator.Suspend();
                acquiredScope.Set();
            }) { IsBackground = true };
            try
            {
                Assert.True(enteredWrite.Wait(TimeSpan.FromSeconds(5)));
                entrant.Start();
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref attemptingScope), TimeSpan.FromSeconds(5)));
                // The only blocking operation after the attempt signal is the
                // coordinator lock. A start signal alone would not prove that
                // this thread actually reached the contended operation.
                Assert.True(SpinWait.SpinUntil(() => acquiredScope.IsSet
                    || (entrant.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)));
                Assert.False(acquiredScope.IsSet);
                finishWrite.Set();
                await writer;
                Assert.True(acquiredScope.Wait(TimeSpan.FromSeconds(5)));
                Assert.True(scan.VerifyDisabled(out _));
                Assert.False(rig.Enabled[0]);
            }
            finally
            {
                finishWrite.Set();
                await writer;
                if ((entrant.ThreadState & ThreadState.Unstarted) == 0)
                    Assert.True(entrant.Join(TimeSpan.FromSeconds(5)));
                scan?.Dispose();
            }
        }
    }
}
