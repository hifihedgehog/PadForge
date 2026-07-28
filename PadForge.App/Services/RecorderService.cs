using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>
    /// Service that handles input recording for mapping assignment.
    /// When the user clicks "Record" on a mapping row, this service:
    ///   1. Captures the current input state as a baseline.
    ///   2. Polls at 30Hz for significant changes (axis movement or button press).
    ///   3. When a change is detected, identifies the source (type + index)
    ///      and writes the descriptor string to the MappingItem.
    ///   4. Stops recording automatically after detection or timeout.
    /// 
    /// Thread model: all operations run on the UI thread via DispatcherTimer.
    /// Reads from UserDevice.InputState which is atomically swapped by the engine.
    /// </summary>
    public class RecorderService : IDisposable
    {
        // ─────────────────────────────────────────────
        //  Constants
        // ─────────────────────────────────────────────

        /// <summary>Recording poll interval in milliseconds (~30Hz).</summary>
        private const int PollIntervalMs = 33;

        /// <summary>Recording timeout in seconds.</summary>
        private const int TimeoutSeconds = 10;

        /// <summary>
        /// Axis movement threshold (unsigned). An axis must move at least this
        /// much from the baseline to be detected. ~25% of full range.
        /// </summary>
        private const int AxisThreshold = 16384;

        /// <summary>
        /// Minimum number of poll cycles an axis must be held past the threshold
        /// before being accepted. Prevents accidental detection from noise.
        /// </summary>
        private const int AxisHoldCycles = 3;

        /// <summary>MIDI CC movement threshold (0-127) for the recorder to
        /// accept a controller turn as the bound input.</summary>
        private const int MidiCcThreshold = 10;

        /// <summary>MIDI pitch-bend movement threshold (0-65535) from the
        /// baseline for the recorder to accept a bend as the bound input.</summary>
        private const int MidiPitchThreshold = 6000;

        /// <summary>A CC value within this distance of center (0x40) is read
        /// as a relative encoder (pulse buttons), not an absolute fader.
        /// Matches MidiInputDevice.RelativeMax.</summary>
        private const int MidiRelativeBand = 16;

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;

        // Per-CC "this CC has shown absolute-fader travel (read outside the
        // 48..80 relative band) during the current recording". A real encoder
        // never leaves the band; a fader sweeping does. Used to stop a fader
        // caught mid-sweep being mis-captured as an encoder pulse (issue #142 b).
        // Cleared when a recording starts.
        private readonly bool[] _ccLeftBand = new bool[MidiInputState.CcCount];
        private DispatcherTimer _timer;
        private bool _disposed;

        /// <summary>The mapping item currently being recorded.</summary>
        private MappingItem _activeMapping;

        /// <summary>When non-null, recording is targeting an ExtraSource
        /// row inside <see cref="_activeMapping"/>'s ExtraSources list
        /// instead of the row's primary descriptor.</summary>
        private MappingSourceItem _activeExtraSource;

        /// <summary>The pad index (0–15) of the active recording.</summary>
        private int _activePadIndex = -1;

        /// <summary>True when recording for the negative direction of an axis.</summary>
        private bool _negRecording;

        /// <summary>Devices the recorder is listening to in the current
        /// session, keyed by InstanceGuid. Filled at StartRecording-time
        /// from the slot's MappedDevices so any input on any assigned
        /// device claims the row.</summary>
        private readonly Dictionary<Guid, UserDevice> _activeDevices = new();

        /// <summary>Per-device baseline state captured at the start of
        /// recording. Each tick clones the device's current state and
        /// diffs against the entry here.</summary>
        private readonly Dictionary<Guid, CustomInputState> _baselines = new();

        /// <summary>Per-device "is this a mouse-class device" flag —
        /// mouse axes are instantaneous deltas and skip the axis-hold
        /// confirmation cycles.</summary>
        private readonly Dictionary<Guid, bool> _isMouseByDevice = new();

        /// <summary>Per-device axis candidate hold tracker. The same
        /// device must clear the threshold for AxisHoldCycles in a row
        /// to avoid false positives from noise.</summary>
        private sealed class AxisCandidate
        {
            public MapType Type = MapType.None;
            public int Index = -1;
            public bool Positive;
            public int HoldCounter;
        }
        private readonly Dictionary<Guid, AxisCandidate> _axisCandidates = new();

        /// <summary>When recording started (for timeout).</summary>
        private DateTime _recordingStartTime;

        /// <summary>Gesture fire-set snapshot at recording start, as
        /// "slot|deviceGuid|firedKey" composites. PollTick treats any
        /// fired gesture NOT in this set as a rising edge and records
        /// its descriptor, so an enabled swipe / tap / touch spot is
        /// recordable straight from a mapping row's Record button. The
        /// baseline means a finger already resting on a touch spot when
        /// recording starts cannot hijack the session.</summary>
        private readonly HashSet<string> _gestureBaseline = new();

        /// <summary>Gesture candidates collected during the current touch
        /// session, keyed by device. Completion waits for THAT device's
        /// fingers to lift so a touch spot (which fires the instant a
        /// finger lands) cannot steal the recording from a swipe / tap /
        /// shape whose key only appears at the lift transition. Non-spot
        /// keys win; the LAST spot the finger held wins among spots.
        /// Per-device keying matters: with two touch devices in the
        /// session, device B's zero-finger state must not release a
        /// candidate collected from device A mid-hold (and mis-attribute
        /// the descriptor to B's GUID).</summary>
        private readonly Dictionary<Guid, string> _gestureNonSpotByDevice = new();
        private readonly Dictionary<Guid, string> _gestureSpotByDevice = new();

        private static bool IsTouchSpotKey(string firedKey) =>
            firedKey.EndsWith("TouchLeft", StringComparison.Ordinal)
            || firedKey.EndsWith("TouchRight", StringComparison.Ordinal)
            || firedKey.EndsWith("TouchTop", StringComparison.Ordinal)
            || firedKey.EndsWith("TouchMulti", StringComparison.Ordinal);

        /// <summary>When recording an Incremental or InvertOnHold source's
        /// Param button (ParamUp / ParamDown / ParamModifier), names the
        /// destination field so the recorder writes the captured descriptor
        /// there instead of overwriting the source's main
        /// <see cref="MappingSourceItem.Descriptor"/>.</summary>
        public enum ParamTarget { None, Up, Down, Modifier }
        private ParamTarget _paramTarget = ParamTarget.None;

        /// <summary>
        /// When true, the recorder waits for all buttons and POVs to return to neutral
        /// before detecting new inputs. Used for follow-up recordings where the previous
        /// input may still be physically held.
        /// </summary>
        private bool _waitForRelease;

        /// <summary>When non-null, recording is targeting a freeform
        /// consumer (e.g. the Shift activator dialog's input picker)
        /// instead of a MappingItem. The callback receives
        /// (deviceGuidLowerInvariant, descriptor) on success.</summary>
        private Action<string, string> _freeformCallback;

        /// <summary>Whether recording is currently active.</summary>
        public bool IsRecording => _activeMapping != null || _freeformCallback != null;

        // ─────────────────────────────────────────────
        //  Events
        // ─────────────────────────────────────────────

        /// <summary>Raised when a mapping is successfully recorded.</summary>
        public event EventHandler<RecordingResult> RecordingCompleted;

        /// <summary>Raised when recording times out without detection.</summary>
        public event EventHandler RecordingTimedOut;

        // ─────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────

        public RecorderService(MainViewModel mainVm)
        {
            _mainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        }

        // ─────────────────────────────────────────────
        //  Start / Stop recording
        // ─────────────────────────────────────────────

        /// <summary>
        /// Starts recording input for the specified mapping item.
        /// If another recording is active, it is cancelled first.
        /// </summary>
        /// <param name="mapping">The mapping item to record a source for.</param>
        /// <param name="padIndex">The pad index (0–15) to read input from.</param>
        /// <param name="deviceGuid">The specific device GUID to record from.</param>
        /// <param name="neutralizeBaseline">When true, forces all buttons and POVs to
        /// their neutral state in the baseline so that any press (even if currently held)
        /// is detected as a fresh change. Used for auto-prompt follow-up recordings
        /// where the previous input may still be physically held.</param>
        public void StartRecording(MappingItem mapping, int padIndex, Guid deviceGuid,
            bool neutralizeBaseline = false, bool negRecording = false)
        {
            _paramTarget = ParamTarget.None;
            StartRecordingInternal(mapping, extraSource: null, padIndex,
                neutralizeBaseline, negRecording);
        }

        /// <summary>Starts cross-device recording for an ExtraSource row.
        /// The recorder listens to every device assigned to the slot and
        /// the first device to fire a button / POV / axis change wins,
        /// writing both <see cref="MappingSourceItem.DeviceGuid"/> and
        /// <see cref="MappingSourceItem.Descriptor"/> to the source.
        /// <para><paramref name="extraSource"/> may be a DETACHED
        /// <see cref="MappingSourceItem"/> not yet in
        /// <see cref="MappingItem.ExtraSources"/> — the
        /// <see cref="RecordingCompleted"/> handler appends it once the
        /// descriptor is in. Pass <paramref name="negRecording"/>=true when
        /// the source is being recorded for the negative quadrant of a
        /// bipolar axis: a button recorded that way is stored with
        /// Invert=true so a press drives the axis to -1.</para></summary>
        public void StartRecordingExtraSource(MappingItem parent,
            MappingSourceItem extraSource, int padIndex,
            bool neutralizeBaseline = false, bool negRecording = false)
        {
            _paramTarget = ParamTarget.None;
            StartRecordingInternal(parent, extraSource, padIndex,
                neutralizeBaseline, negRecording);
        }

        /// <summary>Starts cross-device recording that captures a button
        /// descriptor for one of an ExtraSource row's Param fields
        /// (ParamUp / ParamDown / ParamModifier). Used by the Record
        /// buttons next to the Incremental / InvertOnHold pickers.
        /// Routes through the same poll loop as
        /// <see cref="StartRecordingExtraSource"/>; the only difference
        /// is the write target in <see cref="CompleteRecording"/>.</summary>
        public void StartRecordingExtraSourceParam(MappingItem parent,
            MappingSourceItem extraSource, int padIndex, ParamTarget target)
        {
            _paramTarget = target;
            StartRecordingInternal(parent, extraSource, padIndex,
                neutralizeBaseline: false, negRecording: false);
        }

        /// <summary>Starts a freeform recording session that doesn't write
        /// into a MappingItem. The first button / POV / axis change on
        /// any device assigned to <paramref name="padIndex"/> wins; the
        /// captured (deviceGuid, descriptor) is delivered to
        /// <paramref name="onComplete"/>. Used by the shift activator
        /// dialog so the user can Record an activator input from the
        /// same controller they're configuring.</summary>
        public void StartRecordingFreeform(int padIndex, Action<string, string> onComplete)
        {
            if (onComplete == null) return;

            // Cancel any in-flight recording (MappingItem-based or freeform).
            if (_activeMapping != null) CancelRecording();
            if (_freeformCallback != null) CancelRecording();

            _freeformCallback = onComplete;
            _activePadIndex = padIndex;
            _paramTarget = ParamTarget.None;
            _negRecording = false;

            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            if (padIndex >= 0 && padIndex < _mainVm.Pads.Count)
            {
                var padVm = _mainVm.Pads[padIndex];
                var seen = new HashSet<Guid>();
                foreach (var md in padVm.MappedDevices)
                {
                    if (md == null || md.InstanceGuid == Guid.Empty) continue;
                    if (!seen.Add(md.InstanceGuid)) continue;
                    var udLookup = SettingsManager.UserDevices?.Items;
                    if (udLookup == null) continue;
                    UserDevice ud;
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        ud = udLookup.FirstOrDefault(d =>
                            d.InstanceGuid == md.InstanceGuid && d.IsOnline);
                    }
                    if (ud == null || ud.InputState == null) continue;
                    _activeDevices[md.InstanceGuid] = ud;
                    _baselines[md.InstanceGuid] = ud.InputState.Clone();
                    _isMouseByDevice[md.InstanceGuid] = ud.IsMouse;
                    _axisCandidates[md.InstanceGuid] = new AxisCandidate();
                }
            }

            if (_baselines.Count == 0)
            {
                _freeformCallback = null;
                _activePadIndex = -1;
                _mainVm.SetStatus(Strings.Instance.Status_NoDeviceToRecord, persist: true);
                RecordingTimedOut?.Invoke(this, EventArgs.Empty);
                return;
            }

            _recordingStartTime = DateTime.UtcNow;
            SnapshotGestureBaseline();
            _waitForRelease = false;

            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _timer.Tick += PollTick;
            _timer.Start();
        }

        private void StartRecordingInternal(MappingItem mapping,
            MappingSourceItem extraSource, int padIndex,
            bool neutralizeBaseline, bool negRecording)
        {
            if (mapping == null)
                return;

            // Cancel any existing recording (MappingItem-based or freeform,
            // matching the freeform starter above: a live freeform session
            // would otherwise leak its started timer and stuck callback).
            if (_activeMapping != null || _freeformCallback != null)
                CancelRecording();

            _activeMapping = mapping;
            _activeExtraSource = extraSource;
            _activePadIndex = padIndex;
            _negRecording = negRecording;

            // Collect every device assigned to the slot. The first one
            // to fire wins; this is what makes the Mappings tab a
            // genuinely per-VC view (the Device dropdown to the right
            // doesn't gate which device the user can record from).
            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            if (padIndex >= 0 && padIndex < _mainVm.Pads.Count)
            {
                var padVm = _mainVm.Pads[padIndex];
                var seen = new HashSet<Guid>();
                foreach (var md in padVm.MappedDevices)
                {
                    if (md == null || md.InstanceGuid == Guid.Empty) continue;
                    if (!seen.Add(md.InstanceGuid)) continue;
                    var udLookup = SettingsManager.UserDevices?.Items;
                    if (udLookup == null) continue;
                    UserDevice ud;
                    lock (SettingsManager.UserDevices.SyncRoot)
                    {
                        ud = udLookup.FirstOrDefault(d =>
                            d.InstanceGuid == md.InstanceGuid && d.IsOnline);
                    }
                    if (ud == null || ud.InputState == null) continue;
                    _activeDevices[md.InstanceGuid] = ud;
                    _baselines[md.InstanceGuid] = ud.InputState.Clone();
                    _isMouseByDevice[md.InstanceGuid] = ud.IsMouse;
                    _axisCandidates[md.InstanceGuid] = new AxisCandidate();
                }
            }

            if (_baselines.Count == 0)
            {
                _activeMapping = null;
                _activeExtraSource = null;
                _mainVm.SetStatus(Strings.Instance.Status_NoDeviceToRecord, persist: true);
                RecordingTimedOut?.Invoke(this, EventArgs.Empty);
                return;
            }

            _recordingStartTime = DateTime.UtcNow;
            SnapshotGestureBaseline();

            // For follow-up recordings, wait for the previous input to be released
            // before detecting new presses. This prevents the same POV/button from
            // being immediately re-detected when it's still physically held.
            _waitForRelease = neutralizeBaseline;

            // The per-CC "this fader has left the relative band" flags are
            // per-RECORDING state. Their only reset sat behind
            // "baseline.Midi == null", which a polled MIDI device never
            // satisfies (MidiInputDevice builds its state with a non-null
            // Midi from birth), so a fader that swept during one recording
            // stayed flagged for every later one and its encoder pulses were
            // ignored forever. Reset where the recording actually starts.
            Array.Clear(_ccLeftBand, 0, _ccLeftBand.Length);

            // Mark the recording target.
            if (extraSource != null)
                extraSource.IsRecording = true;
            else
                mapping.IsRecording = true;

            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _timer.Tick += PollTick;
            _timer.Start();

            // Prompt persists through the decay sweep: every session ends in
            // a recorded / cancelled / timed-out write, so it cannot burn in.
            _mainVm.SetStatus(string.Format(Strings.Instance.Status_RecordingPrompt_Format, mapping.TargetLabel), persist: true);
        }

        /// <summary>
        /// Cancels the active recording without assigning a source.
        /// </summary>
        public void CancelRecording()
        {
            // Cancel covers both MappingItem-bound and freeform sessions.
            // Bail when neither is active.
            if (_activeMapping == null && _freeformCallback == null)
                return;

            if (_activeExtraSource != null) _activeExtraSource.IsRecording = false;
            else if (_activeMapping != null) _activeMapping.IsRecording = false;
            CleanupTimer();

            _activeMapping = null;
            _activeExtraSource = null;
            _freeformCallback = null;
            _activePadIndex = -1;
            _paramTarget = ParamTarget.None;
            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            _mainVm.StatusText = Strings.Instance.Status_RecordingCancelled;
        }

        // ─────────────────────────────────────────────
        //  Poll tick (30Hz, UI thread)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called ~30 times per second while recording. Compares current state
        /// against the baseline to detect which input changed.
        /// </summary>
        /// <summary>Snapshots every active device's currently-fired
        /// gestures across all slots into <see cref="_gestureBaseline"/>.
        /// Called when a recording session (re)establishes its baselines.
        /// The gesture contexts live on the engine's InputManager, keyed
        /// (slot, deviceGuid, padIdx); the fired keys already carry the
        /// pad index ("Touchpad 0 TouchLeft") so slot + guid + key is a
        /// unique composite.</summary>
        private void SnapshotGestureBaseline()
        {
            _gestureBaseline.Clear();
            _gestureNonSpotByDevice.Clear();
            _gestureSpotByDevice.Clear();
            var mgr = Views.PadPage.InputService?.Engine;
            if (mgr == null) return;
            try
            {
                foreach (var kv in mgr.GestureContexts)
                {
                    if (!_activeDevices.ContainsKey(kv.Key.Item2)) continue;
                    foreach (var fired in kv.Value.FiredGesturesThisFrame)
                        _gestureBaseline.Add($"{kv.Key.Item1}|{kv.Key.Item2}|{fired}");
                }
            }
            catch
            {
                // The fired sets mutate on the polling thread. A torn
                // enumeration here only costs baseline entries, and a
                // missing baseline entry errs toward recording, which
                // the next tick's detection re-validates anyway.
            }
        }

        /// <summary>Collects rising-edge gesture fires for this device
        /// into the candidate pair, then returns the winning descriptor
        /// once the touch session ends (no active fingers left on any of
        /// the device's pads), or null while the session is still live.
        /// Scoped to the recording's slot: the recognizer gates per
        /// (slot, device, pad), so a gesture enabled only on ANOTHER
        /// slot must not record here (its descriptor would be dead on
        /// this slot's rows). Waiting for lift lets a swipe / tap /
        /// shape key (which only appears at the lift transition) beat
        /// the touch spot that fired the instant the finger landed.
        /// Cross-thread enumeration of the polling thread's fired sets
        /// is guarded: a torn read skips this tick and retries 33 ms
        /// later.</summary>
        private string DetectNewGestureFire(Guid deviceGuid)
        {
            var mgr = Views.PadPage.InputService?.Engine;
            if (mgr == null) return null;
            bool anyActiveFingers = false;
            try
            {
                foreach (var kv in mgr.GestureContexts)
                {
                    if (kv.Key.Item2 != deviceGuid) continue;
                    if (_activePadIndex >= 0 && kv.Key.Item1 != _activePadIndex) continue;
                    if (kv.Value.ActiveFingerCount > 0) anyActiveFingers = true;

                    // Self-healing baseline: a baseline entry whose key is
                    // no longer in the fired set has finished its latch
                    // window, so a LATER fire of the same gesture is a
                    // genuine rising edge. Without this, a one-shot key
                    // still latched when the session started (the
                    // automatic bipolar follow-up recording lands inside
                    // the cooldown window) would be unrecordable for the
                    // whole session. Scoped to THIS context's pad: the
                    // fired keys carry their pad index ("Touchpad 1 ..."),
                    // and pad 0's context must not clear pad 1's baseline
                    // (pad 1's keys are never in pad 0's fired set, so an
                    // unscoped sweep would strip the resting-finger
                    // protection on every other pad of the device).
                    string pfx = $"{kv.Key.Item1}|{kv.Key.Item2}|";
                    string padToken = $"Touchpad {kv.Key.Item3} ";
                    _gestureBaseline.RemoveWhere(c =>
                        c.StartsWith(pfx, StringComparison.Ordinal)
                        && c.AsSpan(pfx.Length).StartsWith(padToken)
                        && !kv.Value.FiredGesturesThisFrame.Contains(c.Substring(pfx.Length)));

                    foreach (var fired in kv.Value.FiredGesturesThisFrame)
                    {
                        if (_gestureBaseline.Contains($"{pfx}{fired}"))
                            continue;
                        if (IsTouchSpotKey(fired))
                            _gestureSpotByDevice[deviceGuid] = fired;   // last spot held wins
                        else if (!_gestureNonSpotByDevice.ContainsKey(deviceGuid))
                            _gestureNonSpotByDevice[deviceGuid] = fired;
                    }
                }
            }
            catch
            {
                // Torn enumeration: retry next tick.
                return null;
            }

            if (anyActiveFingers) return null;
            _gestureNonSpotByDevice.TryGetValue(deviceGuid, out var nonSpot);
            _gestureSpotByDevice.TryGetValue(deviceGuid, out var spot);
            string winner = nonSpot ?? spot;
            if (winner != null)
            {
                _gestureNonSpotByDevice.Remove(deviceGuid);
                _gestureSpotByDevice.Remove(deviceGuid);
            }
            return winner;
        }

        private void PollTick(object sender, EventArgs e)
        {
            // Allow either a MappingItem-bound session or a freeform one.
            // Bail when neither is active or no device baselines were
            // captured at start (defensive — the start path bails first).
            if ((_activeMapping == null && _freeformCallback == null) || _baselines.Count == 0)
            {
                CancelRecording();
                return;
            }

            // Check timeout.
            if ((DateTime.UtcNow - _recordingStartTime).TotalSeconds >= TimeoutSeconds)
            {
                string label = _activeMapping?.TargetLabel ?? "";
                CancelRecording();
                RecordingTimedOut?.Invoke(this, EventArgs.Empty);
                if (!string.IsNullOrEmpty(label))
                    _mainVm.StatusText = string.Format(Strings.Instance.Status_RecordingTimedOut_Format, label);
                return;
            }

            // Iterate every active device. The first to fire a button /
            // POV / axis change wins. Snapshot keys to avoid mutation
            // during enumeration when CancelRecording is called from
            // CompleteRecording.
            var deviceGuids = new List<Guid>(_baselines.Keys);
            // Does any device still hold something this tick? The exit below
            // used to fire after the first tick no matter what, so a device
            // held when recording started left wait-for-release mode with
            // its input still down and was captured immediately, which is
            // the one thing the mode exists to prevent.
            bool anyDeviceStillHeld = false;
            foreach (var dg in deviceGuids)
            {
                if (!_activeDevices.TryGetValue(dg, out var ud)) continue;
                if (ud?.InputState == null) continue;
                if (!_baselines.TryGetValue(dg, out var baseline)) continue;
                var current = ud.InputState.Clone();
                if (current == null) continue;

                // ── Wait-for-release phase: skip detection until all buttons/POVs are neutral ──
                if (_waitForRelease)
                {
                    bool anyHeld = false;
                    for (int i = 0; i < CustomInputState.MaxButtons && !anyHeld; i++)
                        anyHeld = current.Buttons[i];
                    for (int i = 0; i < CustomInputState.MaxPovs && !anyHeld; i++)
                        anyHeld = current.Povs[i] >= 0;
                    // MIDI notes held at record start must clear too, so an
                    // already-pressed key isn't captured the instant recording
                    // begins (mirrors the button release-wait).
                    if (!anyHeld && current.Midi?.Notes != null)
                        for (int i = 0; i < current.Midi.Notes.Length && !anyHeld; i++)
                            anyHeld = current.Midi.Notes[i];

                    if (anyHeld) { anyDeviceStillHeld = true; continue; }

                    // Everything released — capture fresh baseline and reset axis tracking.
                    _baselines[dg] = current;
                    if (_axisCandidates.TryGetValue(dg, out var ac0))
                    {
                        ac0.Type = MapType.None;
                        ac0.Index = -1;
                        ac0.HoldCounter = 0;
                    }
                    continue;
                }

                // ── Touchpad click rides Buttons[16]. Record it with the
                //     canonical "Touchpad 0 Click" descriptor so the user
                //     sees the touchpad-friendly name instead of "Button 16".
                //     Checked before the generic Buttons[] sweep so the
                //     descriptor wins. Gated on the device actually having a
                //     touchpad: consumer button 16 is Eject and an NFC tag
                //     can land on slot 16, and neither should record as a
                //     touchpad click (audit M2). ──
                bool hasTouchpad = ud.HasTouchpad;
                if (hasTouchpad
                    && current.Buttons.Length > 16 && current.Buttons[16]
                    && baseline.Buttons.Length > 16 && !baseline.Buttons[16])
                {
                    CompleteRecordingWithDescriptor("Touchpad 0 Click", dg);
                    return;
                }

                // ── Check buttons first (instant detection). Skip index 16
                //     on touchpad devices: handled above as "Touchpad 0
                //     Click" so the recorder never reports the touchpad as
                //     raw "Button 16". ──
                for (int i = 0; i < CustomInputState.MaxButtons; i++)
                {
                    if (i == 16 && hasTouchpad) continue;
                    if (current.Buttons[i] && !baseline.Buttons[i])
                    {
                        CompleteRecording(MapType.Button, i, null, axisPositive: false, winningDevice: dg);
                        return;
                    }
                }

                // ── Check POV hats ──
                for (int i = 0; i < CustomInputState.MaxPovs; i++)
                {
                    if (baseline.Povs[i] < 0 && current.Povs[i] >= 0)
                    {
                        string direction = CentidegreesToDirection(current.Povs[i]);
                        CompleteRecording(MapType.POV, i, direction, axisPositive: false, winningDevice: dg);
                        return;
                    }
                }

                // ── Touchpad gestures: rising edge vs the recording
                //     baseline. Only gestures the user has ENABLED on the
                //     Touchpad tab ever land in the fired set, so this
                //     detects exactly the descriptors the mapping picker
                //     offers. Checked after buttons / POVs so a deliberate
                //     press wins the tick over an incidental touch. ──
                if (hasTouchpad || ud.IsTouchpad)
                {
                    string gestureDesc = DetectNewGestureFire(dg);
                    if (gestureDesc != null)
                    {
                        CompleteRecordingWithDescriptor(gestureDesc, dg);
                        return;
                    }
                }

                // Param recording (Incremental Up/Down, InvertOnHold Modifier)
                // captures what ReadButtonLikeBool can actually read, which is
                // Button and POV descriptors plus the hardware-bool families
                // it forwards to (capsense, NFC tag, touchpad contact). Nothing
                // else. An axis capture would land in ParamUp / ParamDown /
                // ParamModifier as "Axis N" and silently do nothing, which is
                // the "I have to record 3-4 times before it works" symptom
                // (early tries get captured by stick drift on whichever axis
                // crosses the detection threshold first; later tries hit the
                // real button).
                //
                // The gate sits ABOVE the MIDI blocks, not below them. There
                // is no MIDI branch anywhere in ReadButtonLikeBool or in the
                // ReadHardwareBoolDescriptor it falls through to, so a MIDI
                // note or encoder pulse captured into a param wrote a
                // descriptor that reads false forever while the UI reported
                // success. Same dead capture the axis case describes, minus
                // the ambiguity, so it gets the same treatment.
                if (_paramTarget != ParamTarget.None) continue;

                // ── MIDI notes (button-class, instant rising edge) ──
                // MIDI input lives in the Midi sub-state, not Buttons[]/Axis[],
                // so the sweeps above never see it. A MIDI device that hadn't
                // sent anything at record start has a null baseline.Midi; adopt
                // the current state as the reference and detect from the next
                // tick so a resting note/CC doesn't false-trigger.
                if (current.Midi != null)
                {
                    if (baseline.Midi == null)
                    {
                        baseline.Midi = current.Midi.Clone();
                    }
                    else
                    {
                        bool noteFired = false;
                        for (int i = 0; i < current.Midi.Notes.Length; i++)
                        {
                            if (current.Midi.Notes[i] && !baseline.Midi.Notes[i])
                            {
                                CompleteRecordingWithDescriptor($"Midi Note {i}", dg);
                                noteFired = true;
                                break;
                            }
                        }
                        if (noteFired) return;

                        // Relative-encoder pulses (button-class): a turning
                        // encoder fires a momentary CcUp/CcDown while its value
                        // hovers near center. Gated near-center so a fader
                        // sweeping past center isn't misread as an encoder.
                        for (int i = 0; i < current.Midi.CcUp.Length; i++)
                        {
                            // A CC that has MOVED outside the band this session is a
                            // fader sweeping through center, not an encoder. Its
                            // transient in-band pulses must not be captured. Gate on
                            // "changed from the record baseline": a resting or
                            // never-touched CC defaults to 0 (far from 0x40 but never
                            // swept anywhere), and the old absolute-distance test
                            // pre-marked it as a fader on the first tick, suppressing
                            // its encoder pulses before the user even turned it. That
                            // is the auto-map-encoders regression in #144 / #142.
                            // Encoders only registered if moved before the device was
                            // assigned (which left Cc[i] near center).
                            if (current.Midi.Cc[i] != baseline.Midi.Cc[i]
                                && Math.Abs(current.Midi.Cc[i] - 0x40) > MidiRelativeBand) { _ccLeftBand[i] = true; continue; }
                            if (_ccLeftBand[i]) continue;
                            if (current.Midi.CcUp[i] && !baseline.Midi.CcUp[i])
                            {
                                CompleteRecordingWithDescriptor($"Midi CC {i} Up", dg);
                                return;
                            }
                            if (current.Midi.CcDown[i] && !baseline.Midi.CcDown[i])
                            {
                                CompleteRecordingWithDescriptor($"Midi CC {i} Down", dg);
                                return;
                            }
                        }
                    }
                }

                // ── MIDI CC / pitch bend (axis-class, threshold) ──
                // After the param gate (these are continuous, not button-class)
                // and before the generic axis sweep. baseline.Midi is non-null
                // here (established in the notes block above).
                if (current.Midi != null && baseline.Midi != null)
                {
                    int bestCc = -1, bestCcDelta = MidiCcThreshold;
                    for (int i = 0; i < current.Midi.Cc.Length; i++)
                    {
                        // Skip values in the relative band — those are encoders,
                        // bound as pulse buttons above, not absolute faders. A
                        // first-detent jump to ~0x41 would otherwise read as a
                        // big axis move from a 0 baseline.
                        if (Math.Abs(current.Midi.Cc[i] - 0x40) <= MidiRelativeBand) continue;
                        int d = Math.Abs(current.Midi.Cc[i] - baseline.Midi.Cc[i]);
                        if (d > bestCcDelta) { bestCcDelta = d; bestCc = i; }
                    }
                    if (bestCc >= 0)
                    {
                        CompleteRecordingWithDescriptor($"Midi CC {bestCc}", dg);
                        return;
                    }
                    if (Math.Abs(current.Midi.PitchBend - baseline.Midi.PitchBend) > MidiPitchThreshold)
                    {
                        CompleteRecordingWithDescriptor("Midi Pitch Bend", dg);
                        return;
                    }
                }

                // ── Check axes (requires hold confirmation) ──
                int bestAxisIndex = -1;
                MapType bestAxisType = MapType.None;
                int bestAxisDelta = 0;
                int bestAxisSignedDelta = 0;

                for (int i = 0; i < CustomInputState.MaxAxis; i++)
                {
                    int signedDelta = current.Axis[i] - baseline.Axis[i];
                    int delta = Math.Abs(signedDelta);
                    if (delta > AxisThreshold && delta > bestAxisDelta)
                    {
                        bestAxisDelta = delta;
                        bestAxisIndex = i;
                        bestAxisType = MapType.Axis;
                        bestAxisSignedDelta = signedDelta;
                    }
                }

                for (int i = 0; i < CustomInputState.MaxSliders; i++)
                {
                    int signedDelta = current.Sliders[i] - baseline.Sliders[i];
                    int delta = Math.Abs(signedDelta);
                    if (delta > AxisThreshold && delta > bestAxisDelta)
                    {
                        bestAxisDelta = delta;
                        bestAxisIndex = i;
                        bestAxisType = MapType.Slider;
                        bestAxisSignedDelta = signedDelta;
                    }
                }

                if (!_axisCandidates.TryGetValue(dg, out var ac))
                {
                    ac = new AxisCandidate();
                    _axisCandidates[dg] = ac;
                }

                if (bestAxisIndex >= 0)
                {
                    bool isMouse = _isMouseByDevice.TryGetValue(dg, out var m) && m;
                    if (isMouse)
                    {
                        CompleteRecording(bestAxisType, bestAxisIndex, null,
                            bestAxisSignedDelta > 0, winningDevice: dg);
                        return;
                    }
                    if (bestAxisType == ac.Type && bestAxisIndex == ac.Index)
                    {
                        ac.HoldCounter++;
                        if (ac.HoldCounter >= AxisHoldCycles)
                        {
                            CompleteRecording(bestAxisType, bestAxisIndex, null, ac.Positive, winningDevice: dg);
                            return;
                        }
                    }
                    else
                    {
                        ac.Type = bestAxisType;
                        ac.Index = bestAxisIndex;
                        ac.Positive = bestAxisSignedDelta > 0;
                        ac.HoldCounter = 1;
                    }
                }
                else
                {
                    ac.Type = MapType.None;
                    ac.Index = -1;
                    ac.HoldCounter = 0;
                }
            }

            // After processing all devices, exit wait-for-release mode
            // once every device has cleared.
            if (_waitForRelease && !anyDeviceStillHeld)
                _waitForRelease = false;
        }

        // ─────────────────────────────────────────────
        //  Complete recording
        // ─────────────────────────────────────────────

        /// <summary>
        /// Completes the recording by building the descriptor string and
        /// assigning it to the active mapping item.
        /// </summary>
        /// <param name="type">The input type detected.</param>
        /// <param name="index">The zero-based index within the type.</param>
        /// <param name="povDirection">For POV: the direction string ("Up", "Down", etc.).</param>
        /// <param name="axisPositive">For axes: true if the raw value increased (positive delta).</param>
        private void CompleteRecording(MapType type, int index, string povDirection,
            bool axisPositive = false, Guid winningDevice = default)
        {
            // Freeform shortcut: deliver (deviceGuid, descriptor) to the
            // callback and tear down. Doesn't touch MappingItem state.
            if (_freeformCallback != null && _activeMapping == null)
            {
                var fb = _freeformCallback;
                string fbDesc = BuildDescriptor(type, index, povDirection);
                string fbGuid = winningDevice == Guid.Empty
                    ? "" : winningDevice.ToString().ToLowerInvariant();
                CleanupTimer();
                _freeformCallback = null;
                _activePadIndex = -1;
                _activeDevices.Clear();
                _baselines.Clear();
                _isMouseByDevice.Clear();
                _axisCandidates.Clear();
                fb(fbGuid, fbDesc);
                return;
            }

            if (_activeMapping == null)
                return;

            // Build the descriptor string.
            string descriptor = BuildDescriptor(type, index, povDirection);

            // Snapshot recording target before cleanup.
            var mapping = _activeMapping;
            var extraSource = _activeExtraSource;
            // Capture _negRecording before cleanup. It tells us the recording
            // was started for a NEGATIVE-direction quadrant of a bipolar axis
            // (left / up). For a button source that means "this press should
            // drive the axis to -1", which we encode as Invert=true below.
            // (For axis sources, ShouldAutoInvert already folds _negRecording
            // into shouldInvert.)
            bool negRec = _negRecording;
            var paramTarget = _paramTarget;

            // Stop recording.
            if (extraSource != null) extraSource.IsRecording = false;
            else mapping.IsRecording = false;
            CleanupTimer();
            _activeMapping = null;
            _activeExtraSource = null;
            _activePadIndex = -1;
            _paramTarget = ParamTarget.None;
            // Capture before the clears below, alongside the other state
            // this method reads after teardown. The two #200 half-axis
            // decisions further down queried _isMouseByDevice AFTER it was
            // emptied, so TryGetValue always missed and the negative-half
            // (IH) form never fired: an inverted mouse relative axis on a
            // button-like target kept emitting the bare Invert form, which
            // reads pressed at rest.
            bool winningIsMouse = _isMouseByDevice.TryGetValue(winningDevice, out bool winMouse) && winMouse;

            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            // Auto-detect inversion for axis/slider recordings.
            bool shouldInvert = false;
            if (type == MapType.Axis || type == MapType.Slider)
                shouldInvert = ShouldAutoInvert(mapping, axisPositive, negRec);

            string finalDescriptor;
            // Device the input fired on. Empty only if no device was ever
            // collected (handled earlier — recording would have aborted), so
            // in practice this is always a real GUID. THIS — not whatever the
            // Mappings-tab device dropdown shows — is the device the mapping
            // gets attached to. Do not let any caller override it with the
            // dropdown selection (that was a regression; see the comment in
            // MainWindow.RecordingCompleted).
            string winningGuidStr = winningDevice == Guid.Empty
                ? "" : winningDevice.ToString().ToLowerInvariant();

            if (extraSource != null && paramTarget != ParamTarget.None)
            {
                // ── Recording targeted a Param button on an ExtraSource ──
                // Write the captured descriptor into the kind-specific
                // field (ParamUp / ParamDown / ParamModifier). Don't touch
                // the source's main DeviceGuid / Descriptor / Invert — the
                // Param field is the only thing this record button is
                // supposed to change.
                switch (paramTarget)
                {
                    case ParamTarget.Up:       extraSource.ParamUp       = descriptor; break;
                    case ParamTarget.Down:     extraSource.ParamDown     = descriptor; break;
                    case ParamTarget.Modifier: extraSource.ParamModifier = descriptor; break;
                }
                // Stamp the winning device on the source so the engine knows
                // which device's state to read the Param button from. Without
                // this, an InvertOnHold modifier recorded on a SECONDARY
                // device (e.g. Button 65 on a web/touchpad device) would be
                // looked up against the row's primary device, which has no
                // such index, and IsInvertOnHoldActive silently returns false.
                if (!string.IsNullOrEmpty(winningGuidStr))
                {
                    extraSource.DeviceGuid = winningGuidStr;
                    extraSource.DeviceLabel = ResolveDeviceLabel(winningDevice);
                }
                finalDescriptor = descriptor;
                _mainVm.StatusText = string.Format(
                    Strings.Instance.Status_Recorded_Format, mapping.TargetLabel, finalDescriptor);
                RecordingCompleted?.Invoke(this, new RecordingResult
                {
                    Mapping = mapping,
                    ExtraSource = extraSource,
                    Descriptor = finalDescriptor,
                    Type = type,
                    IsParamRecording = true,
                });
                return;
            }

            if (extraSource != null)
            {
                // ── Recording targeted an EXTRA source (multi-source row) ──
                // Write Descriptor (un-prefixed) + Invert/HalfAxis as the
                // schema's separate per-source fields, plus the winning
                // DeviceGuid (cross-device extras are first-class — a keyboard
                // key extra-source on a stick axis legitimately stores the
                // keyboard's GUID here).
                extraSource.DeviceGuid = winningGuidStr;
                if (!string.IsNullOrEmpty(winningGuidStr))
                    extraSource.DeviceLabel = ResolveDeviceLabel(winningDevice);
                extraSource.Descriptor = descriptor;
                if (type == MapType.Axis || type == MapType.Slider)
                {
                    extraSource.Invert = shouldInvert;
                    // Same negative-half rule as the primary path (issue
                    // #200): an inverted mouse relative axis on a button-like
                    // target selects the negative half instead of the bare
                    // Invert form that reads pressed at rest.
                    extraSource.HalfAxis = shouldInvert
                        && winningIsMouse
                        && IsButtonLikeRecordingTarget(mapping.TargetSettingName);
                }
                else
                {
                    // A discrete button (or POV / touchpad) recorded for the
                    // negative quadrant of a bipolar axis should contribute -1
                    // when pressed — encode that as Invert=true so the engine
                    // reads it as the negative direction. Positive quadrant →
                    // Invert=false (contributes +1).
                    extraSource.Invert = negRec;
                    extraSource.HalfAxis = false;
                }
                // Sync the per-source picker selection to the new state.
                extraSource.SyncSelectedInputFromState(mapping.AvailableInputs);
                // finalDescriptor is only used for the status string + the
                // RecordingResult payload (the handler short-circuits for
                // extra sources and reads extraSource directly). Mirror the
                // legacy "I" prefix form so the status reads e.g. "IButton 7"
                // for an inverted neg button.
                bool extraInverted = (type == MapType.Axis || type == MapType.Slider) ? shouldInvert : negRec;
                finalDescriptor = (extraInverted ? "I" : "") + descriptor;
            }
            else
            {
                // ── Recording targeted the row's PRIMARY source ──
                // Prefix-encode the descriptor (legacy I/H form the
                // MappingItem + Step 3 parser still consume) and tag the row
                // with the winning device GUID — this is the authoritative
                // device assignment for the row (NOT the dropdown selection).
                if (type == MapType.Axis || type == MapType.Slider)
                {
                    // Mouse relative axes rest at center, and the bare Invert
                    // grammar reads a centered axis as pressed for sub-50%
                    // thresholds. A negative-direction mouse recording on a
                    // button-like target therefore selects the negative HALF
                    // (IH prefix), which is off at rest and fires on leftward
                    // or upward motion only (issue #200 companion fix).
                    bool mouseHalf = shouldInvert
                        && winningIsMouse
                        && IsButtonLikeRecordingTarget(mapping.TargetSettingName);
                    descriptor = (shouldInvert ? (mouseHalf ? "IH" : "I") : "") + descriptor;
                }
                if (!string.IsNullOrEmpty(winningGuidStr))
                {
                    mapping.PrimarySourceDeviceGuid = winningGuidStr;
                    mapping.PrimarySourceDeviceLabel = ResolveDeviceLabel(winningDevice);
                }
                mapping.LoadDescriptor(descriptor);
                // E7 authoring default rides the recorder seam too, or
                // recording a button onto the scroll row scrolls DOWN
                // while picking the identical button scrolls UP.
                mapping.ApplyScrollUpAuthoringDefault(mapping.SourceDescriptor);
                finalDescriptor = mapping.SourceDescriptor;
            }

            _mainVm.StatusText = string.Format(Strings.Instance.Status_Recorded_Format, mapping.TargetLabel, finalDescriptor);

            RecordingCompleted?.Invoke(this, new RecordingResult
            {
                Mapping = mapping,
                ExtraSource = extraSource,
                Descriptor = finalDescriptor,
                Type = type,
            });
        }

        private static string ResolveDeviceLabel(Guid g)
        {
            if (g == Guid.Empty) return "";
            var devs = SettingsManager.UserDevices?.Items;
            if (devs == null) return "";
            lock (SettingsManager.UserDevices.SyncRoot)
            {
                foreach (var ud in devs)
                {
                    if (ud != null && ud.InstanceGuid == g)
                        return InputService.LocalizedDeviceName(ud) ?? "";
                }
            }
            return "";
        }

        /// <summary>
        /// Finishes the active recording with a literal descriptor string,
        /// bypassing <see cref="BuildDescriptor"/>. Used for inputs whose
        /// canonical descriptor differs from the raw (MapType, index) — the
        /// touchpad click rides Buttons[16] but records as "Touchpad 0 Click"
        /// so the user sees the touchpad-friendly name in the picker.
        /// </summary>
        private void CompleteRecordingWithDescriptor(string descriptor, Guid winningDevice = default)
        {
            // Freeform shortcut for literal-descriptor inputs (touchpad
            // click, etc.).
            if (_freeformCallback != null && _activeMapping == null)
            {
                var fb = _freeformCallback;
                string fbGuid = winningDevice == Guid.Empty
                    ? "" : winningDevice.ToString().ToLowerInvariant();
                CleanupTimer();
                _freeformCallback = null;
                _activePadIndex = -1;
                _activeDevices.Clear();
                _baselines.Clear();
                _isMouseByDevice.Clear();
                _axisCandidates.Clear();
                fb(fbGuid, descriptor ?? "");
                return;
            }

            if (_activeMapping == null)
                return;

            var mapping = _activeMapping;
            var extraSource = _activeExtraSource;
            // See CompleteRecording: a literal-descriptor input (touchpad
            // click) recorded for a bipolar axis's negative quadrant is
            // stored Invert=true so the press drives the axis to -1.
            bool negRec = _negRecording;
            var paramTarget = _paramTarget;

            if (extraSource != null) extraSource.IsRecording = false;
            else mapping.IsRecording = false;
            CleanupTimer();
            _activeMapping = null;
            _activeExtraSource = null;
            _activePadIndex = -1;
            _paramTarget = ParamTarget.None;
            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            string finalDescriptor;
            // Winning (firing) device — the authoritative device the mapping
            // attaches to, regardless of the Mappings-tab dropdown selection.
            string winningGuidStr = winningDevice == Guid.Empty
                ? "" : winningDevice.ToString().ToLowerInvariant();

            if (extraSource != null && paramTarget != ParamTarget.None)
            {
                // Param recording (Incremental Up/Down or InvertOnHold Modifier).
                // Touchpad-click is technically not button-class for the
                // engine's ReadButtonLikeBool, but the source-of-truth field
                // is still the Param* descriptor — write it and let the
                // engine ignore unknown forms.
                switch (paramTarget)
                {
                    case ParamTarget.Up:       extraSource.ParamUp       = descriptor; break;
                    case ParamTarget.Down:     extraSource.ParamDown     = descriptor; break;
                    case ParamTarget.Modifier: extraSource.ParamModifier = descriptor; break;
                }
                finalDescriptor = descriptor;
                _mainVm.StatusText = string.Format(
                    Strings.Instance.Status_Recorded_Format, mapping.TargetLabel, finalDescriptor);
                RecordingCompleted?.Invoke(this, new RecordingResult
                {
                    Mapping = mapping,
                    ExtraSource = extraSource,
                    Descriptor = finalDescriptor,
                    Type = MapType.Button,
                });
                return;
            }

            if (extraSource != null)
            {
                // Extra-source target on a multi-source row (e.g. a touchpad
                // click added alongside an analog stick axis).
                extraSource.DeviceGuid = winningGuidStr;
                if (!string.IsNullOrEmpty(winningGuidStr))
                    extraSource.DeviceLabel = ResolveDeviceLabel(winningDevice);
                extraSource.Descriptor = descriptor;
                extraSource.Invert = negRec;   // neg quadrant → contributes -1
                extraSource.HalfAxis = false;
                extraSource.SyncSelectedInputFromState(mapping.AvailableInputs);
                finalDescriptor = (negRec ? "I" : "") + descriptor;
            }
            else
            {
                // Primary target — tag the row with the winning device.
                if (!string.IsNullOrEmpty(winningGuidStr))
                {
                    mapping.PrimarySourceDeviceGuid = winningGuidStr;
                    mapping.PrimarySourceDeviceLabel = ResolveDeviceLabel(winningDevice);
                }
                mapping.LoadDescriptor(descriptor);
                // E7 authoring default, the primary-path twin above.
                mapping.ApplyScrollUpAuthoringDefault(mapping.SourceDescriptor);
                finalDescriptor = mapping.SourceDescriptor;
            }

            _mainVm.StatusText = string.Format(
                Strings.Instance.Status_Recorded_Format, mapping.TargetLabel, finalDescriptor);

            RecordingCompleted?.Invoke(this, new RecordingResult
            {
                Mapping = mapping,
                ExtraSource = extraSource,
                Descriptor = finalDescriptor,
                // A recorded MIDI absolute CC ("Midi CC N") or pitch bend is a
                // full bipolar axis (the engine reads it -1..+1), not a button.
                // Tagging it Axis stops the bidirectional-axis auto-prompt from
                // requesting a spurious second source (issue #142 a/b). Relative
                // encoder pulses ("Midi CC N Up"/"Down") and notes stay buttons.
                Type = MidiAxisClass(descriptor) ? MapType.Axis : MapType.Button,
            });
        }

        /// <summary>True for a recorded MIDI descriptor the engine reads as a
        /// full bipolar axis: an absolute CC ("Midi CC N") or pitch bend. The
        /// relative-encoder pulses ("Midi CC N Up"/"Down") and notes are
        /// button-class and excluded.</summary>
        private static bool MidiAxisClass(string descriptor)
            => descriptor == "Midi Pitch Bend"
               || (descriptor != null
                   && descriptor.StartsWith("Midi CC ", StringComparison.Ordinal)
                   && !descriptor.EndsWith(" Up", StringComparison.Ordinal)
                   && !descriptor.EndsWith(" Down", StringComparison.Ordinal));

        /// <summary>
        /// Determines whether an axis recording should auto-apply the Invert prefix
        /// based on the target mapping and the direction of initial movement.
        /// </summary>
        /// <param name="mapping">The target mapping item being recorded.</param>
        /// <param name="axisPositive">True if the raw axis value increased (positive delta).</param>
        /// <summary>True when the recording target coerces the source as a
        /// button-style threshold (the fall-through set of
        /// <see cref="ShouldAutoInvert"/>): everything that is not a stick
        /// axis, trigger, Extended axis, or KBM mouse/scroll channel. Those
        /// targets read centered axes with the bare Invert grammar, which is
        /// on-at-rest for sub-50% thresholds on a mouse's relative axes, so
        /// negative-direction mouse recordings must carry the Half flag
        /// (issue #200 companion fix).</summary>
        private static bool IsButtonLikeRecordingTarget(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            if (target is "LeftThumbAxisX" or "RightThumbAxisX"
                       or "LeftThumbAxisY" or "RightThumbAxisY") return false;
            if (target == "LeftTrigger" || target == "RightTrigger") return false;
            if (target.StartsWith("RawAxis", StringComparison.Ordinal)) return false;
            if (target.StartsWith("KbmMouse", StringComparison.Ordinal)
                || target.StartsWith("KbmScroll", StringComparison.Ordinal)) return false;
            return true;
        }

        private static bool ShouldAutoInvert(MappingItem mapping, bool axisPositive, bool negRecording)
        {
            string target = mapping.TargetSettingName;

            // Stick axis targets: pos target expects positive SDL delta (right/down),
            // neg target expects negative SDL delta (left/up).
            // Invert only if the user pushed in the wrong direction for the target.
            if (target is "LeftThumbAxisX" or "RightThumbAxisX"
                      or "LeftThumbAxisY" or "RightThumbAxisY")
                return negRecording ? axisPositive : !axisPositive;

            // Extended bidirectional stick axes (HasNegDirection): same logic as gamepad sticks.
            if (target.StartsWith("RawAxis", StringComparison.Ordinal) && mapping.HasNegDirection)
                return negRecording ? axisPositive : !axisPositive;

            // KBM bidirectional axes: never auto-invert. The full analog axis
            // maps directly to mouse/scroll movement — positive raw = right/down,
            // which is the correct screen convention. MainWindow already clears
            // the neg descriptor for full-axis recordings on bidirectional targets.
            if (target.StartsWith("KbmMouse", StringComparison.Ordinal)
                || target.StartsWith("KbmScroll", StringComparison.Ordinal))
                return false;

            // Trigger targets: increasing value is natural.
            // Negative delta = reverse polarity = needs inversion.
            if (target == "LeftTrigger" || target == "RightTrigger")
                return !axisPositive;

            // Extended trigger axes (unidirectional, no neg direction): same as gamepad triggers.
            if (target.StartsWith("RawAxis", StringComparison.Ordinal))
                return !axisPositive;

            // All other targets (buttons, d-pad, etc.): invert when the user pushed
            // the axis in the negative direction. This ensures the engine's threshold
            // check ("value < 25%") fires for the direction the user actually moved.
            return !axisPositive;
        }

        /// <summary>
        /// Builds a mapping descriptor string from the detected input.
        /// </summary>
        private static string BuildDescriptor(MapType type, int index, string povDirection)
        {
            return type switch
            {
                MapType.Button => $"Button {index}",
                MapType.Axis => $"Axis {index}",
                MapType.Slider => $"Slider {index}",
                MapType.POV when !string.IsNullOrEmpty(povDirection)
                    => $"POV {index} {povDirection}",
                MapType.POV => $"POV {index}",
                _ => string.Empty
            };
        }

        /// <summary>
        /// Converts a centidegrees POV value to a cardinal direction string.
        /// </summary>
        private static string CentidegreesToDirection(int centidegrees)
        {
            if (centidegrees < 0) return null;

            // Normalize to 0–35999.
            centidegrees = centidegrees % 36000;

            // 8-way detection with 45-degree (4500 centidegrees) sectors.
            // 0=N, 4500=NE, 9000=E, 13500=SE, 18000=S, 22500=SW, 27000=W, 31500=NW
            if (centidegrees >= 33750 || centidegrees < 2250) return "Up";
            if (centidegrees >= 2250 && centidegrees < 6750) return "UpRight";
            if (centidegrees >= 6750 && centidegrees < 11250) return "Right";
            if (centidegrees >= 11250 && centidegrees < 15750) return "DownRight";
            if (centidegrees >= 15750 && centidegrees < 20250) return "Down";
            if (centidegrees >= 20250 && centidegrees < 24750) return "DownLeft";
            if (centidegrees >= 24750 && centidegrees < 29250) return "Left";
            if (centidegrees >= 29250 && centidegrees < 33750) return "UpLeft";

            return "Up"; // Fallback
        }

        // ─────────────────────────────────────────────
        //  Cleanup
        // ─────────────────────────────────────────────

        private void CleanupTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= PollTick;
                _timer = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CancelRecording();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Result of a successful recording operation.
    /// </summary>
    public class RecordingResult
    {
        /// <summary>The mapping item that was recorded.</summary>
        public MappingItem Mapping { get; set; }

        /// <summary>When non-null, the recording targeted this extra source
        /// on <see cref="Mapping"/> rather than the row's primary — the
        /// completion handler should commit it without touching the primary
        /// or running the bipolar auto-prompt.</summary>
        public MappingSourceItem ExtraSource { get; set; }

        /// <summary>The descriptor string assigned (e.g., "Button 0", "Axis 1").</summary>
        public string Descriptor { get; set; }

        /// <summary>The detected input type.</summary>
        public MapType Type { get; set; }

        /// <summary>True when the recording only updated a Param field
        /// (Up / Down / Modifier) on <see cref="ExtraSource"/>. The source
        /// already exists (it's an extra source, or the row's primary kind
        /// holder), so the completion handler must NOT add it to ExtraSources.</summary>
        public bool IsParamRecording { get; set; }
    }
}
