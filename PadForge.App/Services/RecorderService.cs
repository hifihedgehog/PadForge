using System;
using System.Linq;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
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

        /// <summary>The pad index (0–3) of the active recording.</summary>
        private int _activePadIndex = -1;

        /// <summary>The device GUID to record from (the selected device).</summary>
        private Guid _activeDeviceGuid;

        /// <summary>The baseline input state captured at the start of recording.</summary>
        private CustomInputState _baseline;

        /// <summary>Counter for how many cycles an axis candidate has been held.</summary>
        private int _axisHoldCounter;

        /// <summary>The axis candidate being tracked (type + index).</summary>
        private MapType _axisCandidateType;
        private int _axisCandidateIndex;

        /// <summary>Whether the axis candidate moved in the positive direction (value increased).</summary>
        private bool _axisCandidatePositive;

        /// <summary>When recording started (for timeout).</summary>
        private DateTime _recordingStartTime;

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
        /// <param name="padIndex">The pad index (0–3) to read input from.</param>
        /// <param name="deviceGuid">The specific device GUID to record from.</param>
        public void StartRecording(MappingItem mapping, int padIndex, Guid deviceGuid)
        {
            if (mapping == null)
                return;

            // Cancel any existing recording.
            if (_activeMapping != null)
                CancelRecording();

            _activeMapping = mapping;
            _activePadIndex = padIndex;
            _activeDeviceGuid = deviceGuid;
            _axisHoldCounter = 0;
            _axisCandidateType = MapType.None;
            _axisCandidateIndex = -1;
            _recordingStartTime = DateTime.UtcNow;

            // Capture baseline state.
            _baseline = CaptureCurrentState();
            if (_baseline == null)
            {
                // No device available — can't record.
                _activeMapping = null;
                _mainVm.StatusText = "No device connected to record from.";
                return;
            }

            // Mark the mapping as recording.
            mapping.IsRecording = true;

            // Start polling timer.
            _timer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
            };
            _timer.Tick += PollTick;
            _timer.Start();

            _mainVm.StatusText = $"Recording: press a button or move an axis for \"{mapping.TargetLabel}\"...";
        }

        /// <summary>
        /// Cancels the active recording without assigning a source.
        /// </summary>
        public void CancelRecording()
        {
            if (_activeMapping == null)
                return;

            _activeMapping.IsRecording = false;
            CleanupTimer();

            _activeMapping = null;
            _activePadIndex = -1;
            _activeDeviceGuid = Guid.Empty;
            _baseline = null;

            _mainVm.StatusText = "Recording cancelled.";
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
            if (_activeMapping == null || _baseline == null)
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
                _mainVm.StatusText = $"Recording timed out for \"{mapping.TargetLabel}\".";
                return;
            }

            // Read current state.
            CustomInputState current = CaptureCurrentState();
            if (current == null)
            {
                CancelRecording();
                return;
            }

            // ── Check buttons first (instant detection) ──
            for (int i = 0; i < CustomInputState.MaxButtons; i++)
            {
                if (current.Buttons[i] && !_baseline.Buttons[i])
                {
                    CompleteRecording(MapType.Button, i, null);
                    return;
                }
            }

            // ── Check POV hats ──
            for (int i = 0; i < CustomInputState.MaxPovs; i++)
            {
                if (_baseline.Povs[i] < 0 && current.Povs[i] >= 0)
                {
                    // POV went from centered to a direction.
                    string direction = CentidegreesToDirection(current.Povs[i]);
                    CompleteRecording(MapType.POV, i, direction);
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
                int signedDelta = current.Axis[i] - _baseline.Axis[i];
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
                int signedDelta = current.Sliders[i] - _baseline.Sliders[i];
                int delta = Math.Abs(signedDelta);
                if (delta > AxisThreshold && delta > bestAxisDelta)
                {
                    bestAxisDelta = delta;
                    bestAxisIndex = i;
                    bestAxisType = MapType.Slider;
                    bestAxisSignedDelta = signedDelta;
                }
            }

            if (bestAxisIndex >= 0)
            {
                // Is this the same candidate as last cycle?
                if (bestAxisType == _axisCandidateType && bestAxisIndex == _axisCandidateIndex)
                {
                    _axisHoldCounter++;
                    if (_axisHoldCounter >= AxisHoldCycles)
                    {
                        CompleteRecording(bestAxisType, bestAxisIndex, null, _axisCandidatePositive);
                        return;
                    }
                }
                else
                {
                    // New candidate — reset counter.
                    _axisCandidateType = bestAxisType;
                    _axisCandidateIndex = bestAxisIndex;
                    _axisCandidatePositive = bestAxisSignedDelta > 0;
                    _axisHoldCounter = 1;
                }
            }
            else
            {
                // No axis past threshold — reset tracking.
                _axisHoldCounter = 0;
                _axisCandidateType = MapType.None;
                _axisCandidateIndex = -1;
            }
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
        private void CompleteRecording(MapType type, int index, string povDirection, bool axisPositive = false)
        {
            if (_activeMapping == null)
                return;

            // Build the descriptor string.
            string descriptor = BuildDescriptor(type, index, povDirection);

            // Store the mapping item before cleanup.
            var mapping = _activeMapping;
            int padIndex = _activePadIndex;

            // Stop recording.
            mapping.IsRecording = false;
            CleanupTimer();
            _activeMapping = null;
            _activePadIndex = -1;
            _baseline = null;

            // Assign the clean descriptor first (no prefix).
            mapping.SourceDescriptor = descriptor;

            // Auto-detect inversion for axis/slider recordings based on movement direction.
            if (type == MapType.Axis || type == MapType.Slider)
                mapping.IsInverted = ShouldAutoInvert(mapping, axisPositive);

            // Read back the final descriptor (may have "I" prefix from auto-inversion).
            string finalDescriptor = mapping.SourceDescriptor;
            _mainVm.StatusText = $"Recorded \"{mapping.TargetLabel}\" ← {finalDescriptor}";

            // Raise event.
            RecordingCompleted?.Invoke(this, new RecordingResult
            {
                Mapping = mapping,
                PadIndex = padIndex,
                Descriptor = finalDescriptor,
                Type = type,
                Index = index,
                PovDirection = povDirection
            });
        }

        /// <summary>
        /// Determines whether an axis recording should auto-apply the Invert prefix
        /// based on the target mapping and the direction of initial movement.
        /// </summary>
        /// <param name="mapping">The target mapping item being recorded.</param>
        /// <param name="axisPositive">True if the raw axis value increased (positive delta).</param>
        private static bool ShouldAutoInvert(MappingItem mapping, bool axisPositive)
        {
            string target = mapping.TargetSettingName;

            // Y-axis targets: "up" is natural. SDL raw Y increases when pushed down,
            // so positive delta = down = needs inversion.
            if (target == "LeftThumbAxisY" || target == "RightThumbAxisY")
                return axisPositive;

            // X-axis targets: "right" is natural. SDL raw X increases when pushed right,
            // so negative delta = left = needs inversion.
            if (target == "LeftThumbAxisX" || target == "RightThumbAxisX")
                return !axisPositive;

            // Trigger targets: increasing value is natural.
            // Negative delta = reverse polarity = needs inversion.
            if (target == "LeftTrigger" || target == "RightTrigger")
                return !axisPositive;

            // All other targets (buttons, d-pad, etc.): no auto-inversion.
            return false;
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
            if (centidegrees >= 33750 || centidegrees < 2250) return "Up";
            if (centidegrees >= 2250 && centidegrees < 6750) return "Up"; // UpRight → treat as Up
            if (centidegrees >= 6750 && centidegrees < 11250) return "Right";
            if (centidegrees >= 11250 && centidegrees < 15750) return "Down"; // DownRight → treat as Down
            if (centidegrees >= 15750 && centidegrees < 20250) return "Down";
            if (centidegrees >= 20250 && centidegrees < 24750) return "Down"; // DownLeft → treat as Down
            if (centidegrees >= 24750 && centidegrees < 29250) return "Left";
            if (centidegrees >= 29250 && centidegrees < 33750) return "Up"; // UpLeft → treat as Up

            return "Up"; // Fallback
        }

        // ─────────────────────────────────────────────
        //  State capture
        // ─────────────────────────────────────────────

        /// <summary>
        /// Captures the current input state for the active device being recorded.
        /// Uses <see cref="_activeDeviceGuid"/> to find the specific device.
        /// Returns a clone to prevent mutation.
        /// </summary>
        private CustomInputState CaptureCurrentState()
        {
            if (_activeDeviceGuid == Guid.Empty)
                return null;

            var devices = SettingsManager.UserDevices?.Items;
            if (devices == null) return null;

            UserDevice ud;

            lock (SettingsManager.UserDevices.SyncRoot)
            {
                ud = devices.FirstOrDefault(d =>
                    d.InstanceGuid == _activeDeviceGuid && d.IsOnline);
            }

            if (ud == null || ud.InputState == null)
                return null;

            // Clone to prevent race conditions.
            return ud.InputState.Clone();
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

        /// <summary>The pad index the recording was for.</summary>
        public int PadIndex { get; set; }

        /// <summary>The descriptor string assigned (e.g., "Button 0", "Axis 1").</summary>
        public string Descriptor { get; set; }

        /// <summary>The detected input type.</summary>
        public MapType Type { get; set; }

        /// <summary>The detected input index.</summary>
        public int Index { get; set; }

        /// <summary>For POV: the direction detected.</summary>
        public string PovDirection { get; set; }
    }
}
