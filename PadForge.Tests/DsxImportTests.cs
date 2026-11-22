using System;
using System.Linq;
using System.Text;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>#589: every row of the DSX table, against hand-written
    /// profiles in the shape DSX 3.1 and later save, with the keys published
    /// profiles carry.</summary>
    [Collection("SettingsManagerStatics")]
    public class DsxImportTests
    {
        /// <summary>A DSX profile: the general section with the virtual
        /// controller, then the sections a test writes.</summary>
        private static string Dsx(string device = "XBOX_360", string general = "", string sections = "")
            => "{\"profile_type\":\"CONTROLLER\",\"name\":\"Test\",\"controller_general\":{\"ActiveVirtualDevice\":\"" + device + "\""
               + (general.Length > 0 ? "," + general : "") + "}" + (sections.Length > 0 ? "," + sections : "") + "}";

        private static ForeignImportResult Import(string json, string path = "C:/DSX/DSX_Savefile/Configuration Files/Controller Profiles/Test.dsx")
            => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(json), path);

        private static ProfileData Profile(string json)
        {
            var r = Import(json);
            Assert.False(r.Refused, r.RefusedKey);
            return r.Profile;
        }

        private static MappingRow Row(ProfileData p, string target)
            => p.SlotMappingSets.SelectMany(s => s.Rows).FirstOrDefault(r => r.Target == target && r.LayerMask == "Base");

        private static TranslationEntry Line(ForeignImportResult r, string reason)
            => r.Report.Entries.FirstOrDefault(e => e.ReasonKey == reason);

        // ── Detection ───────────────────────────────────────────────────

        [Fact]
        public void Detect_ReadsADsxRootAsDsx_AndAnyOtherJsonAsReWasd()
        {
            Assert.Equal(ForeignConfigKind.Dsx, ForeignConfigReader.Detect(Encoding.UTF8.GetBytes(Dsx())));
            Assert.Equal(ForeignConfigKind.Rewasd, ForeignConfigReader.Detect(Encoding.UTF8.GetBytes("{\"config\":{},\"mappings\":[]}")));
            // controller_general must be a section, as every DSX file writes it.
            Assert.Equal(ForeignConfigKind.Rewasd,
                ForeignConfigReader.Detect(Encoding.UTF8.GetBytes("{\"profile_type\":\"CONTROLLER\",\"controller_general\":3}")));
            Assert.Equal(ForeignConfigKind.Dsx,
                ForeignConfigReader.Detect(Encoding.UTF8.GetBytes("\uFEFF \r\n" + Dsx())));
            Assert.True(ForeignConfigReader.HasForeignExtension("Elden Ring.dsx"));
            Assert.True(ForeignConfigReader.HasForeignExtension("x.DSX"));
        }

        [Fact]
        public void Import_NamesTheProfileAfterDsxsOwnName()
        {
            var r = Import(Dsx().Replace("\"name\":\"Test\"", "\"name\":\"Elden Ring\""), "C:/x/Other.dsx");
            Assert.Equal(ForeignConfigKind.Dsx, r.Kind);
            Assert.Equal("Elden Ring", r.Profile.Name);
            Assert.Equal("DSX", r.ToolName);
        }

        [Fact]
        public void ProfileFolders_LookUnderEverySteamLibrarysDsx()
        {
            string library = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PadForgeDsx" + Guid.NewGuid().ToString("N"));
            string Profiles(params string[] under) => System.IO.Path.Combine(
                new[] { library, "steamapps", "common", "DSX" }.Concat(under)
                    .Concat(new[] { "DSX_Savefile", "Configuration Files", "Controller Profiles" }).ToArray());
            try
            {
                System.IO.Directory.CreateDirectory(Profiles());
                System.IO.Directory.CreateDirectory(Profiles("Main_v3_Beta"));
                var folders = ForeignConfigImporter.DsxProfileFolders(new[] { library, library }).ToList();
                Assert.Equal(2, folders.Count);
                Assert.Contains(Profiles(), folders);
                Assert.Contains(Profiles("Main_v3_Beta"), folders);
                Assert.Empty(ForeignConfigImporter.DsxProfileFolders(new[] { System.IO.Path.Combine(library, "none") }));
            }
            finally
            {
                try { System.IO.Directory.Delete(library, recursive: true); } catch (System.IO.IOException) { }
            }

            var paths = ForeignConfigImporter.LibraryPaths("\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"E:\\\\SteamLibrary\"\n\t}\n}").ToList();
            Assert.Equal(new[] { "C:\\Program Files (x86)\\Steam", "E:\\SteamLibrary" }, paths);
        }

        // ── The virtual controller ──────────────────────────────────────

        [Theory]
        [InlineData("XBOX_360", "", VirtualControllerType.Xbox, "xbox-360-wired")]
        [InlineData("DUALSHOCK_4", "\"ActiveDualShockSubtype\":\"REVISION_1\"", VirtualControllerType.PlayStation, "dualshock-4-v1")]
        [InlineData("DUALSHOCK_4", "\"ActiveDualShockSubtype\":\"REVISION_2\"", VirtualControllerType.PlayStation, "dualshock-4-v2")]
        [InlineData("DUALSENSE", "\"ActiveDualSenseSubtypeV2\":\"REVISION_1_AUDIO_Device\"", VirtualControllerType.PlayStation, "dualsense-composite")]
        [InlineData("DUALSENSE", "\"ActiveDualSenseSubtypeV2\":\"REVISION_1\"", VirtualControllerType.PlayStation, "dualsense")]
        [InlineData("DUALSENSE", "\"ActiveDualSenseSubtype\":\"EDGE_AUDIO_Device\"", VirtualControllerType.PlayStation, "dualsense-edge-composite")]
        public void VirtualController_IsTheOneDsxEmulated(string device, string general, VirtualControllerType type, string id)
        {
            var p = Profile(Dsx(device, general));
            Assert.True(p.SlotCreated[0]);
            Assert.Equal((int)type, p.SlotControllerTypes[0]);
            Assert.Equal(id, p.SlotProfileIds[0]);
            Assert.Equal("Gamepad ButtonA", Assert.Single(Row(p, "ButtonA").Sources).Descriptor);
        }

        [Fact]
        public void NativeMode_MakesNoVirtualController()
        {
            var r = Import(Dsx("NATIVE_MODE"));
            Assert.False(r.Refused);
            Assert.DoesNotContain(r.Profile.SlotControllerTypes.Where((t, i) => r.Profile.SlotCreated[i]),
                t => t == (int)VirtualControllerType.Xbox || t == (int)VirtualControllerType.PlayStation);
            Assert.Null(Row(r.Profile, "ButtonA"));
        }

        // ── Sticks and triggers ─────────────────────────────────────────

        [Fact]
        public void Inversion_TurnsTheAxisRows()
        {
            var p = Profile(Dsx(general: "\"IsLeftAnalogYInverted\":true,\"IsBothAnalogXInverted\":true"));
            Assert.True(Assert.Single(Row(p, "LeftThumbAxisY").Sources).Invert);
            Assert.True(Assert.Single(Row(p, "LeftThumbAxisX").Sources).Invert);
            Assert.True(Assert.Single(Row(p, "RightThumbAxisX").Sources).Invert);
            Assert.False(Assert.Single(Row(p, "RightThumbAxisY").Sources).Invert);
        }

        [Fact]
        public void SwappedSticks_DriveTheOtherSidesOutput()
        {
            var p = Profile(Dsx(sections: "\"controller_sticks\":{\"joysticks_swapped\":true}"));
            Assert.Equal("Gamepad LeftStickX", Assert.Single(Row(p, "RightThumbAxisX").Sources).Descriptor);
            Assert.Equal("Gamepad RightStickY", Assert.Single(Row(p, "LeftThumbAxisY").Sources).Descriptor);
        }

        [Fact]
        public void SimpleRadialDeadZone_IsTheCardsRadialDeadZone()
        {
            var p = Profile(Dsx(sections: "\"controller_deadzone\":{\"LeftStick_DeadzoneMode\":\"SIMPLE_DEADZONE\",\"LeftStick_SimpleDeadzoneMode\":\"RADIAL\",\"LeftStick_SimpleDeadzone\":10,"
                + "\"RightStick_DeadzoneMode\":\"SIMPLE_DEADZONE\",\"RightStick_SimpleDeadzoneMode\":\"AXIAL\",\"RightStick_SimpleDeadzone\":15}"));
            var left = Assert.Single(Row(p, "LeftThumbAxisX").Sources);
            Assert.Equal(2, left.ParamStickDeadZoneShape);
            Assert.Equal(0.10, left.ParamStickDeadZoneInner, 6);
            var right = Assert.Single(Row(p, "RightThumbAxisY").Sources);
            Assert.Equal(1, right.ParamStickDeadZoneShape);
            Assert.Equal(0.15, right.ParamStickDeadZoneInner, 6);
        }

        [Fact]
        public void AdvancedZone_CarriesDeadZoneAntiDeadZoneSaturationAndRange()
        {
            var r = Import(Dsx(sections: "\"controller_deadzone\":{\"LeftStick_DeadzoneMode\":\"ADVANCED_DEADZONE\",\"LeftStick_AdvancedDeadzone_v2\":8,"
                + "\"LeftStick_AdvancedSaturation_v2\":0.95,\"LeftStick_AdvancedRange_v2\":80,\"LeftStick_AdvancedAntiDeadzone_v2\":2,"
                + "\"LeftStick_AdvancedSensitivity_v2\":0,\"LeftStick_AdvancedSmoothAntiDeadzone_v2\":false,\"LeftStick_AdvancedSquareDiagonals_v2\":false}"));
            var src = Assert.Single(Row(r.Profile, "LeftThumbAxisX").Sources);
            Assert.Equal(0.08, src.ParamStickDeadZoneInner, 6);
            Assert.Equal(0.95, src.ParamRangeOuter, 6);
            Assert.Equal(0.02, src.ParamAntiDeadzone, 6);
            // The range scales the output: the stick range macro.
            var range = Assert.Single(r.Profile.Macros, m => m.TriggerMode == MacroTriggerMode.Always
                && m.Actions.Any(a => a.Type == MacroActionType.AxisScale && a.AxisTarget == MacroAxisTarget.LeftStickX));
            Assert.Equal((short)Math.Round(-0.2 * 32767), range.Actions[0].AxisValue);
            var line = Assert.Single(r.Report.Entries, e => e.ReasonKey == ImportReasons.DsxShaping && e.SourcePath == "Left stick zone");
            Assert.Contains("Range", line.Binding);
        }

        [Fact]
        public void TriggerStartAndEnd_AreTheTriggerRowsDeadZoneAndRange()
        {
            var p = Profile(Dsx(sections: "\"controller_deadzone\":{\"lt_start\":10,\"lt_end\":90,\"lt_advanced_mode\":false}"));
            var src = Assert.Single(Row(p, "LeftTrigger").Sources);
            Assert.Equal(1, src.ParamStickDeadZoneShape);
            Assert.Equal(0.10, src.ParamStickDeadZoneInner, 6);
            Assert.Equal(0.90, src.ParamRangeOuter, 6);
            Assert.Equal(0, Assert.Single(Row(p, "RightTrigger").Sources).ParamStickDeadZoneShape);
        }

        // ── Adaptive triggers ───────────────────────────────────────────

        /// <summary>Each mode DSX's profiles key, with the Mod System README's
        /// example values, plays through the #529 encoder: the macro's mode
        /// and values encode, in the encoder's parameter order.</summary>
        [Theory]
        [InlineData("FEEDBACK", "\"rt_feedback_start\":1,\"rt_feedback_resist\":8", TriggerEffectCatalog.ResistanceMode, "1,8")]
        [InlineData("WEAPON", "\"rt_weapon_start\":2,\"rt_weapon_end\":6,\"rt_weapon_resist\":8", TriggerEffectCatalog.WeaponMode, "2,6,8")]
        [InlineData("VIBRATION", "\"rt_vib_start\":1,\"rt_vib_amp\":8,\"rt_vib_freq\":10", TriggerEffectCatalog.VibrationMode, "1,8,10")]
        [InlineData("SLOPE_FEEDBACK", "\"rt_slope_start\":1,\"rt_slope_end\":9,\"rt_slope_resist_start\":8,\"rt_slope_resist_end\":1", TriggerEffectCatalog.SlopeFeedbackMode, "1,9,8,1")]
        [InlineData("MULTIPLE_POSITION_FEEDBACK", "\"rt_multifeedback_resist1\":8,\"rt_multifeedback_resist2\":8,\"rt_multifeedback_resist3\":8,\"rt_multifeedback_resist7\":8", TriggerEffectCatalog.MultiplePositionFeedbackMode, "8,8,8,0,0,0,8,0,0,0")]
        [InlineData("MULTIPLE_POSITION_VIBRATION", "\"rt_multifeedback_freq\":10,\"rt_multifeedback_amp1\":8,\"rt_multifeedback_amp10\":8", TriggerEffectCatalog.MultiplePositionVibrationMode, "10,8,0,0,0,0,0,0,0,0,8")]
        [InlineData("Bow", "\"rt_bow_start\":1,\"rt_bow_end\":8,\"rt_bow_resist\":5,\"rt_bow_snap\":8", TriggerEffectCatalog.BowMode, "1,8,5,8")]
        [InlineData("Galloping", "\"rt_gallop_start\":1,\"rt_gallop_end\":9,\"rt_gallop_foot1\":3,\"rt_gallop_foot2\":6,\"rt_gallop_freq\":4", TriggerEffectCatalog.GallopingMode, "1,9,3,6,4")]
        [InlineData("Machine", "\"rt_machine_start\":1,\"rt_machine_end\":9,\"rt_machine_ampA\":2,\"rt_machine_ampB\":7,\"rt_machine_freq\":10,\"rt_machine_period\":5", TriggerEffectCatalog.MachineMode, "1,9,2,7,10,5")]
        [InlineData("CustomTriggerValue", "\"rt_custom_mode\":\"PulseAB\",\"rt_custom_force1\":40", 12, "8,40,0,0,0,0,0,0")]
        [InlineData("Hardest", "", 6, "")]
        public void TriggerMode_PlaysThroughTheEncoder(string type, string keys, int mode, string values)
        {
            var p = Profile(Dsx(sections: "\"controller_adaptive_triggers\":{\"rt_type\":\"" + type + "\"" + (keys.Length > 0 ? "," + keys : "") + "}"));
            var macro = Assert.Single(p.Macros, m => m.Name == "Right trigger effect");
            Assert.Equal(MacroTriggerMode.Always, macro.TriggerMode);
            var effect = macro.Actions[0];
            Assert.Equal(MacroActionType.TriggerEffect, effect.Type);
            Assert.Equal(MacroTriggerEffectSide.Right, effect.TriggerEffectSide);
            Assert.Equal(mode, effect.TriggerEffectMode);
            Assert.Equal(values, effect.TriggerEffectParameters);
            Assert.Equal(MacroTriggerEffectHold.Duration, effect.TriggerEffectHold);
            Assert.NotNull(TriggerEffectCatalog.Encode(mode, TriggerEffectCatalog.ParseValues(values)));
            Assert.Equal(MacroActionType.Delay, macro.Actions[1].Type);
        }

        [Fact]
        public void TriggerOutsideDsxsRanges_IsSkipped()
        {
            var r = Import(Dsx(sections: "\"controller_adaptive_triggers\":{\"lt_type\":\"WEAPON\",\"lt_weapon_start\":9,\"lt_weapon_end\":6,\"lt_weapon_resist\":8}"));
            Assert.DoesNotContain(r.Profile.Macros, m => m.Name == "Left trigger effect");
            Assert.Equal(TranslationStatus.Skipped, Line(r, ImportReasons.ControllerSetting).Status);
        }

        [Fact]
        public void UnknownTriggerMode_NamesItself()
        {
            var r = Import(Dsx(sections: "\"controller_adaptive_triggers\":{\"lt_type\":\"SOMETHING_NEW\"}"));
            var line = Line(r, ImportReasons.DsxModeName);
            Assert.Equal("SOMETHING_NEW", line.ReasonArgs[0]);
        }

        [Fact]
        public void TriggerPassthrough_OnAVirtualDualSense_LeavesTheTriggersToTheGame()
        {
            var r = Import(Dsx("DUALSENSE", "\"UseDualSenseTriggers\":true",
                "\"controller_adaptive_triggers\":{\"lt_type\":\"FEEDBACK\",\"lt_feedback_start\":1,\"lt_feedback_resist\":8}"));
            Assert.DoesNotContain(r.Profile.Macros, m => m.Actions.Any(a => a.Type == MacroActionType.TriggerEffect));
            Assert.Equal("Left trigger effect", Line(r, ImportReasons.DsxPassthrough).SourcePath);

            // The switch is the virtual DualSense's own: an Xbox one has no
            // game triggers to pass, so the profile's play.
            var xbox = Import(Dsx("XBOX_360", "\"UseDualSenseTriggers\":true",
                "\"controller_adaptive_triggers\":{\"lt_type\":\"FEEDBACK\",\"lt_feedback_start\":1,\"lt_feedback_resist\":8}"));
            Assert.Contains(xbox.Profile.Macros, m => m.Name == "Left trigger effect");
        }

        [Fact]
        public void TriggerPresets_BecomeMacrosToBind()
        {
            var r = Import(Dsx(sections: "\"controller_adaptive_triggers\":{\"lt_type\":\"OFF\",\"rt_type\":\"OFF\"},"
                + "\"controller_adaptive_triggers_active_preset\":0,"
                + "\"controller_adaptive_triggers_preset_list\":[{\"preset_name\":\"Base\",\"lt_type\":\"OFF\",\"rt_type\":\"OFF\"},"
                + "{\"preset_name\":\"Aim\",\"lt_type\":\"OFF\",\"rt_type\":\"WEAPON\",\"rt_weapon_start\":2,\"rt_weapon_end\":6,\"rt_weapon_resist\":8}]"));
            var aim = Assert.Single(r.Profile.Macros, m => m.Name == "Triggers: Aim");
            Assert.True(string.IsNullOrEmpty(aim.TriggerInputs));
            Assert.Equal(MacroActionType.TriggerEffectStop, aim.Actions[0].Type);
            Assert.Equal(MacroTriggerEffectHold.UntilStopped, aim.Actions[1].TriggerEffectHold);
            Assert.Equal(TriggerEffectCatalog.WeaponMode, aim.Actions[1].TriggerEffectMode);
            Assert.DoesNotContain(r.Profile.Macros, m => m.Name == "Triggers: Base");
            Assert.Equal("Aim", Line(r, ImportReasons.DsxPreset).Binding);
        }

        // ── Lights ──────────────────────────────────────────────────────

        [Fact]
        public void StaticColor_IsAHeldColorAtDsxsBrightness()
        {
            var p = Profile(Dsx(sections: "\"controller_led\":{\"colormode\":\"STATIC_COLOR\",\"static_color\":\"#FF0E11DA\",\"brightness\":100}"));
            var bar = Assert.Single(p.Macros, m => m.Name == "Lightbar");
            Assert.Equal(MacroTriggerMode.Always, bar.TriggerMode);
            var color = Assert.Single(bar.Actions);
            Assert.Equal(MacroActionType.LightbarColor, color.Type);
            Assert.Equal(((byte)0x0E, (byte)0x11, (byte)0xDA), (color.LightbarR, color.LightbarG, color.LightbarB));

            var half = Profile(Dsx(sections: "\"controller_led\":{\"colormode\":\"STATIC_COLOR\",\"static_color\":\"#0078D7\",\"brightness\":50}"));
            var dim = Assert.Single(Assert.Single(half.Macros, m => m.Name == "Lightbar").Actions);
            Assert.Equal(((byte)0, (byte)60, (byte)108), (dim.LightbarR, dim.LightbarG, dim.LightbarB));
        }

        [Fact]
        public void Breathing_IsTheTimedLightSteps_AndBatteryTheBatteryLight()
        {
            var r = Import(Dsx(sections: "\"controller_led\":{\"colormode\":\"BREATHING\",\"breathing_color\":\"#0078D7\",\"brightness\":100}"));
            var bar = Assert.Single(r.Profile.Macros, m => m.Name == "Lightbar");
            Assert.Equal(6, bar.Actions.Length);
            Assert.Equal(((byte)0, (byte)0x78, (byte)0xD7), (bar.Actions[4].LightbarR, bar.Actions[4].LightbarG, bar.Actions[4].LightbarB));
            Assert.Equal("Breathing speed", Line(r, ImportReasons.DsxUnit).Binding);

            var battery = Import(Dsx(sections: "\"controller_led\":{\"colormode\":\"BATTERY\",\"battery_color_list\":[\"#FF0000\",\"#FFA500\",\"#FF00FF00\"]}"));
            var action = Assert.Single(Assert.Single(battery.Profile.Macros, m => m.Name == "Lightbar").Actions);
            Assert.Equal(MacroActionType.LightbarModeSet, action.Type);
            Assert.Equal(LightbarMode.Battery, action.LightbarTargetMode);
            Assert.Equal("255,0,0;0,255,0", battery.Profile.SlotMappingSets[0].WorkshopBatteryColors);
            Assert.Equal("Middle battery colors", Line(battery, ImportReasons.NoSuchSetting).Binding);
        }

        [Fact]
        public void LedPassthrough_LeavesTheLightsToTheGame()
        {
            var r = Import(Dsx("DUALSENSE", "\"UseDualSenseLEDs\":true",
                "\"controller_led\":{\"colormode\":\"STATIC_COLOR\",\"static_color\":\"#0078D7\",\"ds_player_mode\":\"THREE\",\"ds_mic_mode\":\"PC_MIC_MUTE_UNMUTE\"}"));
            Assert.DoesNotContain(r.Profile.Macros, m => m.Name == "Lightbar");
            var set = r.Profile.SlotMappingSets[0];
            Assert.Equal("", set.WorkshopPlayerLedMode);
            Assert.Equal("", set.WorkshopMicLedMode);
            Assert.Equal(3, r.Report.Entries.Count(e => e.ReasonKey == ImportReasons.DsxPassthrough));
        }

        [Theory]
        [InlineData("ONE", "Player1")]
        [InlineData("THREE", "Player3")]
        [InlineData("FIVE", "All")]
        [InlineData("ALL_OFF", "Off")]
        public void PlayerLeds_AreTheDevicesPlayerLedMode(string mode, string stamp)
        {
            var p = Profile(Dsx(sections: "\"controller_led\":{\"ds_player_mode\":\"" + mode + "\",\"ds_util_brightness\":\"LOW\"}"));
            Assert.Equal(stamp, p.SlotMappingSets[0].WorkshopPlayerLedMode);
            Assert.Equal("Low", p.SlotMappingSets[0].WorkshopPlayerLedBrightness);
        }

        [Fact]
        public void IndividualPlayerLeds_MatchTheirPattern()
        {
            var p = Profile(Dsx(sections: "\"controller_led\":{\"ds_player_mode\":\"INDIVIDUAL\",\"ds_player_individual_1\":true,\"ds_player_individual_2\":false,"
                + "\"ds_player_individual_3\":true,\"ds_player_individual_4\":false,\"ds_player_individual_5\":true}"));
            Assert.Equal("Player3", p.SlotMappingSets[0].WorkshopPlayerLedMode);
        }

        [Fact]
        public void MicLedFollowingThePcMute_IsFollowDeviceMute()
        {
            var p = Profile(Dsx(sections: "\"controller_led\":{\"ds_mic_mode\":\"PC_MIC_MUTE_UNMUTE\"}"));
            Assert.Equal("FollowDeviceMute", p.SlotMappingSets[0].WorkshopMicLedMode);
            var none = Profile(Dsx(sections: "\"controller_led\":{\"ds_mic_mode\":\"NONE\"}"));
            Assert.Equal("Off", none.SlotMappingSets[0].WorkshopMicLedMode);
        }

        // ── Haptics and volumes ─────────────────────────────────────────

        [Fact]
        public void HeadsetVolume_IsTheDevicesHeadphoneVolume_AndSpeakerVolumeAnAppSetting()
        {
            var r = Import(Dsx(sections: "\"controller_haptics\":{\"output\":{\"speaker_volume\":50,\"headset_volume\":40}}"));
            Assert.Equal("40", r.Profile.SlotMappingSets[0].WorkshopHeadphoneVolume);
            var line = Line(r, ImportReasons.AppSetting);
            Assert.Equal(TranslationStatus.Skipped, line.Status);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Pad_Audio_MasterVolume, line.ReasonArgs[0]);
        }

        [Theory]
        [InlineData("\"controller_haptics\":{\"rumble\":{\"enabled\":false}}", "")]
        [InlineData("", "\"UseXboxRumble\":false")]
        public void RumbleOff_IsARumblePercentOfZero(string sections, string general)
        {
            var p = Profile(Dsx("XBOX_360", general, sections));
            Assert.Equal("0", p.SlotMappingSets[0].WorkshopRumblePercent);
        }

        [Fact]
        public void PersonaHaptics_AreSaidOnlyForAVirtualDualSenseWithAudio()
        {
            var audio = Import(Dsx("DUALSENSE", "\"ActiveDualSenseSubtypeV2\":\"REVISION_1_AUDIO_Device\",\"DualSenseHapticsMode\":\"HAPTICS_TO_RUMBLE\""));
            Assert.NotNull(Line(audio, ImportReasons.DsxHaptics));
            var xbox = Import(Dsx("XBOX_360", "\"DualSenseHapticsMode\":\"HAPTICS_TO_RUMBLE\""));
            Assert.Null(Line(xbox, ImportReasons.DsxHaptics));
        }

        // ── Stick, motion and touchpad modes ────────────────────────────

        [Fact]
        public void StickToMouse_IsMouseMoveRows()
        {
            var r = Import(Dsx(sections: "\"controller_sticks\":{\"ls_mode_updated\":\"STICK_TO_MOUSE\",\"ls_stick_to_mouse_precise_button\":71,\"ls_stick_to_mouse_fast_button\":76}"));
            Assert.Equal("Gamepad LeftStickX", Assert.Single(Row(r.Profile, "KbmMouseX").Sources).Descriptor);
            Assert.Null(Row(r.Profile, "LeftThumbAxisX"));
            Assert.True(r.ReadsMouse);
            Assert.Equal("71, 76", Line(r, ImportReasons.DsxNumber).ReasonArgs[0]);
        }

        [Fact]
        public void StickToScroll_IsWheelRows()
        {
            var p = Profile(Dsx(sections: "\"controller_sticks\":{\"rs_mode_updated\":\"STICK_TO_SCROLL\"}"));
            Assert.Equal(2, Row(p, "KbmScroll").Sources.Count);
            Assert.Equal(2, Row(p, "KbmScrollH").Sources.Count);
            Assert.All(Row(p, "KbmScroll").Sources, s => Assert.True(s.HalfAxis));
        }

        [Fact]
        public void FlickStick_IsTheFlickStickSourceAtDsxsThreshold()
        {
            var p = Profile(Dsx(sections: "\"controller_sticks\":{\"rs_mode_updated\":\"FLICK_STICK\",\"rs_flick_stick_threshold\":0.4}"));
            var src = Assert.Single(Row(p, "KbmMouseX").Sources);
            Assert.Equal("Flick Stick Right", src.Descriptor);
            Assert.Equal(0.4, src.ParamFlickThreshold, 6);
        }

        [Fact]
        public void StickToWasd_IsKeyRows()
        {
            var r = Import(Dsx(sections: "\"controller_sticks\":{\"ls_mode_updated\":\"STICK_TO_WASD\",\"ls_stick_to_wasd_control_scheme\":\"WASD\"}"));
            foreach (byte vk in new byte[] { 0x57, 0x41, 0x53, 0x44 })
                Assert.NotNull(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(vk)));
            var up = Assert.Single(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(0x57)).Sources);
            Assert.Equal("Gamepad LeftStickY", up.Descriptor);
            Assert.True(up.HalfAxis);
            Assert.True(r.ReadsKeyboard);
        }

        [Fact]
        public void StickToControllerButtons_IsDpadRows_AndACustomSchemeIsSkipped()
        {
            var p = Profile(Dsx(sections: "\"controller_sticks\":{\"rs_mode_updated\":\"STICK_TO_CONTROLLER_BUTTONS\",\"rs_stick_to_controller_buttons_control_scheme\":\"D_PAD\"}"));
            Assert.Contains(Row(p, "DPadUp").Sources, s => s.Descriptor == "Gamepad RightStickY" && s.HalfAxis);
            var custom = Import(Dsx(sections: "\"controller_sticks\":{\"rs_mode_updated\":\"STICK_TO_CONTROLLER_BUTTONS\",\"rs_stick_to_controller_buttons_control_scheme\":\"CUSTOM\","
                + "\"right_stick_to_cb_custom_up_direction\":1,\"right_stick_to_cb_custom_down_direction\":2,\"right_stick_to_cb_custom_left_direction\":4,\"right_stick_to_cb_custom_right_direction\":4}"));
            Assert.Equal("1, 2, 4, 4", Line(custom, ImportReasons.DsxNumber).ReasonArgs[0]);
        }

        [Fact]
        public void CustomKeys_ReadAsWindowsKeyNames()
        {
            var r = Import(Dsx(sections: "\"controller_sticks\":{\"ls_mode_updated\":\"STICK_TO_WASD\",\"ls_stick_to_wasd_control_scheme\":\"CUSTOM\","
                + "\"left_stick_to_keyboard_custom_up_direction\":{\"KeyboardMappingType\":\"HOTKEY\",\"ModifierKey\":\"Shift\",\"HotKey\":\"F\"},"
                + "\"left_stick_to_keyboard_custom_down_direction\":{\"KeyboardMappingType\":\"NONE\",\"ModifierKey\":\"None\",\"HotKey\":\"None\"}}"));
            Assert.NotNull(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(0x46)));
            Assert.Equal("Shift + F", Line(r, ImportReasons.DsxKeyName).ReasonArgs[0]);
            Assert.Null(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(0x53)));
        }

        [Fact]
        public void MotionToStick_IsGyroRowsAtDsxsFullSpeed()
        {
            var r = Import(Dsx("DUALSENSE", sections: "\"controller_motion\":{\"motion_mode\":\"MOTION_TO_RIGHT_JOYSTICK\",\"motion_to_right_stick_max_output_speed_deg_s\":250,"
                + "\"motion_is_swap_x_axis\":false,\"motion_is_swap_y_axis\":false}"));
            var x = Row(r.Profile, "RightThumbAxisX").Sources.Single(s => s.Descriptor == "Gyro Yaw");
            Assert.Equal(0.5, x.ParamRangeOuter, 6);
            Assert.False(x.Invert);
            var y = Row(r.Profile, "RightThumbAxisY").Sources.Single(s => s.Descriptor == "Gyro Pitch");
            Assert.True(y.Invert);
            // The real stick still drives it, and the virtual DualSense keeps
            // its motion.
            Assert.Contains(Row(r.Profile, "RightThumbAxisX").Sources, s => s.Descriptor == "Gamepad RightStickX");
            Assert.NotNull(Row(r.Profile, "MotionGyro"));
            Assert.NotNull(Line(r, ImportReasons.DsxMotionAxes));
        }

        [Fact]
        public void MotionToMouse_IsGyroMouseRows_AndASwapTurnsItsAxis()
        {
            var p = Profile(Dsx(sections: "\"controller_motion\":{\"motion_mode\":\"MOTION_TO_MOUSE\",\"motion_is_swap_x_axis\":true}"));
            Assert.Equal("Gyro Yaw", Assert.Single(Row(p, "KbmMouseX").Sources).Descriptor);
            var pitch = Assert.Single(Row(p, "KbmMouseY").Sources);
            Assert.Equal("Gyro Pitch", pitch.Descriptor);
            Assert.True(pitch.Invert);
        }

        [Fact]
        public void TouchpadToMouse_IsTouchpadMouseRows_WithThePressClick()
        {
            var r = Import(Dsx(sections: "\"controller_touchpad\":{\"TouchpadMode\":\"TOUCHPAD_TO_MOUSE\",\"IsTouchpadToMousePressToLeftClickEnabled\":true,"
                + "\"IsTouchpadToMouseTapToLeftClickEnabled\":true}"));
            Assert.Equal("Touchpad 0 Finger 0 X", Assert.Single(Row(r.Profile, "KbmMouseX").Sources).Descriptor);
            Assert.Equal("Touchpad 0 Click", Assert.Single(Row(r.Profile, "KbmMBtn0").Sources).Descriptor);
            Assert.NotNull(Line(r, ImportReasons.TouchpadShape));
        }

        [Fact]
        public void TouchpadPassthrough_FeedsAVirtualDualSensesTouchpad()
        {
            var p = Profile(Dsx("DUALSENSE", sections: "\"controller_touchpad\":{\"TouchpadMode\":\"PASSTHROUGH\"}"));
            Assert.Equal("Touchpad 0 Finger 0 X", Assert.Single(Row(p, "TouchpadX1").Sources).Descriptor);
            Assert.Equal("Touchpad 0 Click", Assert.Single(Row(p, "TouchpadClick").Sources).Descriptor);
            var xbox = Profile(Dsx("XBOX_360", sections: "\"controller_touchpad\":{\"TouchpadMode\":\"PASSTHROUGH\"}"));
            Assert.Null(Row(xbox, "TouchpadX1"));
        }

        // ── Button Mapping ──────────────────────────────────────────────

        /// <summary>The check value of every published binding: FNV-1a over
        /// its inputs as little-endian 16-bit numbers.</summary>
        [Theory]
        [InlineData(new[] { 13 }, 2206074632u)]
        [InlineData(new[] { 108 }, 3912315985u)]
        [InlineData(new[] { 15 }, 3481467866u)]
        [InlineData(new[] { 7 }, 2674862226u)]
        [InlineData(new[] { 76 }, 685893425u)]
        [InlineData(new[] { 41 }, 3688316364u)]
        [InlineData(new[] { 42 }, 1306438919u)]
        [InlineData(new[] { 43 }, 668742302u)]
        [InlineData(new[] { 44 }, 1775226513u)]
        [InlineData(new[] { 71 }, 516984402u)]
        [InlineData(new[] { 6 }, 3312558843u)]
        [InlineData(new[] { 10 }, 2374983655u)]
        [InlineData(new[] { 11 }, 1737287038u)]
        [InlineData(new[] { 17 }, 1268499444u)]
        [InlineData(new[] { 18 }, 3181589295u)]
        [InlineData(new[] { 12 }, 2843771249u)]
        [InlineData(new[] { 8 }, 1099590421u)]
        [InlineData(new[] { 9 }, 461893804u)]
        [InlineData(new[] { 16 }, 1906196061u)]
        [InlineData(new[] { 13, 101 }, 991231413u)]
        public void ComboHash_MatchesEveryPublishedBinding(int[] inputs, uint hash)
            => Assert.Equal(hash, DsxTranslator.ComboHash(inputs));

        private static string Binding(int[] inputs, string activator, string output, bool mute = true, string settings = "null", uint? hash = null)
            => "{\"input\":{\"mode\":\"combo\",\"comboHash\":" + (hash ?? DsxTranslator.ComboHash(inputs)).ToString(System.Globalization.CultureInfo.InvariantCulture)
               + ",\"inputHashes\":[" + string.Join(",", inputs) + "]},\"activators\":{\"" + activator + "\":{\"output\":" + output
               + ",\"muteInput\":" + (mute ? "true" : "false") + ",\"settings\":" + settings + "}}}";

        private static string Lab(params string[] bindings)
            => "\"controller_mapping_lab\":{\"version\":2,\"schema\":\"mapping-lab-test\",\"settings\":{\"actionSystemEnabled\":true},\"bindings\":[" + string.Join(",", bindings) + "]}";

        [Fact]
        public void DpadToAKey_IsAKeyRow_AndAMutedInputLosesItsOwn()
        {
            var r = Import(Dsx(sections: Lab(Binding(new[] { 41 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":16422}"))));
            Assert.Equal("Gamepad DPadUp", Assert.Single(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(0x26)).Sources).Descriptor);
            Assert.Null(Row(r.Profile, "DPadUp"));
            Assert.NotNull(Row(r.Profile, "DPadDown"));
            Assert.Equal(TranslationStatus.Partial, Line(r, ImportReasons.DsxDpad).Status);

            var kept = Import(Dsx(sections: Lab(Binding(new[] { 41 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":16422}", mute: false))));
            Assert.NotNull(Row(kept.Profile, "DPadUp"));
        }

        [Fact]
        public void Turbo_IsATurboMacroAtDsxsDelay()
        {
            var r = Import(Dsx(sections: Lab(Binding(new[] { 42 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":16424}",
                settings: "{\"turboEnabled\":true,\"turboDelayMaxMs\":100,\"turboDurationMinMs\":250}"))));
            var turbo = Assert.Single(r.Profile.Macros, m => m.TriggerMode == MacroTriggerMode.Turbo);
            Assert.Equal(MacroRepeatMode.UntilRelease, turbo.RepeatMode);
            Assert.Equal(100, turbo.RepeatDelayMs);
            Assert.Equal(MacroActionType.KeyPress, Assert.Single(turbo.Actions).Type);
            Assert.Equal(0x28, Assert.Single(turbo.Actions).KeyCode);
            Assert.Equal("100", Line(r, ImportReasons.DsxTurbo).ReasonArgs[0]);
            Assert.Null(Row(r.Profile, "DPadDown"));
        }

        [Fact]
        public void HeldShortcut_WithTheWindowsKey_IsAHoldPair()
        {
            var r = Import(Dsx(sections: Lab(Binding(new[] { 43 }, "Single",
                "{\"mode\":\"hold_until_release\",\"kind\":\"advanced\",\"outputs\":[{\"mappingHash\":16475,\"mode\":\"hold\"},{\"mappingHash\":16452,\"mode\":\"hold\"}]}"))));
            var press = Assert.Single(r.Profile.Macros, m => m.TriggerMode == MacroTriggerMode.OnPress && m.Actions.Any(a => a.Type == MacroActionType.ToggleKey));
            Assert.Equal(MacroLatchDirection.On, press.Actions[0].LatchDirection);
            Assert.Equal(0x5B, press.Actions[0].KeyCode);
            var release = Assert.Single(r.Profile.Macros, m => m.TriggerMode == MacroTriggerMode.OnRelease && m.PairId == press.PairId);
            Assert.All(release.Actions, a => Assert.Equal(MacroLatchDirection.Off, a.LatchDirection));
            // D, a row key, rides a row beside it.
            Assert.NotNull(Row(r.Profile, SteamInputVkTable.KbmKeyTarget(0x44)));
        }

        [Fact]
        public void ProfileSwitch_IsAProfileShortcutOnItsActivator()
        {
            var r = Import(Dsx(sections: Lab(Binding(new[] { 44 }, "Release",
                "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":32773,\"details\":{\"profileSystemTarget\":\"profile:Gyro Aim Base\"}}", mute: false))));
            var macro = Assert.Single(r.Profile.Macros, m => m.Actions.Any(a => a.Type == MacroActionType.ProfileShortcut));
            Assert.Equal(MacroTriggerMode.OnRelease, macro.TriggerMode);
            Assert.Equal(SwitchProfileMode.Specific, macro.Actions[0].ProfileShortcutMode);
            Assert.Equal("Gyro Aim Base", macro.Actions[0].ProfileShortcutName);
        }

        [Fact]
        public void EditedBinding_IsSkipped_AndAnUnpublishedInputSaysWhatItSent()
        {
            var edited = Import(Dsx(sections: Lab(Binding(new[] { 41 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":16422}", hash: 12345))));
            Assert.Equal(TranslationStatus.Skipped, Line(edited, ImportReasons.DsxEdited).Status);
            Assert.NotNull(Row(edited.Profile, "DPadUp"));

            var unknown = Import(Dsx(sections: Lab(Binding(new[] { 7 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":16385}"))));
            var line = Line(unknown, ImportReasons.DsxNumber);
            Assert.Equal("7 Single: Mouse Left", line.Binding);
            Assert.Equal("7", line.ReasonArgs[0]);
        }

        [Fact]
        public void ControllerButtonOutput_IsSkippedWithItsNumber()
        {
            var r = Import(Dsx(sections: Lab(Binding(new[] { 41 }, "Single", "{\"mode\":\"single\",\"kind\":\"mapping\",\"mappingHash\":8}"))));
            var line = Line(r, ImportReasons.DsxNumber);
            Assert.Equal("41 Single: output 8", line.Binding);
            // Nothing came across, so the D-pad keeps its own output.
            Assert.NotNull(Row(r.Profile, "DPadUp"));
        }

        [Theory]
        [InlineData(0x4001, "KbmMBtn0")]
        [InlineData(0x4002, "KbmMBtn1")]
        [InlineData(0x4004, "KbmMBtn2")]
        [InlineData(0x4026, "KbmKey26")]
        [InlineData(0x40AF, "KbmKeyAF")]
        public void Output_IsAVirtualKeyAboveFourThousandHex(int hash, string target)
            => Assert.Equal(target, DsxTranslator.DsxOutput(hash).Target);

        [Theory]
        [InlineData(8)]
        [InlineData(0x8005)]
        [InlineData(0x4000)]
        [InlineData(0x4100)]
        public void Output_OtherwiseIsUnknown(int hash) => Assert.Null(DsxTranslator.DsxOutput(hash));

        // ── The stamps' fold ────────────────────────────────────────────

        [Fact]
        public void DeviceStamps_FoldOntoTheDevicesCards_OnlyOverTheirDefaults()
        {
            var saved = SettingsManager.SlotMappingSets;
            var savedMic = WorkshopTuningApplier.DefaultMicrophone;
            try
            {
                WorkshopTuningApplier.DefaultMicrophone = () => "{0.0.1.00000000}.{test-mic}";
                var sets = new MappingSet[4];
                sets[0] = new MappingSet
                {
                    WorkshopPlayerLedMode = "Player3",
                    WorkshopPlayerLedBrightness = "Low",
                    WorkshopMicLedMode = "FollowDeviceMute",
                    WorkshopHeadphoneVolume = "40",
                };
                SettingsManager.SlotMappingSets = sets;
                Assert.True(WorkshopTuningApplier.HasDeviceStamps(sets[0]));
                var cfg = new DeviceSlotConfig();
                Assert.True(WorkshopTuningApplier.ApplyDeviceStamps(0, cfg));
                Assert.Equal(PlayerLedMode.Player3, cfg.PlayerLedMode);
                Assert.Equal(PlayerLedBrightness.Low, cfg.PlayerLedBrightness);
                Assert.Equal(MicLedMode.FollowDeviceMute, cfg.MicLedMode);
                Assert.Equal("{0.0.1.00000000}.{test-mic}", cfg.MicLedFollowDeviceId);
                Assert.Equal(40, cfg.HeadphoneVolume);
                Assert.False(WorkshopTuningApplier.HasDeviceStamps(sets[0]));

                // A device the user set up keeps its own settings, and the
                // stamps are spent all the same.
                sets[0].WorkshopPlayerLedMode = "Player3";
                sets[0].WorkshopMicLedMode = "Pulse";
                sets[0].WorkshopHeadphoneVolume = "40";
                var chosen = new DeviceSlotConfig { PlayerLedMode = PlayerLedMode.Player1, MicLedMode = MicLedMode.Solid, HeadphoneVolume = 70 };
                Assert.False(WorkshopTuningApplier.ApplyDeviceStamps(0, chosen));
                Assert.Equal(PlayerLedMode.Player1, chosen.PlayerLedMode);
                Assert.Equal(MicLedMode.Solid, chosen.MicLedMode);
                Assert.Equal(70, chosen.HeadphoneVolume);
                Assert.False(WorkshopTuningApplier.HasDeviceStamps(sets[0]));
            }
            finally
            {
                SettingsManager.SlotMappingSets = saved;
                WorkshopTuningApplier.DefaultMicrophone = savedMic;
            }
        }

        [Fact]
        public void DeviceStamps_RideEveryContainerCopy()
        {
            var src = new MappingSet
            {
                WorkshopPlayerLedMode = "All",
                WorkshopPlayerLedBrightness = "Medium",
                WorkshopMicLedMode = "Solid",
                WorkshopHeadphoneVolume = "10",
            };
            var dst = new MappingSet();
            src.CopyWorkshopStampsTo(dst);
            Assert.Equal(("All", "Medium", "Solid", "10"),
                (dst.WorkshopPlayerLedMode, dst.WorkshopPlayerLedBrightness, dst.WorkshopMicLedMode, dst.WorkshopHeadphoneVolume));
        }
    }
}
