using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly System.Collections.Generic.IReadOnlyList<HIDMaestro.HMSimpleStick> _cachedProfileSticks;
        private readonly System.Collections.Generic.IReadOnlyList<HIDMaestro.HMSimpleTrigger> _cachedProfileTriggers;
        private readonly VirtualControllerType _type;
        private HMController _controller;
        private HMaestroFfbDecoder _ffbDecoder;
        private Vibration[] _fbVibrationStates; // for the per-tick FFB re-evaluation
        private DualSensePassthroughDispatcher _ds5Dispatcher;
        private UserEffectsDispatcher _userEffectsDispatcher;
        // Guards _userEffectsDispatcher against the attach/teardown race:
        // AttachDeviceConfig runs on the UI thread (InputService's
        // DevicesUpdated handler) while Disconnect runs on the poll thread's
        // destroy pass, and nothing else orders them.
        private readonly object _dispatcherLock = new();
        private bool _disposed;

        // ── Inbound game-feedback pack (issue #236) ──
        // Controller-LOCAL storage for the raw rumble the game sent THIS
        // virtual controller, packed per LfeOutputState. Keyed by the VC
        // instance, never by pad index: the slot-reorder reroute re-points
        // _virtualControllers[] and the pack travels with its VC, so a
        // swap can never land slot A's rumble on slot B the way the
        // captured FeedbackPadIndex could. The poll thread's feedback
        // lane reads it through the CURRENT array position each tick.
        // Provenance-clean by construction: only the game-write callbacks
        // below fill it (never test rumble, macro rumble, or any
        // per-physical-device projection), which is what lets the
        // rumble-to-audio path read it without feedback loops.
        private long _inboundRumblePack;

        /// <summary>The packed inbound game-feedback state (see
        /// <see cref="PadForge.Engine.Common.LfeOutputState"/>). Written
        /// by the HM output callbacks, read by the poll thread's feedback
        /// lane. A fresh VC reads 0, so create / recreate starts silent.</summary>
        public long InboundRumblePack => System.Threading.Volatile.Read(ref _inboundRumblePack);

        // DualSense / DualSense Edge VID/PID — used to gate the
        // DS5 effect message pass-through dispatcher.  Both USB and BT
        // variants of each profile share the same VID/PID; the profile
        // ID slug differs but doesn't matter for the gating decision.
        private const ushort SonyVid = 0x054C;
        private const ushort DualSensePid = 0x0CE6;
        private const ushort DualSenseEdgePid = 0x0DF2;
        // Nintendo family: the virtual Switch Pro (HM v1.3.18, HM#33)
        // decodes its rumble outputs onto OutputDecoded like Sony.
        private const ushort NintendoVid = 0x057E;

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
        // The trigger rows' wire-field keys when they differ from the
        // canonical positions above (X360: canonical Z/Rz, fields Vx/Vy).
        // HM's lanes disagree across SDK generations about which position
        // they read triggers from — v1.3.9-1.3.16 HID lane: field key;
        // v1.3.17 HID lane: canonical-first with field fallback; v1.3.17
        // GIP/XUSB lane (XInput / WGI consumers): field key only. Mirror
        // every trigger write to both positions so all lanes of whichever
        // SDK is bundled read live values instead of the 0.5 extras seed
        // (which pinned XInput/WGI triggers at 50%, discussion #130).
        private HMAxis _axLeftTriggerField, _axRightTriggerField;

        // Per-call axes scratch dict, allocated once and reused across
        // every SubmitGamepadState / SubmitRawHidState frame to
        // keep the 1 kHz hot path allocation-free. HMGamepadState.Axes
        // is a Dictionary<HMAxis, float>; the encoder consumes the dict
        // by key lookup and is fine with reused references.
        private readonly Dictionary<HMAxis, float> _axesScratch = new();

        // Idle dedup state for the plain SubmitGamepadState path (in
        // practice Xbox slots: every Extended slot is custom and uses
        // SubmitRawHidState, and Sony rides the extended overload).
        // See the contract note at the skip site.
        //
        // 16 ms, NOT longer: the GIP companion's stale watchdog counts
        // READS (companion.c ReadGipData, incremented by the 8 ms pump AND
        // by every IOCTL_XUSB_GET_STATE), and at >500 unchanged-SeqNo reads
        // it tears the mapping down and zeroes the XInput state. A 250 ms
        // keepalive let any consumer mix totalling ~2 000 reads/sec force
        // repeated one-frame releases of held inputs (Codex audit
        // 2026-07-16). 16 ms tolerates ~31 000 reads/sec, which matches the
        // watchdog margin the slowest configurable baseline poll interval
        // (16 ms) already had, and still cuts idle submits ~94% at the
        // default 1 kHz poll.
        private Gamepad _lastSubmittedGp;
        private long _lastSubmitTick;
        private bool _hasSubmitted;
        private const int SubmitKeepaliveMs = 16;

        public HMaestroVirtualController(HMContext ctx, HMProfile profile, VirtualControllerType type)
        {
            _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _type = type;

            // Resolve the 6-slot canonical axis keys via the profile's
            // AxisMap, which maps wire HMAxis → semantic role string
            // ("leftStickX", "rightStickX", "leftTrigger", etc.). For Sony
            // profiles, AxisMap declares HMAxis.Z → "rightStickX",
            // HMAxis.Rx → "leftTrigger", HMAxis.Ry → "rightTrigger",
            // HMAxis.Rz → "rightStickY" — the inverse of the XInput
            // convention StandardAxes ships (rightStick=Rx/Ry, triggers=
            // Z/Rz). Walking AxisMap directly lands each role on the wire
            // byte the consumer expects.
            //
            // Trusting HMGamepadStateHelpers.StandardAxes for this routing
            // was the bug behind the phantom 50% L2/R2 on every PlayStation
            // virtual output: it routed rightStickX (which idles at center
            // = 0.5) onto HMAxis.Rx — the wire position Sony declares as
            // the L2 trigger — so the OS read byte 4 = 0x80 = 50% trigger
            // pull, which auto-asserts the coupled L2 digital button
            // (DInput button 7), with the same flip on Ry → R2.
            // AxisMap is Dictionary<string, string>: key = hex HID usage
            // code ("0x32" for HMAxis.Z), value = role name ("rightStickX").
            // HMAxis is a ushort enum whose values are full HID usage
            // codes (page << 8 | usage) — HMAxis.Z = 0x0132, HMAxis.Rx =
            // 0x0133, etc. AxisMap stores only the low usage byte, so
            // a 2-digit key must be promoted to page-1 (Generic Desktop)
            // before casting. 4-digit keys carry the page byte already.
            var axisMap = _profile.AxisMap;
            HMAxis ResolveAxisByRole(string role, HMAxis defaultAxis)
            {
                if (axisMap == null) return defaultAxis;
                foreach (var kvp in axisMap)
                {
                    if (!string.Equals(kvp.Value, role, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string keyHex = kvp.Key ?? "";
                    if (keyHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        keyHex = keyHex.Substring(2);
                    if (!int.TryParse(keyHex,
                            System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out int axisCode))
                        continue;
                    if (axisCode <= 0xFF) axisCode |= 0x0100;
                    return (HMAxis)axisCode;
                }
                return defaultAxis;
            }
            _axLeftStickX  = ResolveAxisByRole("leftStickX",  HMAxis.X);
            _axLeftStickY  = ResolveAxisByRole("leftStickY",  HMAxis.Y);
            _axRightStickX = ResolveAxisByRole("rightStickX", HMAxis.Rx);
            _axRightStickY = ResolveAxisByRole("rightStickY", HMAxis.Ry);
            _axLeftTrigger  = ResolveAxisByRole("leftTrigger",  HMAxis.Z);
            _axRightTrigger = ResolveAxisByRole("rightTrigger", HMAxis.Rz);

            // Mirror targets: the trigger rows' own wire-field keys, None
            // when they coincide with the canonical position (Sony, where
            // the axisMap already lands the role on the wire field).
            // Ctor-cached layout lists: HMProfile.Sticks/Triggers are
            // computed properties that allocate a fresh List plus record
            // instances per ACCESS (the SDK caches them internally for the
            // same reason, HMController.cs "audit 1n"), and the raw submit
            // path read both per 1 kHz tick (~115 KB/s in the allocation
            // trace). Layout is immutable per profile.
            _cachedProfileSticks = _profile.Sticks;
            _cachedProfileTriggers = _profile.Triggers;

            var profTriggers = _cachedProfileTriggers;
            _axLeftTriggerField  = (profTriggers != null && profTriggers.Count > 0)
                ? profTriggers[0].Axis : HMAxis.None;
            _axRightTriggerField = (profTriggers != null && profTriggers.Count > 1)
                ? profTriggers[1].Axis : HMAxis.None;
            if (_axLeftTriggerField  == _axLeftTrigger)  _axLeftTriggerField  = HMAxis.None;
            if (_axRightTriggerField == _axRightTrigger) _axRightTriggerField = HMAxis.None;

            // Seed the hot-path scratch dict so HM's encoder receives
            // sensible rest values for every declared axis. Sticks center
            // at 0.5, triggers release at 0. Any HMAxis from
            // AvailableAxes that we don't recognize as a stick or trigger
            // defaults to 0.5 (safe stick-like rest) so unhandled extras
            // don't manifest as phantom presses on their wire bytes.
            var availableAxes = _profile.AvailableAxes;
            if (availableAxes != null)
            {
                foreach (var hmAxis in availableAxes)
                {
                    float rest = (hmAxis == _axLeftTrigger || hmAxis == _axRightTrigger
                               || hmAxis == _axLeftTriggerField || hmAxis == _axRightTriggerField) ? 0f : 0.5f;
                    _axesScratch[hmAxis] = rest;
                }
            }
        }

        public void Connect()
        {
            if (IsConnected) return;
            _controller = _ctx.CreateController(_profile);

            // Fresh HMController = fresh shared section. Reset the idle-dedup
            // memory so the first frame always submits instead of waiting out
            // a keepalive window against the previous controller's state.
            _hasSubmitted = false;

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
            string descriptorHex = _profile.DescriptorHex;
            if (DescriptorHasPidFfbBlock(descriptorHex))
            {
                _ffbDecoder = new HMaestroFfbDecoder(_controller, descriptorHex);
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
            // was ever attached. Under _dispatcherLock so a concurrent
            // AttachDeviceConfig (UI thread, from the DevicesUpdated
            // handler) cannot slip between this dispose and the null, nor
            // construct a replacement after teardown: this runs on the poll
            // thread's destroy pass, and the two never synchronized.
            lock (_dispatcherLock)
            {
                try
                {
                    _userEffectsDispatcher?.Dispose();
                }
                catch { /* best-effort teardown */ }
                finally
                {
                    _userEffectsDispatcher = null;
                }
            }

            _controller?.Dispose();
            _controller = null;
            IsConnected = false;
        }

        /// <summary>Attaches a per-slot
        /// <see cref="DeviceSlotConfig"/> so user-configured trigger
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
        public void AttachDeviceConfig(PadForge.ViewModels.DeviceSlotConfig config)
        {
            if (config == null) return;

            // Locked, and gated on IsConnected, against Disconnect's teardown
            // on the poll thread. Two failures without it: the null-test and
            // the use below were separate reads of the field, so a teardown
            // between them threw; and an attach arriving after teardown saw
            // null and CONSTRUCTED a fresh dispatcher, which registers itself
            // in the static _instances map and subscribes to the config, so it
            // outlived the disposed VC as a zombie HID writer that no later
            // cleanup pass revisits.
            lock (_dispatcherLock)
            {
                if (!IsConnected) return;

                var d = _userEffectsDispatcher;
                if (d == null)
                {
                    d = new UserEffectsDispatcher(FeedbackPadIndex, config);
                    _userEffectsDispatcher = d;
                    d.ApplyOnce();
                }
                else
                {
                    d.Rebind(config);
                }
            }
        }

        /// <summary>Re-points this VC's effect dispatchers at a different pad.
        ///
        /// <para>A slot reorder REUSES a kernel VC at a new pad index (pad
        /// index is data identity, visual position is the kernel-slot anchor),
        /// and moving the pointer plus <see cref="FeedbackPadIndex"/> is not
        /// enough: both dispatchers capture their pad index in a readonly
        /// field at construction and resolve their physical target devices
        /// from it (<c>us.MapTo != _padIndex</c>). Left alone they keep
        /// writing lightbar / triggers / rumble to the OLD pad's controllers.
        /// Worse, the DevicesUpdated handler re-binds their CONFIG by the new
        /// index, so they end up running the new pad's settings against the
        /// old pad's hardware.</para>
        ///
        /// <para>The fields are readonly by design (they are read from a timer
        /// thread), so the honest fix is to rebuild rather than mutate. The
        /// registry hand-off is safe: a disposing dispatcher only removes its
        /// slot key when it is still the registered instance, so on a two-pad
        /// swap the second rebuild cannot evict the first's fresh
        /// entry.</para></summary>
        internal void RetargetToPad(int newPadIndex, PadForge.ViewModels.DeviceSlotConfig config)
        {
            FeedbackPadIndex = newPadIndex;

            // DS5 pass-through: rebuild against the new pad. Recreated here
            // rather than deferred, because RegisterFeedbackCallback (its only
            // other creator) runs solely from VC construction, so a reset alone
            // would leave pass-through dead until the slot was torn down.
            if (IsDualSenseVirtual)
            {
                try { _ds5Dispatcher?.Dispose(); }
                catch { /* best-effort teardown */ }
                _ds5Dispatcher = null;
                if (_controller != null && IsConnected)
                {
                    _ds5Dispatcher = new DualSensePassthroughDispatcher(newPadIndex);
                    _ds5Dispatcher.Start();
                }
            }

            // User effects: drop, then let AttachDeviceConfig rebuild against
            // the new FeedbackPadIndex set above.
            lock (_dispatcherLock)
            {
                try { _userEffectsDispatcher?.Dispose(); }
                catch { /* best-effort teardown */ }
                _userEffectsDispatcher = null;
            }
            if (config != null) AttachDeviceConfig(config);
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
        /// <summary>Accepted-submit counter for the freeze probe. Incremented
        /// AFTER the null-controller gate in every Submit* path, so a stall
        /// here (counter static while combined state changes) means Step 5 or
        /// this wrapper stopped, while a counter that keeps climbing against
        /// a frozen driver report pins the freeze at the driver boundary.
        /// long + Volatile read on a probe thread; written only by the poll
        /// thread.</summary>
        public long SubmitCounter;

        /// <summary>True while this wrapper still holds its HM controller.
        /// Every Submit* silently no-ops when it is null, which is exactly
        /// the shape a frozen-output probe must be able to see.</summary>
        public bool ControllerLive => _controller != null;

        public void SubmitRawReport(ReadOnlySpan<byte> report)
        {
            if (_controller == null) return;
            System.Threading.Interlocked.Increment(ref SubmitCounter);
            // Every per-tick Submit path ticks FFB (see TickFfb doc). This
            // is the ONLY submit on USB Sony slots now that Step 5 skips
            // the redundant extended leg when a packer exists.
            TickFfb();
            _controller.SubmitRawReport(report);
        }

        /// <summary>Re-evaluates PID effect state on the engine clock. The
        /// HM packet callback applies the decoder only when the game sends a
        /// report, but effect durations expire on the DEVICE clock — without
        /// this per-tick pass, the last computed vibration latches on the
        /// physical pad as soon as a game goes quiet. Called from every
        /// per-tick Submit path; no-op for non-PID profiles.</summary>
        public void TickFfb()
        {
            var vibs = _fbVibrationStates;
            int idx = FeedbackPadIndex;
            if (_ffbDecoder == null || vibs == null || idx < 0 || idx >= vibs.Length) return;
            // A null element makes Apply a no-op; returning here keeps the
            // pack write below from re-publishing a stale computed pair.
            if (vibs[idx] == null) return;
            _ffbDecoder.ApplyIfDue(vibs[idx]);

            // Inbound pack (#236, owner directive: EVERY feedback source we
            // support is an LFE source): the PID / vendor FFB lane feeds
            // the bass shakers exactly like the Xbox and Sony motor lanes.
            // The pair comes from the decoder's own game-authored compute
            // (effect set x gains, durations expiring per tick), never
            // from the shared Vibration array, so test rumble and macro
            // rumble stay out by the same provenance rule. A sim-racing
            // wheel slot is the audience that filed #234; constant-force
            // steering load and periodic road texture both land here.
            var (ffbLeft, ffbRight) = _ffbDecoder.LastComputedMotors;
            System.Threading.Volatile.Write(ref _inboundRumblePack,
                Engine.Common.LfeOutputState.Pack(ffbLeft, ffbRight, 0, 0));
        }

        public void SubmitGamepadState(Gamepad gp)
        {
            if (_controller == null) return;
            System.Threading.Interlocked.Increment(ref SubmitCounter);
            TickFfb();

            // Idle dedup with a 16 ms keepalive. An unchanged state means an
            // identical frame: the driver reads a seqlocked LATCH (shared
            // section, HMController class doc: "no internal pumping thread;
            // the consumer drives the cadence"), so skipping an identical
            // write changes nothing for state-latching consumers. Three
            // driver watchdogs bound how long SeqNo may sit still:
            //   * driver.c SharedInputWorkerProc: recycles all handles on any
            //     500 ms without an event signal (WAIT_TIMEOUT -> recycle).
            //   * driver.c: staleWakeups > 250 signals-without-SeqNo-advance
            //     recycles too (keepalives advance SeqNo, resetting it).
            //   * companion.c ReadGipData: > 500 consecutive unchanged-SeqNo
            //     READS (8 ms pump + every XInput GET_STATE) tears the GIP
            //     mapping down and DecodeGipToXInput ZEROES the XInput
            //     state. The count is read-rate-bound, not time-bound, which
            //     is why the keepalive is 16 ms (see the field note).
            // Changes still submit the same tick they happen, so latency is
            // untouched; only redundant identical frames drop. RawInput
            // consumers see idle reports at ~62 Hz instead of the poll rate,
            // which is within the app's configurable baseline range.
            long nowTick = Environment.TickCount64;
            if (_hasSubmitted
                && nowTick - _lastSubmitTick < SubmitKeepaliveMs
                && gp.Buttons == _lastSubmittedGp.Buttons
                && gp.LeftTrigger == _lastSubmittedGp.LeftTrigger
                && gp.RightTrigger == _lastSubmittedGp.RightTrigger
                && gp.ThumbLX == _lastSubmittedGp.ThumbLX
                && gp.ThumbLY == _lastSubmittedGp.ThumbLY
                && gp.ThumbRX == _lastSubmittedGp.ThumbRX
                && gp.ThumbRY == _lastSubmittedGp.ThumbRY
                && gp.Share == _lastSubmittedGp.Share)
            {
                return;
            }
            _lastSubmittedGp = gp;
            _lastSubmitTick = nowTick;
            _hasSubmitted = true;

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
            // Hot-path: overwrite the 6 standard slots in the pre-seeded
            // _axesScratch (constructor seeded it with the profile's full
            // axis set at rest values). DO NOT Clear() the dict — any
            // extra-axis entries seeded by the constructor must persist
            // or HM's encoder would default those bytes to logical mid.
            if (_axLeftStickX  != HMAxis.None) _axesScratch[_axLeftStickX]  = (gp.ThumbLX  + 32768f) / 65535f;
            if (_axLeftStickY  != HMAxis.None) _axesScratch[_axLeftStickY]  = (32768f - gp.ThumbLY)  / 65535f;
            if (_axRightStickX != HMAxis.None) _axesScratch[_axRightStickX] = (gp.ThumbRX  + 32768f) / 65535f;
            if (_axRightStickY != HMAxis.None) _axesScratch[_axRightStickY] = (32768f - gp.ThumbRY)  / 65535f;
            if (_axLeftTrigger  != HMAxis.None) _axesScratch[_axLeftTrigger]  = gp.LeftTrigger  / 65535f;
            if (_axRightTrigger != HMAxis.None) _axesScratch[_axRightTrigger] = gp.RightTrigger / 65535f;
            if (_axLeftTriggerField  != HMAxis.None) _axesScratch[_axLeftTriggerField]  = gp.LeftTrigger  / 65535f;
            if (_axRightTriggerField != HMAxis.None) _axesScratch[_axRightTriggerField] = gp.RightTrigger / 65535f;

            var state = new HMGamepadState
            {
                Axes = _axesScratch,
                Buttons = MapButtons(gp),
                Hat = MapHat(gp.Buttons),
            };

            _controller.SubmitState(state);
        }

        // Sony int16 sensor scaling — must stay identical to
        // SonyReportPackers.ScaleGyro / ScaleAccel: BT virtuals produce
        // the same byte values the USB SubmitRawReport path produces,
        // just at the Report 0x31 vendor-blob positions. The scales are
        // the inverse of SDL3's no-hardware-calibration HIDAPI decode
        // (deg/s = raw * 64 / 1024; g = raw / 8192).
        private const float GyroScale  = 1024f / 64f; // = 16
        private const float AccelScale = 8192f;

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
            System.Threading.Interlocked.Increment(ref SubmitCounter);
            TickFfb();

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
            // XInput → HM v1.3.9 [0..1] conversion rules. DO NOT Clear()
            // — _axesScratch was pre-seeded at construction with the
            // profile's full axis set at rest values so extra-axis entries
            // survive frame to frame.
            if (_axLeftStickX  != HMAxis.None) _axesScratch[_axLeftStickX]  = (gp.ThumbLX  + 32768f) / 65535f;
            if (_axLeftStickY  != HMAxis.None) _axesScratch[_axLeftStickY]  = (32768f - gp.ThumbLY)  / 65535f;
            if (_axRightStickX != HMAxis.None) _axesScratch[_axRightStickX] = (gp.ThumbRX  + 32768f) / 65535f;
            if (_axRightStickY != HMAxis.None) _axesScratch[_axRightStickY] = (32768f - gp.ThumbRY)  / 65535f;
            if (_axLeftTrigger  != HMAxis.None) _axesScratch[_axLeftTrigger]  = gp.LeftTrigger  / 65535f;
            if (_axRightTrigger != HMAxis.None) _axesScratch[_axRightTrigger] = gp.RightTrigger / 65535f;
            if (_axLeftTriggerField  != HMAxis.None) _axesScratch[_axLeftTriggerField]  = gp.LeftTrigger  / 65535f;
            if (_axRightTriggerField != HMAxis.None) _axesScratch[_axRightTriggerField] = gp.RightTrigger / 65535f;

            var state = new HMGamepadState
            {
                Axes = _axesScratch,
                Buttons = MapButtons(gp),
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
                // The DualSense 0x31 sensor-timestamp field is in 0.33 µs
                // ticks; convert from microseconds by × 3.
                SensorTimestamp = (uint)((motion.TimestampUs * 3L) & 0xFFFFFFFF),

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

            _controller.SubmitState(state);
        }

        /// <summary>
        /// Submit an RawHidState (produced by the Extended dynamic
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
        /// RawHidState.Axes is in HID convention per Step 3
        /// (positive = down/right), matching HMGamepadState's internal
        /// convention, so no Y negation needed — pass signed short
        /// straight through as a normalized float. Triggers in the raw
        /// state are signed short centered at 0; convert to the 0..1
        /// float range HMGamepadState expects.
        /// </summary>
        public void SubmitRawHidState(RawHidState raw, int sticks, int triggers)
            => SubmitRawHidState(raw, sticks, triggers, default);

        // Last-submitted raw frame for the idle dedup (content compare on
        // the pooled arrays; shapes are stable per layout).
        private short[] _lastRawAxes;
        private uint[] _lastRawButtons;
        private int[] _lastRawPovs;
        private bool _lastRawHadMotion;
        private long _lastRawSubmitTick;
        private bool _hasRawSubmitted;

        // PADFORGE_NO_RAWDEDUP=1 disables the raw idle dedup at launch
        // (regression bisect switch).
        private static readonly bool s_noRawDedup =
            System.Environment.GetEnvironmentVariable("PADFORGE_NO_RAWDEDUP") == "1";

        private bool RawFrameUnchanged(in RawHidState raw)
        {
            static bool EqS(short[] a, short[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
            if (_lastRawButtons == null || raw.Buttons == null
                || _lastRawButtons.Length != raw.Buttons.Length) return false;
            for (int i = 0; i < raw.Buttons.Length; i++)
                if (raw.Buttons[i] != _lastRawButtons[i]) return false;
            if (_lastRawPovs == null || raw.Povs == null
                || _lastRawPovs.Length != raw.Povs.Length) return false;
            for (int i = 0; i < raw.Povs.Length; i++)
                if (raw.Povs[i] != _lastRawPovs[i]) return false;
            return EqS(raw.Axes, _lastRawAxes);
        }

        private void StoreRawFrame(in RawHidState raw, bool hadMotion, long tick)
        {
            static void CopyS(short[] src, ref short[] dst)
            {
                if (src == null) { dst = null; return; }
                if (dst == null || dst.Length != src.Length) dst = new short[src.Length];
                System.Array.Copy(src, dst, src.Length);
            }
            CopyS(raw.Axes, ref _lastRawAxes);
            if (raw.Buttons != null)
            {
                if (_lastRawButtons == null || _lastRawButtons.Length != raw.Buttons.Length)
                    _lastRawButtons = new uint[raw.Buttons.Length];
                System.Array.Copy(raw.Buttons, _lastRawButtons, raw.Buttons.Length);
            }
            else _lastRawButtons = null;
            if (raw.Povs != null)
            {
                if (_lastRawPovs == null || _lastRawPovs.Length != raw.Povs.Length)
                    _lastRawPovs = new int[raw.Povs.Length];
                System.Array.Copy(raw.Povs, _lastRawPovs, raw.Povs.Length);
            }
            else _lastRawPovs = null;
            _lastRawHadMotion = hadMotion;
            _lastRawSubmitTick = tick;
            _hasRawSubmitted = true;
        }

        public void SubmitRawHidState(RawHidState raw, int sticks, int triggers,
            in PadForge.Services.MotionSnapshot motion)
        {
            if (_controller == null) return;
            System.Threading.Interlocked.Increment(ref SubmitCounter);
            TickFfb();

            // Idle dedup, the EXACT basic-path shape (16 ms keepalive):
            // identical frame within the window skips the seqlock publish +
            // SetEvent. driver.c bounds how long SeqNo may sit still (500 ms
            // wait-timeout recycle; 250 stale WAKES recycle), and the 16 ms
            // keepalive republishes well inside both. Motion frames never
            // dedup: SensorTimestamp must advance for downstream fusion.
            long nowRawTick = Environment.TickCount64;
            if (!s_noRawDedup
                && _hasRawSubmitted
                && !motion.HasMotion && !_lastRawHadMotion
                && nowRawTick - _lastRawSubmitTick < SubmitKeepaliveMs
                && RawFrameUnchanged(in raw))
            {
                return;
            }
            if (motion.HasMotion)
            {
                // While motion streams, dedup can never fire (the next
                // frame's gate sees _lastRawHadMotion). Skip the three
                // array copies; only the flags matter. The first frame
                // AFTER motion clears re-stores a full frame below.
                _lastRawHadMotion = true;
                _lastRawSubmitTick = nowRawTick;
                _hasRawSubmitted = true;
            }
            else
            {
                StoreRawFrame(in raw, hadMotion: false, nowRawTick);
            }

            short Ax(int i) => (raw.Axes != null && i >= 0 && i < raw.Axes.Length) ? raw.Axes[i] : (short)0;

            // Convert raw signed short (-32768..+32767) to HM v1.3.9's
            // unified [0..1] axis range. Stick rest is at signed 0
            // -> 0.5, fully positive -> 1.0, fully negative -> 0.0;
            // trigger rest is at signed -32768 -> 0.0, fully pressed
            // (signed +32767) -> 1.0. Both shapes use the same shift
            // because raw.Axes is already centered on signed zero for
            // sticks (per ExtendedSlotConfig's signed-short convention)
            // and on short.MinValue for triggers (per
            // MapToRawTriggerAxis's released-rest contract).
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
            var profileSticks = _cachedProfileSticks;
            var profileTriggers = _cachedProfileTriggers;
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

            // IMU channel (HM v1.3.18, HM#33): MotionSnapshot is already
            // g / deg/s in the SDL sensor frame, and the SDK's field docs
            // direct SDL-reading consumers to submit those values
            // VERBATIM (the per-profile packer owns the wire frame and
            // scale, so the vector round-trips bit-consistent to SDL on
            // the client). Zeroes when the slot maps no motion source.
            if (motion.HasMotion)
            {
                state.AccelGX = motion.AccelX;
                state.AccelGY = motion.AccelY;
                state.AccelGZ = motion.AccelZ;
                state.GyroDpsX = motion.GyroPitch;
                state.GyroDpsY = motion.GyroYaw;
                state.GyroDpsZ = motion.GyroRoll;
            }

            _controller.SubmitState(state);
        }

        /// <summary>Detaches this VC from the engine's feedback array
        /// (audit 2026-07-25, C38). Called synchronously by
        /// DestroyVirtualController BEFORE the motor zero: the driver-side
        /// OutputDecoded / OutputReceived handlers die only when the async
        /// dispose reaches _controller.Dispose() (seconds later for
        /// xinputhid), and every one of them guards on FeedbackPadIndex,
        /// so parking it at -1 makes late callbacks no-op instead of
        /// repopulating a slot this VC no longer owns.</summary>
        public void UnregisterFeedback()
        {
            FeedbackPadIndex = -1;
            _fbVibrationStates = null;
        }

        public void RegisterFeedbackCallback(int padIndex, Vibration[] vibrationStates)
        {
            FeedbackPadIndex = padIndex;
            // Keep the registration for the per-tick FFB re-evaluation —
            // the packet callback below only runs when the game SENDS
            // something, but PID effect durations expire on the device
            // clock. Without a periodic Apply, the last computed vibration
            // latches on the physical pad the moment a game goes quiet
            // (discussion #125: Jedi Outcast's stuck rumble — the field log
            // showed Apply timestamps exactly matching packet arrivals and
            // nothing after the final packet).
            _fbVibrationStates = vibrationStates;
            if (_controller == null) return;
            System.Threading.Interlocked.Increment(ref SubmitCounter);

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
            // layer: UserEffectsDispatcher's gameDrivenRumble branch zeroes
            // the provider bytes for a passthrough target exactly while the
            // game is writing (audit 2026-07-25, C37 replaced the old
            // unconditional provider skip, which also killed test/macro
            // rumble for the device), so the passthrough dispatcher carries
            // the game's rumble and the Sony dispatcher carries the rest.
            _controller.OutputDecoded += (ctrl, e) =>
            {
                int idx = FeedbackPadIndex;
                if (idx < 0 || idx >= vibrationStates.Length) return;

                int declaredSize = _profile.ExtendedOutputReport?.Size ?? -1;

                if (e.Fields.TryGetValue("leftMotor", out var lmObj2) && lmObj2 is byte left
                 && e.Fields.TryGetValue("rightMotor", out var rmObj) && rmObj is byte right)
                {
                    // The Sony motor bytes are only TRUSTED behind the full
                    // validity gate. The codec inserts leftMotor/rightMotor
                    // unconditionally (report ID alone selects the decode),
                    // but per the protocol contract (linux-hid
                    // hid-playstation.c, dualsense_output_worker /
                    // ds4_output_worker: motor bytes are assigned only
                    // inside the block that asserts VALID_FLAG0 bit 0
                    // (+bit 1 HAPTICS_SELECT on DS5), and an
                    // audio/lightbar-only report carries motor=0 meaning
                    // "ignore", NOT "stop"):
                    //   1. exact declared report size (Decode silently
                    //      skips out-of-range bytes, so a truncated BT
                    //      report can surface partial fields);
                    //   2. CRC valid (CrcValid alone is insufficient: it
                    //      initializes true and is skipped when the footer
                    //      is absent, hence the length check too);
                    //   3. the motor-valid flag asserted. DS4 bit 0
                    //      (0x01), DS5 bits 0/1 (0x03).
                    // Fail any leg → PRESERVE the previous state, for BOTH
                    // consumers: the #236 LFE pack AND the legacy
                    // VibrationStates write (2026-07-25 audit: the write
                    // shipped ungated, so a lightbar-only report zeroed
                    // rumble on every non-Sony device on the slot). Flag
                    // asserted with both bytes zero IS a real stop.
                    // Sony pads have no trigger motors; those voices stay 0.
                    byte motorMask = IsDualSenseVirtual ? (byte)0x03 : (byte)0x01;
                    e.Fields.TryGetValue("validFlag0", out var vfObj);
                    bool sonyMotorsValid = SonyMotorsValid(
                        e.RawBytes.Length, declaredSize, e.CrcValid, vfObj, motorMask);

                    // Non-Sony producers (Switch Pro's synthesized decode,
                    // any future flag-less profile) keep the original
                    // unconditional trust: the flag semantics are Sony's.
                    if (MotorWriteAllowed(_profile.VendorId, sonyMotorsValid))
                    {
                        vibrationStates[idx].LeftMotorSpeed  = (ushort)(left  * 257);
                        vibrationStates[idx].RightMotorSpeed = (ushort)(right * 257);
                    }

                    if (sonyMotorsValid)
                    {
                        System.Threading.Volatile.Write(ref _inboundRumblePack,
                            Engine.Common.LfeOutputState.Pack(
                                (ushort)(left * 257), (ushort)(right * 257), 0, 0));
                    }
                    else if (_profile.VendorId == NintendoVid)
                    {
                        // Switch Pro (HM v1.3.18): the driver decodes the
                        // 0x01/0x10 rumble outputs itself and only emits
                        // the motor fields for genuine rumble frames, and
                        // this wire has no validFlag/CRC to gate on. Same
                        // provenance as the motors above: game-authored
                        // only, so the #236 pack rides directly.
                        System.Threading.Volatile.Write(ref _inboundRumblePack,
                            Engine.Common.LfeOutputState.Pack(
                                (ushort)(left * 257), (ushort)(right * 257), 0, 0));
                    }
                }

                // Integrity gate on the passthrough forward (2026-07-25
                // audit): a full-length BT report with a corrupt CRC
                // decodes every field with CrcValid=false, and forwarding
                // it re-frames corrupt bytes into a fresh PHYSICAL write
                // plus poisons the grace-window subsystem mirror. The
                // length leg covers the CrcValid-true-on-absent-footer
                // trap exactly as the motor gate above documents. USB
                // profiles declare no CRC, so CrcValid is trivially true
                // there and only the length leg bites.
                if (_ds5Dispatcher != null
                    && _profile.VendorId == SonyVid
                    && e.RawBytes.Length == declaredSize
                    && e.CrcValid
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
                    // For a remote DualSense this merged output is forwarded at the
                    // PlayStationEffectWriter chokepoint (issue #138), not here.
                    UserEffectsDispatcher.NotifyExternalSubsystems(idx, effectPayload);
                }
            };

            _controller.OutputReceived += (ctrl, pkt) =>
            {
                // Sony vendor test commands (SetFeature 0x80: deviceId,
                // actionId, params — the report dualsense-tester /
                // ds.daidr.me drives the firmware 1 kHz sine generator,
                // speaker/headphone routing, and calibration actions
                // through). Forward to the assigned physical DualSense so
                // the test works through the virtual pad. Report 0x80 only:
                // PID FFB feature writes (0x11 Create New Effect) ride the
                // same HidFeature source and belong to the decoder below.
                // GetFeature (0x81 response) round-trips are NOT forwarded —
                // the driver serves feature reads synchronously and has no
                // deferred-response path; fire-and-forget commands like the
                // sine test don't need one.
                if (_ds5Dispatcher != null
                    && pkt.Source == HMOutputSource.HidFeature
                    && pkt.ReportId == 0x80)
                {
                    _ds5Dispatcher.EnqueueFeature(pkt.ReportId, pkt.Data.Span);
                    return;
                }

                int idx = FeedbackPadIndex;
                if (idx < 0 || idx >= vibrationStates.Length) return;

                var data = pkt.Data.Span;
                bool isXbox = HMaestroProfileCatalog.IsXboxProfile(_profile);

                // XInput vibration packet (IOCTL_XUSB_SET_STATE):
                // [00, 08, leftHi, rightHi, reserved]. Chromium browser
                // Gamepad API sends dual-rumble through this path with
                // alternating hi=127 / hi=0 — that's a square-wave
                // duty cycle, not keepalive noise; don't filter zeros.
                //
                // Extended XINPUT_VIBRATION_EX (Xbox One+ impulse triggers)
                // arrives via the same IOCTL with a longer payload — two
                // extra bytes carry the per-trigger motor magnitudes. HM
                // forwards the IOCTL data verbatim (confirmed against
                // HIDMaestro driver/companion.c:651-730 — size-agnostic
                // WdfRequestRetrieveInputBuffer + PublishOutput pass-through),
                // so the extended bytes land at offsets 4 and 5 right after
                // the standard motor bytes when present. When the standard
                // 5-byte packet arrives without the extended bytes, the
                // game is signalling "no trigger rumble," so zero the
                // trigger motors to clear stale values.
                if (pkt.Source == HMOutputSource.XInput && data.Length >= 5)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 257);

                    if (data.Length >= 7)
                    {
                        vibrationStates[idx].LeftTriggerMotorSpeed = (ushort)(data[4] * 257);
                        vibrationStates[idx].RightTriggerMotorSpeed = (ushort)(data[5] * 257);
                    }
                    else
                    {
                        vibrationStates[idx].LeftTriggerMotorSpeed = 0;
                        vibrationStates[idx].RightTriggerMotorSpeed = 0;
                    }
                    // Inbound pack (#236): same decode, controller-local.
                    // Zeros pass through unfiltered (the square-wave duty
                    // cycle note above applies to the audio path too).
                    System.Threading.Volatile.Write(ref _inboundRumblePack,
                        Engine.Common.LfeOutputState.Pack(
                            (ushort)(data[2] * 257), (ushort)(data[3] * 257),
                            data.Length >= 7 ? (ushort)(data[4] * 257) : (ushort)0,
                            data.Length >= 7 ? (ushort)(data[5] * 257) : (ushort)0));
                    return;
                }

                // Xbox Series BT browser-Gamepad / Game Controller Tester
                // / Game Pass app short HID rumble:
                // [trigL, trigR, motorL, motorR, dur, delay, loop].
                // Same shape as SDL3 HIDAPI's HIDAPI_DriverXboxOne_UpdateRumble
                // outbound payload with the 2-byte header (0x03 0x0F)
                // stripped. Motors are 0..100; scale to ushort (~655x).
                //
                // Probe data 2026-05-19 (1518 captures during Game
                // Controller Tester impulse-trigger run, all len=7,
                // src=HidOutput): nonzero trigger values consistently
                // land at bytes 0/1, never at bytes 2/3, and the
                // tail bytes 4/5/6 are constant 0xFF 0x00 0xEB markers.
                // The previous parser shape read motorL/motorR but
                // dropped the trigger bytes entirely — fixed by also
                // surfacing data[0]/data[1] as impulse trigger motors.
                if (isXbox
                    && pkt.Source == HMOutputSource.HidOutput
                    && data.Length >= 4
                    && data.Length < 8)
                {
                    vibrationStates[idx].LeftTriggerMotorSpeed = (ushort)(data[0] * 655);
                    vibrationStates[idx].RightTriggerMotorSpeed = (ushort)(data[1] * 655);
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[2] * 655);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[3] * 655);
                    // Inbound pack (#236): same decode, controller-local.
                    System.Threading.Volatile.Write(ref _inboundRumblePack,
                        Engine.Common.LfeOutputState.Pack(
                            (ushort)(data[2] * 655), (ushort)(data[3] * 655),
                            (ushort)(data[0] * 655), (ushort)(data[1] * 655)));
                    return;
                }

                // Xbox wired / wireless-receiver legacy HID rumble:
                // motor magnitudes at vendor-specific bytes 5/6.
                if (isXbox
                    && pkt.Source == HMOutputSource.HidOutput
                    && data.Length >= 8)
                {
                    vibrationStates[idx].LeftMotorSpeed = (ushort)(data[5] * 257);
                    vibrationStates[idx].RightMotorSpeed = (ushort)(data[6] * 257);
                    // Inbound pack (#236): body motors only, exactly like
                    // the physical decode above. This packet shape carries
                    // no trigger bytes, so the trigger voices PRESERVE
                    // their previous values rather than inventing a stop.
                    long prevPack = System.Threading.Volatile.Read(ref _inboundRumblePack);
                    System.Threading.Volatile.Write(ref _inboundRumblePack,
                        Engine.Common.LfeOutputState.Pack(
                            (ushort)(data[5] * 257), (ushort)(data[6] * 257),
                            Engine.Common.LfeOutputState.TriggerLeft(prevPack),
                            Engine.Common.LfeOutputState.TriggerRight(prevPack)));
                    return;
                }

                // PID FFB-capable profile (Custom synthetic, catalog
                // profile with the FFB block in its descriptor, OR a
                // Customized+FFB Extended slot rebuilt with
                // AddPidFfbBlock). Decode Set Effect / Set Constant /
                // Set Periodic / Set Condition / Effect Operation /
                // Block Free / Device Control / Device Gain. Apply()
                // aggregates running effects into the Vibration with
                // directional + condition data so
                // SetDirectionalHapticForces routes DirectInput FFB to
                // physical wheels and sticks. Gate is descriptor
                // presence, not VID — catalog profiles keep their
                // original VID/PID.
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

        private static HMButton MapButtons(in Gamepad gp)
        {
            ushort xinputButtons = gp.Buttons;
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
            // HMButton.Share — Xbox Series Share button. HM silently drops
            // the bit on profiles whose descriptor doesn't declare button
            // 13 (Xbox 360 / Xbox One / DualShock 4 / DualSense / etc.),
            // so this set is safe to do unconditionally.
            if (gp.Share) b |= HMButton.Share;
            return b;
        }

        /// <summary>The Sony motor trust gate (#236 / 2026-07-25 audit), as
        /// one pure predicate so tests can pin its legs: exact declared
        /// report size (a truncated BT report surfaces partial fields AND
        /// CrcValid=true, since the codec skips CRC when the footer is out
        /// of range), CRC valid, and the motor-valid flag asserted (DS4
        /// mask 0x01, DS5 mask 0x03, per linux-hid hid-playstation.c). A
        /// failing gate means PRESERVE previous motors, never stop.</summary>
        internal static bool SonyMotorsValid(
            int rawByteCount, int declaredSize, bool crcValid, object validFlag0, byte motorMask)
            => rawByteCount == declaredSize
            && crcValid
            && validFlag0 is byte vf
            && (vf & motorMask) != 0;

        /// <summary>Whether a decoded motor pair may land in
        /// VibrationStates: Sony profiles require the full trust gate; any
        /// other vendor (Switch Pro's synthesized decode, future flag-less
        /// profiles) keeps unconditional trust, because the validity-flag
        /// semantics are Sony's.</summary>
        internal static bool MotorWriteAllowed(ushort vendorId, bool sonyMotorsValid)
            => vendorId != SonyVid || sonyMotorsValid;

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
        internal static bool DescriptorHasPidFfbBlock(string descriptorHex)
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
