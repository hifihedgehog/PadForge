using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>An "(Any Device)" shift activator reads its input as the
    /// device it reads, the rule a mapping row follows
    /// (SourceCoercion.EffectiveDeviceGuid). The touchpad and mouse gesture
    /// lookups are keyed by a concrete device, and the activator passed its
    /// own empty guid, so a layer set to engage on a gesture with "(Any
    /// Device)" never engaged.</summary>
    [Collection("SettingsManagerStatics")]
    public sealed class AnyDeviceGestureActivatorTests : IDisposable
    {
        private const int Slot = 13;
        private const string Pad = "d5000000-0000-0000-0000-0000000000d5";

        private readonly DeviceCollection _savedDevices = SettingsManager.UserDevices;
        private readonly Func<int, string, int, string, bool> _savedFired = SourceCoercion.TouchpadGestureFiredProvider;
        private readonly Func<int, string, int, string, float> _savedAxis = SourceCoercion.TouchpadGestureAxisProvider;
        private readonly Func<int, string, string, bool> _savedMouse = SourceCoercion.MouseGestureFiredProvider;
        private readonly Func<int, string, int, int, bool> _savedMenu = SourceCoercion.MenuItemFiredProvider;

        public AnyDeviceGestureActivatorTests()
        {
            InputManager.ClearAllShiftRuntime();
            var devices = new DeviceCollection();
            devices.Items.Add(new UserDevice
            {
                InstanceGuid = Guid.Parse(Pad), ProductGuid = Guid.Parse(Pad),
                InstanceName = "Touchpad pad", ProductName = "Touchpad pad",
                CapType = InputDeviceType.Gamepad, IsOnline = true, IsEnabled = true,
                InputState = new CustomInputState(),
            });
            SettingsManager.UserDevices = devices;
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SourceCoercion.TouchpadGestureFiredProvider = _savedFired;
            SourceCoercion.TouchpadGestureAxisProvider = _savedAxis;
            SourceCoercion.MouseGestureFiredProvider = _savedMouse;
            SourceCoercion.MenuItemFiredProvider = _savedMenu;
            InputManager.ClearAllShiftRuntime();
        }

        private bool _tapFired;

        /// <summary>The gesture fires on the pad, keyed the way the live
        /// lookup keys it: this slot, the pad's own guid, touchpad 0.</summary>
        private void StubTwoFingerTap()
            => SourceCoercion.TouchpadGestureFiredProvider = (slot, guid, padIdx, name) =>
                _tapFired && slot == Slot && guid == Pad && padIdx == 0 && name == "TwoFingerTap";

        private static MappingSet LayerOn(ShiftActivator act)
        {
            var ms = new MappingSet();
            ms.ShiftActivators.Add(act);
            return ms;
        }

        [Theory]
        [InlineData("")]
        [InlineData(Pad)]
        public void AGestureActivator_EngagesOnTheDeviceItReads(string activatorDevice)
        {
            StubTwoFingerTap();
            var ms = LayerOn(new ShiftActivator
            {
                DeviceGuid = activatorDevice, Descriptor = "Touchpad 0 TwoFingerTap", Mode = "Hold",
                Kind = "Button", LayerMask = "View", LayerName = "View", DelayMs = 0,
            });
            var state = new CustomInputState();

            _tapFired = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
            _tapFired = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
        }

        /// <summary>The Axis kind reads a continuous gesture, Pinch, through
        /// the axis lookup with the same key.</summary>
        [Fact]
        public void AnAnyDevicePinchActivator_EngagesPastItsThreshold()
        {
            float pinch = 0f;
            SourceCoercion.TouchpadGestureAxisProvider = (slot, guid, padIdx, name) =>
                slot == Slot && guid == Pad && padIdx == 0 && name == "PinchAxis" ? pinch : 0f;
            var ms = LayerOn(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Touchpad 0 PinchAxis", Mode = "Hold",
                Kind = "Axis", AxisThreshold = 0.5, LayerMask = "View", LayerName = "View", DelayMs = 0,
            });
            var state = new CustomInputState();

            pinch = 0.8f;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
            pinch = 0.2f;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
        }

        [Fact]
        public void AnAnyDeviceMouseGestureActivator_Engages()
        {
            bool fired = false;
            SourceCoercion.MouseGestureFiredProvider = (slot, guid, name) =>
                fired && slot == Slot && guid == Pad && name == "Left";
            var ms = LayerOn(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Mouse Gesture Left", Mode = "Hold",
                Kind = "Button", LayerMask = "View", LayerName = "View", DelayMs = 0,
            });
            var state = new CustomInputState();

            fired = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
            fired = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
        }

        /// <summary>A menu cell is the slot's. An "(Any Device)" activator
        /// reads it under the empty guid, which the menu runtime answers for
        /// whichever device drives the menu, one scoped to a device that does
        /// not answer the wildcard included. Read as the picked device, such a
        /// cell never engaged the layer.</summary>
        [Fact]
        public void AnAnyDeviceMenuCellActivator_ReadsTheCellForAnyDevice()
        {
            bool fired = false;
            SourceCoercion.MenuItemFiredProvider = (slot, guid, menu, item) =>
                fired && slot == Slot && guid == "" && menu == 2 && item == 0;
            var ms = LayerOn(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Menu 2 Item 0", Mode = "Hold",
                Kind = "Button", LayerMask = "View", LayerName = "View", DelayMs = 0,
            });
            var state = new CustomInputState();

            fired = true;
            Assert.Equal("View", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
            fired = false;
            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
        }

        /// <summary>An "(Any Device)" Cycle's Previous button on a gesture
        /// steps the queue back, read as the device it is read from
        /// (PickWildcardPrevious).</summary>
        [Fact]
        public void AnAnyDeviceCyclesPreviousGesture_StepsBack()
        {
            StubTwoFingerTap();
            var ms = LayerOn(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Button 28", Mode = "Cycle", Kind = "Button",
                CyclePrevDescriptor = "Touchpad 0 TwoFingerTap", CycleLayers = "View|Other",
                CycleWrap = true, CycleIncludeBase = true, DelayMs = 0,
            });
            var state = new CustomInputState();

            // Back from Base wraps to the last layer.
            _tapFired = true;
            Assert.Equal("Other", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
            _tapFired = false;
            Assert.Equal("Other", InputManager.ResolveActiveLayerMask(Slot, ms, state, Pad));
        }
    }
}
