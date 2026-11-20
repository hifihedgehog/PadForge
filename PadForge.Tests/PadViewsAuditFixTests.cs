using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using System.Xml.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Fixes from the audit of the pad page's views: wording, counts and
    /// resets that disagreed with what their card shows.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class PadViewsAuditFixTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings = SettingsManager.UserSettings;
        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
        }

        private static string Page() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml");
        private static string PageCode() => ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs");

        private static Dictionary<string, string> Table(string file = "Strings.resx") =>
            XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Resources", "Strings", file)).Root!
                .Elements("data").ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");

        /// <summary>The text of the element that opens at the last tag
        /// start before <paramref name="anchor"/>, to its closing tag.</summary>
        private static string Around(string text, string anchor, int before, int after)
        {
            int at = text.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(at >= 0, anchor + " is not in the text");
            int from = Math.Max(0, at - before);
            return text.Substring(from, Math.Min(text.Length - from, before + after));
        }

        // X1-9

        [Fact]
        public void BandCount_UsesASingularFormForOneOutput()
        {
            var table = Table();
            Assert.Equal("{0} output", table["Pad_Band_Count_One_Format"]);
            Assert.Equal("{0} outputs", table["Pad_Band_Count_Format"]);
            string page = Page();
            int at = page.IndexOf("<Binding Path=\"ItemCount\"/>", StringComparison.Ordinal);
            Assert.True(at > 0);
            int start = page.LastIndexOf("<MultiBinding", at, StringComparison.Ordinal);
            string block = page.Substring(start, page.IndexOf("</MultiBinding>", at, StringComparison.Ordinal) - start);
            Assert.Contains("OneOrManyFormatConverter", block);
            Assert.Contains("Pad_Band_Count_One_Format", block);
            Assert.Contains("Pad_Band_Count_Format", block);
        }

        [Theory]
        [InlineData(1, "1 output")]
        [InlineData(0, "0 outputs")]
        [InlineData(2, "2 outputs")]
        [InlineData(11, "11 outputs")]
        public void OneOrManyFormatConverter_PicksTheFormByTheCount(int count, string expected)
        {
            var type = typeof(PadForge.Converters.FormatTextConverter).Assembly.GetType("PadForge.Converters.OneOrManyFormatConverter");
            Assert.NotNull(type);
            var converter = (IMultiValueConverter)Activator.CreateInstance(type!)!;
            Assert.Equal(expected, converter.Convert(new object[] { count, "{0} output", "{0} outputs" }, typeof(string), null!, CultureInfo.InvariantCulture));
        }

        // X2-7

        [Fact]
        public void EveryFirstRowLabel_IsAmongTheLabelsThatSizeTheColumn()
        {
            string code = PageCode();
            int at = code.IndexOf("foreach (string label in new[]", StringComparison.Ordinal);
            Assert.True(at > 0);
            string list = code.Substring(at, code.IndexOf('}', at) - at);
            var keys = Regex.Matches(Page(), "Text=\"\\{Binding (\\w+), Source=\\{x:Static strings:Strings.Instance\\}\\}\" Style=\"\\{StaticResource DzFirstLabel\\}\"")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.Contains("Pad_Stick_Speed", keys);
            foreach (string key in keys)
                Assert.Contains("s." + key, list);
        }

        // X3-1

        [Fact]
        public void TheSoundCardsNoSpeakerLine_StatesWhatTheCodeRead()
        {
            var table = Table();
            Assert.False(table.ContainsKey("Pad_Audio_NoSpeaker"));
            string line = table["Pad_Audio_NoSpeakerRoute"];
            Assert.DoesNotContain("no built-in speaker", line);
            Assert.Contains("PadForge has no path", line);
            string page = Page();
            Assert.DoesNotContain("Pad_Audio_NoSpeaker,", page);
            Assert.Contains("Binding Pad_Audio_NoSpeakerRoute,", page);
        }

        // X3-2

        [Fact]
        public void TheInBoxCardsResetAll_ResetsEveryRowTheCardShows()
        {
            var vm = new PadViewModel(0);
            vm.TouchpadTapMaxMotion = 0.2;
            vm.TouchpadLongPressMaxMotion = 0.2;
            vm.TouchpadTwoFingerSwipeAngularTolerance = 45;
            vm.TouchpadPinchThreshold = 0.5;
            vm.TouchpadRotateThresholdDegrees = 60;
            vm.ResetTouchpadInBoxCardCommand.Execute(null);
            Assert.Equal(0.04, vm.TouchpadTapMaxMotion);
            Assert.Equal(0.05, vm.TouchpadLongPressMaxMotion);
            Assert.Equal(25, vm.TouchpadTwoFingerSwipeAngularTolerance);
            Assert.Equal(0.25, vm.TouchpadPinchThreshold);
            Assert.Equal(20, vm.TouchpadRotateThresholdDegrees);
        }

        // X3-3

        [Fact]
        public void TheIndicatorLedsCardsResetAll_ClearsTheMuteLedEndpoint()
        {
            var config = new DeviceSlotConfig { MicLedMode = MicLedMode.FollowDeviceMute, MicLedFollowDeviceId = "endpoint-1" };
            config.ResetIndicatorLedsAllCommand.Execute(null);
            Assert.Equal(MicLedMode.Off, config.MicLedMode);
            Assert.Equal(string.Empty, config.MicLedFollowDeviceId);
        }

        // X3-O1

        [Theory]
        [InlineData(true, PadViewModel.LightsSection, LightbarMode.ScreenColor, true)]
        [InlineData(false, PadViewModel.LightsSection, LightbarMode.ScreenColor, false)]
        [InlineData(true, PadViewModel.RumbleSection, LightbarMode.ScreenColor, false)]
        [InlineData(true, PadViewModel.LightsSection, LightbarMode.PlayerNumber, false)]
        public void TheScreenColorTimer_RunsOnlyWhileTheLightsSectionShowsThatMode(bool shown, int section, LightbarMode mode, bool runs)
        {
            var method = typeof(PadForge.Views.PadPage).GetMethod("ScreenColorStatusRuns", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.NotNull(method);
            Assert.Equal(runs, (bool)method!.Invoke(null, new object[] { shown, section, mode })!);
        }

        [Fact]
        public void TheScreenColorTimer_ReadsAgainWhenTheSectionOrThePageShowsOrHides()
        {
            string code = PageCode();
            int sync = code.IndexOf("private void SyncScreenColorStatus()", StringComparison.Ordinal);
            Assert.True(sync > 0);
            string body = code.Substring(sync, 700);
            Assert.Contains("ScreenColorStatusRuns(IsVisible", body);
            int section = code.IndexOf("e.PropertyName == nameof(PadViewModel.SelectedSection)", StringComparison.Ordinal);
            Assert.True(section > 0);
            Assert.Contains("SyncScreenColorStatus();", code.Substring(section, 1200));
            Assert.Matches(@"IsVisibleChanged\s*\+=\s*\(_, _\)\s*=>\s*SyncScreenColorStatus\(\)", code);
        }

        // X4-4

        [Fact]
        public void TheMacroMoreFoldsHint_NamesTheRowsTheFoldShows()
        {
            string page = Page();
            string fold = Around(page, "x:Name=\"MacroMore\"", 0, 2600);
            Assert.DoesNotContain("Pad_More_Macro,", fold);
            Assert.Contains("<controls:MoreRow.Hint>", fold);
            int hint = fold.IndexOf("<controls:MoreRow.Hint>", StringComparison.Ordinal);
            string hintText = fold.Substring(hint);
            Assert.Contains("ShownNamesConverter", hintText);
            var paths = Regex.Matches(hintText.Substring(0, hintText.IndexOf("</controls:MoreRow.Hint>", StringComparison.Ordinal)), "<Binding Path=\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[]
            {
                "DataContext.HasShiftLayers", "Macro_Layer", "ShowsLayerRow", "Macro_Layer",
                "ShowsTriggerComboEditor", "Macro_Source", "ShowsLegacyAxisRows", "Macro_Threshold",
                "ShowsLegacyAxisRows", "Macro_AxisDirection", "ShowsTriggerComboEditor", "Macro_ConsumeTrigger",
                "ShowsRepeatSection", "Macro_Repeat",
            }, paths);
            Assert.False(Table().ContainsKey("Pad_More_Macro"));
        }

        [Fact]
        public void TheLegacyAxisRows_ShowOnlyInsideTheTriggerComboEditor()
        {
            var property = typeof(MacroItem).GetProperty("ShowsLegacyAxisRows");
            Assert.NotNull(property);
            var macro = new MacroItem { TriggerMode = MacroTriggerMode.OnPress };
            Assert.False((bool)property!.GetValue(macro)!);
            macro.TriggerAxisTargets = new[] { MacroAxisTarget.LeftTrigger };
            Assert.True((bool)property.GetValue(macro)!);
            macro.TriggerMode = MacroTriggerMode.Always;
            Assert.False((bool)property.GetValue(macro)!);
            macro.TriggerMode = MacroTriggerMode.Turbo;
            Assert.True((bool)property.GetValue(macro)!);
        }

        [Fact]
        public void TheLegacyAxisRows_RaiseTheirChangeWhenEitherGateChanges()
        {
            var macro = new MacroItem { TriggerMode = MacroTriggerMode.OnPress };
            var raised = new List<string>();
            macro.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            macro.TriggerMode = MacroTriggerMode.Always;
            Assert.Contains("ShowsLegacyAxisRows", raised);
            raised.Clear();
            macro.TriggerAxisTargets = new[] { MacroAxisTarget.LeftTrigger };
            Assert.Contains("ShowsLegacyAxisRows", raised);
        }

        // X4-5

        [Fact]
        public void TheDreamcastScreenHints_GiveNoWriteRating()
        {
            var table = Table();
            foreach (string key in new[] { "MacroAction_ShowDreamcastScreen_Hint", "DreamcastScreen_Hint_Clock", "DreamcastScreen_Hint_PlayTime" })
            {
                Assert.DoesNotContain("100,000", table[key]);
                Assert.DoesNotContain("rated", table[key]);
                Assert.DoesNotContain("ten weeks", table[key]);
            }
            Assert.Contains("one picture a second", table["MacroAction_ShowDreamcastScreen_Hint"]);
            Assert.Contains("once a minute", table["DreamcastScreen_Hint_Clock"]);
            Assert.Contains("once a minute", table["DreamcastScreen_Hint_PlayTime"]);
        }

        // X4-6

        [Fact]
        public void NudgeCursorsRows_AreLabeledAsOffsets()
        {
            var table = Table();
            Assert.Equal("Horizontal Offset", table["Macro_NudgeDx_Label"]);
            Assert.Equal("Vertical Offset", table["Macro_NudgeDy_Label"]);
            string page = Page();
            string dx = Around(page, "Text=\"{Binding NudgeDx,", 400, 900);
            Assert.Contains("Macro_NudgeDx_Label", dx);
            Assert.DoesNotContain("Macro_MouseX_Label", dx.Substring(dx.IndexOf("Text=\"{Binding Macro_NudgeDx_Label", StringComparison.Ordinal)));
            string dy = Around(page, "Text=\"{Binding NudgeDy,", 400, 900);
            Assert.Contains("Macro_NudgeDy_Label", dy);
            int rows = page.IndexOf("Text=\"{Binding NudgeDx,", StringComparison.Ordinal);
            string both = page.Substring(rows - 400, 2200);
            Assert.DoesNotContain("Macro_MouseY_Label", both.Substring(both.IndexOf("Macro_NudgeDx_Label", StringComparison.Ordinal)));
            Assert.Equal(2, Regex.Matches(both, "SettingLabel=\"\\{Binding Macro_Nudge(Dx|Dy)_Label").Count);
        }

        // C17-4

        [Theory]
        [InlineData("ControllerModelView.Annotations.cs")]
        [InlineData("ControllerModel2DView.Annotations.cs")]
        public void AnAnnotationChip_GivesItsTwelvePixelTextTheLineItNeeds(string file)
        {
            string code = ExtrasPageTests.RepoText("PadForge.App", "Views", file);
            Assert.Contains("Padding = new Thickness(6, 1, 6, 1),", code);
            Assert.DoesNotContain("Padding = new Thickness(6, 2, 6, 2),", code);
            // A 12 px line of the body face is 15.96 px tall (measured
            // 2026-11-20): the chip is 20 tall with a 1 px border and 1 px
            // padding above and below.
            Assert.True(20 - 2 * 1 - 2 * 1 >= 15.96);
            Assert.Equal(20, PadForge.Views.LegendColumn.ChipHeight);
        }

        // S1-5

        [Fact]
        public void TheTrimStickBadge_IsTitleCase()
        {
            Assert.Equal("Trim Stick", Table()["Pad_Row_TrimStick"]);
            // The English locale file carries no copy of the key, so the one edit
            // stands for it.
            Assert.False(Table("Strings.en-GB.resx").ContainsKey("Pad_Row_TrimStick"));
        }

        // S1-7

        [Theory]
        [InlineData(0x0CE6, true)]
        [InlineData(0x0DF2, true)]
        [InlineData(0x05C4, false)]
        [InlineData(0x09CC, false)]
        [InlineData(0x0BA0, false)]
        public void TheOutputPathTooltip_IsForTheDualSenseFamilyOnly(int productId, bool dualSense)
        {
            var id = Guid.NewGuid();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserDevices.Items.Add(new UserDevice
            {
                InstanceGuid = id, VendorId = 0x054C, ProdId = (ushort)productId, IsOnline = true, IsEnabled = true,
                DevicePath = "\\\\?\\hid#vid_054c&pid_" + productId.ToString("x4"),
            });
            var vm = new PadViewModel(0) { SelectedMappedDevice = new PadViewModel.MappedDeviceInfo { InstanceGuid = id } };
            var property = typeof(PadViewModel).GetProperty("SelectedDeviceIsDualSenseFamily");
            Assert.NotNull(property);
            Assert.Equal(dualSense, (bool)property!.GetValue(vm)!);
            Assert.Contains("ToolTipService.IsEnabled=\"{Binding SelectedDeviceIsDualSenseFamily}\"",
                Around(Page(), "ToolTip=\"{Binding Pad_Audio_OutputPath_Tooltip,", 0, 400));
        }
    }
}
