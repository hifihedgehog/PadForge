using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    public enum VJoyPreset { Xbox360, DualShock4, Custom }

    /// <summary>
    /// Per-slot vJoy configuration. Drives stick/trigger/POV/button counts,
    /// HID descriptor generation, and mapping item generation.
    /// For Xbox360/DS4 presets, counts are fixed. For Custom, user-chosen.
    /// </summary>
    public class VJoySlotConfig : ObservableObject
    {
        private VJoyPreset _preset = VJoyPreset.Xbox360;
        public VJoyPreset Preset
        {
            get => _preset;
            set
            {
                if (SetProperty(ref _preset, value))
                    ApplyPresetDefaults();
            }
        }

        private int _thumbstickCount = 2;
        public int ThumbstickCount
        {
            get => _thumbstickCount;
            set => SetProperty(ref _thumbstickCount, Math.Clamp(value, 0, 8));
        }

        private int _triggerCount = 2;
        public int TriggerCount
        {
            get => _triggerCount;
            set => SetProperty(ref _triggerCount, Math.Clamp(value, 0, 6));
        }

        private int _povCount = 1;
        public int PovCount
        {
            get => _povCount;
            set => SetProperty(ref _povCount, Math.Clamp(value, 0, 4));
        }

        private int _buttonCount = 11;
        public int ButtonCount
        {
            get => _buttonCount;
            set => SetProperty(ref _buttonCount, Math.Clamp(value, 0, 128));
        }

        /// <summary>Total vJoy axes = ThumbstickCount * 2 + TriggerCount (max 16).</summary>
        public int TotalAxes => Math.Min(ThumbstickCount * 2 + TriggerCount, 16);

        /// <summary>
        /// Whether this config uses a gamepad-style layout (Xbox 360 or DS4 preset)
        /// vs a raw axis/button layout (Custom preset).
        /// </summary>
        public bool IsGamepadPreset => Preset != VJoyPreset.Custom;

        /// <summary>
        /// Computes the interleaved axis layout: [StickX, StickY, Trigger] per group.
        /// Sticks and triggers alternate so standard convention holds (X,Y,Z → LX,LY,LT; RX,RY,RZ → RX,RY,RT).
        /// </summary>
        public void ComputeAxisLayout(out int[] stickAxisX, out int[] stickAxisY, out int[] triggerAxis)
        {
            stickAxisX = new int[ThumbstickCount];
            stickAxisY = new int[ThumbstickCount];
            triggerAxis = new int[TriggerCount];
            int interleave = Math.Min(ThumbstickCount, TriggerCount);

            for (int g = 0; g < interleave; g++)
            {
                stickAxisX[g] = g * 3;
                stickAxisY[g] = g * 3 + 1;
                triggerAxis[g] = g * 3 + 2;
            }

            int offset = interleave * 3;
            for (int i = interleave; i < ThumbstickCount; i++)
            {
                stickAxisX[i] = offset;
                stickAxisY[i] = offset + 1;
                offset += 2;
            }
            for (int i = interleave; i < TriggerCount; i++)
                triggerAxis[i] = offset++;
        }

        public void ApplyPresetDefaults()
        {
            switch (_preset)
            {
                case VJoyPreset.Xbox360:
                    ThumbstickCount = 2;
                    TriggerCount = 2;
                    PovCount = 1;
                    ButtonCount = 11;
                    break;
                case VJoyPreset.DualShock4:
                    ThumbstickCount = 2;
                    TriggerCount = 2;
                    PovCount = 1;
                    ButtonCount = 14;
                    break;
                // Custom: keep current values
            }
        }
    }

    /// <summary>
    /// Serializable DTO for persisting VJoySlotConfig in PadForge.xml.
    /// </summary>
    public class VJoySlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        [XmlAttribute] public VJoyPreset Preset { get; set; }
        [XmlAttribute] public int ThumbstickCount { get; set; } = 2;
        [XmlAttribute] public int TriggerCount { get; set; } = 2;
        [XmlAttribute] public int PovCount { get; set; } = 1;
        [XmlAttribute] public int ButtonCount { get; set; } = 11;
    }
}
