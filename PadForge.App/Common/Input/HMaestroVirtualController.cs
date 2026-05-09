using System;
using HIDMaestro;
using PadForge.Engine;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Unified virtual controller backed by HIDMaestro. Replaces the v2
    /// Xbox360VirtualController, DS4VirtualController, and ExtendedVirtualController
    /// classes — one IVirtualController implementation handles every preset
    /// and custom HID descriptor through a single SDK surface.
    ///
    /// The Type property reports the user-facing category (Xbox / PlayStation /
    /// Extended) so existing per-type counting logic in InputService keeps
    /// working. The actual HIDMaestro profile is supplied at construction.
    /// </summary>
    internal sealed class HMaestroVirtualController : IVirtualController
    {
        private readonly HMContext _ctx;
        private readonly HMProfile _profile;
        private readonly VirtualControllerType _type;
        private HMController _controller;
        private HMaestroFfbDecoder _ffbDecoder;
        private DualSensePassthroughDispatcher _ds5Dispatcher;
        private UserEffectsDispatcher _userEffectsDispatcher;
        private bool _disposed;

        // DualSense / DualSense Edge VID/PID — used to gate the
        // DS5 effect message pass-through dispatcher.  Both USB and BT
        // variants of each profile share the same VID/PID; the profile
        // ID slug differs but doesn't matter for the gating decision.
        private const ushort SonyVid = 0x054C;
        private const ushort DualSensePid = 0x0CE6;
        private const ushort DualSenseEdgePid = 0x0DF2;

        private bool IsDualSenseVirtual =>
            _profile.VendorId == SonyVid
            && (_profile.ProductId == DualSensePid || _profile.ProductId == DualSenseEdgePid);

        public VirtualControllerType Type => _type;
        public bool IsConnected { get; private set; }
        public int FeedbackPadIndex { get; set; }
        public string ProfileId => _profile.Id;
        public ushort ProfileVendorId => _profile.VendorId;
        public ushort ProfileProductId => _profile.ProductId;

        // Cached HMAxis keys for the active profile's first two sticks +
        // first two triggers, resolved once at construction so the 1 kHz
        // SubmitGamepadState hot path doesn't repeatedly walk
        // _profile.Sticks / _profile.Triggers (each property access
        // allocates a fresh List). HMAxis.None means "this slot doesn't
        // exist on the profile" (e.g. wheels with no second stick); the
        // hot path skips writes to such slots.
        //
        // The standard 6-slot canonical surface maps to whatever HID
        // usages the active profile actually uses — Sony's Z=right-stick
        // and Rx=left-trigger axisMap overrides resolve through
        // _profile.Sticks/_profile.Triggers automatically, so callers
        // pass XInput-convention LX/LY/RX/RY/LT/RT and the profile's
        // simple-view derivation lands them on the right wire axes.
        private HMAxis _axLeftStickX, _axLeftStickY;
        private HMAxis _axRightStickX, _axRightStickY;
        private HMAxis _axLeftTrigger, _axRightTrigger;

        // Per-call axes scratch dict, allocated once and reused across
        // every SubmitGamepadState / SubmitExtendedRawState frame to
        // keep the 1 kHz hot path allocation-free. HMGamepadState.Axes
        // is a Dictionary<HMAxis, float>; the encoder consumes the dict
        // by key lookup and is fine with reused references.
        private readonly Dictionary<HMAxis, float> _axesScratch = new();

        public HMaestroVirtualController(HMContext ctx, HMProfile profile, VirtualControllerType type)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _type = type;

            // Resolve the 6-slot canonical axis keys from the profile's
            // Sticks / Triggers simple-view lists once. Sticks beyond
            // index 1 and triggers beyond index 1 don't fit the canonical
            // gamepad surface and are left unresolved (HMAxis.None) — the
            // Custom Extended path uses _profile.Sticks / .Triggers
            // directly to address every available axis.
            var sticks = _profile.Sticks;
            var triggers = _profile.Triggers;
            _axLeftStickX  = sticks.Count   > 0 ? sticks[0].XAxis  : HMAxis.None;
            _axLeftStickY  = sticks.Count   > 0 ? sticks[0].YAxis  : HMAxis.None;
            _axRightStickX = sticks.Count   > 1 ? sticks[1].XAxis  : HMAxis.None;
            _axRightStickY = sticks.Count   > 1 ? sticks[1].YAxis  : HMAxis.None;
            _axLeftTrigger  = triggers.Count > 0 ? triggers[0].Axis : HMAxis.None;
            _axRightTrigger = triggers.Count > 1 ? triggers[1].Axis : HMAxis.None;
        }

        public void Connect()
        {
            if (IsConnected) return;
            _controller = _ctx.CreateController(_profile);

            // HM v1.3.5 round-2 timing hook for issue #21 (USB virtual
            // input regression). Fires on the submit thread inline; we
            // log only outliers (> 1 ms) to keep volume bounded — at
            // 250 Hz polling, even a 1-in-100 outlier rate produces
            // ~2.5 lines/sec which is fine for the diag file. HM uses
            // these to localize whether the jerk lives in WriteInputFrame
            // (P/Invoke / kernel-section / SetEvent) or BuildReportInto
            // (managed encoder).
            _controller.OnSubmitLatencyMicros = micros =>
            {
                if (micros < 1000) return;
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-submit-latency.log"),
                        $"{DateTime.UtcNow:HH:mm:ss.fff} pad={FeedbackPadIndex} profile={_profile.Id} latency_us={micros}\n");
                }
                catch { }
            };

            // Publish PID Pool + initial PID State BEFORE any GetFeature can
            // race in. DirectInput's CDIEffect::CreateEffect issues
            // GetFeature(PidPool) up-front to discover capabilities, so the
            // shared section must be populated by the time the device shows
            // up to host enumeration. Lazy init on first OutputReceived was
            // too late — the first GetFeature can land before the first
            // SetFeature/Output packet ever does.
            //
            // Gate on the descriptor carrying the PID FFB block, not on VID.
            // The synthetic Custom profile (0xBEEF) ships with FFB built in,
            // but Extended slots that customize a non-Custom catalog profile
            // also rebuild the descriptor with AddPidFfbBlock when the user
            // ticks the FFB checkbox — those keep the catalog VID/PID (so
            // games still recognize the original device's signature) but
            // need the same decoder + PID-state publish path. Inspecting
            // the descriptor catches both cases without coupling to VID.
            if (DescriptorHasPidFfbBlock(_profile.DescriptorHex))
            {
                _ffbDecoder = new HMaestroFfbDecoder(_controller);
                _ffbDecoder.PublishInitialState();
            }

            IsConnected = true;
        }

        public void Disconnect()
        {
            if (!IsConnected) return;

            // Tear the DS5 pass-through dispatcher down BEFORE disposing
            // _controller — once _controller.Dispose() runs, OutputReceived
            // fires its final close events; we want the dispatcher's
            // channel writer rejecting further enqueues by then.
            try
            {
                _ds5Dispatcher?.Dispose();
            }
            catch { /* best-effort teardown */ }
            finally
            {
                _ds5Dispatcher = null;
            }

            // User-effects dispatcher unsubscribes its PropertyChanged
            // handler on Dispose; safe to call regardless of whether one
            // was ever attached.
            try
            {
                _userEffectsDispatcher?.Dispose();
            }
            catch { /* best-effort teardown */ }
            finally
            {
                _userEffectsDispatcher = null;
            }

            _controller?.Dispose();
            _controller = null;
            IsConnected = false;
        }

        /// <summary>Attaches a per-slot
        /// <see cref="PlayStationSlotConfig"/> so user-configured trigger
        /// / lightbar / audio effects synthesize and forward to the
        /// assigned physical DualSense via SDL_SendGamepadEffect.
        /// Called by Step 5 right after RegisterFeedbackCallback for
        /// every HM-backed slot — the dispatcher's runtime resolve
        /// returns no targets when the slot has no DS5 physical mapped,
        /// so attaching unconditionally is cheap. Decoupling the gate
        /// from the virtual's identity lets Feature B work when the
        /// user has a DS4 virtual + physical DS5 assignment, or any
        /// other mismatch where they still want to drive the assigned
        /// physical DS5's lightbar / triggers / audio. Idempotent —
        /// re-attach replaces the existing dispatcher's binding.</summary>
        public void AttachPlayStationConfig(PadForge.ViewModels.PlayStationSlotConfig config)
        {
            if (config == null) return;

            if (_userEffectsDispatcher == null)
            {
                _userEffectsDispatcher = new UserEffectsDispatcher(FeedbackPadIndex, config);
                _userEffectsDispatcher.ApplyOnce();
            }
            else
            {
                _userEffectsDispatcher.Rebind(config);
            }
        }

        /// <summary>Triggers a fresh apply pass on the user-effects
        /// dispatcher. Called by InputService on every
        /// <see cref="InputManager.DevicesUpdated"/> tick so a freshly-
        /// reconnected DualSense gets its configured lightbar / trigger
        /// / audio state re-pushed without waiting for the user to
        /// touch a slider. No-op when no dispatcher is attached.</summary>
        public void ReApplyUserEffects()
        {
            _userEffectsDispatcher?.ApplyOnce();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        /// <summary>Pass-through to <c>HMController.SubmitRawReport</c> for
        /// Sony USB Report 0x01 packets carrying touchpad / gyro / accel /
        /// battery data that <c>HMGamepadState</c> doesn't model. Step 5
        /// calls this AFTER <see cref="SubmitGamepadState"/> so the GIP
        /// buffer stays consistent and the raw report overrides the HID
        /// surface with the full Sony layout.</summary>
        public void SubmitRawReport(ReadOnlySpan<byte> report)
        {
            if (_controller == null) return;
            _controller.SubmitRawReport(report);
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (_controller == null) return;

            // No dedup and no rate limit here — Step 5 already honors the
            // user-configured polling interval (default 1kHz). HIDMaestro is
            // consumer-driven ("the consumer drives the cadence" per the SDK
            // docstring) so every call forwards a fresh frame. Deduping on
            // unchanged state risked dropping rapid press+release bursts
            // between the game's HID reads.

            // HM v1.3.9: HMGamepadState.Axes is a Dictionary<HMAxis, float>
            // keyed by HID usage; named LeftStickX / LeftStickY / RightStickX /
            // RightStickY / LeftTrigger / RightTrigger slots are gone. All
            // values normalize to [0, 1] uniformly:
            //   sticks: 0.5 = center, 0.0 = leftmost / topmost, 1.0 = rightmost / bottommost
            //   triggers: 0.0 = released, 1.0 = fully pressed
            //
            // PadForge's source convention here is XInput (signed -32768..+32767
            // for sticks, ushort 0..65535 for triggers, Y+ = stick up).
            //   * Stick X: shift signed range to unsigned [0..1] —
            //       (v + 32768) / 65535
            //   * Stick Y: Y+ = up in XInput, Y+ = down in HID convention
            //     (which is what HM's encoder writes to the wire), so flip:
            //       (32768 - v) / 65535
            //     ThumbLY=+32767 (XInput up)   -> 0.0 (HM up)
            //     ThumbLY=-32768 (XInput down) -> 1.0 (HM down)
            //     ThumbLY=0      (centered)    -> 0.5
            //   * Trigger: already unsigned, just divide.
            //
            // Hot-path: clear and repopulate the cached _axesScratch dict.
            // Cached HMAxis keys (resolved at construction from the profile's
            // simple-view Sticks/Triggers lists) skip the per-call list walk
            // and let Sony's Z=right-stick / Rx=left-trigger remap land
            // automatically.
            _axesScratch.Clear();
            if (_axLeftStickX  != HMAxis.None) _axesScratch[_axLeftStickX]  = (gp.ThumbLX  + 32768f) / 65535f;
            if (_axLeftStickY  != HMAxis.None) _axesScratch[_axLeftStickY]  = (32768f - gp.ThumbLY)  / 65535f;
            if (_axRightStickX != HMAxis.None) _axesScratch[_axRightStickX] = (gp.ThumbRX  + 32768f) / 65535f;
            if (_axRightStickY != HMAxis.None) _axesScratch[_axRightStickY] = (32768f - gp.ThumbRY)  / 65535f;
            if (_axLeftTrigger  != HMAxis.None) _axesScratch[_axLeftTrigger]  = gp.LeftTrigger  / 65535f;
            if (_axRightTrigger != HMAxis.None) _axesScratch[_axRightTrigger] = gp.RightTrigger / 65535f;

            var state = new HMGamepadState
            {
                Axes = _axesScratch,
                Buttons = MapButtons(gp.Buttons),
                Hat = MapHat(gp.Buttons),
            };

            _controller.SubmitState(state);
        }

        // Sony int16 sensor scaling — matches SonyReportPackers.ScaleGyro
        // and ScaleAccel exactly, which is the working USB path's known-
        // good conversion:
        //   Gyro range ±2000 deg/s → int16 (scale 32767/2000 ≈ 16.38)
        //   Accel range ±4 g       → int16 (scale 32767/4    ≈ 8191.75)
        // Don't reinvent: BT virtuals must produce the same byte values
        // the USB SubmitRawReport path produces, just at different byte
        // positions (the BT Report 0x31 vendor-blob layout).
        private const float GyroScale  = 32767f / 2000f;
        private const float AccelScale = 32767f / 4f;

        // Counters for the touchpad packet sequence + finger tracking IDs.
        // PadForge's TouchpadState carries down/up bools per finger but no
        // tracking ID; we synthesize one that increments on each new touch
        // so consumers see a stable ID while a finger is held and a fresh
        // one on each new press.
        private byte _touchpadPacketCounter;
        private byte _touchpadFinger0Id;
        private byte _touchpadFinger1Id;
        private bool _touchpadFinger0PrevDown;
        private bool _touchpadFinger1PrevDown;

        /// <summary>HM v1.3.5+ overload that submits gamepad state PLUS
        /// touchpad / IMU / battery / mic-mute / headphone data via the
        /// extended <c>HMGamepadState</c> fields. Sony BT virtuals (Report
        /// 0x31 vendor-blob) light up touchpad / gyro / accel / battery on
        /// the consumer side from this path; SubmitRawReport (called
        /// separately for USB profiles) covers the same surface for the
        /// USB Report 0x01 layout. Pass through whatever the assigned
        /// physical pad reported via SDL — for non-Sony or sensor-less
        /// physicals, supply zeros / Has=false and the encoder writes
        /// zeros to those positions.</summary>
        public void SubmitGamepadState(
            Gamepad gp,
            in TouchpadState tp,
            in MotionSnapshot motion,
            byte batteryPercent,
            bool batteryCharging)
        {
            if (_controller == null) return;

            // Tracking-ID synthesis. Bump each finger's ID on rising edge of
            // its down state; keep stable while held; ID stays at last value
            // (with active bit cleared via TouchpadFingerNActive=false) on
            // release so the consumer sees a clean lift then a new press
            // gets a new ID next time.
            if (tp.Down0 && !_touchpadFinger0PrevDown) _touchpadFinger0Id++;
            if (tp.Down1 && !_touchpadFinger1PrevDown) _touchpadFinger1Id++;
            _touchpadFinger0PrevDown = tp.Down0;
            _touchpadFinger1PrevDown = tp.Down1;
            if (tp.PacketCounter != _touchpadPacketCounter) _touchpadPacketCounter = tp.PacketCounter;

            byte battery10 = (byte)Math.Clamp(batteryPercent / 10, 0, 10);
            bool batteryFull = batteryPercent >= 100;

            // Same axis-dict population as the basic SubmitGamepadState
            // overload — see the explanatory comment block there for the
            // XInput → HM v1.3.9 [0..1] conversion rules and why the
            // cached HMAxis keys avoid the per-call list-walk allocation.
            _axesScratch.Clear();
            if (_axLeftStickX  != HMAxis.None) _axesScratch[_axLeftStickX]  = (gp.ThumbLX  + 32768f) / 65535f;
            if (_axLeftStickY  != HMAxis.None) _axesScratch[_axLeftStickY]  = (32768f - gp.ThumbLY)  / 65535f;
            if (_axRightStickX != HMAxis.None) _axesScratch[_axRightStickX] = (gp.ThumbRX  + 32768f) / 65535f;
            if (_axRightStickY != HMAxis.None) _axesScratch[_axRightStickY] = (32768f - gp.ThumbRY)  / 65535f;
            if (_axLeftTrigger  != HMAxis.None) _axesScratch[_axLeftTrigger]  = gp.LeftTrigger  / 65535f;
            if (_axRightTrigger != HMAxis.None) _axesScratch[_axRightTrigger] = gp.RightTrigger / 65535f;

            var state = new HMGamepadState
            {
                Axes = _axesScratch,
                Buttons = MapButtons(gp.Buttons),
                Hat = MapHat(gp.Buttons),

                TouchpadFinger0Active = tp.Down0,
                TouchpadFinger0X = (ushort)Math.Clamp((int)Math.Round(tp.X0 * 1919f), 0, 1919),
                TouchpadFinger0Y = (ushort)Math.Clamp((int)Math.Round(tp.Y0 * 1079f), 0, 1079),
                TouchpadFinger0Id = (byte)(_touchpadFinger0Id & 0x7F),
                TouchpadFinger1Active = tp.Down1,
                TouchpadFinger1X = (ushort)Math.Clamp((int)Math.Round(tp.X1 * 1919f), 0, 1919),
                TouchpadFinger1Y = (ushort)Math.Clamp((int)Math.Round(tp.Y1 * 1079f), 0, 1079),
                TouchpadFinger1Id = (byte)(_touchpadFinger1Id & 0x7F),
                TouchpadPacketCounter = _touchpadPacketCounter,

                GyroPitch = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.GyroPitch * GyroScale), short.MinValue, short.MaxValue) : (short)0,
                GyroYaw   = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.GyroYaw   * GyroScale), short.MinValue, short.MaxValue) : (short)0,
                GyroRoll  = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.GyroRoll  * GyroScale), short.MinValue, short.MaxValue) : (short)0,
                AccelX    = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.AccelX    * AccelScale), short.MinValue, short.MaxValue) : (short)0,
                AccelY    = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.AccelY    * AccelScale), short.MinValue, short.MaxValue) : (short)0,
                AccelZ    = motion.HasMotion ? (short)Math.Clamp((int)Math.Round(motion.AccelZ    * AccelScale), short.MinValue, short.MaxValue) : (short)0,
                SensorTimestamp = (uint)(motion.TimestampUs & 0xFFFFFFFF),

                BatteryLevel    = battery10,
                BatteryCharging = batteryCharging,
                BatteryFull     = batteryFull,

                // Not currently sourced from PadForge's input pipeline — SDL3
                // doesn't surface DS5's MIC_MUTE state or the headphones-
                // connected bit through the gamepad API. Leave at default
                // (false) until we add a side-channel read; HM's encoder
                // writes zero to the corresponding bits.
                MicMuted = false,
                HeadphonesConnected = false,
            };

            // One-line per-second diag — tells us whether the new overload
            // is being hit and whether we're feeding non-zero data into the
            // encoder. Sampled at ~1 Hz so it doesn't drown the log.
            long nowTickMs = Environment.TickCount64;
            if (nowTickMs - _lastExtendedDiagTickMs > 1000)
            {
                _lastExtendedDiagTickMs = nowTickMs;
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-ds5-passthrough.log"),
                        $"{DateTime.UtcNow:HH:mm:ss.fff} [submit-ext] pad={FeedbackPadIndex} profile={_profile.Id} " +
                        $"tp0=({state.TouchpadFinger0Active},{state.TouchpadFinger0X},{state.TouchpadFinger0Y}) " +
                        $"gyro=({state.GyroPitch},{state.GyroYaw},{state.GyroRoll}) " +
                        $"accel=({state.AccelX},{state.AccelY},{state.AccelZ}) " +
                        $"battery=({state.BatteryLevel},charging={state.BatteryCharging},full={state.BatteryFull})\n");
                }
                catch { }
            }

            _controller.SubmitState(state);
        }

        private long _lastExtendedDiagTickMs;

        /// <summary>
        /// Submit an ExtendedRawState (produced by the Extended dynamic
        /// mapping path) directly to HIDMaestro. Covers the full HMGamepadState
        /// surface — 6 axes, 13 buttons, and a hat — without going through
        /// the XInput Gamepad intermediate, so Touchpad/Share buttons and
        /// arbitrary profile layouts aren't truncated the way
        /// <see cref="SubmitGamepadState"/>'s 11-button XInput bitmap would.
        ///
        /// Axis indices are computed via the same interleave logic as
        /// <see cref="PadForge.ViewModels.ExtendedSlotConfig.ComputeAxisLayout"/>
        /// so the right-stick axes land at the correct offsets regardless of
        /// whether the active profile has 0, 1, or 2 triggers. Hardcoding
        /// (3, 4) for right-stick X/Y silently dropped Stick 2 Y for every
        /// 0-trigger or 1-trigger profile.
        ///
        /// ExtendedRawState.Axes is in HID convention per Step 3
        /// (positive = down/right), matching HMGamepadState's internal
        /// convention, so no Y negation needed — pass signed short
        /// straight through as a normalized float. Triggers in the raw
        /// state are signed short centered at 0; convert to the 0..1
        /// float range HMGamepadState expects.
        /// </summary>
        public void SubmitExtendedRawState(ExtendedRawState raw, int sticks, int triggers)
        {
            if (_controller == null) return;

            short Ax(int i) => (raw.Axes != null && i >= 0 && i < raw.Axes.Length) ? raw.Axes[i] : (short)0;

            // Convert raw signed short (-32768..+32767) to HM v1.3.9's
            // unified [0..1] axis range. Stick rest is at signed 0
            // -> 0.5, fully positive -> 1.0, fully negative -> 0.0;
            // trigger rest is at signed -32768 -> 0.0, fully pressed
            // (signed +32767) -> 1.0. Both shapes use the same shift
            // because raw.Axes is already centered on signed zero for
            // sticks (per ExtendedSlotConfig's signed-short convention)
            // and on short.MinValue for triggers (per
            // MapToExtendedTriggerAxis's released-rest contract).
            float ToHmRange(short v) => (v + 32768f) / 65535f;

            // Replicate ExtendedSlotConfig.ComputeAxisLayout. Interleaved
            // groups of (stickX, stickY, trigger) while both sticks and
            // triggers are available; trailing sticks (no-trigger case) pack
            // sequentially at (prev, prev+1), trailing triggers pack one
            // index at a time after that. Guard -1 on anything we don't have.
            int interleave = System.Math.Min(sticks, triggers);
            int StickX(int g) =>
                g < interleave ? g * 3
                : g < sticks   ? interleave * 3 + (g - interleave) * 2
                               : -1;
            int StickY(int g) => StickX(g) >= 0 ? StickX(g) + 1 : -1;
            int TriggerIdx(int g) =>
                g < interleave ? g * 3 + 2
                : g < triggers ? interleave * 3 + System.Math.Max(0, sticks - interleave) * 2 + (g - interleave)
                               : -1;

            // HMButton is a [Flags] uint enum with named members for bits 0..12
            // (A..Share). HidReportBuilder iterates bits 0..31 of the mask
            // passed as (uint)state.Buttons, so any bit we set beyond 12
            // still surfaces — it maps to the profile's corresponding
            // descriptor button position (direct index, or via the profile's
            // ButtonMap if one is declared). Profiles with 13+ buttons (Stadia,
            // flight sticks, wheels, etc.) rely on this to receive inputs
            // past the named button range. Pass through all 32 bits from
            // the raw state mask verbatim.
            uint buttonMask = 0;
            for (int i = 0; i < 32; i++)
            {
                if (raw.IsButtonPressed(i))
                    buttonMask |= 1u << i;
            }
            var buttons = (HMButton)buttonMask;

            var hat = HMHat.None;
            if (raw.Povs != null && raw.Povs.Length > 0)
            {
                int pov = raw.Povs[0];
                if (pov >= 0)
                {
                    int octant = ((pov + 2250) / 4500) % 8;
                    hat = octant switch
                    {
                        0 => HMHat.North,
                        1 => HMHat.NorthEast,
                        2 => HMHat.East,
                        3 => HMHat.SouthEast,
                        4 => HMHat.South,
                        5 => HMHat.SouthWest,
                        6 => HMHat.West,
                        7 => HMHat.NorthWest,
                        _ => HMHat.None
                    };
                }
            }

            // HM v1.3.9: address every analog axis the profile exposes
            // by HMAxis key. Drive _profile.Sticks / _profile.Triggers
            // directly — those are the SDK's authoritative per-row
            // surface. raw.Axes is HID-convention (Y+ = down), so both
            // stick X and stick Y use the same plain ToHmRange shift,
            // no additional Y inversion vs. the basic SubmitGamepadState
            // path's XInput→HID flip.
            var profileSticks = _profile.Sticks;
            var profileTriggers = _profile.Triggers;
            _axesScratch.Clear();
            int sticksToWrite = System.Math.Min(sticks, profileSticks.Count);
            for (int s = 0; s < sticksToWrite; s++)
            {
                int xi = StickX(s);
                int yi = StickY(s);
                if (profileSticks[s].XAxis != HMAxis.None && xi >= 0)
                    _axesScratch[profileSticks[s].XAxis] = ToHmRange(Ax(xi));
                if (profileSticks[s].YAxis != HMAxis.None && yi >= 0)
                    _axesScratch[profileSticks[s].YAxis] = ToHmRange(Ax(yi));
            }
            int triggersToWrite = System.Math.Min(triggers, profileTriggers.Count);
            for (int t = 0; t < triggersToWrite; t++)
            {
                int ti = TriggerIdx(t);
                if (profileTriggers[t].Axis != HMAxis.None && ti >= 0)
                    _axesScratch[profileTriggers[t].Axis] = ToHmRange(Ax(ti));
            }

            var state = new HMGamepadState
            {
                Axes = _axesScratch,
                Buttons = buttons,
                Hat = hat,
            };

            _controller.SubmitState(state);
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
            if (_controller == null) return;

            // Virtual DualSense slots get a per-VC pass-through dispatcher
            // that forwards DS5 effect messages (Report 0x02 USB / 0x31 BT)
            // to the assigned physical DualSense via SDL_SendGamepadEffect.
            // Carries adaptive trigger commands, lightbar RGB, audio bytes,
            // and rumble in a single message.  Created here so its lifetime
            // matches the OutputReceived subscription it serves.
            if (IsDualSenseVirtual && _ds5Dispatcher == null)
            {
                _ds5Dispatcher = new DualSensePassthroughDispatcher(padIndex);
                _ds5Dispatcher.Start();
            }

            // Sony pads (DS5, DS4 in either transport) consume HM v1.3.5's
            // OutputDecoded event for both the rumble decode AND the DS5
            // passthrough forward. The decoded fields surface parsed
            // `leftMotor` / `rightMotor` (transport-agnostic) plus a
            // pre-stripped `sdlPassthrough` byte[] (47 bytes for DS5, 31
            // for DS4) that's already in USB-equivalent form regardless
            // of whether the host wrote Report 0x02 (USB) or Report 0x31
            // (BT framing + CRC32). PadForge forwards `sdlPassthrough`
            // verbatim via SDL_SendGamepadEffect — SDL handles the
            // transport-specific framing for the destination physical pad.
            //
            // Compared to the prior byte-offset approach this also resolves
            // the latent DS5 BT bug where Report 0x31's framing offset
            // shifted every byte by two, plus the off-by-one DS4 read
            // where the old code read the reserved byte instead of
            // leftMotor.
            //
            // vibrationStates is written for every Sony virtual regardless
            // of whether a DualSense passthrough is in flight. Step 2's
            // ApplyForceFeedback reads it to fire SDL_RumbleJoystick on
            // non-Sony devices on the same slot (Xbox, third-party, etc.).
            // Double-fire on the real DualSense is prevented at a different
            // layer: SlotRumbleForDeviceProvider returns (0,0) for any
            // device that's a passthrough target, so the Sony dispatcher
            // emits zero rumble bytes for that specific device while the
            // passthrough dispatcher carries the game's actual rumble.
            _controller.OutputDecoded += (ctrl, e) =>
            {
                int idx = FeedbackPadIndex;
                if (idx < 0 || idx >= vibrationStates.Length) return;

                if (e.Fields.TryGetValue("leftMotor", out var lmObj) && lmObj is byte left
                 && e.Fields.TryGetValue("rightMotor", out var rmObj) && rmObj is byte right)
                {
                    vibrationStates[idx].LeftMotorSpeed  = (ushort)(left  * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(right * 257);
                }

                if (_ds5Dispatcher != null
                    && _profile.VendorId == SonyVid
                    && e.Fields.TryGetValue("effectPayload", out var epObj)
                    && epObj is byte[] effectPayload
                    && effectPayload.Length > 0)
                {
                    _ds5Dispatcher.Enqueue(0x02, effectPayload);
                    // Capture per-subsystem state from the external write.
                    // The user-effects dispatcher mirrors each touched
                    // subsystem (rumble / triggers / mic / lightbar /
                    // player) verbatim for the grace window, while still
                    // animating subsystems the writer didn't touch.
                    UserEffectsDispatcher.NotifyExternalSubsystems(idx, effectPayload);
                }
            };

            _controller.OutputReceived += (ctrl, pkt) =>
            {
                int idx = FeedbackPadIndex;
                if (idx < 0 || idx >= vibrationStates.Length) return;

                var data = pkt.Data.Span;

                // Diagnostic capture — every output report that reaches the
                // virtual, with source / report ID / length / first-16-byte
                // hex. Used to diagnose why some HID consumers (Chromium's
                // DualShock4 driver in particular) write to a virtual pad
                // but the rumble doesn't propagate. Compare a known-good
                // writer's bytes (Steam Input) against the suspect
                // writer's bytes to see whether the report ID, transport
                // shape, or framing differs. Cheap enough to leave on
                // through the v3.1.4 dev cycle; strip at release prep.
                try
                {
                    int dump = Math.Min(data.Length, 16);
                    var sb = new System.Text.StringBuilder(dump * 3);
                    for (int i = 0; i < dump; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(data[i].ToString("X2"));
                    }
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "padforge-output-capture.log"),
                        $"{DateTime.UtcNow:HH:mm:ss.fff} pad={idx} profile={_profile.Id} src={pkt.Source} rpt=0x{pkt.ReportId:X2} len={data.Length} bytes=[{sb}]\n");
                }
                catch { }

                // XInput vibration packet layout (from IOCTL_XUSB_SET_STATE):
                //   data[0] = 0x00 (command)
                //   data[1] = 0x08 (size)
                //   data[2] = left motor byte  (wLeftMotorSpeed  >> 8)
                //   data[3] = right motor byte (wRightMotorSpeed >> 8)
                //   data[4] = reserved
                // Chromium's browser Gamepad API sends dual-rumble via this
                // path; the alternating hi=127 / hi=0 pattern IS the browser's
                // square-wave vibration waveform (not keep-alive noise) — do
                // NOT filter packets where both bytes are 0 (that's the "off"
                // phase of the duty cycle).
                if (pkt.Source == HMOutputSource.XInput && data.Length >= 5)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 257);
                    return;
                }

                // Xbox Series Bluetooth (Microsoft VID 0x045E) — browser
                // Gamepad API on Chromium sends vibration to Xbox Series BT
                // via a HID output report (NOT XInput, unlike wired Xbox 360).
                // Layout is 7 bytes: [trigL, trigR, motorL, motorR, duration,
                // startDelay, loopCount]. Motor bytes are 0..100 magnitudes.
                // Scale to ushort range (~655x). Verified against HIDMaestro
                // test app log of xbox-series-xs-bt + gamepad-tester.com.
                //
                // Gate on _ffbDecoder == null so Microsoft-VID profiles that
                // implement PID FFB (the SideWinder Force Feedback 2 is the
                // canonical case — Microsoft VID 0x045E, hand-authored PID
                // descriptor) don't get their 4..7-byte PID FFB reports
                // stolen by this rumble shape. Xbox 360 / One / Series
                // pads don't have PID FFB so the gate is invisible there.
                if (_ffbDecoder == null
                    && pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x045E
                    && data.Length >= 4
                    && data.Length < 8)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 655);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 655);
                    return;
                }

                // Xbox wired / wireless-receiver long HID rumble report
                // (legacy format, vendor-specific bytes 5/6). Same
                // _ffbDecoder gate as above — the SideWinder's 15-byte
                // PID Set Effect Report would otherwise be misread as
                // an Xbox 8+-byte rumble packet here.
                if (_ffbDecoder == null
                    && pkt.Source == HMOutputSource.HidOutput
                    && _profile.VendorId == 0x045E
                    && data.Length >= 8)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[5] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[6] * 257);
                    return;
                }

                // PID FFB-capable Extended profile (Custom synthetic OR a
                // catalog profile with Customize+FFB on, where Step 5
                // rebuilt the descriptor with AddPidFfbBlock). Decode
                // Set Effect / Set Constant / Set Periodic / Set Condition /
                // Effect Operation / Block Free / Device Control / Device Gain
                // packets, aggregate running effects into the Vibration with
                // directional + condition data so SetDirectionalHapticForces
                // can route real DirectInput FFB to physical wheels and sticks.
                // _ffbDecoder is non-null iff Connect() detected the PID FFB
                // block in the descriptor — that's the gate that matters,
                // not the VID (catalog profiles keep their original VID/PID).
                if (_ffbDecoder != null)
                {
                    if (pkt.Source == HMOutputSource.HidOutput)
                    {
                        _ffbDecoder.OnHidOutput(pkt.ReportId, data);
                        _ffbDecoder.Apply(vibrationStates[idx]);
                        return;
                    }
                    if (pkt.Source == HMOutputSource.HidFeature)
                    {
                        _ffbDecoder.OnHidFeature(pkt.ReportId, data);
                        return;
                    }
                }
            };
        }

        private static HMButton MapButtons(ushort xinputButtons)
        {
            HMButton b = HMButton.None;
            if ((xinputButtons & Gamepad.A) != 0) b |= HMButton.A;
            if ((xinputButtons & Gamepad.B) != 0) b |= HMButton.B;
            if ((xinputButtons & Gamepad.X) != 0) b |= HMButton.X;
            if ((xinputButtons & Gamepad.Y) != 0) b |= HMButton.Y;
            if ((xinputButtons & Gamepad.LEFT_SHOULDER) != 0) b |= HMButton.LeftBumper;
            if ((xinputButtons & Gamepad.RIGHT_SHOULDER) != 0) b |= HMButton.RightBumper;
            if ((xinputButtons & Gamepad.BACK) != 0) b |= HMButton.Back;
            if ((xinputButtons & Gamepad.START) != 0) b |= HMButton.Start;
            if ((xinputButtons & Gamepad.LEFT_THUMB) != 0) b |= HMButton.LeftStick;
            if ((xinputButtons & Gamepad.RIGHT_THUMB) != 0) b |= HMButton.RightStick;
            if ((xinputButtons & Gamepad.GUIDE) != 0) b |= HMButton.Guide;
            if ((xinputButtons & Gamepad.TOUCHPAD) != 0) b |= HMButton.Touchpad;
            return b;
        }

        /// <summary>True when the descriptor declares a HID PID FFB block.
        /// Detected by the canonical opening signature
        /// <c>05 0F 09 21 A1 02</c> — Usage Page (Physical Interface),
        /// Usage (Set Effect Report), Collection (Logical) — which begins
        /// <see cref="HidDescriptorBuilder.MinimumViablePidFfbBlock"/>. The
        /// Physical Interface usage page (0x0F) is reserved for PID and
        /// doesn't appear in non-FFB controller descriptors, so the leading
        /// pair alone would suffice; matching three bytes deeper just makes
        /// false positives from coincidental byte sequences impossible.
        /// Returns false when the descriptor hex is empty/null.</summary>
        private static bool DescriptorHasPidFfbBlock(string descriptorHex)
        {
            if (string.IsNullOrEmpty(descriptorHex)) return false;
            // Detect the PID FFB Set Effect Report Collection signature:
            //   09 21        Usage (Set Effect Report)            -- PID 0x21
            //   A1 02        Collection (Logical)
            // Every HID PID FFB descriptor — synthetic or hand-authored —
            // emits this collection inside the PID Usage Page. Matching
            // it directly also catches descriptors that declare Usage
            // Page 0x0F once at the top of their PID block and then have
            // intervening C0 end-collections before each sub-report
            // (SideWinder Force Feedback 2 is the canonical case: the
            // earlier check for the literal "050f0921a102" sequence
            // wanted Usage Page directly adjacent to the Set Effect
            // collection, which AddPidFfbBlock-built profiles satisfy
            // but hand-authored ones don't). False-positive risk is
            // negligible — Usage 0x21 with a Logical Collection has no
            // common meaning outside the PID page.
            return descriptorHex.IndexOf("0921a102", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HMHat MapHat(ushort xinputButtons)
        {
            bool up = (xinputButtons & Gamepad.DPAD_UP) != 0;
            bool down = (xinputButtons & Gamepad.DPAD_DOWN) != 0;
            bool left = (xinputButtons & Gamepad.DPAD_LEFT) != 0;
            bool right = (xinputButtons & Gamepad.DPAD_RIGHT) != 0;

            if (up && right) return HMHat.NorthEast;
            if (up && left) return HMHat.NorthWest;
            if (down && right) return HMHat.SouthEast;
            if (down && left) return HMHat.SouthWest;
            if (up) return HMHat.North;
            if (down) return HMHat.South;
            if (left) return HMHat.West;
            if (right) return HMHat.East;
            return HMHat.None;
        }
    }
}
