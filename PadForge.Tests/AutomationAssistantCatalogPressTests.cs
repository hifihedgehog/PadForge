using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The catalog's press, its classes and its devices on the real pages
    /// (#600): the tray's Show is the user's alone, a press refused at the
    /// user's answer is refused before it is posted, a window with no class
    /// reads as the user's alone, two dialogs of one class each have their
    /// own ID, and a dialog's button and the tray belong to no device.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationAssistantCatalogPressTests
    {
        private static Window Owner()
        {
            var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Content = new Grid() };
            owner.Show();
            return owner;
        }

        /// <summary>A dialog owned by <paramref name="owner"/> with a Cancel
        /// button that counts its clicks, in the class given or none.</summary>
        private static Window Dialog(Window owner, string title, Action clicked, ReachClass? cls)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title + " text" });
            var cancel = new Button { Content = "Cancel" };
            cancel.Click += (_, _) => clicked();
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancel, "Dialog_Cancel");
            Reach.SetClass(cancel, ReachClass.Free);
            panel.Children.Add(cancel);
            var dialog = new Window { Title = title, Content = panel, Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, Width = 300, Height = 200, WindowStyle = WindowStyle.None, AllowsTransparency = true };
            if (cls is ReachClass c) Reach.SetClass(dialog, c);
            return dialog;
        }

        private static AssistantCatalog CatalogOver(AutomationAssistantCatalogTests.Pages p, Window owner, Action<string> pressTray = null)
            => new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront,
                new AssistantCatalogParts { MainWindow = () => owner, PressTray = pressTray });

        /// <summary>The tray's Show brings PadForge's window forward, which
        /// is the user's alone (#438): its entry and the tray menu's own item
        /// carry that class, so find and read say so.</summary>
        [Fact]
        public void TheTraysShow_IsTheUsersAlone()
        {
            Assert.Equal(ReachClass.UsersOwn, TrayMenu.Items.Single(i => i.Key == "Show").Reach);
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                // The tray's IDs are made from their words' keys, as
                // window.menuitem/Tray/Main_ShowPadForge.
                string id = p.Catalog.Find(null, "menu_item", 2000).Places
                    .Single(m => m.Id.StartsWith("window.menuitem/Tray/", StringComparison.Ordinal) && m.Id.EndsWith("Main_ShowPadForge", StringComparison.Ordinal)).Id;
                var show = p.Catalog.Read(id).Place;
                Assert.Equal("yours-alone", show.Reach);
                Assert.Equal("users-own", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(id)).Code);
            });
        }

        /// <summary>A press planned on a menu item checks again what would
        /// refuse it, before it is posted: a controller's Paste whose command
        /// is turned off by the time the user answers is refused then, where
        /// the posted press failed unseen and the program was told
        /// Pressed.</summary>
        [Fact]
        public void APressRefusedAtTheAnswer_IsRefusedBeforeItIsPosted()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var window = new Window
                {
                    Content = p.Pad, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                    ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                };
                window.Show();
                var readClipboard = AssistantCatalog.ReadClipboard;
                try
                {
                    PageRenderDump.Settle(window.Dispatcher);
                    AssistantCatalog.ReadClipboard = () => new PadForge.Engine.Data.PadSetting().ToJson(PadForge.Engine.VirtualControllerType.Xbox);
                    var paste = p.Catalog.Find(null, "menu_item", 400).Places.First(m => m.Id.StartsWith("pad.menuitem/PadMoreButton/Common_Paste", StringComparison.Ordinal));
                    var plan = p.Catalog.PlanPress(paste.Id);
                    Assert.NotNull(plan.Check);
                    plan.Check();
                    // The controller goes away while the card waits.
                    SettingsManager.SlotCreated[0] = false;
                    Assert.False(p.Vm.Pads[0].PasteSettingsCommand.CanExecute(null));
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => plan.Check()).Code);
                    SettingsManager.SlotCreated[0] = true;
                    // An action's press checks too.
                    var action = p.Catalog.Find(null, "action", 2000).Places.First(a => a.Showing && a.Enabled && a.Reach == "free");
                    var pressed = p.Catalog.PlanPress(action.Id);
                    Assert.NotNull(pressed.Check);
                    pressed.Check();
                }
                finally
                {
                    AssistantCatalog.ReadClipboard = readClipboard;
                    window.Close();
                }
            });
        }

        /// <summary>A window PadForge shows that carries no class reads as
        /// the user's alone in read as it does in where.</summary>
        [Fact]
        public void AWindowWithNoClass_ReadsAsTheUsersAlone_AsWhereSays()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var owner = Owner();
                var dialog = Dialog(owner, "Plain", () => { }, null);
                var catalog = CatalogOver(p, owner);
                dialog.Show();
                try
                {
                    DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    var open = Assert.Single(catalog.Dialogs().Open);
                    Assert.Equal("yours-alone", open.Reach);
                    var read = catalog.Read(open.Id);
                    Assert.Equal("yours-alone", read.Place.Reach);
                    Assert.Empty(read.Lines);
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>Two dialogs of one class open at once each have an ID of
        /// their own, the first its plain one, and a press of the second's
        /// button reaches the second.</summary>
        [Fact]
        public void TwoDialogsOfOneClass_EachHaveTheirOwnId_AndAPressReachesItsOwn()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var owner = Owner();
                int first = 0, second = 0;
                var one = Dialog(owner, "First", () => first++, ReachClass.Free);
                var two = Dialog(owner, "Second", () => second++, ReachClass.Free);
                var catalog = CatalogOver(p, owner);
                one.Show();
                two.Show();
                try
                {
                    DialogRegistry.Register(one);
                    DialogRegistry.Register(two);
                    PageRenderDump.Settle(one.Dispatcher);
                    var open = catalog.Dialogs().Open;
                    Assert.Equal(2, open.Count);
                    Assert.Equal(2, open.Select(d => d.Id).Distinct().Count());
                    Assert.Equal("open/Window", open.Single(d => d.Title == "First").Id);
                    string secondId = open.Single(d => d.Title == "Second").Id;
                    var read = catalog.Read(secondId);
                    Assert.Contains("Second text", read.Lines);
                    var button = Assert.Single(read.Buttons);
                    Assert.StartsWith(secondId + "#", button.Id);
                    catalog.PlanPress(button.Id).Press();
                    PageRenderDump.Settle(two.Dispatcher);
                    Assert.Equal((0, 1), (first, second));
                    // The ID stays with its window.
                    Assert.Equal(secondId, catalog.Dialogs().Open.Single(d => d.Title == "Second").Id);
                }
                finally
                {
                    two.Close();
                    one.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>An open dialog's button belongs to no device, so a
        /// program held to its devices is told its reach, never that the
        /// button it just read has no ID. A Devices-page entry belongs to
        /// the device its ID names.</summary>
        [Fact]
        public void ADialogsButton_BelongsToNoDevice()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var owner = Owner();
                var dialog = Dialog(owner, "Plain", () => { }, ReachClass.Free);
                var catalog = CatalogOver(p, owner);
                dialog.Show();
                try
                {
                    DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    string button = catalog.Read("open/Window").Buttons.Single().Id;
                    Assert.Null(catalog.DeviceHolding(button));
                    var row = p.Vm.Devices.SelectedDevice;
                    var setting = catalog.All().First(s => s.Id.StartsWith("devices", StringComparison.Ordinal) && s.Id.Contains("@d", StringComparison.Ordinal));
                    Assert.Equal(row.InstanceGuid, catalog.DeviceHolding(setting.Id));
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>A dialog's button checks again, before its press is
        /// posted, that it still takes a press: one turned off while the
        /// card waited is refused then, as a page's button is.</summary>
        [Fact]
        public void ADialogsButtonTurnedOffAtTheAnswer_IsRefusedBeforeItIsPosted()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var owner = Owner();
                int clicks = 0;
                var dialog = Dialog(owner, "Plain", () => clicks++, ReachClass.Free);
                var catalog = CatalogOver(p, owner);
                dialog.Show();
                try
                {
                    DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    string id = catalog.Read("open/Window").Buttons.Single().Id;
                    var plan = catalog.PlanPress(id);
                    Assert.NotNull(plan.Check);
                    plan.Check();
                    var cancel = SearchLocator.LogicalDescendants(dialog).OfType<Button>().Single();
                    cancel.IsEnabled = false;
                    Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => plan.Check()).Code);
                    Assert.Equal(0, clicks);
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }
    }
}
