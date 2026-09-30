using System;
using System.Collections.Generic;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        /// <summary>The switch the rows were last put in step with.</summary>
        private bool _blissBoxRowsReadRaw;

        /// <summary>The switch's <see cref="BlissBoxRuntime.Generation"/> at
        /// the last pass.</summary>
        private int _blissBoxGeneration;

        /// <summary>The generation the poll loop last ran Phase 1 for, kept
        /// apart from <see cref="_blissBoxGeneration"/> so a phase before 1l
        /// that throws costs one extra sweep, not one every cycle.</summary>
        private int _blissBoxSweepGeneration;

        /// <summary>Rows whose motors pass to their port on a later cycle: the
        /// hand-off met another writer holding the row's output gate, which
        /// the poll thread never waits on, or SDL refused its stop. Poll
        /// thread only.</summary>
        private readonly HashSet<Guid> _blissBoxHandoffs = new();

        /// <summary>Rows that give SDL their motor levels back on a later
        /// cycle after the switch went off: their retired port has not sent
        /// its final stop yet, another writer holds the output gate, or SDL
        /// refused the write.</summary>
        private readonly HashSet<Guid> _blissBoxCacheResets = new();

        /// <summary>The pending hand-offs and SDL resends are tried again this
        /// often: a contested output gate delays one by at most 100 ms, and a
        /// device whose SDL rumble keeps failing costs a call every 100 ms
        /// rather than one a cycle.</summary>
        private const int BlissBoxRetryMs = 100;

        /// <summary>When the pending rows are next tried, in
        /// <see cref="Environment.TickCount64"/> time. Poll thread only.</summary>
        private long _blissBoxRetriesDue;

        /// <summary>
        /// Phase 1l (issue #469): one API sidecar per online Bliss-Box row
        /// while Read Bliss-Box Adapters is on. The rows are SDL's: a row open
        /// the other way from the switch gets a fresh wrapper, a port opens
        /// beside a row that appeared, and one closes when its row left,
        /// reconnected or the switch went off. A change of the switch brings
        /// this pass forward to the poll thread's next cycle
        /// (<see cref="ConsumeBlissBoxSwitchChange"/>), so the rows, the ports
        /// and the motors move together. Off and unchanged with no ports:
        /// three reads and out.
        /// </summary>
        private bool UpdateBlissBoxPorts()
        {
            // The generation first: a change that lands between the two reads
            // shows as a change on the next pass, never as none.
            int generation = BlissBoxRuntime.Generation;
            bool enabled = BlissBoxRuntime.Enabled;
            bool toggled = generation != _blissBoxGeneration;
            _blissBoxGeneration = generation;
            _blissBoxSweepGeneration = generation;
            bool wasOn = _blissBoxRowsReadRaw;
            // Every cycle while on, since a row can open between the switch
            // and this phase, once more after it goes off, and after a switch
            // that went on and off again between two passes, since Phase 1
            // may have opened a row raw in between.
            bool changed = (enabled || wasOn || toggled) && ReopenBlissBoxRows(enabled);
            _blissBoxRowsReadRaw = enabled;
            if (!enabled && !wasOn && !toggled && BlissBoxRuntime.Ports.Length == 0)
            {
                RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
                return changed;
            }

            var rows = new List<BlissBoxRuntime.Row>();
            var devices = SettingsManager.UserDevices;
            if (enabled && devices != null)
            {
                lock (devices.SyncRoot)
                {
                    foreach (var ud in devices.Items)
                    {
                        if (ud == null || !ud.IsOnline || ud.Device is not SdlDeviceWrapper wrapper) continue;
                        if (!BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId) || string.IsNullOrEmpty(ud.DevicePath)) continue;
                        rows.Add(new BlissBoxRuntime.Row(ud.DevicePath, ud.ProdId, ud.InstanceGuid, wrapper.SdlInstanceId));
                    }
                }
            }

            // Switched off, this retires every port, and each worker sends its
            // final stop as it leaves.
            var opened = BlissBoxRuntime.Sync(rows);
            if (!enabled)
            {
                _blissBoxHandoffs.Clear();
                // SDL's rumble was stopped when the port took the motors, so
                // each row gives SDL its level back, once its port's final
                // stop has gone out (ResetBlissBoxRumbleCache).
                if (wasOn || toggled) QueueBlissBoxCacheResets();
                RetryBlissBoxRows(_blissBoxCacheResets, handOff: false);
                return changed;
            }
            _blissBoxCacheResets.Clear();
            if (opened != null)
                foreach (var port in opened) _blissBoxHandoffs.Add(port.InstanceGuid);
            // A switch that went off and on again since the last pass may
            // have let SDL's path drive a port it kept, so each one takes its
            // row's level again.
            if (toggled)
                foreach (var port in BlissBoxRuntime.Ports) _blissBoxHandoffs.Add(port.InstanceGuid);
            RetryBlissBoxRows(_blissBoxHandoffs, handOff: true);
            return changed;
        }

        /// <summary>True once for each change of the switch Phase 1 has not
        /// seen, so the poll loop runs Phase 1 on this cycle rather than on
        /// its enumeration interval. Poll thread only.</summary>
        private bool ConsumeBlissBoxSwitchChange()
        {
            int generation = BlissBoxRuntime.Generation;
            if (generation == _blissBoxSweepGeneration) return false;
            _blissBoxSweepGeneration = generation;
            return true;
        }

        /// <summary>Every poll cycle, the hand-offs and SDL resends Phase 1l
        /// could not finish, tried again every 100 ms rather than at the next
        /// enumeration interval: a contested output gate, a retired port's
        /// final stop still pending, or SDL refusing a write. Nothing to do
        /// costs two counts.</summary>
        private void RetryPendingBlissBoxRows()
        {
            var pending = _blissBoxRowsReadRaw ? _blissBoxHandoffs : _blissBoxCacheResets;
            if (pending.Count == 0 || Environment.TickCount64 < _blissBoxRetriesDue) return;
            _blissBoxRetriesDue = Environment.TickCount64 + BlissBoxRetryMs;
            RetryBlissBoxRows(pending, handOff: _blissBoxRowsReadRaw);
        }

        /// <summary>Runs the hand-off or the cache reset for each pending row,
        /// keeping the rows it could not finish for a later cycle.</summary>
        private void RetryBlissBoxRows(HashSet<Guid> pending, bool handOff)
        {
            // Nothing is written once the crash path has quiesced the outputs.
            if (pending.Count == 0 || OutputsQuiesced) return;
            List<Guid> done = null;
            foreach (var guid in pending)
            {
                var ud = FindOnlineDeviceByInstanceGuid(guid);
                if (ud == null || (handOff ? HandMotorsToBlissBox(ud) : ResetBlissBoxRumbleCache(ud)))
                    (done ??= new List<Guid>()).Add(guid);
            }
            if (done != null)
                foreach (var guid in done) pending.Remove(guid);
        }

        /// <summary>The switch went off: every port row gives SDL the motor
        /// levels it last recorded (<see cref="ResetBlissBoxRumbleCache"/>).</summary>
        private void QueueBlissBoxCacheResets()
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            lock (devices.SyncRoot)
                foreach (var ud in devices.Items)
                    if (ud?.ForceFeedbackState != null && ud.Device != null && BlissBoxProtocol.IsPort(ud.VendorId, ud.ProdId))
                        _blissBoxCacheResets.Add(ud.InstanceGuid);
        }

        /// <summary>
        /// A port is read raw while the switch is on and through SDL's
        /// gamepad mapping while it is off (<see cref="BlissBoxApi.ReadsRaw"/>).
        /// A row open the other way gets a fresh wrapper for the same SDL
        /// instance, the replug rebind's shape in Phase 1: the row loads the
        /// new wrapper and disposes the old one. SDL counts the opens of one
        /// joystick, so the device stays open across the swap.
        /// </summary>
        private bool ReopenBlissBoxRows(bool readRaw)
        {
            var devices = SettingsManager.UserDevices;
            if (devices == null) return false;
            List<UserDevice> stale = null;
            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                    if (ud != null && ud.IsOnline && ud.Device is SdlDeviceWrapper wrapper
                        && BlissBoxProtocol.IsPort(wrapper.VendorId, wrapper.ProductId)
                        && BlissBoxRowNeedsReopen(wrapper.GameController != IntPtr.Zero, readRaw))
                        (stale ??= new List<UserDevice>()).Add(ud);
            }
            if (stale == null) return false;

            bool changed = false;
            foreach (var ud in stale)
            {
                if (ud.Device is not SdlDeviceWrapper old) continue;
                uint id = old.SdlInstanceId;
                // With the switch off, a port SDL has no mapping for stays raw.
                if (!readRaw && !SDL_IsGamepad(id)) continue;
                var fresh = new SdlDeviceWrapper();
                bool loaded = false;
                try
                {
                    if (fresh.Open(id))
                    {
                        if (ud.InstanceGuid != fresh.InstanceGuid)
                            fresh.OverrideInstanceGuid(ud.InstanceGuid);
                        ud.LoadFromSdlDevice(fresh);
                        loaded = true;
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"Error reopening device (instance {id})", ex);
                }
                finally
                {
                    if (!loaded)
                        try { fresh.Dispose(); } catch { }
                }
                if (!loaded) continue;
                _openedSdlInstanceIds[fresh.SdlInstanceId] = fresh;
                Engine.SdlDiagLog.WriteLine(
                    $"DEV reopen SDL#{id} {fresh.VendorId:X4}:{fresh.ProductId:X4} gamepad={fresh.GameController != IntPtr.Zero} (Bliss-Box switch {(readRaw ? "on" : "off")})");
                changed = true;
            }
            return changed;
        }

        /// <summary>True when a port's row is open the other way from the
        /// switch: through the gamepad mapping while it is on, or raw while it
        /// is off.</summary>
        internal static bool BlissBoxRowNeedsReopen(bool openedAsGamepad, bool readRaw)
            => openedAsGamepad == readRaw;

        /// <summary>The moment a port's motors pass to the adapter's commands:
        /// SDL's rumble stops, and the port takes the levels the row last
        /// recorded, whichever writer recorded them (Step 2, a relayed frame,
        /// or SDL's path before the switch), and tells both motors again
        /// (<see cref="BlissBoxRuntime.TakeMotors"/>). SDL's rumble is its
        /// only effect on a port: SDL opens no haptic device for one, since
        /// the fork's community database maps all four port IDs as a gamepad
        /// (SDL_gamepad_db_community.h:289-292) and SDL_IsJoystickHaptic
        /// refuses a gamepad (SDL_haptic.c:310-311). The row keeps its
        /// motor state through the switch's reopen, which is the same SDL
        /// connection (<see cref="UserDevice.SameConnection"/>), so a level a
        /// Remote Link peer sent once is still there. The port and the row's
        /// motor snapshot then agree, so the change detection that gates every
        /// later write holds for both. A row opened while the switch was off,
        /// on a port SDL found no motors on, gets its motor cache here, since
        /// Step 2 writes only rows that have one. False when another writer
        /// holds the row's output gate, so the hand-off waits for a later
        /// pass, as Step 2 skips a contested write.</summary>
        private static bool HandMotorsToBlissBox(UserDevice ud)
        {
            if (ud?.Device is not SdlDeviceWrapper wrapper) return true;
            // SDL's stop runs under the row's gate too, which an Identify
            // train started while the switch was off holds for its SDL
            // writes, so none of its pulses lands after the stop. SDL's gate
            // would refuse the train's own stop for it, and SDL would run it
            // until the switch went off, the engine stopped or its 65.5 s
            // expiration (SDL_joystick.c:2307-2308). A stop SDL refused
            // leaves its effect running beside the adapter's commands, so the
            // hand-off waits for a later pass. The port's resend follows it in
            // its first step with report 17 read.
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try
            {
                bool stopped;
                try { stopped = wrapper.StopSdlRumble(); } catch { stopped = true; }
                if (!stopped) return false;
                var state = ud.ForceFeedbackState ??= new ForceFeedbackState();
                BlissBoxRuntime.TakeMotors(ud.DevicePath, state.LeftMotorSpeed, state.RightMotorSpeed);
            }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
            return true;
        }

        /// <summary>The motors back to SDL after the switch went off: SDL is
        /// told the levels the row last recorded, and the cache records them
        /// as sent (<see cref="ForceFeedbackState.ResendScalar"/>). A Remote
        /// Link peer sends a steady level once, and SDL's rumble was stopped
        /// when the port took the motors, so waiting for the next change would
        /// leave them stopped. It waits until the row's retired port's worker
        /// has exited after its final stop, which on a GPA reaches the
        /// routines SDL's DirectInput effect drives (0x2E8C to 0x2EC3) and
        /// would end the level SDL was just given. False while that stop is
        /// pending, another writer holds the output gate, or SDL refused the
        /// stop or the level, so the row is tried again until SDL takes both.
        /// Nothing short of that shows the motors run the level: SDL skips a
        /// write that repeats its record (SDL_joystick.c:2287-2290), and a
        /// refused stop leaves that record at a level the final stop ended, so
        /// a later frame's write of the same level reports success with
        /// nothing sent.</summary>
        private static bool ResetBlissBoxRumbleCache(UserDevice ud)
        {
            var state = ud.ForceFeedbackState;
            if (state == null || ud.Device == null) return true;
            if (BlissBoxRuntime.IsRetiring(ud.DevicePath)) return false;
            if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;
            try { return state.ResendScalar(ud.Device); }
            catch { return true; }
            finally { System.Threading.Monitor.Exit(ud.OutputSync); }
        }
    }
}
