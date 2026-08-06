// Hand-written. Lives in its OWN file on purpose: it used to sit at the tail
// of ControllerOverlayLayout.cs, which tools/overlay_positions.py OVERWRITES
// wholesale, so every regeneration silently deleted this class.
namespace PadForge.Models2D;

/// <summary>
/// Two-way translation between the preview element grammar (the Xbox-style
/// TargetNames the 2D/3D preview art, hit zones, quadrant emitter, and
/// annotation anchors all speak) and a Nintendo slot's raw mapping grid
/// (RawBtn / RawAxis / RawPov rows). Axis names map mechanically with the
/// Neg suffix preserved: the quadrant emitter speaks screen convention
/// (positive Y = down), which is also HID wire convention, so no direction
/// crossing is needed (the gamepad path's Step 3 inversion is XInput-only).
///
/// The button table FORKS BY PROFILE, and not merely by length. The two
/// Nintendo families disagree about almost everything past the face buttons:
/// the original Pro Controller puts its D-pad on a hat and ends at Capture
/// (index 13), while the Switch 2 Pro spends four buttons on the D-pad, puts
/// the right-hand controls before the left-hand ones, and carries GR / GL / C
/// past Capture. Feeding a Switch 2 Pro through the original's table lit the
/// wrong art for eleven of its twenty-one buttons.
/// </summary>
public static class NintendoPreviewMap
{
    // Index = raw button number on the switch-pro wire: face B A Y X, L R,
    // ZL ZR (digital), Minus Plus, stick clicks, Home, Capture.
    private static readonly string[] PreviewBySwitchProBtn =
    {
        "ButtonB", "ButtonA", "ButtonY", "ButtonX",           // 0-3
        "LeftShoulder", "RightShoulder",                       // 4-5  L R
        "LeftTrigger", "RightTrigger",                         // 6-7  ZL ZR (digital)
        "ButtonBack", "ButtonStart",                           // 8-9  Minus Plus
        "LeftThumbButton", "RightThumbButton",                 // 10-11
        "ButtonGuide", "ButtonShare",                          // 12-13 Home Capture
    };

    // Index = raw button number on the switch2-pro wire. Order is the field
    // list of the profile's report 0x09 button masks, byte 3 then 4 then 5.
    private static readonly string[] PreviewBySwitch2ProBtn =
    {
        "ButtonB", "ButtonA", "ButtonY", "ButtonX",            // 0-3
        "RightShoulder", "RightTrigger",                        // 4-5  R ZR
        "ButtonStart", "RightThumbButton",                      // 6-7  Plus RS
        "DPadDown", "DPadRight", "DPadLeft", "DPadUp",          // 8-11 D-pad as BUTTONS
        "LeftShoulder", "LeftTrigger",                          // 12-13 L ZL
        "ButtonBack", "LeftThumbButton",                        // 14-15 Minus LS
        "ButtonGuide", "ButtonShare",                           // 16-17 Home Capture
        "RightPaddle", "LeftPaddle",                            // 18-19 GR GL
        "ButtonC",                                              // 20    C
    };

    private static readonly string[] PreviewByRawAxis =
    {
        "LeftThumbAxisX", "LeftThumbAxisY",
        "RightThumbAxisX", "RightThumbAxisY",
    };

    private static bool IsSwitch2(string profileId) =>
        !string.IsNullOrEmpty(profileId)
        && profileId.StartsWith("switch2-pro", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The wire table for a profile. Switch 2 Pro gets its own;
    /// everything else falls back to the original Pro Controller's, which is
    /// also the safe answer for a null or unrecognised id.</summary>
    public static string[] ButtonTable(string profileId) =>
        IsSwitch2(profileId) ? PreviewBySwitch2ProBtn : PreviewBySwitchProBtn;

    /// <summary>Preview element name (optionally with a "Neg" suffix on a
    /// stick axis) to the raw grid target. Null when the element has no
    /// raw-surface counterpart.</summary>
    public static string ToRaw(string previewName, string profileId)
    {
        if (string.IsNullOrEmpty(previewName)) return null;

        bool neg = previewName.EndsWith("Neg", System.StringComparison.Ordinal);
        string name = neg ? previewName.Substring(0, previewName.Length - 3) : previewName;

        for (int i = 0; i < PreviewByRawAxis.Length; i++)
            if (name == PreviewByRawAxis[i])
                return neg ? $"RawAxis{i}Neg" : $"RawAxis{i}";
        if (neg) return null;

        var table = ButtonTable(profileId);
        for (int i = 0; i < table.Length; i++)
            if (name == table[i])
                return $"RawBtn{i}";

        // Only the original Pro Controller carries a hat. On the Switch 2
        // Pro the D-pad is four buttons, which the table above already
        // resolved, so falling through to a POV row here would invent a
        // control the descriptor does not declare.
        if (!IsSwitch2(profileId)
            && name.StartsWith("DPad", System.StringComparison.Ordinal))
            return "RawPov0" + name.Substring(4);
        return null;
    }

    /// <summary>Raw grid target back to the preview element name (Neg
    /// preserved on axes). Null when the raw target has no preview element
    /// (e.g. an out-of-range index).</summary>
    public static string ToPreview(string rawName, string profileId)
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
            var table = ButtonTable(profileId);
            if (int.TryParse(rawName.Substring(6), out int i)
                && i >= 0 && i < table.Length)
                return table[i];
            return null;
        }
        if (rawName.StartsWith("RawPov0", System.StringComparison.Ordinal))
            return "DPad" + rawName.Substring(7);
        return null;
    }
}
