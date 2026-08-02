using System;
using System.Collections.Generic;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-26 round twelve. Round eleven shipped five
    /// product fixes and only rewired an existing test file, so most of
    /// it went out UNLOCKED, against the standing rule that a fix is not
    /// done until a test can fail without it. These lock the ones that
    /// are reachable from a test, and the round-twelve report states
    /// plainly which are not.
    ///
    /// <para>The guard under test here protects a DATA-LOSS class:
    /// PushUiExtraSourcesIntoSlotMappingSets does far more than its name
    /// suggests. It creates rows, destructively clears and rebuilds each
    /// row's Sources from the grid, and stamps derived state. That is
    /// correct when the grid is the source of truth, and catastrophic
    /// when it is not: across a device assignment and every output-type
    /// switch, MappingsViewLoaded is false precisely because the
    /// MappingSet holds freshly auto-mapped rows the grid has never
    /// seen. Pushing there rebuilds them from an empty grid and wipes
    /// them. Harmless while the only caller was the autosave, which
    /// never runs inside those windows. Round ten's adoption drain,
    /// which fires on a device connect, does.</para></summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly26RoundTwelveTests : IDisposable
    {
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedMappingSets;
        private readonly bool[] _savedCreated;

        public AuditJuly26RoundTwelveTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedMappingSets = SettingsManager.SlotMappingSets;
            _savedCreated = (bool[])SettingsManager.SlotCreated.Clone();
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedMappingSets;
            Array.Copy(_savedCreated, SettingsManager.SlotCreated, _savedCreated.Length);
        }

        /// <summary>A slot whose MappingSet carries one authored row that
        /// the grid has NOT been hydrated from.</summary>
        private static (MainViewModel vm, SettingsService ss, MappingSet ms) Arrange()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            // The slot must exist: since the 2026-08-02 ghost-mapping fix
            // the push skips uncreated slots outright, and this class is
            // about the hydration gate, not that one.
            Array.Clear(SettingsManager.SlotCreated, 0, SettingsManager.SlotCreated.Length);
            SettingsManager.SlotCreated[0] = true;

            var ms = new MappingSet
            {
                Rows = new List<MappingRow>
                {
                    new MappingRow
                    {
                        Target = "ButtonA",
                        LayerMask = "Base",
                        Sources = new List<MappingSource>
                        {
                            new MappingSource { Descriptor = "Button 3" },
                        },
                    },
                },
            };
            SettingsManager.SlotMappingSets[0] = ms;

            var vm = new MainViewModel();
            var ss = new SettingsService(vm);
            return (vm, ss, ms);
        }

        /// <summary>THE GUARD. With the grid un-hydrated, the push must
        /// leave the authored row alone. Without the gate it rebuilds
        /// that row's Sources from a grid that knows nothing, and the
        /// authored source is destroyed, then persisted by the
        /// MarkDirty that follows in the drain.</summary>
        [Fact]
        public void Push_LeavesAuthoredRowsAlone_WhenTheGridIsNotHydrated()
        {
            var (vm, ss, ms) = Arrange();
            vm.Pads[0].MappingsViewLoaded = false;   // device assign / output-type switch window

            ss.PushUiExtraSourcesIntoSlotMappingSets();

            var row = ms.Rows.Find(r => r.Target == "ButtonA");
            Assert.NotNull(row);
            Assert.Single(row.Sources);
            Assert.Equal("Button 3", row.Sources[0].Descriptor);
        }

        /// <summary>The positive control, so the gate cannot pass by
        /// disabling the push outright: with the grid hydrated the push
        /// is still authoritative and still rebuilds from it. A pad whose
        /// grid is loaded and holds no descriptor for the target clears
        /// that row's sources, which is the behaviour every save relies
        /// on.</summary>
        [Fact]
        public void Push_StillRebuildsFromTheGrid_WhenItIsHydrated()
        {
            var (vm, ss, ms) = Arrange();
            vm.Pads[0].MappingsViewLoaded = true;

            ss.PushUiExtraSourcesIntoSlotMappingSets();

            var row = ms.Rows.Find(r => r.Target == "ButtonA");
            // The grid is empty of authored descriptors, so the
            // authoritative push clears the row it owns.
            Assert.True(row == null || row.Sources.Count == 0,
                "a hydrated grid must remain authoritative over its own rows");
        }

        /// <summary>The ordering rule the drain applies to queued device
        /// re-keys lives in exactly one place, and it must never fold two
        /// pairs together. Round nine's collapse merged two controllers
        /// onto one identity when they swapped ports; round ten replaced
        /// it; round eleven extracted it so the drain and the tests share
        /// one copy. This pins the no-fold property directly.</summary>
        [Fact]
        public void RekeyPasses_AreNeverFolded()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();

            // Two devices swapping ports: dev1 A->C, then dev2 B->A.
            var passes = InputService.BuildRekeyPasses(new[] { (a, c), (b, a) });

            Assert.Equal(2, passes.Count);
            Assert.Equal((a, c), (passes[0].Old, passes[0].New));
            Assert.Equal((b, a), (passes[1].Old, passes[1].New));   // NOT (b, c)
        }

        /// <summary>THE ONE THE UNIT TESTS COULD NOT SEE. Rounds ten and
        /// eleven taught a device re-key to follow the PadSetting device
        /// pins, and asserted it against the in-memory PadSetting only.
        /// But SaveToFile's FIRST action pushes ViewModel -> PadSetting,
        /// and the drain never touched the ViewModel mirrors, so the very
        /// next save (which the drain itself arms via MarkDirty) wrote the
        /// dead guid straight back. The self-pinned case was repaired by
        /// accident; the CROSS-DEVICE case these fields exist for was not,
        /// and on restart the Aim Engage source named a device that no
        /// longer existed.
        ///
        /// <para>This drives the real save-side push, so it fails if
        /// either half of the re-key is missing.</para></summary>
        [Fact]
        public void DevicePins_SurviveTheSavePathsViewModelPush()
        {
            var (vm, ss, _) = Arrange();
            var oldGuid = Guid.NewGuid();
            var newGuid = Guid.NewGuid();

            // A slot whose SELECTED device is something else entirely:
            // the pins name a second, cross-device controller. This is
            // the shape with no accidental repair.
            var selected = Guid.NewGuid();
            var ud = new UserDevice { InstanceGuid = selected, IsOnline = true };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            var ps = new PadSetting
            {
                GyroAimEngageDeviceGuid = oldGuid.ToString(),
                LeftTriggerRouteActivatorDeviceGuid = oldGuid.ToString(),
            };
            var us = new UserSetting { InstanceGuid = selected, MapTo = 0 };
            us.SetPadSetting(ps);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(us);

            // The ViewModel mirrors carry the same pre-re-key guid, which
            // is what the save pushes back down. The pad must actually
            // HAVE a selected device, or UpdatePadSettingsFromViewModels
            // skips it and the test passes for the wrong reason (which is
            // exactly how the first cut of this test proved nothing).
            var pad = vm.Pads[0];
            var mapped = new PadViewModel.MappedDeviceInfo { InstanceGuid = selected };
            pad.MappedDevices.Add(mapped);
            pad.SelectedMappedDevice = mapped;
            pad.GyroAimEngageDeviceGuid = oldGuid.ToString();
            pad.LeftTriggerRouteActivatorDeviceGuid = oldGuid.ToString();

            InputService.RemapDeviceGuidsInStoredPadSettings(oldGuid, newGuid);
            InputService.RemapDeviceGuidsInPadViewModels(
                oldGuid, newGuid, new[] { pad });

            // Now let the SAVE path push the ViewModels down, exactly as
            // SaveToFile does before it serializes anything.
            ss.UpdatePadSettingsFromViewModels();

            Assert.Equal(newGuid.ToString(), ps.GyroAimEngageDeviceGuid);
            Assert.Equal(newGuid.ToString(), ps.LeftTriggerRouteActivatorDeviceGuid);
        }

        /// <summary>No-op and Empty pairs never reach the remap: Empty is
        /// not an identity, and a self-map would be wasted work on every
        /// guid-keyed store.</summary>
        [Fact]
        public void RekeyPasses_DropNoOpsAndEmpties()
        {
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var passes = InputService.BuildRekeyPasses(new[]
            {
                (a, a),                 // self-map
                (Guid.Empty, b),        // Empty source
                (a, Guid.Empty),        // Empty destination
                (a, b),                 // the only real one
            });

            Assert.Single(passes);
            Assert.Equal((a, b), (passes[0].Old, passes[0].New));
        }
    }
}
