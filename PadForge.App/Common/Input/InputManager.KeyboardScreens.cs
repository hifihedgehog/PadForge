using System;
using System.Collections.Generic;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        // The keyboard screens (issue #519), a row each. The screen host finds
        // the screens on its worker and publishes them, and the poll thread
        // registers and retires the rows, the Falcons' shape. A row reads a
        // static the engines fill and opens without any I/O.
        private readonly Dictionary<string, KeyboardScreenDevice> _keyboardScreenRows = new(StringComparer.Ordinal);
        private readonly object _keyboardScreenLock = new object();
        private volatile bool _keyboardScreenInputsSuppressed;
        private volatile bool _keyboardScreenAnyRows;
        private KeyboardScreenInfo[] _keyboardScreensSeen = Array.Empty<KeyboardScreenInfo>();
        private int _keyboardScreenVersion;

        /// <summary>A Toggle Screen Overlay macro, for the UI thread to carry out:
        /// a bit per overlay's number, and bit 0 for every overlay.</summary>
        private void RequestScreenOverlayToggle(int number)
            => System.Threading.Interlocked.Or(ref ToggleScreenOverlayMask, 1 << Math.Clamp(number, 0, KeyboardScreenInfo.MaxOverlays));

        /// <summary>The open screen rows, for the Devices page.</summary>
        internal KeyboardScreenDevice[] KeyboardScreenRows
        {
            get
            {
                lock (_keyboardScreenLock)
                {
                    var rows = new KeyboardScreenDevice[_keyboardScreenRows.Count];
                    _keyboardScreenRows.Values.CopyTo(rows, 0);
                    return rows;
                }
            }
        }

        /// <summary>Phase 1j2 (issue #519). A row per present screen while
        /// the feature is on, recreated when the user removes one from the
        /// Devices page while its screen is still there, the G-key row's
        /// rule. Off with nothing to retire: three volatile reads and
        /// out.</summary>
        private bool UpdateKeyboardScreenDevices()
        {
            if (_keyboardScreenInputsSuppressed)
                return false;

            bool enabled = KeyboardScreensRuntime.Enabled;
            if (!enabled && !_keyboardScreenAnyRows)
                return false;

            var screens = enabled ? KeyboardScreensRuntime.Screens : Array.Empty<KeyboardScreenInfo>();
            bool changed = false;
            lock (_keyboardScreenLock)
            {
                if (_keyboardScreenInputsSuppressed)
                    return false;

                bool reconfigured = _keyboardScreenVersion != KeyboardScreensRuntime.Version;
                _keyboardScreenVersion = KeyboardScreensRuntime.Version;
                if (_keyboardScreenRows.Count > 0)
                {
                    List<string> gone = null;
                    foreach (var (id, dev) in _keyboardScreenRows)
                    {
                        bool present = Array.IndexOf(screens, dev.Screen) >= 0;
                        bool removedByUser = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid) == null;
                        if (!present || removedByUser || reconfigured)
                            (gone ??= new List<string>()).Add(id);
                    }
                    if (gone != null)
                    {
                        foreach (string id in gone)
                        {
                            var dev = _keyboardScreenRows[id];
                            RetireKeyboardScreenRow(dev);
                            _keyboardScreenRows.Remove(id);
                            MarkChanged(ref changed, "screen", "- " + dev.DevicePath);
                        }
                    }
                }

                if (!ReferenceEquals(screens, _keyboardScreensSeen) || changed)
                {
                    _keyboardScreensSeen = screens;
                    foreach (var screen in screens)
                    {
                        if (_keyboardScreenRows.ContainsKey(screen.Id)) continue;
                        try
                        {
                            var created = new KeyboardScreenDevice(screen);
                            if (!created.Open())
                            {
                                created.Dispose();
                                continue;
                            }
                            UserDevice ud = FindOrCreateUserDevice(created.InstanceGuid, created.ProductGuid);
                            ud.LoadFromExternalDevice(created);
                            ud.IsOnline = true;
                            _keyboardScreenRows[screen.Id] = created;
                            MarkChanged(ref changed, "screen", "+ " + created.DevicePath);
                        }
                        catch (Exception ex)
                        {
                            RaiseError("Error opening a keyboard screen's row", ex);
                        }
                    }
                }
                _keyboardScreenAnyRows = _keyboardScreenRows.Count > 0;
            }
            return changed;
        }

        // Caller holds _keyboardScreenLock.
        private void RetireKeyboardScreenRow(KeyboardScreenDevice dev)
        {
            var ud = FindOnlineDeviceByInstanceGuid(dev.InstanceGuid);
            if (ud != null)
            {
                ud.IsOnline = false;
                ud.Device = null;
                // A key held at retire time would stay stamped on the slot's
                // output, the analog keyboards' reason.
                NeutralizeMappedOutputsFor(ud);
            }
            dev.Dispose();
        }

        public void ShutdownKeyboardScreenInputs()
        {
            _keyboardScreenInputsSuppressed = true;
            lock (_keyboardScreenLock)
            {
                foreach (var dev in _keyboardScreenRows.Values)
                    RetireKeyboardScreenRow(dev);
                _keyboardScreenRows.Clear();
                _keyboardScreenAnyRows = false;
            }
        }
    }
}
