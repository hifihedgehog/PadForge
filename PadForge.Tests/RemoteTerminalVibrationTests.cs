using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.RemoteLink;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class RemoteTerminalVibrationTests
    {
        [Fact]
        public void LastConsumerAssignmentStopsTheOwnerAndASurvivingAssignmentDoesNot()
        {
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var savedMembership = RemoteLinkOutputRouter.IsPeerConnected;
            var web = new WebControllerDevice("terminal-" + Guid.NewGuid().ToString("N"), "Browser pad");
            var owner = new UserDevice();
            owner.LoadFromWebDevice(web);
            var peer = new RemotePeerDevice(new RemotePeerDeviceInfo
            {
                PeerFingerprintHex = "owner", PeerLocalDeviceId = web.InstanceGuid.ToString("N"),
                HasRumble = true, VendorId = web.VendorId, ProductId = web.ProductId,
                NumAxes = 6, NumButtons = 17, NumHats = 1, InputDeviceType = InputDeviceType.Gamepad
            });
            var remote = new UserDevice();
            remote.LoadFromExternalDevice(peer);
            var sent = new List<(ushort Left, ushort Right)>();
            web.RumbleRequested += (left, right) => sent.Add((left, right));
            var service = new InputService(new MainViewModel());
            var apply = typeof(InputService).GetMethod("ApplyRemoteOutput", BindingFlags.NonPublic | BindingFlags.Instance);
            RemoteLinkOutputRouter.Register(peer.DevicePath, "owner", 0);
            RemoteLinkOutputRouter.IsPeerConnected = _ => true;
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                apply.Invoke(service, new object[] { effect, web, owner, "consumer", null });
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                foreach (int slot in new[] { 0, 1 })
                {
                    var setting = new UserSetting { InstanceGuid = remote.InstanceGuid, MapTo = slot };
                    setting.SetPadSetting(new PadSetting { ForceOverall = "100" });
                    SettingsManager.UserSettings.Items.Add(setting);
                }
                var manager = new InputManager();
                manager.VibrationStates[1].LeftMotorSpeed = 40000;
                PollFeedback(manager, remote);
                Assert.True(owner.ForceFeedbackState.IsActive);
                Assert.False(remote.ForceFeedbackState.IsActive);
                Assert.Contains(sent, x => x.Left > 0);
                sent.Clear();

                SettingsManager.UserSettings.Items.RemoveAll(x => x.MapTo == 0);
                PollFeedback(manager, remote);
                Assert.True(owner.ForceFeedbackState.IsActive);
                Assert.Empty(sent);

                SettingsManager.UserSettings.Items.Clear();
                PollFeedback(manager, remote);
                Assert.False(owner.ForceFeedbackState.IsActive);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
                PollFeedback(manager, remote);
                Assert.Single(sent);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.IsPeerConnected = savedMembership;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
                RemoteLinkOutputRouter.ReleaseDevice(owner.DevicePath);
            }
        }

        [Fact]
        public void ARouteThatNeverSentVibrationDoesNotClaimTheOwnerBySendingZero()
        {
            string path = "peer://owner/unused-" + Guid.NewGuid().ToString("N");
            var saved = RemoteLinkOutputRouter.SendOutput;
            int sent = 0;
            RemoteLinkOutputRouter.SendOutput = (_, _, _) => sent++;
            RemoteLinkOutputRouter.Register(path, "owner", 0);
            try
            {
                Assert.False(RemoteLinkOutputRouter.StopVibration(path));
                Assert.Equal(0, sent);
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(1, 0)));
                Assert.True(RemoteLinkOutputRouter.StopVibration(path));
                Assert.Equal(2, sent);
                Assert.False(RemoteLinkOutputRouter.StopVibration(path));
            }
            finally
            {
                RemoteLinkOutputRouter.SendOutput = saved;
                RemoteLinkOutputRouter.Unregister(path);
            }
        }

        [Fact]
        public void AnIdentifyPulseOutlivesThePollOfAPeerRowWithNoSlot()
        {
            // The poll sends a peer row with no slot a stop whenever the
            // relay's record shows a level, so a pulse recorded there ended
            // about a millisecond after it went out.
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var peer = NewPeer(triggerMotors: false);
            var remote = new UserDevice();
            remote.LoadFromExternalDevice(peer);
            var frames = new List<Vibration>();
            RemoteLinkOutputRouter.Register(peer.DevicePath, "owner", 0);
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                frames.Add(effect.Vibration);
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var manager = new InputManager();
                Assert.True(RemoteLinkOutputRouter.ShipIdentify(peer.DevicePath, 65535, 65535));
                PollFeedback(manager, remote);
                PollFeedback(manager, remote);
                Assert.Equal(65535, Assert.Single(frames).LeftMotorSpeed);
                Assert.True(RemoteLinkOutputRouter.ShipIdentify(peer.DevicePath, 0, 0));
                PollFeedback(manager, remote);
                Assert.Equal(2, frames.Count);
                Assert.Equal(0, frames[1].LeftMotorSpeed);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
            }
        }

        [Fact]
        public void AnIdentifyStopLetsASlotWriterSendItsLevelAgain()
        {
            // A slot the peer row gained during the train sends its level, and
            // the train's stop then ends it at the owner. Kept, the record
            // would take the writer's next frame as already sent.
            string path = "peer://owner/identify-" + Guid.NewGuid().ToString("N");
            var saved = RemoteLinkOutputRouter.SendOutput;
            int sent = 0;
            RemoteLinkOutputRouter.SendOutput = (_, _, _) => sent++;
            RemoteLinkOutputRouter.Register(path, "owner", 0);
            try
            {
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(30000, 0)));
                Assert.True(RemoteLinkOutputRouter.ShipIdentify(path, 0, 0));
                Assert.True(RemoteLinkOutputRouter.ShipVibration(path, new Vibration(30000, 0)));
                Assert.Equal(3, sent);
            }
            finally
            {
                RemoteLinkOutputRouter.SendOutput = saved;
                RemoteLinkOutputRouter.Unregister(path);
            }
        }

        [Theory]
        [InlineData(false, "1", true)]
        [InlineData(true, "1", false)]
        [InlineData(false, "0", false)]
        public void TheConsumersTriggerFoldGoesIntoTheRelayedFrame(bool triggerMotors, string fold, bool folded)
        {
            // The owner replays a relayed frame with a neutral setting, so this
            // PC's Trigger Rumble Fold reaches a pad without trigger motors only
            // in the frame itself.
            var savedSettings = SettingsManager.UserSettings;
            var savedSend = RemoteLinkOutputRouter.SendOutput;
            var peer = NewPeer(triggerMotors);
            var remote = new UserDevice();
            remote.LoadFromExternalDevice(peer);
            var frames = new List<Vibration>();
            RemoteLinkOutputRouter.Register(peer.DevicePath, "owner", 0);
            RemoteLinkOutputRouter.SendOutput = (_, _, bytes) =>
            {
                Assert.True(OutputEffectCodec.TryDecode(bytes, out var effect));
                frames.Add(effect.Vibration);
            };
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var setting = new UserSetting { InstanceGuid = remote.InstanceGuid, MapTo = 0 };
                setting.SetPadSetting(new PadSetting { ForceOverall = "100", TriggerRumbleFold = fold });
                SettingsManager.UserSettings.Items.Add(setting);
                var manager = new InputManager();
                manager.VibrationStates[0].LeftTriggerMotorSpeed = 40000;
                PollFeedback(manager, remote);
                var frame = Assert.Single(frames);
                Assert.Equal(40000, frame.LeftTriggerMotorSpeed);
                Assert.Equal(folded ? 40000 : 0, frame.LeftMotorSpeed);
            }
            finally
            {
                SettingsManager.UserSettings = savedSettings;
                RemoteLinkOutputRouter.SendOutput = savedSend;
                RemoteLinkOutputRouter.Unregister(peer.DevicePath);
            }
        }

        private static RemotePeerDevice NewPeer(bool triggerMotors) => new(new RemotePeerDeviceInfo
        {
            PeerFingerprintHex = "owner", PeerLocalDeviceId = Guid.NewGuid().ToString("N"),
            HasRumble = true, HasRumbleTriggers = triggerMotors, VendorId = 0x1234, ProductId = 0x5678,
            NumAxes = 6, NumButtons = 17, NumHats = 1, InputDeviceType = InputDeviceType.Gamepad
        });

        private static void PollFeedback(InputManager manager, UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { device });
    }
}
