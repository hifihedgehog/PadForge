using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PadForge.Services
{
    /// <summary>
    /// The control pipe (#366, #597): a launcher, a script or PadForge.exe
    /// started again with switches sends one command line and reads one
    /// answer, without PadForge's window or the focus.
    ///
    /// <para>The app runs with administrator rights and the programs that
    /// send to it run without. A pipe crosses that line: its access list
    /// names who may connect, and a pipe a process with rights makes still
    /// carries the default medium label, so a program without rights that the
    /// list admits can open it. The list admits the user whose desktop this
    /// is, administrators and the system, and refuses whoever comes in over
    /// the network. Follows the design of Lenovo Legion Toolkit's IpcServer
    /// (a WPF app with rights serving NamedPipeServerStreamAcl.Create, opt-in
    /// behind a settings flag, one request and one response per connection
    /// in a loop). DS4Windows is the cautionary opposite: its WM_COPYDATA
    /// command lane never calls ChangeWindowMessageFilter, so once it runs
    /// with rights it goes deaf to senders without them.</para>
    ///
    /// <para>The name is held from <see cref="Start"/> to <see cref="Stop"/>.
    /// The first instance is made as the first of its name, so a program
    /// that made the name before PadForge did is found out and nothing is
    /// served beside it. From then on the next instance stands before a
    /// connection is served, so no client finds the name gone between two
    /// commands, and only administrators and the system may add an instance,
    /// so no other program of the user's can answer in PadForge's place.</para>
    ///
    /// <para>Protocol: plain UTF-8, one command line in, one response line
    /// out, one command per connection. Tokens are fixed ASCII, never
    /// localized, because this is a machine interface. A request is read up
    /// to 1,024 bytes. An answer can be longer. The executor is injected so
    /// tests drive the protocol without an engine.</para>
    /// </summary>
    public sealed class ExternalControlService : IDisposable
    {
        /// <summary>Pipe name a client connects to. Local machine only.</summary>
        public const string PipeName = "PadForge.Control";

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests ControlPipeOffLimits), never by the app. The name
        /// above is the one a running PadForge on the same PC serves and its
        /// launchers write to. While this is set, a service on that name
        /// starts no server and connects to none, as a service that was
        /// never started. A service on a name of a test's own runs as
        /// before. False in production, where every path is as it was.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>Whether this service is on the app's own pipe. Windows
        /// compares pipe names without regard to case.</summary>
        private bool ServesTheAppsOwnPipe => string.Equals(_pipeName, PipeName, StringComparison.OrdinalIgnoreCase);

        /// <summary>The longest request line kept, in bytes. What a client
        /// sends past it is read and dropped, so a client that wrote a longer
        /// line is not left waiting in its write.</summary>
        internal const int LongestRequest = 1024;

        /// <summary>How much of a line that runs past the cap is read before
        /// the connection is given up.</summary>
        internal const int LongestDrained = 64 * 1024;

        /// <summary>How long a connected client has to send its line, and how
        /// long it has to take the answer. One connection is served at a
        /// time, so a client that connects and says nothing, or never reads,
        /// would hold every other program off the pipe. The command's own
        /// run is not timed: starting the engine takes as long as it
        /// takes.</summary>
        internal static readonly TimeSpan DefaultClientTimeout = TimeSpan.FromSeconds(5);

        /// <summary>The time a client is given, <see cref="DefaultClientTimeout"/>
        /// unless a test sets a shorter one before the start.</summary>
        internal TimeSpan ClientTimeout { get; set; } = DefaultClientTimeout;

        /// <summary>One line a program sent.</summary>
        public readonly struct Request
        {
            public Request(string line, int clientProcessId, CancellationToken stopping)
            {
                Line = line;
                ClientProcessId = clientProcessId;
                Stopping = stopping;
            }

            /// <summary>The line, trimmed.</summary>
            public string Line { get; }

            /// <summary>The process at the other end as Windows names it, or
            /// 0. It is shown to the user and decides nothing: a program can
            /// stage a process ID.</summary>
            public int ClientProcessId { get; }

            /// <summary>Canceled when the service stops, so an executor that
            /// waits for the interface's thread lets go and Stop never waits
            /// on a command that waits on it.</summary>
            public CancellationToken Stopping { get; }
        }

        /// <summary>What a line is answered with.</summary>
        public readonly struct Answer
        {
            public Answer(string line, Action afterWritten = null)
            {
                Line = line;
                AfterWritten = afterWritten;
            }

            /// <summary>The response line.</summary>
            public string Line { get; }

            /// <summary>Run once the response has been written to the pipe,
            /// or null. A command that closes PadForge answers first and
            /// closes here.</summary>
            public Action AfterWritten { get; }
        }

        /// <summary>Where the pipe stands.</summary>
        public enum PipeState
        {
            /// <summary>Not started, or stopped.</summary>
            Stopped,

            /// <summary>The name is held and connections are answered.</summary>
            Serving,

            /// <summary>Another program made a pipe of this name first.
            /// Nothing is served.</summary>
            NameTaken,

            /// <summary>Windows refused the pipe for another reason.</summary>
            Failed,
        }

        private readonly Func<Request, Answer> _executor;
        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private Task _loop;
        private volatile PipeState _state;
        private int _disposed;

        /// <param name="executor">Runs one command line and returns the
        /// response line. Called off the UI thread; the executor is
        /// responsible for its own dispatch.</param>
        /// <param name="pipeName">Overrides the pipe name. Production always
        /// uses the default. A pipe name is MACHINE-GLOBAL, so a test that
        /// served the production name would be answered by (or would collide
        /// with) a running PadForge on the same machine: observed live, where
        /// a test client reached the real app's server and got the real app's
        /// reply. Tests pass a unique name, and the test host starts no
        /// service on the production one (OffLimitsForTest).</param>
        public ExternalControlService(Func<string, string> executor, string pipeName = null)
            : this(Wrap(executor), pipeName)
        {
        }

        /// <param name="executor">As above, with the caller's process, the
        /// stop signal and what to do once the answer is written.</param>
        /// <param name="pipeName">As above.</param>
        public ExternalControlService(Func<Request, Answer> executor, string pipeName = null)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _pipeName = string.IsNullOrEmpty(pipeName) ? PipeName : pipeName;
        }

        private static Func<Request, Answer> Wrap(Func<string, string> executor)
        {
            if (executor == null) throw new ArgumentNullException(nameof(executor));
            return request => new Answer(executor(request.Line));
        }

        /// <summary>True while the accept loop is running.</summary>
        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        /// <summary>Where the pipe stands now.</summary>
        public PipeState State => _state;

        /// <summary>Raised on a worker thread when the pipe stops serving
        /// with nobody having stopped it. <see cref="Start"/> and
        /// <see cref="Stop"/> raise nothing: their caller has the answer.</summary>
        public event Action StateChanged;

        /// <summary>Takes the name and starts answering. The first instance
        /// is made before this returns, so the answer says whether the name
        /// was free.</summary>
        public PipeState Start()
        {
            // The test host serves no pipe under the app's own name. The loop
            // never starts there, so Stop finds nothing to nudge.
            if (OffLimitsForTest && ServesTheAppsOwnPipe) return PipeState.Stopped;
            if (_cts != null) return _state; // Already started.

            NamedPipeServerStream first;
            try
            {
                first = CreateServer(_pipeName, firstOfItsName: true);
            }
            catch (UnauthorizedAccessException)
            {
                // Windows refuses a first instance of a name that already has
                // one. PadForge closed its own before this, so the pipe that
                // stands is another program's.
                return _state = PipeState.NameTaken;
            }
            catch
            {
                return _state = PipeState.Failed;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state = PipeState.Serving;
            // The loop is not started under the token: a stop that comes
            // before the loop's first line would cancel the start, and the
            // first instance would stay open with nothing to close it.
            _loop = Task.Run(() => AcceptLoop(first, token));
            return _state;
        }

        public void Stop()
        {
            if (_cts == null)
            {
                _state = PipeState.Stopped;
                return;
            }
            try { _cts.Cancel(); } catch { }
            // Canceling the token ends a pending WaitForConnectionAsync on this
            // asynchronous pipe (a .NET 10 probe ended one in under 2 ms), and
            // the loop also exits on the next iteration's token check. The
            // throwaway client is a second nudge for a loop that has not ended
            // by then. A loop that has ended gets none: where it ended by
            // itself the name is another program's, and that program's pipe
            // is left alone.
            bool ended = true;
            try { ended = _loop == null || _loop.Wait(200); } catch { }
            if (!ended)
            {
                try
                {
                    using var nudge = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                    nudge.Connect(200);
                }
                catch { /* no server parked, or it already tore down */ }
                try { _loop.Wait(2000); } catch { }
            }
            _cts.Dispose();
            _cts = null;
            _loop = null;
            _state = PipeState.Stopped;
        }

        private async Task AcceptLoop(NamedPipeServerStream first, CancellationToken token)
        {
            // The instance a client can connect to next. While the loop runs
            // there is always one, so the name stays PadForge's.
            NamedPipeServerStream waiting = first;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    NamedPipeServerStream connected = waiting;
                    waiting = null;
                    try
                    {
                        await connected.WaitForConnectionAsync(token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) break;

                        // The next instance stands before this connection is
                        // served. Windows lets only administrators and the
                        // system add one to a name that exists.
                        try { waiting = CreateServer(_pipeName, firstOfItsName: false); }
                        catch { /* made again below, as the first of its name */ }

                        await ServeAsync(connected, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch { /* one bad connection never kills the loop */ }
                    finally
                    {
                        try { connected.Dispose(); } catch { }
                    }

                    // No instance stood through that connection, so the name
                    // was free for a moment. It is taken again as a first
                    // instance, which finds out a program that took it.
                    if (waiting == null) waiting = CreateServer(_pipeName, firstOfItsName: true);
                }
            }
            catch (UnauthorizedAccessException)
            {
                EndedOnItsOwn(PipeState.NameTaken, token);
            }
            catch
            {
                EndedOnItsOwn(PipeState.Failed, token);
            }
            finally
            {
                try { waiting?.Dispose(); } catch { }
            }
        }

        /// <summary>The loop ended with nobody having stopped it: the name
        /// could not be taken again. A stop that is under way says Stopped
        /// itself.</summary>
        private void EndedOnItsOwn(PipeState state, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            _state = state;
            try { StateChanged?.Invoke(); } catch { }
        }

        private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
        {
            string line;
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                reading.CancelAfter(ClientTimeout);
                line = await ReadLineAsync(server, reading.Token).ConfigureAwait(false);
            }

            // ALWAYS answer, including on an empty line. A client that
            // gets no response blocks in its own ReadLine until the
            // pipe closes, which reads as a hung launcher rather than
            // a rejected command. Live-verified on this defect: an
            // earlier guard skipped the write and a bare newline hung.
            Answer answer;
            try { answer = _executor(new Request(line, ClientProcessId(server), token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { answer = new Answer("error internal"); }
            using (var writing = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                writing.CancelAfter(ClientTimeout);
                await WriteLineAsync(server, answer.Line ?? "error internal", writing.Token).ConfigureAwait(false);
            }

            // The answer is in the pipe. What follows it must not take the
            // loop down with it.
            try { answer.AfterWritten?.Invoke(); } catch { }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        private static int ClientProcessId(NamedPipeServerStream server)
        {
            try
            {
                return GetNamedPipeClientProcessId(server.SafePipeHandle, out uint id) ? unchecked((int)id) : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Who may open the pipe. The user whose desktop this is reads and
        /// writes, which is all a client does. Administrators and the system
        /// have every right, the one to add an instance among them, which the
        /// server needs for the instance that stands while a connection is
        /// served: the app runs with administrator rights and takes it from
        /// that rule. Whoever comes in over the network is refused, whatever
        /// account that is.
        ///
        /// <para>A server without administrator rights gets no instance right
        /// from that rule, so it is given one of its own. PadForge is never
        /// such a server (Program.Main starts the app with rights). A test's
        /// server is.</para>
        /// </summary>
        internal static PipeSecurity BuildSecurity(SecurityIdentifier desktopUser, SecurityIdentifier server,
            bool serverIsAdministrator)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            if (desktopUser != null)
                security.AddAccessRule(new PipeAccessRule(desktopUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, AccessControlType.Allow));
            if (!serverIsAdministrator && server != null)
                security.AddAccessRule(new PipeAccessRule(server,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            return security;
        }

        /// <summary>The access list for this process as it runs now: the
        /// desktop's user from the shell's own process, as the lighting state
        /// reads it, and this process's user where there is no shell.</summary>
        internal static PipeSecurity BuildSecurity()
        {
            using var identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier server = identity.User;
            SecurityIdentifier desktopUser = server;
            string shell = PadForge.Common.Input.Peripherals.WindowsLightingState.ShellUserSid();
            if (!string.IsNullOrEmpty(shell))
            {
                try { desktopUser = new SecurityIdentifier(shell); }
                catch (ArgumentException) { /* this process's user stands in */ }
            }
            bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            return BuildSecurity(desktopUser, server, administrator);
        }

        private static NamedPipeServerStream CreateServer(string pipeName, bool firstOfItsName)
        {
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                firstOfItsName ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: BuildSecurity());
        }

        private static async Task<string> ReadLineAsync(Stream s, CancellationToken token)
        {
            // One UTF-8 line, capped at 1024 bytes so a malformed client cannot
            // grow the buffer without bound. Commands are short (a verb plus a
            // profile name). Splitting on the '\n' and '\r' bytes is safe: UTF-8
            // never uses those values inside a multi-byte character.
            var chunk = new byte[LongestRequest];
            var line = new byte[LongestRequest];
            int kept = 0, read = 0;
            bool ended = false;
            while (!ended && read < LongestRequest + LongestDrained)
            {
                int n = await s.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                if (n == 0) break;            // client closed its write end
                read += n;
                for (int i = 0; i < n; i++)
                {
                    if (chunk[i] == (byte)'\n') { ended = true; break; }
                    if (chunk[i] == (byte)'\r') continue;
                    // Past the cap the line is read on and not kept.
                    if (kept < LongestRequest) line[kept++] = chunk[i];
                }
            }
            return Encoding.UTF8.GetString(line, 0, kept).Trim();
        }

        private static async Task WriteLineAsync(Stream s, string line, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            await s.WriteAsync(bytes.AsMemory(0, bytes.Length), token).ConfigureAwait(false);
            await s.FlushAsync(token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
