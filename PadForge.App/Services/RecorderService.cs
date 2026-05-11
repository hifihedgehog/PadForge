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

        // ─────────────────────────────────────────────
        //  State
        // ─────────────────────────────────────────────

        private readonly MainViewModel _mainVm;
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

        /// <summary>
        /// When true, the recorder waits for all buttons and POVs to return to neutral
        /// before detecting new inputs. Used for follow-up recordings where the previous
        /// input may still be physically held.
        /// </summary>
        private bool _waitForRelease;

        /// <summary>Whether recording is currently active.</summary>
        public bool IsRecording => _activeMapping != null;

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
            StartRecordingInternal(parent, extraSource, padIndex,
                neutralizeBaseline, negRecording);
        }

        private void StartRecordingInternal(MappingItem mapping,
            MappingSourceItem extraSource, int padIndex,
            bool neutralizeBaseline, bool negRecording)
        {
            if (mapping == null)
                return;

            // Cancel any existing recording.
            if (_activeMapping != null)
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
                _mainVm.StatusText = Strings.Instance.Status_NoDeviceToRecord;
                RecordingTimedOut?.Invoke(this, EventArgs.Empty);
                return;
            }

            _recordingStartTime = DateTime.UtcNow;

            // For follow-up recordings, wait for the previous input to be released
            // before detecting new presses. This prevents the same POV/button from
            // being immediately re-detected when it's still physically held.
            _waitForRelease = neutralizeBaseline;

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

            _mainVm.StatusText = string.Format(Strings.Instance.Status_RecordingPrompt_Format, mapping.TargetLabel);
        }

        /// <summary>
        /// Cancels the active recording without assigning a source.
        /// </summary>
        public void CancelRecording()
        {
            if (_activeMapping == null)
                return;

            if (_activeExtraSource != null) _activeExtraSource.IsRecording = false;
            else _activeMapping.IsRecording = false;
            CleanupTimer();

            _activeMapping = null;
            _activeExtraSource = null;
            _activePadIndex = -1;
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
        private void PollTick(object sender, EventArgs e)
        {
            if (_activeMapping == null || _baselines.Count == 0)
            {
                CancelRecording();
                return;
            }

            // Check timeout.
            if ((DateTime.UtcNow - _recordingStartTime).TotalSeconds >= TimeoutSeconds)
            {
                var mapping = _activeMapping;
                CancelRecording();
                RecordingTimedOut?.Invoke(this, EventArgs.Empty);
                _mainVm.StatusText = string.Format(Strings.Instance.Status_RecordingTimedOut_Format, mapping.TargetLabel);
                return;
            }

            // Iterate every active device. The first to fire a button /
            // POV / axis change wins. Snapshot keys to avoid mutation
            // during enumeration when CancelRecording is called from
            // CompleteRecording.
            var deviceGuids = new List<Guid>(_baselines.Keys);
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
                    bool anyHeld = current.TouchpadClick;
                    for (int i = 0; i < CustomInputState.MaxButtons && !anyHeld; i++)
                        anyHeld = current.Buttons[i];
                    for (int i = 0; i < CustomInputState.MaxPovs && !anyHeld; i++)
                        anyHeld = current.Povs[i] >= 0;

                    if (anyHeld) continue;

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

                // ── Touchpad click (dedicated bool — not in Buttons[]) ──
                if (current.TouchpadClick && !baseline.TouchpadClick)
                {
                    CompleteRecordingWithDescriptor("Touchpad 0 Click", dg);
                    return;
                }

                // ── Check buttons first (instant detection) ──
                for (int i = 0; i < CustomInputState.MaxButtons; i++)
                {
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
            if (_waitForRelease)
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

            // Stop recording.
            if (extraSource != null) extraSource.IsRecording = false;
            else mapping.IsRecording = false;
            CleanupTimer();
            _activeMapping = null;
            _activeExtraSource = null;
            _activePadIndex = -1;
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
                    extraSource.HalfAxis = false;
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
                    descriptor = (shouldInvert ? "I" : "") + descriptor;
                if (!string.IsNullOrEmpty(winningGuidStr))
                {
                    mapping.PrimarySourceDeviceGuid = winningGuidStr;
                    mapping.PrimarySourceDeviceLabel = ResolveDeviceLabel(winningDevice);
                }
                mapping.LoadDescriptor(descriptor);
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
                        return ud.ResolvedName ?? ud.ProductName ?? ud.InstanceName ?? "";
                }
            }
            return "";
        }

        /// <summary>
        /// Finishes the active recording with a literal descriptor string,
        /// bypassing <see cref="BuildDescriptor"/>. Used for inputs that don't
        /// fit the (MapType, index) shape — e.g. touchpad click, which lives in
        /// CustomInputState.TouchpadClick rather than the Buttons[] array and
        /// emits the engine-recognized "Touchpad 0 Click" form directly.
        /// </summary>
        private void CompleteRecordingWithDescriptor(string descriptor, Guid winningDevice = default)
        {
            if (_activeMapping == null)
                return;

            var mapping = _activeMapping;
            var extraSource = _activeExtraSource;
            // See CompleteRecording: a literal-descriptor input (touchpad
            // click) recorded for a bipolar axis's negative quadrant is
            // stored Invert=true so the press drives the axis to -1.
            bool negRec = _negRecording;

            if (extraSource != null) extraSource.IsRecording = false;
            else mapping.IsRecording = false;
            CleanupTimer();
            _activeMapping = null;
            _activeExtraSource = null;
            _activePadIndex = -1;
            _activeDevices.Clear();
            _baselines.Clear();
            _isMouseByDevice.Clear();
            _axisCandidates.Clear();

            string finalDescriptor;
            // Winning (firing) device — the authoritative device the mapping
            // attaches to, regardless of the Mappings-tab dropdown selection.
            string winningGuidStr = winningDevice == Guid.Empty
                ? "" : winningDevice.ToString().ToLowerInvariant();
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
                finalDescriptor = mapping.SourceDescriptor;
            }

            _mainVm.StatusText = string.Format(
                Strings.Instance.Status_Recorded_Format, mapping.TargetLabel, finalDescriptor);

            RecordingCompleted?.Invoke(this, new RecordingResult
            {
                Mapping = mapping,
                ExtraSource = extraSource,
                Descriptor = finalDescriptor,
                Type = MapType.Button,
            });
        }

        /// <summary>
        /// Determines whether an axis recording should auto-apply the Invert prefix
        /// based on the target mapping and the direction of initial movement.
        /// </summary>
        /// <param name="mapping">The target mapping item being recorded.</param>
        /// <param name="axisPositive">True if the raw axis value increased (positive delta).</param>
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
            if (target.StartsWith("ExtendedAxis", StringComparison.Ordinal) && mapping.HasNegDirection)
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
            if (target.StartsWith("ExtendedAxis", StringComparison.Ordinal))
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
    }
}
