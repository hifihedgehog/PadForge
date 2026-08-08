using System;
using PadForge.Engine;
using HIDMaestro;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Virtual VR controller pair (issue #49) served by HIDMaestro's native
    /// OpenVR driver (HM#32, v1.6.0). One instance drives BOTH SteamVR hand
    /// controllers through one <see cref="HMVRController"/> pipe. The driver
    /// registers the devices with SteamVR only while this consumer is live,
    /// so an idle machine shows no phantom controllers.
    ///
    /// All calls are in-process (named-pipe transport inside
    /// HIDMaestro.Core), so Connect/Disconnect need none of the bounded-RPC
    /// ceremony the MIDI wrapper carries for midisrv.
    /// </summary>
    internal sealed class HMaestroVRController : IVirtualController
    {
        private HMVRController _vr;
        private bool _connected;
        private bool _disposed;

        // Haptic pulses arrive as (hand, amplitude, duration) events and fan
        // into the slot's Vibration entry, the same lane game rumble rides
        // (left hand → left motor, right hand → right motor). A pulse must
        // DECAY: the rumble path latches motor speeds until someone writes
        // zero, so each hand keeps an expiry deadline and a one-shot timer
        // zeroes the lane when the last pulse ends.
        private Vibration[] _fbVibrationStates;
        private readonly object _hapticLock = new();
        private System.Threading.Timer _hapticExpiryTimer;
        private long _leftPulseEndTick;   // Environment.TickCount64 deadline
        private long _rightPulseEndTick;

        /// <summary>Floor for a pulse's audible length. OpenVR apps send
        /// micro-pulses (often 0-5 ms) at high repeat rates; a literal
        /// duration would expire before the 16ms rumble keepalive ever
        /// forwards it.</summary>
        private const int MinPulseMs = 50;

        public VirtualControllerType Type => VirtualControllerType.Vr;
        public bool IsConnected => _connected;
        public int FeedbackPadIndex { get; set; }

        /// <summary>True when SteamVR itself is present. Without SteamVR the
        /// driver has no host to register with, so slot creation refuses
        /// early with a clear message instead of a silent dead pipe.
        /// Cached briefly: the probe walks Steam's library metadata on disk,
        /// and the sidebar rail rebuild queries once per slot.</summary>
        public static bool IsAvailable()
        {
            long now = Environment.TickCount64;
            if (s_availHasValue && now - s_availCheckedTick < AvailabilityTtlMs)
                return s_availCached;
            bool avail;
            try { avail = HMVR.IsSteamVRInstalled; }
            catch { avail = false; }
            s_availCached = avail;
            s_availCheckedTick = now;
            s_availHasValue = true;
            return avail;
        }

        private const int AvailabilityTtlMs = 5_000;
        private static long s_availCheckedTick;
        private static bool s_availCached;

        /// <summary>Whether the cache holds a real answer. An explicit flag,
        /// NOT a sentinel timestamp: seeding the tick with long.MinValue
        /// made `now - s_availCheckedTick` overflow to a large NEGATIVE
        /// value, which is always below the TTL, so the very first call
        /// returned the default `false` without probing and never stamped
        /// the tick. SteamVR then read as absent forever: the type gates
        /// stayed closed, the Settings card stayed "Not installed", and
        /// clicking Install found the payload already present, skipped its
        /// retry loop, and returned in milliseconds so the overlay merely
        /// flashed (owner report 2026-08-08).</summary>
        private static bool s_availHasValue;

        /// <summary>Drops the availability cache so the next
        /// <see cref="IsAvailable"/> re-probes. Call after installing
        /// SteamVR so the UI gates lift without waiting out the TTL.</summary>
        public static void ResetAvailability() => s_availHasValue = false;

        public void Connect()
        {
            if (_connected) return;

            if (!HMVR.IsSteamVRInstalled)
                throw new InvalidOperationException(
                    "SteamVR is not installed. Virtual VR controllers require SteamVR.");
            if (!HMVR.EnsureDriverRegistered())
                throw new InvalidOperationException(
                    "The HIDMaestro OpenVR driver could not be registered with SteamVR.");

            var vr = new HMVRController();
            vr.HapticReceived += OnHapticReceived;
            _vr = vr;
            _connected = true;
        }

        public void Disconnect()
        {
            if (!_connected) return;
            _connected = false;

            var vr = _vr;
            _vr = null;
            if (vr != null)
            {
                vr.HapticReceived -= OnHapticReceived;
                try { vr.Dispose(); } catch { /* best effort */ }
            }

            lock (_hapticLock)
            {
                _hapticExpiryTimer?.Dispose();
                _hapticExpiryTimer = null;
                ZeroHapticLanes();
            }
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            // Not used for VR. Step 5 calls SubmitVrState. Kept for the
            // IVirtualController interface.
        }

        /// <summary>
        /// Packs the pipeline's VrRawState into the driver's HMVRState and
        /// submits it. Button bits are identical by construction
        /// (VrHandRaw.Buttons mirrors HMVRButton), so the conversion is a
        /// cast. Axis domains: sticks bipolar short → -1..1, triggers and
        /// grips one-sided 0..32767 → 0..1.
        ///
        /// Sign accounting (the one-seam rule): the Step 3 evaluator
        /// output keeps SDL's native stick convention, Y positive = DOWN.
        /// Every wire that wants Y-up flips ONCE at its own pack seam:
        /// the gamepad path at the ThumbLY target write (NegateAxis /
        /// the MappingSetEval "-value" write). OpenVR's joystick Y is
        /// +up, so this lane's single flip lives in PackHand and nowhere
        /// upstream.
        /// </summary>
        public void SubmitVrState(in VrRawState raw)
        {
            var vr = _vr;
            if (!_connected || vr == null) return;

            var st = new HMVRState
            {
                Left = PackHand(in raw.Left),
                Right = PackHand(in raw.Right),
            };
            try { vr.SubmitState(in st); } catch { /* driver pipe dropped */ }
        }

        private static HMVRHandState PackHand(in VrHandRaw hand)
        {
            float stickY = hand.StickY >= 0 ? hand.StickY / 32767f : hand.StickY / 32768f;
            return new HMVRHandState
            {
                Buttons = (HMVRButton)hand.Buttons,
                Trigger = hand.Trigger / 32767f,
                Grip = hand.Grip / 32767f,
                StickX = hand.StickX >= 0 ? hand.StickX / 32767f : hand.StickX / 32768f,
                // The lane's single Y flip: SDL Y-down → OpenVR Y-up. See
                // the SubmitVrState doc comment.
                StickY = -stickY,
                // v1 has no pose source; the driver holds the controllers at
                // its fixed standing-height default while PoseValid is false.
                PoseValid = false,
            };
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
            _fbVibrationStates = vibrationStates;
        }

        private void OnHapticReceived(object sender, HMVRHapticEventArgs e)
        {
            var states = _fbVibrationStates;
            int idx = FeedbackPadIndex;
            if (states == null || idx < 0 || idx >= states.Length) return;
            var vib = states[idx];
            if (vib == null) return;

            ushort speed = (ushort)(Math.Clamp(e.Amplitude, 0f, 1f) * 65535f);
            int durationMs = Math.Max((int)(e.DurationSeconds * 1000f), MinPulseMs);

            lock (_hapticLock)
            {
                // Re-check INSIDE the lock. Disconnect flips _connected and
                // only then takes this lock to zero the lanes and dispose
                // the timer, so a check outside it can pass, block here,
                // and resume after teardown: the motor re-latches on a slot
                // the VC no longer drives and ScheduleExpiryLocked builds a
                // fresh timer nothing will ever dispose.
                if (!_connected) return;
                long endTick = Environment.TickCount64 + durationMs;
                if (e.Hand == HMVRHand.Left)
                {
                    vib.LeftMotorSpeed = speed;
                    if (endTick > _leftPulseEndTick) _leftPulseEndTick = endTick;
                }
                else
                {
                    vib.RightMotorSpeed = speed;
                    if (endTick > _rightPulseEndTick) _rightPulseEndTick = endTick;
                }
                ScheduleExpiryLocked();
            }
        }

        /// <summary>Arms the one-shot expiry timer for the earliest pending
        /// deadline. Caller holds <see cref="_hapticLock"/>.</summary>
        private void ScheduleExpiryLocked()
        {
            long now = Environment.TickCount64;
            long next = long.MaxValue;
            if (_leftPulseEndTick > now) next = Math.Min(next, _leftPulseEndTick);
            if (_rightPulseEndTick > now) next = Math.Min(next, _rightPulseEndTick);
            if (next == long.MaxValue) return;

            int due = (int)Math.Max(next - now, 1);
            if (_hapticExpiryTimer == null)
                _hapticExpiryTimer = new System.Threading.Timer(OnHapticExpiry, null, due, System.Threading.Timeout.Infinite);
            else
                _hapticExpiryTimer.Change(due, System.Threading.Timeout.Infinite);
        }

        private void OnHapticExpiry(object state)
        {
            lock (_hapticLock)
            {
                long now = Environment.TickCount64;
                var states = _fbVibrationStates;
                int idx = FeedbackPadIndex;
                var vib = (states != null && idx >= 0 && idx < states.Length) ? states[idx] : null;
                if (vib != null)
                {
                    if (_leftPulseEndTick != 0 && now >= _leftPulseEndTick)
                    {
                        vib.LeftMotorSpeed = 0;
                        _leftPulseEndTick = 0;
                    }
                    if (_rightPulseEndTick != 0 && now >= _rightPulseEndTick)
                    {
                        vib.RightMotorSpeed = 0;
                        _rightPulseEndTick = 0;
                    }
                }
                ScheduleExpiryLocked();
            }
        }

        /// <summary>Zeroes both haptic lanes. Caller holds
        /// <see cref="_hapticLock"/>.</summary>
        private void ZeroHapticLanes()
        {
            _leftPulseEndTick = 0;
            _rightPulseEndTick = 0;
            var states = _fbVibrationStates;
            int idx = FeedbackPadIndex;
            if (states != null && idx >= 0 && idx < states.Length && states[idx] != null)
            {
                states[idx].LeftMotorSpeed = 0;
                states[idx].RightMotorSpeed = 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
