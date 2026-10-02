using System;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The button pressure rows (discussion #476): how hard cross, circle,
    /// square, triangle, L1, R1 and the four D-pad directions are pressed,
    /// which the DualShock 3 (SIXAXIS): Full preset's report carries.
    ///
    /// <para>Step 3 reads each row like a trigger, once per device pass, the
    /// way the Motion rows are read. Step 4 takes the harder press per button
    /// across the slot's devices, the rule triggers combine by. Step 5 hands
    /// the result to the virtual controller, which sends a button's pressure
    /// only while the frame presses that button
    /// (HMaestroVirtualController.PressureOnTheWire).</para>
    ///
    /// <para>A device's value holds while the device is briefly unavailable
    /// and goes to rest when it leaves, the OutputState rule, so a pressure
    /// cannot outlive the press it belongs to: the button it pairs with
    /// follows the same rule.</para>
    /// </summary>
    public partial class InputManager
    {
        /// <summary>The slot's pressure rows combined across its devices for
        /// the current pass. Read by Step 5 and the grid's value
        /// column.</summary>
        public ButtonPressureState[] CombinedPressureStates { get; } = new ButtonPressureState[MaxPads];

        // Row-presence gate, the Motion rows' 250 ms shape. Poll thread only.
        private readonly object[] _pressureRowsSetRef = new object[MaxPads];
        private readonly int[] _pressureRowsCount = new int[MaxPads];
        private readonly long[] _pressureRowsCheckedTick = new long[MaxPads];
        private readonly bool[] _pressureHasRows = new bool[MaxPads];

        /// <summary>True when the slot's preset carries button pressure: a
        /// PlayStation slot on the DualShock 3 (SIXAXIS): Full.</summary>
        internal bool SlotCarriesPressure(int slot)
            => slot >= 0 && slot < MaxPads
            && SlotControllerTypes[slot] == VirtualControllerType.PlayStation
            && HMaestroProfileCatalog.ReportCarriesPressure(SlotProfileIds[slot]);

        /// <summary>True when the slot carries pressure and a pressure row
        /// holds an input. The rows are rescanned every 250 ms, and at once
        /// when the set is replaced or a row is added or removed, so a slot
        /// without them skips the ten reads.</summary>
        private bool PressureRowsActive(int slot, MappingSet ms)
        {
            if (!SlotCarriesPressure(slot)) return false;
            long now = Environment.TickCount64;
            int rowCount = ms?.Rows?.Count ?? 0;
            if (!ReferenceEquals(_pressureRowsSetRef[slot], ms)
                || _pressureRowsCount[slot] != rowCount
                || now - _pressureRowsCheckedTick[slot] >= 250)
            {
                _pressureRowsSetRef[slot] = ms;
                _pressureRowsCount[slot] = rowCount;
                _pressureRowsCheckedTick[slot] = now;
                _pressureHasRows[slot] = HasSourcedPressureRow(ms);
            }
            return _pressureHasRows[slot];
        }

        /// <summary>True when any row on any layer names a pressure target and
        /// carries an input. An InvertOnHold modifier alone reads nothing,
        /// the rule <see cref="HasSourcedMotionAxisRow"/> follows.</summary>
        internal static bool HasSourcedPressureRow(MappingSet ms)
        {
            var rows = ms?.Rows;
            if (rows == null) return false;
            int count = rows.Count;
            for (int i = 0; i < count && i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || !MappingSetMigrator.IsPressureTarget(r.Target)) continue;
                var sources = r.Sources;
                if (sources == null) continue;
                int n = sources.Count;
                for (int j = 0; j < n && j < sources.Count; j++)
                {
                    var s = sources[j];
                    if (s != null && !string.Equals(s.Kind, "InvertOnHold", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Step 3: one device's pressure rows.</summary>
        private static ButtonPressureState EvaluatePressureRows(CustomInputState state,
            MappingSet ms, string deviceGuid, int slot)
        {
            var values = default(ButtonPressureState);
            var targets = MappingSetMigrator.PressureTargets;
            for (int i = 0; i < targets.Count; i++)
            {
                if (TryEvaluateMappingSetRawTrigger(state, ms, deviceGuid, slot, targets[i], out short v))
                    values[i] = PressureByte(v);
            }
            return values;
        }

        /// <summary>A raw-surface trigger value, short.MinValue released to
        /// short.MaxValue fully pressed, as a pressure byte, rounded. A
        /// DualShock 3 pressure axis round-trips exactly: its byte times 257
        /// comes back as the byte.</summary>
        internal static byte PressureByte(short v)
            => (byte)(((v + 32768) * 255 + 32767) / 65535);

        /// <summary>Step 4: the harder press per button across the slot's
        /// devices.</summary>
        private void CombinePressureRows(int padIndex, int slotCount)
        {
            var combined = default(ButtonPressureState);
            for (int si = 0; si < slotCount; si++)
            {
                var us = _padIndexBuffer[si];
                if (us == null) continue;
                combined = ButtonPressureState.Max(combined, us.PressureOutputState);
            }
            CombinedPressureStates[padIndex] = combined;
        }
    }
}
