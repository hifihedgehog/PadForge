using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class VcOemClaimTests
{
    private const ushort Vid = 0xCAFE;
    private const ushort Pid = 1;
    private const uint Key = ((uint)Vid << 16) | Pid;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(InputManager im, string name)
        => (T)typeof(InputManager).GetField(name, Private)!.GetValue(im)!;

    private static void DestroyAll(InputManager im)
        => typeof(InputManager).GetMethod("DestroyAllVirtualControllers", Private)!.Invoke(im, null);

    private sealed class Controller : IVirtualController
    {
        public VirtualControllerType Type => VirtualControllerType.KeyboardMouse;
        public bool IsConnected => true;
        public int FeedbackPadIndex { get; set; }
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void SubmitGamepadState(Gamepad state) { }
        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
    }

    [Fact]
    public async Task ConcurrentClaimsShareOneReferenceCountAndReleaseUnderLifecycleLock()
    {
        var im = new InputManager();
        object gate = Field<object>(im, "_vcLifecycleLock");
        int unlocked = 0, sets = 0, clears = 0;
        im.OemOverrideSet = (_, _, _) =>
        {
            if (!Monitor.IsEntered(gate)) Interlocked.Increment(ref unlocked);
            Interlocked.Increment(ref sets);
        };
        im.OemOverrideClear = (_, _) =>
        {
            if (!Monitor.IsEntered(gate)) Interlocked.Increment(ref unlocked);
            Interlocked.Increment(ref clears);
        };
        await Task.WhenAll(Enumerable.Range(0, InputManager.MaxPads)
            .Select(slot => Task.Run(() => im.TryAcquireOemOverrideClaim(slot, Vid, Pid, "Shared"))));
        Assert.Equal(InputManager.MaxPads, sets);
        Assert.Equal(0, unlocked);
        Assert.Equal(InputManager.MaxPads, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);
        DestroyAll(im);
        Assert.Equal(1, clears);
        Assert.Equal(0, unlocked);
        Assert.Empty(Field<Dictionary<uint, int>>(im, "_oemOverrideRefs"));
    }

    [Fact]
    public void PendingLoserCannotReplaceOrReleaseThePublishedControllersClaim()
    {
        var im = new InputManager();
        int clears = 0;
        im.OemOverrideSet = (_, _, _) => { };
        im.OemOverrideClear = (_, _) => clears++;
        var winner = new Controller();
        Assert.True(im.TryPublishCreatedController(0, winner, out _, out _));
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Winner");
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Pending", pending: true);
        Assert.Equal("Winner", Field<string[]>(im, "_lastAppliedOemLabel")[0]);
        Assert.Equal(2, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);

        Assert.True(im.TryPublishCreatedController(0, new Controller(), out var prior, out _));
        Assert.Same(winner, prior);
        im.ReleasePendingOemOverrideClaim(0);
        im.ReleasePendingOemOverrideClaim(0);
        Assert.Equal(0, clears);
        Assert.Equal(Key, Field<uint[]>(im, "_oemOverrideClaimedVidPid")[0]);
        Assert.Equal("Winner", Field<string[]>(im, "_lastAppliedOemLabel")[0]);
        Assert.Equal(1, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);
        DestroyAll(im);
        Assert.Equal(1, clears);
    }

    [Fact]
    public void WinningPublicationTransfersPendingClaimAndReleasesAnOrphan()
    {
        var im = new InputManager();
        var cleared = new List<ushort>();
        im.OemOverrideSet = (_, _, _) => { };
        im.OemOverrideClear = (_, pid) => cleared.Add(pid);
        im.TryAcquireOemOverrideClaim(0, Vid, 2, "Orphan");
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "New", pending: true);
        Assert.True(im.TryPublishCreatedController(0, new Controller(), out var prior, out _));
        Assert.Null(prior);
        Assert.Equal(new ushort[] { 2 }, cleared);
        Assert.Equal(Key, Field<uint[]>(im, "_oemOverrideClaimedVidPid")[0]);
        Assert.Equal("New", Field<string[]>(im, "_lastAppliedOemLabel")[0]);
        Assert.Equal(1, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);
        im.ReleasePendingOemOverrideClaim(0);
        Assert.Single(cleared);
        DestroyAll(im);
        Assert.Equal(new ushort[] { 2, 1 }, cleared);
    }

    [Fact]
    public void ClosingBeforeOrAfterAcquisitionLeavesNoPendingClaim()
    {
        var im = new InputManager();
        int sets = 0, clears = 0;
        im.OemOverrideSet = (_, _, _) => sets++;
        im.OemOverrideClear = (_, _) => clears++;
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Pending", pending: true);
        Assert.Equal(1, sets);
        Assert.Equal(1, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);
        DestroyAll(im);
        Assert.Equal(1, clears);
        Assert.Empty(Field<Dictionary<uint, int>>(im, "_oemOverrideRefs"));
        Assert.False(im.TryPublishCreatedController(0, new Controller(), out _, out _));
        im.ReleasePendingOemOverrideClaim(0);
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Too late", pending: true);
        Assert.Equal(1, sets);
        Assert.Equal(1, clears);
    }

    [Fact]
    public void OrdinaryTeardownDoesNotTakeAnInFlightWorkersClaim()
    {
        var im = new InputManager();
        int clears = 0;
        im.OemOverrideSet = (_, _, _) => { };
        im.OemOverrideClear = (_, _) => clears++;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Field<Task[]>(im, "_pendingConnectTask")[0] = pending.Task;
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Pending", pending: true);
        im.DestroyVirtualControllerAsync(0);
        Assert.Equal(0, clears);
        Assert.Equal(1, Field<Dictionary<uint, int>>(im, "_oemOverrideRefs")[Key]);
        pending.SetResult();
        im.ReleasePendingOemOverrideClaim(0);
        Assert.Equal(1, clears);
        DestroyAll(im);
        Assert.Equal(1, clears);
    }

    [Fact]
    public void AFailedNativeClaimDoesNotAcquireAReference()
    {
        var im = new InputManager();
        int sets = 0, clears = 0;
        im.OemOverrideSet = (_, _, _) => { sets++; throw new InvalidOperationException("claim probe"); };
        im.OemOverrideClear = (_, _) => clears++;
        im.TryAcquireOemOverrideClaim(0, Vid, Pid, "Pending", pending: true);
        Assert.Equal(1, sets);
        Assert.Empty(Field<Dictionary<uint, int>>(im, "_oemOverrideRefs"));
        im.ReleasePendingOemOverrideClaim(0);
        DestroyAll(im);
        Assert.Equal(0, clears);
    }
}
