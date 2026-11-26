using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using PadForge.Common;
using PadForge.Engine.Common.NativeHost;
using PadForge.Services.Automation;

namespace PadForge.Services.NativeHost
{
    /// <summary>
    /// The library host's own run (#600): PadForge.exe with
    /// <see cref="NativeHost.Switch"/> and the control pipe's name, handled in
    /// Program.Main before WPF. It connects to PadForge, reads the pipe's
    /// owner before it sends anything, and opens each channel PadForge asks
    /// for on a thread of its own. It leaves when PadForge's control channel
    /// ends, and PadForge's job ends it if PadForge goes first.
    ///
    /// <para>It never loads a library with rights: a run at high integrity
    /// leaves at once, unless UAC is off, where every program of the user
    /// runs so.</para>
    /// </summary>
    internal static class NativeHostRun
    {
        /// <summary>The services, by the name a channel asks for.</summary>
        internal static readonly IReadOnlyDictionary<string, Func<IHostService>> Services = new Dictionary<string, Func<IHostService>>(StringComparer.Ordinal)
        {
            [FalconHostService.Name] = () => new FalconHostService(),
            [PadForge.Engine.Common.OpenXr.OpenXrHeadPoseSource.LibraryHostService] = PadForge.Engine.Common.OpenXr.OpenXrHeadPoseSource.NewLibraryHostService,
            [OpenVrHostService.Name] = () => new OpenVrHostService(),
            [Strike7HostService.Name] = () => new Strike7HostService(),
            [AuraHostService.Name] = () => new AuraHostService(),
        };

        internal const int ExitRefused = 3, ExitNoPadForge = 4;

        /// <summary>How long the host waits for its services to end, each
        /// closing what it opened, before it answers a quit or leaves.</summary>
        internal const int QuitWaitMs = 5000;

        /// <summary>The most service ends one answer of the control channel
        /// carries, and the longest each one's text.</summary>
        internal const int MaxEnded = 8, MaxEndedText = 160;

        internal static bool IsRun(string[] args)
            => args is { Length: 2 } && args[0] == NativeHost.Switch && NativeHost.IsPipeName(args[1]);

        /// <summary>Whether this process runs with rights while its user has
        /// a lower right: as SYSTEM while UAC is on, or at high integrity
        /// while the desktop shell's user has a limited token beside the full
        /// one (PluginLauncher.ShellUserHasNoLowerRight).</summary>
        internal static bool RunsWithRights()
            => System32Calls.IsSystem()
                ? !NativeHost.UacIsOff()
                : System32Calls.IntegrityRid() >= System32Calls.HighIntegrityRid && !PadForge.Services.Plugins.PluginLauncher.ShellUserHasNoLowerRight();

        internal static int Run(string[] args)
        {
            if (!IsRun(args) || RunsWithRights()) return ExitRefused;
            var control = Connect(args[1]);
            if (control == null) return ExitNoPadForge;
            Serve(control, Connect, Services);
            return 0;
        }

        /// <summary>
        /// Answers PadForge's control channel until it ends or PadForge asks
        /// the host to leave. <paramref name="connect"/> connects a service's
        /// channel by its pipe's name, and <paramref name="services"/> names
        /// the services.
        ///
        /// <para>A service's thread is a background thread, which the runtime
        /// does not run to its end when the process exits, and PadForge ends
        /// the process once it hears a quit's answer. So before it answers a
        /// quit, and when the control channel ends, the host waits up to
        /// <see cref="QuitWaitMs"/> for the services, whose own ends close
        /// what each opened: Aura's control of the lights, OpenVR's session.
        /// PadForge closes its ends of their channels before it sends the
        /// quit (<see cref="NativeHost.Stop"/>), which ends them.</para>
        ///
        /// <para>The host keeps no log PadForge reads, so why a service ended
        /// by throwing goes to PadForge with the next open's answer, and
        /// PadForge writes it into its own diagnostics.</para>
        /// </summary>
        internal static void Serve(HostConnection control, Func<string, HostConnection> connect, IReadOnlyDictionary<string, Func<IHostService>> services)
        {
            var threads = new List<Thread>();
            var ended = new ConcurrentQueue<string>();
            try
            {
                while (control.Receive(out var request))
                {
                    try
                    {
                        int op = request.Int();
                        if (op == NativeHost.OpQuit)
                        {
                            request.End();
                            WaitForServices(threads);
                            control.Send(w => { });
                            break;
                        }
                        if (op != NativeHost.OpOpen) throw new HostProtocolException("unknown operation");
                        string service = request.Text(32);
                        string pipe = request.Text(64);
                        request.End();
                        string refused = Open(service, pipe, connect, services, threads, ended);
                        control.Send(w =>
                        {
                            w.Bool(refused == null).Text(refused);
                            var lines = new List<string>(MaxEnded);
                            while (lines.Count < MaxEnded && ended.TryDequeue(out string line)) lines.Add(line);
                            w.Int(lines.Count);
                            foreach (string line in lines) w.Text(line);
                        });
                    }
                    catch (HostProtocolException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // PadForge ended or asked: every library goes with this
                // process, once each service has closed what it opened.
                WaitForServices(threads);
                control.Dispose();
            }
        }

        private static void WaitForServices(List<Thread> threads)
        {
            long until = Environment.TickCount64 + QuitWaitMs;
            foreach (var thread in threads)
                thread.Join((int)Math.Max(0, until - Environment.TickCount64));
            threads.Clear();
        }

        /// <summary>Connects a channel for <paramref name="service"/> on a
        /// thread of its own. Null when it runs, else why not.</summary>
        private static string Open(string service, string pipe, Func<string, HostConnection> connect,
            IReadOnlyDictionary<string, Func<IHostService>> services, List<Thread> threads, ConcurrentQueue<string> ended)
        {
            if (service == null || !services.TryGetValue(service, out var make)) return "no such service";
            if (!NativeHost.IsPipeName(pipe)) return "not a channel's name";
            var channel = connect(pipe);
            if (channel == null) return "the channel did not answer as PadForge";
            threads.RemoveAll(t => !t.IsAlive);
            var thread = new Thread(() =>
            {
                try { make().Serve(channel); }
                catch (Exception ex)
                {
                    string line = service + " ended: " + ex.GetType().Name;
                    if (ended.Count < MaxEnded) ended.Enqueue(line.Length > MaxEndedText ? line.Substring(0, MaxEndedText) : line);
                }
                finally { channel.Dispose(); }
            })
            { IsBackground = true, Name = "PadForge.NativeHost." + service };
            thread.Start();
            threads.Add(thread);
            return null;
        }

        /// <summary>Connects to a pipe PadForge made, at the identification
        /// level, and takes it only when its owner is Administrators or
        /// SYSTEM (<see cref="PipeOwner.Taken"/> with no other owner). The
        /// host's pipes are never a test's, so the caller's own user is not
        /// taken as their owner, whatever their name. With UAC off
        /// PadForge's pipe has the same owner.</summary>
        private static HostConnection Connect(string name)
        {
            var pipe = ClientPipe(name);
            try
            {
                pipe.Connect(NativeHost.ConnectLimitMs);
                if (!PipeOwner.Taken(PipeOwner.Of(pipe.SafePipeHandle)))
                {
                    pipe.Dispose();
                    return null;
                }
                return new HostConnection(pipe, pipe, pipe);
            }
            catch (Exception)
            {
                pipe.Dispose();
                return null;
            }
        }

        /// <summary>The host's end of a pipe PadForge made: synchronous, at
        /// the identification level. Not connected yet.</summary>
        internal static NamedPipeClientStream ClientPipe(string name)
            => new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
    }
}
