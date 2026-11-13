using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// AccessibleNames on live controls: each rule runs on elements in a shown
    /// window, and each name is read back through the element's automation
    /// peer, as a screen reader reads it. The rules are called directly, so
    /// no class handler is registered for the rest of the test process.
    /// Strings are read at run time, so the culture tests run apart.
    /// </summary>
    [Collection("CultureSwitching")]
    public class AccessibleNamesTests
    {
        // A Segoe MDL2 glyph (Cancel), built from its code so the source
        // stays ASCII.
        private static readonly string Glyph = ((char)0xE711).ToString();

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        /// <summary>Shows content in a window off screen that never takes
        /// focus, lays it out, runs the body, and closes the window.</summary>
        private static void Shown(FrameworkElement content, Action body)
        {
            var window = new Window
            {
                Width = 400, Height = 600, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = content,
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                body();
            }
            finally
            {
                window.Close();
            }
        }

        private static string NameOf(UIElement element)
            => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

        private static StackPanel Panel(params UIElement[] children)
        {
            var panel = new StackPanel();
            foreach (var child in children)
                panel.Children.Add(child);
            return panel;
        }

        // A button type of this test's own, so the class handlers below reach
        // no other control in the test process.
        private sealed class ProbeButton : Button { }

        private static int s_probeLoaded;
        private static int s_probeSized;
        private static readonly bool s_probeHandlers = RegisterProbeHandlers();

        private static bool RegisterProbeHandlers()
        {
            EventManager.RegisterClassHandler(typeof(ProbeButton), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, e) => Interlocked.Increment(ref s_probeLoaded)));
            EventManager.RegisterClassHandler(typeof(ProbeButton), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler((s, e) => Interlocked.Increment(ref s_probeSized)));
            return true;
        }

        [Fact]
        public void ASizeChangedClassHandler_ReachesAPlainButton_WhereALoadedOneDoesNot() => RunSta(() =>
        {
            Assert.True(s_probeHandlers);
            int loaded = s_probeLoaded, sized = s_probeSized;
            Shown(Panel(new ProbeButton { Content = "Probe" }), () => { });
            // WPF sends Loaded only to an element with a Loaded handler of its
            // own (FrameworkElement.ThisHasLoadedChangeEventHandler), so
            // AccessibleNames.Start names controls on their first SizeChanged.
            Assert.Equal(loaded, s_probeLoaded);
            Assert.True(s_probeSized > sized, "SizeChanged never reached the button");
        });

        [Fact]
        public void AButtonThatShowsNoText_IsNamedByItsStringTooltip_AndTextItShowsStaysItsName() => RunSta(() =>
        {
            var glyph = new Button { Content = new TextBlock { Text = Glyph }, ToolTip = "Delete" };
            var text = new Button { Content = "Close", ToolTip = "Closes the dialog" };
            var row = Panel(new TextBlock { Text = Glyph }, new TextBlock { Text = "Record" });
            row.Orientation = Orientation.Horizontal;
            var panel = new Button { Content = row, ToolTip = "Records a gesture" };
            var element = new Button { Content = new TextBlock { Text = Glyph }, ToolTip = new ToolTip { Content = "Tip" } };
            var own = new Button { Content = new TextBlock { Text = Glyph }, ToolTip = "Tip" };
            AutomationProperties.SetName(own, "Own");
            // BidiText.ToolTip draws its string as a TextBlock tooltip.
            var bidi = new Button { Content = new TextBlock { Text = Glyph } };
            BidiText.SetToolTip(bidi, "Bidi tip");
            var buttons = new[] { glyph, text, panel, element, own, bidi };
            Shown(Panel(buttons), () =>
            {
                foreach (var b in buttons)
                    AccessibleNames.NameButton(b);
                Assert.Equal("Delete", NameOf(glyph));
                Assert.Equal("Close", NameOf(text));
                Assert.Equal("Record", NameOf(panel));
                // A ToolTip control is not read: the button keeps WPF's own
                // name, and XAML names such a button itself.
                Assert.Equal(Glyph, NameOf(element));
                Assert.Equal("Own", NameOf(own));
                Assert.IsType<TextBlock>(bidi.ToolTip);
                Assert.Equal("Bidi tip", NameOf(bidi));
                BidiText.SetToolTip(bidi, "Changed");
                Assert.Equal("Changed", NameOf(bidi));

                glyph.ToolTip = "Remove";
                Assert.Equal("Remove", NameOf(glyph));
            });
        });

        private sealed class RecordState : INotifyPropertyChanged
        {
            private string _text = "Record";

            public string Text
            {
                get => _text;
                set
                {
                    _text = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        [Fact]
        public void AButtonThatShowsNoText_IsNamedByTheTextItsTooltipTextBlockShows() => RunSta(() =>
        {
            // The record buttons: a glyph that turns to Stop while recording,
            // and a TextBlock tooltip bound to the row's "Record" or
            // "Recording".
            var state = new RecordState();
            var tip = new TextBlock();
            tip.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecordState.Text)));
            var bound = new Button { Content = new TextBlock { Text = Glyph }, ToolTip = tip, DataContext = state };
            var literal = new Button { Content = new TextBlock { Text = Glyph }, ToolTip = new TextBlock { Text = "Clear" } };
            var shows = new Button { Content = "Close", ToolTip = new TextBlock { Text = "Closes the dialog" } };
            var buttons = new[] { bound, literal, shows };
            Shown(Panel(buttons), () =>
            {
                // The tooltip's own binding reads nothing until the tooltip
                // opens.
                Assert.Equal(string.Empty, tip.Text);
                foreach (var b in buttons)
                    AccessibleNames.NameButton(b);
                Assert.Equal("Record", NameOf(bound));
                state.Text = "Recording";
                Assert.Equal("Recording", NameOf(bound));
                Assert.Equal("Clear", NameOf(literal));
                Assert.Equal("Close", NameOf(shows));
            });
        });

        [Fact]
        public void AnInput_IsNamedByTheLabelBesideIt_ThenItsTooltip_ThenItsPlaceholder() => RunSta(() =>
        {
            var label = new TextBlock();
            BindingOperations.SetBinding(label, TextBlock.TextProperty,
                new Binding(nameof(Strings.Common_Close)) { Source = Strings.Instance });
            var slider = new Slider();
            var labeled = Panel(label, slider);
            labeled.Orientation = Orientation.Horizontal;

            // A DockPanel lists a right-docked reset button ahead of its input.
            var reset = new SettingResetButton { SettingLabel = "Name" };
            DockPanel.SetDock(reset, Dock.Right);
            var docked = new DockPanel();
            var named = new TextBox();
            docked.Children.Add(reset);
            docked.Children.Add(named);

            // A letter written in the XAML labels a color's sliders, and a
            // dash passes the label on rather than labeling anything.
            var red = new Slider();
            var redBox = new TextBox();
            var span = new TextBox();
            var rgb = Panel(new TextBlock { Text = "R" }, red, redBox, new TextBlock { Text = "-" }, span);
            rgb.Orientation = Orientation.Horizontal;

            // A label above the row a slider and its box sit in.
            var low = new Slider();
            var lowBox = new TextBox();
            var lowRow = Panel(low, lowBox);
            lowRow.Orientation = Orientation.Horizontal;
            var range = Panel(new TextBlock { Text = "Range" }, lowRow);

            // The row's reset button names the setting ahead of a short label,
            // for the slider and its value box alike.
            var portLabel = new TextBlock { Text = "Port" };
            var port = new Slider();
            var portBox = new TextBox();
            var portRow = Panel(portLabel, port, portBox, new SettingResetButton { SettingLabel = "Web Controller Port" });
            portRow.Orientation = Orientation.Horizontal;

            // Hz is the frequency box's unit, not the gain slider's label.
            var gain = new Slider { ToolTip = "Gain" };
            var units = Panel(new TextBox(), new TextBlock { Text = "Hz" }, gain);
            units.Orientation = Orientation.Horizontal;

            // The next box follows rows that hold inputs of their own, which
            // label nothing past themselves.
            var tipped = new TextBox { ToolTip = "Port number" };
            var bidiTipped = new TextBox();
            BidiText.SetToolTip(bidiTipped, "Key combination");
            var placeholder = new Wpf.Ui.Controls.TextBox { PlaceholderText = "Search" };
            var bare = new ComboBox();
            Shown(Panel(labeled, docked, rgb, range, portRow, units, tipped, bidiTipped, placeholder, bare), () =>
            {
                foreach (var input in new FrameworkElement[] { slider, named, red, redBox, span, low, lowBox, port, portBox, gain, tipped, bidiTipped, placeholder, bare })
                    AccessibleNames.NameInput(input);
                Assert.Equal("Web Controller Port", NameOf(port));
                Assert.Equal("Web Controller Port", NameOf(portBox));
                Assert.Equal("Key combination", NameOf(bidiTipped));
                Assert.Equal("Gain", NameOf(gain));
                Assert.Same(label, AutomationProperties.GetLabeledBy(slider));
                Assert.Equal(Strings.Instance.Common_Close, NameOf(slider));
                Assert.Equal("Name", NameOf(named));
                Assert.Equal("R", NameOf(red));
                Assert.Equal("R", NameOf(redBox));
                Assert.Equal("R", NameOf(span));
                Assert.Equal("Range", NameOf(low));
                Assert.Equal("Range", NameOf(lowBox));
                Assert.Equal("Port number", NameOf(tipped));
                Assert.Equal("Search", NameOf(placeholder));
                Assert.Equal(string.Empty, NameOf(bare));
            });
        });

        private static TextBlock Bound(string key)
        {
            var text = new TextBlock();
            BindingOperations.SetBinding(text, TextBlock.TextProperty, new Binding(key) { Source = Strings.Instance });
            return text;
        }

        /// <summary>A row of the controller page holds more than one box: a
        /// value and the same value as the device reports it, or the two ends
        /// of a range. Each box reads the row's label, and what tells it from
        /// the box before it: the word between two ends (to), and its own
        /// tooltip (Raw axis value). With the label alone, a screen reader
        /// read two boxes of a row by one name, and with the tooltip alone
        /// it read every raw box of a card by one name (#574).</summary>
        [Fact]
        public void TheBoxesOfOneRow_EachReadTheRowsLabel_AndWhatSetsThemApart() => RunSta(() =>
        {
            string label = Strings.Instance.Common_Close, to = Strings.Instance.Common_To;

            // A value, its unit, then the raw value: Center Offset X on the
            // controller page.
            var slider = new Slider();
            var value = new TextBox();
            var raw = new TextBox { ToolTip = "Raw axis value" };
            var offset = Panel(Bound(nameof(Strings.Common_Close)), slider, value, new TextBlock { Text = "%" }, raw, new Button { Content = "Reset" });
            offset.Orientation = Orientation.Horizontal;

            // A range: its two ends, their unit, then the two raw ends.
            var low = new TextBox();
            var high = new TextBox();
            var rawLow = new TextBox { ToolTip = "Raw axis value" };
            var rawHigh = new TextBox { ToolTip = "Raw axis value" };
            var range = Panel(Bound(nameof(Strings.Common_Close)), new Slider(), low, Bound(nameof(Strings.Common_To)), high,
                new TextBlock { Text = "%" }, rawLow, Bound(nameof(Strings.Common_To)), rawHigh);
            range.Orientation = Orientation.Horizontal;

            // A box with a tooltip and a row of its own keeps the label alone.
            var alone = new TextBox { ToolTip = "A hint about the value" };
            var single = Panel(Bound(nameof(Strings.Common_Close)), alone);
            single.Orientation = Orientation.Horizontal;

            Shown(Panel(offset, range, single), () =>
            {
                foreach (var input in new FrameworkElement[] { slider, value, raw, low, high, rawLow, rawHigh, alone })
                    AccessibleNames.NameInput(input);
                Assert.Equal(label, NameOf(slider));
                Assert.Equal(label, NameOf(value));
                Assert.Equal(label + ", Raw axis value", NameOf(raw));

                Assert.Equal(label, NameOf(low));
                Assert.Equal(label + ", " + to, NameOf(high));
                Assert.Equal(label + ", Raw axis value", NameOf(rawLow));
                Assert.Equal(label + ", " + to + ", Raw axis value", NameOf(rawHigh));

                Assert.Equal(label, NameOf(alone));

                // No two boxes of a row share a name.
                Assert.Equal(4, new[] { low, high, rawLow, rawHigh }.Select(NameOf).Distinct().Count());
            });
        });

        private sealed class Plain
        {
            public string Title { get; set; }
        }

        private sealed class Named
        {
            public override string ToString() => "Named item";
        }

        private enum Side { Left, Right }

        private static DataTemplate Template(string body)
            => (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" + body + "</DataTemplate>");

        private static string[] ItemNames(ItemsControl list)
            => UIElementAutomationPeer.CreatePeerForElement(list).GetChildren().Select(p => p.GetName()).ToArray();

        [Fact]
        public void ASlidersTrackButtons_AreNamedWithTheSlider_EvenOneThatNeverTakesASize() => RunSta(() =>
        {
            // PadForge's slider template (App.xaml EmberSlider): the increase
            // button draws nothing, and wpf-ui's implicit RepeatButton style
            // aligns it left and center (Button.xaml DefaultRepeatButtonStyle),
            // so it never takes a size and has no first size to be named on.
            var slider = (Slider)XamlReader.Parse(
                "<Slider xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"" +
                " xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Minimum=\"0\" Maximum=\"255\" Value=\"128\" Width=\"148\">" +
                "<Slider.Template><ControlTemplate TargetType=\"Slider\"><Grid MinHeight=\"24\"><Track x:Name=\"PART_Track\">" +
                "<Track.DecreaseRepeatButton><RepeatButton Command=\"Slider.DecreaseLarge\"><RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"RepeatButton\"><Grid Background=\"Transparent\"><Border Height=\"4\" Margin=\"1,0,0,0\"/></Grid></ControlTemplate>" +
                "</RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command=\"Slider.IncreaseLarge\"><RepeatButton.Template>" +
                "<ControlTemplate TargetType=\"RepeatButton\"><Grid Background=\"Transparent\"/></ControlTemplate>" +
                "</RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb Width=\"14\" Height=\"14\"/></Track.Thumb>" +
                "</Track></Grid></ControlTemplate></Slider.Template></Slider>");
            var panel = Panel(slider);
            var aligned = new Style(typeof(System.Windows.Controls.Primitives.RepeatButton));
            aligned.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left));
            aligned.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            panel.Resources.Add(typeof(System.Windows.Controls.Primitives.RepeatButton), aligned);
            Shown(panel, () =>
            {
                var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
                Assert.Equal(new Size(0, 0), track.IncreaseRepeatButton.RenderSize);
                AccessibleNames.NameInput(slider);
                Assert.Equal(Strings.Instance.Common_Decrease, NameOf(track.DecreaseRepeatButton));
                Assert.Equal(Strings.Instance.Common_Increase, NameOf(track.IncreaseRepeatButton));
            });
        });

        [Fact]
        public void AListItemThatWouldReadATypeOrEnumName_IsLabeledByTheTextItShows() => RunSta(() =>
        {
            var plain = new ItemsControl
            {
                ItemsSource = new object[] { new Plain { Title = "Gyro" }, new Named() },
                ItemTemplate = Template("<StackPanel><TextBlock Text=\"&#xE700;\"/><TextBlock Text=\"{Binding Title}\"/></StackPanel>"),
            };
            var sides = new ListBox
            {
                ItemsSource = new[] { Side.Left, Side.Right },
                ItemTemplate = Template("<TextBlock Text=\"{Binding StringFormat='Shown {0}'}\"/>"),
            };
            Shown(Panel(plain, sides), () =>
            {
                foreach (var list in new ItemsControl[] { plain, sides })
                    for (int i = 0; i < list.Items.Count; i++)
                        AccessibleNames.NameItem((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(i));
                Assert.Equal(new[] { "Gyro", "Named item" }, ItemNames(plain));
                Assert.Equal(new[] { "Shown Left", "Shown Right" }, ItemNames(sides));
            });
        });

        [Fact]
        public void ASlotReadsAsTheDashboardCardTitlesIt_AndOtherItemsByTheNameTheyShow()
        {
            string slot3 = Strings.Instance.Dashboard_Slot + " 3";
            Assert.Equal(slot3, AccessibleNames.SlotTitle(3));
            Assert.Equal(slot3, new SlotSummary(0) { SlotNumber = 3 }.ToString());
            Assert.Equal(Strings.Instance.ControllerType_Xbox + " #2",
                new SlotBadge { VcType = PadForge.Engine.VirtualControllerType.Xbox, TypeNumber = 2 }.ToString());
            Assert.Equal(Strings.Instance.Pad_Gyro, new SlotStageInfo("Gyro", "x").ToString());
            Assert.Equal(Strings.Instance.Pad_Sticks, new SlotStageInfo("Sticks", string.Empty, isStickShape: true).ToString());
            Assert.Equal("Left Stick", new StickConfigItem(0, "Left Stick").ToString());
            Assert.Equal("Racing", new ProfileListItem { Name = "Racing" }.ToString());
        }
    }
}
