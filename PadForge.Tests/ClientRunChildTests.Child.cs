using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using Xunit;

namespace PadForge.Tests
{
    public partial class ClientRunChildTests
    {
        // ─────────────────────────────────────────────
        //  A child of this process
        // ─────────────────────────────────────────────

        /// <summary>
        /// A child of this process, started the way PadForge starts a plugin
        /// (PluginLauncher.Start, PadForge.App/Services/Plugins/PluginLauncher.cs:183-216):
        /// made suspended, put in a job that ends it and whatever it starts
        /// when this object lets go, followed by a window watch, and only
        /// then let run. Its process is known, held in the job and watched
        /// before it runs one instruction of its own.
        ///
        /// <para>Its standard input, output and error are anonymous pipes of
        /// this process, and those three handles are the only ones it
        /// inherits (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), so no handle another
        /// test holds open rides along. Its standard output is read byte for
        /// byte, as lines, and as a stream a protocol client reads.</para>
        ///
        /// <para>With <c>debugged</c> this process is its debugger as well:
        /// Windows reports each image it maps (LOAD_DLL_DEBUG_EVENT), and its
        /// exit stops it with its address space whole, which is where the
        /// listing of its mapped files is taken
        /// (EXIT_PROCESS_DEBUG_EVENT, the moment ProcDump's -t writes its dump
        /// at). A run that ends at once is then checked by everything it
        /// mapped up to its last instruction.</para>
        ///
        /// <para>A PadForge.exe child names a pipe of the test's own (#600
        /// item 52): its run is then a test's (ClientRun.Seams.ForThisRun),
        /// which reaches that pipe alone and never a PadForge that runs on
        /// the PC.</para>
        /// </summary>
        internal sealed class Child : IDisposable
        {
            public readonly int Id;
            public readonly SafeProcessHandle Handle;
            public readonly WindowWatch Watch;
            public readonly bool Debugged;
            private readonly bool _ownWatch;
            private readonly SafeFileHandle _job;
            private readonly AnonymousPipeServerStream _input, _output, _error;
            private readonly MemoryStream _stdout = new();
            private readonly BlockingCollection<string> _lines = new();
            private readonly Channel<byte[]> _protocol = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
            private readonly StringBuilder _stderr = new();
            private readonly Thread _reading, _draining, _ending;
            private readonly ManualResetEventSlim _exited = new();
            private readonly object _gate = new();
            private Action<int> _exitedHandlers;
            private int? _exitCode;
            private Stream _protocolOutput;

            /// <summary>The file name of each image Windows reported the child
            /// mapped, with <c>debugged</c>.</summary>
            public readonly ConcurrentQueue<string> LoadedImages = new();

            /// <summary>The file name of every image and file the child had
            /// mapped at its exit event, with <c>debugged</c>, or null before
            /// it.</summary>
            public HashSet<string> MappedAtExit { get; private set; }

            /// <summary>Why the listing at the exit event could not be taken,
            /// or null.</summary>
            public string ExitListingFailure { get; private set; }

            public Child(string exe, string arguments, IReadOnlyDictionary<string, string> variables, WindowWatch watch = null, bool debugged = false)
            {
                variables ??= new Dictionary<string, string>();
                if (string.Equals(Path.GetFileName(exe), "PadForge.exe", StringComparison.OrdinalIgnoreCase))
                {
                    variables.TryGetValue(WebHelper.PipeVariable, out string pipe);
                    Assert.False(string.IsNullOrWhiteSpace(pipe) || PipeOwner.IsOwnName(pipe),
                        "A PadForge.exe child names a pipe of the test's own in " + WebHelper.PipeVariable
                        + ", so that its run is a test's and reaches no PadForge that runs on the PC.");
                }
                Debugged = debugged;
                _ownWatch = watch == null;
                Watch = watch ?? new WindowWatch();
                _input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
                _output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                _error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                var start = new Start(exe, arguments, EnvironmentBlock(variables, debugged), Path.GetDirectoryName(exe), debugged,
                    new[] { _input.ClientSafePipeHandle.DangerousGetHandle(), _output.ClientSafePipeHandle.DangerousGetHandle(), _error.ClientSafePipeHandle.DangerousGetHandle() });

                // Windows reports a debuggee's events to the thread that made
                // it, so a debugged child is made on the thread that waits.
                var made = new TaskCompletionSource<PROCESS_INFORMATION>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (debugged)
                {
                    _ending = new Thread(() => DebugLoop(start, made)) { IsBackground = true, Name = "PadForge child debugger" };
                    _ending.Start();
                }
                else
                {
                    try { made.SetResult(Create(start)); }
                    catch (Exception e) { made.SetException(e); }
                }
                PROCESS_INFORMATION info;
                try { info = made.Task.GetAwaiter().GetResult(); }
                catch
                {
                    Watch.DisposeIfOwned(_ownWatch);
                    throw;
                }
                Id = info.dwProcessId;
                Handle = new SafeProcessHandle(info.hProcess, ownsHandle: true);
                // The child holds its ends now. This process keeps its own.
                _input.DisposeLocalCopyOfClientHandle();
                _output.DisposeLocalCopyOfClientHandle();
                _error.DisposeLocalCopyOfClientHandle();

                _job = CreateJobObjectW(IntPtr.Zero, null);
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                bool held = !_job.IsInvalid
                    && SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
                    && AssignProcessToJobObject(_job, Handle);
                int jobError = Marshal.GetLastWin32Error();
                Exception followFailure = null;
                if (held)
                {
                    try { Watch.Follow(Id); }
                    catch (Exception e) { followFailure = e; }
                }
                if (!held || followFailure != null)
                {
                    TerminateProcess(Handle, 1);
                    CloseHandle(info.hThread);
                    if (!debugged) Handle.Dispose();
                    _job.Dispose();
                    Watch.DisposeIfOwned(_ownWatch);
                    if (followFailure != null) ExceptionDispatchInfo.Capture(followFailure).Throw();
                    Assert.Fail("the child is in no job: " + jobError);
                }

                _reading = new Thread(ReadOut) { IsBackground = true, Name = "PadForge child output" };
                _draining = new Thread(DrainErrors) { IsBackground = true, Name = "PadForge child errors" };
                _reading.Start();
                _draining.Start();
                if (!debugged)
                {
                    _ending = new Thread(WaitForEnd) { IsBackground = true, Name = "PadForge child end" };
                    _ending.Start();
                }
                // Let it run.
                ResumeThread(info.hThread);
                CloseHandle(info.hThread);
            }

            private sealed record Start(string Exe, string Arguments, string Block, string Folder, bool Debugged, IntPtr[] Inherit);

            /// <summary>This process's variables with <paramref name="variables"/>
            /// in place of any of the same name, PadForge's web key left out
            /// unless given, sorted as CreateProcess wants them.</summary>
            private static string EnvironmentBlock(IReadOnlyDictionary<string, string> variables, bool debugged)
            {
                var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    all[(string)entry.Key] = (string)entry.Value;
                all.Remove(WebHelper.KeyVariable);
                // A process made under a debugger gets Windows' debug heap
                // unless this is set, which would make the debugged run differ
                // from a host's start.
                if (debugged) all["_NO_DEBUG_HEAP"] = "1";
                foreach (var (name, value) in variables)
                {
                    if (value == null) all.Remove(name);
                    else all[name] = value;
                }
                var block = new StringBuilder();
                foreach (var (name, value) in all) block.Append(name).Append('=').Append(value).Append('\0');
                return block.Append('\0').ToString();
            }

            private static PROCESS_INFORMATION Create(Start start)
            {
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                IntPtr list = Marshal.AllocHGlobal(size);
                IntPtr handles = Marshal.AllocHGlobal(IntPtr.Size * start.Inherit.Length);
                IntPtr environment = Marshal.StringToHGlobalUni(start.Block);
                bool listMade = false;
                try
                {
                    Assert.True(InitializeProcThreadAttributeList(list, 1, 0, ref size), "no attribute list: " + Marshal.GetLastWin32Error());
                    listMade = true;
                    for (int i = 0; i < start.Inherit.Length; i++) Marshal.WriteIntPtr(handles, i * IntPtr.Size, start.Inherit[i]);
                    Assert.True(UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, handles, (IntPtr)(IntPtr.Size * start.Inherit.Length), IntPtr.Zero, IntPtr.Zero),
                        "the handle list was not set: " + Marshal.GetLastWin32Error());
                    var startup = new STARTUPINFOEX();
                    startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    startup.StartupInfo.dwFlags = StartfUseStdHandles;
                    startup.StartupInfo.hStdInput = start.Inherit[0];
                    startup.StartupInfo.hStdOutput = start.Inherit[1];
                    startup.StartupInfo.hStdError = start.Inherit[2];
                    startup.lpAttributeList = list;
                    var line = new StringBuilder("\"" + start.Exe + "\"" + (string.IsNullOrEmpty(start.Arguments) ? string.Empty : " " + start.Arguments));
                    // No show state is asked for: a window the child made
                    // would show as a plain start shows it, the strictest a
                    // host's start can be. CREATE_NO_WINDOW keeps a console
                    // child off the desktop and does nothing to a windowed one.
                    uint flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow
                        | (start.Debugged ? DebugOnlyThisProcess : 0);
                    // A start that needed rights fails here with Windows'
                    // ERROR_ELEVATION_REQUIRED: no prompt can stand between.
                    Assert.True(CreateProcessW(start.Exe, line, IntPtr.Zero, IntPtr.Zero, true, flags, environment, start.Folder, ref startup, out var info),
                        "the child did not start: " + Marshal.GetLastWin32Error());
                    return info;
                }
                finally
                {
                    if (listMade) DeleteProcThreadAttributeList(list);
                    Marshal.FreeHGlobal(list);
                    Marshal.FreeHGlobal(handles);
                    Marshal.FreeHGlobal(environment);
                }
            }

            // ── Its end ──

            private void WaitForEnd()
            {
                WaitForSingleObject(Handle, Infinite);
                Ended(GetExitCodeProcess(Handle, out uint code) ? unchecked((int)code) : -1);
            }

            private void Ended(int code)
            {
                Action<int> handlers;
                lock (_gate)
                {
                    _exitCode = code;
                    handlers = _exitedHandlers;
                    _exitedHandlers = null;
                }
                _exited.Set();
                ThreadPool.QueueUserWorkItem(_ => handlers?.Invoke(code));
            }

            /// <summary>Raised once the child ended, with its exit code. A
            /// handler added after that is told at once.</summary>
            public event Action<int> Exited
            {
                add
                {
                    int? code;
                    lock (_gate)
                    {
                        code = _exitCode;
                        if (code == null) _exitedHandlers += value;
                    }
                    if (code != null) value?.Invoke(code.Value);
                }
                remove { lock (_gate) _exitedHandlers -= value; }
            }

            public bool HasExited => _exited.IsSet;

            public int ExitCode
            {
                get
                {
                    lock (_gate) return _exitCode ?? throw new InvalidOperationException("the child has not ended");
                }
            }

            /// <summary>
            /// The debugger's loop, on the thread that made the child. Each
            /// event is let go at once: the loader's first breakpoint as
            /// handled, every other exception as not, so the child's own
            /// handlers see it as they would with no debugger. Each image's
            /// file is named as Windows hands it over, and at the exit event
            /// the child's mapped files are listed before it is let go.
            /// </summary>
            private void DebugLoop(Start start, TaskCompletionSource<PROCESS_INFORMATION> made)
            {
                PROCESS_INFORMATION info;
                try { info = Create(start); }
                catch (Exception e)
                {
                    made.SetException(e);
                    return;
                }
                made.SetResult(info);
                IntPtr ev = Marshal.AllocHGlobal(DebugEventBytes);
                try
                {
                    bool loaderBreak = false;
                    while (true)
                    {
                        if (!WaitForDebugEvent(ev, 250))
                        {
                            // A child ended without an exit event (killed
                            // before it ran) leaves nothing to wait for.
                            if (Handle != null && WaitForSingleObject(Handle, 0) == 0)
                            {
                                Ended(GetExitCodeProcess(Handle, out uint left) ? unchecked((int)left) : -1);
                                return;
                            }
                            continue;
                        }
                        uint code = (uint)Marshal.ReadInt32(ev, 0);
                        int processId = Marshal.ReadInt32(ev, 4), threadId = Marshal.ReadInt32(ev, 8);
                        IntPtr union = ev + DebugUnionAt;
                        uint status = DbgContinue;
                        switch (code)
                        {
                            case CreateProcessDebugEvent:
                                CloseFile(Marshal.ReadIntPtr(union, 0));
                                break;
                            case LoadDllDebugEvent:
                            {
                                IntPtr file = Marshal.ReadIntPtr(union, 0);
                                string path = FinalPath(file);
                                CloseFile(file);
                                if (path != null) LoadedImages.Enqueue(Path.GetFileName(path));
                                break;
                            }
                            case ExceptionDebugEvent:
                            {
                                uint exception = (uint)Marshal.ReadInt32(union, 0);
                                if (exception == ExceptionBreakpoint && !loaderBreak) loaderBreak = true;
                                else status = DbgExceptionNotHandled;
                                break;
                            }
                            case ExitProcessDebugEvent:
                            {
                                int exit = Marshal.ReadInt32(union, 0);
                                try { MappedAtExit = MappedFiles(Handle); }
                                catch (Exception e) { ExitListingFailure = e.Message; }
                                ContinueDebugEvent(processId, threadId, DbgContinue);
                                Ended(exit);
                                return;
                            }
                        }
                        ContinueDebugEvent(processId, threadId, status);
                    }
                }
                finally { Marshal.FreeHGlobal(ev); }
            }

            private static void CloseFile(IntPtr file)
            {
                if (file != IntPtr.Zero) CloseHandle(file);
            }

            private static string FinalPath(IntPtr file)
            {
                if (file == IntPtr.Zero) return null;
                var name = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(file, name, (uint)name.Capacity, 0);
                return n > 0 && n < name.Capacity ? name.ToString() : null;
            }

            // ── Its streams ──

            private void ReadOut()
            {
                var line = new List<byte>();
                var buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = _output.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (_stdout) _stdout.Write(buffer, 0, n);
                        _protocol.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
                        for (int i = 0; i < n; i++)
                        {
                            if (buffer[i] == (byte)'\n')
                            {
                                _lines.Add(Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r'));
                                line.Clear();
                            }
                            else line.Add(buffer[i]);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
                finally
                {
                    _lines.CompleteAdding();
                    _protocol.Writer.TryComplete();
                }
            }

            private void DrainErrors()
            {
                var decoder = new UTF8Encoding(false).GetDecoder();
                var bytes = new byte[4096];
                var chars = new char[4096 + 8];
                try
                {
                    int n;
                    while ((n = _error.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        int c = decoder.GetChars(bytes, 0, n, chars, 0);
                        lock (_stderr) _stderr.Append(chars, 0, c);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }

            /// <summary>Every byte the child wrote to standard output so far.</summary>
            public byte[] StandardOutput { get { lock (_stdout) return _stdout.ToArray(); } }

            public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);

            public string StandardError { get { lock (_stderr) return _stderr.ToString(); } }

            /// <summary>The child's standard input, for a protocol client to
            /// write to.</summary>
            public Stream ProtocolInput => _input;

            /// <summary>The child's standard output, for one protocol client to
            /// read: every byte, in order, ended when the child closes it.</summary>
            public Stream ProtocolOutput => _protocolOutput ??= new ChannelStream(_protocol.Reader);

            /// <summary>Closes the child's standard input, as a host does when
            /// it lets go of a server.</summary>
            public void EndInput() => _input.Dispose();

            /// <summary>The next line of standard output, or null when none
            /// comes within <paramref name="ms"/>.</summary>
            public string NextLine(int ms) => _lines.TryTake(out string line, ms) ? line : null;

            /// <summary>Waits for the child to end, then for everything it
            /// wrote.</summary>
            public bool Ends(int ms)
            {
                if (!_exited.Wait(ms)) return false;
                _reading.Join(10000);
                _draining.Join(10000);
                return true;
            }

            public void Kill() => TerminateProcess(Handle, 1);

            /// <summary>Closes the job, which ends the child and what it
            /// started.</summary>
            public void EndJob() => _job.Dispose();

            /// <summary>Whether the test still holds the child's job, so
            /// nothing of the test's ended the child.</summary>
            public bool JobOpen => !_job.IsClosed;

            public void Dispose()
            {
                // Closing the job's last handle ends the child and anything it
                // started.
                _job.Dispose();
                _exited.Wait(10000);
                _ending?.Join(10000);
                _reading?.Join(5000);
                _draining?.Join(5000);
                _input.Dispose();
                _output.Dispose();
                _error.Dispose();
                Handle.Dispose();
                Watch.DisposeIfOwned(_ownWatch);
            }
        }

        /// <summary>A stream over chunks of bytes, read in order and ended
        /// when the writer completes: the child's standard output as a
        /// protocol client reads it, while the test keeps every byte.</summary>
        private sealed class ChannelStream : Stream
        {
            private readonly ChannelReader<byte[]> _chunks;
            private byte[] _chunk;
            private int _at;

            public ChannelStream(ChannelReader<byte[]> chunks) => _chunks = chunks;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count)
                => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.Length == 0) return 0;
                while (_chunk == null || _at >= _chunk.Length)
                {
                    if (!await _chunks.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                    if (_chunks.TryRead(out var next))
                    {
                        _chunk = next;
                        _at = 0;
                    }
                }
                int n = Math.Min(buffer.Length, _chunk.Length - _at);
                _chunk.AsMemory(_at, n).CopyTo(buffer);
                _at += n;
                return n;
            }
        }

        /// <summary>A word of a command line, quoted as Windows' own reader
        /// takes it back (CommandLineToArgvW): bare when it holds no space,
        /// tab or quote, else in quotes with each quote and the backslashes
        /// before it escaped.</summary>
        internal static string Word(string word)
        {
            if (word.Length > 0 && word.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return word;
            var quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in word)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') quoted.Append('\\', slashes * 2 + 1);
                else quoted.Append('\\', slashes);
                slashes = 0;
                quoted.Append(c);
            }
            return quoted.Append('\\', slashes * 2).Append('"').ToString();
        }

        /// <summary>The words as one command line's tail.</summary>
        internal static string Words(IEnumerable<string> words) => string.Join(" ", words.Select(Word));

        // ── The start's calls, as PluginLauncher.cs:411-530 has them ──

        private const uint CreateSuspended = 0x00000004, DebugOnlyThisProcess = 0x00000002;
        private const uint CreateUnicodeEnvironment = 0x00000400, ExtendedStartupInfoPresent = 0x00080000, CreateNoWindow = 0x08000000;
        private const int StartfUseStdHandles = 0x00000100;
        private static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;
        private const uint Infinite = 0xFFFFFFFF;

        // The debugger's: winbase.h and minwinbase.h. DEBUG_EVENT is 176 bytes
        // on x64 and arm64, its union after three DWORDs at the union's own
        // 8-byte alignment.
        private const int DebugEventBytes = 256, DebugUnionAt = 16;
        private const uint ExceptionDebugEvent = 1, CreateProcessDebugEvent = 3, ExitProcessDebugEvent = 5, LoadDllDebugEvent = 6;
        private const uint ExceptionBreakpoint = 0x80000003;
        private const uint DbgContinue = 0x00010002, DbgExceptionNotHandled = 0x80010001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returned);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string folder,
            ref STARTUPINFOEX startup, out PROCESS_INFORMATION information);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WaitForDebugEvent(IntPtr debugEvent, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ContinueDebugEvent(int processId, int threadId, uint status);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint size, uint flags);
    }
}
