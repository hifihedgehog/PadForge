using PadForge.Engine;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #9 macro actions MoveMouseToScreenPosition (MouseX / MouseY) and
    /// RepeatKeyWhileHeld (KeyCode + IntervalMs), exercised through the full
    /// ActionData DTO round-trip that both the settings XML and the macro
    /// clipboard share (SettingsService.BuildMacroDataForMacro / LoadMacroFromData).
    /// Also pins the append-only enum contract the clipboard depends on.
    /// </summary>
    public class MacroCursorKeyActionTests
    {
        private static MacroItem OneAction(MacroAction a)
        {
            var m = new MacroItem { Name = "M", IsEnabled = true };
            m.Actions.Add(a);
            return m;
        }

        private static MacroAction RoundTrip(MacroAction a)
        {
            var data = SettingsService.BuildMacroDataForMacro(OneAction(a), 0);
            var clone = SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null);
            return Assert.Single(clone.Actions);
        }

        [Fact]
        public void MoveMouseToScreenPosition_RoundTripsCoords()
        {
            var clone = RoundTrip(new MacroAction
            {
                Type = MacroActionType.MoveMouseToScreenPosition,
                MouseX = 300,
                MouseY = 200,
            });
            Assert.Equal(MacroActionType.MoveMouseToScreenPosition, clone.Type);
            Assert.Equal(300, clone.MouseX);
            Assert.Equal(200, clone.MouseY);
        }

        [Fact]
        public void RepeatKeyWhileHeld_RoundTripsKeyAndInterval()
        {
            var clone = RoundTrip(new MacroAction
            {
                Type = MacroActionType.RepeatKeyWhileHeld,
                KeyCode = 0x41, // VK_A
                IntervalMs = 250,
            });
            Assert.Equal(MacroActionType.RepeatKeyWhileHeld, clone.Type);
            Assert.Equal(0x41, clone.KeyCode);
            Assert.Equal(250, clone.IntervalMs);
        }

        [Fact]
        public void IntervalMs_ClampedToRange()
        {
            Assert.Equal(1000, new MacroAction { IntervalMs = 5000 }.IntervalMs);
            Assert.Equal(10, new MacroAction { IntervalMs = 1 }.IntervalMs);
            Assert.Equal(100, new MacroAction().IntervalMs); // default
        }

        [Fact]
        public void ParsedKeyCodes_MemoizedUntilInputsChange()
        {
            var a = new MacroAction { KeyString = "{Control}{Alt}" };
            var first = a.ParsedKeyCodes;
            Assert.Equal(2, first.Length);
            Assert.Same(first, a.ParsedKeyCodes);

            a.KeyString = "{Delete}";
            var second = a.ParsedKeyCodes;
            Assert.NotSame(first, second);
            Assert.Single(second);
            Assert.Same(second, a.ParsedKeyCodes);

            var legacy = new MacroAction { KeyCode = 0x41 };
            var firstLegacy = legacy.ParsedKeyCodes;
            Assert.Equal(new[] { 0x41 }, firstLegacy);
            Assert.Same(firstLegacy, legacy.ParsedKeyCodes);

            legacy.KeyCode = 0x42;
            Assert.Equal(new[] { 0x42 }, legacy.ParsedKeyCodes);
        }

        [Fact]
        public void NewActionTypes_AppendedAtEnumTail()
        {
            // The macro clipboard serializes MacroActionType numerically, so the
            // two new members MUST stay the last two ordinals (append-only).
            var values = System.Enum.GetValues<MacroActionType>();
            Assert.Equal(MacroActionType.RepeatKeyWhileHeld, values[^1]);
            Assert.Equal(MacroActionType.MoveMouseToScreenPosition, values[^2]);
        }

        [Fact]
        public void DisplayText_SummarizesNewActions()
        {
            var mv = new MacroAction
            {
                Type = MacroActionType.MoveMouseToScreenPosition,
                MouseX = 100,
                MouseY = 200,
            };
            Assert.Contains("100", mv.DisplayText);
            Assert.Contains("200", mv.DisplayText);

            var rk = new MacroAction
            {
                Type = MacroActionType.RepeatKeyWhileHeld,
                KeyCode = 0x41,
                IntervalMs = 100,
            };
            Assert.Contains("100", rk.DisplayText);
        }
    }
}
