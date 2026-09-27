using System;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The WingMan Warrior's spin dial. The fork's serial driver reports it as
    /// ball 0's horizontal motion, under the name Linux's warrior.c gives the
    /// stick (hifihedgehog/SDL#33 Part 5), and PadForge offers it as
    /// "Mouse Motion X".
    /// </summary>
    public class SpinDialTests
    {
        [Theory]
        [InlineData("Logitech WingMan Warrior")]
        [InlineData("logitech wingman warrior")]
        [InlineData("  Logitech WingMan Warrior ")]
        public void TheWarrior_HasTheDial(string name)
        {
            Assert.True(SpinDialIdentity.HasSpinDial(name));
            Assert.True(new UserDevice { ProductName = name }.HasSpinDial);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Logitech CyberMan")]
        [InlineData("Logitech WingMan Extreme Digital 3D")]
        [InlineData("Logitech WingMan Warrior 2")]
        [InlineData("Gravis Stinger")]
        public void OtherDevices_HaveNoDial(string name)
        {
            Assert.False(SpinDialIdentity.HasSpinDial(name));
            Assert.False(new UserDevice { ProductName = name }.HasSpinDial);
        }

        /// <summary>The dial turns on one axis, so the picker offers its
        /// horizontal motion and nothing for a vertical motion that stays
        /// zero.</summary>
        [Fact]
        public void ThePicker_OffersTheDialAsMouseMotionXOnly()
        {
            var warrior = new UserDevice { ProductName = SpinDialIdentity.WingManWarrior };
            var choices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(warrior);
            Assert.Single(choices, c => c.Descriptor == "Mouse Motion X");
            Assert.DoesNotContain(choices, c => c.Descriptor == "Mouse Motion Y");
        }

        [Fact]
        public void AJoyCon2_KeepsBothMouseAxes()
        {
            var joyCon = new UserDevice { VendorId = 0x057E, ProdId = 0x2066, ProductName = "Nintendo Switch 2 Joy-Con (R)" };
            var choices = PadForge.Common.MappingDisplayResolver.BuildInputChoices(joyCon);
            Assert.Single(choices, c => c.Descriptor == "Mouse Motion X");
            Assert.Single(choices, c => c.Descriptor == "Mouse Motion Y");
        }

        /// <summary>The wrapper reads the dial only from a live Warrior whose
        /// DLL reports the ball. A DLL without the fork's serial driver
        /// reports none.</summary>
        [Theory]
        [InlineData("Logitech WingMan Warrior", true, 1, true)]
        [InlineData("Logitech WingMan Warrior", true, 0, false)]
        [InlineData("Logitech WingMan Warrior", false, 1, false)]
        [InlineData("Gravis Stinger", true, 1, false)]
        public void TheWrapper_ReadsTheDialOnlyFromTheWarriorsBall(string name, bool joystick, int balls, bool expected)
        {
            using var wrapper = new SdlDeviceWrapper();
            void Set(string property, object value) => typeof(SdlDeviceWrapper).GetProperty(property)!.SetValue(wrapper, value);
            try
            {
                Set("Name", name);
                Set("Joystick", joystick ? new IntPtr(1) : IntPtr.Zero);
                // Only the managed capability resolver receives this synthetic handle.
                wrapper.UpdateSpinDialCapability(balls);
                Assert.Equal(expected, wrapper.HasSpinDial);
            }
            finally { Set("Joystick", IntPtr.Zero); }
        }
    }
}
