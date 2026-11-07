using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using PadForge.Common.ConfigImport;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.SteamWorkshop.Translation;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>#561: every row of the DS4Windows table, against hand-written
    /// profiles in the shape <c>ProfileDTO.cs</c> writes.</summary>
    [Collection("SettingsManagerStatics")]
    public class Ds4WindowsImportTests
    {
        // Real profiles always write the touchpad's mode, and an absent one
        // means Mouse, so a fixture that is about something else says Controls.
        private static string Xml(string body)
            => "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DS4Windows app_version=\"3.2.19\" config_version=\"5\">"
             + (body.Contains("TouchpadOutputMode", StringComparison.Ordinal) || body.Contains("UseTPforControls", StringComparison.Ordinal)
                 ? "" : "<TouchpadOutputMode>Controls</TouchpadOutputMode>")
             + body + "</DS4Windows>";

        private static ForeignImportResult Import(string body, string path = "C:/DS4Windows/Profiles/Test.xml")
            => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(Xml(body)), path);

        private static ProfileData Profile(string body)
        {
            var r = Import(body);
            Assert.False(r.Refused, r.RefusedKey);
            return r.Profile;
        }

        private static MappingRow Row(ProfileData p, string target, string layer = "Base")
            => p.SlotMappingSets.SelectMany(s => s.Rows).FirstOrDefault(r => r.Target == target && r.LayerMask == layer);

        private static ActionData Only(MacroData m) => Assert.Single(m.Actions);

        private static string Control(string group, string control, string value)
            => $"<Control><{group}><{control}>{value}</{control}></{group}></Control>";

        private static CustomInputState Centered()
        {
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            return state;
        }

        // A row's read of one source, its gate included.
        private static bool ButtonRead(CustomInputState state, MappingSource src)
            => SourceEvaluator.EvaluateForButtonTarget(state, src, 50, 0, "ButtonA", 0, null, 0, "");

        // The left stick pushed to this angle (90 is up) and this far.
        private static void Point(CustomInputState state, double degrees, double distance)
        {
            double r = degrees * Math.PI / 180.0;
            state.Axis[0] = (int)Math.Round(32768 + 32767 * distance * Math.Cos(r));
            state.Axis[1] = (int)Math.Round(32768 - 32767 * distance * Math.Sin(r));
        }

        // ── Output device ───────────────────────────────────────────────

        [Fact]
        public void X360Output_IsAnXboxSlot()
        {
            var p = Profile("<OutputContDevice>X360</OutputContDevice>");
            Assert.Equal((int)VirtualControllerType.Xbox, p.SlotControllerTypes[0]);
            Assert.Equal("xbox-360-wired", p.SlotProfileIds[0]);
        }

        [Fact]
        public void Ds4Output_IsAPlayStationSlot()
        {
            var p = Profile("<OutputContDevice>DS4</OutputContDevice>");
            Assert.Equal((int)VirtualControllerType.PlayStation, p.SlotControllerTypes[0]);
            Assert.Equal("dualshock-4-v2", p.SlotProfileIds[0]);
            Assert.NotNull(HMaestroProfileCatalog.GetProfileById("dualshock-4-v2"));
        }

        [Fact]
        public void FileName_NamesTheProfile()
            => Assert.Equal("Test", Profile("").Name);

        // ── Defaults and control overrides ──────────────────────────────

        [Theory]
        [InlineData("ButtonA", "Gamepad ButtonA")]
        [InlineData("ButtonB", "Gamepad ButtonB")]
        [InlineData("ButtonX", "Gamepad ButtonX")]
        [InlineData("ButtonY", "Gamepad ButtonY")]
        [InlineData("LeftShoulder", "Gamepad LeftShoulder")]
        [InlineData("ButtonBack", "Gamepad ButtonBack")]
        [InlineData("ButtonGuide", "Gamepad ButtonGuide")]
        [InlineData("DPadUp", "Gamepad DPadUp")]
        [InlineData("LeftThumbButton", "Gamepad LeftStick")]
        [InlineData("LeftTrigger", "Gamepad LeftTrigger")]
        [InlineData("RightThumbAxisX", "Gamepad RightStickX")]
        public void EveryControl_StartsOnItsDefaultOutput(string target, string descriptor)
            => Assert.Equal(descriptor, Row(Profile(""), target).Sources[0].Descriptor);

        [Fact]
        public void ButtonValue_MovesTheControl()
        {
            var p = Profile(Control("Button", "Cross", "Left Mouse Button"));
            Assert.Null(Row(p, "ButtonA"));
            Assert.Equal("Gamepad ButtonA", Row(p, "KbmMBtn0").Sources[0].Descriptor);
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[1]);
        }

        [Theory]
        [InlineData("A Button", "ButtonA")]
        [InlineData("Cross", "ButtonA")]
        [InlineData("Y Button", "ButtonY")]
        [InlineData("Triangle", "ButtonY")]
        [InlineData("Left Bumper", "LeftShoulder")]
        [InlineData("L1", "LeftShoulder")]
        [InlineData("Up Button", "DPadUp")]
        [InlineData("Dpad Up", "DPadUp")]
        [InlineData("Back", "ButtonBack")]
        [InlineData("Share", "ButtonBack")]
        [InlineData("Guide", "ButtonGuide")]
        [InlineData("Left Trigger", "LeftTrigger")]
        [InlineData("Middle Mouse Button", "KbmMBtn2")]
        [InlineData("4th Mouse Button", "KbmMBtn3")]
        public void BothNameLists_AreRead(string value, string target)
        {
            var p = Profile(Control("Button", "Square", value));
            Assert.Contains(Row(p, target).Sources, s => s.Descriptor == "Gamepad ButtonX");
        }

        [Theory]
        [InlineData("Left X-Axis-", "LeftThumbAxisX", -1)]
        [InlineData("Left X-Axis+", "LeftThumbAxisX", 1)]
        [InlineData("Left Y-Axis-", "LeftThumbAxisY", -1)]
        [InlineData("Right Y-Axis+", "RightThumbAxisY", 1)]
        [InlineData("Mouse Left", "KbmMouseX", -1)]
        [InlineData("Mouse Down", "KbmMouseY", 1)]
        public void ButtonToAxisDirection_HasTheRightSign(string value, string target, int sign)
        {
            var state = new CustomInputState();
            state.Buttons[2] = true; // X, the Square button
            var p = Profile(Control("Button", "Square", value));
            var src = Row(p, target).Sources.First(s => s.Descriptor == "Gamepad ButtonX");
            Assert.Equal(sign, Math.Sign(SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, "")));
        }

        [Fact]
        public void Wheel_ReadsUpAsInverted()
        {
            var p = Profile(Control("Button", "L1", "Mouse Wheel Up"));
            Assert.True(Row(p, "KbmScroll").Sources[0].Invert);
        }

        [Fact]
        public void Unbound_DropsTheDefaultRow()
        {
            var p = Profile(Control("Button", "Cross", "Unbound"));
            Assert.Null(Row(p, "ButtonA"));
        }

        [Fact]
        public void KeyTypeUnbound_DropsTheDefaultRow()
        {
            var p = Profile("<Control><Key><Cross>32</Cross></Key><KeyType><Cross>Unbound</Cross></KeyType></Control>");
            Assert.Null(Row(p, "ButtonA"));
            Assert.Null(Row(p, "KbmKey20"));
        }

        [Fact]
        public void TouchpadClickOutput_NeedsAPlayStationSlot()
        {
            var x = Import(Control("Button", "Square", "Touchpad Click"));
            Assert.Contains(x.Report.Entries, e => e.ReasonKey == ImportReasons.NoSuchPadButton);
            var ds4 = Profile("<OutputContDevice>DS4</OutputContDevice>" + Control("Button", "Square", "Touchpad Click"));
            Assert.Contains(Row(ds4, "TouchpadClick").Sources, s => s.Descriptor == "Gamepad ButtonX");
        }

        [Fact]
        public void OverridingOneStickDirection_KeepsTheOtherHalf()
        {
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            var p = Profile(Control("Button", "LYNeg", "W Key Stand-In"));
            var src = Assert.Single(Row(p, "LeftThumbAxisY").Sources);
            Assert.True(src.HalfAxis);
            state.Axis[1] = 0; // up: the overridden half
            Assert.Equal(0f, SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, ""), 2);
            state.Axis[1] = 65535; // down: the half that stays
            Assert.True(SourceCoercion.EvaluateForBipolarAxisTarget(state, src, 0, false, "") > 0.9f);
        }

        [Fact]
        public void OverridingBothDirections_RemovesTheAxis()
        {
            var p = Profile("<Control><Key><LYNeg>87</LYNeg><LYPos>83</LYPos></Key></Control>");
            Assert.Null(Row(p, "LeftThumbAxisY"));
            Assert.Equal("Gamepad LeftStickY", Row(p, "KbmKey57").Sources[0].Descriptor);
            Assert.True(Row(p, "KbmKey57").Sources[0].Invert);
            Assert.False(Row(p, "KbmKey53").Sources[0].Invert);
        }

        // ── Keys ────────────────────────────────────────────────────────

        [Fact]
        public void Key_IsARow()
        {
            var p = Profile(Control("Key", "Cross", "32"));
            Assert.Null(Row(p, "ButtonA"));
            Assert.Equal("Gamepad ButtonA", Row(p, "KbmKey20").Sources[0].Descriptor);
        }

        [Fact]
        public void ScanCodeFlag_StillLandsAsARow()
        {
            var p = Profile("<Control><Key><Cross>32</Cross></Key><KeyType><Cross>ScanCode</Cross></KeyType></Control>");
            Assert.NotNull(Row(p, "KbmKey20"));
        }

        [Fact]
        public void ToggleFlag_FlipsTheKey()
        {
            var p = Profile("<Control><Key><Cross>32</Cross></Key><KeyType><Cross>Toggle</Cross></KeyType></Control>");
            var m = Assert.Single(p.Macros, x => x.Actions.Any(a => a.Type == MacroActionType.ToggleKey));
            Assert.Equal(MacroLatchDirection.Toggle, Only(m).LatchDirection);
            Assert.Equal(0x20, Only(m).KeyCode);
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
        }

        [Fact]
        public void KeyWithNoRow_IsAHoldPair()
        {
            var p = Profile(Control("Key", "Cross", "91")); // the Windows key
            Assert.Null(Row(p, "KbmKey5B"));
            var pair = p.Macros.Where(m => m.PairId != 0).ToList();
            Assert.Equal(2, pair.Count);
            Assert.Equal(MacroLatchDirection.On, Only(pair[0]).LatchDirection);
            Assert.Equal(MacroTriggerMode.OnRelease, pair[1].TriggerMode);
        }

        // ── Macros ──────────────────────────────────────────────────────

        private static MacroData MacroOf(string body)
            => Profile(body).Macros.First(m => m.Actions.All(a => a.Type != MacroActionType.LightbarColor || m.Name != "Lightbar"));

        [Fact]
        public void PlainMacro_PlaysOncePerPress()
        {
            var m = MacroOf(Control("Macro", "Cross", "65/65"));
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.Once, m.RepeatMode);
            Assert.Null(Row(Profile(Control("Macro", "Cross", "65/65")), "ButtonA"));
        }

        [Theory]
        [InlineData("Macro,HoldMacro")]
        [InlineData("MacroHoldMacro")]
        [InlineData("MacroRepeatMacro")]
        public void HoldAndRepeatFlags_RunWhileHeld(string keyType)
        {
            var m = MacroOf($"<Control><Macro><Cross>65/65</Cross></Macro><KeyType><Cross>{keyType}</Cross></KeyType></Control>");
            Assert.Equal(MacroTriggerMode.WhileHeld, m.TriggerMode);
            Assert.Equal(MacroRepeatMode.UntilRelease, m.RepeatMode);
        }

        [Fact]
        public void MacroCodes_AlternateDownAndUp_WithWaitsBetween()
        {
            var a = MacroOf(Control("Macro", "Cross", "65/350/65")).Actions;
            Assert.Equal(3, a.Length);
            Assert.Equal(MacroActionType.ToggleKey, a[0].Type);
            Assert.Equal(MacroLatchDirection.On, a[0].LatchDirection);
            Assert.Equal(MacroActionType.Delay, a[1].Type);
            Assert.Equal(50, a[1].DurationMs);
            Assert.Equal(MacroLatchDirection.Off, a[2].LatchDirection);
        }

        [Fact]
        public void PressWithNoWaitBeforeItsRelease_IsOneTap()
        {
            var a = MacroOf(Control("Macro", "Cross", "65/65"));
            Assert.Equal(MacroActionType.KeyPress, Only(a).Type);
        }

        [Fact]
        public void WhatAMacroLeavesHeld_IsReleasedAtItsEnd()
        {
            var a = MacroOf(Control("Macro", "Cross", "65/350/66/350/66")).Actions;
            Assert.Equal(MacroLatchDirection.Off, a[^1].LatchDirection);
            Assert.Equal(65, a[^1].KeyCode);
        }

        [Theory]
        [InlineData(256, MacroActionType.MouseButtonPress)]
        [InlineData(261, MacroActionType.ButtonPress)]
        [InlineData(274, MacroActionType.AxisHold)]
        [InlineData(278, MacroActionType.AxisHold)]
        public void MacroButtonCodes_LowerToTheirOutput(int code, MacroActionType type)
        {
            var a = MacroOf(Control("Macro", "Cross", code + "/" + code));
            Assert.Equal(type, Only(a).Type);
        }

        [Fact]
        public void MacroGamepadCode_NamesTheButtonBit()
        {
            Assert.Equal(Gamepad.A, Only(MacroOf(Control("Macro", "Square", "261/261"))).ButtonFlags);
            Assert.Equal(Gamepad.Y, Only(MacroOf(Control("Macro", "Square", "264/264"))).ButtonFlags);
            Assert.Equal(Gamepad.START, Only(MacroOf(Control("Macro", "Square", "265/265"))).ButtonFlags);
        }

        [Fact]
        public void MacroLightbarCode_HoldsItsColorUntilTheMacroEnds()
        {
            var actions = MacroOf(Control("Macro", "Cross", "1010020030/500")).Actions;
            var a = actions[0];
            Assert.Equal(MacroActionType.LightbarColor, a.Type);
            Assert.Equal(MacroLightbarHoldMode.Sticky, a.LightbarHoldMode);
            // PlayMacroTask resets the color when the macro finishes.
            Assert.Equal(MacroActionType.LightbarColorClear, actions[^1].Type);
            Assert.Equal((byte)10, a.LightbarR);
            Assert.Equal((byte)20, a.LightbarG);
            Assert.Equal((byte)30, a.LightbarB);
        }

        [Fact]
        public void MacroLightbarReset_ClearsIt()
            => Assert.Equal(MacroActionType.LightbarColorClear, Only(MacroOf(Control("Macro", "Cross", "1000000000"))).Type);

        [Fact]
        public void MacroRumbleCode_IsARumble()
        {
            var a = Only(MacroOf(Control("Macro", "Cross", "1255128")));
            Assert.Equal(MacroActionType.Rumble, a.Type);
            Assert.Equal(100, a.RumbleStrengthLeft);
            Assert.Equal(50, a.RumbleStrengthRight);
        }

        [Fact]
        public void MacroRumbleZero_Stops()
            => Assert.Equal(MacroActionType.RumbleStop, Only(MacroOf(Control("Macro", "Cross", "1000000"))).Type);

        [Fact]
        public void CodesBetweenTheTableAndTheWaits_AreNoOps()
        {
            // A public profile holds 299, which DS4Windows reads as a wait of
            // minus one.
            var a = MacroOf(Control("Macro", "Cross", "264/299/264"));
            Assert.Equal(MacroActionType.ButtonPress, Only(a).Type);
        }

        [Fact]
        public void Extras_RunWhileHeld_BesideTheMapping()
        {
            var p = Profile(Control("Extras", "Cross", "255,128,1,10,20,30,0,0,0"));
            Assert.NotNull(Row(p, "ButtonA"));
            var press = p.Macros.First(m => m.Name.EndsWith("extras"));
            Assert.Equal(MacroActionType.Rumble, press.Actions[0].Type);
            Assert.Equal(MacroRumbleHoldMode.Sticky, press.Actions[0].RumbleHoldMode);
            Assert.Equal(100, press.Actions[0].RumbleStrengthLeft);
            Assert.Equal(MacroActionType.LightbarColor, press.Actions[1].Type);
            Assert.Equal((byte)20, press.Actions[1].LightbarG);
            var release = p.Macros.First(m => m.PairId == press.PairId && m.TriggerMode == MacroTriggerMode.OnRelease);
            Assert.Equal(MacroActionType.RumbleStop, release.Actions[0].Type);
            Assert.Equal(MacroActionType.LightbarColorClear, release.Actions[1].Type);
        }

        [Fact]
        public void ExtrasAllZero_AddNothing()
        {
            var p = Profile("<LightbarMode>Passthru</LightbarMode>" + Control("Extras", "Cross", "0,0,0,0,0,0,0,0,0"));
            Assert.Empty(p.Macros);
        }

        [Fact]
        public void ExtrasMouseSensitivity_IsNamed()
        {
            var r = Import(Control("Extras", "Cross", "0,0,0,0,0,0,0,1,50"));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting);
        }

        [Fact]
        public void LightbarMacro_PlaysItsColorsAndTimes()
        {
            var p = Profile("<LightbarMode>Passthru</LightbarMode>" + Control("LightbarMacro", "Cross", "True/255,255,255:100;255,0,0:50;/Press/False"));
            var a = Assert.Single(p.Macros).Actions;
            Assert.Equal(MacroActionType.LightbarColor, a[0].Type);
            Assert.Equal(100, a[1].DurationMs);
            Assert.Equal((byte)0, a[2].LightbarG);
            Assert.Equal(50, a[3].DurationMs);
            Assert.Equal(MacroActionType.LightbarColorClear, a[4].Type);
            Assert.NotNull(Row(p, "ButtonA"));
        }

        [Fact]
        public void InactiveLightbarMacro_AddsNothing()
        {
            var p = Profile("<LightbarMode>Passthru</LightbarMode>" + Control("LightbarMacro", "Cross", "False/255,255,255:100;/Press/False"));
            Assert.Empty(p.Macros);
        }

        // ── Shift ───────────────────────────────────────────────────────

        private const string ShiftOnL1 = """<ShiftControl><Button><Cross Trigger="12">B Button</Cross></Button></ShiftControl>""";

        [Fact]
        public void ShiftControl_IsAHeldLayerOnItsTrigger()
        {
            var p = Profile(ShiftOnL1);
            var act = Assert.Single(p.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Hold", act.Mode);
            Assert.Equal("Shift12", act.LayerMask);
            Assert.Equal("Gamepad LeftShoulder", act.Descriptor);
            Assert.True(act.InheritUnmapped);
            // DS4Windows keeps the trigger's own mapping working while it shifts.
            Assert.True(act.PostponeMapping);
        }

        [Fact]
        public void ShiftedControl_LeavesItsDefault_AndKeepsTheOutputsOwner()
        {
            var p = Profile(ShiftOnL1);
            var layerB = Row(p, "ButtonB", "Shift12");
            Assert.Contains(layerB.Sources, s => s.Descriptor == "Gamepad ButtonA");
            Assert.Contains(layerB.Sources, s => s.Descriptor == "Gamepad ButtonB");
            Assert.True(Row(p, "ButtonA", "Shift12").NoInherit);
            Assert.Null(Row(p, "ButtonX", "Shift12"));
        }

        [Theory]
        [InlineData(1, "Gamepad ButtonA")]
        [InlineData(4, "Gamepad ButtonY")]
        [InlineData(5, "Gamepad ButtonStart")]
        [InlineData(6, "Gamepad ButtonBack")]
        [InlineData(7, "Gamepad DPadUp")]
        [InlineData(11, "Gamepad ButtonGuide")]
        [InlineData(13, "Gamepad RightShoulder")]
        [InlineData(16, "Gamepad LeftStick")]
        [InlineData(18, "Touchpad 0 Click Left")]
        [InlineData(26, "Touchpad 0 Finger 0 Down")]
        [InlineData(27, "Button 11")]
        [InlineData(30, "Gamepad Paddle2")]
        public void ShiftTriggerIndex_NamesItsControl(int index, string descriptor)
        {
            var p = Profile($"""<ShiftControl><Button><Square Trigger="{index}">B Button</Square></Button></ShiftControl>""");
            Assert.Equal(descriptor, Assert.Single(p.SlotMappingSets[0].ShiftActivators).Descriptor);
        }

        [Fact]
        public void TriggerShift_IsAnAxisActivator()
        {
            var p = Profile("""<ShiftControl><Button><Square Trigger="14">B Button</Square></Button></ShiftControl>""");
            var act = Assert.Single(p.SlotMappingSets[0].ShiftActivators);
            Assert.Equal("Axis", act.Kind);
            Assert.Equal("Gamepad LeftTrigger", act.Descriptor);
        }

        [Fact]
        public void TwoTriggers_MakeTwoLayers()
        {
            var p = Profile("""<ShiftControl><Button><Cross Trigger="12">B Button</Cross><Square Trigger="13">Y Button</Square></Button></ShiftControl>""");
            Assert.Equal(2, p.SlotMappingSets[0].ShiftActivators.Count);
        }

        [Fact]
        public void ShiftMacro_ReplacingABaseMacro_MakesTheLayerStopInheriting()
        {
            var p = Profile(Control("Macro", "Cross", "65/65")
                + """<ShiftControl><Macro><Cross Trigger="12">66/66</Cross></Macro></ShiftControl>""");
            Assert.False(Assert.Single(p.SlotMappingSets[0].ShiftActivators).InheritUnmapped);
            Assert.Contains(p.Macros, m => m.LayerMask == "Base" && m.Actions.Any(a => a.KeyCode == 65));
            Assert.Contains(p.Macros, m => m.LayerMask == "Shift12" && m.Actions.Any(a => a.KeyCode == 66));
            Assert.DoesNotContain(p.Macros, m => m.LayerMask == "Shift12" && m.Actions.Any(a => a.KeyCode == 65));
        }

        [Fact]
        public void ShiftUnbound_BlocksOnTheLayerOnly()
        {
            var p = Profile("""<ShiftControl><Button><Cross Trigger="12">Unbound</Cross></Button></ShiftControl>""");
            Assert.NotNull(Row(p, "ButtonA"));
            Assert.True(Row(p, "ButtonA", "Shift12").NoInherit);
        }

        [Fact]
        public void ShiftTriggerPastTheTable_IsNamed()
        {
            var r = Import("""<ShiftControl><Button><Cross Trigger="99">B Button</Cross></Button></ShiftControl>""");
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownInput);
        }

        // ── Stick tuning ────────────────────────────────────────────────

        [Fact]
        public void StickTuning_RidesTheSource()
        {
            var src = Row(Profile("<LSDeadZone>64</LSDeadZone><LSAntiDeadZone>20</LSAntiDeadZone><LSMaxZone>90</LSMaxZone><Sensitivity>2|1|1|1|1|1</Sensitivity>"), "LeftThumbAxisX").Sources[0];
            Assert.Equal(2, src.ParamStickDeadZoneShape);
            Assert.InRange(src.ParamStickDeadZoneInner, 0.50, 0.51);
            Assert.Equal(0.2, src.ParamAntiDeadzone, 3);
            Assert.Equal(0.9, src.ParamRangeOuter, 3);
            Assert.Equal(2.0, src.Sensitivity, 3);
        }

        [Fact]
        public void AxialDeadZone_TakesItsAxisOptions()
        {
            var p = Profile("<LSDeadZoneType>Axial</LSDeadZoneType><LSAxialDeadOptions><DeadZoneX>127</DeadZoneX><DeadZoneY>0</DeadZoneY><MaxZoneX>80</MaxZoneX><AntiDeadZoneY>10</AntiDeadZoneY></LSAxialDeadOptions>");
            var x = Row(p, "LeftThumbAxisX").Sources[0];
            var y = Row(p, "LeftThumbAxisY").Sources[0];
            Assert.Equal(1, x.ParamStickDeadZoneShape);
            Assert.Equal(0.8, x.ParamRangeOuter, 3);
            Assert.Equal(0, y.ParamStickDeadZoneShape);
            Assert.Equal(0.1, y.ParamAntiDeadzone, 3);
        }

        [Fact]
        public void Rotation_IsRowMath_InDs4WindowsOwnFormula()
        {
            var p = Profile("<LSRotation>30</LSRotation>");
            var x = Row(p, "LeftThumbAxisX");
            Assert.Equal("Sum", x.CombineMode);
            var state = new CustomInputState();
            for (int i = 0; i < state.Axis.Length; i++) state.Axis[i] = 32768;
            state.Axis[0] = 65535;
            float xOut = x.Sources.Sum(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s, 0, false, ""));
            float yOut = Row(p, "LeftThumbAxisY").Sources.Sum(s => SourceCoercion.EvaluateForBipolarAxisTarget(state, s, 0, false, ""));
            Assert.InRange(xOut, 0.80f, 0.92f);
            Assert.InRange(yOut, 0.42f, 0.58f);
        }

        // The y a curve stamp gives at x, read from its "x,y;x,y" points.
        private static double CurveAt(string points, double x)
        {
            var pts = points.Split(';').Select(p => p.Split(','))
                .Select(p => (X: double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture),
                              Y: double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))).ToList();
            for (int i = 1; i < pts.Count; i++)
                if (x <= pts[i].X)
                    return pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * (x - pts[i - 1].X) / (pts[i].X - pts[i - 1].X);
            return pts[^1].Y;
        }

        private static string StampFor(ProfileData p, string target)
            => p.SlotMappingSets[0].WorkshopCurves.Split('|').Single(e => e.StartsWith(target + "=", StringComparison.Ordinal))[(target.Length + 1)..];

        [Theory]
        [InlineData("quadratic", 0.25)]
        [InlineData("cubic", 0.125)]
        [InlineData("enhanced-precision", 0.42)]
        [InlineData("easeout-quad", 0.75)]
        [InlineData("easeout-cubic", 0.875)]
        public void CurveNames_CarryTheirExactShape(string mode, double atHalf)
        {
            var r = Import($"<LSOutputCurveMode>{mode}</LSOutputCurveMode>");
            Assert.InRange(CurveAt(StampFor(r.Profile, "LeftThumbAxisX"), 0.5), atHalf - 0.02, atHalf + 0.02);
            Assert.InRange(CurveAt(StampFor(r.Profile, "LeftThumbAxisY"), 0.5), atHalf - 0.02, atHalf + 0.02);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void CustomCurve_IsItsOwnBezier()
        {
            var r = Import("<LSOutputCurveMode>custom</LSOutputCurveMode><LSOutputCurveCustom>0.42, 0.00, 1.00, 1.00</LSOutputCurveCustom>");
            var curve = Ds4WindowsTranslator.BezierCurve("0.42, 0.00, 1.00, 1.00");
            Assert.InRange(CurveAt(StampFor(r.Profile, "LeftThumbAxisX"), 0.5), curve(0.5) - 0.02, curve(0.5) + 0.02);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void Bezier_ReadsTheStrayPercentSign()
            => Assert.NotNull(Ds4WindowsTranslator.BezierCurve("0.00, 1.00, 0.00, 1.0000%"));

        [Fact]
        public void MaxOutput_CapsTheStick()
        {
            var r = Import("<LSMaxOutput>90</LSMaxOutput>");
            string points = StampFor(r.Profile, "LeftThumbAxisX");
            Assert.InRange(CurveAt(points, 1.0), 0.89, 0.91);
            Assert.InRange(CurveAt(points, 0.5), 0.49, 0.51);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void FlickStick_ReplacesTheAxisRowsWithItsCalibration()
        {
            var p = Profile("<RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings><RealWorldCalibration>10</RealWorldCalibration><FlickThreshold>0.8</FlickThreshold><FlickTime>0.2</FlickTime></FlickStickSettings></RSOutputSettings>");
            Assert.Null(Row(p, "RightThumbAxisX"));
            var src = Row(p, "KbmMouseX").Sources[0];
            Assert.Equal("Flick Stick Right", src.Descriptor);
            // ProcessFlickStick: mouse delta = degrees * calibration.
            Assert.Equal(3600.0, src.ParamFlickCountsPer360, 3);
            Assert.Equal(0.8, src.ParamFlickThreshold, 3);
            Assert.Equal(0.2, src.ParamFlickTime, 3);
        }

        // ── Triggers ────────────────────────────────────────────────────

        [Fact]
        public void TriggerTuning_RidesTheSource()
        {
            var src = Row(Profile("<LeftTriggerMiddle>51</LeftTriggerMiddle><L2AntiDeadZone>10</L2AntiDeadZone><L2MaxZone>80</L2MaxZone><L2OutputCurveMode>quadratic</L2OutputCurveMode>"), "LeftTrigger").Sources[0];
            Assert.Equal(1, src.ParamStickDeadZoneShape);
            Assert.Equal(0.2, src.ParamStickDeadZoneInner, 3);
            Assert.Equal(0.1, src.ParamAntiDeadzone, 3);
            Assert.Equal(0.8, src.ParamRangeOuter, 3);
        }

        [Fact]
        public void FullPull_IsAThresholdRead()
        {
            var p = Profile("<L2TwoStageMode>Normal</L2TwoStageMode>" + Control("Button", "L2FullPull", "A Button"));
            var src = Row(p, "ButtonA").Sources.First(s => s.Descriptor == "Gamepad LeftTrigger");
            Assert.Equal(99, src.DeadZone);
            var state = new CustomInputState();
            state.Axis[2] = 65535 / 2;
            Assert.False(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
            state.Axis[2] = 65535;
            Assert.True(SourceCoercion.EvaluateForButtonTarget(state, src, 50, 0, ""));
        }

        [Fact]
        public void ExtrasColor_YieldsToAnActiveLightbarMacro_AndNamesItsFlash()
        {
            var both = Profile("<Control><Extras><Cross>0,0,1,1,2,3,0,0,0</Cross></Extras><LightbarMacro><Cross>True/9,9,9:100;/Press/False</Cross></LightbarMacro></Control>");
            Assert.DoesNotContain(both.Macros, m => m.Name == "Cross extras");
            Assert.Contains(both.Macros, m => m.Name == "Cross lightbar");

            var off = Profile("<Control><Extras><Cross>0,0,1,1,2,3,0,0,0</Cross></Extras><LightbarMacro><Cross>False/9,9,9:100;/Press/False</Cross></LightbarMacro></Control>");
            Assert.Contains(off.Macros, m => m.Name == "Cross extras");

            var flash = Import(Control("Extras", "Cross", "0,0,1,1,2,3,5,0,0"));
            Assert.Contains(flash.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("Flash"));
        }

        [Fact]
        public void SpecialKey_OnATouchMoment_IsOneTap()
        {
            using var f = new Ds4Folder("<Actions>" + Action("K", "Key", "65", trigger: "Touch Ended") + "</Actions>");
            var m = f.Read("<ProfileActions>K</ProfileActions>").Profile.Macros.Single(x => x.Name == "K");
            Assert.Equal(MacroTriggerMode.OnRelease, m.TriggerMode);
            Assert.Equal(MacroActionType.KeyPress, Only(m).Type);
        }

        [Fact]
        public void TriggerSensitivity_ReadsItsOwnPartOfTheString()
        {
            var p = Profile("<Sensitivity>1.5|1.25|2|3|1|1</Sensitivity>");
            Assert.Equal(2.0, Row(p, "LeftTrigger").Sources[0].Sensitivity, 3);
            Assert.Equal(3.0, Row(p, "RightTrigger").Sources[0].Sensitivity, 3);
            Assert.Equal(1.5, Row(p, "LeftThumbAxisX").Sources[0].Sensitivity, 3);
            Assert.Equal(1.25, Row(p, "RightThumbAxisX").Sources[0].Sensitivity, 3);
        }

        [Theory]
        [InlineData("1|1|1|1|1|1", 35)]
        [InlineData("1|1|1|1|2|2", 17)]
        [InlineData("1|1|1|1|0.5|0.5", 99)]
        public void Tilt_ReadsPastWhatDs4WindowsCalls_ScaledByItsSensitivity(string sensitivity, int deadZone)
        {
            var src = Row(Profile("<Sensitivity>" + sensitivity + "</Sensitivity>" + Control("Button", "GyroXPos", "A Button")), "ButtonA")
                .Sources.First(s => s.Descriptor == "Gyro Lean X");
            Assert.Equal(deadZone, src.DeadZone);
        }

        [Theory]
        [InlineData("Mouse", "GyroXPos", false)]
        [InlineData("Controls", "GyroXPos", true)]
        [InlineData("Controls", "GyroSwipeLeft", false)]
        [InlineData("DirectionalSwipe", "GyroSwipeLeft", true)]
        public void AGyroControl_FiresOnlyUnderItsMode(string mode, string control, bool fires)
        {
            var p = Profile("<GyroOutputMode>" + mode + "</GyroOutputMode>" + Control("Button", control, "Y Button"));
            Assert.Equal(fires, Row(p, "ButtonY").Sources.Any(s => s.Descriptor.StartsWith("Gyro", StringComparison.Ordinal)));
        }

        [Fact]
        public void AShiftHeldByATiltThatCannotFire_OpensByNothing()
        {
            // Trigger 22 is Mapping.cs's GyroZNeg.
            var r = Import("<GyroOutputMode>Mouse</GyroOutputMode>"
                + """<ShiftControl><Button><Cross Trigger="22">B Button</Cross></Button></ShiftControl>""");
            var act = r.Profile.SlotMappingSets[0].ShiftActivators.Single(a => a.LayerMask == "Shift22");
            Assert.Equal("Passive", act.Mode);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.LayerNoInput);
        }

        [Theory]
        [InlineData("<UseTPforControls>True</UseTPforControls>", false)]
        [InlineData("<UseTPforControls>False</UseTPforControls>", true)]
        [InlineData("", true)]
        [InlineData("<TouchpadOutputMode>Controls</TouchpadOutputMode>", false)]
        public void TheTouchpadMode_FollowsProfileDtosDefaultsAndItsOlderElement(string element, bool mouse)
        {
            var doc = "<DS4Windows>" + element + "</DS4Windows>";
            var p = ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(doc), "C:/x/Profiles/x.xml").Profile;
            Assert.Equal(mouse, Row(p, "KbmMouseX") != null);
        }

        [Theory]
        [InlineData("<UseSAforMouse>True</UseSAforMouse>", true)]
        [InlineData("<UseSAforMouse>False</UseSAforMouse>", false)]
        [InlineData("", false)]
        [InlineData("<GyroOutputMode>Mouse</GyroOutputMode>", true)]
        public void TheGyroMode_FollowsProfileDtosDefaultsAndItsOlderElement(string element, bool mouse)
        {
            var p = Profile(element);
            Assert.Equal(mouse, Row(p, "KbmMouseX") != null);
        }

        [Fact]
        public void DinputOnly_MakesNoVirtualController_AndKeepsTheKeys()
        {
            var p = Profile("<DinputOnly>True</DinputOnly>" + Control("Key", "Cross", "65"));
            Assert.Equal((int)VirtualControllerType.KeyboardMouse, p.SlotControllerTypes[0]);
            Assert.False(p.SlotCreated[1]);
            Assert.Null(Row(p, "ButtonB"));
            Assert.Equal("Gamepad ButtonA", Row(p, "KbmKey41").Sources[0].Descriptor);
        }

        [Fact]
        public void DinputOnly_WithNothingElse_HasNothingToImport()
            => Assert.False(Import("<DinputOnly>True</DinputOnly>").HasSlots);

        [Theory]
        [InlineData("LSAntiSnapback", "True", true)]
        [InlineData("RSAntiSnapback", "True", true)]
        [InlineData("TouchpadButtonMode", "None", false)]
        [InlineData("TouchDisInvTriggers", "4", false)]
        [InlineData("touchToggle", "False", false)]
        [InlineData("TrackballMode", "True", true)]
        [InlineData("flashBatteryAt", "20", true)]
        [InlineData("ChargingType", "3", true)]
        [InlineData("RumbleAutostopTime", "500", true)]
        [InlineData("LSSquareStick", "True", true)]
        public void WhatNoRowReads_IsNamed(string element, string value, bool padForgeHasNone)
        {
            // A setting PadForge has no counterpart for says so, and one it
            // reads another way says it did not come across.
            var r = Import($"<{element}>{value}</{element}>");
            string reason = padForgeHasNone ? ImportReasons.NoSuchSetting : ImportReasons.ControllerSetting;
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == reason && e.ReasonArgs.Contains(element));
        }

        [Theory]
        [InlineData("LSAntiSnapback", "False")]
        [InlineData("TouchpadButtonMode", "Click")]
        [InlineData("TouchDisInvTriggers", "-1")]
        [InlineData("touchToggle", "True")]
        [InlineData("flashBatteryAt", "0")]
        public void WhatIsAtItsDefault_SaysNothing(string element, string value)
        {
            var r = Import($"<{element}>{value}</{element}>");
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonArgs.Contains(element));
        }

        [Fact]
        public void DeltaAcceleration_IsNamedOnlyWhenOn()
        {
            Assert.Contains(Import("<LSDeltaAccelSettings><Enabled>True</Enabled></LSDeltaAccelSettings>").Report.Entries,
                e => e.ReasonArgs.Contains("LSDeltaAccelSettings"));
            Assert.DoesNotContain(Import("<LSDeltaAccelSettings><Enabled>False</Enabled></LSDeltaAccelSettings>").Report.Entries,
                e => e.ReasonArgs.Contains("LSDeltaAccelSettings"));
        }

        [Fact]
        public void TheGyroSettingsOfAModeInUse_AreNamed_AndOfOneNotInUse_AreNot()
        {
            Assert.Contains(Import("<GyroOutputMode>Mouse</GyroOutputMode><GyroMouseDeadZone>20</GyroMouseDeadZone>").Report.Entries,
                e => e.ReasonArgs.Contains("GyroMouseDeadZone"));
            Assert.DoesNotContain(Import("<GyroOutputMode>Controls</GyroOutputMode><GyroMouseDeadZone>20</GyroMouseDeadZone>").Report.Entries,
                e => e.ReasonArgs.Contains("GyroMouseDeadZone"));
            Assert.Contains(Import("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickMaxOutputEnabled>True</GyroMouseStickMaxOutputEnabled>").Report.Entries,
                e => e.ReasonArgs.Contains("GyroMouseStickMaxOutputEnabled"));
        }

        [Theory]
        [InlineData(1, true, false)]
        [InlineData(2, false, true)]
        public void GyroToStick_InvertBits_ReadXFirst(int bits, bool x, bool y)
        {
            var p = Profile("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>RightStick</GyroMouseStickOutputStick><GyroMouseStickInvert>" + bits + "</GyroMouseStickInvert>");
            Assert.Equal(x, Row(p, "RightThumbAxisX").Sources.First(s => s.Descriptor == "Gyro Yaw").Invert);
            Assert.Equal(y, Row(p, "RightThumbAxisY").Sources.First(s => s.Descriptor == "Gyro Pitch").Invert);
        }

        [Theory]
        [InlineData(2, true, false)]
        [InlineData(1, false, true)]
        public void GyroToMouse_InvertBits_ReadYFirst(int bits, bool x, bool y)
        {
            var p = Profile("<GyroOutputMode>Mouse</GyroOutputMode><GyroInvert>" + bits + "</GyroInvert>");
            Assert.Equal(x, Row(p, "KbmMouseX").Sources.First(s => s.Descriptor == "Gyro Yaw").Invert);
            Assert.Equal(y, Row(p, "KbmMouseY").Sources.First(s => s.Descriptor == "Gyro Pitch").Invert);
        }

        [Theory]
        [InlineData("", "Gyro Yaw", 89, 89)]
        [InlineData("<GyroSwipeSettings><DeadZoneX>45</DeadZoneX><DeadZoneY>9</DeadZoneY></GyroSwipeSettings>", "Gyro Yaw", 50, 10)]
        [InlineData("<GyroSwipeSettings><XAxis>Roll</XAxis></GyroSwipeSettings>", "Gyro Roll", 89, 89)]
        public void GyroSwipes_ReadPastTheFilesSpeed(string settings, string xAxis, int x, int y)
        {
            var p = Profile("<GyroOutputMode>DirectionalSwipe</GyroOutputMode>" + settings
                + "<Control><Button><GyroSwipeLeft>A Button</GyroSwipeLeft><GyroSwipeUp>B Button</GyroSwipeUp></Button></Control>");
            var left = Row(p, "ButtonA").Sources.First(s => s.Descriptor == xAxis);
            var up = Row(p, "ButtonB").Sources.First(s => s.Descriptor == "Gyro Pitch");
            Assert.Equal(x, left.DeadZone);
            Assert.Equal(y, up.DeadZone);
            Assert.True(left.HalfAxis && !left.Invert);
        }

        [Theory]
        [InlineData("Normal", "")]
        [InlineData("HairTrigger", "Stage HairTrigger Soft 0 1 39 Gamepad LeftTrigger")]
        [InlineData("ExclusiveButtons", "Between 39 99 00 Gamepad LeftTrigger")]
        public void ATriggerAsAButton_PressesPast100_UnderItsTwoStageMode(string mode, string gate)
        {
            // GetBoolMapping presses a trigger past 100 of 255 for a pad
            // button, and the two-stage mode decides when the soft pull's
            // binding runs at all.
            var src = Row(Profile("<L2TwoStageMode>" + mode + "</L2TwoStageMode>" + Control("Button", "L2", "A Button")), "ButtonA")
                .Sources.First(s => s.Descriptor == "Gamepad LeftTrigger");
            Assert.Equal(39, src.DeadZone);
            Assert.Equal(gate, src.GateDescriptor);
        }

        [Fact]
        public void ATriggerAsAKey_PressesPastItsDeadZone()
        {
            // GetBoolActionMapping reads a trigger as pressed past 0, after
            // its dead zone.
            var src = Row(Profile("<LeftTriggerMiddle>51</LeftTriggerMiddle>" + Control("Key", "L2", "65")), "KbmKey41")
                .Sources.First(s => s.Descriptor == "Gamepad LeftTrigger");
            Assert.Equal(20, src.DeadZone);
        }

        [Fact]
        public void AStickDirectionAsAKey_ReadsItsSector()
        {
            // GetBoolActionMapping: left reads from 112.5 to 247.5 degrees,
            // past the dead zone.
            var src = Row(Profile("<LSDeadZone>13</LSDeadZone>" + Control("Key", "LXNeg", "65")), "KbmKey41")
                .Sources.First(s => s.Descriptor == "Gamepad LeftStickX");
            Assert.Equal(BoundedReads.Sector(180, 67.5, 11, "Gamepad LeftStickRing"), src.GateDescriptor);
            var state = Centered();
            Point(state, 150, 0.6);
            Assert.True(ButtonRead(state, src));
            Point(state, 100, 0.6);
            Assert.False(ButtonRead(state, src));
            Point(state, 180, 0.05);
            Assert.False(ButtonRead(state, src));
            Point(state, 240, 0.6);
            Assert.True(ButtonRead(state, src));
        }

        [Fact]
        public void AStickDirectionAsAPadButton_ReadsPastAFixedPress()
        {
            // GetBoolMapping: a direction is pressed past 55 of 127.
            var src = Row(Profile(Control("Button", "LXNeg", "A Button")), "ButtonA")
                .Sources.First(s => s.Descriptor == "Gamepad LeftStickX");
            Assert.Equal(43, src.DeadZone);
            Assert.Equal("", src.GateDescriptor);
        }

        [Fact]
        public void FullPullWithTwoStageOff_IsLeftOut()
        {
            // Mapping.cs passes a full pull binding over in Disabled, the default.
            var p = Profile(Control("Button", "L2FullPull", "B Button"));
            Assert.DoesNotContain(Row(p, "ButtonB").Sources, s => s.Descriptor == "Gamepad LeftTrigger");
        }

        [Theory]
        [InlineData("HipFire", "Stage HipFire Soft 120 1 1 Gamepad LeftTrigger")]
        [InlineData("HipFireExclusiveButtons", "Stage HipFireExclusive Soft 120 1 1 Gamepad LeftTrigger")]
        [InlineData("HairTrigger", "Stage HairTrigger Soft 0 1 1 Gamepad LeftTrigger")]
        [InlineData("ExclusiveButtons", "Between 1 99 00 Gamepad LeftTrigger")]
        public void TwoStageModes_HoldTheTriggerBack(string mode, string gate)
        {
            var r = Import("<L2TwoStageMode>" + mode + "</L2TwoStageMode><L2HipFireTime>120</L2HipFireTime>");
            var src = Row(r.Profile, "LeftTrigger").Sources.First(s => s.Descriptor == "Gamepad LeftTrigger");
            Assert.Equal(gate, src.GateDescriptor);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void HipFireWait_AddsTheDebounce()
        {
            var src = Row(Profile("<L2TwoStageMode>HipFire</L2TwoStageMode><L2HipFireTime>100</L2HipFireTime><DebouncingMs>20</DebouncingMs>"), "LeftTrigger")
                .Sources.First(s => s.Descriptor == "Gamepad LeftTrigger");
            Assert.Equal(120, BoundedReads.Parse(src.GateDescriptor).WaitMs);
        }

        [Fact]
        public void LightbarMacro_RunsOnlyOnTheControlsDs4WindowsRunsItFor()
        {
            Assert.DoesNotContain(Profile(Control("LightbarMacro", "L2", "True/9,9,9:100;/Press/False")).Macros, m => m.Name.EndsWith("lightbar"));
            Assert.Contains(Profile(Control("LightbarMacro", "Square", "True/9,9,9:100;/Press/False")).Macros, m => m.Name.EndsWith("lightbar"));
        }

        [Fact]
        public void VerticalScale_ScalesTheYRowOnly()
        {
            var p = Profile("<LSVerticalScale>50</LSVerticalScale>");
            Assert.Equal(0.5, Row(p, "LeftThumbAxisY").Sources[0].Sensitivity, 3);
            Assert.Equal(1.0, Row(p, "LeftThumbAxisX").Sources[0].Sensitivity, 3);
            Assert.Equal(1.0, Row(p, "RightThumbAxisY").Sources[0].Sensitivity, 3);
        }

        [Fact]
        public void MultiAction_IsTapHoldAndDoubleTap_OnItsFirstTrigger()
        {
            using var f = new Ds4Folder("<Actions>" + Action("M", "MultiAction", "65/65,66/66,67/67", trigger: "Cross/L1") + "</Actions>");
            var macros = f.Read("<ProfileActions>M</ProfileActions>").Profile.Macros;
            var tap = macros.First(m => m.Name == "M (tap)");
            var hold = macros.First(m => m.Name == "M (hold)");
            var twice = macros.First(m => m.Name == "M (double tap)");
            Assert.Equal(MacroTriggerMode.SinglePress, tap.TriggerMode);
            Assert.Equal(MacroTriggerMode.HoldForMs, hold.TriggerMode);
            Assert.Equal(500, hold.TriggerHoldMs);
            Assert.Equal(MacroTriggerMode.DoublePress, twice.TriggerMode);
            Assert.Equal(300, twice.TriggerDoublePressMs);
            Assert.Equal(300, tap.TriggerDoublePressMs);
            Assert.Equal(65, Only(tap).KeyCode);
            Assert.Equal(66, Only(hold).KeyCode);
            Assert.Equal(67, Only(twice).KeyCode);
            Assert.All(new[] { tap, hold, twice }, m => Assert.Single(m.TriggerInputs.Split('|')));
        }

        [Theory]
        [InlineData("65/65,66/66,,", MacroTriggerMode.ShortPress, 2)]
        [InlineData("65/65,,67/67", MacroTriggerMode.SinglePress, 2)]
        [InlineData("65/65,,", MacroTriggerMode.OnPress, 1)]
        public void MultiAction_TheTapWaitsOnlyForWhatIsBesideIt(string details, MacroTriggerMode tapMode, int count)
        {
            using var f = new Ds4Folder("<Actions>" + Action("M", "MultiAction", details) + "</Actions>");
            var macros = f.Read("<ProfileActions>M</ProfileActions>").Profile.Macros;
            Assert.Equal(tapMode, macros.First(m => m.Name == "M (tap)").TriggerMode);
            Assert.Equal(count, macros.Count(m => m.Name.StartsWith("M (", StringComparison.Ordinal)));
        }

        [Fact]
        public void GameDvr_PlaysItsShortcuts()
        {
            using var f = new Ds4Folder("<Actions>" + Action("D", "XboxGameDVR", "0,1,3,70") + "</Actions>");
            var macros = f.Read("<ProfileActions>D</ProfileActions>").Profile.Macros;
            var tap = macros.First(m => m.Name == "D (tap)").Actions;
            Assert.Equal(91, tap[0].KeyCode);
            Assert.Equal(71, tap[1].KeyCode);
            Assert.Equal(70, Only(macros.First(m => m.Name == "D (double tap)")).KeyCode);
            Assert.Contains(macros.First(m => m.Name == "D (hold)").Actions, a => a.KeyCode == 82);
        }

        [Fact]
        public void GyroCalibration_Recenters()
        {
            using var f = new Ds4Folder("<Actions>" + Action("G", "GyroCalibration", "") + "</Actions>");
            Assert.Equal(MacroActionType.GyroRecenter, Only(f.Read("<ProfileActions>G</ProfileActions>").Profile.Macros.First(m => m.Name == "G")).Type);
        }

        [Fact]
        public void WheelCalibration_RecentersTheLean()
        {
            using var f = new Ds4Folder("<Actions>" + Action("W", "SASteeringWheelEmulationCalibrate", "") + "</Actions>");
            var r = f.Read("<ProfileActions>W</ProfileActions>");
            Assert.Equal(MacroActionType.GyroRecenter, Only(r.Profile.Macros.First(m => m.Name == "W")).Type);
        }

        [Theory]
        [InlineData("TouchStarted", MacroTriggerMode.OnPress)]
        [InlineData("TouchEnded", MacroTriggerMode.OnRelease)]
        public void TouchStartAndEnd_AreMoments(string control, MacroTriggerMode mode)
        {
            var key = Profile(Control("Key", control, "65"));
            Assert.Null(Row(key, "KbmKey41"));
            Assert.Equal(mode, key.Macros.First(m => m.Name == "A").TriggerMode);
            Assert.Equal(MacroActionType.KeyPress, Only(key.Macros.First(m => m.Name == "A")).Type);

            var button = Profile(Control("Button", control, "A Button"));
            Assert.Equal(mode, button.Macros.First(m => m.Name == "A").TriggerMode);

            var macro = Profile(Control("Macro", control, "65/65"));
            Assert.Equal(mode, macro.Macros.First(m => m.Name == control).TriggerMode);
        }

        /// <summary>Every element of a profile that uses most of the format,
        /// given text of the wrong shape in turn.</summary>
        [Fact]
        public void AnyElementWithTheWrongText_NeverThrows()
        {
            string rich = Xml("<OutputContDevice>DS4</OutputContDevice><Color>1,2,3</Color><Rainbow>0</Rainbow><LSDeadZone>10</LSDeadZone><LSDeadZoneType>Axial</LSDeadZoneType><LSAxialDeadOptions><DeadZoneX>1</DeadZoneX><MaxZoneY>90</MaxZoneY></LSAxialDeadOptions><LSRotation>10</LSRotation><LSVerticalScale>50</LSVerticalScale><LSOutputCurveMode>custom</LSOutputCurveMode><LSOutputCurveCustom>.1,.2,.3,.4</LSOutputCurveCustom><RSOutputMode>FlickStick</RSOutputMode><RSOutputSettings><FlickStickSettings><RealWorldCalibration>5</RealWorldCalibration><FlickThreshold>0.9</FlickThreshold><FlickTime>0.1</FlickTime></FlickStickSettings></RSOutputSettings><Sensitivity>1|1|1|1|1|1</Sensitivity><L2TwoStageMode>HipFireExclusiveButtons</L2TwoStageMode><L2HipFireTime>100</L2HipFireTime><L2TriggerEffect>Rigid</L2TriggerEffect><LeftTriggerMiddle>10</LeftTriggerMiddle><L2MaxOutput>50</L2MaxOutput><GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickTriggers>0,1</GyroMouseStickTriggers><GyroMouseStickInvert>1</GyroMouseStickInvert><TouchpadOutputMode>Mouse</TouchpadOutputMode><TouchpadInvert>1</TouchpadInvert><touchSensitivity>100</touchSensitivity><scrollSensitivity>1</scrollSensitivity><LaunchProgram>x.exe</LaunchProgram><ProfileActions>M</ProfileActions>"
                + "<Control><Button><Cross>A Button</Cross><LXNeg>Unbound</LXNeg></Button><Key><Circle>65</Circle></Key><Macro><Square>65/65/350/1010020030/1001002</Square></Macro><KeyType><Square>HoldMacro</Square><Circle>Toggle</Circle></KeyType><Extras><Triangle>1,2,1,4,5,6,0,1,9</Triangle></Extras><LightbarMacro><L1>True/1,2,3:100;/Press/False</L1></LightbarMacro></Control>"
                + "<ShiftControl><Button><Cross Trigger=\"5\">B Button</Cross></Button></ShiftControl>");
            var root = System.Xml.Linq.XDocument.Parse(rich);
            int count = root.Descendants().Count();
            Assert.True(count > 40, count.ToString());

            foreach (string wrong in new[] { "", "x", "-1", "99999999999999999999", "1e308", "NaN", "a,b,c", "1/2/3", "/", "|", ",", ":;", "True", "True/a/b/c" })
            {
                for (int i = 0; i < count; i++)
                {
                    var copy = System.Xml.Linq.XDocument.Parse(rich);
                    var element = copy.Descendants().ElementAt(i);
                    if (element.HasElements) element.Add(new System.Xml.Linq.XAttribute("Trigger", wrong));
                    else element.Value = wrong;
                    var bytes = Encoding.UTF8.GetBytes(copy.ToString());
                    var ex = Record.Exception(() => ForeignConfigImporter.TranslateCore(bytes, "C:/x/Profiles/x.xml"));
                    Assert.True(ex == null, element.Name + " = " + wrong + ": " + ex);
                }
            }
        }

        [Theory]
        [InlineData(new byte[] { })]
        [InlineData(new byte[] { 0xFF, 0xFE })]
        [InlineData(new byte[] { 0xEF, 0xBB, 0xBF })]
        [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 })]
        [InlineData(new byte[] { 0x3C, 0x3F, 0x78, 0x6D, 0x6C, 0x20, 0x65, 0x6E, 0x63, 0x6F, 0x64, 0x69, 0x6E, 0x67, 0x3D, 0x22, 0x71, 0x22, 0x3F, 0x3E })]
        public void BytesThatAreNoFile_AreRefused(byte[] bytes)
        {
            var r = ForeignConfigImporter.TranslateCore(bytes, "x.xml");
            Assert.True(r.Refused);
        }

        [Fact]
        public void KeepKeyState_LeavesWhatTheMacroHolds()
        {
            using var f = new Ds4Folder("<Actions>"
                + Action("Keep", "Macro", "65/1010020030/300", "KeepKeyState", "Cross")
                + Action("Plain", "Macro", "66/1010020030/300", "", "Circle") + "</Actions>");
            var macros = f.Read("<ProfileActions>Keep/Plain</ProfileActions>").Profile.Macros;

            var kept = macros.First(m => m.Name == "Keep").Actions;
            Assert.Contains(kept, a => a.Type == MacroActionType.ToggleKey && a.KeyCode == 65 && a.LatchDirection == MacroLatchDirection.On);
            Assert.DoesNotContain(kept, a => a.LatchDirection == MacroLatchDirection.Off);
            Assert.DoesNotContain(kept, a => a.Type == MacroActionType.LightbarColorClear);

            var plain = macros.First(m => m.Name == "Plain").Actions;
            Assert.Contains(plain, a => a.Type == MacroActionType.ToggleKey && a.KeyCode == 66 && a.LatchDirection == MacroLatchDirection.Off);
            Assert.Equal(MacroActionType.LightbarColorClear, plain[^1].Type);
        }

        [Fact]
        public void KeepKeyState_OnARepeatGetsNoReleaseTwin()
        {
            using var f = new Ds4Folder("<Actions>" + Action("M", "Macro", "65/350", "Repeat,KeepKeyState", "Cross") + "</Actions>");
            var macros = f.Read("<ProfileActions>M</ProfileActions>").Profile.Macros.Where(m => m.Name.StartsWith("M", StringComparison.Ordinal)).ToList();
            Assert.Equal(MacroTriggerMode.WhileHeld, Assert.Single(macros).TriggerMode);
        }

        [Fact]
        public void TouchpadClickPassthrough_KeepsTheClick()
        {
            var ds4 = Profile("<OutputContDevice>DS4</OutputContDevice><TouchpadOutputMode>Controls</TouchpadOutputMode><TouchpadClickPassthru>True</TouchpadClickPassthru>");
            Assert.Equal("Touchpad 0 Click", Row(ds4, "TouchpadClick").Sources[0].Descriptor);
            Assert.Null(Row(ds4, "TouchpadX1"));
        }

        [Fact]
        public void MacroTouchpadClickCode_IsNamed_BecauseAMacroHasNoBitForIt()
        {
            var r = Import("<OutputContDevice>DS4</OutputContDevice>" + Control("Macro", "Cross", "286/286"));
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.UnknownOutput && e.ReasonArgs.Contains("286"));
        }

        [Fact]
        public void Utf16Profile_Reads()
        {
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(
                """<?xml version="1.0" encoding="utf-16"?><DS4Windows config_version="5"><Color>1,2,3</Color></DS4Windows>""")).ToArray();
            Assert.False(ForeignConfigImporter.Translate(bytes, "x.xml").Refused);
        }

        [Theory]
        [InlineData("Rigid", TriggerEffectCatalog.ResistanceMode)]
        [InlineData("FullClick", TriggerEffectCatalog.WeaponMode)]
        [InlineData("Pulse", TriggerEffectCatalog.VibrationMode)]
        public void TriggerEffect_IsAMacroOnThatSide(string effect, int mode)
        {
            var p = Profile($"<LightbarMode>Passthru</LightbarMode><R2TriggerEffect>{effect}</R2TriggerEffect>");
            var m = Assert.Single(p.Macros);
            Assert.Equal(MacroTriggerMode.Always, m.TriggerMode);
            var a = m.Actions[0];
            Assert.Equal(MacroActionType.TriggerEffect, a.Type);
            Assert.Equal(mode, a.TriggerEffectMode);
            Assert.Equal(MacroTriggerEffectSide.Right, a.TriggerEffectSide);
            // It ends by itself after a profile switch, and does not encode a
            // block every frame.
            Assert.Equal(MacroTriggerEffectHold.Duration, a.TriggerEffectHold);
            Assert.True(m.Actions[1].DurationMs < a.TriggerEffectMs);
            var loaded = SettingsService.LoadMacroFromData(m, VirtualControllerType.PlayStation, null);
            Assert.NotNull(TriggerEffectCatalog.Encode(loaded.Actions[0].TriggerEffectMode, loaded.Actions[0].TriggerEffectValues));
        }

        // ── Gyro ────────────────────────────────────────────────────────

        [Fact]
        public void GyroMouse_RowsTheCursor_WithItsEngage()
        {
            var p = Profile("<GyroOutputMode>Mouse</GyroOutputMode><SATriggers>4</SATriggers><GyroTriggerTurns>True</GyroTriggerTurns><GyroSensitivity>150</GyroSensitivity><GyroSensVerticalScale>50</GyroSensVerticalScale><GyroInvert>2</GyroInvert>");
            var x = Row(p, "KbmMouseX").Sources[0];
            var y = Row(p, "KbmMouseY").Sources[0];
            Assert.Equal("Gyro Yaw", x.Descriptor);
            Assert.Equal(1.5, x.GyroSensitivity, 3);
            Assert.True(x.Invert);
            Assert.Equal("Gyro Pitch", y.Descriptor);
            Assert.Equal(0.75, y.GyroSensitivity, 3);
            Assert.False(y.Invert);
            foreach (var set in p.SlotMappingSets.Where((s, i) => p.SlotCreated[i]))
            {
                Assert.Equal("Gamepad LeftShoulder", set.WorkshopGyroEngageDescriptor);
                Assert.False(set.WorkshopGyroEngageInvert);
            }
        }

        [Fact]
        public void TriggerThatTurnsGyroOff_IsAnInvertedEngage()
        {
            var p = Profile("<GyroOutputMode>Mouse</GyroOutputMode><SATriggers>0</SATriggers><GyroTriggerTurns>False</GyroTriggerTurns>");
            Assert.True(p.SlotMappingSets[0].WorkshopGyroEngageInvert);
            Assert.Equal("Gamepad ButtonA", p.SlotMappingSets[0].WorkshopGyroEngageDescriptor);
        }

        [Fact]
        public void AlwaysOnGyro_HasNoEngage()
        {
            var p = Profile("<GyroOutputMode>Mouse</GyroOutputMode><SATriggers>-1</SATriggers>");
            Assert.Equal("", p.SlotMappingSets[0].WorkshopGyroEngageDescriptor);
        }

        [Fact]
        public void GyroMouseJoystick_RowsTheChosenStick()
        {
            var p = Profile("<GyroOutputMode>MouseJoystick</GyroOutputMode><GyroMouseStickOutputStick>LeftStick</GyroMouseStickOutputStick><GyroMouseStickTriggers>6</GyroMouseStickTriggers><GyroMouseStickTriggerTurns>True</GyroMouseStickTriggerTurns><GyroMouseStickToggle>True</GyroMouseStickToggle>");
            Assert.Contains(Row(p, "LeftThumbAxisX").Sources, s => s.Descriptor == "Gyro Yaw");
            Assert.Contains(Row(p, "LeftThumbAxisY").Sources, s => s.Descriptor == "Gyro Pitch");
            Assert.Equal("Gamepad RightShoulder", p.SlotMappingSets[0].WorkshopGyroEngageDescriptor);
            Assert.True(p.SlotMappingSets[0].WorkshopGyroEngageToggle);
        }

        [Fact]
        public void GyroPassthrough_OnAPlayStationSlot_IsMotionRows()
        {
            var ds4 = Profile("<OutputContDevice>DS4</OutputContDevice><GyroOutputMode>Passthru</GyroOutputMode>");
            Assert.Equal("Motion Gyro", Row(ds4, "MotionGyro").Sources[0].Descriptor);
            Assert.Equal("Motion Accel", Row(ds4, "MotionAccel").Sources[0].Descriptor);
            Assert.Null(Row(Profile("<OutputContDevice>X360</OutputContDevice><GyroOutputMode>Passthru</GyroOutputMode>"), "MotionGyro"));
        }

        [Theory]
        [InlineData("GyroXPos", "Gyro Lean X", true)]
        [InlineData("GyroXNeg", "Gyro Lean X", false)]
        [InlineData("GyroZPos", "Gyro Lean Y", true)]
        [InlineData("GyroZNeg", "Gyro Lean Y", false)]
        public void TiltControls_ReadSustainedLean(string control, string descriptor, bool lower)
        {
            var src = Row(Profile("<GyroOutputMode>Controls</GyroOutputMode>" + Control("Button", control, "A Button")), "ButtonA")
                .Sources.First(s => s.Descriptor == descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(lower, src.Invert);
        }

        [Theory]
        [InlineData("GyroSwipeLeft", "Gyro Yaw", false)]
        [InlineData("GyroSwipeRight", "Gyro Yaw", true)]
        [InlineData("GyroSwipeUp", "Gyro Pitch", false)]
        [InlineData("GyroSwipeDown", "Gyro Pitch", true)]
        public void GyroSwipes_AreHalfAxisReads(string control, string descriptor, bool lower)
        {
            var src = Row(Profile("<GyroOutputMode>DirectionalSwipe</GyroOutputMode>" + Control("Button", control, "A Button")), "ButtonA")
                .Sources.First(s => s.Descriptor == descriptor);
            Assert.True(src.HalfAxis);
            Assert.Equal(lower, src.Invert);
        }

        // ── Touchpad ────────────────────────────────────────────────────

        [Fact]
        public void TouchpadMouse_RowsTheCursorAndItsClickRegions()
        {
            var p = Profile("<TouchpadOutputMode>Mouse</TouchpadOutputMode><touchSensitivity>200</touchSensitivity>");
            Assert.Equal("Touchpad 0 Finger 0 X", Row(p, "KbmMouseX").Sources[0].Descriptor);
            Assert.Equal(2.0, Row(p, "KbmMouseY").Sources[0].Sensitivity, 3);
            var left = Row(p, "KbmMBtn0");
            Assert.Contains(left.Sources, s => s.Descriptor == "Touchpad 0 Click Left");
            Assert.Contains(left.Sources, s => s.Descriptor == "Touchpad 0 Click Right");
            Assert.Contains(Row(p, "KbmMBtn1").Sources, s => s.Descriptor == "Touchpad 0 TouchMulti");
            Assert.Contains(Row(p, "KbmMBtn2").Sources, s => s.Descriptor == "Touchpad 0 TouchTop");
        }

        [Theory]
        [InlineData("TouchUpper", "Touchpad 0 TouchTop")]
        [InlineData("TouchMulti", "Touchpad 0 TouchMulti")]
        public void TouchRegions_FireOnTheClick_AsMouseCsSetsThem(string control, string descriptor)
        {
            var p = Profile(Control("Button", control, "A Button"));
            var src = Row(p, "ButtonA").Sources.First(s => s.Descriptor == descriptor);
            Assert.Equal("Touchpad 0 Click", src.GateDescriptor);
        }

        [Fact]
        public void GatedRegion_OnAMacro_IsTwoEntries()
        {
            var p = Profile("<LightbarMode>Passthru</LightbarMode>" + Control("Macro", "TouchUpper", "65/65"));
            Assert.Equal(2, Assert.Single(p.Macros).TriggerInputs.Split('|').Length);
        }

        [Fact]
        public void LowerRightCorner_RightClicks()
        {
            var p = Profile("<TouchpadOutputMode>Mouse</TouchpadOutputMode><lowerRCOn>True</lowerRCOn>");
            Assert.Contains(Row(p, "KbmMBtn1").Sources, s => s.Descriptor == "Touchpad 0 Click Right");
        }

        [Fact]
        public void RegionTheProfileRebinds_KeepsItsRebinding()
        {
            var p = Profile("<TouchpadOutputMode>Mouse</TouchpadOutputMode>" + Control("Button", "TouchMulti", "Unbound"));
            Assert.DoesNotContain(Row(p, "KbmMBtn1")?.Sources ?? new System.Collections.Generic.List<MappingSource>(),
                s => s.Descriptor == "Touchpad 0 TouchMulti");
        }

        [Fact]
        public void TouchpadAbsoluteMouse_IsThePointerFamily()
        {
            var p = Profile("<TouchpadOutputMode>AbsoluteMouse</TouchpadOutputMode>");
            Assert.Equal("Touchpad 0 Pointer X", Row(p, "KbmMouseX").Sources[0].Descriptor);
            Assert.Equal("Touchpad 0 Pointer Y", Row(p, "KbmMouseY").Sources[0].Descriptor);
        }

        [Fact]
        public void TouchpadJoystick_RowsTheChosenStick()
        {
            var p = Profile("<TouchpadOutputMode>MouseJoystick</TouchpadOutputMode><TouchpadMouseStick><OutputStick>RightStick</OutputStick></TouchpadMouseStick>");
            Assert.Contains(Row(p, "RightThumbAxisX").Sources, s => s.Descriptor == "Touchpad 0 StickX");
            Assert.Contains(Row(p, "RightThumbAxisY").Sources, s => s.Descriptor == "Touchpad 0 StickY");
        }

        [Fact]
        public void TouchpadPassthrough_OnAPlayStationSlot_IsTheTouchpadRows()
        {
            var ds4 = Profile("<OutputContDevice>DS4</OutputContDevice><TouchpadOutputMode>Passthru</TouchpadOutputMode>");
            Assert.Equal("Touchpad 0 Finger 0 X", Row(ds4, "TouchpadX1").Sources[0].Descriptor);
            Assert.Equal("Touchpad 0 Finger 1 Y", Row(ds4, "TouchpadY2").Sources[0].Descriptor);
            Assert.Equal("Touchpad 0 Click", Row(ds4, "TouchpadClick").Sources[0].Descriptor);
            Assert.Null(Row(Profile("<TouchpadOutputMode>Passthru</TouchpadOutputMode>"), "TouchpadX1"));
        }

        [Theory]
        [InlineData("SwipeUp", "Touchpad 0 SwipeUp")]
        [InlineData("SwipeLeft", "Touchpad 0 SwipeLeft")]
        [InlineData("TouchLeft", "Touchpad 0 Click Left")]
        [InlineData("TouchRight", "Touchpad 0 Click Right")]
        [InlineData("TouchUpper", "Touchpad 0 TouchTop")]
        [InlineData("TouchMulti", "Touchpad 0 TouchMulti")]
        [InlineData("Mute", "Button 11")]
        [InlineData("BRP", "Gamepad Paddle1")]
        [InlineData("BLP", "Gamepad Paddle2")]
        [InlineData("FnR", "Gamepad Paddle3")]
        [InlineData("FnL", "Gamepad Paddle4")]
        public void TouchAndExtraControls_ReadTheirDescriptor(string control, string descriptor)
        {
            var p = Profile(Control("Button", control, "A Button"));
            Assert.Contains(Row(p, "ButtonA").Sources, s => s.Descriptor == descriptor);
        }

        [Fact]
        public void OuterRing_ReadsItsRadiusAndInvert()
        {
            var p = Profile("<LSOuterBindDead>60</LSOuterBindDead><LSOuterBindInvert>True</LSOuterBindInvert>" + Control("Button", "LSOuter", "A Button"));
            var src = Row(p, "ButtonA").Sources.First(s => s.Descriptor == "Gamepad LeftStickRing");
            Assert.Equal(60, src.DeadZone);
            Assert.True(src.Invert);
        }

        // ── Lightbar ────────────────────────────────────────────────────

        [Fact]
        public void Color_IsAHeldLightbarOnAlways()
        {
            var m = Assert.Single(Profile("<Color>0,143,117</Color>").Macros);
            Assert.Equal(MacroTriggerMode.Always, m.TriggerMode);
            var a = Only(m);
            Assert.Equal(MacroActionType.LightbarColor, a.Type);
            // Reactive with a hold: a Sticky would stay on the bar after a
            // profile switch, with nothing left to clear it.
            Assert.Equal(MacroLightbarHoldMode.Reactive, a.LightbarHoldMode);
            Assert.True(a.LightbarHoldMs > 0);
            Assert.Equal((byte)143, a.LightbarG);
        }

        [Fact]
        public void Rainbow_GoesRoundEveryHueInItsTime()
        {
            var m = Assert.Single(Profile("<Color>1,2,3</Color><Rainbow>6</Rainbow>").Macros);
            Assert.Equal(MacroTriggerMode.Always, m.TriggerMode);
            Assert.Equal(72, m.Actions.Length);
            int waits = m.Actions.Where(a => a.Type == MacroActionType.Delay).Sum(a => a.DurationMs);
            Assert.InRange(waits, 5900, 6100);
        }

        [Fact]
        public void LightbarPassthrough_LeavesTheBarToTheGame()
            => Assert.Empty(Profile("<Color>1,2,3</Color><LightbarMode>Passthru</LightbarMode>").Macros);

        [Fact]
        public void BatteryIndicator_ShowsTheBattery_InTheProfilesColors()
        {
            var r = Import("<Color>1,2,3</Color><LowColor>4,5,6</LowColor><ledAsBatteryIndicator>True</ledAsBatteryIndicator>");
            var a = Only(Assert.Single(r.Profile.Macros));
            Assert.Equal(MacroActionType.LightbarModeSet, a.Type);
            Assert.Equal(LightbarMode.Battery, a.LightbarTargetMode);
            // getTransitionedColor: LowColor at empty, Color at full.
            Assert.Equal("4,5,6;1,2,3", r.Profile.SlotMappingSets[0].WorkshopBatteryColors);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void RainbowUnderTheBatteryLight_IsTheRainbow_AndSaysItDoesNotDim()
        {
            var r = Import("<Color>1,2,3</Color><Rainbow>6</Rainbow><ledAsBatteryIndicator>True</ledAsBatteryIndicator>");
            Assert.Equal(72, Assert.Single(r.Profile.Macros).Actions.Length);
            Assert.Contains(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting && e.ReasonArgs.Contains("ledAsBatteryIndicator"));
        }

        [Fact]
        public void RainbowSaturation_DimsEveryHue()
        {
            var m = Assert.Single(Profile("<Rainbow>6</Rainbow><MaxSatRainbow>50</MaxSatRainbow>").Macros);
            Assert.Equal((byte)127, m.Actions[0].LightbarR);
            Assert.True(m.Actions.Where(x => x.Type == MacroActionType.LightbarColor).All(x => x.LightbarR <= 127 && x.LightbarG <= 127 && x.LightbarB <= 127));
        }

        // ── Safety and leftovers ────────────────────────────────────────

        [Fact]
        public void LaunchProgram_StartsWithTheProfile_OnceOnEveryLayer_AndThePreviewNamesIt()
        {
            var r = Import("<LaunchProgram>C:\\Games\\tool.exe</LaunchProgram>");
            var m = Assert.Single(r.Profile.Macros);
            Assert.Equal(MacroTriggerMode.Always, m.TriggerMode);
            Assert.Equal("", m.LayerMask);
            Assert.Equal(MacroActionType.RunProgram, m.Actions[0].Type);
            Assert.Equal("C:\\Games\\tool.exe", m.Actions[0].ProgramPath);
            // Then it waits for as long as a wait can last, so it starts once.
            Assert.Equal(int.MaxValue, m.Actions[1].DurationMs);
            // The preview says it starts when the profile loads, not on a button.
            Assert.Contains("C:\\Games\\tool.exe", r.Starts);
            Assert.Empty(r.Runs);
        }

        [Theory]
        [InlineData("LX", "LeftThumbAxisX")]
        [InlineData("RY", "RightThumbAxisY")]
        public void SteeringWheelEmulation_IsAMotionLeanRow(string axis, string target)
        {
            var r = Import("<SASteeringWheelEmulationAxis>" + axis + "</SASteeringWheelEmulationAxis><SASteeringWheelEmulationRange>180</SASteeringWheelEmulationRange>");
            var src = Assert.Single(Row(r.Profile, target).Sources);
            Assert.Equal(SourceCoercion.MotionLeanDescriptor, src.Descriptor);
            // A 180 degree wheel turns 90 degrees each way.
            Assert.Equal(90.0, src.ParamMotionOuterDz, 3);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
            Assert.DoesNotContain(Import("<SASteeringWheelEmulationAxis>None</SASteeringWheelEmulationAxis>").Report.Entries,
                e => e.ReasonKey == ImportReasons.SteeringWheel);
        }

        [Fact]
        public void RumbleBoostUnder100_SetsTheMotorStrength()
        {
            var r = Import("<RumbleBoost>50</RumbleBoost><LSFuzz>3</LSFuzz>");
            Assert.Equal("50", r.Profile.SlotMappingSets[0].WorkshopRumblePercent);
            // PadForge has no stick fuzz, and the line says so.
            Assert.Single(r.Report.Entries, e => e.ReasonKey == ImportReasons.NoSuchSetting && e.ReasonArgs.Contains("LSFuzz"));
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ControllerSetting);
            Assert.DoesNotContain(Import("<RumbleBoost>100</RumbleBoost><LSFuzz>0</LSFuzz>").Report.Entries,
                e => e.ReasonKey is ImportReasons.ControllerSetting or ImportReasons.NoSuchSetting);
        }

        [Fact]
        public void IdleDisconnect_SetsTheDevicesIdleTime()
            => Assert.Equal("600", Profile("<idleDisconnectTimeout>600</idleDisconnectTimeout>").SlotMappingSets[0].WorkshopIdleDisconnectSeconds);

        [Fact]
        public void ADtd_IsRefusedAndSaysWhy()
        {
            string evil = "<?xml version=\"1.0\"?><!DOCTYPE DS4Windows [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><DS4Windows><Color>&x;</Color></DS4Windows>";
            var r = ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(evil), "x.xml");
            Assert.True(r.Refused);
            Assert.Equal(ImportReasons.FileHasDtd, r.RefusedKey);
        }

        [Fact]
        public void ExpandingEntities_AreRefused()
        {
            string bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY a \"aaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><DS4Windows><Color>&b;</Color></DS4Windows>";
            Assert.True(ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(bomb), "x.xml").Refused);
        }

        [Theory]
        [InlineData("<Actions></Actions>")]
        [InlineData("<Programs />")]
        [InlineData("<root/>")]
        [InlineData("just text")]
        [InlineData("")]
        public void OtherFiles_AreRefused(string content)
        {
            var r = ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(content), "x.xml");
            Assert.Equal(ImportReasons.FileNotConfig, r.RefusedKey);
        }

        [Fact]
        public void ForkElements_AreIgnored()
        {
            var r = Import("<SomeForkOnlySetting><Deep>1</Deep></SomeForkOnlySetting><Color>1,2,3</Color>");
            Assert.False(r.Refused);
            Assert.Equal(0, r.Report.SkippedCount);
        }

        // ── Special actions and auto profiles, beside the Profiles folder ──

        private sealed class Ds4Folder : IDisposable
        {
            public readonly string Root = Path.Combine(Path.GetTempPath(), "pf-561-" + Guid.NewGuid().ToString("N"));
            public string ProfilePath => Path.Combine(Root, "Profiles", "Shooter.xml");

            public Ds4Folder(string actions = null, string autoProfiles = null)
            {
                Directory.CreateDirectory(Path.Combine(Root, "Profiles"));
                if (actions != null) File.WriteAllText(Path.Combine(Root, "Actions.xml"), actions);
                if (autoProfiles != null) File.WriteAllText(Path.Combine(Root, "Auto Profiles.xml"), autoProfiles);
            }

            public ForeignImportResult Read(string body)
                => ForeignConfigImporter.Translate(Encoding.UTF8.GetBytes(Xml(body)), ProfilePath);

            public void Dispose()
            {
                string profiles = Path.Combine(Root, "Profiles");
                string actions = Path.Combine(Root, "Actions.xml");
                string auto = Path.Combine(Root, "Auto Profiles.xml");
                if (File.Exists(actions)) File.Delete(actions);
                if (File.Exists(auto)) File.Delete(auto);
                Directory.Delete(profiles);
                Directory.Delete(Root);
            }
        }

        private static string Action(string name, string type, string details, string extras = "", string trigger = "Cross/L1")
            => $"<Action Name=\"{name}\"><Trigger>{trigger}</Trigger><Type>{type}</Type><Details>{details}</Details><Extras>{extras}</Extras></Action>";

        [Fact]
        public void SpecialMacro_LandsOnItsChord()
        {
            using var f = new Ds4Folder("<Actions>" + Action("Reload", "Macro", "82/82") + "</Actions>");
            var r = f.Read("<ProfileActions>Reload</ProfileActions>");
            var m = r.Profile.Macros.First(x => x.Name == "Reload");
            Assert.Equal(2, m.TriggerInputs.Split('|').Length);
            Assert.Equal(MacroTriggerMode.OnPress, m.TriggerMode);
            Assert.Equal(MacroActionType.KeyPress, Only(m).Type);
        }

        [Theory]
        [InlineData("Repeat", MacroTriggerMode.WhileHeld)]
        [InlineData("RunOnRelease", MacroTriggerMode.OnRelease)]
        public void SpecialMacroExtras_ChooseItsShape(string extras, MacroTriggerMode mode)
        {
            using var f = new Ds4Folder("<Actions>" + Action("M", "Macro", "82/82", extras) + "</Actions>");
            Assert.Equal(mode, f.Read("<ProfileActions>M</ProfileActions>").Profile.Macros.First(x => x.Name == "M").TriggerMode);
        }

        [Fact]
        public void SpecialKey_HoldsWhileHeld()
        {
            using var f = new Ds4Folder("<Actions>" + Action("K", "Key", "65") + "</Actions>");
            var p = f.Read("<ProfileActions>K</ProfileActions>").Profile;
            var press = p.Macros.First(x => x.Name == "K");
            Assert.Equal(MacroLatchDirection.On, Only(press).LatchDirection);
            Assert.Contains(p.Macros, x => x.PairId == press.PairId && x.TriggerMode == MacroTriggerMode.OnRelease);
        }

        [Theory]
        [InlineData("Press", MacroTriggerMode.OnPress)]
        [InlineData("Release", MacroTriggerMode.OnRelease)]
        public void SpecialKeyWithAnUnloadTrigger_HoldsUntilThatTrigger(string style, MacroTriggerMode mode)
        {
            using var f = new Ds4Folder("<Actions><Action Name=\"K\"><Trigger>Cross</Trigger><Type>Key</Type><Details>65</Details>"
                + "<UnloadTrigger>Circle</UnloadTrigger><UnloadStyle>" + style + "</UnloadStyle></Action></Actions>");
            var r = f.Read("<ProfileActions>K</ProfileActions>");
            Assert.Equal(MacroLatchDirection.On, Only(r.Profile.Macros.First(x => x.Name == "K")).LatchDirection);
            var release = r.Profile.Macros.First(x => x.Name == "K (release)");
            Assert.Equal(mode, release.TriggerMode);
            Assert.Equal(MacroLatchDirection.Off, Only(release).LatchDirection);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void SpecialDisconnect_HoldsForItsTime_AndDisconnectsTheSlotsDevices()
        {
            using var f = new Ds4Folder("<Actions>" + Action("D", "DisconnectBT", "2") + "</Actions>");
            var m = f.Read("<ProfileActions>D</ProfileActions>").Profile.Macros.First(x => x.Name == "D");
            Assert.Equal(MacroTriggerMode.HoldForMs, m.TriggerMode);
            Assert.Equal(2000, m.TriggerHoldMs);
            var a = Only(m);
            Assert.Equal(MacroActionType.DisconnectController, a.Type);
            Assert.Equal(MacroDisconnectTarget.SlotDevices, a.DisconnectTarget);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("<Actions></Actions>")]
        public void DefaultDisconnectAction_ComesAcross_WhereverTheProfileGoes(string actions)
        {
            // ScpUtil writes "Disconnect Controller", PS and Options held, into
            // every new Actions.xml, so a shared profile that names it finds it.
            using var f = new Ds4Folder(actions);
            var r = f.Read("<ProfileActions>Disconnect Controller</ProfileActions>");
            var m = r.Profile.Macros.First(x => x.Name == "Disconnect Controller");
            Assert.Equal(2, m.TriggerInputs.Split('|').Length);
            Assert.Equal(MacroActionType.DisconnectController, Only(m).Type);
            Assert.DoesNotContain(r.Report.Entries, e => e.ReasonKey == ImportReasons.ActionNotFound);
        }

        [Fact]
        public void AnActionsFilesOwnDisconnect_WinsOverTheDefault()
        {
            using var f = new Ds4Folder("<Actions>" + Action("Disconnect Controller", "DisconnectBT", "5", trigger: "Share") + "</Actions>");
            var m = f.Read("<ProfileActions>Disconnect Controller</ProfileActions>").Profile.Macros.First(x => x.Name == "Disconnect Controller");
            Assert.Single(m.TriggerInputs.Split('|'));
            Assert.Equal(5000, m.TriggerHoldMs);
        }

        [Fact]
        public void SpecialProgram_RunsWithItsArguments_AndThePreviewNamesIt()
        {
            using var f = new Ds4Folder("<Actions><Action Name=\"P\"><Trigger>Cross/L1</Trigger><Type>Program</Type><Details>C:\\Games\\tool.exe</Details>"
                + "<Arguements>-fast $hidden</Arguements><Delay>1.5</Delay></Action></Actions>");
            var r = f.Read("<ProfileActions>P</ProfileActions>");
            var m = r.Profile.Macros.First(x => x.Name == "P");
            Assert.Equal(MacroTriggerMode.HoldForMs, m.TriggerMode);
            Assert.Equal(1500, m.TriggerHoldMs);
            var a = Only(m);
            Assert.Equal(MacroActionType.RunProgram, a.Type);
            Assert.Equal("C:\\Games\\tool.exe", a.ProgramPath);
            Assert.Equal("-fast", a.ProgramArgs);
            Assert.Contains("C:\\Games\\tool.exe", r.Runs);
        }

        [Fact]
        public void SpecialProfileSwitch_LoadsTheNamedProfile()
        {
            using var f = new Ds4Folder("<Actions>" + Action("S", "Profile", "Other") + "</Actions>");
            var r = f.Read("<ProfileActions>S</ProfileActions>");
            var a = Only(r.Profile.Macros.First(x => x.Name == "S"));
            Assert.Equal(MacroActionType.ProfileShortcut, a.Type);
            Assert.Equal(SwitchProfileMode.Specific, a.ProfileShortcutMode);
            Assert.Equal("Other", a.ProfileShortcutName);
            Assert.Equal(0, r.Report.PartialCount + r.Report.SkippedCount);
        }

        [Fact]
        public void ProfileSwitchWithAWayBack_NamesTheProfileShortcut()
        {
            // The way back runs while the other profile is loaded, which a
            // Profile Shortcut on the Profiles page reads from any profile.
            using var f = new Ds4Folder("<Actions><Action Name=\"S\"><Trigger>Cross/L1</Trigger><Type>Profile</Type><Details>Other</Details>"
                + "<UnloadTrigger>Circle</UnloadTrigger></Action></Actions>");
            var r = f.Read("<ProfileActions>S</ProfileActions>");
            Assert.Equal(MacroActionType.ProfileShortcut, Only(r.Profile.Macros.First(x => x.Name == "S")).Type);
            var line = Assert.Single(r.Report.Entries, e => e.Status == TranslationStatus.Partial);
            Assert.Equal(ImportReasons.UnloadTrigger, line.ReasonKey);
            Assert.Contains("Other", line.ReasonArgs);
        }

        [Fact]
        public void SpecialActionMissingFromTheFile_IsNamed()
        {
            using var f = new Ds4Folder("<Actions></Actions>");
            Assert.Contains(f.Read("<ProfileActions>Gone</ProfileActions>").Report.Entries,
                e => e.ReasonKey == ImportReasons.ActionNotFound && e.ReasonArgs.Contains("Gone"));
        }

        [Theory]
        [InlineData("Left Stick Up", "Gamepad LeftStickY")]
        [InlineData("L2 Full Pull", "Gamepad LeftTrigger")]
        [InlineData("Left", "Gamepad DPadLeft")]
        [InlineData("Left Touch", "Touchpad 0 Click Left")]
        [InlineData("Tilt Up", "Gyro Lean Y")]
        [InlineData("Function Left", "Gamepad Paddle4")]
        public void SpecialActionTriggers_AreNamedInWords(string trigger, string descriptor)
        {
            using var f = new Ds4Folder("<Actions>" + Action("A", "Macro", "82/82", trigger: trigger) + "</Actions>");
            var m = f.Read("<ProfileActions>A</ProfileActions>").Profile.Macros.First(x => x.Name == "A");
            Assert.Contains(descriptor, m.TriggerInputs);
        }

        [Fact]
        public void AutoProfiles_FillTheExecutables()
        {
            using var f = new Ds4Folder(autoProfiles:
                """<Programs><Program path="C:\Games\a.exe" title=""><Controller1>Shooter</Controller1><Controller2>Other</Controller2></Program><Program path="C:\Games\b.exe" title=""><Controller3>Other</Controller3></Program><Program path="C:\Games\c.exe" title=""><Controller8>Shooter</Controller8></Program></Programs>""");
            Assert.Equal("C:\\Games\\a.exe|C:\\Games\\c.exe", f.Read("").Profile.ExecutableNames);
        }

        [Fact]
        public void NoFilesBeside_IsFine()
        {
            using var f = new Ds4Folder();
            var r = f.Read("");
            Assert.False(r.Refused);
            Assert.Equal(string.Empty, r.Profile.ExecutableNames);
        }

        // ── A profile like any other ────────────────────────────────────

        [Fact]
        public void Profile_RoundTripsTheSettingsFile()
        {
            var p = Profile(Control("Macro", "Cross", "65/350/65") + ShiftOnL1
                + "<Color>1,2,3</Color><LSDeadZone>20</LSDeadZone><GyroOutputMode>Mouse</GyroOutputMode><SATriggers>4</SATriggers>");
            var serializer = new XmlSerializer(typeof(ProfileData));
            using var buffer = new MemoryStream();
            serializer.Serialize(buffer, p);
            buffer.Position = 0;
            var back = (ProfileData)serializer.Deserialize(buffer);
            Assert.Equal(p.Macros.Length, back.Macros.Length);
            Assert.Equal(p.SlotMappingSets[0].Rows.Count, back.SlotMappingSets[0].Rows.Count);
            Assert.Equal(p.SlotMappingSets[0].ShiftActivators.Count, back.SlotMappingSets[0].ShiftActivators.Count);
            Assert.Equal("Gamepad LeftShoulder", back.SlotMappingSets[0].WorkshopGyroEngageDescriptor);
        }

        [Fact]
        public void ANoBindingFile_StillGivesThePadItsRows_NeverAnEmptySlot()
        {
            var r = Import("");
            Assert.True(r.HasSlots);
            Assert.True(r.ReadsController);
            Assert.False(r.ReadsKeyboard);
            Assert.Null(r.Profile.Entries);
        }
    }
}
