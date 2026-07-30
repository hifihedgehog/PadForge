using System;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Root-cause proof for the confirmed regression "offline gamepad +
    /// online keyboard on a Nintendo slot leaves the virtual controller's
    /// stick axes off-center at rest."
    ///
    /// Mechanism under test: the SINGLE-source branch of the per-target
    /// MappingSet evaluators (InputManager.Step3.MappingSetEval.cs,
    /// TryEvaluateMappingSetBipolarAxis ~:2475, ...RawTrigger ~:2671,
    /// ...Button ~:2427) resolves a device-pinned source with
    ///
    ///     var devState = string.IsNullOrEmpty(src.DeviceGuid)
    ///         ? state
    ///         : (LookupDeviceState(src.DeviceGuid) ?? state);
    ///
    /// LookupDeviceState returns null for an OFFLINE device (the memo's
    /// documented "offline-contributes-zero contract",
    /// MappingSetEval.cs:1943), but the "?? state" fallback then evaluates
    /// the offline device's descriptor against the CURRENT pass's device
    /// state instead. During the online keyboard's Step-3 pass, the offline
    /// gamepad's automapped stick row ("Axis 0" on RawAxis0,
    /// SettingsManager.CreateDefaultPadSetting:670) reads the keyboard's
    /// CustomInputState.Axis[0], which is unsigned 0 (keyboards expose no
    /// axes; the ctor zeroes the array and center is 32768), and the
    /// bipolar read maps unsigned 0 to -1.0 (SourceCoercion.ReadAsBipolar
    /// Axis case: (0 - 32768) / 32767 clamped). Result: short.MinValue on
    /// every automapped stick axis of the keyboard's RawHidOutputState,
    /// which Step 4 seeds into CombinedRawHidStates (the offline device's
    /// all-null raw state is skipped), Step 5 converts to 0.0 via
    /// (v + 32768) / 65535, and HM v1.3.18's SwitchProPacker packs as
    /// 12-bit 0x200 = full corner deflection.
    ///
    /// The MULTI-source branch of the same evaluator already implements
    /// the intended contract: BuildCustomContribsForBipolarAxis does
    /// "devState == null -> list.Add(0f)" (MappingSetEval.cs:2116-2117),
    /// so an offline-pinned source contributes rest. These tests pin the
    /// inconsistency between the two branches.
    ///
    /// The two former DEFECT tests originally asserted the buggy values
    /// to prove the mechanism; the fallback is now fixed
    /// (offline-pinned single source contributes rest) and they assert
    /// the rest values as regression guards.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class OfflinePinnedSourceEvalTests : IDisposable
    {
        private static readonly Guid OfflinePadGuid = new("aaaaaaaa-1111-2222-3333-444444444444");
        private const string KeyboardGuid = "bbbbbbbb-5555-6666-7777-888888888888";
        private const int Slot = 7;

        private readonly SettingsCollection _savedSettings;
        private readonly DeviceCollection _savedDevices;
        private readonly MappingSet[] _savedMappingSets;

        public OfflinePinnedSourceEvalTests()
        {
            _savedSettings = SettingsManager.UserSettings;
            _savedDevices = SettingsManager.UserDevices;
            _savedMappingSets = SettingsManager.SlotMappingSets;

            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
        }

        public void Dispose()
        {
            SettingsManager.UserSettings = _savedSettings;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.SlotMappingSets = _savedMappingSets;
        }

        /// <summary>Registers the gamepad the way the repro topology has it:
        /// assigned to the slot but not online (e.g. a Bluetooth pad that is
        /// switched off). LookupDeviceState returns null for it.</summary>
        private static void AddOfflineGamepad()
        {
            var ud = new UserDevice
            {
                InstanceGuid = OfflinePadGuid,
                ProductName = "Offline Test Gamepad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = false,
                InputState = null,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
        }

        /// <summary>The Nintendo automap row shape: slot-scoped MappingSet
        /// row targeting a raw stick axis, single source pinned to the
        /// gamepad's GUID with the SDL "Axis 0" descriptor
        /// (MappingSetMigrator.cs:484 stamps DeviceGuid per device).</summary>
        private static MappingSet StickRowPinnedToOfflinePad(
            bool invert = false, string descriptor = "Axis 0", string target = "RawAxis0")
        {
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = target,
                Sources =
                {
                    new MappingSource
                    {
                        Kind = "Direct",
                        Descriptor = descriptor,
                        DeviceGuid = OfflinePadGuid.ToString().ToLowerInvariant(),
                        Invert = invert,
                    },
                },
            });
            return ms;
        }

        /// <summary>A keyboard-like CustomInputState: the ctor zeroes
        /// Axis[], and unsigned center is 32768, so every axis of a device
        /// that has no axes reads as full-negative deflection when coerced
        /// bipolar.</summary>
        private static CustomInputState KeyboardState() => new();

        // ─────────────────────────────────────────────────────────────
        //  REGRESSION GUARD (was the Bug B defect): single-source stick
        //  row pinned to an offline device contributes rest, never the
        //  co-assigned online device's state.
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void SingleSource_OfflinePinnedStickRow_ContributesCenteredRest()
        {
            AddOfflineGamepad();
            var ms = StickRowPinnedToOfflinePad();

            bool handled = InputManager.TryEvaluateMappingSetBipolarAxis(
                KeyboardState(), ms, KeyboardGuid, Slot, "RawAxis0", out short value);

            Assert.True(handled, "Row exists; the evaluator must own the target.");

            // Offline-contributes-zero contract: the row owns the target
            // (handled == true) and the offline-pinned source reads
            // centered rest, matching the multi-source branch. The old
            // "?? state" fallback read the KEYBOARD's zeroed Axis[0]
            // (unsigned 0 = bipolar -1.0) and returned short.MinValue.
            Assert.Equal(0, value);
        }

        [Fact]
        public void SingleSource_OfflinePinnedInvertedTriggerRow_ReadsReleased()
        {
            AddOfflineGamepad();
            // An inverted trigger-axis source (some DirectInput pads rest
            // high): Invert applies 1 - raw, and raw over the keyboard's
            // zeroed Axis[2] is 0, so the phantom trigger reads FULLY
            // PULLED while both devices are untouched.
            var ms = StickRowPinnedToOfflinePad(
                invert: true, descriptor: "Axis 2", target: "RawAxis2");

            bool handled = InputManager.TryEvaluateMappingSetRawTrigger(
                KeyboardState(), ms, KeyboardGuid, Slot, "RawAxis2", out short value);

            Assert.True(handled);

            // Offline-contributes-zero contract: released trigger. The
            // old fallback inverted the keyboard's zeroed axis into a
            // phantom FULL PULL (short.MaxValue) with no device touched.
            Assert.Equal(short.MinValue, value);
        }

        // ─────────────────────────────────────────────────────────────
        //  CONTRAST: the multi-source branch of the SAME evaluator
        //  already implements offline-contributes-rest.
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void MultiSource_OfflinePinnedStickRow_ContributesRest_Centered()
        {
            AddOfflineGamepad();
            var ms = new MappingSet();
            ms.Rows.Add(new MappingRow
            {
                Target = "RawAxis0",
                CombineMode = "MaxAbs",
                Sources =
                {
                    // Two contributing sources force the multi-source branch
                    // (BuildCustomContribsForBipolarAxis). Different
                    // descriptors + same Invert so the pair is NOT folded as
                    // a bipolar Neg pair.
                    new MappingSource
                    {
                        Kind = "Direct", Descriptor = "Axis 0",
                        DeviceGuid = OfflinePadGuid.ToString().ToLowerInvariant(),
                    },
                    new MappingSource
                    {
                        Kind = "Direct", Descriptor = "Axis 3",
                        DeviceGuid = OfflinePadGuid.ToString().ToLowerInvariant(),
                    },
                },
            });

            bool handled = InputManager.TryEvaluateMappingSetBipolarAxis(
                KeyboardState(), ms, KeyboardGuid, Slot, "RawAxis0", out short value);

            Assert.True(handled);
            // Intended contract, already honored on this branch:
            // LookupDeviceStateFast == null -> contribute 0f (rest).
            Assert.Equal(0, value);
        }

        // ─────────────────────────────────────────────────────────────
        //  CONTROL: the same single-source row with the device ONLINE and
        //  centered evaluates to rest. The poison is the offline
        //  fallback, not the evaluator math.
        // ─────────────────────────────────────────────────────────────

        [Fact]
        public void SingleSource_OnlinePinnedStickRow_CenteredDeviceReadsCentered()
        {
            var onlineState = new CustomInputState();
            onlineState.Axis[0] = 32768; // unsigned center
            var ud = new UserDevice
            {
                InstanceGuid = OfflinePadGuid,
                ProductName = "Online Test Gamepad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                InputState = onlineState,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var ms = StickRowPinnedToOfflinePad();

            bool handled = InputManager.TryEvaluateMappingSetBipolarAxis(
                KeyboardState(), ms, KeyboardGuid, Slot, "RawAxis0", out short value);

            Assert.True(handled);
            Assert.Equal(0, value);
        }

        [Fact]
        public void SingleSource_OnlinePinnedStickRow_DeflectedDeviceReadsDeflection()
        {
            var onlineState = new CustomInputState();
            onlineState.Axis[0] = 65535; // full positive
            var ud = new UserDevice
            {
                InstanceGuid = OfflinePadGuid,
                ProductName = "Online Test Gamepad",
                CapType = InputDeviceType.Gamepad,
                IsOnline = true,
                InputState = onlineState,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);

            var ms = StickRowPinnedToOfflinePad();

            bool handled = InputManager.TryEvaluateMappingSetBipolarAxis(
                KeyboardState(), ms, KeyboardGuid, Slot, "RawAxis0", out short value);

            Assert.True(handled);
            // (65535 - 32768) / 32767 = +1.0 -> short.MaxValue.
            Assert.Equal(short.MaxValue, value);
        }
    }
}
