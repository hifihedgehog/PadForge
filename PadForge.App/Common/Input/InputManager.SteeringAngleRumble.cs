using System.Threading;
using PadForge.Engine;
using PadForge.Engine.Data;
using static SDL3.SDL;

namespace PadForge.Common.Input
{
    public partial class InputManager
    {
        private readonly long[] _steeringAngleFrames = new long[MaxPads];

        private void PublishSteeringAngleFrame(int slot)
        {
            lock (_vcLifecycleLock)
            {
                var vc = _virtualControllers[slot];
                bool active = _running && !OutputsQuiesced && !_idle
                    && (!SuspendWhenBackground || HostIsForeground)
                    && SettingsManager.SlotCreated[slot] && SettingsManager.SlotEnabled[slot]
                    && SlotControllerTypes[slot] is VirtualControllerType.Xbox or VirtualControllerType.PlayStation
                    && vc != null && vc.IsConnected && _slotInactiveCounter[slot] == 0;
                Volatile.Write(ref _steeringAngleFrames[slot],
                    active ? SteeringAngleRumble.Pack(CombinedOutputStates[slot]) : 0);
            }
        }

        internal long ReadSteeringAngleFrame(int slot)
            => slot >= 0 && slot < MaxPads && _running && !OutputsQuiesced && !_idle
                && (!SuspendWhenBackground || HostIsForeground)
                && SettingsManager.SlotCreated[slot] && SettingsManager.SlotEnabled[slot]
                && SlotControllerTypes[slot] is VirtualControllerType.Xbox or VirtualControllerType.PlayStation
                ? Volatile.Read(ref _steeringAngleFrames[slot]) : 0;

        private void FlushSteeringAngleRumble()
        {
            bool hadAngle = false;
            for (int slot = 0; slot < MaxPads; slot++)
                hadAngle |= Interlocked.Exchange(ref _steeringAngleFrames[slot], 0) != 0;
            if (!hadAngle) return;
            // Focus suspension skips Step 2. Revisit its existing writer once
            // with the cue cleared, while preserving other feedback sources.
            var devices = SettingsManager.UserDevices;
            if (devices == null) return;
            lock (devices.SyncRoot)
            {
                foreach (var device in devices.Items)
                    if (SupportsSteeringAngleRumble(device)) ApplyForceFeedback(device);
            }
        }

        internal static bool SupportsSteeringAngleRumble(UserDevice device)
        {
            if (device?.Device == null) return false;
            // Native wheel writers convert scalar rumble into steering force.
            // This option must never create torque or suppress their idle spring.
            if (LogitechRawHidWriter.IsLogitechWheel(device.VendorId, device.ProdId)
                || FanatecRawHidWriter.IsFanatecWheel(device.VendorId, device.ProdId)
                || FanatecRawHidWriter.IsFanatecPedal(device.VendorId, device.ProdId)
                || ThrustmasterRawHidWriter.IsThrustmasterWheel(device.VendorId, device.ProdId)
                || (device.Device.HasHaptic && (device.Device.HapticFeatures & SDL_HAPTIC_CONSTANT) != 0))
                return false;
            return device.Device.HasRumble || device.Device.HasHaptic
                || XboxControllerIdentity.IsImpulseTriggerDevice(device.VendorId, device.ProdId);
        }

        internal Vibration ResolveUserRumble(int slot, PadSetting ps, Vibration raw,
            Vibration scratch, bool allowSteering = true)
        {
            var result = MacroRumbleOverride.Merge(raw, MacroRumbleOverrides[slot], scratch);
            return allowSteering
                ? SteeringAngleRumble.Merge(result, ps, ReadSteeringAngleFrame(slot), scratch)
                : result;
        }
    }
}
