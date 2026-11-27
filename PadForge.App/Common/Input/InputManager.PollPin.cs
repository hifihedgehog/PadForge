using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>The poll thread's core under the PADFORGE_POLL_PIN bench
        /// switch: left free (unset or 0, the default), held to one core (1),
        /// or free and held by turns, one heartbeat window each (flip).</summary>
        internal enum PollPinMode { Free, Pinned, Flip }

        internal static PollPinMode ParsePollPin(string value)
        {
            if (value == "1") return PollPinMode.Pinned;
            if (string.Equals(value, "flip", StringComparison.OrdinalIgnoreCase)) return PollPinMode.Flip;
            return PollPinMode.Free;
        }

        private static readonly PollPinMode s_pollPin = ParsePollPin(Environment.GetEnvironmentVariable("PADFORGE_POLL_PIN"));

        // Poll thread only: the switch's state for this loop.
        private PollPinMode _pinMode;
        private UIntPtr _pinFreeMask;
        private int _pinCore = -1;
        private bool _pinHeld;

        /// <summary>The core the poll thread is held to: within
        /// <paramref name="processMask"/>, never logical processor 0, and of
        /// the highest efficiency class there (the performance cores of a
        /// CPU with mixed core types). Among those, the highest numbered,
        /// the farthest from processor 0, where Windows sends most device
        /// interrupts and deferred calls. -1 when no processor qualifies.
        /// <paramref name="cpus"/> holds each processor of group 0 with its
        /// efficiency class.</summary>
        internal static int ChoosePollCore(ulong processMask, IReadOnlyList<(int Index, byte EfficiencyClass)> cpus)
        {
            int best = -1, bestClass = -1;
            foreach (var (index, efficiency) in cpus)
            {
                if (index <= 0 || index >= 64 || (processMask & (1UL << index)) == 0) continue;
                if (efficiency > bestClass || (efficiency == bestClass && index > best))
                {
                    best = index;
                    bestClass = efficiency;
                }
            }
            return best;
        }

        /// <summary>Group 0's processors and their efficiency classes, from
        /// GetSystemCpuSetInformation. Empty when Windows does not answer.</summary>
        private static List<(int Index, byte EfficiencyClass)> ReadCpuSets()
        {
            var cpus = new List<(int, byte)>();
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint needed, IntPtr.Zero, 0);
            if (needed == 0) return cpus;
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, needed, out needed, IntPtr.Zero, 0)) return cpus;
                int offset = 0;
                while (offset + 20 <= needed)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    // SYSTEM_CPU_SET_INFORMATION: Size, Type (0 = CpuSetInformation),
                    // then Id (8), Group (12), LogicalProcessorIndex (14),
                    // CoreIndex, LastLevelCacheIndex, NumaNodeIndex,
                    // EfficiencyClass (18).
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0 && Marshal.ReadInt16(buffer, offset + 12) == 0)
                        cpus.Add((Marshal.ReadByte(buffer, offset + 14), Marshal.ReadByte(buffer, offset + 18)));
                    offset += size;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return cpus;
        }

        /// <summary>At the loop's start, on the poll thread: with the switch
        /// unset nothing is touched. Otherwise the core is chosen, and the
        /// thread is held to it for the first window (1 and flip).</summary>
        private void BeginPollPin()
        {
            _pinMode = s_pollPin;
            _pinHeld = false;
            if (_pinMode == PollPinMode.Free) return;
            try
            {
                // -1 is the current process's own pseudo handle.
                if (!GetProcessAffinityMask(new IntPtr(-1), out UIntPtr processMask, out _))
                    throw new InvalidOperationException("GetProcessAffinityMask failed, error " + Marshal.GetLastWin32Error());
                _pinFreeMask = processMask;
                _pinCore = ChoosePollCore((ulong)processMask, ReadCpuSets());
                if (_pinCore < 0) throw new InvalidOperationException("no processor besides 0 in the process mask");
                HoldPollCore(true);
                Engine.SdlDiagLog.WriteLine($"POLL PIN mode={_pinMode} core={_pinCore}");
            }
            catch (Exception ex)
            {
                _pinMode = PollPinMode.Free;
                Engine.SdlDiagLog.WriteLine("POLL PIN not set, the loop runs free: " + ex.Message);
            }
        }

        private void HoldPollCore(bool hold)
        {
            UIntPtr mask = hold ? (UIntPtr)(1UL << _pinCore) : _pinFreeMask;
            if (SetThreadAffinityMask(GetCurrentThread(), mask) == UIntPtr.Zero)
                throw new InvalidOperationException("SetThreadAffinityMask failed, error " + Marshal.GetLastWin32Error());
            _pinHeld = hold;
        }

        /// <summary>The heartbeat's word for the window it reports.</summary>
        private string PollPinWord()
            => _pinMode == PollPinMode.Free ? "" : _pinHeld ? $" pin=core{_pinCore}" : " pin=free";

        /// <summary>After each heartbeat, under flip: the next window takes
        /// the other mode. A failure leaves the loop free and says so
        /// once.</summary>
        private void NextPollPinWindow()
        {
            if (_pinMode != PollPinMode.Flip) return;
            try { HoldPollCore(!_pinHeld); }
            catch (Exception ex)
            {
                _pinMode = PollPinMode.Free;
                try { SetThreadAffinityMask(GetCurrentThread(), _pinFreeMask); } catch { }
                Engine.SdlDiagLog.WriteLine("POLL PIN not set, the loop runs free: " + ex.Message);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessAffinityMask(IntPtr process, out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);
    }
}
