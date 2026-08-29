using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using PadForge.ViewModels;
using PadForge.Resources.Strings;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Both trackpads on every Valve profile. Each Valve frame carries one
    /// finger per pad, so the slot's two-finger touch surface splits as
    /// finger 0 = left pad, finger 1 = right pad: the grid rows, the
    /// packer's split, the 1:1 automap of a physical Valve pad, and the
    /// motion-row backfill for an Extended slot on a Valve profile.
    /// </summary>
    public class ValveTouchpadTests
    {
        private static short I16(byte[] b, int off) => (short)(b[off] | (b[off + 1] << 8));

        private static byte[] Pack(string id, TouchpadState tp)
        {
            var raw = RawHidState.Create(8, 32, 1);
            raw.Povs[0] = -1;
            raw.Axes[2] = raw.Axes[5] = short.MinValue;   // triggers rest low

            var dest = new byte[ValveReportPackers.MaxReportSize];
            ValveReportPackers.ForProfile(id).Pack(raw, tp, default, 1, dest);
            return dest;
        }

        /// <summary>Pad offsets per wire: the Deck's sLeftPadX/Y at 16/18
        /// and sRightPadX/Y at 20/22 (SDL_hidapi_steamdeck.c), the 2015
        /// ValveInReport's at the same offsets (SDL_hidapi_steam.c), the
        /// 2026 TritonMTUFull_t's sLeftPad at 18/20 and sRightPad at 24/26
        /// (controller_structs.h). Every driver decodes y as
        /// -wire / 65536 + 0.5, so a top-of-pad touch (y = 0) is +wire.</summary>
        [Theory]
        [InlineData("steam-deck-composite", 16, 18, 20, 22)]
        [InlineData("steam-controller-composite", 16, 18, 20, 22)]
        [InlineData("steam-controller-2", 18, 20, 24, 26)]
        public void Finger0IsTheLeftPad_Finger1IsTheRight(string id, int lx, int ly, int rx, int ry)
        {
            var left = Pack(id, new TouchpadState { Down0 = true, X0 = 1f, Y0 = 0f });
            Assert.True(I16(left, lx) > 30000, $"{id} left X {I16(left, lx)}");
            Assert.True(I16(left, ly) > 30000, $"{id} left Y {I16(left, ly)}");
            Assert.Equal(0, I16(left, rx));
            Assert.Equal(0, I16(left, ry));

            var right = Pack(id, new TouchpadState { Down1 = true, X1 = 0f, Y1 = 1f });
            Assert.Equal(0, I16(right, lx));
            Assert.Equal(0, I16(right, ly));
            Assert.True(I16(right, rx) < -30000, $"{id} right X {I16(right, rx)}");
            Assert.True(I16(right, ry) < -30000, $"{id} right Y {I16(right, ry)}");
        }

        /// <summary>A lifted finger writes a centered pad, never a stale
        /// position.</summary>
        [Theory]
        [InlineData("steam-deck-composite", 16, 18)]
        [InlineData("steam-controller-2", 18, 20)]
        public void LiftedFinger_CentersThePad(string id, int lx, int ly)
        {
            var b = Pack(id, new TouchpadState { Down0 = false, X0 = 1f, Y0 = 1f });
            Assert.Equal(0, I16(b, lx));
            Assert.Equal(0, I16(b, ly));
        }

        /// <summary>The grid lists both pads as position and touch rows
        /// under the pad's own name, and no shared click row: the clicks
        /// are the wire's own raw buttons.</summary>
        [Theory]
        [InlineData("steam-deck-composite")]
        [InlineData("steam-controller")]
        [InlineData("steam-controller-composite")]
        [InlineData("steam-controller-2")]
        public void Grid_ListsBothPads(string id)
        {
            var vm = new PadViewModel(0)
            {
                OutputType = VirtualControllerType.Extended,
                ProfileId = id,
            };
            var byTarget = vm.Mappings.ToDictionary(m => m.TargetSettingName, m => m.TargetLabel);
            var s = Strings.Instance;
            Assert.Equal(s.Mapping_LeftPadX, byTarget["TouchpadX1"]);
            Assert.Equal(s.Mapping_LeftPadY, byTarget["TouchpadY1"]);
            Assert.Equal(s.Mapping_LeftPadTouch, byTarget["TouchpadContact1"]);
            Assert.Equal(s.Mapping_RightPadX, byTarget["TouchpadX2"]);
            Assert.Equal(s.Mapping_RightPadY, byTarget["TouchpadY2"]);
            Assert.Equal(s.Mapping_RightPadTouch, byTarget["TouchpadContact2"]);
            Assert.False(byTarget.ContainsKey("TouchpadClick"));
            Assert.Contains($"RawBtn{NintendoPreviewMap.IndexOf(id, "LeftTouchpadClick")}", byTarget.Keys);
            // Whichever name this family gives the right pad's click. The
            // 2015 calls it the right STICK button, because that is what SDL
            // sends it as.
            Assert.Contains(
                $"RawBtn{NintendoPreviewMap.IndexOf(id, NintendoPreviewMap.RightPadClickRole(id))}",
                byTarget.Keys);
        }

        private static DeviceObjectItem Btn(int i) => new() { InputIndex = i, ObjectType = DeviceObjectTypeFlags.PushButton };
        private static DeviceObjectItem Ax(int i) => new() { InputIndex = i, ObjectType = DeviceObjectTypeFlags.AbsoluteAxis };
        private static DeviceObjectItem Hat() => new() { InputIndex = 0, ObjectType = DeviceObjectTypeFlags.PointOfViewController };

        /// <summary>A physical Valve pad as SDL presents it: six axes, a
        /// hat, buttons through the last SDL position it sends, two
        /// one-finger pads, gyro and accel.</summary>
        private static UserDevice ValveSource(int buttons) => new UserDevice
        {
            CapType = (int)InputDeviceType.Gamepad,
            DeviceObjects = Enumerable.Range(0, 6).Select(Ax)
                .Concat(new[] { Hat() })
                .Concat(Enumerable.Range(0, buttons).Select(Btn)).ToArray(),
            HasTouchpad = true,
            CapTouchpadCount = 2,
            HasGyro = true,
            HasAccel = true,
        };

        private static string Raw(PadSetting ps, string id, string role)
        {
            int i = NintendoPreviewMap.IndexOf(id, role);
            Assert.True(i >= 0, $"{id} has no {role}");
            return ps.GetRawMapping($"RawBtn{i}");
        }

        /// <summary>A Steam Controller 2026 on its own profile routes 1:1:
        /// every wire button, both pads, all six axes, the hat onto the
        /// four D-pad buttons, and the IMU. SDL's Triton driver sends QAM at
        /// 11, the rear buttons RP1 LP1 RP2 LP2 at 12-15 (R4 L4 R5 L5), the
        /// pads as touchpad 0 / 1, and the pad clicks as the gamepad buttons
        /// its mapping advertises: touchpad:b17 (left) at position 16 and
        /// misc2:b16 (right) at 17 (SDL_hidapi_steam_triton.c 48-54,
        /// 584-585, SDL_gamepad.c 1269).</summary>
        [Fact]
        public void Automap_SteamController2026_RoutesEverythingOneToOne()
        {
            const string id = "steam-controller-2";
            var ps = SettingsManager.CreateDefaultPadSetting(ValveSource(18), VirtualControllerType.Extended, id);

            Assert.Equal("Button 0", Raw(ps, id, "ButtonA"));
            Assert.Equal("Button 10", Raw(ps, id, "ButtonGuide"));
            Assert.Equal("Button 11", Raw(ps, id, "ButtonQuickAccess"));
            Assert.Equal("Button 12", Raw(ps, id, "Paddle1"));   // R4
            Assert.Equal("Button 13", Raw(ps, id, "Paddle2"));   // L4
            Assert.Equal("Button 14", Raw(ps, id, "Paddle3"));   // R5
            Assert.Equal("Button 15", Raw(ps, id, "Paddle4"));   // L5
            Assert.Equal("Button 16", Raw(ps, id, "LeftTouchpadClick"));
            Assert.Equal("Button 17", Raw(ps, id, "RightTouchpadClick"));
            Assert.Equal("POV 0 Up", Raw(ps, id, "DPadUp"));
            Assert.Equal("POV 0 Right", Raw(ps, id, "DPadRight"));

            for (int i = 0; i < NintendoPreviewMap.ButtonCount(id); i++)
                Assert.False(string.IsNullOrEmpty(ps.GetRawMapping($"RawBtn{i}")), $"RawBtn{i} unbound");
            for (int a = 0; a < 6; a++)
                Assert.Equal($"Axis {a}", ps.GetRawMapping($"RawAxis{a}"));

            Assert.Equal("Touchpad 0 Finger 0 X", ps.TouchpadX1);
            Assert.Equal("Touchpad 0 Finger 0 Y", ps.TouchpadY1);
            Assert.Equal("Touchpad 0 Finger 0 Down", ps.TouchpadContact1);
            Assert.Equal("Touchpad 1 Finger 0 X", ps.TouchpadX2);
            Assert.Equal("Touchpad 1 Finger 0 Y", ps.TouchpadY2);
            Assert.Equal("Touchpad 1 Finger 0 Down", ps.TouchpadContact2);
            Assert.True(string.IsNullOrEmpty(ps.TouchpadClick));

            Assert.Equal("Motion Gyro", ps.MotionGyro);
            Assert.Equal("Motion Accel", ps.MotionAccel);
        }

        /// <summary>A 2015 Steam Controller (SDL sends its grips as RP1 /
        /// LP1 at 12 / 13: SDL_gamepad.c 1263, and advertises no touchpad
        /// or misc2 button, so its clicks ride the wrapper's pressure
        /// descriptors) lands on the 2015 wire's grips, and every slot on
        /// that wire is bound.</summary>
        [Fact]
        public void Automap_SteamController2015_BindsGripsAndPads()
        {
            const string id = "steam-controller-composite";
            var ps = SettingsManager.CreateDefaultPadSetting(ValveSource(14), VirtualControllerType.Extended, id);

            Assert.Equal("Button 12", Raw(ps, id, "RightGrip"));
            Assert.Equal("Button 13", Raw(ps, id, "LeftGrip"));
            Assert.Equal("Touchpad 0 Click", Raw(ps, id, "LeftTouchpadClick"));
            // This pad's right click is the right STICK button, which is
            // what SDL sends it as, so the automap fills that name.
            Assert.Equal("Touchpad 1 Click", Raw(ps, id, "RightThumbButton"));
            for (int i = 0; i < NintendoPreviewMap.ButtonCount(id); i++)
                Assert.False(string.IsNullOrEmpty(ps.GetRawMapping($"RawBtn{i}")), $"RawBtn{i} unbound");
            Assert.Equal("Touchpad 1 Finger 0 X", ps.TouchpadX2);
        }

        /// <summary>A Deck's own controls land on the Deck wire, including
        /// the hat on the hat.</summary>
        [Fact]
        public void Automap_SteamDeck_BindsEverySlot()
        {
            const string id = "steam-deck-composite";
            var ps = SettingsManager.CreateDefaultPadSetting(ValveSource(18), VirtualControllerType.Extended, id);
            Assert.Equal("Button 11", Raw(ps, id, "ButtonQuickAccess"));
            Assert.Equal("Button 15", Raw(ps, id, "Paddle4"));
            Assert.Equal("Button 16", Raw(ps, id, "LeftTouchpadClick"));
            Assert.Equal("Button 17", Raw(ps, id, "RightTouchpadClick"));
            Assert.Equal("POV 0 Down", ps.GetRawMapping("RawPov0Down"));
            for (int i = 0; i < NintendoPreviewMap.ButtonCount(id); i++)
                Assert.False(string.IsNullOrEmpty(ps.GetRawMapping($"RawBtn{i}")), $"RawBtn{i} unbound");
        }

        /// <summary>A one-pad source (a DualSense) lands on the left pad
        /// only, and a pad-less source binds no pad at all.</summary>
        [Fact]
        public void Automap_OnePadSource_LandsOnTheLeftPadOnly()
        {
            var ds = ValveSource(12);
            ds.CapTouchpadCount = 1;
            var ps = SettingsManager.CreateDefaultPadSetting(ds, VirtualControllerType.Extended, "steam-controller-2");
            Assert.Equal("Touchpad 0 Finger 0 X", ps.TouchpadX1);
            Assert.True(string.IsNullOrEmpty(ps.TouchpadX2));
            Assert.True(string.IsNullOrEmpty(Raw(ps, "steam-controller-2", "RightTouchpadClick")));

            var xbox = ValveSource(11);
            xbox.HasTouchpad = false;
            xbox.CapTouchpadCount = 0;
            ps = SettingsManager.CreateDefaultPadSetting(xbox, VirtualControllerType.Extended, "steam-controller-2");
            Assert.True(string.IsNullOrEmpty(ps.TouchpadX1));
            Assert.True(string.IsNullOrEmpty(Raw(ps, "steam-controller-2", "LeftTouchpadClick")));
        }

        /// <summary>An Extended slot is not a motion family, but a Valve
        /// profile on it carries an IMU, so the backfill admits it by the
        /// profile flag and still skips a plain Extended slot.</summary>
        [Fact]
        public void MotionRows_BackfillValveExtendedSlots()
        {
            IReadOnlyList<(string DeviceGuid, bool HasGyro, bool HasAccel)> devs = new[] { ("{AAAA-BBBB}", true, true) };

            var plain = new MappingSet { Rows = new List<MappingRow>() };
            MappingSetMigrator.EnsureMotionRows(plain, (int)VirtualControllerType.Extended, devs);
            Assert.Empty(plain.Rows);

            var valve = new MappingSet { Rows = new List<MappingRow>() };
            MappingSetMigrator.EnsureMotionRows(valve, (int)VirtualControllerType.Extended, motionCapableProfile: true, devs);
            Assert.NotNull(valve.Rows.Find(r => r.Target == MappingSetMigrator.MotionGyroTarget));
            Assert.NotNull(valve.Rows.Find(r => r.Target == MappingSetMigrator.MotionAccelTarget));
        }
    }
}
