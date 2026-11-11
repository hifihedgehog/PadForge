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
            ("SteeringAngleRumbleCard", "Pad.Rumble.SteeringAngle", "Pad_SteeringAngleRumble_Header", "Pad_SteeringAngleRumble_FoldHint"),
            ("ConstantForceFoldCard", "Pad.Rumble.ConstantForce", "Pad_ConstantForce_Header", "Pad_ConstantForce_FoldHint"),
            ("AudioRumbleFoldCard", "Pad.Rumble.Audio", "Settings_AudioRumble", "Pad_AudioRumble_FoldHint"),
            ("TriggerRoutingFoldCard", "Pad.Rumble.TriggerRouting", "Pad_TriggerRouting_Header", "Pad_TriggerRouting_FoldHint"),
            ("ConstantTriggerForceFoldCard", "Pad.Rumble.ConstantTriggerForce", "Pad_ConstantTriggerForce_Header", "Pad_ConstantTriggerForce_FoldHint"),
            ("AudioTriggerRumbleFoldCard", "Pad.Rumble.AudioTriggers", "Pad_AudioTriggerRumble_Header", "Pad_AudioTriggerRumble_FoldHint"),
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
                new[] { "CenterOffsetX", "DeadZoneShapeIndex", "{Binding DeadZoneX, Mode=TwoWay}", "AntiDeadZoneY", "{Binding Linear, Mode=TwoWay}", "MomentumGlide",
                        "PresetNameX", "PresetNameY", "CalibrateBoundaryCommand", "MaxRangeYNeg", "Pad_Steering_Header" }),
            ("Pad_More_Falcon", "Pad.Rumble.Falcon.More", "Pad_More_Falcon",
                new[] { "{Binding FalconFeel, Mode=TwoWay}", "FalconSpring", "FalconForceCap" },
                new[] { "FalconKickButton", "{Binding FalconKick, Mode=TwoWay}", "FalconShake", "FalconBraceButton", "FalconPreloadButton", "FalconGravity" }),
            ("Pad_More_ForceFeedback", "Pad.Rumble.ForceFeedback.More", "Pad_More_ForceFeedback",
                new[] { "ForceOverallGain", "Pad_MotorActivity", "TestRumbleCommand" },
                new[] { "LeftMotorStrength", "RightMotorStrength", "Pad_TestLeftMotor", "Pad_TestRightMotor", "TriggerRumbleFold", "SwapMotors" }),
            ("Pad_More_ImpulseTriggers", "Pad.Rumble.ImpulseTriggers.More", "Pad_More_ImpulseTriggers",
                new[] { "ImpulseOverallGain", "Pad_ImpulseTriggers_MotorActivity", "TestLeftImpulseTriggerCommand", "TestRightImpulseTriggerCommand" },
                new[] { "ImpulseLeftStrength", "ImpulseRightStrength", "ImpulseSwapTriggers", "AtVibrationToImpulse" }),
            ("Pad_More_BassShakers", "Pad.Rumble.BassShakers.More", "Pad_More_BassShakers",
                new[] { "{Binding RumbleAudioEnabled, Mode=TwoWay}" },
                new[] { "RumbleAudioEndpointId", "RumbleAudioChannelMode", "RumbleAudioMasterGain" }),
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
            }
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
                // Up front: what stands before the row in its own panel.
                string before = string.Concat(row.ElementsBeforeSelf().Select(e => e.ToString()));
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
            string[] CardsOf(string group) => page.Descendants().Single(e => e.Name.LocalName == "StackPanel" && Name(e) == group)
                .Elements().Where(e => e.Name.LocalName == "FoldCard" || ((string)e.Attribute("Style") ?? "").Contains("CardBorder"))
                .Select(e => Name(e) ?? e.Descendants().Where(d => ((string)d.Attribute("Style") ?? "").Contains("CardTitle")).Select(d => Key(d, "Text")).First())
                .ToArray();

            Assert.Equal(new[] { "GyroCalibrationCard", "GyroSensitivityCard", "Pad_Gyro_Grip_Header", "GyroEngageCard", "GyroResponseCard",
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
    }
}
