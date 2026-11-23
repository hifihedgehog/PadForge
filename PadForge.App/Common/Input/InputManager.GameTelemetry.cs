using System;
using PadForge.Common.Input.Telemetry;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Game telemetry (Phase 1i2, issue #531). The row opens without
        // blocking I/O (a UDP bind, an attempt at a file mapping), so the
        // poll thread runs the whole lifecycle itself, the head tracker's
        // shape.
        private volatile GameTelemetryDevice _gameTelemetryDevice;
        private readonly object _gameTelemetryLock = new object();
        private volatile bool _gameTelemetryInputsSuppressed;

        /// <summary>The live Game Telemetry row, null while the feature is
        /// off or the row is retired. Read by the status lane that writes its line on Extras and Home, which
        /// keys its cache on the instance plus
        /// <see cref="GameTelemetryDevice.StatusVersion"/>.</summary>
        internal GameTelemetryDevice GameTelemetry => _gameTelemetryDevice;

        /// <summary>Phase 1i2 (issue #531). One row while the switch is on,
        /// recreated when the user removes it (the NFC recreate pattern),
        /// reopened when the port, the game or the custom formats change, and
        /// its port tried again while another program holds it. Off with
        /// nothing to retire: three volatile reads and out.</summary>
        private bool UpdateGameTelemetryDevice()
        {
            if (_gameTelemetryInputsSuppressed)
                return false;

            bool enabled = GameTelemetryRuntime.Enabled;
            if (!enabled && _gameTelemetryDevice == null)
                return false;

            bool changed = false;
            lock (_gameTelemetryLock)
            {
                if (_gameTelemetryInputsSuppressed)
                    return false;

                var dev = _gameTelemetryDevice;
                if (dev != null)
                {
                    bool removedByUser = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid) == null;
                    bool reconfigured = dev.ConfigVersion != GameTelemetryRuntime.Version;
                    if (!enabled || removedByUser || reconfigured)
                    {
                        RetireGameTelemetryRow();
                        MarkChanged(ref changed, "telemetry", !enabled ? "- (feature off)" : removedByUser ? "- (removed by user)" : "- (reconfigured)");
                    }
                    else
                    {
                        // A port another program held when the row opened is
                        // tried again on every sweep, so the row listens once
                        // that program exits. A row that listens skips this.
                        dev.RetryBind();
                    }
                }

                if (enabled && _gameTelemetryDevice == null)
                {
                    try
                    {
                        var created = GameTelemetryDevice.FromCurrentSettings();
                        if (created.Open())
                        {
                            UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                            ud.LoadFromExternalDevice(created);
                            ud.IsOnline = true;
                            _gameTelemetryDevice = created;
                            MarkChanged(ref changed, "telemetry", "+ opened");
                        }
                        else created.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RaiseDeviceError("Error opening game telemetry device", ex);
                    }
                }
            }
            return changed;
        }

        // Caller holds _gameTelemetryLock.
        private void RetireGameTelemetryRow()
        {
            var dev = _gameTelemetryDevice;
            if (dev == null) return;
            var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            dev.Dispose();
            _gameTelemetryDevice = null;
        }

        public void ShutdownGameTelemetryInputs()
        {
            _gameTelemetryInputsSuppressed = true;
            lock (_gameTelemetryLock)
            {
                RetireGameTelemetryRow();
            }
        }
    }
}
