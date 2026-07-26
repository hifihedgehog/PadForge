using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Audit 2026-07-25 round seven: the round-six fixes audited
    /// as hard as the bugs they fixed. R2: the motion gate must reject
    /// steady motion, not only varying motion. R7: deleting a legacy
    /// "Base"-masked activator must heal the data, never sweep the base
    /// rows and base-scoped macros that share the mask.</summary>
    [Collection("SettingsManagerStatics")]
    public class AuditJuly25RoundSevenTests : IDisposable
    {
        private static readonly Guid DevGuid = new("ababab00-4444-5555-6666-777777777777");
        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedSlotSets;

        public AuditJuly25RoundSevenTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedSlotSets = SettingsManager.SlotMappingSets;
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedSlotSets;
        }

        private static CustomInputState ArrangeDevice(out UserDevice ud)
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            var state = new CustomInputState();
            ud = new UserDevice
            {
                InstanceGuid = DevGuid,
                ProductName = "Round Seven Pad",
                IsOnline = true,
                HasGyro = true,
                HasGyroAux = false,
                InputState = state,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            return state;
        }

        // ── R2: the plausibility bound ──

        /// <summary>A pad rotating at a STEADY rate has zero peak-to-peak
        /// range, so the round-six motion gate passed it and stored the
        /// rotation rate as the bias. The same shape covers a state stream
        /// frozen on a mid-motion sample. Only the average-magnitude bound
        /// can reject it: real at-rest drift sits near 0.02 rad/s, two
        /// orders under the value here.</summary>
        [Fact]
        public async Task SteadyRotation_IsRejectedByThePlausibilityBound()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[1] = 1.0f;                    // constant 1 rad/s yaw
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            bool ok = await svc.RecalibrateAsync(ud, ps, 250);

            Assert.False(ok);
            Assert.Equal("0", ps.GyroBiasYaw);
            Assert.Equal("", ps.GyroCalibratedAtUtc ?? "");
        }

        /// <summary>The bound must not reject genuine drift: a constant
        /// small offset is exactly what calibration exists to measure.</summary>
        [Fact]
        public async Task GenuineDrift_StillCalibrates()
        {
            var state = ArrangeDevice(out var ud);
            state.Gyro[0] = 0.04f;                   // plausible real drift
            var ps = new PadSetting();
            var svc = new GyroCalibratorService();

            bool ok = await svc.RecalibrateAsync(ud, ps, 250);

            Assert.True(ok);
            Assert.Equal(0.04f, float.Parse(ps.GyroBiasPitch,
                System.Globalization.CultureInfo.InvariantCulture), 3);
        }

        // ── R7: legacy-"Base" delete heals, normal delete sweeps ──

        private static MappingSet SlotWithRows(params string[] rowMasks)
        {
            var ms = new MappingSet
            {
                ShiftActivators = new List<ShiftActivator>(),
                Rows = new List<MappingRow>(),
            };
            foreach (var mask in rowMasks)
                ms.Rows.Add(new MappingRow { LayerMask = mask, Target = "A" });
            return ms;
        }

        /// <summary>THE detonation fix. A legacy activator whose persisted
        /// mask is literally "Base" shares its mask with every base mapping
        /// row and every base-scoped macro. Deleting it removes ONLY the
        /// bogus activator; the rows and macros survive. Before round seven
        /// the normal sweep deleted every base row on the slot.</summary>
        [Fact]
        public void DeletingALegacyBaseActivator_HealsWithoutTouchingBaseRows()
        {
            var ms = SlotWithRows("Base", "Base", "Shift");
            var legacy = new ShiftActivator { LayerMask = "Base", LayerName = "Base", Mode = "Hold" };
            ms.ShiftActivators.Add(legacy);
            SettingsManager.SlotMappingSets = new[] { ms };

            var vm = new PadViewModel(0);
            vm.Macros.Add(new MacroItem { LayerMask = "Base", IsEnabled = true });

            PadForge.Views.PadPage.ExecuteLayerDelete(ms, legacy, "Base", new[] { vm });

            Assert.Empty(ms.ShiftActivators);
            Assert.Equal(3, ms.Rows.Count);                  // rows untouched
            Assert.True(vm.Macros[0].IsEnabled);             // macro untouched
            Assert.Equal("Base", vm.Macros[0].LayerMask);
        }

        /// <summary>The positive control: a normal layer delete still
        /// removes its rows and disables + untags its macros when no
        /// related slot declares the mask.</summary>
        [Fact]
        public void DeletingANormalLayer_StillSweepsItsRowsAndMacros()
        {
            var ms = SlotWithRows("Shift", "Shift", "Base");
            var act = new ShiftActivator { LayerMask = "Shift", LayerName = "Shift", Mode = "Hold" };
            ms.ShiftActivators.Add(act);
            SettingsManager.SlotMappingSets = new[] { ms };

            var vm = new PadViewModel(0);
            vm.Macros.Add(new MacroItem { LayerMask = "Shift", IsEnabled = true });

            PadForge.Views.PadPage.ExecuteLayerDelete(ms, act, "Shift", new[] { vm });

            Assert.Empty(ms.ShiftActivators);
            Assert.Single(ms.Rows);                          // only the Base row remains
            Assert.Equal("Base", ms.Rows[0].LayerMask);
            Assert.False(vm.Macros[0].IsEnabled);
            Assert.Equal("", vm.Macros[0].LayerMask);
        }
    }
}
