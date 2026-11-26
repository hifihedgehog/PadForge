using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using PadForge.Common.Input;
using Xunit.Abstractions;

namespace PadForge.Tests;

[Collection("AudioProfileLifecycle")]
public sealed class PersonaReaderLifetimeTests(ITestOutputHelper output)
{
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetFileType(IntPtr handle);

    // StartBtMic gained an optional DualShock 4 flag (#528). Reflection does
    // not fill optional parameters, so the lanes' shared call does.
    static object[] WithDefaults(MethodInfo method, params object[] given)
        => method.GetParameters().Select((p, i) => i < given.Length ? given[i] : p.DefaultValue).ToArray();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetiredReaderCannotPublishIntoAReplacement(bool bluetooth, bool afterRead)
    {
        string name = "PadForge-persona-lifetime-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        var feed = new AudioPassthroughService.PersonaFeed { Owner = new(), RouteGeneration = 1, Slot = 14 };
        feed.Audio = (HMUsbAudio)RuntimeHelpers.GetUninitializedObject(typeof(HMUsbAudio));
        var service = typeof(AudioPassthroughService);
        string lane = bluetooth ? "BtMic" : "UsbJack";
        var start = service.GetMethod("Start" + lane, Static)!;
        var stop = service.GetMethod("Stop" + lane, Static)!;
        var oldPad = Guid.NewGuid();
        var newPad = Guid.NewGuid();
        var oldBoundary = AudioPassthroughService.PersonaReadBoundary;
        var oldOpening = AudioPassthroughService.PersonaReaderOpening;
        using var paused = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var opening = new ManualResetEventSlim();
        using var allowOpen = new ManualResetEventSlim();
        Thread reader = null, replacement = null;
        AudioPassthroughService.PersonaReader lifetime = null;
        int selected = 0, rawReads = 0;
        bool holdOpen = false;
        var packet = new byte[bluetooth ? 78 : 64];
        packet[0] = bluetooth ? (byte)0x31 : (byte)1;
        int status = bluetooth ? 55 : 54;
        packet[status] = 1;
        try
        {
            AudioPassthroughService.PersonaReaderOpening = (f, bt) =>
            {
                if (!ReferenceEquals(f, feed) || !holdOpen) return;
                opening.Set();
                if (!allowOpen.Wait(5000)) throw new TimeoutException();
            };
            AudioPassthroughService.PersonaReadBoundary = (f, bt, before, raw, count) =>
            {
                if (!ReferenceEquals(f, feed)) return;
                if (!before && count > 0) Interlocked.Increment(ref rawReads);
                if (before == !afterRead && Interlocked.Increment(ref selected) == 2)
                {
                    paused.Set();
                    if (!release.Wait(5000)) throw new TimeoutException();
                }
            };
            start.Invoke(null, WithDefaults(start, feed, oldPad, @"\\.\pipe\" + name, 1));
            await connected.WaitAsync(TimeSpan.FromSeconds(5));
            reader = (Thread)feed.GetType().GetField(lane + "Thread")!.GetValue(feed)!;
            await server.WriteAsync(packet);
            Assert.True(SpinWait.SpinUntil(() => AudioPassthroughService.TryGetHeadphoneJack(oldPad) == true, 2000));
            if (afterRead)
            {
                packet[status] = 0;
                await server.WriteAsync(packet);
            }
            Assert.True(paused.Wait(2000));
            IntPtr handle = (IntPtr)feed.GetType().GetField(lane + "Handle")!.GetValue(feed)!;
            lifetime = (AudioPassthroughService.PersonaReader)feed.GetType().GetField(lane + "Reader")!.GetValue(feed)!;
            Task<int> closeRead = bluetooth ? server.ReadAsync(new byte[1024], 0, 1024) : Task.FromResult(0);
            stop.Invoke(null, StopArgs(stop, feed));
            if (bluetooth) Assert.Equal(AudioPassthroughService.Ds5HapticBtReportSize, await closeRead.WaitAsync(TimeSpan.FromSeconds(2)));
            bool unpublished = (IntPtr)feed.GetType().GetField(lane + "Handle")!.GetValue(feed)! == IntPtr.Zero;
            bool retained = GetFileType(handle) == 3;
            holdOpen = true;
            start.Invoke(null, WithDefaults(start, feed, newPad, @"\\.\pipe\missing-" + name, 1));
            Assert.True(opening.Wait(2000));
            replacement = (Thread)feed.GetType().GetField(lane + "Thread")!.GetValue(feed)!;
            AudioPassthroughService.NoteHeadphoneJack(newPad, true);
            release.Set();
            bool drained = reader.Join(1000);
            if (!drained)
            {
                await server.WriteAsync(packet);
                Assert.True(reader.Join(2000));
            }
            bool current = AudioPassthroughService.TryGetHeadphoneJack(newPad) == true;
            bool previous = AudioPassthroughService.TryGetHeadphoneJack(oldPad) == true;
            await lifetime.Cancellation.WaitAsync(TimeSpan.FromSeconds(2));
            output.WriteLine($"bluetooth={bluetooth} afterRead={afterRead} rawReads={rawReads} unpublished={unpublished} retained={retained} drained={drained} current={current} previous={previous}");
            Assert.True(rawReads > 0);
            Assert.True(unpublished);
            Assert.True(retained);
            Assert.True(drained);
            Assert.True(current);
            Assert.True(previous);
            Assert.Equal((uint)0, GetFileType(handle));
            stop.Invoke(null, StopArgs(stop, feed));
            allowOpen.Set();
            Assert.True(replacement.Join(2000));
        }
        finally
        {
            feed.Retired = true;
            release.Set();
            allowOpen.Set();
            if (reader?.IsAlive == true)
            {
                try { await server.WriteAsync(packet); } catch (System.IO.IOException) { }
                Assert.True(reader.Join(2000));
            }
            if (replacement?.IsAlive == true) Assert.True(replacement.Join(2000));
            if (lifetime != null) await lifetime.Cancellation.WaitAsync(TimeSpan.FromSeconds(2));
            AudioPassthroughService.PersonaReadBoundary = oldBoundary;
            AudioPassthroughService.PersonaReaderOpening = oldOpening;
            var states = (ConcurrentDictionary<Guid, bool>)service.GetField("s_padJackState", Static)!.GetValue(null)!;
            states.TryRemove(oldPad, out _);
            states.TryRemove(newPad, out _);
        }
    }

    // StopBtMic takes whether to write the close, which retirement does.
    static object[] StopArgs(MethodInfo stop, AudioPassthroughService.PersonaFeed feed)
        => stop.GetParameters().Length == 2 ? [feed, true] : [feed];
}
