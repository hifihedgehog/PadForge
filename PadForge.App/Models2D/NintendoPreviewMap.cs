// Hand-written. Lives in its OWN file on purpose: it used to sit at the tail
// of ControllerOverlayLayout.cs, which tools/overlay_positions.py OVERWRITES
// wholesale, so every regeneration silently deleted this class.
namespace PadForge.Models2D;

/// <summary>
/// Two-way translation between the preview element grammar (the Xbox-style
/// TargetNames the 2D/3D preview art, hit zones, quadrant emitter, and
/// annotation anchors all speak) and a lettered Extended slot's raw mapping
/// grid (RawBtn / RawAxis / RawPov rows). Axis names map mechanically with
/// the Neg suffix preserved: the quadrant emitter speaks screen convention
/// (positive Y = down), which is also HID wire convention, so no direction
/// crossing is needed (the gamepad path's Step 3 inversion is XInput-only).
///
/// The class keeps its original name, but it is the raw wire map for EVERY
/// profile family whose rows carry real control names rather than "Button
/// N": the two Nintendo families and the three Valve families. A family is
/// a wire: every raw index means the same control on every profile in it,
/// and nothing else can be assumed across families.
///
/// The button table FORKS BY FAMILY, and not merely by length. The original
/// Pro Controller reports its D-pad as a HID hat switch and ends at Capture;
/// the Switch 2 Pro reports its D-pad as four discrete buttons, puts the
/// right-hand controls before the left-hand ones, and carries GR / GL / C.
/// The Valve pads add trackpad clicks, rear grips or paddles and the Quick
/// Access button, and the 2015 pad has one stick where the others have two.
/// Feeding a pad through another family's table lights the wrong art and
/// binds the wrong wire.
///
/// THIS FILE IS THE ONLY PLACE ANY OF THESE WIRE ORDERS IS WRITTEN DOWN. The
/// Switch order was once copied into five consumers, each copy silently
/// went stale for the Switch 2 Pro, and each produced its own visible bug.
/// Add a family here and nowhere else. The Valve frame packers resolve their
/// slots through <see cref="IndexOf"/> for the same reason.
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

    // Steam Deck (steam-deck, steam-deck-composite). This is the automap
    // slot space the Deck frame packer was authored against: A0 B1 X2 Y3
    // LB4 RB5 View6 Menu7 LS8 RS9 Steam10 QAM11, then the rear buttons in
    // SDL's R1 L1 R2 L2 order (Paddle1 = R4, Paddle2 = L4, Paddle3 = R5,
    // Paddle4 = L5, the translator convention the 3D model uses too), then
    // the two pad clicks. The D-pad is a hat on this wire.
    private static readonly string[] PreviewByDeckBtn =
    {
        "ButtonA", "ButtonB", "ButtonX", "ButtonY",            // 0-3
        "LeftShoulder", "RightShoulder",                        // 4-5  L1 R1
        "ButtonBack", "ButtonStart",                            // 6-7  View Menu
        "LeftThumbButton", "RightThumbButton",                  // 8-9  L3 R3
        "ButtonGuide", "ButtonQuickAccess",                     // 10-11 Steam QAM
        "Paddle1", "Paddle2", "Paddle3", "Paddle4",             // 12-15 R4 L4 R5 L5
        "LeftTouchpadClick", "RightTouchpadClick",              // 16-17
    };

    // Steam Controller 2015 (steam-controller, steam-controller-composite).
    // One stick, so no right stick click; the right pad rides the right
    // stick axes. Two rear grips. The left pad's four directional click
    // zones are the "D-pad", a hat on this wire (SDL_hidapi_steam.c's
    // STEAM_DPAD_* masks are the left pad's quadrant bits).
    private static readonly string[] PreviewBySteamControllerBtn =
    {
        "ButtonA", "ButtonB", "ButtonX", "ButtonY",            // 0-3
        "LeftShoulder", "RightShoulder",                        // 4-5  bumpers
        "ButtonBack", "ButtonStart",                            // 6-7  the < and > buttons
        "LeftThumbButton",                                      // 8    stick click
        "ButtonGuide",                                          // 9    Steam
        "LeftGrip", "RightGrip",                                // 10-11
        "LeftTouchpadClick", "RightTouchpadClick",              // 12-13
    };

    // Steam Controller 2026 (steam-controller-2). Two sticks, a real D-pad
    // reported as four buttons (sc2-research HID_REPORT_FORMAT.md, the
    // TritonButtons table, verbatim from SDL3's controller_structs.h), four
    // rear buttons in the same R4 L4 R5 L5 order as the Deck, Quick Access.
    private static readonly string[] PreviewBySteamController2Btn =
    {
        "ButtonA", "ButtonB", "ButtonX", "ButtonY",            // 0-3
        "LeftShoulder", "RightShoulder",                        // 4-5
        "ButtonBack", "ButtonStart",                            // 6-7  View Menu
        "LeftThumbButton", "RightThumbButton",                  // 8-9  L3 R3
        "ButtonGuide", "ButtonQuickAccess",                     // 10-11 Steam QAM
        "Paddle1", "Paddle2", "Paddle3", "Paddle4",             // 12-15 R4 L4 R5 L5
        "LeftTouchpadClick", "RightTouchpadClick",              // 16-17
        "DPadUp", "DPadDown", "DPadLeft", "DPadRight",          // 18-21 discrete buttons
    };

    // Nintendo pads have no analog triggers, so ComputeAxisLayout packs the
    // two sticks at 0-3 with nothing between them.
    private static readonly string[] PreviewByNintendoAxis =
    {
        "LeftThumbAxisX", "LeftThumbAxisY",
        "RightThumbAxisX", "RightThumbAxisY",
    };

    // Valve pads carry two sticks and two analog triggers, which
    // ComputeAxisLayout interleaves as [LX LY LT | RX RY RT], the same order
    // the automap and the frame packers use. On the 2015 pad "RightThumb"
    // is the right trackpad ridden as a stick.
    private static readonly string[] PreviewByValveAxis =
    {
        "LeftThumbAxisX", "LeftThumbAxisY", "LeftTrigger",
        "RightThumbAxisX", "RightThumbAxisY", "RightTrigger",
    };

    /// <summary>A wire family. Every profile in a family shares one raw
    /// index space; none is shared across families.</summary>
    public enum Family { None, SwitchPro, Switch2Pro, SteamDeck, SteamController, SteamController2 }

    private static bool Starts(string id, string prefix) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);

    /// <summary>The wire family of a profile id, or None for a profile whose
    /// rows are numbered. Order matters: "steam-controller-2" starts with
    /// "steam-controller".</summary>
    public static Family FamilyOf(string profileId)
    {
        if (string.IsNullOrEmpty(profileId)) return Family.None;
        if (Starts(profileId, "switch2-pro")) return Family.Switch2Pro;
        if (string.Equals(profileId, "switch-pro", System.StringComparison.OrdinalIgnoreCase)) return Family.SwitchPro;
        if (Starts(profileId, "steam-deck")) return Family.SteamDeck;
        if (Starts(profileId, "steam-controller-2")) return Family.SteamController2;
        if (Starts(profileId, "steam-controller")) return Family.SteamController;
        return Family.None;
    }

    /// <summary>True for any profile whose grid rows carry real control
    /// names and whose raw indices are resolved through this map.</summary>
    public static bool IsLettered(string profileId) => FamilyOf(profileId) != Family.None;

    /// <summary>True for the three Valve families.</summary>
    public static bool IsValve(string profileId) => FamilyOf(profileId) is
        Family.SteamDeck or Family.SteamController or Family.SteamController2;

    private static bool IsSwitch2(string profileId) => FamilyOf(profileId) == Family.Switch2Pro;

    /// <summary>True when the two profiles share a wire: every raw button
    /// index means the same control on both, so existing raw targets need
    /// no translation between them.</summary>
    public static bool SameWireFamily(string profileIdA, string profileIdB) =>
        FamilyOf(profileIdA) == FamilyOf(profileIdB);

    /// <summary>The wire table for a profile. Each family gets its own;
    /// an unlettered or unrecognized id falls back to the original Pro
    /// Controller's, which is the safe answer for a null.</summary>
    public static string[] ButtonTable(string profileId) => FamilyOf(profileId) switch
    {
        Family.Switch2Pro => PreviewBySwitch2ProBtn,
        Family.SteamDeck => PreviewByDeckBtn,
        Family.SteamController => PreviewBySteamControllerBtn,
        Family.SteamController2 => PreviewBySteamController2Btn,
        _ => PreviewBySwitchProBtn,
    };

    /// <summary>The axis table for a profile: raw axis index to preview
    /// element. Valve pads interleave their analog triggers; Nintendo pads
    /// have none.</summary>
    public static string[] AxisTable(string profileId) =>
        IsValve(profileId) ? PreviewByValveAxis : PreviewByNintendoAxis;

    /// <summary>Stick count the raw surface has to be sized for.</summary>
    public static int StickCount(string profileId) =>
        FamilyOf(profileId) == Family.SteamController ? 2 : 2;   // the 2015 pad rides its right pad as a stick

    /// <summary>Analog trigger count on the wire (zero on Nintendo, whose
    /// ZL / ZR are buttons).</summary>
    public static int TriggerCount(string profileId) => IsValve(profileId) ? 2 : 0;

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

    /// <summary>Wire axis index of a preview axis role, or -1.</summary>
    public static int AxisIndexOf(string profileId, string previewName)
    {
        var table = AxisTable(profileId);
        for (int i = 0; i < table.Length; i++)
            if (table[i] == previewName) return i;
        return -1;
    }

    /// <summary>True when the profile reports its D-pad as a HID hat switch
    /// rather than as four discrete buttons. A target that binds the wrong
    /// one binds nothing.</summary>
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

        // Axes first: on a Valve pad "LeftTrigger" is an analog axis, on a
        // Nintendo pad it is a button, and the axis table decides which.
        var axes = AxisTable(profileId);
        for (int i = 0; i < axes.Length; i++)
            if (name == axes[i])
                return neg ? $"RawAxis{i}Neg" : $"RawAxis{i}";
        if (neg) return null;

        var table = ButtonTable(profileId);
        for (int i = 0; i < table.Length; i++)
            if (name == table[i])
                return $"RawBtn{i}";

        // A D-pad the table did not resolve is a hat, on the families that
        // have one. Falling through to a POV row on a pad that spends real
        // buttons on its D-pad would invent a control its wire lacks.
        if (DPadIsHat(profileId)
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
    /// binding rather than pointing it at wire that is not there). Tuning
    /// keys pass through untouched; axes translate by role, because the
    /// Valve and Nintendo axis tables disagree about where the right stick
    /// sits.</summary>
    public static string TranslateRawTarget(string rawName, string fromProfileId, string toProfileId)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;
        if (SameWireFamily(fromProfileId, toProfileId)) return rawName;

        bool isButton = rawName.StartsWith("RawBtn", System.StringComparison.Ordinal);
        bool isHat = rawName.StartsWith("RawPov", System.StringComparison.Ordinal);
        bool isAxis = rawName.StartsWith("RawAxis", System.StringComparison.Ordinal);
        if (!isButton && !isHat && !isAxis) return rawName;

        string role = ToPreview(rawName, fromProfileId);
        if (role == null)
        {
            // A button index the OUTGOING wire does not role-map cannot
            // have been authored by its grid: it is an orphan from an
            // earlier translation bug or hand-edited XML. Preserving it
            // would carry it forever and can mint DUPLICATE targets, so a
            // cross-family translation prunes it.
            return null;
        }
        return ToRaw(role, toProfileId);   // null when the target pad lacks the role
    }

    /// <summary>Raw grid target back to the preview element name (Neg
    /// preserved on axes). Null when the raw target has no preview element
    /// (e.g. an out-of-range index).</summary>
    public static string ToPreview(string rawName, string profileId)
    {
        if (string.IsNullOrEmpty(rawName)) return null;

        if (rawName.StartsWith("RawAxis", System.StringComparison.Ordinal))
        {
            var axes = AxisTable(profileId);
            bool neg = rawName.EndsWith("Neg", System.StringComparison.Ordinal);
            string idxStr = rawName.Substring(7, rawName.Length - 7 - (neg ? 3 : 0));
            if (int.TryParse(idxStr, out int i) && i >= 0 && i < axes.Length)
                return neg ? axes[i] + "Neg" : axes[i];
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
