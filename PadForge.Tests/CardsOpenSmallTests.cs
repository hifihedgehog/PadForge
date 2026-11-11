using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using PadForge.Converters;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The cards of a controller's page open small (#574, step 6): a card
    /// for a feature that is off until switched on folds to one line, and a
    /// large card shows the settings most people change and keeps the rest
    /// under a More row. These tests are the table of which card does
    /// which, so a card that changes kind, loses its key or moves a setting
    /// across the More line fails by name.
    /// </summary>
    public class CardsOpenSmallTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static XDocument Page() => XDocument.Parse(ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));
        private static string Name(XElement e) => (string)e.Attribute(X + "Name");
        private static string Id(XElement e) => (string)e.Attribute("AutomationProperties.AutomationId");

        private static string Key(XElement e, string attribute)
        {
            string value = (string)e.Attribute(attribute) ?? "";
            const string lead = "{Binding ";
            int comma = value.IndexOf(',');
            return value.StartsWith(lead, StringComparison.Ordinal) && comma > 0 ? value.Substring(lead.Length, comma - lead.Length) : value;
        }

        /// <summary>Every card that folds to one line: its name, the key
        /// its open state is stored under, its title and its hint.</summary>
        public static readonly (string Name, string StateKey, string Header, string Hint)[] FoldCards =
        {
            ("SocdFoldCard", "Pad.Output.Socd", "Pad_Socd_Card_Header", "Pad_Socd_FoldHint"),
            ("KbmSocdFoldCard", "Pad.Output.KbmSocd", "Pad_Socd_Card_Header", "Pad_Kbm_Socd_FoldHint"),
            ("KeepAwakeFoldCard", "Pad.Output.KeepAwake", "Pad_KeepAwake_Card_Header", "Pad_KeepAwake_FoldHint"),
            ("LockFeedbackCard", "Pad.Sticks.LockFeedback", "Pad_Steering_LockFeedback", "Pad_LockFeedback_FoldHint"),
            ("FlickStickCard", "Pad.Sticks.FlickStick", "Pad_FlickStick_Header", "Pad_FlickStick_FoldHint"),
            ("AudioRumbleFoldCard", "Pad.Rumble.Audio", "Settings_AudioRumble", "Pad_AudioRumble_FoldHint"),
            ("TriggerRoutingFoldCard", "Pad.Rumble.TriggerRouting", "Pad_TriggerRouting_Header", "Pad_TriggerRouting_FoldHint"),
            ("AudioTriggerRumbleFoldCard", "Pad.Rumble.AudioTriggers", "Pad_AudioTriggerRumble_Header", "Pad_AudioTriggerRumble_FoldHint"),
            // Grip's hint is its paragraph, which the line trims: its first
            // sentence is what the board writes there.
            ("GyroGripCard", "Pad.Motion.Grip", "Pad_Gyro_Grip_Header", "Pad_Gyro_Grip_Subtitle"),
            ("GyroEngageCard", "Pad.Motion.Engage", "Pad_Gyro_Engage_Header", "Pad_GyroEngage_FoldHint"),
            ("GyroResponseCard", "Pad.Motion.Response", "Pad_Gyro_Response_Header", "Pad_GyroResponse_FoldHint"),
            ("MotionSteeringFoldCard", "Pad.Motion.Steering", "Pad_MotionSteer_Header", "Pad_MotionSteering_FoldHint"),
            ("GyroTiltFoldCard", "Pad.Motion.Tilt", "Pad_GyroTilt_Header", "Pad_GyroTilt_FoldHint"),
            ("GyroPassthroughCard", "Pad.Motion.Passthrough", "Pad_Gyro_Passthrough_Header", "Pad_Gyro_ApplyToPassthrough"),
            ("GyroSimulationCard", "Pad.Motion.Simulation", "Pad_Gyro_Simulation_Header", "Pad_GyroSimulation_FoldHint"),
            ("CompassYawCard", "Pad.Motion.Compass", "Pad_Gyro_Compass_Header", "Pad_Compass_FoldHint"),
            ("TouchpadStickFoldCard", "Pad.Touch.Stick", "Pad_Touchpad_Stick_Header", "Pad_TouchpadStick_FoldHint"),
            ("TouchpadPointerFoldCard", "Pad.Touch.Pointer", "Pad_Touchpad_Pointer_Header", "Pad_AbsolutePointer_FoldHint"),
            ("SyntheticPressureFoldCard", "Pad.Touch.Pressure", "Pad_Touchpad_SyntheticPressure_Header", "Pad_SyntheticPressure_FoldHint"),
            ("SwipeHapticsFoldCard", "Pad.Touch.SwipeHaptics", "Pad_Touchpad_SwipeHaptics_Header", "Pad_SwipeHaptics_FoldHint"),
            ("GestureDetectionFoldCard", "Pad.Touch.Gestures", "Pad_Touchpad_Detection_Header", "Pad_GestureDetection_FoldHint"),
            ("InBoxGesturesFoldCard", "Pad.Touch.InBox", "Pad_Touchpad_InBox_Header", "Pad_InBoxGestures_FoldHint"),
            ("CustomGesturesFoldCard", "Pad.Touch.Custom", "Pad_Touchpad_Custom_Header", "Pad_CustomGestures_FoldHint"),
            ("PeripheralLightingCard", "Pad.Lights.Control", "Pad_Lighting_ControlCard", "Pad_LightingControl_FoldHint"),
            ("IndicatorLedsCard", "Pad.Lights.Indicators", "Pad_Lighting_IndicatorLeds", "Pad_IndicatorLeds_FoldHint"),
            ("GuideLedCard", "Pad.Lights.Guide", "Pad_Lighting_GuideLed", "Pad_GuideLed_FoldHint"),
            ("SoundPackagesFoldCard", "Pad.Sound.Packages", "Pad_Audio_Packages_Header", "Pad_SoundPackages_FoldHint"),
            ("IconPackagesFoldCard", "Pad.Menus.IconPackages", "Pad_Menus_IconPackages_Header", "Pad_IconPackages_FoldHint"),
        };

        /// <summary>Every More row: its id, its key, its hint, a text that
        /// each setting kept up front holds, and one that each setting
        /// under More holds.</summary>
        public static readonly (string Id, string StateKey, string Hint, string[] UpFront, string[] UnderMore)[] MoreRows =
        {
            ("Pad_More_Stick", "{Binding MoreStateKey}", "Pad_More_Stick",
                new[] { "{Binding DeadZoneBoth, Mode=TwoWay}", "PresetNameBoth", "SensitivityCurveBoth", "CalibrateCenter_Click", "ResetStickSensitivityCommand" },
                new[] { "CenterOffsetX", "{Binding DeadZoneShapeIndex, Mode=TwoWay", "{Binding DeadZoneX, Mode=TwoWay}", "AntiDeadZoneY", "{Binding Linear, Mode=TwoWay}", "MomentumGlide",
                        "PresetNameX", "PresetNameY", "CalibrateBoundaryCommand", "MaxRangeYNeg", "Pad_Steering_Header" }),
            ("Pad_More_Falcon", "Pad.Rumble.Falcon.More", "Pad_More_Falcon",
                new[] { "{Binding FalconFeel, Mode=TwoWay}", "FalconSpring", "FalconForceCap" },
                new[] { "FalconKickButton", "{Binding FalconKick, Mode=TwoWay}", "FalconShake", "FalconBraceButton", "FalconPreloadButton", "FalconGravity" }),
            ("Pad_More_ForceFeedback", "Pad.Rumble.ForceFeedback.More", "Pad_More_ForceFeedback",
                new[] { "ForceOverallGain", "Pad_MotorActivity", "TestRumbleCommand" },
                new[] { "LeftMotorStrength", "RightMotorStrength", "Pad_TestLeftMotor", "Pad_TestRightMotor", "TriggerRumbleFold", "SwapMotors",
                        "SteeringAngleRumbleEnabled", "SteeringAngleRumbleAxis", "SteeringAngleRumbleMotor", "SteeringAngleRumbleStrength",
                        "SteeringAngleRumbleDeadzone", "ConstantForceEnabled", "ConstantForcePadBorder", "ConstantForceX", "ConstantForceY" }),
            ("Pad_More_ImpulseTriggers", "Pad.Rumble.ImpulseTriggers.More", "Pad_More_ImpulseTriggers",
                new[] { "ImpulseOverallGain", "Pad_ImpulseTriggers_MotorActivity" },
                new[] { "ImpulseLeftStrength", "ImpulseRightStrength", "TestLeftImpulseTriggerCommand", "TestRightImpulseTriggerCommand",
                        "ImpulseSwapTriggers", "AtVibrationToImpulse", "ConstantTriggerForceEnabled", "ConstantTriggerForceLeft", "ConstantTriggerForceRight" }),
            ("Pad_More_BassShakers", "Pad.Rumble.BassShakers.More", "Pad_More_BassShakers",
                new[] { "{Binding RumbleAudioEnabled, Mode=TwoWay}" },
                new[] { "RumbleAudioEndpointId", "RumbleAudioChannelMode", "RumbleAudioMasterGain" }),
            // Calibration opens on its two buttons and when it was last
            // done. The live readings are under More.
            ("Pad_More_GyroCalibration", "Pad.Motion.Calibration.More", "Settings_GyroLiveRate_Label",
                new[] { "GyroCalibrateCommand", "GyroResetCalibrationCommand", "GyroCalibrationLabel" },
                new[] { "GyroLiveRatePitch", "GyroLiveRateYaw", "GyroLiveRateRoll", "AccelLiveX", "AccelLiveY", "AccelLiveZ" }),
            ("Pad_More_GyroSensitivity", "Pad.Motion.Sensitivity.More", "Pad_More_GyroSensitivity",
                new[] { "{Binding GyroSensitivityH, Mode=TwoWay", "{Binding GyroSensitivityV, Mode=TwoWay" },
                new[] { "GyroSensitivityUnits", "GyroSpace", "GyroInvertPitch", "GyroInvertYaw", "GyroInvertRoll", "GyroRealWorldCalibration" }),
            ("Pad_More_TouchpadMouse", "Pad.Touch.Mouse.More", "Pad_More_TouchpadMouse",
                new[] { "{Binding TouchpadMouseSensitivityX, Mode=TwoWay", "{Binding TouchpadMouseSensitivityY, Mode=TwoWay", "TouchpadMouseJitterReduction" },
                new[] { "TouchpadMouseInvertX", "TouchpadMouseInvertY", "{Binding TouchpadMouseMomentum, Mode=TwoWay", "TouchpadPointerResponse",
                        "TouchpadTrackpadThreshold", "TouchpadMouseAcceleration", "TouchpadMomentumFlingGain", "TouchpadMomentumStacking" }),
            ("Pad_More_Lightbar", "Pad.Lights.Lightbar.More", "Pad_Lighting_InputReactive",
                new[] { "DeviceConfig.ResetLightbarModeCommand", "DeviceConfig.LightbarPeriodMs", "DeviceConfig.ScreenColorArea", "DeviceConfig.LightbarRed" },
                new[] { "DeviceConfig.ResetInputReactiveModeCommand", "DeviceConfig.LightbarInputHoldMs", "DeviceConfig.LightbarInputReactivePalette", "DeviceConfig.InputReactiveR" }),
            ("Pad_More_SoundOutput", "Pad.Sound.Output.More", "Pad_More_SoundOutput",
                new[] { "{Binding SoundMasterVolume", "DeviceConfig.AudioPassthroughEnabled, Mode=TwoWay" },
                new[] { "SelectedMirrorSourceId", "DeviceConfig.AudioMirrorEngageMode", "DeviceConfig.AudioToneFilterMode", "DeviceConfig.HeadphoneVolume",
                        "DeviceConfig.Ds5AudioBufferLength", "DeviceConfig.AudioOutputPath", "DeviceConfig.AudioCrossfeedLevel", "SoundTestCommand" }),
        };

        [Fact]
        public void TheCardsThatFoldAreTheTables()
        {
            var cards = Page().Descendants().Where(e => e.Name.LocalName == "FoldCard").ToList();
            Assert.Equal(FoldCards.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal),
                cards.Select(Name).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var want in FoldCards)
            {
                var card = cards.Single(c => Name(c) == want.Name);
                Assert.Equal(want.StateKey, (string)card.Attribute("StateKey"));
                Assert.Equal(want.Header, Key(card, "Header"));
                Assert.Equal(want.Hint, Key(card, "Hint"));
                // The card's rows are one panel, and nothing of the old
                // title row is left in it.
                var panel = Assert.Single(card.Elements(), e => e.Name.LocalName == "StackPanel");
                Assert.DoesNotContain(panel.Descendants(), e => ((string)e.Attribute("Style") ?? "").Contains("CardTitle"));
                // It is a line of a list, which draws the one card its
                // lines share, and it has no margin of its own there.
                Assert.Equal("FoldList", card.Parent!.Name.LocalName);
                Assert.Null(card.Attribute("Margin"));
                // Its Reset All stands at the line's end and never in its
                // rows. Its description is the line's tooltip, so the rows
                // open on no paragraph that says what the part is.
                Assert.DoesNotContain(panel.Elements(), e => ((string)e.Attribute("Style") ?? "").Contains("ResetAllButton"));
                var action = card.Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.HeaderAction");
                if (action != null)
                    Assert.Equal("{StaticResource ResetAllButton}", (string)Assert.Single(action.Elements()).Attribute("Style"));
                if (card.Attribute("Description") != null)
                    Assert.DoesNotContain(panel.Elements().Take(1), e => ((string)e.Attribute("Style") ?? "").Contains("CardDescription")
                        && e.Attribute("Visibility") == null && Name(e) == null);
            }
            // A list holds fold cards and nothing else.
            foreach (var list in Page().Descendants().Where(e => e.Name.LocalName == "FoldList"))
                Assert.All(list.Elements(), e => Assert.Equal("FoldCard", e.Name.LocalName));
            // The 22 parts that have a Reset All and the 22 that have a
            // description keep them. Grip has a description and no Reset
            // All: its one row has its own reset.
            Assert.Equal(22, cards.Count(c => c.Elements().Any(e => e.Name.LocalName == "FoldCard.HeaderAction")));
            Assert.Equal(22, cards.Count(c => c.Attribute("Description") != null));
            Assert.Null(cards.Single(c => Name(c) == "GyroGripCard").Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.HeaderAction"));
        }

        /// <summary>A feature that is off until switched on, and that the
        /// Rumble board keeps under its card's More row, is a switch with
        /// its settings under it while it is on: Steering Angle Rumble and
        /// Constant Force under Force Feedback, Constant Trigger Force
        /// under Impulse Triggers. Each was a card of its own that folded.
        /// What the feature does is the tooltip of its switch and of the
        /// switch's label.</summary>
        [Theory]
        [InlineData("SteeringAngleRumbleRows", "Pad_More_ForceFeedback", "SteeringAngleRumbleEnabled", "Pad_SteeringAngleRumble_Description", 4)]
        [InlineData("ConstantForceRows", "Pad_More_ForceFeedback", "ConstantForceEnabled", "Pad_ConstantForce_Description", 2)]
        [InlineData("ConstantTriggerForceRows", "Pad_More_ImpulseTriggers", "ConstantTriggerForceEnabled", "Pad_ConstantTriggerForce_Description", 2)]
        public void ASwitchUnderMore_ShowsItsSettingsWhileItIsOn(string block, string more, string flag, string description, int settings)
        {
            var page = Page();
            var rows = Assert.Single(page.Descendants(), e => Name(e) == block);
            Assert.Equal("StackPanel", rows.Name.LocalName);
            Assert.Contains(Assert.Single(page.Descendants(), e => Id(e) == more), rows.Ancestors());
            var parts = rows.Elements().ToList();
            Assert.Equal(new[] { "SettingRow", "StackPanel" }, parts.Select(p => p.Name.LocalName));

            // The switch's row: label, switch, reset.
            var first = parts[0].Elements().ToList();
            Assert.Equal("{StaticResource DzLabel}", (string)first[0].Attribute("Style"));
            Assert.Equal("{StaticResource SwitchCheckBox}", (string)first[1].Attribute("Style"));
            Assert.Equal("{Binding " + flag + ", Mode=TwoWay}", (string)first[1].Attribute("IsChecked"));
            Assert.Equal(description, Key(first[0], "ToolTip"));
            Assert.Equal(description, Key(first[1], "ToolTip"));

            // Its settings: there while the switch is on, each a row whose
            // label is set in from the switch's, with a reset of its own.
            Assert.Equal("{Binding " + flag + ", Converter={StaticResource BoolToVisibilityConverter}}", (string)parts[1].Attribute("Visibility"));
            var under = parts[1].Elements().Where(e => e.Name.LocalName == "SettingRow").ToList();
            Assert.Equal(settings, under.Count);
            Assert.All(under, row =>
            {
                Assert.Equal("{StaticResource DzSubLabel}", (string)row.Elements().First().Attribute("Style"));
                Assert.Contains(row.Elements(), e => ((string)e.Attribute("Style") ?? "").Contains("ResetButton") || e.Name.LocalName == "SettingResetButton");
            });
            // Hidden with their switch off, they need no enabled state of
            // their own tied to it.
            Assert.DoesNotContain(parts[1].Descendants(), e => ((string)e.Attribute("IsEnabled") ?? "").Contains(flag));
        }

        [Fact]
        public void EveryFoldingPartHasAKeyOfItsOwn()
        {
            var parts = Page().Descendants().Where(e => e.Name.LocalName is "FoldCard" or "MoreRow").ToList();
            var keys = parts.Select(p => (string)p.Attribute("StateKey")).ToList();
            Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            // The one bound key is the stick's, which is one per stick.
            Assert.Equal(new[] { "{Binding MoreStateKey}" }, keys.Where(k => k.StartsWith("{", StringComparison.Ordinal)));
        }

        [Fact]
        public void EveryMoreRowKeepsItsSettingsOnTheirSideOfTheLine()
        {
            var page = Page();
            var rows = page.Descendants().Where(e => e.Name.LocalName == "MoreRow").ToList();
            Assert.Equal(MoreRows.Select(m => m.Id).OrderBy(n => n, StringComparer.Ordinal),
                rows.Select(Id).OrderBy(n => n, StringComparer.Ordinal));
            foreach (var want in MoreRows)
            {
                var row = rows.Single(r => Id(r) == want.Id);
                Assert.Equal(want.StateKey, (string)row.Attribute("StateKey"));
                Assert.Equal("Common_More", Key(row, "Header"));
                Assert.Equal(want.Hint, Key(row, "Hint"));

                string under = row.ToString();
                // Up front: what stands before the row in its own panel. A
                // stick's row follows the card's two columns, and there it
                // is the settings column alone: the title's summary and the
                // live stick view beside the settings read the values under
                // More to show them, and set none.
                var columns = row.ElementsBeforeSelf().LastOrDefault();
                var settings = columns?.Elements().FirstOrDefault(e => (string)e.Attribute("Grid.Column") == "0");
                string before = settings != null
                    ? settings.ToString()
                    : string.Concat(row.ElementsBeforeSelf().Select(e => e.ToString()));
                foreach (string text in want.UpFront)
                {
                    Assert.True(before.Contains(text, StringComparison.Ordinal), want.Id + ": " + text + " is not up front");
                    Assert.False(under.Contains(text, StringComparison.Ordinal), want.Id + ": " + text + " is under More too");
                }
                foreach (string text in want.UnderMore)
                {
                    Assert.True(under.Contains(text, StringComparison.Ordinal), want.Id + ": " + text + " is not under More");
                    Assert.False(before.Contains(text, StringComparison.Ordinal), want.Id + ": " + text + " is up front too");
                }
                // The More row is the last thing in its panel.
                Assert.Empty(row.ElementsAfterSelf());
            }
        }

        /// <summary>Open cards come first in the two groups that mix them
        /// with folded ones.</summary>
        [Fact]
        public void OpenCardsComeBeforeFoldedOnesInGyroAndTouchpad()
        {
            var page = Page();
            // A group is a group box around one panel. A list's fold cards
            // stand where the list does.
            string[] CardsOf(string group) => Assert.Single(page.Descendants().Single(e => e.Name.LocalName == "GroupBox" && Name(e) == group).Elements())
                .Elements().SelectMany(e => e.Name.LocalName == "FoldList" ? e.Elements() : new[] { e })
                .Where(e => e.Name.LocalName == "FoldCard" || ((string)e.Attribute("Style") ?? "").Contains("CardBorder"))
                .Select(e => Name(e) ?? e.Descendants().Where(d => ((string)d.Attribute("Style") ?? "").Contains("CardTitle")).Select(d => Key(d, "Text")).First())
                .ToArray();

            // Grip is the first line of the gyro's list, as its board has it.
            Assert.Equal(new[] { "GyroCalibrationCard", "GyroSensitivityCard", "GyroGripCard", "GyroEngageCard", "GyroResponseCard",
                "MotionSteeringFoldCard", "GyroTiltFoldCard", "GyroPassthroughCard", "GyroSimulationCard", "CompassYawCard" }, CardsOf("TabGyro"));
            Assert.Equal(new[] { "Pad_Touchpad_Mouse_Header", "TouchpadStickFoldCard", "TouchpadPointerFoldCard", "SyntheticPressureFoldCard",
                "SwipeHapticsFoldCard", "GestureDetectionFoldCard", "InBoxGesturesFoldCard", "CustomGesturesFoldCard" }, CardsOf("TabTouchpad"));
        }

        // ── the stick card's two up-front controls ─────────────────────

        [Fact]
        public void OneDeadzoneSetsBothAxes_AndReadsTheLarger()
        {
            var stick = new StickConfigItem(0, "Left Stick");
            var raised = new List<string>();
            stick.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            stick.DeadZoneBoth = 12;
            Assert.Equal(12, stick.DeadZoneX);
            Assert.Equal(12, stick.DeadZoneY);
            Assert.Contains(nameof(StickConfigItem.DeadZoneBoth), raised);
            Assert.Contains(nameof(StickConfigItem.DeadZoneBothDigit), raised);

            // An axis set on its own under More shows in the one slider.
            stick.DeadZoneY = 30;
            Assert.Equal(30, stick.DeadZoneBoth);
            Assert.Equal(12, stick.DeadZoneX);

            stick.DeadZoneBoth = 250;
            Assert.Equal(100, stick.DeadZoneX);
            stick.ResetDeadZoneBothCommand.Execute(null);
            Assert.Equal(0, stick.DeadZoneX);
            Assert.Equal(0, stick.DeadZoneY);
        }

        [Fact]
        public void OneSensitivityCurveSetsBothAxes()
        {
            var stick = new StickConfigItem(1, "Right Stick");
            string linear = stick.PresetNameX;
            Assert.Equal(linear, stick.PresetNameBoth);

            stick.SensitivityCurveBoth = "0,0;0.5,0.2;1,1";
            Assert.Equal("0,0;0.5,0.2;1,1", stick.SensitivityCurveX);
            Assert.Equal("0,0;0.5,0.2;1,1", stick.SensitivityCurveY);
            Assert.Equal(stick.PresetNameX, stick.PresetNameBoth);

            // Two curves that differ follow no one preset.
            stick.SensitivityCurveY = "0,0;1,1";
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Curve_Custom, stick.PresetNameBoth);
            Assert.Equal(stick.SensitivityCurveX, stick.SensitivityCurveBoth);

            stick.ResetSensitivityBothCommand.Execute(null);
            Assert.Equal("0,0;1,1", stick.SensitivityCurveX);
            Assert.Equal("0,0;1,1", stick.SensitivityCurveY);
            Assert.Equal(linear, stick.PresetNameBoth);
        }

        [Fact]
        public void EachStickKeepsItsOwnMoreRow()
        {
            Assert.Equal("Pad.Sticks.Stick0.More", new StickConfigItem(0, "Left Stick").MoreStateKey);
            Assert.Equal("Pad.Sticks.Stick3.More", new StickConfigItem(3, "Stick 4").MoreStateKey);
        }

        // ── what a folded card says at the end of its line ─────────────

        [Fact]
        public void OnOrOff_FromOneFlagOrAny()
        {
            var c = new OnOffTextConverter();
            object Line(params object[] values) => c.Convert(values, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("On", Line(true, "On", "Off"));
            Assert.Equal("Off", Line(false, "On", "Off"));
            // Any of several cues.
            Assert.Equal("On", Line(false, false, true, false, "On", "Off"));
            Assert.Equal("Off", Line(false, false, false, false, "On", "Off"));
            // A number or an enum counts as on when it is not zero.
            Assert.Equal("Off", Line(0, DayOfWeek.Sunday, "On", "Off"));
            Assert.Equal("On", Line(0, DayOfWeek.Monday, "On", "Off"));
            // A binding with no value yet is off, and too few values are nothing.
            Assert.Equal("Off", Line(System.Windows.DependencyProperty.UnsetValue, "On", "Off"));
            Assert.Equal("", Line("On", "Off"));
        }

        [Fact]
        public void ALineOfValues_TakesItsWordsFromTheFormat()
        {
            var c = new FormatTextConverter();
            object Line(params object[] values) => c.Convert(values, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("15° to 135°", Line(15.0, 135.0, "{0}° to {1}°"));
            Assert.Equal("15° bis 135°", Line(15.0, 135.0, "{0}° bis {1}°"));
            Assert.Equal("", Line(System.Windows.DependencyProperty.UnsetValue, 135.0, "{0}° to {1}°"));
            Assert.Equal("", Line(15.0, 135.0, ""));
            Assert.Equal("", Line(15.0, "{0} {1} {2}"));
        }

        /// <summary>A setting picked from a list reads as the name its
        /// list shows, which is the language's, where the stored value is
        /// the same word in every language.</summary>
        [Fact]
        public void APickedSetting_ReadsAsTheNameItsListShows()
        {
            var c = new OptionTextConverter();
            object Line(params object[] values) => c.Convert(values, typeof(string), null, CultureInfo.InvariantCulture);
            string shown = "Zeigend";
            var options = new[]
            {
                new GyroLabeledOption(() => shown, "Pointing"),
                new GyroLabeledOption(() => "Seitlich", "Sideways"),
            };

            Assert.Equal("Zeigend", Line("Pointing", options, "any string"));
            Assert.Equal("Seitlich", Line("Sideways", options, "any string"));
            // The name is looked up when the line is read, so the same
            // option reads in the new language after a change of it.
            shown = "Pointing";
            Assert.Equal("Pointing", Line("Pointing", options, "any string"));
            // A value the list does not hold reads as itself, and a
            // binding with no value yet is nothing.
            Assert.Equal("Upright", Line("Upright", options, "any string"));
            Assert.Equal("", Line(System.Windows.DependencyProperty.UnsetValue, options, "any string"));
            Assert.Equal("Pointing", Line("Pointing", System.Windows.DependencyProperty.UnsetValue, "any string"));
            Assert.Equal("", Line("Pointing"));

            // The page's grip line is bound that way.
            var vm = new PadViewModel(0);
            Assert.Contains(vm.MotionGripOptions, o => o.Value == vm.MotionGrip);
            Assert.Equal(vm.MotionGripOptions.First(o => o.Value == vm.MotionGrip).Display,
                Line(vm.MotionGrip, vm.MotionGripOptions, "any string"));
        }
    }
}
