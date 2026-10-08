using System;
using System.Collections.Generic;
using System.Threading;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input.Peripherals
{
    /// <summary>
    /// Runs the peripheral outputs while the engine runs (#494): the HID++
    /// and GameSense workers, the Razer Sensa worker while its row is
    /// assigned, and the linker pass that ties device rows to paths.
    ///
    /// <para>Nothing here opens a vendor SDK for a device nobody assigned:
    /// the linker reads presence from the registry, files and process names,
    /// and each backend reaches its vendor only while a path it serves is
    /// claimed. Assignment drives every output, the way it drives a
    /// gamepad's.</para>
    /// </summary>
    internal sealed class PeripheralOutputHost : IDisposable
    {
        internal const int LinkMs = 500;
        internal const int PresenceMs = 5000;

        /// <summary>How long a Sensa worker that ended on its own waits
        /// before the next one, the worker's own provider retry.</summary>
        internal const int SensaRestartMs = 30000;

        private readonly HidppBackend _hidpp;
        private readonly GameSenseBackend _gameSense;
        private readonly Func<SensaHapticsService> _sensaFactory;
        private readonly Dictionary<string, Guid> _containers = new(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent _wake = new(false);
        private SensaHapticsService _sensa;
        private Action<SensaServiceState> _sensaReport;
        private long _sensaRestartAt;
        private Thread _thread;
        private volatile bool _stop;
        private int _disposed;

        /// <summary>Raised from the host thread when a row's recorded
        /// outputs changed, so the owner saves the settings and refreshes the
        /// device list. The tabs refresh on <see cref="PeripheralOutputs.LinksChanged"/>
        /// and <see cref="PeripheralOutputs.StatusChanged"/>, raised first.</summary>
        public event Action CapabilitiesChanged;

        public PeripheralOutputHost()
            : this(new HidppBackend(), new GameSenseBackend(), () => new SensaHapticsService()) { }

        internal PeripheralOutputHost(HidppBackend hidpp, GameSenseBackend gameSense, Func<SensaHapticsService> sensa)
        {
            _hidpp = hidpp;
            _gameSense = gameSense;
            _sensaFactory = sensa;
        }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _hidpp.Start();
            _gameSense.Start();
            PeripheralOutputs.Presence = PeripheralPresence.Read();
            _thread = new Thread(Loop) { IsBackground = true, Name = "PeripheralLink" };
            _thread.Start();
        }

        /// <summary>Brings the next link pass forward: devices came or went.</summary>
        public void Nudge()
        {
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        public void Stop()
        {
            if (_thread != null)
            {
                _stop = true;
                _wake.Set();
                try { _thread.Join(3000); } catch { }
                _thread = null;
            }
            StopSensa();
            _gameSense.Stop();
            _hidpp.Stop();
            PeripheralOutputs.PublishLinks(LinkTable.Empty);
            PeripheralOutputs.Hidpp = HidppSnapshot.Empty;
        }

        private void Loop()
        {
            long nextPresence = Environment.TickCount64 + PresenceMs;
            while (!_stop)
            {
                try
                {
                    long now = Environment.TickCount64;
                    if (now >= nextPresence)
                    {
                        PeripheralOutputs.Presence = PeripheralPresence.Read();
                        nextPresence = now + PresenceMs;
                    }
                    Link();
                    SensaLifecycle();
                }
                catch (Exception ex)
                {
                    PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL link pass fault: " + ex.GetType().Name);
                }
                // A pass held up past Stop's join, a Sensa worker slow to
                // stop, can come back after Dispose released the handle.
                try { _wake.WaitOne(LinkMs); }
                catch (ObjectDisposedException) { return; }
            }
        }

        /// <summary>One pass: snapshot the rows, link them, publish a change,
        /// and keep each row's recorded outputs current.</summary>
        private void Link()
        {
            var presence = PeripheralOutputs.Presence;
            var hidpp = _hidpp.Snapshot;
            PeripheralOutputs.Hidpp = hidpp;

            var rows = new List<LinkRow>();
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            var users = new List<Engine.Data.UserDevice>();
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null || !ud.IsOnline) continue;
                    if (ud.Device is PeripheralOutputRow vendorRow)
                    {
                        rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId, Guid.Empty, vendorRow.Kind));
                        users.Add(ud);
                        continue;
                    }
                    if (!PeripheralLinker.IsMouseOrKeyboard(ud.CapType)) continue;
                    // A device forwarded from another PC plays its rumble
                    // there, so only that PC links it to its vendor channel.
                    if (RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath)) continue;
                    if (ud.VendorId != PeripheralLinker.LogitechVid && ud.VendorId != PeripheralLinker.RazerVid
                        && ud.VendorId != PeripheralLinker.SteelSeriesVid) continue;
                    rows.Add(new LinkRow(ud.InstanceGuid, ud.CapType, ud.VendorId, ud.ProdId,
                        Guid.Empty, null));
                    users.Add(ud);
                }
            }
            // Container IDs outside the device lock: a configuration manager
            // call is a kernel round trip, once per path.
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].VendorRow != null) continue;
                string path = users[i].DevicePath;
                if (string.IsNullOrEmpty(path)) continue;
                if (!_containers.TryGetValue(path, out var container))
                {
                    try { container = AudioPassthroughService.DevicePathContainerId(path); }
                    catch { container = Guid.Empty; }
                    _containers[path] = container;
                }
                rows[i] = rows[i] with { Container = container };
            }

            var table = PeripheralLinker.Build(rows, hidpp, presence);

            // The records first, so a tab that refreshes on the new table
            // reads the record that goes with it.
            bool changed = false;
            for (int i = 0; i < rows.Count; i++)
            {
                var ud = users[i];
                var links = table.For(rows[i].Device);
                // A Logitech row whose container is still being probed, or
                // before the first scan, keeps what it had: a mouse asleep at
                // launch keeps its tabs.
                bool stillLooking = rows[i].VendorId == PeripheralLinker.LogitechVid && rows[i].VendorRow == null
                    && (!hidpp.Scanned || hidpp.PendingContainers.Contains(rows[i].Container));
                int next = PeripheralLinker.Capabilities(ud.PeripheralOutputs, links, stillLooking);
                if (next == ud.PeripheralOutputs) continue;
                ud.PeripheralOutputs = next;
                changed = true;
            }

            if (!table.SameAs(PeripheralOutputs.Links))
            {
                PeripheralOutputs.PublishLinks(table);
                PadForge.Engine.SdlDiagLog.WriteLine($"PERIPHERAL links: {table.ByDevice.Count} row(s), {table.ByPath.Count} path(s)");
            }
            else if (changed)
            {
                // A record that moved under an unchanged table still changes
                // what the tabs show.
                PeripheralOutputs.NotifyStatusChanged();
            }
            if (changed)
            {
                try { CapabilitiesChanged?.Invoke(); } catch { }
            }
        }

        /// <summary>The Sensa worker runs while the Sensa row is assigned to
        /// a virtual controller and stops when it leaves the last one, which
        /// stops the engine's events and releases the provider. A worker that
        /// ends on its own while the row stays assigned, because the engine
        /// did not load or a straggling predecessor kept the slot
        /// (<see cref="SensaHapticsService"/>'s bounded join), is started again
        /// after <see cref="SensaRestartMs"/>, so a missing HAR.dll does not
        /// spin.</summary>
        private void SensaLifecycle()
        {
            var sensaGuid = PeripheralOutputRow.IdentityFor(PeripheralRowKind.RazerSensa);
            bool linked = PeripheralOutputs.HasHaptics(sensaGuid);
            bool assigned = linked && SettingsManager.SlotOrders.GetIdentityPlayerNumber(sensaGuid) > 0;
            if (!assigned)
            {
                _sensaRestartAt = 0;
                if (_sensa == null) return;
                StopSensa();
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker stopped (row unassigned)");
                return;
            }

            long now = Environment.TickCount64;
            if (_sensa != null && !_sensa.WorkerAlive)
            {
                StopSensa();
                _sensaRestartAt = now + SensaRestartMs;
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Waiting);
                PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker ended on its own, starting again later");
            }
            if (_sensa != null || now < _sensaRestartAt) return;

            var sensa = _sensaFactory();
            // Only the current worker reports: a stopped one's last word
            // would otherwise land after its successor's.
            Action<SensaServiceState> report = state =>
            {
                if (!ReferenceEquals(Volatile.Read(ref _sensa), sensa)) return;
                PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics,
                    state == SensaServiceState.Active ? BackendState.Connected : BackendState.Waiting);
            };
            sensa.StateChanged += report;
            _sensaReport = report;
            Volatile.Write(ref _sensa, sensa);
            sensa.Start();
            PadForge.Engine.SdlDiagLog.WriteLine("PERIPHERAL Sensa worker started (row assigned)");
        }

        /// <summary>For an abnormal exit, after the engine's quiesce zeroed
        /// every level: waits up to <paramref name="waitMs"/> for GG to be
        /// told (<see cref="GameSenseBackend.WaitForSilence"/>). A HID++
        /// pulse ends on its own.</summary>
        public void WaitForSilence(int waitMs) => _gameSense.WaitForSilence(waitMs);

        private void StopSensa()
        {
            var sensa = Interlocked.Exchange(ref _sensa, null);
            if (sensa == null) return;
            if (_sensaReport != null) sensa.StateChanged -= _sensaReport;
            _sensaReport = null;
            try { sensa.Dispose(); } catch { }
            PeripheralOutputs.SetBackendState(OutputFamily.Interhaptics, BackendState.Idle);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Stop();
            _gameSense.Dispose();
            _hidpp.Dispose();
            _wake.Dispose();
        }
    }
}
