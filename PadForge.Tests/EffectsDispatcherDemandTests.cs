using System.Collections;
using System.IO;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class EffectsDispatcherDemandTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static void Call(UserEffectsDispatcher d, string name) =>
        typeof(UserEffectsDispatcher).GetMethod(name, Instance)!.Invoke(d, name == "OnAnimTickCore" ? [null] : null);
    static bool Active(UserEffectsDispatcher d) =>
        (bool)typeof(UserEffectsDispatcher).GetField("_animTickActive", Instance)!.GetValue(d);
    static void ParkTimer(UserEffectsDispatcher d) =>
        (typeof(UserEffectsDispatcher).GetField("_animTimer", Instance)!.GetValue(d) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);

    sealed class Scope : IDisposable
    {
        readonly DeviceCollection devices = SettingsManager.UserDevices;
        readonly SettingsCollection settings = SettingsManager.UserSettings;
        readonly Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> configs = UserEffectsDispatcher.SlotPerDeviceConfigsProvider;
        readonly Func<float> peak = UserEffectsDispatcher.AudioPeakProvider;
        readonly Func<int, Guid> target = UserEffectsDispatcher.TestRumbleTargetGuidProvider;
        readonly Func<int, (byte right, byte left)> rumble = UserEffectsDispatcher.SlotRawRumbleProvider;
        public int Dispatches;
        public Scope()
        {
            AudioPassthroughService.Shutdown();
            foreach (string name in new[] { "_workerThread", "_btThread" })
                if (typeof(AudioPassthroughService).GetField(name, Static)!.GetValue(null) is Thread t && t.IsAlive)
                    Assert.True(t.Join(5000));
            SettingsManager.UserDevices = new();
            SettingsManager.UserSettings = new();
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = null;
            UserEffectsDispatcher.AudioPeakProvider = () => .25f;
            UserEffectsDispatcher.SlotRawRumbleProvider = _ => (0, 0);
            UserEffectsDispatcher.TestRumbleTargetGuidProvider = _ => { Dispatches++; return Guid.Empty; };
        }
        public void Dispose()
        {
            UserEffectsDispatcher.SlotPerDeviceConfigsProvider = configs;
            UserEffectsDispatcher.AudioPeakProvider = peak;
            UserEffectsDispatcher.TestRumbleTargetGuidProvider = target;
            UserEffectsDispatcher.SlotRawRumbleProvider = rumble;
            SettingsManager.UserDevices = devices;
            SettingsManager.UserSettings = settings;
        }
    }

    [Theory]
    [InlineData(LightbarMode.Off)]
    [InlineData(LightbarMode.AudioPulse)]
    public void LiveSpeakerRoutingSurvivesTicksAndIdleRecheck(LightbarMode mode)
    {
        using var scope = new Scope();
        const int slot = 15;
        var id = Guid.NewGuid();
        var audioType = typeof(AudioPassthroughService);
        var sinkType = audioType.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(sinkType, true)!;
        sinkType.GetField("Slot")!.SetValue(sink, slot);
        sinkType.GetField("DeviceGuid")!.SetValue(sink, id);
        sinkType.GetField("BtHandle")!.SetValue(sink, new IntPtr(1));
        var sinks = (IDictionary)audioType.GetField("_sinks", Static)!.GetValue(null);
        var gate = audioType.GetField("_lock", Static)!.GetValue(null);
        var config = new DeviceSlotConfig { LightbarMode = mode };
        using var dispatcher = new UserEffectsDispatcher(slot, config, startTimer: false);
        lock (gate) sinks.Add(id, sink);
        try
        {
            Assert.True(AudioPassthroughService.SlotWantsSpeakerPath(slot));
            Call(dispatcher, "UpdateAnimTimer");
            ParkTimer(dispatcher);
            Assert.True(Active(dispatcher));
            scope.Dispatches = 0;
            Call(dispatcher, "OnAnimTickCore");
            Call(dispatcher, "OnAnimTickCore");
            Assert.Equal(2, scope.Dispatches);
            Assert.True(Active(dispatcher));
            Call(dispatcher, "StopAnimTimerIfStillIdle");
            Assert.True(Active(dispatcher));
            lock (gate) sinks.Remove(id);
            config.LightbarMode = LightbarMode.Off;
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(Active(dispatcher));
            dispatcher.Dispose();
            Call(dispatcher, "UpdateAnimTimer");
            Assert.False(Active(dispatcher));
        }
        finally { lock (gate) sinks.Remove(id); }
    }

    [Theory]
    [InlineData(LightbarMode.AudioPulse, 1)]
    [InlineData(LightbarMode.AudioPulseRandom, 1)]
    [InlineData(LightbarMode.AudioThresholds, 1)]
    [InlineData(LightbarMode.AudioGradient, 1)]
    [InlineData(LightbarMode.AudioCrossFade, 1)]
    [InlineData(LightbarMode.AudioPulseRainbow, 2)]
    [InlineData(LightbarMode.Rainbow, 2)]
    [InlineData(LightbarMode.Breathing, 2)]
    [InlineData(LightbarMode.Strobe, 2)]
    [InlineData(LightbarMode.ColorCycle, 2)]
    [InlineData(LightbarMode.ScreenColor, 2)]
    [InlineData(LightbarMode.InputReactive, 2)]
    [InlineData(LightbarMode.InputReactiveCycle, 2)]
    [InlineData(LightbarMode.InputReactiveFixed, 2)]
    public void FlatAudioPreservesEveryOtherDevicesAnimation(LightbarMode otherMode, int expected)
    {
        using var scope = new Scope();
        var audio = new DeviceSlotConfig { LightbarMode = LightbarMode.AudioPulse };
        var other = new DeviceSlotConfig { LightbarMode = otherMode };
        UserEffectsDispatcher.SlotPerDeviceConfigsProvider = _ =>
            new Dictionary<Guid, DeviceSlotConfig> { [Guid.Empty] = audio, [new Guid("00000000-0000-0000-0000-000000000001")] = other };
        using var dispatcher = new UserEffectsDispatcher(15, audio, startTimer: false);
        Call(dispatcher, "OnAnimTickCore");
        Assert.Equal(1, scope.Dispatches);
        Call(dispatcher, "OnAnimTickCore");
        Assert.Equal(expected, scope.Dispatches);
    }

    static readonly KeyColorTarget[] OneKey = { new(0x1E, null) };
    static readonly byte[] TriggerBlock = new byte[11];

    /// <summary>A macro's timed key color fades, counts down and ends on the
    /// writer the store keeps alive. With nothing else animating, each tick
    /// repaints the slot's lit peripherals and writes no Sony report, and on
    /// a flat audio peak it does the same.</summary>
    [Theory]
    [InlineData(LightbarMode.Off)]
    [InlineData(LightbarMode.AudioPulse)]
    public void ATimedKeyColor_RepaintsOnEveryTick_AndEndsWithNothingElseAnimating(LightbarMode mode)
    {
        using var scope = new Scope();
        var saved = UserEffectsDispatcher.MacroKeyColorsProvider;
        var store = new MacroKeyColorState(15);
        UserEffectsDispatcher.MacroKeyColorsProvider = _ => store;
        string dir = Path.Combine(Path.GetTempPath(), "PadForge-KeyColorTick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "BTHENUM-report.bin");
        File.WriteAllBytes(file, []);
        var id = Guid.NewGuid();
        SettingsManager.UserDevices.Items.Add(new UserDevice
        {
            InstanceGuid = id, VendorId = 0x054c, ProdId = 0x0ce6, IsOnline = true, IsEnabled = true, DevicePath = file,
        });
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = id, MapTo = 15 });
        var dispatcher = new UserEffectsDispatcher(15, new DeviceSlotConfig { LightbarMode = mode }, startTimer: false);
        try
        {
            long now = Environment.TickCount64;
            store.Play(new object(), OneKey, 0xFF0000, MacroKeyColorStyle.Fade, MacroKeyColorRest.Keep, 0, now, now + 60_000, null);
            Call(dispatcher, "UpdateAnimTimer");
            ParkTimer(dispatcher);
            Assert.True(Active(dispatcher));
            // Each pass counts what it lit, and this slot's row is a Sony pad.
            void TickWithALitPeripheral()
            {
                typeof(UserEffectsDispatcher).GetField("_litPeripherals", Instance)!.SetValue(dispatcher, 1);
                Call(dispatcher, "OnAnimTickCore");
            }

            // A first tick on an audio mode dispatches for its peak.
            TickWithALitPeripheral();
            File.WriteAllBytes(file, []);
            int visits = dispatcher.FullLaneVisits;
            scope.Dispatches = 0;
            TickWithALitPeripheral();
            TickWithALitPeripheral();
            Assert.Equal(2, scope.Dispatches);
            Assert.Equal(visits, dispatcher.FullLaneVisits);
            Assert.Empty(File.ReadAllBytes(file));
            Assert.True(store.AnyTimed);
            Assert.True(Active(dispatcher));

            store.Clear();
            store.Play(new object(), OneKey, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, now - 2000, now - 1000, null);
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(store.Any);
            Assert.False(store.AnyTimed);
        }
        finally
        {
            dispatcher.Dispose();
            UserEffectsDispatcher.MacroKeyColorsProvider = saved;
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A slot that lights no peripheral paints nothing, so its timed
    /// key color still has to end, or the store keeps the writer ticking
    /// for the rest of the run.</summary>
    [Fact]
    public void ATimedKeyColor_EndsOnASlotThatLightsNothing_AndLetsTheWriterStop()
    {
        using var scope = new Scope();
        var saved = UserEffectsDispatcher.MacroKeyColorsProvider;
        var store = new MacroKeyColorState(15);
        UserEffectsDispatcher.MacroKeyColorsProvider = _ => store;
        using var dispatcher = new UserEffectsDispatcher(15, new DeviceSlotConfig { LightbarMode = LightbarMode.Off }, startTimer: false);
        try
        {
            long now = Environment.TickCount64;
            store.Play(new object(), OneKey, 0xFF0000, MacroKeyColorStyle.Steady, MacroKeyColorRest.Keep, 0, now - 2000, now - 1000, null);
            Call(dispatcher, "UpdateAnimTimer");
            ParkTimer(dispatcher);
            Assert.True(Active(dispatcher));
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(store.Any);
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(Active(dispatcher));
        }
        finally { UserEffectsDispatcher.MacroKeyColorsProvider = saved; }
    }

    /// <summary>The same for a macro's timed trigger effect: its end wakes the
    /// dispatcher that writes the Off, on a slot with nothing else animating
    /// and on one with no pad left.</summary>
    [Fact]
    public void ATimedTriggerEffect_EndsOnTheTick_AndWakesTheWriter()
    {
        using var scope = new Scope();
        var saved = UserEffectsDispatcher.MacroTriggerEffectsProvider;
        var savedChanged = MacroTriggerEffectState.Changed;
        var store = new MacroTriggerEffectState(15);
        UserEffectsDispatcher.MacroTriggerEffectsProvider = _ => store;
        int wakes = 0;
        using var dispatcher = new UserEffectsDispatcher(15, new DeviceSlotConfig { LightbarMode = LightbarMode.Off }, startTimer: false);
        try
        {
            store.Play(TriggerBlock, MacroTriggerEffectSide.Right, Environment.TickCount64 - 1, null);
            MacroTriggerEffectState.Changed = _ => wakes++;
            Call(dispatcher, "UpdateAnimTimer");
            ParkTimer(dispatcher);
            Call(dispatcher, "OnAnimTickCore");
            Assert.False(store.AnyTimed);
            Assert.False(store.Any);
            Assert.Equal(1, wakes);
        }
        finally
        {
            UserEffectsDispatcher.MacroTriggerEffectsProvider = saved;
            MacroTriggerEffectState.Changed = savedChanged;
        }
    }

    /// <summary>A pad write can take a second and more, so the poll thread
    /// never runs one: the zero write after a game's rumble stops, and a
    /// battery change's refresh, each go to the thread pool, and the write
    /// still comes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePollThreadQueuesItsPasses_AndTheyStillRun(bool battery)
    {
        using var scope = new Scope();
        using var dispatcher = new UserEffectsDispatcher(15, new DeviceSlotConfig { LightbarMode = LightbarMode.Off }, startTimer: false);
        if (!battery)
        {
            UserEffectsDispatcher.OnPollingTick(15, true, false);
            ParkTimer(dispatcher);
            Assert.True(Active(dispatcher));
        }
        int poll = Environment.CurrentManagedThreadId, passThread = 0;
        using var ran = new ManualResetEventSlim();
        UserEffectsDispatcher.TestRumbleTargetGuidProvider = _ =>
        {
            Volatile.Write(ref passThread, Environment.CurrentManagedThreadId);
            ran.Set();
            return Guid.Empty;
        };
        if (battery) UserEffectsDispatcher.NotifyBatteryPercentChanged(15);
        else UserEffectsDispatcher.OnPollingTick(15, false, false);
        Assert.True(ran.Wait(5000));
        Assert.NotEqual(poll, Volatile.Read(ref passThread));
        Assert.False(Active(dispatcher));
    }
}
