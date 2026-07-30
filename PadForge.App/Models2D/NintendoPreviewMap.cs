// Hand-written. Lives in its OWN file on purpose: it used to sit at the tail
// of ControllerOverlayLayout.cs, which tools/overlay_positions.py OVERWRITES
// wholesale, so every regeneration silently deleted this class.
namespace PadForge.Models2D;

/// <summary>
/// Two-way translation between the preview element grammar (the Xbox-style
/// TargetNames the 2D/3D preview art, hit zones, quadrant emitter, and
/// annotation anchors all speak) and a Nintendo slot's raw mapping grid
/// (RawBtn / RawAxis / RawPov rows). The button-index correspondence is the
/// switch-pro wire order (B A Y X, L R, ZL ZR, Minus Plus, LS RS, Home,
/// Capture), the same table PadViewModel.UpdateNintendoPreviewFromRaw uses
/// in the state-to-preview direction. Axis names map mechanically with the
/// Neg suffix preserved: the quadrant emitter speaks screen convention
/// (positive Y = down), which is also HID wire convention, so no direction
/// crossing is needed (the gamepad path's Step 3 inversion is XInput-only).
/// </summary>
public static class NintendoPreviewMap
{
    // Index = raw button number on the switch-pro wire.
    private static readonly string[] PreviewByRawBtn =
    {
        "ButtonB", "ButtonA", "ButtonY", "ButtonX",          // 0-3
        "LeftShoulder", "RightShoulder",                      // 4-5  L R
        "LeftTrigger", "RightTrigger",                        // 6-7  ZL ZR (digital)
        "ButtonBack", "ButtonStart",                          // 8-9  Minus Plus
        "LeftThumbButton", "RightThumbButton",                // 10-11
        "ButtonGuide", "ButtonShare",                         // 12-13 Home Capture
    };

    private static readonly string[] PreviewByRawAxis =
    {
        "LeftThumbAxisX", "LeftThumbAxisY",
        "RightThumbAxisX", "RightThumbAxisY",
    };

    /// <summary>Preview element name (optionally with a "Neg" suffix on a
    /// stick axis) to the raw grid target. Null when the element has no
    /// raw-surface counterpart.</summary>
    public static string ToRaw(string previewName)
    {
        if (string.IsNullOrEmpty(previewName)) return null;

        bool neg = previewName.EndsWith("Neg", System.StringComparison.Ordinal);
        string name = neg ? previewName.Substring(0, previewName.Length - 3) : previewName;

        for (int i = 0; i < PreviewByRawAxis.Length; i++)
            if (name == PreviewByRawAxis[i])
                return neg ? $"RawAxis{i}Neg" : $"RawAxis{i}";
        if (neg) return null;

        for (int i = 0; i < PreviewByRawBtn.Length; i++)
            if (name == PreviewByRawBtn[i])
                return $"RawBtn{i}";
        if (name.StartsWith("DPad", System.StringComparison.Ordinal))
            return "RawPov0" + name.Substring(4);
        return null;
    }

    /// <summary>Raw grid target back to the preview element name (Neg
    /// preserved on axes). Null when the raw target has no preview element
    /// (e.g. an out-of-range index).</summary>
    public static string ToPreview(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return null;

        if (rawName.StartsWith("RawAxis", System.StringComparison.Ordinal))
        {
            bool neg = rawName.EndsWith("Neg", System.StringComparison.Ordinal);
            string idxStr = rawName.Substring(7, rawName.Length - 7 - (neg ? 3 : 0));
            if (int.TryParse(idxStr, out int i) && i >= 0 && i < PreviewByRawAxis.Length)
                return neg ? PreviewByRawAxis[i] + "Neg" : PreviewByRawAxis[i];
            return null;
        }
        if (rawName.StartsWith("RawBtn", System.StringComparison.Ordinal))
        {
            if (int.TryParse(rawName.Substring(6), out int i)
                && i >= 0 && i < PreviewByRawBtn.Length)
                return PreviewByRawBtn[i];
            return null;
        }
        if (rawName.StartsWith("RawPov0", System.StringComparison.Ordinal))
            return "DPad" + rawName.Substring(7);
        return null;
    }
}
