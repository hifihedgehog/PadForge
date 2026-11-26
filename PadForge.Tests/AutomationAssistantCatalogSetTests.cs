using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using PadForge.Common;
using PadForge.Search;
using PadForge.Services.Automation;
using PadForge.Services.Catalog;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The catalog's set on the real pages (#600): a number that is no
    /// number, an item its row no longer shows, a card and a dialog, a
    /// mapping row's option and an item's setting in its own card, each
    /// refused or written as a person's change would be.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class AutomationAssistantCatalogSetTests
    {
        private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

        private static Window Show(FrameworkElement page)
        {
            var window = new Window
            {
                Content = page, Width = 1456, Height = 985, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None,
            };
            window.Show();
            PageRenderDump.Settle(window.Dispatcher);
            return window;
        }

        /// <summary>NaN and Infinity read as numbers and pass both range
        /// tests, so they are refused before anything is written.</summary>
        [Fact]
        public void ANumberThatIsNotFinite_IsRefused_AndNothingChanges()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var number = p.Catalog.All().First(s => s.Takes == "number" && s.Maximum != null && s.Value != null);
                foreach (string word in new[] { "NaN", "Infinity", "-Infinity" })
                {
                    var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(number.Id, J(word)));
                    Assert.Equal("bad-argument", refusal.Code);
                    Assert.Equal(number.Value.Value.GetRawText(), p.Catalog.Read(number.Id).Place.Value.Value.GetRawText());
                }
            });
        }

        /// <summary>An item its row no longer shows refuses its write with
        /// InvalidOperationException (MouseSteeringCatalog): set says so in
        /// the item's words, as not available.</summary>
        [Fact]
        public void AnItemItsRowNoLongerShows_IsRefusedInItsWords()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                Func<IReadOnlyList<LiveItem>> provider = () => new[]
                {
                    new LiveItem("test/fh/gone", LiveKind.Setting, "Gone Setting", ReachClass.Free)
                    {
                        Read = () => "1",
                        Takes = "number",
                        Write = _ => throw new InvalidOperationException("This setting is not shown on the row now."),
                    },
                };
                LiveCatalog.AddProvider(provider);
                try
                {
                    var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set("test/fh/gone", J(5)));
                    Assert.Equal("not-available", refusal.Code);
                    Assert.Equal("This setting is not shown on the row now.", refusal.Words);
                }
                finally
                {
                    LiveCatalog.RemoveProvider(provider);
                }
            });
        }

        /// <summary>A card and a dialog are not set, and press does not
        /// press them either, so set's refusal names what does work.</summary>
        [Fact]
        public void ACardAndADialog_AreRefused_WithWordsThatDoNotSendTheProgramToPress()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var card = p.Catalog.Find(null, "card", 400).Places.First(c => c.Showing && c.Reach != "yours-alone");
                var onCard = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(card.Id, J(true)));
                Assert.Equal("not-available", onCard.Code);
                Assert.DoesNotContain("press presses it", onCard.Words);
                Assert.Equal("not-available", Assert.Throws<AssistantRefusal>(() => p.Catalog.PlanPress(card.Id)).Code);

                var owner = new Window { Width = 400, Height = 300, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None, AllowsTransparency = true, Content = new System.Windows.Controls.Grid() };
                owner.Show();
                var dialog = new SdlMappingDialog(SdlMappingDialog.Mode.Find, "Arcade Stick", "030000000d0f00009900000000000000", new[] { "Xbox #1" })
                {
                    Owner = owner, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                };
                var catalog = new AssistantCatalog(p.Service, new SearchNavigator(p.Service, p.PageOf, p.Vm, _ => { }, _ => { }, () => { }),
                    p.PageOf, p.Vm, pad => p.Pad.DataContext = pad, () => AssistantWindowPlace.InFront, new AssistantCatalogParts { MainWindow = () => owner });
                dialog.Show();
                try
                {
                    DialogRegistry.Register(dialog);
                    PageRenderDump.Settle(dialog.Dispatcher);
                    var open = Assert.Single(catalog.Dialogs().Open, d => d.Id.StartsWith("dialog.dialog/SdlMappingDialog/", StringComparison.Ordinal));
                    var onDialog = Assert.Throws<AssistantRefusal>(() => catalog.Set(open.Id, J(true)));
                    Assert.Equal("not-available", onDialog.Code);
                    Assert.DoesNotContain("press presses it", onDialog.Words);
                    Assert.Contains("read", onDialog.Words);
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            });
        }

        /// <summary>A mapping row's option, such as its Motion Deadzone, has
        /// an ID that names no row: set refuses it, where it wrote into
        /// the controller, which has no such setting, and said Set.</summary>
        [Fact]
        public void AMappingRowsOption_IsRefused()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var options = p.Service.Index.Where(e => e.RowOptions && e.Kind == SearchKind.Setting && e.Value?.Kind == SearchValueKind.Number).ToList();
                Assert.NotEmpty(options);
                foreach (var entry in options)
                {
                    string id = SearchIds.With(p.Service.IdOf(entry), 1, null, null);
                    var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(id, J(10)));
                    Assert.Equal("not-available", refusal.Code);
                    Assert.Equal(AssistantCatalog.RowOptionWords, refusal.Words);
                }
            });
        }

        /// <summary>A trigger's Max Range is an item's setting, written
        /// through the trigger's own card: 150 is refused with the card's
        /// range, where it was clamped to 100 and answered Set, and 80 is
        /// written.</summary>
        [Fact]
        public void AnItemsSetting_IsCheckedAndWrittenThroughItsOwnCard()
        {
            AutomationAssistantCatalogTests.WithPages(p =>
            {
                var window = Show(p.Pad);
                try
                {
                    p.Pad.OpenSection(PadViewModel.SticksSection);
                    PageRenderDump.Settle(window.Dispatcher);
                    var pad = p.Vm.Pads[0];
                    Assert.NotEmpty(pad.TriggerConfigs);
                    // The trigger card's Anti-Dead Zone: a slider of 0 to 100
                    // and a box, whose setter clamps what comes past them.
                    var entry = p.Service.Index.First(e => e.Kind == SearchKind.Setting && e.ItemsPath == "TriggerConfigs"
                                                           && e.Value?.Kind == SearchValueKind.Number && e.Value.Path == nameof(TriggerConfigItem.AntiDeadZone));
                    var place = p.Catalog.All().First(s => s.Id.StartsWith(p.Service.IdOf(entry) + "@", StringComparison.Ordinal));
                    var trigger = pad.TriggerConfigs[0];
                    double before = trigger.AntiDeadZone;
                    var refusal = Assert.Throws<AssistantRefusal>(() => p.Catalog.Set(place.Id, J(150)));
                    Assert.Equal("out-of-range", refusal.Code);
                    Assert.Equal(before, trigger.AntiDeadZone);
                    p.Catalog.Set(place.Id, J(20));
                    Assert.Equal(20, trigger.AntiDeadZone);
                    Assert.Equal(100, p.Catalog.Read(place.Id).Place.Maximum);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }
}
