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
    /// hats as <c>"POV N Up/Down/Left/Right"</c>. Enumeration order mirrors that
    /// method exactly (axes, then sliders, then buttons, then hats), so the
    /// generated rows are the same rows a user would author by hand, one at a
    /// time.</para>
    ///
    /// <para>All axes clone as full-range bipolar Extended stick axes
    /// (TriggerCount stays 0). A passthrough proxy has no reason to treat any
    /// physical axis as a unipolar trigger, and reading the source through the
    /// bipolar evaluator preserves its natural rest and travel. Extended tops out
    /// at 8 axes, 128 buttons, and 4 POVs (DirectInput limits); anything the
    /// device exposes beyond those caps is reported as unmapped rather than
    /// dropped silently.</para>
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

            EnumerateInputs(ud, out var axisDescriptors, out var buttonDescriptors, out var povInputIndices);

            // ── Axes (+ sliders) → bipolar Extended stick axes ──
            result.AxesAvailable = axisDescriptors.Count;
            int axesMapped = Math.Min(axisDescriptors.Count, MaxAxes);
            result.AxesMapped = axesMapped;
            // ceil(axesMapped / 2) sticks, no triggers. 0 axes → 0 sticks.
            result.Sticks = (axesMapped + 1) / 2;
            result.Triggers = 0;
            for (int k = 0; k < axesMapped; k++)
                result.Rows.Add(new CloneRow($"ExtendedAxis{k}", axisDescriptors[k]));

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
        /// Produces the ordered source-descriptor lists the clone maps from,
        /// mirroring <c>MappingDisplayResolver.BuildInputChoices</c>: when the
        /// device has enumerated <see cref="UserDevice.DeviceObjects"/> they drive
        /// the order and indices (axes, then sliders, then buttons, then hats);
        /// otherwise the capability counts are the fallback. Kept in the Engine so
        /// the generator is unit-testable without the App layer.
        /// </summary>
        internal static void EnumerateInputs(
            UserDevice ud,
            out List<string> axisDescriptors,
            out List<string> buttonDescriptors,
            out List<int> povInputIndices)
        {
            axisDescriptors = new List<string>();
            buttonDescriptors = new List<string>();
            povInputIndices = new List<int>();
            if (ud == null) return;

            if (ud.DeviceObjects != null && ud.DeviceObjects.Length > 0)
            {
                // Axes first (non-slider), then sliders. Both feed Extended axes.
                foreach (var obj in ud.DeviceObjects)
                    if (obj != null && obj.IsAxis && !obj.IsSlider)
                        axisDescriptors.Add($"Axis {obj.InputIndex}");
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

            // Fallback: no enumerated objects (offline device). Cap counts drive a
            // dense enumeration. Slider vs axis can't be told apart here, so every
            // axis is an "Axis N".
            for (int i = 0; i < ud.CapAxeCount; i++)
                axisDescriptors.Add($"Axis {i}");
            int btnCount = Math.Max(ud.CapButtonCount, ud.RawButtonCount);
            for (int i = 0; i < btnCount; i++)
                buttonDescriptors.Add($"Button {i}");
            for (int i = 0; i < ud.CapPovCount; i++)
                povInputIndices.Add(i);
        }
    }
}
