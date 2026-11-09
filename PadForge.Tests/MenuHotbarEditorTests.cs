using System.Linq;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The Menus tab's Hotbar style: the four controls and three switches show
    /// for a hotbar only, Click Input and stay-open do not, the stick dead zone
    /// takes Steam's hotbar default, the fire modes read as a hotbar runs them,
    /// and a control records a button, refuses an axis and resets to the
    /// opener's direction.
    /// </summary>
    public class MenuHotbarEditorTests
    {
        private static MenuEditorItem Ring()
            => new(new MenuDefinitionEntry { MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = true,
                HostDescriptor = "Gamepad DPad" });

        [Fact]
        public void TurningAMenuIntoAHotbar_ShowsItsRows_AndTakesItsDeadZone()
        {
            var e = Ring();
            Assert.False(e.IsHotbar);
            e.KindIndex = 3;
            Assert.Equal(MenuKind.Hotbar, e.Entry.Kind);
            Assert.True(e.IsHotbar);
            Assert.True(e.IsCellMenu);
            Assert.False(e.ShowsClickRow);
            Assert.False(e.Entry.HasCenter);
            Assert.Equal(MenuHotbar.SteamDeadzonePercent, e.Entry.EngageDeadzonePercent);
            Assert.Equal(Strings.Instance.Menu_Fire_Click_Hotbar_Desc, e.SelectedFireDescription);
            Assert.Equal(new[] { 0, 1, 2, 3 }, e.FireOptions.Select(o => o.Value));
            Assert.Equal(Strings.Instance.Menu_Fire_Always_Hotbar_Desc, e.FireOptions[3].Description);

            // And back: the ring's dead zone and fire descriptions return.
            e.KindIndex = 0;
            Assert.Equal(25, e.Entry.EngageDeadzonePercent);
            Assert.Equal(Strings.Instance.Menu_Fire_Click_Desc, e.SelectedFireDescription);
            Assert.True(e.ShowsClickRow);
        }

        [Fact]
        public void AHotbar_HasNoStayOpenRow_EvenOnALayer()
        {
            var e = Ring();
            e.Entry.LayerMask = "L1";
            e.KindIndex = 3;
            Assert.False(e.ShowLayerHold);
            Assert.False(e.EffectiveLayerHoldsOpen);
        }

        [Fact]
        public void AControl_RecordsAButton_RefusesAnAxis_AndResetsToTheOpener()
        {
            var e = Ring();
            e.KindIndex = 3;
            Assert.Equal(Strings.Instance.Menu_ClickDefault, e.HotbarFireSubtitle);
            Assert.True(e.TryApplyRecorded(MenuEditorItem.MenuRecordTarget.HotbarFire, "Button 5"));
            Assert.Equal("Button 5", e.Entry.HotbarFireDescriptor);
            Assert.Equal("Button 5", e.HotbarFireSelected);
            Assert.False(e.TryApplyRecorded(MenuEditorItem.MenuRecordTarget.HotbarShow, "Axis 2"));
            Assert.Equal("", e.Entry.HotbarShowDescriptor);
            e.ResetHotbarFireCommand.Execute(null);
            Assert.Equal("", e.Entry.HotbarFireDescriptor);
            Assert.Equal("Gamepad DPadUp", MenuHotbar.ControlDescriptor(e.Entry, HotbarControl.Fire));

            e.HotbarNextSelected = "Button 7";
            Assert.Equal("Button 7", e.Entry.HotbarNextDescriptor);
        }

        [Fact]
        public void ACustomOpener_HasNoDirections_SoAnEmptyControlReadsNotSet()
        {
            var e = Ring();
            e.KindIndex = 3;
            e.Entry.HostDescriptor = "Custom";
            Assert.Equal(Strings.Instance.Menu_NotRecorded, e.HotbarShowSubtitle);
            Assert.False(e.ShowsCustomAxesRows);
        }

        [Fact]
        public void TheSwitches_WriteThrough_AndResetToSteamsDefaults()
        {
            var e = Ring();
            e.KindIndex = 3;
            e.HotbarWrap = true;
            e.HotbarDismissOnFire = false;
            e.HotbarRecenter = false;
            Assert.True(e.Entry.HotbarWrap);
            Assert.False(e.Entry.HotbarDismissOnFire);
            Assert.False(e.Entry.HotbarRecenter);
            foreach (var name in new[] { nameof(MenuEditorItem.HotbarWrap), nameof(MenuEditorItem.HotbarDismissOnFire),
                         nameof(MenuEditorItem.HotbarRecenter) })
                e.ResetSettingCommand.Execute(name);
            Assert.False(e.Entry.HotbarWrap);
            Assert.True(e.Entry.HotbarDismissOnFire);
            Assert.True(e.Entry.HotbarRecenter);
        }
    }
}
