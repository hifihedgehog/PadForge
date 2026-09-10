using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        private readonly ConcurrentDictionary<Guid, GyroTiltDeviceState> _gyroTiltStates = new();
        private readonly HashSet<Guid> _gyroTiltKnownDevices = new();
        private long _gyroTiltPruneAt;
        private static long _gyroTiltGeneration;

        /// <summary>Step 2 updates each assigned slot once after reading its device.
        /// Calibration follows the rate reader's per-device, per-slot contract.</summary>
        internal void UpdateGyroTiltGravity(UserDevice device, ISdlInputDevice source,
            CustomInputState state, long timestamp)
        {
            if (!device.HasGyro || !device.HasAccel)
            {
                ForgetGyroTiltGravity(device.InstanceGuid);
                return;
            }
            var context = _gyroTiltStates.GetOrAdd(device.InstanceGuid, static _ => new GyroTiltDeviceState());
            int[] slots = GetAssignedSlotsSnapshot(device.InstanceGuid);
            uint assigned = 0;
            Span<Vector3> biases = stackalloc Vector3[MaxPads];
            // A cache miss can acquire UserSettings.SyncRoot. Resolve before
            // taking the context lock, which protects only runtime sensor state.
            foreach (int slot in slots)
            {
                if ((uint)slot >= MaxPads) continue;
                assigned |= 1u << slot;
                var bias = SourceCoercion.GyroBiasProvider?.Invoke(device.InstanceGuidString, slot) ?? default;
                biases[slot] = new Vector3(bias.pitch, bias.yaw, bias.roll);
            }
            context.Update(device, source, timestamp, assigned, biases,
                SensorVector(state.Gyro), SensorVector(state.Accel));
        }

        private static Vector3 SensorVector(float[] values)
            => values != null && values.Length >= 3
                ? new Vector3(values[0], values[1], values[2])
                : new Vector3(float.NaN);

        internal GyroTiltGravitySample? ReadGyroTiltGravity(string deviceGuid, int slot)
        {
            if ((uint)slot >= MaxPads || !Guid.TryParse(deviceGuid, out var guid)) return null;
            return _gyroTiltStates.TryGetValue(guid, out var context) ? context.Read(slot) : null;
        }

        /// <summary>A failed read loses the estimate, but keeps the user's neutral pose.</summary>
        internal void InvalidateGyroTiltGravity(Guid deviceGuid)
        {
            if (_gyroTiltStates.TryGetValue(deviceGuid, out var context)) context.Invalidate();
        }

        private void ForgetGyroTiltGravity(Guid deviceGuid)
        {
            if (_gyroTiltStates.TryRemove(deviceGuid, out var context)) context.Disconnect();
        }

        private void DisconnectGyroTiltGravity(Guid deviceGuid)
        {
            if (_gyroTiltStates.TryGetValue(deviceGuid, out var context)) context.Disconnect();
        }

        /// <summary>Called with UserDevices.SyncRoot held, alongside the device snapshot.
        /// Offline records keep a neutral capability marker. Removed records cannot
        /// retain a wrapper or grow the registry beyond the saved device collection.</summary>
        private void PruneGyroTiltGravity(IEnumerable<UserDevice> devices)
        {
            long now = Environment.TickCount64;
            if (_gyroTiltStates.IsEmpty || now < _gyroTiltPruneAt) return;
            _gyroTiltPruneAt = now + 250;
            _gyroTiltKnownDevices.Clear();
            foreach (var device in devices)
                if (device != null) _gyroTiltKnownDevices.Add(device.InstanceGuid);
            foreach (var pair in _gyroTiltStates)
                if (!_gyroTiltKnownDevices.Contains(pair.Key)) ForgetGyroTiltGravity(pair.Key);
        }

        /// <summary>Explicit recenter/profile/stop boundaries also retire the captured tilt pose.</summary>
        internal void ResetGyroTiltGravity(Guid? deviceGuid = null)
        {
            if (deviceGuid.HasValue)
            {
                if (_gyroTiltStates.TryGetValue(deviceGuid.Value, out var context)) context.Reset();
            }
            else
            {
                // Keep capability registration during an explicit reset. Until
                // the next poll seeds the pose, reads must return zero instead
                // of switching temporarily to the legacy estimate.
                foreach (var context in _gyroTiltStates.Values) context.Reset();
            }
        }

        private sealed class GyroTiltSlotState
        {
            public readonly GyroTiltGravityEstimator Estimator = new();
            public readonly long Generation = Interlocked.Increment(ref _gyroTiltGeneration);
            public long Timestamp;
        }

        private sealed class GyroTiltDeviceState
        {
            private readonly object _sync = new();
            private readonly GyroTiltSlotState[] _slots = new GyroTiltSlotState[MaxPads];
            private UserDevice _device;
            private ISdlInputDevice _source;
            private uint _instanceId;
            private uint _assigned;

            public void Update(UserDevice device, ISdlInputDevice source, long timestamp,
                uint assigned, ReadOnlySpan<Vector3> biases, Vector3 gyro, Vector3 acceleration)
            {
                lock (_sync)
                {
                    uint instanceId = source?.SdlInstanceId ?? 0;
                    if (!ReferenceEquals(_device, device) || !ReferenceEquals(_source, source) || _instanceId != instanceId)
                    {
                        Array.Clear(_slots);
                        _device = device;
                        _source = source;
                        _instanceId = instanceId;
                    }
                    _assigned = assigned;
                    for (int slot = 0; slot < MaxPads; slot++)
                    {
                        if ((assigned & (1u << slot)) == 0)
                        {
                            _slots[slot] = null;
                            continue;
                        }
                        var entry = _slots[slot] ??= new GyroTiltSlotState();
                        Vector3 calibrated = gyro - biases[slot];
                        double dt = (timestamp - entry.Timestamp) / (double)Stopwatch.Frequency;
                        if (!float.IsFinite(calibrated.X) || !float.IsFinite(calibrated.Y) || !float.IsFinite(calibrated.Z))
                            entry.Estimator.Reset();
                        else if (entry.Timestamp == 0 || dt > 0.25 || dt < 0)
                        {
                            // The mapping pipeline uses the same 0.25 s resume
                            // bound. Reseed instead of integrating missing motion.
                            // A sample gap must not move the captured neutral.
                            entry.Estimator.Seed(acceleration);
                        }
                        else if (dt > 0)
                            entry.Estimator.Update(calibrated, acceleration, (float)dt);
                        entry.Timestamp = timestamp;
                    }
                }
            }

            public GyroTiltGravitySample Read(int slot)
            {
                lock (_sync)
                {
                    var entry = _slots[slot];
                    if (entry == null || (_assigned & (1u << slot)) == 0
                        || _device == null || !_device.IsOnline || !ReferenceEquals(_device.Device, _source))
                        return default;
                    Vector3 gravity = entry.Estimator.Gravity;
                    return new GyroTiltGravitySample(gravity.X, gravity.Y, gravity.Z, entry.Generation);
                }
            }

            public void Invalidate()
            {
                lock (_sync)
                {
                    foreach (var entry in _slots)
                    {
                        if (entry == null) continue;
                        entry.Estimator.Reset();
                        entry.Timestamp = 0;
                    }
                }
            }

            public void Reset()
            {
                lock (_sync) Array.Clear(_slots);
            }

            public void Disconnect()
            {
                lock (_sync)
                {
                    Array.Clear(_slots);
                    _device = null;
                    _source = null;
                    _instanceId = 0;
                    _assigned = 0;
                }
            }
        }
    }
}
