using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Services.Automation;
using PadForge.Services.Automation.Web;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A web assistant's reach gets its control (#600 DESIGN-2 N3): the
    /// Allowed on the Web row gives the Reach list and, while it is held to
    /// its own devices, Pick Devices…, as a network key's row does. The
    /// choice is kept on the grant and works on its open connection at once.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebGrantReachTests
    {
        [Fact]
        public void TheAllowedRow_SetsAWebAssistantsReach_AndPickDevicesNamesTheDevices()
        {
            using var rig = new AssistantRig();
            var store = new WebAccessStore();
            using var door = new WebDoor(rig.Host, store, new WebTestLauncher(), () => @"C:\PadForge\PadForge.exe", rig.PipeName);
            var picked = new ProgramReach { Devices = new[] { new ReachDevice(Guid.NewGuid(), "Xbox Wireless Controller") } };
            var asked = new List<(string Name, ProgramReach Reach)>();
            var view = new WebAccessViewModel(door, store)
            {
                AskToTurnOn = () => true,
                PickReachDevices = (name, reach) => { asked.Add((name, reach)); return picked; },
            };
            view.IsOn = true;
            store.Add(new WebGrant { Id = "g1", Origin = "claude.ai", Name = "Claude", Allowed = DateTimeOffset.UtcNow });
            view.Refresh();

            var row = view.Allowed.Single();
            Assert.Equal(0, row.SelectedReach);
            Assert.False(row.IsLimited);
            Assert.Equal("Web_Reach_g1", row.ReachId);
            Assert.Equal("Web_Pick_g1", row.PickId);

            row.SelectedReach = 1;
            Assert.Equal(ProgramReach.OwnDevices, store.Grants.Single().Reach);
            view.Refresh();
            row = view.Allowed.Single();
            Assert.True(row.IsLimited);
            Assert.EndsWith("Drives its own devices alone. It still reads every device.", row.Line);

            row.PickCommand.Execute(null);
            Assert.Equal(("claude.ai", ProgramReach.OwnDevices), asked.Single());
            Assert.Equal(picked, store.Grants.Single().Reach);
            view.Refresh();
            Assert.EndsWith("Drives its own devices and Xbox Wireless Controller. It still reads every device.", view.Allowed.Single().Line);

            // Kept in PadForge.xml with the grant.
            var data = store.Save();
            Assert.Equal("Xbox Wireless Controller", data.Assistants.Single().Reach.Devices.Single().Name);

            view.Allowed.Single().SelectedReach = 0;
            Assert.Null(store.Grants.Single().Reach);
        }
    }
}
