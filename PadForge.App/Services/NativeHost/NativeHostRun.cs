using System;
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
    /// The library host's own run (#597): PadForge.exe with
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

        internal static bool IsRun(string[] args)
            => args is { Length: 2 } && args[0] == NativeHost.Switch && NativeHost.IsPipeName(args[1]);

        /// <summary>Whether this process runs with rights while UAC
        /// splits tokens.</summary>
        internal static bool RunsWithRights()
            => (System32Calls.IsSystem() || System32Calls.IntegrityRid() >= System32Calls.HighIntegrityRid) && !NativeHost.UacIsOff();

        internal static int Run(string[] args)
        {
            if (!IsRun(args) || RunsWithRights()) return ExitRefused;
            var control = Connect(args[1]);
            if (control == null) return ExitNoPadForge;
            while (control.Receive(out var request))
            {
                try
                {
                    int op = request.Int();
                    if (op == NativeHost.OpQuit)
                    {
                        request.End();
                        control.Send(w => { });
                        break;
                    }
                    if (op != NativeHost.OpOpen) throw new HostProtocolException("unknown operation");
                    string service = request.Text(32);
                    string pipe = request.Text(64);
                    request.End();
                    string refused = Open(service, pipe);
                    control.Send(w => w.Bool(refused == null).Text(refused));
                }
                catch (HostProtocolException)
                {
                    break;
                }
            }
            // PadForge ended or asked: every library goes with this process.
            control.Dispose();
            return 0;
        }

        /// <summary>Connects a channel for <paramref name="service"/> on a
        /// thread of its own. Null when it runs, else why not.</summary>
        private static string Open(string service, string pipe)
        {
            if (service == null || !Services.TryGetValue(service, out var make)) return "no such service";
            if (!NativeHost.IsPipeName(pipe)) return "not a channel's name";
            var channel = Connect(pipe);
            if (channel == null) return "the channel did not answer as PadForge";
            var thread = new Thread(() =>
            {
                try { make().Serve(channel); }
                catch (Exception ex) { PadForge.Engine.SdlDiagLog.WriteLine("NATIVEHOST " + service + " ended: " + ex.GetType().Name); }
                finally { channel.Dispose(); }
            })
            { IsBackground = true, Name = "PadForge.NativeHost." + service };
            thread.Start();
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
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
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
    }
}
