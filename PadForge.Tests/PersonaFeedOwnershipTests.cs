using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro;
using NAudio.Wave;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests;

[Collection("SettingsManagerStatics")]
public sealed class PersonaFeedOwnershipTests : IDisposable
{
    private const int Pad = 14;
    private const int NextPad = 15;
    private const string Profile = "persona-owner-probe";
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private readonly SettingsCollection _settings = SettingsManager.UserSettings;
    private readonly DeviceCollection _devices = SettingsManager.UserDevices;
    private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
    private readonly bool[] _enabled = (bool[])SettingsManager.SlotEnabled.Clone();
    private readonly Func<string, IWaveIn> _captureFactory = AudioPassthroughService.PersonaMicCaptureFactory;
    private readonly Action _reconcile = AudioPassthroughService.PersonaReconcileRequest;
    private readonly Action<Action> _cleanupQueue = AudioPassthroughService.PersonaCleanupQueue;
    private readonly ConcurrentQueue<Action> _cleanupWork = new();
    private readonly List<InputManager> _managers = new();
    private int _wakeCount;

    public PersonaFeedOwnershipTests()
    {
        SettingsManager.UserSettings = new SettingsCollection();
        SettingsManager.UserDevices = new DeviceCollection();
        SettingsManager.SlotCreated[Pad] = SettingsManager.SlotCreated[NextPad] = true;
        SettingsManager.SlotEnabled[Pad] = SettingsManager.SlotEnabled[NextPad] = true;
        AudioPassthroughService.PersonaMicCaptureFactory = _ => null;
        AudioPassthroughService.PersonaReconcileRequest = () => Interlocked.Increment(ref _wakeCount);
        AudioPassthroughService.PersonaCleanupQueue = action => _cleanupWork.Enqueue(action);
    }

    public void Dispose()
    {
        foreach (var im in _managers) DestroyAll(im);
        RunCleanup();
        AudioPassthroughService.DrainRetiredPersonaFeeds();
        AudioPassthroughService.PersonaMicCaptureFactory = _captureFactory;
        AudioPassthroughService.PersonaReconcileRequest = _reconcile;
        AudioPassthroughService.PersonaCleanupQueue = _cleanupQueue;
        SettingsManager.UserSettings = _settings;
        SettingsManager.UserDevices = _devices;
        Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        Array.Copy(_enabled, SettingsManager.SlotEnabled, _enabled.Length);
    }

    private void RunCleanup()
    {
        while (_cleanupWork.TryDequeue(out var work)) work();
    }

    private static T Field<T>(object obj, string name)
        => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;

    private static void Backing(object obj, string name, object value)
        => obj.GetType().GetField("<" + name + ">k__BackingField", Private)!.SetValue(obj, value);

    private static void DestroyAll(InputManager im)
        => typeof(InputManager).GetMethod("DestroyAllVirtualControllers", Private)!.Invoke(im, null);

    private InputManager Manager(Func<IVirtualController, HMUsbAudio> provider)
    {
        var im = new InputManager { PersonaAudioProvider = provider };
        im.SlotProfileIds[Pad] = im.SlotProfileIds[NextPad] = Profile;
        _managers.Add(im);
        return im;
    }

    private static HMaestroVirtualController Controller(int slot)
    {
        // This supplies managed controller state only. Connect is never called.
        var vc = (HMaestroVirtualController)RuntimeHelpers.GetUninitializedObject(typeof(HMaestroVirtualController));
        typeof(HMaestroVirtualController).GetField("_dispatcherLock", Private)!.SetValue(vc, new object());
        typeof(HMaestroVirtualController).GetField("_profile", Private)!.SetValue(vc,
            new HMProfileBuilder().Id(Profile).Vid(0xCAFE).Pid(1).Build());
        typeof(HMaestroVirtualController).GetField("_type", Private)!.SetValue(vc, VirtualControllerType.Extended);
        typeof(HMaestroVirtualController).GetProperty(nameof(HMaestroVirtualController.IsConnected))!.SetValue(vc, true);
        vc.FeedbackPadIndex = slot;
        return vc;
    }

    private static HMUsbAudio Audio()
    {
        // SDK event fields and cached format metadata need no device backend.
        var audio = (HMUsbAudio)RuntimeHelpers.GetUninitializedObject(typeof(HMUsbAudio));
        var output = (HMAudioOutput)RuntimeHelpers.GetUninitializedObject(typeof(HMAudioOutput));
        Backing(output, nameof(HMAudioOutput.Channels), 4);
        Backing(output, nameof(HMAudioOutput.SampleRateHz), 48000);
        Backing(output, nameof(HMAudioOutput.BitsPerSample), 16);
        Backing(output, nameof(HMAudioOutput.ChannelRoles), new[] { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" });
        var mic = (HMMicrophoneInput)RuntimeHelpers.GetUninitializedObject(typeof(HMMicrophoneInput));
        Backing(mic, nameof(HMMicrophoneInput.Channels), 2);
        Backing(mic, nameof(HMMicrophoneInput.SampleRateHz), 48000);
        Backing(mic, nameof(HMMicrophoneInput.BitsPerSample), 16);
        Backing(audio, nameof(HMUsbAudio.Output), output);
        Backing(audio, nameof(HMUsbAudio.Microphone), mic);
        return audio;
    }

    private static void Emit(HMUsbAudio audio)
    {
        var handler = (Action<HMAudioOutput, ReadOnlyMemory<byte>>)typeof(HMAudioOutput)
            .GetField("FramesReceived", Private)!.GetValue(audio.Output);
        handler?.Invoke(audio.Output, new byte[] { 0, 32, 0, 16, 0, 64, 0, 192 });
    }

    private static ConcurrentDictionary<Guid, AudioPassthroughService.RemoteAudioRing> Speakers
        => (ConcurrentDictionary<Guid, AudioPassthroughService.RemoteAudioRing>)typeof(AudioPassthroughService)
            .GetField("_personaSpeakerRings", StaticPrivate)!.GetValue(null)!;
    private static ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing> Haptics
        => (ConcurrentDictionary<Guid, AudioPassthroughService.PersonaHapticRing>)typeof(AudioPassthroughService)
            .GetField("_personaHapticRings", StaticPrivate)!.GetValue(null)!;

    private static AudioPassthroughService.PersonaFeed Publish(InputManager im, int slot, IVirtualController vc)
    {
        Assert.True(im.TryPublishCreatedController(slot, vc, out var prior, out _, out var feed));
        Assert.Null(prior);
        Assert.NotNull(feed);
        Assert.Same(feed, AudioPassthroughService.SnapshotPersonaFeed(slot, out _));
        return feed;
    }

    private static void Route(AudioPassthroughService.PersonaFeed feed, Guid target)
        => AudioPassthroughService.RefreshPersonaTargets(feed, feed.Slot, feed.RouteGeneration,
            new List<(Guid, string, bool, bool)> { (target, "peer://managed-audio", true, true) });

    private static void AssertPcm(Guid target)
    {
        var speaker = new float[2];
        Speakers[target].ReadFloatAdd(speaker, 0, speaker.Length);
        Assert.Equal(new[] { 0.25f, 0.125f }, speaker);
        var haptic = new short[2];
        Assert.Equal(1, Haptics[target].ReadFrames(haptic, 1));
        Assert.Equal(new short[] { 16384, -16384 }, haptic);
    }

    private static void MakeActive(int slot)
    {
        var guid = Guid.NewGuid();
        SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = slot });
        SettingsManager.UserDevices.Items.Add(new UserDevice { InstanceGuid = guid, IsOnline = true });
    }

    [Fact]
    public void WinningPublicationCreatesDemandAndDeliversPcmWithoutMirrorOrMacros()
    {
        var audio = Audio();
        var im = Manager(_ => audio);
        var lifecycle = Field<object>(im, "_vcLifecycleLock");
        var target = Guid.NewGuid();
        using var vc = Controller(Pad);
        AudioPassthroughService.PersonaReconcileRequest = () =>
        {
            Assert.False(Monitor.IsEntered(lifecycle));
            _wakeCount++;
        };
        AudioPassthroughService.PersonaFeed feed;
        lock (lifecycle)
        {
            feed = Publish(im, Pad, vc);
            Assert.Equal(0, _wakeCount);
        }
        AudioPassthroughService.RequestPersonaReconcile(feed);
        Assert.Equal(1, _wakeCount);
        Route(feed, target);
        Emit(audio);
        AssertPcm(target);
    }

    [Fact]
    public void ClosedPublicationAndOldCleanupCannotReplaceANewerFeed()
    {
        var oldAudio = Audio();
        var old = Manager(_ => oldAudio);
        using var oldVc = Controller(Pad);
        var oldFeed = Publish(old, Pad, oldVc);
        var target = Guid.NewGuid();
        Route(oldFeed, target);
        Emit(oldAudio);
        AssertPcm(target);
        DestroyAll(old);
        var newerAudio = Audio();
        var newer = Manager(_ => newerAudio);
        using var newerVc = Controller(Pad);
        var current = Publish(newer, Pad, newerVc);
        Route(current, target);
        Emit(newerAudio);
        var ring = Speakers[target];
        old.PersonaAudioProvider = _ => throw new InvalidOperationException("A closed worker requested audio.");
        using var late = Controller(Pad);
        Assert.False(old.TryPublishCreatedController(Pad, late, out _, out _, out var rejected));
        Assert.Null(rejected);
        AudioPassthroughService.RetirePersonaFeed(oldFeed);
        AudioPassthroughService.DrainRetiredPersonaFeeds();
        Emit(oldAudio);
        Assert.Same(current, AudioPassthroughService.SnapshotPersonaFeed(Pad, out _));
        Assert.Same(ring, Speakers[target]);
        AssertPcm(target);
        Assert.Equal(0, Haptics[target].FramesAvailable);
    }

    [Fact]
    public void WinningReplacementDrainsOldCaptureBeforeStartingItsOwn()
    {
        var oldAudio = Audio();
        var old = Manager(_ => oldAudio);
        using var oldVc = Controller(Pad);
        var oldFeed = Publish(old, Pad, oldVc);
        var target = Guid.NewGuid();
        var oldCapture = new Capture();
        AudioPassthroughService.PersonaMicCaptureFactory = _ => oldCapture;
        AudioPassthroughService.RefreshPersonaTargets(oldFeed, Pad, oldFeed.RouteGeneration,
            new List<(Guid, string, bool, bool)> { (target, "old-capture", false, true) });
        Emit(oldAudio);
        AssertPcm(target);
        var speaker = Speakers[target];
        var haptic = Haptics[target];
        var newAudio = Audio();
        var newer = Manager(_ => newAudio);
        using var newVc = Controller(Pad);
        var current = Publish(newer, Pad, newVc);
        Assert.True(oldFeed.Retired);
        Assert.Equal(0, oldCapture.Stops);
        bool oldClosedBeforeNewStart = false;
        var newCapture = new Capture
        {
            OnStart = () => oldClosedBeforeNewStart = oldCapture.Stops == 1 && oldCapture.Disposals == 1,
        };
        AudioPassthroughService.PersonaMicCaptureFactory = _ => newCapture;
        AudioPassthroughService.RefreshPersonaTargets(current, Pad, current.RouteGeneration,
            new List<(Guid, string, bool, bool)> { (target, "new-capture", false, true) });
        Assert.True(oldClosedBeforeNewStart);
        Assert.Same(newCapture, current.Mic);
        Emit(newAudio);
        DestroyAll(old);
        RunCleanup();
        Emit(oldAudio);
        Assert.Same(current, AudioPassthroughService.SnapshotPersonaFeed(Pad, out _));
        Assert.Same(speaker, Speakers[target]);
        Assert.Same(haptic, Haptics[target]);
        AssertPcm(target);
        Assert.Equal(0, Haptics[target].FramesAvailable);
    }

    [Fact]
    public void ReorderLoserNeverRegistersAudio()
    {
        var audio = Audio();
        using var winner = Controller(Pad);
        using var loser = Controller(NextPad);
        int providerCalls = 0;
        var im = Manager(vc =>
        {
            providerCalls++;
            Assert.Same(winner, vc);
            return audio;
        });
        var feed = Publish(im, Pad, winner);
        MakeActive(NextPad);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Field<Task[]>(im, "_pendingConnectTask")[NextPad] = pending.Task;
        try
        {
            im.RerouteVirtualControllersForReorder(VirtualControllerType.Extended,
                new[] { Pad, NextPad }, new[] { NextPad, Pad });
            Assert.Same(winner, Field<IVirtualController[]>(im, "_virtualControllers")[NextPad]);
            Assert.True(im.TryPublishCreatedController(NextPad, loser, out var prior, out _, out var rejected));
            Assert.Same(winner, prior);
            Assert.Null(rejected);
            Assert.Equal(1, providerCalls);
            loser.Dispose();
            Assert.Same(feed, AudioPassthroughService.SnapshotPersonaFeed(NextPad, out _));
            var target = Guid.NewGuid();
            Route(feed, target);
            Emit(audio);
            AssertPcm(target);
        }
        finally { pending.TrySetResult(true); }
    }

    [Fact]
    public void ReorderKeepsBothBuffersUntilBothNewRoutesResolve()
    {
        var firstAudio = Audio();
        var secondAudio = Audio();
        using var first = Controller(Pad);
        using var second = Controller(NextPad);
        var im = Manager(vc => ReferenceEquals(vc, first) ? firstAudio : secondAudio);
        var a = Publish(im, Pad, first);
        var b = Publish(im, NextPad, second);
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        Route(a, left); Route(b, right);
        Emit(firstAudio); Emit(secondAudio);
        var leftSpeaker = Speakers[left]; var rightSpeaker = Speakers[right];
        var leftHaptic = Haptics[left]; var rightHaptic = Haptics[right];
        im.RerouteVirtualControllersForReorder(VirtualControllerType.Extended,
            new[] { Pad, NextPad }, new[] { NextPad, Pad });
        Assert.Same(first, Field<IVirtualController[]>(im, "_virtualControllers")[NextPad]);
        Assert.Same(second, Field<IVirtualController[]>(im, "_virtualControllers")[Pad]);
        Route(a, right);
        Assert.Same(leftSpeaker, Speakers[left]);
        Assert.Same(leftHaptic, Haptics[left]);
        Route(b, left);
        Assert.Same(rightSpeaker, Speakers[right]);
        Assert.Same(rightHaptic, Haptics[right]);
        AssertPcm(left); AssertPcm(right);
        Emit(firstAudio); Emit(secondAudio);
        AssertPcm(left); AssertPcm(right);
    }

    private sealed class Capture : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        public Action OnStart;
        public Action OnStop;
        public int Starts, Stops, Disposals;
        public void StartRecording() { Starts++; OnStart?.Invoke(); }
        public void StopRecording() { Stops++; OnStop?.Invoke(); }
        public void Dispose() { Disposals++; RecordingStopped?.Invoke(this, new StoppedEventArgs()); }
        public void Emit(byte[] data) => DataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("retire")]
    [InlineData("move")]
    public async Task CaptureStartupPublishesOnlyForTheCurrentTokenAndRoute(string transition)
    {
        var audio = Audio();
        var im = Manager(_ => audio);
        using var vc = Controller(Pad);
        var feed = Publish(im, Pad, vc);
        var lifecycle = Field<object>(im, "_vcLifecycleLock");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool nativeUnderPublication = false;
        bool startCompleted = false;
        var capture = new Capture
        {
            OnStart = () =>
            {
                nativeUnderPublication |= Monitor.IsEntered(lifecycle);
                entered.Set();
                startCompleted = release.Wait(TimeSpan.FromSeconds(5));
            },
            OnStop = () => nativeUnderPublication |= Monitor.IsEntered(lifecycle),
        };
        AudioPassthroughService.PersonaMicCaptureFactory = _ => capture;
        int generation = feed.RouteGeneration;
        var work = Task.Run(() => AudioPassthroughService.RefreshPersonaTargets(feed, Pad, generation,
            new List<(Guid, string, bool, bool)> { (Guid.NewGuid(), "managed-capture", false, true) }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (transition == "retire")
            {
                lock (lifecycle) AudioPassthroughService.RetirePersonaFeed(feed);
            }
            else if (transition == "move")
            {
                MakeActive(NextPad);
                im.RerouteVirtualControllersForReorder(VirtualControllerType.Extended,
                    new[] { Pad, NextPad }, new[] { NextPad, Pad });
            }
        }
        finally
        {
            release.Set();
            await work.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(startCompleted);
        Assert.False(nativeUnderPublication);
        Assert.Equal(1, capture.Starts);
        // Observe the commit before allowing queued retirement cleanup to heal it.
        if (transition != "current") Assert.Null(feed.Mic);
        RunCleanup();
        if (transition == "current")
        {
            Assert.Same(capture, feed.Mic);
            Assert.Equal(0, capture.Disposals);
            lock (lifecycle) AudioPassthroughService.RetirePersonaFeed(feed);
            Assert.Equal(0, capture.Stops);
            RunCleanup();
        }
        Assert.False(nativeUnderPublication);
        Assert.Null(feed.Mic);
        Assert.Equal(1, capture.Stops);
        Assert.Equal(1, capture.Disposals);
    }

    [Fact]
    public void ClosingAnOwnerRejectsDeferredWakeAndDrainsPublishedCapture()
    {
        var audio = Audio();
        var im = Manager(_ => audio);
        using var vc = Controller(Pad);
        var feed = Publish(im, Pad, vc);
        var capture = new Capture();
        AudioPassthroughService.PersonaMicCaptureFactory = _ => capture;
        AudioPassthroughService.RefreshPersonaTargets(feed, Pad, feed.RouteGeneration,
            new List<(Guid, string, bool, bool)> { (Guid.NewGuid(), "managed-capture", false, true) });
        Assert.Same(capture, feed.Mic);
        AudioPassthroughService.RequestPersonaReconcile(feed);
        Assert.Equal(1, _wakeCount);
        AudioPassthroughService.ClosePersonaOwner(feed.Owner);
        AudioPassthroughService.Shutdown();
        AudioPassthroughService.DrainRetiredPersonaFeeds();
        AudioPassthroughService.RequestPersonaReconcile(feed);
        Assert.Equal(1, _wakeCount);
        Assert.Equal(1, capture.Disposals);
        var next = Manager(_ => Audio());
        using var nextVc = Controller(Pad);
        var nextFeed = Publish(next, Pad, nextVc);
        AudioPassthroughService.RequestPersonaReconcile(nextFeed);
        Assert.Equal(2, _wakeCount);
    }

    [Fact]
    public void ProfileAudioShutdownKeepsTheLiveOwnerAndCapture()
    {
        var audio = Audio();
        var im = Manager(_ => audio);
        using var vc = Controller(Pad);
        var feed = Publish(im, Pad, vc);
        var capture = new Capture();
        int captureOpens = 0;
        AudioPassthroughService.PersonaMicCaptureFactory = _ => { captureOpens++; return capture; };
        var target = Guid.NewGuid();
        var pads = new List<(Guid, string, bool, bool)> { (target, "managed-capture", false, true) };
        AudioPassthroughService.RefreshPersonaTargets(feed, Pad, feed.RouteGeneration, pads);
        Emit(audio);
        AssertPcm(target);
        Assert.Equal(1, captureOpens);
        Assert.Same(capture, feed.Mic);

        // Profile applies stop the restartable audio service, not its VC owner.
        AudioPassthroughService.Shutdown();
        RunCleanup();
        Assert.False(feed.Owner.Closed);
        Assert.False(feed.Retired);
        Assert.Same(feed, AudioPassthroughService.SnapshotPersonaFeed(Pad, out _));
        Assert.Same(capture, feed.Mic);
        Assert.Equal(0, capture.Stops);
        Assert.Equal(0, capture.Disposals);

        AudioPassthroughService.RequestPersonaReconcile(feed);
        Assert.Equal(1, _wakeCount);
        AudioPassthroughService.RefreshPersonaTargets(feed, Pad, feed.RouteGeneration, pads);
        Assert.Same(capture, feed.Mic);
        Assert.Equal(1, captureOpens);
        Assert.Equal(1, capture.Starts);
        Assert.Equal(0, capture.Stops);
        Emit(audio);
        AssertPcm(target);
    }

    [Fact]
    public void NonCompositePublicationDoesNotCreateAudioDemand()
    {
        var im = Manager(_ => null);
        using var vc = Controller(Pad);
        Assert.True(im.TryPublishCreatedController(Pad, vc, out var prior, out _, out var feed));
        Assert.Null(prior); Assert.Null(feed);
        Assert.Null(AudioPassthroughService.SnapshotPersonaFeed(Pad, out _));
        AudioPassthroughService.RequestPersonaReconcile(feed);
        Assert.Equal(0, _wakeCount);
    }
}
