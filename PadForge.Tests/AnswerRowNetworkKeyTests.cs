using System;
using System.Linq;
using PadForge.Services;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Answer From Another Device follows the network keys (#600 items 53
    /// and 54): its row shows while a key exists with no web grant, and goes
    /// when the last key is revoked. Making or revoking a key raises the
    /// store's Changed, the refresh the window gives the row and its
    /// listener for a web grant.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class AnswerRowNetworkKeyTests
    {
        [Fact]
        public void TheRow_ShowsWithANetworkKeyAndNoWebGrant_AndGoesWhenTheLastIsRevoked()
        {
            var store = WebAccessStore.Shared;
            Assert.Empty(store.Grants);
            Assert.Empty(store.Keys);
            using var rig = new AssistantRig();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName,
                network: new NetworkDoor(_ => string.Empty));
            int changed = 0;
            void Count(object s, EventArgs e) => changed++;
            store.Changed += Count;
            try
            {
                var answers = new WebAnswer();
                var view = new AnswerAccessViewModel(answers);
                Assert.False(view.ShowsRow);

                Assert.NotNull(door.AddNetworkKey("Office PC", AutomationLevel.Read));
                Assert.Equal(1, changed);
                view.Refresh();
                Assert.True(view.ShowsRow);
                Assert.Empty(store.Grants);

                Assert.NotNull(door.AddNetworkKey("Laptop", AutomationLevel.Read));
                door.RevokeNetworkKey(store.Keys.First().Id);
                view.Refresh();
                Assert.True(view.ShowsRow);
                int before = changed;
                door.RevokeNetworkKey(store.Keys.Single().Id);
                Assert.Equal(before + 1, changed);
                view.Refresh();
                Assert.False(view.ShowsRow);
                Assert.False(answers.Wanted);
            }
            finally
            {
                store.Changed -= Count;
                foreach (var key in store.Keys) store.RevokeKey(key.Id);
            }
        }
    }
}
