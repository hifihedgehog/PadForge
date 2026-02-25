using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Represents a single mapping row linking a physical input source
    /// (e.g., "Button 0", "Axis 1") to an XInput output target
    /// (e.g., "ButtonA", "LeftThumbAxisX").
    /// 
    /// Displayed in the mapping grid on the Pad page. Supports input
    /// recording to auto-detect the source.
    /// </summary>
    public class MappingItem : ObservableObject
    {
        /// <summary>
        /// Creates a mapping item.
        /// </summary>
        /// <param name="targetLabel">Human-readable label for the XInput target (e.g., "A", "Left Stick X").</param>
        /// <param name="targetSettingName">PadSetting property name (e.g., "ButtonA", "LeftThumbAxisX").</param>
        /// <param name="category">Category for grouping in tabs.</param>
        public MappingItem(string targetLabel, string targetSettingName, MappingCategory category)
        {
            TargetLabel = targetLabel ?? string.Empty;
            TargetSettingName = targetSettingName ?? string.Empty;
            Category = category;
        }

        // ─────────────────────────────────────────────
        //  Target (XInput output)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Human-readable label for the XInput output this row maps to.
        /// Example: "A", "Left Stick X", "Right Trigger".
        /// </summary>
        public string TargetLabel { get; }

        /// <summary>
        /// The PadSetting property name this mapping corresponds to.
        /// Used to read/write the mapping descriptor string from PadSetting.
        /// Example: "ButtonA", "LeftThumbAxisX", "RightTrigger".
        /// </summary>
        public string TargetSettingName { get; }

        /// <summary>
        /// Category for grouping mapping rows in tabs.
        /// </summary>
        public MappingCategory Category { get; }

        // ─────────────────────────────────────────────
        //  Source (physical input)
        // ─────────────────────────────────────────────

        private string _sourceDescriptor = string.Empty;

        /// <summary>
        /// The mapping descriptor string identifying the physical input source.
        /// Format: "{MapType} {Index}" or "IH{MapType} {Index}" or "POV {Index} {Direction}"
        /// Examples: "Button 0", "Axis 1", "IHAxis 2", "POV 0 Up", "Slider 0"
        /// Empty string means unmapped.
        /// </summary>
        public string SourceDescriptor
        {
            get => _sourceDescriptor;
            set
            {
                if (SetProperty(ref _sourceDescriptor, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(SourceDisplayText));
                    OnPropertyChanged(nameof(IsMapped));
                }
            }
        }

        /// <summary>
        /// Human-readable display text for the source.
        /// Shows the descriptor or "Not mapped" if empty.
        /// </summary>
        public string SourceDisplayText =>
            string.IsNullOrEmpty(_sourceDescriptor) ? "Not mapped" : _sourceDescriptor;

        /// <summary>
        /// Whether this mapping row has a source assigned.
        /// </summary>
        public bool IsMapped => !string.IsNullOrEmpty(_sourceDescriptor);

        // ─────────────────────────────────────────────
        //  Recording state
        // ─────────────────────────────────────────────

        private bool _isRecording;

        /// <summary>
        /// Whether this mapping row is currently in recording mode,
        /// waiting for the user to press a button or move an axis.
        /// </summary>
        public bool IsRecording
        {
            get => _isRecording;
            set
            {
                if (SetProperty(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordButtonText));
                }
            }
        }

        /// <summary>
        /// Text for the record button: "Record" or "Recording..." (with a visual cue).
        /// </summary>
        public string RecordButtonText => IsRecording ? "Recording..." : "Record";

        // ─────────────────────────────────────────────
        //  Live value display
        // ─────────────────────────────────────────────

        private string _currentValueText = string.Empty;

        /// <summary>
        /// Shows the current raw value of the source input in real-time.
        /// Updated at 30Hz when the Pad page is visible.
        /// </summary>
        public string CurrentValueText
        {
            get => _currentValueText;
            set => SetProperty(ref _currentValueText, value ?? string.Empty);
        }

        // ─────────────────────────────────────────────
        //  Options
        // ─────────────────────────────────────────────

        private bool _isInverted;

        /// <summary>
        /// Sets the source descriptor and syncs the IsInverted/IsHalfAxis flags
        /// from the "I" and "H" prefixes in the descriptor string.
        /// </summary>
        public void LoadDescriptor(string descriptor)
        {
            string d = descriptor ?? string.Empty;
            bool inv = false;
            bool half = false;

            if (d.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
            {
                inv = true;
                half = true;
            }
            else if (d.StartsWith("I", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
            {
                inv = true;
            }
            else if (d.StartsWith("H", StringComparison.OrdinalIgnoreCase) && d.Length > 1 && !char.IsDigit(d[1]))
            {
                half = true;
            }

            // Set flags first (without triggering RebuildDescriptor).
            _isInverted = inv;
            OnPropertyChanged(nameof(IsInverted));
            _isHalfAxis = half;
            OnPropertyChanged(nameof(IsHalfAxis));

            // Then set the descriptor string.
            SourceDescriptor = d;
        }

        /// <summary>Whether the axis value should be inverted.</summary>
        public bool IsInverted
        {
            get => _isInverted;
            set
            {
                if (SetProperty(ref _isInverted, value))
                    RebuildDescriptor();
            }
        }

        private bool _isHalfAxis;

        /// <summary>Whether to use only the upper half of the axis range.</summary>
        public bool IsHalfAxis
        {
            get => _isHalfAxis;
            set
            {
                if (SetProperty(ref _isHalfAxis, value))
                    RebuildDescriptor();
            }
        }

        /// <summary>
        /// Rebuilds the source descriptor when inversion or half-axis options change.
        /// Adds/removes the "I" and "H" prefixes.
        /// </summary>
        private void RebuildDescriptor()
        {
            if (string.IsNullOrEmpty(_sourceDescriptor))
                return;

            // Strip existing prefixes.
            string clean = _sourceDescriptor;
            if (clean.StartsWith("IH", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(2);
            else if (clean.StartsWith("I", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                clean = clean.Substring(1);
            else if (clean.StartsWith("H", StringComparison.OrdinalIgnoreCase) && clean.Length > 1 && !char.IsDigit(clean[1]))
                clean = clean.Substring(1);

            // Rebuild with new prefixes.
            string prefix = "";
            if (_isInverted) prefix += "I";
            if (_isHalfAxis) prefix += "H";

            SourceDescriptor = prefix + clean;
        }

        // ─────────────────────────────────────────────
        //  Commands
        // ─────────────────────────────────────────────

        private RelayCommand _toggleRecordCommand;

        /// <summary>Command to toggle recording mode for this mapping row.</summary>
        public RelayCommand ToggleRecordCommand =>
            _toggleRecordCommand ??= new RelayCommand(() =>
            {
                if (IsRecording)
                    StopRecordingRequested?.Invoke(this, EventArgs.Empty);
                else
                    StartRecordingRequested?.Invoke(this, EventArgs.Empty);
            });

        private RelayCommand _clearCommand;

        /// <summary>Command to clear the source assignment.</summary>
        public RelayCommand ClearCommand =>
            _clearCommand ??= new RelayCommand(() =>
            {
                SourceDescriptor = string.Empty;
                IsInverted = false;
                IsHalfAxis = false;
            });

        /// <summary>Raised when the user clicks Record on this row.</summary>
        public event EventHandler StartRecordingRequested;

        /// <summary>Raised when recording should stop on this row.</summary>
        public event EventHandler StopRecordingRequested;

        // ─────────────────────────────────────────────
        //  Display
        // ─────────────────────────────────────────────

        public override string ToString()
        {
            return $"{TargetLabel} ← {SourceDisplayText}";
        }
    }

    /// <summary>
    /// Categories for grouping mapping items in tabs.
    /// </summary>
    public enum MappingCategory
    {
        Buttons,
        DPad,
        Triggers,
        LeftStick,
        RightStick
    }
}
