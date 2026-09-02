using System;
using System.Runtime.InteropServices;
using NAudio.Dsp;
using NAudio.Wave;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Fourth-order Butterworth low-pass on a stereo float stream (#381):
    /// two cascaded RBJ biquads per channel with the standard fourth-order
    /// Q split (0.5411961, 1.3065630), applied at the 48 kHz mix rate
    /// BEFORE the sinc downsample so actuator content above the cutoff
    /// never reaches the wire. The default 250 Hz cutoff is the
    /// requester's hardware-measured audibility threshold on their Steam
    /// Controller 2026 (discussion #371), deliberately user-tunable
    /// because it is one unit's measurement, not a device specification.
    /// Same NAudio BiQuadFilter machinery MirrorDsp already uses, and the
    /// same swap discipline: SetCutoff is only called from the reading
    /// thread between reads.
    /// </summary>
    internal sealed class TritonPcmLowPassProvider : ISampleProvider
    {
        private const double Q1 = 0.5411961;
        private const double Q2 = 1.3065630;

        private readonly ISampleProvider _src;
        private BiQuadFilter _l1, _l2, _r1, _r2;
        private int _cutoffHz;

        public TritonPcmLowPassProvider(ISampleProvider source, int cutoffHz)
        {
            _src = source;
            SetCutoff(cutoffHz);
        }

        public WaveFormat WaveFormat => _src.WaveFormat;

        public int CutoffHz => _cutoffHz;

        /// <summary>Rebuilds the four biquads for a new cutoff. Reader
        /// thread only. The cutoff is clamped well below Nyquist of the
        /// 8 kHz stream target so the filtered band survives the
        /// downsample intact.</summary>
        public void SetCutoff(int cutoffHz)
        {
            _cutoffHz = Math.Clamp(cutoffHz, 60, 1000);
            int rate = _src.WaveFormat.SampleRate;
            _l1 = BiQuadFilter.LowPassFilter(rate, _cutoffHz, (float)Q1);
            _l2 = BiQuadFilter.LowPassFilter(rate, _cutoffHz, (float)Q2);
            _r1 = BiQuadFilter.LowPassFilter(rate, _cutoffHz, (float)Q1);
            _r2 = BiQuadFilter.LowPassFilter(rate, _cutoffHz, (float)Q2);
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int got = _src.Read(buffer, offset, count);
            int ch = _src.WaveFormat.Channels;
            if (ch == 2)
            {
                for (int i = offset; i + 1 < offset + got; i += 2)
                {
                    buffer[i] = _l2.Transform(_l1.Transform(buffer[i]));
                    buffer[i + 1] = _r2.Transform(_r1.Transform(buffer[i + 1]));
                }
            }
            else
            {
                for (int i = offset; i < offset + got; i++)
                    buffer[i] = _l2.Transform(_l1.Transform(buffer[i]));
            }
            return got;
        }
    }

    /// <summary>
    /// Small overlapped WriteFile ring for the Triton PCM stream (#381):
    /// up to Slots 0x88 reports in flight so submission cadence is set by
    /// the pacing clock, not per-write completion latency. This is the
    /// answer to the requester's Puck finding (synchronous writes
    /// sustained about 250 reports a second against a needed 258, so
    /// latency grew about 31 ms every second): with queued URBs the USB
    /// stack fills every interrupt-pipe slot, which none of the
    /// reference tools could do through synchronous hidapi writes. The
    /// shape follows AudioPassthroughService's BtWritePool precedent:
    /// pre-pinned buffers, per-slot events, harvest on the next submit,
    /// leak rather than free anything the kernel may still touch.
    /// Single-threaded: only the sink's stream thread calls it.
    /// </summary>
    internal sealed class TritonPcmWriteRing : IDisposable
    {
        /// <summary>Reports in flight at once. The wired 16-bit mode packs
        /// TritonPcmEncoder.FramesPerPacket16 = 15 frames per packet and
        /// the tick produces HapticToneService.PcmFramesPerTick = 80 frames
        /// every 10 ms, so a tick submits six packets (5.33, the fraction
        /// carrying into the next tick). Four slots left the fifth and
        /// sixth submit refused every tick, PcmPending pinned at its cap,
        /// and a quarter of every second dropped (F18). Eight is six plus
        /// two of completion-jitter headroom.</summary>
        internal const int Slots = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlapped64
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attrs, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr hEvent);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr hFile, IntPtr buffer, uint count, IntPtr written, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr hFile, IntPtr overlapped, out uint transferred, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int ErrorIoPending = 997;
        private const uint WaitObject0 = 0;

        private readonly int _reportLen;
        private readonly byte[][] _bufs = new byte[Slots][];
        private readonly GCHandle[] _pins = new GCHandle[Slots];
        private readonly IntPtr[] _events = new IntPtr[Slots];
        private readonly IntPtr[] _ovls = new IntPtr[Slots];
        private readonly bool[] _busy = new bool[Slots];
        private bool _disposed;

        /// <summary>Writes that failed outright, for the diag line.</summary>
        public long HardFailures;

        public TritonPcmWriteRing(int reportLen)
            : this(reportLen, () => CreateEventW(IntPtr.Zero, manualReset: true, initialState: true, null))
        {
        }

        /// <summary>Test seam (InternalsVisibleTo PadForge.Tests): the
        /// event factory is injectable so the zero-handle path can be
        /// driven without exhausting kernel handles. Throws
        /// InvalidOperationException when any event fails to create,
        /// after releasing everything allocated so far (F16): a zero
        /// event is never signaled, so its slot would never be reclaimed
        /// and the ring would go silent once every slot had been used.</summary>
        internal TritonPcmWriteRing(int reportLen, Func<IntPtr> createEvent)
        {
            _reportLen = Math.Max(reportLen, 64);
            for (int i = 0; i < Slots; i++)
            {
                _bufs[i] = new byte[_reportLen];
                _pins[i] = GCHandle.Alloc(_bufs[i], GCHandleType.Pinned);
                _events[i] = createEvent();
                if (_events[i] == IntPtr.Zero)
                {
                    // Nothing has reached the kernel yet, so every slot
                    // built so far can be freed outright.
                    int err = Marshal.GetLastWin32Error();
                    for (int j = 0; j <= i; j++)
                    {
                        if (_events[j] != IntPtr.Zero) { try { CloseHandle(_events[j]); } catch { } }
                        if (_ovls[j] != IntPtr.Zero) { try { Marshal.FreeHGlobal(_ovls[j]); } catch { } }
                        if (_pins[j].IsAllocated) { try { _pins[j].Free(); } catch { } }
                    }
                    throw new InvalidOperationException($"CreateEventW returned a zero handle for slot {i} (err={err})");
                }
                _ovls[i] = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped64>());
                var ovl = new NativeOverlapped64 { EventHandle = _events[i] };
                Marshal.StructureToPtr(ovl, _ovls[i], false);
            }
        }

        /// <summary>Tries to submit one report. Returns false when every
        /// slot still has a write in flight (the caller keeps the
        /// frames pending and retries next tick). A hard write failure
        /// consumes the report and counts it, so a dead handle degrades
        /// to counted drops instead of a stalled stream.</summary>
        public bool TrySubmit(IntPtr device, ReadOnlySpan<byte> report)
        {
            if (_disposed || device == IntPtr.Zero) return true;
            int slot = -1;
            for (int i = 0; i < Slots; i++)
            {
                if (!_busy[i]) { slot = i; break; }
                if (WaitForSingleObject(_events[i], 0) == WaitObject0)
                {
                    GetOverlappedResult(device, _ovls[i], out _, wait: false);
                    _busy[i] = false;
                    slot = i;
                    break;
                }
            }
            if (slot < 0) return false;

            var buf = _bufs[slot];
            int n = Math.Min(report.Length, buf.Length);
            report.Slice(0, n).CopyTo(buf);
            Array.Clear(buf, n, buf.Length - n);

            ResetEvent(_events[slot]);
            bool ok = WriteFile(device, _pins[slot].AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, _ovls[slot]);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorIoPending)
                {
                    _busy[slot] = true;
                    return true;
                }
                HardFailures++;
                return true;
            }
            // Synchronous completion still signals the event, so the slot
            // is immediately reclaimable on the next pass.
            _busy[slot] = true;
            return true;
        }

        /// <summary>Best-effort release. Anything the kernel may still be
        /// writing stays allocated (the OverlappedWrite leak-on-stall
        /// discipline): the events and pins for slots whose I/O never
        /// completed are deliberately kept.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            for (int i = 0; i < Slots; i++)
            {
                bool safe = !_busy[i] || WaitForSingleObject(_events[i], 200) == WaitObject0;
                if (safe)
                {
                    try { CloseHandle(_events[i]); } catch { }
                    try { Marshal.FreeHGlobal(_ovls[i]); } catch { }
                    try { _pins[i].Free(); } catch { }
                }
            }
        }
    }
}
