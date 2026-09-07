using System;
using System.Collections;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public class MenuOwnerCleanupTests
    {
        private static readonly FieldInfo Drivers = typeof(InputManager).GetField("_menuDrivers", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo PurgeStamp = typeof(InputManager).GetField("_menuCtxLastPurgeMs", BindingFlags.Instance | BindingFlags.NonPublic);

        [Fact]
        public void PeriodicPurgeRetiresRemovedOwnersAndKeepsTheRestingDriver()
        {
            using var f = new MenuAuditFixture(MenuFireType.Always);
            var second = f.AddDevice();
            f.State.Buttons[0] = true;
            for (int id = 2; id <= 12; id++)
            {
                var menu = f.Menu.Clone();
                menu.MenuId = id;
                f.Set.Menus.Add(menu);
            }
            f.Frame();
            MenuAuditFixture.Deflect(second.InputState);
            f.Frame();
            MenuAuditFixture.Center(second.InputState);
            f.Frame();
            f.Frame();
            var drivers = (IDictionary)Drivers.GetValue(f.Manager);
            Assert.Equal(12, drivers.Count);
            Assert.Equal(second.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);

            // A definition replacement can retire owners without the editor callback.
            f.Set.Menus.RemoveAll(m => m.MenuId != 1);
            PurgeStamp.SetValue(f.Manager, Environment.TickCount64 - 6000);
            f.Frame();

            Assert.Single(f.Manager.MenuContexts.Keys, k => k.MenuId == 1 && k.Device == second.InstanceGuid);
            Assert.All(f.Manager.MenuContexts.Keys, k => Assert.Equal(1, k.MenuId));
            Assert.Single(drivers);
            Assert.Equal(second.InstanceGuid, f.Manager.ActiveMenuOverlay.Device);
            Assert.Equal(0, f.Manager.ActiveMenuOverlay.HoveredIndex);
            Assert.True(f.Fired(0));
        }
    }
}
