using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Guard pins for the Pad page findings in the 2026-09-15 audit. These are
    /// markup and code-behind contracts, so they read the source the way the
    /// page's other guard tests do.
    /// </summary>
    public class PadPageAuditFixTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

        private static string Xaml() => Read("PadForge.App", "Views", "PadPage.xaml");
        private static string CodeBehind() => Read("PadForge.App", "Views", "PadPage.xaml.cs");

        // C276: a local value outranks a style trigger.

        /// <summary>The formula status colors itself through style triggers.
        /// A Foreground set on the element outranks any trigger setter, so
        /// with one there neither the invalid red nor the warning orange could
        /// ever apply and the status stayed gray whatever the formula did.
        /// The default belongs in the style, where the triggers can beat it.</summary>
        [Fact]
        public void TheFormulaStatusDefaultColorLivesInItsStyle()
        {
            string x = Xaml();
            int i = x.IndexOf("Text=\"{Binding CustomExpressionStatus}\"", StringComparison.Ordinal);
            Assert.True(i > 0, "the formula status TextBlock is gone");

            // Up to the end of its style block.
            int end = x.IndexOf("</Style>", i, StringComparison.Ordinal);
            Assert.True(end > i);
            string block = x.Substring(i, end - i);

            Assert.DoesNotContain("Foreground=\"{DynamicResource TextFillColorSecondaryBrush}\">", block);
            Assert.Contains("<Setter Property=\"Foreground\" Value=\"{DynamicResource TextFillColorSecondaryBrush}\"/>", block);
            // Both triggers are still there to win.
            Assert.Contains("IsCustomExpressionInvalid", block);
            Assert.Contains("IsCustomExpressionWarning", block);
        }

        /// <summary>Invalid is declared last so it wins when a formula is both
        /// invalid and warning. Equal-precedence triggers resolve to the last
        /// one that matches.</summary>
        [Fact]
        public void InvalidOutranksWarningOnTheFormulaStatus()
        {
            string x = Xaml();
            int i = x.IndexOf("Text=\"{Binding CustomExpressionStatus}\"", StringComparison.Ordinal);
            int end = x.IndexOf("</Style>", i, StringComparison.Ordinal);
            string block = x.Substring(i, end - i);

            int warn = block.IndexOf("IsCustomExpressionWarning", StringComparison.Ordinal);
            int invalid = block.IndexOf("IsCustomExpressionInvalid", StringComparison.Ordinal);
            Assert.True(warn < invalid,
                "warning is declared after invalid, so an invalid formula would show the warning color");
        }

        // C278: a reset button follows the control it resets.

        /// <summary>A setting that can be hidden takes its reset button with
        /// it. An ungated reset left a lone button sitting behind nothing.</summary>
        [Theory]
        [InlineData("Reset_MappingItem_InvertOutput_5776")]
        [InlineData("Reset_MappingSourceItem_InvertOutput_4943")]
        [InlineData("Reset_MappingItem_NoInherit_5788")]
        [InlineData("Reset_MacroExpressionVariable_OutputChannel_1658")]
        public void AGatedSettingsResetButtonIsGatedToo(string automationId)
        {
            string x = Xaml();
            int i = x.IndexOf(automationId, StringComparison.Ordinal);
            Assert.True(i > 0, $"{automationId} is gone");

            // The whole element, back to its opening tag.
            int start = x.LastIndexOf("<reset:SettingResetButton", i, StringComparison.Ordinal);
            Assert.True(start >= 0);
            string element = x.Substring(start, i - start);

            Assert.Contains("Visibility=", element);
        }

        // C279: the shared slider style ticks coarser than this range.

        /// <summary>The shared slider style snaps to a 0.1 grid, so a range
        /// of 0.80 to 1.00 offered exactly three values while the box beside
        /// it promised two decimals.</summary>
        [Fact]
        public void TheMomentumGlideSliderCanReachTwoDecimals()
        {
            string x = Xaml();
            int i = x.IndexOf("Value=\"{Binding MomentumGlide, Mode=TwoWay}\"", StringComparison.Ordinal);
            Assert.True(i > 0, "the momentum glide slider is gone");
            string element = x.Substring(i, Math.Min(500, x.Length - i));
            int close = element.IndexOf("/>", StringComparison.Ordinal);
            element = element.Substring(0, close);

            Assert.Contains("TickFrequency=\"0.01\"", element);
        }

        // C280: the duration row borrowed the mode picker's label.

        /// <summary>The steering-lock Hold row is a millisecond duration. It
        /// used the mode picker's label key, which four locales translate as
        /// "Mode", so the row read "Mode" above a millisecond slider.</summary>
        [Fact]
        public void TheSteeringLockHoldRowUsesTheDurationLabel()
        {
            string x = Xaml();
            int i = x.IndexOf("Value=\"{Binding SteeringLockLightbarHoldMs, Mode=TwoWay}\"",
                StringComparison.Ordinal);
            Assert.True(i > 0, "the steering-lock hold slider is gone");

            // The label sits immediately above the slider in the same row.
            string before = x.Substring(Math.Max(0, i - 600), Math.Min(600, i));
            Assert.Contains("Macro_TriggerHoldMs_Label", before);
            Assert.DoesNotContain("Macro_LightbarHold_Label", before);
        }

        /// <summary>The key it used labels the mode picker, and still does, so
        /// the two rows are not simply swapped.</summary>
        [Fact]
        public void TheModePickerKeepsItsOwnLabel()
        {
            Assert.Contains("Macro_LightbarHold_Label", Xaml());
        }

        // C231 and C232: the code-behind contracts.

        /// <summary>VR hides Sticks and Triggers, and was the one collapsing
        /// predicate with no eviction, so a user sitting on either tab when
        /// the slot became VR was left on a collapsed tab with no selected
        /// button.</summary>
        [Fact]
        public void AVrSlotEvictsASelectionOnACollapsedTab()
        {
            string cs = CodeBehind();
            Assert.Contains("if ((isMidi || isVrSlot) && (vm.SelectedConfigTab == 3 || vm.SelectedConfigTab == 4))", cs);
        }

        /// <summary>The runtime clear guarded one of the two statements it was
        /// indented around, so the second dereferenced the view model the
        /// guard had just tested for null.</summary>
        [Fact]
        public void TheShiftRuntimeClearGuardsBothOfItsStatements()
        {
            string cs = CodeBehind();
            var m = Regex.Match(cs,
                @"if \(_currentPadVm != null\)\s*\r?\n\s*\{\s*\r?\n\s*PadForge\.Common\.Input\.InputManager\.ClearShiftRuntime\(_currentPadVm\.PadIndex\);\s*\r?\n\s*PadForge\.Services\.InputService\.ClearMenuRuntimeForSlot\(_currentPadVm\.PadIndex\);\s*\r?\n\s*\}");
            Assert.True(m.Success, "the two clears are not both inside the null guard");
        }

        // C235: the Triggers tab reads a count that can change on its own.

        // C237 and C238: the passthrough clone.

        /// <summary>The clone confirm is not application-modal, so while it is
        /// up the user can switch slots, change the slot's type, apply a
        /// profile that replaces the config instance, or unassign the device.
        /// The apply writes both the page's controls and the slot's layout, so
        /// landing it on a different slot rewrites a configuration nobody
        /// asked about. The layer-delete confirm on this page already
        /// re-validates after its await for exactly this reason.</summary>
        [Fact]
        public void TheCloneRevalidatesAfterItsConfirm()
        {
            string cs = CodeBehind();
            int i = cs.IndexOf("await confirm.ShowDialogAsync()", StringComparison.Ordinal);
            Assert.True(i > 0, "the clone confirm is gone");
            int apply = cs.IndexOf("ApplyPassthroughClone(vm,", i, StringComparison.Ordinal);
            Assert.True(apply > i, "the apply no longer follows the confirm");
            string between = cs.Substring(i, apply - i);

            Assert.Contains("ReferenceEquals(DataContext, vm)", between);
            Assert.Contains("vm.OutputType != Engine.VirtualControllerType.Extended", between);
            Assert.Contains("ReferenceEquals(vm.ExtendedConfig, cfgAtOpen)", between);
            Assert.Contains("ReferenceEquals(vm.SelectedMappedDevice, sel)", between);
        }

        /// <summary>The clone displaces another device's primary onto the same
        /// row as an extra source, which the method's own summary promises
        /// keeps multi-device combining additive. It rebuilt that source from
        /// the descriptor and a couple of flags, so everything else the
        /// primary carried was dropped and it came back behaving differently
        /// from what the user authored.</summary>
        [Theory]
        [InlineData("InvertOutput = oldInvertOutput,")]
        [InlineData("Bidirectional = oldBidirectional,")]
        [InlineData("Sensitivity = oldSensitivity,")]
        [InlineData("GyroSensitivity = oldGyroSensitivity,")]
        [InlineData("MouseCursorSensitivity = oldMouseCursorSensitivity,")]
        [InlineData("IrPointerSensitivity = oldIrPointerSensitivity,")]
        [InlineData("Kind = oldKind,")]
        public void ADisplacedPrimaryKeepsWhatItCarried(string assignment)
        {
            Assert.Contains(assignment, CodeBehind());
        }

        // C240: a picker could blank the value it was showing.

        /// <summary>Replacing a picker's items under a live two-way selection
        /// can write a default back into the bound object while the old
        /// selection is momentarily unresolvable. Both device-axis pickers are
        /// wired to the dropdown open AND to Loaded straight from the markup,
        /// so the every-day paths replaced the list with no capture and merely
        /// LOOKING at the picker could blank the saved device. The page
        /// already treats this as a defect at two other pickers.</summary>
        [Theory]
        [InlineData("DeviceAxisPicker_DropDownOpened")]
        [InlineData("DeviceAxisIndexPicker_DropDownOpened")]
        public void EveryPickerPathHoldsItsSelectionAcrossARepopulate(string handler)
        {
            string cs = CodeBehind();
            int i = cs.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
            Assert.True(i > 0, handler + " is gone");
            // To the next member declaration: the body holds nested blocks
            // that close at the same depth as a method would.
            int end = cs.IndexOf("\n        private ", i + 10, StringComparison.Ordinal);
            if (end < 0) end = cs.Length;
            string body = cs.Substring(i, end - i);

            Assert.Contains("PickerSelectionGuard", body);
            Assert.Contains("cb.ItemsSource =", body);
            // The guard has to be taken before the list is replaced. Match the
            // ASSIGNMENT, not the word: the comment above the guard names the
            // property it protects.
            Assert.True(body.IndexOf("new PickerSelectionGuard(", StringComparison.Ordinal)
                      < body.IndexOf("cb.ItemsSource =", StringComparison.Ordinal),
                "the items are replaced before the selection is captured");
        }

        /// <summary>The guard restores the pair it captured, so a write-back
        /// during the repopulate is undone rather than merely detected.</summary>
        [Fact]
        public void ThePickerGuardRestoresBothHalvesOfThePair()
        {
            string cs = CodeBehind();
            int i = cs.IndexOf("private readonly struct PickerSelectionGuard", StringComparison.Ordinal);
            Assert.True(i > 0, "the picker guard is gone");
            int end = cs.IndexOf("\n        private void HookDeviceAxisPickerRefresh", i, StringComparison.Ordinal);
            string body = cs.Substring(i, end - i);

            Assert.Contains("SourceDeviceGuid = _guid;", body);
            Assert.Contains("SourceDeviceAxisIndex = _axisIndex;", body);
        }

        // C239: the MIDI bar heard nothing and wrote everything back.

        /// <summary>A profile switch on the SAME slot mutates the MIDI config
        /// in place, with no output type or data context change, so nothing
        /// told the bar to re-read and its six boxes kept the outgoing
        /// profile's numbers. The write-back reads all six whenever one loses
        /// focus, so a single click into Channel pushed five stale values over
        /// the profile that had just been applied.</summary>
        [Fact]
        public void TheMidiBarFollowsItsConfig()
        {
            string cs = CodeBehind();
            Assert.Contains("_currentMidiConfig.PropertyChanged += OnMidiConfigBarPropertyChanged;", cs);
            Assert.Contains("_currentMidiConfig.PropertyChanged -= OnMidiConfigBarPropertyChanged;", cs);
        }

        /// <summary>The re-read now runs on a config change, not only on a
        /// page load, so it has to leave a box the user is typing in alone or
        /// it destroys the edit and the caret with it.</summary>
        [Fact]
        public void TheMidiBarDoesNotOverwriteABoxBeingEdited()
        {
            string cs = CodeBehind();
            int i = cs.IndexOf("private void SyncMidiConfigBar()", StringComparison.Ordinal);
            Assert.True(i > 0, "the MIDI bar sync is gone");
            int end = cs.IndexOf("\n        }", i, StringComparison.Ordinal);
            string body = cs.Substring(i, end - i);

            Assert.Contains("IsKeyboardFocusWithin", body);
            // Every box goes through the guarded setter, none assigned raw.
            Assert.DoesNotContain("MidiChannelBox.Text = ", body);
            Assert.DoesNotContain("MidiVelocityBox.Text = ", body);
        }

        /// <summary>The Triggers tab gates on the layout's trigger count, and
        /// that count changes with no OutputType or DataContext change behind
        /// it, so the tab kept the outgoing layout's visibility.</summary>
        [Fact]
        public void ATriggerCountChangeReSyncsTheTabVisibility()
        {
            string cs = CodeBehind();
            var m = Regex.Match(cs,
                @"if \(e\.PropertyName == nameof\(PadForge\.ViewModels\.ExtendedSlotConfig\.TriggerCount\)\)\s*\r?\n\s*SyncTabVisibility\(\);");
            Assert.True(m.Success, "a trigger count change no longer re-syncs the tabs");
        }
    }
}
