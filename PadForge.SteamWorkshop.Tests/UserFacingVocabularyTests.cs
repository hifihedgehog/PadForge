using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PadForge.SteamWorkshop.Model;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;

namespace PadForge.SteamWorkshop.Tests
{
    /// <summary>
    /// <para>Steam's config format is written in SCREAMING_SNAKE and
    /// lower_snake wire tokens ("controller_action", "set_led",
    /// "LEFT_CONTROL", "right_joystick"). None of that vocabulary belongs in
    /// front of a user, and on 2026-07-28 all of it was: the translation
    /// manifest rendered whole raw binding strings in its source column, and
    /// the macro and shift-layer names written into the IMPORTED PROFILE
    /// carried slot and key tokens verbatim, where they persisted into the
    /// macro list and onto the shift-layer flyout.</para>
    /// <para>These guards hold the two halves of that fix. The report side:
    /// every successful translation names the input it resolved, which is
    /// what keeps the manifest off its raw-binding fallback. The vocabulary
    /// side: nothing a user reads is spelled in Steam's grammar.</para>
    /// </summary>
    public class UserFacingVocabularyTests
    {
        /// <summary>A bare Steam grammar token: two or more underscore-joined
        /// runs, all one case. Deliberately does NOT match ordinary prose,
        /// which has spaces, so a friendly string never trips it.</summary>
        private static readonly Regex SteamToken =
            new(@"\b([A-Z][A-Z0-9]*(_[A-Z0-9]+)+|[a-z][a-z0-9]*(_[a-z0-9]+)+)\b",
                RegexOptions.Compiled);

        private static TranslationReport Report(long fileId)
        {
            var config = SteamInputConfig.FromVdf(
                VdfParser.Parse(File.ReadAllText(TestFixtures.Path_(fileId))));
            return new ConfigTranslator()
                .Translate(config, new TranslationOptions { FileId = fileId })
                .Report;
        }

        private static IEnumerable<long> AllIds()
            => TestFixtures.AllVdfPaths()
                .Select(p => long.Parse(Path.GetFileNameWithoutExtension(p)));

        // ── the regex itself works ────────────────────────────────────────

        [Fact]
        public void TokenDetector_MatchesSteamGrammar_AndSparesProse()
        {
            // Positive control. Without these the "no tokens found" assertions
            // below would pass on a detector that matches nothing at all,
            // which is the failure mode that lets a vocabulary regression
            // ship under a green suite.
            Assert.Matches(SteamToken, "controller_action set_led 242 25");
            Assert.Matches(SteamToken, "Hold LEFT_CONTROL macro");
            Assert.Matches(SteamToken, "right_joystick shift");
            Assert.Matches(SteamToken, "Wheel list (left_trackpad)");

            // Negative control: the friendly forms must NOT trip it.
            Assert.DoesNotMatch(SteamToken, "Set light color");
            Assert.DoesNotMatch(SteamToken, "Hold Left Ctrl macro");
            Assert.DoesNotMatch(SteamToken, "Right Stick shift");
            Assert.DoesNotMatch(SteamToken, "Wheel list (Left Trackpad)");
        }

        // ── the report side ───────────────────────────────────────────────

        [Fact]
        public void EverySuccessfulEmit_NamesTheInputItResolved()
        {
            // The browse dialog splits Emitted on " <- " to fill its two
            // columns and falls back to the RAW BINDING when the arrow is
            // absent. That fallback is correct for a skip and wrong for a
            // success, so every success must carry the arrow. Macros were the
            // gap: they reported a bare phrase ("Set LED macro") and so
            // advertised themselves to the user as
            // "controller_action set_led 242 25 0 100 255 1".
            var offenders = new List<string>();
            int successes = 0;
            foreach (long id in AllIds())
            {
                foreach (var e in Report(id).Entries)
                {
                    if (e.Status == TranslationStatus.Skipped) continue;
                    if (string.IsNullOrEmpty(e.Emitted)) continue;
                    // An aggregate entry describes a whole preset and has no
                    // single input to name.
                    if (string.IsNullOrEmpty(e.Binding)) continue;
                    successes++;
                    if (!e.Emitted.Contains(" <- ", StringComparison.Ordinal))
                        offenders.Add($"{id}: {e.SourcePath} => {e.Emitted}");
                }
            }
            Assert.True(successes > 100, $"harness saw only {successes} successes; it stopped measuring");
            Assert.True(offenders.Count == 0,
                "These translated successfully but named no input, so the manifest will show the user "
                + "the raw Steam binding instead:\n  " + string.Join("\n  ", offenders.Take(20)));
        }

        [Fact]
        public void NoReportedEmit_IsWrittenInSteamGrammar()
        {
            var offenders = new List<string>();
            int scanned = 0;
            foreach (long id in AllIds())
            {
                foreach (var e in Report(id).Entries)
                {
                    if (string.IsNullOrEmpty(e.Emitted)) continue;
                    scanned++;
                    // The descriptor half is PadForge's own engine grammar
                    // ("Gamepad LeftStickX"), which carries no underscores.
                    // Only the display half is under test here.
                    int arrow = e.Emitted.IndexOf(" <- ", StringComparison.Ordinal);
                    string shown = arrow >= 0 ? e.Emitted.Substring(0, arrow) : e.Emitted;
                    var m = SteamToken.Match(shown);
                    if (m.Success) offenders.Add($"{id}: \"{shown}\" contains \"{m.Value}\"");
                }
            }
            Assert.True(scanned > 100, $"harness scanned only {scanned} emits; it stopped measuring");
            Assert.True(offenders.Count == 0,
                "Steam wire grammar reached the manifest's target column:\n  "
                + string.Join("\n  ", offenders.Take(20)));
        }

        // ── the saved-profile side ────────────────────────────────────────

        [Fact]
        public void NoMacroOrLayerNameSavedToTheProfile_IsWrittenInSteamGrammar()
        {
            // These strings OUTLIVE the import. They land in the user's
            // profile, show in the macro list, and (for a layer name) on the
            // shift-layer flyout. A token here is not a rendering slip that a
            // later release quietly fixes; it is written to disk.
            var offenders = new List<string>();
            int scanned = 0;
            foreach (long id in AllIds())
            {
                var profile = new ConfigTranslator().Translate(
                    SteamInputConfig.FromVdf(VdfParser.Parse(
                        File.ReadAllText(TestFixtures.Path_(id)))),
                    new TranslationOptions { FileId = id });

                foreach (var name in profile.Macros.Select(m => m.Name))
                {
                    scanned++;
                    var m = SteamToken.Match(name ?? "");
                    if (m.Success) offenders.Add($"{id}: macro \"{name}\" contains \"{m.Value}\"");
                }
                var activators = profile.XboxMappingSet.ShiftActivators
                    .Concat(profile.KbmMappingSet.ShiftActivators);
                foreach (var a in activators)
                {
                    if (string.IsNullOrEmpty(a.LayerName)) continue;
                    scanned++;
                    var m = SteamToken.Match(a.LayerName);
                    if (m.Success)
                        offenders.Add($"{id}: layer name \"{a.LayerName}\" contains \"{m.Value}\"");
                }
            }
            Assert.True(scanned > 100, $"harness scanned only {scanned} names; it stopped measuring");
            Assert.True(offenders.Count == 0,
                "Steam wire grammar was written into the user's saved profile:\n  "
                + string.Join("\n  ", offenders.Take(20)));
        }

        [Fact]
        public void LayerMask_KeepsItsRawToken_WhileLayerNameDoesNot()
        {
            // The counterpart of the rule above, and the reason it is safe.
            // LayerMask is IDENTITY and is matched against saved profile data,
            // so it must keep the exact token it always had. Only LayerName,
            // the display string, was allowed to change.
            var masks = new List<string>();
            var names = new List<string>();
            foreach (long id in AllIds())
            {
                var profile = new ConfigTranslator().Translate(
                    SteamInputConfig.FromVdf(VdfParser.Parse(
                        File.ReadAllText(TestFixtures.Path_(id)))),
                    new TranslationOptions { FileId = id });
                foreach (var a in profile.XboxMappingSet.ShiftActivators
                             .Concat(profile.KbmMappingSet.ShiftActivators))
                {
                    if ((a.LayerMask ?? "").Contains("_MS_", StringComparison.Ordinal))
                    {
                        masks.Add(a.LayerMask);
                        names.Add(a.LayerName ?? "");
                    }
                }
            }
            Assert.True(masks.Count > 0, "no mode-shift activator in the corpus; nothing was measured");
            // The mask still carries the config's own slot token verbatim.
            // Asserted as a substring rather than against SteamToken: the
            // mask is a MIXED-case composite ("Layer_789818086_0_MS_
            // right_trackpad_24") and the display regex deliberately only
            // matches single-case runs, so it does not fire on identity
            // strings at all. That is the correct behaviour for the display
            // rule and useless as a check on the identity, which is why this
            // asserts what it actually cares about.
            Assert.All(masks, m => Assert.Contains("_MS_", m, StringComparison.Ordinal));
            Assert.Contains(masks, m => m.Contains("_trackpad_", StringComparison.Ordinal)
                                        || m.Contains("_joystick_", StringComparison.Ordinal));
            Assert.All(names, n => Assert.DoesNotMatch(SteamToken, n));
        }

        // ── the vocabulary tables ─────────────────────────────────────────

        [Theory]
        [InlineData("LEFT_CONTROL", "Left Ctrl")]
        [InlineData("RIGHT_SHIFT", "Right Shift")]
        [InlineData("KEYPAD_PLUS", "Keypad Plus")]
        [InlineData("KEYPAD_DASH", "Keypad Minus")]
        [InlineData("DOWN_ARROW", "Down Arrow")]
        [InlineData("PAGE_UP", "Page Up")]
        [InlineData("CAPSLOCK", "Caps Lock")]
        [InlineData("BACK_TICK", "Backtick")]
        [InlineData("SPACE", "Space")]
        [InlineData("F5", "F5")]
        [InlineData("A", "A")]
        [InlineData("7", "7")]
        public void KeyDisplayName_SpellsTheToken(string token, string expected)
            => Assert.Equal(expected, SteamInputVkTable.KeyDisplayName(token));

        [Theory]
        [InlineData("left_trackpad", "Left Trackpad")]
        [InlineData("right_trackpad", "Right Trackpad")]
        [InlineData("joystick", "Left Stick")]
        [InlineData("right_joystick", "Right Stick")]
        [InlineData("button_diamond", "Face Buttons")]
        [InlineData("dpad", "D-Pad")]
        [InlineData("gyro", "Gyro")]
        public void SlotDisplayName_NamesTheSlotTheWayTheAppDoes(string token, string expected)
            => Assert.Equal(expected, PhysicalSlotResolver.SlotDisplayName(token));

        // ── one activator per intent ──────────────────────────────────────

        [Fact]
        public void NoInputBothTogglesAndCyclesTheSameLayer()
        {
            // Steam spells a toggleable layer as two halves on one button:
            // add_layer in the set below, remove_layer inside the layer.
            // Lowering each half independently gave that button a Toggle AND
            // a Cycle for the same layer, which fight at runtime and drew
            // the layer twice in the shift-layer strip. RCT3 Weno showed
            // five layers for a config declaring two.
            var offenders = new List<string>();
            int pairs = 0;
            foreach (long id in AllIds())
            {
                var profile = new ConfigTranslator().Translate(
                    SteamInputConfig.FromVdf(VdfParser.Parse(
                        File.ReadAllText(TestFixtures.Path_(id)))),
                    new TranslationOptions { FileId = id });

                foreach (var set in new[] { profile.XboxMappingSet, profile.KbmMappingSet })
                {
                    var byInputLayer = set.ShiftActivators
                        .Where(a => !string.IsNullOrEmpty(a.LayerMask) && !string.IsNullOrEmpty(a.Descriptor))
                        .GroupBy(a => (a.LayerMask, a.Descriptor));
                    foreach (var g in byInputLayer)
                    {
                        pairs++;
                        var modes = g.Select(a => a.Mode).ToList();
                        if (modes.Count > 1)
                            offenders.Add($"{id}: {g.Key.Descriptor} drives {g.Key.LayerMask} "
                                + $"{modes.Count} ways ({string.Join(", ", modes)})");
                    }
                }
            }
            Assert.True(pairs > 20, $"harness saw only {pairs} (input, layer) pairs");
            Assert.True(offenders.Count == 0,
                "One input drives one layer through more than one activator, so the two fight and "
                + "the layer draws more than once:\n  " + string.Join("\n  ", offenders.Take(20)));
        }

        [Fact]
        public void EveryLayerAConfigDeclares_ProducesExactlyOneDistinctMask()
        {
            // The count a user sees in the shift-layer strip is the number
            // of DISTINCT masks, so pin that against the config's own layer
            // count rather than against the activator count, which is
            // legitimately larger when several inputs reach one layer.
            foreach (long id in AllIds())
            {
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(
                    File.ReadAllText(TestFixtures.Path_(id))));
                var profile = new ConfigTranslator().Translate(
                    config, new TranslationOptions { FileId = id });

                // Preset-backed masks only: mode-shift layers are a
                // separate construct with their own _MS_ masks.
                var masks = profile.XboxMappingSet.ShiftActivators
                    .Concat(profile.KbmMappingSet.ShiftActivators)
                    .Select(a => a.LayerMask ?? "")
                    .Where(m => m.StartsWith($"Layer_{id}_", StringComparison.Ordinal)
                                && !m.Contains("_MS_", StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                // Never more distinct preset layers than the config has
                // presets past Base.
                int presetsPastBase = Math.Max(0, config.Presets.Count - 1);
                Assert.True(masks.Count <= presetsPastBase,
                    $"{id}: {masks.Count} distinct preset layers from a config with "
                    + $"{presetsPastBase} presets past Base: {string.Join(", ", masks)}");
            }
        }

        // ── one name per macro ────────────────────────────────────────────

        [Fact]
        public void EveryMacroReport_UsesTheNameTheMacroWillCarryAfterImport()
        {
            // The preview prints the report line on the controller diagram
            // and the macro list prints the macro's Name, so a macro named
            // "Turbo wheel" that reported itself as "Turbo wheel macro
            // (125 ms)" gave the user two vocabularies for one thing. Every
            // macro report must now match a macro that actually exists in
            // the profile it came from.
            var offenders = new List<string>();
            int checked_ = 0;
            foreach (long id in AllIds())
            {
                var profile = new ConfigTranslator().Translate(
                    SteamInputConfig.FromVdf(VdfParser.Parse(
                        File.ReadAllText(TestFixtures.Path_(id)))),
                    new TranslationOptions { FileId = id });

                var labels = new HashSet<string>(
                    profile.Macros.Select(m => ConfigTranslator.MacroLabel(m.Name)),
                    StringComparer.Ordinal);
                if (labels.Count == 0) continue;

                foreach (var e in profile.Report.Entries)
                {
                    if (e.ReasonKey != TranslationReasons.MacroEmitted) continue;
                    if (string.IsNullOrEmpty(e.Emitted)) continue;
                    int arrow = e.Emitted.IndexOf(" <- ", StringComparison.Ordinal);
                    string shown = (arrow >= 0 ? e.Emitted.Substring(0, arrow) : e.Emitted).Trim();
                    checked_++;
                    // A cycle step appends its ordinal ("Wheel list step 2"),
                    // so a prefix match counts.
                    if (!labels.Contains(shown) && !labels.Any(l =>
                            shown.StartsWith(l + " ", StringComparison.Ordinal)))
                        offenders.Add($"{id}: reported \"{shown}\", no macro is named that");
                }
            }
            // The corpus carries 90 macro reports. The floor is a guard on the
            // HARNESS, not on the corpus: it fails if a future change stops
            // these entries being produced and quietly turns the sweep below
            // into a pass over nothing.
            Assert.True(checked_ >= 80, $"harness checked only {checked_} macro reports");
            Assert.True(offenders.Count == 0,
                "A macro reports itself by a name it does not have, so the preview and the "
                + "imported macro list disagree:\n  " + string.Join("\n  ", offenders.Take(20)));
        }

        [Theory]
        [InlineData("Warp cursor (Left Trigger)", "Warp cursor")]
        [InlineData("Turbo wheel (Paddle2)", "Turbo wheel")]
        [InlineData("Click mouse Left (ButtonA)", "Click mouse Left")]
        [InlineData("Wheel list (Left Trackpad)", "Wheel list")]
        // No trailing parenthetical: nothing to strip.
        [InlineData("Warp cursor", "Warp cursor")]
        // A parenthetical that is not trailing survives whole.
        [InlineData("Turbo wheel (125 ms) burst", "Turbo wheel (125 ms) burst")]
        public void MacroLabel_StripsOnlyTheTrailingMemberParenthetical(string name, string expected)
            => Assert.Equal(expected, ConfigTranslator.MacroLabel(name));

        // ── rear buttons ──────────────────────────────────────────────────

        /// <summary>PadForge's paddle numbering follows the SDL button order
        /// it maps onto (SourceCoercion: Paddle1..4 = buttons 12..15 =
        /// RPaddle1 / LPaddle1 / RPaddle2 / LPaddle2), and SDL_gamepad.h
        /// names those Steam Controller buttons outright: PADDLE1 is the
        /// UPPER pair (L4 / R4), PADDLE2 the LOWER (L5 / R5). So
        /// Paddle1=R4, Paddle2=L4, Paddle3=R5, Paddle4=L5, which is where
        /// the generated Deck overlay draws them.</summary>
        [Theory]
        // Four rear buttons (Deck, SC 2026): the upper pair carries the
        // _upper token, so the plain token is the LOWER pair.
        [InlineData("button_back_left_upper", true, "Gamepad Paddle2")]   // L4
        [InlineData("button_back_right_upper", true, "Gamepad Paddle1")]  // R4
        [InlineData("button_back_left", true, "Gamepad Paddle4")]         // L5
        [InlineData("button_back_right", true, "Gamepad Paddle3")]        // R5
        // Two rear buttons: the plain token is the only pair, so primary.
        [InlineData("button_back_left", false, "Gamepad Paddle2")]
        [InlineData("button_back_right", false, "Gamepad Paddle1")]
        public void RearButton_ResolvesToThePaddleTheUserActuallyPresses(
            string token, bool fourRear, string expected)
        {
            var src = PhysicalSlotResolver.Resolve(
                SteamSlot.Switch, token, nintendoLabels: false,
                singlePadTrackpads: false, fourRearButtons: fourRear);
            Assert.Equal(expected, src?.Descriptor);
        }

        [Fact]
        public void FourRearButtons_IsTrueForExactlyTheTypesTheCorpusProves()
        {
            // The _upper tokens appear on these two types and on no other
            // across the committed fixtures. A type wrongly marked here
            // silently moves every plain rear binding to the other paddle.
            Assert.True(PhysicalSlotResolver.UsesFourRearButtons("controller_neptune"));
            Assert.True(PhysicalSlotResolver.UsesFourRearButtons("controller_triton"));
            foreach (var two in new[]
                     {
                         "controller_steamcontroller_gordon", "controller_xboxone",
                         "controller_xbox360", "controller_ps4", "controller_ps5",
                         "controller_switch_pro", "controller_switch_joycon_pair", "",
                     })
                Assert.False(PhysicalSlotResolver.UsesFourRearButtons(two), two);
        }

        [Fact]
        public void EveryFixtureUsingUpperTokens_IsAFourRearButtonType()
        {
            // Footprint closure against the corpus rather than against the
            // table above, so a fixture added later with _upper members on a
            // type this build calls two-paddle fails here instead of quietly
            // importing onto the wrong buttons.
            foreach (var path in TestFixtures.AllVdfPaths())
            {
                string text = File.ReadAllText(path);
                if (!text.Contains("button_back_left_upper", StringComparison.Ordinal)
                    && !text.Contains("button_back_right_upper", StringComparison.Ordinal))
                    continue;
                var config = SteamInputConfig.FromVdf(VdfParser.Parse(text));
                Assert.True(PhysicalSlotResolver.UsesFourRearButtons(config.ControllerType),
                    $"{Path.GetFileName(path)} binds an _upper rear button but its type "
                    + $"'{config.ControllerType}' is not marked as having four of them");
            }
        }

        [Fact]
        public void SlotDisplayName_PassesAnUnknownTokenThrough()
        {
            // Guessing at a token this build has never seen would be worse
            // than showing it. A future Steam slot must not silently render
            // as some other slot's name.
            Assert.Equal("some_future_slot",
                PhysicalSlotResolver.SlotDisplayName("some_future_slot"));
        }

        [Fact]
        public void SlotDisplayName_AgreesWithParseSlot_OverTheWholeEnum()
        {
            // Footprint closure: every SteamSlot the parser can produce has a
            // name, so adding a slot without naming it fails here rather than
            // reaching a user as "Input".
            foreach (SteamSlot slot in Enum.GetValues<SteamSlot>())
            {
                if (slot == SteamSlot.Unknown) continue;
                string name = PhysicalSlotResolver.SlotDisplayName(slot);
                Assert.False(string.IsNullOrWhiteSpace(name), $"{slot} has no display name");
                Assert.DoesNotMatch(SteamToken, name);
                Assert.NotEqual("Input", name);
            }
        }
    }
}
