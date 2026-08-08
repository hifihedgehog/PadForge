namespace PadForge.Engine.Common.Mapping
{
    /// <summary>
    /// Classifies a mapping target by its natural value range. Drives the
    /// per-target combine-mode menu and the source-to-target coercion table
    /// in <see cref="SourceCoercion"/>.
    /// </summary>
    public enum TargetKind
    {
        /// <summary>Boolean target (e.g. ButtonA). Output is bool.</summary>
        Button,

        /// <summary>Bipolar axis target (e.g. LeftThumbAxisX). Output is
        /// float in [-1, +1].</summary>
        BipolarAxis,

        /// <summary>Unipolar trigger target (e.g. LeftTrigger). Output is
        /// float in [0, +1].</summary>
        Trigger,

        /// <summary>Boolean POV-direction target (e.g. DPadUp). Output is
        /// bool. Combined-DPad target is special-cased; it doesn't go
        /// through the per-row coercion path.</summary>
        PovDirection,
    }

    /// <summary>Looks up the <see cref="TargetKind"/> for a row's
    /// <c>Target</c> name. Centralizes the mapping so the combine-mode
    /// menu, coercion table, and Step 3 dispatch agree on the
    /// classification.</summary>
    public static class TargetKindResolver
    {
        public static TargetKind Resolve(string target)
        {
            if (string.IsNullOrEmpty(target)) return TargetKind.Button;

            // VR targets (issue #49): stick axes are bipolar, trigger and
            // grip pulls are unipolar; every other Vr name (clicks,
            // touches, System) is a button via the default arm. The
            // "...Click" names end past the axis suffixes, so the
            // suffix checks below cannot misfire on them.
            if (target.StartsWith("Vr", System.StringComparison.Ordinal))
            {
                if (target.EndsWith("StickX", System.StringComparison.Ordinal)
                    || target.EndsWith("StickY", System.StringComparison.Ordinal))
                    return TargetKind.BipolarAxis;
                if (target.EndsWith("Trigger", System.StringComparison.Ordinal)
                    || target.EndsWith("Grip", System.StringComparison.Ordinal))
                    return TargetKind.Trigger;
                return TargetKind.Button;
            }

            switch (target)
            {
                case "LeftTrigger":
                case "RightTrigger":
                    return TargetKind.Trigger;

                case "LeftThumbAxisX":
                case "LeftThumbAxisY":
                case "RightThumbAxisX":
                case "RightThumbAxisY":
                    return TargetKind.BipolarAxis;

                case "DPadUp":
                case "DPadDown":
                case "DPadLeft":
                case "DPadRight":
                    return TargetKind.PovDirection;

                default:
                    // Everything else (ButtonA/B/X/Y, LeftShoulder, ButtonBack,
                    // ButtonStart, ButtonGuide, ButtonShare, LeftThumbButton,
                    // RightThumbButton, etc.) is a button target. The legacy
                    // "DPad" (combined POV) string sneaks through here as a
                    // button — Step 3 special-cases it before the kind-based
                    // dispatch runs.
                    return TargetKind.Button;
            }
        }
    }
}
