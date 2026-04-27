using System.Xml.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PadForge.ViewModels
{
    /// <summary>
    /// Per-slot Extended configuration. Drives stick/trigger/POV/button counts,
    /// HID descriptor generation, and mapping item generation. Counts come from
    /// the active HIDMaestro profile and may be overridden by the user.
    /// DirectInput limit: 8 axes max (shared between sticks and triggers),
    /// 128 buttons, 4 POVs.
    /// </summary>
    public class ExtendedSlotConfig : ObservableObject
    {
        /// <summary>DirectInput maximum axis count (shared between sticks and triggers).</summary>
        public const int MaxAxes = 8;

        private int _thumbstickCount = 2;
        public int ThumbstickCount
        {
            get => _thumbstickCount;
            set
            {
                // Each stick uses 2 axes. Clamp so total axes (sticks*2 + triggers) <= MaxAxes.
                int maxSticks = (MaxAxes - _triggerCount) / 2;
                SetProperty(ref _thumbstickCount, Math.Clamp(value, 0, Math.Max(maxSticks, 0)));
            }
        }

        private int _triggerCount = 2;
        public int TriggerCount
        {
            get => _triggerCount;
            set
            {
                // Each trigger uses 1 axis. Clamp so total axes (sticks*2 + triggers) <= MaxAxes.
                int maxTriggers = MaxAxes - _thumbstickCount * 2;
                SetProperty(ref _triggerCount, Math.Clamp(value, 0, Math.Max(maxTriggers, 0)));
            }
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

        private bool _customize;
        /// <summary>
        /// Master toggle for the Extended config bar's override fields. When
        /// false (default), the VC is built from the catalog profile with no
        /// customizations — Product String, layout counts, and OEM override
        /// are ignored even if they hold values. When true, each sub-field is
        /// applied on top of the catalog profile via HMProfileBuilder. Lets
        /// users flip between pristine catalog behavior and customized
        /// behavior with a single toggle, and keeps the UI honest about
        /// which fields actually affect the live device.
        /// </summary>
        public bool Customize
        {
            get => _customize;
            set => SetProperty(ref _customize, value);
        }

        private bool _oemNameOverride;
        /// <summary>
        /// Whether this slot claims the Windows DirectInput OEM-name table
        /// entry for its profile's VID:PID on create. When true, Step 5
        /// calls <see cref="HIDMaestro.HMOemNameOverride.Set"/> using
        /// <see cref="ProductString"/> as the label so joy.cpl and DirectInput
        /// UIs show that string instead of whatever Windows preloaded for
        /// the VID:PID (which wins over the profile's HID iProduct for
        /// common clone PIDs like 0079:0006 "PC TWIN SHOCK Gamepad").
        /// </summary>
        public bool OemNameOverride
        {
            get => _oemNameOverride;
            set => SetProperty(ref _oemNameOverride, value);
        }

        private string _productString = string.Empty;
        /// <summary>
        /// User-editable product string. Populated from the active profile's
        /// <c>ProductString</c> when a profile is selected and the field is
        /// empty. When <see cref="OemNameOverride"/> is enabled this becomes
        /// the label pushed to the DirectInput OEM-name registry via
        /// <see cref="HIDMaestro.HMOemNameOverride.Set"/>.
        /// </summary>
        public string ProductString
        {
            get => _productString;
            set => SetProperty(ref _productString, value ?? string.Empty);
        }

        /// <summary>Total Extended axes = ThumbstickCount * 2 + TriggerCount (max 8).</summary>
        public int TotalAxes => Math.Min(ThumbstickCount * 2 + TriggerCount, MaxAxes);

        /// <summary>Maximum thumbstick count given current TriggerCount.</summary>
        public int MaxThumbsticks => (MaxAxes - _triggerCount) / 2;

        /// <summary>Maximum trigger count given current ThumbstickCount.</summary>
        public int MaxTriggers => MaxAxes - _thumbstickCount * 2;

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
    }

    /// <summary>
    /// Serializable DTO for persisting ExtendedSlotConfig in PadForge.xml.
    /// </summary>
    public class ExtendedSlotConfigData
    {
        [XmlAttribute] public int SlotIndex { get; set; }
        [XmlAttribute] public int ThumbstickCount { get; set; } = 2;
        [XmlAttribute] public int TriggerCount { get; set; } = 2;
        [XmlAttribute] public int PovCount { get; set; } = 1;
        [XmlAttribute] public int ButtonCount { get; set; } = 11;
        [XmlAttribute] public bool OemNameOverride { get; set; }
        [XmlAttribute] public string ProductString { get; set; } = string.Empty;
        [XmlAttribute] public bool Customize { get; set; }
    }
}
