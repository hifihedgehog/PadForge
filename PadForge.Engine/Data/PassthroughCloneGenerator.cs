using System;
using System.Collections.Generic;

namespace PadForge.Engine.Data
{
    /// <summary>
    /// Generates a 1:1 "passthrough clone" of a physical device onto an Extended
    /// virtual controller: every physical axis, slider, button, and hat direction
    /// maps to the same-indexed Extended output, so a DirectInput consumer sees a
    /// faithful mirror of the source device (issue #196).
    ///
    /// <para>The descriptor strings match what the mapping picker stores for the
    /// same inputs (<c>MappingDisplayResolver.BuildInputChoices</c>): axes as
    /// <c>"Axis N"</c>, sliders as <c>"Slider N"</c>, buttons as <c>"Button N"</c>,
    /// hats as <c>"POV N Up/Down/Left/Right"</c>. Input classes enumerate in the
    /// picker's order (axes, then sliders, then buttons, then hats) and each
    /// hat fans out to the picker's four direction descriptors, so the
    /// generated rows are the same rows a user would author by hand, one at a
    /// time.</para>
    ///
    /// <para>Axis classification: raw devices (joysticks, wheels, HOTAS) clone
    /// every axis as a full-range bipolar Extended stick axis, because HID and
    /// DirectInput carry no trigger concept for them and the bipolar identity
    /// read transmits the axis end to end unchanged. SDL-recognized gamepads DO
    /// carry a reliable signal: the device-object convention fixes Left/Right
    /// Trigger at axis positions 2 and 5 (<c>SdlDeviceWrapper.GetGamepadAxisName</c>,
    /// LX LY LT RX RY RT, with #193 extra axes at 6+), so those two route to
    /// Extended trigger slots and everything else to sticks. The interleaved
    /// axis layout (<c>ExtendedSlotConfig.ComputeAxisLayout</c>, replicated
    /// below) places trigger slots at the same flat indices the gamepad uses,
    /// so identity holds there too: a standard 6-axis pad clones as
    /// ExtendedAxis0..5 ← Axis 0..5 with 2 and 5 as triggers.</para>
    ///
    /// <para>Extended tops out at 8 axes, 128 buttons, and 4 POVs (DirectInput
    /// limits); anything the device exposes beyond those caps is reported as
    /// unmapped rather than dropped silently.</para>
    /// </summary>
    public static class PassthroughCloneGenerator
    {
        /// <summary>DirectInput axis ceiling shared by sticks and triggers.</summary>
        public const int MaxAxes = 8;
        /// <summary>Extended button ceiling.</summary>
        public const int MaxButtons = 128;
        /// <summary>Extended POV ceiling.</summary>
        public const int MaxPovs = 4;

        private static readonly string[] PovDirections = { "Up", "Down", "Left", "Right" };

        /// <summary>One generated identity row: an Extended output target and the
        /// source descriptor that feeds it.</summary>
        public readonly struct CloneRow
        {
            public CloneRow(string target, string descriptor)
            {
                Target = target;
                Descriptor = descriptor;
            }

            /// <summary>Extended output key, e.g. <c>"ExtendedAxis0"</c>,
            /// <c>"ExtendedBtn3"</c>, <c>"ExtendedPov0Up"</c>.</summary>
            public string Target { get; }

            /// <summary>Source descriptor, e.g. <c>"Axis 0"</c>, <c>"Button 3"</c>,
            /// <c>"POV 0 Up"</c>. Owned by the cloned device.</summary>
            public string Descriptor { get; }
        }

        /// <summary>The generated clone: the Extended layout to apply plus the
        /// identity rows to write. Also carries pre-clamp availability counts so
        /// the caller can report what was left unmapped when the device exceeds an
        /// Extended cap.</summary>
        public sealed class CloneResult
        {
            // Extended layout to apply.
            public int Sticks { get; set; }
            public int Triggers { get; set; }
            public int Povs { get; set; }
            public int Buttons { get; set; }

            /// <summary>Axis slots the applied layout actually exposes. Each
            /// stick carries two axes, so an odd-axis device gets one more
            /// layout slot than it fills (the tail slot stays unmapped).</summary>
            public int LayoutAxes => Sticks * 2 + Triggers;

            // Identity rows (target ← descriptor).
            public List<CloneRow> Rows { get; } = new List<CloneRow>();

            // What the device exposes, before Extended caps.
            public int AxesAvailable { get; set; }
            public int ButtonsAvailable { get; set; }
            public int PovsAvailable { get; set; }

            // What actually mapped, after caps.
            public int AxesMapped { get; set; }
            public int ButtonsMapped { get; set; }
            public int PovsMapped { get; set; }

            /// <summary>True when the device exposed more inputs of some class than
            /// Extended can carry, so the tail was left unmapped.</summary>
            public bool HasOverflow =>
                AxesAvailable > AxesMapped
                || ButtonsAvailable > ButtonsMapped
                || PovsAvailable > PovsMapped;
        }

        /// <summary>
        /// Builds the identity clone for <paramref name="ud"/>. Never returns null;
        /// a device with no inputs yields an empty layout and no rows.
        /// </summary>
        public static CloneResult Generate(UserDevice ud)
        {
            var result = new CloneResult();
            if (ud == null) return result;

            EnumerateInputs(ud, out var axisDescriptors, out var triggerDescriptors,
                out var buttonDescriptors, out var povInputIndices);

            // ── Axes → Extended stick + trigger slots ──
            // Triggers first (a gamepad has at most two and they carry the only
            // reliable classification), then sticks fill what remains of the
            // 8-axis DirectInput budget.
            result.AxesAvailable = axisDescriptors.Count + triggerDescriptors.Count;
            int triggersMapped = Math.Min(triggerDescriptors.Count, MaxAxes);
            int maxStickAxes = ((MaxAxes - triggersMapped) / 2) * 2;
            int stickAxesMapped = Math.Min(axisDescriptors.Count, maxStickAxes);
            result.AxesMapped = stickAxesMapped + triggersMapped;
            // ceil(stickAxesMapped / 2) sticks. 0 axes → 0 sticks.
            result.Sticks = (stickAxesMapped + 1) / 2;
            result.Triggers = triggersMapped;

            // Slot placement mirrors ExtendedSlotConfig.ComputeAxisLayout:
            // interleaved [StickX, StickY, Trigger] groups, then leftover stick
            // pairs, then leftover triggers. For the gamepad shape (2 sticks +
            // 2 triggers) this lands triggers on flat indices 2 and 5, matching
            // the source positions, so the clone stays index-identical.
            int interleave = Math.Min(result.Sticks, triggersMapped);
            var stickSlots = new List<int>(result.Sticks * 2);
            var triggerSlots = new List<int>(triggersMapped);
            for (int g = 0; g < interleave; g++)
            {
                stickSlots.Add(g * 3);
                stickSlots.Add(g * 3 + 1);
                triggerSlots.Add(g * 3 + 2);
            }
            int slotOffset = interleave * 3;
            for (int i = interleave; i < result.Sticks; i++)
            {
                stickSlots.Add(slotOffset);
                stickSlots.Add(slotOffset + 1);
                slotOffset += 2;
            }
            for (int i = interleave; i < triggersMapped; i++)
                triggerSlots.Add(slotOffset++);

            for (int k = 0; k < stickAxesMapped; k++)
                result.Rows.Add(new CloneRow($"ExtendedAxis{stickSlots[k]}", axisDescriptors[k]));
            for (int k = 0; k < triggersMapped; k++)
                result.Rows.Add(new CloneRow($"ExtendedAxis{triggerSlots[k]}", triggerDescriptors[k]));

            // ── Buttons ──
            result.ButtonsAvailable = buttonDescriptors.Count;
            int buttonsMapped = Math.Min(buttonDescriptors.Count, MaxButtons);
            result.ButtonsMapped = buttonsMapped;
            result.Buttons = buttonsMapped;
            for (int k = 0; k < buttonsMapped; k++)
                result.Rows.Add(new CloneRow($"ExtendedBtn{k}", buttonDescriptors[k]));

            // ── POVs (four directions each) ──
            result.PovsAvailable = povInputIndices.Count;
            int povsMapped = Math.Min(povInputIndices.Count, MaxPovs);
            result.PovsMapped = povsMapped;
            result.Povs = povsMapped;
            for (int k = 0; k < povsMapped; k++)
            {
                int srcHat = povInputIndices[k];
                foreach (string dir in PovDirections)
                    result.Rows.Add(new CloneRow($"ExtendedPov{k}{dir}", $"POV {srcHat} {dir}"));
            }

            return result;
        }

        /// <summary>
        /// True when this axis position carries the gamepad trigger convention:
        /// the device is gamepad-class and the axis sits at position 2 (Left
        /// Trigger) or 5 (Right Trigger). Positions are fixed by
        /// <c>SdlDeviceWrapper.GetGamepadAxisName</c> (LX LY LT RX RY RT); #193
        /// extra generic axes carry indices 6+ and never collide. Raw joysticks
        /// have no trigger signal anywhere in HID/DirectInput, so nothing else
        /// classifies as a trigger.
        /// </summary>
        private static bool IsGamepadTriggerAxis(UserDevice ud, int inputIndex)
            => ud.CapType == InputDeviceType.Gamepad && (inputIndex == 2 || inputIndex == 5);

        /// <summary>
        /// Produces the ordered source-descriptor lists the clone maps from,
        /// mirroring <c>MappingDisplayResolver.BuildInputChoices</c>: when the
        /// device has enumerated <see cref="UserDevice.DeviceObjects"/> they drive
        /// the order and indices (axes, then sliders, then buttons, then hats);
        /// otherwise the capability counts are the fallback. Gamepad trigger axes
        /// (positions 2 and 5) split into their own list so they land on Extended
        /// trigger slots. Kept in the Engine so the generator is unit-testable
        /// without the App layer.
        /// </summary>
        internal static void EnumerateInputs(
            UserDevice ud,
            out List<string> axisDescriptors,
            out List<string> triggerDescriptors,
            out List<string> buttonDescriptors,
            out List<int> povInputIndices)
        {
            axisDescriptors = new List<string>();
            triggerDescriptors = new List<string>();
            buttonDescriptors = new List<string>();
            povInputIndices = new List<int>();
            if (ud == null) return;

            if (ud.DeviceObjects != null && ud.DeviceObjects.Length > 0)
            {
                // Axes first (non-slider), then sliders. Both feed Extended axes.
                foreach (var obj in ud.DeviceObjects)
                {
                    if (obj == null || !obj.IsAxis || obj.IsSlider) continue;
                    if (IsGamepadTriggerAxis(ud, obj.InputIndex))
                        triggerDescriptors.Add($"Axis {obj.InputIndex}");
                    else
                        axisDescriptors.Add($"Axis {obj.InputIndex}");
                }
                foreach (var obj in ud.DeviceObjects)
                    if (obj != null && obj.IsSlider)
                        axisDescriptors.Add($"Slider {obj.InputIndex}");
                foreach (var obj in ud.DeviceObjects)
                    if (obj != null && obj.IsButton)
                        buttonDescriptors.Add($"Button {obj.InputIndex}");
                foreach (var obj in ud.DeviceObjects)
                    if (obj != null && obj.IsPov)
                        povInputIndices.Add(obj.InputIndex);
                return;
            }

            // Fallback: no enumerated objects (an offline device, or a device
            // class that never populates DeviceObjects). Cap counts drive a dense
            // enumeration; the gamepad trigger positions classify the same way.
            // Slider vs axis can't be told apart here, so every non-trigger axis
            // is an "Axis N". Known divergence from the picker: sparse-button
            // devices (TouchpadOverlayDevice, touchpad-only web clients) surface
            // only their live SupportedButtonIndices in the picker but enumerate
            // densely here; those are touchpad feeder devices with no Extended
            // passthrough use, so the dense fallback stands.
            for (int i = 0; i < ud.CapAxeCount; i++)
            {
                if (IsGamepadTriggerAxis(ud, i))
                    triggerDescriptors.Add($"Axis {i}");
                else
                    axisDescriptors.Add($"Axis {i}");
            }
            int btnCount = Math.Max(ud.CapButtonCount, ud.RawButtonCount);
            for (int i = 0; i < btnCount; i++)
                buttonDescriptors.Add($"Button {i}");
            for (int i = 0; i < ud.CapPovCount; i++)
                povInputIndices.Add(i);
        }
    }
}
