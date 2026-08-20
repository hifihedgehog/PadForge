using PadForge.Common.Input;
using PadForge.Engine;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A driver that a virtual controller depends on must not be removable
    /// while that controller exists. HidHide already refused to uninstall
    /// under a hidden device and MIDI Services under a MIDI slot, but SteamVR
    /// had no such guard at all: the runtime every VR slot needs could be
    /// deleted with those slots sitting in the rail.
    ///
    /// <para>These pin the predicate both gates now read. It answers from the
    /// PERSISTED topology (SlotCreated plus the per-type order lists), not
    /// from engine or view-model state, so it still refuses with the engine
    /// stopped, which is exactly when someone is tidying up drivers.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class UninstallGuardTests : System.IDisposable
    {
        private readonly bool[] _createdBefore;

        public UninstallGuardTests()
        {
            _createdBefore = (bool[])SettingsManager.SlotCreated.Clone();
            System.Array.Clear(SettingsManager.SlotCreated);
            foreach (var t in VirtualControllerGroups.InOrder)
                SettingsManager.SlotOrders.GetOrderFor(t).Clear();
        }

        public void Dispose()
        {
            foreach (var t in VirtualControllerGroups.InOrder)
                SettingsManager.SlotOrders.GetOrderFor(t).Clear();
            System.Array.Copy(_createdBefore, SettingsManager.SlotCreated, _createdBefore.Length);
        }

        [Fact]
        public void NoSlots_LeavesBothDriversRemovable()
        {
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Midi));
        }

        /// <summary>THE GAP: a VR slot must hold SteamVR in place.</summary>
        [Fact]
        public void AVrSlot_HoldsSteamVr()
        {
            SettingsManager.SlotOrders.Add(3, VirtualControllerType.Vr);
            SettingsManager.SlotCreated[3] = true;
            Assert.True(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
        }

        [Fact]
        public void AMidiSlot_HoldsMidiServices()
        {
            SettingsManager.SlotOrders.Add(2, VirtualControllerType.Midi);
            SettingsManager.SlotCreated[2] = true;
            Assert.True(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Midi));
        }

        /// <summary>Each gate answers for its own type only, so a MIDI slot
        /// must not keep SteamVR installed and vice versa.</summary>
        [Fact]
        public void TypesDoNotCrossGate()
        {
            SettingsManager.SlotOrders.Add(1, VirtualControllerType.Midi);
            SettingsManager.SlotCreated[1] = true;
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
        }

        /// <summary>An Xbox slot holds neither driver.</summary>
        [Fact]
        public void AnUnrelatedSlot_HoldsNeither()
        {
            SettingsManager.SlotOrders.Add(0, VirtualControllerType.Xbox);
            SettingsManager.SlotCreated[0] = true;
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Midi));
        }

        /// <summary>Ordered but never created is not created: a stale order
        /// entry must not keep a driver hostage.</summary>
        [Fact]
        public void OrderedButNotCreated_DoesNotGate()
        {
            SettingsManager.SlotOrders.Add(4, VirtualControllerType.Vr);
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
        }

        /// <summary>Deleting the last slot of a type releases its driver.</summary>
        [Fact]
        public void RemovingTheLastSlot_ReleasesTheDriver()
        {
            SettingsManager.SlotOrders.Add(5, VirtualControllerType.Vr);
            SettingsManager.SlotCreated[5] = true;
            Assert.True(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));

            SettingsManager.SlotCreated[5] = false;
            Assert.False(SettingsManager.HasCreatedSlotOfType(VirtualControllerType.Vr));
        }
    }
}
