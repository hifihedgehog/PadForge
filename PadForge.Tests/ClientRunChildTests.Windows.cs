using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  The window watch
        // ─────────────────────────────────────────────

        /// <summary>
        /// Every window a process makes, shows, hides and destroys, by
        /// events and not by looks (#600 item 52, AS-194). A thread of its
        /// own pumps messages from before any child starts, and holds an
        /// out-of-context WinEvent hook per process it follows
        /// (SetWinEventHook with the process's ID, EVENT_OBJECT_CREATE to
        /// EVENT_OBJECT_HIDE). Windows queues each event for that thread as
        /// it is raised ("Because the hook function is called across process
        /// boundaries, the system must queue events. Although this method is
        /// asynchronous, events are guaranteed to be in sequential order",
        /// Microsoft, SetWinEventHook), so a window shown and hidden between
        /// two looks of a listing still comes through. A child is followed
        /// while it is suspended, before it runs any code of its own.
        /// </summary>
        internal sealed class WindowWatch : IDisposable
        {
            /// <summary>One event of a window object: its process, its kind,
            /// the window, the thread that raised it, and its class and
            /// whether it was visible when the watch took it, which a window
            /// already destroyed by then no longer has.</summary>
            internal readonly record struct Event(int Process, uint Kind, IntPtr Window, uint Thread, string Class, bool VisibleThen)
            {
                public override string ToString()
                    => KindWord(Kind) + " " + Window.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                       + " \"" + Class + "\" thread " + Thread + (VisibleThen ? " visible" : string.Empty);
            }

            private readonly Thread _thread;
            private readonly ManualResetEventSlim _ready = new();
            private readonly ConcurrentQueue<Action> _work = new();
            private readonly List<Event> _events = new();
            private readonly Dictionary<IntPtr, int> _hooks = new();
            private readonly WinEventProc _callback;
            private uint _threadId;
            private Exception _failure;

            public WindowWatch()
            {
                _callback = OnEvent;
                _thread = new Thread(Pump) { IsBackground = true, Name = "PadForge child window watch" };
                _thread.Start();
                Assert.True(_ready.Wait(10000), "the window watch's thread did not start");
                if (_failure != null) ExceptionDispatchInfo.Capture(_failure).Throw();
            }

            private void Pump()
            {
                try
                {
                    _threadId = GetCurrentThreadId();
                    // The first call into the message functions gives the
                    // thread its queue, before anything is posted to it.
                    PeekMessageW(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                }
                catch (Exception e) { _failure = e; }
                finally { _ready.Set(); }
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == WmRunWork)
                    {
                        while (_work.TryDequeue(out var job)) job();
                        continue;
                    }
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
                foreach (var hook in _hooks.Keys) UnhookWinEvent(hook);
            }

            /// <summary>Runs <paramref name="action"/> on the watch's thread
            /// and waits for it: the hooks are made and read there alone.</summary>
            private void OnWatchThread(Action action)
            {
                using var done = new ManualResetEventSlim();
                Exception failure = null;
                _work.Enqueue(() =>
                {
                    try { action(); }
                    catch (Exception e) { failure = e; }
                    finally { done.Set(); }
                });
                Assert.True(PostThreadMessageW(_threadId, WmRunWork, IntPtr.Zero, IntPtr.Zero), "the window watch's thread takes no message: " + Marshal.GetLastWin32Error());
                Assert.True(done.Wait(10000), "the window watch's thread did not answer");
                if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            }

            /// <summary>Follows every window of a process from now, or the
            /// windows of one of its threads. A thread's hook names the thread
            /// alone, the form Windows documents for it ("If the idProcess
            /// parameter is zero and idThread is nonzero, the hook function
            /// receives the specified events only from the thread specified
            /// by idThread", SetWinEventHook's remarks), and its events count
            /// as the process's.</summary>
            public void Follow(int processId, int threadId = 0)
                => OnWatchThread(() =>
                {
                    IntPtr hook = SetWinEventHook(EventObjectCreate, EventObjectHide, IntPtr.Zero, _callback,
                        threadId != 0 ? 0u : (uint)processId, (uint)threadId, WineventOutOfContext);
                    Assert.True(hook != IntPtr.Zero, "no window hook for process " + processId + ": " + Marshal.GetLastWin32Error());
                    _hooks[hook] = processId;
                });

            private void OnEvent(IntPtr hook, uint kind, IntPtr window, int idObject, int idChild, uint thread, uint time)
            {
                // A window itself: not its caret, its cursor or a part of it.
                if (idObject != ObjidWindow || idChild != ChildidSelf || window == IntPtr.Zero) return;
                if (!_hooks.TryGetValue(hook, out int process)) return;
                var name = new StringBuilder(256);
                GetClassNameW(window, name, name.Capacity);
                var seen = new Event(process, kind, window, thread, name.ToString(), IsWindowVisible(window));
                lock (_events) _events.Add(seen);
            }

            /// <summary>Every event of a process taken so far.</summary>
            public List<Event> Of(int processId)
            {
                lock (_events) return _events.Where(e => e.Process == processId).ToList();
            }

            /// <summary>The windows a process showed: a show event, or a
            /// window the watch found visible as it took its creation.</summary>
            public List<Event> Shown(int processId)
                => Of(processId).Where(e => e.Kind == EventObjectShow || (e.Kind == EventObjectCreate && e.VisibleThen)).ToList();

            /// <summary>Waits up to <paramref name="ms"/> for an event of a
            /// process that matches.</summary>
            public bool WaitFor(int processId, Func<Event, bool> match, int ms = 10000)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < ms)
                {
                    if (Of(processId).Any(match)) return true;
                    Thread.Sleep(20);
                }
                return Of(processId).Any(match);
            }

            /// <summary>
            /// Shows and hides a window of this process at once and waits
            /// for its show. Windows hands this thread its events in the order
            /// they were raised, so every event raised before the window's
            /// has been taken once its show is in, a child's included. It is
            /// the watch's own positive control, made in the same watch as
            /// what it checks. Gives the window's handle.
            /// </summary>
            public IntPtr Drain(bool madeVisible = false)
            {
                IntPtr window = FlashAWindow(this, madeVisible);
                Assert.True(WaitFor(Environment.ProcessId, e => e.Window == window && e.Kind == EventObjectShow),
                    "the watch never took the show of a window this test showed and hid at once. Taken: "
                    + string.Join(", ", Of(Environment.ProcessId)));
                return window;
            }

            public void Dispose()
            {
                if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
                _thread.Join(10000);
                _ready.Dispose();
            }

            /// <summary>Disposes the watch when the caller made it.</summary>
            public void DisposeIfOwned(bool owned)
            {
                if (owned) Dispose();
            }
        }

        internal static string KindWord(uint kind) => kind switch
        {
            EventObjectCreate => "create",
            EventObjectDestroy => "destroy",
            EventObjectShow => "show",
            EventObjectHide => "hide",
            _ => "0x" + kind.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// A top-level window of this process, on a thread of its own that
        /// the watch follows before the window is made: shown and hidden at
        /// once and destroyed, or with <paramref name="madeVisible"/> made
        /// visible by its style and destroyed at once. It sits off every
        /// screen, takes no activation and has no taskbar button, so nothing
        /// shows in front of the person at the PC, as the reference's own
        /// window test keeps its form at -32000.
        /// </summary>
        internal static IntPtr FlashAWindow(WindowWatch watch, bool madeVisible = false)
        {
            IntPtr window = IntPtr.Zero;
            int threadId = 0;
            Exception failure = null;
            using var named = new ManualResetEventSlim();
            using var followed = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    threadId = (int)GetCurrentThreadId();
                    named.Set();
                    Assert.True(followed.Wait(10000), "the watch did not follow the window's thread");
                    uint style = WsPopup | (madeVisible ? WsVisible : 0);
                    window = CreateWindowExW(WsExToolWindow | WsExNoActivate, "STATIC", "PadForge child test flash", style,
                        -32000, -32000, 10, 10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    Assert.True(window != IntPtr.Zero, "no window was made: " + Marshal.GetLastWin32Error());
                    if (!madeVisible)
                    {
                        ShowWindow(window, SwShowNoActivate);
                        ShowWindow(window, SwHide);
                    }
                    DestroyWindow(window);
                }
                catch (Exception e) { failure = e; }
                finally { named.Set(); }
            }) { IsBackground = true, Name = "PadForge child test flash" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(named.Wait(10000), "the window's thread did not start");
            if (failure == null)
            {
                watch.Follow(Environment.ProcessId, threadId);
                followed.Set();
            }
            Assert.True(thread.Join(20000), "the window's thread did not end");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            return window;
        }

        /// <summary>The watch takes a window that is shown and hidden at
        /// once, which a listing every 25 ms, as the child tests had, sees
        /// only when a look falls between the show and the hide. Its create,
        /// show, hide and destroy come in the order they were raised.</summary>
        [Fact]
        public void AWindowShownAndHiddenAtOnce_IsTakenByTheWatch()
        {
            using var watch = new WindowWatch();
            IntPtr window = watch.Drain();
            var kinds = watch.Of(Environment.ProcessId).Where(e => e.Window == window).Select(e => e.Kind).ToList();
            Assert.True(InOrder(kinds, EventObjectCreate, EventObjectShow, EventObjectHide, EventObjectDestroy),
                "the window's events: " + string.Join(", ", kinds.Select(KindWord)));
            Assert.Contains(watch.Shown(Environment.ProcessId), e => e.Window == window);
        }

        /// <summary>Whether <paramref name="wanted"/> come in
        /// <paramref name="kinds"/> in that order, with anything between.</summary>
        private static bool InOrder(IReadOnlyList<uint> kinds, params uint[] wanted)
        {
            int at = 0;
            foreach (uint kind in kinds)
                if (at < wanted.Length && kind == wanted[at]) at++;
            return at == wanted.Length;
        }

        /// <summary>A window made with WS_VISIBLE and destroyed at once,
        /// never passed to ShowWindow, is taken as shown as well: the window
        /// check holds for a window that never goes through ShowWindow.</summary>
        [Fact]
        public void AWindowMadeVisibleAndDestroyedAtOnce_IsTakenAsShown()
        {
            using var watch = new WindowWatch();
            IntPtr window = watch.Drain(madeVisible: true);
            Assert.Contains(watch.Shown(Environment.ProcessId), e => e.Window == window);
        }

        // ─────────────────────────────────────────────
        //  The watch's calls
        // ─────────────────────────────────────────────

        private const uint EventObjectCreate = 0x8000, EventObjectDestroy = 0x8001, EventObjectShow = 0x8002, EventObjectHide = 0x8003;
        private const uint WineventOutOfContext = 0x0000;
        private const int ObjidWindow = 0, ChildidSelf = 0;
        private const uint WmQuit = 0x0012, WmRunWork = 0x8000 + 0x52, PmNoRemove = 0x0000;
        private const uint WsPopup = 0x80000000, WsVisible = 0x10000000;
        private const uint WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
        private const int SwHide = 0, SwShowNoActivate = 4;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int idObject, int idChild, uint eventThread, uint eventTime);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX, ptY;
            public uint lPrivate;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG msg, IntPtr window, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessageW(out MSG msg, IntPtr window, uint filterMin, uint filterMax, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
