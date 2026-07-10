using System;
using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>Covers the #112 macro QOL logic that ships in PadForge.App:
    /// the macro/action DTO round-trip helpers, the clipboard JSON envelope, and
    /// the Copy From Other Device device-guid rewrite plus orphan detection.</summary>
    public class MacroQolTests
    {
        private static MacroItem SampleMacro()
        {
            var m = new MacroItem { Name = "Combo", IsEnabled = true, RepeatCount = 3 };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.ButtonPress,
                ButtonFlags = 4,
                KeyString = "{Ctrl}{C}",
                DurationMs = 75,
            });
            return m;
        }

        [Fact]
        public void MacroData_RoundTrip_PreservesFields()
        {
            var data = SettingsService.BuildMacroDataForMacro(SampleMacro(), 2);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);

            Assert.Equal("Combo", clone.Name);
            Assert.True(clone.IsEnabled);
            Assert.Equal(3, clone.RepeatCount);
            Assert.Single(clone.Actions);
            Assert.Equal(MacroActionType.ButtonPress, clone.Actions[0].Type);
            Assert.Equal((ushort)4, clone.Actions[0].ButtonFlags);
            Assert.Equal("{Ctrl}{C}", clone.Actions[0].KeyString);
            Assert.Equal(75, clone.Actions[0].DurationMs);
        }

        [Fact]
        public void ClipboardEnvelope_RoundTrips()
        {
            var data = SettingsService.BuildMacroDataForMacro(SampleMacro(), 0);
            string json = SettingsService.SerializeMacrosToClipboard(new[] { data });

            var env = SettingsService.TryParseMacroClipboard(json);
            Assert.NotNull(env);
            Assert.Single(env.Macros);
            Assert.Equal("Combo", env.Macros[0].Name);
            Assert.Equal(MacroActionType.ButtonPress, env.Macros[0].Actions[0].Type);
        }

        [Fact]
        public void TryParseMacroClipboard_RejectsForeignText()
        {
            Assert.Null(SettingsService.TryParseMacroClipboard(""));
            Assert.Null(SettingsService.TryParseMacroClipboard("not json"));
            Assert.Null(SettingsService.TryParseMacroClipboard("{\"Type\":\"PadForgeSettings\"}"));
        }

        // ── Text Block (#201) ──

        // Delimiters that trip stringly-packed formats (the pipe is the trigger-spec
        // separator), XML-special characters, CJK, an emoji surrogate pair, and a
        // newline. Actions persist as discrete XML elements, so all of it must
        // survive every round-trip verbatim.
        private const string HostileText = "gg | \"well\" <played> & 拜拜 🎮\nsecond line";

        private static MacroItem TextBlockMacro()
        {
            var m = new MacroItem { Name = "Chat", IsEnabled = true };
            m.Actions.Add(new MacroAction
            {
                Type = MacroActionType.TextBlock,
                TextContent = HostileText,
                TextPerCharDelayMs = 10,
            });
            return m;
        }

        [Fact]
        public void TextBlock_MacroData_RoundTrip_PreservesTextAndDelay()
        {
            var data = SettingsService.BuildMacroDataForMacro(TextBlockMacro(), 1);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);

            Assert.Single(clone.Actions);
            Assert.Equal(MacroActionType.TextBlock, clone.Actions[0].Type);
            Assert.Equal(HostileText, clone.Actions[0].TextContent);
            Assert.Equal(10, clone.Actions[0].TextPerCharDelayMs);
        }

        [Fact]
        public void TextBlock_ActionData_DuplicatePath_RoundTrips()
        {
            // The #112 Duplicate command deep-copies through the ActionData DTO.
            var original = TextBlockMacro().Actions[0];
            var clone = SettingsService.BuildMacroAction(SettingsService.BuildActionData(original));

            Assert.Equal(MacroActionType.TextBlock, clone.Type);
            Assert.Equal(HostileText, clone.TextContent);
            Assert.Equal(10, clone.TextPerCharDelayMs);
        }

        [Fact]
        public void TextBlock_ClipboardEnvelope_RoundTrips()
        {
            var data = SettingsService.BuildMacroDataForMacro(TextBlockMacro(), 0);
            string json = SettingsService.SerializeMacrosToClipboard(new[] { data });

            var env = SettingsService.TryParseMacroClipboard(json);
            Assert.NotNull(env);
            var clone = SettingsService.LoadMacroFromData(env.Macros[0], VirtualControllerType.Xbox, null);
            Assert.Equal(HostileText, clone.Actions[0].TextContent);
        }

        [Fact]
        public void TextBlock_Pacing_DelayZero_EmitsWholeStringImmediately()
        {
            Assert.Equal(5, MacroAction.ComputeTextEmitTarget("hello", 0, 0));
            Assert.Equal(5, MacroAction.ComputeTextEmitTarget("hello", 0, 12345));
        }

        [Fact]
        public void TextBlock_Pacing_PerCharDelay_EmitsOnePerInterval()
        {
            // Char k emits when elapsed reaches k * delay: the first character
            // goes out on the action's first tick, the rest follow the clock.
            Assert.Equal(1, MacroAction.ComputeTextEmitTarget("hello", 10, 0));
            Assert.Equal(1, MacroAction.ComputeTextEmitTarget("hello", 10, 9.9));
            Assert.Equal(2, MacroAction.ComputeTextEmitTarget("hello", 10, 10));
            Assert.Equal(3, MacroAction.ComputeTextEmitTarget("hello", 10, 25));
            Assert.Equal(5, MacroAction.ComputeTextEmitTarget("hello", 10, 100000));
        }

        [Fact]
        public void TextBlock_Pacing_NeverSplitsSurrogatePair()
        {
            // "a🎮b" is four UTF-16 code units: 'a', high, low, 'b'. At elapsed 10
            // the naive boundary lands between the surrogate halves (target 2);
            // the low half must ride along in the same emission (target 3).
            const string s = "a\U0001F3AEb";
            Assert.Equal(4, s.Length);
            Assert.Equal(3, MacroAction.ComputeTextEmitTarget(s, 10, 10));
            // Boundaries not touching the pair stay put.
            Assert.Equal(1, MacroAction.ComputeTextEmitTarget(s, 10, 0));
            Assert.Equal(4, MacroAction.ComputeTextEmitTarget(s, 10, 30));
        }

        [Fact]
        public void TextBlock_Pacing_EmptyText_EmitsNothing()
        {
            Assert.Equal(0, MacroAction.ComputeTextEmitTarget("", 10, 100));
            Assert.Equal(0, MacroAction.ComputeTextEmitTarget(null, 0, 0));
        }
    }
}
