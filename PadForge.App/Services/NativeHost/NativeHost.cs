using System;
using PadForge.Engine.Common.NativeHost;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using PadForge.Services.Plugins;

namespace PadForge.Services.NativeHost
{
    /// <summary>
    /// The library host (#597): PadForge.exe started once more, with the
    /// user's ordinary rights, to load the third-party libraries that live in
    /// folders other accounts can change, or that load code from such
    /// folders: SteamVR's OpenVR client, an OpenXR runtime, Force
    /// Dimension's library for the Falcon, a Strike7 library the user picked
    /// and the ASUS Aura SDK. Loaded into PadForge, which runs with
    /// administrator rights, any of them would hand those rights to whoever
    /// can write those folders. Loaded here, they run with the rights the
    /// folders' writers already have.
    ///
    /// <para>The host starts through the plugins' launcher
    /// (<see cref="PluginLauncher"/>): the desktop shell is its parent, so it
    /// takes the shell's token, and it runs in a job that ends it when
    /// PadForge ends. It never falls back to PadForge's rights while UAC
    /// splits the user's token. With UAC off every program of the user runs
    /// with full rights, the shell included, so there is no lower right to
    /// start it with, and it is started as PadForge's own helper
    /// (<see cref="Common.ShellLaunch.StartOwnHelper"/>) in a job of its
    /// own.</para>
    ///
    /// <para>Every channel is a pipe of a new random name PadForge makes
    /// first, with Administrators as its owner, open to SYSTEM,
    /// Administrators and the user, closed to the network. The host reads
    /// that owner before it sends anything (<see cref="Automation.PipeOwner"/>),
    /// and PadForge takes a channel only from the process it started. What
    /// the host sends is bounded field by field by the service that reads
    /// it. A call that does not come back within <see cref="CallLimitMs"/>
    /// ends the host, and the next call starts a new one.</para>
    /// </summary>
    internal sealed class NativeHost
    {
        internal const string Switch = "--native-host";
        internal const string PipePrefix = "PadForge.NativeHost.";

        /// <summary>The longest one call may take before the host counts as
        /// hung. Creating an OpenXR instance or opening a Falcon takes
        /// seconds at most.</summary>
        internal const int CallLimitMs = 20_000;

        internal const int ConnectLimitMs = 15_000;

        /// <summary>The control channel's operations.</summary>
        internal const int OpOpen = 1, OpQuit = 2;

        internal static NativeHost Shared { get; } = new(new PluginLauncher());

        /// <summary>Test seam: opens a channel to a service in place of the
        /// host, as a test's in-process fake does. Null in production.</summary>
        internal static Func<string, HostConnection> OpenSeam;

        /// <summary>Set by the test assembly for the life of its process: no
        /// host is started, and every open without
        /// <see cref="OpenSeam"/> fails.</summary>
        internal static bool OffLimitsForTest;

        private readonly IPluginLauncher _launcher;
        private readonly object _gate = new();
        private readonly List<WeakReference<HostConnection>> _channels = new();
        private IPluginProcess _process;
        private Process _ownHelper;
        private Job _ownJob;
        private HostConnection _control;
        private int _generation;
        private int _failures;
        private long _nextStart;
        private long _startedAt;
        private Timer _watchdog;

        private readonly string _exe;

        /// <summary>A host started through <paramref name="launcher"/> from
        /// <paramref name="exe"/>, PadForge's own exe when null.</summary>
        internal NativeHost(IPluginLauncher launcher, string exe = null)
        {
            _launcher = launcher;
            _exe = exe;
        }

        /// <summary>The host that runs now. A channel of another generation
        /// belongs to a host that ended.</summary>
        internal int Generation
        {
            get { lock (_gate) return _generation; }
        }

        /// <summary>Whether a pipe name is one this host makes.</summary>
        internal static bool IsPipeName(string name)
        {
            if (name == null || name.Length != PipePrefix.Length + 32 || !name.StartsWith(PipePrefix, StringComparison.Ordinal)) return false;
            for (int i = PipePrefix.Length; i < name.Length; i++)
                if (!(name[i] is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
            return true;
        }

        internal static string NewPipeName() => PipePrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>
        /// A channel to <paramref name="service"/> in the host, starting the
        /// host when none runs, or null with <paramref name="reason"/>. Each
        /// channel is served on a thread of its own in the host.
        /// </summary>
        internal HostConnection Open(string service, out string reason)
        {
            reason = null;
            var seam = OpenSeam;
            if (seam != null)
            {
                var fake = seam(service);
                if (fake == null) reason = "the test's host refused " + service;
                return fake;
            }
            if (OffLimitsForTest)
            {
                reason = "the test host starts no library host";
                return null;
            }
            lock (_gate)
            {
                if (!EnsureRunning(out reason)) return null;
                string name = NewPipeName();
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = MakePipe(name);
                    var waiting = pipe.WaitForConnectionAsync();
                    bool opened = false;
                    string refused = null;
                    if (!_control.Call(w => w.Int(OpOpen).Text(service).Text(name), r =>
                        {
                            opened = r.Bool();
                            refused = r.Text(512);
                        }))
                    {
                        reason = "the library host ended";
                        LostHost();
                        pipe.Dispose();
                        return null;
                    }
                    if (!opened)
                    {
                        reason = "the library host could not open " + service + ": " + (refused ?? "no reason");
                        pipe.Dispose();
                        return null;
                    }
                    if (!waiting.Wait(ConnectLimitMs) || !FromTheHost(pipe))
                    {
                        reason = "the library host did not connect for " + service;
                        pipe.Dispose();
                        return null;
                    }
                    var channel = new HostConnection(pipe, pipe, pipe) { Generation = _generation };
                    _channels.Add(new WeakReference<HostConnection>(channel));
                    return channel;
                }
                catch (Exception ex)
                {
                    reason = "the library host's channel failed: " + ex.Message;
                    pipe?.Dispose();
                    return null;
                }
            }
        }

        /// <summary>Ends the host and every library in it, as an uninstall
        /// that has to free SteamVR's files asks. The next open starts a
        /// new one.</summary>
        internal void Stop()
        {
            lock (_gate)
            {
                if (_control != null)
                {
                    _control.Call(w => w.Int(OpQuit), null);
                    _control.Dispose();
                }
                KillHost();
            }
        }

        // ── Starting the host ──

        private bool EnsureRunning(out string reason)
        {
            reason = null;
            if (_control != null && !_control.Broken && (_process != null || _ownHelper != null)) return true;
            LostHost();
            long now = Environment.TickCount64;
            if (now < _nextStart)
            {
                reason = "the library host is started again shortly";
                return false;
            }
            string exe = _exe ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                reason = "PadForge's own path is not known";
                return false;
            }
            string name = NewPipeName();
            NamedPipeServerStream pipe = null;
            try
            {
                pipe = MakePipe(name);
                var waiting = pipe.WaitForConnectionAsync();
                if (!StartProcess(exe, name, out reason))
                {
                    pipe.Dispose();
                    Failed();
                    return false;
                }
                if (!waiting.Wait(ConnectLimitMs) || !FromTheHost(pipe))
                {
                    reason = "the library host did not connect";
                    pipe.Dispose();
                    KillHost();
                    Failed();
                    return false;
                }
                _generation++;
                _control = new HostConnection(pipe, pipe, pipe) { Generation = _generation };
                _startedAt = now;
                _watchdog ??= new Timer(_ => Watch(), null, 1000, 1000);
                return true;
            }
            catch (Exception ex)
            {
                reason = "the library host could not start: " + ex.Message;
                pipe?.Dispose();
                KillHost();
                Failed();
                return false;
            }
        }

        private bool StartProcess(string exe, string pipeName, out string reason)
        {
            reason = null;
            string folder = Path.GetDirectoryName(exe);
            string line = "\"" + exe + "\" " + Switch + " " + pipeName;
            var launch = _launcher.Start(new PluginCommand(exe, line, folder), null);
            if (launch.Process != null)
            {
                var process = launch.Process;
                _process = process;
                // A host that ends ends its control channel, so the next
                // open starts another.
                process.Exited += _ =>
                {
                    lock (_gate)
                        if (ReferenceEquals(_process, process)) _control?.Dispose();
                };
                return true;
            }
            if (launch.Refusal == PluginRefusal.ShellElevated && UacIsOff())
            {
                // Every program of the user runs with full rights, the shell
                // too: no lower right exists to start the host with.
                var job = Job.KillOnClose();
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
                psi.ArgumentList.Add(Switch);
                psi.ArgumentList.Add(pipeName);
                var started = Common.ShellLaunch.StartOwnHelper(psi);
                if (started == null || job == null || !job.Assign(started))
                {
                    try { started?.Kill(); } catch (Exception) { }
                    job?.Dispose();
                    started?.Dispose();
                    reason = "the library host could not start";
                    return false;
                }
                _ownHelper = started;
                _ownJob = job;
                return true;
            }
            reason = "the library host could not start with the user's rights (" + launch.Refusal
                     + (launch.Error != 0 ? ", error " + launch.Error : string.Empty) + ")";
            return false;
        }

        /// <summary>Whether UAC is off: EnableLUA is 0 in the machine's
        /// policy (Microsoft, User Account Control settings).</summary>
        internal static bool UacIsOff()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
                return key?.GetValue("EnableLUA") is int value && value == 0;
            }
            catch
            {
                return false;
            }
        }

        private void Failed()
        {
            _failures++;
            _nextStart = Environment.TickCount64 + Math.Min(30_000, 1000L << Math.Min(_failures - 1, 5));
        }

        private void LostHost()
        {
            if (_control == null && _process == null && _ownHelper == null) return;
            // A host that ran a minute had nothing wrong with its start.
            if (Environment.TickCount64 - _startedAt > 60_000) _failures = 0;
            else Failed();
            _control?.Dispose();
            _control = null;
            KillHost();
        }

        private void KillHost()
        {
            var process = _process;
            _process = null;
            if (process != null)
            {
                process.Stop();
                process.Dispose();
            }
            var own = _ownHelper;
            _ownHelper = null;
            _ownJob?.Dispose();
            _ownJob = null;
            own?.Dispose();
            foreach (var weak in _channels)
                if (weak.TryGetTarget(out var channel)) channel.Dispose();
            _channels.Clear();
            _control = null;
        }

        /// <summary>Every second: a call that has waited past
        /// <see cref="CallLimitMs"/> ends the host, so the call fails and
        /// the next one starts a new host.</summary>
        private void Watch()
        {
            long limit = Stopwatch.Frequency * CallLimitMs / 1000;
            long now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                bool hung = false;
                _channels.RemoveAll(w => !w.TryGetTarget(out var c) || c.Broken);
                foreach (var weak in _channels)
                    if (weak.TryGetTarget(out var channel) && channel.CallStarted != 0 && now - channel.CallStarted > limit) hung = true;
                if (!hung) return;
                PadForge.Engine.SdlDiagLog.WriteLine("NATIVEHOST a call waited past " + CallLimitMs + " ms, so the library host is ended.");
                _control?.Dispose();
                _control = null;
                KillHost();
                Failed();
            }
        }

        // ── Pipes ──

        /// <summary>A channel's pipe, with the access list and owner every
        /// pipe of PadForge's has (ExternalControlService.BuildSecurity): the
        /// desktop's user reads and writes, as the host runs as that user,
        /// Administrators and the system have every right, the network none,
        /// and the owner is Administrators, or the system as SYSTEM. One
        /// instance, the first of its name.</summary>
        private static NamedPipeServerStream MakePipe(string name)
            => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 65536, 65536, ExternalControlService.BuildSecurity());

        /// <summary>Whether the pipe's client is the host this PadForge
        /// started: the process itself, or with UAC off the helper it
        /// started as its own.</summary>
        private bool FromTheHost(NamedPipeServerStream pipe)
        {
            int id = Automation.AutomationClientProcess.IdOf(pipe);
            if (id == 0) return false;
            if (_process != null) return _process.Owns((int)id);
            var own = _ownHelper;
            try { return own != null && !own.HasExited && own.Id == (int)id; }
            catch (Exception) { return false; }
        }

        /// <summary>A job that ends its processes when its handle closes,
        /// for the host started as PadForge's own helper. It is the job the
        /// protected start puts its copy in (System32Calls.NewKillOnCloseJob,
        /// the layout of Microsoft's JOBOBJECT_EXTENDED_LIMIT_INFORMATION),
        /// held here with its handle.</summary>
        private sealed class Job : IDisposable
        {
            private IntPtr _handle;

            private Job(IntPtr handle) => _handle = handle;

            internal static Job KillOnClose()
            {
                IntPtr handle = Common.System32Calls.NewKillOnCloseJob();
                return handle == IntPtr.Zero ? null : new Job(handle);
            }

            internal bool Assign(Process process)
            {
                IntPtr handle = _handle;
                return handle != IntPtr.Zero && Common.System32Calls.AssignToJob(handle, process.Handle);
            }

            public void Dispose() => Common.System32Calls.Close(Interlocked.Exchange(ref _handle, IntPtr.Zero));
        }
    }
}
