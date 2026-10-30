using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit.Abstractions;

namespace PadForge.Tests;

/// <summary>
/// Runs beside no other collection. AudioProfileLifecycleTests replaces the
/// settings every class of the SettingsManagerStatics collection reads (the
/// device and setting lists, the slot arrays and orders, the profiles), and
/// the InputService it builds reaches HidHideController's statics when it is
/// disposed: a fake driver a hiding test had put in HidHideController.IoSeam
/// would be read, and written with entries in the managed set. It also shuts
/// down the one AudioPassthroughService the other classes of this collection
/// use. DisableParallelization keeps it apart from all of them: xunit runs
/// such a collection after the parallel ones have ended, and its classes one
/// at a time. Membership in SettingsManagerStatics would keep it from the
/// hiding tests alone and let it run beside the audio classes here.
/// </summary>
[CollectionDefinition("AudioProfileLifecycle", DisableParallelization = true)]
public sealed class AudioProfileLifecycleCollection { }

/// <summary>The collection above is what keeps
/// <see cref="AudioProfileLifecycleTests"/> away from the statics it shares.
/// A change that drops the setting, or moves the class to a collection other
/// classes run beside, fails here.</summary>
public sealed class AudioProfileLifecycleIsolationTests
{
    [Fact]
    public void TheClassRunsInACollectionNoOtherRunsBeside()
    {
        var collection = Assert.Single(typeof(AudioProfileLifecycleTests).GetCustomAttributesData(),
            data => data.AttributeType == typeof(CollectionAttribute));
        Assert.Equal("AudioProfileLifecycle", collection.ConstructorArguments[0].Value);

        var definition = Assert.Single(typeof(AudioProfileLifecycleCollection).GetCustomAttributesData(),
            data => data.AttributeType == typeof(CollectionDefinitionAttribute));
        Assert.Equal("AudioProfileLifecycle", definition.ConstructorArguments[0].Value);
        Assert.True(typeof(AudioProfileLifecycleCollection).GetCustomAttribute<CollectionDefinitionAttribute>()!.DisableParallelization);

        // The hiding tests are in a collection of their own, which has no
        // definition and so runs with the parallel ones, before this one.
        Assert.DoesNotContain(typeof(AudioProfileLifecycleTests).Assembly.GetTypes(),
            type => type.GetCustomAttributesData().Any(data => data.AttributeType == typeof(CollectionDefinitionAttribute)
                && Equals(data.ConstructorArguments[0].Value, "SettingsManagerStatics")));
        foreach (var hiding in new[] { typeof(HidHideSelfAccessTests), typeof(HidHideOffLimitsTests), typeof(HidHideDiagnosticsTests) })
            Assert.Equal("SettingsManagerStatics", Assert.Single(hiding.GetCustomAttributesData(),
                data => data.AttributeType == typeof(CollectionAttribute)).ConstructorArguments[0].Value);
    }
}

[Collection("AudioProfileLifecycle")]
public sealed class AudioProfileLifecycleTests
{
    readonly ITestOutputHelper output;
    public AudioProfileLifecycleTests(ITestOutputHelper output) => this.output = output;
    static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(false, VirtualControllerType.Xbox)]
    [InlineData(false, VirtualControllerType.Extended)]
    [InlineData(false, VirtualControllerType.KeyboardMouse)]
    [InlineData(false, VirtualControllerType.Midi)]
    [InlineData(false, VirtualControllerType.Nintendo)]
    [InlineData(false, VirtualControllerType.Vr)]
    [InlineData(true, VirtualControllerType.Xbox)]
    public void ProfileSwitchPreservesTheMirrorAndToggleControlStillStreams(bool typeOnly, VirtualControllerType targetType)
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try { RunProfileTransition(typeOnly, targetType); } catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Audio transition probe did not finish.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    void RunProfileTransition(bool typeOnly, VirtualControllerType targetType)
    {
        // No hiding test runs beside this class, so none has a fake driver in
        // place for the InputService below to reach when it is disposed.
        Assert.Null(PadForge.Common.HidHideController.IoSeam);
        using var stateScope = new StaticStateScope();
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        var oldSets = SettingsManager.SlotMappingSets;
        var oldCreated = SettingsManager.SlotCreated;
        var oldEnabled = SettingsManager.SlotEnabled;
        var oldAfter = SettingsService.AfterMappingSetsRefreshed;
        var oldProvider = AudioPassthroughService.PassthroughConfigProvider;
        var oldMacroDemand = AudioPassthroughService.SlotWantsMacroAudioProvider;
        var oldSender = RemoteLinkOutputRouter.SendAudio;
        var id = Guid.NewGuid();
        const string sourceId = "detached-system-audio-source";
        string route = "peer://detached-audio/" + id;
        var capture = new AudioPassthroughService.CaptureEntry { EndpointId = sourceId, Write = 4800 };
        Array.Fill(capture.Ring, 0.25f);
        var audioType = typeof(AudioPassthroughService);
        object gate = audioType.GetField("_lock", StaticPrivate)!.GetValue(null);
        var captures = (IDictionary)audioType.GetField("_captures", StaticPrivate)!.GetValue(null);
        var runningField = audioType.GetField("_running", StaticPrivate)!;
        var sinks = (IDictionary)audioType.GetField("_sinks", StaticPrivate)!.GetValue(null);
        int blocks = 0;
        Timer producer = null;
        UserEffectsDispatcher effects = null;
        InputService input = null;
        try
        {
            AudioPassthroughService.Shutdown();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = Enumerable.Range(0, 16).Select(_ => new MappingSet()).ToArray();
            SettingsManager.SlotCreated = new bool[16];
            SettingsManager.SlotEnabled = new bool[16];
            SettingsManager.SlotCreated[0] = SettingsManager.SlotEnabled[0] = true;
            var device = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, ProductName = "Detached Sony audio",
                InstanceName = "Detached Sony audio", VendorId = 0x054c, ProdId = 0x0ce6,
                CapType = InputDeviceType.Gamepad, IsOnline = true, IsEnabled = true, DevicePath = route
            };
            SettingsManager.UserDevices.Items.Add(device);
            var ps = new PadSetting();
            ps.UpdateChecksum();
            var us = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = 0, IsEnabled = true };
            us.SetPadSetting(ps);
            SettingsManager.UserSettings.Items.Add(us);
            var vm = new MainViewModel();
            var settings = new SettingsService(vm);
            input = new InputService(vm) { SettingsService = settings };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            pad.ProfileId = "dualsense-composite";
            var mapped = new PadViewModel.MappedDeviceInfo { InstanceGuid = id, Name = "Detached Sony audio", IsOnline = true };
            pad.MappedDevices.Add(mapped);
            pad.SelectedMappedDevice = mapped;
            pad.DeviceConfig.LightbarMode = LightbarMode.Off;
            pad.DeviceConfig.AudioPassthroughEnabled = true;
            pad.DeviceConfig.AudioMirrorSourceId = sourceId;
            AudioPassthroughService.PassthroughConfigProvider = slot =>
                slot == 0 ? pad.PerDeviceSlotConfigs.Select(kv => (kv.Key, kv.Value.AudioPassthroughEnabled, kv.Value.AudioMirrorSourceId)).ToArray()
                    : Array.Empty<(Guid, bool, string)>();
            AudioPassthroughService.SlotWantsMacroAudioProvider = _ => false;
            RemoteLinkOutputRouter.Register(route, "detached-audio-peer", 0);
            RemoteLinkOutputRouter.SendAudio = (_, _, pcm) =>
            {
                if (pcm.Any(b => b != 0)) Interlocked.Increment(ref blocks);
            };
            producer = new Timer(_ => { lock (capture.Ring) capture.Write += 480; }, null, 0, 10);
            lock (gate) captures[sourceId] = capture;
            effects = new UserEffectsDispatcher(0, pad.DeviceConfig, startTimer: false);
            AudioPassthroughService.Reconcile();
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref blocks) >= 3, 5000), "Initial controlled PCM did not stream.");
            int initial = Volatile.Read(ref blocks);
            output.WriteLine("initial nonzero PCM blocks=" + initial);

            SoundMacroService.StopAll();
            Assert.True((bool)runningField.GetValue(null), "Replacing macro sounds stopped the system mirror.");

            var profile = new ProfileData
            {
                Name = "Detached Xbox profile", Macros = Array.Empty<MacroData>(),
                SlotCreated = (bool[])SettingsManager.SlotCreated.Clone(),
                SlotEnabled = (bool[])SettingsManager.SlotEnabled.Clone(),
                SlotControllerTypes = Enumerable.Repeat((int)targetType, 16).ToArray(),
                SlotProfileIds = Enumerable.Range(0, 16).Select(i => i == 0 && targetType == VirtualControllerType.Xbox ? "xbox-360-wired" : null).ToArray(),
                SlotMappingSets = Enumerable.Range(0, 16).Select(_ => new MappingSet()).ToArray(),
                PadSettings = [ps.CloneDeep()],
                Entries = [new ProfileEntry { InstanceGuid = id, ProductGuid = id, MapTo = 0, PadSettingChecksum = ps.PadSettingChecksum }],
                DeviceSlotConfigs = settings.BuildDeviceConfigSnapshot(),
            };
            if (typeOnly)
            {
                var window = (PadForge.MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PadForge.MainWindow));
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(PadForge.MainWindow).GetField("_viewModel", fields)!.SetValue(window, vm);
                typeof(PadForge.MainWindow).GetField("_settingsService", fields)!.SetValue(window, settings);
                typeof(PadForge.MainWindow).GetField("_inputService", fields)!.SetValue(window, input);
                typeof(PadForge.MainWindow).GetMethod("ChangeSlotType", fields)!.Invoke(window,
                    [0, VirtualControllerType.Xbox]);
            }
            else input.ApplyProfile(profile);
            effects.Dispose();
            effects = new UserEffectsDispatcher(0, pad.DeviceConfig, startTimer: false);
            Assert.True(pad.DeviceConfig.AudioPassthroughEnabled, "The incoming profile lost its mirror preference.");
            bool runningAfterProfile;
            int sinkCountAfterProfile;
            lock (gate)
            {
                runningAfterProfile = (bool)runningField.GetValue(null);
                sinkCountAfterProfile = sinks.Count;
                // The controlled source remains available for both the silent
                // observation and the subsequent toggle. This does not arm a worker.
                captures[sourceId] = capture;
            }
            int afterSwitch = Volatile.Read(ref blocks);
            Thread.Sleep(200);
            int afterQuietWindow = Volatile.Read(ref blocks);
            output.WriteLine($"after transition target={targetType} typeOnly={typeOnly} running={runningAfterProfile} sinks={sinkCountAfterProfile} nonzeroBlocks={afterQuietWindow - afterSwitch}");

            lock (SettingsManager.UserDevices.SyncRoot)
            lock (SettingsManager.UserSettings.SyncRoot)
            lock (gate)
            {
                pad.DeviceConfig.AudioPassthroughEnabled = false;
                pad.DeviceConfig.AudioPassthroughEnabled = true;
            }
            bool toggleStreams = SpinWait.SpinUntil(() => Volatile.Read(ref blocks) > afterQuietWindow + 2, 5000);
            bool runningAfterToggle = (bool)runningField.GetValue(null);
            output.WriteLine($"after toggle running={runningAfterToggle} nonzeroBlocks={Volatile.Read(ref blocks) - afterQuietWindow}");
            Assert.True(toggleStreams && runningAfterToggle, "Same-window toggle control did not restore controlled PCM.");
            Assert.True(runningAfterProfile && sinkCountAfterProfile > 0 && afterQuietWindow > afterSwitch,
                "Profile application stopped an enabled mirror although the same-window toggle restored it.");

            using var engine = new InputManager();
            typeof(InputManager).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, true);
            typeof(InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(input, engine);
            AudioPassthroughService.Shutdown();
            Assert.False((bool)runningField.GetValue(null));
            input.ApplyProfile(profile);
            Assert.True((bool)runningField.GetValue(null), "A running engine did not arm the incoming mirror.");
            engine.Stop();
            Assert.False((bool)runningField.GetValue(null));
            lock (gate) Assert.Empty(sinks);
            input.ApplyProfile(profile);
            Assert.False((bool)runningField.GetValue(null));
            typeof(InputService).GetField("_inputManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(input, null);
        }
        finally
        {
            if (producer != null)
            {
                using var drained = new ManualResetEvent(false);
                producer.Dispose(drained);
                Assert.True(drained.WaitOne(5000));
            }
            effects?.Dispose();
            AudioPassthroughService.Shutdown();
            foreach (string field in new[] { "_btThread", "_workerThread" })
                (audioType.GetField(field, StaticPrivate)!.GetValue(null) as Thread)?.Join(2000);
            if (input != null)
            {
                PadForge.Resources.Strings.Strings.CultureChanged -=
                    (Action)Delegate.CreateDelegate(typeof(Action), input,
                        typeof(InputService).GetMethod("OnCultureChanged", BindingFlags.Instance | BindingFlags.NonPublic)!);
                input.Dispose();
            }
            RemoteLinkOutputRouter.Unregister(route);
            RemoteLinkOutputRouter.SendAudio = oldSender;
            AudioPassthroughService.PassthroughConfigProvider = oldProvider;
            AudioPassthroughService.SlotWantsMacroAudioProvider = oldMacroDemand;
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
            SettingsManager.SlotMappingSets = oldSets;
            SettingsManager.SlotCreated = oldCreated;
            SettingsManager.SlotEnabled = oldEnabled;
            SettingsService.AfterMappingSetsRefreshed = oldAfter;
        }
    }

    sealed class StaticStateScope : IDisposable
    {
        readonly List<(PropertyInfo Property, object Value)> properties = new();
        readonly List<(FieldInfo Field, object Value)> callbacks = new();
        public StaticStateScope()
        {
            foreach (string name in new[] { "XboxSlotOrder", "PlayStationSlotOrder", "ExtendedSlotOrder",
                "KeyboardMouseSlotOrder", "MidiSlotOrder", "NintendoSlotOrder", "VrSlotOrder",
                "Profiles", "ActiveProfileId", "PendingDefaultSnapshot" })
            {
                var property = typeof(SettingsManager).GetProperty(name)!;
                properties.Add((property, property.GetValue(null)));
                if (property.PropertyType == typeof(List<int>)) property.SetValue(null, new List<int>());
            }
            foreach (var type in new[] { typeof(PsMoveDirectService), typeof(Ds3DirectService),
                typeof(PadForge.Resources.Strings.Strings) })
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (!field.IsInitOnly && typeof(Delegate).IsAssignableFrom(field.FieldType))
                        callbacks.Add((field, field.GetValue(null)));
        }
        public void Dispose()
        {
            foreach (var (property, value) in properties) property.SetValue(null, value);
            foreach (var (field, value) in callbacks) field.SetValue(null, value);
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
        }
    }
}
