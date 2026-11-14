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
            // SOCD's title is the board's short one.
            ("SocdFoldCard", "Pad.Output.Socd", "Pad_Socd_FoldTitle", "Pad_Socd_FoldHint"),
            ("KbmSocdFoldCard", "Pad.Output.KbmSocd", "Pad_Socd_FoldTitle", "Pad_Kbm_Socd_FoldHint"),
            ("KeepAwakeFoldCard", "Pad.Output.KeepAwake", "Pad_KeepAwake_Card_Header", "Pad_KeepAwake_FoldHint"),
            ("LockFeedbackCard", "Pad.Sticks.LockFeedback", "Pad_Steering_LockFeedback", "Pad_LockFeedback_FoldHint"),
            ("FlickStickCard", "Pad.Sticks.FlickStick", "Pad_FlickStick_Header", "Pad_FlickStick_FoldHint"),
            ("AudioRumbleFoldCard", "Pad.Rumble.Audio", "Settings_AudioRumble", "Pad_AudioRumble_FoldHint"),
            ("TriggerRoutingFoldCard", "Pad.Rumble.TriggerRouting", "Pad_TriggerRouting_Header", "Pad_TriggerRouting_FoldHint"),
            ("AudioTriggerRumbleFoldCard", "Pad.Rumble.AudioTriggers", "Pad_AudioTriggerRumble_Header", "Pad_AudioTriggerRumble_FoldHint"),
            // Grip's hint is the board's words, the first sentence of its
            // paragraph, which is the line's tooltip.
            ("GyroGripCard", "Pad.Motion.Grip", "Pad_Gyro_Grip_Header", "Pad_Grip_FoldHint"),
            ("GyroEngageCard", "Pad.Motion.Engage", "Pad_Gyro_Engage_Header", "Pad_GyroEngage_FoldHint"),
            ("GyroResponseCard", "Pad.Motion.Response", "Pad_Gyro_Response_Header", "Pad_GyroResponse_FoldHint"),
            ("GyroSpaceCard", "Pad.Motion.Space", "Pad_Gyro_Space_Header", "Pad_GyroSpace_FoldHint"),
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
            // The audio chain's stages under Sound Output. The board draws
            // Crossfeed with no hint, and the Limiter has none either.
            ("CrossfeedFoldCard", "Pad.Sound.Crossfeed", "Pad_Audio_Crossfeed", ""),
            ("ParametricEqFoldCard", "Pad.Sound.Eq", "Pad_Audio_Eq", "Pad_AudioEq_FoldHint"),
            ("LimiterFoldCard", "Pad.Sound.Limiter", "Pad_Audio_Limiter", ""),
            // Sound Macros folds, as the Audio column's rarely used parts do
            // (#574). Its value is how many macros play a sound.
            ("SoundMacrosFoldCard", "Pad.Sound.Macros", "Pad_Audio_Macros_Header", ""),
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
            // A trigger's typed range and raw values (the world-class pass).
            ("Pad_More_Trigger", "{Binding MoreStateKey}", "Pad_More_Trigger",
                new[] { "LowerValue=\"{Binding DeadZone, Mode=TwoWay}\"", "{Binding AntiDeadZone, Mode=TwoWay}", "TriggerPresetCombo",
                        "CurveString=\"{Binding SensitivityCurve, Mode=TwoWay}\"", "RawNorm" },
                new[] { "{Binding DeadZone, Mode=TwoWay, UpdateSourceTrigger=LostFocus}", "{Binding MaxRange, Mode=TwoWay, UpdateSourceTrigger=LostFocus}",
                        "DeadZoneDigit", "MaxRangeDigit", "AntiDeadZoneDigit" }),
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
            ("Pad_More_GyroCalibration", "Pad.Motion.Calibration.More", "Pad_More_GyroCalibration",
                new[] { "GyroCalibrateCommand", "GyroResetCalibrationCommand", "GyroCalibrationLabel" },
                new[] { "GyroLiveRatePitch", "GyroLiveRateYaw", "GyroLiveRateRoll", "AccelLiveX", "AccelLiveY", "AccelLiveZ" }),
            ("Pad_More_GyroSensitivity", "Pad.Motion.Sensitivity.More", "Pad_More_GyroSensitivity",
                new[] { "{Binding GyroSensitivityH, Mode=TwoWay", "{Binding GyroSensitivityV, Mode=TwoWay" },
                new[] { "GyroSensitivityUnits", "GyroInvertPitch", "GyroInvertYaw", "GyroInvertRoll", "GyroRealWorldCalibration" }),
            ("Pad_More_TouchpadMouse", "Pad.Touch.Mouse.More", "Pad_More_TouchpadMouse",
                new[] { "{Binding TouchpadMouseSensitivityX, Mode=TwoWay", "{Binding TouchpadMouseSensitivityY, Mode=TwoWay", "TouchpadMouseJitterReduction" },
                new[] { "TouchpadMouseInvertX", "TouchpadMouseInvertY", "{Binding TouchpadMouseMomentum, Mode=TwoWay", "TouchpadPointerResponse",
                        "TouchpadTrackpadThreshold", "TouchpadMouseAcceleration", "TouchpadMomentumFlingGain", "TouchpadMomentumStacking" }),
            // The Lightbar card has no More row: its board shows Input
            // Reactive up front, under the mode (TheLightbarCard_ShowsInputReactiveUpFront).
            ("Pad_More_SoundOutput", "Pad.Sound.Output.More", "Pad_More_SoundOutput",
                new[] { "{Binding SoundMasterVolume", "DeviceConfig.AudioPassthroughEnabled, Mode=TwoWay" },
                new[] { "SelectedMirrorSourceId", "DeviceConfig.AudioMirrorEngageMode", "DeviceConfig.AudioToneFilterMode", "DeviceConfig.HeadphoneVolume",
                        "DeviceConfig.Ds5AudioBufferLength", "DeviceConfig.AudioOutputPath", "SoundTestCommand" }),
            // A macro opens on what fires it and what it does, as its board
            // draws it. Its hint is a string of its own, the board's words.
            ("Pad_More_Macro", "Pad.Macros.More", "Pad_More_Macro",
                new[] { "{Binding TriggerMode, Mode=TwoWay}", "RecordTriggerCommand", "TriggerInputItems", "SlotMacroTriggerChoicesView", "ActionsList", "AddActionCommand" },
                new[] { "{Binding LayerMask, Mode=TwoWay}", "{Binding TriggerSource, Mode=TwoWay}", "TriggerAxisThreshold", "TriggerAxisDirectionIndex",
                        "{Binding ConsumeTriggerButtons, Mode=TwoWay}", "{Binding RepeatMode, Mode=TwoWay}", "{Binding RepeatCount, Mode=TwoWay}" }),
            // A MIDI slot's card opens on its port and its channel, as its
            // board draws it. Its hint is put together from the rows' names.
            ("Pad_More_Midi", "Pad.Buttons.Midi.More", "Pad_MidiCCCount+Pad_MidiStartCC+Pad_MidiNoteCount+Pad_MidiStartNote+Pad_MidiVelocity",
                new[] { "MidiOutputPortBox", "MidiChannelBox", "ResetMidiAll_Click" },
                new[] { "MidiCcCountBox", "MidiStartCcBox", "MidiNoteCountBox", "MidiStartNoteBox", "MidiVelocityBox" }),
            // A menu opens on its style, what opens it, and its cells or its
            // keyboard, as its board draws it. Its hint is put together too:
            // each row's name after the rule that shows the row, and the
            // overlay's name with none, since its rows always show. For the
            // board's menu, a ring on a slot with layers, that is the
            // board's own line. Icon Packages is a line of its own under
            // the editor's card.
            ("Pad_More_Menu", "Pad.Menus.More",
                "DataContext.HasShiftLayers+Menu_Layer+ShowsLayerRow+Menu_Layer+ShowLayerHold+Menu_LayerHoldsOpen+ShowWebControllerRow+Menu_ShowOnWebController"
                + "+ShowsClickRow+Menu_ClickInput+IsCellMenu+Menu_FireMode+IsRadial+Menu_HasCenter+IsCellMenu+Menu_Deadzone+Dashboard_MenuOverlay",
                new[] { "{Binding KindIndex, Mode=TwoWay}", "KeyboardLayoutIndex", "{Binding Cells}", "Menu_Hotbar_Show", "Menu_HostHalf", "Menu_Keyboard_Controls" },
                new[] { "Menu_Layer", "Menu_ShowOnWebController", "Menu_ClickInput", "Menu_FireMode", "Menu_HasCenter", "Menu_Deadzone_Tip", "Dashboard_MenuOverlay" }),
        };

        /// <summary>A More row's hint: the key of its string, or the keys of
        /// the strings it is put together from, joined by a plus.</summary>
        private static string HintOf(XElement row)
        {
            var composed = row.Elements().FirstOrDefault(e => e.Name.LocalName == "MoreRow.Hint");
            if (composed == null) return Key(row, "Hint");
            return string.Join("+", composed.Descendants().Where(e => e.Name.LocalName == "Binding").Select(e => (string)e.Attribute("Path")));
        }

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
            // The 22 parts that have a Reset All and the 27 that have a
            // description keep them. Grip, Reference Frame, the audio
            // chain's three and Sound Macros have a description and no
            // Reset All: each of their rows has its own reset, Sound
            // Output's Reset All resets the chain too, and Sound Macros
            // holds macros, not settings.
            Assert.Equal(22, cards.Count(c => c.Elements().Any(e => e.Name.LocalName == "FoldCard.HeaderAction")));
            Assert.Equal(27, cards.Count(c => c.Attribute("Description") != null));
            foreach (string name in new[] { "GyroGripCard", "GyroSpaceCard", "CrossfeedFoldCard", "ParametricEqFoldCard", "LimiterFoldCard", "SoundMacrosFoldCard" })
                Assert.Null(cards.Single(c => Name(c) == name).Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.HeaderAction"));
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
            var bound = keys.Where(k => k.StartsWith("{", StringComparison.Ordinal)).ToList();
            var written = keys.Except(bound).ToList();
            Assert.Equal(keys.Count - bound.Count, written.Distinct(StringComparer.Ordinal).Count());
            // The two bound keys are a stick's and a trigger's, each one per
            // card, and no stick's key is a trigger's.
            Assert.Equal(new[] { "{Binding MoreStateKey}", "{Binding MoreStateKey}" }, bound);
            Assert.Equal(new[] { "Pad_More_Stick", "Pad_More_Trigger" }, parts.Where(p => ((string)p.Attribute("StateKey")).StartsWith("{", StringComparison.Ordinal)).Select(Id));
            Assert.Equal("Pad.Triggers.Trigger0.More", new TriggerConfigItem(0, "Left Trigger").MoreStateKey);
            Assert.Equal("Pad.Triggers.Trigger1.More", new TriggerConfigItem(1, "Right Trigger").MoreStateKey);
            Assert.NotEqual(new StickConfigItem(0, "Left Stick").MoreStateKey, new TriggerConfigItem(0, "Left Trigger").MoreStateKey);
            Assert.DoesNotContain(new TriggerConfigItem(0, "Left Trigger").MoreStateKey, written);
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
                Assert.Equal(want.Hint, HintOf(row));

                string under = row.ToString();
                // Up front: what stands before the row in its own panel. A
                // stick's and a trigger's row follows the card's line of
                // columns, and there it is the columns of settings and of
                // charts: the title's summary and the live stick view at
                // the line's end (a Border) read the values under More to
                // show them, and set none.
                var columns = row.ElementsBeforeSelf().LastOrDefault();
                bool ofAStickOrATrigger = want.Id is "Pad_More_Stick" or "Pad_More_Trigger";
                if (ofAStickOrATrigger)
                    Assert.Equal(want.Id == "Pad_More_Stick" ? "ThreePartPanel" : "FlexLinePanel", columns.Name.LocalName);
                string before = ofAStickOrATrigger
                    ? string.Concat(columns.Elements().Where(e => e.Name.LocalName == "StackPanel").Select(e => e.ToString()))
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

        /// <summary>The Lightbar card as its board draws it: the mode, the
        /// mode's own settings, then Input Reactive, all in the card with
        /// no More row. With the overlay off Input Reactive is one row, and
        /// its hold, decay, palette and color show only while it is on, so
        /// the card still opens small.</summary>
        [Fact]
        public void TheLightbarCard_ShowsInputReactiveUpFront()
        {
            var page = Page();
            var card = Assert.Single(page.Descendants().Single(e => Name(e) == "LightbarModeCard").Elements());
            Assert.Equal("StackPanel", card.Name.LocalName);
            Assert.DoesNotContain(card.Descendants(), e => e.Name.LocalName == "MoreRow");
            Assert.DoesNotContain(page.Descendants(), e => Id(e) == "Pad_More_Lightbar");

            XElement Row(string binding) => card.Elements().Single(e => e.Name.LocalName == "SettingRow"
                && e.Elements().Any(c => c.Name.LocalName == "ComboBox" && c.Attributes().Any(a => a.Value.Contains(binding))));
            var mode = Row("DeviceConfig.LightbarMode");
            var reactive = Row("DeviceConfig.InputReactiveMode");
            Assert.Contains(mode, reactive.ElementsBeforeSelf());
            // What Input Reactive opens follows its row and shows by its
            // own state, and the card ends on the line its board writes at
            // its foot.
            var after = reactive.ElementsAfterSelf().ToList();
            Assert.Equal(4, after.Count);
            Assert.Contains("DeviceConfig.IsInputReactiveActive", (string)after[0].Attribute("Visibility"));
            Assert.Contains("DeviceConfig.ShowPaletteForOverlay", (string)after[1].Attribute("Visibility"));
            Assert.Contains("DeviceConfig.IsInputReactiveFixed", (string)after[2].Attribute("Visibility"));
            Assert.Equal("LightbarGamesLine", Name(after[3]));
        }

        /// <summary>The line at the Lightbar card's foot, as its board writes
        /// it: games take over whenever they write lighting. That holds while
        /// the mode is Player Number and Input Reactive is off, where a game's
        /// color stays: any other mode, or the reactive overlay, comes back
        /// after a write's grace, and Off keeps the light dark. It took the
        /// place of a token under the preview that only English read.</summary>
        [Fact]
        public void TheLightbarCardsFootLine_ShowsWhereAGamesColorStays()
        {
            var page = Page();
            var line = page.Descendants().Single(e => Name(e) == "LightbarGamesLine");
            Assert.Equal("TextBlock", line.Name.LocalName);
            Assert.Equal("Pad_Lighting_GamesWin_Hint", Key(line, "Text"));
            Assert.Equal("13", (string)line.Attribute("FontSize"));
            Assert.Equal("{DynamicResource TextFillColorTertiaryBrush}", (string)line.Attribute("Foreground"));
            Assert.Equal("Wrap", (string)line.Attribute("TextWrapping"));
            var style = Assert.Single(Assert.Single(line.Elements(), e => e.Name.LocalName == "TextBlock.Style").Elements());
            Assert.Equal("Collapsed", (string)style.Elements().Single(e => e.Name.LocalName == "Setter").Attribute("Value"));
            var shown = Assert.Single(style.Descendants(), e => e.Name.LocalName == "MultiDataTrigger");
            Assert.Equal(new[] { "{Binding DeviceConfig.LightbarMode}={x:Static vm:LightbarMode.PlayerNumber}", "{Binding DeviceConfig.IsInputReactiveActive}=False" },
                shown.Descendants().Where(e => e.Name.LocalName == "Condition").Select(c => (string)c.Attribute("Binding") + "=" + (string)c.Attribute("Value")));
            Assert.Equal("Visible", (string)Assert.Single(shown.Elements(), e => e.Name.LocalName == "Setter").Attribute("Value"));
            Assert.DoesNotContain("GAME WRITES WIN", ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));
        }

        /// <summary>Reference Frame, as the Motion board draws it: a line of the
        /// gyro's list after Response Shaping, holding the Space row that was
        /// under Sensitivity's More, with the three choices for a hint and no
        /// value at its end. It reads the gyroscope's rate, so it hides on an
        /// accelerometer-only device as the card it came from does.</summary>
        [Fact]
        public void SpaceIsALineOfItsOwn_ReferenceFrame()
        {
            var page = Page();
            var fold = page.Descendants().Single(e => Name(e) == "GyroSpaceCard");
            Assert.Same(page.Descendants().Single(e => Name(e) == "GyroResponseCard"), fold.ElementsBeforeSelf().Last());
            Assert.Null(fold.Elements().SingleOrDefault(e => e.Name.LocalName == "FoldCard.Summary"));
            Assert.Equal("Settings_GyroSpace_Tooltip", Key(fold, "Description"));
            var row = Assert.Single(Assert.Single(fold.Elements(), e => e.Name.LocalName == "StackPanel").Elements());
            Assert.Equal("SettingRow", row.Name.LocalName);
            Assert.Contains("{Binding GyroSpace, Mode=TwoWay}", row.ToString());
            Assert.Contains("{Binding ResetGyroSpaceCommand}", row.ToString());
            Assert.Equal("Settings_GyroSpace", Key(row.Elements().First(), "Text"));
            Assert.DoesNotContain("GyroSpace", page.Descendants().Single(e => Id(e) == "Pad_More_GyroSensitivity").ToString());
            Assert.Contains("if (GyroSpaceCard != null) GyroSpaceCard.Visibility = rateVis;",
                ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml.cs"));
        }

        /// <summary>The audio chain under Sound Output, as the Lights board
        /// draws it: each stage a line of its own in one list right under the
        /// card, Crossfeed ending with its preset and Parametric EQ with On or
        /// Off. The Limiter, the stage the board has no place for, is the
        /// list's third line, also with On or Off. The list shows where the
        /// chain runs, and Sound Output's More keeps none of it.</summary>
        [Fact]
        public void TheAudioChainIsThreeLinesUnderSoundOutput()
        {
            var page = Page();
            var list = page.Descendants().Single(e => Name(e) == "AudioChainList");
            Assert.Equal("FoldList", list.Name.LocalName);
            Assert.Equal(new[] { "CrossfeedFoldCard", "ParametricEqFoldCard", "LimiterFoldCard" }, list.Elements().Select(Name));
            Assert.Equal("{Binding SelectedDeviceHasDspChain, Converter={StaticResource BoolToVisibilityConverter}}", (string)list.Attribute("Visibility"));
            Assert.Equal("12", (string)list.Attribute("Gap"));

            // Right under the Sound Output card, whose More holds none of it.
            var output = list.ElementsBeforeSelf().Last();
            var more = Assert.Single(output.Descendants(), e => Id(e) == "Pad_More_SoundOutput");
            foreach (string gone in new[] { "AudioCrossfeedLevel", "AudioEqEnabled", "AudioLimiterEnabled", "EqCurveControl", "SelectedDeviceHasDspChain" })
                Assert.DoesNotContain(gone, more.ToString());

            XElement Line(string name) => list.Elements().Single(e => Name(e) == name);
            Assert.Contains("{Binding DeviceConfig.AudioCrossfeedLevel, Mode=TwoWay}", Line("CrossfeedFoldCard").ToString());
            Assert.Contains("EqCurveControl", Line("ParametricEqFoldCard").ToString());
            Assert.Contains("{Binding DeviceConfig.AudioLimiterEnabled, Mode=TwoWay}", Line("LimiterFoldCard").ToString());
            // A row that belongs to a line's first row has its label set in.
            foreach (string label in new[] { "Pad_Audio_CrossfeedCut", "Pad_Audio_CrossfeedFeed", "Pad_Audio_EqPreamp", "Pad_Audio_LimiterCeiling" })
                Assert.Equal("{StaticResource DzSubLabel}", (string)list.Descendants().Single(e => e.Name.LocalName == "TextBlock" && Key(e, "Text") == label).Attribute("Style"));

            // The EQ and the Limiter end with On or Off.
            XElement Summary(string name) => Assert.Single(Assert.Single(Line(name).Elements(), e => e.Name.LocalName == "FoldCard.Summary").Elements());
            foreach (var (name, flag) in new[] { ("ParametricEqFoldCard", "DeviceConfig.AudioEqEnabled"), ("LimiterFoldCard", "DeviceConfig.AudioLimiterEnabled") })
            {
                Assert.Equal("{StaticResource OnOffTextConverter}", (string)Summary(name).Attribute("Converter"));
                Assert.Equal(new[] { flag, "Common_On", "Common_Off" }, Summary(name).Elements().Select(b => (string)b.Attribute("Path")));
            }
            // Crossfeed ends with its preset by the name its picker shows, in
            // the picker's order: a string of the table, or one of the two
            // names the picker does not translate.
            var crossfeed = Summary("CrossfeedFoldCard");
            Assert.Equal("{StaticResource IndexTextConverter}", (string)crossfeed.Attribute("Converter"));
            var names = crossfeed.Elements().ToList();
            Assert.Equal("DeviceConfig.AudioCrossfeedLevel", (string)names[0].Attribute("Path"));
            var picker = Line("CrossfeedFoldCard").Descendants().Single(e => e.Name.LocalName == "ComboBox"
                && (string)e.Attribute("SelectedIndex") == "{Binding DeviceConfig.AudioCrossfeedLevel, Mode=TwoWay}");
            string Shown(XElement item) => item.Attribute("Content") != null ? Key(item, "Content")
                : Key(item.Descendants().First(d => d.Name.LocalName == "TextBlock"), "Text");
            Assert.Equal(picker.Elements().Where(e => e.Name.LocalName == "ComboBoxItem").Select(Shown),
                names.Skip(1).Select(b => (string)b.Attribute("Path") ?? (string)b.Attribute("Source")));
            Assert.Equal(10, names.Count - 1);
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

            // Grip is the first line of the gyro's list, and Reference Frame
            // follows Response Shaping, as its board has them.
            Assert.Equal(new[] { "GyroCalibrationCard", "GyroSensitivityCard", "GyroGripCard", "GyroEngageCard", "GyroResponseCard",
                "GyroSpaceCard", "MotionSteeringFoldCard", "GyroTiltFoldCard", "GyroPassthroughCard", "GyroSimulationCard", "CompassYawCard" }, CardsOf("TabGyro"));
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

        /// <summary>The stick card's first rows, as the Sticks board draws
        /// them (#574): the deadzone with one box and its unit, the raw
        /// value staying under More for each axis, and Center before
        /// Calibrate Center. A stick's Range text under More is the board's
        /// two sentences.</summary>
        [Fact]
        public void TheStickCardsFirstRowsAreTheBoards()
        {
            var page = Page();
            var deadzone = Assert.Single(page.Descendants(), e => (string)e.Attribute("AutomationProperties.AutomationId") == "StickDeadZoneBothSlider").Parent;
            Assert.Equal(new[] { "TextBlock", "Slider", "TextBox", "TextBlock", "Button" }, deadzone.Elements().Select(e => e.Name.LocalName));
            Assert.DoesNotContain("DeadZoneBothDigit", ExtrasPageTests.RepoText("PadForge.App", "Views", "PadPage.xaml"));
            var center = Assert.Single(page.Descendants(), e => (string)e.Attribute("Click") == "CalibrateCenter_Click").Parent;
            Assert.Equal("{Binding Pad_Stick_Center, Source={x:Static strings:Strings.Instance}}", (string)center.Elements().First().Attribute("Text"));
            var s = PadForge.Resources.Strings.Strings.Instance;
            Assert.Equal("Center", s.Pad_Stick_Center);
            Assert.Equal("Calibrate Boundary measures the edge the stick can reach and reshapes it to a full circle. The four sliders only cap each direction.",
                s.Pad_Sticks_Section_Range_Hint);
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

        /// <summary>A setting picked by its place in a list reads as the
        /// name at that place: the Indicator LEDs line ends with the LED
        /// brightness that way.</summary>
        [Fact]
        public void ASettingPickedByItsPlace_ReadsAsTheNameAtThatPlace()
        {
            var c = new IndexTextConverter();
            object Line(params object[] values) => c.Convert(values, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("High", Line(PlayerLedBrightness.High, "High", "Medium", "Low"));
            Assert.Equal("Medium", Line(PlayerLedBrightness.Medium, "High", "Medium", "Low"));
            Assert.Equal("Niedrig", Line(PlayerLedBrightness.Low, "Hoch", "Mittel", "Niedrig"));
            Assert.Equal("Medium", Line(1, "High", "Medium", "Low"));
            // A place the list does not have, a binding with no value yet,
            // and too few values are nothing.
            Assert.Equal("", Line(3, "High", "Medium", "Low"));
            Assert.Equal("", Line(-1, "High", "Medium", "Low"));
            Assert.Equal("", Line(System.Windows.DependencyProperty.UnsetValue, "High", "Medium", "Low"));
            Assert.Equal("", Line(0, System.Windows.DependencyProperty.UnsetValue));
            Assert.Equal("", Line(0));

            // The enum counts from zero in the picker's order, which the
            // page's line relies on.
            Assert.Equal(new[] { 0, 1, 2 }, new[] { (int)PlayerLedBrightness.High, (int)PlayerLedBrightness.Medium, (int)PlayerLedBrightness.Low });
            var card = Page().Descendants().Single(e => Name(e) == "IndicatorLedsCard");
            var summary = Assert.Single(Assert.Single(card.Elements(), e => e.Name.LocalName == "FoldCard.Summary").Elements());
            Assert.Equal("{StaticResource IndexTextConverter}", (string)summary.Attribute("Converter"));
            Assert.Equal(new[] { "DeviceConfig.PlayerLedBrightness", "Pad_Lighting_PlayerLed_Brightness_High",
                "Pad_Lighting_PlayerLed_Brightness_Medium", "Pad_Lighting_PlayerLed_Brightness_Low" },
                summary.Elements().Select(b => (string)b.Attribute("Path")));
            // The picker lists the same names in the same order.
            var picker = card.Descendants().Single(e => e.Name.LocalName == "ComboBox"
                && ((string)e.Attribute("SelectedIndex") ?? "").Contains("DeviceConfig.PlayerLedBrightness"));
            Assert.Equal(summary.Elements().Skip(1).Select(b => (string)b.Attribute("Path")),
                picker.Elements().Select(i => Key(i, "Content")));
        }
    }
}
