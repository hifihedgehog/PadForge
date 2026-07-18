using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>THE LOCKDOWN GUARD (v23). Translates EVERY corpus fixture
    /// and asserts the complete multiset of report reason keys equals the
    /// approved list below, so no lowering can silently regress into a
    /// note and no new note class can ship unreviewed: the owner reading a
    /// fresh note in an imported profile before the translator's authors
    /// do is the exact failure this pins out. Any change to the multiset
    /// (a count moving, a key appearing, a key vanishing) fails the build
    /// until the approved list is deliberately re-blessed alongside the
    /// goldens.
    ///
    /// <para>Admission standard (the v23 re-audit, the standard that
    /// caught gyro_button and then the touch-hosted layer activators):
    /// every key is a Clean emission record, or carries one of three
    /// justifications spelled out on its entry. Steam-session/client
    /// means only the game's own Steam session can deliver it.
    /// Config-error means the authored config itself gives the note
    /// nothing to lower. Impossibility-proof-in-code means the cited
    /// code names the missing engine primitive or the non-commuting
    /// math. A drop that is buildable with existing channels may not
    /// enter this list. It gets built instead.</para></summary>
    public class ReportReasonLockdownTests
    {
        /// <summary>The approved corpus-wide reason-key multiset,
        /// "key=count" per entry, ordinal-sorted, one justification per
        /// key. Safety-net keys with zero corpus occurrences (for
        /// example ActivatorInputNotSupported, since v23 only the
        /// double-press layer-verb net) sit outside the multiset by
        /// construction.</summary>
        private static readonly string[] ApprovedMultiset =
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

        [Fact]
        public void Corpus_ReportReasonMultiset_MatchesTheApprovedList()
        {
            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                var translated = new ConfigTranslator().Translate(config, new TranslationOptions
                {
                    FileId = long.Parse(Path.GetFileNameWithoutExtension(path)),
                });
                foreach (var e in translated.Report.Entries)
                {
                    string key = string.IsNullOrEmpty(e.ReasonKey) ? "(empty)" : e.ReasonKey;
                    counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
                }
            }

            var actual = new StringBuilder();
            foreach (var kv in counts)
                actual.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');

            Assert.Equal(string.Join('\n', ApprovedMultiset), actual.ToString().TrimEnd('\n'));
        }
    }
}
