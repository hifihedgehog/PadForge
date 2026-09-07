using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class RemotePeerRecoveryTests : IDisposable
    {
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();

        public RemotePeerRecoveryTests()
        {
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            Array.Clear(SettingsManager.SlotCreated);
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _devices;
            SettingsManager.UserSettings = _settings;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        }

        [Fact]
        public void PollingRecoversTheSamePeerAfterAnInputGap()
        {
            long now = Stopwatch.Frequency;
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "source", PeerLocalDeviceId = "pad",
                Name = "Remote pad", NumAxes = 6, NumButtons = 17, NumHats = 1,
                InputDeviceType = InputDeviceType.Gamepad
            }, 200, () => now);
            var device = new UserDevice();
            device.LoadFromExternalDevice(peer);
            device.IsOnline = true;
            SettingsManager.UserDevices.Items.Add(device);
            var manager = new InputManager();
            int recovered = 0;
            manager.DevicesUpdated += (_, _) => recovered++;
            var errors = new List<string>();
            manager.ErrorOccurred += (_, e) => errors.Add(e.ToString());

            Send(peer, 0, 1);
            Poll(manager);
            Assert.True(device.IsOnline);
            Assert.True(device.InputState.Buttons[0]);

            now += Stopwatch.Frequency;
            Poll(manager);
            Assert.False(device.IsOnline);
            Assert.True(peer.IsAttached);

            Send(peer, 1, 2);
            Poll(manager);
            Assert.True(device.IsOnline);
            Assert.Same(peer, device.Device);
            Assert.True(device.InputState.Buttons[1]);
            Assert.False(device.InputState.Buttons[0]);
            Assert.Equal(1, recovered);
            Poll(manager);
            Assert.Equal(1, recovered);
            Assert.Empty(errors);
        }

        [Fact]
        public void ADisconnectedOrDisposedPeerCannotRecoverFromBufferedInput()
        {
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "source", PeerLocalDeviceId = "pad", NumAxes = 6
            });
            Send(peer, 0, 1);
            Assert.NotNull(peer.GetCurrentState());
            peer.SetConnected(false);
            Assert.Null(peer.GetCurrentState());
            peer.SetConnected(true);
            Assert.NotNull(peer.GetCurrentState());
            peer.Dispose();
            peer.SetConnected(true);
            Assert.Null(peer.GetCurrentState());
        }

        private static void Send(RemotePeerDevice peer, int button, ulong timestamp)
        {
            var state = CustomInputStateCodec.CreateNeutral();
            state.Buttons[button] = true;
            Assert.True(peer.ApplyFramePayload(CustomInputStateCodec.Encode(state,
                new CustomInputStateCodec.Caps(false, false)), timestamp));
        }

        private static void Poll(InputManager manager)
            => typeof(InputManager).GetMethod("UpdateInputStates", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, null);
    }
}
