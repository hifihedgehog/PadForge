using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Xml.Linq;
using PadForge.Common;
using PadForge.Controls;
using PadForge.Converters;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using Xunit;
using NavigationViewItem = Wpf.Ui.Controls.NavigationViewItem;

namespace PadForge.Tests
{
    /// <summary>
    /// What the screen-reader pass over every page found and fixed (#574):
    /// buttons and rows a listener could not tell apart, parts with no name,
    /// and parts that read as the wrong thing. Strings are read at run time, so
    /// the class runs apart from the culture tests.
    /// </summary>
    [Collection("CultureSwitching")]
    public class ScreenReaderPassTests
    {
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

        private static void Shown(FrameworkElement content, Action body)
        {
            var window = new Window
            {
                Width = 500, Height = 600, ShowActivated = false, ShowInTaskbar = false,
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

        private static AutomationPeer PeerOf(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element);

        private static string NameOf(UIElement element) => PeerOf(element).GetName();

        private static string RepoText(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static string App => typeof(FoldCard).Assembly.GetName().Name;

        private const string Presentation = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        // ── Reset All ──

        /// <summary>A page shows one Reset All a card, all with the same
        /// words. Each reads its words and then its card, which its tooltip
        /// names through the format the tooltips are built from.</summary>
        [Fact]
        public void AResetAllButton_ReadsItsWordsAndThenItsCard()
        {
            var converter = new ResetAllNameConverter();
            string Name(object words, object tip, object format, string resetAll = "Reset All")
                => (string)converter.Convert(new[] { words, tip, format, resetAll }, typeof(string), null, CultureInfo.InvariantCulture);

            Assert.Equal("Reset All, Left Thumbstick", Name("Reset All", "Reset Left Thumbstick", "Reset {0}"));
            // A language that puts the verb last.
            Assert.Equal("Alles zurücksetzen, Updates", Name("Alles zurücksetzen", "Updates zurücksetzen", "{0} zurücksetzen", "Alles zurücksetzen"));
            // A tooltip the format does not fit follows the words whole when it is a phrase.
            Assert.Equal("Reset All, Reset the LED count of every header", Name("Reset All", "Reset the LED count of every header", "{0} zurücksetzen"));
            Assert.Equal("Reset All, Reset", Name("Reset All", "Reset", "Reset {0}"));
            // A sentence is help, and stays the help text.
            Assert.Equal(string.Empty, Name("Reset All", "Remove the colors from every layer.", "Reset {0}"));
            // With no tooltip the button reads the words it shows.
            Assert.Equal(string.Empty, Name("Reset All", null, "Reset {0}"));
            // A tooltip that is no string, such as one built of elements.
            Assert.Equal(string.Empty, Name("Reset All", new object(), "Reset {0}"));
            // A button of this look with other words reads those words: the
            // Reset Calibration button read its whole tooltip after them.
            Assert.Equal(string.Empty, Name("Reset Calibration", "Reset Calibration", "Reset {0}"));
            Assert.Equal(string.Empty, Name("Remove", "Remove Package", "Reset {0}"));

            // In the language: a card's tooltip is the format around its title.
            string format = Strings.Instance.Pad_ResetSection_Format;
            Assert.Equal(Strings.Instance.Pad_ResetAll + ", Left Thumbstick",
                Name(Strings.Instance.Pad_ResetAll, string.Format(format, "Left Thumbstick"), format, Strings.Instance.Pad_ResetAll));

            // Every Reset All button takes the name from its style.
            string app = RepoText("PadForge.App", "App.xaml").Replace("\r\n", "\n");
            Assert.Matches(new Regex("<Style x:Key=\"ResetAllButton\"[^>]*>\\s*<Setter Property=\"AutomationProperties.Name\">\\s*<Setter.Value>\\s*" +
                "<MultiBinding Converter=\"\\{StaticResource ResetAllNameConverter\\}\">\\s*" +
                "<Binding Path=\"Content\" RelativeSource=\"\\{RelativeSource Self\\}\"/>\\s*" +
                "<Binding Path=\"ToolTip\" RelativeSource=\"\\{RelativeSource Self\\}\"/>\\s*" +
                "<Binding Path=\"Pad_ResetSection_Format\" Source=\"\\{x:Static strings:Strings.Instance\\}\"/>\\s*" +
                "<Binding Path=\"Pad_ResetAll\" Source=\"\\{x:Static strings:Strings.Instance\\}\"/>"), app);
        }

        // ── A closed fold ──

        private static T Fold<T>(string type) where T : Expander
            => (T)XamlReader.Parse(
                "<" + type + " " + Presentation + " xmlns:c=\"clr-namespace:PadForge.Controls;assembly=" + App + "\" Header=\"Engage\">" +
                "<" + type + ".Template><ControlTemplate TargetType=\"" + (type.StartsWith("c:", StringComparison.Ordinal) ? "c:FoldPart" : "Expander") + "\"><StackPanel>" +
                "<ToggleButton x:Name=\"HeaderSite\" Content=\"{TemplateBinding Header}\" IsChecked=\"{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}\"/>" +
                "<Button Content=\"Reset All\"/>" +
                "<Border x:Name=\"ExpandSite\" Visibility=\"Collapsed\"><ContentPresenter/></Border></StackPanel>" +
                "<ControlTemplate.Triggers><Trigger Property=\"IsExpanded\" Value=\"True\"><Setter TargetName=\"ExpandSite\" Property=\"Visibility\" Value=\"Visible\"/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></" + type + ".Template>" +
                "<StackPanel><Button Content=\"Inside\"/><ComboBox/></StackPanel></" + type + ">");

        /// <summary>A closed part lists its header and what stands beside
        /// it, and nothing of what it holds. A plain Expander that was
        /// opened once lists its content from then on, closed or open, and
        /// so reaches a picker's stale items: a closed Engage part read 157
        /// list items.</summary>
        [Fact]
        public void AClosedFold_ListsNothingItHolds_AndAnOpenOneListsItAgain() => RunSta(() =>
        {
            var fold = Fold<FoldCard>("c:FoldCard");
            var plain = Fold<Expander>("Expander");
            var panel = new StackPanel();
            panel.Children.Add(fold);
            panel.Children.Add(plain);
            Shown(panel, () =>
            {
                string[] Names(UIElement part)
                {
                    PeerOf(part).ResetChildrenCache();
                    return PeerOf(part).GetChildren().Select(p => p.GetName()).ToArray();
                }
                void Set(bool open)
                {
                    fold.IsExpanded = plain.IsExpanded = open;
                    panel.UpdateLayout();
                }

                // Never opened, neither part has built what it holds.
                Assert.Equal(new[] { "Engage", "Reset All" }, Names(fold));
                Assert.Equal(new[] { "Engage", "Reset All" }, Names(plain));
                Assert.IsAssignableFrom<ExpanderAutomationPeer>(PeerOf(fold));
                Assert.NotNull(PeerOf(fold).GetPattern(PatternInterface.ExpandCollapse));

                Set(true);
                Assert.Contains("Inside", Names(fold));
                Assert.Contains("Reset All", Names(fold));
                Assert.Contains("Inside", Names(plain));

                // Closed again, the plain Expander still lists its content.
                Set(false);
                Assert.Contains("Inside", Names(plain));
                Assert.Equal(new[] { "Engage", "Reset All" }, Names(fold));
            });
        });

        // ── The sidebar's label ──

        /// <summary>The word Controllers stands among the sidebar's entries
        /// as an item of the list. It reads as text, and the entries count
        /// entries: with the label counted, the first slot was the sixth of
        /// seven where the list shows it fifth of six.</summary>
        [Fact]
        public void TheSidebarsLabel_ReadsAsText_AndHasNoPlaceAmongTheEntries() => RunSta(() =>
        {
            var list = new NavItemsControl();
            var home = new NavigationViewItem { Tag = "Home", Content = "Home" };
            var label = new TextBlock { Text = "Controllers" };
            var slot = new NavigationViewItem { Tag = "Pad1", Content = "Slot 1" };
            list.Items.Add(home);
            list.Items.Add(label);
            list.Items.Add(slot);
            list.Measure(new Size(300, 600));
            list.Arrange(new Rect(0, 0, 300, 600));
            list.UpdateLayout();
            var peers = PeerOf(list).GetChildren().OfType<ItemAutomationPeer>().ToList();
            AutomationPeer Of(object item) => peers.Single(p => ReferenceEquals(p.Item, item));

            Assert.Equal(AutomationControlType.ListItem, Of(home).GetAutomationControlType());
            Assert.Equal(AutomationControlType.Text, Of(label).GetAutomationControlType());
            Assert.Equal((1, 2), (Of(home).GetPositionInSet(), Of(home).GetSizeOfSet()));
            Assert.Equal((2, 2), (Of(slot).GetPositionInSet(), Of(slot).GetSizeOfSet()));
            Assert.Equal((-1, -1), (Of(label).GetPositionInSet(), Of(label).GetSizeOfSet()));
        });

        // ── Cards and what stands on them ──

        [Fact]
        public void ATriggersCard_ReadsItsTitle_AsAStickCardDoes()
        {
            Assert.Equal("Left Trigger", new TriggerConfigItem(0, "Left Trigger").ToString());
            Assert.Equal(string.Empty, new TriggerConfigItem(0, null).ToString());
        }

        /// <summary>The shape beside a card's title and a trigger's travel
        /// gauge are drawings of what the card says in words and numbers. As
        /// UserControls each read as a custom control with no name, so each
        /// takes the peer the app's other icons have, which is out of the
        /// control view and lists nothing
        /// (AccessibilityPeerTests.ADecorativeIcon_HidesTheGlyphItDraws).
        /// Both draw from the app's own resources, so the test reads the
        /// classes and makes neither.</summary>
        [Fact]
        public void TheCardIconAndTheTravelGauge_KeepOutOfTheControlView()
        {
            const System.Reflection.BindingFlags Own = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly;
            Assert.NotNull(typeof(PadForge.Views.Controls.LabeledShapeIcon).GetMethod("OnCreateAutomationPeer", Own));
            Assert.NotNull(typeof(TriggerTravelArc).GetMethod("OnCreateAutomationPeer", Own));
            Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new PadForge.Controls.DecorativeIconPeer(this);",
                RepoText("PadForge.App", "Views", "Controls", "LabeledShapeIcon.xaml.cs"));
            Assert.Contains("protected override AutomationPeer OnCreateAutomationPeer() => new DecorativeIconPeer(this);",
                RepoText("PadForge.App", "Controls", "TriggerTravelArc.xaml.cs"));
        }

        [Fact]
        public void ACurveEditor_SaysWhatItIs_InTheLanguage() => RunSta(() =>
        {
            var editor = new CurveEditor { CurveString = "0,0;1,1", Width = 200, Height = 200 };
            Shown(editor, () =>
            {
                Assert.Equal("curve editor", Strings.Instance.Common_CurveEditor);
                Assert.Equal(Strings.Instance.Common_CurveEditor, PeerOf(editor).GetLocalizedControlType());
            });
        });

        // ── Glyphs inside buttons ──

        /// <summary>wpf-ui draws a check box's mark and a scroll bar's arrow
        /// with a TextBlock it makes in code, which stays in the control
        /// view and read as a private-use character. Such a glyph reads as
        /// its button does. PadForge's own GlyphText is out of the control
        /// view and is left alone, and so is a glyph with a name of its
        /// own.</summary>
        [Fact]
        public void AGlyphInsideAButton_ReadsAsTheButtonDoes() => RunSta(() =>
        {
            var mark = new TextBlock { Text = Glyph };
            var words = new TextBlock { Text = "Low Motor" };
            var checkContent = new StackPanel();
            checkContent.Children.Add(mark);
            checkContent.Children.Add(words);
            var check = new CheckBox { Content = checkContent };

            var arrow = new TextBlock { Text = Glyph };
            var tipped = new Button { Content = arrow, ToolTip = "Scroll Up" };

            var quiet = new GlyphText { Text = Glyph };
            var own = new TextBlock { Text = Glyph };
            System.Windows.Automation.AutomationProperties.SetName(own, "Named");
            var mixed = new StackPanel();
            mixed.Children.Add(quiet);
            mixed.Children.Add(own);
            var third = new Button { Content = mixed, ToolTip = "Third" };

            // A button inside a button names its own glyph.
            var innerGlyph = new TextBlock { Text = Glyph };
            var inner = new Button { Content = innerGlyph, ToolTip = "Inner" };
            var outerPanel = new StackPanel();
            outerPanel.Children.Add(new TextBlock { Text = "Outer" });
            outerPanel.Children.Add(inner);
            var outer = new ToggleButton { Content = outerPanel };

            var panel = new StackPanel();
            foreach (UIElement child in new UIElement[] { check, tipped, third, outer })
                panel.Children.Add(child);
            Shown(panel, () =>
            {
                foreach (ButtonBase button in new ButtonBase[] { check, tipped, third, outer, inner })
                    AccessibleNames.NameButton(button);
                Assert.Equal("Low Motor", NameOf(check));
                Assert.Equal("Low Motor", NameOf(mark));
                Assert.Equal("Scroll Up", NameOf(tipped));
                Assert.Equal("Scroll Up", NameOf(arrow));
                Assert.Equal("Named", NameOf(own));
                Assert.Equal(string.Empty, System.Windows.Automation.AutomationProperties.GetName(quiet));
                Assert.Equal("Inner", NameOf(innerGlyph));
            });
        });

        // ── Rows inside a card's template ──

        /// <summary>A row written in a DataTemplate, as a stick's and a
        /// trigger's rows are. Text a template writes is no local value, so
        /// the % after a value box was no unit to the label search, which
        /// stopped at it: the raw box read its tooltip alone, and every raw
        /// box of a card read alike.</summary>
        [Fact]
        public void ARowInsideATemplate_ReadsAsARowOutsideOneDoes() => RunSta(() =>
        {
            string strings = "xmlns:s=\"clr-namespace:PadForge.Resources.Strings;assembly=" + App + "\"";
            var template = (DataTemplate)XamlReader.Parse(
                "<DataTemplate " + Presentation + " " + strings + "><StackPanel>" +
                "<StackPanel Orientation=\"Horizontal\">" +
                "<TextBlock Text=\"{Binding Common_Close, Source={x:Static s:Strings.Instance}}\"/><Slider Width=\"60\"/><TextBox/>" +
                "<TextBlock Text=\"%\"/><TextBox ToolTip=\"Raw axis value\"/></StackPanel>" +
                "<StackPanel Orientation=\"Horizontal\">" +
                "<TextBlock Text=\"{Binding Common_Close, Source={x:Static s:Strings.Instance}}\"/><TextBox/>" +
                "<TextBlock Text=\"{Binding Common_To, Source={x:Static s:Strings.Instance}}\"/><TextBox/><TextBlock Text=\"%\"/>" +
                "<TextBox ToolTip=\"Raw axis value\"/><TextBlock Text=\"{Binding Common_To, Source={x:Static s:Strings.Instance}}\"/><TextBox ToolTip=\"Raw axis value\"/></StackPanel>" +
                "<StackPanel Orientation=\"Horizontal\"><TextBlock Text=\"R\"/><Slider Width=\"60\"/><TextBox/></StackPanel>" +
                "</StackPanel></DataTemplate>");
            var host = new ContentPresenter { Content = new object(), ContentTemplate = template };
            Shown(host, () =>
            {
                var boxes = new List<TextBox>();
                var sliders = new List<Slider>();
                var open = new Stack<DependencyObject>();
                open.Push(host);
                while (open.Count > 0)
                {
                    DependencyObject d = open.Pop();
                    if (d is TextBox box) { boxes.Add(box); continue; }
                    if (d is Slider slider) { sliders.Add(slider); continue; }
                    for (int i = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--)
                        open.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
                }
                Assert.Equal(7, boxes.Count);
                foreach (FrameworkElement input in boxes.Cast<FrameworkElement>().Concat(sliders))
                    AccessibleNames.NameInput(input);
                string label = Strings.Instance.Common_Close, to = Strings.Instance.Common_To;
                Assert.Equal(new[]
                {
                    label, label + ", Raw axis value",
                    label, label + ", " + to, label + ", Raw axis value", label + ", " + to + ", Raw axis value",
                    "R",
                }, boxes.Select(NameOf).ToArray());
                Assert.Equal(new[] { label, "R" }, sliders.Select(NameOf).ToArray());
            });
        });

        // ── Meters ──

        /// <summary>Every meter has a name: a bar with none reads as
        /// progress bar and a number, of nothing. One bar is named in code
        /// as its dialog counts down.</summary>
        [Fact]
        public void EveryMeterHasAName()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var unnamed = new List<string>();
            int meters = 0;
            foreach (string file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "PadForge.App"), "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;
                XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                foreach (XElement bar in doc.Descendants().Where(e => e.Name.LocalName == "ProgressBar"))
                {
                    meters++;
                    bool named = bar.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name");
                    string id = bar.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;
                    if (!named && id != "CountdownBar")
                        unnamed.Add(Path.GetFileName(file) + ":" + ((System.Xml.IXmlLineInfo)bar).LineNumber);
                }
            }
            Assert.True(meters >= 18, "the scan found " + meters + " meters");
            Assert.True(unnamed.Count == 0, "meters with no name: " + string.Join(", ", unnamed));
            Assert.Contains("AutomationProperties.SetName(CountdownBar, sentence);", RepoText("PadForge.App", "Views", "KeepDriverDialog.xaml.cs"));
        }

        // ── The controller page's groups ──

        /// <summary>Trigger Routing holds the same rows for each trigger,
        /// and Adaptive Triggers the same GameCube button. Each trigger's
        /// rows stand in a group that reads the trigger's name, and each
        /// GameCube button reads its trigger.</summary>
        [Fact]
        public void EachTriggersRows_StandInAGroupOfItsName()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument page = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            XElement card = page.Descendants().Single(e => (string)e.Attribute(x + "Name") == "TriggerRoutingFoldCard");
            var groups = card.Descendants().Where(e => e.Name.LocalName == "GroupBox").ToList();
            Assert.Equal(2, groups.Count);
            foreach (var (group, side) in groups.Zip(new[] { "Left", "Right" }))
            {
                Assert.Equal("{StaticResource BareGroupBox}", (string)group.Attribute("Style"));
                Assert.Equal("{Binding Pad_TriggerRouting_" + side + "Trigger, Source={x:Static strings:Strings.Instance}}",
                    group.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
                // The trigger's five rows, each with its command, and no other trigger's.
                var commands = group.Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName == "Command").Select(a => a.Value).ToList();
                Assert.Equal(5, commands.Count(c => c.Contains("Reset" + side + "TriggerRoute")));
                Assert.DoesNotContain(commands, c => c.Contains("Reset" + (side == "Left" ? "Right" : "Left") + "TriggerRoute"));
            }
            // Every row of the card is in one of the two groups.
            Assert.DoesNotContain(card.Descendants(), e => e.Name.LocalName == "SettingRow" && !e.Ancestors().Any(a => a.Name.LocalName == "GroupBox"));

            var gameCube = page.Descendants().Where(e => e.Name.LocalName == "Button" && (string)e.Attribute("Content") == "GameCube").ToList();
            Assert.Equal(2, gameCube.Count);
            Assert.Equal(new[]
            {
                "{Binding Pad_LeftTrigger, Source={x:Static strings:Strings.Instance}, StringFormat='GameCube, {0}'}",
                "{Binding Pad_RightTrigger, Source={x:Static strings:Strings.Instance}, StringFormat='GameCube, {0}'}",
            }, gameCube.Select(b => b.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value).ToArray());
        }

        // ── What takes no room, and what is only drawn ──

        /// <summary>A list with nothing in it and a text with no words take
        /// no room, and a peer says an element is on screen while it is
        /// visible, so each read as on screen with no rectangle. With
        /// FromClip an element that takes no room is off screen. A plain
        /// list takes it from the app's list style while it is empty.</summary>
        [Fact]
        public void AnEmptyListAndAnEmptyText_AreOffScreen_UntilTheyHoldSomething() => RunSta(() =>
        {
            var list = new ItemsControl();
            var stock = new ItemsControl();
            var text = new TextBlock();
            System.Windows.Automation.AutomationProperties.SetIsOffscreenBehavior(list, System.Windows.Automation.IsOffscreenBehavior.FromClip);
            System.Windows.Automation.AutomationProperties.SetIsOffscreenBehavior(text, System.Windows.Automation.IsOffscreenBehavior.FromClip);
            // A text at the end of a row, as the boundary note is: as wide
            // as its words.
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text);
            var panel = new StackPanel();
            panel.Children.Add(list);
            panel.Children.Add(stock);
            panel.Children.Add(row);
            Shown(panel, () =>
            {
                Assert.True(PeerOf(list).IsOffscreen(), "the empty list, " + list.RenderSize);
                Assert.True(PeerOf(text).IsOffscreen(), "the empty text, " + text.RenderSize);
                // As WPF has it, an empty list is on screen with no rectangle.
                Assert.False(PeerOf(stock).IsOffscreen());
                Rect stockRect = PeerOf(stock).GetBoundingRectangle();
                Assert.True(stockRect.IsEmpty || stockRect.Height == 0, "the stock list's rectangle, " + stockRect);

                list.Items.Add("Left Motor");
                text.Text = "92% round";
                panel.UpdateLayout();
                Assert.False(PeerOf(list).IsOffscreen());
                Assert.False(PeerOf(text).IsOffscreen());
            });

            string app = RepoText("PadForge.App", "App.xaml").Replace("\r\n", "\n");
            Assert.Matches(new Regex("<Style TargetType=\"ItemsControl\">\\s*<Setter Property=\"Focusable\" Value=\"False\"/>\\s*<Style.Triggers>\\s*" +
                "<Trigger Property=\"HasItems\" Value=\"False\">\\s*<Setter Property=\"AutomationProperties.IsOffscreenBehavior\" Value=\"FromClip\"/>"), app);
            // A list with a style of its own takes the app's list style under it.
            string devices = RepoText("PadForge.App", "Views", "DevicesPage.xaml");
            Assert.DoesNotContain("<Style TargetType=\"ItemsControl\">", devices);
            string pad = RepoText("PadForge.App", "Views", "PadPage.xaml").Replace("\r\n", "\n");
            Assert.Contains("<TextBlock Text=\"{Binding BoundaryCircularityText}\"\n" +
                "                                                           AutomationProperties.IsOffscreenBehavior=\"FromClip\"", pad);
            // The list moves with its line (the Audio card's tray since the
            // sections pass), so its attributes are matched apart from how
            // deep they stand.
            Assert.Matches("<ListBox x:Name=\"SoundPackagesList\" MaxHeight=\"170\"\\s+" +
                "AutomationProperties\\.Name=\"\\{Binding Pad_Audio_Packages_Header, Source=\\{x:Static strings:Strings\\.Instance\\}\\}\"\\s+" +
                "AutomationProperties\\.IsOffscreenBehavior=\"FromClip\"", pad);
        });

        /// <summary>The range slider itself takes no focus and is no control
        /// to UI Automation. Each of its two thumbs is a Tab stop that reads
        /// as a slider with the range's name (the upper end with the word
        /// between a range's two ends after it), its end's value and that
        /// end's own bounds, and the arrow keys, Page Up, Page Down, Home
        /// and End move it. While the thumbs moved by the pointer alone, a
        /// trigger's range could be typed only in the boxes under More, and
        /// Tab went from Reset All straight to Reset Range.</summary>
        [Fact]
        public void TheRangeSlidersThumbs_AreSlidersTheKeyboardMoves() => RunSta(() =>
        {
            var slider = new RangeSlider { Width = 300, Minimum = 0, Maximum = 100, LowerValue = 20, UpperValue = 80 };
            System.Windows.Automation.AutomationProperties.SetName(slider, "Range");
            Shown(slider, () =>
            {
                Assert.False(slider.Focusable);
                AutomationPeer own = PeerOf(slider);
                Assert.False(own.IsControlElement());
                Assert.False(own.IsContentElement());

                var lower = (Thumb)slider.Template.FindName("PART_LowerThumb", slider);
                var upper = (Thumb)slider.Template.FindName("PART_UpperThumb", slider);
                foreach (Thumb thumb in new[] { lower, upper })
                {
                    Assert.IsType<RangeThumb>(thumb);
                    Assert.True(thumb.Focusable);
                    Assert.True(thumb.IsTabStop);
                    Assert.NotNull(thumb.FocusVisualStyle);
                }

                AutomationPeer low = PeerOf(lower), high = PeerOf(upper);
                foreach (AutomationPeer peer in new[] { low, high })
                {
                    Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
                    Assert.True(peer.IsControlElement());
                    Assert.True(peer.IsContentElement());
                    Assert.True(peer.IsKeyboardFocusable());
                }
                Assert.Equal("Range", low.GetName());
                Assert.Equal("Range, " + Strings.Instance.Common_To, high.GetName());
                var lowValue = (IRangeValueProvider)low.GetPattern(PatternInterface.RangeValue);
                var highValue = (IRangeValueProvider)high.GetPattern(PatternInterface.RangeValue);
                Assert.Equal((20.0, 0.0, 80.0), (lowValue.Value, lowValue.Minimum, lowValue.Maximum));
                Assert.Equal((80.0, 20.0, 100.0), (highValue.Value, highValue.Minimum, highValue.Maximum));
                Assert.Equal((RangeSlider.SmallStep, RangeSlider.LargeStep), (lowValue.SmallChange, lowValue.LargeChange));
                Assert.False(lowValue.IsReadOnly);
                // A screen reader sets an end, inside that end's bounds.
                lowValue.SetValue(35);
                Assert.Equal(35, slider.LowerValue);
                highValue.SetValue(10);
                Assert.Equal(35, slider.UpperValue);
                highValue.SetValue(80);
                lowValue.SetValue(20);

                // The keys, handed to a thumb.
                var source = PresentationSource.FromVisual(lower);
                Assert.NotNull(source);
                bool Press(Thumb thumb, Key key)
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.KeyDownEvent };
                    thumb.RaiseEvent(args);
                    return args.Handled;
                }
                Assert.True(Press(lower, Key.Right));
                Assert.Equal(21, slider.LowerValue);
                Assert.True(Press(lower, Key.Left));
                Assert.True(Press(lower, Key.Down));
                Assert.Equal(19, slider.LowerValue);
                Assert.True(Press(lower, Key.PageUp));
                Assert.Equal(29, slider.LowerValue);
                // The lower end stops at the upper one, and Home is its least.
                Assert.True(Press(lower, Key.End));
                Assert.Equal(80, slider.LowerValue);
                Assert.True(Press(lower, Key.Home));
                Assert.Equal(0, slider.LowerValue);
                Assert.True(Press(upper, Key.PageDown));
                Assert.Equal(70, slider.UpperValue);
                Assert.True(Press(upper, Key.Up));
                Assert.Equal(71, slider.UpperValue);
                Assert.True(Press(upper, Key.End));
                Assert.Equal(100, slider.UpperValue);
                // The upper end stops at the lower one.
                Assert.True(Press(upper, Key.Home));
                Assert.Equal(0, slider.UpperValue);
                // A key that moves nothing is left for whoever wants it.
                Assert.False(Press(upper, Key.Tab));
                Assert.False(Press(lower, Key.A));
            });
        });

        [Theory]
        [InlineData(Key.Left, 50.0, 49.0)]
        [InlineData(Key.Down, 50.0, 49.0)]
        [InlineData(Key.Right, 50.0, 51.0)]
        [InlineData(Key.Up, 50.0, 51.0)]
        [InlineData(Key.PageDown, 50.0, 40.0)]
        [InlineData(Key.PageUp, 50.0, 60.0)]
        [InlineData(Key.Home, 50.0, 20.0)]
        [InlineData(Key.End, 50.0, 80.0)]
        // A step never passes the end's bounds.
        [InlineData(Key.Left, 20.4, 20.0)]
        [InlineData(Key.Right, 79.5, 80.0)]
        [InlineData(Key.PageDown, 25.0, 20.0)]
        [InlineData(Key.PageUp, 75.0, 80.0)]
        public void AKeyTakesARangesEnd_AStep_ALargeStep_OrToItsBounds(Key key, double from, double expected)
            => Assert.Equal(expected, RangeSlider.Nudged(from, key, 20, 80));

        [Fact]
        public void AKeyThatMovesNothing_IsNoStep()
        {
            Assert.True(double.IsNaN(RangeSlider.Nudged(50, Key.Tab, 20, 80)));
            Assert.True(double.IsNaN(RangeSlider.Nudged(50, Key.Enter, 20, 80)));
        }

        /// <summary>A picture drawn with shapes says nothing to UI
        /// Automation. A NamedPicture reads as one image with the name and
        /// the words set on it, and lists nothing of what it holds.</summary>
        [Fact]
        public void ANamedPicture_ReadsAsOneImage_WithItsNameAndWhatItShows() => RunSta(() =>
        {
            var picture = new NamedPicture { Child = new Button { Content = "Inside" } };
            System.Windows.Automation.AutomationProperties.SetName(picture, "Left Thumbstick");
            System.Windows.Automation.AutomationProperties.SetHelpText(picture, "X 78%, Y 25%");
            Shown(picture, () =>
            {
                AutomationPeer peer = PeerOf(picture);
                Assert.Equal(AutomationControlType.Image, peer.GetAutomationControlType());
                Assert.Equal("Left Thumbstick", peer.GetName());
                Assert.Equal("X 78%, Y 25%", peer.GetHelpText());
                Assert.True(peer.IsControlElement());
                Assert.False(peer.IsKeyboardFocusable());
                Assert.Null(peer.GetChildren());
            });
        });

        /// <summary>A key hint's name is its whole sentence with the keys
        /// in it, so a key's label inside it is out of the control view: a
        /// listener heard the sentence and then each key again.</summary>
        [Fact]
        public void AKeyHint_ReadsAsOneSentence() => RunSta(() =>
        {
            var hint = new KeyHint { Format = "{0} {1} to move", Key0 = "Up", Key1 = "Down" };
            Shown(hint, () =>
            {
                Assert.Equal("Up Down to move", NameOf(hint));
                var kids = PeerOf(hint).GetChildren() ?? new List<AutomationPeer>();
                Assert.DoesNotContain(kids, k => k.IsControlElement() || k.IsContentElement());
            });
        });

        /// <summary>The layers of a drawn picture are images with no name:
        /// a controller's 2D picture, the lightbar's, the VR controllers'
        /// and a shared profile's. Each is made as the app's quiet image.
        /// The color box of a color picker is worked by the pointer beside
        /// the HEX box and the sliders that read the color, and keeps out
        /// of the control view too.</summary>
        [Fact]
        public void TheLayersOfADrawnPicture_AreQuietImages()
        {
            foreach (var (file, layers) in new[]
            {
                (new[] { "PadForge.App", "Views", "ControllerModel2DView.xaml.cs" }, 2),
                (new[] { "PadForge.App", "Views", "VRPreviewView.xaml.cs" }, 4),
                (new[] { "PadForge.App", "Views", "WorkshopControllerPreview.xaml.cs" }, 1),
                (new[] { "PadForge.App", "Views", "PadPage.xaml.cs" }, 2),
            })
            {
                string source = RepoText(file);
                Assert.DoesNotMatch(new Regex(@"new Image\b"), source);
                Assert.Equal(layers, Regex.Matches(source, @"new PadForge\.Controls\.DecorativeImage\b").Count);
            }
            Assert.Contains("=> new PadForge.Controls.DecorativeIconPeer(this);", RepoText("PadForge.App", "Views", "ColorPickerControl.xaml.cs"));
        }

        // ── Lists and parts that read their heading ──

        [Fact]
        public void AListUnderAHeading_ReadsTheHeading()
        {
            string S(string key) => "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            string NameOn(XDocument doc, string element)
                => doc.Descendants().Single(e => (string)e.Attribute(x + "Name") == element)
                    .Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value;

            Assert.Equal(S("Home_Controllers"), NameOn(XDocument.Parse(RepoText("PadForge.App", "Views", "HomePage.xaml")), "SlotsItemsControl"));
            XDocument add = XDocument.Parse(RepoText("PadForge.App", "Views", "AddControllerView.xaml"));
            Assert.Equal(S("AddController_DrivenBy"), NameOn(add, "DrivenByList"));
            Assert.Equal(S("AddController_Common"), NameOn(add, "CommonTiles"));
            Assert.Equal(S("AddController_MoreOutputs"), NameOn(add, "MoreTiles"));
            Assert.Equal(S("Devices_UsedBy"), NameOn(XDocument.Parse(RepoText("PadForge.App", "Views", "DevicesPage.xaml")), "UsedByChips"));
            XDocument pad = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            Assert.Equal(S("Pad_View_Picture"), NameOn(pad, "ControllerModel3D"));
            Assert.Equal(S("Pad_View_Picture"), NameOn(pad, "ControllerModel2D"));

            // The header's More reads the controller it acts on, since a
            // list on the page has a More of its own: the word More, then
            // the two values the title shows. The title is drawn in runs,
            // and a TextBlock drawn in runs has no Text to bind to (the
            // name read "More for" and nothing).
            XElement more = pad.Descendants().Single(e => (string)e.Attribute(x + "Name") == "PadMoreButton");
            Assert.DoesNotContain(more.Attributes(), a => a.Name.LocalName == "AutomationProperties.Name");
            XElement name = more.Elements().Single(e => e.Name.LocalName == "AutomationProperties.Name").Elements().Single();
            Assert.Equal("MultiBinding", name.Name.LocalName);
            Assert.Equal("{}{0}, {1} #{2}", (string)name.Attribute("StringFormat"));
            Assert.Equal(new[] { "Common_More|{x:Static strings:Strings.Instance}", "OutputTypeDisplayName|", "TypeInstanceLabel|" },
                name.Elements().Select(b => (string)b.Attribute("Path") + "|" + (string)b.Attribute("Source")).ToArray());

            // The Devices panel's New Controller chip stands in a named
            // holder: as an item of the list it was its own container, and
            // read as a data item with nothing to invoke.
            XDocument devices = XDocument.Parse(RepoText("PadForge.App", "Views", "DevicesPage.xaml"));
            XElement chip = devices.Descendants().Single(e => (string)e.Attribute(x + "Name") == "NewControllerChip");
            Assert.Equal("Grid", chip.Parent.Name.LocalName);
            Assert.Equal("CompositeCollection", chip.Parent.Parent.Name.LocalName);
            Assert.Equal(S("Devices_UsedBy_New"), chip.Parent.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
            Assert.Equal("{Binding CanAddController, Converter={StaticResource BoolToVisibilityConverter}}", (string)chip.Parent.Attribute("Visibility"));
            Assert.Null(chip.Attribute("Visibility"));
        }

        /// <summary>A button whose word says nothing of what reads with the
        /// name of what it acts on, in the form a driver's button has:
        /// HidHide's list of applications before Add and Remove, and
        /// Starter Profiles before See All.</summary>
        [Fact]
        public void AButtonThatNamesNoThing_ReadsWithWhatItActsOn()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            static string[] NameParts(XElement button)
            {
                XElement name = button.Elements().Single(e => e.Name.LocalName == "AutomationProperties.Name").Elements().Single();
                Assert.Equal("MultiBinding", name.Name.LocalName);
                Assert.Equal("{}{0}: {1}", (string)name.Attribute("StringFormat"));
                Assert.All(name.Elements(), b => Assert.Equal("{x:Static strings:Strings.Instance}", (string)b.Attribute("Source")));
                return name.Elements().Select(b => (string)b.Attribute("Path")).ToArray();
            }
            XDocument settings = XDocument.Parse(RepoText("PadForge.App", "Views", "SettingsPage.xaml"));
            XElement Named(XDocument page, string id) => page.Descendants().Single(e => (string)e.Attribute(x + "Name") == id);
            Assert.Equal(new[] { "Settings_WhitelistedApps", "Settings_AddDots" }, NameParts(Named(settings, "WhitelistAdd")));
            Assert.Equal(new[] { "Settings_WhitelistedApps", "Common_Remove" }, NameParts(Named(settings, "WhitelistRemove")));
            XDocument profiles = XDocument.Parse(RepoText("PadForge.App", "Views", "ProfilesPage.xaml"));
            Assert.Equal(new[] { "Profiles_Starters", "Common_SeeAll" }, NameParts(Named(profiles, "SeeAllStarters")));
            Assert.Equal("Starter Profiles: See All", string.Format("{0}: {1}", Strings.Instance.Profiles_Starters, Strings.Instance.Common_SeeAll));
        }

        /// <summary>A list numbers the entries it is told to count and no
        /// others: the Used By chips' label leads them as an item of their
        /// list, and WPF's own count read the first of two chips as "2 of
        /// 3". The Devices page numbers its chips as their containers are
        /// made and as New Controller comes and goes.</summary>
        [Fact]
        public void AListNumbersTheEntriesItCounts_AndNotItsLabel() => RunSta(() =>
        {
            var label = new TextBlock { Text = "Used By" };
            var button = new Grid();
            var list = new ItemsControl();
            list.Items.Add(label);
            list.Items.Add("Xbox #1");
            list.Items.Add("Keyboard + Mouse #1");
            list.Items.Add(button);
            Shown(list, () =>
            {
                DependencyObject Of(object item) => list.ItemContainerGenerator.ContainerFromItem(item);
                (int Place, int Size) Read(object item)
                    => (System.Windows.Automation.AutomationProperties.GetPositionInSet(Of(item)), System.Windows.Automation.AutomationProperties.GetSizeOfSet(Of(item)));

                ListItemPeers.Number(list, item => !ReferenceEquals(item, label));
                Assert.Equal((1, 3), Read("Xbox #1"));
                Assert.Equal((2, 3), Read("Keyboard + Mouse #1"));
                Assert.Equal((3, 3), Read(button));
                Assert.Equal((-1, -1), Read(label));

                // The button gone: two chips.
                ListItemPeers.Number(list, item => !ReferenceEquals(item, label) && !ReferenceEquals(item, button));
                Assert.Equal((1, 2), Read("Xbox #1"));
                Assert.Equal((2, 2), Read("Keyboard + Mouse #1"));
            });

            string page = RepoText("PadForge.App", "Views", "DevicesPage.xaml.cs");
            Assert.Contains("if (UsedByChips.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)", page);
            Assert.Contains("item => !ReferenceEquals(item, UsedByLabel) && (canAdd || !ReferenceEquals(item, NewControllerChip.Parent))", page);
            Assert.Contains("if (e.PropertyName == nameof(ViewModels.DevicesViewModel.CanAddController))", page);
        });

        /// <summary>A label that leads a list's entries as one of the list's
        /// items is passed over. The list reads the label's words as its
        /// name, and left a plain text the label was also an entry that
        /// repeated them and held nothing, which is what the audit found on the
        /// Devices page's Used By. An item of a plain list answers what its
        /// own element's peer answers, so the label's peer is neither a
        /// control nor a content element. A plain text and a holder beside
        /// it stay entries.</summary>
        [Fact]
        public void AListsLabel_IsNotOneOfItsEntries() => RunSta(() =>
        {
            var label = new ListLabel { Text = "Used By" };
            var plain = new TextBlock { Text = "Plain" };
            var holder = new Grid();
            holder.Children.Add(new Button { Content = "New Controller" });
            var list = new ItemsControl();
            list.Items.Add(label);
            list.Items.Add(plain);
            list.Items.Add(holder);
            Shown(list, () =>
            {
                var items = UIElementAutomationPeer.CreatePeerForElement(list).GetChildren();
                Assert.Equal(3, items.Count);
                Assert.False(items[0].IsControlElement());
                Assert.False(items[0].IsContentElement());
                Assert.True(items[1].IsControlElement());
                Assert.True(items[2].IsControlElement());
            });

            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument devices = XDocument.Parse(RepoText("PadForge.App", "Views", "DevicesPage.xaml"));
            XElement usedBy = devices.Descendants().Single(e => (string)e.Attribute(x + "Name") == "UsedByLabel");
            Assert.Equal("ListLabel", usedBy.Name.LocalName);
            // The words the label shows are the words the list is named by.
            XElement chips = devices.Descendants().Single(e => (string)e.Attribute(x + "Name") == "UsedByChips");
            Assert.Equal((string)usedBy.Attribute("Text"),
                chips.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        });

        /// <summary>The mapping list's filter box read "Picture": the words
        /// of the part switch before it had become its label. It reads the
        /// words it shows while empty.</summary>
        [Fact]
        public void TheMappingFilterBox_ReadsItsOwnWords()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument pad = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            XElement box = pad.Descendants().Single(e => (string)e.Attribute(x + "Name") == "MappingFilterSearchBox");
            Assert.Equal("{Binding Mapping_FilterSearch_Placeholder, Source={x:Static strings:Strings.Instance}}",
                box.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
            Assert.Equal((string)box.Attribute("PlaceholderText"), box.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        }

        /// <summary>The Devices page's test pictures: the mouse is a
        /// drawing and reads as one picture with its label's word, and the
        /// two MIDI pictures read their headings.</summary>
        [Fact]
        public void TheDevicesTestPictures_ReadTheirLabels()
        {
            string S(string key) => "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument devices = XDocument.Parse(RepoText("PadForge.App", "Views", "DevicesPage.xaml"));
            XElement mouse = devices.Descendants().Single(e => e.Name.LocalName == "MousePreviewControl");
            Assert.Equal("NamedPicture", mouse.Parent.Name.LocalName);
            Assert.Equal(S("Devices_Mouse"), mouse.Parent.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
            foreach (var (name, key) in new[] { ("MidiNotesPreview", "Preview_NoteOutputs"), ("MidiCcPreview", "Preview_CCOutputs") })
                Assert.Equal(S(key), devices.Descendants().Single(e => (string)e.Attribute(x + "Name") == name)
                    .Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        }

        /// <summary>A color's box holds a HEX box and three sliders, and
        /// the boxes of several colors stand side by side, so by Tab alone
        /// HEX, R, G and B read alike for each color. A box under a label
        /// stands in a group that reads the label. A box that hides itself
        /// hides its group with it: an empty group would read as on screen
        /// with no rectangle.</summary>
        [Fact]
        public void AColorsBox_StandsInAGroupThatReadsTheColorsName()
        {
            XDocument pad = XDocument.Parse(RepoText("PadForge.App", "Views", "PadPage.xaml"));
            foreach (string key in new[]
            {
                "Pad_Steering_Lock_LightbarColor", "Pad_Lighting_BatteryLowColor", "Pad_Lighting_BatteryHighColor",
                "Pad_Lighting_AudioLightbar_LowColor", "Pad_Lighting_AudioLightbar_MidColor", "Pad_Lighting_AudioLightbar_HighColor",
                "Pad_Lighting_InputReactive_FixedColor",
            })
            {
                string name = "{Binding " + key + ", Source={x:Static strings:Strings.Instance}}";
                XElement group = pad.Descendants().Single(e => e.Name.LocalName == "GroupBox"
                    && e.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name" && a.Value == name));
                Assert.Equal("{StaticResource BareGroupBox}", (string)group.Attribute("Style"));
                XElement box = Assert.Single(group.Elements());
                Assert.Equal("Border", box.Name.LocalName);
                Assert.Single(group.Descendants(), e => e.Name.LocalName == "ColorPickerControl");
                Assert.Null(box.Attribute("Visibility"));
            }
            XElement fixedColor = pad.Descendants().Single(e => e.Name.LocalName == "GroupBox" && e.Attributes().Any(a =>
                a.Name.LocalName == "AutomationProperties.Name" && a.Value.Contains("Pad_Lighting_InputReactive_FixedColor")));
            Assert.Equal("{Binding DeviceConfig.IsInputReactiveFixed, Converter={StaticResource BoolToVisibilityConverter}}", (string)fixedColor.Attribute("Visibility"));
        }

        // ── The order Tab takes ──

        private static readonly HashSet<string> TabStops = new()
        {
            "Button", "ToggleButton", "RepeatButton", "CheckBox", "RadioButton", "ComboBox", "TextBox", "NumberBox", "PasswordBox", "Slider",
            "SettingResetButton", "ListBox", "HyperlinkButton", "SegmentButton", "StateToggleButton", "AvailabilityButton", "ToggleSwitch",
            "AutoSuggestBox", "SearchField",
        };

        private static bool HoldsTabStop(XElement e) => e.DescendantsAndSelf().Any(d => TabStops.Contains(d.Name.LocalName));

        /// <summary>A DockPanel lays a right-docked child out before the
        /// child that fills the rest, so the right-docked one is written
        /// first, and Tab follows the order things are written in: it
        /// reached a row's reset, or a list head's New and More buttons,
        /// before the control at their left. Each such DockPanel is a group
        /// of its own for Tab, and its children take a TabIndex in the
        /// order they show: the ones at the left 0, the right-docked ones 1
        /// and up, the one written first last.</summary>
        [Fact]
        public void ADockPanelsTabOrder_FollowsWhatItShows()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            int panels = 0;
            var wrong = new List<string>();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(dir.FullName, "PadForge.App"), "*.xaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;
                XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                foreach (XElement dock in doc.Descendants().Where(e => e.Name.LocalName == "DockPanel"))
                {
                    var kids = dock.Elements().Where(e => !e.Name.LocalName.StartsWith("DockPanel.", StringComparison.Ordinal) && HoldsTabStop(e)).ToList();
                    bool Right(XElement e) => e.Attributes().Any(a => a.Name.LocalName == "DockPanel.Dock" && a.Value == "Right");
                    var right = kids.Where(Right).ToList();
                    if (!right.Any(r => kids.Skip(kids.IndexOf(r) + 1).Any(o => !Right(o))))
                        continue;
                    panels++;
                    string where = Path.GetFileName(file) + ":" + ((System.Xml.IXmlLineInfo)dock).LineNumber;
                    if (dock.Attributes().FirstOrDefault(a => a.Name.LocalName == "KeyboardNavigation.TabNavigation")?.Value != "Local")
                    {
                        wrong.Add(where + " is no group for Tab");
                        continue;
                    }
                    int Index(XElement e)
                    {
                        string own = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "TabIndex")?.Value
                            ?? e.Attributes().FirstOrDefault(a => a.Name.LocalName == "KeyboardNavigation.TabIndex")?.Value;
                        return own == null ? -1 : int.Parse(own, CultureInfo.InvariantCulture);
                    }
                    // What shows from the left: the children at the left as written, then the right-docked ones, the last written first.
                    var shown = kids.Where(k => !Right(k)).Concat(Enumerable.Reverse(right)).ToList();
                    var byTab = kids.Select((k, written) => (k, written)).OrderBy(p => Index(p.k)).ThenBy(p => p.written).Select(p => p.k).ToList();
                    if (kids.Any(k => Index(k) < 0) || !shown.SequenceEqual(byTab))
                        wrong.Add(where + " tabs " + string.Join(", ", byTab.Select(k => k.Name.LocalName + "=" + Index(k))));
                    // A panel's index counts only when the panel is a group for Tab.
                    foreach (XElement k in kids.Where(k => !TabStops.Contains(k.Name.LocalName)))
                        if (k.Attributes().FirstOrDefault(a => a.Name.LocalName == "KeyboardNavigation.TabNavigation")?.Value != "Local")
                            wrong.Add(where + ": " + k.Name.LocalName + " is no group for Tab");
                }
            }
            Assert.True(panels >= 17, "the scan found " + panels + " such panels");
            Assert.True(wrong.Count == 0, string.Join("; ", wrong));
        }

        /// <summary>The same in a live panel: Tab goes to the control at the
        /// left, then to the right-docked one that is written before
        /// it.</summary>
        [Fact]
        public void TabReachesTheLeftControlBeforeTheRightDockedOne() => RunSta(() =>
        {
            var panel = (DockPanel)XamlReader.Parse(
                "<DockPanel " + Presentation + " KeyboardNavigation.TabNavigation=\"Local\">" +
                "<Button x:Name=\"New\" DockPanel.Dock=\"Right\" TabIndex=\"2\" Content=\"New\"/>" +
                "<Button x:Name=\"More\" DockPanel.Dock=\"Right\" TabIndex=\"1\" Content=\"More\"/>" +
                "<StackPanel x:Name=\"Left\" KeyboardNavigation.TabNavigation=\"Local\" KeyboardNavigation.TabIndex=\"0\">" +
                "<TextBox x:Name=\"First\"/><TextBox x:Name=\"Second\"/></StackPanel></DockPanel>");
            var before = new Button { Content = "Before" };
            var after = new Button { Content = "After" };
            var host = new StackPanel();
            host.Children.Add(before);
            host.Children.Add(panel);
            host.Children.Add(after);
            Shown(host, () =>
            {
                var window = Window.GetWindow(host);
                var order = new List<string>();
                before.Focus();
                UIElement at = before;
                for (int i = 0; i < 5; i++)
                {
                    at.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
                    at = (UIElement)System.Windows.Input.FocusManager.GetFocusedElement(window);
                    order.Add(at is FrameworkElement fe && fe.Name.Length > 0 ? fe.Name : (at as Button)?.Content as string);
                }
                Assert.Equal(new[] { "First", "Second", "More", "New", "After" }, order.ToArray());
            });
        });

        // ── The shell ──

        [Fact]
        public void TheNavigationViewsEmptyFrame_TakesNoFocus()
        {
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XDocument shell = XDocument.Parse(RepoText("PadForge.App", "Resources", "Shell.xaml"));
            XElement frame = shell.Descendants().Single(e => (string)e.Attribute(x + "Name") == "PART_NavigationViewContentPresenter");
            Assert.Equal("False", (string)frame.Attribute("Focusable"));
            Assert.Equal("False", (string)frame.Attribute("IsTabStop"));
        }
    }
}
