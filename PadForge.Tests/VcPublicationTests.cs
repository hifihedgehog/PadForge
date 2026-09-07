using System;
using System.Collections.Concurrent;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public class VcPublicationTests
{
    private const int Pad = 14;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static T Field<T>(InputManager im, string name)
        => (T)typeof(InputManager).GetField(name, Private)!.GetValue(im)!;

    private static ConcurrentDictionary<int, UserEffectsDispatcher> Registry
        => (ConcurrentDictionary<int, UserEffectsDispatcher>)typeof(UserEffectsDispatcher)
            .GetField("_instances", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static void DestroyAll(InputManager im)
        => typeof(InputManager).GetMethod("DestroyAllVirtualControllers", Private)!.Invoke(im, null);

    private sealed class Controller : IVirtualController
    {
        public VirtualControllerType Type => VirtualControllerType.KeyboardMouse;
        public bool IsConnected => !Disposed;
        public bool Disposed;
        public int FeedbackPadIndex { get; set; }
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() => Disposed = true;
        public void SubmitGamepadState(Gamepad state) { }
        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates) { }
    }

    private static HMaestroVirtualController ConnectedManagedHm()
    {
        // Only managed effects paths run. No context, driver handle, or Connect call.
        var hm = (HMaestroVirtualController)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(HMaestroVirtualController));
        typeof(HMaestroVirtualController).GetField("_dispatcherLock", Private)!.SetValue(hm, new object());
        typeof(HMaestroVirtualController).GetField("_profile", Private)!.SetValue(hm,
            new HIDMaestro.HMProfileBuilder().Id("publication-probe").Vid(0xCAFE).Pid(1).Build());
        typeof(HMaestroVirtualController).GetField("_type", Private)!.SetValue(hm, VirtualControllerType.Extended);
        typeof(HMaestroVirtualController).GetProperty(nameof(HMaestroVirtualController.IsConnected))!.SetValue(hm, true);
        hm.FeedbackPadIndex = Pad;
        return hm;
    }

    [Fact]
    public void HmWinnerOwnsEffectsAndAnUnpublishedSpareCannotReplaceThem()
    {
        var im = new InputManager();
        im._deviceSlotConfigs[Pad] = new DeviceSlotConfig();
        using var winner = ConnectedManagedHm();
        using var spare = ConnectedManagedHm();
        try
        {
            Assert.True(im.TryPublishCreatedController(Pad, winner, out var prior, out var effects));
            Assert.Null(prior);
            Assert.NotNull(effects);
            Assert.Same(effects, Registry[Pad]);
            Assert.Null(Field<UserEffectsDispatcher[]>(im, "_nonHmDispatchers")[Pad]);
            Assert.True(im.TryPublishCreatedController(Pad, spare, out prior, out var spareEffects));
            Assert.Same(winner, prior);
            Assert.Null(spareEffects);
            spare.Dispose();
            Assert.Same(effects, Registry[Pad]);
            Assert.True(winner.IsConnected);
        }
        finally { DestroyAll(im); }
        Assert.False(Registry.ContainsKey(Pad));
    }

    [Fact]
    public void ClosedHmPublicationLeavesANewerManagersRegistrationIntact()
    {
        var closed = new InputManager();
        closed._deviceSlotConfigs[Pad] = new DeviceSlotConfig();
        DestroyAll(closed);
        using var newer = new UserEffectsDispatcher(Pad, new DeviceSlotConfig(), startTimer: false);
        using var late = ConnectedManagedHm();
        Assert.Same(newer, Registry[Pad]);
        Assert.False(closed.TryPublishCreatedController(Pad, late, out var prior, out var effects));
        Assert.Null(prior);
        Assert.Null(effects);
        late.Dispose();
        Assert.Same(newer, Registry[Pad]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FailedConstructorPreservesTheLatestLiveRegistration(int transition)
    {
        var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        using var previous = new UserEffectsDispatcher(Pad, new DeviceSlotConfig(), startTimer: false);
        UserEffectsDispatcher replacement = null;
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ =>
        {
            if (transition == 1) previous.Dispose();
            if (transition == 2) replacement = new UserEffectsDispatcher(Pad, new DeviceSlotConfig(), startTimer: false);
            throw new InvalidOperationException("constructor replacement probe");
        };
        try
        {
            Assert.Throws<InvalidOperationException>(() => new UserEffectsDispatcher(Pad, new DeviceSlotConfig()));
            if (transition == 0) Assert.Same(previous, Registry[Pad]);
            else if (transition == 1) Assert.False(Registry.ContainsKey(Pad));
            else Assert.Same(replacement, Registry[Pad]);
        }
        finally
        {
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
            replacement?.Dispose();
        }
    }

    [Fact]
    public void WinningPublicationOwnsEffects_LoserAndClosedPublicationLeaveRegistryAlone()
    {
        var im = new InputManager();
        im._deviceSlotConfigs[Pad] = new DeviceSlotConfig();
        var winner = new Controller();
        try
        {
            Assert.True(im.TryPublishCreatedController(Pad, winner, out var prior, out var effects));
            Assert.Null(prior);
            Assert.NotNull(effects);
            Assert.Same(winner, Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
            Assert.Same(effects, Registry[Pad]);
            Assert.Same(effects, Field<UserEffectsDispatcher[]>(im, "_nonHmDispatchers")[Pad]);

            Assert.True(im.TryPublishCreatedController(Pad, new Controller(), out prior, out var spareEffects));
            Assert.Same(winner, prior);
            Assert.Null(spareEffects);
            Assert.Same(effects, Registry[Pad]);

            DestroyAll(im);
            Assert.True(winner.Disposed);
            Assert.False(Registry.ContainsKey(Pad));
            using var replacement = new UserEffectsDispatcher(Pad, new DeviceSlotConfig(), startTimer: false);
            Assert.False(im.TryPublishCreatedController(Pad, new Controller(), out prior, out spareEffects));
            Assert.Null(prior);
            Assert.Null(spareEffects);
            Assert.Same(replacement, Registry[Pad]);

            // A delayed worker's initialization cannot resurrect its disposed timer.
            effects.StartDeferredEffects();
            Assert.False((bool)typeof(UserEffectsDispatcher).GetField("_animTickActive", Private)!.GetValue(effects)!);
        }
        finally { DestroyAll(im); }
    }

    [Fact]
    public void TeardownDisposesTrackedEffectsWithoutPublishedController()
    {
        var im = new InputManager();
        using var effects = new UserEffectsDispatcher(Pad, new DeviceSlotConfig(), startTimer: false);
        Field<UserEffectsDispatcher[]>(im, "_nonHmDispatchers")[Pad] = effects;
        Assert.Null(Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
        Assert.Same(effects, Registry[Pad]);
        DestroyAll(im);
        Assert.Null(Field<UserEffectsDispatcher[]>(im, "_nonHmDispatchers")[Pad]);
        Assert.False(Registry.ContainsKey(Pad));
    }

    [Fact]
    public void NoConfigPublishesWithoutEffects_AndPublicationDoesNotWalkProviders()
    {
        var im = new InputManager();
        var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        int reads = 0;
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => { reads++; throw new InvalidOperationException("provider probe"); };
        try
        {
            Assert.True(im.TryPublishCreatedController(Pad, new Controller(), out _, out var effects));
            Assert.Null(effects);
            Assert.False(Registry.ContainsKey(Pad));
            im.DestroyVirtualControllerAsync(Pad);
            im._deviceSlotConfigs[Pad] = new DeviceSlotConfig();
            Assert.True(im.TryPublishCreatedController(Pad, new Controller(), out _, out effects));
            Assert.NotNull(effects);
            Assert.Equal(0, reads);
            Assert.Throws<InvalidOperationException>(() => effects.StartDeferredEffects());
            Assert.Equal(1, reads);
        }
        finally
        {
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved;
            DestroyAll(im);
        }
    }

    [Fact]
    public void FailedConstructorRemovesItsRegistration()
    {
        var saved = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ => throw new InvalidOperationException("constructor probe");
        try
        {
            Assert.Throws<InvalidOperationException>(() => new UserEffectsDispatcher(Pad, new DeviceSlotConfig()));
            Assert.False(Registry.ContainsKey(Pad));
        }
        finally { UserEffectsDispatcher.SlotPerDeviceConfigsProvider = saved; }
    }
}
