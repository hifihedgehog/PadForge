using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PadForge.Common.Recording
{
    /// <summary>
    /// What a problem recording says of PadForge's window (#600): whether it
    /// answers, and while it does not, the dispatcher operation its thread
    /// runs, the last window message it took, whether the thread uses the
    /// processor or waits, and Windows' wait chain of the thread
    /// (GetThreadWaitChain), which names a deadlock. No managed stack is
    /// read: .NET has no way to read another thread's stack in process, and
    /// PadForge's single-file build carries no debugging component to read
    /// it from outside.
    ///
    /// <para>The window's thread beats every 250 ms while something is
    /// captured (a timer on its dispatcher), keeps the operation it runs
    /// (the dispatcher's hooks) and the message it took (ComponentDispatcher),
    /// each a field write. The recorder's own thread reads them.</para>
    /// </summary>
    internal static class WindowWatch
    {
        private static int _threadId;
        private static IntPtr _thread;
        private static long _beat;
        private static volatile DispatcherOperation _operation;
        private static int _message;
        private static DispatcherTimer _timer;
        private static Dispatcher _dispatcher;
        private static volatile string _shown;

        /// <summary>The beat's interval.</summary>
        internal static readonly TimeSpan BeatEvery = TimeSpan.FromMilliseconds(250);

        /// <summary>Whether the window's thread is watched.</summary>
        internal static bool Installed => _dispatcher != null;

        /// <summary>When the window's thread last beat, on the Stopwatch's
        /// clock, or 0 while it does not beat.</summary>
        internal static long LastBeat => Interlocked.Read(ref _beat);

        /// <summary>How the window is shown: in front, behind another window,
        /// minimized, hidden in the tray or closed.</summary>
        internal static string Shown => _shown;

        /// <summary>The window's display scale, in percent, or 0.</summary>
        internal static int DisplayScale;

        /// <summary>Starts watching the thread it is called on, the window's.
        /// Once.</summary>
        internal static void Install(Dispatcher dispatcher)
        {
            if (_dispatcher != null || dispatcher == null) return;
            _dispatcher = dispatcher;
            _threadId = GetCurrentThreadId();
            _thread = OpenThread(ThreadQueryLimitedInformation, false, _threadId);
            dispatcher.Hooks.OperationStarted += (s, e) => _operation = e.Operation;
            dispatcher.Hooks.OperationCompleted += (s, e) => _operation = null;
            dispatcher.Hooks.OperationAborted += (s, e) => _operation = null;
            ComponentDispatcher.ThreadFilterMessage += OnMessage;
            _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = BeatEvery };
            _timer.Tick += (s, e) => Interlocked.Exchange(ref _beat, Stopwatch.GetTimestamp());
        }

        private static void OnMessage(ref MSG msg, ref bool handled) => _message = msg.message;

        /// <summary>Beats while something is captured, and stops beating when
        /// nothing is. The window's thread.</summary>
        internal static void Beat(bool capturing)
        {
            if (_timer == null) return;
            if (capturing && !_timer.IsEnabled)
            {
                Interlocked.Exchange(ref _beat, Stopwatch.GetTimestamp());
                _timer.Start();
            }
            else if (!capturing && _timer.IsEnabled)
            {
                _timer.Stop();
                Interlocked.Exchange(ref _beat, 0);
            }
        }

        /// <summary>Says how the window is shown now. The window's thread.</summary>
        internal static void SetShown(string shown) => _shown = shown;

        // ─────────────────────────────────────────────
        //  While the window does not answer
        // ─────────────────────────────────────────────

        private static long _stallCpuStart;

        /// <summary>
        /// What the window's thread is in, read from the recorder's thread:
        /// the dispatcher operation, the last message, its processor time
        /// since it stopped answering (busy or waiting) and Windows' wait
        /// chain. All of it code names and numbers.
        /// </summary>
        internal static string Describe(bool first, double stalledSeconds)
        {
            var parts = new List<string>();
            var operation = _operation;
            parts.Add(operation == null ? "in no dispatcher operation" : "in " + OperationName(operation));
            parts.Add("last message " + MessageName(_message));
            long cpu = ThreadCpu(_thread);
            if (first) _stallCpuStart = cpu;
            if (cpu >= 0 && _stallCpuStart >= 0)
            {
                double used = (cpu - _stallCpuStart) / 1e7;
                parts.Add("its thread used " + used.ToString("0.0", CultureInfo.InvariantCulture) + " s of processor time since"
                    + (first ? string.Empty : used > stalledSeconds / 2 ? " (busy)" : " (waiting)"));
            }
            string chain = WaitChain(_threadId);
            if (chain != null) parts.Add(chain);
            return string.Join("; ", parts);
        }

        private static readonly FieldInfo MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The method a dispatcher operation runs, as Type.Method,
        /// with a lambda named after the method it was written in, or the
        /// operation's priority when its method cannot be read.</summary>
        internal static string OperationName(DispatcherOperation operation)
        {
            Delegate method = null;
            try { method = MethodField?.GetValue(operation) as Delegate; }
            catch (Exception ex) when (ex is FieldAccessException or TargetException) { }
            if (method == null) return "an operation at " + operation.Priority + " priority";
            return CodeName(method.Method.DeclaringType, method.Method.Name) + " at " + operation.Priority + " priority";
        }

        /// <summary>Type.Method, with a compiler-made lambda named after the
        /// method it was written in.</summary>
        internal static string CodeName(Type type, string method)
        {
            while (type != null && type.DeclaringType != null && type.Name.StartsWith("<", StringComparison.Ordinal))
                type = type.DeclaringType;
            if (method.StartsWith("<", StringComparison.Ordinal))
            {
                int end = method.IndexOf('>');
                method = (end > 1 ? method.Substring(1, end - 1) : method) + " (a lambda in it)";
            }
            return (type == null ? string.Empty : type.Name + ".") + method;
        }

        internal static string MessageName(int message) => message switch
        {
            0 => "none yet",
            0x000F => "WM_PAINT",
            0x0014 => "WM_ERASEBKGND",
            0x001A => "WM_SETTINGCHANGE",
            0x0024 => "WM_GETMINMAXINFO",
            0x0047 => "WM_WINDOWPOSCHANGED",
            0x0100 => "WM_KEYDOWN",
            0x0101 => "WM_KEYUP",
            0x0113 => "WM_TIMER",
            0x0200 => "WM_MOUSEMOVE",
            0x0201 => "WM_LBUTTONDOWN",
            0x0202 => "WM_LBUTTONUP",
            0x0219 => "WM_DEVICECHANGE",
            0x02E0 => "WM_DPICHANGED",
            0x0312 => "WM_HOTKEY",
            0x031A => "WM_THEMECHANGED",
            >= 0xC000 => "a registered message 0x" + message.ToString("X4", CultureInfo.InvariantCulture) + " (the dispatcher's own)",
            _ => "0x" + message.ToString("X4", CultureInfo.InvariantCulture),
        };

        private const int ThreadQueryLimitedInformation = 0x0800;

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(int access, bool inherit, int threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        /// <summary>A thread's processor time in 100 ns units, or -1.</summary>
        internal static long ThreadCpu(IntPtr thread)
        {
            if (thread == IntPtr.Zero || !GetThreadTimes(thread, out _, out _, out long kernel, out long user)) return -1;
            return kernel + user;
        }

        /// <summary>A thread of this process, opened to read its processor
        /// time, for a test.</summary>
        internal static IntPtr OpenForTimes(int threadId) => OpenThread(ThreadQueryLimitedInformation, false, threadId);

        /// <summary>The calling thread's native ID.</summary>
        internal static int CurrentThreadId() => GetCurrentThreadId();

        // ─────────────────────────────────────────────
        //  What Windows sees the thread wait on
        // ─────────────────────────────────────────────

        /// <summary>WAITCHAIN_NODE_INFO (wct.h): 280 bytes, the object's type
        /// and status, then a union whose thread member starts with the
        /// process and thread IDs.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 280)]
        private struct WaitNode
        {
            [FieldOffset(0)] public int ObjectType;
            [FieldOffset(4)] public int ObjectStatus;
            [FieldOffset(8)] public int ProcessId;
            [FieldOffset(12)] public int ThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenThreadWaitChainSession(int flags, IntPtr callback);

        [DllImport("advapi32.dll")]
        private static extern void CloseThreadWaitChainSession(IntPtr session);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, int flags, int threadId,
            ref int nodeCount, [Out] WaitNode[] nodes, out bool isCycle);

        private const int ThreadNode = 8;
        private const int StatusBlocked = 3;

        /// <summary>
        /// What Windows sees a thread of this process wait on, in order, or
        /// null when it does not say. Windows names a critical section, a
        /// window message sent to another thread, a mutex, a call to a Windows
        /// service, COM, a thread or a process. A .NET lock or event is not
        /// among them, so a thread blocked on one shows as blocked on nothing
        /// named. Windows also says when the waits go round in a circle: a
        /// deadlock.
        /// </summary>
        internal static string WaitChain(int threadId)
        {
            if (threadId == 0) return null;
            IntPtr session = OpenThreadWaitChainSession(0, IntPtr.Zero);
            if (session == IntPtr.Zero) return null;
            try
            {
                var nodes = new WaitNode[16];
                int count = nodes.Length;
                if (!GetThreadWaitChain(session, IntPtr.Zero, 0, threadId, ref count, nodes, out bool cycle) || count < 1) return null;
                int self = Environment.ProcessId;
                var parts = new List<string>();
                for (int i = 1; i < count && i < nodes.Length; i++)
                {
                    var node = nodes[i];
                    parts.Add(node.ObjectType == ThreadNode
                        ? (node.ProcessId == self ? "a PadForge thread" : "a thread of another program")
                        : WaitName(node.ObjectType));
                }
                string text = parts.Count == 0
                    ? (nodes[0].ObjectStatus == StatusBlocked ? "Windows sees it blocked on nothing it can name (a .NET lock or event)" : "Windows sees it running")
                    : "Windows sees it wait on " + string.Join(", held by ", parts);
                return cycle ? text + ", in a circle: a deadlock" : text;
            }
            finally
            {
                CloseThreadWaitChainSession(session);
            }
        }

        private static string WaitName(int type) => type switch
        {
            1 => "a critical section",
            2 => "a window message sent to another thread",
            3 => "a mutex",
            4 => "a call to a Windows service",
            5 => "COM",
            6 => "a thread",
            7 => "a process",
            9 => "COM activation",
            11 => "a network socket",
            12 => "a file share",
            _ => "something Windows does not name",
        };
    }
}
