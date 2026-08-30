using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PadForge.Services
{
    /// <summary>
    /// Named-pipe control channel (#366): lets a launcher, script, or
    /// automation tool activate and deactivate profiles from outside
    /// PadForge, without the UI or window focus.
    ///
    /// <para>PadForge runs elevated (app.manifest requireAdministrator), so a
    /// plain <c>PadForge.exe &lt;args&gt;</c> call from an unelevated launcher
    /// would pop a UAC prompt every time. A pipe avoids that: this elevated
    /// server grants Authenticated Users read-write on the pipe DACL, and a
    /// Medium-integrity client connects because the pipe carries a Medium
    /// default mandatory label even though an elevated process created it.
    /// Mirrors Lenovo Legion Toolkit's IpcServer (an elevated WPF app serving
    /// NamedPipeServerStreamAcl.Create with an AuthenticatedUserSid rule,
    /// opt-in behind a settings flag, one request-response per connection in
    /// a loop). DS4Windows is the cautionary opposite: its WM_COPYDATA command
    /// lane never calls ChangeWindowMessageFilter, so once it runs elevated it
    /// goes deaf to unelevated senders. The pipe sidesteps that class.</para>
    ///
    /// <para>Protocol: plain UTF-8, one command line in, one response line
    /// out, one command per connection. Tokens are fixed ASCII, never
    /// localized, because this is a machine interface. The executor is
    /// injected so tests drive the protocol without an engine.</para>
    /// </summary>
    public sealed class ExternalControlService : IDisposable
    {
        /// <summary>Pipe name a client connects to. Local machine only.</summary>
        public const string PipeName = "PadForge.Control";

        private readonly Func<string, string> _executor;
        private CancellationTokenSource _cts;
        private Task _loop;
        private int _disposed;

        /// <param name="executor">Runs one command line and returns the
        /// response line. Called off the UI thread; the executor is
        /// responsible for its own dispatch.</param>
        public ExternalControlService(Func<string, string> executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        /// <summary>True while the accept loop is running.</summary>
        public bool IsRunning => _loop != null && !_loop.IsCompleted;

        public void Start()
        {
            if (_cts != null) return; // Already started.
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => AcceptLoop(token), token);
        }

        public void Stop()
        {
            if (_cts == null) return;
            try { _cts.Cancel(); } catch { }
            // A server stream parked in WaitForConnectionAsync does not observe
            // the token until a connection arrives, so nudge it with a
            // throwaway client. Best-effort: the loop also exits on the next
            // iteration's token check.
            try
            {
                using var nudge = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                nudge.Connect(200);
            }
            catch { /* no server parked, or it already tore down */ }
            try { _loop?.Wait(2000); } catch { }
            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = CreateServer();
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    string request = await ReadLineAsync(server, token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(request))
                    {
                        string response;
                        try { response = _executor(request) ?? "error internal"; }
                        catch { response = "error internal"; }
                        await WriteLineAsync(server, response, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* one bad connection never kills the loop */ }
                finally
                {
                    try { server?.Dispose(); } catch { }
                }
            }
        }

        private static NamedPipeServerStream CreateServer()
        {
            // Authenticated Users get read-write so an unelevated launcher can
            // drive an elevated PadForge. No Everyone, no anonymous.
            var security = new PipeSecurity();
            var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            security.AddAccessRule(new PipeAccessRule(
                authUsers,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));

            return NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                pipeSecurity: security);
        }

        private static async Task<string> ReadLineAsync(Stream s, CancellationToken token)
        {
            // One line, capped so a malformed client cannot grow the buffer
            // without bound. Commands are short (a verb plus a profile name).
            var buf = new byte[1];
            var sb = new StringBuilder();
            while (sb.Length < 1024)
            {
                int n = await s.ReadAsync(buf.AsMemory(0, 1), token).ConfigureAwait(false);
                if (n == 0) break;            // client closed its write end
                if (buf[0] == (byte)'\n') break;
                if (buf[0] == (byte)'\r') continue;
                sb.Append((char)buf[0]);
            }
            return sb.ToString().Trim();
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
