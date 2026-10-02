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
            var jaguar = Map(11, Gen4);
            Assert.Equal(new[] { B(1), B(0), B(7), null }, Buttons(jaguar)[..4]);
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
        [InlineData(11, Gen3)]   // no 3.x layout
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

            public Statics()
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            }

            public UserSetting Assign(UserDevice port, int slot, PadSetting ps = null)
            {
                var us = new UserSetting { InstanceGuid = port.InstanceGuid, MapTo = slot };
                if (ps != null) us.SetPadSetting(ps);
                lock (SettingsManager.UserSettings.SyncRoot) SettingsManager.UserSettings.Items.Add(us);
                return us;
            }

            public void Dispose()
            {
                SettingsManager.UserSettings = _settings;
                SettingsManager.UserDevices = _devices;
                SettingsManager.SlotMappingSets = _sets;
            }
        }

        private static (VirtualControllerType, string, ExtendedSlotConfig) XboxSlot(int slot)
            => (VirtualControllerType.Xbox, "xbox-360-wired", null);

        [Fact]
        public void APortAssignedWhileItSearchedIsMappedOnceItsControllerIsIdentified()
        {
            using var statics = new Statics();
            var port = Port(12);
            var us = statics.Assign(port, 2);
            Assert.True(DeviceService.PortHasUnboundSlot(port));

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot));

            var ps = us.GetPadSetting();
            Assert.Equal(B(0), ps.ButtonA);
            Assert.Equal(B(18), ps.ButtonGuide);
            Assert.Equal(ps.PadSettingChecksum, us.PadSettingChecksum);
            Assert.False(DeviceService.PortHasUnboundSlot(port));
            Assert.False(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot));
        }

        [Fact]
        public void AnEmptySettingKeepsItsTuningWhenTheMappingFills()
        {
            using var statics = new Statics();
            var port = Port();
            var empty = new PadSetting { LeftThumbDeadZoneX = "12" };
            var us = statics.Assign(port, 0, empty);

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(27, Gen4), XboxSlot));

            Assert.Same(empty, us.GetPadSetting());
            Assert.Equal("12", empty.LeftThumbDeadZoneX);
            Assert.Equal(B(1), empty.ButtonB);
            Assert.Equal(empty.ComputeChecksum(), empty.PadSettingChecksum);
            Assert.Equal(empty.PadSettingChecksum, us.PadSettingChecksum);
        }

        [Fact]
        public void APortThatBindsAnythingKeepsItsMapping()
        {
            using var statics = new Statics();
            var port = Port();
            var mapped = new PadSetting { ButtonA = B(1) };
            var us = statics.Assign(port, 0, mapped);
            Assert.False(DeviceService.PortHasUnboundSlot(port));
            Assert.False(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), XboxSlot));
            Assert.True(string.IsNullOrEmpty(mapped.ButtonB));

            // A source of its own in the slot's set counts too, a row the
            // grid recorded that the PadSetting does not carry.
            var other = Port();
            statics.Assign(other, 1);
            SettingsManager.SlotMappingSets[1] = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new()
                    {
                        Target = "ButtonA", LayerMask = "Base",
                        Sources = new List<MappingSource> { new() { DeviceGuid = other.InstanceGuid.ToString().ToUpperInvariant(), Descriptor = B(0) } },
                    },
                },
            };
            Assert.False(DeviceService.PortHasUnboundSlot(other));
            Assert.False(DeviceService.AutoMapIdentifiedPort(other, Map(121, Gen4), XboxSlot));
        }

        [Fact]
        public void OnlyTheSlotsThePortIsAssignedToAreMapped()
        {
            using var statics = new Statics();
            var port = Port();
            var parked = statics.Assign(port, -1);
            var assigned = statics.Assign(port, 3);
            var shapes = new List<int>();

            Assert.True(DeviceService.AutoMapIdentifiedPort(port, Map(121, Gen4), slot =>
            {
                shapes.Add(slot);
                return (VirtualControllerType.PlayStation, "dualshock-4-v2", null);
            }));

            Assert.Equal(new[] { 3 }, shapes);
            Assert.Null(parked.GetPadSetting());
            Assert.Equal(B(0), assigned.GetPadSetting().ButtonA);
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

        [Fact]
        public void APortChangeMapsUnboundPortsThroughTheAssignmentRefresh()
        {
            string src = System.IO.File.ReadAllText(RepoPath("PadForge.App", "Services", "InputService.cs"));
            int handler = src.IndexOf("private void OnBlissBoxPortChanged", StringComparison.Ordinal);
            int next = src.IndexOf("private bool AutoMapIdentifiedPorts", handler, StringComparison.Ordinal);
            string body = src[handler..next];
            Assert.Contains("BlissBoxRuntime.GamepadMapFor(ud) is { } map", body);
            Assert.Contains("AutoMapIdentifiedPorts(placed)", body);
            Assert.Contains("RefreshAfterDeviceAssignmentChange();", body);
            int glue = src.IndexOf("{", next, StringComparison.Ordinal);
            string glueBody = src[glue..src.IndexOf("\n        }", glue, StringComparison.Ordinal)];
            // The grids' pending edits reach the settings before the test for
            // an unbound slot, and only when a port has one.
            int precheck = glueBody.IndexOf("if (!placed.Exists(p => DeviceService.PortHasUnboundSlot(p.Device))) return false;", StringComparison.Ordinal);
            Assert.True(precheck >= 0, "the flush runs only for a port with an unbound slot");
            Assert.True(precheck < glueBody.IndexOf("FlushPendingDeviceEdits", StringComparison.Ordinal));
            Assert.True(glueBody.IndexOf("FlushPendingDeviceEdits", StringComparison.Ordinal)
                < glueBody.IndexOf("DeviceService.AutoMapIdentifiedPort(", StringComparison.Ordinal));
        }

        private static string RepoPath(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            return System.IO.Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
        }
    }
}
