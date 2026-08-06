using System;
using System.Linq;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The DualSense mic-mute output and the Edge paddle / Fn outputs: the
    /// wire bits per SDL_hidapi_ps5.c's parser (mute 0x04, LEFT_FUNCTION
    /// 0x10, RIGHT_FUNCTION 0x20, LEFT_PADDLE 0x40, RIGHT_PADDLE 0x80 in
    /// the third buttons byte), the profile-gated mapping rows, and the
    /// physical-to-virtual automap.
    /// </summary>
    public class Ds5ExtraButtonTests
    {
        private static byte PackButtons3(Gamepad gp)
        {
            var tp = default(TouchpadState);
            var motion = default(MotionSnapshot);
            var dest = new byte[63];
            SonyReportPackers.ForProfile("dualsense-edge-composite")(
                in gp, in tp, in motion, 100, false, 1, dest);
            return dest[9];
        }

        [Fact]
        public void MicMute_PacksBit04()
        {
            var gp = default(Gamepad);
            gp.MicMute = true;
            Assert.Equal(0x04, PackButtons3(gp) & 0x04);
            Assert.Equal(0, PackButtons3(default) & 0x04);
        }

        /// <summary>Each Edge extra lands on its own wire bit, per the SDL
        /// parser's assignments, with no crosstalk.</summary>
        [Theory]
        [InlineData("LeftFunction", 0x10)]
        [InlineData("RightFunction", 0x20)]
        [InlineData("LeftPaddle", 0x40)]
        [InlineData("RightPaddle", 0x80)]
        public void EdgeExtras_PackTheirOwnBit(string field, int bit)
        {
            var gp = default(Gamepad);
            switch (field)
            {
                case "LeftFunction": gp.LeftFunction = true; break;
                case "RightFunction": gp.RightFunction = true; break;
                case "LeftPaddle": gp.LeftPaddle = true; break;
                case "RightPaddle": gp.RightPaddle = true; break;
            }
            byte b = PackButtons3(gp);
            Assert.Equal(bit, b & 0xF4);
        }

        /// <summary>Row gating follows the wire: the DualShock 4 has no mic
        /// button, plain DualSense has no paddles, the Edge has all five.</summary>
        [Theory]
        [InlineData("dualshock-4-v2-composite", false, false)]
        [InlineData("dualsense-composite", true, false)]
        [InlineData("dualsense", true, false)]
        [InlineData("dualsense-edge-composite", true, true)]
        public void MappingRows_GateOnTheProfileFamily(
            string profileId, bool expectMute, bool expectEdge)
        {
            var vm = new PadViewModel(0)
            {
                OutputType = VirtualControllerType.PlayStation,
                ProfileId = profileId,
            };
            var targets = vm.Mappings.Select(m => m.TargetSettingName).ToList();
            Assert.Equal(expectMute, targets.Contains("ButtonMute"));
            Assert.Equal(expectEdge, targets.Contains("LeftPaddle"));
            Assert.Equal(expectEdge, targets.Contains("RightPaddle"));
            Assert.Equal(expectEdge, targets.Contains("LeftFunction"));
            Assert.Equal(expectEdge, targets.Contains("RightFunction"));
        }

        private static DeviceObjectItem Obj(int idx) => new()
        {
            InputIndex = idx,
            ObjectType = DeviceObjectTypeFlags.PushButton,
        };

        /// <summary>A physical DualSense (buttons through Misc1 at 11)
        /// automapped onto a virtual DualSense binds its mic button; a
        /// physical Edge (through position 15) binds all five, same-role.</summary>
        [Fact]
        public void Automap_BindsMicAndEdgeExtrasSameRole()
        {
            var ds5 = new UserDevice
            {
                CapType = (int)InputDeviceType.Gamepad,
                DeviceObjects = Enumerable.Range(0, 12).Select(Obj).ToArray(),
            };
            var ps = SettingsManager.CreateDefaultPadSetting(
                ds5, VirtualControllerType.PlayStation, "dualsense-composite");
            Assert.Equal("Button 11", ps.ButtonMute);
            Assert.True(string.IsNullOrEmpty(ps.LeftPaddle));

            var edge = new UserDevice
            {
                CapType = (int)InputDeviceType.Gamepad,
                DeviceObjects = Enumerable.Range(0, 16).Select(Obj).ToArray(),
            };
            ps = SettingsManager.CreateDefaultPadSetting(
                edge, VirtualControllerType.PlayStation, "dualsense-edge-composite");
            Assert.Equal("Button 11", ps.ButtonMute);
            Assert.Equal("Button 12", ps.RightPaddle);
            Assert.Equal("Button 13", ps.LeftPaddle);
            Assert.Equal("Button 14", ps.RightFunction);
            Assert.Equal("Button 15", ps.LeftFunction);

            // And a DualShock 4 target never binds a mute it cannot carry.
            ps = SettingsManager.CreateDefaultPadSetting(
                ds5, VirtualControllerType.PlayStation, "dualshock-4-v2-composite");
            Assert.True(string.IsNullOrEmpty(ps.ButtonMute));
        }

        /// <summary>The five fields persist through the PadSetting mirror
        /// set (hash, HasAnyMapping, descriptor list).</summary>
        [Fact]
        public void PadSetting_MirrorsCarryTheNewFields()
        {
            var ps = new PadSetting { ButtonMute = "Button 11", LeftFunction = "Button 15" };
            Assert.True(ps.HasAnyMapping);
            var descriptors = ps.GetAllMappingDescriptors();
            Assert.Contains("Button 11", descriptors);
            Assert.Contains("Button 15", descriptors);
            ps.UpdateChecksum();
            var blank = new PadSetting();
            blank.UpdateChecksum();
            Assert.NotEqual(blank.PadSettingChecksum, ps.PadSettingChecksum);
        }
    }
}
