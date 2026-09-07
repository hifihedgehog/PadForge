using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class RemoteTerminalVibrationCodecTests
    {
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(12)]
        [InlineData(15)]
        public void TerminalFrameClearsEveryVibrationChannelAndReassignmentReassertsIt(int channels)
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "terminal-owner",
                PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
                VendorId = 0x1234, ProductId = 0x5678,
                HasRumble = true, HasHaptic = true,
                InputDeviceType = InputDeviceType.Gamepad,
                NumAxes = 6, NumButtons = 17, NumHats = 1
            });
            var device = new UserDevice();
            device.LoadFromExternalDevice(peer);
            var packets = new List<byte[]>();
            RemoteLinkOutputRouter.Register(peer.DevicePath, "terminal-owner", 7);
            RemoteLinkOutputRouter.SendOutput = (fingerprint, slot, bytes) =>
            {
                Assert.Equal("terminal-owner", fingerprint);
                Assert.Equal((byte)7, slot);
                packets.Add(bytes);
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                AddAssignment(device, 0);
                AddAssignment(device, 1);
                var manager = new InputManager();
                var raw = manager.VibrationStates[1];
                raw.LeftTriggerMotorSpeed = (ushort)((channels & 1) != 0 ? 12000 : 0);
                raw.RightTriggerMotorSpeed = (ushort)((channels & 2) != 0 ? 23000 : 0);
                raw.HasDirectionalData = (channels & 4) != 0;
                raw.HasConditionData = (channels & 8) != 0;
                raw.EffectType = 1;
                raw.SignedMagnitude = -3210;
                raw.Direction = 8192;
                raw.Period = 25;
                raw.ConditionAxisCount = raw.HasConditionData ? 1 : 0;
                raw.ConditionAxes = raw.HasConditionData
                    ? new[] { new ConditionAxisData { PositiveCoefficient = 4000, NegativeCoefficient = -2500 } }
                    : null;

                Poll(manager, device);
                byte[] activePacket = Assert.Single(packets);
                var active = Decode(activePacket);
                Assert.Equal((ushort)0, active.LeftMotorSpeed);
                Assert.Equal((ushort)0, active.RightMotorSpeed);
                Assert.Equal(raw.LeftTriggerMotorSpeed, active.LeftTriggerMotorSpeed);
                Assert.Equal(raw.RightTriggerMotorSpeed, active.RightTriggerMotorSpeed);
                Assert.Equal(raw.HasDirectionalData, active.HasDirectionalData);
                Assert.Equal(raw.HasConditionData, active.HasConditionData);
                Assert.False(device.ForceFeedbackState.IsActive);

                packets.Clear();
                SettingsManager.UserSettings.Items.RemoveAll(setting => setting.MapTo == 0);
                Poll(manager, device);
                Assert.All(packets, packet => Assert.Equal(activePacket, packet));

                packets.Clear();
                SettingsManager.UserSettings.Items.Clear();
                Poll(manager, device);
                var terminal = Decode(Assert.Single(packets));
                Assert.Equal((ushort)0, terminal.LeftMotorSpeed);
                Assert.Equal((ushort)0, terminal.RightMotorSpeed);
                Assert.Equal((ushort)0, terminal.LeftTriggerMotorSpeed);
                Assert.Equal((ushort)0, terminal.RightTriggerMotorSpeed);
                Assert.False(terminal.HasDirectionalData);
                Assert.False(terminal.HasConditionData);
                Assert.Equal(0, terminal.ConditionAxisCount);
                Assert.Null(terminal.ConditionAxes);
                Assert.Equal((short)0, terminal.SignedMagnitude);
                Poll(manager, device);
                Assert.Single(packets);

                AddAssignment(device, 1);
                Poll(manager, device);
                Assert.Equal(2, packets.Count);
                Assert.Equal(activePacket, packets[1]);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
                peer.Dispose();
            }
        }

        private static void AddAssignment(UserDevice device, int slot)
        {
            var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = slot };
            setting.SetPadSetting(new PadSetting
            {
                ForceOverall = "100", ImpulseOverallGain = "100",
                ImpulseLeftStrength = "100", ImpulseRightStrength = "100"
            });
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static Vibration Decode(byte[] packet)
        {
            Assert.True(OutputEffectCodec.TryDecode(packet, out var effect));
            Assert.Equal(OutputEffectCodec.Kind.Vibration, effect.Kind);
            return effect.Vibration;
        }

        private static void Poll(InputManager manager, UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { device });
    }
}
