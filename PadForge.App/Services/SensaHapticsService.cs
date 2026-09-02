using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services
{
    /// <summary>Connection state the service reports to its owner, who maps
    /// it to localized status text on the UI thread.</summary>
    public enum SensaServiceState
    {
        Stopped,
        WaitingForRuntime,
        Active,
    }

    /// <summary>
    /// Razer Sensa HD haptics translation (#374, asked in discussion #369):
    /// streams PadForge's rumble into the Interhaptics engine, whose Razer
    /// provider renders on Sensa HD devices (Wolverine V3 line, Kraken V4
    /// Pro, Freyja). The WYVRN app API plays only pre-authored named clips
    /// and carries no amplitude channel, so this rides the engine layer
    /// underneath: Razer's public Interhaptics Core SDK, whose parametric
    /// API is amplitude-shaped at runtime.
    ///
    /// <para>Every native call and its ORDER mirror the shipping Unity
    /// integration (WyvrnOfficial/Interhaptics_Unity_CoreSDK, cloned beside
    /// the other references), function by function: HAR.Init, provider
    /// ProviderInit kept only on true (HapticDeviceManager.DeviceInitLoop),
    /// AddParametricEffect + AddTargetToEventMarshal + SetEventIntensity +
    /// PlayEvent(id, -now, 0, 0) (HAR.cs PlayParametricHapticEffect, whose
    /// negative-now offset aligns the effect clock with ComputeAllEvents'
    /// time argument), then per tick ComputeAllEvents followed by
    /// ProviderIsPresent gating ProviderRenderHaptics (DeviceRenderLoop),
    /// and StopAllEvents / ProviderClean / Quit at teardown. The Unity
    /// reference makes every call from one thread, and so does this worker.</para>
    ///
    /// <para>The provider is a thin bridge to Synapse's installed
    /// Interhaptics runtime (it locates RzInterHaptics.dll through the
    /// registry and signals the mixer's global event, per a strings-level
    /// read of the shipped DLL). Without Synapse it fails init cleanly, so
    /// the worker retries every 30 seconds and reports WaitingForRuntime,
    /// the same degradation contract as the Chroma mirror.</para>
    /// </summary>
    public sealed class SensaHapticsService : IDisposable
    {
        private static class Har
        {
            // Bindings mirror Interhaptics_Unity_CoreSDK HAR.Native.cs
            // verbatim (same names, same signatures, default marshaling):
            // that file is the shipping proof these exact P/Invokes drive
            // the shipped HAR.dll.
            private const string Dll = "HAR";

            [DllImport(Dll)] public static extern bool Init();
            [DllImport(Dll)] public static extern void Quit();
            [DllImport(Dll)]
            public static extern int AddParametricEffect(
                [In] double[] _amplitude, int _amplitudeSize,
                [In] double[] _pitch, int _pitchSize,
                double _freqMin, double _freqMax,
                [In] double[] _transient, int _transientSize,
                bool _isLooping);
            [DllImport(Dll)] public static extern void SetEventIntensity(int _hMaterialId, double _intensity);
            [DllImport(Dll)] public static extern void PlayEvent(int _hMaterialId, double _vibrationOffset, double _textureOffset, double _stiffnessOffset);
            [DllImport(Dll)] public static extern void StopAllEvents();
            [DllImport(Dll)] public static extern void ComputeAllEvents(double _curTime);
            [DllImport(Dll)] public static extern void AddTargetToEventMarshal(int _hMaterialId, CommandData[] _target, int _size);
        }

        private static class Provider
        {
            // The RazerSensaProvider trio, names verbatim from
            // RazerSensaProvider.cs's RazerSensaProviderNative.
            private const string Dll = "Interhaptics.RazerProvider";

            [DllImport(Dll)] public static extern bool ProviderInit();
            [DllImport(Dll)] public static extern bool ProviderIsPresent();
            [DllImport(Dll)] public static extern bool ProviderClean();
            [DllImport(Dll)] public static extern void ProviderRenderHaptics();
        }

        /// <summary>Interhaptics.HapticBodyMapping.CommandData: three int
        /// enums (Sign, Group, Side), sequential and blittable, layout from
        /// SharedTypes.h and the Unity BodyMapping.cs twin.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CommandData
        {
            public int Sign;   // Operator: Plus = 1
            public int Group;  // GroupID: All = 0
            public int Side;   // LateralFlag: Global = 0

            public CommandData(int sign, int group, int side)
            {
                Sign = sign;
                Group = group;
                Side = side;
            }
        }

        /// <summary>The published rumble amplitude, 0..1, stored as float
        /// bits. Written lock-free from the poll thread's per-tick publish,
        /// read by the worker. Static so the publisher needs no service
        /// reference.</summary>
        private static int s_amplitudeBits;

        /// <summary>True while a service instance's worker runs, so the
        /// poll-thread publisher costs one volatile read when the feature
        /// is off.</summary>
        private static int s_publisherArmed;

        /// <summary>The most recent worker thread, joined by the next
        /// worker before it arms the publisher. Stop joins 3 s and nulls
        /// _thread regardless, so a worker still inside ProviderInit can
        /// outlive its service. When it returned, its finally disarmed the
        /// publisher and called Har.Quit under the NEW instance's engine
        /// (F10). Static because the publisher flag it protects is.</summary>
        private static Thread s_lastWorker;

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): runs on
        /// the worker right before Provider.ProviderInit, so a test can
        /// hold a worker inside the bring-up window.</summary>
        internal static Action BeforeProviderInit;

        private readonly int _retryMs;
        private readonly int _tickMs;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;

        /// <summary>Raised from the worker thread on state changes. The
        /// owner marshals to the UI thread.</summary>
        public event Action<SensaServiceState> StateChanged;

        public SensaHapticsService(int retryMs = 30000, int tickMs = 16)
        {
            _retryMs = retryMs;
            _tickMs = tickMs;
        }

        /// <summary>Whether the poll-thread publisher should bother.</summary>
        public static bool PublisherArmed => Volatile.Read(ref s_publisherArmed) != 0;

        /// <summary>Provider bring-up attempts this worker has made. The
        /// long.MinValue sentinel bug made this observably ZERO while the
        /// worker looked healthy from every other angle (found by a live
        /// stack dump), so the retry cadence is a tested, countable fact.</summary>
        internal int ProviderInitAttempts => Volatile.Read(ref _providerInitAttempts);
        private int _providerInitAttempts;

        /// <summary>Whether this instance's worker thread is alive (test
        /// seam for the predecessor hand-off).</summary>
        internal bool WorkerAlive => _thread?.IsAlive == true;

        /// <summary>Publishes the merged rumble amplitude (0..1). Called at
        /// poll rate from the engine's rumble lane; one volatile write.</summary>
        public static void PublishAmplitude(float amplitude)
            => Volatile.Write(ref s_amplitudeBits, BitConverter.SingleToInt32Bits(
                amplitude < 0f ? 0f : (amplitude > 1f ? 1f : amplitude)));

        /// <summary>Max of the four packed feedback voices, normalized 0..1.
        /// The pack is <see cref="PadForge.Engine.Common.LfeOutputState"/>'s
        /// four ushort voices.</summary>
        public static float PackToAmplitude(long pack)
        {
            ushort a = (ushort)(pack & 0xFFFF);
            ushort b = (ushort)((pack >> 16) & 0xFFFF);
            ushort c = (ushort)((pack >> 32) & 0xFFFF);
            ushort d = (ushort)((pack >> 48) & 0xFFFF);
            int max = Math.Max(Math.Max(a, b), Math.Max(c, d));
            return max / 65535f;
        }

        public void Start()
        {
            if (_thread != null) return; // Already started.
            _stop = false;
            _thread = new Thread(Worker) { IsBackground = true, Name = "SensaHaptics" };
            _thread.Start();
        }

        public void Stop()
        {
            if (_thread == null) return;
            _stop = true;
            try { _thread.Join(3000); } catch { }
            _thread = null;
        }

        private void Report(SensaServiceState state)
        {
            try { StateChanged?.Invoke(state); } catch { }
        }

        private void Worker()
        {
            // Serialize against a predecessor that outlived its Stop (F10):
            // its finally disarms the publisher and quits the engine, and
            // both must land before this worker arms and inits, never
            // after.
            var prev = Interlocked.Exchange(ref s_lastWorker, Thread.CurrentThread);
            if (prev != null && prev != Thread.CurrentThread && prev.IsAlive) prev.Join();
            Volatile.Write(ref s_publisherArmed, 1);
            bool harUp = false;
            bool providerUp = false;
            try
            {
                // 1. Engine up (HAR.dll runs with no Razer device present).
                PadForge.Engine.SdlDiagLog.WriteLine("SENSA worker: calling HAR.Init");
                try { harUp = Har.Init(); }
                catch (DllNotFoundException) { PadForge.Engine.SdlDiagLog.WriteLine("SENSA HAR.dll not found"); }
                catch (EntryPointNotFoundException) { PadForge.Engine.SdlDiagLog.WriteLine("SENSA HAR entry point missing"); }
                PadForge.Engine.SdlDiagLog.WriteLine($"SENSA HAR.Init => {harUp}");
                if (!harUp)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("SENSA HAR.Init failed or HAR.dll missing");
                    return; // finally reports Stopped.
                }

                // 2. One looping constant envelope; live intensity is the
                //    rumble stream. Amplitude pairs are Time-Value: hold 1.0
                //    across a one-second loop.
                var clock = Stopwatch.StartNew();
                int effectId = Har.AddParametricEffect(
                    new double[] { 0.0, 1.0, 1.0, 1.0 }, 4,
                    null, 0,
                    65.0, 300.0,   // the Unity reference's DEFAULT_FREQ_MIN/MAX
                    null, 0,
                    true);
                if (effectId == -1)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("SENSA AddParametricEffect returned -1");
                    return;
                }
                Har.AddTargetToEventMarshal(effectId,
                    new[] { new CommandData(1 /* Plus */, 0 /* All */, 0 /* Global */) }, 1);
                Har.SetEventIntensity(effectId, 0.0);
                Har.PlayEvent(effectId, -clock.Elapsed.TotalSeconds, 0.0, 0.0);

                // Sentinel arithmetic, not long.MinValue: TickCount64 minus
                // MinValue overflows negative, which silently disabled the
                // provider retry forever (found by a live stack dump: the
                // worker sat healthy in its tick loop while the init block
                // never entered, and the attempts counter read zero). One
                // full interval in the past fires the first attempt on the
                // first tick.
                long lastProviderTry = Environment.TickCount64 - _retryMs;
                float lastIntensity = -1f;
                Report(SensaServiceState.WaitingForRuntime);

                while (!_stop)
                {
                    long nowTick = Environment.TickCount64;

                    // 3. Provider bring-up, retried while Synapse's runtime
                    //    is absent. ProviderInit fails cleanly without it.
                    if (!providerUp && nowTick - lastProviderTry >= _retryMs)
                    {
                        lastProviderTry = nowTick;
                        Interlocked.Increment(ref _providerInitAttempts);
                        BeforeProviderInit?.Invoke();
                        try { providerUp = Provider.ProviderInit(); }
                        catch (DllNotFoundException) { }
                        catch (EntryPointNotFoundException) { }
                        PadForge.Engine.SdlDiagLog.WriteLine($"SENSA provider init => {providerUp}");
                        if (providerUp) Report(SensaServiceState.Active);
                    }

                    // 4. Stream the rumble.
                    float amp = BitConverter.Int32BitsToSingle(Volatile.Read(ref s_amplitudeBits));
                    if (amp != lastIntensity)
                    {
                        lastIntensity = amp;
                        Har.SetEventIntensity(effectId, amp);
                    }

                    // 5. Advance and render: the DeviceRenderLoop order.
                    Har.ComputeAllEvents(clock.Elapsed.TotalSeconds);
                    if (providerUp && Provider.ProviderIsPresent())
                        Provider.ProviderRenderHaptics();

                    Thread.Sleep(_tickMs);
                }
            }
            catch (Exception ex)
            {
                PadForge.Engine.SdlDiagLog.WriteLine("SENSA worker fault: " + ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref s_publisherArmed, 0);
                Volatile.Write(ref s_amplitudeBits, 0);
                if (harUp)
                {
                    try { Har.StopAllEvents(); } catch { }
                    if (providerUp)
                    {
                        try { Provider.ProviderClean(); } catch { }
                    }
                    try { Har.Quit(); } catch { }
                }
                Report(SensaServiceState.Stopped);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
