using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// SDL's layout against a controller's rows (#600 items 45, 46 and 48):
    /// the place SDL's row gives each row, where they differ, what Copy reads
    /// from a raw joystick's rows and what it cannot hold, a guessed layout,
    /// and a snapshot of rows put back. Plain logic over mapping sets.
    /// </summary>
    public class ControllerDatabaseLayoutTests
    {
        private static readonly string Device = Guid.Parse("11111111-2222-4333-8444-555555555555").ToString();
        private static readonly string Other = Guid.Parse("99999999-2222-4333-8444-555555555555").ToString();

        private static MappingRow Row(string target, params MappingSource[] sources) =>
            new() { Target = target, Sources = sources.ToList() };

        private static MappingSource From(string descriptor, string device = null, bool invert = false, bool half = false, string kind = "Direct") =>
            new() { DeviceGuid = device ?? Device, Descriptor = descriptor, Invert = invert, HalfAxis = half, Kind = kind };

        private static SdlMappingRow Sdl(string bindings)
        {
            Assert.True(SdlMappingRow.TryParse("03000000aa0b00001100000000000000,Pad," + bindings + ",platform:Windows,", out var row, out var why), why.ToString());
            return row;
        }

        [Fact]
        public void PadForgesPlaces_AreSdlsFields()
        {
            Assert.Equal("a", ControllerDatabaseLayout.FieldOfGamepadInput("Button 0"));
            Assert.Equal("guide", ControllerDatabaseLayout.FieldOfGamepadInput("Button 10"));
            Assert.Equal("paddle1", ControllerDatabaseLayout.FieldOfGamepadInput("Button 12"));
            Assert.Equal("touchpad", ControllerDatabaseLayout.FieldOfGamepadInput("Button 16"));
            Assert.Equal("lefttrigger", ControllerDatabaseLayout.FieldOfGamepadInput("Axis 2"));
            Assert.Equal("dpleft", ControllerDatabaseLayout.FieldOfGamepadInput("POV 0 Left"));
            Assert.Null(ControllerDatabaseLayout.FieldOfGamepadInput("POV 1 Left"));
            Assert.Null(ControllerDatabaseLayout.FieldOfGamepadInput("Button 22"));
            Assert.Equal("misc1", ControllerDatabaseLayout.FieldOfTarget("ButtonShare"));
            Assert.Equal("paddle4", ControllerDatabaseLayout.FieldOfTarget("LeftFunction"));
            Assert.Null(ControllerDatabaseLayout.FieldOfTarget("RawButton3"));
        }

        [Fact]
        public void ARawJoysticksSources_AreSdlsInputs()
        {
            Assert.Equal("b3", ControllerDatabaseLayout.InputOfRawSource(From("Button 3")));
            Assert.Equal("a1", ControllerDatabaseLayout.InputOfRawSource(From("Axis 1")));
            Assert.Equal("a1~", ControllerDatabaseLayout.InputOfRawSource(From("Axis 1", invert: true)));
            Assert.Equal("+a2", ControllerDatabaseLayout.InputOfRawSource(From("Axis 2", half: true)));
            Assert.Equal("-a2", ControllerDatabaseLayout.InputOfRawSource(From("Axis 2", invert: true, half: true)));
            Assert.Equal("a24", ControllerDatabaseLayout.InputOfRawSource(From("Slider 0")));
            Assert.Equal("h0.4", ControllerDatabaseLayout.InputOfRawSource(From("POV 0 Down")));
            Assert.Null(ControllerDatabaseLayout.InputOfRawSource(From("Gyro X")));
        }

        /// <summary>On a device SDL reads as a gamepad, a row differs when
        /// its input is another place than its target's: the board's B on
        /// SDL's X.</summary>
        [Fact]
        public void OnAGamepad_ARowDiffers_WhenItsPlaceIsNotItsTargets()
        {
            var set = new MappingSet { Rows = { Row("ButtonA", From("Button 0")), Row("ButtonB", From("Button 2")), Row("ButtonGuide", From("Button 10")),
                Row("ButtonY", From("Button 3", Other)) } };
            var lines = ControllerDatabaseLayout.Places(set, Device, readAsGamepad: true, Sdl("a:b0,b:b1,x:b2"), d => d);
            Assert.Equal(new[] { "ButtonA", "ButtonB", "ButtonGuide" }, lines.Select(l => l.Target));
            Assert.False(lines[0].Differs);
            Assert.Equal("a: b0", lines[0].RowSays);
            Assert.True(lines[1].Differs);
            Assert.Equal("b: b1", lines[1].RowSays);
            Assert.Null(lines[2].RowSays);
        }

        /// <summary>On a raw joystick, a row differs when its input is not
        /// the one SDL's row binds to the target's field.</summary>
        [Fact]
        public void OnARawJoystick_ARowDiffers_WhenItsInputIsNotTheRowsBinding()
        {
            var set = new MappingSet { Rows = { Row("ButtonA", From("Button 0")), Row("ButtonB", From("Button 2")),
                Row("LeftThumbAxisX", From("Axis 0")), Row("LeftThumbAxisY", From("Axis 1", invert: true)) } };
            var lines = ControllerDatabaseLayout.Places(set, Device, readAsGamepad: false, Sdl("a:b0,b:b1,leftx:a0,lefty:a1~"), d => d);
            Assert.Equal(new[] { false, true, false, false }, lines.Select(l => l.Differs));
        }

        [Fact]
        public void TheFieldsNoRowReads_AreSaid()
        {
            var set = new MappingSet { Rows = { Row("ButtonA", From("Button 0")), Row("ButtonB", From("Button 1")) } };
            Assert.Equal(new[] { "misc1", "touchpad" },
                ControllerDatabaseLayout.FieldsNotUsed(Sdl("a:b0,b:b1,misc1:b14,touchpad:b13"), new[] { set }, Device));
            Assert.Empty(ControllerDatabaseLayout.FieldsNotUsed(Sdl("a:b0,misc1:b14"), new[] { new MappingSet() }, Device));
        }

        /// <summary>Copy for a raw joystick reads the picked controller's
        /// rows, and says what SDL's form cannot hold.</summary>
        [Fact]
        public void CopyFromARawJoysticksRows_TakesItsInputs_AndSaysWhatItLeavesOut()
        {
            var set = new MappingSet
            {
                Rows =
                {
                    Row("ButtonA", From("Button 2"), From("Button 0", Other)),
                    Row("LeftThumbAxisY", From("Axis 1", invert: true)),
                    Row("LeftTrigger", From("Axis 2", half: true)),
                    Row("DPadUp", From("POV 0 Up")),
                    Row("ButtonB", From("Button 1", kind: "Turbo")),
                    Row("KbmKeySpace", From("Button 5")),
                },
            };
            var bindings = ControllerDatabaseLayout.BindingsFromRows(set, Device, out var leftOut);
            Assert.Equal(new[] { "a:b2", "lefty:a1~", "lefttrigger:+a2", "dpup:h0.1" }, bindings.Select(b => b.ToString()));
            Assert.True(leftOut.HasFlag(SdlLeftOut.Actions));
            Assert.True(leftOut.HasFlag(SdlLeftOut.KeyboardOutputs));
            var row = SdlMappingRow.Build("03000000aa0b00001100000000000000", "T.16000M, the stick", bindings);
            Assert.Equal("03000000aa0b00001100000000000000,T.16000M  the stick,a:b2,dpup:h0.1,lefttrigger:+a2,lefty:a1~,platform:Windows,", row.Text);
            Assert.True(SdlMappingRow.TryParse(row.Text, out _, out _));
        }

        [Fact]
        public void DeadZonesAndCurves_AreSaidLeftOut()
        {
            Assert.Equal(SdlLeftOut.None, ControllerDatabaseLayout.TuningLeftOut(new PadSetting()));
            Assert.Equal(SdlLeftOut.DeadZones, ControllerDatabaseLayout.TuningLeftOut(new PadSetting { LeftThumbDeadZoneX = "10" }));
            Assert.Equal(SdlLeftOut.Curves, ControllerDatabaseLayout.TuningLeftOut(new PadSetting { RightThumbSensitivityCurveX = "0.5" }));
        }

        [Fact]
        public void WhatIsLeftOut_IsSaidAsAList()
        {
            Assert.Null(SdlMappingCard.LeftOutWords(SdlLeftOut.None));
            Assert.Equal("macros", SdlMappingCard.LeftOutWords(SdlLeftOut.Macros));
            Assert.Equal("macros, dead zones and keyboard and mouse outputs",
                SdlMappingCard.LeftOutWords(SdlLeftOut.Macros | SdlLeftOut.DeadZones | SdlLeftOut.KeyboardOutputs));
        }

        // ── Guess a Layout (DB-04) ──

        [Fact]
        public void TheGuess_FollowsTheDevicesInputs()
        {
            string Text(List<SdlBinding> b) => string.Join(",", b.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal));

            // Six axes: the triggers on axes of their own.
            Assert.Equal("a:b0,b:b1,back:b6,dpdown:h0.4,dpleft:h0.8,dpright:h0.2,dpup:h0.1,guide:b10,leftshoulder:b4,leftstick:b8,lefttrigger:a2,leftx:a0,lefty:a1,"
                + "rightshoulder:b5,rightstick:b9,righttrigger:a5,rightx:a3,righty:a4,start:b7,x:b2,y:b3",
                Text(ControllerDatabaseLayout.Guess(6, 11, 1, 0x1234)));
            // Four axes and twelve buttons: the triggers are buttons 6 and 7.
            var four = ControllerDatabaseLayout.Guess(4, 12, 0, 0x1234);
            Assert.Equal("b6", four.Single(b => b.Field == "lefttrigger").Input);
            Assert.Equal("a2", four.Single(b => b.Field == "rightx").Input);
            Assert.DoesNotContain(four, b => b.Field == "guide");
            Assert.DoesNotContain(four, b => b.Field.StartsWith("dp", StringComparison.Ordinal));
            // Sony's DirectInput order, the community's own DualShock 4 row.
            var sony = ControllerDatabaseLayout.Guess(6, 14, 1, 0x054C);
            Assert.Equal("b1", sony.Single(b => b.Field == "a").Input);
            Assert.Equal("b0", sony.Single(b => b.Field == "x").Input);
            Assert.Equal("a3", sony.Single(b => b.Field == "lefttrigger").Input);
            Assert.Equal("a5", sony.Single(b => b.Field == "righty").Input);
            // A button the device lacks is never guessed.
            Assert.All(ControllerDatabaseLayout.Guess(2, 4, 0, 0x1234), b => Assert.True(b.Input[0] != 'b' || int.Parse(b.Input.Substring(1)) < 4));
        }

        // ── A row laying out a device SDL does not read ──

        [Fact]
        public void ARow_LaysOutAPluginsDevice_InPlainInputs()
        {
            var placement = ControllerDatabaseLayout.PlacementOf(Sdl("a:b2,b:b0,leftx:a0,lefty:a1~,lefttrigger:b6,righttrigger:+a5,dpup:h0.1,dpdown:h0.4,dpleft:h0.8,dpright:h0.2"),
                out var leftOut);
            Assert.Equal("Button 2", placement.Button(0));
            Assert.Equal("Button 0", placement.Button(1));
            Assert.Equal("Axis 0", placement.Axis(0));
            Assert.Equal("Button 6", placement.Axis(2));
            Assert.True(placement.DPad);
            Assert.Equal(new[] { "lefty", "righttrigger" }, leftOut.OrderBy(x => x, StringComparer.Ordinal));
            Assert.False(ControllerDatabaseLayout.PlacementOf(Sdl("a:b0,dpup:b11"), out leftOut).DPad);
        }

        /// <summary>A D-pad on four buttons is the placement's D-pad
        /// buttons, up, down, left and right, and a direction on a hat that
        /// is not the whole D-pad is left out and named.</summary>
        [Fact]
        public void ARowsDPadOnButtons_IsThePlacementsDPadButtons()
        {
            var placement = ControllerDatabaseLayout.PlacementOf(Sdl("a:b0,dpup:b12,dpdown:b13,dpleft:b14,dpright:b15"), out var leftOut);
            Assert.False(placement.DPad);
            Assert.Equal("Button 12", placement.DPadButton(0));
            Assert.Equal("Button 13", placement.DPadButton(1));
            Assert.Equal("Button 14", placement.DPadButton(2));
            Assert.Equal("Button 15", placement.DPadButton(3));
            Assert.Empty(leftOut);

            placement = ControllerDatabaseLayout.PlacementOf(Sdl("a:b0,dpup:h0.1,dpdown:b13"), out leftOut);
            Assert.False(placement.DPad);
            Assert.Null(placement.DPadButton(0));
            Assert.Equal("Button 13", placement.DPadButton(1));
            Assert.Equal(new[] { "dpup" }, leftOut);

            // The paste dialog names what is left out for a device SDL does not read.
            var check = PadForge.Views.SdlMappingDialog.Verdict(PadForge.Views.SdlMappingDialog.Mode.Paste,
                "03000000aa0b00001100000000000000,Pad,a:b0,lefty:a1~,platform:Windows,", null, out _, out _);
            Assert.Contains(string.Format(PadForge.Resources.Strings.Strings.Instance.SdlMappingDialog_Detail_LeftOut_Format, "lefty"), check.Detail);
        }

        // ── Remove Mine's rows ──

        /// <summary>A snapshot's sources join the row of their target, or
        /// come back as their own row, on a new list.</summary>
        [Fact]
        public void TheRowsFromBefore_ArePutBack()
        {
            var live = new List<MappingRow> { Row("ButtonA", From("Button 0", Other)), Row("ButtonB", From("Button 1", Other)) };
            var snapshot = new List<MappingRow> { Row("ButtonA", From("Button 5")), Row("ButtonX", From("Button 7")) };
            var result = ControllerDatabaseController.PutBack(live, snapshot);
            Assert.NotSame(live, result);
            Assert.Equal(new[] { "ButtonA", "ButtonB", "ButtonX" }, result.Select(r => r.Target));
            Assert.Equal(new[] { "Button 0", "Button 5" }, result[0].Sources.Select(s => s.Descriptor));
            Assert.Equal("Button 7", result[2].Sources.Single().Descriptor);
            // The snapshot's own rows are copies, not the stored ones.
            Assert.NotSame(snapshot[1], result[2]);
        }

        [Fact]
        public void ARowsCopy_KeepsOnlyTheSourcesAsked()
        {
            var row = Row("ButtonA", From("Button 0"), From("Button 3", Other));
            var copy = ControllerDatabaseMine.CopyOf(row, s => string.Equals(s.DeviceGuid, Device, StringComparison.OrdinalIgnoreCase));
            Assert.Single(copy.Sources);
            Assert.Equal(2, row.Sources.Count);
            Assert.Equal("ButtonA", copy.Target);
        }

        /// <summary>The mapping SDL held before goes back under the ID the
        /// user's row took, so it lands on the same entry of SDL's list.</summary>
        [Fact]
        public void TheOldMapping_GoesBackUnderTheUsersId()
        {
            Assert.True(SdlMappingRow.TryParse("050057564c050000e60c000000006800,Mine,a:b0,", out var row, out _));
            var mine = new ControllerDatabaseMineRow("050057564c050000e60c000000006800", row, DateTime.UtcNow,
                "050057564c050000e60c000000006800,DualSense Wireless Controller,a:b1,crc:5657,", Guid.Empty, Array.Empty<ControllerDatabaseBefore>());
            Assert.Equal("050057564c050000e60c000000006800,DualSense Wireless Controller,a:b1,crc:5657,", ControllerDatabaseUse.Under(mine.Id, mine.OldRow));
        }

        [Fact]
        public void SdlsOwnMappingText_IsReadByTheUserGrammar()
        {
            Assert.Equal("00000000000000000000000000000001,XInput Controller,a:b0,b:b1",
                ControllerDatabaseController.Sanitized("xinput,XInput Controller,a:b0,b:b1"));
            Assert.Equal("03000000aa0b00001100000000000000,Pad,a:b0,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,",
                ControllerDatabaseController.Sanitized("03000000aa0b00001100000000000000,Pad,a:b0,hint:!SDL_GAMECONTROLLER_USE_BUTTON_LABELS:=1,hint:SDL_OTHER:=1,sdk>=:21,"));
        }
    }
}
