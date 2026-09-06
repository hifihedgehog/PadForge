using System;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.RemoteLink;
using PadForge.ViewModels;

namespace PadForge.Services
{
    /// <summary>Applies authenticated assignment requests on the UI dispatcher.</summary>
    internal sealed class RemoteAssignmentService
    {
        private readonly MainViewModel _viewModel;
        private readonly SettingsService _settings;
        private readonly DeviceService _devices;

        public RemoteAssignmentService(MainViewModel viewModel, SettingsService settings, DeviceService devices)
        {
            _viewModel = viewModel;
            _settings = settings;
            _devices = devices;
        }

        public LinkAssignmentReply Handle(LinkAssignmentContext context)
            => context.Execute(() => Apply(context));

        private LinkAssignmentReply Apply(LinkAssignmentContext context)
        {
            var request = context.Request;
            var device = SettingsManager.FindDeviceByInstanceGuid(context.Device.InstanceGuid);
            if (device == null || !device.IsOnline || !ReferenceEquals(device.Device, context.Device)
                || _devices == null || InputService.VmMappingsStale)
                return LinkAssignmentReply.For(request, LinkAssignmentStatus.Unavailable);

            var peer = _settings.RemoteLink.Trust.Peers.FirstOrDefault(p =>
                string.Equals(p.FingerprintHex, context.PeerFingerprint, StringComparison.OrdinalIgnoreCase));
            if (peer?.AllowRemoteAssignments != true)
                return LinkAssignmentReply.For(request, LinkAssignmentStatus.Denied);

            bool CanAssign(int slot) => !peer.GamepadOnly ||
                _viewModel.Pads[slot].OutputType is VirtualControllerType.Xbox
                    or VirtualControllerType.PlayStation or VirtualControllerType.Nintendo
                    or VirtualControllerType.Extended;

            var status = LinkAssignmentStatus.Ok;
            if (request.IsSet)
            {
                if (request.Revision != _settings.AssignmentRevision) status = LinkAssignmentStatus.Stale;
                else if (request.Slot >= InputManager.MaxPads || !SettingsManager.SlotCreated[request.Slot])
                    status = LinkAssignmentStatus.Unavailable;
                else if (request.Assigned && !CanAssign(request.Slot)) status = LinkAssignmentStatus.Denied;
                else if (!_devices.SetDeviceSlotAssignment(device.InstanceGuid, request.Slot, request.Assigned))
                    status = LinkAssignmentStatus.Unavailable;
            }

            var assigned = SettingsManager.GetAssignedSlots(device.InstanceGuid);
            var slots = Enumerable.Range(0, InputManager.MaxPads)
                .Where(i => SettingsManager.SlotCreated[i])
                .Select(i => new LinkAssignmentSlot((byte)i, _viewModel.Pads[i].SlotLabel,
                    assigned.Contains(i), CanAssign(i))).ToArray();
            return new(request.RequestId, request.DeviceId, status, _settings.AssignmentRevision,
                _viewModel.Settings.ActiveProfileInfo, slots);
        }
    }
}
