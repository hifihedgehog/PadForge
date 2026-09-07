using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class MappedFeedbackOutputGateTests
    {
        [Fact]
        public async Task AContestedMappedWriteSkipsWithoutClearingPeerStateOrWaiting()
        {
            var saved = SettingsManager.UserSettings;
            var membership = RemoteLinkOutputRouter.IsPeerConnected;
            var web = new WebControllerDevice("mapped-gate-" + Guid.NewGuid().ToString("N"), "Browser pad");
            var device = new UserDevice();
            device.LoadFromWebDevice(web);
            var setting = new UserSetting { InstanceGuid = device.InstanceGuid, MapTo = 0 };
            setting.SetPadSetting(new PadSetting { ForceOverall = "100" });
            var sent = new List<(ushort, ushort)>();
            web.RumbleRequested += (l, r) => sent.Add((l, r));
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task holder = null;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserSettings.Items.Add(setting);
                RemoteLinkOutputRouter.IsPeerConnected = _ => true;
                device.ForceFeedbackState.SetDeviceForces(device, web, setting.GetPadSetting(), new Vibration(40000, 0));
                Assert.True(device.ForceFeedbackState.IsActive);
                sent.Clear();
                Assert.False(RemoteLinkOutputRouter.IsClaimedByPeer(device.DevicePath));
                holder = Task.Run(() =>
                {
                    lock (device.OutputSync)
                    {
                        entered.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                        Assert.True(RemoteLinkOutputRouter.ClaimOutput(device.DevicePath, "peer"));
                    }
                });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                var manager = new InputManager();
                await Task.Run(() => Run(manager, device)).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Empty(sent);
                Assert.True(device.ForceFeedbackState.IsActive);
                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(5));
                Run(manager, device);
                Assert.Empty(sent);
                Assert.True(RemoteLinkOutputRouter.PeerWroteLast(device.DevicePath));

                RemoteLinkOutputRouter.ReleaseDevice(device.DevicePath);
                Run(manager, device);
                Assert.False(device.ForceFeedbackState.IsActive);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);
            }
            finally
            {
                release.Set();
                if (holder != null) await holder.WaitAsync(TimeSpan.FromSeconds(5));
                SettingsManager.UserSettings = saved;
                RemoteLinkOutputRouter.IsPeerConnected = membership;
                RemoteLinkOutputRouter.ReleaseDevice(device.DevicePath);
            }
        }

        private static void Run(InputManager manager, UserDevice device)
            => typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(manager, new object[] { device });
    }
}
