using System;
using System.Collections.Generic;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Single source of truth for the lockdown-approved reason keys. Two
    /// readers compile this file: <c>ReportReasonLockdownTests</c> (in this
    /// project) asserts the curated fixture corpus against
    /// <see cref="CorpusMultiset"/>, and <c>tools/SteamWorkshopSweep</c>
    /// (which links this file as a shared source) reads
    /// <see cref="ApprovedKeys"/> to flag every wild-corpus report line
    /// outside the approved vocabulary. Change the list here and both stay
    /// in step by construction.
    /// </summary>
    internal static class ApprovedReasonLockdown
    {
        /// <summary>The approved corpus-wide reason-key multiset,
        /// "key=count" per entry, ordinal-sorted, one justification per
        /// key. Safety-net keys with zero corpus occurrences (for
        /// example ActivatorInputNotSupported, since v23 only the
        /// double-press layer-verb net) sit outside the multiset by
        /// construction.</summary>
        internal static readonly string[] CorpusMultiset =
        {
            // 5 -> 3 in v25: the stick-hosted MOUSE pairs consumed their
            // radii through ParamStickDeadZoneShape, retiring two.
            // 3 -> 1 in the 67fca4d9 pass: the same stamp reached the
            // joystick_move thumb-pair emitter, crossed and matched.
            // Round six (R3) closed the emitter that pass missed:
            // EmitMouseJoystickAxes (mouse_joystick / gyro_to_joystick)
            // now stamps its stick hosts and scopes the residual to the
            // finger / gyro lanes. The gyro-lean deflection pair also
            // names its dropped radii now (R5) instead of losing them
            // silently. So the residual CODE PATHS are: trackpad-hosted
            // pairs, gyro-hosted pairs, and the deflection pair; "only
            // the trackpad remains" below is a statement about the
            // CORPUS, not the code (round seven wording fix).
            // What remains is a genuine non-Axis-read boundary, not an
            // unfinished stamp: trackpad pairs ride Touchpad finger /
            // gesture descriptors and gyro pairs ride the rate / lean
            // reads, none of which pass through the Axis path where the
            // geometry applies. Retiring these needs those reads to gain
            // the pair test, not another stamp. The corpus count stays 1
            // (the joystick_move right_trackpad group in 1150803559); no
            // fixture authors deadzone keys on the other residual hosts.
            "Workshop_Tr_DeadZoneRadialResidual=1",
            // Steam-session/client: in-game Steam Input API action
            // blocks are delivered to the game by its own Steam
            // session. No virtual controller can feed them.
            "Workshop_Tr_GameActionsNotSupported=8",
            // Impossibility proof in code: every engine ShiftActivator
            // mode keys on the press edge (ReadActivatorInput has no
            // release-edge trigger), so release-hosted layer verbs
            // lower one edge early under this name (v19 T6).
            "Workshop_Tr_LayerReleaseEdgeApproximated=7",
            // Clean emission record, not a residual.
            "Workshop_Tr_MacroEmitted=90",
            // Clean emission record, not a residual.
            "Workshop_Tr_MenuEmitted=21",
            // Impossibility proof in code: mouse_dampening_trigger is a
            // live cross-input analog modulation and the row grammar's
            // only second-input constructs are the boolean AND gate and
            // the InvertOnHold sign flip. Proof at
            // ConfigTranslator.MouseModeTuningKeys (v18).
            "Workshop_Tr_MouseModeTuningDropped=3",
            // Steam-session/client: all five sites are action sets of
            // the two IGA fixtures (1129670518, 1172518660), switched
            // by the GAME through the Steam Input API. The config
            // authors no controller-side input that could host a
            // switch, so there is nothing to lower onto.
            "Workshop_Tr_PresetHasNoActivator=5",
            // Impossibility proof in code: the engine has no
            // remove-named-layer primitive, so REMOVE_LAYER lowers as a
            // press-to-step Cycle beside whatever engaged the layer and
            // a press can need one extra step before Base. Proof at the
            // REMOVE_LAYER lowering (v10 G8).
            //
            // 12 -> 8: four of these were never approximations. Where the
            // SAME input also ENGAGES that layer (Steam's usual shape, an
            // add_layer or hold_layer on a button below and a remove_layer
            // on that same button inside the layer), the engaging activator
            // already carries the return exactly: a Toggle turns the layer
            // off, a Hold drops it on release. The Cycle was a duplicate
            // that fought it rather than a construct standing in for a
            // missing one. Those four now drop and report Clean as the
            // layer emission they are, per
            // DropRemovesCoveredByTheirOwnToggle. Two were Toggle-covered
            // (2374887917, 3456927474) and two Hold-covered (3725174032,
            // both rear paddles). The eight that remain are removes on
            // inputs that do not engage the layer themselves, where the
            // Cycle is the only way back and the note still holds.
            // ShiftLayerEmitted is unchanged at 43: four activators left,
            // four entries arrived.
            "Workshop_Tr_RemoveLayerApproximated=8",
            // Impossibility proof in code (math): nonlinear per-leg
            // shaping does not commute with the rotation's two-source
            // Sum, so rotated legs carry only the linear knobs. Proof
            // at the withholding site (v19 T5).
            "Workshop_Tr_RotationNonlinearWithheld=1",
            // Clean emission record, not a residual.
            "Workshop_Tr_RowEmitted=987",
            // Impossibility proof in code: no circular-scratch angle
            // primitive, so wheel drag rides the detent rows and the
            // cycle steps forward-only. Proof at the scrollwheel
            // lowering's geometry note.
            "Workshop_Tr_ScrollWheelApproximated=5",
            // Clean emission record. 41 to 43 in v23: the two
            // touch-hosted hold_layer sites in 2374887917 build as
            // held touch-spot activators now.
            "Workshop_Tr_ShiftLayerEmitted=43",
            // Config-error: the layer produced no rows and each
            // binding's own entry names why, or a Base-hosted jump to
            // Base switches nothing. The note replaces silence.
            "Workshop_Tr_ShiftLayerEmpty=4",
            // Clean emission record (Partial only when the momentary
            // identity row rides beside the latch).
            "Workshop_Tr_ToggleLatchEmitted=1",
        };

        /// <summary>Approved reason keys with ZERO occurrences in the
        /// curated corpus (v25). The corpus multiset above pins the
        /// curated 30's exact counts; class membership is wider, because
        /// counts are corpus-specific and a key can be fully adjudicated
        /// while no curated fixture happens to exercise it. The wild
        /// sweep compares against the KEY CLASS
        /// (<see cref="ApprovedKeys"/> = multiset keys plus this list),
        /// so an approved class never surfaces in the wild digest as if
        /// it were unadjudicated (the v24 round listed SteamSystemAction
        /// there for exactly this structural gap). Same admission
        /// standard as the multiset: each entry carries its class and
        /// proof.</summary>
        internal static readonly string[] ZeroCorpusApprovedKeys =
        {
            // Config-error: the authored config references a preset index
            // that resolves to no preset (TryResolvePresetIndex walks the
            // config's own preset list). Honest reporting of the config's
            // own dangling reference; nothing exists to lower onto.
            "Workshop_Tr_MissingPreset",
            // Config-error: the menu group's inputs block is authored
            // EMPTY (zero touch_menu_button_N cells with activators).
            // Wild witness 1574823616 group 25: radial_menu with
            // inputs {}. The note replaces silence for the config's own
            // empty content; there are no cells to build.
            "Workshop_Tr_MenuEmpty",
            // Config-error: the mode_shift binding names a (slot, group)
            // pair its OWN hosting preset does not register as an active
            // modeshift (group_source_bindings is per preset). Wild
            // witness 3582936576: preset 1 hosts "mode_shift gyro 53"
            // while only preset 0 registers group 53, an authored
            // cross-set copy; other sightings reference group ids absent
            // from the whole config. Honest dangling-reference reporting,
            // the MissingPreset shape on the mode-shift lane.
            "Workshop_Tr_MissingModeShiftGroup",
            // Steam-session/client: controller_action verbs that drive
            // the Steam client itself or a Steam-side subsystem
            // (toggle_magnifier, steammusic_*, gr_* game recording,
            // ts_* touchscreen, bigpicture_open, host_poweroff,
            // dots_per_360_calibration_spin, turn_to_face_direction; the
            // SteamClientActions set and its prefix families, harvested
            // from steamclient.dll's own token table in v13). Only the
            // game's own Steam session can deliver these; no virtual
            // controller can.
            "Workshop_Tr_SteamSystemAction",
            // Physically-impossible input (v26): the Steam Link
            // on-screen touch controls. button_macro5..7 (enum bits
            // 37-39, ATTRIBCAP_MISC5..7) name buttons past SDL's whole
            // gamepad surface (SDL_GAMEPAD_BUTTON_MISC6 = Steam macro 4
            // is the last misc slot, SDL_gamepad.h), and
            // button_macro1finger / 2finger (bits 48/49) are the mobile
            // overlay's "One Finger Tap" / "Two Finger Tap" (shipped
            // strings; the glyph map files them under eIgnore). PadForge
            // input comes exclusively through SDL, so no drivable
            // controller carries them. Proof at
            // PhysicalSlotResolver.IsMobileTouchOnlyToken.
            "Workshop_Tr_MobileTouchSurfaceOnly",
            // Config-error (v26): a chord activator whose settings carry
            // no chord_button (absent key, or the shared enum's 0 = the
            // none/default sentinel; Steam's serializer omits defaults).
            // The partner picker was never set, so not even Steam can
            // fire the chord; there is nothing to gate on. Proof at
            // ConfigTranslator.HasChordPartner.
            "Workshop_Tr_ChordWithoutPartner",
            // Impossibility proof in code (v26 re-attack): a stick has
            // no absolute position surface, so the 1:1 region map cannot
            // exist there; the clamp macro (engaged on the v17
            // deflection-ring read since v26) is the equivalent
            // construct and this Partial names the approximation. Proof
            // at TranslateMouseRegion's clamp branch.
            "Workshop_Tr_MouseRegionApproximated",
            // Impossibility-equivalent (v26 re-attack): Steam's
            // camera_reset unwinds STEAM'S OWN emitted-motion ledger, a
            // best-effort even there (no remapper knows the game
            // camera). PadForge re-references its gyro aim integration
            // state (GyroRecenter), the equivalent state it owns;
            // rebuilding Steam's ledger would reproduce the same
            // approximation with more state, not more fidelity. Proof on
            // TranslationReasons.CameraResetApproximated.
            "Workshop_Tr_CameraResetApproximated",
        };

        /// <summary>The approved reason-key CLASS: the multiset keys (the
        /// "=count" tail stripped) plus the zero-corpus approved keys.
        /// The wild-corpus sweep reports every line outside this set.</summary>
        internal static IReadOnlySet<string> ApprovedKeys { get; } = BuildKeys();

        private static IReadOnlySet<string> BuildKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in CorpusMultiset)
            {
                int eq = entry.LastIndexOf('=');
                keys.Add(eq < 0 ? entry : entry.Substring(0, eq));
            }
            foreach (var key in ZeroCorpusApprovedKeys)
                keys.Add(key);
            return keys;
        }
    }
}
