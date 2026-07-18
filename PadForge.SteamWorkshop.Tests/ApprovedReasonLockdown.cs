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
            // Impossibility proof in code: the analog pair read has no
            // per-source companion-axis channel and the outer radius
            // applies per axis, so radial geometry cannot lower yet.
            // Proof at ConfigTranslator.ReportRadialDeadZoneResidual
            // (v19 T2). Retires when the pair-read channel lands.
            "Workshop_Tr_DeadZoneRadialResidual=5",
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
            "Workshop_Tr_RemoveLayerApproximated=12",
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

        /// <summary>The approved reason keys, derived from
        /// <see cref="CorpusMultiset"/> by stripping the "=count" tail.
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
            return keys;
        }
    }
}
