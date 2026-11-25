using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PadForge.Resources.Strings;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A count reads right for one. The Clone Device 1:1 confirm said "The
    /// layout becomes 1 axes, 1 buttons, and 1 POVs", and about two dozen
    /// other lines put a number before a plural noun with no words for one:
    /// "Exported 1 names.", "Giving it back in 1 seconds", "Cycle tap list
    /// (1 steps)", "1 lights in #FF0000 while held", "1 votes". Each string
    /// that counts now has a second string for a count of one, paired in
    /// <see cref="Strings.OneForms"/>, and code takes its words from
    /// <see cref="Strings.Counted"/> or <see cref="Strings.CountFormat(string, long)"/>.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class CountWordsTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static Dictionary<string, string> English() =>
            XDocument.Load(Path.Combine(RepoRoot(), "PadForge.App", "Resources", "Strings", "Strings.resx"))
                .Root.Elements("data")
                .ToDictionary(d => (string)d.Attribute("name"), d => d.Element("value")?.Value ?? "");

        // A place for a value, then a word that ends in s, with at most one
        // word between them: "{0} buttons", "{0} key events", "{1} bound cells".
        private static readonly Regex CountBeforeAPlural =
            new(@"\{(\d+)(?::[^}]*)?\}\s+(?:[A-Za-z-]+\s+)?([A-Za-z-]+s)\b");

        // Words that follow a name, never a count: "{0} is", "{0} needs SteamVR".
        private static readonly HashSet<string> FollowsAName = new(StringComparer.OrdinalIgnoreCase)
        {
            "is", "as", "was", "has", "does", "its", "this", "ms", "needs", "reads", "sets", "reaches",
            "exists", "goes", "sends", "shows", "peaks", "frees", "references", "switches", "keeps", "wants", "asks",
        };

        // Strings whose value before a plural is never one, with the reason.
        private static readonly Dictionary<string, string> NeverOne = new(StringComparer.Ordinal)
        {
            ["AddController_Full_Format"] = "{0} is the most controllers PadForge runs (InputManager.MaxPads)",
            ["Automation_Asked_Repeated_Format"] = "{1} is a run of two or more of the same ask (AutomationAsk.Text writes it only past one)",
            ["Settings_PollingRateTooltip_Format"] = "{0} is how often Windows wakes the engine, 64 times a second or more",
            ["Devices_DriverKeepNote_Format"] = "{0} is MainWindow.KeepDriverSeconds",
            ["Devices_DriverKeepNote_Mouse_Format"] = "{0} is MainWindow.KeepDriverSeconds",
            ["Devices_TouchpadKeepNote_Format"] = "{0} is MainWindow.KeepDriverSeconds",
            ["ReleaseTest_Chart_Name_Format"] = "{0} is ReleaseTest.ReleaseMs in seconds",
            ["TwitchChat_Problem_TooLong_Format"] = "{0} is ChatCommandRegistry.MaxWordLength",
            ["TwitchChat_Problem_Full_Format"] = "{0} is ChatCommandRegistry.MaxWords",
            ["Menu_Keyboard_Controls_Summary_Format"] = "{1} is the layout's own controls, several in every layout",
            ["Menu_Keyboard_Threshold_Name_Format"] = "{0} is a control's name, and Axis is part of the setting's name",
            ["Status_TouchpadTakeFailed_Format"] = "{0} is a device's name, and Windows is Windows",
            ["Pad_Lighting_RouteShared"] = "{0} is a lighting program's name, and lights is the verb",
            ["Pad_Lighting_RouteSharedVendorRow"] = "{0} is a lighting program's name, and lights is the verb",
            ["Pad_Lighting_RouteOtherController"] = "{0} is a controller's number, and lights is the verb",
            ["Pad_Lighting_KeyColors_Others_BaseLayer_Format"] = "{0} is the base layer's name",
            ["MacroAction_ShowKeyboardScreen_Format"] = "{0} is the screen's name",
            ["MacroAction_ShowKeyboardScreen_Hold_Format"] = "{0} is the screen's name",
            ["Import_ExtrasMacro_Format"] = "{0} is an input's label, as in its siblings {0} lightbar and {0} range",
            ["ControllerDatabase_BuiltIn_Format"] = "{0} is ControllerDatabaseStatus.BuiltInCount: SDL's own Windows rows and the community's",
            ["ControllerDatabase_BuiltInUpdated_Format"] = "{0} is ControllerDatabaseStatus.BuiltInCount, and {3} and {4} stand before no noun",
            ["Main_DevicesDisconnectedNamed_Format"] = "{0} is two or more: a note for one device names it (DeviceNoteDirector.Text)",
            ["Main_DevicesConnectedNamed_Format"] = "{0} is two or more: a note for one device names it (DeviceNoteDirector.Text)",
            ["ExeFolder_TargetTaken_Format"] = "{0} is a folder's path, and holds is the verb",
            ["Plugins_Device_Settings_Format"] = "{0} is a plugin's name, and Settings is the dialog's title",
        };

        /// <summary>Every English string that puts a count before a plural
        /// noun has its words for one, or is listed with the reason its count
        /// is never one. The reasons that name a constant are checked against
        /// it.</summary>
        [Fact]
        public void EveryCountBeforeAPluralNoun_HasItsWordsForOne()
        {
            var english = English();
            Assert.True(english.Count > 5000, $"only {english.Count} English strings were read");

            // The words for one are checked by the pair's own test below.
            var oneForms = Strings.OneForms.Values.ToHashSet(StringComparer.Ordinal);
            var hits = new List<string>();
            foreach (var (key, value) in english)
            {
                if (oneForms.Contains(key)) continue;
                bool counts = CountBeforeAPlural.Matches(value).Any(m => !FollowsAName.Contains(m.Groups[2].Value));
                if (counts) hits.Add(key);
            }
            // Positive control: the scan sees the pairs that were right before.
            Assert.Contains("Devices_Caps_Axes_Format", hits);
            Assert.Contains("Pad_Band_Count_Format", hits);

            var unpaired = hits.Where(k => !Strings.OneForms.ContainsKey(k) && !NeverOne.ContainsKey(k))
                .Select(k => k + " = " + english[k]).ToList();
            Assert.True(unpaired.Count == 0,
                "these strings put a count before a plural noun and have no words for one:\n" + string.Join("\n", unpaired));

            // Each reason still holds: its string still counts, and its
            // constant is more than one.
            foreach (var key in NeverOne.Keys)
                Assert.True(hits.Contains(key), key + " is listed as never one but no longer counts, so take it off the list");
            int[] neverOne =
            {
                PadForge.Common.Input.InputManager.MaxPads,
                PadForge.MainWindow.KeepDriverSeconds,
                PadForge.Common.Input.Twitch.ChatCommandRegistry.MaxWordLength,
                PadForge.Common.Input.Twitch.ChatCommandRegistry.MaxWords,
                PadForge.Common.Input.ControllerDatabaseStatus.BuiltInCount(
                    PadForge.Common.Input.ControllerDatabaseBaseline.Embedded, PadForge.Common.Input.ControllerDatabaseUse.OwnRows),
            };
            Assert.All(neverOne, n => Assert.True(n > 1, n + " is one"));
            int releaseMs = PadForge.Engine.Steering.ReleaseTest.ReleaseMs;
            Assert.NotEqual(1000, releaseMs);
            foreach (PadForge.Engine.Menus.KeyboardLayout layout in Enum.GetValues(typeof(PadForge.Engine.Menus.KeyboardLayout)))
                Assert.True(PadForge.Engine.Menus.KeyboardMenuDefaults.Actions(layout).Count > 1, layout + " has one control");
            // A device note for one device is its name and no count.
            foreach (var kind in new[] { PadForge.Services.DeviceNoteKind.Disconnected, PadForge.Services.DeviceNoteKind.Connected })
            {
                var forOne = new PadForge.Services.DeviceNote(kind, 1, Guid.NewGuid(), -1);
                string line = PadForge.Services.DeviceNoteDirector.Text(forOne, "Pad", null, -1);
                Assert.Contains("Pad", line, StringComparison.Ordinal);
                Assert.DoesNotContain("1", line, StringComparison.Ordinal);
            }
        }

        /// <summary>Both strings of a pair exist, they differ, and the words
        /// for one take no value the other string lacks, so one set of values
        /// fills either.</summary>
        [Fact]
        public void EveryPair_HasBothStrings_AndTheWordsForOneTakeNoNewValue()
        {
            var english = English();
            Assert.NotEmpty(Strings.OneForms);
            var place = new Regex(@"\{(\d+)(?::[^}]*)?\}");
            // The value that is the count: {0}, except in these two.
            var countPlace = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [nameof(Strings.Recorder_Samples_Format)] = 1,
                [nameof(Strings.FlydigiScreen_Cut_Format)] = 1,
            };
            foreach (var (many, one) in Strings.OneForms)
            {
                Assert.True(english.ContainsKey(many), many + " is not in Strings.resx");
                Assert.True(english.ContainsKey(one), one + " is not in Strings.resx");
                Assert.NotEqual(english[many], english[one]);
                var manyPlaces = place.Matches(english[many]).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                var onePlaces = place.Matches(english[one]).Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                Assert.True(onePlaces.IsSubsetOf(manyPlaces), one + " takes a value " + many + " does not");
                int counted = countPlace.TryGetValue(many, out int at) ? at : 0;
                Assert.False(CountBeforeAPlural.Matches(english[one]).Any(m => !FollowsAName.Contains(m.Groups[2].Value)
                        && int.Parse(m.Groups[1].Value) == counted),
                    one + " still puts its count before a plural");
            }
            // The countdown draws its number in a run of its own, split at {0}.
            Assert.Contains("{0}", english[Strings.OneForms[nameof(Strings.Devices_DriverKeepCountdown_Format)]]);
        }

        /// <summary>No code formats a string that counts by itself: it asks
        /// Strings.Counted or Strings.CountFormat, which pick the words for
        /// one. A XAML binding to one goes through OneOrManyFormatConverter
        /// beside the words for one. The two report lists take a reason's
        /// words the same way.</summary>
        [Fact]
        public void NoCodeFormatsACountingString_ByItself()
        {
            Assert.NotEmpty(Strings.OneForms);
            var app = Path.Combine(RepoRoot(), "PadForge.App");
            var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
                .Where(p => (p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".xaml", StringComparison.Ordinal))
                    && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                    && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                    && !p.EndsWith("Strings.Designer.cs", StringComparison.Ordinal))
                .ToList();
            Assert.True(files.Count > 300, $"only {files.Count} files were read");

            var wrong = new List<string>();
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                foreach (var (many, one) in Strings.OneForms)
                {
                    foreach (Match m in Regex.Matches(text, @"\b" + many + @"\b"))
                    {
                        string before = text.Substring(Math.Max(0, m.Index - 400), Math.Min(400, m.Index));
                        bool named = before.EndsWith("nameof(Strings.", StringComparison.Ordinal);
                        bool converted = file.EndsWith(".xaml", StringComparison.Ordinal)
                            && before.Contains("OneOrManyFormatConverter", StringComparison.Ordinal)
                            && before.Contains(one, StringComparison.Ordinal);
                        if (!named && !converted)
                            wrong.Add(Path.GetFileName(file) + ": " + many);
                    }
                }
            }
            Assert.True(wrong.Count == 0, "these places format a counting string by itself:\n" + string.Join("\n", wrong));

            foreach (var view in new[] { "CommunityConfigsPanel.xaml.cs", "ImportConfigDialog.xaml.cs" })
            {
                string text = File.ReadAllText(Path.Combine(app, "Views", view));
                Assert.DoesNotContain("Strings.Get(entry.ReasonKey)", text);
                Assert.Contains("Strings.CountFormat(entry.ReasonKey", text);
            }
        }

        // ── What a user reads ───────────────────────────────────────────

        [Fact]
        public void TheCloneConfirm_CountsEachKind_InItsOwnWords()
        {
            Assert.Equal("Clone Wii Remote onto this slot as a 1:1 passthrough? The layout becomes 1 axis, 1 button, "
                + "and 1 POV, and this device’s rows on the slot are replaced with its own inputs.",
                PadPage.CloneConfirmText("Wii Remote", 1, 1, 1));
            Assert.Equal("Clone Pad onto this slot as a 1:1 passthrough? The layout becomes 6 axes, 12 buttons, "
                + "and 0 POVs, and this device’s rows on the slot are replaced with its own inputs.",
                PadPage.CloneConfirmText("Pad", 6, 12, 0));
            Assert.Equal("This device has more inputs than an Extended controller can carry. Mapped 1 of 1 axis, "
                + "128 of 200 buttons, and 0 of 0 POVs. The rest were left unmapped.",
                PadPage.CloneOverflowText(1, 1, 128, 200, 0, 0));
        }

        private static MacroAction Step(MacroActionType type) => new() { Type = type };

        [Fact]
        public void AMacroStepsLine_SaysOne_ForOne()
        {
            var tap = Step(MacroActionType.CycleTapList);
            tap.CycleStepsCsv = "W:1";
            Assert.Equal("Cycle tap list (1 step)", tap.DisplayText);
            tap.CycleStepsCsv = "W:1,W:2";
            Assert.Equal("Cycle tap list (2 steps)", tap.DisplayText);

            var lightbar = Step(MacroActionType.LightbarModeCycle);
            lightbar.LightbarCycleModesCsv = "1";
            Assert.Equal("Cycle through 1 lightbar mode", lightbar.DisplayText);
            lightbar.LightbarCycleModesCsv = "1,2";
            Assert.Equal("Cycle through 2 lightbar modes", lightbar.DisplayText);

            var pointer = Step(MacroActionType.PointerModeCycle);
            pointer.PointerCycleModesCsv = "Mouse";
            Assert.Equal("Cycle through 1 pointer mode", pointer.DisplayText);
            pointer.PointerCycleModesCsv = "Mouse,FpsMouse";
            Assert.Equal("Cycle through 2 pointer modes", pointer.DisplayText);

            var wheel = Step(MacroActionType.ToggleWheel);
            wheel.AxisValue = 1;
            Assert.Equal("Toggle wheel scroll (1 tick every 100 ms)", wheel.DisplayText);
            wheel.AxisValue = -1;
            Assert.Equal("Toggle wheel scroll (-1 tick every 100 ms)", wheel.DisplayText);
            wheel.AxisValue = 3;
            Assert.Equal("Toggle wheel scroll (3 ticks every 100 ms)", wheel.DisplayText);

            var clear = Step(MacroActionType.ClearKeyColors);
            clear.KeyColorTargets = "k1E";
            Assert.Equal("Clear key colors on 1 light", clear.DisplayText);
            clear.KeyColorTargets = "k1E,k1F";
            Assert.Equal("Clear key colors on 2 lights", clear.DisplayText);
        }

        [Theory]
        [InlineData(MacroKeyColorStyle.Steady, MacroKeyColorHold.Duration, "1 light in #FF0000 for 3000 ms", "2 lights in #FF0000 for 3000 ms")]
        [InlineData(MacroKeyColorStyle.Steady, MacroKeyColorHold.UntilRelease, "1 light in #FF0000 while held", "2 lights in #FF0000 while held")]
        [InlineData(MacroKeyColorStyle.Steady, MacroKeyColorHold.UntilCleared, "1 light in #FF0000 until cleared", "2 lights in #FF0000 until cleared")]
        [InlineData(MacroKeyColorStyle.Fade, MacroKeyColorHold.Duration, "1 light in #FF0000, fading over 3000 ms", "2 lights in #FF0000, fading over 3000 ms")]
        [InlineData(MacroKeyColorStyle.Countdown, MacroKeyColorHold.Duration, "1 light in #FF0000, counting down over 3000 ms", "2 lights in #FF0000, counting down over 3000 ms")]
        public void SetKeyColors_SaysOneLight_ForOne(MacroKeyColorStyle style, MacroKeyColorHold hold, string one, string two)
        {
            var step = new MacroAction
            {
                Type = MacroActionType.SetKeyColors,
                LightbarR = 0xFF,
                LightbarG = 0,
                LightbarB = 0,
                KeyColorStyle = style,
                KeyColorHold = hold,
                KeyColorMs = 3000,
            };
            step.KeyColorTargets = "k1E";
            Assert.Equal(one, step.DisplayText);
            step.KeyColorTargets = "k1E,k1F";
            Assert.Equal(two, step.DisplayText);
        }

        [Fact]
        public void AnEqImport_OfOneBand_SaysOneBand()
        {
            var vm = new PadViewModel(0) { DeviceConfig = new DeviceSlotConfig() };
            vm.ImportAutoEqTextForTest("Preamp: -3 dB\nFilter 1: ON PK Fc 100 Hz Gain 3 dB Q 1.0", "One.txt");
            Assert.Equal("Imported 1 band with a -3 dB preamp from One.txt.", vm.EqImportStatus);
            vm.ImportAutoEqTextForTest("Preamp: -3 dB\nFilter 1: ON PK Fc 100 Hz Gain 3 dB Q 1.0\n"
                + "Filter 2: ON PK Fc 200 Hz Gain 2 dB Q 1.0", "Two.txt");
            Assert.Equal("Imported 2 bands with a -3 dB preamp from Two.txt.", vm.EqImportStatus);
        }

        private static TranslationEntry Reason(TranslationStatus status, string key, string count)
            => new() { Status = status, ReasonKey = key, ReasonArgs = { count } };

        [Fact]
        public void AnImportReport_CountsItsReasons_InTheirOwnWords()
        {
            var one = Reason(TranslationStatus.Skipped, TranslationReasons.GameActionsNotSupported, "1");
            var three = Reason(TranslationStatus.Skipped, TranslationReasons.GameActionsNotSupported, "3");
            Assert.Equal("1 in-game action, Steam-only, no game-side hook", CommunityConfigsPanel.ReasonText(one));
            Assert.Equal("1 in-game action, Steam-only, no game-side hook", ImportConfigDialog.ReasonText(one));
            Assert.Equal("3 in-game actions, Steam-only, no game-side hook", CommunityConfigsPanel.ReasonText(three));
            Assert.Equal("3 in-game actions, Steam-only, no game-side hook", ImportConfigDialog.ReasonText(three));

            Assert.Equal("on-screen menu created (1 bound cell)",
                ImportConfigDialog.ReasonText(Reason(TranslationStatus.Clean, TranslationReasons.MenuEmitted, "1")));
            Assert.Equal("on-screen menu created (4 bound cells)",
                ImportConfigDialog.ReasonText(Reason(TranslationStatus.Clean, TranslationReasons.MenuEmitted, "4")));
        }

        [Fact]
        public void TheHelpers_PickTheWordsForOne_OnlyForOne()
        {
            string many = nameof(Strings.Devices_Caps_Axes_Format);
            Assert.Equal("1 axis", Strings.Counted(many, 1));
            Assert.Equal("0 axes", Strings.Counted(many, 0));
            Assert.Equal("2 axes", Strings.Counted(many, 2));
            Assert.Equal(Strings.Get(Strings.OneForms[many]), Strings.CountFormat(many, -1));
            // A report's values arrive as text.
            Assert.Equal(Strings.Get(many), Strings.CountFormat(many, "x"));
            Assert.Equal(Strings.Get(many), Strings.CountFormat(many, (string)null));
            Assert.Equal(Strings.Get(Strings.OneForms[many]), Strings.CountFormat(many, "1"));
            // A string with no words for one is its own words for any count.
            Assert.Equal(Strings.Get(nameof(Strings.Common_Save)), Strings.CountFormat(nameof(Strings.Common_Save), 1));
            Assert.Equal("1 button", Strings.Counted(nameof(Strings.Devices_Caps_Buttons_Format), 1));
        }
    }
}
