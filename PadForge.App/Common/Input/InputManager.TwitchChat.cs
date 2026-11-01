using System;
using PadForge.Common.Input.Twitch;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // Twitch chat (Phase 1i3, issue #555). The row opens without blocking
        // I/O (the chat connection runs on a task of its own), so the poll
        // thread runs the whole lifecycle itself, the Game Telemetry row's
        // shape.
        private volatile TwitchChatDevice _twitchChatDevice;
        private readonly object _twitchChatLock = new object();
        private volatile bool _twitchChatInputsSuppressed;

        /// <summary>The live Twitch Chat row, null while the feature is off or
        /// the row is retired. Read by the Dashboard status lane, which keys
        /// its cache on the instance plus
        /// <see cref="TwitchChatDevice.StatusVersion"/>.</summary>
        internal TwitchChatDevice TwitchChat => _twitchChatDevice;

        /// <summary>Phase 1i3 (issue #555). One row while the switch is on,
        /// recreated when the user removes it (the NFC recreate pattern) and
        /// reopened when the channel changes. Off with nothing to retire:
        /// three volatile reads and out.</summary>
        private bool UpdateTwitchChatDevice()
        {
            if (_twitchChatInputsSuppressed)
                return false;

            bool enabled = TwitchChatRuntime.Enabled;
            if (!enabled && _twitchChatDevice == null)
                return false;

            bool changed = false;
            lock (_twitchChatLock)
            {
                if (_twitchChatInputsSuppressed)
                    return false;

                var dev = _twitchChatDevice;
                if (dev != null)
                {
                    bool removedByUser = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid) == null;
                    bool reconfigured = dev.ConfigVersion != TwitchChatRuntime.Version;
                    if (!enabled || removedByUser || reconfigured)
                    {
                        RetireTwitchChatRow();
                        MarkChanged(ref changed, "twitch", !enabled ? "- (feature off)" : removedByUser ? "- (removed by user)" : "- (reconfigured)");
                    }
                }

                if (enabled && _twitchChatDevice == null)
                {
                    try
                    {
                        var created = TwitchChatDevice.FromCurrentSettings();
                        if (created.Open())
                        {
                            UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                            ud.LoadFromExternalDevice(created);
                            ud.IsOnline = true;
                            _twitchChatDevice = created;
                            MarkChanged(ref changed, "twitch", "+ opened");
                        }
                        else created.Dispose();
                    }
                    catch (Exception ex)
                    {
                        RaiseError("Error opening Twitch chat device", ex);
                    }
                }
            }
            return changed;
        }

        // Caller holds _twitchChatLock.
        private void RetireTwitchChatRow()
        {
            var dev = _twitchChatDevice;
            if (dev == null) return;
            var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                NeutralizeMappedOutputsFor(ud);
            }
            dev.Dispose();
            _twitchChatDevice = null;
        }

        public void ShutdownTwitchChatInputs()
        {
            _twitchChatInputsSuppressed = true;
            lock (_twitchChatLock)
            {
                RetireTwitchChatRow();
            }
        }
    }
}
