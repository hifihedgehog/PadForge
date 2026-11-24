using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PadForge.Services.Plugins
{
    /// <summary>
    /// The clock a built-in plugin's thread waits on (#597): a
    /// high-resolution waitable timer, as the poll loop sleeps on
    /// (InputManager.SleepOnTimer), so Replay's frames leave within a
    /// millisecond or two of their time instead of the 15.6 ms the default
    /// clock gives. Failing that, an ordinary sleep. One per thread.
    /// </summary>
    internal sealed class FeederClock : IDisposable
    {
        private IntPtr _timer;

        public FeederClock()
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer == IntPtr.Zero)
                _timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, 0, TimerAllAccess);
        }

        /// <summary>Sleeps for <paramref name="ticks"/> of
        /// <see cref="Stopwatch"/> time. Never ends before it, and ends on the
        /// clock's next tick after it.</summary>
        public void Sleep(long ticks)
        {
            if (ticks <= 0) return;
            long dueTime = -(ticks * 10_000_000 / Stopwatch.Frequency);
            if (dueTime > -1) dueTime = -1;
            if (_timer != IntPtr.Zero && SetWaitableTimerEx(_timer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
            Thread.Sleep((int)Math.Max(1, ticks * 1000 / Stopwatch.Frequency));
        }

        public void Dispose()
        {
            var timer = Interlocked.Exchange(ref _timer, IntPtr.Zero);
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }

        private const uint CreateWaitableTimerHighResolution = 0x00000002;
        private const uint TimerAllAccess = 0x1F0003;
        private const uint Infinite = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, IntPtr name, uint flags, uint access);

        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period,
            IntPtr completion, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
