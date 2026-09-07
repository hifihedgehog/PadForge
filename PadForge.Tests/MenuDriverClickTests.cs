using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class MenuDriverClickTests
    {
        [Theory]
        [InlineData(MenuFireType.Click, false)]
        [InlineData(MenuFireType.Click, true)]
        [InlineData(MenuFireType.ClickRelease, false)]
        [InlineData(MenuFireType.ClickRelease, true)]
        public void ASecondControllerCanClickTheRestingCenter(MenuFireType fire, bool customHost)
        {
            using var f = new MenuAuditFixture(fire);
            if (customHost)
            {
                f.Menu.HostDescriptor = "Custom";
                f.Menu.CustomXDescriptor = "Gamepad RightStickX";
                f.Menu.CustomYDescriptor = "Gamepad RightStickY";
            }
            var second = f.AddDevice();
            f.State.Buttons[0] = true;
            f.Frame();
            f.Frame();
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.False(f.Fired(0));

            second.InputState.Buttons[1] = true;
            f.Frame();
            f.Frame();
            Assert.Equal(second.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Equal(0, f.Manager.ActiveMenuOverlay.HoveredIndex);
            Assert.Equal(fire == MenuFireType.Click, f.Fired(0));
            second.InputState.Buttons[1] = false;
            f.Frame();
            Assert.Equal(fire == MenuFireType.ClickRelease, f.Fired(0));

            // The first controller can still take over by moving.
            f.Deflect();
            f.Frame();
            f.Frame();
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Equal(2, f.Manager.ActiveMenuOverlay.HoveredIndex);
        }

        [Fact]
        public void ACenterClickDoesNotArmTouchRelease()
        {
            using var f = new MenuAuditFixture();
            var second = f.AddDevice();
            f.State.Buttons[0] = true;
            f.Frame();
            f.Frame();
            second.InputState.Buttons[1] = true;
            f.Frame();
            f.Frame();
            Assert.Equal(second.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.False(f.Manager.MenuContexts[(0, second.InstanceGuid, 1)].State.PhysicalEngaged);
            second.InputState.Buttons[1] = false;
            f.Frame();
            Assert.False(f.Fired(0));

            MenuAuditFixture.Deflect(second.InputState);
            f.Frame();
            Assert.True(f.Manager.MenuContexts[(0, second.InstanceGuid, 1)].State.PhysicalEngaged);
            MenuAuditFixture.Center(second.InputState);
            f.Frame();
            Assert.True(f.Fired(2));
        }

        [Theory]
        [InlineData("Custom", "", "", MenuKind.Radial, true)]
        [InlineData("Custom", "Gamepad RightStickX", "", MenuKind.Radial, true)]
        [InlineData("Custom", "", "Gamepad RightStickY", MenuKind.Radial, true)]
        [InlineData("Touchpad 0", "", "", MenuKind.Radial, true)]
        [InlineData("Gamepad DPad", "", "", MenuKind.Radial, true)]
        [InlineData("Gamepad Diamond", "", "", MenuKind.Radial, true)]
        [InlineData("Gamepad RightStick", "", "", MenuKind.Radial, false)]
        [InlineData("Gamepad RightStick", "", "", MenuKind.Grid, false)]
        public void AClickWithoutARestingCenterKeepsTheCurrentDriver(
            string host, string customX, string customY, MenuKind kind, bool hasCenter)
        {
            using var f = new MenuAuditFixture(MenuFireType.Click);
            var second = f.AddDevice();
            f.Menu.HostDescriptor = host;
            f.Menu.CustomXDescriptor = customX;
            f.Menu.CustomYDescriptor = customY;
            f.Menu.Kind = kind;
            f.Menu.HasCenter = hasCenter;
            f.Menu.ClickDescriptor = "Button 15";
            f.State.Touchpads = new[] { new TouchpadInputState(2) };
            second.InputState.Touchpads = new[] { new TouchpadInputState(2) };
            // No direction button opens the layer or supplies this click.
            InputManager.ApplyMacroLayerSwitch(0, "L1");
            f.Frame();
            f.Frame();
            Assert.Equal(f.Device.InstanceGuid, Driver(f));
            Assert.Equal(-1, f.Manager.ActiveMenuOverlay.HoveredIndex);

            second.InputState.Buttons[15] = true;
            f.Frame();
            f.Frame();
            var context = f.Manager.MenuContexts[(0, second.InstanceGuid, 1)];
            Assert.True(context.State.Clicked, "the assigned click must reach the evaluator");
            Assert.False(context.State.PhysicalEngaged, "the click must not be a steering input");
            Assert.Equal(-1, context.State.HoveredIndex);
            Assert.Equal(f.Device.InstanceGuid, Driver(f));
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Empty(f.Manager._desiredLatchedKeys);
        }

        [Fact]
        public void AHeldCenterClickReclaimsTheDriverAndReleasesAfterAnotherTakeover()
        {
            using var f = new MenuAuditFixture(MenuFireType.ClickRelease);
            var second = f.AddDevice();
            f.State.Buttons[0] = true;
            f.Frame();
            f.Frame();
            second.InputState.Buttons[1] = true;
            f.Frame();
            f.Frame();
            var pending = f.Manager.MenuContexts[(0, second.InstanceGuid, 1)];
            Assert.Equal(second.InstanceGuid, Driver(f));
            Assert.True(pending.State.Clicked);
            Assert.Equal(0, pending.State.HoveredIndex);
            Assert.Equal(-1, pending.State.PulsedIndex);

            f.Deflect();
            using (InputManager.EnterMenuPublication())
            {
                f.Manager.UpdateMenuContexts(f.Device, f.State);
                Assert.Equal(f.Device.InstanceGuid, Driver(f));
                // The held click remains a level-based claim after takeover.
                f.Manager.UpdateMenuContexts(second, second.InputState);
                Assert.Equal(second.InstanceGuid, Driver(f));
                Assert.Equal(-1, pending.State.PulsedIndex);

                f.Manager.UpdateMenuContexts(f.Device, f.State);
                Assert.Equal(f.Device.InstanceGuid, Driver(f));
                second.InputState.Buttons[1] = false;
                f.Manager.UpdateMenuContexts(second, second.InputState);
                Assert.Equal(f.Device.InstanceGuid, Driver(f));
                Assert.Equal(0, pending.State.PulsedIndex);
                Assert.False(pending.State.Clicked);
            }
            f.Frame();
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Equal(2, f.Manager.ActiveMenuOverlay.HoveredIndex);
            Assert.True(f.Fired(0));
            Assert.Contains((ushort)0x41, f.Manager._desiredLatchedKeys);
        }

        [Fact]
        public void APendingTouchClickStillReleasesAfterAnotherDeviceTakesOver()
        {
            using var f = new MenuAuditFixture(MenuFireType.ClickRelease);
            var second = f.AddDevice();
            f.Menu.HostDescriptor = "Touchpad 0";
            f.State.Touchpads = new[] { new TouchpadInputState(2) };
            second.InputState.Touchpads = new[] { new TouchpadInputState(2) };
            var firstPad = f.State.Touchpads[0];
            var secondPad = second.InputState.Touchpads[0];
            f.State.Buttons[0] = true;
            f.Frame();
            f.Frame();

            secondPad.FingerDown[0] = true;
            secondPad.FingerX[0] = 1f;
            secondPad.FingerY[0] = 0.5f;
            second.InputState.Buttons[1] = true;
            f.Frame();
            f.Frame();
            Assert.Equal(second.InstanceGuid, Driver(f));
            Assert.Equal(2, f.Manager.ActiveMenuOverlay.HoveredIndex);
            secondPad.FingerDown[0] = false;
            f.Frame();
            var pending = f.Manager.MenuContexts[(0, second.InstanceGuid, 1)];
            Assert.True(pending.State.Clicked);
            Assert.False(pending.State.PhysicalEngaged);
            Assert.Equal(2, pending.State.HoveredIndex);
            Assert.Equal(-1, pending.State.PulsedIndex);

            firstPad.FingerDown[0] = true;
            firstPad.FingerX[0] = 0.5f;
            firstPad.FingerY[0] = 0f;
            f.Frame();
            f.Frame();
            Assert.Equal(f.Device.InstanceGuid, Driver(f));
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Equal(1, f.Manager.ActiveMenuOverlay.HoveredIndex);
            Assert.Equal(2, pending.State.HoveredIndex);

            second.InputState.Buttons[1] = false;
            f.Frame();
            Assert.Equal(2, pending.State.PulsedIndex);
            Assert.True(f.Fired(2));
            Assert.Contains((ushort)0x43, f.Manager._desiredLatchedKeys);
            Assert.Equal(f.Device.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
        }

        private static Guid Driver(MenuAuditFixture fixture)
        {
            var record = fixture.Drivers[(0, 1)];
            Assert.NotNull(record);
            return (Guid)record.GetType().GetField("Device").GetValue(record);
        }
    }
}
