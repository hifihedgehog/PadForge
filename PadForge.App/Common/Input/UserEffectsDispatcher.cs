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
        // DualShock 4 family, three PIDs: the v1 (CUH-ZCT1), the v2
        // (CUH-ZCT2) and Sony's wireless USB adapter, which carries a DS4
        // over its own radio link (linux hid-ids.h PS4_CONTROLLER,
        // PS4_CONTROLLER_2 and PS4_CONTROLLER_DONGLE). Same VID, different
        // output report shape (Report 0x05 USB / 0x11 BT, no AT / no player
        // LEDs / no mic LED). Lighting tab base color + audio modes apply
        // via the DS4 path. AT and Indicator LED settings are ignored.
        private const ushort Ds4Pid_V1      = 0x05C4;
        private const ushort Ds4Pid_V2      = 0x09CC;
        private const ushort Ds4Pid_Adapter = 0x0BA0;

        // Map (PID, transport) → HM profile id. Each device's
        // extendedOutputReport spec tells HMOutputEncoder how to pack the
        // semantic fields into wire-format bytes for that transport. There
        // is no "dualshock-4-v1-bt" profile — DS4 v1 over BT uses the v2-bt
        // descriptor, which has the same effect-report layout.
        private static HMProfile ResolveSonyProfile(ushort pid, bool isBluetooth)
        {
            string id = pid switch
            {
                PidStandard    => isBluetooth ? "dualsense-bt-full" : "dualsense",
                PidEdge        => isBluetooth ? "dualsense-edge-bt" : "dualsense-edge",
                Ds4Pid_V1      => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v1",
                Ds4Pid_V2      => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v2",
                Ds4Pid_Adapter => isBluetooth ? "dualshock-4-v2-bt" : "dualshock-4-v2",
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

        /// <summary>Static provider for the slot's persona haptics alone as
        /// one device takes them (discussion #522): the haptic level through
        /// each of the device's rows' Overall Gain, motor strengths and swap,
        /// with no game rumble, macro rumble, audio rumble or any other source
        /// in it, and (0, 0) when the level is not fresh or the device plays
        /// the haptic track itself. InputService wires it to
        /// <see cref="PersonaHapticRumble.ForDevice"/>. Read only inside a
        /// game's rumble release grace, where it is the one PadForge source a
        /// DualSense gets.</summary>
        public static Func<int, Guid, (byte right, byte left)> SlotPersonaRumbleForDeviceProvider { get; set; }

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

        /// <summary>A slot's adaptive trigger effects played by macros
        /// (#531), or null. Read once per pass, after a game mod's hold.</summary>
        public static Func<int, Common.Input.MacroTriggerEffectState> MacroTriggerEffectsProvider { get; set; }

        /// <summary>A slot's key colors played by macros (#557), or null.
        /// Painted over the Lighting tab's key colors.</summary>
        public static Func<int, Common.Input.MacroKeyColorState> MacroKeyColorsProvider { get; set; }

        // Animated-lightbar polling cadence — 30Hz is enough to feel
        // responsive without flooding the BT HID write path. WriteFile
        // open+close is ~1ms per call; 30Hz = 30ms budget.
        private const int AnimTickMs = 33;

        // Audio onset threshold for AudioPulseRandom: peak rising from
        // below this to above it counts as a pulse onset and rolls a
        // new random color.
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
        // dispatcher carries random-color memory across audio onsets,
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
        // reads it from the thread pool (battery-percent change); a plain
        // Dictionary resizing during that read is undefined behavior, and two
        // overlapping (non-serialized) timer callbacks could double-insert.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DeviceState> _deviceStates = new();

        // ── Write ordering across concurrent dispatches ──────────────────
        //
        // DispatchSnapshot runs from the UI thread (ApplyOnce /
        // OnConfigChanged), the thread pool (QueueDispatch, for the polling
        // thread, which never runs it, and the other queued passes), the 33 ms
        // animation timer, and the audio callback. It is NOT serialized as a
        // whole. _animTickBusy only excludes timer-vs-timer overlap.
        //
        // While the HID write still happened inside devices.SyncRoot, that lock
        // made capture+write one unit and the last writer was necessarily the
        // last capturer. The write now runs outside that lock (a synchronous
        // ~1 kHz-blocking HID write had no business holding the poll thread's
        // lock), which re-opens the ordering: two dispatches can capture in one
        // order and land their writes in the other. The concrete loss is a
        // latched motor. A timer dispatch captures nonzero rumble, the poll
        // dispatch that stops the timer captures and writes the final zero
        // frame, then the timer's stale nonzero write lands last and no
        // further dispatch is coming to correct it.
        //
        // Fix: stamp every captured payload with a monotonic sequence INSIDE
        // devices.SyncRoot (so sequence order == capture order), then drop any
        // write whose capture is older than one already written to that device.
        // Newest capture wins regardless of thread scheduling.
        //
        // s_writeGate is a LEAF lock: taken only after devices.SyncRoot has
        // been released, and it acquires nothing while held. That is
        // deliberate. An outer "serialize the whole dispatch" lock would nest
        // above devices.SyncRoot and deadlock the moment any caller poked a
        // dispatch while holding it. It also restores the cross-dispatcher write
        // serialization that devices.SyncRoot used to provide for one physical
        // pad shared by two slots.
        private static readonly object s_writeGate = new();
        private static long s_dispatchSeq;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> s_lastWriteSeq =
            new(StringComparer.OrdinalIgnoreCase);

        // Sony pads whose audio route PadForge left off the headset, by the
        // last frame that landed on each: a DualSense's output path, or a
        // DualShock 4's USB route (#546). Static, because a pad keeps its
        // route when a slot's dispatcher is replaced. ReleaseAudioRoutes puts
        // each back on the headset when the engine stops or the outputs are
        // quiesced, and SweepOrphanedRoutes once no slot maps the pad, so
        // audio to the pad does not stay on its speaker after PadForge lets
        // go. A pad PadForge never routed is never listed.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Path, HMProfile Profile, bool Ds5)> s_routedOffHeadset = new();

        // DualSense pads whose last delivered Headphone Volume is below full,
        // by the last frame that lowered it. The register holds until a power
        // cycle, and PadForge has no way to read what it was before, so a pad
        // PadForge lets go takes the full 0x7F its default slider writes
        // (DS5_Bridge controller_output_state.cpp sets kHeadphoneVolumeMax on
        // the headphone path). duaLib's letGo leaves the register alone
        // (duaLibUtils.cpp, AllowHeadphoneVolume = false). A DualShock 4 is not
        // listed: PadForge leaves its USB headset level to Windows and its
        // Bluetooth volumes reach only PadForge's own stream.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string Path, HMProfile Profile)> s_headphoneLowered = new();

        // What a peer's effect frames left on a DualSense of this PC, an output
        // path off the headset or a Headphone Volume below full, and the peer
        // that last asserted either. The firmware holds both until something
        // writes them. The marks move to the two lists above once no connected
        // peer holds the pad (LetGoOfPeerMarks).
        private readonly record struct PeerAudioMark(string Path, HMProfile Profile, string Peer, bool OffHeadset, bool Lowered);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, PeerAudioMark> s_peerAudioMarks = new();

        // What each peer last said of a DualSense of this PC, field by field, and
        // when. A peer's frames say a field only when its own setting changes, and
        // the relay drops a body that has not changed, so when a peer leaves this
        // is the only record of what the ones still connected want. A pad shows
        // the newest word of each field (SettlePeerWords).
        internal readonly record struct PeerWord(long Order, byte Value);
        private readonly record struct PeerAudioWords(string Path, HMProfile Profile,
            PeerWord? Route, PeerWord? Control2, PeerWord? Level, PeerWord? Speaker);
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Device, string Peer), PeerAudioWords> s_peerAudioWords = new();
        private static long s_peerWordOrder;

        // Held while a frame records what it says, and while what peers said is
        // read and written to a pad, so an older word never lands over a newer
        // one. Frames that say none of these fields never take it. It is taken
        // outside s_writeGate, which stays a leaf.
        private static readonly object s_peerWordGate = new();

        /// <summary>One field's newest word on a pad, and its newest word from a
        /// peer still connected.</summary>
        internal struct NewestWord
        {
            public PeerWord? Standing, Kept;
            public string KeptBy;

            public void Note(PeerWord? word, bool keeps, string peer)
            {
                if (word is not PeerWord said) return;
                if (Standing is not PeerWord standing || said.Order > standing.Order) Standing = said;
                if (keeps && (Kept is not PeerWord kept || said.Order > kept.Order))
                {
                    Kept = said;
                    KeptBy = peer;
                }
            }

            /// <summary>What the pad shows of this field came from a peer that left.</summary>
            public readonly bool Left => Standing != null && Standing != Kept;
        }

        // Set when the routes are released and cleared at the next engine
        // start (ResumeAudioRoutes). While set, no frame routes a pad: none
        // counts PadForge as playing to it, asserts an explicit path or a
        // Headphone Volume, or sends a USB route. The dispatchers still run
        // after an abnormal exit's release.
        private static volatile bool s_audioRoutesReleased;

        // The sequence the last release took. A frame captured before it that
        // puts a pad off the headset or lowers its Headphone Volume is dropped
        // at the write, listed or not: the release saw no such pad and would
        // never put it back. One already inside its write when the release
        // gives up waiting puts its own pad back when it lands.
        private static long s_audioReleaseSeq;

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

        // Same shape again for the mic LED. Held every tick, the mic-light
        // enable bit republishes our MicLedMode over whatever an external
        // program set, 1.5 s after their grace window closes.
        private readonly Dictionary<Guid, bool> _prevPadForgeWantsMicLed = new();

        // And again for the player-indicator row. Held every tick, the
        // player-indicator enable bit republishes our pips over an
        // external writer's once their grace window closes.
        private readonly Dictionary<Guid, bool> _prevPadForgeWantsPips = new();

        // Whether the last pass wrote a game mod's hold (#529) on each
        // subsystem of each device. A game's mirrored write is left to
        // persist in firmware when it stops, but a mod's hold ends on
        // purpose (its program exited, it reset, the user cleared it), so the
        // pass owes the pad one write of PadForge's own value when a hold
        // ends. Accessed only inside devices.SyncRoot, like the four above.
        private readonly Dictionary<Guid, bool> _prevModRightTrig = new();
        private readonly Dictionary<Guid, bool> _prevModLeftTrig = new();
        private readonly Dictionary<Guid, bool> _prevModMicLed = new();
        private readonly Dictionary<Guid, bool> _prevModPips = new();
        // The same for a macro's trigger effect (#531).
        private readonly Dictionary<Guid, bool> _prevMacroRightTrig = new();
        private readonly Dictionary<Guid, bool> _prevMacroLeftTrig = new();

        /// <summary>TickCount64 of the last change to what a game mod holds
        /// on this slot (#529), 0 before any.</summary>
        private long _modChangedMs;

        /// <summary>1 while a pass for a mod change is queued.</summary>
        private int _modDispatchQueued;

        /// <summary>How long the slot's 30 Hz writer stays alive after a mod
        /// changes what it holds. A mod that sends every frame is then
        /// written at the pass's own rate, not once per packet, and the
        /// writer stops two seconds after the mod settles, the firmware
        /// holding what was written.</summary>
        internal const long ModChangeTimerMs = 2000;

        // Bounded unmute burst per device claim. Asserting AllowAudioMute
        // over the zero-filled MuteControl byte is a correction for a pad
        // an earlier owner left hardware-muted, and it has to run when we
        // first take the pad. Held forever it also reverses any deliberate
        // external mute within one dispatch frame, so it runs as a short
        // burst and then stops. 60 frames is ~2 s at the 30 Hz dispatch
        // cadence, which survives a dropped write during enumeration
        // without becoming a standing order. Re-arms on a new InstanceGuid
        // and on a fresh ownership claim, matching duaLib's
        // one-shot-at-letGo usage (duaLib.cpp:180).
        private const int AudioMuteBurstFrames = 60;
        private readonly Dictionary<Guid, int> _audioMuteBurstLeft = new();

        // Last headphone volume written per device, duaLib-style change
        // gating (duaLib.cpp:617 asserts AllowHeadphoneVolume on change
        // or reconnect and clears it otherwise). The VALUE ships every
        // packet; only the enable bit is gated on this.
        private readonly Dictionary<Guid, int> _prevHeadphoneVolume = new();

        // Last output path written per device, same duaLib change gating
        // as the headphone volume (duaLib.cpp:597).
        private readonly Dictionary<Guid, int> _prevAudioOutputPath = new();

        // Whether each Bluetooth DualShock 4's headset microphone was armed
        // at its last packet (#528). Arming asserts the microphone gain
        // once, the same change gating as the headphone volume.
        private readonly Dictionary<Guid, bool> _prevDs4MicArmed = new();

        // The route each DualShock 4 on USB last took through feature report
        // 0xE0 (#546, Ds4UsbAudio.RouteToWrite). Absent until PadForge writes
        // one, so a pad PadForge never routed is left alone.
        private readonly Dictionary<Guid, int> _prevDs4UsbRoute = new();

        // When a USB route the pad refused goes out again, per device. Rumble
        // and lights landed, so the frame is delivered and nothing re-arms, and
        // the route alone waits the time XboxImpulseHidWriter.FailedRetryCooldownMs
        // waits. A pad that keeps refusing gets one frame and one try a second.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _usbRouteRetryAt = new();
        internal const long UsbRouteRetryMs = 1000;

        private bool UsbRouteRetryDue(Guid device, long nowMs)
        {
            if (!_usbRouteRetryAt.TryGetValue(device, out long at)) return true;
            if (nowMs < at) return false;
            _usbRouteRetryAt.TryRemove(device, out _);
            return true;
        }

        private bool AnyUsbRouteRetryDue(long nowMs)
        {
            if (_usbRouteRetryAt.IsEmpty) return false;
            foreach (var entry in _usbRouteRetryAt)
                if (nowMs >= entry.Value) return true;
            return false;
        }

        // The four maps above hold what the pad has RECEIVED. A frame
        // commits its values only after PlayStationEffectWriter.Write accepts
        // it (CommitDeliveredAudio), the delivery contract SpeakerCleared and
        // BtBarReleased follow, so a frame the stale-skip drops or the writer
        // rejects leaves its asserts owed to the next capture. The commit runs
        // after s_writeGate is released, outside devices.SyncRoot, so the maps
        // take their own leaf lock. _audioCommitSeq holds, per device, the
        // dispatch sequence of the last commit or re-arm
        // (RearmAudioAsserts): a frame captured before it commits nothing, so
        // a late commit can neither undo a re-arm nor a newer commit.
        private readonly object _audioPrevGate = new();
        private readonly Dictionary<Guid, long> _audioCommitSeq = new();

        private void RearmAudioAsserts(Guid device)
        {
            lock (_audioPrevGate)
            {
                _prevHeadphoneVolume.Remove(device);
                _prevAudioOutputPath.Remove(device);
                _prevDs4MicArmed.Remove(device);
                _prevDs4UsbRoute.Remove(device);
                _audioCommitSeq[device] = System.Threading.Interlocked.Read(ref s_dispatchSeq);
            }
            _usbRouteRetryAt.TryRemove(device, out _);
        }

        private void CommitDeliveredAudio(Guid device, long seq,
            (int HeadphoneVolume, int OutputPath, bool Ds4MicArmed, int Ds4UsbRoute) audio)
        {
            lock (_audioPrevGate)
            {
                if (_audioCommitSeq.TryGetValue(device, out long last) && last >= seq) return;
                _audioCommitSeq[device] = seq;
                _prevHeadphoneVolume[device] = audio.HeadphoneVolume;
                _prevAudioOutputPath[device] = audio.OutputPath;
                _prevDs4MicArmed[device] = audio.Ds4MicArmed;
                // A frame with no route leaves the last one standing.
                if (audio.Ds4UsbRoute >= 0) _prevDs4UsbRoute[device] = audio.Ds4UsbRoute;
            }
        }

        /// <summary>Whether PadForge plays to the pad's speaker now: a sink
        /// wants the speaker path and the routes have not been released.</summary>
        private static bool PlaysToSpeaker(Guid device)
            => !s_audioRoutesReleased && AudioPassthroughService.WantsSpeakerPath(device);

        /// <summary>The route a frame leaves its pad on, 0 being the headset:
        /// the DualShock 4 USB route it sends, or the DualSense output path it
        /// asserts (valid_flag0 bit 7, path in bits 4-5 of the audio control
        /// byte). -1 when the frame leaves the route alone.</summary>
        internal static int DeliveredRoute(IReadOnlyDictionary<string, object> fields, out bool ds5)
        {
            ds5 = false;
            if (fields.TryGetValue("ds4UsbRoute", out var usb) && usb is byte route) return route;
            if (fields.TryGetValue("audioControlFlags", out var control) && control is byte flags
                && fields.TryGetValue("validFlag0", out var valid) && valid is byte flag0
                && (flag0 & 0x80) != 0)
            {
                ds5 = true;
                return (flags >> 4) & 0x03;
            }
            return -1;
        }

        /// <summary>Lists or clears a pad in <see cref="s_routedOffHeadset"/>
        /// from a frame that landed. A peer's owner routes its own pad.</summary>
        private static void NoteDeliveredRoute(Guid device, string path, HMProfile profile,
            IReadOnlyDictionary<string, object> fields)
        {
            int route = DeliveredRoute(fields, out bool ds5);
            if (route < 0 || RemoteLinkOutputRouter.IsPeerPath(path)) return;
            if (route == 0) s_routedOffHeadset.TryRemove(device, out _);
            else s_routedOffHeadset[device] = (path, profile, ds5);
        }

        /// <summary>Whether a frame asserts a DualSense Headphone Volume below
        /// full (valid_flag0 bit 4).</summary>
        internal static bool LowersHeadphones(IReadOnlyDictionary<string, object> fields, out bool asserted)
        {
            asserted = fields.TryGetValue("headphoneVolume", out var level) && level is byte
                && fields.TryGetValue("validFlag0", out var valid) && valid is byte flag0
                && (flag0 & 0x10) != 0;
            return asserted && (byte)level < Ds5EffectSynthesizer.HeadphoneVolumeFull;
        }

        /// <summary>Lists or clears a pad in <see cref="s_headphoneLowered"/>
        /// from a frame that landed.</summary>
        private static void NoteDeliveredHeadphoneVolume(Guid device, string path, HMProfile profile,
            IReadOnlyDictionary<string, object> fields)
        {
            bool lowered = LowersHeadphones(fields, out bool asserted);
            if (!asserted || RemoteLinkOutputRouter.IsPeerPath(path)) return;
            if (lowered) s_headphoneLowered[device] = (path, profile);
            else s_headphoneLowered.TryRemove(device, out _);
        }

        /// <summary>Owner side: reads what a peer's DualSense effect body
        /// asserts: valid_flag0 bit 7 with the path in bits 4-5 of byte 7,
        /// valid_flag0 bit 4 with the Headphone Volume in byte 4, valid_flag0 bit 5
        /// with the Speaker Volume in byte 5, and valid_flag1 bit 7 with the
        /// pre-gain in byte 37. What it leaves alone stays as the last frame set
        /// it.</summary>
        internal static void NotePeerEffectBody(Guid device, string path, ushort productId, byte[] body, string peer)
        {
            if (productId is not (PidStandard or PidEdge) || body == null || body.Length < 8) return;
            bool assertsPath = (body[0] & 0x80) != 0, assertsLevel = (body[0] & 0x10) != 0;
            bool assertsSpeaker = (body[0] & 0x20) != 0, assertsControl2 = (body[1] & 0x80) != 0 && body.Length > 37;
            if (!assertsPath && !assertsLevel && !assertsSpeaker && !assertsControl2) return;
            lock (s_peerWordGate)
            {
                s_peerAudioWords.TryGetValue((device, peer), out var said);
                var profile = said.Path == path ? said.Profile
                    : ResolveSonyProfile(productId, PlayStationEffectWriter.IsBluetoothPath(path));
                s_peerAudioWords[(device, peer)] = new PeerAudioWords(path, profile,
                    assertsPath ? new PeerWord(++s_peerWordOrder, body[7]) : said.Route,
                    assertsControl2 ? new PeerWord(++s_peerWordOrder, body[37]) : said.Control2,
                    assertsLevel ? new PeerWord(++s_peerWordOrder, body[4]) : said.Level,
                    assertsSpeaker ? new PeerWord(++s_peerWordOrder, body[5]) : said.Speaker);
                if (!assertsPath && !assertsLevel) return;
                s_peerAudioMarks.TryGetValue(device, out var mark);
                bool offHeadset = assertsPath ? ((body[7] >> 4) & 0x03) != 0 : mark.OffHeadset;
                bool lowered = assertsLevel ? body[4] < Ds5EffectSynthesizer.HeadphoneVolumeFull : mark.Lowered;
                if (!offHeadset && !lowered) s_peerAudioMarks.TryRemove(device, out _);
                else s_peerAudioMarks[device] = new PeerAudioMark(path, profile, peer, offHeadset, lowered);
            }
        }

        /// <summary>A peer's last session dropped. Runs after
        /// <see cref="RemoteLinkOutputRouter.ReleasePeer"/>, on the thread pool,
        /// since the link server calls it and the pads take a write.</summary>
        internal static void NotePeerDropped(string peer)
        {
            if (s_peerAudioMarks.IsEmpty && s_peerAudioWords.IsEmpty) return;
            System.Threading.ThreadPool.QueueUserWorkItem(static _ =>
            {
                try { LetGoOfPeerMarks(); } catch { }
            });
        }

        /// <summary>A peer's frame has reached the pad. Its last session can
        /// have dropped after the frame's claim, with the let-go already gone and
        /// finding nothing, or having taken this frame's word with it. So what the
        /// frame said for a peer that no longer holds the pad is noted again, and
        /// takes the let-go now. It runs after the frame, so what the pad goes back
        /// to lands last.</summary>
        internal static void NotePeerFrameApplied(Guid device, string path, ushort productId, byte[] body, string peer)
        {
            if (RemoteLinkOutputRouter.PeerKeepsDevice(null, peer)) return;
            NotePeerEffectBody(device, path, productId, body, peer);
            if (!s_peerAudioWords.ContainsKey((device, peer))) return;
            System.Threading.ThreadPool.QueueUserWorkItem(static _ =>
            {
                try { LetGoOfPeerMarks(); } catch { }
            });
        }

        /// <summary>This PC stopped sharing a pad, so no peer can hold it.</summary>
        internal static void NoteDeviceUnshared(Guid device)
        {
            foreach (var said in s_peerAudioWords.Keys)
                if (said.Device == device) s_peerAudioWords.TryRemove(said, out _);
            if (!s_peerAudioMarks.ContainsKey(device)) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LetGoOfPeerMarks(device); } catch { }
            });
        }

        /// <summary>Settles the pads a peer that left said something of
        /// (<see cref="SettlePeerWords"/>), or hands what peers left on
        /// <paramref name="unshared"/> to the lists this PC's own routes use. After
        /// that the two usual writers take over: a slot that holds the pad
        /// forgets what it wrote and asserts its own path and level again, an
        /// Automatic slot gives the headset back to a listed pad, and with no
        /// slot <see cref="SweepOrphanedRoutes"/> writes the headset path and
        /// full volume. A remote-fed sink still draining holds the pad until the
        /// worker takes it down and sweeps.</summary>
        internal static void LetGoOfPeerMarks(Guid? unshared = null)
        {
            List<Guid> moved = null;
            if (unshared is Guid only)
            {
                if (s_peerAudioMarks.TryGetValue(only, out var mark))
                {
                    MovePeerMark(only, mark);
                    moved = new List<Guid> { only };
                }
            }
            else
            {
                var pads = new HashSet<Guid>(s_peerAudioMarks.Keys);
                foreach (var said in s_peerAudioWords.Keys) pads.Add(said.Device);
                foreach (var pad in pads)
                    if (SettlePeerWords(pad)) (moved ??= new List<Guid>()).Add(pad);
            }
            if (moved == null) return;
            foreach (var dispatcher in _instances.Values)
            {
                if (dispatcher == null) continue;
                foreach (var device in moved) dispatcher.RearmAudioAsserts(device);
                dispatcher.QueueDispatch();
            }
            SweepOrphanedRoutes();
        }

        /// <summary>Settles one pad once a peer has left. A field whose newest word
        /// came from a peer still connected stays as it is. A field whose newest word
        /// came from one that left goes to the newest word of those connected, or
        /// where it starts when none has said it: the headset, full volume, no
        /// pre-gain. That goes past the lease, since a connected peer's frames will
        /// not say it again and the lease is theirs. With no connected peer holding
        /// the pad, or a slot of this PC writing to it with the lease lapsed, the usual
        /// let-go takes over instead. True when the pad's slots owe an assert: what
        /// they last wrote is no longer what it shows.</summary>
        private static bool SettlePeerWords(Guid device)
        {
            // Ahead of the gate, which is taken outside the write gate alone.
            var settings = SettingsManager.UserSettings;
            bool slotHolds = settings != null && ASlotHolds(settings, device);
            lock (s_peerWordGate)
            {
                NewestWord route = default, control2 = default, level = default, speaker = default;
                string path = null;
                HMProfile profile = null;
                List<(Guid, string)> gone = null;
                foreach (var entry in s_peerAudioWords)
                {
                    if (entry.Key.Device != device) continue;
                    bool keeps = RemoteLinkOutputRouter.PeerKeepsDevice(null, entry.Key.Peer);
                    if (!keeps) (gone ??= new List<(Guid, string)>()).Add(entry.Key);
                    path = entry.Value.Path;
                    profile = entry.Value.Profile;
                    route.Note(entry.Value.Route, keeps, entry.Key.Peer);
                    control2.Note(entry.Value.Control2, keeps, entry.Key.Peer);
                    level.Note(entry.Value.Level, keeps, entry.Key.Peer);
                    speaker.Note(entry.Value.Speaker, keeps, entry.Key.Peer);
                }
                // What a peer that left said is settled here, and it says nothing
                // to a session it opens later.
                if (gone != null)
                    foreach (var key in gone) s_peerAudioWords.TryRemove(key, out _);

                bool hadMark = s_peerAudioMarks.TryGetValue(device, out var mark);
                // A frame past its guard can mark a pad after the release cleared
                // the words, and only the mark says what it left.
                if (path == null)
                {
                    if (!hadMark || RemoteLinkOutputRouter.PeerKeepsDevice(mark.Path, mark.Peer)) return false;
                    MovePeerMark(device, mark);
                    return true;
                }
                if (!route.Left && !control2.Left && !level.Left && !speaker.Left) return false;
                bool peerHolds = route.Kept != null || control2.Kept != null || level.Kept != null || speaker.Kept != null
                    || RemoteLinkOutputRouter.PeerKeepsDevice(path, null);
                if (!peerHolds || (slotHolds && !RemoteLinkOutputRouter.IsClaimedByPeer(path)))
                {
                    if (!peerHolds) AudioPassthroughService.ForgetRemotePath(device);
                    if (hadMark) MovePeerMark(device, mark);
                    return hadMark;
                }

                byte? writeRoute = route.Left ? route.Kept?.Value ?? 0 : null;
                byte? writeLevel = level.Left ? level.Kept?.Value ?? Ds5EffectSynthesizer.HeadphoneVolumeFull : null;
                // The headset frame's pre-gain, when nobody has said one.
                byte? writeControl2 = control2.Left ? control2.Kept?.Value ?? 0
                    : route.Left && route.Kept == null && control2.Standing == null ? (byte)0 : null;
                // A Speaker Volume nobody said any more stays: only a speaker reads it.
                byte? writeSpeaker = speaker.Left ? speaker.Kept?.Value : null;
                bool offHeadset = (((route.Kept?.Value ?? 0) >> 4) & 0x03) != 0;
                bool lowered = (level.Kept?.Value ?? Ds5EffectSynthesizer.HeadphoneVolumeFull) < Ds5EffectSynthesizer.HeadphoneVolumeFull;
                bool written = false;
                lock (s_writeGate)
                {
                    // The release has every pad from here on (WriteRemoteFedRoute).
                    if (s_audioRoutesReleased) return false;
                    try
                    {
                        written = PlayStationEffectWriter.WritePastTheLease(path, profile,
                            Ds5EffectSynthesizer.PeerAudioFields(writeRoute, writeControl2, writeLevel, writeSpeaker));
                    }
                    catch { }
                    // The release ran during the write and could not wait for it.
                    if (written && s_audioRoutesReleased)
                    {
                        if (offHeadset) s_routedOffHeadset[device] = (path, profile, true);
                        if (lowered) s_headphoneLowered[device] = (path, profile);
                        LetGoOfEveryListedPad();
                        return false;
                    }
                }
                if (!written)
                {
                    if (hadMark) MovePeerMark(device, mark);
                    return hadMark;
                }
                if (offHeadset || lowered)
                    s_peerAudioMarks[device] = new PeerAudioMark(path, profile, offHeadset ? route.KeptBy : level.KeptBy, offHeadset, lowered);
                else s_peerAudioMarks.TryRemove(device, out _);
                if (route.Left)
                {
                    if (route.Kept is PeerWord named) AudioPassthroughService.NoteRemotePath(device, named.Value);
                    else AudioPassthroughService.ForgetRemotePath(device);
                }
                return true;
            }
        }

        private static void MovePeerMark(Guid device, PeerAudioMark mark)
        {
            s_peerAudioMarks.TryRemove(device, out _);
            s_orphanReleaseTried.TryRemove(device, out _);
            AudioPassthroughService.ForgetRemotePath(device);
            if (mark.OffHeadset) s_routedOffHeadset[device] = (mark.Path, mark.Profile, true);
            if (mark.Lowered) s_headphoneLowered[device] = (mark.Path, mark.Profile);
        }

        /// <summary>Puts one pad back on its headset and at full Headphone
        /// Volume. Called under s_writeGate. True when nothing is left to put
        /// back.</summary>
        private static bool LetGoOfPad(Guid device)
        {
            bool done = true;
            if (s_routedOffHeadset.TryGetValue(device, out var route))
            {
                if (WriteHeadsetRoute(route)) s_routedOffHeadset.TryRemove(device, out _);
                else done = false;
            }
            if (s_headphoneLowered.TryGetValue(device, out var level))
            {
                bool written = false;
                try { written = PlayStationEffectWriter.Write(level.Path, level.Profile, Ds5EffectSynthesizer.FullHeadphoneVolumeFields()); }
                catch { }
                if (written) s_headphoneLowered.TryRemove(device, out _);
                else done = false;
            }
            return done;
        }

        /// <summary>Puts every pad PadForge left off the headset back on it,
        /// and every DualSense it left at a lowered Headphone Volume back at
        /// full. duaLib releases a pad when it closes too (duaLibUtils.cpp
        /// letGo, from scePadClose and scePadTerminate), but it turns a
        /// DualShock 4's route off (AudioOutput::Disabled), which would leave
        /// Windows audio to the pad nowhere to play, and leaves a DualSense's
        /// audio path where it was. Here a DualSense takes
        /// <see cref="Ds5EffectSynthesizer.HeadsetPathFields"/> and a
        /// DualShock 4 takes USB route 0. Runs when the engine stops and when
        /// the outputs are quiesced on an abnormal exit, and waits at most
        /// 250 ms for a write in progress.</summary>
        internal static void ReleaseAudioRoutes()
        {
            // Under the capture lock, so every frame captured before this
            // point carries an older sequence and every later one sees the
            // release. With no device collection nothing is captured.
            long seq;
            lock (SettingsManager.UserDevices?.SyncRoot ?? new object())
            {
                s_audioRoutesReleased = true;
                seq = System.Threading.Interlocked.Increment(ref s_dispatchSeq);
                System.Threading.Interlocked.Exchange(ref s_audioReleaseSeq, seq);
            }
            // No peer holds a pad past this PC's exit, and its lease guards a
            // DualSense's frames, so it goes with the marks.
            foreach (var mark in s_peerAudioMarks)
            {
                RemoteLinkOutputRouter.ReleaseDevice(mark.Value.Path);
                MovePeerMark(mark.Key, mark.Value);
            }
            // A peer whose frames asserted nothing has no mark and can still
            // hold the lease on a pad this PC's frames listed.
            foreach (var entry in s_routedOffHeadset) RemoteLinkOutputRouter.ReleaseDevice(entry.Value.Path);
            foreach (var entry in s_headphoneLowered) RemoteLinkOutputRouter.ReleaseDevice(entry.Value.Path);
            s_peerAudioWords.Clear();
            LetGoOfEveryPadAtRelease(seq);
        }

        /// <summary>The release's writes, under the gate. A write that holds
        /// the gate past the wait can land and let go itself, or fail, and the
        /// frames after a release route nothing, so the release is tried again
        /// until the gate comes free. The engine starting again ends it: the
        /// pads are that run's to route.</summary>
        private static void LetGoOfEveryPadAtRelease(long seq)
        {
            bool gated = false;
            try
            {
                System.Threading.Monitor.TryEnter(s_writeGate, 250, ref gated);
                if (!gated)
                {
                    s_releaseRetry.Change(ReleaseRetryMs, System.Threading.Timeout.Infinite);
                    return;
                }
                if (!s_audioRoutesReleased) return;
                // Read under the gate: a frame inside its write lists its pad
                // before it lets go of the gate. A write that outlasts the wait
                // puts its own pad back when it lands.
                if (s_routedOffHeadset.IsEmpty && s_headphoneLowered.IsEmpty) return;
                // A frame captured before the release that lands after it is
                // dropped as stale.
                foreach (var entry in s_routedOffHeadset)
                    s_lastWriteSeq.AddOrUpdate(entry.Value.Path, seq, (_, last) => Math.Max(last, seq));
                foreach (var entry in s_headphoneLowered)
                    s_lastWriteSeq.AddOrUpdate(entry.Value.Path, seq, (_, last) => Math.Max(last, seq));
                LetGoOfEveryListedPad();
            }
            finally
            {
                if (gated) System.Threading.Monitor.Exit(s_writeGate);
            }
        }

        /// <summary>Puts every pad on the two lists back. Called under
        /// s_writeGate.</summary>
        private static void LetGoOfEveryListedPad()
        {
            foreach (var device in s_routedOffHeadset.Keys.Union(s_headphoneLowered.Keys))
                LetGoOfPad(device);
        }

        /// <summary>Lets frames route a pad again. The engine calls it when it
        /// starts.</summary>
        internal static void ResumeAudioRoutes() => s_audioRoutesReleased = false;

        /// <summary>Puts one pad back on its headset: a DualSense takes
        /// <see cref="Ds5EffectSynthesizer.HeadsetPathFields"/>, a DualShock 4
        /// USB route 0. Called under s_writeGate.</summary>
        private static bool WriteHeadsetRoute((string Path, HMProfile Profile, bool Ds5) target)
        {
            try
            {
                if (target.Ds5)
                    return PlayStationEffectWriter.Write(target.Path, target.Profile, Ds5EffectSynthesizer.HeadsetPathFields());
                // A peer's output lease guards the effect reports. A peer's
                // frames carry no route, so a DualShock 4's USB route is the
                // owner's alone.
                return PlayStationEffectWriter.SendDs4UsbRoute(target.Path, Ds4UsbAudio.RouteStereoHeadset);
            }
            catch { return false; }
        }

        /// <summary>Owner side: routes a DualShock 4 on USB that plays a peer's
        /// sound (#138, #546). The consumer's frames cannot: 0xE0 is a feature
        /// report, and SDL's PS4 driver sets the valid flags, so no effect body
        /// turns the speaker's volume on. The audio worker calls it past the
        /// peer's output lease, which guards only the effect reports. A route
        /// off the headset is listed for <see cref="ReleaseAudioRoutes"/>. The
        /// slots that map the pad forget what they wrote, so each one writes
        /// its own route again when the lease lets it, and the headset coming
        /// back owes them a pass.</summary>
        internal static bool WriteRemoteFedRoute(Guid device, string path, byte route)
        {
            lock (s_writeGate)
            {
                if (s_audioRoutesReleased) return false;
                try { if (!PlayStationEffectWriter.WriteDs4UsbRouteAlone(path, route)) return false; }
                catch { return false; }
                if (route == Ds4UsbAudio.RouteStereoHeadset) s_routedOffHeadset.TryRemove(device, out _);
                else s_routedOffHeadset[device] = (path, null, false);
                // The release ran during the write and could not wait for it.
                if (s_audioRoutesReleased) LetGoOfEveryListedPad();
            }
            foreach (var dispatcher in _instances.Values)
            {
                if (dispatcher == null) continue;
                dispatcher.RearmAudioAsserts(device);
                if (route == Ds4UsbAudio.RouteStereoHeadset) dispatcher.QueueDispatch();
            }
            return true;
        }

        // Pads whose release after leaving every slot failed, so a pad that
        // stops answering is not written on every frame. Cleared when a slot
        // maps the pad again, and the engine stop tries it once more. A peer's
        // lease refusing the write says nothing about the pad, so that pad is
        // not listed here.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> s_orphanReleaseTried = new();

        // Goes off after a release that could not wait for the gate.
        internal const int ReleaseRetryMs = 500;
        private static readonly System.Threading.Timer s_releaseRetry = new(static _ =>
        {
            try { LetGoOfEveryPadAtRelease(System.Threading.Interlocked.Read(ref s_audioReleaseSeq)); } catch { }
        });

        // Goes off a second after the last sweep a peer's lease held back. No
        // dispatcher may be left to sweep when the lease lapses, and a quiet
        // peer that stays connected sends no event when it does.
        private const int LeaseRetryMs = 1000;
        private static readonly System.Threading.Timer s_leaseRetry = new(static _ =>
        {
            try { SweepOrphanedRoutes(); } catch { }
        });

        /// <summary>Lets go of a pad PadForge left off its headset or at a
        /// lowered Headphone Volume (<see cref="LetGoOfPad"/>) once no slot
        /// holds it: unassigned, its slot deleted or switched off, or dropped
        /// by a profile (#546). A slot switched off takes its dispatcher with
        /// it and keeps its row, so a row counts while its slot has a
        /// dispatcher or is switched on, the second for the moment between two
        /// dispatchers. A sink playing to the pad holds it too, since the
        /// headset back would move its sound, and taking the sink down sweeps
        /// again. The decision and its sequence are taken under the capture
        /// lock, so a slot that maps the pad afterwards captures a newer frame,
        /// and that frame wins. An offline pad waits until it is back. A peer's
        /// lease refuses a DualSense's frames, so that pad waits for the lease
        /// and a timer sweeps again. A connected peer's mark holds the pad until
        /// <see cref="LetGoOfPeerMarks"/> moves it. Runs on every dispatch, on
        /// the thread pool when a dispatcher is disposed, since a deleted slot
        /// takes its dispatcher with it, and on the audio worker when it takes a
        /// sink down.</summary>
        internal static void SweepOrphanedRoutes()
        {
            if (s_routedOffHeadset.IsEmpty && s_headphoneLowered.IsEmpty) return;
            var devices = SettingsManager.UserDevices;
            var settings = SettingsManager.UserSettings;
            if (devices == null || settings == null) return;
            List<Guid> orphans = null;
            long seq = 0;
            // Devices before settings, the canonical order.
            lock (devices.SyncRoot)
            {
                foreach (var device in s_routedOffHeadset.Keys) NoteIfOrphaned(device);
                foreach (var device in s_headphoneLowered.Keys)
                    if (!s_routedOffHeadset.ContainsKey(device)) NoteIfOrphaned(device);
                if (orphans != null) seq = System.Threading.Interlocked.Increment(ref s_dispatchSeq);
            }
            if (orphans == null) return;
            bool leaseHeld = false;
            lock (s_writeGate)
            {
                foreach (var device in orphans)
                {
                    bool listed = s_routedOffHeadset.TryGetValue(device, out var route);
                    bool lowered = s_headphoneLowered.TryGetValue(device, out var level);
                    string path = listed ? route.Path : lowered ? level.Path : null;
                    if (path == null) continue;
                    // A DualShock 4's route passes the lease (WriteHeadsetRoute).
                    if (((listed && route.Ds5) || lowered) && RemoteLinkOutputRouter.IsClaimedByPeer(path))
                    {
                        leaseHeld = true;
                        continue;
                    }
                    // A slot that took the pad since wrote a newer frame.
                    if (s_lastWriteSeq.TryGetValue(path, out long last) && last > seq) continue;
                    s_lastWriteSeq[path] = seq;
                    if (LetGoOfPad(device)) continue;
                    // A claim that came in since the check above refused it.
                    if (RemoteLinkOutputRouter.IsClaimedByPeer(path)) leaseHeld = true;
                    else s_orphanReleaseTried[device] = 0;
                }
            }
            if (leaseHeld) s_leaseRetry.Change(LeaseRetryMs, System.Threading.Timeout.Infinite);

            void NoteIfOrphaned(Guid device)
            {
                if (ASlotHolds(settings, device))
                {
                    s_orphanReleaseTried.TryRemove(device, out _);
                    return;
                }
                if (s_orphanReleaseTried.ContainsKey(device)) return;
                if (s_peerAudioMarks.ContainsKey(device)) return;
                if (AudioPassthroughService.WantsSpeakerPath(device)) return;
                foreach (var ud in devices.Items)
                {
                    if (ud == null || ud.InstanceGuid != device || !ud.IsOnline) continue;
                    (orphans ??= new List<Guid>()).Add(device);
                    return;
                }
            }
        }

        /// <summary>Whether a row maps the pad to a slot with a dispatcher, or
        /// to one switched on, which has or is about to have one.</summary>
        private static bool ASlotHolds(SettingsCollection settings, Guid device)
        {
            var created = SettingsManager.SlotCreated;
            var enabled = SettingsManager.SlotEnabled;
            lock (settings.SyncRoot)
            {
                foreach (var row in settings.Items)
                {
                    if (row == null || row.InstanceGuid != device || row.MapTo < 0) continue;
                    int slot = row.MapTo;
                    if (_instances.ContainsKey(slot)) return true;
                    if (created != null && enabled != null && slot < created.Length && slot < enabled.Length
                        && created[slot] && enabled[slot]) return true;
                }
            }
            return false;
        }

        // Devices seen offline since their last dispatch, so the return
        // re-arms every change-gated authority write (the duaLib
        // wasDisconnected pattern). Accessed only inside devices.SyncRoot
        // during DispatchSnapshot, like the prev maps above other than the
        // three audio maps, which have their own gate.
        private readonly HashSet<Guid> _deviceWasOffline = new();

        // Devices this dispatcher wrote on its previous dispatch as their
        // OWNER (single-writer ownership, owner facts 2026-07-20). On the
        // first owned dispatch of a device the prev-state maps above are
        // seeded TRUE so the enable/drop-frame machinery emits the
        // mandatory stop/disengage frame even when the current frame is
        // zero: a prior owner (or a dead prior process) may have left
        // rumble or an AT effect latched in firmware. Accessed only inside
        // devices.SyncRoot during DispatchSnapshot, like _prevHadRumble.
        private readonly HashSet<Guid> _ownedLastDispatch = new();

        // Union bitmask of slots sharing any of this slot's assigned Sony
        // devices (self bit always set). Refreshed by DispatchSnapshot from
        // the same settings walk that resolves ownership; read by the
        // audio-mode early-exit so a steady peak cannot starve rumble
        // onset/stop originating on a sharing slot. One tick of staleness
        // delays a merged edge by at most 33 ms.
        private volatile int _groupSlotsMask;

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

        private static long _lbFieldsLastLog;
        private static string _lbLastSig;

        private struct ExternalSubsystemState
        {
            public long RumbleTick;
            public byte RumbleRight;
            public byte RumbleLeft;
            /// <summary>The writer's own rumble mode bits: valid_flag0
            /// bits 0 and 1, and valid_flag2 bit 2. Mirrored with the motor
            /// bytes so PadForge's pass keeps the pad in the writer's mode
            /// instead of its own (#434).</summary>
            public byte RumbleFlag0Bits;
            public byte RumbleFlag2Bits;

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

            // validFlag2-gated subsystems. PadForge composes that byte per
            // bit rather than writing a flat 0xFF, which is what made an
            // external brightness impossible to hold. Each bit an external
            // writer asserts captures its own byte, so the writer's intent
            // (no-fade against forced-fade lightbar setup, dim, medium or
            // bright player LED) survives PadForge's animation cadence, and a
            // writer claiming only rumble claims neither of these.
            public long LightbarSetupTick;
            public byte LightbarSetup;

            public long LedBrightnessTick;
            public byte LedBrightness;
        }

        /// <summary>Decides whether this dispatch asserts a mirrored
        /// subsystem's enable bit, and what the per-device "PadForge wanted
        /// this last frame" flag becomes.
        ///
        /// <para>Assert when PadForge is authoring the subsystem, when an
        /// external writer is currently being mirrored (their bytes need
        /// the gate open to land), or for one transition frame after
        /// PadForge stopped wanting it (so a user's Solid-to-Off or
        /// AT-to-Off change actually reaches the firmware instead of
        /// leaving the previous state latched).</para>
        ///
        /// <para>NextPrev tracks PadForge's OWN intent and nothing else.
        /// Folding <paramref name="externalMirroring"/> into it makes the
        /// frame after their grace window expires look like a PadForge
        /// transition, which fires a "disengage" packet carrying OUR value
        /// over the one they just set. That defeats the entire point of the
        /// mirror: every one of these subsystems is supposed to let an
        /// external writer's state persist in firmware once we stand down.
        /// Cost the mic LED and the adaptive triggers exactly that,
        /// reported on hardware 2026-08-01.</para></summary>
        internal static (bool Assert, bool NextPrev) GateMirroredSubsystem(
            bool padForgeWants, bool externalMirroring, bool prevPadForgeWanted)
            => (padForgeWants || externalMirroring || prevPadForgeWanted, padForgeWants);

        /// <summary>Whether this dispatch asserts the lightbar enable bit for
        /// <paramref name="deviceGuid"/>, or stands down and lets the
        /// pass-through own the bar.
        ///
        /// <para>Extracted so the DECISION is testable, not just the two
        /// predicates behind it. #334 was not a wrong predicate, it was the
        /// wrong QUESTION at this call site: it asked whether the lane was
        /// busy (<c>IsHoldingState</c>, true for any forwarded effect
        /// payload) instead of whether the lane had the BAR. Tests over the
        /// helpers alone stayed green through that swap, so the mutation
        /// that reproduces the regression has to be able to reach this.</para></summary>
        internal static bool ShouldAssertLightbar(
            int padIndex, Guid deviceGuid, bool gameDrivenBar, bool isDs5)
            => !((gameDrivenBar
                    || DualSensePassthroughDispatcher.IsHoldingLightbar(padIndex))
                && isDs5
                && DualSensePassthroughDispatcher.IsPassthroughTarget(padIndex, deviceGuid));

        /// <summary>One device's overrides with a game mod's hold (#529)
        /// filled in, and which subsystems the mod's values took.</summary>
        internal readonly struct ModFill
        {
            public readonly ExternalSubsystemOverrides Overrides;
            public readonly bool RightTrigger;
            public readonly bool LeftTrigger;
            public readonly bool MuteLight;
            public readonly bool PlayerLights;

            public ModFill(ExternalSubsystemOverrides overrides,
                bool rightTrigger, bool leftTrigger, bool muteLight, bool playerLights)
            {
                Overrides = overrides;
                RightTrigger = rightTrigger;
                LeftTrigger = leftTrigger;
                MuteLight = muteLight;
                PlayerLights = playerLights;
            }
        }

        /// <summary>A mod's held lightbar as 0x00RRGGBB, the form the lighting
        /// rows take a game's color in, or null.</summary>
        internal static int? ModColor(GameModEffects.Hold hold)
        {
            var c = hold?.Lightbar;
            if (c == null || c.Length < 3) return null;
            return (c[0] << 16) | (c[1] << 8) | c[2];
        }

        /// <summary>Places a game mod's held values (#529) in every subsystem
        /// a game is not writing within its grace window: a game's own write
        /// wins, and the mod's hold applies otherwise. The triggers, the mute
        /// light and the player row are the DualSense's, and the lightbar
        /// is every Sony pad's. The mod's lightbar is not counted as a game's
        /// (<c>LightbarEverExternal</c> stays as the game left it), so when
        /// the hold ends PadForge's own color returns.</summary>
        internal static ModFill FillFromModHold(ExternalSubsystemOverrides overrides,
            GameModEffects.Hold hold, bool isDs5)
        {
            bool right = false, left = false, mute = false, pips = false;
            if (hold != null)
            {
                if (isDs5)
                {
                    if (overrides.RightTriggerEffect == null && hold.RightTrigger != null)
                    {
                        overrides.RightTriggerEffect = (byte[])hold.RightTrigger.Clone();
                        right = true;
                    }
                    if (overrides.LeftTriggerEffect == null && hold.LeftTrigger != null)
                    {
                        overrides.LeftTriggerEffect = (byte[])hold.LeftTrigger.Clone();
                        left = true;
                    }
                    if (!overrides.MuteLed.HasValue && hold.MuteLight.HasValue)
                    {
                        overrides.MuteLed = hold.MuteLight;
                        mute = true;
                    }
                    if (!overrides.PlayerIndicator.HasValue && hold.PlayerLights.HasValue)
                    {
                        overrides.PlayerIndicator = hold.PlayerLights;
                        pips = true;
                    }
                }
                if (overrides.LightbarRgb == null && hold.Lightbar != null)
                    overrides.LightbarRgb = (byte[])hold.Lightbar.Clone();
            }
            return new ModFill(overrides, right, left, mute, pips);
        }

        /// <summary>Places a macro's adaptive trigger effects (#531) on the
        /// DualSense triggers neither a game nor a game mod holds this pass.
        /// A trigger it fills reads as held, so the end of the effect owes
        /// one more write of PadForge's own value, the mod's rule.</summary>
        internal static ModFill FillFromMacroEffects(ExternalSubsystemOverrides overrides,
            byte[] left, byte[] right, bool isDs5)
        {
            bool r = false, l = false;
            if (isDs5)
            {
                if (overrides.RightTriggerEffect == null && right != null && right.Length >= 11)
                {
                    overrides.RightTriggerEffect = right;
                    r = true;
                }
                if (overrides.LeftTriggerEffect == null && left != null && left.Length >= 11)
                {
                    overrides.LeftTriggerEffect = left;
                    l = true;
                }
            }
            return new ModFill(overrides, r, l, false, false);
        }

        /// <summary>DS5-over-Bluetooth pads owed the connect one-shot that
        /// releases the LEDs from firmware control (ResetLights + FadeOut of
        /// the default blue). Armed when a Bluetooth DS5 is first seen online
        /// and again whenever it returns from an outage, consumed only after
        /// the write carrying it actually lands, the same
        /// consume-on-delivery contract the speaker-path one-shot uses.
        ///
        /// <para>Without this a DS5 powered on over BT sits on its firmware
        /// default blue and ignores every color PadForge writes, whatever
        /// the Lighting tab says, until an app restart happens to re-open
        /// the device (#334, second report). The standing FadeOut that
        /// d4c011f5 removed had been delivering this release by accident on
        /// every tick since the Lighting tab shipped.</para></summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _btBarReleasePending = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, long> _failedDeliveries = new();
        private readonly HashSet<Guid> _btBarReleaseSeen = new();

        // Devices assigned to this slot on the previous dispatch. A
        // change here means the user re-assigned the slot, which is a
        // deliberate identity claim and has to re-arm state that is
        // otherwise latched for the life of the process.
        private readonly HashSet<Guid> _lastAssignedGuids = new();

        /// <summary>True when this slot's assigned-device set differs from
        /// the previous dispatch. Order does not matter and duplicates cannot
        /// occur, so a count comparison plus a membership sweep is exact.</summary>
        internal static bool AssignmentSetChanged(
            System.Collections.Generic.IReadOnlyList<Guid> current,
            System.Collections.Generic.HashSet<Guid> previous)
        {
            if (current == null || previous == null) return false;
            if (current.Count != previous.Count) return true;
            for (int i = 0; i < current.Count; i++)
            {
                if (!previous.Contains(current[i])) return true;
            }
            return false;
        }
        /// <summary>Reacts to this slot's assigned-device set changing.
        ///
        /// <para>Two latches survive an unassign and would otherwise make
        /// a re-assigned pad behave as though it had never left, until the
        /// app is restarted. Reported 2026-08-01: unassign a DualSense,
        /// re-assign it, and it never picks up its slot's identity color.</para>
        ///
        /// <para>The first is <see cref="ExternalSubsystemState"/>. It is
        /// static, keyed by pad index, and deliberately never reset so a
        /// game's lightbar write survives VC recreates on the slot (#191).
        /// That makes <c>LightbarEverExternal</c> true forever once anything
        /// writes the bar, which permanently disarms the identity floor:
        /// <c>floorArmed = playerNumber &gt; 0 &amp;&amp; !LightbarEverExternal</c>.
        /// Surviving a VC recreate is right. Surviving the device leaving
        /// the slot is not, because the next assignment is a fresh claim.</para>
        ///
        /// <para>The second is <see cref="_ownedLastDispatch"/>. An unassigned
        /// device is skipped by the guid filter rather than removed, so its
        /// entry lingers and the re-add returns false, silently skipping the
        /// seed block that emits the mandatory stop/disengage frame.</para></summary>
        private void OnAssignmentSetObserved(System.Collections.Generic.List<Guid> guids, DeviceCollection devices)
        {
            // Devices that left keep no ownership seed, so a later
            // re-assignment reseeds and emits its stop frame. These maps
            // are documented as devices.SyncRoot-only, and DispatchSnapshot
            // is NOT serialized per instance (static callers reach it via
            // inst.DispatchSnapshot). Compare, retire and publish the set
            // under the same lock so concurrent observations cannot merge.
            lock (devices.SyncRoot)
            {
                foreach (var device in _failedDeliveries.Keys)
                    if (!guids.Contains(device)) _failedDeliveries.TryRemove(device, out _);
                foreach (var device in _usbRouteRetryAt.Keys)
                    if (!guids.Contains(device)) _usbRouteRetryAt.TryRemove(device, out _);
                // Set before the no-change return, so the first change a
                // dispatcher sees after a quiet first look still counts.
                bool firstLook = !_assignmentSeen;
                _assignmentSeen = true;
                if (!AssignmentSetChanged(guids, _lastAssignedGuids)) return;
                foreach (var g in _lastAssignedGuids)
                {
                    if (guids.Contains(g)) continue;
                    // A mouse or keyboard that left the slot (#494) is no
                    // longer lit from it. Leaf call, no lock taken.
                    Common.Input.Peripherals.PeripheralOutputs.ReleaseLighting(g, _padIndex);
                    _ownedLastDispatch.Remove(g);
                    _prevHadRumble.Remove(g);
                    _prevPadForgeWantsRightTrig.Remove(g);
                    _prevPadForgeWantsLeftTrig.Remove(g);
                    _prevPadForgeWantsMicLed.Remove(g);
                    _prevPadForgeWantsPips.Remove(g);
                    _prevModRightTrig.Remove(g);
                    _prevModLeftTrig.Remove(g);
                    _prevModMicLed.Remove(g);
                    _prevModPips.Remove(g);
                    _prevMacroRightTrig.Remove(g);
                    _prevMacroLeftTrig.Remove(g);
                    _audioMuteBurstLeft.Remove(g);
                    RearmAudioAsserts(g);
                }
                // A new assignment starts with no external lighting claim.
                // Keep the established devices-before-external lock order.
                lock (s_externalStateLock)
                    s_externalState.Remove(_padIndex);
                // The same rule for the slot's mice and keyboards (#494), but
                // not on a new dispatcher's first look: a reorder rebuilds it
                // after moving its controller's game color here.
                if (!firstLook) Common.Input.Peripherals.GameLightbarCapture.Forget(_padIndex);

                _lastAssignedGuids.Clear();
                foreach (var g in guids) _lastAssignedGuids.Add(g);
            }
        }
        private static readonly Dictionary<int, ExternalSubsystemState> s_externalState = new();
        private static readonly object s_externalStateLock = new();

        /// <summary>Whether this dispatcher has looked at its slot's devices
        /// yet. devices.SyncRoot only.</summary>
        private bool _assignmentSeen;

        /// <summary>Captured external-write overrides that this dispatch
        /// frame should honor. Null fields mean "PadForge owns this
        /// subsystem this frame — use our own animated / configured
        /// value." Non-null fields carry the most recent external
        /// writer's bytes for subsystems we should mirror.</summary>
        public struct ExternalSubsystemOverrides
        {
            public byte? RumbleRight;
            public byte? RumbleLeft;
            /// <summary>The external writer's rumble mode bits, valid only
            /// while <see cref="RumbleRight"/> and <see cref="RumbleLeft"/>
            /// carry values: valid_flag0 bits 0 and 1 in
            /// <see cref="RumbleFlag0Bits"/>, valid_flag2 bit 2 in
            /// <see cref="RumbleFlag2Bits"/>. The synthesizer asserts these
            /// bits verbatim and none of its own, so a game selecting the
            /// original emulation is not flipped into the improved one by
            /// the 30 Hz pass (#434).</summary>
            public byte RumbleFlag0Bits;
            public byte RumbleFlag2Bits;
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

            /// <summary>True once ANY external write has touched this
            /// slot's player-indicator row this process lifetime, even
            /// outside the grace window. Same stand-down semantic as
            /// <see cref="LightbarEverExternal"/>: PadForge's identity
            /// floor owns the pips until something else claims them, and
            /// then yields for the session so the claimant's row persists
            /// in firmware instead of being overwritten 1.5 s later.</summary>
            public bool PlayerIndicatorEverExternal;
            /// <summary>The last RGB an external writer set, RETAINED past
            /// the grace window (unlike <see cref="LightbarRgb"/>, which is
            /// grace-gated and means "they own the bar right now"). When
            /// PadForge is deliberately not authoring the bar it carries
            /// these bytes instead of zeros, so a firmware that honors the
            /// RGB regardless of the enable bit re-applies the same color
            /// rather than blanking it.</summary>
            public byte[] LastLightbarRgb;
        }

        /// <summary>Called by <c>HMaestroVirtualController.OutputDecoded</c>
        /// for every external host write to a Sony virtual.
        ///
        /// <para>DualSense-shaped payloads ONLY. Every offset below is the
        /// DS5 output-report layout: validFlags at [0] and [1], motors at
        /// [2] and [3], the two 11-byte trigger blocks at [10] and [21].
        /// A DualShock 4 effect report has none of that, which is why the
        /// caller gates on a live DS5 dispatcher and why a DS4 virtual gets
        /// no per-subsystem mirror. Firing this for DS4 would capture
        /// garbage, not fix a gap. A DS4 mirror needs its own parser.</para>
        ///
        /// <para>Inspects the 47-byte USB-shape effect payload's validFlag bits to identify
        /// which subsystems the writer touched; captures their bytes and
        /// refreshes a per-subsystem timestamp. Subsequent
        /// <see cref="DispatchSnapshot"/> calls within the grace window
        /// mirror those bytes (per subsystem, independently) so PadForge
        /// keeps animating subsystems the writer didn't touch while
        /// preserving the writer's intent for the ones it did.</para></summary>
        public static void NotifyExternalSubsystems(int padIndex, ReadOnlySpan<byte> effectPayload)
        {
            if (effectPayload.Length < 47) return;
            byte vf0 = effectPayload[0];
            byte vf1 = effectPayload[1];
            byte rumbleVf2 = effectPayload[38];
            long now = Environment.TickCount64;
            string rumbleTransition = null;

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
                // valid_flag2 bit 2 is the improved emulation that firmware
                // 2.24 added (Linux DS_OUTPUT_VALID_FLAG2_COMPATIBLE_VIBRATION2,
                // SDL3 ucEnableBits3 0x04). A writer on new firmware can
                // select rumble through that bit alone, so it counts as a
                // rumble claim too. The bits are captured with the motor
                // bytes and re-asserted verbatim by the synthesizer (#434).
                //
                // A frame with every rumble bit clear, after a claim, is
                // the writer's release: the firmware drops emulated rumble
                // when the bits go away (SDL3 SDL_hidapi_ps5.c,
                // HIDAPI_DriverPS5_UpdateEffects: "Leaving emulated rumble
                // bits off will restore audio haptics", and that is how
                // SDL3 itself stops rumble). Mirror the stop as zero bits
                // and zero motors for the grace window. Capturing claims
                // alone left the last nonzero motors in the mirror, and
                // the 30 Hz pass re-asserted them for the whole window
                // after the game had already stopped the pad (#434).
                bool rumbleClaimed = (vf0 & 0x03) != 0 || (rumbleVf2 & 0x04) != 0;
                bool rumbleHeld = st.RumbleFlag0Bits != 0 || st.RumbleFlag2Bits != 0;
                if (rumbleClaimed)
                {
                    bool wasZero = st.RumbleRight == 0 && st.RumbleLeft == 0;
                    bool isZero = effectPayload[2] == 0 && effectPayload[3] == 0;
                    if (!rumbleHeld) rumbleTransition = "claim";
                    else if (!wasZero && isZero) rumbleTransition = "zero";
                    else if (wasZero && !isZero) rumbleTransition = "resume";
                    st.RumbleTick = now;
                    st.RumbleRight = effectPayload[2];
                    st.RumbleLeft = effectPayload[3];
                    st.RumbleFlag0Bits = (byte)(vf0 & 0x03);
                    st.RumbleFlag2Bits = (byte)(rumbleVf2 & 0x04);
                }
                else if (rumbleHeld)
                {
                    rumbleTransition = "release";
                    st.RumbleTick = now;
                    st.RumbleRight = 0;
                    st.RumbleLeft = 0;
                    st.RumbleFlag0Bits = 0;
                    st.RumbleFlag2Bits = 0;
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

                // validFlag2 (payload[38]) per bit, the map the synthesizer composes
                // and the passthrough merge already honors: bit 0
                // AllowLightBrightnessChange gates ledBrightness (byte 42), bit 1
                // AllowColorLightFadeAnimation gates lightbarSetup (byte 41), and
                // bit 2 is rumble, which claims neither byte.
                //
                // This arm used to treat ANY nonzero byte as a claim on both, on the
                // grounds that the bit map was unsettled and that PadForge writes a
                // flat 0xFF. Neither holds. The map is named from duaLib right above
                // the synthesizer's own constants, the flat write was retired and a
                // test forbids it, and eleven lines above this very method already
                // reads bit 2 on its own.
                //
                // The cost was a game writing rumble alone. That sends bit 2 with
                // bytes 41 and 42 zero, which this captured as an external claim of
                // brightness ZERO for the whole grace window, and zero is the High
                // setting, so a user on Medium or Low had it overridden to High for
                // as long as the rumble ran.
                byte vf2 = effectPayload[38];
                if ((vf2 & 0x02) != 0)
                {
                    st.LightbarSetupTick = now;
                    st.LightbarSetup = effectPayload[41];
                }
                if ((vf2 & 0x01) != 0)
                {
                    st.LedBrightnessTick = now;
                    st.LedBrightness = effectPayload[42];
                }

                s_externalState[padIndex] = st;
            }

            if (rumbleTransition != null)
                LogRumbleTransition(padIndex, rumbleTransition, vf0, vf1, rumbleVf2,
                    effectPayload[2], effectPayload[3]);
        }

        private const int MirrorLogPerSecond = 200;

        /// <summary>One diagnostics line per rumble transition seen by the
        /// mirror: a claim, a release by clearing the bits, a flagged zero
        /// and a resume from zero. Capped per second with the overflow
        /// reported as a count by the trace queue's own thread, so a trace
        /// shows the exact shape of a game's stop frame (#434 could not be
        /// read from the reporter's trace because only the first packet
        /// was logged). Runs on the virtual pad's output callback, outside
        /// the state lock, and hands the line to the trace queue so the
        /// callback never waits on the diagnostics file.</summary>
        private static void LogRumbleTransition(int padIndex, string transition,
            byte vf0, byte vf1, byte vf2, byte right, byte left)
        {
            Ds5WriteTrace.EnqueueDiag("DS5MIRROR",
                $"DS5MIRROR pad={padIndex} rumble={transition} vf0={vf0:X2} vf1={vf1:X2} vf2={vf2:X2} mR={right} mL={left}",
                MirrorLogPerSecond);
        }

        /// <summary>Copies the last game-written 11-byte trigger-effect
        /// blocks for the slot into the caller's buffers, for the
        /// AT-to-impulse translation (#271 item 3). Unlike the
        /// grace-gated DS5 mirror in <see cref="GetActiveOverrides"/>,
        /// the blocks are handed out for as long as they stay latched:
        /// adaptive-trigger programs are stateful on real firmware (set
        /// once, held until overwritten), so the translation holds them
        /// the same way. A side the game never wrote comes back zeroed
        /// (mode 0x00 = off). Returns false when the slot has never seen
        /// a trigger-effect write at all. Copies, never aliases: the
        /// backing arrays are rewritten in place under the lock on every
        /// external effect packet.</summary>
        public static bool TryGetInboundTriggerEffects(int padIndex, Span<byte> left, Span<byte> right)
        {
            if (left.Length < 11 || right.Length < 11) return false;
            left.Slice(0, 11).Clear();
            right.Slice(0, 11).Clear();
            bool haveLeft = false, haveRight = false;
            lock (s_externalStateLock)
            {
                if (s_externalState.TryGetValue(padIndex, out var st))
                {
                    if (st.LeftTrig != null)
                    {
                        st.LeftTrig.AsSpan(0, 11).CopyTo(left);
                        haveLeft = true;
                    }
                    if (st.RightTrig != null)
                    {
                        st.RightTrig.AsSpan(0, 11).CopyTo(right);
                        haveRight = true;
                    }
                }
            }
            // A game mod's held program (#529) for a side the game never
            // wrote, so an Xbox pad with Translate Adaptive Trigger
            // Vibrations plays a mod's vibrations as it plays a game's.
            // The polling thread calls this, so the controller order comes
            // from the 250 ms cache.
            if ((!haveLeft || !haveRight) && GameModEffects.AnyHeld)
            {
                var hold = GameModEffects.ForSlotCached(padIndex);
                if (hold != null)
                {
                    if (!haveLeft && hold.LeftTrigger != null)
                    {
                        hold.LeftTrigger.AsSpan(0, 11).CopyTo(left);
                        haveLeft = true;
                    }
                    if (!haveRight && hold.RightTrigger != null)
                    {
                        hold.RightTrigger.AsSpan(0, 11).CopyTo(right);
                        haveRight = true;
                    }
                }
            }
            // Then a macro's trigger effect (#531), in the Sony pass's order.
            if (!haveLeft || !haveRight)
            {
                var macro = MacroTriggerEffectsProvider?.Invoke(padIndex);
                if (macro != null && macro.Any)
                {
                    macro.Read(Environment.TickCount64, out var macroLeft, out var macroRight);
                    if (!haveLeft && macroLeft != null)
                    {
                        macroLeft.AsSpan(0, 11).CopyTo(left);
                        haveLeft = true;
                    }
                    if (!haveRight && macroRight != null)
                    {
                        macroRight.AsSpan(0, 11).CopyTo(right);
                        haveRight = true;
                    }
                }
            }
            return haveLeft || haveRight;
        }

        /// <summary>How long every external subsystem must sit untouched
        /// before the writer is treated as gone and the identity floor
        /// re-arms. Same fifteen seconds the effect lane uses to release the
        /// adaptive triggers, and the same number DualSenseY-v2 uses to
        /// decide a driving app has stopped talking (source/udp.cpp:326).</summary>
        private const long ExternalClaimLapseMs = 15_000;

        /// <summary>Whether every external claim on this slot has gone quiet
        /// long enough to be treated as abandoned. Pure, so the rule is
        /// testable without a controller.</summary>
        internal static bool ExternalClaimHasLapsed(long newestTick, long nowTicks)
            => newestTick != 0 && nowTicks - newestTick >= ExternalClaimLapseMs;

        /// <summary>Hands the lightbar and the player pips back when the
        /// program that claimed them is gone.
        ///
        /// <para>PadForge stands down from both the moment anything else
        /// writes them, so a game's color is not fought over. That half was
        /// right. The missing half is that the claim never lapsed: the record
        /// backing <see cref="ExternalSubsystemState.LightbarEverExternal"/>
        /// was only ever dropped when the slot's assigned devices changed, so
        /// after a game exited the bar sat on whatever it last set for the
        /// rest of the process. Reported by two users and reproduced here on
        /// two machines, 2026-08-15: a trace shows the packets still going out
        /// with ledBit=False, PadForge deliberately not claiming a bar nobody
        /// owns any more.</para>
        ///
        /// <para>Gated on the NEWEST tick across every subsystem, not on the
        /// lightbar's own. A game that sets the bar once at a loading screen
        /// and then drives only rumble still counts as present, which is the
        /// case #191 exists to protect. Only a slot where nothing external has
        /// written anything for fifteen seconds is treated as abandoned.</para></summary>
        private void ExpireLapsedExternalClaim(int padIndex)
        {
            long now = Environment.TickCount64;
            lock (s_externalStateLock)
            {
                if (!s_externalState.TryGetValue(padIndex, out var st)) return;
                long newest = Math.Max(
                    Math.Max(Math.Max(st.RumbleTick, st.RightTrigTick),
                             Math.Max(st.LeftTrigTick, st.MicLedTick)),
                    Math.Max(Math.Max(st.LightbarTick, st.PlayerIndTick),
                             Math.Max(st.LightbarSetupTick, st.LedBrightnessTick)));
                if (!ExternalClaimHasLapsed(newest, now)) return;
                s_externalState.Remove(padIndex);
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
                    ov.RumbleFlag0Bits = st.RumbleFlag0Bits;
                    ov.RumbleFlag2Bits = st.RumbleFlag2Bits;
                }
                // Copy, don't alias. st.RightTrig / st.LeftTrig are reused
                // buffers that OnOutputPacket rewrites IN PLACE under this same
                // lock on every external effect packet. Handing the caller the
                // live array let a packet arriving between the read and the
                // encode rewrite bytes the caller had already decided to send,
                // producing a frame that is neither the old nor the new effect.
                // The lightbar case below has always allocated for this reason;
                // these two were the outliers.
                if (now - st.RightTrigTick < ExternalSubsystemGraceMs && st.RightTrig != null)
                    ov.RightTriggerEffect = (byte[])st.RightTrig.Clone();
                if (now - st.LeftTrigTick < ExternalSubsystemGraceMs && st.LeftTrig != null)
                    ov.LeftTriggerEffect = (byte[])st.LeftTrig.Clone();
                if (now - st.MicLedTick < ExternalSubsystemGraceMs)
                    ov.MuteLed = st.MicLed;
                if (now - st.LightbarTick < ExternalSubsystemGraceMs)
                    ov.LightbarRgb = new byte[] { st.LightbarR, st.LightbarG, st.LightbarB };
                ov.LightbarEverExternal = st.LightbarTick != 0;
                if (st.LightbarTick != 0)
                    ov.LastLightbarRgb = new[] { st.LightbarR, st.LightbarG, st.LightbarB };
                if (now - st.PlayerIndTick < ExternalSubsystemGraceMs)
                    ov.PlayerIndicator = st.PlayerInd;
                ov.PlayerIndicatorEverExternal = st.PlayerIndTick != 0;
                if (now - st.LightbarSetupTick < ExternalSubsystemGraceMs)
                    ov.LightbarSetup = st.LightbarSetup;
                if (now - st.LedBrightnessTick < ExternalSubsystemGraceMs)
                    ov.LedBrightness = st.LedBrightness;
            }
            return ov;
        }

        /// <summary>The overrides a dispatch frame would honor right now,
        /// for tests of the mirror's capture and grace rules.</summary>
        internal static ExternalSubsystemOverrides PeekExternalOverrides(int padIndex)
            => GetActiveOverrides(padIndex);

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

        public UserEffectsDispatcher(int padIndex, DeviceSlotConfig config, bool startTimer = true)
        {
            _padIndex = padIndex;
            _config = config;
            _instances.TryGetValue(padIndex, out var previous);
            try
            {
                if (_config != null)
                    _config.PropertyChanged += OnConfigChanged;
                _instances[padIndex] = this;
                RollRandomColor();
                if (startTimer) UpdateAnimTimer();
            }
            catch
            {
                Dispose();
                // Failed construction never takes ownership from a live dispatcher.
                // A concurrent replacement still has priority over the prior one.
                if (previous != null && !previous._disposed
                    && _instances.TryAdd(padIndex, previous)
                    && previous._disposed)
                    ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<int, UserEffectsDispatcher>>)_instances)
                        .Remove(new System.Collections.Generic.KeyValuePair<int, UserEffectsDispatcher>(padIndex, previous));
                throw;
            }
        }

        internal void StartDeferredEffects()
        {
            if (_disposed) return;
            UpdateAnimTimer();
            ApplyOnce();
        }

        /// <summary>Pure core of the single-writer ownership walk
        /// (owner facts, 2026-07-20), extracted so the tests can lock
        /// it. For every settings row whose device is in
        /// <paramref name="guids"/>: fold the row's slot into the
        /// share-group mask, and elect the device's owner as the lowest
        /// live-dispatcher slot among its rows (falling back to
        /// <paramref name="padIndex"/>). Caller holds the settings lock.</summary>
        internal static void ResolveOwnersAndGroup(
            System.Collections.Generic.IList<UserSetting> items,
            System.Collections.Generic.List<Guid> guids,
            int padIndex,
            Func<int, bool> slotLive,
            System.Collections.Generic.Dictionary<Guid, int> owners,
            ref int groupMask)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var us = items[i];
                if (us == null) continue;
                int slot = us.MapTo;
                if (slot < 0 || slot >= InputManager.MaxPads) continue;
                if (us.InstanceGuid == Guid.Empty) continue;
                if (!guids.Contains(us.InstanceGuid)) continue;
                groupMask |= 1 << slot;
                if (!owners.TryGetValue(us.InstanceGuid, out int cur))
                    cur = padIndex;
                if (slot < cur && slotLive(slot))
                    cur = slot;
                owners[us.InstanceGuid] = cur;
            }
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
                if (StartOrStopAnimTimer()) QueueDispatch();
            }
        }

        /// <summary>1 while a pass from <see cref="QueueDispatch"/> is queued.</summary>
        private int _dispatchQueued;

        /// <summary>Queues one pass off the caller's thread, for the poll
        /// thread, whose next read of every pad must not wait on a HID write,
        /// and the audio worker. A pass reads the current values when it
        /// captures, so a burst of requests queues one.</summary>
        private void QueueDispatch()
        {
            if (System.Threading.Interlocked.Exchange(ref _dispatchQueued, 1) != 0) return;
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static d =>
            {
                System.Threading.Volatile.Write(ref d._dispatchQueued, 0);
                try { d.DispatchSnapshot(); }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("DISPATCH queued pass failed: " + ex.GetType().Name);
                }
            }, this, preferLocal: false);
        }

        /// <summary>Re-binds to a new <see cref="DeviceSlotConfig"/>
        /// instance. Used when the parent <see cref="PadViewModel"/>
        /// reassigns its config via the setter (e.g. profile load).</summary>
        public void Rebind(DeviceSlotConfig config)
        {
            if (_disposed) return;
            // Reclaim the registry key. The spare-VC race registers the
            // spare's dispatcher over this one at construction; when the
            // spare loses and disposes, its removal leaves the slot key
            // EMPTY and pokes/battery/sound notifications go nowhere.
            // Rebind is the winner's re-attach path, so re-assert.
            _instances[_padIndex] = this;
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

        internal static bool TryApplyOnce(int padIndex)
        {
            if (!_instances.TryGetValue(padIndex, out var dispatcher)
                || dispatcher._disposed || dispatcher._config == null) return false;
            dispatcher.ApplyOnce();
            return true;
        }

        /// <summary>The lit mice, keyboards and lighting rows the last pass
        /// colored (#494), so a refresh asked for a slot without any costs
        /// nothing.</summary>
        private volatile int _litPeripherals;

        /// <summary>0 idle, 1 a refresh is queued or running, 2 another is
        /// owed when it ends.</summary>
        private int _peripheralRefreshState;
        private long _peripheralRefreshAt;

        /// <summary>Asks a slot's dispatcher for a pass over its lit
        /// peripherals alone, because something they show changed outside it:
        /// the game's lightbar on the slot's virtual
        /// (<see cref="Common.Input.Peripherals.GameLightbarCapture"/>), or a
        /// device's charge. One refresh runs at a time on the thread pool, at
        /// most once per animation tick, and requests that arrive while it
        /// runs fold into one more. A game that animates its lightbar every
        /// frame therefore reaches the peripherals at the dispatcher's own
        /// cadence, the HIDMaestro output reader that reports the color never
        /// waits, and no Sony effect report is written for it.</summary>
        internal static void RequestPeripheralRefresh(int padIndex, bool evenUnlit = false)
        {
            if (!_instances.TryGetValue(padIndex, out var d) || d == null || d._disposed) return;
            // A slot that lit nothing last pass needs no refresh, unless a
            // device gained a path since.
            if (!evenUnlit && d._litPeripherals == 0) return;
            while (true)
            {
                int state = System.Threading.Volatile.Read(ref d._peripheralRefreshState);
                if (state == 2) return;
                if (state == 1)
                {
                    if (System.Threading.Interlocked.CompareExchange(ref d._peripheralRefreshState, 2, 1) == 1) return;
                    continue;
                }
                if (System.Threading.Interlocked.CompareExchange(ref d._peripheralRefreshState, 1, 0) == 0) break;
            }
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(
                static state => ((UserEffectsDispatcher)state).RunPeripheralRefresh(), d);
        }

        private void RunPeripheralRefresh()
        {
            bool idle = false;
            try
            {
                while (!_disposed)
                {
                    long wait = AnimTickMs - (Environment.TickCount64 - _peripheralRefreshAt);
                    if (wait > 0) System.Threading.Thread.Sleep((int)Math.Min(wait, AnimTickMs));
                    // Requests from here on owe one more pass.
                    System.Threading.Volatile.Write(ref _peripheralRefreshState, 1);
                    _peripheralRefreshAt = Environment.TickCount64;
                    DispatchSnapshot(peripheralsOnly: true);
                    if (System.Threading.Interlocked.CompareExchange(ref _peripheralRefreshState, 0, 1) == 1)
                    {
                        idle = true;
                        return;
                    }
                }
            }
            catch
            {
                // A thread-pool fault must never take the process down.
            }
            finally
            {
                if (!idle) System.Threading.Volatile.Write(ref _peripheralRefreshState, 0);
            }
        }

        /// <summary><see cref="RequestPeripheralRefresh"/> for every slot.</summary>
        internal static void RequestPeripheralRefreshAll(bool evenUnlit = false)
        {
            foreach (var pad in _instances.Keys) RequestPeripheralRefresh(pad, evenUnlit);
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
            _failedDeliveries.Clear();
            StopAnimTimer();
            if (_config != null)
                _config.PropertyChanged -= OnConfigChanged;
            _config = null;
            // Only remove from the registry if WE are still the registered
            // instance — a fresh dispatcher could have replaced us mid-life
            // (rebind during VC reset) and we shouldn't yank its slot key.
            // Atomic pair-remove: the old TryGetValue + key-only TryRemove
            // could evict a replacement that registered between the two.
            ((System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<int, UserEffectsDispatcher>>)_instances)
                .Remove(new System.Collections.Generic.KeyValuePair<int, UserEffectsDispatcher>(_padIndex, this));
            // A deleted slot takes its dispatcher with it, and no other slot
            // may dispatch soon, so the pads it left off their headsets or at
            // a lowered Headphone Volume are swept from here (#546). Off this
            // thread: the sweep may write.
            if (!s_routedOffHeadset.IsEmpty || !s_headphoneLowered.IsEmpty)
                System.Threading.ThreadPool.QueueUserWorkItem(static _ =>
                {
                    try { SweepOrphanedRoutes(); } catch { }
                });
        }

        /// <summary>Whether a live dispatcher lights this slot. A slot left
        /// without one, its virtual controller gone, lights nothing, so the
        /// peripheral link pass drops its claims (#494). The release waits for
        /// that pass rather than happening here, because a reorder disposes a
        /// slot's dispatcher and registers its replacement a moment later, and
        /// releasing in between would hand every device back and take it
        /// again.</summary>
        internal static bool HasLiveDispatcher(int padIndex)
            => _instances.TryGetValue(padIndex, out var d) && d != null && !d._disposed;

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
                || e.PropertyName == nameof(DeviceSlotConfig.MacroOverrideExpiresAtUtc)
                // A mouse's or keyboard's control switch (#494) decides
                // whether its animated mode counts toward the timer.
                || e.PropertyName == nameof(DeviceSlotConfig.PeripheralLightingEnabled))
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
        /// inside DispatchSnapshot, so this method just queues the snapshot.
        /// The polling thread calls it and never waits on the pass.</summary>
        public static void NotifyBatteryPercentChanged(int padIndex)
        {
            if (_instances.TryGetValue(padIndex, out var inst) && inst != null)
                inst.QueueDispatch();
        }

        /// <summary>Audio-tab routing or volume changed for the slot — push a
        /// dispatch immediately so the speaker output path / volume bytes land
        /// now instead of riding the next lightbar/battery dispatch (which can
        /// be seconds away on an idle slot).</summary>
        public static void NotifySoundRoutingChanged(int padIndex)
        {
            if (!_instances.TryGetValue(padIndex, out var inst) || inst == null) return;
            // Re-evaluate demand BEFORE the snapshot. A sink coming alive
            // changes whether this slot needs the timer at all, and
            // UpdateAnimTimer is otherwise only reached from config edits and
            // the rumble poke, neither of which fires for a mirror toggle on
            // an otherwise idle slot. Dispatching without it asserted the
            // speaker path exactly once and then went quiet again.
            inst.UpdateAnimTimer();
            inst.DispatchSnapshot();
        }

        private static bool ConfigNeedsTimer(DeviceSlotConfig cfg) =>
            cfg != null && (IsAnimated(cfg.LightbarMode)
                || (cfg.HasActiveMacroLightbarOverride
                    && cfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive)
                || cfg.InputReactiveMode != InputReactiveMode.Off);

        /// <summary>Whether a device's config drives what this slot shows. A
        /// mouse or keyboard whose Lighting tab leaves its lighting to its own
        /// software shows nothing from here (#494), so its mode keeps no timer
        /// running. A vendor row or a unit row has no such switch and is
        /// driven whenever a path lights it
        /// (<see cref="Common.Input.Peripherals.DeviceLinks.OutputOnly"/>). A
        /// peripheral with no path now, asleep or offline or its vendor's
        /// software closed, is decided by what this slot's last pass saw
        /// (<paramref name="peripherals"/>, true for an online mouse or
        /// keyboard): such a mouse follows its switch, and a vendor row, a
        /// unit row or an offline device drives nothing. Every other device
        /// keeps the old rule.</summary>
        internal static bool LightingDriven(Guid device, DeviceSlotConfig cfg,
            IReadOnlyDictionary<Guid, bool> peripherals = null)
        {
            var links = Common.Input.Peripherals.PeripheralOutputs.Links.For(device);
            if (links != null && links.Lighting.Length > 0)
                return links.OutputOnly || cfg?.PeripheralLightingEnabled == true;
            if (peripherals != null && peripherals.TryGetValue(device, out bool canDrive))
                return canDrive && cfg?.PeripheralLightingEnabled == true;
            return true;
        }

        /// <summary>Devices the full pass has visited, every lane included,
        /// for the test that a peripheral refresh never runs it.</summary>
        private int _fullLaneVisits;
        internal int FullLaneVisits => System.Threading.Volatile.Read(ref _fullLaneVisits);

        /// <summary>The lit peripherals this slot's last pass saw, each true
        /// when it is an online mouse or keyboard, false for a vendor row, a
        /// unit row or an offline device. Swapped whole, never changed
        /// after.</summary>
        private volatile IReadOnlyDictionary<Guid, bool> _peripheralKinds = EmptyPeripheralKinds;
        private static readonly IReadOnlyDictionary<Guid, bool> EmptyPeripheralKinds = new Dictionary<Guid, bool>();

        private bool HasTimerDemand()
        {
            if (_disposed || _config == null) return false;
            var configs = SlotPerDeviceConfigsProvider?.Invoke(_padIndex);
            if (configs != null && configs.Count > 0)
            {
                var peripherals = _peripheralKinds;
                foreach (var pair in configs)
                    if (LightingDriven(pair.Key, pair.Value, peripherals) && ConfigNeedsTimer(pair.Value)) return true;
            }
            else if (ConfigNeedsTimer(_config)) return true;
            return !_failedDeliveries.IsEmpty || !_usbRouteRetryAt.IsEmpty || _slotNeedsRumbleTimer
                || AudioPassthroughService.SlotWantsSpeakerPath(_padIndex)
                // An armed DualShock 4 microphone lapses after about four
                // seconds without a 0xA1 effects report (#528).
                || AudioPassthroughService.SlotHasArmedDs4Mic(_padIndex)
                // A game mod changed what it holds here (#529).
                || ModChangedRecently(System.Threading.Volatile.Read(ref _modChangedMs), Environment.TickCount64)
                // A macro's trigger effect ends at a time (#531), which no
                // event announces.
                || MacroTriggerEffectsProvider?.Invoke(_padIndex)?.AnyTimed == true
                // So does a macro's key color, and it fades or counts down
                // meanwhile (#557).
                || MacroKeyColorsProvider?.Invoke(_padIndex)?.AnyTimed == true;
        }

        /// <summary>Whether a mod change at <paramref name="changedMs"/>
        /// still keeps the writer alive at <paramref name="nowMs"/>.</summary>
        internal static bool ModChangedRecently(long changedMs, long nowMs)
            => changedMs != 0 && nowMs - changedMs < ModChangeTimerMs;

        /// <summary>A game mod changed what it holds for this slot (#529).
        /// Queues one pass off the caller's thread, which for a packet is the
        /// game mod server's receive thread and must answer the next packet
        /// without waiting on a HID write. A burst of changes queues one pass,
        /// and the writer kept alive by <see cref="ModChangeTimerMs"/> takes
        /// the rest at its own rate.</summary>
        internal static void NotifyModEffectsChanged(int padIndex)
        {
            if (!_instances.TryGetValue(padIndex, out var inst) || inst == null) return;
            System.Threading.Volatile.Write(ref inst._modChangedMs, Environment.TickCount64);
            if (System.Threading.Interlocked.Exchange(ref inst._modDispatchQueued, 1) != 0) return;
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static d =>
            {
                System.Threading.Volatile.Write(ref d._modDispatchQueued, 0);
                if (d._disposed) return;
                try
                {
                    d.UpdateAnimTimer();
                    d.DispatchSnapshot();
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("DISPATCH game mod pass failed: " + ex.GetType().Name);
                }
            }, inst, preferLocal: false);
        }

        /// <summary>Whether a mod's hold on a subsystem ended since the last
        /// pass, recording this pass's state. True once, so the pass asserts
        /// the subsystem one more time with PadForge's own value: Off for a
        /// trigger or a mute light the pad page leaves unset, PadForge's pips
        /// for the player row.</summary>
        internal static bool ModHoldEnded(Dictionary<Guid, bool> previous, Guid device, bool holdsNow)
        {
            bool held = previous.TryGetValue(device, out var was) && was;
            previous[device] = holdsNow;
            return held && !holdsNow;
        }

        private void UpdateAnimTimer()
        {
            if (StartOrStopAnimTimer()) DispatchSnapshot();
        }

        /// <summary>Starts or stops the writer by its demand. True when it
        /// stopped, which owes the pad a final snapshot, after the timer lock
        /// and on a thread that may wait on a pad write: dispatch touches
        /// HID/effect paths and must not run under the lifecycle lock. Without
        /// that snapshot, the dispatcher's last per-tick write was the live
        /// rumble, and when the polling thread reports gameRumble=false and
        /// the timer stops, the controller never gets a "rumble = 0" packet
        /// and keeps rumbling until the next dispatch, which can be 6-8s away
        /// if the user has no animated lightbar or audio rumble running.
        /// <see cref="OnPollingTickInstance"/> queues it and every other
        /// caller runs it, the counterpart to OnAnimTick's early-exit final
        /// snapshot.</summary>
        private bool StartOrStopAnimTimer()
        {
            // Animation, reactive overlays, rumble and speaker routing share
            // one writer. Every demand keeps that writer alive.
            bool wantTimer = HasTimerDemand();

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
                    StopAnimTimerLocked();
                    dispatchFinal = true;
                }
            }
            return dispatchFinal;
        }


        private void StopAnimTimer()
        {
            lock (_animTimerLock)
                StopAnimTimerLocked();
        }

        /// <summary>Rechecks every demand under the timer lock. A new
        /// demand can arrive after the tick's snapshot and before its stop.</summary>
        private void StopAnimTimerIfStillIdle()
        {
            lock (_animTimerLock)
            {
                if (HasTimerDemand()) return;
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
            bool anyTimeDrivenMode = false;
            bool anyAudioPulseRandom = false;
            bool anyInputReactiveOverlay = false;
            float maxSensitivity = (float)cfg.AudioLightbarSensitivity;
            if (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
            {
                maxSensitivity = 0f;
                var peripherals = _peripheralKinds;
                foreach (var kvp in perDeviceCfgs)
                {
                    var devCfg = kvp.Value;
                    if (devCfg == null) continue;
                    if (!LightingDriven(kvp.Key, devCfg, peripherals)) continue;
                    var devMode = devCfg.LightbarMode;
                    if (IsAnimated(devMode)) anyAnimated = true;
                    if (IsAnimated(devMode) && !IsAudioMode(devMode)) anyTimeDrivenMode = true;
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
                anyTimeDrivenMode = anyAnimated && !anyAudioMode;
                anyAudioPulseRandom = mode == LightbarMode.AudioPulseRandom;
                bool overrideActive = cfg.HasActiveMacroLightbarOverride;
                anyReactiveRunning = overrideActive && cfg.MacroOverrideHoldMode == MacroLightbarHoldMode.Reactive;
                anyInputReactiveOverlay = cfg.InputReactiveMode != InputReactiveMode.Off;
            }

            bool outputNeedsDispatch = !_failedDeliveries.IsEmpty || _slotNeedsRumbleTimer
                || AnyUsbRouteRetryDue(Environment.TickCount64)
                || AudioPassthroughService.SlotWantsSpeakerPath(_padIndex)
                // An armed DualShock 4 microphone lapses after about four
                // seconds without a 0xA1 effects report (#528).
                || AudioPassthroughService.SlotHasArmedDs4Mic(_padIndex);

            // A macro's trigger effect and key color end at a time no event
            // announces, and HasTimerDemand keeps this writer alive for it. A
            // pass reaches neither store on a slot with no pad or no lit
            // peripheral, so the tick ends them, and the end's Changed wakes
            // the pass that writes it. A key color still running fades or
            // counts down, which only the lit peripherals show.
            long macroNowMs = Environment.TickCount64;
            var macroTriggers = MacroTriggerEffectsProvider?.Invoke(_padIndex);
            if (macroTriggers?.AnyTimed == true) macroTriggers.Read(macroNowMs, out byte[] _, out byte[] _);
            var macroColors = MacroKeyColorsProvider?.Invoke(_padIndex);
            macroColors?.Expire(macroNowMs);
            bool keyColorsPaint = macroColors?.AnyTimed == true && _litPeripherals > 0;

            // If no device wants an animated mode, no Reactive override,
            // no input-reactive overlay, and no output work to push,
            // dispatch one final snapshot (so a just-expired override
            // hands off cleanly) and stop the timer. Sticky holds don't
            // keep the timer running. RGB and intensity are constant. A
            // running key color repaints the lit peripherals and writes no
            // Sony report.
            if (!anyAnimated && !anyReactiveRunning && !anyInputReactiveOverlay && !outputNeedsDispatch)
            {
                if (_lastTickOverrideActive)
                {
                    DispatchSnapshot();
                }
                _lastTickOverrideActive = false;
                if (keyColorsPaint) DispatchSnapshot(peripheralsOnly: true);
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
            //   - Speaker routing needs its path and volume asserted.
            if (!anyAnimated && (anyReactiveRunning || anyInputReactiveOverlay || outputNeedsDispatch))
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

            // Roll a new random color on the rising edge of an audio
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
            // color using its own LightbarMode + palette, so two
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
                byte rRight = r.right, rLeft = r.left;
                // Group-aware change detect (owner facts, 2026-07-20): this
                // slot may own a pad whose rumble originates on ANOTHER
                // sharing slot, and a steady audio peak must not starve
                // that onset/stop. Max the raw bytes across the share
                // group resolved by the last DispatchSnapshot.
                int grp = _groupSlotsMask;
                for (int gs = 0; grp != 0 && gs < InputManager.MaxPads; gs++)
                {
                    if (gs == _padIndex || (grp & (1 << gs)) == 0) continue;
                    var go = SlotRawRumbleProvider?.Invoke(gs) ?? ((byte)0, (byte)0);
                    if (go.right > rRight) rRight = go.right;
                    if (go.left > rLeft) rLeft = go.left;
                }
                bool rumbleChanged = rRight != _lastDispatchedRumbleR || rLeft != _lastDispatchedRumbleL;

                // Suppress the rainbow-pulse mode's special-case
                // anti-skip only when ANY device is on AudioPulseRainbow
                // — keeps that mode's per-tick hue rotation alive.
                bool anyAudioPulseRainbow = (perDeviceCfgs != null && perDeviceCfgs.Count > 0)
                    ? AnyDeviceMode(perDeviceCfgs, LightbarMode.AudioPulseRainbow)
                    : cfg.LightbarMode == LightbarMode.AudioPulseRainbow;
                // A flat peak can skip only when no other demand needs a
                // fresh snapshot. Another device's time animation, reactive
                // overlays, rumble and speaker routing still need dispatch.
                if (!zeroCrossing && !rumbleChanged && delta < 0.004f && !anyAudioPulseRainbow
                    && !anyReactiveRunning && !anyInputReactiveOverlay
                    && !anyTimeDrivenMode && !outputNeedsDispatch)
                {
                    if (keyColorsPaint) DispatchSnapshot(peripheralsOnly: true);
                    return;
                }
                _lastDispatchedPeak = scaled;
                _lastDispatchedRumbleR = rRight;
                _lastDispatchedRumbleL = rLeft;
            }

            // Dispatch WITHOUT a pre-scaled peak, so each device rescales the
            // raw peak by its OWN AudioLightbarSensitivity in the device loop.
            // Passing `scaled` here handed every Sony device on the slot the
            // peak computed from maxSensitivity, so a device deliberately set
            // low flashed exactly as hard as the most sensitive one beside it
            // and its own slider did nothing on the 30 Hz path. maxSensitivity
            // is still right for the onset and early-exit decisions above:
            // those gate whether ANY device renders, and must not suppress a
            // device more sensitive than the anchor.
            DispatchSnapshot();
        }

        /// <summary>True when any per-device config on the slot is in
        /// <paramref name="mode"/>. With no per-device configs the caller
        /// must fall back to the anchor _config, exactly as the other
        /// per-tick aggregates in OnAnimTickCore do: this returning false on
        /// an empty map dropped the AudioPulseRainbow anti-skip exception on
        /// a slot that had not built per-device configs yet, and the mode's
        /// per-tick hue rotation stalled whenever the audio delta was
        /// small.</summary>
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
            // so the color reads cleanly through the diffuser at any
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

            // Roll per-device pulse color using each device's own
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
                    // LightbarRed/G/B; no per-device pulse color to
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

        // The audioPeak parameter is gone. All eight call sites invoked this
        // with no argument, so it was always -1 and the "caller supplied a
        // peak" branch below could never be taken. Keeping it implied an
        // override path that did not exist.
        private int _lastMoveSphereSig = -1;
        private long _lastMoveSphereLogMs;

        /// <summary>A mouse, keyboard, vendor row or unit row with lighting:
        /// one its record says has it, so a device asleep keeps its claim, or
        /// one a lighting path links now.</summary>
        private static bool IsLitPeripheral(UserDevice ud)
            => ud.HasPeripheralLighting || Common.Input.Peripherals.PeripheralOutputs.HasLighting(ud.InstanceGuid);

        /// <summary>One lit peripheral's color for this slot (#494), or its
        /// release when the device's own software keeps its lighting. True
        /// when this slot lights it. Called under devices.SyncRoot: every
        /// call below is a leaf, and the backends do the I/O.
        ///
        /// <para>Only the device's own config on this slot lights it. A
        /// device with none yet, assigned a moment before its default entry
        /// is made, is released rather than lit with the anchor, the selected
        /// device's config, whose Control This Device's Lighting switch and
        /// mode are another device's. Its own entry comes on a later pass: a
        /// vendor row or a unit row lights from it, having no switch
        /// (<see cref="Common.Input.Peripherals.IOutputOnlyRow"/>), and a
        /// mouse or keyboard waits for its own.</para>
        ///
        /// <para>The color is the Lighting tab's mode through the core every
        /// lit device shares, under the game's lightbar the way a DualSense
        /// takes it (<see cref="Common.Input.Peripherals.PeripheralLightingColor"/>).
        /// Player Number shows this slot's own color, since each slot holds its
        /// own claim and the claim from the smallest displayed player number
        /// is the one the device shows (<see cref="Common.Input.Peripherals.PeripheralOutputs.Rules"/>).
        /// The Sony lane's lowest-raw-index owner rule is deliberately not
        /// used: it can disagree with the displayed numbers.</para></summary>
        private bool LightPeripheral(UserDevice ud, IReadOnlyDictionary<Guid, DeviceSlotConfig> perDeviceCfgs,
            float rawAudioPeak, long nowMs, int slotNumber, int? modColor)
        {
            Guid guid = ud.InstanceGuid;
            DeviceSlotConfig devCfg = null;
            if (perDeviceCfgs != null && perDeviceCfgs.TryGetValue(guid, out var resolved))
                devCfg = resolved;
            bool outputOnly = ud.Device is Common.Input.Peripherals.IOutputOnlyRow;
            // The switch and the hold in one read. A load writes them one
            // after the other on the UI thread and this pass can run on the
            // timer's: read apart, the switch of the outgoing settings could
            // meet the hold of the incoming ones.
            var hold = PeripheralClaimLevel.Colors;
            bool lit = devCfg != null && devCfg.PeripheralLightingClaim(out hold);
            if (devCfg == null || (!outputOnly && !lit))
            {
                Common.Input.Peripherals.PeripheralOutputs.ReleaseLighting(guid, _padIndex);
                return false;
            }

            Common.Input.Peripherals.GameLightbarCapture.Read(_padIndex, nowMs, out int? fresh, out int? last);
            // A game mod's lightbar color (#529) where no game color is
            // live, the way the DualSense takes it: a game's write wins.
            if (fresh == null && modColor.HasValue)
            {
                fresh = modColor;
                last = modColor;
            }
            float peak = Math.Clamp(rawAudioPeak * (float)devCfg.AudioLightbarSensitivity, 0f, 1f);
            var state = _deviceStates.TryGetValue(guid, out var ds) ? ds : null;
            uint pulse = state?.PulseColor ?? 0;
            float pulseIntensity = ComputePulseIntensity(nowMs, devCfg);
            // A Logitech device reports its charge over HID++. Any other
            // holds Battery mode at its full-charge end, the web pad's rule.
            int charge = Common.Input.Peripherals.PeripheralOutputs.BatteryOf(guid);
            byte battery = (byte)Math.Clamp(charge < 0 ? 100 : charge, 0, 100);
            Common.Input.Peripherals.PeripheralLightingColor.Resolve(devCfg, fresh, last, peak, nowMs,
                _randomColor, pulse, pulseIntensity, battery, slotNumber, out byte r, out byte g, out byte b);
            // The claim carries the device's Lighting Hold, how firmly this
            // slot may hold it while it lights it. A vendor row or a unit
            // row has no such choice on its tab, so it asks for colors
            // alone whatever its config holds.
            int level = outputOnly ? 0 : (int)hold;
            // The device's light colors for the shift layer engaged now
            // (#557), with the slot's macro key colors over them, over the
            // color above.
            var keys = KeyFrameFor(devCfg, nowMs);
            Common.Input.Peripherals.PeripheralOutputs.SetLighting(guid, _padIndex, slotNumber, r, g, b, level, keys,
                devCfg.LedStripCountsParsed);
            return true;
        }

        /// <summary>The light colors <paramref name="devCfg"/> holds for
        /// this slot's engaged shift layer (#557), with the key colors the
        /// slot's macros play over them, or null when there are none.</summary>
        private Common.Input.Peripherals.KeyFrame KeyFrameFor(DeviceSlotConfig devCfg, long nowMs)
        {
            var settings = devCfg.KeyLightingParsed;
            var frame = settings == null || settings.IsEmpty ? null : settings.FrameFor(EngagedLayerOf(_padIndex));
            var macros = MacroKeyColorsProvider?.Invoke(_padIndex);
            return macros != null && macros.Any ? macros.Compose(frame, nowMs) : frame;
        }

        /// <summary>A slot's macro key colors changed (#557): one started,
        /// ended or was cleared. Starts or stops the writer that paints a
        /// fade, a countdown or an end, and repaints the slot's lit
        /// peripherals. Called on the polling thread and the dispatcher's,
        /// and never blocks.</summary>
        internal static void NotifyMacroKeyColorsChanged(int padIndex)
        {
            if (!_instances.TryGetValue(padIndex, out var inst) || inst == null) return;
            if (System.Threading.Interlocked.Exchange(ref inst._keyColorsQueued, 1) != 0) return;
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(static d =>
            {
                System.Threading.Volatile.Write(ref d._keyColorsQueued, 0);
                if (d._disposed) return;
                try
                {
                    d.UpdateAnimTimer();
                    RequestPeripheralRefresh(d._padIndex);
                }
                catch (Exception ex)
                {
                    Engine.SdlDiagLog.WriteLine("DISPATCH macro key colors pass failed: " + ex.GetType().Name);
                }
            }, inst, preferLocal: false);
        }

        private int _keyColorsQueued;

        /// <summary>The shift layer engaged on <paramref name="padIndex"/>,
        /// "Base" when none is or the slot has no mapping set.</summary>
        private static string EngagedLayerOf(int padIndex)
        {
            var sets = SettingsManager.SlotMappingSets;
            if (sets == null || padIndex < 0 || padIndex >= sets.Length || sets[padIndex] == null)
                return Common.Input.Peripherals.KeyLightingSettings.BaseMask;
            return InputManager.GetEngagedLayerMask(padIndex, sets[padIndex]);
        }

        /// <summary>A slot's shift layer changed (#557,
        /// InputManager.EngagedLayerChanged). The slot's lit peripherals are
        /// repainted (<see cref="RequestPeripheralRefresh"/>) when a device
        /// on it has light colors on any layer but Base, since only then can
        /// the change show. Called on the polling thread, and never
        /// blocks.</summary>
        internal static void NotifyKeyLayersChanged(int padIndex)
        {
            var configs = SlotPerDeviceConfigsProvider?.Invoke(padIndex);
            if (configs == null) return;
            foreach (var cfg in configs.Values)
            {
                var settings = cfg?.KeyLightingParsed;
                if (settings == null) continue;
                foreach (var mask in settings.Layers.Keys)
                {
                    if (mask == Common.Input.Peripherals.KeyLightingSettings.BaseMask) continue;
                    RequestPeripheralRefresh(padIndex);
                    return;
                }
            }
        }

        /// <param name="peripheralsOnly">Light the slot's mice, keyboards and
        /// vendor rows and nothing else (#494): no Sony effect report, no web
        /// pad or PS Move color. For a refresh only they need.</param>
        private void DispatchSnapshot(bool peripheralsOnly = false)
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
            // Before reading the overrides: if nothing external has written
            // this slot for fifteen seconds, the claimant is gone and the
            // identity floor takes the bar and the pips back.
            ExpireLapsedExternalClaim(_padIndex);

            int playerNumber = SettingsManager.SlotOrders.GetGlobalSlotNumber(_padIndex);
            // GetGlobalSlotNumber returns 0 for a slot that exists and is
            // assigned but sits in no group's ORDER list, and its own summary
            // tells callers to fall back to padIndex + 1. This site did not,
            // and the failure is invisible in the obvious place: the lightbar
            // looks right either way, because PlayerIdentityDefaults.Wrap maps
            // both 0 and 1 to color index 0. Only the pips give it away, since
            // they are gated on playerNumber > 0, so the pad shows player one's
            // color with no pips at all. Reported on hardware 2026-08-15.
            if (playerNumber <= 0) playerNumber = _padIndex + 1;

            // For non-tick dispatches (slider drag, OnDevicesUpdated re-
            // apply, etc.), pull the current peak so the audio path
            // doesn't snap to black between ticks. The synthesizer
            // ignores the peak when the active mode doesn't read it.
            float rawAudioPeak = AudioPeakProvider?.Invoke() ?? 0f;
            // Peak scaling happens per device inside the loop below, each
            // against its own AudioLightbarSensitivity. There is no pre-loop
            // default: the local that used to hold one was never read.
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

            // Resolve assigned DS5 GUIDs plus, in the SAME settings
            // snapshot so ownership can never disagree with the guid list
            // (the stale-assignment write window), each device's owner
            // slot and the union share-group mask. Owner = lowest slot
            // among the device's assignment rows with a live dispatcher.
            var guids = new System.Collections.Generic.List<Guid>(4);
            var owners = new System.Collections.Generic.Dictionary<Guid, int>(4);
            int groupMask = 1 << _padIndex;
            lock (settings.SyncRoot)
            {
                foreach (var us in settings.Items)
                {
                    if (us == null) continue;
                    if (us.MapTo != _padIndex) continue;
                    if (us.InstanceGuid == Guid.Empty) continue;
                    guids.Add(us.InstanceGuid);
                }
                if (guids.Count > 0)
                    ResolveOwnersAndGroup(settings.Items, guids, _padIndex,
                        static slot => _instances.ContainsKey(slot),
                        owners, ref groupMask);
            }
            _groupSlotsMask = groupMask;
            OnAssignmentSetObserved(guids, devices);
            SweepOrphanedRoutes();
            if (guids.Count == 0)
            {
                _litPeripherals = 0;
                _peripheralKinds = EmptyPeripheralKinds;
                return;
            }

            // Payloads are built under the device lock and written after it.
            // PlayStationEffectWriter.Write does CreateFileW + WriteFile with a 1000 ms
            // wait and then an untimed completion drain, and the ~1 kHz poll
            // thread needs this same lock every cycle to read input. Writing
            // under it meant one animated pad (a 33 ms timer) held the poll
            // thread out for the duration of a synchronous HID write, and a
            // stalled write could hold it out indefinitely. Nothing here needs
            // the lock: the writer opens its own handle, and every value below
            // is either a captured string/reference or a per-iteration
            // allocation.
            // SpeakerCleared carries the device whose speaker-path-cleared
            // one-shot this payload is spending, or Guid.Empty. The one-shot is
            // only consumed after the write for this entry actually lands, so a
            // stale-skip or a failed write leaves it armed for the next tick.
            // Audio carries the values this frame's audio asserts were judged
            // against, committed under the same rule (CommitDeliveredAudio).
            var pending = new List<(Guid Device, string Path, HMProfile Profile,
                IReadOnlyDictionary<string, object> Fields, long Seq, long RecoveredThrough,
                Guid SpeakerCleared, Guid BtBarReleased,
                (int HeadphoneVolume, int OutputPath, bool Ds4MicArmed, int Ds4UsbRoute) Audio)>();
            int litPeripherals = 0;
            Dictionary<Guid, bool> peripheralKinds = null;

            lock (devices.SyncRoot)
            {
                // Per-subsystem override snapshot for this dispatch. Subsystems
                // the external writer recently touched are mirrored from their
                // captured bytes; subsystems they didn't touch keep flowing
                // PadForge's own animated / configured values. Test rumble
                // (user-initiated inside PadForge) bypasses external rumble
                // mirroring so the user's test always wins.
                //
                // Captured UNDER the device lock, the same hold that stamps
                // every payload's sequence below, so capture order is
                // sequence order. Captured before the lock, a dispatch could
                // snapshot a nonzero mirror, wait on the lock while a
                // release and the queued final snapshot went
                // through, then stamp a HIGHER sequence and write the stale
                // pair after the stop with no timer left to correct it
                // (#434).
                var overrides = GetActiveOverrides(_padIndex);
                if (testTarget != Guid.Empty)
                {
                    overrides.RumbleRight = null;
                    overrides.RumbleLeft  = null;
                }
                // What a game mod holds for this slot (#529), captured under
                // the same lock for the same reason: a release and this pass
                // are then ordered like everything else it writes.
                var modHold = GameModEffects.ForSlot(_padIndex);
                // What a macro plays on this slot's triggers (#531), the same
                // way. Copies: Step 4b may change the store meanwhile.
                byte[] macroLeftTrig = null, macroRightTrig = null;
                MacroTriggerEffectsProvider?.Invoke(_padIndex)?.Read(
                    Environment.TickCount64, out macroLeftTrig, out macroRightTrig);

                foreach (var ud in devices.Items)
                {
                    if (ud == null) continue;

                    bool isDs5 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == PidStandard || ud.ProdId == PidEdge);
                    bool isDs4 = ud.VendorId == SonyVid &&
                                 (ud.ProdId == Ds4Pid_V1 || ud.ProdId == Ds4Pid_V2 || ud.ProdId == Ds4Pid_Adapter);
                    bool isPs = isDs5 || isDs4;

                    if (!guids.Contains(ud.InstanceGuid)) continue;
                    if (peripheralsOnly)
                    {
                        if (isPs || !IsLitPeripheral(ud)) continue;
                        (peripheralKinds ??= new Dictionary<Guid, bool>())[ud.InstanceGuid] =
                            ud.IsOnline && ud.Device is not Common.Input.Peripherals.IOutputOnlyRow;
                        if (!ud.IsOnline)
                            Common.Input.Peripherals.PeripheralOutputs.ReleaseLighting(ud.InstanceGuid, _padIndex);
                        else if (LightPeripheral(ud, perDeviceCfgs, rawAudioPeak, nowMs, playerNumber, ModColor(modHold)))
                            litPeripherals++;
                        continue;
                    }
                    System.Threading.Interlocked.Increment(ref _fullLaneVisits);
                    if (!ud.IsOnline)
                    {
                        // An offline row lights nothing from this slot (#494),
                        // and an offline peripheral keeps no timer running.
                        if (!isPs && IsLitPeripheral(ud))
                            (peripheralKinds ??= new Dictionary<Guid, bool>())[ud.InstanceGuid] = false;
                        Common.Input.Peripherals.PeripheralOutputs.ReleaseLighting(ud.InstanceGuid, _padIndex);
                        _failedDeliveries.TryRemove(ud.InstanceGuid, out _);
                        _usbRouteRetryAt.TryRemove(ud.InstanceGuid, out _);
                        // Remember the outage so the return re-arms below.
                        _deviceWasOffline.Add(ud.InstanceGuid);
                        continue;
                    }
                    if (_deviceWasOffline.Remove(ud.InstanceGuid))
                    {
                        // The device came back. A USB re-enumeration (and a BT
                        // re-pair) FACTORY-RESETS the pad's audio state: output
                        // path back to headphones, volumes back to defaults,
                        // mute state cleared. Every change-gated authority
                        // write in this dispatcher believed its value was
                        // already applied, so nothing re-armed and the pad sat
                        // on a silent headphone path until an app restart
                        // happened to re-claim it. duaLib carries a
                        // wasDisconnected flag for exactly this and ORs it
                        // into every gate (duaLib.cpp:576/613/617). This is
                        // that flag. Traced live 2026-08-01: replug left the
                        // speaker silent on both endpoints while every sink
                        // rebuilt cleanly.
                        _prevHadRumble[ud.InstanceGuid] = true;
                        _prevPadForgeWantsLeftTrig[ud.InstanceGuid] = true;
                        _prevPadForgeWantsRightTrig[ud.InstanceGuid] = true;
                        _prevPadForgeWantsMicLed[ud.InstanceGuid] = true;
                        _prevPadForgeWantsPips[ud.InstanceGuid] = true;
                        _audioMuteBurstLeft[ud.InstanceGuid] = AudioMuteBurstFrames;
                        RearmAudioAsserts(ud.InstanceGuid);
                        // A BT re-pair also puts the LEDs back under firmware
                        // control, so the connect release is owed again.
                        if (isDs5 && PlayStationEffectWriter.IsBluetoothPath(ud.DevicePath))
                            _btBarReleasePending.TryAdd(ud.InstanceGuid, 0);
                        Engine.SdlDiagLog.WriteLine("DISPATCH re-arm (device returned) guid="
                            + ud.InstanceGuid.ToString("N").Substring(0, 8));
                    }
                    // First online sighting this session: duaLib sends the
                    // release at every enumeration, so a pad that connected
                    // before PadForge started, or connects mid-session for
                    // the first time, is owed one too.
                    if (isDs5 && ud.IsOnline
                        && !_btBarReleaseSeen.Contains(ud.InstanceGuid)
                        && PlayStationEffectWriter.IsBluetoothPath(ud.DevicePath))
                    {
                        _btBarReleaseSeen.Add(ud.InstanceGuid);
                        _btBarReleasePending.TryAdd(ud.InstanceGuid, 0);
                    }
                    // Web controller lightbar: a phone drawing a DualShock 4
                    // or a DualSense renders the same bar the hardware has, so
                    // it gets the same lighting engine rather than a still
                    // color. Same shape as the Move lane below it, and for
                    // the same reason: a device whose bar is not written with
                    // a HID effect packet still deserves the shared color
                    // core (static / breathing / palette / audio / battery /
                    // input-reactive), delivered through its own writer.
                    //
                    // The identity refresh uses this dispatcher while it is
                    // live, so it cannot overwrite an overlay or macro color.
                    if (ud.Device is PadForge.Engine.WebControllerDevice webPad && webPad.HasLightbar)
                    {
                        if (owners.TryGetValue(ud.InstanceGuid, out int webOwner) && webOwner != _padIndex)
                            continue;
                        DeviceSlotConfig webCfg = null;
                        if (perDeviceCfgs != null
                            && perDeviceCfgs.TryGetValue(ud.InstanceGuid, out var resolvedWeb))
                            webCfg = resolvedWeb;
                        webCfg ??= cfg;
                        if (webCfg != null)
                        {
                            float webPeak = Math.Clamp(
                                rawAudioPeak * (float)webCfg.AudioLightbarSensitivity, 0f, 1f);
                            var webState = _deviceStates.TryGetValue(ud.InstanceGuid, out var wds) ? wds : null;
                            uint webPulse = webState?.PulseColor ?? 0;
                            float webPulseIntensity = ComputePulseIntensity(nowMs, webCfg);
                            // No battery to read from a browser, so Battery
                            // mode holds its full-charge end rather than
                            // pretending to a level it cannot know.
                            int webPlayer = SettingsManager.SlotOrders.GetIdentityPlayerNumber(ud.InstanceGuid);
                            if (webPlayer <= 0) webPlayer = _padIndex + 1;
                            Ds4EffectSynthesizer.ResolveLightbarRgb(webCfg, webPeak, nowMs,
                                _randomColor, webPulse, webPulseIntensity, 100, webPlayer,
                                out byte wr, out byte wg, out byte wb, PlayerIdentityDefaults.WebColorFor(webPlayer));
                            webPad.SetLed(wr, wg, wb);
                        }
                        continue;
                    }

                    // PS Move sphere (#277): the sphere is the Move's lightbar.
                    // Same per-device lighting config, same shared color core
                    // as the DS4/DS5 paths (static / breathing / palette /
                    // audio / battery / input-reactive all ride through),
                    // delivered via the Move service's writer instead of a
                    // HID effect packet. The service is the pad's sole writer.
                    if (ud.VendorId == SonyVid && ud.ProdId == 0x03D5)
                    {
                        // Single-writer ownership applies to the sphere too
                        // (2026-08-18 audit): a Move assigned to two slots
                        // must not have two dispatchers fighting its color.
                        // Same lowest-live-slot rule as the DS4/DS5 path.
                        if (owners.TryGetValue(ud.InstanceGuid, out int moveOwner) && moveOwner != _padIndex)
                            continue;
                        DeviceSlotConfig moveCfg = null;
                        if (perDeviceCfgs != null
                            && perDeviceCfgs.TryGetValue(ud.InstanceGuid, out var resolvedMove))
                            moveCfg = resolvedMove;
                        moveCfg ??= cfg;
                        if (moveCfg != null && ud.Device is PadForge.Engine.SdlDeviceWrapper moveWrap)
                        {
                            int movePlayer = SettingsManager.SlotOrders.GetIdentityPlayerNumber(ud.InstanceGuid);
                            if (movePlayer <= 0) movePlayer = _padIndex + 1;
                            float movePulseIntensity = ComputePulseIntensity(nowMs, moveCfg);
                            bool moveOverride = moveCfg.ComputeMacroOverrideIntensity() > 0f;
                            bool moveOverlay = moveCfg.InputReactiveMode != InputReactiveMode.Off
                                && movePulseIntensity > 0f;
                            if (moveCfg.LightbarMode == LightbarMode.PlayerNumber && !moveOverride && !moveOverlay)
                            {
                                // Release the composed color when the overlay
                                // or macro ends, then restore the service's floor.
                                bool released = Common.Input.PsMoveDirectService.ReleaseLedClaim(moveWrap.SdlInstanceId);
                                Common.Input.PsMoveDirectService.TrySetPlayerNumber(moveWrap.SdlInstanceId, movePlayer);
                                int floorSig = unchecked((int)0x7F000000) | (released ? 1 : 0);
                                if (floorSig != _lastMoveSphereSig)
                                {
                                    _lastMoveSphereSig = floorSig;
                                    PadForge.Engine.SdlDiagLog.WriteLine(
                                        $"MOVELIGHT slot={_padIndex} mode=PlayerNumber floor released={released}");
                                }
                            }
                            else
                            {
                                float movePeak = Math.Clamp(
                                    rawAudioPeak * (float)moveCfg.AudioLightbarSensitivity, 0f, 1f);
                                var moveState = _deviceStates.TryGetValue(ud.InstanceGuid, out var mds) ? mds : null;
                                uint movePulse = moveState?.PulseColor ?? 0;
                                var power = Common.Input.PsMoveDirectService.GetPowerInfo(moveWrap.SdlInstanceId);
                                // Unknown / charging reports Percent = -1;
                                // Battery mode must read that as full, not
                                // empty (2026-08-18 audit).
                                int rawPct = power?.Percent ?? 100;
                                byte movePct = (byte)Math.Clamp(rawPct < 0 ? 100 : rawPct, 0, 100);
                                Ds4EffectSynthesizer.ResolveLightbarRgb(moveCfg, movePeak, nowMs,
                                    _randomColor, movePulse, movePulseIntensity, movePct, movePlayer,
                                    out byte sr, out byte sg, out byte sb,
                                    PsMoveDirectService.DefaultSphereColor(movePlayer));
                                var sphere = (r: sr, g: sg, b: sb);
                                bool sphereOk = Common.Input.PsMoveDirectService.TrySetLed(
                                    moveWrap.SdlInstanceId, sphere.r, sphere.g, sphere.b);
                                // Evidence line, change-gated AND rate-limited
                                // (2026-08-18 audit): an animated mode
                                // (Rainbow, AudioVisualizer) changes the color
                                // every dispatch, so a pure change gate logged
                                // ~32 lines a second, 82k lines in one bench
                                // session, drowning the 400-line crash ring
                                // and the diag mirror. Mode or setOk changes
                                // log immediately; a color-only change logs at
                                // most once a second.
                                int sphereSig = (sphere.r << 16) | (sphere.g << 8) | sphere.b
                                    | ((int)moveCfg.LightbarMode << 24) | (sphereOk ? 1 << 30 : 0);
                                int sphereMode = sphereSig & unchecked((int)0xFF000000) | (sphereOk ? 1 << 30 : 0);
                                int lastMode = _lastMoveSphereSig & unchecked((int)0xFF000000) | (_lastMoveSphereSig & (1 << 30));
                                if (sphereSig != _lastMoveSphereSig
                                    && (sphereMode != lastMode || nowMs - _lastMoveSphereLogMs >= 1000))
                                {
                                    _lastMoveSphereSig = sphereSig;
                                    _lastMoveSphereLogMs = nowMs;
                                    PadForge.Engine.SdlDiagLog.WriteLine(
                                        $"MOVELIGHT slot={_padIndex} mode={moveCfg.LightbarMode} "
                                        + $"rgb={sphere.r:X2}{sphere.g:X2}{sphere.b:X2} setOk={sphereOk}");
                                }
                            }
                        }
                        continue;
                    }

                    // Mice, keyboards, the vendor lighting rows and the unit
                    // rows (#494): the web pad's shape, one color per device
                    // through the same core, handed to the peripheral backends
                    // instead of written here, since a backend's I/O must
                    // never hold this lock the poll thread needs.
                    if (!isPs && IsLitPeripheral(ud))
                    {
                        (peripheralKinds ??= new Dictionary<Guid, bool>())[ud.InstanceGuid] =
                            ud.Device is not Common.Input.Peripherals.IOutputOnlyRow;
                        if (LightPeripheral(ud, perDeviceCfgs, rawAudioPeak, nowMs, playerNumber, ModColor(modHold)))
                            litPeripherals++;
                        continue;
                    }

                    if (!isPs) continue;

                    // Single-writer ownership (owner facts, 2026-07-20):
                    // the lowest live-dispatcher slot among this device's
                    // assignment rows makes every write; the other slots'
                    // dispatchers skip it. Resolved from the same settings
                    // snapshot as `guids`, checked after the cheap gates so
                    // non-Sony devices never pay for it. On losing
                    // ownership the seed entry drops so a later re-own
                    // reseeds the stop-frame state below.
                    if (owners.TryGetValue(ud.InstanceGuid, out int ownSlot) && ownSlot != _padIndex)
                    {
                        _ownedLastDispatch.Remove(ud.InstanceGuid);
                        _failedDeliveries.TryRemove(ud.InstanceGuid, out _);
                        _usbRouteRetryAt.TryRemove(ud.InstanceGuid, out _);
                        continue;
                    }
                    // New ownership or a failed delivery owes a full state
                    // refresh. Re-arm the stop/disengage fields before building
                    // the retry, even when the current values are zero.
                    _failedDeliveries.TryGetValue(ud.InstanceGuid, out long recoveredThrough);
                    if (_ownedLastDispatch.Add(ud.InstanceGuid) || recoveredThrough != 0)
                    {
                        _prevHadRumble[ud.InstanceGuid] = true;
                        _prevPadForgeWantsLeftTrig[ud.InstanceGuid] = true;
                        _prevPadForgeWantsRightTrig[ud.InstanceGuid] = true;
                        _prevPadForgeWantsMicLed[ud.InstanceGuid] = true;
                        _prevPadForgeWantsPips[ud.InstanceGuid] = true;
                        // Same reasoning for the mic: a prior owner may
                        // have left the pad hardware-muted, so a fresh
                        // claim re-arms the bounded unmute burst.
                        _audioMuteBurstLeft[ud.InstanceGuid] = AudioMuteBurstFrames;
                        // Force one headphone-volume write on a fresh claim.
                        RearmAudioAsserts(ud.InstanceGuid);
                    }

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
                    bool isBluetooth = PlayStationEffectWriter.IsBluetoothPath(path);

                    // Resolve the HM profile whose extendedOutputReport spec
                    // describes this device's wire format. The synthesizer
                    // emits semantic fields (rightMotor / leftMotor /
                    // lightbar / triggers / ...); PlayStationEffectWriter feeds them
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
                    // so the override is null and this branch is skipped:
                    // the bytes flow through to the real DualSense via
                    // the dispatcher path, and the rumble claim published
                    // below carries them into the pass-through lane's
                    // frames too, so a game still writing the virtual pad
                    // does not cancel them.
                    bool gameDrivenRumble = devOverrides.RumbleRight.HasValue && devOverrides.RumbleLeft.HasValue;
                    if (gameDrivenRumble && isDs5
                        && DualSensePassthroughDispatcher.IsPassthroughTarget(_padIndex, ud.InstanceGuid))
                    {
                        rR = 0;
                        rL = 0;
                    }

                    // A release in its grace window (the game cleared every
                    // rumble bit to go back to audio haptics) on a DualSense
                    // that takes the persona haptics as rumble because its
                    // haptic route is down: the converted haptics stand in for
                    // the haptics it cannot play, the rule the DualShock 4
                    // already follows (Ds4EffectSynthesizer, rumbleReleased).
                    // The pass owns the rumble for this device at the persona
                    // level alone, and the claim published below carries it
                    // into the pass-through lane's frames, which the game keeps
                    // sending with bit 1 clear. Every other PadForge source
                    // keeps the grace.
                    if (isDs5 && gameDrivenRumble
                        && devOverrides.RumbleFlag0Bits == 0 && devOverrides.RumbleFlag2Bits == 0)
                    {
                        (byte right, byte left) persona = deliverRumble
                            ? SlotPersonaRumbleForDeviceProvider?.Invoke(_padIndex, ud.InstanceGuid) ?? ((byte)0, (byte)0)
                            : ((byte)0, (byte)0);
                        if ((persona.right | persona.left) != 0)
                        {
                            rR = persona.right;
                            rL = persona.left;
                            devOverrides.RumbleRight = null;
                            devOverrides.RumbleLeft = null;
                        }
                    }

                    // The lightbar has the same conflict and never got the
                    // same treatment. While a game drives the bar on a
                    // pass-through target, the pass-through is already
                    // forwarding the game's own bytes at up to 500 Hz, and
                    // this pass re-asserts a MIRRORED copy at 30 Hz. Two
                    // writers, one live and one a frame behind, and the pad
                    // visibly alternates between them.
                    //
                    // Measured (#300, r3213 USB trace): 135 lightbar writes
                    // with the enable bit SET against 6 without, all carrying
                    // the game's color, while the effect lane wrote 439 times
                    // a second. The reporter's description was exact: "an
                    // overlap between two, one lagging and the other not
                    // lagging, and the controller is flashing between them."
                    //
                    // Suppressing the ENABLE bit rather than the value is the
                    // point. Clearing the color alone would drop through to
                    // PadForge's own configured mode and still fight, just
                    // with a different color.
                    bool gameDrivenBar = devOverrides.LightbarRgb != null
                        && devOverrides.LightbarRgb.Length >= 3;
                    // The mirror override decays with the 1.5 s grace, but
                    // the pass-through re-asserts the game's bytes for its
                    // whole idle window (up to 15 s after the last packet).
                    // Suppress for as long as the pass-through HOLDS the
                    // bar, or a game rewriting it every few seconds
                    // re-opens the two-writer flashing this fixed. The
                    // pass-through's own release hands the identity bar
                    // back, so nothing is lost while suppressed.
                    //
                    // "Holds the bar" has to mean the BAR. This asked
                    // IsHoldingState, which is true whenever the lane has
                    // forwarded ANY effect payload, and shipped as #334:
                    // a host driving adaptive triggers or rumble and never
                    // touching the lightbar still took it away from the
                    // Lighting tab for the full 15 s idle window, so the
                    // pad sat on its firmware default blue and no color
                    // setting did anything. IsPassthroughTarget only asks
                    // that an online DualSense be mapped to the slot, so
                    // there was nothing else narrowing it either. The pips
                    // and the mic LED were unaffected because they gate
                    // through GateMirroredSubsystem, which is exactly why
                    // it presented as "only the lightbar is dead".
                    bool assertLightbar = ShouldAssertLightbar(
                        _padIndex, ud.InstanceGuid, gameDrivenBar, isDs5);

                    // A game mod's hold (#529) fills each subsystem a game is
                    // not writing within its grace window, after the lightbar
                    // decision above (so a mod's color never stands this pass
                    // down on a pass-through target, and a game driving the
                    // bar through the pass-through still owns it) and before
                    // the impulse and steering fills below (which see the
                    // mod's block and stand back). The pad page's settings
                    // apply where neither writes.
                    var modFill = FillFromModHold(devOverrides, modHold, isDs5);
                    devOverrides = modFill.Overrides;
                    // A macro's trigger effect (#531) fills a trigger neither the
                    // game nor a mod holds, before the impulse and steering fills,
                    // which then stand back as they do for a mod.
                    var macroFill = FillFromMacroEffects(devOverrides, macroLeftTrig, macroRightTrig, isDs5);
                    devOverrides = macroFill.Overrides;

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
                    float devPeak = Math.Clamp(
                        rawAudioPeak * (float)devCfg.AudioLightbarSensitivity,
                        0f, 1f);

                    // Per-device pulse color + intensity (DrainInputPulses
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

                    // One rumble state on the wire from both writers. The
                    // pass-through lane forwards a game's frames to this same
                    // DualSense, and a frame without haptics select puts the pad
                    // back on audio haptics, so PadForge's own rumble here was
                    // canceled by every forwarded frame while a game that plays
                    // audio haptics kept talking. Publish the rumble half of the
                    // frame the synthesizer builds below, from the same
                    // arguments, and the lane writes it into each game frame
                    // that does not select rumble itself. While the mirror holds
                    // a game's rumble the half is the game's, and nothing is
                    // claimed: the game's own frames carry it.
                    if (isDs5)
                        DualSensePassthroughDispatcher.PublishRumbleClaim(ud.InstanceGuid,
                            Ds5EffectSynthesizer.ResolveRumble(rR, rL, assertRumbleEnable, devOverrides),
                            Environment.TickCount64);

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
                    var rightTrigGate = GateMirroredSubsystem(
                        padForgeWantsRightAt, devOverrides.RightTriggerEffect != null, prevPadForgeWantsRightAt);
                    var leftTrigGate = GateMirroredSubsystem(
                        padForgeWantsLeftAt, devOverrides.LeftTriggerEffect != null, prevPadForgeWantsLeftAt);
                    // One more write of PadForge's own value when a mod's hold
                    // ends (#529). | rather than ||, so every pass records
                    // what it wrote.
                    bool assertRightTrig = rightTrigGate.Assert
                        | ModHoldEnded(_prevModRightTrig, ud.InstanceGuid, modFill.RightTrigger)
                        | ModHoldEnded(_prevMacroRightTrig, ud.InstanceGuid, macroFill.RightTrigger);
                    bool assertLeftTrig  = leftTrigGate.Assert
                        | ModHoldEnded(_prevModLeftTrig, ud.InstanceGuid, modFill.LeftTrigger)
                        | ModHoldEnded(_prevMacroLeftTrig, ud.InstanceGuid, macroFill.LeftTrigger);

                    // Mic LED, same rule as the triggers. FollowDeviceMute
                    // counts as a deliberate intent: it resolves to Off or
                    // Solid per frame and PadForge must author either.
                    bool padForgeWantsMicLed = devCfg != null && devCfg.MicLedMode != MicLedMode.Off;
                    bool prevWantsMicLed = _prevPadForgeWantsMicLed.TryGetValue(ud.InstanceGuid, out var pm) && pm;
                    var micLedGate = GateMirroredSubsystem(
                        padForgeWantsMicLed, devOverrides.MuteLed.HasValue, prevWantsMicLed);
                    bool assertMicLed = micLedGate.Assert
                        | ModHoldEnded(_prevModMicLed, ud.InstanceGuid, modFill.MuteLight);
                    _prevPadForgeWantsMicLed[ud.InstanceGuid] = micLedGate.NextPrev;

                    // Player indicator, same rule. PadForge authors the row
                    // when the user picked a deliberate PlayerLedMode, or
                    // while the #191 identity floor is still armed because
                    // nothing external has ever claimed the pips. Once
                    // something has, the floor yields for the session
                    // exactly as the lightbar's does, so an SDL or Steam
                    // Input player-index assignment persists in firmware
                    // instead of being overwritten 1.5 s later.
                    bool padForgeWantsPips = devCfg == null
                        || devCfg.PlayerLedMode != PlayerLedMode.PlayerNumber
                        || !devOverrides.PlayerIndicatorEverExternal;
                    bool prevWantsPips = _prevPadForgeWantsPips.TryGetValue(ud.InstanceGuid, out var pp) && pp;
                    var pipsGate = GateMirroredSubsystem(
                        padForgeWantsPips, devOverrides.PlayerIndicator.HasValue, prevWantsPips);
                    bool assertPips = pipsGate.Assert
                        | ModHoldEnded(_prevModPips, ud.InstanceGuid, modFill.PlayerLights);
                    _prevPadForgeWantsPips[ud.InstanceGuid] = pipsGate.NextPrev;

                    // Bounded unmute burst. Counts down per dispatch and
                    // then stops, so an external program's deliberate mute
                    // survives instead of being reversed the next frame.
                    int muteBurst = _audioMuteBurstLeft.TryGetValue(ud.InstanceGuid, out var mb)
                        ? mb : AudioMuteBurstFrames;
                    bool assertAudioMute = muteBurst > 0;
                    _audioMuteBurstLeft[ud.InstanceGuid] = muteBurst > 0 ? muteBurst - 1 : 0;

                    // Headphone volume: assert the enable bit while the
                    // audio-hardware claim burst runs or when the configured
                    // value differs from the one the pad last received
                    // (slider, macro, profile apply). Idle, the bit stays
                    // clear and the firmware retains state.
                    int hpVol = devCfg?.HeadphoneVolume ?? 100;
                    // Output path: a non-Automatic choice is asserted on the
                    // claim burst or a change. Automatic asserts only the
                    // headphone path's return (the speaker block below).
                    int pathVal = AudioPassthroughService.ResolveOutputPath(
                        devCfg != null ? (int)devCfg.AudioOutputPath : 0, ud.InstanceGuid);
                    bool ds4Bt = !isDs5 && isBluetooth;
                    bool ds4MicArmed = ds4Bt && AudioPassthroughService.IsDs4MicArmed(ud.InstanceGuid);
                    // Compared with what the pad last received. The values
                    // are committed when this frame lands (CommitDeliveredAudio).
                    bool hpChanged, pathChanged, ds4MicWasArmed;
                    int lastUsbRoute, lastPath;
                    lock (_audioPrevGate)
                    {
                        hpChanged = !_prevHeadphoneVolume.TryGetValue(ud.InstanceGuid, out var prevHp)
                            || prevHp != hpVol;
                        pathChanged = !_prevAudioOutputPath.TryGetValue(ud.InstanceGuid, out lastPath)
                            || lastPath != pathVal;
                        ds4MicWasArmed = _prevDs4MicArmed.TryGetValue(ud.InstanceGuid, out var dma) && dma;
                        lastUsbRoute = _prevDs4UsbRoute.TryGetValue(ud.InstanceGuid, out var lur) ? lur : -1;
                    }
                    bool assertHeadphone = (assertAudioMute || hpChanged) && !s_audioRoutesReleased;
                    // Released routes stay released until the engine starts.
                    bool assertAudioCtl = pathVal > 0 && (assertAudioMute || pathChanged)
                        && !s_audioRoutesReleased;

                    // DualShock 4 on Bluetooth (#528). Byte 2 is 0xA1 while its
                    // headset microphone is armed and 0xA0 otherwise, and the
                    // microphone gain is asserted when arming starts. Its route
                    // lives in the audio stream's target byte, so this packet
                    // carries the route's volumes instead (the jack routes mute
                    // the speaker, Speaker Only mutes the jack), asserted on the
                    // DualSense rule: the claim burst, a level change, a route
                    // change. On USB none of these bytes apply: Windows keeps
                    // the headset's level, and a first-model pad has no USB
                    // audio.
                    var (ds4Headphone, ds4Speaker) = Ds4BtAudio.VolumesFor(pathVal, hpVol);
                    bool assertDs4Volumes = ds4Bt && (assertAudioMute || hpChanged || pathChanged);
                    bool assertDs4Mic = ds4MicArmed && (!ds4MicWasArmed || assertAudioMute);
                    // DualShock 4 on USB (#546): a wired second model or a pad
                    // on Sony's adapter takes its route as feature report 0xE0,
                    // written when it changes (Ds4UsbAudio.RouteToWrite) and
                    // committed with this frame's delivery. A peer's owner
                    // routes its own pad.
                    bool ds4Usb = !isDs5 && !isBluetooth
                        && AudioPassthroughService.Ds4HasAudio((ushort)ud.ProdId, false)
                        && !RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath);
                    int ds4UsbRoute = ds4Usb && !s_audioRoutesReleased
                        ? Ds4UsbAudio.RouteToWrite(pathVal, PlaysToSpeaker(ud.InstanceGuid), lastUsbRoute,
                            s_routedOffHeadset.ContainsKey(ud.InstanceGuid))
                        : -1;
                    if (ds4UsbRoute < 0) _usbRouteRetryAt.TryRemove(ud.InstanceGuid, out _);
                    else if (!UsbRouteRetryDue(ud.InstanceGuid, nowMs)) ds4UsbRoute = -1;
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
                    _prevPadForgeWantsRightTrig[ud.InstanceGuid] = rightTrigGate.NextPrev;
                    _prevPadForgeWantsLeftTrig[ud.InstanceGuid]  = leftTrigGate.NextPrev;

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
                        // same device: per PlayStationEffectWriter's docstring,
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
                        // What keeps audio rumble from pulsing:
                        //   1. A frame that does not author rumble claims
                        //      none. ResolveRumble clears every rumble bit, so
                        //      its motor bytes write over nothing SDL sent (#434).
                        //   2. The rumble bytes of a frame that does author
                        //      rumble include the audio mix (when audio rumble
                        //      is enabled), so the dispatcher reinforces SDL's
                        //      audio rumble rather than fighting it.
                        //      SlotRumbleForDeviceProvider runs
                        //      ScaleRumbleForDevice for this: it pulls raw
                        //      VibrationStates (game rumble) and mixes audio
                        //      in, yielding the same value SDL sends.
                        //
                        // For test-rumble target gating, rR/rL are zero on
                        // non-target devices.
                        // The BT connect one-shot rides this device's next
                        // built frame. Spent on delivery, not here: a frame
                        // the stale-skip drops must leave it armed.
                        bool btConnectRelease = isDs5
                            && _btBarReleasePending.ContainsKey(ud.InstanceGuid);
                        var fields = isDs5
                            ? Ds5EffectSynthesizer.BuildFields(
                                devCfg, devPeak, nowMs,
                                _randomColor, devPulseColor, devPulseIntensity,
                                rR, rL, assertRumbleEnable,
                                assertRightTrig, assertLeftTrig, devOverrides, pctByte,
                                devPlayerNumber, assertMicLed, assertAudioMute,
                                assertPips, hpVol, assertHeadphone,
                                pathVal, assertAudioCtl, assertLightbar,
                                btConnectRelease)
                            : Ds4EffectSynthesizer.BuildFields(
                                devCfg, devPeak, nowMs,
                                _randomColor, devPulseColor, devPulseIntensity,
                                rR, rL, assertRumbleEnable, devOverrides, pctByte,
                                devPlayerNumber,
                                ds4Headphone, ds4Speaker, assertDs4Volumes,
                                ds4MicArmed ? Ds4BtAudio.ModeMicrophone : Ds4BtAudio.ModeSpeaker,
                                assertDs4Mic, ds4UsbRoute);

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
                        //     drives it to. Encoded by PlayStationEffectWriter's
                        //     audioControl2 poke (the HM profile doesn't
                        //     declare the byte).
                        // When routing switches away, restore the headphone
                        // path once so the speaker doesn't stay latched.
                        Guid speakerCleared = Guid.Empty;
                        if (isDs5)
                        {
                            // An explicit user Output Path owns byte 7 and its
                            // enable bit; the #83 macro-speaker block keeps only
                            // its speaker-VOLUME half. Automatic preserves the
                            // old behavior byte for byte.
                            bool userPathForced = pathVal > 0;
                            if (PlaysToSpeaker(ud.InstanceGuid))
                            {
                                int master = SoundMacroService.GetSlotVolume(_padIndex);
                                byte spkVol = master <= 0
                                    ? (byte)0
                                    : (byte)(0x3D + master * (0x64 - 0x3D) / 100);
                                fields["validFlag0"] = (byte)((byte)fields["validFlag0"]
                                    | (userPathForced ? 0x20 : 0xA0));
                                fields["validFlag1"] = (byte)((byte)fields["validFlag1"] | 0x80);
                                fields["speakerVolume"] = spkVol;
                                if (!userPathForced)
                                    fields["audioControlFlags"] = (byte)(3 << 4);
                                fields["audioControl2"] = (byte)3;
                            }
                            else
                            {
                                // Automatic puts the headphone path back once:
                                // after a speaker sink is torn down (the
                                // one-shot), when it replaces an explicit path
                                // that moved the pad off the headphones, or for
                                // a pad another slot left off them (#546, as a
                                // DualShock 4's USB route does). The one-shot is
                                // spent either way, so it cannot fire a stale
                                // restore later. A forced path needs no restore:
                                // the firmware holds the user's choice.
                                bool cleared = AudioPassthroughService.PeekSpeakerPathCleared(ud.InstanceGuid);
                                if (!userPathForced
                                    && (cleared || lastPath > (int)AudioOutputPath.StereoHeadset
                                        || s_routedOffHeadset.ContainsKey(ud.InstanceGuid)))
                                {
                                    fields["validFlag0"] = (byte)((byte)fields["validFlag0"] | 0x80);
                                    fields["validFlag1"] = (byte)((byte)fields["validFlag1"] | 0x80);
                                    fields["audioControlFlags"] = (byte)0;
                                    fields["audioControl2"] = (byte)0;
                                }
                                if (cleared) speakerCleared = ud.InstanceGuid;
                            }
                        }

                        // Final lightbar bytes as they go to the pad. The bar
                        // has to survive the synthesizer, the speaker-path
                        // merge and the drop-frame gate, and a dark bar looks
                        // identical at every stage. Logs on CHANGE plus a slow
                        // heartbeat, so a blanking write is visible as an
                        // event rather than buried in a steady stream.
                        {
                            byte tvf1 = (byte)fields["validFlag1"];
                            var trgb = fields.TryGetValue("lightbar", out var tv) ? tv as byte[] : null;
                            // The keyboard screens' Lightbar mode (#519) shows
                            // the color the pad takes: a DualShock 4 frame
                            // always asserts the bar (validFlag0 0x07), a
                            // DualSense frame with its enable bit.
                            if (trgb != null && trgb.Length >= 3 && (!isDs5 || (tvf1 & 0x04) != 0))
                                PadForge.Common.Input.KeyboardScreens.LightbarColors.Record(
                                    _padIndex, ud.InstanceGuid, trgb[0], trgb[1], trgb[2]);
                            string sig = $"{tvf1:X2}/{(trgb != null ? $"{trgb[0]},{trgb[1]},{trgb[2]}" : "none")}";
                            long tnow = Environment.TickCount64;
                            if (sig != _lbLastSig || tnow - _lbFieldsLastLog >= 5000)
                            {
                                _lbLastSig = sig;
                                _lbFieldsLastLog = tnow;
                                PadForge.Engine.SdlDiagLog.WriteLine(
                                    $"LIGHTBAR out vf1=0x{tvf1:X2} ledBit={((tvf1 & 0x04) != 0)}"
                                    + $" rgb={(trgb != null ? $"{trgb[0]},{trgb[1]},{trgb[2]}" : "none")}"
                                    + $" vf2=0x{(fields.TryGetValue("validFlag2", out var t2) ? (byte)t2 : (byte)0):X2}"
                                    + $" setup=0x{(fields.TryGetValue("lightbarSetup", out var ts) ? (byte)ts : (byte)0):X2}");
                            }
                        }

                        // Sequence stamped under devices.SyncRoot, so sequence
                        // order is capture order across every dispatching
                        // thread. The write loop below uses it to drop a
                        // capture that a newer one has already superseded.
                        pending.Add((ud.InstanceGuid, path, profile, fields,
                            System.Threading.Interlocked.Increment(ref s_dispatchSeq),
                            recoveredThrough,
                            speakerCleared,
                            btConnectRelease ? ud.InstanceGuid : Guid.Empty,
                            (hpVol, pathVal, ds4MicArmed, ds4UsbRoute)));
                    }
                    catch
                    {
                        // Best-effort: a failed synthesis on one device shouldn't
                        // prevent the dispatcher from servicing the rest of
                        // the slot's mapped Sony pads on the next tick.
                    }
                }
            }

            _litPeripherals = litPeripherals;
            _peripheralKinds = peripheralKinds ?? EmptyPeripheralKinds;

            // Lock released. Do the blocking HID I/O here, serialized against
            // every other dispatch's writes and ordered by capture sequence so
            // the newest captured frame is the one that survives on each
            // device. See the s_writeGate notes at the field declaration for
            // why this gate is a leaf and not an outer dispatch lock.
            if (pending.Count == 0) return;
            bool deliveryStateChanged = false;
            List<(Guid Device, long Seq, (int HeadphoneVolume, int OutputPath, bool Ds4MicArmed, int Ds4UsbRoute) Audio)> delivered = null;
            lock (s_writeGate)
            {
                foreach (var w in pending)
                {
                    try
                    {
                        // A newer capture already landed on this device: this
                        // frame is stale and writing it would undo the newer
                        // one. That is the latched-rumble case, where the stale
                        // frame carries motor bytes the newer one just zeroed
                        // and no further dispatch is scheduled to correct it.
                        if (s_lastWriteSeq.TryGetValue(w.Path, out var last) && last > w.Seq)
                            continue;
                        if (w.Seq < System.Threading.Interlocked.Read(ref s_audioReleaseSeq)
                            && (DeliveredRoute(w.Fields, out _) > 0 || LowersHeadphones(w.Fields, out _)))
                            continue;
                        // A prebuilt frame has not re-armed the transitions
                        // lost by a later failure. Let the retry build fresh state.
                        if (_failedDeliveries.TryGetValue(w.Device, out long failedAt)
                            && failedAt > w.RecoveredThrough)
                        {
                            deliveryStateChanged = true;
                            continue;
                        }
                        s_lastWriteSeq[w.Path] = w.Seq;
                        if (!PlayStationEffectWriter.Write(w.Path, w.Profile, w.Fields, out bool routeLanded))
                        {
                            _failedDeliveries[w.Device] = w.Seq;
                            deliveryStateChanged = true;
                            continue;
                        }
                        deliveryStateChanged |= _failedDeliveries.TryRemove(w.Device, out _);
                        // Spend routing transitions after the writer accepts
                        // this frame. Rejected or superseded writes leave them
                        // pending for a fresh retry.
                        if (w.SpeakerCleared != Guid.Empty)
                            AudioPassthroughService.TryConsumeSpeakerPathCleared(w.SpeakerCleared);
                        // Same delivery contract for the BT LED release: the
                        // bytes went out, so the one-shot is spent. A dropped
                        // or failed frame leaves it armed for the next tick.
                        if (w.BtBarReleased != Guid.Empty)
                            _btBarReleasePending.TryRemove(w.BtBarReleased, out _);
                        // Same contract for the route the pad now plays on and
                        // its Headphone Volume, which ReleaseAudioRoutes reads
                        // when PadForge lets go. A route the pad refused is not
                        // one it plays on, and the frame commits without it.
                        var audio = w.Audio;
                        if (routeLanded) NoteDeliveredRoute(w.Device, w.Path, w.Profile, w.Fields);
                        else
                        {
                            audio.Ds4UsbRoute = -1;
                            _usbRouteRetryAt[w.Device] = Environment.TickCount64 + UsbRouteRetryMs;
                            deliveryStateChanged = true;
                        }
                        NoteDeliveredHeadphoneVolume(w.Device, w.Path, w.Profile, w.Fields);
                        // The release ran during this write and could not wait
                        // for it, so it put back nothing, and never saw what the
                        // frame just listed.
                        if (w.Seq < System.Threading.Interlocked.Read(ref s_audioReleaseSeq))
                            LetGoOfEveryListedPad();
                        // The audio values are committed once the gate is
                        // released, which takes nothing while held.
                        (delivered ??= new()).Add((w.Device, w.Seq, audio));
                    }
                    catch
                    {
                        _failedDeliveries[w.Device] = w.Seq;
                        deliveryStateChanged = true;
                    }
                }
            }
            if (delivered != null)
                foreach (var d in delivered)
                    CommitDeliveredAudio(d.Device, d.Seq, d.Audio);
            if (deliveryStateChanged) UpdateAnimTimer();
        }
    }
}
