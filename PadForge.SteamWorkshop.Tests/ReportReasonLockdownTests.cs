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
    /// enter this list. It gets built instead.</para>
    ///
    /// <para>The approved multiset itself lives in
    /// <see cref="ApprovedReasonLockdown"/>, the single source of truth
    /// shared with the wild-corpus sweep tool
    /// (tools/SteamWorkshopSweep), which links that file and flags every
    /// live-harvested report line outside the approved key set.</para></summary>
    public class ReportReasonLockdownTests
    {

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

            // The bless flag writes the ACTUAL multiset to a scratch file
            // for the deliberate re-bless workflow (the approved list is
            // shared source with per-key justifications, so it is always
            // updated BY HAND against this dump, never overwritten).
            if (Environment.GetEnvironmentVariable("PADFORGE_BLESS_GOLDEN") == "1")
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "padforge-corpus-multiset.txt"),
                    actual.ToString());
            }

            Assert.Equal(string.Join('\n', ApprovedReasonLockdown.CorpusMultiset), actual.ToString().TrimEnd('\n'));
        }

        /// <summary>The zero-corpus approved list (v25) exists precisely
        /// for keys the curated corpus does not exercise. A key appearing
        /// in BOTH lists means a curated fixture started emitting it: the
        /// multiset test above already fails on the count, and this pins
        /// the bookkeeping so the key is MOVED (with its proof comment)
        /// rather than double-listed.</summary>
        [Fact]
        public void ZeroCorpusApprovedKeys_AreDisjointFromTheMultiset()
        {
            var multisetKeys = ApprovedReasonLockdown.CorpusMultiset
                .Select(e => e.Substring(0, e.LastIndexOf('=')))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var key in ApprovedReasonLockdown.ZeroCorpusApprovedKeys)
                Assert.DoesNotContain(key, multisetKeys);
        }
    }
}
