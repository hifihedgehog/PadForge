using System;
using System.Collections.Generic;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Data;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Issue #415 (discussion #402): the feedback pass runs against a web pad
    /// that has left its last slot. It swaps the shared UserSettings, so it
    /// rides the statics collection like every other test that does.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class BrowserGamepadFeedbackPassTests
    {
        private static void RunFeedbackPass(InputManager im, UserDevice ud)
        {
            var m = typeof(InputManager).GetMethod("ApplyForceFeedback", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);
            m.Invoke(im, new object[] { ud });
        }

        private static (UserDevice ud, WebControllerDevice web, List<(ushort, ushort)> sent) Rumbling(string key)
        {
            var web = new WebControllerDevice(key, "Browser Gamepad 1", false, "gamepad");
            var sent = new List<(ushort, ushort)>();
            web.RumbleRequested += (l, h) => sent.Add((l, h));
            var ud = new UserDevice();
            ud.LoadFromWebDevice(web);
            Assert.NotNull(ud.ForceFeedbackState);
            ud.ForceFeedbackState.SetDeviceForces(ud, web, new PadSetting(), new Vibration(65535, 0));
            Assert.True(ud.ForceFeedbackState.IsActive);
            Assert.Contains(sent, s => s.Item1 > 0);
            sent.Clear();
            return (ud, web, sent);
        }

        [Fact]
        public void AWebPadWithNoSlots_IsStoppedOnce_AndNotAgain()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();    // no assignments at all
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                var im = new InputManager();
                RunFeedbackPass(im, ud);
                Assert.Equal(new[] { ((ushort)0, (ushort)0) }, sent);         // exactly one zero
                Assert.False(ud.ForceFeedbackState.IsActive);
                RunFeedbackPass(im, ud);
                Assert.Single(sent);                                        // and never a second
            }
            finally { SettingsManager.UserSettings = saved; }
        }

        [Fact]
        public void AWebPadARemotePeerIsDriving_KeepsItsRumble()
        {
            var saved = SettingsManager.UserSettings;
            try
            {
                SettingsManager.UserSettings = new SettingsCollection();
                var (ud, _, sent) = Rumbling("fb-" + Guid.NewGuid().ToString("N"));
                RemoteLinkOutputRouter.ClaimOutput(ud.DevicePath);          // a relayed frame holds the lease
                RunFeedbackPass(new InputManager(), ud);
                Assert.Empty(sent);
                Assert.True(ud.ForceFeedbackState.IsActive);
            }
            finally { SettingsManager.UserSettings = saved; }
        }
    }
}
