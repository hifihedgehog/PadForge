using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Models2D;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Who owns the gamepad-shaped preview properties.
    ///
    /// <para>Two writers set the same properties: the raw bridge, which
    /// lights a picture of the PHYSICAL pad by wire index, and the gamepad
    /// path, which lights the virtual controller's output. Exactly one may
    /// own a slot, and when both wrote, the later tick won and the earlier
    /// writer's work vanished within a frame.</para>
    ///
    /// <para>That is what happened to every Valve profile. The families live
    /// under the EXTENDED output type, the raw bridge claimed them, and the
    /// gamepad path skipped only Nintendo, so every press lit for one tick
    /// and went out. The 2015 pad showed it plainest: its d-pad is the left
    /// trackpad, so pressing a direction lit nothing the player could see
    /// (owner report 2026-08-29).</para></summary>
    public class ValvePreviewBridgeTests
    {
        private static PadViewModel Slot(string profileId,
            VirtualControllerType type = VirtualControllerType.Extended)
            => new(0) { OutputType = type, ProfileId = profileId };

        private static RawHidState Rest(string profileId)
        {
            var raw = RawHidState.Create(
                NintendoPreviewMap.ButtonCount(profileId), 32,
                NintendoPreviewMap.DPadIsHat(profileId) ? 1 : 0);
            if (raw.Povs is { Length: > 0 }) raw.Povs[0] = -1;
            // Valve triggers rest at short.MinValue on the raw surface.
            raw.Axes[2] = raw.Axes[5] = short.MinValue;
            return raw;
        }

        /// <summary>A gamepad tick with nothing pressed, which is what the
        /// engine sends thirty times a second whether or not the player is
        /// touching anything.</summary>
        private static void IdleGamepadTick(PadViewModel vm)
            => vm.UpdateFromEngineState(new Gamepad(), new Vibration());

        /// <summary>The 2015's d-pad IS its left trackpad, reported as a hat.
        /// The wedge cut out of that pad lights off DPadUp, so a stomped
        /// bridge left the player pressing a direction with nothing on
        /// screen to say so.</summary>
        [Fact]
        public void The2015HatSurvivesAGamepadTick()
        {
            var vm = Slot("steam-controller");
            var raw = Rest("steam-controller");
            raw.Povs[0] = 0;                       // hat UP
            vm.UpdateFromRawHidState(raw);
            Assert.True(vm.DPadUp);

            IdleGamepadTick(vm);
            Assert.True(vm.DPadUp);
        }

        /// <summary>The same for a button, on every Valve family, so this is
        /// pinned as a property of the wire rather than of one control.</summary>
        [Theory]
        [InlineData("steam-controller")]
        [InlineData("steam-deck")]
        [InlineData("steam-controller-2")]
        public void AValvePressSurvivesAGamepadTick(string profileId)
        {
            var vm = Slot(profileId);
            var raw = Rest(profileId);
            int a = NintendoPreviewMap.IndexOf(profileId, "ButtonA");
            Assert.True(a >= 0);
            raw.Buttons[a / 32] |= 1u << (a % 32);
            vm.UpdateFromRawHidState(raw);
            Assert.True(vm.ButtonA);

            IdleGamepadTick(vm);
            Assert.True(vm.ButtonA);
        }

        /// <summary>The 2015's right pad click is one bit doing two jobs, and
        /// both of them have to outlive the tick.</summary>
        [Fact]
        public void The2015RightPadClickLightsBothItsRolesAndKeepsThem()
        {
            var vm = Slot("steam-controller");
            var raw = Rest("steam-controller");
            int i = NintendoPreviewMap.IndexOf("steam-controller", "RightTouchpadClick");
            raw.Buttons[i / 32] |= 1u << (i % 32);
            vm.UpdateFromRawHidState(raw);
            Assert.True(vm.RightTouchpadClick);
            Assert.True(vm.RightThumbButton);

            IdleGamepadTick(vm);
            Assert.True(vm.RightTouchpadClick);
            Assert.True(vm.RightThumbButton);
        }

        /// <summary>Releasing still releases. A gate that simply froze the
        /// preview would pass every case above and be worse than the
        /// bug.</summary>
        [Fact]
        public void AValveReleaseStillClearsThePreview()
        {
            var vm = Slot("steam-controller");
            var raw = Rest("steam-controller");
            raw.Povs[0] = 0;
            vm.UpdateFromRawHidState(raw);
            Assert.True(vm.DPadUp);

            vm.UpdateFromRawHidState(Rest("steam-controller"));
            Assert.False(vm.DPadUp);
        }

        /// <summary>An Extended slot on an ordinary profile keeps the gamepad
        /// path. The raw bridge only knows how to light a pad whose wire it
        /// has a table for, so claiming every Extended slot would blank the
        /// preview for all of them.</summary>
        [Fact]
        public void AnOrdinaryExtendedSlotStillTakesTheGamepadPath()
        {
            var vm = Slot("xbox360");
            var gp = new Gamepad();
            gp.Buttons = Gamepad.A;
            vm.UpdateFromEngineState(gp, new Vibration());
            Assert.True(vm.ButtonA);
        }
    }
}
