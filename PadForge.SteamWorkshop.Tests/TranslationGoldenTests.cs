using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// Golden-file snapshots: every committed fixture VDF translates (default
    /// options + its file id) to a committed, hand-reviewed snapshot under
    /// <c>Golden/</c>. A diff here is a deliberate translator change. Review
    /// it, then regenerate by running the suite once with the environment
    /// variable <c>PADFORGE_BLESS_GOLDEN=1</c> (writes into the source tree)
    /// and commit the result.
    /// </summary>
    public class TranslationGoldenTests
    {
        private static readonly long[] AllFixtureIds =
        {
            708227783, 770509247, 789818086, 793611331, 875948877,
            930657498, 1129670518, 1150803559, 1172518660, 1223976670,
            1370740828, 1451857916, 1723403062, 1957995349, 2220285578,
            2374887917, 2494749393, 2774979654, 2790927974, 2795727040,
            2853328208, 2858159083, 2948704083, 3353173512, 3353604014,
            3354224367, 3443409487, 3451446931, 3456927474, 3725174032,
        };

        public static IEnumerable<object[]> FixtureIds()
            => AllFixtureIds.Select(id => new object[] { id });

        [Fact]
        public void FixtureListMatchesDirectory()
        {
            var onDisk = TestFixtures.AllVdfPaths()
                .Select(p => long.Parse(Path.GetFileNameWithoutExtension(p)))
                .OrderBy(id => id)
                .ToArray();
            Assert.Equal(AllFixtureIds.OrderBy(id => id).ToArray(), onDisk);
        }

        [Theory]
        [MemberData(nameof(FixtureIds))]
        public void Fixture_MatchesGolden(long fileId)
        {
            string actual = Translate(fileId);

            if (Environment.GetEnvironmentVariable("PADFORGE_BLESS_GOLDEN") == "1")
            {
                Directory.CreateDirectory(SourceGoldenDir);
                File.WriteAllText(Path.Combine(SourceGoldenDir, fileId + ".golden.txt"), actual);
                return;
            }

            string goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", fileId + ".golden.txt");
            Assert.True(File.Exists(goldenPath),
                $"Missing golden snapshot {goldenPath}. Run once with PADFORGE_BLESS_GOLDEN=1, review, commit.");
            string expected = Normalize(File.ReadAllText(goldenPath));
            Assert.Equal(expected, Normalize(actual));
        }

        [Theory]
        [MemberData(nameof(FixtureIds))]
        public void Fixture_TranslationIsDeterministic(long fileId)
        {
            Assert.Equal(Translate(fileId), Translate(fileId));
        }

        /// <summary>v14/v15/v16/v17 arm closures: the retired vocabulary has
        /// zero emission sites across the whole corpus.
        /// TrackpadFeatureRequired died to the apply-time auto-arm (v14),
        /// the directional-swipe skip family died to the gyro half reads,
        /// the member walk, and the AxisHold / MouseWheelTap /
        /// half-stamped-activator channels (v15), the two macro-trigger
        /// plumbing notes went silent Clean (v15), the terminal round
        /// built the mouse_delta nudge and the scroll_wheel_list cycle
        /// while the census retired the surfaceless-scrollwheel arm (v16),
        /// and the last two gaps built in v17: Double_Press lowers to the
        /// engine's DoublePress macro trigger on every host, and edge
        /// members build on the stick-ring family / the trackpad touch
        /// read. The expected-behavior notes retired with v17 too:
        /// SCREENSHOT and SHOW_KEYBOARD keep their macros but report
        /// nothing, Soft_Press rows report Clean (a soft press IS a
        /// press threshold), and set_led restore-default reports Clean
        /// (clearing the override IS the restore), because those notes
        /// described exactly what a user expects. None of these keys may
        /// ever appear in a fresh report again.</summary>
        [Theory]
        [MemberData(nameof(FixtureIds))]
        public void Fixture_EmitsNoRetiredReason(long fileId)
        {
            var root = VdfParser.Parse(TestFixtures.Read(fileId));
            var config = SteamInputConfig.FromVdf(root);
            var translated = new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
            });
            var retired = new[]
            {
                "Workshop_Tr_TrackpadFeatureRequired",
                "Workshop_Tr_ScrollGestureModeNotSupported",
                "Workshop_Tr_GyroSwipeNotSupported",
                "Workshop_Tr_SwipeSurfaceNotSupported",
                "Workshop_Tr_FlickAxisTargetNotSupported",
                "Workshop_Tr_FlickBindingNotOneShot",
                "Workshop_Tr_MacroTriggerViaXboxOutput",
                "Workshop_Tr_MacroTriggerRetargetedToInput",
                "Workshop_Tr_MouseDeltaNotSupported",
                "Workshop_Tr_ScrollWheelModeNotSupported",
                "Workshop_Tr_DoublePressNotSupported",
                "Workshop_Tr_EdgeInputNotSupported",
                "Workshop_Tr_ScreenshotApproximated",
                "Workshop_Tr_ShowKeyboardApproximated",
                "Workshop_Tr_SoftPressApproximated",
                "Workshop_Tr_SetLedDefaultApproximated",
            };
            Assert.DoesNotContain(translated.Report.Entries, e =>
                System.Array.IndexOf(retired, e.ReasonKey) >= 0);
        }

        /// <summary>v16 retired-arm census guard: the surfaceless-
        /// scrollwheel skip arm was deleted on the proof that no
        /// scrollwheel group in Steam's grammar hosts anywhere but a
        /// trackpad or joystick. This walks EVERY group_source_bindings
        /// entry of every fixture (active, inactive, and modeshift alike,
        /// wider than translation reaches) and fails the moment a
        /// scrollwheel group binds a non-drag host, which would demand
        /// the per-press detent lowering the arm's deletion note names.</summary>
        [Fact]
        public void ScrollWheelGroups_Corpus_HostOnlyOnDragSurfaces()
        {
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                var wheelGroups = config.Groups
                    .Where(g => string.Equals(g.Mode, "scrollwheel", StringComparison.OrdinalIgnoreCase))
                    .Select(g => g.Id)
                    .ToHashSet();
                if (wheelGroups.Count == 0) continue;
                foreach (var preset in config.Presets)
                {
                    foreach (var kv in preset.GroupSourceBindings)
                    {
                        if (!wheelGroups.Contains(kv.Key)) continue;
                        var tokens = (kv.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var slot = PhysicalSlotResolver.ParseSlot(tokens.Length > 0 ? tokens[0] : "");
                        Assert.True(
                            PhysicalSlotResolver.IsTrackpad(slot) || PhysicalSlotResolver.IsStick(slot),
                            $"{Path.GetFileName(path)}: scrollwheel group {kv.Key} hosts on '{kv.Value}'");
                    }
                }
            }
        }

        /// <summary>v17 retired-arm census guard for EdgeInputNotSupported:
        /// the key was deleted on the proof that Steam's grammar hosts edge
        /// members only where a ring-capable read exists (trigger pulls,
        /// trackpads, sticks: the 2026-07-17 census over the corpus plus
        /// Valve's 54 shipped controller_base templates found no other
        /// host). This walks every group carrying an "edge" input across
        /// every fixture, wider than translation reaches, and fails the
        /// moment one binds a host outside that set, which would demand a
        /// new ring surface instead of the UnknownPhysicalInput net.</summary>
        [Fact]
        public void EdgeMembers_Corpus_HostOnlyOnRingCapableSurfaces()
        {
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                var edgeGroups = config.Groups
                    .Where(g => g.Inputs.Keys.Any(k =>
                        string.Equals(k, "edge", StringComparison.OrdinalIgnoreCase)))
                    .Select(g => g.Id)
                    .ToHashSet();
                if (edgeGroups.Count == 0) continue;
                foreach (var preset in config.Presets)
                {
                    foreach (var kv in preset.GroupSourceBindings)
                    {
                        if (!edgeGroups.Contains(kv.Key)) continue;
                        var tokens = (kv.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var slot = PhysicalSlotResolver.ParseSlot(tokens.Length > 0 ? tokens[0] : "");
                        Assert.True(
                            PhysicalSlotResolver.IsTrackpad(slot)
                            || PhysicalSlotResolver.IsStick(slot)
                            || slot == SteamSlot.LeftTrigger || slot == SteamSlot.RightTrigger,
                            $"{Path.GetFileName(path)}: edge group {kv.Key} hosts on '{kv.Value}'");
                    }
                }
            }
        }

        /// <summary>v17 retired-arm census guard for
        /// DoublePressNotSupported: the key was deleted when the
        /// double-press macro trigger built. The two arms with no
        /// construct (layer verbs, mode shifts) route to the
        /// ActivatorInputNotSupported net, on the census proof that no
        /// Double_Press activator in the corpus (nor in Valve's 54
        /// shipped templates, checked 2026-07-17) carries one. This walk
        /// pins the committed half of that census: every Double_Press
        /// binding stays inside the built vocabulary.</summary>
        [Fact]
        public void DoublePressActivators_Corpus_CarryOnlyBuiltVocabulary()
        {
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(File.ReadAllText(path)));
                foreach (var group in config.Groups)
                {
                    foreach (var input in group.Inputs.Values)
                    {
                        foreach (var act in input.Activators)
                        {
                            if (!string.Equals((act.Type ?? "").Trim(), "Double_Press",
                                    StringComparison.OrdinalIgnoreCase))
                                continue;
                            foreach (var b in act.Bindings)
                            {
                                string t = (b.Type ?? "").Trim().ToLowerInvariant();
                                Assert.True(
                                    t is "key_press" or "mouse_button" or "mouse_wheel"
                                      or "xinput_button" or "game_action" or ""
                                    || (t == "controller_action"
                                        && !b.Param.TrimStart().StartsWith("add_layer", StringComparison.OrdinalIgnoreCase)
                                        && !b.Param.TrimStart().StartsWith("hold_layer", StringComparison.OrdinalIgnoreCase)
                                        && !b.Param.TrimStart().StartsWith("remove_layer", StringComparison.OrdinalIgnoreCase)
                                        && !b.Param.TrimStart().StartsWith("change_preset", StringComparison.OrdinalIgnoreCase)),
                                    $"{Path.GetFileName(path)}: Double_Press hosts '{b.Raw}'");
                            }
                        }
                    }
                }
            }
        }

        private static string Translate(long fileId)
        {
            var root = VdfParser.Parse(TestFixtures.Read(fileId));
            var config = SteamInputConfig.FromVdf(root);
            var translated = new ConfigTranslator().Translate(config, new TranslationOptions
            {
                FileId = fileId,
            });
            return GoldenProjection.Render(translated);
        }

        private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

        /// <summary>Source-tree Golden directory (bless target): the test
        /// assembly runs from bin/{cfg}/{tfm}, three levels below the project.</summary>
        private static string SourceGoldenDir
            => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden"));
    }
}
