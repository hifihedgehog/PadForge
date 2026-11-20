using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PadForge.Engine;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A ComboBox finds its pick in a list made again by Equals. Measured
    /// 2026-11-20: a list of new items equal only to themselves makes the box
    /// write null through SelectedValue and show nothing, even when every
    /// item carries the same value. The mode list of a mapping source is made
    /// again for each language, and its Kind setter turns null into Direct,
    /// so a language switch set every open row's mode to Direct. The SOCD and
    /// keep-awake lists are made on every read, and those boxes went blank
    /// after a profile switch. Every picker item is equal by its key now.
    /// </summary>
    public class PickerItemEqualityTests
    {
        [Fact]
        public void ALanguageSwitch_KeepsASourcesMode()
        {
            RunSta(() =>
            {
                var before = CultureInfo.CurrentUICulture;
                var source = new MappingSourceItem { Kind = "Toggle" };
                var combo = new ComboBox { DataContext = source, SelectedValuePath = "Value" };
                combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MappingSourceItem.KindChoices)));
                combo.SetBinding(Selector.SelectedValueProperty,
                    new Binding(nameof(MappingSourceItem.Kind)) { Mode = BindingMode.TwoWay });
                var window = new Window
                {
                    Content = combo, Width = 200, Height = 60, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
                };
                try
                {
                    window.Show();
                    Pump(window);
                    Assert.Equal("Toggle", combo.SelectedValue);

                    CultureInfo.CurrentUICulture = new CultureInfo(before.Name == "de-DE" ? "fr-FR" : "de-DE");
                    source.RefreshCulture();
                    Pump(window);

                    Assert.Equal("Toggle", source.Kind);
                    Assert.Equal("Toggle", combo.SelectedValue);
                }
                finally
                {
                    CultureInfo.CurrentUICulture = before;
                    window.Close();
                }
            });
        }

        [Fact]
        public void EveryPickerItem_IsEqualByItsKey()
        {
            Assert.Equal(new MappingSourceItem.KindChoice { Value = "Toggle", Name = "A" },
                new MappingSourceItem.KindChoice { Value = "Toggle", Name = "B" });
            Assert.NotEqual(new MappingSourceItem.KindChoice { Value = "Toggle" }, new MappingSourceItem.KindChoice { Value = "Direct" });
            Assert.Equal(new MappingItem.CombineModeOption { Value = "MaxAbs", Name = "A" }, new MappingItem.CombineModeOption { Value = "MaxAbs", Name = "B" });
            Assert.Equal(new SocdModeOption { Value = "LastWins", Name = "A" }, new SocdModeOption { Value = "LastWins", Name = "B" });
            Assert.Equal(new KbmSurfaceOption { Value = "Both", Name = "A" }, new KbmSurfaceOption { Value = "Both", Name = "B" });
            Assert.Equal(new SocdKeyOption { Vk = 0x41, Label = "A" }, new SocdKeyOption { Vk = 0x41, Label = "B" });
            Assert.Equal(new MacroTypeChoice { Type = MacroActionType.ButtonPress, Label = "A" },
                new MacroTypeChoice { Type = MacroActionType.ButtonPress, Label = "B" });
            Assert.Equal(new MacroOutputChannelOption { Value = MacroOutputChannel.A, Name = "A" },
                new MacroOutputChannelOption { Value = MacroOutputChannel.A, Name = "B" });
            Assert.Equal(new KeyBackgroundChoice { Rule = PadForge.Common.Input.Peripherals.KeyBackgroundRule.Off, Label = "A" }, new KeyBackgroundChoice { Rule = PadForge.Common.Input.Peripherals.KeyBackgroundRule.Off, Label = "B" });
            Assert.Equal(new GyroLabeledOption(() => "A", "Player"), new GyroLabeledOption(() => "B", "Player"));
            Assert.Equal(new MappingSourceItem.KindChoice { Value = "Toggle" }.GetHashCode(), new MappingSourceItem.KindChoice { Value = "Toggle" }.GetHashCode());
        }

        private static void Pump(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(20000), "STA test body timed out");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
