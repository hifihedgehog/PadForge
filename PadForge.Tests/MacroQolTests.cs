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
            var guid = Guid.NewGuid();
            var data = SettingsService.BuildMacroDataForMacro(SampleMacro(), 0);
            string json = SettingsService.SerializeMacrosToClipboard(new[] { data }, guid.ToString("N"));

            var env = SettingsService.TryParseMacroClipboard(json);
            Assert.NotNull(env);
            Assert.Equal(guid.ToString("N"), env.SourceDeviceGuid);
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

        [Fact]
        public void RewriteForDevice_RemapsAllSurfaces()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var m = new MacroItem { Name = "X", TriggerDeviceGuid = a };
            m.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, SourceDeviceGuid = a });
            m.TriggerExpressionVariables.Add(new MacroExpressionVariable { DeviceGuid = a, RawButton = 5 });

            m.RewriteForDevice(a, b, idx => idx == 5);

            Assert.Equal(b, m.TriggerDeviceGuid);
            Assert.Equal(b, m.Actions[0].SourceDeviceGuid);
            Assert.Equal(b, m.TriggerExpressionVariables[0].DeviceGuid);
            Assert.False(m.TriggerExpressionVariables[0].IsOrphan);
            Assert.Equal(0, m.OrphanCount);
        }

        [Fact]
        public void RewriteForDevice_FlagsOrphanWhenTargetLacksButton()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var m = new MacroItem { Name = "X" };
            m.TriggerExpressionVariables.Add(new MacroExpressionVariable { DeviceGuid = a, RawButton = 9 });

            m.RewriteForDevice(a, b, idx => false); // target has no buttons

            Assert.Equal(b, m.TriggerExpressionVariables[0].DeviceGuid);
            Assert.True(m.TriggerExpressionVariables[0].IsOrphan);
            Assert.Equal(1, m.OrphanCount);
            Assert.True(m.HasOrphans);
        }

        [Fact]
        public void GetPrimaryDeviceGuid_PrefersLegacyTriggerDevice()
        {
            Assert.Equal(Guid.Empty, new MacroItem().GetPrimaryDeviceGuid());

            var a = Guid.NewGuid();
            Assert.Equal(a, new MacroItem { TriggerDeviceGuid = a }.GetPrimaryDeviceGuid());
        }
    }
}
