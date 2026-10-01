using System;
using System.Numerics;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;

namespace PadForge.Common.Input
{
    /// <summary>
    /// The Motion Pitch, Yaw and Roll rows (#475): stick, trigger, button and
    /// key input turned into the virtual controller's motion.
    ///
    /// <para>Step 3 evaluates the three rows once per device pass, the way the
    /// VR outputs are evaluated, and stamps each device's values with the
    /// pass. Step 4 combines the current pass's values. After the macro pass,
    /// <see cref="ApplyMotionRows"/> steps each slot's
    /// <see cref="MotionRowsModel"/> and composes its output onto the motion
    /// snapshot <see cref="UpdateMotionSnapshots"/> built from the real
    /// sensors, then publishes it for Step 5 and DSU.</para>
    ///
    /// <para>With a real controller's motion on the slot, the two combine the
    /// way Dolphin combines its emulated motion with a bound IMU
    /// (WiimoteEmu.cpp GetAngularVelocity and GetAcceleration): the rates
    /// add, so real gyro aim behaves the same after any stick turn, and the
    /// real accelerometer turns only by the Angle lean. Without a live real
    /// accelerometer, gravity turned by the whole simulated orientation takes
    /// its place, so consumers that fuse gravity hold a simulated tilt.</para>
    /// </summary>
    public partial class InputManager
    {
        /// <summary>One model per slot. Static like the source-kind
        /// runtimes, so the static reset lanes reach it. Poll thread only:
        /// other threads post requests.</summary>
        private static readonly MotionRowsModel[] s_motionRowModels = InitMotionRowModels();

        private static MotionRowsModel[] InitMotionRowModels()
        {
            var arr = new MotionRowsModel[MaxPads];
            for (int i = 0; i < arr.Length; i++) arr[i] = new MotionRowsModel();
            return arr;
        }

        private const int MotionRowsLevel = 1;
        private const int MotionRowsReset = 2;
        private const int MotionRowsRestartClock = 4;

        /// <summary>Requests from any thread, consumed by the poll thread at
        /// the slot's next pass.</summary>
        private static readonly int[] s_motionRowsRequests = new int[MaxPads];

        /// <summary>True while the slot's model has moved from rest, so an
        /// idle slot is not reset every poll.</summary>
        private static readonly bool[] s_motionRowsEngaged = new bool[MaxPads];

        /// <summary>Asks the slot's model to return its Speed rotation to
        /// level (the Gyro Recenter action). -1 is every slot.</summary>
        internal static void RequestMotionRowsLevel(int slot) => PostMotionRowsRequest(slot, MotionRowsLevel);

        /// <summary>Asks the slot's model back to rest: a profile switch, a
        /// paste, a Copy From, a slot delete or compaction. -1 is every
        /// slot.</summary>
        internal static void RequestMotionRowsReset(int slot) => PostMotionRowsRequest(slot, MotionRowsReset);

        private static void PostMotionRowsRequest(int slot, int flag)
        {
            if (slot < 0)
            {
                for (int i = 0; i < s_motionRowsRequests.Length; i++)
                    Interlocked.Or(ref s_motionRowsRequests[i], flag);
                return;
            }
            if (slot < s_motionRowsRequests.Length)
                Interlocked.Or(ref s_motionRowsRequests[slot], flag);
        }

        /// <summary>The slot's three rows combined across its devices for the
        /// current pass. Read by the stage and the grid's value column.</summary>
        public MotionRowValues[] CombinedMotionRows { get; } = new MotionRowValues[MaxPads];

        // Poll-thread state the snapshot pass hands the stage.
        private readonly bool[] _motionRowsActive = new bool[MaxPads];
        private readonly bool[] _motionRowsHasDevice = new bool[MaxPads];
        private readonly bool[] _motionGyroLive = new bool[MaxPads];
        private readonly bool[] _motionAccelLive = new bool[MaxPads];
        private readonly MotionSnapshot[] _motionRowsBase = new MotionSnapshot[MaxPads];

        /// <summary>The slot families whose grids carry the Motion rows:
        /// PlayStation, Nintendo, and Extended on a Valve profile. The rule
        /// MappingSetMigrator.EnsureMotionRows and the three grid builders
        /// follow.</summary>
        internal bool SlotCarriesMotion(int slot)
            => slot >= 0 && slot < MaxPads
            && (SlotControllerTypes[slot] is VirtualControllerType.PlayStation or VirtualControllerType.Nintendo
                || (SlotControllerTypes[slot] == VirtualControllerType.Extended
                    && SlotRawHidSurface[slot]
                    && Models2D.NintendoPreviewMap.IsValve(SlotProfileIds[slot])));

        /// <summary>True when any row on any layer names one of the three
        /// targets and carries an input. An InvertOnHold modifier alone reads
        /// nothing, the rule MappingSetMigrator.IsEmptyMotionRow applies to
        /// the Motion Gyro and Motion Accelerometer rows.</summary>
        internal static bool HasSourcedMotionAxisRow(MappingSet ms)
        {
            var rows = ms?.Rows;
            if (rows == null) return false;
            int count = rows.Count;
            for (int i = 0; i < count && i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || !MappingSetMigrator.IsMotionAxisTarget(r.Target)) continue;
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

        /// <summary>The rest-at-zero test the three rows read through
        /// SourceCoercion.SourceRestsAtZeroProvider: the activator rule
        /// (#443). A gamepad trigger, a slider, an analog key, a VR trigger or
        /// grip, or a Bliss-Box pressure axis reads one way on them.</summary>
        internal static bool SourceRestsAtZero(string descriptor, string deviceGuid)
            => IsUnipolarActivatorSource(descriptor, deviceGuid);

        /// <summary>Step 3: one device's values for the three rows.</summary>
        private static MotionRowValues EvaluateMotionRows(CustomInputState state,
            MappingSet ms, string deviceGuid, int slot)
        {
            return new MotionRowValues
            {
                Pitch = ReadMotionRow(state, ms, deviceGuid, slot, MappingSetMigrator.MotionPitchTarget),
                Yaw = ReadMotionRow(state, ms, deviceGuid, slot, MappingSetMigrator.MotionYawTarget),
                Roll = ReadMotionRow(state, ms, deviceGuid, slot, MappingSetMigrator.MotionRollTarget),
                Frame = _stickTrimFrameSeq,
            };
        }

        private static float ReadMotionRow(CustomInputState state, MappingSet ms,
            string deviceGuid, int slot, string target)
        {
            if (!TryEvaluateMappingSetBipolarAxis(state, ms, deviceGuid, slot, target, out short v))
                return 0f;
            float value = Math.Clamp(v / 32767f, -1f, 1f);
            // A layer row producing output keeps a Toggle layer's auto-cancel
            // (#206) from switching the layer off mid-turn. The gamepad row
            // loop stamps its stick rows at the same mark.
            if (MathF.Abs(value) > 0.10f)
                StampLayerActivity(slot, FindActiveRowForTarget(ms, target, slot, out _));
            return value;
        }

        /// <summary>Step 4: the current pass's values across the slot's
        /// devices, the largest magnitude per axis, the rule stick axes
        /// combine by. A device that did not run Step 3 this pass (offline,
        /// removed, or no longer evaluated) contributes nothing.</summary>
        private void CombineMotionRows(int padIndex, int slotCount)
        {
            var combined = default(MotionRowValues);
            long frame = _stickTrimFrameSeq;
            for (int si = 0; si < slotCount; si++)
            {
                var us = _padIndexBuffer[si];
                if (us == null) continue;
                var v = us.MotionRowsOutputState;
                if (v.Frame != frame) continue;
                if (MathF.Abs(v.Pitch) > MathF.Abs(combined.Pitch)) combined.Pitch = v.Pitch;
                if (MathF.Abs(v.Yaw) > MathF.Abs(combined.Yaw)) combined.Yaw = v.Yaw;
                if (MathF.Abs(v.Roll) > MathF.Abs(combined.Roll)) combined.Roll = v.Roll;
                combined.Frame = frame;
            }
            CombinedMotionRows[padIndex] = combined;
        }

        /// <summary>The motion stage: steps each driven slot's model and
        /// publishes its composed snapshots. Runs after the macro pass, so a
        /// Gyro Recenter lands in the same poll, and before Step 5 and the
        /// DSU broadcast.</summary>
        private void ApplyMotionRows()
        {
            for (int pad = 0; pad < MaxPads; pad++)
            {
                var model = s_motionRowModels[pad];
                int req = Interlocked.Exchange(ref s_motionRowsRequests[pad], 0);
                if ((req & MotionRowsReset) != 0) { model.Reset(); s_motionRowsEngaged[pad] = false; }
                else
                {
                    if ((req & MotionRowsLevel) != 0) model.Level();
                    if ((req & MotionRowsRestartClock) != 0) model.RestartClock();
                }

                if (!SettingsManager.SlotCreated[pad] || !_motionRowsActive[pad])
                {
                    if (s_motionRowsEngaged[pad]) { model.Reset(); s_motionRowsEngaged[pad] = false; }
                    continue;
                }

                var baseSnapshot = _motionRowsBase[pad];
                if (!_motionRowsHasDevice[pad])
                {
                    // No device answers: the slot streams nothing, the rule
                    // real motion follows. The pose holds for its return.
                    model.RestartClock();
                    Publish(pad, baseSnapshot);
                    continue;
                }

                var ms = SettingsManager.SlotMappingSets != null && pad < SettingsManager.SlotMappingSets.Length
                    ? SettingsManager.SlotMappingSets[pad] : null;
                // Values from an earlier pass (Step 4 skipped the slot) are
                // not this poll's.
                var values = CombinedMotionRows[pad];
                if (values.Frame != _stickTrimFrameSeq) values = default;
                var pitch = MotionAxis(ms, pad, MappingSetMigrator.MotionPitchTarget, values.Pitch);
                var yaw = MotionAxis(ms, pad, MappingSetMigrator.MotionYawTarget, values.Yaw);
                var roll = MotionAxis(ms, pad, MappingSetMigrator.MotionRollTarget, values.Roll);
                model.Step(pitch, yaw, roll, baseSnapshot.TimestampUs);
                s_motionRowsEngaged[pad] = true;

                Publish(pad, Compose(model, baseSnapshot, _motionGyroLive[pad], _motionAccelLive[pad]));
            }
        }

        private void Publish(int pad, in MotionSnapshot snapshot)
        {
            MotionSnapshots[pad] = snapshot;
            if (pad < DsuMotionSnapshots.Length)
                DsuMotionSnapshots[pad] = snapshot;
        }

        /// <summary>The active row's settings for one target, with its
        /// combined value.</summary>
        private static MotionRowsModel.Axis MotionAxis(MappingSet ms, int pad, string target, float value)
        {
            var row = FindActiveRowForTarget(ms, target, pad, out _);
            if (row == null)
                return new MotionRowsModel.Axis
                {
                    Value = value,
                    SpeedDps = MappingRow.DefaultMotionSpeed,
                    AngleDeg = MappingRow.DefaultMotionAngle,
                    Deadzone = MappingRow.DefaultMotionDeadzone / 100f,
                };
            return new MotionRowsModel.Axis
            {
                Value = value,
                Angle = string.Equals(row.MotionResponse, MappingRow.MotionResponseAngle, StringComparison.Ordinal),
                SpeedDps = row.MotionSpeed,
                MinSpeedDps = row.MotionMinSpeed,
                AngleDeg = row.MotionAngle,
                Deadzone = row.MotionDeadzone / 100f,
            };
        }

        /// <summary>The model's output on top of the real sensors.</summary>
        internal static MotionSnapshot Compose(MotionRowsModel model, in MotionSnapshot real,
            bool gyroLive, bool accelLive)
        {
            var gyro = model.GyroDps;
            if (gyroLive)
                gyro += new Vector3(real.GyroPitch, real.GyroYaw, real.GyroRoll);
            var accel = accelLive
                ? MotionRowsModel.ToBody(model.Lean, new Vector3(real.AccelX, real.AccelY, real.AccelZ))
                : MotionRowsModel.ToBody(model.Orientation, Vector3.UnitY);
            return new MotionSnapshot
            {
                GyroPitch = gyro.X, GyroYaw = gyro.Y, GyroRoll = gyro.Z,
                AccelX = accel.X, AccelY = accel.Y, AccelZ = accel.Z,
                HasMotion = true,
                TimestampUs = real.TimestampUs,
            };
        }
    }
}
