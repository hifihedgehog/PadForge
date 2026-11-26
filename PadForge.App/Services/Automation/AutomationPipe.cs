using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The Automation pipe, <c>PadForge.Automation</c> (#600): one long-lived
    /// duplex connection per program, served while Automation is on (and
    /// while Plugins is on). It has the control pipe's access list: the
    /// desktop's user, administrators and the system are let in, and whoever
    /// comes in over the network is refused. While no connection it handed
    /// out is open, an instance it makes is made as the first of its name,
    /// so a program that took the name first is found out and nothing is
    /// served beside it. While one is open the name is PadForge's own, and
    /// Windows refuses a first instance of it, so a new one is made as a
    /// further instance (#600).
    ///
    /// <para>The access list only lets a program open the pipe. Who it is
    /// is decided from the token Windows hands the server for the
    /// connection (<see cref="AutomationClientIdentity"/>, asked through
    /// <see cref="Judge"/> once the first line arrived): another user,
    /// another Windows session, low integrity and an app container are
    /// refused with one line and the connection closed. The process ID and
    /// image are read for display only.</para>
    ///
    /// <para>The accept loop mirrors the control pipe's
    /// (ExternalControlService): the next instance stands before a
    /// connection is served, so no program finds the name gone between two
    /// connects. A connection is handed to the sessions and the loop goes
    /// back to waiting at once.</para>
    /// </summary>
    internal sealed class AutomationPipe : IDisposable
    {
        /// <summary>The pipe's name. Local machine only.</summary>
        public const string PipeName = "PadForge.Automation";

        /// <summary>Set by the test assembly for the life of its process
        /// (PadForge.Tests AutomationPipeOffLimits), never by the app. While
        /// it is set, no server is started under the app's own name, so a
        /// test never answers in the place of a PadForge that runs on the
        /// same PC. A pipe of a test's own name runs as before.</summary>
        internal static bool OffLimitsForTest;

        /// <summary>The in and out buffers each connection asks Windows
        /// for. A stream of events fits without a write waiting on every
        /// line.</summary>
        internal const int BufferSize = 64 * 1024;

        private readonly Func<NamedPipeServerStream, AutomationClientProcess, CancellationToken, Task> _serve;
        private readonly string _pipeName;
        private CancellationTokenSource _cts;
        private Task _loop;
        private volatile ExternalControlService.PipeState _state;
        private int _disposed;

        // The handles of the connections handed to the sessions, kept until
        // the serve ends. A session closes its stream itself (its Close
        // disposes it) before its end runs, so a handed instance is open
        // while its handle is not closed, and a first instance is asked for
        // once every handed handle is. Read as an instance is made, under one
        // lock. It outlives a stop, since a session outlives the pipe's stop.
        private readonly object _instances = new object();
        private readonly System.Collections.Generic.List<Microsoft.Win32.SafeHandles.SafePipeHandle> _handed = new();

        /// <param name="serve">Runs one connection, already taken, to its
        /// end. It owns the stream and the process.</param>
        /// <param name="pipeName">The app gives none and serves
        /// <see cref="PipeName"/>. A test gives a name of its own.</param>
        public AutomationPipe(Func<NamedPipeServerStream, AutomationClientProcess, CancellationToken, Task> serve, string pipeName = null)
        {
            _serve = serve ?? throw new ArgumentNullException(nameof(serve));
            _pipeName = string.IsNullOrEmpty(pipeName) ? PipeName : pipeName;
        }

        /// <summary>The name served.</summary>
        public string Name => _pipeName;

        private bool ServesTheAppsOwnPipe => string.Equals(_pipeName, PipeName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads the client's token. A test stands its own facts
        /// here for the cases a test bench cannot make: another session, low
        /// integrity, an app container.</summary>
        internal Func<NamedPipeServerStream, AutomationClientToken?> ReadToken { get; set; } = AutomationClientIdentity.Read;

        /// <summary>Whom the server takes, read at the start.</summary>
        internal (string Desktop, string Server, int Session) Desktop { get; private set; }

        /// <summary>The process at the other end of a connection. A test
        /// stands in processes of its own, since every connection a test
        /// makes comes from its one process.</summary>
        internal Func<NamedPipeServerStream, AutomationClientProcess> ProcessOf { get; set; } = AutomationClientProcess.Of;

        /// <summary>
        /// Whether the client of a connection is taken: null when it is, or
        /// the reason in a word. Windows lets a server impersonate its client
        /// only once it has read from the pipe, so a session asks this when
        /// its first line has arrived and before it runs it.
        /// </summary>
        internal string Judge(NamedPipeServerStream connected)
        {
            var desktop = Desktop;
            return AutomationClientIdentity.Judge(connected == null ? null : ReadToken(connected),
                desktop.Desktop, desktop.Server, desktop.Session);
        }

        /// <summary>Where the pipe stands.</summary>
        public ExternalControlService.PipeState State => _state;

        /// <summary>Raised on a worker when the pipe stops serving with
        /// nobody having stopped it.</summary>
        public event Action StateChanged;

        /// <summary>Takes the name and starts answering. The first instance
        /// is made before this returns.</summary>
        public ExternalControlService.PipeState Start()
        {
            if (OffLimitsForTest && ServesTheAppsOwnPipe) return ExternalControlService.PipeState.Stopped;
            if (_cts != null) return _state;

            Desktop = AutomationClientIdentity.ThisDesktop();
            NamedPipeServerStream first;
            try
            {
                first = NewInstance();
            }
            catch (UnauthorizedAccessException)
            {
                return _state = ExternalControlService.PipeState.NameTaken;
            }
            catch
            {
                return _state = ExternalControlService.PipeState.Failed;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _state = ExternalControlService.PipeState.Serving;
            _loop = Task.Run(() => AcceptLoop(first, token));
            return _state;
        }

        public void Stop()
        {
            if (_cts == null)
            {
                _state = ExternalControlService.PipeState.Stopped;
                return;
            }
            try { _cts.Cancel(); } catch { }
            bool ended = true;
            try { ended = _loop == null || _loop.Wait(200); } catch { }
            if (!ended)
            {
                // The control pipe's second nudge, for a loop that has not
                // ended by now.
                try
                {
                    using var nudge = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                    nudge.Connect(200);
                }
                catch { /* no server parked, or it already tore down */ }
                try { _loop.Wait(2000); } catch { }
            }
            _cts.Dispose();
            _cts = null;
            _loop = null;
            _state = ExternalControlService.PipeState.Stopped;
        }

        private async Task AcceptLoop(NamedPipeServerStream first, CancellationToken token)
        {
            NamedPipeServerStream waiting = first;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    NamedPipeServerStream connected = waiting;
                    waiting = null;
                    bool handed = false;
                    try
                    {
                        await connected.WaitForConnectionAsync(token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) break;

                        try { waiting = CreateServer(_pipeName, firstOfItsName: false); }
                        catch { /* made again below */ }

                        handed = Take(connected);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                    catch { /* one bad connection never kills the loop */ }
                    finally
                    {
                        if (!handed)
                        {
                            try { connected.Dispose(); } catch { }
                        }
                    }

                    if (waiting == null) waiting = NewInstance();
                }
            }
            catch (UnauthorizedAccessException)
            {
                EndedOnItsOwn(ExternalControlService.PipeState.NameTaken, token);
            }
            catch
            {
                EndedOnItsOwn(ExternalControlService.PipeState.Failed, token);
            }
            finally
            {
                try { waiting?.Dispose(); } catch { }
            }
        }

        /// <summary>Hands a connection to the sessions without waiting for
        /// it. A session outlives the pipe's stop: the sessions close their
        /// own once their last line is written.</summary>
        private bool Take(NamedPipeServerStream connected)
        {
            AutomationClientProcess process;
            try { process = ProcessOf(connected) ?? AutomationClientProcess.None; }
            catch { process = AutomationClientProcess.None; }
            // The serve's first step runs here, in the order the pipe took the
            // connections, so the sessions list those that wait for their
            // hello in that order (#600). The rest runs off this loop: the
            // serve goes on from the thread pool at its first wait.
            // The handle is read before the serve runs: a stream closed by
            // then holds no instance and is not kept.
            Microsoft.Win32.SafeHandles.SafePipeHandle handle = null;
            try { handle = connected.SafePipeHandle; } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
            if (handle != null) lock (_instances) _handed.Add(handle);
            Task served;
            try { served = _serve(connected, process, CancellationToken.None) ?? Task.CompletedTask; }
            catch (Exception ex) { served = Task.FromException(ex); }
            _ = served.ContinueWith(done =>
            {
                _ = done.Exception; // a session's failure is its own
                try { connected.Dispose(); } catch { }
                if (handle != null) lock (_instances) _handed.Remove(handle);
                process.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }

        private void EndedOnItsOwn(ExternalControlService.PipeState state, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            _state = state;
            try { StateChanged?.Invoke(); } catch { }
        }

        /// <summary>An instance to wait on: the first of its name while no
        /// connection handed out is open, else a further one.</summary>
        private NamedPipeServerStream NewInstance()
        {
            lock (_instances)
            {
                bool anyOpen = false;
                foreach (var handle in _handed)
                    if (!handle.IsClosed) { anyOpen = true; break; }
                return CreateServer(_pipeName, firstOfItsName: !anyOpen);
            }
        }

        private static NamedPipeServerStream CreateServer(string pipeName, bool firstOfItsName)
        {
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                firstOfItsName ? PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance : PipeOptions.Asynchronous,
                inBufferSize: BufferSize,
                outBufferSize: BufferSize,
                pipeSecurity: ExternalControlService.BuildSecurity());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
        }
    }
}
