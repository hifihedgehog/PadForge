using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave.SampleProviders;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class HapticConnectionLifetimeTests : IDisposable
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static readonly Type Tone = typeof(HapticToneService);

    /// <summary>A tone this class sends lives longer than any of its tests
    /// runs. Each test sends one frame, or starts one test tone, and waits
    /// for the sink's stream thread to play it. That thread idles below
    /// normal priority and a new frame can make the sink rebuild first, so
    /// on a busy PC the thread first weighed a frame more than 250 ms after
    /// it was made, past the frame's life. The tone never played and the
    /// wait ran out with nothing wrong in the product. Held, a tone plays
    /// whenever the thread gets to it.</summary>
    const int HeldMs = 600_000;

    readonly Action restore;
    readonly object gate;
    readonly IList sinks;
    readonly string directory;
    readonly string pathA, pathB;
    readonly UserDevice device;
    readonly UserSetting setting;
    readonly ITestOutputHelper output;
    int opens;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder path, uint size, uint flags);

    public class Input : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args)
        {
            if (method.Name == "get_GamepadHandle") return IntPtr.Zero;
            if (method.Name == "Dispose") return null;
            throw new InvalidOperationException("Unexpected input call: " + method.Name);
        }
    }

    static ISdlInputDevice Connection() => DispatchProxy.Create<ISdlInputDevice, Input>();
    static T Field<T>(object sink, string field) => (T)sink.GetType().GetField(field)!.GetValue(sink)!;
    object OnlySink() { lock (gate) return Assert.Single(sinks.Cast<object>()); }
    static string OpenPath(object sink)
    {
        var text = new StringBuilder(1024);
        Assert.NotEqual(0u, GetFinalPathNameByHandle(Field<IntPtr>(sink, "Handle"), text, 1024, 0));
        string value = text.ToString();
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value.Substring(4) : value;
    }

    public HapticConnectionLifetimeTests(ITestOutputHelper output)
    {
        this.output = output;
        gate = Tone.GetField("_lock", Static)!.GetValue(null)!;
        sinks = (IList)Tone.GetField("_sinks", Static)!.GetValue(null)!;
        Assert.Empty(sinks);
        Assert.Null(Tone.GetField("_reconcileTimer", Static)!.GetValue(null));
        Assert.Equal(0, (int)Tone.GetField("_reconcileBusy", Static)!.GetValue(null)!);
        var oldSettings = SettingsManager.UserSettings;
        var oldDevices = SettingsManager.UserDevices;
        var oldMirror = AudioPassthroughService.PassthroughConfigProvider;
        var oldPersona = HapticToneService.PersonaHapticsProvider;
        var oldFilter = HapticToneService.ToneFilterProvider;
        var oldOpening = HapticToneService.RawTransportOpening;
        var oldReady = HapticToneService.RemoteToneDispatchReady;
        var oldFeature = HapticToneService.FeatureReportWriter;
        int oldLifetime = RemoteToneRequest.LifetimeMs;
        bool oldSuppressed = (bool)Tone.GetField("_suppressed", Static)!.GetValue(null)!;
        restore = () =>
        {
            SettingsManager.UserSettings = oldSettings;
            SettingsManager.UserDevices = oldDevices;
            AudioPassthroughService.PassthroughConfigProvider = oldMirror;
            HapticToneService.PersonaHapticsProvider = oldPersona;
            HapticToneService.ToneFilterProvider = oldFilter;
            HapticToneService.RawTransportOpening = oldOpening;
            HapticToneService.RemoteToneDispatchReady = oldReady;
            HapticToneService.FeatureReportWriter = oldFeature;
            RemoteToneRequest.LifetimeMs = oldLifetime;
            Tone.GetField("_suppressed", Static)!.SetValue(null, oldSuppressed);
        };
        directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForge-haptic-lifetime-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        pathA = System.IO.Path.Combine(directory, "first.bin");
        pathB = System.IO.Path.Combine(directory, "second.bin");
        System.IO.File.WriteAllBytes(pathA, [11, 22, 33]);
        System.IO.File.WriteAllBytes(pathB, [44, 55, 66]);
        SettingsManager.UserSettings = new SettingsCollection();
        SettingsManager.UserDevices = new DeviceCollection();
        device = new UserDevice
        {
            InstanceGuid = Guid.NewGuid(), ProductGuid = Guid.NewGuid(), Device = Connection(),
            DevicePath = pathA, VendorId = 0x28de, ProdId = 0x1205,
            IsOnline = true, IsEnabled = true, CapType = InputDeviceType.Gamepad
        };
        setting = new UserSetting { InstanceGuid = device.InstanceGuid, ProductGuid = device.ProductGuid, MapTo = 14, IsEnabled = true };
        SettingsManager.UserSettings.Items.Add(setting);
        SettingsManager.UserDevices.Items.Add(device);
        AudioPassthroughService.PassthroughConfigProvider = _ => Array.Empty<(Guid, bool, string)>();
        HapticToneService.PersonaHapticsProvider = (_, _) => (false, 100);
        HapticToneService.ToneFilterProvider = (_, _) => (0, 800);
        HapticToneService.RawTransportOpening = _ => Interlocked.Increment(ref opens);
        RemoteToneRequest.LifetimeMs = HeldMs;
        Tone.GetField("_suppressed", Static)!.SetValue(null, false);
    }

    public void Dispose()
    {
        HapticToneService.Shutdown();
        restore();
        Assert.Equal(new byte[] { 11, 22, 33 }, System.IO.File.ReadAllBytes(pathA));
        Assert.Equal(new byte[] { 44, 55, 66 }, System.IO.File.ReadAllBytes(pathB));
        System.IO.File.Delete(pathA);
        System.IO.File.Delete(pathB);
        System.IO.Directory.Delete(directory);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RawReconnectReplacesItsTransportEvenWhenThePathIsUnchanged(bool samePath, bool sameConnection)
    {
        HapticToneService.Reconcile();
        var first = OnlySink();
        Assert.True(Field<bool>(first, "Running"));
        Assert.Equal(pathA, OpenPath(first), ignoreCase: true);
        Assert.Equal(1, opens);
        var mixer = Field<MixingSampleProvider>(first, "MacroMixer");
        HapticToneService.Reconcile();
        Assert.Same(first, OnlySink());
        Assert.Equal(1, opens);
        lock (SettingsManager.UserDevices.SyncRoot)
        {
            if (!sameConnection) device.Device = Connection();
            device.DevicePath = samePath ? pathA : pathB;
        }
        HapticToneService.Reconcile();
        var second = OnlySink();
        bool replaced = !ReferenceEquals(first, second);
        bool priorStopped = !Field<bool>(first, "Running") && !Field<Thread>(first, "Thread").IsAlive;
        bool correctPath = string.Equals(device.DevicePath, OpenPath(second), StringComparison.OrdinalIgnoreCase);
        int reconnectOpens = opens;
        bool mixerPreserved = ReferenceEquals(mixer, Field<MixingSampleProvider>(second, "MacroMixer"));

        // The fresh-start path is the same-window native-open control.
        HapticToneService.Shutdown();
        Tone.GetField("_suppressed", Static)!.SetValue(null, false);
        HapticToneService.Reconcile();
        Assert.Equal(device.DevicePath, OpenPath(OnlySink()), ignoreCase: true);
        output.WriteLine($"samePath={samePath} sameConnection={sameConnection} unchangedControlOpens=1 reconnectOpens={reconnectOpens} replaced={replaced} priorStopped={priorStopped} correctPath={correctPath} freshStartControl=True");
        Assert.True(replaced);
        Assert.True(priorStopped);
        Assert.True(correctPath);
        Assert.True(mixerPreserved);
        Assert.Equal(2, reconnectOpens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredOpenCannotPublishItsPreparedHandle(bool disconnected)
    {
        HapticToneService.Reconcile();
        Assert.Equal(pathA, OpenPath(OnlySink()), ignoreCase: true);
        HapticToneService.Shutdown();
        Tone.GetField("_suppressed", Static)!.SetValue(null, false);
        var observer = HapticToneService.RawTransportOpening;
        using var opening = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool released = false;
        HapticToneService.RawTransportOpening = path =>
        {
            observer(path);
            opening.Set();
            released = release.Wait(5000);
        };
        var build = Task.Run(HapticToneService.Reconcile);
        try
        {
            Assert.True(opening.Wait(2000));
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                if (disconnected) device.IsOnline = false;
                else { device.Device = Connection(); device.DevicePath = pathB; }
            }
        }
        finally
        {
            release.Set();
            await build.WaitAsync(TimeSpan.FromSeconds(5));
            HapticToneService.RawTransportOpening = observer;
        }
        bool rejected;
        lock (gate) rejected = sinks.Count == 0;
        HapticToneService.Shutdown();
        device.IsOnline = true;
        device.Device = Connection();
        device.DevicePath = pathB;
        Tone.GetField("_suppressed", Static)!.SetValue(null, false);
        HapticToneService.Reconcile();
        Assert.Equal(pathB, OpenPath(OnlySink()), ignoreCase: true);
        output.WriteLine($"disconnected={disconnected} initialControl=True retiredOpenRejected={rejected} freshControl=True");
        Assert.True(released);
        Assert.True(rejected);
    }

    [Fact]
    public void PeerRefreshKeepsItsLogicalMixerWithoutOpeningAHidHandle()
    {
        device.DevicePath = "peer://first/controlled";
        HapticToneService.Reconcile();
        var first = OnlySink();
        var mixer = Field<MixingSampleProvider>(first, "MacroMixer");
        device.Device = Connection();
        device.DevicePath = "peer://second/controlled";
        HapticToneService.Reconcile();
        Assert.Same(first, OnlySink());
        Assert.Same(mixer, Field<MixingSampleProvider>(first, "MacroMixer"));
        Assert.Equal(device.DevicePath, Field<string>(first, "HidPath"));
        Assert.Equal(IntPtr.Zero, Field<IntPtr>(first, "Handle"));
        Assert.Equal(0, opens);
    }

    static void WaitRemoteBuild()
    {
        var queue = Tone.GetField("_remoteToneStarts", Static)!.GetValue(null)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var queueGate = queue.GetType().GetField("_gate", flags)!.GetValue(queue)!;
        var pending = (IDictionary)queue.GetType().GetField("_pending", flags)!.GetValue(queue)!;
        Assert.True(SpinWait.SpinUntil(() => { lock (queueGate) return pending.Count == 0; }, 5000));
    }

    [Fact]
    public void RemoteToneReconnectReplacesTheOwnersRawHandle()
    {
        SettingsManager.UserSettings.Items.Clear();
        HapticToneService.ApplyRemoteTone(device, 200, .5f);
        WaitRemoteBuild();
        var first = OnlySink();
        Assert.Equal(pathA, OpenPath(first), ignoreCase: true);
        device.Device = Connection();
        device.DevicePath = pathB;
        HapticToneService.ApplyRemoteTone(device, 220, .5f);
        WaitRemoteBuild();
        var second = OnlySink();
        bool replaced = !ReferenceEquals(first, second);
        bool correctPath = string.Equals(pathB, OpenPath(second), StringComparison.OrdinalIgnoreCase);
        HapticToneService.Shutdown();
        Tone.GetField("_suppressed", Static)!.SetValue(null, false);
        HapticToneService.ApplyRemoteTone(device, 220, .5f);
        WaitRemoteBuild();
        Assert.Equal(pathB, OpenPath(OnlySink()), ignoreCase: true);
        output.WriteLine($"remoteInitialControl=True replaced={replaced} correctPath={correctPath} remoteFreshControl=True");
        Assert.True(replaced);
        Assert.True(correctPath);
    }

    [Theory]
    [InlineData("Steam", 1, "Steam", 2, true)]
    [InlineData("Steam", 1, "Steam", 0, false)]
    [InlineData("Steam", 0, "Steam", 1, false)]
    [InlineData("Steam", 0, "Steam", 0, false)]
    [InlineData("SteamDeck", 1, "SteamDeck", 2, false)]
    [InlineData("Pro", 0, "Pro", 0, false)]
    [InlineData("Steam", 1, "SteamDeck", 1, false)]
    public void ConnectionReuseRespectsTheTransportMode(string previousFamily, int previousHandle,
        string nextFamily, int nextHandle, bool reusable)
    {
        var sinkType = Tone.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var familyType = Tone.GetNestedType("Family", BindingFlags.NonPublic)!;
        var sink = Activator.CreateInstance(sinkType, true)!;
        var next = Connection();
        sinkType.GetField("Family")!.SetValue(sink, Enum.Parse(familyType, previousFamily));
        sinkType.GetField("GamepadHandle")!.SetValue(sink, new IntPtr(previousHandle));
        sinkType.GetField("HidPath")!.SetValue(sink, pathA);
        sinkType.GetField("Connection")!.SetValue(sink, device.Device);
        // These handles enter only the transport decision, never a native call.
        bool actual = (bool)Tone.GetMethod("CanReuseTransport", Static)!.Invoke(null,
            [sink, Enum.Parse(familyType, nextFamily), pathA, next, new IntPtr(nextHandle)])!;
        Assert.Equal(reusable, actual);
        if (reusable)
        {
            sinkType.GetField("SteamOn")!.SetValue(sink, true);
            sinkType.GetField("PulsePendingSides")!.SetValue(sink, 3);
            Tone.GetMethod("RefreshSinkConnection", Static)!.Invoke(null,
                [sink, device, next, Enum.Parse(familyType, nextFamily), pathA, new IntPtr(nextHandle)]);
            Assert.Same(next, Field<ISdlInputDevice>(sink, "Connection"));
            Assert.Equal(new IntPtr(nextHandle), Field<IntPtr>(sink, "GamepadHandle"));
            Assert.False(Field<bool>(sink, "SteamOn"));
            Assert.Equal(0, Field<int>(sink, "PulsePendingSides"));
        }
    }

    [Fact]
    public void PeerPathChangeDropsPendingPulseState()
    {
        var type = Tone.GetNestedType("Sink", BindingFlags.NonPublic)!;
        var family = Enum.Parse(Tone.GetNestedType("Family", BindingFlags.NonPublic)!, "SteamDeck");
        var sink = Activator.CreateInstance(type, true)!;
        type.GetField("Connection")!.SetValue(sink, device.Device);
        type.GetField("Family")!.SetValue(sink, family);
        type.GetField("Remote")!.SetValue(sink, true);
        type.GetField("HidPath")!.SetValue(sink, "peer://first/controlled");
        type.GetField("PulsePendingSides")!.SetValue(sink, 3);
        type.GetField("PulseAmp")!.SetValue(sink, .5f);
        type.GetField("PulseAmpLeft")!.SetValue(sink, .3f);
        type.GetField("PulseAmpRight")!.SetValue(sink, .5f);
        type.GetField("PulseLastSendMs")!.SetValue(sink, 123L);
        type.GetField("PulseRemoteZeroPending")!.SetValue(sink, true);
        Tone.GetMethod("RefreshSinkConnection", Static)!.Invoke(null,
            [sink, device, device.Device, family, "peer://second/controlled", IntPtr.Zero]);
        Assert.Equal(0, Field<int>(sink, "PulsePendingSides"));
        Assert.Equal(0f, Field<float>(sink, "PulseAmp"));
        Assert.Equal(0f, Field<float>(sink, "PulseAmpLeft"));
        Assert.Equal(0f, Field<float>(sink, "PulseAmpRight"));
        Assert.Equal(0, Field<long>(sink, "PulseLastSendMs"));
        Assert.False(Field<bool>(sink, "PulseRemoteZeroPending"));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("retire")]
    [InlineData("source")]
    [InlineData("other-peer-stop")]
    public async Task RemoteToneRechecksOwnershipAtTheFeatureWrite(string change)
    {
        SettingsManager.UserSettings.Items.Clear();
        var reports = new ConcurrentQueue<byte[]>();
        var native = HapticToneService.FeatureReportWriter;
        HapticToneService.FeatureReportWriter = (handle, bytes, count) =>
        {
            reports.Enqueue(bytes.Take(count).ToArray());
            return native(handle, bytes, count);
        };
        static bool Positive(byte[] bytes) => bytes.Length > 9 && bytes[1] == 0x8f && (bytes[8] != 0 || bytes[9] != 0);
        var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
        void Send(LinkConnectionLifetime owner, uint sequence, float hz, float amp)
        {
            var frame = new LinkIncomingFrame(owner, LinkMessageType.Output, 0, sequence, OutputEffectCodec.EncodeHapticTone(hz, amp));
            lock (device.OutputSync)
                Assert.True(frame.TryCommit(4, () => HapticToneService.ApplyRemoteTone(device, hz, amp, frame.Ticket(4))));
            WaitRemoteBuild();
        }
        Send(lifetime, 0, 200, .5f);
        Assert.True(SpinWait.SpinUntil(() => reports.Count(Positive) >= 2, 30000));
        await Task.Delay(60);
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool released = false;
        int held = 0;
        HapticToneService.RemoteToneDispatchReady = (id, request) =>
        {
            if (id == device.InstanceGuid && request.Hz == 220 && Interlocked.Exchange(ref held, 1) == 0)
            {
                ready.Set();
                released = release.Wait(30000);
            }
        };
        int cut = 0;
        try
        {
            Send(lifetime, 1, 220, .5f);
            Assert.True(ready.Wait(30000));
            if (change == "stop") Send(lifetime, 2, 0, 0);
            else if (change == "other-peer-stop")
            {
                var peer = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
                Send(peer, 0, 0, 0);
                Assert.True(lifetime.IsCurrent);
            }
            else if (change == "retire") lifetime.Retire();
            else lock (device.OutputSync) device.Device = Connection();
            cut = reports.Count;
        }
        finally { release.Set(); }
        await Task.Delay(100);
        var after = reports.Skip(cut).ToArray();
        int obsolete = after.Count(Positive);
        HapticToneService.RemoteToneDispatchReady = null;
        var current = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
        int freshStart = reports.Count;
        Send(current, 0, 300, .5f);
        Assert.True(SpinWait.SpinUntil(() => reports.Skip(freshStart).Any(Positive), 30000));
        output.WriteLine($"change={change} firstPositiveControl=True obsoletePositiveWrites={obsolete} freshPositiveControl=True");
        output.WriteLine("firstRaw=" + Convert.ToHexString(reports.First(Positive)));
        output.WriteLine("afterInvalidationRaw=" + string.Join("|", after.Select(Convert.ToHexString)));
        Assert.True(released);
        Assert.Equal(0, obsolete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightFeatureWritesHoldTheirCommitGate(bool retire)
    {
        SettingsManager.UserSettings.Items.Clear();
        var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
        void Send(uint sequence, float hz, float amplitude)
        {
            var frame = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, sequence, OutputEffectCodec.EncodeHapticTone(hz, amplitude));
            lock (device.OutputSync)
                Assert.True(frame.TryCommit(4, () => HapticToneService.ApplyRemoteTone(device, hz, amplitude, frame.Ticket(4))));
            WaitRemoteBuild();
        }
        using var inWrite = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var changing = new ManualResetEventSlim();
        int positives = 0, hold = 0;
        bool released = false;
        var native = HapticToneService.FeatureReportWriter;
        HapticToneService.FeatureReportWriter = (handle, bytes, count) =>
        {
            if (count > 9 && bytes[1] == 0x8f && (bytes[8] != 0 || bytes[9] != 0))
            {
                Interlocked.Increment(ref positives);
                if (Interlocked.Exchange(ref hold, 0) == 1)
                {
                    inWrite.Set();
                    released = release.Wait(5000);
                }
            }
            return native(handle, bytes, count);
        };
        Send(0, 200, .5f);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref positives) >= 2, 2000));
        await Task.Delay(60);
        Volatile.Write(ref hold, 1);
        Send(1, 220, .5f);
        Assert.True(inWrite.Wait(2000));
        var change = Task.Run(() =>
        {
            changing.Set();
            if (retire) lifetime.Retire();
            else Send(2, 0, 0);
        });
        bool completedDuringWrite;
        try
        {
            Assert.True(changing.Wait(2000));
            completedDuringWrite = await Task.WhenAny(change, Task.Delay(100)) == change;
        }
        finally
        {
            release.Set();
            await change.WaitAsync(TimeSpan.FromSeconds(5));
        }
        output.WriteLine($"retire={retire} initialPositiveControl=True completedDuringWrite={completedDuringWrite} completedAfterRelease=True");
        Assert.True(released);
        Assert.False(completedDuringWrite);
    }

    [Fact]
    public async Task ARemoteStopPreservesAnActiveLocalTestTone()
    {
        var reports = new ConcurrentQueue<byte[]>();
        var native = HapticToneService.FeatureReportWriter;
        HapticToneService.FeatureReportWriter = (handle, bytes, count) =>
        {
            reports.Enqueue(bytes.Take(count).ToArray());
            return native(handle, bytes, count);
        };
        static bool Positive(byte[] bytes) => bytes.Length > 9 && bytes[1] == 0x8f && (bytes[8] != 0 || bytes[9] != 0);
        HapticToneService.Reconcile();
        Assert.True(HapticToneService.TriggerTestTone(device.InstanceGuid, 400, HeldMs));
        Assert.True(SpinWait.SpinUntil(() => reports.Count(Positive) >= 2, 2000));
        var lifetime = LinkLifetimeFixtures.Lifetime(new[] { LinkLifetimeFixtures.Info(device.InstanceGuid.ToString("N")) });
        var stop = new LinkIncomingFrame(lifetime, LinkMessageType.Output, 0, 0, OutputEffectCodec.EncodeHapticTone(0, 0));
        lock (device.OutputSync)
            Assert.True(stop.TryCommit(4, () => HapticToneService.ApplyRemoteTone(device, 0, 0, stop.Ticket(4))));
        int cut = reports.Count;
        await Task.Delay(100);
        Assert.DoesNotContain(reports.Skip(cut), bytes => !Positive(bytes));
        Assert.True(HapticToneService.TriggerTestTone(device.InstanceGuid, 450, HeldMs));
        Assert.True(SpinWait.SpinUntil(() => reports.Skip(cut).Any(Positive), 2000));
        output.WriteLine("localPositiveBeforeStop=True localPositiveAfterStop=True remoteZeroDidNotCancelTest=True");
    }
}
