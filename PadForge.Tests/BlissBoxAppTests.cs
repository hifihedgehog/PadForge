using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Bliss-Box support above the protocol (issue #469): the controller
    /// names per type and firmware, their translations, the object list and
    /// state merge, the rumble routing, the settings, the Dreamcast screen's
    /// pictures and the Show Dreamcast Screen macro action.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class BlissBoxAppTests
    {
        // ── Names ──

        [Fact]
        public void EachGenerationNamesButtonsFromItsOwnSource()
        {
            // RetroArch 3.24: NES button 0 is B. DeviceBuddy: A.
            Assert.Equal("B", BlissBoxControllers.ButtonName(17, 3, 0));
            Assert.Equal("A", BlissBoxControllers.ButtonName(17, 4, 0));
            // SNES X and Y trade places between the two.
            Assert.Equal("Y", BlissBoxControllers.ButtonName(27, 3, 2));
            Assert.Equal("X", BlissBoxControllers.ButtonName(27, 4, 2));
            // Genesis C and Z.
            Assert.Equal("C", BlissBoxControllers.ButtonName(21, 3, 7));
            Assert.Equal("Z", BlissBoxControllers.ButtonName(21, 4, 7));
            // The GameCube's analog triggers ride Z and Rz on both: the 3.0
            // firmware Bliss-Box distributes writes them there (0x1042), and
            // leaves the Slider and Dial RetroArch's file names at center.
            Assert.Equal("Left Trigger", BlissBoxControllers.AxisName(9, 3, 2));
            Assert.Equal("Right Trigger", BlissBoxControllers.AxisName(9, 3, 5));
            Assert.Null(BlissBoxControllers.AxisName(9, 3, 6));
            Assert.Equal("Left Trigger", BlissBoxControllers.AxisName(9, 4, 2));
            // The Saturn 3D pad's and the Dreamcast pad's triggers too, which
            // the firmware copies there whatever the layouts draw.
            Assert.Equal("Right Trigger", BlissBoxControllers.AxisName(8, 3, 5));
            Assert.Equal("Left Trigger", BlissBoxControllers.AxisName(16, 4, 2));
            // The sources agree on the PlayStation and the N64.
            Assert.Equal("Cross", BlissBoxControllers.ButtonName(121, 3, 0));
            Assert.Equal("Cross", BlissBoxControllers.ButtonName(121, 4, 0));
            Assert.Equal("Z Trigger", BlissBoxControllers.ButtonName(19, 3, 4));
            Assert.Equal("C-Up", BlissBoxControllers.ButtonName(19, 4, 8));
            // Nothing settles the Jaguar on 3.x, so it keeps its own names.
            Assert.Null(BlissBoxControllers.ButtonName(11, 3, 7));
            Assert.Equal("A", BlissBoxControllers.ButtonName(11, 4, 7));
            // 2.x has no source, and an unknown type keeps its numbers.
            Assert.False(BlissBoxControllers.HasLayout(121, 2));
            Assert.Null(BlissBoxControllers.ButtonName(200, 4, 0));
            Assert.Equal("D-Pad", BlissBoxControllers.HatName(121, 4));
            Assert.Null(BlissBoxControllers.HatName(6, 4));
            // An unnamed type is left to the App, which shows its number in
            // the user's language.
            Assert.Null(BlissBoxControllers.Name(200, 4));
            Assert.Equal("Super Action Controller", BlissBoxControllers.Name(34, 4));
            string names = Repo("PadForge.App", "Services", "InputService.BlissBox.cs");
            Assert.Contains("?? string.Format(CultureInfo.CurrentCulture, Strings.Instance.BlissBox_TypeNumber_Format, info.Type);", names);
            Assert.Contains("return info == null ? Strings.Instance.BlissBox_NoController : ControllerName(info);", names);
            Assert.Contains("live == null ? s.BlissBox_NoController : ControllerName(live), FirmwareText(info));", names);
            Assert.Equal("DualShock 2", BlissBoxControllers.Name(121, 3));
            // Two codes name different controllers on each generation: GPA
            // 4.86's DE-9 driver types an Atari driving controller 12 (0x2436)
            // and returns 66 (0x22D7), and 3.0 returns 66 for a Wii extension
            // (0x23A9 to 0x23BB).
            Assert.Equal("Atari driving controller", BlissBoxControllers.Name(12, 4));
            Assert.Equal("PlayStation wheel", BlissBoxControllers.Name(12, 3));
            Assert.Equal("FM Towns pad", BlissBoxControllers.Name(66, 4));
            Assert.Equal("Wii drums", BlissBoxControllers.Name(66, 3));
        }

        [Fact]
        public void PressureNamesFollowThePadsOwnOrder()
        {
            // psx-spx's reply order, which every firmware copies straight into
            // report 21, 2.0 included (0x2313 to 0x231C).
            var names = BlissBoxControllers.PressureNames;
            Assert.Equal(12, names.Count);
            Assert.Equal("D-Pad Right Pressure", names[0]);
            Assert.Equal("D-Pad Down Pressure", names[3]);
            Assert.Equal("Triangle Pressure", names[4]);
            Assert.Equal("Circle Pressure", names[5]);
            Assert.Equal("Cross Pressure", names[6]);
            Assert.Equal("Square Pressure", names[7]);
            Assert.Equal("R2 Pressure", names[11]);
        }

        [Fact]
        public void PressureChipsCarryTheButtonNameAlone()
        {
            // The chips sit under a pressure heading, so a chip reads
            // "Triangle", not "Triangle Pressure" cut short.
            foreach (var name in BlissBoxControllers.PressureNames)
                Assert.Equal(name, DevicesViewModel.PressureButton(name) + " Pressure");
            Assert.Equal("D-Pad Right", DevicesViewModel.PressureButton("D-Pad Right Pressure"));
            Assert.Equal("L1", DevicesViewModel.PressureButton("L1"));
        }

        private static DeviceObjectItem[] RawJoystick(int axes, int buttons)
        {
            var items = new List<DeviceObjectItem>();
            for (int i = 0; i < axes; i++)
                items.Add(new DeviceObjectItem { InputIndex = i, Name = "Axis " + i, ObjectTypeGuid = ObjectGuid.ZAxis, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis });
            items.Add(new DeviceObjectItem { InputIndex = 0, Name = "POV", ObjectTypeGuid = ObjectGuid.PovController, ObjectType = DeviceObjectTypeFlags.PointOfViewController });
            for (int i = 0; i < buttons; i++)
                items.Add(new DeviceObjectItem { InputIndex = i, Name = "Button " + i, ObjectTypeGuid = ObjectGuid.Button, ObjectType = DeviceObjectTypeFlags.PushButton });
            return items.ToArray();
        }

        [Fact]
        public void AnObjectListTakesTheControllersNames_TheArrowsAndThePressures()
        {
            var info = new BlissBoxInfo(BlissBoxControllers.TypeDualShock2, 0, 4, 86, 1);
            var named = BlissBoxRuntime.NameObjects(info, 8, RawJoystick(8, 24));
            Assert.Equal("Cross", named.Single(o => o.IsButton && o.InputIndex == 0).Name);
            Assert.Equal("Left Stick X", named.Single(o => o.IsAxis && o.InputIndex == 0).Name);
            Assert.Equal("D-Pad", named.Single(o => o.IsPov).Name);
            Assert.Equal("Up Arrow", named.Single(o => o.IsButton && o.InputIndex == 20).Name);
            Assert.Equal("Right Arrow", named.Single(o => o.IsButton && o.InputIndex == 23).Name);
            // The twelve pressures follow the eight joystick axes, as axes.
            var pressures = named.Where(o => o.IsAxis && o.InputIndex >= 8).OrderBy(o => o.InputIndex).ToArray();
            Assert.Equal(12, pressures.Length);
            Assert.Equal(8, pressures[0].InputIndex);
            Assert.All(pressures, p => Assert.False(p.IsSlider));
            Assert.Equal("Cross Pressure", pressures[6].Name);
            // An unnamed button keeps the joystick's name.
            Assert.Equal("Button 10", named.Single(o => o.IsButton && o.InputIndex == 10).Name);

            // A GPA sends arrows only on buttons it declares, so a joystick
            // short of them gets none appended.
            var n64 = BlissBoxRuntime.NameObjects(new BlissBoxInfo(19, 0, 4, 86, 1), 8, RawJoystick(8, 16));
            Assert.DoesNotContain(n64, o => o.IsButton && o.Name.EndsWith(" Arrow", StringComparison.Ordinal));
            Assert.DoesNotContain(n64, o => o.IsAxis && o.InputIndex >= 8);
            // PadForge's native poll fills them on a 3.x PlayStation digital
            // pad, so there they are appended.
            var mat = BlissBoxRuntime.NameObjects(new BlissBoxInfo(BlissBoxControllers.TypePlayStationDigital, 0, 3, 34, 1), 8, RawJoystick(8, 8));
            Assert.Equal(new[] { 10, 11, 12, 13 },
                mat.Where(o => o.IsButton && o.Name.EndsWith(" Arrow", StringComparison.Ordinal)).Select(o => o.InputIndex).OrderBy(i => i));
        }

        [Fact]
        public void TheArrowsSitWhereEachFirmwareSendsThem()
        {
            // 3.0 ORs them into buttons 10 to 13 (0x3295) for every pad but
            // the Zapper, GPA 4.86 into 20 to 23 (0x34A1) for every pad but the
            // Genesis 3-button and FM Towns pads, with the PC-FX's own bits
            // there too. 2.x names nothing.
            Assert.Equal(10, BlissBoxControllers.FirstArrowButton(BlissBoxControllers.TypePlayStationDigital, 3));
            Assert.Equal(10, BlissBoxControllers.FirstArrowButton(BlissBoxControllers.TypeNintendo64, 3));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(28, 3));
            Assert.Equal(20, BlissBoxControllers.FirstArrowButton(BlissBoxControllers.TypeDualShock2, 4));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(20, 4));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(26, 4));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(66, 4));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(BlissBoxControllers.TypeDualShock2, 2));
            // Only a pad with a D-pad can press opposite directions. The
            // Jaguar and the Atari 5200 carry keypad keys on buttons 10 to 13
            // on 3.x (3.0 0x2099, 0x1BDA), and the Zapper has no D-pad.
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(11, 3));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(6, 3));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(28, 4));
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(1, 3));
            var jaguar = BlissBoxRuntime.NameObjects(new BlissBoxInfo(11, 0, 3, 34, 1), 8, RawJoystick(8, 24));
            Assert.Equal("Button 10", jaguar.Single(o => o.IsButton && o.InputIndex == 10).Name);

            var ds2 = BlissBoxRuntime.NameObjects(new BlissBoxInfo(BlissBoxControllers.TypeDualShock2, 0, 3, 34, 1), 8, RawJoystick(8, 24));
            Assert.Equal("Up Arrow", ds2.Single(o => o.IsButton && o.InputIndex == 10).Name);
            Assert.Equal("Right Arrow", ds2.Single(o => o.IsButton && o.InputIndex == 13).Name);
            Assert.Equal("L3", ds2.Single(o => o.IsButton && o.InputIndex == 14).Name);
            Assert.Equal("Button 20", ds2.Single(o => o.IsButton && o.InputIndex == 20).Name);
            // A button the pad's own layout names keeps its name.
            var coleco = BlissBoxRuntime.NameObjects(new BlissBoxInfo(1, 0, 3, 34, 1), 8, RawJoystick(8, 24));
            Assert.Equal("Keypad 4", coleco.Single(o => o.IsButton && o.InputIndex == 10).Name);
            Assert.Equal("Keypad 5", coleco.Single(o => o.IsButton && o.InputIndex == 13).Name);
            var towns = BlissBoxRuntime.NameObjects(new BlissBoxInfo(66, 0, 4, 86, 1), 8, RawJoystick(8, 24));
            Assert.DoesNotContain(towns, o => o.Name.EndsWith(" Arrow", StringComparison.Ordinal));
        }

        [Fact]
        public void MotorsGoToTheControllersThatHaveThem()
        {
            // One motor on the GameCube, Dreamcast and N64 pads and the fishing
            // rod, two on the DualShock and DualShock 2. The neGcon has no
            // motor (psx-spx), and the adapter drives no JogCon force feedback.
            foreach (byte one in new byte[] { 9, 16, 19, 73 }) Assert.Equal(1, BlissBoxControllers.MotorCount(one));
            foreach (byte two in new byte[] { 115, 121 }) Assert.Equal(2, BlissBoxControllers.MotorCount(two));
            foreach (byte none in new byte[] { 0, 3, 8, 17, 51, 65, 83, 127 }) Assert.Equal(0, BlissBoxControllers.MotorCount(none));
        }

        [Fact]
        public void TheTriggerAxesRestAtZeroOnARawPort()
        {
            // Read raw, a port is a joystick, and a joystick's axes count as
            // centered, so a released trigger read as full deflection.
            Assert.True(BlissBoxControllers.IsTriggerAxis(9, 3, 2));
            Assert.True(BlissBoxControllers.IsTriggerAxis(9, 4, 5));
            Assert.True(BlissBoxControllers.IsTriggerAxis(16, 3, 5));
            Assert.True(BlissBoxControllers.IsTriggerAxis(8, 4, 2));
            Assert.False(BlissBoxControllers.IsTriggerAxis(19, 4, 2));
            Assert.False(BlissBoxControllers.IsTriggerAxis(9, 3, 6));
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("return (session?.LiveInfo ?? session?.KnownInfo) is { } info", runtime);
            Assert.Contains("&& BlissBoxControllers.IsTriggerAxis(info.Type, info.Major, axis);", runtime);
            Assert.Contains("PadForge.Common.Input.BlissBoxRuntime.RestsAtZero(dev, axis)",
                Repo("PadForge.App", "Common", "Input", "InputManager.Step3.MappingSetEval.cs"));
        }

        [Fact]
        public void TheMergeWritesPressuresAsAxesAndArrowsAsButtons()
        {
            var state = new CustomInputState();
            var pressure = new byte[12];
            pressure[6] = 255;
            pressure[0] = 128;
            BlissBoxRuntime.MergeInto(state, pressure, 0x01 | 0x08, BlissBoxRuntime.PressureAxisBase(8), 20);
            Assert.Equal(65535, state.Axis[8 + 6]);
            Assert.Equal(128 * 257, state.Axis[8]);
            Assert.Equal(0, state.Axis[8 + 11]);
            Assert.True(state.Buttons[20]);
            Assert.False(state.Buttons[21]);
            Assert.True(state.Buttons[23]);

            // No room past 12 declared axes: no pressure axes.
            Assert.Equal(12, BlissBoxRuntime.PressureAxisBase(12));
            Assert.Equal(-1, BlissBoxRuntime.PressureAxisBase(13));
            var untouched = new CustomInputState();
            BlissBoxRuntime.MergeInto(untouched, pressure, -1, -1, 20);
            Assert.All(untouched.Axis, a => Assert.Equal(0, a));
            Assert.All(untouched.Buttons, b => Assert.False(b));

            // On a 3.x adapter the native poll lands where the firmware's own
            // arrows do.
            var threeX = new CustomInputState();
            BlissBoxRuntime.MergeInto(threeX, null, 0x02, -1, 10);
            Assert.True(threeX.Buttons[11]);
            Assert.False(threeX.Buttons[21]);
        }

        [Fact]
        public void TheNamesTranslateThroughTheExistingStrings()
        {
            var s = Strings.Instance;
            Assert.Equal(s.Btn_Cross, MappingDisplayResolver.LocalizeObjectName("Cross"));
            Assert.Equal(s.DevObj_Start, MappingDisplayResolver.LocalizeObjectName("Start"));
            Assert.Equal(s.DevObj_Select, MappingDisplayResolver.LocalizeObjectName("Select"));
            Assert.Equal(string.Format(s.DevObj_Pressure_Format, s.Btn_Cross), MappingDisplayResolver.LocalizeObjectName("Cross Pressure"));
            Assert.Equal(string.Format(s.DevObj_Pressure_Format, s.DevObj_DPad + " " + s.POV_Right),
                MappingDisplayResolver.LocalizeObjectName("D-Pad Right Pressure"));
            Assert.Equal(string.Format(s.DevObj_Pressure_Format, "L1"), MappingDisplayResolver.LocalizeObjectName("L1 Pressure"));
            Assert.Equal(string.Format(s.DevObj_Arrow_Format, s.POV_Up), MappingDisplayResolver.LocalizeObjectName("Up Arrow"));
            Assert.Equal(string.Format(s.DevObj_CButton_Format, s.POV_Left), MappingDisplayResolver.LocalizeObjectName("C-Left"));
            Assert.Equal(s.DevObj_RightDPad + " " + s.POV_Down, MappingDisplayResolver.LocalizeObjectName("Right D-Pad Down"));
            Assert.Equal(string.Format(s.DevObj_Keypad_Format, "#"), MappingDisplayResolver.LocalizeObjectName("Keypad #"));
            Assert.Equal(string.Format(s.DevObj_Fire_Format, 2), MappingDisplayResolver.LocalizeObjectName("Fire 2"));
            Assert.Equal(string.Format(s.DevObj_Dial_Format, 1), MappingDisplayResolver.LocalizeObjectName("Dial 1"));
            Assert.Equal(s.DevObj_ZTrigger, MappingDisplayResolver.LocalizeObjectName("Z Trigger"));
            Assert.Equal(s.DevObj_CStickY, MappingDisplayResolver.LocalizeObjectName("C-Stick Y"));
            // Legends pass through as printed.
            Assert.Equal("II", MappingDisplayResolver.LocalizeObjectName("II"));
            Assert.Equal("ZL", MappingDisplayResolver.LocalizeObjectName("ZL"));
        }

        [Fact]
        public void EveryNameATableCanHandOutTranslatesOrIsALegend()
        {
            var names = new HashSet<string>();
            foreach (byte type in Enumerable.Range(0, 256).Select(i => (byte)i))
                foreach (byte major in new byte[] { 3, 4 })
                {
                    for (int i = 0; i < 32; i++)
                    {
                        if (BlissBoxControllers.ButtonName(type, major, i) is { } b) names.Add(b);
                        if (BlissBoxControllers.AxisName(type, major, i) is { } a) names.Add(a);
                    }
                }
            names.UnionWith(BlissBoxControllers.ArrowNames);
            names.UnionWith(BlissBoxControllers.PressureNames);
            names.Add("D-Pad");

            // Japanese translates every word the tables use, so a name that
            // comes back unchanged there fell through LocalizeObjectName.
            // Legends (A, II, L1, ZL) pass through by design, and so does the
            // 5200's PAUSE key, which shares the keyboard key's name.
            var untranslated = new List<string>();
            var thread = new Thread(() =>
            {
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("ja");
                foreach (var name in names)
                {
                    if (name.Length <= 3 || name == "Pause") continue;
                    if (MappingDisplayResolver.LocalizeObjectName(name) == name) untranslated.Add(name);
                }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            Assert.Empty(untranslated);
        }

        // ── Rumble routing ──

        [Fact]
        public void EverySiteThatRoutesThePadixMotorsRoutesABlissBoxPort()
        {
            string step2 = Repo("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            Assert.Contains("bool isBlissBox = PadForge.Engine.Common.BlissBox.BlissBoxApi.OwnsRumble(ud.VendorId, ud.ProdId);", step2);
            Assert.Contains("if (!isXboxImpulse && !isVendorFfb && !isPadixConverter && !isBlissBox)", step2);
            Assert.Contains("else if ((isXboxImpulse || isPadixConverter || isBlissBox) && ud.Device == null)", step2);
            Assert.Contains("BlissBoxRuntime.SetRumble(ud.DevicePath, 0, 0);", step2);
            Assert.Contains("if (!BlissBoxRuntime.SetRumble(ud.DevicePath, blissL, blissR))", step2);
            int dispatch = step2.IndexOf("if (!BlissBoxRuntime.SetRumble(ud.DevicePath, blissL, blissR))", StringComparison.Ordinal);
            int sdl = step2.IndexOf("ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);", StringComparison.Ordinal);
            Assert.True(dispatch > 0 && sdl > dispatch, "the Bliss-Box dispatch must precede the SDL write");

            string wrapper = Repo("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            int setRumble = wrapper.IndexOf("public bool SetRumble(ushort lowFreq, ushort highFreq, uint durationMs = uint.MaxValue)", StringComparison.Ordinal);
            int guard = wrapper.IndexOf("if (BlissBoxApi.OwnsRumble(VendorId, ProductId))", StringComparison.Ordinal);
            int call = wrapper.IndexOf("return SDL_RumbleJoystick(Joystick, lowFreq, highFreq, durationMs);", StringComparison.Ordinal);
            Assert.True(setRumble > 0 && guard > setRumble && call > guard, "SetRumble must refuse an owned port before calling SDL");
            Assert.Contains("get => _hasRumble || BlissBoxApi.OwnsRumble(VendorId, ProductId);", wrapper);

            // The stop-all sweep drops a pulse still owed as it stops the port.
            Assert.Contains("BlissBoxRuntime.StopRumble(ud.DevicePath);", Repo("PadForge.App", "Common", "Input", "InputManager.cs"));
            string service = Repo("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("PadForge.Common.Input.BlissBoxRuntime.SetRumble(\n                                    ud.DevicePath, bvib.LeftMotorSpeed, bvib.RightMotorSpeed);", service);
            Assert.Contains("PadForge.Common.Input.BlissBoxRuntime.SetRumble(ud.DevicePath, left, right);", service);
        }

        [Fact]
        public void TheSwitchGatesTheEnginesRumbleOwnership()
        {
            bool saved = BlissBoxApi.Enabled;
            try
            {
                BlissBoxApi.Enabled = false;
                Assert.False(BlissBoxApi.OwnsRumble(0x16D0, 0x0D04));
                BlissBoxApi.Enabled = true;
                Assert.True(BlissBoxApi.OwnsRumble(0x16D0, 0x0D04));
                Assert.False(BlissBoxApi.OwnsRumble(0x16D0, 0x0A60));
                Assert.True(BlissBoxRuntime.Enabled);
            }
            finally { BlissBoxApi.Enabled = saved; }
        }

        // ── Reading a port raw ──

        [Fact]
        public void APortOpensRawWhileTheSwitchIsOn()
        {
            // SDL maps all four port IDs as a "4Play Adapter" gamepad through
            // the fork's community table, so the switch has to turn that off
            // for the port's names, pressures and arrows to reach the picker.
            bool saved = BlissBoxApi.Enabled;
            try
            {
                BlissBoxApi.Enabled = false;
                Assert.True(SdlDeviceWrapper.OpensAsGamepad(true, 0x16D0, 0x0D04));
                BlissBoxApi.Enabled = true;
                foreach (ushort pid in new ushort[] { 0x0D04, 0x0D05, 0x0D06, 0x0D07 })
                    Assert.False(SdlDeviceWrapper.OpensAsGamepad(true, 0x16D0, pid));
                // Any other mapped device still opens as a gamepad, and an
                // unmapped one never does.
                Assert.True(SdlDeviceWrapper.OpensAsGamepad(true, 0x054C, 0x0CE6));
                Assert.False(SdlDeviceWrapper.OpensAsGamepad(false, 0x16D0, 0x0D04));
                Assert.True(BlissBoxApi.ReadsRaw(0x16D0, 0x0D04));
                Assert.False(BlissBoxApi.ReadsRaw(0x16D0, 0x0A60));
            }
            finally { BlissBoxApi.Enabled = saved; }
        }

        [Fact]
        public void ARawPortIsAJoystick_NotTheGamepadSdlTypesIt()
        {
            var gamepad = SDL3.SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
            Assert.Equal(InputDeviceType.Joystick, SdlDeviceWrapper.InputDeviceTypeFor(gamepad, false, 0x16D0, 0x0D04));
            Assert.Equal(InputDeviceType.Gamepad, SdlDeviceWrapper.InputDeviceTypeFor(gamepad, true, 0x16D0, 0x0D04));
            // Nothing changes for any other device.
            Assert.Equal(InputDeviceType.Gamepad, SdlDeviceWrapper.InputDeviceTypeFor(gamepad, false, 0x054C, 0x0CE6));
            Assert.Equal(InputDeviceType.Driving,
                SdlDeviceWrapper.InputDeviceTypeFor(SDL3.SDL.SDL_JoystickType.SDL_JOYSTICK_TYPE_WHEEL, false, 0x046D, 0xC24F));
        }

        [Fact]
        public void ARowOpenTheOtherWayFromTheSwitchIsReopened()
        {
            Assert.True(InputManager.BlissBoxRowNeedsReopen(openedAsGamepad: true, readRaw: true));
            Assert.True(InputManager.BlissBoxRowNeedsReopen(openedAsGamepad: false, readRaw: false));
            Assert.False(InputManager.BlissBoxRowNeedsReopen(openedAsGamepad: false, readRaw: true));
            Assert.False(InputManager.BlissBoxRowNeedsReopen(openedAsGamepad: true, readRaw: false));

            // The reopen runs in Phase 1l before the ports are paired, so a
            // port opened on the switch's first cycle meets a raw row.
            string code = Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs");
            int reopen = code.IndexOf("ReopenBlissBoxRows(enabled)", StringComparison.Ordinal);
            int sync = code.IndexOf("BlissBoxRuntime.Sync(rows)", StringComparison.Ordinal);
            Assert.True(reopen > 0 && sync > reopen);
            // The wrapper decides at open, from SDL's answer and the port IDs.
            string wrapper = Repo("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            Assert.Contains("if (OpensAsGamepad(SDL_IsGamepad(instanceId),", wrapper);
        }

        [Fact]
        public void TheMergeRunsBeforeTheIdleDetector()
        {
            string step2 = Repo("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            int merge = step2.IndexOf("BlissBoxRuntime.Merge(ud, newState);", StringComparison.Ordinal);
            int idle = step2.IndexOf("UpdateIdleDisconnect(ud, newState);", StringComparison.Ordinal);
            int publish = step2.IndexOf("ud.InputState = newState;", StringComparison.Ordinal);
            Assert.True(merge > 0 && merge < publish && publish < idle, "the merge must land before the state is published");
        }

        // ── Settings ──

        [Fact]
        public void TheSwitch_IsOffByDefault_ResetsOff_AndPersists()
        {
            var vm = new SettingsViewModel();
            bool saved = BlissBoxApi.Enabled;
            try
            {
                Assert.False(vm.BlissBoxEnabled);
                vm.BlissBoxEnabled = true;
                Assert.True(BlissBoxApi.Enabled);
                vm.ResetSettingCommand.Execute(nameof(SettingsViewModel.BlissBoxEnabled));
                Assert.False(vm.BlissBoxEnabled);
                Assert.False(BlissBoxApi.Enabled);
            }
            finally { BlissBoxApi.Enabled = saved; }

            Assert.Contains("nameof(SettingsViewModel.BlissBoxEnabled)", Repo("PadForge.App", "MainWindow.xaml.cs"));
            string settings = Repo("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("vm.BlissBoxEnabled = appSettings.BlissBoxEnabled;", settings);
            Assert.Contains("BlissBoxEnabled = vm.BlissBoxEnabled,", settings);
            Assert.Contains("vm.BlissBoxPorts = BlissBoxPortData.Normalize(appSettings.BlissBoxPorts);", settings);
            string page = Repo("PadForge.App", "Views", "SettingsPage.xaml");
            int analog = page.IndexOf("Binding Settings_AnalogKeyboards,", StringComparison.Ordinal);
            int bliss = page.IndexOf("Binding Settings_BlissBox,", StringComparison.Ordinal);
            Assert.True(analog > 0 && bliss > analog, "the Bliss-Box row sits after the analog keyboards row");
        }

        [Fact]
        public void PortChoicesDropUnreadableAndRepeatedDevices()
        {
            var guid = Guid.NewGuid();
            var loaded = BlissBoxPortData.Normalize(new[]
            {
                new BlissBoxPortData { Device = guid.ToString("N"), ScreenMode = DreamcastScreenMode.Clock },
                new BlissBoxPortData { Device = guid.ToString("D"), ScreenMode = DreamcastScreenMode.Picture },
                new BlissBoxPortData { Device = "not a guid" },
                null,
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), ScreenMode = (DreamcastScreenMode)99, NativeArrows = true },
                // Every value at its default once the mode is read back.
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), ScreenMode = (DreamcastScreenMode)99 },
            });
            Assert.Equal(2, loaded.Count);
            Assert.Equal(DreamcastScreenMode.Clock, loaded[0].ScreenMode);
            Assert.Equal(guid.ToString("D"), loaded[0].Device);
            Assert.Equal(DreamcastScreenMode.Adapter, loaded[1].ScreenMode);
            Assert.Empty(BlissBoxPortData.Normalize(null));
        }

        [Fact]
        public void APictureThatDoesNotDecodeIsDroppedOnLoad()
        {
            // A copy of the adapter's own picture that never decodes would
            // block a new copy, the restore and the entry's cleanup for good.
            string good = Convert.ToBase64String(new byte[192]);
            var loaded = BlissBoxPortData.Normalize(new[]
            {
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), ScreenMode = DreamcastScreenMode.Clock, AdapterPicture = "AAAA" },
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), ScreenMode = DreamcastScreenMode.Picture, Picture = "not base64!", AdapterPicture = good },
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), AdapterPicture = Convert.ToBase64String(new byte[191]) },
            });
            Assert.Equal(2, loaded.Count);
            Assert.Null(loaded[0].AdapterPicture);
            Assert.Equal(DreamcastScreenMode.Clock, loaded[0].ScreenMode);
            Assert.Null(loaded[1].Picture);
            Assert.Equal(good, loaded[1].AdapterPicture);
        }

        [Fact]
        public void TheFirstCopyOfAnAdaptersPictureIsSavedAtOnce()
        {
            // Saved before the port writes over the original, not after the
            // autosave's quiet time, which a crash, a kill or a reload beats,
            // and with the autosave's follow-ups, which a direct Save kept the
            // timer from raising.
            string wiring = Repo("PadForge.App", "Services", "InputService.BlissBox.cs");
            Assert.Contains("() => _settingsService?.SaveNow() ?? true,", wiring);
            string settings = Repo("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("() => _settingsService?.SaveCount ?? 0);", wiring);
            // Counted once the file is written and only then, so a failed save
            // never releases the hold.
            int written = settings.IndexOf("File.WriteAllBytes(filePath, serializedBytes);", StringComparison.Ordinal);
            int counted = settings.IndexOf("System.Threading.Interlocked.Increment(ref _saveCount);", StringComparison.Ordinal);
            Assert.True(written > 0 && counted > written);
            Assert.Equal(counted, settings.LastIndexOf("Increment(ref _saveCount)", StringComparison.Ordinal) - "System.Threading.Interlocked.".Length);
            Assert.Contains("            if (!Save()) return false;\n            AutoSaved?.Invoke(this, EventArgs.Empty);", settings);
            Assert.Contains("public bool Save() => SaveToFile(_settingsFilePath);", settings);
            string tick = Repo("PadForge.App", "Services", "DreamcastScreenService.cs");
            int check = tick.IndexOf("if (!MayReplace(port.InstanceGuid, data, stored, wire)) continue;", StringComparison.Ordinal);
            int write = tick.IndexOf("if (session.SetScreen(wire)) port.Wake();", check, StringComparison.Ordinal);
            Assert.True(check > 0 && write > check);
        }

        [Fact]
        public void PlayTimeSurvivesAMomentOutOfThePort()
        {
            // The adapter searching for a moment, or the channel reopening, is
            // not a new session. A pad gone for longer starts again. The port
            // is never started, so no worker runs.
            var service = new DreamcastScreenService(new SettingsViewModel(), null);
            var port = new BlissBoxPort(@"\\?\hid#padforge-play-time-test", 0x0D04, Guid.NewGuid(), 1);
            try
            {
                var pad = new BlissBoxInfo(BlissBoxControllers.TypeDreamcast, 0, 4, 86, 1);
                Assert.True(service.TrackPad(port, pad, 0));
                Assert.False(service.TrackPad(port, null, 5000));
                Assert.False(service.TrackPad(port, new BlissBoxInfo(BlissBoxControllers.TypeNintendo64, 0, 4, 86, 1), 6000));
                Assert.True(service.TrackPad(port, pad, 9000));
                Assert.Equal(0, service.PlayTimeStart(port, 9000));
                Assert.False(service.TrackPad(port, null, 9500));
                long back = 9000 + DreamcastScreenService.PlayTimeGraceMs + 1;
                Assert.True(service.TrackPad(port, pad, back));
                Assert.Equal(back, service.PlayTimeStart(port, back));
            }
            finally { port.Dispose(); }
        }

        [Fact]
        public void TheAdaptersPictureIsKeptUntilItsCopyIsSaved()
        {
            // The copy is the only one once the adapter's picture is
            // replaced, and a save that failed leaves it in memory alone.
            bool saves = false;
            int written = 0;
            var service = new DreamcastScreenService(new SettingsViewModel(), null, () => saves, () => written);
            var device = Guid.NewGuid();
            var stored = new byte[192];
            var wire = Enumerable.Repeat((byte)0xFF, 192).ToArray();
            Assert.False(service.MayReplace(device, null, stored, wire));
            Assert.Equal(Convert.ToBase64String(stored), service.Get(device).AdapterPicture);
            // A reload that finds no file clears the unsaved flag without a
            // write, so only a later write lets the picture through.
            Assert.False(service.MayReplace(device, service.Get(device), stored, wire));
            written++;
            Assert.True(service.MayReplace(device, service.Get(device), stored, wire));
            // A save that works lets the first picture through at once.
            saves = true;
            Assert.True(service.MayReplace(Guid.NewGuid(), null, stored, wire));
            // Nothing to replace, nothing to copy.
            var same = Guid.NewGuid();
            Assert.True(service.MayReplace(same, null, stored, (byte[])stored.Clone()));
            Assert.Null(service.Get(same));
        }

        [Fact]
        public void AnEmptyEntryNeverHidesARealOneForTheSameDevice()
        {
            var guid = Guid.NewGuid();
            var loaded = BlissBoxPortData.Normalize(new[]
            {
                new BlissBoxPortData { Device = guid.ToString(), AdapterPicture = "broken" },
                new BlissBoxPortData { Device = guid.ToString(), ScreenMode = DreamcastScreenMode.Clock, AdapterPicture = Convert.ToBase64String(new byte[192]) },
            });
            var kept = Assert.Single(loaded);
            Assert.Equal(DreamcastScreenMode.Clock, kept.ScreenMode);
            Assert.NotNull(kept.AdapterPicture);
        }

        [Fact]
        public void APortsChoicesAreKeptUntilTheyAreAllDefaults()
        {
            var vm = new SettingsViewModel();
            int dirty = 0;
            var service = new DreamcastScreenService(vm, () => dirty++);
            var device = Guid.NewGuid();
            service.Update(device, d => d.NativeArrows = true);
            Assert.True(service.Get(device).NativeArrows);
            Assert.Single(vm.BlissBoxPorts);
            service.Update(device, d => d.NativeArrows = false);
            Assert.Null(service.Get(device));
            Assert.Empty(vm.BlissBoxPorts);
            Assert.Equal(2, dirty);
        }

        // ── The Dreamcast screen ──

        [Fact]
        public void FramesKeepEightAtMost_AndSkipWhatIsNotAPicture()
        {
            var frames = Enumerable.Range(0, 10).Select(i => Enumerable.Repeat((byte)i, 192).ToArray()).ToList();
            string encoded = DreamcastScreenService.EncodeFrames(frames);
            var decoded = DreamcastScreenService.DecodeFrames(encoded);
            Assert.Equal(8, decoded.Count);
            Assert.Equal(frames[7], decoded[7]);
            Assert.Single(DreamcastScreenService.DecodeFrames("not base64," + Convert.ToBase64String(frames[1])));
            Assert.Empty(DreamcastScreenService.DecodeFrames(Convert.ToBase64String(new byte[10])));
            Assert.Empty(DreamcastScreenService.DecodeFrames(null));
        }

        [Fact]
        public void PlayTimeReadsHoursAndMinutes()
        {
            Assert.Equal("0:00", DreamcastScreenService.FormatPlayTime(TimeSpan.FromSeconds(59)));
            Assert.Equal("1:05", DreamcastScreenService.FormatPlayTime(TimeSpan.FromMinutes(65)));
            Assert.Equal("26:00", DreamcastScreenService.FormatPlayTime(TimeSpan.FromHours(26)));
        }

        [Fact]
        public void TheProfileNumberCountsFromTheDefaultProfile()
        {
            string savedId = SettingsManager.ActiveProfileId;
            var savedProfiles = SettingsManager.Profiles;
            try
            {
                SettingsManager.Profiles = new List<ProfileData>
                {
                    new ProfileData { Id = "a", Name = "Racing" },
                    new ProfileData { Id = "b", Name = "Fighting" },
                };
                SettingsManager.ActiveProfileId = null;
                Assert.Equal(0, DreamcastScreenService.ActiveProfileNumber());
                Assert.Equal(Strings.Instance.Profile_Default, DreamcastScreenService.ActiveProfileName());
                SettingsManager.ActiveProfileId = "b";
                Assert.Equal(2, DreamcastScreenService.ActiveProfileNumber());
                Assert.Equal("Fighting", DreamcastScreenService.ActiveProfileName());
            }
            finally
            {
                SettingsManager.Profiles = savedProfiles;
                SettingsManager.ActiveProfileId = savedId;
            }
        }

        [Fact]
        public void TextAndPicturesRenderToOneBitAPixel()
        {
            RunSta(() =>
            {
                var clock = DreamcastScreenService.RenderText("12:34");
                Assert.Equal(192, clock.Length);
                int dark = Enumerable.Range(0, 48 * 32).Count(p => BlissBoxScreen.IsDark(clock, p % 48, p / 48));
                Assert.InRange(dark, 40, 48 * 32 / 2);
                Assert.All(Enumerable.Range(0, 192), i => Assert.True(DreamcastScreenService.RenderText(string.Empty)[i] == 0));

                // A 48 by 32 PNG, black on its left half: the left half reads dark.
                var pixels = new byte[48 * 32 * 4];
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 48; x++)
                    {
                        int i = (y * 48 + x) * 4;
                        byte v = x < 24 ? (byte)0 : (byte)255;
                        pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 255;
                    }
                var bitmap = BitmapSource.Create(48, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 48 * 4);
                string path = Path.Combine(Path.GetTempPath(), "padforge-vmu-" + Guid.NewGuid().ToString("N") + ".png");
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(path)) encoder.Save(stream);
                    var image = DreamcastScreenService.ImportPicture(path);
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 48; x++)
                            Assert.Equal(x < 24, BlissBoxScreen.IsDark(image, x, y));
                }
                finally { File.Delete(path); }
            });
        }

        [Fact]
        public void ImportPicksTheFormatByExtension()
        {
            string lcd = Path.Combine(Path.GetTempPath(), "padforge-vmu-" + Guid.NewGuid().ToString("N") + ".lcd");
            string vms = Path.ChangeExtension(lcd, ".vms");
            try
            {
                var lcdFile = new byte[BlissBoxScreen.LcdPixelOffset + 48 * 32];
                lcdFile[BlissBoxScreen.LcdPixelOffset + 49] = BlissBoxScreen.LcdDark; // (1, 1)
                File.WriteAllBytes(lcd, lcdFile);
                Assert.True(BlissBoxScreen.IsDark(DreamcastScreenService.ImportPicture(lcd), 1, 1));

                var vmsFile = new byte[0x60 + 128];
                BitConverter.GetBytes(0x60u).CopyTo(vmsFile, 16);
                vmsFile[0x60] = 0x80; // icon (0, 0)
                File.WriteAllBytes(vms, vmsFile);
                Assert.True(BlissBoxScreen.IsDark(DreamcastScreenService.ImportPicture(vms), 8, 0));
            }
            finally
            {
                File.Delete(lcd);
                File.Delete(vms);
            }
        }

        // ── Show Dreamcast Screen ──

        private static MacroAction Show(string frames, int frameMs = 1500, int repeat = 2) => new()
        {
            Type = MacroActionType.ShowDreamcastScreen,
            DreamcastFrames = frames,
            DreamcastFrameMs = frameMs,
            DreamcastRepeat = repeat,
        };

        private static List<(int Pad, string Frames, int FrameMs, int Repeat)> Recording(Action body)
        {
            var calls = new List<(int, string, int, int)>();
            var saved = InputManager.MacroDreamcastSink;
            InputManager.MacroDreamcastSink = (pad, frames, frameMs, repeat) => calls.Add((pad, frames, frameMs, repeat));
            try { body(); }
            finally { InputManager.MacroDreamcastSink = saved; }
            return calls;
        }

        [Fact]
        public void BothMacroLoopsStartTheShowOncePerFiring()
        {
            string frames = Convert.ToBase64String(new byte[192]);
            var im = new InputManager();
            var gamepadMacro = new MacroItem
            {
                Name = "vmu",
                IsEnabled = true,
                PadIndex = 2,
                TriggerButtons = Gamepad.A,
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            gamepadMacro.Actions.Add(Show(frames));
            var calls = Recording(() =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var gp = new Gamepad { Buttons = Gamepad.A };
                    im.EvaluateSlotMacros(ref gp, new[] { gamepadMacro });
                }
            });
            var call = Assert.Single(calls);
            Assert.Equal((2, frames, 1500, 2), call);

            var raw = new MacroItem
            {
                Name = "raw",
                IsEnabled = true,
                PadIndex = 1,
                TriggerCustomButtons = "00000001,00000000,00000000,00000000",
                TriggerMode = MacroTriggerMode.OnPress,
                RepeatMode = MacroRepeatMode.Once,
                ConsumeTriggerButtons = false,
            };
            raw.Actions.Add(Show(frames));
            var extended = Recording(() =>
            {
                var state = RawHidState.Create(8, 32, 1);
                state.Buttons[0] = 1;
                im.EvaluateSlotMacrosExtended(ref state, new[] { raw });
            });
            Assert.Equal(1, Assert.Single(extended).Pad);
        }

        [Fact]
        public void AShowWithNoPortOpenIsDropped_NotQueued()
        {
            Assert.Empty(BlissBoxRuntime.Ports);
            for (int i = 0; i < 100; i++)
                DreamcastScreenService.RequestShow(0, Convert.ToBase64String(new byte[192]), 1000, 1);
            Assert.Equal(0, DreamcastScreenService.PendingShows);
        }

        [Fact]
        public void TheActionSurvivesSaveAndLoad_AndHoldsASecondAtLeast()
        {
            string frames = DreamcastScreenService.EncodeFrames(new[] { new byte[192], Enumerable.Repeat((byte)0xAA, 192).ToArray() });
            var item = new MacroItem { Name = "rt" };
            item.Actions.Add(Show(frames, frameMs: 2500, repeat: 3));
            var data = SettingsService.BuildMacroDataForMacro(item, 0);
            var back = Assert.Single(SettingsService.LoadMacroFromData(data, VirtualControllerType.Xbox, null).Actions);
            Assert.Equal(MacroActionType.ShowDreamcastScreen, back.Type);
            Assert.Equal(frames, back.DreamcastFrames);
            Assert.Equal(2500, back.DreamcastFrameMs);
            Assert.Equal(3, back.DreamcastRepeat);
            Assert.Equal(2, back.DreamcastFrameCount);

            var quick = Show(frames, frameMs: 10, repeat: 0);
            Assert.Equal(DreamcastScreenService.MinFrameMs, quick.DreamcastFrameMs);
            Assert.Equal(1, quick.DreamcastRepeat);
        }

        [Fact]
        public void TheEditorAddsAndRemovesPictures_EightAtMost()
        {
            var action = Show(string.Empty);
            Assert.True(action.IsShowDreamcastScreenType);
            Assert.False(action.IsDurationType);
            for (int i = 0; i < 10; i++) action.AddDreamcastFrame(Enumerable.Repeat((byte)i, 192).ToArray());
            Assert.Equal(8, action.DreamcastFrameCount);
            Assert.False(action.CanAddDreamcastFrame);
            action.RemoveDreamcastFrame(0);
            Assert.Equal(7, action.DreamcastFrameCount);
            Assert.Equal(Enumerable.Repeat((byte)1, 192).ToArray(), DreamcastScreenService.DecodeFrames(action.DreamcastFrames)[0]);
            Assert.Equal(string.Format(Strings.Instance.MacroAction_ShowDreamcastScreen_Format, 7, 1500, 2), action.DisplayText);

            var choice = Assert.Single(MacroTypeCatalog.Choices, c => c.Type == MacroActionType.ShowDreamcastScreen);
            Assert.Equal(Strings.Instance.Macro_Cat_Leds, choice.Category);
            Assert.Equal(Strings.Instance.MacroAction_ShowDreamcastScreen_Tooltip, choice.Tooltip);

            action.ResetSettingCommand.Execute(nameof(MacroAction.DreamcastFrames));
            Assert.Equal(0, action.DreamcastFrameCount);
        }

        // ── Review round three ──

        [Fact]
        public void PressureAxesRestAtZero_WhicheverControllerIsInThePort()
        {
            // The #443 rule: an axis activator reads a centered axis as -1 at
            // rest, so a pressure axis, 0 at rest, must count as one-way. The
            // rule follows the row's shape: a row read raw keeps it until Step
            // 1 reopens the row through SDL's gamepad mapping, whatever the
            // switch says meanwhile.
            bool saved = BlissBoxApi.Enabled;
            var raw = new SdlDeviceWrapper();
            try
            {
                var port = new UserDevice { VendorId = 0x16D0, ProdId = 0x0D04, Device = raw };
                BlissBoxApi.Enabled = true;
                Assert.True(InputManager.AxisRestsAtZero("Axis 8", port));
                Assert.True(InputManager.AxisRestsAtZero("Axis 19", port));
                Assert.False(InputManager.AxisRestsAtZero("Axis 7", port));
                Assert.False(InputManager.AxisRestsAtZero("Axis 20", port));
                // Another device's axis 8 is untouched.
                Assert.False(InputManager.AxisRestsAtZero("Axis 8", new UserDevice { VendorId = 0x054C, ProdId = 0x0268, Device = raw }));
                // Switched off, the row is still raw until the reopen.
                BlissBoxApi.Enabled = false;
                Assert.True(InputManager.AxisRestsAtZero("Axis 8", port));
                // A row that is not an SDL joystick read raw never takes it.
                port.Device = DispatchProxy.Create<ISdlInputDevice, RumbleRecorder>();
                Assert.False(InputManager.AxisRestsAtZero("Axis 8", port));
            }
            finally
            {
                BlissBoxApi.Enabled = saved;
                raw.Dispose();
            }
        }

        [Fact]
        public void AShowGivesEachFrameItsTimeOnceTheAdapterHoldsIt()
        {
            // Frames once followed a fixed schedule, and the EEPROM guard's
            // second between writes then dropped some.
            var a = new byte[192]; a[0] = 0x80;
            var b = new byte[192]; b[1] = 0x80;
            var show = new DreamcastShow(new List<byte[]> { a, b }, 1000, 1, now: 0);
            var other = new byte[192];
            Assert.Equal(a, show.Frame(other, 0));
            Assert.Equal(a, show.Frame(other, 1500));
            Assert.Equal(a, show.Frame(BlissBoxScreen.ToWire(a), 1600));
            Assert.Equal(a, show.Frame(BlissBoxScreen.ToWire(a), 2599));
            Assert.Equal(b, show.Frame(BlissBoxScreen.ToWire(a), 2600));
            Assert.Equal(b, show.Frame(BlissBoxScreen.ToWire(b), 2700));
            Assert.Null(show.Frame(BlissBoxScreen.ToWire(b), 3700));
        }

        [Fact]
        public void AFrameThatNeverArrivesCountsFromWhenItBecameCurrent()
        {
            var a = new byte[192]; a[0] = 0x80;
            var show = new DreamcastShow(new List<byte[]> { a }, 1000, 1, now: 0);
            var other = new byte[192];
            Assert.Equal(a, show.Frame(other, DreamcastShow.DeliveryLimitMs - 1));
            Assert.Null(show.Frame(other, DreamcastShow.DeliveryLimitMs));
        }

        [Fact]
        public void AFrameHoldsTheGuardsSecondWhateverTheActionSays()
        {
            var a = new byte[192]; a[0] = 0x80;
            var wire = BlissBoxScreen.ToWire(a);
            var show = new DreamcastShow(new List<byte[]> { a }, 200, 1, now: 0);
            Assert.Equal(a, show.Frame(wire, 0));
            // 200 ms would have ended it here.
            Assert.Equal(a, show.Frame(wire, 500));
            Assert.Null(show.Frame(wire, DreamcastScreenService.MinFrameMs));
        }

        [Fact]
        public void AResetKeepsEachPortsCopyOfTheAdaptersPicture()
        {
            string picture = Convert.ToBase64String(new byte[192]);
            var ports = new[]
            {
                new BlissBoxPortData { Device = Guid.NewGuid().ToString("D"), ScreenMode = DreamcastScreenMode.Clock, AdapterPicture = picture, NativeArrows = true },
                new BlissBoxPortData { Device = Guid.NewGuid().ToString("D"), ScreenMode = DreamcastScreenMode.Clock },
            };
            var kept = Assert.Single(BlissBoxPortData.KeepAdapterPictures(ports));
            Assert.Equal(ports[0].Device, kept.Device);
            Assert.Equal(picture, kept.AdapterPicture);
            Assert.Equal(DreamcastScreenMode.Adapter, kept.ScreenMode);
            Assert.False(kept.NativeArrows);
            Assert.Null(BlissBoxPortData.KeepAdapterPictures(new[] { ports[1] }));

            string reset = Repo("PadForge.App", "Services", "SettingsService.cs");
            Assert.Contains("BlissBoxPorts = BlissBoxPortData.KeepAdapterPictures(_mainVm.Settings.BlissBoxPorts),", reset);
        }

        [Fact]
        public void ExportingTheDefaultProfileCarriesItsPicture()
        {
            // The Default profile's picture lives in the settings file, not on
            // the snapshot the export writes.
            string code = Repo("PadForge.App", "MainWindow.xaml.cs");
            Assert.Contains("profile.DreamcastPicture = _viewModel.Settings.DefaultProfileDreamcastPicture;", code);
            Assert.Contains("profile.DreamcastPicture = priorPicture;", code);
        }

        [Fact]
        public void ThePreviewRebuildsWhenTheRowIsOpenedAgain()
        {
            // A reopen keeps the row's guid, and the preview once rebuilt only
            // when the guid changed.
            string code = Repo("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("|| !ReferenceEquals(ud.Device, _lastRawStateDevice))", code);
        }

        [Fact]
        public void TheSwitchsReopenKeepsTheRowsMotorState()
        {
            // The reopen is the same SDL connection, so the levels a game or a
            // Remote Link peer last asked for are still owed. A fresh state
            // handed the new port zero, and a peer sends a steady level once.
            var a = new SdlDeviceWrapper { SdlInstanceId = 7 };
            var b = new SdlDeviceWrapper { SdlInstanceId = 7 };
            var c = new SdlDeviceWrapper { SdlInstanceId = 8 };
            try
            {
                Assert.True(UserDevice.SameConnection(a, a));
                Assert.True(UserDevice.SameConnection(a, b));
                Assert.False(UserDevice.SameConnection(a, c));
                Assert.False(UserDevice.SameConnection(null, a));
                Assert.False(UserDevice.SameConnection(new SdlDeviceWrapper(), new SdlDeviceWrapper()));
                // The Remote Link exposure holds the old wrapper, which the
                // reopen disposes, clearing its instance ID.
                var old = new SdlDeviceWrapper { SdlInstanceId = 7 };
                old.Dispose();
                Assert.Equal(0u, old.SdlInstanceId);
                Assert.True(UserDevice.SameConnection(old, b));
                Assert.False(UserDevice.SameConnection(old, c));
            }
            finally
            {
                a.Dispose();
                b.Dispose();
                c.Dispose();
            }

            // LoadFromSdlDevice keeps the row's motor state for the same
            // connection and starts a new one for another. With the switch on
            // a port owns its rumble, so it has a state at all.
            bool savedSwitch = BlissBoxApi.Enabled;
            var first = PortWrapper(7);
            var second = PortWrapper(7);
            var other = PortWrapper(8);
            try
            {
                BlissBoxApi.Enabled = true;
                var ud = new UserDevice();
                ud.LoadFromSdlDevice(first);
                var state = ud.ForceFeedbackState;
                Assert.NotNull(state);
                ud.LoadFromSdlDevice(second);
                Assert.Same(state, ud.ForceFeedbackState);
                ud.LoadFromSdlDevice(other);
                Assert.NotSame(state, ud.ForceFeedbackState);
            }
            finally
            {
                BlissBoxApi.Enabled = savedSwitch;
                first.Dispose();
                second.Dispose();
                other.Dispose();
            }

            string code = Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs");
            Assert.Contains("BlissBoxRuntime.TakeMotors(ud.DevicePath, state.LeftMotorSpeed, state.RightMotorSpeed);", code);
            Assert.Contains("try { return state.ResendScalar(ud.Device); }", code);
            // A switch that went off and on again between two passes hands
            // every open port its level again.
            Assert.Contains("            if (toggled)\n                foreach (var port in BlissBoxRuntime.Ports) _blissBoxHandoffs.Add(port.InstanceGuid);", code);
            Assert.Contains("bool changed = (enabled || wasOn || toggled) && ReopenBlissBoxRows(enabled);", code);
        }

        /// <summary>An unopened wrapper that reads as a Bliss-Box port.</summary>
        private static SdlDeviceWrapper PortWrapper(uint instance)
        {
            var wrapper = new SdlDeviceWrapper { SdlInstanceId = instance };
            typeof(SdlDeviceWrapper).GetProperty(nameof(SdlDeviceWrapper.VendorId)).SetValue(wrapper, (ushort)0x16D0);
            typeof(SdlDeviceWrapper).GetProperty(nameof(SdlDeviceWrapper.ProductId)).SetValue(wrapper, (ushort)0x0D04);
            return wrapper;
        }

        [Fact]
        public void SdlGetsTheRecordedLevelsBackAtOnce()
        {
            // Switched off, SDL takes the port with the level a game or a peer
            // last asked for, not the next change, which a peer holding a
            // steady level never sends.
            var device = DispatchProxy.Create<ISdlInputDevice, RumbleRecorder>();
            var recorder = (RumbleRecorder)(object)device;
            var state = new ForceFeedbackState();
            // A write SDL refused leaves the cache behind the level asked for.
            recorder.Accept = false;
            state.SetDeviceForces(null, device, new PadSetting(), new Vibration(30000, 1000));
            recorder.Accept = true;
            recorder.Sent.Clear();
            Assert.True(state.ResendScalar(device));
            // A stop goes first: SDL skips a write that repeats the levels it
            // last took (SDL_joystick.c:2287-2290), and a GPA's final stop from
            // the retired port can end a level SDL took while it retired.
            Assert.Equal(new[] { (0, 0), (30000, 1000) }, recorder.Sent);
            // The cache now matches, so the same level is not written twice.
            state.SetDeviceForces(null, device, new PadSetting(), new Vibration(30000, 1000));
            Assert.Equal(2, recorder.Sent.Count);
            // A resend SDL refuses says so, so the row is tried again, and the
            // next frame writes it too.
            recorder.Accept = false;
            state.TryRecordMotorSnapshot(20000, 2000);
            Assert.False(state.ResendScalar(device));
            recorder.Accept = true;
            recorder.Sent.Clear();
            state.SetDeviceForces(null, device, new PadSetting(), new Vibration(20000, 2000));
            Assert.Equal((20000, 2000), Assert.Single(recorder.Sent));
            state.TryRecordMotorSnapshot(0, 0);
            recorder.Sent.Clear();
            Assert.True(state.ResendScalar(device));
            Assert.Equal((0, 0), Assert.Single(recorder.Sent));
            // A refused stop leaves SDL's record at a level the port's final
            // stop may have ended, which the level's write would then skip as
            // unchanged and report as taken.
            recorder.RefuseStop = true;
            recorder.Sent.Clear();
            state.TryRecordMotorSnapshot(30000, 1000);
            Assert.False(state.ResendScalar(device));
            Assert.Equal((0, 0), Assert.Single(recorder.Sent));
            // The row is tried again until SDL takes both, since a later
            // frame's write of the same level can report success with nothing
            // sent while SDL's record still holds it.
            recorder.RefuseStop = false;
            recorder.Sent.Clear();
            Assert.True(state.ResendScalar(device));
            Assert.Equal(new[] { (0, 0), (30000, 1000) }, recorder.Sent);
            // A port SDL found no motors on takes no level, and none is kept
            // for the next switch-on to hand the port.
            recorder.Rumble = false;
            recorder.Sent.Clear();
            state.TryRecordMotorSnapshot(30000, 1000);
            Assert.True(state.ResendScalar(device));
            Assert.Empty(recorder.Sent);
            Assert.Equal(0, state.LeftMotorSpeed);
            Assert.Equal(0, state.RightMotorSpeed);
            // A pending row is tried again every 100 ms, not every cycle, and
            // each retry runs the resend itself.
            string phase = Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs");
            Assert.Contains("if (ud == null || (handOff ? HandMotorsToBlissBox(ud) : ResetBlissBoxRumbleCache(ud)))", phase);
            Assert.Contains("if (pending.Count == 0 || Environment.TickCount64 < _blissBoxRetriesDue) return;", phase);
            Assert.Contains("_blissBoxRetriesDue = Environment.TickCount64 + BlissBoxRetryMs;", phase);
            // A stop SDL refused at the hand-off leaves its effect running
            // beside the adapter's commands, so the hand-off waits.
            Assert.Contains("try { stopped = wrapper.StopSdlRumble(); } catch { stopped = true; }\n                if (!stopped) return false;", phase);
            // The stop runs under the row's gate, which Identify's SDL lane
            // holds for its writes, so no pulse lands after it.
            int gate = phase.IndexOf("if (!System.Threading.Monitor.TryEnter(ud.OutputSync)) return false;", StringComparison.Ordinal);
            int stop = phase.IndexOf("try { stopped = wrapper.StopSdlRumble(); }", StringComparison.Ordinal);
            Assert.True(gate > 0 && stop > gate);
            Assert.Contains("lock (ud.OutputSync)\n                                {\n                                    // Again under the gate, which a relayed\n                                    // frame may have held past the quiesce.\n                                    if (_inputManager?.OutputsQuiesced == true && (left != 0 || right != 0)) return;\n                                    if (left != 0 || right != 0) dev.SetRumble(left, right);",
                Repo("PadForge.App", "Services", "InputService.cs"));
        }

        [Fact]
        public void TheTriggerFoldReachesTheDirectWriters()
        {
            // The Padix converter and a Bliss-Box port take only their body
            // levels, so Trigger Rumble Fold did nothing there.
            ushort left = 1000, right = 2000;
            ForceFeedbackState.FoldTriggersForDirectWriter(new PadSetting { TriggerRumbleFold = "1" }, 5000, 500, ref left, ref right);
            Assert.Equal((5000, 2000), (left, right));
            ForceFeedbackState.FoldTriggersForDirectWriter(new PadSetting(), 9000, 9000, ref left, ref right);
            Assert.Equal((5000, 2000), (left, right));
            string step2 = Repo("PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs");
            Assert.Contains("ForceFeedbackState.FoldTriggersForDirectWriter(firstPadSetting, combinedLT, combinedRT, ref padixL, ref padixR);", step2);
            Assert.Contains("ForceFeedbackState.FoldTriggersForDirectWriter(firstPadSetting, combinedLT, combinedRT, ref blissL, ref blissR);", step2);
            Assert.Contains("if (ud.ForceFeedbackState.TryRecordMotorSnapshot(blissL, blissR))", step2);
        }

        public class RumbleRecorder : DispatchProxy
        {
            public readonly List<(int, int)> Sent = new();
            /// <summary>Whether SDL takes the writes.</summary>
            public bool Accept = true;
            /// <summary>Whether SDL found motors on the device.</summary>
            public bool Rumble = true;
            /// <summary>Whether SDL refuses a stop.</summary>
            public bool RefuseStop;
            protected override object Invoke(MethodInfo method, object[] args)
            {
                if (method.Name == "SetRumble") { Sent.Add(((ushort)args[0], (ushort)args[1])); return Accept; }
                if (method.Name == "StopRumble") { Sent.Add((0, 0)); return Accept && !RefuseStop; }
                if (method.Name == "get_HasRumble") return Rumble;
                if (method.ReturnType == typeof(void)) return null;
                return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
            }
        }

        [Fact]
        public void TheLastControllersTriggersRestWhileTheAdapterSearches()
        {
            // A searching adapter's report holds 0x80 on its axes (3.0 0x3103
            // to 0x3117, GPA 0x28B9), and the rest rule still reads the last
            // controller's triggers there, which read 0x80 as half pressed.
            var state = new CustomInputState();
            for (int i = 0; i < 8; i++) state.Axis[i] = 0x80 * 257;
            BlissBoxRuntime.RestTriggers(state, new BlissBoxInfo(9, 0, 3, 34, 1));
            Assert.Equal(0, state.Axis[2]);
            Assert.Equal(0, state.Axis[5]);
            Assert.Equal(0x80 * 257, state.Axis[0]);
            Assert.Equal(0x80 * 257, state.Axis[3]);
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("if (session.Info is { Searching: true } searching)", runtime);
            Assert.Contains("if (session.KnownInfo is { } known) RestTriggers(state, known);", runtime);
        }

        [Fact]
        public void A3xAdaptersArrowsRestWhileItSearches()
        {
            // A searching 3.0 adapter sets its direction byte to 0xFF (0x3121
            // to 0x3129), and a latch it kept ORs that into buttons 10 to 13
            // (0x32A0 to 0x32A9), so all four arrows read pressed with no
            // controller in the port.
            var state = new CustomInputState();
            for (int i = 9; i < 15; i++) state.Buttons[i] = true;
            BlissBoxRuntime.ClearArrows(state, 10);
            Assert.True(state.Buttons[9]);
            for (int i = 10; i < 14; i++) Assert.False(state.Buttons[i]);
            Assert.True(state.Buttons[14]);
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("if (searching.Major == 3) ClearArrows(state, 10);", runtime);
        }

        [Fact]
        public void TheAdaptersOwnArrowsStopTheNativePoll()
        {
            // Read from the report before the merge adds the poll's own, so
            // the poll's arrows never count as the adapter's.
            var state = new CustomInputState();
            Assert.False(BlissBoxRuntime.ArrowsSent(state, 10));
            state.Buttons[12] = true;
            Assert.True(BlissBoxRuntime.ArrowsSent(state, 10));
            Assert.False(BlissBoxRuntime.ArrowsSent(state, -1));
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            int check = runtime.IndexOf("if (session.NativeArrowsActive && ArrowsSent(state, firstArrow)) session.ArrowsLatched = true;", StringComparison.Ordinal);
            int merge = runtime.IndexOf("MergeInto(state, session.Pressure, session.Arrows, PressureAxisBase(ud.Device), firstArrow);", StringComparison.Ordinal);
            Assert.True(check > 0 && merge > check);
        }

        [Fact]
        public void OnlyALaidOutControllerOrADualShock2NamesItsObjects()
        {
            // Any other controller keeps numbered names, as a raw joystick
            // does, rather than the joystick's own axis names.
            Assert.True(BlissBoxRuntime.NamesObjectsFor(new BlissBoxInfo(19, 0, 4, 86, 1)));
            Assert.False(BlissBoxRuntime.NamesObjectsFor(new BlissBoxInfo(19, 0, 2, 30, 1)));
            Assert.True(BlissBoxRuntime.NamesObjectsFor(new BlissBoxInfo(BlissBoxControllers.TypeDualShock2, 0, 2, 30, 1)));
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("=> ud != null && OpenedRaw(ud.Device) && Find(ud)?.Session.LiveInfo is { } info\n               && NamesObjectsFor(info);", runtime);
            Assert.Contains("!PadForge.Common.Input.BlissBoxRuntime.NamesObjects(ud) &&",
                Repo("PadForge.App", "Common", "MappingDisplayResolver.cs"));
        }

        [Fact]
        public void APeersCopyOfAPortKeepsThePressureRule()
        {
            // A Remote Link peer's row has no port on this PC, so it takes the
            // owner's shape: its pressure axes rest at 0 while the owner reads
            // the port raw. The owner sends no object list for a joystick, so
            // the peer's names for axes 2 and 5 are a gamepad's triggers
            // whatever the pad is, and a DualShock's axes there are centered on
            // the owner. The peer counts them centered too, as it does for
            // every raw joystick.
            UserDevice Row(int type)
            {
                var device = new PadForge.Engine.RemoteLink.RemotePeerDevice(new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo
                {
                    VendorId = 0x16D0,
                    ProductId = 0x0D04,
                    InputDeviceType = type,
                    NumAxes = 8,
                });
                return new UserDevice
                {
                    VendorId = 0x16D0,
                    ProdId = 0x0D04,
                    CapType = type,
                    Device = device,
                    DeviceObjects = device.GetDeviceObjects(),
                };
            }
            var raw = Row(InputDeviceType.Joystick);
            Assert.Contains(raw.DeviceObjects, item => item.InputIndex == 2 && item.Name == "Left Trigger");
            Assert.True(InputManager.AxisRestsAtZero("Axis 8", raw));
            Assert.False(InputManager.AxisRestsAtZero("Axis 2", raw));
            Assert.False(InputManager.AxisRestsAtZero("Axis 0", raw));
            Assert.False(InputManager.AxisRestsAtZero("Axis 8", Row(InputDeviceType.Gamepad)));
        }

        [Fact]
        public void AShowDiesWithThePortItPlayedOn()
        {
            // A row that reconnects gets a new port in the same pass, so the
            // open ports never empty and a show keyed by the device carried
            // over. A reset drops what is left.
            var service = new DreamcastScreenService(new SettingsViewModel(), null);
            var device = Guid.NewGuid();
            var first = new BlissBoxPort(@"\\?\hid#vid_16d0&pid_0d04#first", 0x0D04, device, 11);
            var second = new BlissBoxPort(@"\\?\hid#vid_16d0&pid_0d04#first", 0x0D04, device, 12);
            try
            {
                var show = new DreamcastShow(new List<byte[]> { new byte[192] }, 1000, 1, now: 0);
                service.ShowOn(first, show);
                service.Prune(new[] { first });
                Assert.True(service.HasShow(first));
                service.Prune(new[] { second });
                Assert.False(service.HasShow(first));
                service.ShowOn(second, show);
                service.Reset();
                Assert.False(service.HasShow(second));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Fact]
        public void TheEnginesStopDropsTheShowsOnceThePortsHaveClosed()
        {
            // The reset ran in the stop's first step, while the poll thread
            // still ran macros and the ports stayed open, so a show queued
            // after it played on the next start's ports.
            string code = Repo("PadForge.App", "Services", "InputService.cs");
            int stop = code.IndexOf("                _inputManager.Stop();", StringComparison.Ordinal);
            int reset = code.IndexOf("if (_dreamcastScreen != null) _dreamcastScreen.Reset();\n                else DreamcastScreenService.DropRequests();", StringComparison.Ordinal);
            Assert.True(stop > 0 && reset > stop);
            Assert.Equal(reset, code.LastIndexOf("_dreamcastScreen.Reset();", StringComparison.Ordinal) - "if (_dreamcastScreen != null) ".Length);
            const string path = @"\\?\hid#padforge-test-no-such-device-reset";
            try
            {
                BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, Guid.NewGuid(), 7) });
                DreamcastScreenService.RequestShow(0, Convert.ToBase64String(new byte[192]), 1000, 1);
                Assert.Equal(1, DreamcastScreenService.PendingShows);
                new DreamcastScreenService(new SettingsViewModel(), null).Reset();
                Assert.Equal(0, DreamcastScreenService.PendingShows);
                // The engine's stop before any tick made the service.
                DreamcastScreenService.RequestShow(0, Convert.ToBase64String(new byte[192]), 1000, 1);
                DreamcastScreenService.DropRequests();
                Assert.Equal(0, DreamcastScreenService.PendingShows);
            }
            finally { BlissBoxRuntime.Shutdown(); }
        }

        [Fact]
        public void NoPortOpensAfterTheEnginesStop()
        {
            // A poll pass that outlived the stop's 3 s join opened a port
            // behind the shutdown, which nothing closed until the next start.
            const string path = @"\\?\hid#padforge-test-no-such-device-closed";
            try
            {
                BlissBoxRuntime.Close();
                Assert.Null(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, Guid.NewGuid(), 7) }));
                Assert.Empty(BlissBoxRuntime.Ports);
                BlissBoxRuntime.Open();
                Assert.Single(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, Guid.NewGuid(), 7) }));
            }
            finally
            {
                BlissBoxRuntime.Open();
                BlissBoxRuntime.Shutdown();
            }
            string manager = Repo("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("            BlissBoxRuntime.Open();\n\n            Array.Clear(_steeringAngleFrames);\n            _running = true;", manager);
            Assert.Contains("            BlissBoxRuntime.Close();", manager);
            // The engine's stop and the quiesce also stop SDL's own rumble on a
            // port row the switch owns, which SDL's gate refuses otherwise.
            Assert.Contains("if (ud.Device is SdlDeviceWrapper wrapper)\n                            {\n                                try { wrapper.StopSdlRumble(); }", manager);
            // The port's motors are told their levels again after that stop,
            // as after the hand-off's.
            Assert.Contains("                                catch { /* best effort */ }\n                                // As after the hand-off's stop.\n                                BlissBoxRuntime.ResendMotors(ud.DevicePath);", manager);
        }

        [Fact]
        public void AReplacedPortsActionsEnd()
        {
            // After a player change the old port answers until its channel
            // closes, and a Dreamcast Screen save or a Native Arrows change in
            // that time wrote back the choices the change dropped.
            string window = Repo("PadForge.App", "MainWindow.BlissBox.cs");
            Assert.Contains("if (port == null || port.Replaced) return;", window);
            Assert.Contains("                    if (result.Ok)\n                    {\n                        port.Replaced = true;\n                        service.Remove(port.InstanceGuid);", window);
            Assert.Contains("row.BlissBoxIdle = !port.Session.Busy && !port.Replaced;",
                Repo("PadForge.App", "Services", "InputService.BlissBox.cs"));
            // A show on it ends too, or it would copy the adapter's picture
            // into the entry the change dropped.
            Assert.Contains("if (port.Replaced || !TrackPad(port, session.LiveInfo, now))",
                Repo("PadForge.App", "Services", "DreamcastScreenService.cs"));
        }

        [Fact]
        public void TheScreenDialogWaitsForAJob()
        {
            // A player change's end drops the old device's choices, and a
            // Dreamcast Screen dialog opened during the change and saved
            // after it brought them back, as the other actions do not.
            string page = Repo("PadForge.App", "Views", "DevicesPage.xaml");
            int button = page.IndexOf("Click=\"DreamcastScreen_Click\"", StringComparison.Ordinal);
            Assert.True(button > 0);
            Assert.Contains("IsEnabled=\"{Binding SelectedDevice.BlissBoxIdle}\"", page.Substring(button, 200));
            string window = Repo("PadForge.App", "MainWindow.BlissBox.cs");
            Assert.Contains("case BlissBoxAction.DreamcastScreen:\n                {\n                    // A player change's end drops the old device's choices,\n                    // which a dialog saved after it would bring back.\n                    if (port.Session.Busy) return;", window);
        }

        [Fact]
        public void APassCountsAsRunningBeforeItReadsTheLevels()
        {
            // A GPA pass read the levels before it marked itself running, so
            // the crash path could read rest between that read and a write of
            // a level from before its quiesce.
            string session = Repo("PadForge.Engine", "Common", "BlissBox", "BlissBoxSession.cs");
            int pass = session.IndexOf("private void WriteMotors(long now)", StringComparison.Ordinal);
            int count = session.IndexOf("Interlocked.Increment(ref _motorPasses);", pass, StringComparison.Ordinal);
            int read = session.IndexOf("WantedStrengths();", pass, StringComparison.Ordinal);
            Assert.True(pass > 0 && count > pass && read > count);
            // A GPA's peaks clear before the read, so a level asked for during
            // the pass keeps the motors out of rest.
            int clear = session.IndexOf("if (advanced) ClearPeaks();", pass, StringComparison.Ordinal);
            Assert.True(clear > count && read > clear);
            // The pass after a picture write takes the clock again, and so
            // does the step's first, after its reads.
            Assert.Contains("if (WriteScreen(now)) WriteMotors(_clock());", session);
            Assert.Contains("            // each.\n            WriteMotors(_clock());", session);
            // The crash stop waits for a picture write in flight and the pass
            // after it.
            Assert.Contains("if (rest || now >= (sawPicture ? pictureEnd : end)) return;",
                Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs"));
            // A switch-off resend or a hand-off writes nothing once the
            // outputs are quiesced.
            Assert.Contains("if (pending.Count == 0 || OutputsQuiesced) return;",
                Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs"));
            // The crash stop reads the ports again on each look, and a port
            // opened after it is quiesced before its worker starts.
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("                foreach (var port in Ports)\n                    if (!waiting.Contains(port)) waiting.Add(port);", runtime);
            // The latch is set under the lock Sync opens ports under, and the
            // wait takes in the ports still retiring.
            Assert.Contains("                Monitor.TryEnter(_lock, 100, ref taken);\n                _quiescedAll = true;\n                if (taken) waiting.AddRange(_retiring);", runtime);
            Assert.Contains("if (_quiescedAll) created.Session.Quiesce();\n                    created.Changed += OnPortChanged;\n                    created.Start();", runtime);
            // A quiesced port reads no picture on the crash path's wakes.
            Assert.Contains("if (_storedScreen == null && !_quiesced) ReadScreen();", session);
        }

        [Fact]
        public void TheCrashPathDropsAPulseNotYetSent()
        {
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            // Each port is quiesced on each look, so a writer that had passed
            // the engine's quiesce check cannot hand it a level after its stop.
            Assert.Contains("                if (waiting.Count == 0) return;\n                foreach (var port in waiting)\n                {\n                    port.Session.Quiesce();\n                    port.Wake();", runtime);
            // A row that goes offline drops its port's levels, which the SDL
            // stop there never reaches.
            Assert.Contains("try { BlissBoxRuntime.StopRumble(ud.DevicePath); }",
                Repo("PadForge.App", "Common", "Input", "InputManager.Step1.UpdateDevices.cs"));
            // The engine's stop and the quiesce's first sweep drop it too, and
            // a relayed frame cannot start a motor again behind the quiesce.
            Assert.Contains("try { BlissBoxRuntime.StopRumble(ud.DevicePath); }",
                Repo("PadForge.App", "Common", "Input", "InputManager.cs"));
            string service = Repo("PadForge.App", "Services", "InputService.cs");
            int apply = service.IndexOf("private void ApplyRemoteOutput(", StringComparison.Ordinal);
            int quiesced = service.IndexOf("if (_inputManager?.OutputsQuiesced == true && !IsVibrationStop(effect)) return;", apply, StringComparison.Ordinal);
            int claim = service.IndexOf("RemoteLinkOutputRouter.ClaimOutput(", apply, StringComparison.Ordinal);
            Assert.True(apply > 0 && quiesced > apply && claim > quiesced);
            // A port whose channel is down opens it once more for its stop.
            Assert.Contains("try { _transport.Channel = AnalogKeyboardHidChannel.OpenShared(Path); } catch { }\n            }\n            try { if (_transport.Channel != null) Session.StopMotors(); } catch { }",
                Repo("PadForge.App", "Common", "Input", "BlissBoxPort.cs"));
        }

        [Fact]
        public void TheEnginesStopSettlesTheStatusLines()
        {
            // The UI timer stops with the engine, so the Settings lines and a
            // port's Devices line and actions kept their last running state.
            string code = Repo("PadForge.App", "Services", "InputService.cs");
            int stop = code.IndexOf("                _inputManager.Stop();", StringComparison.Ordinal);
            int refresh = code.IndexOf("                    UpdateHeadTrackingStatus();\n                    UpdateGKeysStatus();\n                    UpdateAnalogKeyboardsStatus();\n                    UpdateBlissBoxStatus();", StringComparison.Ordinal);
            Assert.True(stop > 0 && refresh > stop);
            Assert.Contains("UpdateBlissBoxDeviceRow(selected, selectedDevice);", code);
            // A row selected while the engine is stopped clears the line and
            // actions it kept from when the engine ran.
            Assert.Contains("            UpdateBlissBoxDeviceRow(selected, ud);\n\n            // Build the structural layout from cached capabilities.", code);
            // Identify hands back the level the row's snapshot holds, which a
            // Remote Link peer's steady level would otherwise lose.
            Assert.Contains("                        Buzz(65535, 65535);\n                        await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);\n                        Restore();", code);
            // SDL resends its last level every 2 s (SDL_joystick.c), so on an
            // Xbox One+ pad the level goes through the raw writer after an SDL
            // stop, under the gate relayed frames take.
            Assert.Contains("bool impulse = !peer && !padix && !blissBox", code);
            Assert.Contains("lock (row.OutputSync)", code);
            Assert.Contains("Buzz(0, 0);\n                                ushort lt = fs?.LeftTriggerMotorSpeed ?? 0, rt = fs?.RightTriggerMotorSpeed ?? 0;\n                                if (_inputManager?.OutputsQuiesced == true && (left | right | lt | rt) != 0) return;\n                                PadForge.Common.Input.XboxImpulseHidWriter.Write(row, left, right, lt, rt);", code);
            // A row removed during the last pulse gets a stop.
            Assert.Contains("if (row == null) { Buzz(0, 0); return; }", code);
            // The crash path's quiesce ends a train in flight.
            // A stop still goes out, since a pulse that reached the device just
            // after the crash sweep has no other writer left to end it.
            Assert.Contains("// writer left to end it.\n                            if (_inputManager?.OutputsQuiesced == true && (left != 0 || right != 0)) return;", code);
            Assert.Contains("private static bool IsVibrationStop(OutputEffectCodec.OutputEffect effect)", code);
        }

        [Fact]
        public async System.Threading.Tasks.Task ASuccessorWaitsForTheOldWorkerBeforeItsResend()
        {
            var guid = Guid.NewGuid();
            const string path = @"\\?\hid#padforge-test-no-such-device-successor-wait";
            using var exited = new System.Threading.ManualResetEventSlim(false);
            try
            {
                var successor = Assert.Single(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 9) }));
                var handOver = System.Threading.Tasks.Task.Run(() => BlissBoxRuntime.HandOverToSuccessor(path, () => exited.IsSet, null));
                await System.Threading.Tasks.Task.Delay(200);
                Assert.False(handOver.IsCompleted);
                Assert.True(successor.Session.MotorsAtRest);
                exited.Set();
                await handOver.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(successor.Session.MotorsAtRest);
            }
            finally { BlissBoxRuntime.Shutdown(); }
        }

        [Fact]
        public void TheChipsFollowALanguageChange()
        {
            // The pressure chips were rebuilt only when their count changed and
            // an analog key's only when it first showed, so both kept the old
            // language's names.
            var vm = new DevicesViewModel();
            vm.UpdateBlissBoxPressure(new byte[12]);
            var keys = new PadForge.Engine.AnalogKeyInputState();
            keys.Set(4, 0.5f);
            vm.UpdateAnalogKeys(keys);
            var pressure = vm.BlissBoxPressure[6];
            var key = Assert.Single(vm.AnalogKeys);
            string pressureName = pressure.Name, keyName = key.Name;
            pressure.Name = key.Name = "stale";
            typeof(DevicesViewModel).GetMethod("OnCultureChanged", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(vm, null);
            Assert.Equal(pressureName, pressure.Name);
            Assert.Equal(keyName, key.Name);
        }

        [Fact]
        public void ASuccessorTellsTheMotorsAgainOnceTheOldWorkerExits()
        {
            // The retired worker's final stop could land after the port that
            // took over its path had written its first levels.
            var guid = Guid.NewGuid();
            const string path = @"\\?\hid#padforge-test-no-such-device-successor";
            try
            {
                Assert.Single(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 7) }));
                var successor = Assert.Single(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 8) }));
                Assert.True(System.Threading.SpinWait.SpinUntil(() => !successor.Session.MotorsAtRest, 5000));
            }
            finally { BlissBoxRuntime.Shutdown(); }
        }

        [Fact]
        public void TheSwitchMovesTheMotorsOnThePollThreadsNextCycle()
        {
            // The switch's own effects once ran on the UI thread at once while
            // the rows and ports changed up to five seconds later, so a port
            // sat silent or kept a level nothing could stop in between.
            string runtime = Repo("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs");
            Assert.Contains("if (value != was) Interlocked.Increment(ref _generation);", runtime);
            Assert.DoesNotContain("StopSdlRumbleOnPorts", runtime);
            // A retired port counts until its worker has exited, past
            // Dispose's 3 s wait, and the list is pruned as ports retire.
            Assert.Contains("_retiring.RemoveAll(port => port.Exited);", runtime);
            Assert.Contains("_retiring.RemoveAll(p => p.Exited);\n                        _retiring.Add(closing);", runtime);
            string loop = Repo("PadForge.App", "Common", "Input", "InputManager.cs");
            Assert.Contains("if (_enumerationTimer.ElapsedMilliseconds >= 5000 || ConsumeBlissBoxSwitchChange())", loop);
            Assert.Contains("if (firstCycle || _enumerationTimer.ElapsedMilliseconds >= EnumerationIntervalMs || ConsumeBlissBoxSwitchChange())", loop);
            // Every cycle, right after the sweep, in both loops.
            Assert.Contains("                                UpdateDevices();\n                            }\n                            RetryPendingBlissBoxRows();", loop);
            Assert.Contains("                            enumMs = (Stopwatch.GetTimestamp() - tsEnum) * 1000 / Stopwatch.Frequency;\n                        }\n                        RetryPendingBlissBoxRows();", loop);
            // Phase 1l catches the sweep's generation up, so a phase before it
            // that throws costs one extra sweep, not one a cycle.
            string sweep = Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs");
            Assert.Contains("            _blissBoxGeneration = generation;\n            _blissBoxSweepGeneration = generation;", sweep);
            // SDL gets its level back only after the retired port's final stop,
            // which on a GPA reaches the routines SDL's effect drives.
            string phase = Repo("PadForge.App", "Common", "Input", "InputManager.BlissBox.cs");
            int retiring = phase.IndexOf("if (BlissBoxRuntime.IsRetiring(ud.DevicePath)) return false;", StringComparison.Ordinal);
            int resend = phase.IndexOf("try { return state.ResendScalar(ud.Device); }", StringComparison.Ordinal);
            Assert.True(retiring > 0 && resend > retiring);
            int sync = phase.IndexOf("var opened = BlissBoxRuntime.Sync(rows);", StringComparison.Ordinal);
            int queue = phase.IndexOf("if (wasOn || toggled) QueueBlissBoxCacheResets();", StringComparison.Ordinal);
            Assert.True(sync > 0 && queue > sync);
        }

        [Fact]
        public void ARelayedFrameReachesTheRowsReopenedWrapper()
        {
            // The exposure keeps the wrapper it was built with for up to 2 s,
            // and the switch's reopen swaps it, so a peer's stop sent then was
            // dropped with the port still running its level.
            string code = Repo("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("|| !UserDevice.SameConnection(source, live)) return;", code);
            Assert.Contains("ApplyRemoteOutput(effect, live, device, frame.PeerFingerprint,", code);
            // The exposure's wrapper is disposed by the time the frame lands.
            var exposed = new SdlDeviceWrapper { SdlInstanceId = 21 };
            var reopened = new SdlDeviceWrapper { SdlInstanceId = 21 };
            try
            {
                exposed.Dispose();
                Assert.True(UserDevice.SameConnection(exposed, reopened));
            }
            finally { reopened.Dispose(); }
        }

        [Fact]
        public void ACopyStaysHeldWhenItsSaveThrows()
        {
            // The hold was recorded only after the save returned, so a save
            // that threw let the next pass replace the picture with the copy
            // in memory alone.
            bool fail = true;
            var service = new DreamcastScreenService(new SettingsViewModel(), null,
                () => fail ? throw new InvalidOperationException() : true, () => 0);
            var device = Guid.NewGuid();
            var stored = new byte[192];
            var wire = Enumerable.Repeat((byte)0xFF, 192).ToArray();
            Assert.Throws<InvalidOperationException>(() => service.MayReplace(device, null, stored, wire));
            fail = false;
            Assert.False(service.MayReplace(device, service.Get(device), stored, wire));
        }

        [Fact]
        public void TheScreenServiceTicksWithNoPortOpen()
        {
            // A show playing or queued when the last port closed resumed on
            // the next port for the same device, since nothing ticked the
            // service while no port was open. Before the service exists, a
            // request whose port closed is dropped there.
            string code = Repo("PadForge.App", "Services", "InputService.BlissBox.cs");
            Assert.Contains("            if (BlissBoxRuntime.Ports.Length == 0 && _dreamcastScreen == null)\n            {\n                DreamcastScreenService.DropRequests();\n                return;\n            }", code);
        }

        [Fact]
        public void EverySwitchChangeCountsOnce()
        {
            bool saved = BlissBoxApi.Enabled;
            try
            {
                BlissBoxRuntime.Enabled = false;
                int start = BlissBoxRuntime.Generation;
                BlissBoxRuntime.Enabled = false;
                Assert.Equal(start, BlissBoxRuntime.Generation);
                BlissBoxRuntime.Enabled = true;
                BlissBoxRuntime.Enabled = true;
                BlissBoxRuntime.Enabled = false;
                Assert.Equal(start + 2, BlissBoxRuntime.Generation);
            }
            finally { BlissBoxRuntime.Enabled = saved; }
        }

        [Fact]
        public void ARowThatReconnectsGetsANewPort()
        {
            // The old port's session kept the levels it last held and sent
            // them to the new connection, while the row's new motor snapshot
            // started at rest, so the game's next zero never reached them.
            var guid = Guid.NewGuid();
            const string path = @"\\?\hid#padforge-test-no-such-device";
            try
            {
                var opened = BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 7) });
                var first = Assert.Single(opened);
                Assert.Null(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 7) }));
                Assert.Same(first, Assert.Single(BlissBoxRuntime.Ports));
                var again = Assert.Single(BlissBoxRuntime.Sync(new[] { new BlissBoxRuntime.Row(path, 0x0D04, guid, 8) }));
                Assert.NotSame(first, again);
                Assert.Same(again, Assert.Single(BlissBoxRuntime.Ports));
                Assert.Equal(8u, again.SdlInstanceId);
            }
            finally { BlissBoxRuntime.Shutdown(); }
            Assert.Empty(BlissBoxRuntime.Ports);
        }

        [Fact]
        public void IdentifyNeverWritesAPeerRowsPathDirectly()
        {
            // A peer row carries the owner's VID and PID, and the direct lanes
            // would write a path that exists only on the other PC.
            string code = Repo("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("bool peer = PadForge.Common.Input.RemoteLinkOutputRouter.IsPeerPath(ud.DevicePath);", code);
            Assert.Contains("bool padix = !peer && PadForge.Engine.PadixConverterIdentity", code);
            Assert.Contains("bool blissBox = !peer && PadForge.Engine.Common.BlissBox.BlissBoxApi", code);
        }

        [Fact]
        public void EveryJobErrorHasItsOwnStatusText()
        {
            // A refused player change once read "Check that the controller is
            // plugged in", the no-reply text, which the command never needs.
            var texts = Enum.GetValues<BlissBoxJobError>()
                .Where(error => error != BlissBoxJobError.None)
                .Select(error => MainWindow.JobErrorText(BlissBoxJobResult.Fail(error, 7)))
                .ToList();
            Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
            Assert.Equal(texts.Count, texts.Distinct().Count());
            Assert.Equal(Strings.Instance.BlissBoxJob_PlayerUnchanged,
                MainWindow.JobErrorText(BlissBoxJobResult.Fail(BlissBoxJobError.PlayerUnchanged)));
        }

        [Fact]
        public void TheChosenPictureAndThePlayerNumberEachHaveAReset()
        {
            string screen = Repo("PadForge.App", "Views", "DreamcastScreenDialog.xaml");
            Assert.Contains("Click=\"ResetPicture_Click\"", screen);
            string player = Repo("PadForge.App", "Views", "BlissBoxPlayerDialog.xaml");
            Assert.Contains("Click=\"ResetPlayer_Click\"", player);
            string resets = Repo("PadForge.App", "Views", "DialogSettingResets.cs");
            Assert.Contains("_picture = null;", resets);
            Assert.Contains("=> PlayerBox.SelectedIndex = _current - 1;", resets);
        }

        [Fact]
        public void TheSessionReportsOnlyChanges_SoTheWorkerWakesOnlyForThem()
        {
            var session = new BlissBoxSession(new NullTransport(), 1);
            Assert.True(session.SetRumble(1000, 0));
            Assert.False(session.SetRumble(1000, 0));
            Assert.True(session.SetRumble(0, 0));
            var wire = new byte[192];
            Assert.True(session.SetScreen(wire));
            Assert.False(session.SetScreen((byte[])wire.Clone()));
            Assert.True(session.SetScreen(null));
            Assert.False(session.SetScreen(null));
        }

        private sealed class NullTransport : IBlissBoxTransport
        {
            public bool SetFeature(byte[] report, int timeoutMs) => SetFeature(report);
            public bool SetFeature(byte[] report) => false;
            public int GetFeature(byte[] buffer) => -1;
            public int FeatureLength => 0;
        }

        // ── Helpers ──

        private static string Repo(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }

        private static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "render run timed out");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
