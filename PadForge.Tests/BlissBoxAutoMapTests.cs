using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.BlissBox;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A Bliss-Box port read raw maps by SDL's gamepad conventions: the GPA's
    /// Select and Start button carries SDL's name for its role, each
    /// controller's inputs take the roles SDL's own mapping for that
    /// console's pad gives them (RetroArch's Bliss-Box files where SDL has
    /// none), and the default mapping binds the port through that placement
    /// on every slot type, as it binds an SDL gamepad.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class BlissBoxAutoMapTests
    {
        private const byte Gen3 = 3;
        private const byte Gen4 = 4;
        private const int FirstPressure = 8;

        private static BlissBoxGamepadMap Map(byte type, byte major)
            => BlissBoxControllers.GamepadMap(type, major, FirstPressure);

        private static string[] Buttons(BlissBoxGamepadMap map)
            => Enumerable.Range(0, BlissBoxGamepadMap.ButtonPositions).Select(map.Button).ToArray();

        private static string[] Axes(BlissBoxGamepadMap map)
            => Enumerable.Range(0, BlissBoxGamepadMap.AxisPositions).Select(map.Axis).ToArray();

        private static string B(int i) => $"Button {i}";

        private static string A(int i) => $"Axis {i}";

        // ── Names ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(65)]
        [InlineData(83)]
        [InlineData(115)]
        [InlineData(119)]
        [InlineData(121)]
        public void AGpaNamesThePlayStationSelectAndStartButtonGuide(byte type)
        {
            Assert.Equal("Guide", BlissBoxControllers.ButtonName(type, Gen4, 18));
            // Its arrows sit past it, at 20 to 23.
            Assert.Equal(20, BlissBoxControllers.FirstArrowButton(type, Gen4));
        }

        [Theory]
        [InlineData(65)]
        [InlineData(115)]
        [InlineData(121)]
        public void The3xFirmwareSendsNoSuchButton(byte type)
            => Assert.Null(BlissBoxControllers.ButtonName(type, Gen3, 18));

        [Fact]
        public void TheGuideNameIsOneTheResolverTranslates()
        {
            // The same invariant name an SDL gamepad's guide carries.
            Assert.Equal("Guide", GamepadObjectNames.Button(10));
            Assert.Equal(Resources.Strings.Strings.Instance.DevObj_Guide,
                MappingDisplayResolver.LocalizeObjectName("Guide"));
        }

        [Fact]
        public void TheWiiClassicKeepsItsPrintedHomeButton()
            => Assert.Equal("Home Button", BlissBoxControllers.ButtonName(31, Gen4, 18));

        // ── Placements, SDL's own mappings ────────────────────────────────

        [Theory]
        [InlineData(115, Gen3)]
        [InlineData(121, Gen3)]
        [InlineData(83, Gen4)]
        [InlineData(115, Gen4)]
        [InlineData(119, Gen4)]
        [InlineData(121, Gen4)]
        public void APlayStationPadMapsAsSdlsPs3Driver(byte type, byte major)
        {
            var map = Map(type, major);
            var b = Buttons(map);
            Assert.Equal(new[] { B(0), B(1), B(2), B(3) }, b[..4]);   // Cross Circle Square Triangle
            Assert.Equal(B(6), b[4]);    // L1
            Assert.Equal(B(7), b[5]);    // R1
            Assert.Equal(B(4), b[6]);    // Select
            Assert.Equal(B(5), b[7]);    // Start
            Assert.Equal(B(14), b[8]);   // L3
            Assert.Equal(B(15), b[9]);   // R3
            Assert.Equal(major == Gen4 ? B(18) : null, b[10]);
            Assert.Equal(new[] { A(0), A(1), B(8), A(3), A(4), B(9) }, Axes(map));
            Assert.True(map.DPad);
            Assert.All(b.Skip(11), Assert.Null);
        }

        [Theory]
        [InlineData(Gen3)]
        [InlineData(Gen4)]
        public void APlayStationDigitalPadHasNoSticks(byte major)
        {
            var map = Map(65, major);
            Assert.Equal(B(0), map.Button(0));
            Assert.Equal(new string[] { null, null, B(8), null, null, B(9) }, Axes(map));
        }

        [Fact]
        public void ADualShock2OnlyCarriesItsPressuresInTheTargetsOrder()
        {
            // Cross, circle, square, triangle, L1, R1, up, down, left, right,
            // out of the pad's own order (right, left, up, down, triangle,
            // circle, cross, square, L1, R1, L2, R2) from the first axis.
            Assert.Equal(new[] { 14, 13, 15, 12, 16, 17, 10, 11, 9, 8 }, Map(121, Gen4).PressureAxes);
            Assert.Equal(new[] { 14, 13, 15, 12, 16, 17, 10, 11, 9, 8 }, Map(121, Gen3).PressureAxes);
            Assert.Null(Map(115, Gen4).PressureAxes);
            Assert.Null(BlissBoxControllers.GamepadMap(121, Gen4, -1).PressureAxes);
            for (int i = 0; i < 10; i++)
            {
                int axis = Map(121, Gen4).PressureAxes[i] - FirstPressure;
                string face = new[] { "Cross", "Circle", "Square", "Triangle", "L1", "R1",
                    "D-Pad Up", "D-Pad Down", "D-Pad Left", "D-Pad Right" }[i];
                Assert.Equal(face + " Pressure", BlissBoxControllers.PressureNames[axis]);
            }
        }

        [Fact]
        public void TheSnesPadMapsByPositionOnBothFirmwares()
        {
            // 3.x sends Y on 2 and X on 3, the GPA the other way round, and
            // both land where they sit: Y west, X north.
            Assert.Equal(new[] { B(0), B(1), B(2), B(3), B(6), B(7), B(4), B(5) }, Buttons(Map(27, Gen3))[..8]);
            Assert.Equal(new[] { B(0), B(1), B(3), B(2), B(6), B(7), B(4), B(5) }, Buttons(Map(27, Gen4))[..8]);
        }

        [Fact]
        public void TheNesPadMapsByLetterOnBothFirmwares()
        {
            // A south and B east, wherever each firmware sends them.
            Assert.Equal(B(1), Map(17, Gen3).Button(0));
            Assert.Equal(B(0), Map(17, Gen3).Button(1));
            Assert.Equal(B(0), Map(17, Gen4).Button(0));
            Assert.Equal(B(1), Map(17, Gen4).Button(1));
            Assert.Equal(B(4), Map(17, Gen4).Button(6));
            Assert.Equal(B(5), Map(17, Gen4).Button(7));
        }

        [Theory]
        [InlineData(Gen3)]
        [InlineData(Gen4)]
        public void TheN64PadMapsAsSdlsSwitchOnlineN64(byte major)
        {
            var map = Map(19, major);
            Assert.Equal(B(1), map.Button(0));    // A
            Assert.Equal(B(0), map.Button(1));    // B
            Assert.Equal(B(3), map.Button(2));    // C-Down
            Assert.Equal(B(2), map.Button(3));    // C-Left
            Assert.Equal(B(6), map.Button(4));    // L
            Assert.Equal(B(7), map.Button(5));    // R
            Assert.Equal(B(8), map.Button(6));    // C-Up
            Assert.Equal(B(5), map.Button(7));    // Start
            Assert.Equal(B(9), map.Button(17));   // C-Right on Misc 2
            Assert.Equal(new[] { A(0), A(1), B(4), null, null, null }, Axes(map));
        }

        [Fact]
        public void TheGenesisPadMapsAsSdlsSwitchOnlineGenesisOnBothFirmwares()
        {
            // C and Z trade indices between the generations.
            foreach (var (major, c, z) in new[] { (Gen3, 7, 6), (Gen4, 6, 7) })
            {
                var map = Map(21, major);
                Assert.Equal(new[] { B(0), B(1), B(2), B(3) }, Buttons(map)[..4]);
                Assert.Equal(B(z), map.Button(4));
                Assert.Equal(B(c), map.Button(5));
                Assert.Equal(B(5), map.Button(7));
                Assert.Equal(B(4), map.Axis(5));   // Mode
            }
            Assert.Equal(B(7), Map(20, Gen3).Button(5));
            Assert.Null(Map(20, Gen3).Button(2));
        }

        [Theory]
        [InlineData(Gen3)]
        [InlineData(Gen4)]
        public void TheGameCubePadMapsAsSdlsAdapter(byte major)
        {
            var map = Map(9, major);
            Assert.Equal(new[] { B(1), B(3), B(0), B(2) }, Buttons(map)[..4]);   // A X B Y
            Assert.Null(map.Button(4));            // the GPA's digital L stays off the shoulders
            Assert.Equal(B(4), map.Button(5));     // Z
            Assert.Equal(B(5), map.Button(7));
            Assert.Equal(new[] { A(0), A(1), A(2), A(3), A(4), A(5) }, Axes(map));
        }

        [Fact]
        public void TheWiiClassicMapsAsSdlsWiiDriver()
        {
            foreach (var major in new[] { Gen3, Gen4 })
            {
                var map = Map(31, major);
                Assert.Equal(new[] { B(0), B(1), B(2), B(3), B(6), B(7), B(4), B(5) }, Buttons(map)[..8]);
                Assert.Equal(new[] { A(0), A(1), B(8), A(3), A(4), B(9) }, Axes(map));
            }
            Assert.Null(Map(31, Gen3).Button(10));
            Assert.Equal(B(18), Map(31, Gen4).Button(10));
        }

        [Fact]
        public void TheNunchukMapsAsSdlsWiiDriver()
        {
            var map = Map(13, Gen4);
            Assert.Equal(B(1), map.Button(4));   // C
            Assert.Equal(new[] { A(1), A(0), B(0), null, null, null }, Axes(map));
            Assert.False(map.DPad);
        }

        // ── Placements, RetroArch's Bliss-Box files ───────────────────────

        [Fact]
        public void TheDreamcastPadsTakeAnAnalogTriggerBeforeADigitalOne()
        {
            Assert.Equal(new[] { A(0), A(1), A(2), null, null, A(5) }, Axes(Map(16, Gen3)));
            Assert.Equal(new[] { A(0), A(1), A(2), null, null, A(5) }, Axes(Map(16, Gen4)));
            // The ASCII pad has only the digital ones.
            Assert.Equal(new[] { A(0), A(1), B(6), null, null, B(7) }, Axes(Map(15, Gen4)));
            Assert.Equal(new[] { B(0), B(1), B(2), B(3) }, Buttons(Map(16, Gen4))[..4]);
        }

        [Fact]
        public void TheSaturnPadsPutZAndCOnTheShouldersAndLAndROnTheTriggers()
        {
            var digital = Map(3, Gen3);
            Assert.Equal(new[] { B(0), B(1), B(2), B(3), B(6), B(7) }, Buttons(digital)[..6]);
            Assert.Equal(new string[] { null, null, B(8), null, null, B(9) }, Axes(digital));
            Assert.Equal(new[] { A(0), A(1), A(2), null, null, A(5) }, Axes(Map(8, Gen4)));
        }

        [Fact]
        public void TheNeoGeoPadMapsWhatEachFirmwaresSourceNames()
        {
            Assert.Equal(new[] { B(0), B(3), B(1), B(2) }, Buttons(Map(49, Gen3))[..4]);
            // DeviceBuddy leaves the GPA's four face buttons unlabeled.
            var gpa = Map(49, Gen4);
            Assert.All(Buttons(gpa)[..4], Assert.Null);
            Assert.Equal(B(4), gpa.Button(6));
            Assert.Equal(B(5), gpa.Button(7));
        }

        [Theory]
        [InlineData(23, Gen3)]
        [InlineData(23, Gen4)]
        [InlineData(54, Gen4)]
        public void TheTurboGrafxPadsPutIIOnSouthAndIOnEast(byte type, byte major)
        {
            var map = Map(type, major);
            Assert.Equal(B(0), map.Button(0));
            Assert.Equal(B(1), map.Button(1));
            Assert.Equal(B(4), map.Button(6));
            Assert.Equal(B(5), map.Button(7));
            Assert.Null(map.Button(2));
        }

        [Fact]
        public void TheOtherRetroArchPadsMapAsItsFilesBindThem()
        {
            var threeDo = Map(25, Gen3);
            Assert.Equal(new[] { B(1), B(7), B(0), null, B(8), B(9), B(4), B(5) }, Buttons(threeDo)[..8]);
            // The Jaguar on both generations: B, C and A on the face, then
            // Option on Back and Pause on Start.
            foreach (byte major in new byte[] { Gen3, Gen4 })
            {
                var jaguar = Map(11, major);
                Assert.Equal(new[] { B(1), B(0), B(7), null, null, null, B(4), B(5) }, Buttons(jaguar)[..8]);
            }
            Assert.Equal(-1, BlissBoxControllers.FirstArrowButton(11, Gen3));   // its keypad sits on 10 to 13
            Assert.Equal(B(0), Map(0, Gen3).Button(0));
            Assert.Equal(B(0), Map(255, Gen4).Button(0));
            var coleco = Map(1, Gen3);
            Assert.Equal(B(1), coleco.Button(0));
            Assert.Equal(B(0), coleco.Button(1));
            Assert.True(coleco.DPad);   // its disc, though its names call the hat no D-pad
        }

        [Theory]
        [InlineData(6, Gen4)]    // Atari 5200
        [InlineData(7, Gen4)]    // paddle
        [InlineData(10, Gen4)]   // Pippin
        [InlineData(29, Gen4)]   // Virtual Boy
        [InlineData(33, Gen4)]   // CD-i
        [InlineData(46, Gen4)]   // PC gameport joystick
        [InlineData(66, Gen4)]   // FM Towns
        [InlineData(78, Gen4)]   // XE-1 AP
        [InlineData(13, Gen3)]
        [InlineData(1, Gen4)]
        [InlineData(115, 2)]     // firmware 2.0 has no layouts
        [InlineData(12, Gen3)]
        public void AControllerNoSourcePlacesHasNoMap(byte type, byte major)
            => Assert.Null(BlissBoxControllers.GamepadMap(type, major, FirstPressure));

        // ── The default mapping ───────────────────────────────────────────

        /// <summary>A port read raw: 8 axes, 24 buttons and the hat, plus
        /// a DualShock 2's twelve pressure axes when asked.</summary>
        private static UserDevice Port(int pressureAxes = 0, int buttons = 24, bool hat = true)
        {
            var objects = new List<DeviceObjectItem>();
            for (int i = 0; i < 8 + pressureAxes; i++)
                objects.Add(new DeviceObjectItem { InputIndex = i, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis });
            for (int i = 0; i < buttons; i++)
                objects.Add(new DeviceObjectItem { InputIndex = i, ObjectType = DeviceObjectTypeFlags.PushButton });
            if (hat)
                objects.Add(new DeviceObjectItem { InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PointOfViewController });
            return new UserDevice
            {
                InstanceGuid = Guid.NewGuid(), VendorId = 0x16D0, ProdId = 0x0D04,
                CapType = InputDeviceType.Joystick, IsOnline = true, DeviceObjects = objects.ToArray(),
            };
        }

        private static PadSetting Default(UserDevice port, VirtualControllerType type, string profile,
            byte controller = 121, byte major = Gen4, ExtendedSlotConfig extended = null)
            => SettingsManager.CreateDefaultPadSetting(port, type, profile, extended, Map(controller, major));

        [Fact]
        public void AnXboxSlotMapsAGpaDualShock2AsSdlMapsAPlayStationPad()
        {
            var ps = Default(Port(12), VirtualControllerType.Xbox, "xbox-360-wired");
            Assert.Equal(B(0), ps.ButtonA);
            Assert.Equal(B(1), ps.ButtonB);
            Assert.Equal(B(2), ps.ButtonX);
            Assert.Equal(B(3), ps.ButtonY);
            Assert.Equal(B(6), ps.LeftShoulder);
            Assert.Equal(B(7), ps.RightShoulder);
            Assert.Equal(B(4), ps.ButtonBack);
            Assert.Equal(B(5), ps.ButtonStart);
            Assert.Equal(B(14), ps.LeftThumbButton);
            Assert.Equal(B(15), ps.RightThumbButton);
            Assert.Equal(B(18), ps.ButtonGuide);
            Assert.Equal(B(8), ps.LeftTrigger);
            Assert.Equal(B(9), ps.RightTrigger);
            Assert.Equal(A(0), ps.LeftThumbAxisX);
            Assert.Equal(A(1), ps.LeftThumbAxisY);
            Assert.Equal(A(3), ps.RightThumbAxisX);
            Assert.Equal(A(4), ps.RightThumbAxisY);
            Assert.Equal("POV 0 Up", ps.DPadUp);
            Assert.Equal("POV 0 Right", ps.DPadRight);
            Assert.Equal(ps.ComputeChecksum(), ps.PadSettingChecksum);
        }

        [Fact]
        public void APortsRawButtonsPastTheLayoutBindNothingTheyDoNotPlace()
        {
            // The raw joystick has buttons 11 to 17, which on an SDL gamepad
            // are Misc 1, the paddles and the touchpad click. On the port they
            // are arrows, keypad keys or nothing, and none may bind there.
            var port = Port();
            var xbox = Default(port, VirtualControllerType.Xbox, "xbox-series-xs-bt");
            Assert.True(string.IsNullOrEmpty(xbox.ButtonShare));
            var edge = Default(port, VirtualControllerType.PlayStation, "dualsense-edge");
            Assert.True(string.IsNullOrEmpty(edge.ButtonMute));
            Assert.True(string.IsNullOrEmpty(edge.RightPaddle));
            Assert.True(string.IsNullOrEmpty(edge.LeftPaddle));
            Assert.True(string.IsNullOrEmpty(edge.RightFunction));
            Assert.True(string.IsNullOrEmpty(edge.LeftFunction));
            Assert.True(string.IsNullOrEmpty(edge.TouchpadClick));
            Assert.True(string.IsNullOrEmpty(edge.MotionGyro));
        }

        [Fact]
        public void ADualShock2OnTheFullPresetFillsThePressureRows()
        {
            var ps = Default(Port(12), VirtualControllerType.PlayStation, "dualshock-3-full");
            Assert.Equal(new[] { 14, 13, 15, 12, 16, 17, 10, 11, 9, 8 }.Select(A),
                MappingSetMigrator.PressureTargets.Select(t => (string)typeof(PadSetting).GetProperty(t).GetValue(ps)));
            // Other presets and other pads carry none.
            Assert.True(string.IsNullOrEmpty(Default(Port(12), VirtualControllerType.PlayStation, "dualshock-3").PressureButtonA));
            Assert.True(string.IsNullOrEmpty(Default(Port(), VirtualControllerType.PlayStation, "dualshock-3-full", controller: 115).PressureButtonA));
            // A pressure axis missing from the port's list stays unbound.
            Assert.True(string.IsNullOrEmpty(Default(Port(5), VirtualControllerType.PlayStation, "dualshock-3-full").PressureButtonA));
            Assert.Equal(A(10), Default(Port(5), VirtualControllerType.PlayStation, "dualshock-3-full").PressureDPadUp);
        }

        [Fact]
        public void AnInputThePortsListLacksStaysUnbound()
        {
            var ps = Default(Port(buttons: 16), VirtualControllerType.Xbox, "xbox-360-wired");
            Assert.True(string.IsNullOrEmpty(ps.ButtonGuide));                    // button 18 is missing
            Assert.Equal(B(15), ps.RightThumbButton);
            Assert.True(string.IsNullOrEmpty(Default(Port(hat: false), VirtualControllerType.Xbox, "xbox-360-wired").DPadUp));
            // A Nunchuk's hat is no D-pad.
            Assert.True(string.IsNullOrEmpty(Default(Port(), VirtualControllerType.Xbox, "xbox-360-wired", controller: 13).DPadUp));
        }

        [Fact]
        public void ForceRawJoystickModeLeavesThePortUnmapped()
        {
            var port = Port();
            port.ForceRawJoystickMode = true;
            Assert.False(Default(port, VirtualControllerType.Xbox, "xbox-360-wired").HasAnyMapping);
        }

        [Fact]
        public void APortNoControllerPlacesGetsNoDefaultMapping()
        {
            // No live port answers for this row, so the public entry finds
            // no placement, and a joystick is not mapped.
            var ps = SettingsManager.CreateDefaultPadSetting(Port(), VirtualControllerType.Xbox, "xbox-360-wired");
            Assert.False(ps.HasAnyMapping);
            Assert.False(SettingsManager.CreateDefaultPadSetting(Port(), VirtualControllerType.Xbox,
                "xbox-360-wired", null, null).HasAnyMapping);
        }

        [Fact]
        public void ANintendoSlotTakesThePlacementByRole()
        {
            const string profile = "switch-pro";
            var ps = Default(Port(), VirtualControllerType.Nintendo, profile, controller: 27);
            string Raw(string role) => ps.GetRawMapping($"RawBtn{Models2D.NintendoPreviewMap.IndexOf(profile, role)}");
            Assert.Equal(B(0), Raw("ButtonB"));    // the SNES B, south on both
            Assert.Equal(B(1), Raw("ButtonA"));
            Assert.Equal(B(3), Raw("ButtonY"));    // the GPA's Y
            Assert.Equal(B(2), Raw("ButtonX"));
            Assert.Equal(B(6), Raw("LeftShoulder"));
            Assert.Equal(B(4), Raw("ButtonBack"));
            Assert.True(string.IsNullOrEmpty(Raw("ButtonGuide")));
            Assert.Equal("POV 0 Up", ps.GetRawMapping("RawPov0Up"));

            var ds2 = Default(Port(), VirtualControllerType.Nintendo, profile);
            string DsRaw(string role) => ds2.GetRawMapping($"RawBtn{Models2D.NintendoPreviewMap.IndexOf(profile, role)}");
            Assert.Equal(B(8), DsRaw("LeftTrigger"));   // L2 presses ZL
            Assert.Equal(B(18), DsRaw("ButtonGuide"));
            Assert.Equal(A(3), ds2.GetRawMapping("RawAxis2"));
            Assert.True(string.IsNullOrEmpty(DsRaw("ButtonShare")));
        }

        [Fact]
        public void AnN64sCRightReachesTheSwitch2ProsCButton()
        {
            const string profile = "switch2-pro";
            var ps = Default(Port(), VirtualControllerType.Nintendo, profile, controller: 19);
            Assert.Equal(B(9), ps.GetRawMapping($"RawBtn{Models2D.NintendoPreviewMap.IndexOf(profile, "ButtonC")}"));
        }

        [Fact]
        public void AnUnletteredExtendedSlotTakesThePlacementByPosition()
        {
            var cfg = new ExtendedSlotConfig();
            cfg.ComputeAxisLayout(out int[] slotX, out int[] slotY, out int[] slotTrig);
            var ps = Default(Port(), VirtualControllerType.Extended, null, extended: cfg);
            Assert.Equal(A(0), ps.GetRawMapping($"RawAxis{slotX[0]}"));
            Assert.Equal(A(4), ps.GetRawMapping($"RawAxis{slotY[1]}"));
            Assert.Equal(B(8), ps.GetRawMapping($"RawAxis{slotTrig[0]}"));
            Assert.Equal(B(9), ps.GetRawMapping($"RawAxis{slotTrig[1]}"));
            Assert.Equal(B(3), ps.GetRawMapping("RawBtn3"));
            if (cfg.ButtonCount > 10) Assert.Equal(B(18), ps.GetRawMapping("RawBtn10"));
            if (cfg.ButtonCount > 11) Assert.True(string.IsNullOrEmpty(ps.GetRawMapping("RawBtn11")));
        }

        [Fact]
        public void AValveSlotTakesThePlacementByRole()
        {
            const string profile = "steam-deck";
            var ps = Default(Port(), VirtualControllerType.Extended, profile);
            string Raw(string role) => ps.GetRawMapping($"RawBtn{Models2D.NintendoPreviewMap.IndexOf(profile, role)}");
            Assert.Equal(B(0), Raw("ButtonA"));
            Assert.Equal(B(18), Raw("ButtonGuide"));
            Assert.Equal(B(8), ps.GetRawMapping("RawAxis2"));
            Assert.True(string.IsNullOrEmpty(Raw("ButtonQuickAccess")));
            Assert.True(string.IsNullOrEmpty(Raw("Paddle1")));
        }

        [Fact]
        public void MidiAndVrSlotsTakeThePlacementToo()
        {
            var midi = Default(Port(), VirtualControllerType.Midi, null);
            Assert.Equal(B(8), midi.GetMidiMapping("MidiCC2"));
            Assert.Equal(B(18), midi.GetMidiMapping("MidiNote10"));
            Assert.Equal(B(0), midi.GetMidiMapping("MidiNote0"));
            var vr = Default(Port(), VirtualControllerType.Vr, null);
            Assert.Equal(B(8), vr.GetVrMapping("VrLTriggerClick"));
            Assert.Equal(B(6), vr.GetVrMapping("VrLGripClick"));
            Assert.Equal(B(4), vr.GetVrMapping("VrLSystem"));
        }

        // ── A controller identified after the port was assigned ───────────

        private sealed class Statics : IDisposable
        {
            private readonly SettingsCollection _settings = SettingsManager.UserSettings;
            private readonly DeviceCollection _devices = SettingsManager.UserDevices;
            private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
            private readonly bool _switch = BlissBoxApi.Enabled;

            public Statics()
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            }

            /// <summary>An assignment as a test states it: the setting, and
            /// what the port owes it.</summary>
            public UserSetting Assign(UserDevice port, int slot, PadSetting ps = null,
                BlissBoxOwedMapping owed = BlissBoxOwedMapping.None)
            {
                var us = new UserSetting { InstanceGuid = port.InstanceGuid, MapTo = slot, BlissBoxOwed = owed };
                if (ps != null) us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);
                return us;
            }

            /// <summary>An assignment as production makes one: the default
            /// the port's row builds now, the per-device tuning the Pad page
            /// writes into it, and what that default leaves owed.</summary>
            public UserSetting AssignFresh(UserDevice port, int slot)
            {
                var ps = SettingsManager.CreateDefaultPadSetting(port, VirtualControllerType.Xbox, "xbox-360-wired");
                ps.SetRawMapping("GyroTiltRange", "45");
                ps.FlushRawMappings();
                ps.UpdateChecksum();
                var us = Assign(port, slot, ps);
                us.PadSettingChecksum = ps.PadSettingChecksum;
                BlissBoxRuntime.NoteOwedDefault(us, port, fresh: true);
                return us;
            }

            /// <summary>Lists the device, as a connected or cached row is.</summary>
            public void Add(UserDevice ud)
            {
                lock (SettingsManager.UserDevices.SyncRoot) SettingsManager.UserDevices.Items.Add(ud);
            }

            public void Dispose()
            {
                SettingsManager.UserSettings = _settings;
                SettingsManager.UserDevices = _devices;
                SettingsManager.SlotMappingSets = _sets;
                BlissBoxApi.Enabled = _switch;
            }
        }

        private static (VirtualControllerType, string, ExtendedSlotConfig) XboxSlot(int slot)
            => (VirtualControllerType.Xbox, "xbox-360-wired", null);

        private static (VirtualControllerType, string, ExtendedSlotConfig) FullSlot(int slot)
            => (VirtualControllerType.PlayStation, "dualshock-3-full", null);

        private static string Field(PadSetting ps, string target)
            => (string)typeof(PadSetting).GetProperty(target).GetValue(ps);

        /// <summary>A port assigned while it searched gets an empty default
        /// that keeps the tuning the Pad page writes into it, and owes the
        /// slot its default. The controller's identification maps it once,
        /// tuning kept, and the request ends (A2).</summary>
        [Fact]
        public void APortAssignedWhileItSearchedIsMappedOnceItsControllerIsIdentified()
        {
            using var statics = new Statics();
            BlissBoxApi.Enabled = true;
            var port = Port(12);
            var us = statics.AssignFresh(port, 2);
            Assert.True(string.IsNullOrEmpty(us.GetPadSetting().ButtonA));
            // The tuning entry alone counts as a mapping, which is what kept
            // the earlier binds-nothing rule from ever mapping this slot.
            Assert.True(us.GetPadSetting().HasAnyMapping);
            Assert.Equal(BlissBoxOwedMapping.Default, us.BlissBoxOwed);
            Assert.True(DeviceService.PortHasOwedMapping(port));

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot, out bool mapped));
            Assert.True(mapped);

            var ps = us.GetPadSetting();
            Assert.Equal(B(0), ps.ButtonA);
            Assert.Equal(B(18), ps.ButtonGuide);
            Assert.Equal("45", ps.GetRawMapping("GyroTiltRange"));
            Assert.Equal(ps.PadSettingChecksum, us.PadSettingChecksum);
            Assert.Equal(BlissBoxOwedMapping.None, us.BlissBoxOwed);
            Assert.False(DeviceService.PortHasOwedMapping(port));
            Assert.False(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot, out mapped));
            Assert.False(mapped);
        }

        [Fact]
        public void AnEmptySettingKeepsItsTuningWhenTheMappingFills()
        {
            using var statics = new Statics();
            var port = Port();
            var empty = new PadSetting { LeftThumbDeadZoneX = "12" };
            var us = statics.Assign(port, 0, empty, BlissBoxOwedMapping.Default);

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(27, Gen4), XboxSlot, out bool mapped));
            Assert.True(mapped);

            Assert.Same(empty, us.GetPadSetting());
            Assert.Equal("12", empty.LeftThumbDeadZoneX);
            Assert.Equal(B(1), empty.ButtonB);
            Assert.Equal(empty.ComputeChecksum(), empty.PadSettingChecksum);
            Assert.Equal(empty.PadSettingChecksum, us.PadSettingChecksum);
        }

        /// <summary>A port fills only what an assignment or a fill left it
        /// owing. A slot mapped at its assignment keeps its mapping, and a
        /// slot the user has since emptied stays empty through the next
        /// port change (B1).</summary>
        [Fact]
        public void APortOwesNothingItWasNotAsked()
        {
            using var statics = new Statics();
            var port = Port();
            var mapped = new PadSetting { ButtonA = B(1) };
            statics.Assign(port, 0, mapped);
            var emptied = statics.Assign(port, 1, new PadSetting());
            Assert.False(DeviceService.PortHasOwedMapping(port));
            Assert.False(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot, out bool changed));
            Assert.False(changed);
            Assert.True(string.IsNullOrEmpty(mapped.ButtonB));
            Assert.False(emptied.GetPadSetting().HasAnyMapping);
        }

        [Fact]
        public void OnlyTheSlotsThePortIsAssignedToAreMapped()
        {
            using var statics = new Statics();
            var port = Port();
            var parked = statics.Assign(port, -1, owed: BlissBoxOwedMapping.Default);
            var assigned = statics.Assign(port, 3, owed: BlissBoxOwedMapping.Default);
            var shapes = new List<int>();

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), slot =>
            {
                shapes.Add(slot);
                return (VirtualControllerType.PlayStation, "dualshock-4-v2", null);
            }, out bool mapped));

            Assert.True(mapped);
            Assert.Equal(new[] { 3 }, shapes);
            Assert.Null(parked.GetPadSetting());
            Assert.Equal(BlissBoxOwedMapping.Default, parked.BlissBoxOwed);
            Assert.Equal(B(0), assigned.GetPadSetting().ButtonA);
        }

        /// <summary>A row the user bound, recorded or cleared while the default
        /// was owed stays as the user left it, on every port the slot owes,
        /// whichever device the row names. Every other empty field takes the
        /// default, so the slot ends as an assignment made with the controller
        /// in the port would have left it, with the user's edits on top (A2,
        /// B1).</summary>
        [Fact]
        public void TheDefaultLeavesTheRowsTheUserTouched()
        {
            using var statics = new Statics();
            var port = Port();
            var us = statics.Assign(port, 0, new PadSetting(), BlissBoxOwedMapping.Default);
            var bystander = statics.Assign(Port(), 0, new PadSetting());
            var elsewhere = statics.Assign(port, 1, new PadSetting(), BlissBoxOwedMapping.Default);

            Assert.True(DeviceService.KeepAuthoredRow(0, "Base", "ButtonA", null));
            Assert.True(DeviceService.KeepAuthoredRow(0, null, "LeftThumbAxisX", "LeftThumbAxisXNeg"));
            Assert.False(DeviceService.KeepAuthoredRow(0, "Base", "ButtonA", null));
            // A shift layer's rows carry no default.
            Assert.False(DeviceService.KeepAuthoredRow(0, "Shift 1", "ButtonB", null));
            Assert.Equal(new[] { "ButtonA", "LeftThumbAxisX", "LeftThumbAxisXNeg" }, us.BlissBoxKeptTargets);
            Assert.Null(bystander.BlissBoxKeptTargets);
            Assert.Null(elsewhere.BlissBoxKeptTargets);

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot, out bool mapped));
            Assert.True(mapped);
            var ps = us.GetPadSetting();
            Assert.True(string.IsNullOrEmpty(ps.ButtonA));
            Assert.True(string.IsNullOrEmpty(ps.LeftThumbAxisX));
            Assert.Equal(B(1), ps.ButtonB);
            Assert.Equal(A(1), ps.LeftThumbAxisY);
            Assert.Null(us.BlissBoxKeptTargets);
            Assert.Equal(B(0), elsewhere.GetPadSetting().ButtonA);
        }

        /// <summary>A change to the DualShock 3 (SIXAXIS): Full while the
        /// port's DualShock 2 is unplugged leaves the ten pressure rows owed.
        /// The slot's ordinary bindings neither block nor cancel them, a
        /// pressure row the user touched meanwhile stays as the user left it,
        /// and nothing outside the ten is filled (B4).</summary>
        [Fact]
        public void APressureRequestFillsOnlyThePressureRowsTheUserLeftAlone()
        {
            using var statics = new Statics();
            BlissBoxApi.Enabled = true;
            var port = Port(12);
            statics.Add(port);
            var bound = new PadSetting { ButtonA = B(5) };
            var us = statics.Assign(port, 0, bound);

            Assert.False(DeviceService.FillEmptyPressureMappingsForSlot(0, "dualshock-3-full"));
            Assert.Equal(BlissBoxOwedMapping.Pressure, us.BlissBoxOwed);
            DeviceService.KeepAuthoredRow(0, "Base", "ButtonB", null);
            DeviceService.KeepAuthoredRow(0, "Base", MappingSetMigrator.PressureTargets[0], null);

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), FullSlot, out bool mapped));
            Assert.True(mapped);
            Assert.Equal(B(5), bound.ButtonA);
            Assert.True(string.IsNullOrEmpty(bound.ButtonX));
            Assert.True(string.IsNullOrEmpty(Field(bound, MappingSetMigrator.PressureTargets[0])));
            for (int i = 1; i < MappingSetMigrator.PressureTargets.Count; i++)
                Assert.False(string.IsNullOrEmpty(Field(bound, MappingSetMigrator.PressureTargets[i])));
            Assert.Equal(BlissBoxOwedMapping.None, us.BlissBoxOwed);
        }

        /// <summary>A live profile change's fill and a fill of an existing
        /// setting at assignment owe the default where the port answered
        /// nothing. A fresh default replaces whatever the assignment owed,
        /// kept rows included.</summary>
        [Fact]
        public void AFillOwesTheDefaultAndAFreshDefaultReplacesWhatWasOwed()
        {
            using var statics = new Statics();
            BlissBoxApi.Enabled = true;
            var port = Port();
            statics.Add(port);
            var us = statics.Assign(port, 0, new PadSetting());
            DeviceService.FillEmptyAutoMappingsForSlot(0, VirtualControllerType.Extended, "steam-deck");
            Assert.Equal(BlissBoxOwedMapping.Default, us.BlissBoxOwed);

            us.BlissBoxOwed = BlissBoxOwedMapping.Pressure;
            us.BlissBoxKeptTargets = new[] { "ButtonA" };
            BlissBoxRuntime.NoteOwedDefault(us, port, fresh: false);
            Assert.Equal(BlissBoxOwedMapping.Default | BlissBoxOwedMapping.Pressure, us.BlissBoxOwed);
            Assert.Equal(new[] { "ButtonA" }, us.BlissBoxKeptTargets);
            BlissBoxRuntime.NoteOwedDefault(us, port, fresh: true);
            Assert.Equal(BlissBoxOwedMapping.Default, us.BlissBoxOwed);
            Assert.Null(us.BlissBoxKeptTargets);
            port.ForceRawJoystickMode = true;
            BlissBoxRuntime.NoteOwedDefault(us, port, fresh: true);
            Assert.Equal(BlissBoxOwedMapping.None, us.BlissBoxOwed);
        }

        /// <summary>A live wire change renames the rows a port's owed default
        /// leaves alone as it renames the bindings, Minus moving from the
        /// original Pro Controller's button 8 to the Switch 2 Pro's 14, and
        /// drops a row the new pad lacks.</summary>
        [Fact]
        public void TheKeptRowsMoveWithTheWire()
        {
            using var statics = new Statics();
            const int slot = 7;
            var us = statics.Assign(Port(), slot, new PadSetting(), BlissBoxOwedMapping.Default);
            us.BlissBoxKeptTargets = new[] { "RawBtn8", "RawBtn63", "ButtonA" };
            SettingsManager.StampNintendoWire(slot, "switch-pro");
            try
            {
                SettingsManager.TranslateNintendoRawMappings(slot, "switch2-pro-controller");
                Assert.Equal(new[] { "RawBtn14", "ButtonA" }, us.BlissBoxKeptTargets);
            }
            finally { SettingsManager.StampNintendoWire(slot, null); }
        }

        /// <summary>A request with nothing left to fill still ends, and the
        /// caller is told so it saves, so the request does not come back after
        /// a restart.</summary>
        [Fact]
        public void ARequestWithNothingToFillEndsAndIsSaved()
        {
            using var statics = new Statics();
            var port = Port(12);
            var us = statics.Assign(port, 0, new PadSetting(), BlissBoxOwedMapping.Pressure);
            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot, out bool mapped));
            Assert.False(mapped);
            Assert.Equal(BlissBoxOwedMapping.None, us.BlissBoxOwed);
            Assert.False(us.GetPadSetting().HasAnyMapping);
        }

        /// <summary>Clear All, a paste and Copy From replace the slot's whole
        /// routing, so nothing a port owed it is filled after, an empty paste
        /// and Copy From an empty slot included. Force Raw Joystick Mode makes
        /// the device's mapping manual on every slot (B1).</summary>
        [Fact]
        public void ReplacingASlotsRoutingDropsWhatItsPortsOwedIt()
        {
            using var statics = new Statics();
            var port = Port();
            var cleared = statics.Assign(port, 0, new PadSetting(), BlissBoxOwedMapping.Default);
            var pasted = statics.Assign(port, 1, new PadSetting(), BlissBoxOwedMapping.Default | BlissBoxOwedMapping.Pressure);
            var copied = statics.Assign(port, 2, new PadSetting(), BlissBoxOwedMapping.Default);
            var other = statics.Assign(Port(), 3, new PadSetting(), BlissBoxOwedMapping.Default);
            DeviceService.KeepAuthoredRow(1, "Base", "ButtonA", null);

            Assert.True(DeviceService.CancelOwedMappingForSlot(0));
            Assert.False(DeviceService.CancelOwedMappingForSlot(0));
            InputService.ApplySlotMappingSetFromRows(1, new List<MappingRow>());
            InputService.ReplaceSlotMappingSet(2, 9);
            Assert.Equal(BlissBoxOwedMapping.None, cleared.BlissBoxOwed);
            Assert.Equal(BlissBoxOwedMapping.None, pasted.BlissBoxOwed);
            Assert.Null(pasted.BlissBoxKeptTargets);
            Assert.Equal(BlissBoxOwedMapping.None, copied.BlissBoxOwed);
            Assert.Equal(BlissBoxOwedMapping.Default, other.BlissBoxOwed);

            Assert.True(DeviceService.CancelOwedMappingForDevice(other.InstanceGuid));
            Assert.Equal(BlissBoxOwedMapping.None, other.BlissBoxOwed);
        }

        /// <summary>Only a default built with nothing to place the port's
        /// controls through is owed: a local port read raw with no controller
        /// identified, or read through SDL from a row cached in the raw shape.
        /// Force Raw keeps mapping manual, and a Remote Link copy, live or
        /// cached, follows its owner (A7, N1).</summary>
        [Fact]
        public void OnlyAPortWithNothingToPlaceItsControlsOwesItsDefault()
        {
            using var statics = new Statics();
            var raw = Port();
            var sdl = Port();
            sdl.CapType = InputDeviceType.Gamepad;
            var forced = Port();
            forced.ForceRawJoystickMode = true;
            var cachedPeer = Port();
            cachedPeer.DevicePath = "peer://0123456789abcdef/7";
            var livePeer = Port();
            livePeer.Device = new PadForge.Engine.RemoteLink.RemotePeerDevice(new PadForge.Engine.RemoteLink.RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "0123456789abcdef0123456789abcdef",
                PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
                Name = "Peer",
                Online = true,
            });

            BlissBoxApi.Enabled = true;
            Assert.True(BlissBoxRuntime.DefaultOwed(raw));
            Assert.True(BlissBoxRuntime.DefaultOwed(sdl));
            Assert.False(BlissBoxRuntime.DefaultOwed(forced));
            Assert.False(BlissBoxRuntime.DefaultOwed(cachedPeer));
            Assert.False(BlissBoxRuntime.DefaultOwed(livePeer));
            Assert.False(BlissBoxRuntime.OwedDefaultReady(raw, out var map));
            Assert.Null(map);

            BlissBoxApi.Enabled = false;
            Assert.True(BlissBoxRuntime.DefaultOwed(raw));
            Assert.False(BlissBoxRuntime.DefaultOwed(sdl));
            Assert.False(BlissBoxRuntime.DefaultOwed(forced));
            Assert.False(BlissBoxRuntime.DefaultOwed(cachedPeer));
            Assert.False(BlissBoxRuntime.OwedDefaultReady(raw, out _));
            Assert.False(BlissBoxRuntime.DefaultOwed(new UserDevice
            {
                VendorId = 0x054C, ProdId = 0x0CE6, CapType = InputDeviceType.Gamepad,
            }));
        }

        /// <summary>With the reader on, a port cached as SDL's gamepad gets an
        /// empty default, not SDL's positions, which the raw read gives other
        /// meanings, and its setting is never judged foreign by that cached
        /// shape. With the reader off the cached SDL row maps at once
        /// (N1).</summary>
        [Fact]
        public void APortCachedAsSdlsGamepadMapsOnlyThroughThePlacementWhileTheReaderIsOn()
        {
            using var statics = new Statics();
            var port = Port();
            port.CapType = InputDeviceType.Gamepad;
            port.RawButtonCount = 24;
            var foreign = typeof(DeviceService).GetMethod("IsForeignPadSetting",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(foreign);
            bool Foreign(PadSetting ps) => (bool)foreign.Invoke(null,
                new object[] { ps, port, VirtualControllerType.Xbox, "xbox-360-wired" });
            var authored = new PadSetting { ButtonA = B(30) };

            BlissBoxApi.Enabled = true;
            Assert.False(SettingsManager.CreateDefaultPadSetting(port, VirtualControllerType.Xbox, "xbox-360-wired").HasAnyMapping);
            Assert.Equal(B(0), Default(port, VirtualControllerType.Xbox, "xbox-360-wired").ButtonA);
            Assert.False(Foreign(authored));

            BlissBoxApi.Enabled = false;
            Assert.Equal(B(4), SettingsManager.CreateDefaultPadSetting(port, VirtualControllerType.Xbox, "xbox-360-wired").LeftShoulder);
            Assert.True(Foreign(authored));
        }

        /// <summary>What a port owes survives the settings file and a profile
        /// snapshot, and an assignment that owes nothing writes
        /// nothing.</summary>
        [Fact]
        public void WhatAPortOwesRoundTripsThroughTheSettingsFileAndProfiles()
        {
            static T RoundTrip<T>(T value, out string xml)
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(typeof(T));
                using var writer = new System.IO.StringWriter();
                serializer.Serialize(writer, value);
                xml = writer.ToString();
                using var reader = new System.IO.StringReader(xml);
                return (T)serializer.Deserialize(reader);
            }

            var us = RoundTrip(new UserSetting
            {
                BlissBoxOwed = BlissBoxOwedMapping.Default | BlissBoxOwedMapping.Pressure,
                BlissBoxKeptTargets = new[] { "ButtonA", MappingSetMigrator.PressureTargets[0] },
            }, out _);
            Assert.Equal(BlissBoxOwedMapping.Default | BlissBoxOwedMapping.Pressure, us.BlissBoxOwed);
            Assert.Equal(new[] { "ButtonA", MappingSetMigrator.PressureTargets[0] }, us.BlissBoxKeptTargets);
            RoundTrip(new UserSetting(), out string plain);
            Assert.DoesNotContain("BlissBox", plain);

            var entry = RoundTrip(new ProfileEntry
            {
                BlissBoxOwed = BlissBoxOwedMapping.Pressure,
                BlissBoxKeptTargets = new[] { "ButtonB" },
            }, out _);
            Assert.Equal(BlissBoxOwedMapping.Pressure, entry.BlissBoxOwed);
            Assert.Equal(new[] { "ButtonB" }, entry.BlissBoxKeptTargets);
            RoundTrip(new ProfileEntry(), out string plainEntry);
            Assert.DoesNotContain("BlissBox", plainEntry);
        }

        /// <summary>The runtime half, which needs a live adapter port to run:
        /// the default mapping asks the port for its placement, and the port
        /// answers only for a row read raw with a controller identified in
        /// it now, not the last one it saw.</summary>
        [Fact]
        public void TheDefaultMappingAsksThePortForTheControllerInItNow()
        {
            string manager = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Common", "SettingsManager.cs"));
            Assert.Contains("ud == null ? null : Common.Input.BlissBoxRuntime.GamepadMapFor(ud));", manager);
            string runtime = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Common", "Input", "BlissBoxRuntime.cs"));
            int at = runtime.IndexOf("public static BlissBoxGamepadMap GamepadMapFor(UserDevice ud)", StringComparison.Ordinal);
            string body = runtime[at..runtime.IndexOf("\n        }", at, StringComparison.Ordinal)];
            Assert.Contains("!OpenedRaw(ud.Device)", body);
            Assert.Contains("Find(ud)?.Session.LiveInfo is { } info", body);
            Assert.DoesNotContain("KnownInfo", body);
            Assert.Contains("PressureAxisBase(ud.Device)", body);
        }

        private static string Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " moved");
            return src[at..src.IndexOf("\n        }", at, StringComparison.Ordinal)];
        }

        /// <summary>A handler's text, from its subscription to the line that
        /// closes it at the same indentation.</summary>
        private static string Handler(string src, string subscription, string close = "};")
        {
            int at = src.IndexOf(subscription, StringComparison.Ordinal);
            Assert.True(at >= 0, subscription + " moved");
            int line = src.LastIndexOf('\n', at) + 1;
            int end = src.IndexOf("\n" + src[line..at] + close, at, StringComparison.Ordinal);
            Assert.True(end > at, subscription + " has no closing line");
            return src[at..end];
        }

        /// <summary>The runtime wiring: a port change, a device arrival and a
        /// profile switch each complete what a port that can be placed owes,
        /// the grids' edits are flushed first and only when a port owes
        /// something, any change saves, and only a mapping change reloads the
        /// grids.</summary>
        [Fact]
        public void APortThatCanBePlacedGetsWhatItOwesThroughTheAssignmentRefresh()
        {
            string src = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("CompleteOwedBlissBoxMappings();", Body(src, "private void OnBlissBoxPortChanged"));
            Assert.Contains("CompleteOwedBlissBoxMappings();", Body(src, "private void OnDevicesUpdated"));
            Assert.Contains("_dispatcher?.BeginInvoke(new Action(CompleteOwedBlissBoxMappings));",
                Body(src, "public void ApplyProfile(ProfileData profile)"));

            string complete = Body(src, "private void CompleteOwedBlissBoxMappings()");
            Assert.Contains("BlissBoxRuntime.OwedDefaultReady(ud, out var map)", complete);
            Assert.Contains("AutoMapIdentifiedPorts(placed, out bool mapped)", complete);
            Assert.Contains("_settingsService?.MarkDirty();", complete);
            Assert.Contains("if (mapped) RefreshAfterDeviceAssignmentChange();", complete);

            string glue = Body(src, "private bool AutoMapIdentifiedPorts(");
            int precheck = glue.IndexOf("if (!placed.Exists(p => DeviceService.PortHasOwedMapping(p.Device))) return false;", StringComparison.Ordinal);
            Assert.True(precheck >= 0, "the flush runs only for a port that owes a slot something");
            Assert.True(precheck < glue.IndexOf("FlushPendingDeviceEdits", StringComparison.Ordinal));
            Assert.True(glue.IndexOf("FlushPendingDeviceEdits", StringComparison.Ordinal)
                < glue.IndexOf("DeviceService.AutoMapIdentifiedPort(", StringComparison.Ordinal));
        }

        /// <summary>The user's own edits on the grid keep their rows, and the
        /// edits that replace a slot's routing drop what it was owed, while a
        /// reload, which sets the same properties, does neither.</summary>
        [Fact]
        public void TheUsersEditsReachWhatAPortOwes()
        {
            string window = System.IO.File.ReadAllText(RepoPath("PadForge.App", "MainWindow.xaml.cs"));
            const string keep = "DeviceService.KeepAuthoredRow(capturedPad.PadIndex, capturedPad.ActiveLayerMask, mapping.TargetSettingName, mapping.NegSettingName)";
            const string guard = "!InputService.SuppressMappingEditPush && !InputService.VmMappingsStale";
            foreach (string hook in new[]
            {
                "mapping.PropertyChanged += (s, e) =>",
                "msi.PropertyChanged += (s, e) =>",
                "mapping.ExtraSources.CollectionChanged += (s, e) =>",
                "mapping.Cleared += (s, e) =>",
            })
            {
                string body = Handler(window, hook);
                Assert.Contains(keep, body);
                Assert.Contains(guard, body);
            }
            // A stateful kind and its keys carry no descriptor, so the kind
            // and key changes keep the row as a pick does.
            string msi = Handler(window, "msi.PropertyChanged += (s, e) =>");
            string keepCondition = msi[..msi.IndexOf(keep, StringComparison.Ordinal)];
            foreach (string field in new[] { "DeviceGuid", "Descriptor", "Kind", "ParamUp", "ParamDown", "ParamModifier" })
                Assert.Contains($"nameof(MappingSourceItem.{field})", keepCondition);
            string item = System.IO.File.ReadAllText(RepoPath("PadForge.App", "ViewModels", "MappingItem.cs"));
            Assert.Contains("Cleared?.Invoke(this, EventArgs.Empty);",
                Handler(item, "_clearCommand ??= new RelayCommand(() =>", "});"));

            string page = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Views", "PadPage.xaml.cs"));
            string clearAll = Body(page, "private void ClearAllMappings_Click(");
            Assert.True(clearAll.IndexOf("vm.ClearMappingsCommand.Execute(null);", StringComparison.Ordinal)
                < clearAll.IndexOf("DeviceService.CancelOwedMappingForSlot(vm.PadIndex)", StringComparison.Ordinal));
            Assert.Contains("vm.ActiveLayerMask == \"Base\")", clearAll);

            string devices = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "DeviceService.cs"));
            string hiding = Body(devices, "private void OnDeviceHidingChanged(");
            Assert.True(hiding.IndexOf("CancelOwedMappingForDevice(instanceGuid)", StringComparison.Ordinal)
                < hiding.IndexOf("ud.ForceRawJoystickMode = row.ForceRawJoystickMode;", StringComparison.Ordinal));

            string input = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "InputService.cs"));
            Assert.Contains("DeviceService.CancelOwedMapping(us);", Body(input, "public void ApplyPadSettingToCurrentDeviceTranslated("));
            Assert.Contains("DeviceService.CancelOwedMapping(us);", Body(input, "public void ApplyPadSettingToCurrentDevice("));
        }

        /// <summary>Every default built for an assignment records what it
        /// leaves owed, and what a profile holds travels with it: both
        /// assignment paths, the type re-map, both fills and both profile
        /// snapshot builders, with the profile switch restoring it, None
        /// included.</summary>
        [Fact]
        public void EveryAssignmentPathRecordsWhatItsPortOwes()
        {
            string devices = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "DeviceService.cs"));
            Assert.Equal(2, CountOf(devices, "BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: true);"));
            Assert.Equal(2, CountOf(devices, "BlissBoxRuntime.NoteOwedDefault(us, udForGuid, fresh: false);"));
            Assert.Contains("us.BlissBoxOwed |= BlissBoxOwedMapping.Default;", Body(devices, "public static void FillEmptyAutoMappingsForSlot("));
            Assert.Contains("us.BlissBoxOwed |= BlissBoxOwedMapping.Pressure;", Body(devices, "private static bool FillEmptyPlayStationMappingsForSlot("));
            string manager = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Common", "SettingsManager.cs"));
            Assert.Contains("BlissBoxRuntime.NoteOwedDefault(us, ud, fresh: true);", Body(manager, "public static void ReAutoMapSlot("));

            string input = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "InputService.cs"));
            string settings = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "SettingsService.cs"));
            foreach (string builder in new[] { input, settings })
            {
                Assert.Contains("BlissBoxOwed = us.BlissBoxOwed,", builder);
                Assert.Contains("BlissBoxKeptTargets = us.BlissBoxKeptTargets,", builder);
            }
            string apply = Body(input, "public void ApplyProfile(ProfileData profile)");
            Assert.Contains("assignments[us] = (entry.MapTo, template.CloneDeep(), entry.BlissBoxOwed, entry.BlissBoxKeptTargets);", apply);
            Assert.Contains("us.BlissBoxOwed = assign.Owed;", apply);
            Assert.Contains("us.BlissBoxKeptTargets = assign.Kept;", apply);
        }

        private static int CountOf(string text, string value)
        {
            int count = 0;
            for (int at = text.IndexOf(value, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static string RepoPath(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            return System.IO.Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }

        /// <summary>The Gamepad names read a port through its map on every
        /// poll, so one map serves every caller with the same three values.</summary>
        [Fact]
        public void OnePlacementServesEveryCallerWithTheSameValues()
            => Assert.Same(BlissBoxControllers.GamepadMap(121, Gen4, -1), BlissBoxControllers.GamepadMap(121, Gen4, -1));
    }
}
