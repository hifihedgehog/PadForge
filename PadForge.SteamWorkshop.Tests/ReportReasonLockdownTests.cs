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
    /// <summary>THE LOCKDOWN GUARD (v22). Translates EVERY corpus fixture
    /// and asserts the complete multiset of report reason keys equals the
    /// approved list below, so no lowering can silently regress into a
    /// note and no new note class can ship unreviewed: the owner reading a
    /// fresh note in an imported profile before the translator's authors
    /// do is the exact failure this pins out. Any change to the multiset
    /// (a count moving, a key appearing, a key vanishing) fails the build
    /// until the approved list is deliberately re-blessed alongside the
    /// goldens.</summary>
    public class ReportReasonLockdownTests
    {
        /// <summary>The approved corpus-wide reason-key multiset,
        /// "key=count" per line, ordinal-sorted. Beside the Clean
        /// emission keys (RowEmitted / MacroEmitted / MenuEmitted /
        /// ShiftLayerEmitted / ToggleLatchEmitted), three classes
        /// survive at v22:
        /// Steam-session/client class: GameActionsNotSupported (in-game
        /// Steam Input API actions only Steam can deliver).
        /// Config-error class: PresetHasNoActivator, ShiftLayerEmpty
        /// (rowless layers whose bindings' own entries say why), and
        /// ActivatorInputNotSupported (the double-press layer-verb
        /// safety net).
        /// Named-approximation class, each with its impossibility proof
        /// at the emission site: DeadZoneRadialResidual,
        /// GyroButtonMaskDropped (ungrounded gyro_button indices only;
        /// every corpus ratchet mask lowers),
        /// LayerReleaseEdgeApproximated, MouseModeTuningDropped,
        /// RemoveLayerApproximated, RotationNonlinearWithheld,
        /// ScrollWheelApproximated.</summary>
        private const string ApprovedMultiset = @"
Workshop_Tr_ActivatorInputNotSupported=2
Workshop_Tr_DeadZoneRadialResidual=5
Workshop_Tr_GameActionsNotSupported=8
Workshop_Tr_GyroButtonMaskDropped=5
Workshop_Tr_LayerReleaseEdgeApproximated=7
Workshop_Tr_MacroEmitted=90
Workshop_Tr_MenuEmitted=21
Workshop_Tr_MouseModeTuningDropped=3
Workshop_Tr_PresetHasNoActivator=5
Workshop_Tr_RemoveLayerApproximated=12
Workshop_Tr_RotationNonlinearWithheld=1
Workshop_Tr_RowEmitted=987
Workshop_Tr_ScrollWheelApproximated=5
Workshop_Tr_ShiftLayerEmitted=41
Workshop_Tr_ShiftLayerEmpty=4
Workshop_Tr_ToggleLatchEmitted=1
";

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

            Assert.Equal(Normalize(ApprovedMultiset), actual.ToString().TrimEnd('\n'));
        }

        private static string Normalize(string s)
            => string.Join('\n', s.Replace("\r\n", "\n").Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
