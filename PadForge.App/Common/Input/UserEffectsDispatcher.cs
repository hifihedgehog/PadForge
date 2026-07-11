using System;
using System.ComponentModel;
using HIDMaestro;
using PadForge.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    /// <summary>
    /// Per-virtual-DS5-VC dispatcher for Feature B (user-configured
    /// adaptive trigger / lightbar / audio effects). Subscribes to the
    /// slot's <see cref="DeviceSlotConfig"/> PropertyChanged and
    /// re-synthesizes + sends the DS5 effect message to every assigned
    /// physical DualSense whenever the user touches a setting on the
    /// Adaptive Triggers or Lighting tab.
    ///
    /// <para>Game-driven Feature A passthrough (handled by
    /// <see cref="DualSensePassthroughDispatcher"/>) runs independently
    /// — game writes win per packet because they fire at high cadence.
    /// Feature B fills the silence between game writes: when the
    /// user-configured layer is enabled and no game has written
    /// recently, the trigger / lightbar settings the user picked here
    /// are what the physical pad reflects.</para>
    ///
    /// <para>The dispatcher writes synchronously on the UI thread when
    /// PropertyChanged fires. The cost is one parsed-field dictionary
    /// allocation, one synthesizer call, and one
    /// <see cref="HMOutputEncoder.Encode"/> + raw HID write per assigned
    /// physical DualSense / DualShock 4. Total is well under a
    /// millisecond per user-interaction event, which is bounded by how
    /// fast a human can drag a slider.</para>
    ///
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para><b>SOLE WRITER FOR DS5 / DS4 — DO NOT REINTRODUCE SDL RUMBLE.</b></para>
    /// <para>══════════════════════════════════════════════════════════════</para>
    /// <para>This dispatcher writes the ENTIRE effect packet (rumble +
    /// lightbar + adaptive triggers + mic LED) for every Sony pad mapped
    /// to its slot. <c>InputManager.Step2.ApplyForceFeedback</c> returns
    /// early for Sony VID 0x054C / DS5 / DS4 PIDs so SDL_RumbleJoystick
    /// is never called. This is intentional and load-bearing.</para>
    /// <para>Why: SDL3's PS5/PS4 driver writes its own effect packet
    /// through a separate HID handle. Two writers competing on an
    /// asynchronously-sampled audio peak (<see cref="AudioBassDetector"/>)
    /// produce a 30 Hz motor stutter — the v3.1.x audio-rumble +
    /// animated-lightbar regression. One writer cannot race with itself.</para>
    /// <para>The polling thread (<c>InputManager.Step2.UpdateInputStates</c>)
    /// broadcasts a per-slot poke via <see cref="OnPollingTick"/> every
    /// tick so the dispatcher's 33 ms timer stays alive whenever audio
    /// rumble is enabled or game rumble is in flight, even when the
    /// lightbar mode is static / off. Without that poke, an idle-lightbar
    /// slot would have NO writer at all once the SDL skip is in place.</para>
    /// <para>If you need to debug DS5/DS4 rumble: this dispatcher's
    /// <see cref="DispatchSnapshot"/> is the only code path that writes
    /// rumble bytes to those devices. Audio mix + per-device gain are
    /// computed in <c>InputService.SlotRumbleForDeviceProvider</c>.</para>
    /// <para>See memory: sony-rumble-sole-writer-architecture.md.</para>
    /// </summary>
    internal sealed class UserEffectsDispatcher : IDisposable
    {
        private const ushort SonyVid = 0x054C;
        private const ushort PidStandard = 0x0CE6;  // DualSense
        private const ushort PidEdge = 0x0DF2;      // DualSense Edge
        // DualShock 4 family — three PIDs cover the v1, v1 alternate, and
        // v2 hardware revisions. Same VID, different output report
        // shape (Report 0x05 USB / 0x11 BT, no AT / no player LEDs / no
        // mic LED). Lighting tab base color + audio modes apply via the
        // DS4 path; AT and Indicator LED settings are silently ignored.
        private const ushort Ds4Pid_V1     = 0x05C4;
        private const ushort Ds4Pid_V1Alt  = 0x09CC;
        private const ushort Ds4Pid_V2     = 0x0BA0;

        // Map (PID, transport) → HM profile id. Each device's
        // extendedOutputReport spec tells HMOutputEncoder how to pack the
        // semantic fields into wire-format bytes for that transport. There
        // is no "dualshock-4-v1-bt" profile — DS4 v1 over BT uses the v2-bt
        // descriptor, which has the same effect-report layout.
        private static HMProfile ResolveSonyProfile(ushort pid, bool isBluetooth)
        {
            string id = pid switch
            {
                PidStandard   => isBluetooth ? "dualsense-bt-full" : "dualsense",
                PidEdge       => isBluetooth ? "dualsense-edge-bt" : "dualsense-edge",
                Ds4Pid_V1     => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v1",
                Ds4Pid_V1Alt  => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v2",
                Ds4Pid_V2     => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v2",
                _ => null,
            };
            return id != null ? HMaestroProfileCatalog.GetProfileById(id) : null;
        }

        /// <summary>Static provider for the system audio peak (0..1).
        /// InputService wires this to <c>AudioBassDetector.FullSpectrumPeak</c>
        /// at startup. Returns 0 when the detector hasn't been initialized
        /// yet — audio-to-lightbar then dispatches a black frame, harmless.</summary>
        public static Func<float> AudioPeakProvider { get; set; }

        /// <summary>Static provider for the current button-state bitmap
        /// of a given pad index. InputService wires this to read from
        /// <c>InputManager.CombinedOutputStates[i].Buttons</c>. Used by
        /// <see cref="LightbarMode.InputReactive"/> to detect rising edges
        /// and enqueue a fading pulse.</summary>
        public static Func<int, uint> SlotButtonsProvider { get; set; }

        /// <summary>Static provider for the current rumble state of a
        /// given (slot, physical device) pair, returned as 8-bit
        /// right/left motor values (0..255). InputService wires this to
        /// scale the slot's raw <c>VibrationStates</c> by the specific
        /// device's PadSetting (audio rumble + ForceOverall + motor
        /// strengths + swap), so each Sony device mapped to the slot can
        /// have different gain or audio rumble settings. The synthesizer
        /// carries these values in every effect packet plus asserts bit
        /// 0 of validFlag1, so the 30 Hz lightbar dispatch doesn't crowd
        /// SDL3's separate SDL_RumbleJoystick writes off the BT HID
        /// channel.</summary>
        public static Func<int, Guid, (byte right, byte left)> SlotRumbleForDeviceProvider { get; set; }

        /// <summary>Static provider for the slot's raw (unscaled) rumble
        /// used for change detection only — when this changes mid audio
        /// tick, the dispatcher forces a fresh dispatch so the per-device
        /// motor bytes propagate immediately rather than waiting for the
        /// next audio peak update. Per-device scaling happens later via
        /// <see cref="SlotRumbleForDeviceProvider"/> in the device loop.</summary>
        public static Func<int, (byte right, byte left)> SlotRawRumbleProvider { get; set; }

        /// <summary>Static provider for the per-slot test-rumble target
        /// GUID. Returns <see cref="Guid.Empty"/> when no test rumble is
        /// active for the slot. When set, the dispatcher zeros the rumble
        /// bytes (and clears the rumble-emulation bit on DS5) for any
        /// physical device whose InstanceGuid doesn't match — otherwise an
        /// Xbox VC test rumble would still ride the dispatcher's effect
        /// packet and rumble every Sony device mapped to the slot. Step 2's
        /// SDL physical-rumble path already honors this filter via
        /// <c>InputManager.TestRumbleTargetGuid</c>.</summary>
        public static Func<int, Guid> TestRumbleTargetGuidProvider { get; set; }

        /// <summary>Static provider for a specific physical device's reported
        /// battery percent (0..100). Drives the Battery lightbar mode's
        /// low→full gradient lerp. Keyed by (slot, device InstanceGuid)
        /// because the Battery lightbar is a per-device output: two Sony pads
        /// on one slot each light to their own charge. Returns 100 when the
        /// device has no battery info (defaults to "full" so the lightbar
        /// shows the high-charge color rather than the empty color when
        /// battery telemetry is unavailable).</summary>
        public static Func<int, Guid, byte> SlotBatteryPercentProvider { get; set; }

        /// <summary>Static provider for the per-(slot, physical device)
        /// impulse-trigger motor values (8-bit right / left). Returns the
        /// game's XINPUT_VIBRATION_EX trigger magnitudes after per-device
        /// scaling (ImpulseOverallGain + Impulse{Left,Right}Strength +
        /// audio-trigger mix + ImpulseSwapTriggers). Returns (0, 0) when
        /// the slot's output VC is not Xbox-class — other VCs don't emit
        /// impulse trigger commands.
        ///
        /// Drives the impulse-to-AdaptiveTrigger-Vibration auto-routing on
        /// DualSense pads. The dispatcher injects an AT Vibration block
        /// into <see cref="ExternalSubsystemOverrides.RightTriggerEffect"/>
        /// / LeftTriggerEffect for each trigger with a non-zero magnitude,
        /// taking precedence over the user's configured Adaptive Triggers
        /// tab cfg. The user's cfg resumes the moment the motor returns to
        /// 0 — override-with-resume semantics, same as
        /// <c>ConstantTriggerForceEvaluator</c>. Matches Special K's pattern
        /// (SpecialK/src/input/hid_reports/playstation.cpp:2995-3030).
        /// </summary>
        public static Func<int, Guid, (byte right, byte left)> SlotImpulseTriggerForDeviceProvider { get; set; }

        /// <summary>Per-slot steering at-lock AT-resistance (0..1) provider (#94,
        /// channel 4), wired to <c>InputManager.SteeringAtResistance[slot]</c>. 0 when
        /// the toggle is off or no steering source is approaching lock.</summary>
        public static Func<int, float> SteeringAtResistanceProvider { get; set; }

        /// <summary>Per-slot steering at-lock trigger-vibration pulse (0..1) provider (#94,
        /// channel 2), wired to <c>InputManager.GetSteeringTrigVib(slot)</c>. A momentary
        /// hold+fade pulse fired on lock entry; 0 otherwise.</summary>
        public static Func<int, float> SteeringTriggerVibProvider { get; set; }

        // Animated-lightbar polling cadence — 30Hz is enough to feel
        // responsive without flooding the BT HID write path. WriteFile
        // open+close is ~1ms per call; 30Hz = 30ms budget.
        private const int AnimTickMs = 33;

        // Audio onset threshold for AudioPulseRandom: peak rising from
        // below this to above it counts as a pulse onset and rolls a
        // new random colour.
        private const float AudioOnsetEnter = 0.30f;
        private const float AudioOnsetExit  = 0.15f;

        private readonly int _padIndex;
        private DeviceSlotConfig _config;
        private System.Threading.Timer _animTimer;
        private bool _animTickActive;
        // Guards the _animTimer / _animTickActive read-modify-write.
        // UpdateAnimTimer is reached from BOTH the polling thread
        // (OnPollingTickInstance) and the UI thread (OnConfigChanged /
        // Open / Rebind); without this lock two threads observing
        // !_animTickActive in the same window would each construct a
        // Timer, orphan the first (33 ms leak for process life), and
        // double-dispatch effect packets to the pad.
        private readonly object _animTimerLock = new();
        private volatile bool _disposed;

        // Per-mode runtime state. The synthesizer is stateless; the
        // dispatcher carries random-colour memory across audio onsets,
        // the active input-reactive pulse, and the previous button mask
        // for rising-edge detection.
        //
        // Per-(slot, device) state lives in <see cref="_deviceStates"/>
        // — each Sony device on the slot picks its own random hue or
        // palette entry per press so two DualSenses with different
        // palettes (or different per-device random rolls) flash
        // independently. The pulse start timestamp and previous button
        // mask are slot-level (one button-press event drives every
        // device's pulse together).
        private uint _randomColor;
        private bool _audioOnsetActive;
        private long _pulseStartMs;
        private uint _lastButtons;
        private readonly Random _rng = new Random();

        private sealed class DeviceState
        {
            public uint PulseColor;
            public int PalettePulseIndex;
        }
        // ConcurrentDictionary: GetOrCreateDeviceState inserts from the
        // animation-timer thread (DrainInputPulses) while DispatchSnapshot
        // reads it from the polling thread (battery-percent change); a plain
        // Dictionary resizing during that read is undefined behavior, and two
        // overlapping (non-serialized) timer callbacks could double-insert.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DeviceState> _deviceStates = new();

        // Per-device flag remembering whether PadForge's last dispatched
        // packet carried non-zero rumble. Drives the validFlag0 bit-0
        // gating: assert the rumble enable bit when current frame has
        // rumble OR previous frame had rumble (the one-shot drop frame
        // that lets the firmware actually zero the motors before we stop
        // touching them). Clearing bit 0 in subsequent idle frames lets
        // external writers paint rumble without our 30 Hz animation
        // packets clobbering their values. Accessed only inside the
        // devices.SyncRoot lock during DispatchSnapshot.
        private readonly Dictionary<Guid, bool> _prevHadRumble = new();

        // Per-device "did the user have AT engaged on the previous packet"
        // tracking — drives the trigger-enable drop-frame logic the same
        // way _prevHadRumble does for rumble. Asserted when PadForge wants
        // AT this frame OR external is mirroring OR we asserted last
        // frame and now don't (so the cfg-toggle-to-Off transition fires
        // one disengage packet then stops). Cleared steady-state idle so
        // an external program's AT engagement persists in the firmware
        // past our mirror grace.
        private readonly Dictionary<Guid, bool> _prevPadForgeWantsRightTrig = new();
        private readonly Dictionary<Guid, bool> _prevPadForgeWantsLeftTrig = new();

        // Per-device timestamp of the last non-zero impulse-trigger sample
        // for the right / left trigger. Drives the linger window for the
        // impulse-to-AT Vibration auto-route — between rapid game pulses
        // the dispatcher's 30 Hz tick frequently catches a 0 sample even
        // though the trigger is actively being driven, so we hold the
        // Vibration mode override active until the timestamp ages out.
        // Strength is still the CURRENT impR/impL value (so the firmware
        // buzzes at the right amplitude when a pulse hits and goes silent
        // immediately between pulses) — only the MODE is held.
        private readonly Dictionary<Guid, long> _impulseLastNonZeroTickRight = new();
        private readonly Dictionary<Guid, long> _impulseLastNonZeroTickLeft = new();

        // Linger window (ms) for the impulse-to-AT Vibration override.
        // 100 ms is ~3 dispatcher ticks at 30 Hz, enough to bridge GCT's
        // ~20 ms intra-burst pulse gaps without delaying the drop to
        // cfg / Off by long enough to feel laggy when the game stops
        // writing the impulse motor.
        private const long ImpulseAtLingerMs = 100;

        // Per-subsystem external-writer mirroring. When a host (game,
        // ds.daidr.me, any WebHID / hidapi consumer granted access to a
        // PadForge virtual) writes an effect packet, the validFlag bits
        // tell us which subsystems it's updating. Each subsystem touched
        // is captured here with its on-wire bytes and a refresh
        // timestamp; PadForge's own dispatch then mirrors those bytes
        // for as long as the timestamp stays fresh, so PadForge keeps
        // animating subsystems the external writer DIDN'T touch (e.g.
        // ds.daidr.me's rumble button doesn't pause our lightbar
        // animation) while subsystems it DID touch retain the external
        // writer's intent across our 30 Hz cadence (every PadForge
        // packet carries the mirrored bytes, so the firmware sees a
        // stable value across our writes instead of being fought between
        // our animation defaults and the external writer's commands).
        // After the grace window expires without a refresh, ownership
        // returns to PadForge for that subsystem.
        private const long ExternalSubsystemGraceMs = 1500;

        private struct ExternalSubsystemState
        {
            public long RumbleTick;
            public byte RumbleRight;
            public byte RumbleLeft;

            public long RightTrigTick;
            public byte[] RightTrig;     // 11 bytes (mode + 10 params)

            public long LeftTrigTick;
            public byte[] LeftTrig;

            public long MicLedTick;
            public byte MicLed;

            public long LightbarTick;
            public byte LightbarR, LightbarG, LightbarB;

            public long PlayerIndTick;
            public byte PlayerInd;

            // validFlag2-gated subsystems. PadForge always writes
            // validFlag2 = 0xFF in its own packets so brightness +
            // setup go through; when an external writer asserts
            // validFlag2 (any nonzero) we capture both bytes and mirror
            // them so the writer's intent (no-fade vs forced-fade
            // lightbar setup, dim/medium/bright player-LED brightness)
            // survives PadForge's animation cadence.
            public long LightbarSetupTick;
            public byte LightbarSetup;

            public long LedBrightnessTick;
            public byte LedBrightness;
        }

        private static readonly Dictionary<int, ExternalSubsystemState> s_externalState = new();
        private static readonly object s_externalStateLock = new();

        /// <summary>Captured external-write overrides that this dispatch
        /// frame should honor. Null fields mean "PadForge owns this
        /// subsystem this frame — use our own animated / configured
        /// value." Non-null fields carry the most recent external
        /// writer's bytes for subsystems we should mirror.</summary>
        public struct ExternalSubsystemOverrides
        {
            public byte? RumbleRight;
            public byte? RumbleLeft;
            public byte[] RightTriggerEffect;   // 11 bytes when present
            public byte[] LeftTriggerEffect;
            public byte? MuteLed;
            public byte[] LightbarRgb;          // 3 bytes when present
            public byte? PlayerIndicator;
            public byte? LightbarSetup;
            public byte? LedBrightness;

            /// <summary>True once ANY external write has touched this
            /// slot's lightbar this process lifetime (the backing state
            /// is never reset, surviving VC recreates on the slot), even
            /// outside the grace window. The DS5 player-identity idle floor (#191) stands
            /// down permanently after that: pre-floor behavior let a
            /// game's last lightbar write persist in firmware forever
            /// (the enable bit stayed clear when unconfigured), and a
            /// floor that reclaims the bar 1.5 s after a one-shot game
            /// write would stomp exactly those games.</summary>
            public bool LightbarEverExternal;
        }

        /// <summary>Called by <c>HMaestroVirtualController.OutputDecoded</c>
        /// for every external host write to a Sony virtual. Inspects the
        /// 47-byte USB-shape effect payload's validFlag bits to identify
        /// which subsystems the writer touched; captures their bytes and
        /// refreshes a per-subsystem timestamp. Subsequent
        /// <see cref="DispatchSnapshot"/> calls within the grace window
        /// mirror those bytes (per subsystem, independently) so PadForge
        /// keeps animating subsystems the writer didn't touch while
        /// preserving the writer's intent for the ones it did.</summary>
        public static void NotifyExternalSubsystems(int padIndex, ReadOnlySpan<byte> effectPayload)
        {
            if (effectPayload.Length < 47) return;
            byte vf0 = effectPayload[0];
            byte vf1 = effectPayload[1];
            long now = Environment.TickCount64;

            lock (s_externalStateLock)
            {
                if (!s_externalState.TryGetValue(padIndex, out var st))
                    st = default;

                // validFlag0 bits 0 + 1: rumble. Bit 0 is "compatible
                // vibration" (DS4-style motor rumble) and bit 1 is
                // "haptics select" per Linux's hid-playstation; either
                // engages the motors. Steam Input asserts bit 1 only on
                // its DS5 rumble writes (verified in the dispatcher diag
                // log: vf0=0x02 with rumble=(127,127)). Capturing only
                // bit 0 missed Steam entirely. Motor bytes at payload[2]
                // (right) and payload[3] (left).
                if ((vf0 & 0x03) != 0)
                {
                    st.RumbleTick = now;
                    st.RumbleRight = effectPayload[2];
                    st.RumbleLeft = effectPayload[3];
                }
                // validFlag0 bit 2: right trigger effect. 11 bytes at
                // payload[10..20].
                if ((vf0 & 0x04) != 0)
                {
                    st.RightTrigTick = now;
                    st.RightTrig ??= new byte[11];
                    effectPayload.Slice(10, 11).CopyTo(st.RightTrig);
                }
                // validFlag0 bit 3: left trigger effect. 11 bytes at
                // payload[21..31].
                if ((vf0 & 0x08) != 0)
                {
                    st.LeftTrigTick = now;
                    st.LeftTrig ??= new byte[11];
                    effectPayload.Slice(21, 11).CopyTo(st.LeftTrig);
                }
                // validFlag1 bit 0: mic LED. Single byte at payload[8].
                if ((vf1 & 0x01) != 0)
                {
                    st.MicLedTick = now;
                    st.MicLed = effectPayload[8];
                }
                // validFlag1 bit 2: lightbar RGB. 3 bytes at payload[44..46].
                if ((vf1 & 0x04) != 0)
                {
                    st.LightbarTick = now;
                    st.LightbarR = effectPayload[44];
                    st.LightbarG = effectPayload[45];
                    st.LightbarB = effectPayload[46];
                }
                // validFlag1 bit 4: player indicator. Single byte at payload[43].
                if ((vf1 & 0x10) != 0)
                {
                    st.PlayerIndTick = now;
                    st.PlayerInd = effectPayload[43];
                }

                // validFlag2 (payload[38]) gates lightbarSetup (payload[41])
                // and ledBrightness (payload[42]). We don't enumerate
                // individual validFlag2 bits — Sony's bit map for this byte
                // is documented inconsistently across community sources.
                // Defensive heuristic: any nonzero validFlag2 means the
                // external writer is asserting at least one of the
                // lightbarSetup / ledBrightness updates, so capture both
                // bytes. PadForge writes validFlag2 = 0xFF in its own
                // packets so this clause won't loop back on us — OutputDecoded
                // only fires for the virtual's host writes, not PadForge's
                // raw HID output to the physical pad.
                byte vf2 = effectPayload[38];
                if (vf2 != 0)
                {
                    st.LightbarSetupTick = now;
                    st.LightbarSetup = effectPayload[41];
                    st.LedBrightnessTick = now;
                    st.LedBrightness = effectPayload[42];
                }

                s_externalState[padIndex] = st;
            }
        }

        private static ExternalSubsystemOverrides GetActiveOverrides(int padIndex)
        {
            var ov = default(ExternalSubsystemOverrides);
            lock (s_externalStateLock)
            {
                if (!s_externalState.TryGetValue(padIndex, out var st)) return ov;
                long now = Environment.TickCount64;
                if (now - st.RumbleTick < ExternalSubsystemGraceMs)
                {
                    ov.RumbleRight = st.RumbleRight;
                    ov.RumbleLeft  = st.RumbleLeft;
                }
                if (now - st.RightTrigTick < ExternalSubsystemGraceMs && st.RightTrig != null)
                    ov.RightTriggerEffect = st.RightTrig;
                if (now - st.LeftTrigTick < ExternalSubsystemGraceMs && st.LeftTrig != null)
                    ov.LeftTriggerEffect = st.LeftTrig;
                if (now - st.MicLedTick < ExternalSubsystemGraceMs)
                    ov.MuteLed = st.MicLed;
                if (now - st.LightbarTick < ExternalSubsystemGraceMs)
                    ov.LightbarRgb = new byte[] { st.LightbarR, st.LightbarG, st.LightbarB };
                ov.LightbarEverExternal = st.LightbarTick != 0;
                if (now - st.PlayerIndTick < ExternalSubsystemGraceMs)
                    ov.PlayerIndicator = st.PlayerInd;
                if (now - st.LightbarSetupTick < ExternalSubsystemGraceMs)
                    ov.LightbarSetup = st.LightbarSetup;
                if (now - st.LedBrightnessTick < ExternalSubsystemGraceMs)
                    ov.LedBrightness = st.LedBrightness;
            }
            return ov;
        }

        private DeviceState GetOrCreateDeviceState(Guid deviceGuid)
            => _deviceStates.GetOrAdd(deviceGuid, _ => new DeviceState());

        /// <summary>Static provider returning every per-device
        /// <see cref="DeviceSlotConfig"/> on a slot. The dispatcher's
        /// device loop reads this to synthesize per-device output (each
        /// device renders its own LightbarMode + colors / palette).
        /// Wired by InputService to
        /// <c>InputManager._perDeviceSlotConfigs[slot]</c>.</summary>
        public static Func<int, IReadOnlyDictionary<Guid, DeviceSlotConfig>> SlotPerDeviceConfigsProvider { get; set; }

        // Per-slot instance registry. Step 2's polling thread broadcasts
        // a per-tick rumble-status poke through OnPollingTick(padIndex, ...)
        // so the timer state can react to game-rumble onset and audio-rumble
        // toggle even when the slot's lightbar mode is static. Without this,
        // an idle-lightbar slot would never spin up the dispatcher's timer
        // and the SDL-skip path (ApplyForceFeedback) would leave Sony pads
        // with no rumble writer at all.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, UserEffectsDispatcher> _instances = new();

        public UserEffectsDispatcher(int padIndex, DeviceSlotConfig config)
        {
            _padIndex = padIndex;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            _instances[padIndex] = this;
            RollRandomColor();
            UpdateAnimTimer();
        }

        /// <summary>Polling-thread broadcast — Step 2 calls this every
        /// tick for each slot with the current "any reason to keep the
        /// dispatcher's effect-packet timer alive" inputs. The dispatcher
        /// merges this with its lightbar-animation logic in
        /// <see cref="UpdateAnimTimer"/>; transitions in either direction
        /// kick the timer on or off so audio-rumble / game-rumble onset
        /// without an animated lightbar still produces dispatcher writes,
        /// and a slot that drops both stays parked.</summary>
        public static void OnPollingTick(int padIndex, bool slotHasGameRumble, bool slotHasAudioRumbleEnabled)
        {
            if (_instances.TryGetValue(padIndex, out var d))
                d.OnPollingTickInstance(slotHasGameRumble, slotHasAudioRumbleEnabled);
        }

        // Volatile: written by the polling thread's edge-triggered poke, read
        // by the anim-timer thread's self-stop re-check under _animTimerLock.
        private volatile bool _slotNeedsRumbleTimer;
        private void OnPollingTickInstance(bool gameRumble, bool audioRumbleEnabled)
        {
            bool need = gameRumble || audioRumbleEnabled;
            if (need != _slotNeedsRumbleTimer)
            {
                _slotNeedsRumbleTimer = need;
                UpdateAnimTimer();
            }
        }

        /// <summary>Re-binds to a new <see cref="DeviceSlotConfig"/>
        /// instance. Used when the parent <see cref="PadViewModel"/>
        /// reassigns its config via the setter (e.g. profile load).</summary>
        public void Rebind(DeviceSlotConfig config)
        {
            if (_disposed) return;
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = config;
            if (_config != null)
                _config.PropertyChanged += OnConfigChanged;
            UpdateAnimTimer();
            // Push a snapshot immediately so the assigned DS5 reflects
            // the new config without waiting for the next user edit.
            ApplyOnce();
        }

        /// <summary>Manually trigger one apply pass. Used after the
        /// dispatcher is constructed (initial state) and from Rebind.</summary>
        public void ApplyOnce()
        {
            if (_disposed || _config == null) return;
            DispatchSnapshot();
        }

        /// <summary>One apply pass on EVERY live dispatcher. Called after
        /// slot topology changes (create / delete / reorder) so each
        /// pad's player-identity idle floor (#191) picks up its new
        /// virtual controller number without waiting for the next
        /// config or device poke.</summary>
        public static void ApplyOnceAll()
        {
            foreach (var d in _instances.Values)
            {
                try { d?.ApplyOnce(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopAnimTimer();
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = null;
            // Only remove from the registry if WE are still the registered
            // instance — a fresh dispatcher could have replaced us mid-life
            // (rebind during VC reset) and we shouldn't yank its slot key.
            _instances.TryGetValue(_padIndex, out var current);
            if (ReferenceEquals(current, this))
                _instances.TryRemove(_padIndex, out _);
        }

        private void OnConfigChanged(object sender, PropertyChangedEventArgs e)
        {
            // Mode / period / overlay / macro-override changes can flip
            // whether the periodic timer should be running. Without
            // re-evaluating, enabling the input-reactive overlay on a
            // slot whose base mode is static / off would never start
            // the timer, so button-press edges would never be detected.
            if (e.PropertyName == nameof(DeviceSlotConfig.LightbarMode)
                || e.PropertyName == nameof(DeviceSlotConfig.LightbarPeriodMs)
                || e.PropertyName == nameof(DeviceSlotConfig.InputReactiveMode)
                || e.PropertyName == nameof(DeviceSlotConfig.MacroOverrideExpiresAtUtc))
                UpdateAnimTimer();
            if (e.PropertyName == nameof(DeviceSlotConfig.AudioPassthroughEnabled)
                || e.PropertyName == nameof(DeviceSlotConfig.AudioMirrorSourceId))
            {
                AudioPassthroughService.Reconcile(); // start/stop/repoint the mirror now
                WiiSpeakerService.Reconcile();        // same for a Wii Remote speaker mirror
                HapticToneService.Reconcile();        // and the Switch/Steam haptic-tone mirror
            }
            DispatchSnapshot();
        }

        // ────────────────────────────────────────────────
        //  Animation / audio / input-reactive timer
        // ────────────────────────────────────────────────
        // Runs while the active LightbarMode is animated (anything that
        // depends on time, audio peak, or input state). Idle modes (Off
        // and Static) only dispatch on config changes, so the timer
        // stays parked.

        private static bool IsAnimated(LightbarMode mode) =>
            mode is LightbarMode.Breathing
                  or LightbarMode.Strobe
                  or LightbarMode.Rainbow
                  or LightbarMode.ColorCycle
                  or LightbarMode.AudioPulse
                  or LightbarMode.AudioPulseRandom
                  or LightbarMode.AudioPulseRainbow
                  or LightbarMode.AudioThresholds
                  or LightbarMode.AudioGradient
                  or LightbarMode.AudioCrossFade
                  or LightbarMode.InputReactive
                  or LightbarMode.InputReactiveCycle
                  or LightbarMode.InputReactiveFixed;
        // Battery mode is push-driven: InputManager fires
        // <see cref="NotifyBatteryPercentChanged"/> whenever the slot's
        // BatteryPercents entry transitions, which calls
        // DispatchSnapshot once. The animation timer stays parked, so
        // a slot sitting on Battery mode costs zero per-tick CPU.

        /// <summary>One-shot refresh request from InputManager when the
        /// slot's reported battery percent changes. The dispatcher
        /// reads the new percent via <see cref="SlotBatteryPercentProvider"/>
        /// inside DispatchSnapshot, so this method just kicks the
        /// snapshot. Safe to call from the polling thread.</summary>
        public static void NotifyBatteryPercentChanged(int padIndex)
        {
            if (_instances.TryGetValue(padIndex, out var inst) && inst != null)
                inst.DispatchSnapshot();
        }

        /// <summary>Audio-tab routing or volume changed for the slot — push a
        /// dispatch immediately so the speaker output path / volume bytes land
        /// now instead of riding the next lightbar/battery dispatch (which can
        /// be seconds away on an idle slot).</summary>
        public static void NotifySoundRoutingChanged(int padIndex)
        {
            if (_instances.TryGetValue(padIndex, out var inst) && inst != null)
                inst.DispatchSnapshot();
        }

        private void UpdateAnimTimer()
        {
            // Timer wants to run when:
            //   - LightbarMode is animated (audio / breathing / etc.), or
            //   - A Reactive macro override is in flight (intensity is
            //     decaying and needs per-tick re-dispatch).
            // A Sticky override has constant RGB and constant intensity
            // (1.0), so the dispatcher just needs the one snapshot fired
            // off the OnConfigChanged event. No timer required.
            // Walk every per-device config on the slot — the timer runs
            // when any device wants animation or has a reactive override
            // in flight, not just the SelectedMappedDevice's. Falls back
            // to the anchor _config when the per-device dictionary
            // hasn't been wired yet (early startup).
            bool wantTimer = false;
            if (!_disposed)
            {
                var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
                if (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
                {
                    foreach (var kvp in perDeviceCfgs)
                    {
                        var devCfg = kvp.Value;
                        if (devCfg == null) continue;
                        if (IsAnimated(devCfg.LightbarMode))
                        {
                            wantTimer = true;
                            break;
                        }
                        if (devCfg.HasActiveMacroLightbarOverride
                            && devCfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive)
                        {
                            wantTimer = true;
                            break;
                        }
                        // v3.2 input-reactive overlay needs the timer
                        // running for both button-press edge detection
                        // (DrainInputPulses fires only from OnAnimTick)
                        // and for the pulse-intensity decay that fades
                        // the flash back into the base color.
                        if (devCfg.InputReactiveMode != InputReactiveMode.Off)
                        {
                            wantTimer = true;
                            break;
                        }
                    }
                }
                else if (_config != null)
                {
                    bool reactiveOverrideRunning =
                        _config.HasActiveMacroLightbarOverride
                        && _config.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
                    wantTimer = IsAnimated(_config.LightbarMode)
                        || reactiveOverrideRunning
                        || _config.InputReactiveMode != InputReactiveMode.Off;
                }

                // Polling-thread rumble poke. The dispatcher is the SOLE
                // writer of DS5/DS4 effect packets, so it must keep its
                // timer alive across every state where rumble bytes need
                // to flow — game-rumble in flight (raw VibrationStates
                // non-zero) and audio-rumble enabled on any per-device
                // PadSetting. Without this gate, an idle-lightbar slot
                // would have no writer at all once Step 2 stopped
                // calling SDL_RumbleJoystick for Sony pads.
                if (_slotNeedsRumbleTimer)
                    wantTimer = true;
            }

            bool dispatchFinal = false;
            lock (_animTimerLock)
            {
                // _disposed re-checked INSIDE the lock: a polling-thread call
                // that computed wantTimer before Dispose ran must not
                // resurrect a timer on the dead instance (audit F2). The
                // outside check only saves the provider walk.
                if (wantTimer && !_animTickActive && !_disposed)
                {
                    _animTickActive = true;
                    _animTimer = new System.Threading.Timer(
                        OnAnimTick, null, AnimTickMs, AnimTickMs);
                }
                else if (!wantTimer && _animTickActive)
                {
                    // Stop the timer under the lock, but defer the final
                    // snapshot dispatch until after releasing it (dispatch
                    // touches HID/effect paths and must not run under the
                    // lifecycle lock).
                    StopAnimTimerLocked();
                    dispatchFinal = true;
                }
            }

            if (dispatchFinal)
                // Dispatch a final snapshot after stopping so the rumble
                // bytes (now zeroed) reach the firmware. Without this,
                // the dispatcher's last per-tick write was the live
                // rumble; when the polling thread reports gameRumble=false
                // and we stop the timer, the controller never gets a
                // "rumble = 0" packet and keeps rumbling until the next
                // dispatch (which can be 6-8s away if the user has no
                // animated lightbar / audio rumble running). This is the
                // OnPollingTickInstance counterpart to OnAnimTick's
                // early-exit final snapshot at line ~479.
                DispatchSnapshot();
        }


        private void StopAnimTimer()
        {
            lock (_animTimerLock)
                StopAnimTimerLocked();
        }

        /// <summary>OnAnimTick's self-stop path: re-verify the polling-thread
        /// rumble poke under the lock before stopping. The poke is
        /// edge-triggered (OnPollingTickInstance calls UpdateAnimTimer only on
        /// a need transition), so a rumble onset landing between the tick's
        /// unlocked read and this stop would otherwise leave the slot with no
        /// effect writer until the next rumble edge (audit F3). The lightbar
        /// conditions need no re-check here: config changes always re-poke via
        /// OnConfigChanged.</summary>
        private void StopAnimTimerIfStillIdle()
        {
            lock (_animTimerLock)
            {
                if (_slotNeedsRumbleTimer) return; // onset raced the stop: keep running
                StopAnimTimerLocked();
            }
        }

        /// <summary>Timer teardown; caller must hold <see cref="_animTimerLock"/>.</summary>
        private void StopAnimTimerLocked()
        {
            _animTickActive = false;
            try { _animTimer?.Dispose(); } catch { }
            _animTimer = null;
            _lastDispatchedPeak = -1f;
        }

        private float _lastDispatchedPeak = -1f;
        private byte _lastDispatchedRumbleR;
        private byte _lastDispatchedRumbleL;
        private bool _lastTickOverrideActive;

        private int _animTickBusy;

        private void OnAnimTick(object state)
        {
            // System.Threading.Timer callbacks overlap when a tick runs past the
            // period. Serialize them: a tick still in progress skips the next
            // (drops one lightbar frame, benign) so _rng / _lastButtons / pulse
            // state can't be mutated by two callbacks at once. The try/catch keeps
            // a fault (e.g. _config nulled by a concurrent Dispose) from a timer
            // thread from terminating the whole process.
            if (System.Threading.Interlocked.Exchange(ref _animTickBusy, 1) == 1) return;
            try { OnAnimTickCore(state); }
            catch { /* dropped frame; a timer-thread exception must never crash the app */ }
            finally { System.Threading.Interlocked.Exchange(ref _animTickBusy, 0); }
        }

        private void OnAnimTickCore(object _)
        {
            // Snapshot once: Dispose can null _config on the UI thread mid-callback,
            // and every deref below would NRE on the raw field.
            var cfg = _config;
            if (_disposed || cfg == null) return;

            // Aggregate state across every per-device config on the
            // slot. The timer only stops when NO device wants it.
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
            bool anyAnimated = false;
            bool anyReactiveRunning = false;
            bool anyAudioMode = false;
            bool anyAudioPulseRandom = false;
            bool anyInputReactiveOverlay = false;
            float maxSensitivity = (float)cfg.AudioLightbarSensitivity;
            if (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
            {
                maxSensitivity = 0f;
                foreach (var kvp in perDeviceCfgs)
                {
                    var devCfg = kvp.Value;
                    if (devCfg == null) continue;
                    var devMode = devCfg.LightbarMode;
                    if (IsAnimated(devMode)) anyAnimated = true;
                    if (IsAudioMode(devMode))
                    {
                        anyAudioMode = true;
                        if (devMode == LightbarMode.AudioPulseRandom) anyAudioPulseRandom = true;
                    }
                    var s = (float)devCfg.AudioLightbarSensitivity;
                    if (s > maxSensitivity) maxSensitivity = s;
                    if (devCfg.HasActiveMacroLightbarOverride
                        && devCfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive)
                        anyReactiveRunning = true;
                    if (devCfg.InputReactiveMode != InputReactiveMode.Off)
                        anyInputReactiveOverlay = true;
                }
            }
            else
            {
                // No per-device dictionary wired yet — fall back to anchor.
                var mode = cfg.LightbarMode;
                anyAnimated = IsAnimated(mode);
                anyAudioMode = IsAudioMode(mode);
                anyAudioPulseRandom = mode == LightbarMode.AudioPulseRandom;
                bool overrideActive = cfg.HasActiveMacroLightbarOverride;
                anyReactiveRunning = overrideActive && cfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
                anyInputReactiveOverlay = cfg.InputReactiveMode != InputReactiveMode.Off;
            }

            // If no device wants an animated mode, no Reactive override,
            // no input-reactive overlay, and no rumble work to push,
            // dispatch one final snapshot (so a just-expired override
            // hands off cleanly) and stop the timer. Sticky holds don't
            // keep the timer running — RGB and intensity are constant.
            if (!anyAnimated && !anyReactiveRunning && !anyInputReactiveOverlay && !_slotNeedsRumbleTimer)
            {
                if (_lastTickOverrideActive)
                {
                    DispatchSnapshot();
                }
                _lastTickOverrideActive = false;
                StopAnimTimerIfStillIdle();
                return;
            }
            _lastTickOverrideActive = anyReactiveRunning;

            // Non-animated paths that still need every-tick dispatch:
            //   - Reactive macro override is decaying intensity → ramp.
            //   - Input-reactive overlay is enabled → DrainInputPulses
            //     must run every tick to detect button-press edges, and
            //     pulseIntensity decays smoothly back to base color.
            //   - Rumble work in flight → effect packet must carry the
            //     per-device rumble bytes (sole-writer model).
            if (!anyAnimated && (anyReactiveRunning || anyInputReactiveOverlay || _slotNeedsRumbleTimer))
            {
                if (anyInputReactiveOverlay) DrainInputPulses();
                DispatchSnapshot();
                return;
            }

            // Slot-level audio peak — used only by the steady-state
            // early-exit below. Per-device peak scaling happens inside
            // the device synth call. Use the slot's max sensitivity so
            // the early-exit threshold doesn't suppress a device that's
            // more sensitive than the selected one.
            float rawPeak = AudioPeakProvider?.Invoke() ?? 0f;
            float scaled = Math.Clamp(rawPeak * maxSensitivity, 0f, 1f);

            // Roll a new random colour on the rising edge of an audio
            // onset, so AudioPulseRandom flashes a fresh hue per pulse.
            // Slot-level — every AudioPulseRandom device on the slot
            // shares the same per-onset hue.
            if (anyAudioPulseRandom)
            {
                if (!_audioOnsetActive && scaled >= AudioOnsetEnter)
                {
                    _audioOnsetActive = true;
                    RollRandomColor();
                }
                else if (_audioOnsetActive && scaled <= AudioOnsetExit)
                {
                    _audioOnsetActive = false;
                }
            }

            // Drain button rising edges. The slot-level button mask is
            // shared across devices but each device rolls its own pulse
            // colour using its own LightbarMode + palette, so two
            // DualSenses on the slot can flash independently.
            DrainInputPulses();

            if (anyAudioMode)
            {
                float delta = MathF.Abs(scaled - _lastDispatchedPeak);
                bool zeroCrossing =
                    (scaled == 0f && _lastDispatchedPeak > 0f)
                    || (_lastDispatchedPeak == 0f && scaled > 0f);

                // Don't suppress the dispatch when game rumble changes —
                // even a steady audio peak shouldn't stall the rumble
                // passthrough. Uses the slot's raw rumble for change
                // detection; per-device scaling happens later in the
                // device loop via SlotRumbleForDeviceProvider.
                var r = SlotRawRumbleProvider?.Invoke(_padIndex) ?? ((byte)0, (byte)0);
                bool rumbleChanged = r.right != _lastDispatchedRumbleR || r.left != _lastDispatchedRumbleL;

                // Suppress the rainbow-pulse mode's special-case
                // anti-skip only when ANY device is on AudioPulseRainbow
                // — keeps that mode's per-tick hue rotation alive.
                bool anyAudioPulseRainbow = AnyDeviceMode(perDeviceCfgs, LightbarMode.AudioPulseRainbow);
                if (!zeroCrossing && !rumbleChanged && delta < 0.004f && !anyAudioPulseRainbow)
                    return;
                _lastDispatchedPeak = scaled;
                _lastDispatchedRumbleR = r.right;
                _lastDispatchedRumbleL = r.left;
            }

            DispatchSnapshot(scaled);
        }

        /// <summary>True when any device's config on the slot is in the
        /// given <see cref="LightbarMode"/>. Used by tick-suppression
        /// special-cases (e.g. AudioPulseRainbow's per-tick rotation).</summary>
        private static bool AnyDeviceMode(IReadOnlyDictionary<Guid, DeviceSlotConfig> cfgs, LightbarMode mode)
        {
            if (cfgs == null) return false;
            foreach (var kvp in cfgs)
                if (kvp.Value != null && kvp.Value.LightbarMode == mode) return true;
            return false;
        }

        private static bool IsAudioMode(LightbarMode m) =>
            m is LightbarMode.AudioPulse
              or LightbarMode.AudioPulseRandom
              or LightbarMode.AudioPulseRainbow
              or LightbarMode.AudioThresholds
              or LightbarMode.AudioGradient
              or LightbarMode.AudioCrossFade;

        private void RollRandomColor()
        {
            // Pick a vivid hue uniformly. Saturation+value pinned to 1
            // so the colour reads cleanly through the diffuser at any
            // peak intensity.
            int h = _rng.Next(0, 360);
            HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
            _randomColor = (uint)((r << 16) | (g << 8) | b);
        }

        private void DrainInputPulses()
        {
            // Slot-level button-press detection — one rising-edge event
            // per tick fans out to every per-device pulse below.
            var provider = SlotButtonsProvider;
            uint buttons = provider != null ? provider(_padIndex) : 0u;
            uint newlyPressed = buttons & ~_lastButtons;
            _lastButtons = buttons;
            if (newlyPressed == 0) return;

            // Roll per-device pulse colour using each device's own
            // input-reactive overlay mode + palette. The legacy
            // LightbarMode.InputReactive* values are still honored for
            // any unmigrated saves / runtime macro applications, but
            // the v3.2+ overlay surface is cfg.InputReactiveMode
            // (independent of base LightbarMode, lerped over the base
            // by pulseIntensity in the synthesizer).
            //
            //   - Random / legacy InputReactive    → random hue
            //   - Cycle  / legacy InputReactiveCycle → palette step
            //   - Fixed  / legacy InputReactiveFixed → no roll
            //     (synthesizer reads cfg.LightbarRed/G/B)
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
            if (perDeviceCfgs != null)
            {
                foreach (var kvp in perDeviceCfgs)
                {
                    var devCfg = kvp.Value;
                    if (devCfg == null) continue;
                    var devMode = devCfg.LightbarMode;
                    var overlayMode = devCfg.InputReactiveMode;

                    bool wantRandomRoll =
                        overlayMode == InputReactiveMode.Random
                        || devMode == LightbarMode.InputReactive;
                    bool wantPaletteRoll =
                        overlayMode == InputReactiveMode.Cycle
                        || devMode == LightbarMode.InputReactiveCycle;
                    bool wantFixed =
                        overlayMode == InputReactiveMode.Fixed
                        || devMode == LightbarMode.InputReactiveFixed;

                    if (!wantRandomRoll && !wantPaletteRoll && !wantFixed)
                        continue;

                    var state = GetOrCreateDeviceState(kvp.Key);
                    if (wantRandomRoll)
                    {
                        int h = _rng.Next(0, 360);
                        HsvToRgb(h, 1.0, 1.0, out var r, out var g, out var b);
                        state.PulseColor = (uint)((r << 16) | (g << 8) | b);
                    }
                    else if (wantPaletteRoll)
                    {
                        // InputReactive Cycle steps its OWN palette, not the ColorCycle one.
                        var palette = devCfg.SnapshotLightbarInputReactivePalette();
                        int n = palette.Length;
                        if (n > 0)
                        {
                            state.PalettePulseIndex = (state.PalettePulseIndex + 1) % n;
                            var entry = palette[state.PalettePulseIndex];
                            state.PulseColor = (uint)((entry.R << 16) | (entry.G << 8) | entry.B);
                        }
                        else
                        {
                            state.PulseColor = 0;
                        }
                    }
                    // wantFixed: synthesizer reads the device's
                    // LightbarRed/G/B; no per-device pulse colour to
                    // roll here.
                }
            }
            _pulseStartMs = Environment.TickCount64;
        }

        private float ComputePulseIntensity(long nowMs, DeviceSlotConfig cfg)
        {
            if (_pulseStartMs == 0 || cfg == null) return 0f;
            long elapsed = nowMs - _pulseStartMs;
            int hold = Math.Max(cfg.LightbarInputHoldMs, 0);
            int decay = Math.Max(cfg.LightbarInputDecayMs, 0);
            if (elapsed < 0) return 1f;
            if (elapsed < hold) return 1f;
            if (decay <= 0) return elapsed >= hold ? 0f : 1f;
            long fadeElapsed = elapsed - hold;
            if (fadeElapsed >= decay) return 0f;
            return 1f - (float)fadeElapsed / decay;
        }

        private static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double rp, gp, bp;
            if (h < 60)       { rp = c; gp = x; bp = 0; }
            else if (h < 120) { rp = x; gp = c; bp = 0; }
            else if (h < 180) { rp = 0; gp = c; bp = x; }
            else if (h < 240) { rp = 0; gp = x; bp = c; }
            else if (h < 300) { rp = x; gp = 0; bp = c; }
            else              { rp = c; gp = 0; bp = x; }
            r = (byte)Math.Round((rp + m) * 255);
            g = (byte)Math.Round((gp + m) * 255);
            b = (byte)Math.Round((bp + m) * 255);
        }

        private void DispatchSnapshot(float audioPeak = -1f)
        {
            // Snapshot once (Dispose can null the field on the UI thread while a
            // timer-thread dispatch is mid-flight).
            var cfg = _config;
            if (cfg == null) return;

            // Player-identity idle floor (#191): recomputed per dispatch
            // so slot create / delete / reorder self-heals on the next
            // poke (ApplyOnceAll fires one after topology changes).
            // Passed to the synthesizers, which only use it when nothing
            // else claims the lightbar / player LEDs. This slot's own
            // number is only the FALLBACK: each device resolves its
            // identity winner in the loop below, so two dispatchers
            // sharing one pad write the same number instead of fighting.
            int playerNumber = SettingsManager.SlotOrders.GetGlobalSlotNumber(_padIndex);

            // For non-tick dispatches (slider drag, OnDevicesUpdated re-
            // apply, etc.), pull the current peak so the audio path
            // doesn't snap to black between ticks. The synthesizer
            // ignores the peak when the active mode doesn't read it.
            float rawAudioPeak = AudioPeakProvider?.Invoke() ?? 0f;
            // Per-device peak scaling happens inside the device loop (each
            // device has its own AudioLightbarSensitivity); this fallback
            // uses the slot's "anchor" config sensitivity for the
            // non-tick path's pre-loop default.
            float peakForSynthDefault = audioPeak >= 0f
                ? audioPeak
                : Math.Clamp(
                    rawAudioPeak * (float)cfg.AudioLightbarSensitivity,
                    0f, 1f);
            long nowMs = Environment.TickCount64;

            // Test-rumble target for this slot. When set, only the matching
            // device receives the rumble bytes inside the effect packet —
            // every other Sony device mapped to the slot still gets its
            // lightbar / trigger / mic-LED updates but with rumble bytes
            // zeroed out. Without this gate, an Xbox-VC test rumble would
            // ride the dispatcher's 30 Hz packet to every DualSense mapped
            // to the slot. Step 2's SDL physical-rumble path already honors
            // the same filter via InputManager.TestRumbleTargetGuid.
            Guid testTarget = TestRumbleTargetGuidProvider?.Invoke(_padIndex) ?? Guid.Empty;

            // Per-subsystem override snapshot for this dispatch. Subsystems
            // the external writer recently touched are mirrored from their
            // captured bytes; subsystems they didn't touch keep flowing
            // PadForge's own animated / configured values. Test rumble
            // (user-initiated inside PadForge) bypasses external rumble
            // mirroring so the user's test always wins.
            var overrides = GetActiveOverrides(_padIndex);
            if (testTarget != Guid.Empty)
            {
                overrides.RumbleRight = null;
                overrides.RumbleLeft  = null;
            }

            // Per-(slot, device) lighting configs — each Sony device on
            // the slot synthesizes from its own LightbarMode / colors /
            // palette / decay so two DualSenses can light up
            // independently. Falls back to the dispatcher's anchor
            // _config (the slot's selected device) when the per-device
            // dictionary hasn't been wired yet.
            var perDeviceCfgs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);

            var settings = SettingsManager.UserSettings;
            var devices = SettingsManager.UserDevices;
            if (settings == null || devices == null) return;

            // Resolve assigned DS5 GUIDs.
            var guids = new System.Collections.Generic.List<Guid>(4);
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != _padIndex) continue;
                    if (us.InstanceGuid == Guid.Empty) continue;
                    guids.Add(us.InstanceGuid);
                }
            }
            if (guids.Count == 0) return;

            lock (devices.SyncRoot)
            {
                foreach (var ud in devices.Items)
                {
                    if (ud == null) continue;

                    bool isDs5 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == PidStandard || ud.ProdId == PidEdge);
                    bool isDs4 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == Ds4Pid_V1 || ud.ProdId == Ds4Pid_V1Alt || ud.ProdId == Ds4Pid_V2);
                    bool isPs = isDs5 || isDs4;

                    if (!guids.Contains(ud.InstanceGuid)) continue;
                    if (!ud.IsOnline) continue;
                    if (!isPs) continue;

                    // Identity precedence: a pad shared across virtual
                    // controllers takes the winning (smallest displayed)
                    // number, so this dispatcher and the other slot's
                    // dispatcher write identical identity bytes instead
                    // of blinking the LEDs between two players. Settings
                    // lock inside the devices lock is the documented
                    // devices-before-settings order.
                    int devPlayerNumber = SettingsManager.SlotOrders.GetIdentityPlayerNumber(ud.InstanceGuid);
                    if (devPlayerNumber <= 0) devPlayerNumber = playerNumber;

                    // Per-device shadow of the external-mirror override struct.
                    // Each device may receive its own dispatcher-injected
                    // overrides (e.g. impulse-trigger → AT Vibration auto-route
                    // with per-device strength scaling), and a struct mutation
                    // on `overrides` would leak to the next iteration. The
                    // outer `overrides` is preserved as the read-only external
                    // capture; the per-device copy carries dispatcher adds.
                    var devOverrides = overrides;

                    string path = ud.DevicePath;
                    if (string.IsNullOrEmpty(path)) continue;
                    bool isBluetooth = SonyEffectWriter.IsBluetoothPath(path);

                    // Resolve the HM profile whose extendedOutputReport spec
                    // describes this device's wire format. The synthesizer
                    // emits semantic fields (rightMotor / leftMotor /
                    // lightbar / triggers / ...); SonyEffectWriter feeds them
                    // through HMOutputEncoder to produce the on-wire bytes
                    // including BT framing and CRC32 footer where the spec
                    // declares them.
                    var profile = ResolveSonyProfile(ud.ProdId, isBluetooth);
                    if (profile == null) continue;

                    // Per-device rumble bytes — each Sony device on the
                    // slot pulls its OWN PadSetting (audio rumble + gain
                    // + motor balance + swap) so different physical
                    // devices on the same slot get different output.
                    var perDevRumble = SlotRumbleForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid)
                                       ?? ((byte)0, (byte)0);
                    // Test-rumble target gates the rumble bytes only —
                    // lightbar/trigger/mic-LED still update on non-target
                    // devices so an active animation doesn't freeze across
                    // the 500 ms test window.
                    bool deliverRumble = testTarget == Guid.Empty || ud.InstanceGuid == testTarget;
                    byte rR = deliverRumble ? perDevRumble.right : (byte)0;
                    byte rL = deliverRumble ? perDevRumble.left  : (byte)0;

                    // Game-driven passthrough: when a game wrote to the
                    // virtual DualSense, OutputDecoded enqueues the full
                    // effect payload to the passthrough dispatcher AND
                    // the per-subsystem mirror captures the rumble bytes
                    // as overrides.RumbleRight/Left. For the specific
                    // real DualSense the passthrough targets, zero our
                    // rumble bytes so the Sony dispatcher's effect packet
                    // doesn't race the passthrough write to the same
                    // device. Test rumble doesn't go through OutputDecoded,
                    // so the override is null and this branch is skipped —
                    // the bytes flow through to the real DualSense via
                    // the dispatcher path, which is the only writer.
                    bool gameDrivenRumble = devOverrides.RumbleRight.HasValue && devOverrides.RumbleLeft.HasValue;
                    if (gameDrivenRumble && isDs5
                        && DualSensePassthroughDispatcher.IsPassthroughTarget(_padIndex, ud.InstanceGuid))
                    {
                        rR = 0;
                        rL = 0;
                    }

                    // Resolve this device's per-device lighting config.
                    // Falls back to the slot's anchor config if missing
                    // (transient case before the dictionary is wired).
                    DeviceSlotConfig devCfg = null;
                    if (perDeviceCfgs != null
                        && perDeviceCfgs.TryGetValue(ud.InstanceGuid, out var resolved))
                        devCfg = resolved;
                    devCfg ??= cfg;
                    if (devCfg == null) continue;

                    // Impulse-trigger → DualSense Adaptive Trigger Vibration
                    // auto-route. When the slot's output VC is XInput-class
                    // and the game writes an impulse trigger motor magnitude
                    // (XINPUT_VIBRATION_EX bytes 4/5), the provider returns
                    // a non-zero byte and we synthesize an AT Vibration block
                    // overriding that trigger's effect for the tick.
                    //
                    // Linger window: the dispatcher polls at 30 Hz but games
                    // (and GCT) drive the impulse motor high-low at sub-tick
                    // intervals. Without a linger window the dispatcher
                    // catches a 0 sample on most ticks and constantly drops
                    // out of Vibration mode, silencing the trigger between
                    // pulses we should feel as one continuous buzz. Hold
                    // Vibration mode for ~100 ms after the last non-zero
                    // sample. Strength still tracks the current sample —
                    // 0 during inter-pulse gaps produces no buzz, the
                    // amplitude when a pulse hits buzzes immediately, and
                    // the mode stays so successive pulses don't pay the
                    // mode-switch cost.
                    //
                    // After the linger expires the override drops, the
                    // drop-frame logic in the assert below emits one final
                    // cfg-or-Off encode, and the user's configured AT cfg
                    // (resistance, weapon, galloping…) resumes.
                    //
                    // Test-rumble target gate mirrors Step 2's Xbox writer
                    // path (InputManager.Step2.UpdateInputStates.cs:344-346):
                    // when a test rumble targets a specific device in the
                    // slot, only that device receives the override. Real-game
                    // impulse-trigger writes (testTarget == Empty) apply to
                    // every assigned Sony pad with the per-device scaling
                    // already baked into the provider's returned byte.
                    if (isDs5 && (testTarget == Guid.Empty || ud.InstanceGuid == testTarget))
                    {
                        var (impR, impL) = SlotImpulseTriggerForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid)
                                           ?? ((byte)0, (byte)0);
                        long nowTickMs = Environment.TickCount64;
                        if (impR > 0) _impulseLastNonZeroTickRight[ud.InstanceGuid] = nowTickMs;
                        if (impL > 0) _impulseLastNonZeroTickLeft[ud.InstanceGuid]  = nowTickMs;
                        bool rightLingerActive = _impulseLastNonZeroTickRight.TryGetValue(ud.InstanceGuid, out var lrR)
                            && nowTickMs - lrR < ImpulseAtLingerMs;
                        bool leftLingerActive  = _impulseLastNonZeroTickLeft.TryGetValue(ud.InstanceGuid, out var lrL)
                            && nowTickMs - lrL < ImpulseAtLingerMs;
                        if (rightLingerActive && devOverrides.RightTriggerEffect == null)
                            devOverrides.RightTriggerEffect = Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock(impR);
                        if (leftLingerActive && devOverrides.LeftTriggerEffect == null)
                            devOverrides.LeftTriggerEffect = Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock(impL);

                        // Steering at-lock AT resistance (#94, channel 4): ramp trigger
                        // resistance with how close a steering source is to lock. Gated to
                        // triggers the user hasn't configured (mode Off) with no override
                        // already set this frame, so it never fights a user AT effect or the
                        // impulse-AT buzz above. Already DS5- and test-target-scoped here.
                        // Channel 2 (trigger-vibration pulse) runs before channel 4 so the
                        // momentary at-lock buzz wins the trigger-effect slot during its
                        // hold+fade window; channel 4's continuous resistance resumes once
                        // the pulse decays to 0. Both gate on the trigger being unconfigured
                        // (mode Off) with no override already set this frame.
                        float steerVib = SteeringTriggerVibProvider?.Invoke(_padIndex) ?? 0f;
                        if (steerVib > 0f)
                        {
                            byte strength = (byte)Math.Clamp((int)(steerVib * 255f), 0, 255);
                            if (devCfg.RightTriggerMode == AdaptiveTriggerMode.Off && devOverrides.RightTriggerEffect == null)
                                devOverrides.RightTriggerEffect = Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock(strength);
                            if (devCfg.LeftTriggerMode == AdaptiveTriggerMode.Off && devOverrides.LeftTriggerEffect == null)
                                devOverrides.LeftTriggerEffect = Ds5EffectSynthesizer.BuildAtVibrationOverrideBlock(strength);
                        }

                        float steerRes = SteeringAtResistanceProvider?.Invoke(_padIndex) ?? 0f;
                        if (steerRes > 0f)
                        {
                            byte force = (byte)Math.Clamp((int)(steerRes * 255f), 0, 255);
                            if (devCfg.RightTriggerMode == AdaptiveTriggerMode.Off && devOverrides.RightTriggerEffect == null)
                                devOverrides.RightTriggerEffect = Ds5EffectSynthesizer.BuildAtResistanceOverrideBlock(force);
                            if (devCfg.LeftTriggerMode == AdaptiveTriggerMode.Off && devOverrides.LeftTriggerEffect == null)
                                devOverrides.LeftTriggerEffect = Ds5EffectSynthesizer.BuildAtResistanceOverrideBlock(force);
                        }
                    }

                    // Per-device peak scaling (each device has own
                    // AudioLightbarSensitivity).
                    float devPeak = audioPeak >= 0f
                        ? audioPeak
                        : Math.Clamp(
                            rawAudioPeak * (float)devCfg.AudioLightbarSensitivity,
                            0f, 1f);

                    // Per-device pulse colour + intensity (DrainInputPulses
                    // rolled per-device above).
                    var devState = _deviceStates.TryGetValue(ud.InstanceGuid, out var ds) ? ds : null;
                    uint devPulseColor = devState?.PulseColor ?? 0;
                    float devPulseIntensity = ComputePulseIntensity(nowMs, devCfg);

                    // Rumble enable gating — clear validFlag0 bit 0 once
                    // PadForge's own rumble has been zero for one full
                    // frame. The transition frame still asserts the enable
                    // bit with motor=0 so the firmware actually drops the
                    // motor; subsequent idle frames clear bit 0 entirely
                    // and stop touching the rumble bytes, which lets
                    // external writers (ds.daidr.me, OpenRGB, dualsensectl,
                    // game-side hidapi callers) paint rumble without
                    // PadForge's 30 Hz animation cadence clobbering it.
                    bool padForgeHasRumble = (rR | rL) != 0;
                    bool prevHadRumble = _prevHadRumble.TryGetValue(ud.InstanceGuid, out var phr) && phr;
                    bool assertRumbleEnable = padForgeHasRumble || prevHadRumble;
                    _prevHadRumble[ud.InstanceGuid] = padForgeHasRumble;

                    // AT trigger enable gating — same shape as rumble.
                    // Assert when PadForge has AT configured OR external
                    // is currently mirroring OR we need a one-shot drop
                    // frame to disengage on a cfg→Off transition. Idle
                    // (no PadForge AT, no override, no transition): don't
                    // assert, firmware retains whatever was last written
                    // (which may be an external program's AT engagement
                    // we want to preserve indefinitely).
                    bool padForgeWantsRightAt = devCfg != null && devCfg.RightTriggerMode != AdaptiveTriggerMode.Off;
                    bool padForgeWantsLeftAt  = devCfg != null && devCfg.LeftTriggerMode  != AdaptiveTriggerMode.Off;
                    bool prevPadForgeWantsRightAt = _prevPadForgeWantsRightTrig.TryGetValue(ud.InstanceGuid, out var pr) && pr;
                    bool prevPadForgeWantsLeftAt  = _prevPadForgeWantsLeftTrig.TryGetValue(ud.InstanceGuid, out var pl) && pl;

                    // Slot's reported battery percent (clamped 0..100) for
                    // the synthesizer's Battery lightbar mode lerp. Default
                    // to 100 ("full") when the provider isn't wired so a
                    // misconfigured slot doesn't paint empty-battery red.
                    byte pctByte = SlotBatteryPercentProvider?.Invoke(_padIndex, ud.InstanceGuid) ?? (byte)100;
                    bool assertRightTrig = padForgeWantsRightAt
                        || devOverrides.RightTriggerEffect != null
                        || prevPadForgeWantsRightAt;
                    bool assertLeftTrig  = padForgeWantsLeftAt
                        || devOverrides.LeftTriggerEffect != null
                        || prevPadForgeWantsLeftAt;
                    // Track whether THIS tick wrote a trigger effect for
                    // either source — PadForge cfg OR a dispatcher-injected
                    // override (external mirror, impulse-trigger → AT
                    // Vibration auto-route). The next-tick drop-frame logic
                    // needs both: when the override drops AND cfg is Off,
                    // we still owe the firmware one final write with the
                    // cfg-or-Off block so it leaves Vibration mode.
                    // Without this, the DS5 firmware latches whatever
                    // trigger effect was last asserted (Vibration with
                    // impulse-trigger amplitude) and never disengages.
                    _prevPadForgeWantsRightTrig[ud.InstanceGuid] = padForgeWantsRightAt
                        || devOverrides.RightTriggerEffect != null;
                    _prevPadForgeWantsLeftTrig[ud.InstanceGuid]  = padForgeWantsLeftAt
                        || devOverrides.LeftTriggerEffect != null;

                    try
                    {
                        // ── CRITICAL: rumble-byte contract for DS5/DS4 ──
                        //
                        // PadForge writes DS5/DS4 effect packets via raw HID
                        // at up to 30 Hz, BYPASSING SDL3 entirely. SDL3's
                        // PS5/PS4 driver also writes effect packets — for
                        // SDL_RumbleJoystick calls the SDL path carries the
                        // audio-mixed rumble bytes from
                        // ForceFeedbackState.SetDeviceForces. Two writers,
                        // same device: per SonyEffectWriter's docstring,
                        // "the firmware applies whichever WriteFile lands
                        // most recently."
                        //
                        // That means: every PadForge dispatcher write is
                        // ALSO writing rumble bytes from this packet's
                        // perspective. If the dispatcher writes 0 motor
                        // values 30 Hz between SDL's audio-rumble writes,
                        // motors pulse audio→0→audio→0 — average strength
                        // collapses (the v3.1.x audio-rumble regression).
                        //
                        // Two rules that MUST hold for audio rumble to
                        // feel right:
                        //   1. Bit 0 of validFlag0 (EnableRumbleEmulation)
                        //      stays set unconditionally on every dispatcher
                        //      packet. Clearing it ("disable compatibility
                        //      motor mode") races SDL's bit-0-set writes off
                        //      the channel.
                        //   2. The rumble bytes the dispatcher carries MUST
                        //      include audio mix (when audio rumble is
                        //      enabled) so the dispatcher reinforces SDL's
                        //      audio rumble rather than fighting it.
                        //      SlotRumbleForDeviceProvider runs
                        //      ScaleRumbleForDevice for this — it pulls raw
                        //      VibrationStates (game rumble) and mixes audio
                        //      in, yielding the same value SDL sends.
                        //
                        // For test-rumble target gating, we still zero rR/rL
                        // on non-target devices — but bit 0 stays set so the
                        // firmware applies our zero in compatibility mode
                        // (a transient zero SDL's next write can replace if
                        // it really wants to drive that device). Matches
                        // 3.1.0 behavior; does NOT compound into a steady-
                        // state motor kill.
                        var fields = isDs5
                            ? Ds5EffectSynthesizer.BuildFields(
                                devCfg, devPeak, nowMs,
                                _randomColor, devPulseColor, devPulseIntensity,
                                rR, rL, assertRumbleEnable,
                                assertRightTrig, assertLeftTrig, devOverrides, pctByte,
                                devPlayerNumber)
                            : Ds4EffectSynthesizer.BuildFields(
                                devCfg, devPeak, nowMs,
                                _randomColor, devPulseColor, devPulseIntensity,
                                rR, rL, assertRumbleEnable, devOverrides, pctByte,
                                devPlayerNumber);

                        // Macro-sound speaker routing (issue #83). The DualSense
                        // firmware sends its USB program audio to the headphone
                        // path by default and keeps the internal speaker silent.
                        // While this slot's Audio tab targets the controller's
                        // own endpoint, assert the speaker output path + volume
                        // in the same output report the lightbar rides
                        // (dualsensectl-verified: valid_flag0 0x20 speaker-volume
                        // enable + 0x80 audio-control enable; audio_flags path
                        // 3<<4 = internal speaker). Loudness is two firmware
                        // knobs, and this block is their single owner — sample
                        // amplitudes stay full-scale:
                        //   - speakerVolume: effective range is 0x3D..0x64
                        //     (dualsensectl: "the PS5 use 0x3d-0x64; trying
                        //     over 0x64 doesnt change"), so the 0-100% master
                        //     volume spans exactly that window (0 mutes).
                        //   - speaker pre-gain: audio_flags2 bits 0-2 with
                        //     valid_flag1 bit 7 (AUDIO_CONTROL2_ENABLE); value
                        //     3 per dualsensectl's reference snippet. Without
                        //     it the speaker tops out well below what the PS5
                        //     drives it to. Encoded by SonyEffectWriter's
                        //     audioControl2 poke (the HM profile doesn't
                        //     declare the byte).
                        // When routing switches away, restore the headphone
                        // path once so the speaker doesn't stay latched.
                        if (isDs5)
                        {
                            if (AudioPassthroughService.WantsSpeakerPath(ud.InstanceGuid))
                            {
                                int master = SoundMacroService.GetSlotVolume(_padIndex);
                                byte spkVol = master <= 0
                                    ? (byte)0
                                    : (byte)(0x3D + master * (0x64 - 0x3D) / 100);
                                fields["validFlag0"] = (byte)((byte)fields["validFlag0"] | 0xA0);
                                fields["validFlag1"] = (byte)((byte)fields["validFlag1"] | 0x80);
                                fields["speakerVolume"] = spkVol;
                                fields["audioControlFlags"] = (byte)(3 << 4);
                                fields["audioControl2"] = (byte)3;
                            }
                            else if (AudioPassthroughService.TryConsumeSpeakerPathCleared(ud.InstanceGuid))
                            {
                                fields["validFlag0"] = (byte)((byte)fields["validFlag0"] | 0x80);
                                fields["validFlag1"] = (byte)((byte)fields["validFlag1"] | 0x80);
                                fields["audioControlFlags"] = (byte)0;
                                fields["audioControl2"] = (byte)0;
                            }
                        }

                        SonyEffectWriter.Write(path, profile, fields);
                    }
                    catch
                    {
                        // Best-effort: a failed write on one device shouldn't
                        // prevent the dispatcher from servicing the rest of
                        // the slot's mapped Sony pads on the next tick.
                    }
                }
            }
        }
    }
}
