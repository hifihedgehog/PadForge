using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        private readonly ConcurrentDictionary<Guid, GyroSimulationDeviceState> _gyroSimStates = new();
        private readonly HashSet<Guid> _gyroSimKnownDevices = new();
        private long _gyroSimPruneAt;

        /// <summary>Step 2 updates each assigned slot of a device once after
        /// reading it (#472). The estimator runs per (device, slot), the key
        /// the option is stored under, and only on slots that turned it on
        /// for a device that lacks an axis its accelerometer can supply.</summary>
        internal void UpdateGyroSimulation(UserDevice device, ISdlInputDevice source,
            CustomInputState state, long timestamp)
        {
            var axes = SimulatedGyro.MissingAxes(device.VendorId, device.ProdId, device.HasGyro, device.HasAccel);
            if (axes == SimulatedGyro.Axes.None)
            {
                ForgetGyroSimulation(device.InstanceGuid);
                return;
            }
            var tuningProvider = SourceCoercion.GyroTuningProvider;
            int[] slots = GetAssignedSlotsSnapshot(device.InstanceGuid);
            uint enabled = 0;
            Span<float> smoothing = stackalloc float[MaxPads];
            // A tuning cache miss can acquire UserSettings.SyncRoot. Resolve
            // before taking the context lock, as the Gyro Tilt update does.
            foreach (int slot in slots)
            {
                if ((uint)slot >= MaxPads || tuningProvider == null) continue;
                var tuning = tuningProvider(device.InstanceGuidString, slot);
                if (!tuning.SimulateGyro) continue;
                enabled |= 1u << slot;
                smoothing[slot] = tuning.SimulationSmoothingSeconds;
            }
            if (enabled == 0)
            {
                ForgetGyroSimulation(device.InstanceGuid);
                return;
            }
            var context = _gyroSimStates.GetOrAdd(device.InstanceGuid, static _ => new GyroSimulationDeviceState());
            context.Update(device, source, timestamp, enabled, smoothing, axes, SensorVector(state.Accel));
        }

        /// <summary>The funnel asks only when the (device, slot) tuning has
        /// the option on, so devices without it never reach the lookup. The
        /// Gyro tab's readout reads from the UI thread, under the same
        /// per-device lock the polling thread writes under.</summary>
        internal SimulatedGyroSample? ReadGyroSimulation(string deviceGuid, int slot)
        {
            if ((uint)slot >= MaxPads || !Guid.TryParse(deviceGuid, out var guid)) return null;
            return _gyroSimStates.TryGetValue(guid, out var context) ? context.Read(slot) : null;
        }

        /// <summary>A failed read drops the estimates. The next good sample
        /// seeds them again.</summary>
        internal void InvalidateGyroSimulation(Guid deviceGuid)
        {
            if (_gyroSimStates.TryGetValue(deviceGuid, out var context)) context.Invalidate();
        }

        /// <summary>Runs for every device without the option on every poll.
        /// TryGetValue takes no lock, and TryRemove locks its bucket, so the
        /// common miss stays lock-free.</summary>
        private void ForgetGyroSimulation(Guid deviceGuid)
        {
            if (!_gyroSimStates.TryGetValue(deviceGuid, out _)) return;
            if (_gyroSimStates.TryRemove(deviceGuid, out var context)) context.Disconnect();
        }

        private void DisconnectGyroSimulation(Guid deviceGuid)
        {
            if (_gyroSimStates.TryGetValue(deviceGuid, out var context)) context.Disconnect();
        }

        /// <summary>Called with UserDevices.SyncRoot held, alongside the
        /// device snapshot. A removed record cannot keep a wrapper or grow
        /// the registry past the saved device collection.</summary>
        private void PruneGyroSimulation(IEnumerable<UserDevice> devices)
        {
            // No IsEmpty here: on an empty dictionary it takes every bucket
            // lock, and this registry is empty on almost every PC. The
            // enumeration below takes none.
            long now = Environment.TickCount64;
            if (now < _gyroSimPruneAt) return;
            _gyroSimPruneAt = now + 250;
            bool any = false;
            foreach (var _ in _gyroSimStates) { any = true; break; }
            if (!any) return;
            _gyroSimKnownDevices.Clear();
            foreach (var device in devices)
                if (device != null) _gyroSimKnownDevices.Add(device.InstanceGuid);
            foreach (var pair in _gyroSimStates)
                if (!_gyroSimKnownDevices.Contains(pair.Key)) ForgetGyroSimulation(pair.Key);
        }

        /// <summary>A profile switch starts the estimates over, as it does for
        /// Gyro Tilt. A recenter does not: the rate has no neutral to
        /// re-reference, and a reset would only drop the smoothing in flight.</summary>
        internal void ResetGyroSimulation(Guid? deviceGuid = null)
        {
            if (deviceGuid.HasValue)
            {
                if (_gyroSimStates.TryGetValue(deviceGuid.Value, out var context)) context.Reset();
            }
            else
            {
                foreach (var context in _gyroSimStates.Values) context.Reset();
            }
        }

        private sealed class GyroSimulationSlotState
        {
            public readonly AccelRateEstimator Estimator = new();
            public long Timestamp;
        }

        private sealed class GyroSimulationDeviceState
        {
            private readonly object _sync = new();
            private readonly GyroSimulationSlotState[] _slots = new GyroSimulationSlotState[MaxPads];
            private UserDevice _device;
            private ISdlInputDevice _source;
            private uint _instanceId;
            private SimulatedGyro.Axes _axes;

            public void Update(UserDevice device, ISdlInputDevice source, long timestamp, uint enabled,
                ReadOnlySpan<float> smoothing, SimulatedGyro.Axes axes, Vector3 acceleration)
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
                    _axes = axes;
                    for (int slot = 0; slot < MaxPads; slot++)
                    {
                        if ((enabled & (1u << slot)) == 0)
                        {
                            _slots[slot] = null;
                            continue;
                        }
                        var entry = _slots[slot] ??= new GyroSimulationSlotState();
                        float dt = entry.Timestamp == 0
                            ? 0f
                            : (float)((timestamp - entry.Timestamp) / (double)Stopwatch.Frequency);
                        entry.Estimator.Update(acceleration, dt, smoothing[slot]);
                        entry.Timestamp = timestamp;
                    }
                }
            }

            public SimulatedGyroSample? Read(int slot)
            {
                lock (_sync)
                {
                    var entry = _slots[slot];
                    // Update drops every slot outside the enabled mask under
                    // this lock, so a live entry is an enabled, assigned slot.
                    if (entry == null
                        || _device == null || !_device.IsOnline || !ReferenceEquals(_device.Device, _source))
                        return null;
                    Vector3 rate = entry.Estimator.Rate;
                    return new SimulatedGyroSample(rate.X, rate.Y, rate.Z, _axes);
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
                    _axes = SimulatedGyro.Axes.None;
                }
            }
        }
    }
}
