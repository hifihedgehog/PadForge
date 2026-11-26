using System;
using System.Collections.Generic;
using Microsoft.Win32;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// The device notes (#597): the flyout names a mapped device when it
    /// disconnects and when it connects, each behind its own switch under
    /// Extras, Overlays. This part reads the mapped rows, hands them to
    /// <see cref="DeviceNoteDirector"/>, which holds every rule, and shows
    /// the line it answers with on the profile flyout's window.
    /// </summary>
    public partial class InputService
    {
        private readonly DeviceNoteDirector _deviceNotes = new DeviceNoteDirector();

        /// <summary>The director, so tests can read what it knows.</summary>
        internal DeviceNoteDirector DeviceNotes => _deviceNotes;

        // The readings of one walk and the copy of the device list they are
        // made from, both kept between walks so a walk allocates nothing.
        private readonly List<DeviceNoteReading> _deviceNoteReadings = new List<DeviceNoteReading>();
        private UserDevice[] _deviceNoteRows;

        private long _lastDeviceNoteWalk;

        // The last USB port cycle of PadForge's own that the notes were
        // told of (HiddenControllerRelease.LastCycleAt), and the last other
        // work of its own that drops devices for a while (OwnDrops.Settle).
        private long _deviceNoteCycleSeen = DeviceNoteDirector.Never;
        private long _deviceNoteSettleSeen = DeviceNoteDirector.Never;

        /// <summary>Whether a device's row is a connection, one that can
        /// raise a note. The Devices page's own rule for a device a person
        /// plugs in, the one its count of connected devices and Home's use
        /// (<see cref="DeviceRowViewModel.IsCountedDevice"/>,
        /// <see cref="CountConnectedDevices"/>): no merged row, no
        /// consumer-control collection, and no row PadForge or an extra
        /// feeds, so Twitch Chat, a microphone's voice phrases, Game
        /// Telemetry, a keyboard screen, an overlay, a Web Controller's
        /// phone and a Remote Link peer raise none. A MIDI device, a Falcon
        /// and an analog keyboard stand for a device and do.</summary>
        internal static bool RaisesDeviceNotes(UserDevice ud)
            => ud != null && ud.InstanceGuid != Guid.Empty
               && !IsVirtualOrShadowDevice(ud)
               && DeviceRowViewModel.IsCountedDevice(ud.DevicePath, DeviceTypeGlyph.KeyFor(ud.CapType));

        /// <summary>Reads every mapped device into <paramref name="into"/>:
        /// each device assigned to a virtual controller that exists and is
        /// on, with those controllers as bits, whether it is online and
        /// whether it raises notes, with the stamp of PadForge's own work that
        /// covers it (<see cref="OwnDrops"/>), read after the row so a row
        /// read away has its stamp seen. Each list is held only for its copy,
        /// and neither inside the other, as the dashboard's walk reads them
        /// (<see cref="CopyDeviceRows"/>).</summary>
        internal static void ReadMappedDevices(List<DeviceNoteReading> into, ref UserDevice[] rows)
        {
            into.Clear();
            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null) return;

            lock (settings.SyncRoot)
            {
                foreach (var setting in settings.Items)
                {
                    if (setting == null) continue;
                    int slot = setting.MapTo;
                    if (slot < 0 || slot >= InputManager.MaxPads) continue;
                    if (!SettingsManager.SlotCreated[slot] || !SettingsManager.SlotEnabled[slot]) continue;
                    int at = IndexOfReading(into, setting.InstanceGuid);
                    if (at < 0) into.Add(new DeviceNoteReading(setting.InstanceGuid, false, 1 << slot, false));
                    else into[at] = new DeviceNoteReading(setting.InstanceGuid, false, into[at].Slots | (1 << slot), false);
                }
            }
            if (into.Count == 0) return;

            // An assignment whose device has no row stays as it was made
            // above: not online, and raising nothing.
            foreach (var ud in CopyDeviceRows(devices, ref rows))
            {
                if (ud == null) continue;
                int at = IndexOfReading(into, ud.InstanceGuid);
                if (at >= 0)
                {
                    bool online = ud.IsOnline;
                    into[at] = new DeviceNoteReading(ud.InstanceGuid, online, into[at].Slots, RaisesDeviceNotes(ud),
                        OwnDrops.EveryAt, OwnDrops.RowAt(ud.InstanceGuid));
                }
            }
        }

        private static int IndexOfReading(List<DeviceNoteReading> readings, Guid device)
        {
            for (int i = 0; i < readings.Count; i++)
                if (readings[i].Device == device) return i;
            return -1;
        }

        /// <summary>UI-timer leg for the device notes, four times a second.
        /// Gated inside, as the flyouts beside it are: with both switches
        /// off the director forgets what it knew, so a note turned on later
        /// starts from what is true then.</summary>
        private void UpdateDeviceNotes(bool noteDrops, bool noteReturns)
        {
            long now = Environment.TickCount64;
            if (now - _lastDeviceNoteWalk < DeviceNoteDirector.WalkMs) return;
            _lastDeviceNoteWalk = now;
            try
            {
                // A hidden controller's USB port that PadForge cycled drops
                // the controller and brings it back. Nothing is said of
                // either, at an engine start or when a device is hidden.
                long cycled = _controllerRelease.LastCycleAt;
                if (cycled > _deviceNoteCycleSeen)
                {
                    _deviceNoteCycleSeen = cycled;
                    _deviceNotes.Mark(cycled);
                }
                // A Bluetooth radio or a device's driver PadForge restarted:
                // the same, for every device.
                long settled = OwnDrops.SettleAt;
                if (settled > _deviceNoteSettleSeen)
                {
                    _deviceNoteSettleSeen = settled;
                    _deviceNotes.Mark(settled);
                }

                if (noteDrops || noteReturns) ReadMappedDevices(_deviceNoteReadings, ref _deviceNoteRows);
                else _deviceNoteReadings.Clear();

                // The flyout shows one thing at a time. A profile switch or
                // the controllers turned on or off holds every decision
                // until it has left, and a note that is up holds the next
                // line. A note that is up changes in place as the devices it
                // names change.
                bool up = _switchOverlay != null && _switchOverlay.IsUp;
                bool shown = up && _switchOverlay.IsShowingDeviceNote;
                var note = _deviceNotes.Tick(now,
                    _inputManager?.DevicesEnumeratedSince ?? DeviceNoteDirector.Never,
                    _deviceNoteReadings, noteDrops, noteReturns,
                    waiting: up && !shown, free: !up, shown: shown);
                if (note.Kind == DeviceNoteKind.None) return;

                string text = DeviceNoteText(note);
                if (string.IsNullOrEmpty(text)) return;
                if (note.InPlace)
                {
                    // The same note, its words and glyph changed with no
                    // slide in (#600 item 42).
                    SwitchOverlay().UpdateDeviceNote(text, note.Kind == DeviceNoteKind.IsBack, restartTime: !note.Grows);
                    return;
                }
                // On the monitor of the window in front, clicks passing
                // through, a drop for four seconds (#600 item 42).
                SwitchOverlay().ShowDeviceNote(text, note.Kind == DeviceNoteKind.Connected);
            }
            catch { /* a note must never break the tick */ }
        }

        /// <summary>The words of a line, or null when it names a device that
        /// has no row any more. A line for one device carries the name the
        /// Devices list shows. A drop that left a controller with no device
        /// names the controller as the interface does (Virtual Controller
        /// 2), and a return carries the battery level when PadForge knows
        /// it.</summary>
        internal string DeviceNoteText(DeviceNote note)
        {
            string device = null, controller = null;
            int battery = -1;
            if (note.Count > 1)
            {
                // A burst names each device it took (#600 item 42). A
                // device that has no row any more is left out, and one left
                // alone is said as its own line.
                var names = new List<string>(note.Devices.Count);
                Guid only = Guid.Empty;
                foreach (var each in note.Devices)
                {
                    string name = LocalizedDeviceName(FindUserDevice(each));
                    if (string.IsNullOrEmpty(name)) continue;
                    names.Add(name);
                    only = each;
                }
                if (names.Count == 0) return null;
                if (names.Count == 1)
                    return DeviceNoteText(new DeviceNote(note.Kind, 1, only, -1) { Times = note.Times });
                return DeviceNoteDirector.Text(new DeviceNote(note.Kind, names.Count, Guid.Empty, -1, note.Devices),
                    null, null, -1, names);
            }
            if (note.Count == 1)
            {
                var ud = FindUserDevice(note.Device);
                device = LocalizedDeviceName(ud);
                if (string.IsNullOrEmpty(device)) return null;
                if (note.Kind == DeviceNoteKind.Connected) battery = GetEffectiveBattery(ud).Pct;
                if (note.EmptiedSlot >= 0 && note.EmptiedSlot < _mainVm.Pads.Count)
                    controller = _mainVm.Pads[note.EmptiedSlot].SlotLabel;
            }
            return DeviceNoteDirector.Text(note, device, controller, battery);
        }

        /// <summary>Whether a device note is on screen. The shift-layer
        /// flyout stands in the same place and gives way to it
        /// (<see cref="UpdateShiftLayerFlyout"/>).</summary>
        private bool DeviceNoteIsUp => _switchOverlay != null && _switchOverlay.IsShowingDeviceNote;

        /// <summary>An engine start: the rows of the new run say nothing
        /// until they have settled, so a device that was connected all along
        /// raises no note. Windows' sleep is listened for from here to the
        /// stop.</summary>
        private void StartDeviceNotes()
        {
            _lastDeviceNoteWalk = 0;
            _deviceNotes.Mark(Environment.TickCount64);
            SystemEvents.PowerModeChanged -= OnDeviceNotePowerMode;
            SystemEvents.PowerModeChanged += OnDeviceNotePowerMode;
        }

        /// <summary>An engine stop: nothing is said of the rows it takes
        /// offline, and nothing is kept for the next run.</summary>
        private void StopDeviceNotes()
        {
            SystemEvents.PowerModeChanged -= OnDeviceNotePowerMode;
            _deviceNotes.Reset();
        }

        /// <summary>Windows is going to sleep, or has woken: the rows are
        /// old from that moment. Raised on a thread of Windows' own, so the
        /// mark is made on the interface's thread, where the walk
        /// runs.</summary>
        private void OnDeviceNotePowerMode(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Suspend && e.Mode != PowerModes.Resume) return;
            _dispatcher.BeginInvoke(new Action(() => _deviceNotes.Mark(Environment.TickCount64)));
        }

        /// <summary>The one diagnostics line for a device that dropped three
        /// times or more in a minute and now says nothing for five.</summary>
        private void LogDeviceNoteSilence(Guid device, int drops)
        {
            string name = LocalizedDeviceName(FindUserDevice(device)) ?? device.ToString();
            PadForge.Engine.SdlDiagLog.WriteLine(
                $"DEVNOTE {name} dropped {drops} times in a minute. Its notes rest for five minutes.");
        }
    }
}
