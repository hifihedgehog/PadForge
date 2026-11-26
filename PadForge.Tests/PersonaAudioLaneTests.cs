using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using PadForge.Common.Input;

namespace PadForge.Tests;

/// <summary>
/// The persona and jack readers against a pipe that stands in for a pad,
/// the microphone ring's rules, the Bluetooth microphone session's
/// recovery, the stop path's close, the owner slot's rings, the per-feed
/// counts, the DualShock 4 lane's reused buffers and the haptic level after
/// a parked stream. Members the fixes add are reached by name, so each test
/// reads what the code does today.
/// </summary>
[Collection("AudioProfileLifecycle")]
public sealed class PersonaAudioLaneTests
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    static readonly Type Aps = typeof(AudioPassthroughService);

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

    static MethodInfo Method(string name) => Aps.GetMethod(name, Static) ?? throw new Xunit.Sdk.XunitException("AudioPassthroughService has no " + name);
    static object Call(string name, params object[] args) => Method(name).Invoke(null, args);
    static object StaticField(string name) => Aps.GetField(name, Static)!.GetValue(null);

    static (NamedPipeServerStream Server, string Path) Pipe(int instances = 1)
    {
        string name = "PadForge-lane-" + Guid.NewGuid().ToString("N");
        return (new NamedPipeServerStream(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous), @"\\.\pipe\" + name);
    }

    static byte[] Ds5BtReport(byte flags = 0, bool plugged = false)
    {
        var r = new byte[78];
        r[0] = 0x31;
        r[1] = flags;
        r[55] = plugged ? (byte)1 : (byte)0;
        return r;
    }

    sealed class JackRig : IDisposable
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly IDictionary Watches = (IDictionary)StaticField("_jackWatch");
        public readonly object Gate = StaticField("_jackLock");
        public int Reads;
        readonly Action<Guid, bool, byte[], int> _old = AudioPassthroughService.JackWatchReadBoundary;

        public JackRig()
        {
            AudioPassthroughService.JackWatchReadBoundary = (pad, before, raw, count) =>
            {
                if (pad == Id && !before && count > 0) Interlocked.Increment(ref Reads);
            };
        }

        public object Watch() { lock (Gate) return Watches.Contains(Id) ? Watches[Id] : null; }
        public static Thread ThreadOf(object watch) => (Thread)watch.GetType().GetField("Thread")!.GetValue(watch);

        public void Dispose()
        {
            Call("StopJackWatch", Id);
            AudioPassthroughService.JackWatchReadBoundary = _old;
            Aps.GetField("ReadErrorForTest", Static)?.SetValue(null, null);
            lock (Gate) Watches.Remove(Id);
            ((ConcurrentDictionary<Guid, bool>)StaticField("s_padJackState")).TryRemove(Id, out _);
        }
    }

    /// <summary>A Bluetooth DualSense's 0x31 with the microphone flag
    /// carries Opus where the state struct sits, and is no jack
    /// reading.</summary>
    [Fact]
    public async Task TheJackWatch_PassesOverAMicrophoneReport()
    {
        var (server, path) = Pipe();
        using (server)
        using (var rig = new JackRig())
        {
            var connection = server.WaitForConnectionAsync();
            Call("EnsureJackWatch", rig.Id, path, true, false);
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            await server.WriteAsync(Ds5BtReport());
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(rig.Id) == false, 2000));

            // Opus whose byte 55 has its low bit set.
            await server.WriteAsync(Ds5BtReport(flags: 0x02, plugged: true));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref rig.Reads) >= 2, 2000));
            Thread.Sleep(100);
            Assert.False(AudioPassthroughService.TryGetHeadphoneJack(rig.Id));

            // The positive control: a state report that says plugged.
            await server.WriteAsync(Ds5BtReport(plugged: true));
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(rig.Id) == true, 2000));
        }
    }

    /// <summary>A voice session's lane whose pad left its node opens the
    /// pad again by itself and keeps its place: nothing else starts it.</summary>
    [Fact]
    public async Task AVoiceLane_WhosePadLeft_OpensItAgain()
    {
        var (first, path) = Pipe(instances: 2);
        string name = path.Substring(@"\\.\pipe\".Length);
        NamedPipeServerStream second = null;
        var fault = Aps.GetField("ReadErrorForTest", Static)!;
        var lanes = (IDictionary)StaticField("_voiceMicLanes");
        var pad = Guid.NewGuid();
        int fail = 0;
        // The lane writes its session toggles with a blocking write, and the
        // test's pipe holds no buffer: a reader drains each instance.
        static void Drain(NamedPipeServerStream server) => _ = Task.Run(async () =>
        {
            var sink = new byte[1024];
            try { while (await server.ReadAsync(sink) > 0) { } } catch { }
        });
        try
        {
            fault.SetValue(null, (Func<Guid, int>)(p => p == pad && Volatile.Read(ref fail) == 1 ? 1167 : 0));
            var connection = first.WaitForConnectionAsync();
            Assert.True(AudioPassthroughService.StartVoiceBtMic(pad, path));
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            Drain(first);
            await first.WriteAsync(Ds5BtReport());

            // ERROR_DEVICE_NOT_CONNECTED on a read, with no stop.
            Volatile.Write(ref fail, 1);
            second = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var again = second.WaitForConnectionAsync();
            // This write meets the fault on the lane's next read. When the
            // lane came back from its first read only after the fault was
            // set, that read failed instead and the lane closed the pipe's
            // other end, so the write finds no reader. Either way the lane
            // met the fault, which is all this step is for.
            try { await first.WriteAsync(Ds5BtReport()); }
            catch (System.IO.IOException) { }
            await Task.Delay(300);
            Volatile.Write(ref fail, 0);
            await again.WaitAsync(TimeSpan.FromSeconds(5));
            Drain(second);
            Assert.True(lanes.Contains(pad), "the lane left its session");
        }
        finally
        {
            fault.SetValue(null, null);
            // The pipes go first: a stop's CancelIoEx on a synchronous pipe
            // handle can wait on the read that handle has pending, and a
            // pipe with no server ends that read.
            first.Dispose();
            second?.Dispose();
            AudioPassthroughService.StopVoiceBtMic(pad);
            Assert.True(SpinWait.SpinUntil(() => !lanes.Contains(pad), 3000), "the lane did not end");
        }
    }

    /// <summary>A read that says the pad left ends the watch, and the
    /// next pass opens the pad again.</summary>
    [Fact]
    public async Task TheJackWatch_EndsWhenThePadLeaves_AndTheNextPassOpensItAgain()
    {
        var (first, path) = Pipe(instances: 2);
        string name = path.Substring(@"\\.\pipe\".Length);
        NamedPipeServerStream second = null;
        var fault = Aps.GetField("ReadErrorForTest", Static);
        Assert.NotNull(fault);
        int fail = 0;
        using (first)
        using (var rig = new JackRig())
        {
            fault!.SetValue(null, (Func<Guid, int>)(pad => pad == rig.Id && Volatile.Read(ref fail) == 1 ? 1167 : 0));
            var connection = first.WaitForConnectionAsync();
            Call("EnsureJackWatch", rig.Id, path, true, false);
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            await first.WriteAsync(Ds5BtReport());
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(rig.Id) == false, 2000));
            var watch = rig.Watch();
            Assert.NotNull(watch);

            // ERROR_DEVICE_NOT_CONNECTED on the next read, with no stop.
            Volatile.Write(ref fail, 1);
            await first.WriteAsync(Ds5BtReport());
            Assert.True(JackRig.ThreadOf(watch).Join(2000), "the reader kept its dead handle");
            Assert.Null(rig.Watch());

            Volatile.Write(ref fail, 0);
            // The pad back: a new instance of the pipe, made only now so the
            // first open could not take it.
            second = new NamedPipeServerStream(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var again = second.WaitForConnectionAsync();
            Call("EnsureJackWatch", rig.Id, path, true, false);
            await again.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(watch, rig.Watch());
            await second.WriteAsync(Ds5BtReport(plugged: true));
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(rig.Id) == true, 2000));
        }
        second?.Dispose();
    }

    // ── The microphone ring ──

    static bool MicTakesBlock(int ring, int buffered, int block, bool streaming)
        => (bool)Aps.GetMethod("MicTakesBlock", Static, [typeof(int), typeof(int), typeof(int), typeof(bool)])
            !.Invoke(null, [ring, buffered, block, streaming])!;

    static bool MicSkipsDecode(int ring, int buffered, int block, bool streaming, ref int last)
    {
        var m = Aps.GetMethod("MicSkipsDecode", Static) ?? throw new Xunit.Sdk.XunitException("no MicSkipsDecode");
        object[] args = [ring, buffered, block, streaming, last];
        bool skip = (bool)m.Invoke(null, args)!;
        last = (int)args[4];
        return skip;
    }


    /// <summary>A second of 10 ms blocks with no host stream open leaves
    /// one block in the ring, the lead a host that opens then gets.</summary>
    [Fact]
    public void WithNoHostStream_TheRingHoldsOneBlock()
    {
        Assert.NotNull(Aps.GetMethod("MicTakesBlock", Static, [typeof(int), typeof(int), typeof(int), typeof(bool)]));
        const int ring = 49152, block = 1920;
        int buffered = 0;
        for (int i = 0; i < 100; i++)
            if (MicTakesBlock(ring, buffered, block, streaming: false)) buffered += block;
        Assert.Equal(block, buffered);
        // A host that reads takes every block that fits.
        Assert.True(MicTakesBlock(ring, block * 10, block, streaming: true));
        Assert.False(MicTakesBlock(ring, ring - block, block, streaming: true));
    }

    /// <summary>The USB lane, through a microphone whose host holds no
    /// stream, keeps one block.</summary>
    [Fact]
    public void TheUsbLane_WithNoHostStream_KeepsOneBlock()
    {
        using var rig = new MicRig(new NAudio.Wave.WaveFormat(48000, 16, 2), ringBytes: 49152);
        rig.Start();
        var block = new byte[1920];
        for (int i = 0; i < block.Length; i += 2) block[i + 1] = 0x10;
        for (int i = 0; i < 100; i++) rig.Capture.Emit(block);
        Assert.Equal(1920, rig.Microphone.BufferedBytes);
    }

    /// <summary>The Bluetooth lane's skip follows the ring's drain while a
    /// host stream is open.</summary>
    [Fact]
    public void TheBluetoothSkip_FollowsTheRingsDrain()
    {
        const int ring = 49152, block = 1920;
        int last = 0;
        // No stream, one block held: skipped.
        Assert.True(MicSkipsDecode(ring, block, block, streaming: false, ref last));
        // Open and full, with no drain since the last frame: skipped.
        Assert.True(MicSkipsDecode(ring, ring - 1, block, streaming: true, ref last));
        Assert.True(MicSkipsDecode(ring, ring - 1, block, streaming: true, ref last));
        // The host read some: decoded.
        Assert.False(MicSkipsDecode(ring, ring - 1 - 192, block, streaming: true, ref last));
        // Room for a block: decoded.
        Assert.False(MicSkipsDecode(ring, 0, block, streaming: true, ref last));
    }

    // ── The Bluetooth microphone session ──

    static object Session()
    {
        var type = Aps.GetNestedType("Ds5MicSession", BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new Xunit.Sdk.XunitException("no Ds5MicSession");
        return Activator.CreateInstance(type, nonPublic: true)!;
    }

    static string Step(object session, bool want, long frames, bool carried, long now)
    {
        var result = session.GetType().GetMethod("Step", Instance)!.Invoke(session, [want, frames, carried, now])!;
        return result.GetType().GetField("Item1")!.GetValue(result)!.ToString()!;
    }

    /// <summary>A burst of tries that ran out with no frame is armed again
    /// 30 s later, and frames that stop are asked for again.</summary>
    [Fact]
    public void TheMicSession_RecoversAsTheVoiceLaneDoes()
    {
        var session = Session();
        var t = session.GetType();
        t.GetField("Open")!.SetValue(session, 1);
        t.GetField("Tries")!.SetValue(session, 5);
        t.GetField("SentTicks")!.SetValue(session, 0L);
        int opens = 0;
        for (long now = 100; now <= 40_000; now += 100)
            if (Step(session, want: true, frames: 0, carried: false, now) == "Open") opens++;
        Assert.True(opens >= 1, "no OPEN in 40 s of ticks");

        // Frames that flowed and stopped for 3 s.
        var stalled = Session();
        Assert.Equal("Open", Step(stalled, true, 0, false, 0));
        Assert.Equal("None", Step(stalled, true, 40, false, 1000));
        Assert.Equal("None", Step(stalled, true, 40, false, 3500));
        Assert.Equal("Open", Step(stalled, true, 40, false, 4100));
        // While the stream carries the byte, no toggle of its own.
        var carried = Session();
        Assert.Equal("Open", Step(carried, true, 0, true, 0));
        Assert.Equal("None", Step(carried, true, 0, true, 5000));
    }

    /// <summary>A rebuilt transport gives the sink a fresh session.</summary>
    [Fact]
    public void ADetachedSink_StartsAFreshMicSession()
    {
        var sinkType = Aps.GetNestedType("Sink", BindingFlags.NonPublic)!;
        object sink = Activator.CreateInstance(sinkType, nonPublic: true)!;
        var field = sinkType.GetField("Ds5Mic");
        Assert.NotNull(field);
        var before = field!.GetValue(sink)!;
        before.GetType().GetField("Tries")!.SetValue(before, 5);
        before.GetType().GetField("Open")!.SetValue(before, 1);
        Call("DetachTransport_NoLock", sink);
        var after = field.GetValue(sink)!;
        Assert.NotSame(before, after);
        Assert.Equal(0, (int)after.GetType().GetField("Tries")!.GetValue(after)!);
        Assert.Equal(0, (int)after.GetType().GetField("Open")!.GetValue(after)!);
    }

    // ── The stop path's close ──

    static IntPtr OpenSync(string path)
    {
        var native = Aps.GetNestedType("NativeMethods", BindingFlags.NonPublic)!;
        return (IntPtr)native.GetMethod("OpenHidSync")!.Invoke(null, [path])!;
    }

    /// <summary>A lane restart on a Bluetooth DualSense that stays writes no
    /// close on the reader's handle, and a stop at retirement does.</summary>
    [Fact]
    public async Task TheLaneRestart_LeavesTheCloseToTheSink_AndRetirementClosesIt()
    {
        var pad = Guid.NewGuid();
        var pads = new List<(Guid Guid, string Path, bool IsBt, bool IsDs4)>
        {
            (Guid.NewGuid(), "usb-path", false, false),
            (pad, "bt-path", true, false),
        };
        var closes = Aps.GetMethod("SinkClosesBtMicSession", Static);
        Assert.NotNull(closes);
        Assert.True((bool)closes!.Invoke(null, [pad, pads])!);
        Assert.False((bool)closes.Invoke(null, [Guid.NewGuid(), pads])!);

        var stop = Method("StopBtMic");
        Assert.Equal(2, stop.GetParameters().Length);
        var (server, path) = Pipe();
        using (server)
        {
            var connection = server.WaitForConnectionAsync();
            IntPtr h = OpenSync(path);
            Assert.NotEqual(new IntPtr(-1), h);
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = 3 };
                feed.BtMicHandle = h;
                feed.BtMicPadGuid = pad;
                // The read is posted first and the stop runs on its own
                // thread: a close written here would block on the pipe, which
                // holds no buffer, until the read takes it.
                var buffer = new byte[256];
                using (var quiet = new CancellationTokenSource(300))
                {
                    var silence = server.ReadAtLeastAsync(buffer, 1, true, quiet.Token).AsTask();
                    var stopped = Task.Run(() => stop.Invoke(null, [feed, false]));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silence);
                    await stopped.WaitAsync(TimeSpan.FromSeconds(2));
                }

                feed.BtMicHandle = h;
                feed.BtMicPadGuid = pad;
                // The pipe holds no buffer, so the write waits for this read.
                using var wait = new CancellationTokenSource(2000);
                var reading = server.ReadAsync(buffer, wait.Token).AsTask();
                stop.Invoke(null, [feed, true]);
                int got = await reading;
                Assert.Equal(AudioPassthroughService.Ds5HapticBtReportSize, got);
                Assert.Equal(0x32, buffer[0]);
                Assert.Equal(0xFE, buffer[4]);
            }
            finally { CloseHandle(h); }
        }
    }

    /// <summary>A reader that waits for the lane's lock holds no callback
    /// gate, so HIDMaestro's pump goes on while a close is being written.</summary>
    [Fact]
    public async Task AReaderWaitingOnTheLane_NeverHoldsThePump()
    {
        var (server, path) = Pipe();
        var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = 6, RouteGeneration = 1 };
        int afterRead = 0;
        var oldBoundary = AudioPassthroughService.PersonaReadBoundary;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (feed.HidLaneLock) { held.Set(); release.Wait(10000); } }) { IsBackground = true };
        try
        {
            AudioPassthroughService.PersonaReadBoundary = (f, bt, before, raw, count) =>
            {
                if (ReferenceEquals(f, feed) && !before && count > 0) Interlocked.Increment(ref afterRead);
            };
            using (server)
            {
                var connection = server.WaitForConnectionAsync();
                Call("StartBtMic", feed, Guid.NewGuid(), path, 1, false);
                await connection.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(SpinWait.SpinUntil(() => feed.BtMicHandle != IntPtr.Zero, 2000));
                // The stop path's write, as long as the test holds the lane.
                holder.Start();
                Assert.True(held.Wait(2000));
                await server.WriteAsync(Ds5BtReport());
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref afterRead) >= 1, 2000));
                Thread.Sleep(100);

                var pump = Task.Run(() => Call("OnPersonaFrames", feed, null, ReadOnlyMemory<byte>.Empty));
                bool quick = await Task.WhenAny(pump, Task.Delay(500)) == pump;
                release.Set();
                await pump.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(quick, "the pump waited behind the reader");
            }
        }
        finally
        {
            release.Set();
            feed.Retired = true;
            var stop = Method("StopBtMic");
            stop.Invoke(null, stop.GetParameters().Length == 2 ? [feed, true] : [feed]);
            AudioPassthroughService.PersonaReadBoundary = oldBoundary;
        }
    }

    /// <summary>The reconcile's lane restart, through RefreshPersonaTargets:
    /// a USB DualSense joins the slot of a Bluetooth DualSense whose
    /// microphone lane runs, the lane moves to the USB pad, and the
    /// Bluetooth pad, still among the pads, gets no close on its
    /// reader.</summary>
    [Fact]
    public async Task AReconcileWhoseBluetoothPadStays_WritesNoCloseOnItsReader()
    {
        var feeds = (IDictionary)StaticField("_personaFeeds");
        var gate = StaticField("_personaFeedLock");
        const int slot = 12;
        var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = slot, Published = true, RouteGeneration = 1 };
        object previous;
        lock (gate) { previous = feeds.Contains(slot) ? feeds[slot] : null; feeds[slot] = feed; }
        var factory = AudioPassthroughService.PersonaMicCaptureFactory;
        var (server, path) = Pipe();
        var bt = Guid.NewGuid();
        IntPtr h = IntPtr.Zero;
        try
        {
            AudioPassthroughService.PersonaMicCaptureFactory = _ => null;
            AudioPassthroughService.RefreshPersonaTargets(feed, slot, 1, new List<(Guid Guid, string Path, bool IsBt, bool IsDs4)>());
            var connection = server.WaitForConnectionAsync();
            h = OpenSync(path);
            Assert.NotEqual(new IntPtr(-1), h);
            await connection.WaitAsync(TimeSpan.FromSeconds(5));
            // The Bluetooth pad's microphone lane, as its reader holds it.
            lock (feed.HidLaneLock) { feed.BtMicHandle = h; feed.BtMicPadGuid = bt; }
            var pads = new List<(Guid Guid, string Path, bool IsBt, bool IsDs4)>
            {
                (Guid.NewGuid(), @"\\.\pipe\PadForge-none-" + Guid.NewGuid().ToString("N"), false, false),
                (bt, path, true, false),
            };
            var buffer = new byte[256];
            using var quiet = new CancellationTokenSource(500);
            var silence = server.ReadAtLeastAsync(buffer, 1, true, quiet.Token).AsTask();
            var pass = Task.Run(() => AudioPassthroughService.RefreshPersonaTargets(feed, slot, 1, pads));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silence);
            await pass.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(Guid.Empty, feed.BtMicPadGuid);
        }
        finally
        {
            feed.Retired = true;
            AudioPassthroughService.PersonaMicCaptureFactory = factory;
            lock (gate) { if (previous == null) feeds.Remove(slot); else feeds[slot] = previous; }
            if (h != IntPtr.Zero && h != new IntPtr(-1)) CloseHandle(h);
            server.Dispose();
        }
    }

    /// <summary>BtMicLoop over a full ring while the host holds its stream:
    /// frames that come while the ring does not drain are not decoded, and
    /// once the host drains it they are decoded and submitted.</summary>
    [Fact]
    public async Task TheBluetoothReader_DecodesWhenTheRingDrains_AndNotBefore()
    {
        const int ring = 49152;
        using var rig = new MicRig(new NAudio.Wave.WaveFormat(48000, 16, 2), ringBytes: ring);
        var engine = typeof(HMMicrophoneInput).GetField("_engine", Instance)!.GetValue(rig.Microphone)!;
        var et = engine.GetType();
        var engineLock = et.GetField("_lock", Instance)!.GetValue(engine)!;
        ((Dictionary<byte, byte>)et.GetField("_altSetting", Instance)!.GetValue(engine)!)[0] = 1;
        void Set(string name, int value) { lock (engineLock) et.GetField(name, Instance)!.SetValue(engine, value); }
        // Full: the head one byte short of the tail.
        Set("_micTail", 0);
        Set("_micHead", ring - 1);
        var feed = rig.Feed;
        var pad = Guid.NewGuid();
        var (server, path) = Pipe();
        // One 10 ms mono Opus frame at the stream's own size.
        var enc = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        enc.Bitrate = 71 * 8 * 100;
        enc.UseVBR = false;
        var tone = new short[480];
        for (int i = 0; i < tone.Length; i++) tone[i] = (short)(Math.Sin(i * 0.2) * 8000);
        var opus = new byte[71];
        int len = enc.Encode(tone, 480, opus, 71);
        Assert.True(len > 0);
        byte[] Frame()
        {
            var r = Ds5BtReport(flags: 0x02);
            Array.Copy(opus, 0, r, 3, 71);
            return r;
        }
        // The first decode sets the codec byte it saw, never reset after.
        bool Decoded() => feed.Diag.MicTocFirst != 0xFFFF;
        using (server)
        {
            try
            {
                var connection = server.WaitForConnectionAsync();
                Call("StartBtMic", feed, pad, path, 1, false);
                await connection.WaitAsync(TimeSpan.FromSeconds(5));
                // The reader thread is the lane's: no write reaches the pipe
                // from it, so no drain is needed on this side.
                for (int i = 0; i < 5; i++) await server.WriteAsync(Frame());
                Assert.True(SpinWait.SpinUntil(() => feed.BtMicRxFrames >= 5, 3000), "the reader read " + feed.BtMicRxFrames);
                Thread.Sleep(100);
                Assert.False(Decoded(), "a frame was decoded into a full ring nobody drains");

                // The host reads half the ring.
                Set("_micTail", ring / 2);
                int before = rig.Microphone.BufferedBytes;
                for (int i = 0; i < 5; i++) await server.WriteAsync(Frame());
                Assert.True(SpinWait.SpinUntil(() => rig.Microphone.BufferedBytes > before, 3000), "nothing was submitted");
                Assert.True(Decoded());
            }
            finally
            {
                feed.Retired = true;
                var stop = Method("StopBtMic");
                stop.Invoke(null, stop.GetParameters().Length == 2 ? [feed, false] : [feed]);
            }
        }
    }

    // ── The pump's rings and counts ──

    static HMAudioOutput Output(int channels)
    {
        var output = (HMAudioOutput)RuntimeHelpers.GetUninitializedObject(typeof(HMAudioOutput));
        typeof(HMAudioOutput).GetField("<Channels>k__BackingField", Instance)!.SetValue(output, channels);
        var engineField = typeof(HMAudioOutput).GetField("_engine", Instance)!;
        var engine = RuntimeHelpers.GetUninitializedObject(engineField.FieldType);
        engine.GetType().GetField("_lock", Instance)!.SetValue(engine, new object());
        engine.GetType().GetField("_altSetting", Instance)!.SetValue(engine, new Dictionary<byte, byte>());
        engineField.SetValue(output, engine);
        return output;
    }

    static byte[] Window(int frames, short level)
    {
        var w = new byte[frames * 4];
        for (int f = 0; f < frames; f++)
        {
            BitConverter.GetBytes(level).CopyTo(w, f * 4);
            BitConverter.GetBytes(level).CopyTo(w, f * 4 + 2);
        }
        return w;
    }

    sealed class FeedRig : IDisposable
    {
        readonly IDictionary _feeds = (IDictionary)StaticField("_personaFeeds");
        readonly object _lock = StaticField("_personaFeedLock");
        readonly Dictionary<int, object> _previous = new();
        public readonly Guid Pad = Guid.NewGuid();

        public AudioPassthroughService.PersonaFeed Add(int slot)
        {
            var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = slot, Published = true, SpkL = 0, SpkR = 1, HapL = -1, HapR = -1 };
            feed.Targets = new[] { Pad };
            lock (_lock)
            {
                if (!_previous.ContainsKey(slot)) _previous[slot] = _feeds.Contains(slot) ? _feeds[slot] : null;
                _feeds[slot] = feed;
                Call("ClearUnusedPersonaRingsNoLock", (IEnumerable<Guid>)Array.Empty<Guid>());
            }
            return feed;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var (slot, feed) in _previous)
                    if (feed == null) _feeds.Remove(slot); else _feeds[slot] = feed;
                Call("ClearUnusedPersonaRingsNoLock", (IEnumerable<Guid>)new[] { Pad });
            }
            ((IDictionary)StaticField("_personaSpeakerRings")).Remove(Pad);
        }
    }

    static void Route(AudioPassthroughService.PersonaFeed feed, HMAudioOutput output, byte[] window)
        => Call("RoutePersonaFrames", feed, output, (ReadOnlyMemory<byte>)window);

    /// <summary>A pad on two slots' personas plays the lower slot's stream
    /// alone, one window per window.</summary>
    [Fact]
    public void APadOnTwoPersonas_TakesItsOwnerSlotsStreamAlone()
    {
        using var rig = new FeedRig();
        var owner = rig.Add(2);
        var other = rig.Add(5);
        var output = Output(2);
        Route(owner, output, Window(480, 8000));
        Route(other, output, Window(480, -8000));
        var rings = (IDictionary)StaticField("_personaSpeakerRings");
        var ring = rings[rig.Pad]!;
        long written = (long)ring.GetType().GetField("_write", Instance)!.GetValue(ring)!;
        Assert.Equal(480 * 2, written);
    }

    static long CbCount(AudioPassthroughService.PersonaFeed feed)
    {
        var diag = feed.GetType().GetField("Diag")?.GetValue(feed) ?? throw new Xunit.Sdk.XunitException("the feed keeps no counts of its own");
        return (long)diag.GetType().GetField("CbCount")!.GetValue(diag)!;
    }

    /// <summary>Each feed counts its own pump's windows.</summary>
    [Fact]
    public void EachFeed_CountsItsOwnWindows()
    {
        using var rig = new FeedRig();
        var a = rig.Add(2);
        var b = rig.Add(5);
        var output = Output(2);
        for (int i = 0; i < 3; i++) Route(a, output, Window(480, 100));
        for (int i = 0; i < 5; i++) Route(b, output, Window(480, 100));
        Assert.Equal(3, CbCount(a));
        Assert.Equal(5, CbCount(b));
    }

    /// <summary>The DualShock 4 lane's tick reuses its frames and
    /// reports.</summary>
    [Fact]
    public void TheDs4Tick_AllocatesNothingInSteadyState()
    {
        var sinkType = Aps.GetNestedType("Sink", BindingFlags.NonPublic)!;
        object sink = Activator.CreateInstance(sinkType, nonPublic: true)!;
        sinkType.GetField("DeviceGuid")!.SetValue(sink, Guid.NewGuid());
        sinkType.GetField("IsBt")!.SetValue(sink, true);
        sinkType.GetField("IsDs4")!.SetValue(sink, true);
        sinkType.GetField("Ds4Sbc")!.SetValue(sink, new Ds4SbcEncoder());
        sinkType.GetField("Ds4Pending")!.SetValue(sink, new short[Ds4SbcEncoder.PcmSamplesPerFrame * 4]);
        sinkType.GetField("Ds4Frames")!.SetValue(sink, new Queue<byte[]>(12));
        var method = Method("Ds4BtTick");
        var s = Expression.Parameter(typeof(object));
        var p = Expression.Parameter(typeof(float[]));
        var n = Expression.Parameter(typeof(int));
        var tick = Expression.Lambda<Action<object, float[], int>>(Expression.Call(method, Expression.Convert(s, sinkType), p, n), s, p, n).Compile();
        var pull = new float[512 * 2];
        for (int i = 0; i < pull.Length; i++) pull[i] = (float)Math.Sin(i * 0.05) * 0.25f;
        var saved = AudioPassthroughService.DeviceAudioOutputPathProvider;
        try
        {
            AudioPassthroughService.DeviceAudioOutputPathProvider = null;
            for (int i = 0; i < 50; i++) tick(sink, pull, 512);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) tick(sink, pull, 512);
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal(0, after - before);
        }
        finally { AudioPassthroughService.DeviceAudioOutputPathProvider = saved; }
    }

    /// <summary>A haptic level from before a park is gone when the stream
    /// opens again.</summary>
    [Fact]
    public void AfterAParkedStream_TheHapticLevelStartsFromSilence()
    {
        const int stride = 8;
        var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Slot = 4, HapL = 2, HapR = 3, SrcRate = 48000 };
        var loud = new byte[480 * stride];
        var quiet = new byte[480 * stride];
        for (int f = 0; f < 480; f++)
        {
            BitConverter.GetBytes(short.MaxValue).CopyTo(loud, f * stride + 4);
            BitConverter.GetBytes(short.MaxValue).CopyTo(loud, f * stride + 6);
        }
        const long t = 5_000_000;
        for (int i = 0; i < 20; i++) AudioPassthroughService.FollowPersonaHaptics(feed, loud, stride, t + 10 * i);
        Assert.NotEqual(0L, feed.HapticLevel);
        AudioPassthroughService.FollowPersonaHaptics(feed, quiet, stride, t + 190 + 200);
        Assert.Equal(0L, feed.HapticLevel);
    }

    // The USB microphone lane over a microphone of the test's own, after
    // CapturePcmFormatTests.MicRig, with a ring of the size HIDMaestro gives.
    sealed class Capture(NAudio.Wave.WaveFormat format) : NAudio.Wave.IWaveIn
    {
        public NAudio.Wave.WaveFormat WaveFormat { get; set; } = format;
        public event EventHandler<NAudio.Wave.WaveInEventArgs> DataAvailable;
        public event EventHandler<NAudio.Wave.StoppedEventArgs> RecordingStopped;
        public int Disposals;
        public void StartRecording() { }
        public void StopRecording() { }
        public void Dispose() { Disposals++; RecordingStopped?.Invoke(this, new NAudio.Wave.StoppedEventArgs()); }
        public void Emit(byte[] data) => DataAvailable?.Invoke(this, new NAudio.Wave.WaveInEventArgs(data, data.Length));
    }

    static void Backing(object obj, string name, object value) => obj.GetType().GetField("<" + name + ">k__BackingField", Instance)!.SetValue(obj, value);

    sealed class MicRig : IDisposable
    {
        public readonly Capture Capture;
        public readonly AudioPassthroughService.PersonaFeed Feed;
        public readonly HMMicrophoneInput Microphone;
        readonly IDictionary _feeds;
        readonly object _gate, _previous;
        readonly Func<string, NAudio.Wave.IWaveIn> _factory;

        public MicRig(NAudio.Wave.WaveFormat format, int ringBytes, int channels = 2)
        {
            Capture = new Capture(format);
            var audio = (HMUsbAudio)RuntimeHelpers.GetUninitializedObject(typeof(HMUsbAudio));
            Microphone = (HMMicrophoneInput)RuntimeHelpers.GetUninitializedObject(typeof(HMMicrophoneInput));
            Backing(audio, "Microphone", Microphone); Backing(Microphone, "Channels", channels); Backing(Microphone, "SampleRateHz", 48000); Backing(Microphone, "BitsPerSample", 16);
            var engineField = typeof(HMMicrophoneInput).GetField("_engine", Instance)!;
            var engine = RuntimeHelpers.GetUninitializedObject(engineField.FieldType);
            engine.GetType().GetField("_lock", Instance)!.SetValue(engine, new object());
            engine.GetType().GetField("_altSetting", Instance)!.SetValue(engine, new Dictionary<byte, byte>());
            engine.GetType().GetField("_micRing", Instance)!.SetValue(engine, new byte[ringBytes]);
            engine.GetType().GetField("_micFrameBytes", Instance)?.SetValue(engine, channels * 2);
            engineField.SetValue(Microphone, engine);
            Feed = new AudioPassthroughService.PersonaFeed { Owner = new(), Audio = audio, Slot = 13, Published = true, RouteGeneration = 1 };
            _feeds = (IDictionary)StaticField("_personaFeeds");
            _gate = StaticField("_personaFeedLock");
            _factory = AudioPassthroughService.PersonaMicCaptureFactory;
            lock (_gate) { _previous = _feeds.Contains(13) ? _feeds[13] : null; _feeds[13] = Feed; }
            AudioPassthroughService.PersonaMicCaptureFactory = _ => Capture;
        }

        public void Start() => Call("StartPersonaMic", Feed, Guid.NewGuid(), "managed-input", 13, 1);

        public void Dispose()
        {
            Feed.Retired = true;
            if (Capture.Disposals == 0) Capture.Dispose();
            AudioPassthroughService.PersonaMicCaptureFactory = _factory;
            lock (_gate) { if (_previous == null) _feeds.Remove(13); else _feeds[13] = _previous; }
        }
    }
}
