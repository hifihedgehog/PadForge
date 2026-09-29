using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            // The GameCube's analog triggers ride axes 6 and 7, then 2 and 5.
            Assert.Equal("Left Trigger", BlissBoxControllers.AxisName(9, 3, 6));
            Assert.Equal("Left Trigger", BlissBoxControllers.AxisName(9, 4, 2));
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
            Assert.Equal("Type 200", BlissBoxControllers.Name(200));
            Assert.Equal("DualShock 2", BlissBoxControllers.Name(121));
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
            // The chips sit under a pressure heading and are 118 px wide, so a
            // chip reads "Triangle", not "Triangle Pressure" cut short.
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

            // A 16-button joystick gets its arrow buttons appended.
            var n64 = BlissBoxRuntime.NameObjects(new BlissBoxInfo(19, 0, 4, 86, 1), 8, RawJoystick(8, 16));
            Assert.Equal(4, n64.Count(o => o.IsButton && o.Name.EndsWith(" Arrow", StringComparison.Ordinal)));
            Assert.DoesNotContain(n64, o => o.IsAxis && o.InputIndex >= 8);
        }

        [Fact]
        public void TheMergeWritesPressuresAsAxesAndArrowsAsButtons()
        {
            var state = new CustomInputState();
            var pressure = new byte[12];
            pressure[6] = 255;
            pressure[0] = 128;
            BlissBoxRuntime.MergeInto(state, pressure, 0x01 | 0x08, BlissBoxRuntime.PressureAxisBase(8));
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
            BlissBoxRuntime.MergeInto(untouched, pressure, -1, -1);
            Assert.All(untouched.Axis, a => Assert.Equal(0, a));
            Assert.All(untouched.Buttons, b => Assert.False(b));
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
            Assert.Contains("if (!BlissBoxRuntime.SetRumble(ud.DevicePath, combinedL, combinedR))", step2);
            int dispatch = step2.IndexOf("if (!BlissBoxRuntime.SetRumble(ud.DevicePath, combinedL, combinedR))", StringComparison.Ordinal);
            int sdl = step2.IndexOf("ud.ForceFeedbackState.SetDeviceForces(ud, ud.Device, firstPadSetting, _combinedVibration);", StringComparison.Ordinal);
            Assert.True(dispatch > 0 && sdl > dispatch, "the Bliss-Box dispatch must precede the SDL write");

            string wrapper = Repo("PadForge.Engine", "Common", "SdlDeviceWrapper.cs");
            int setRumble = wrapper.IndexOf("public bool SetRumble(ushort lowFreq, ushort highFreq, uint durationMs = uint.MaxValue)", StringComparison.Ordinal);
            int guard = wrapper.IndexOf("if (BlissBoxApi.OwnsRumble(VendorId, ProductId))", StringComparison.Ordinal);
            int call = wrapper.IndexOf("return SDL_RumbleJoystick(Joystick, lowFreq, highFreq, durationMs);", StringComparison.Ordinal);
            Assert.True(setRumble > 0 && guard > setRumble && call > guard, "SetRumble must refuse an owned port before calling SDL");
            Assert.Contains("get => _hasRumble || BlissBoxApi.OwnsRumble(VendorId, ProductId);", wrapper);

            Assert.Contains("BlissBoxRuntime.SetRumble(ud.DevicePath, 0, 0);", Repo("PadForge.App", "Common", "Input", "InputManager.cs"));
            string service = Repo("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("PadForge.Common.Input.BlissBoxRuntime.SetRumble(\n                                    ud.DevicePath, bvib.LeftMotorSpeed, bvib.RightMotorSpeed);", service);
            Assert.Contains("PadForge.Common.Input.BlissBoxRuntime.SetRumble(ud.DevicePath, level, level);", service);
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
                new BlissBoxPortData { Device = Guid.NewGuid().ToString(), ScreenMode = (DreamcastScreenMode)99 },
            });
            Assert.Equal(2, loaded.Count);
            Assert.Equal(DreamcastScreenMode.Clock, loaded[0].ScreenMode);
            Assert.Equal(guid.ToString("D"), loaded[0].Device);
            Assert.Equal(DreamcastScreenMode.Adapter, loaded[1].ScreenMode);
            Assert.Empty(BlissBoxPortData.Normalize(null));
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
            // rest, so a pressure axis, 0 at rest, must count as one-way.
            bool saved = BlissBoxApi.Enabled;
            try
            {
                var port = new UserDevice { VendorId = 0x16D0, ProdId = 0x0D04 };
                BlissBoxApi.Enabled = true;
                Assert.True(InputManager.AxisRestsAtZero("Axis 8", port));
                Assert.True(InputManager.AxisRestsAtZero("Axis 19", port));
                Assert.False(InputManager.AxisRestsAtZero("Axis 7", port));
                Assert.False(InputManager.AxisRestsAtZero("Axis 20", port));
                // Another device's axis 8 is untouched.
                Assert.False(InputManager.AxisRestsAtZero("Axis 8", new UserDevice { VendorId = 0x054C, ProdId = 0x0268 }));
                // With the switch off the port reads through SDL's mapping.
                BlissBoxApi.Enabled = false;
                Assert.False(InputManager.AxisRestsAtZero("Axis 8", port));
            }
            finally { BlissBoxApi.Enabled = saved; }
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
            var show = new DreamcastShow(new List<byte[]> { a }, 200, 1, now: 0);
            var other = new byte[192];
            Assert.Equal(a, show.Frame(other, DreamcastShow.DeliveryLimitMs - 1));
            // Frame times hold to the guard's second whatever the action says.
            Assert.Null(show.Frame(other, DreamcastShow.DeliveryLimitMs));
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
