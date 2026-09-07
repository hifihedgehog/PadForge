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

        private static void PollFeedback(InputManager manager, UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { device });
    }
}
