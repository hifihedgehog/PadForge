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
/// the original Pro Controller reports its D-pad as a HID hat switch and ends
/// at Capture (index 13), while the Switch 2 Pro reports its D-pad as four
/// discrete buttons, puts the right-hand controls before the left-hand ones,
/// and carries GR / GL / C past Capture. Feeding a Switch 2 Pro through the
/// original's table lit the wrong art for eleven of its twenty-one buttons.
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
        "DPadDown", "DPadRight", "DPadLeft", "DPadUp",          // 8-11 D-pad, as discrete buttons
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
    /// also the safe answer for a null or unrecognised id.
    ///
    /// THIS TABLE IS THE ONLY PLACE A NINTENDO WIRE ORDER IS WRITTEN DOWN.
    /// It was copied into five consumers once (grid rows, raw labels, the
    /// state bridge, the automap defaults, and this map), each copy silently
    /// went stale for the Switch 2 Pro, and each one produced its own
    /// separate visible bug. Add a profile here and nowhere else.</summary>
    public static string[] ButtonTable(string profileId) =>
        IsSwitch2(profileId) ? PreviewBySwitch2ProBtn : PreviewBySwitchProBtn;

    /// <summary>Number of role-mapped buttons on a profile's wire. Sizes
    /// every raw surface: grid rows, SOCD bounds, macro pickers.</summary>
    public static int ButtonCount(string profileId) => ButtonTable(profileId).Length;

    /// <summary>Wire index of a preview role, or -1 when the pad has no such
    /// control. Callers author against ROLES and let this resolve the index,
    /// which is what keeps a second wire order from being written down.</summary>
    public static int IndexOf(string profileId, string previewName)
    {
        if (string.IsNullOrEmpty(previewName)) return -1;
        var table = ButtonTable(profileId);
        for (int i = 0; i < table.Length; i++)
            if (table[i] == previewName) return i;
        return -1;
    }

    /// <summary>True when the profile reports its D-pad as a HID hat switch
    /// (the original Pro Controller) rather than as four discrete buttons
    /// (the Switch 2 Pro). Both pads have a D-pad; they encode it
    /// differently, and a target that binds the wrong one binds nothing.</summary>
    public static bool DPadIsHat(string profileId) =>
        IndexOf(profileId, "DPadUp") < 0;

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

        // Only the original Pro Controller reports its D-pad as a hat. The
        // Switch 2 Pro has a D-pad too, but as four discrete buttons, which
        // the table above already resolved, so falling through to a POV row
        // here would invent a control its descriptor does not declare.
        if (!IsSwitch2(profileId)
            && name.StartsWith("DPad", System.StringComparison.Ordinal))
            return "RawPov0" + name.Substring(4);
        return null;
    }

    /// <summary>Rewrite a raw grid target from one profile's wire to
    /// another's, preserving the ROLE. Raw indices are wire-relative, so
    /// without this every existing binding silently changes meaning when the
    /// profile changes: RawBtn8 is Minus on the original Pro Controller and
    /// the D-pad's Down button on the Switch 2 Pro.
    ///
    /// Returns the new target, the input unchanged when the wires agree, or
    /// null when the target pad has no such control (the caller drops the
    /// binding rather than pointing it at wire that is not there). Axis and
    /// tuning keys are wire-independent and pass through untouched.</summary>
    public static string TranslateRawTarget(string rawName, string fromProfileId, string toProfileId)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;
        if (IsSwitch2(fromProfileId) == IsSwitch2(toProfileId)) return rawName;

        // Only BUTTON and HAT targets are wire-relative. Axis targets,
        // deadzones and every other tuning key are addressed the same way on
        // both pads, and running them through the button table resolved them
        // to roles it does not contain, which dropped every stick binding on
        // a profile change.
        bool isButton = rawName.StartsWith("RawBtn", System.StringComparison.Ordinal);
        bool isHat = rawName.StartsWith("RawPov", System.StringComparison.Ordinal);
        if (!isButton && !isHat) return rawName;

        string role = ToPreview(rawName, fromProfileId);
        if (role == null)
            return rawName;   // an index this pad does not use; leave it be

        int i = IndexOf(toProfileId, role);
        if (i >= 0) return $"RawBtn{i}";

        // The target reports this role on its hat instead of a button.
        if (role.StartsWith("DPad", System.StringComparison.Ordinal))
            return "RawPov0" + role.Substring(4);

        return null;   // the target pad genuinely has no such control
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
