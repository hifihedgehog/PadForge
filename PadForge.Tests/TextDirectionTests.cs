using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Every TextBlock in a right-to-left window takes the direction its own
    /// text needs (#505). WPF lays each TextBlock out in its window's direction
    /// and has no bracket-pair rule: a device named "Xbox One Controller
    /// (2015)" read "(2015) Xbox One Controller", and "Igor Budzhak (MIT)"
    /// followed by Arabic put the closing bracket on the far side of the name.
    /// Each test checks where WPF draws the characters, in window
    /// coordinates, and the device name test draws the same name unwatched
    /// beside it as the positive control.
    /// </summary>
    public class TextDirectionTests
    {
        // Letters, not words: right-to-left text on either side of a credit.
        private const string Before = "ابت ";
        private const string After = " ثجح";
        private const string Device = "Xbox One Controller (2015)";

        // Three later values: the first update and the ones after it must all arrive.
        private static readonly string[] Credits = { "chmfp (GPL-3.0-or-later)", "antifier (MIT)", "VRPN (BSL-1.0)" };

        private sealed class Source : INotifyPropertyChanged
        {
            private string _value;
            public string Value
            {
                get => _value;
                set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

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

        /// <summary>A right-to-left window, watched or not, that stays as
        /// built once it has loaded.
        ///
        /// <para>UiLanguageTests calls UiLanguage.Start, which registers a
        /// class handler on Window.Loaded for the rest of the test process
        /// (EventManager class handlers cannot be removed). Once it has run,
        /// every window that loads takes the direction and the Watch value of
        /// the loading thread's UI culture, and on this English thread that
        /// turned these windows left to right with Watch off. Three tests of
        /// this class failed whenever the start happened to run first. So the
        /// window's direction, language and Watch are set again after it
        /// loads.</para></summary>
        private static Window Show(UIElement content, bool watch)
        {
            TextDirection.Start();
            var language = XmlLanguage.GetLanguage("ar");
            var window = new Window
            {
                Content = content, Width = 700, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, FlowDirection = FlowDirection.RightToLeft,
                Language = language,
            };
            if (watch) TextDirection.SetWatch(window, true);
            window.Show();
            window.Language = language;
            window.FlowDirection = FlowDirection.RightToLeft;
            TextDirection.SetWatch(window, watch);
            Drain(window);
            return window;
        }

        private static void Drain(Window window)
            => window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        /// <summary>Where the character at <paramref name="index"/> starts, in
        /// window coordinates, whichever Run holds it.</summary>
        private static double X(TextBlock block, Visual root, int index)
        {
            TextPointer p = block.ContentStart;
            int seen = 0;
            while (p != null)
            {
                if (p.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    int length = p.GetTextRunLength(LogicalDirection.Forward);
                    if (index < seen + length)
                    {
                        Rect r = p.GetPositionAtOffset(index - seen).GetCharacterRect(LogicalDirection.Forward);
                        return block.TransformToAncestor(root).Transform(r.TopLeft).X;
                    }
                    seen += length;
                    p = p.GetPositionAtOffset(length);
                }
                else
                    p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
            throw new InvalidOperationException("index " + index + " is past the text");
        }

        /// <summary>The text a block shows, Runs joined.</summary>
        private static string Shown(TextBlock block) => new TextRange(block.ContentStart, block.ContentEnd).Text;

        private static TextBlock Find(DependencyObject root)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock block) return block;
                var found = Find(child);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>"Name (note)" reads left to right on screen.</summary>
        private static void AssertReadsLeftToRight(TextBlock block, Visual root, string text, string credit)
        {
            int start = text.IndexOf(credit, StringComparison.Ordinal);
            Assert.True(start >= 0, credit + " is not in " + text);
            int open = text.IndexOf('(', start), close = text.IndexOf(')', start);
            double name = X(block, root, start), o = X(block, root, open), note = X(block, root, open + 1), c = X(block, root, close);
            Assert.True(name < o && o < note && note < c,
                $"{credit} is not drawn left to right: name {name}, ( {o}, note {note}, ) {c}");
        }

        // ─────────────────────────────────────────────
        //  Latin text takes the direction of its first letter
        // ─────────────────────────────────────────────

        [Theory]
        [InlineData("Xbox 360", false)]
        [InlineData("PadForge 5.0", false)]
        [InlineData("v5.0", false)]
        [InlineData(Device, true)]
        [InlineData("Controller (XBOX 360 For Windows)", true)]
        [InlineData("2 Players", true)]
        [InlineData("Loading…", true)]
        [InlineData("{Delete}", true)]
        [InlineData("  Xbox  ", false)]
        [InlineData("", false)]
        public void LatinTextThatARightToLeftLineDrawsDifferentlyIsFound(string text, bool needs)
            => Assert.Equal(needs, TextDirection.NeedsLeftToRight(text));

        [Fact]
        public void ADeviceNameReadsLeftToRight()
        {
            RunSta(() =>
            {
                var watched = new TextBlock { Text = Device };
                var plain = new TextBlock { Text = Device };
                Window window = Show(watched, watch: true), control = Show(plain, watch: false);
                try
                {
                    Assert.Equal(FlowDirection.LeftToRight, watched.FlowDirection);
                    AssertReadsLeftToRight(watched, window, Device, Device);
                    // The same name unwatched: WPF draws "(2015)" left of "Xbox".
                    Assert.True(X(plain, control, Device.IndexOf('(')) < X(plain, control, 0),
                        "unwatched, the name no longer draws its brackets first: WPF may lay out Latin text by its first letter now");
                }
                finally { window.Close(); control.Close(); }
            });
        }

        /// <summary>The block keeps its place: its alignment and padding are
        /// mirrored with its direction, and both come back when the window
        /// turns left to right.</summary>
        [Fact]
        public void AFlippedBlockKeepsItsPlaceAndComesBack()
        {
            RunSta(() =>
            {
                var block = new TextBlock { Text = Device, Padding = new Thickness(8, 0, 2, 0) };
                Window window = Show(block, watch: true);
                try
                {
                    Assert.Equal(TextAlignment.Right, block.TextAlignment);
                    Assert.Equal(new Thickness(2, 0, 8, 0), block.Padding);

                    TextDirection.SetWatch(window, false);
                    Drain(window);
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(TextBlock.TextAlignmentProperty));
                    Assert.Equal(new Thickness(8, 0, 2, 0), block.Padding);
                    Assert.Equal(FlowDirection.RightToLeft, block.FlowDirection);
                }
                finally { window.Close(); }
            });
        }

        [Fact]
        public void TextInALeftToRightPinIsLeftAlone()
        {
            RunSta(() =>
            {
                var block = new TextBlock { Text = Device };
                var pin = new Border { FlowDirection = FlowDirection.LeftToRight, Child = block };
                Window window = Show(pin, watch: true);
                try
                {
                    Assert.Equal(DependencyProperty.UnsetValue, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    Assert.Equal(TextAlignment.Left, block.TextAlignment);
                }
                finally { window.Close(); }
            });
        }

        // ─────────────────────────────────────────────
        //  Mixed text gets its Latin credits as Runs
        // ─────────────────────────────────────────────

        /// <summary>New Inlines cost a laid-out TextBlock the binding on its
        /// Text, so the text is taken over from that binding's source: every
        /// later value arrives, with its credits as Runs.</summary>
        [Fact]
        public void BoundMixedTextKeepsItsCreditsTogether()
        {
            RunSta(() =>
            {
                var source = new Source { Value = Before + "Igor Budzhak (MIT)" + After };
                var block = new TextBlock();
                block.SetBinding(TextBlock.TextProperty, new Binding(nameof(Source.Value)) { Source = source });
                Window window = Show(block, watch: true);
                try
                {
                    AssertReadsLeftToRight(block, window, source.Value, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        source.Value = Before + credit + After;
                        Drain(window);
                        Assert.Equal(source.Value, Shown(block));
                        AssertReadsLeftToRight(block, window, source.Value, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>Text a data template binds is taken over: the Runs follow
        /// the item from then on.</summary>
        [Fact]
        public void TemplateTextFollowsItsItem()
        {
            RunSta(() =>
            {
                var item = new Source { Value = Before + "Igor Budzhak (MIT)" + After };
                var factory = new FrameworkElementFactory(typeof(TextBlock));
                factory.SetBinding(TextBlock.TextProperty, new Binding(nameof(Source.Value)));
                var list = new ItemsControl
                {
                    ItemsSource = new ObservableCollection<Source> { item },
                    ItemTemplate = new DataTemplate { VisualTree = factory },
                };
                Window window = Show(list, watch: true);
                try
                {
                    TextBlock block = Find(list);
                    Assert.NotNull(block);
                    AssertReadsLeftToRight(block, window, item.Value, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        item.Value = Before + credit + After;
                        Drain(window);
                        Assert.Equal(item.Value, Shown(block));
                        AssertReadsLeftToRight(block, window, item.Value, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>A string a ContentPresenter shows is taken over through
        /// its Content.</summary>
        [Fact]
        public void ContentPresenterTextFollowsItsContent()
        {
            RunSta(() =>
            {
                var content = new ContentControl { Content = Before + "Igor Budzhak (MIT)" + After };
                Window window = Show(content, watch: true);
                try
                {
                    TextBlock block = Find(content);
                    Assert.NotNull(block);
                    AssertReadsLeftToRight(block, window, (string)content.Content, "Igor Budzhak (MIT)");

                    foreach (string credit in Credits)
                    {
                        content.Content = Before + credit + After;
                        Drain(window);
                        block = Find(content);
                        Assert.Equal((string)content.Content, Shown(block));
                        AssertReadsLeftToRight(block, window, (string)content.Content, credit);
                    }
                }
                finally { window.Close(); }
            });
        }

        /// <summary>A tooltip opens outside its window's tree and is watched
        /// as it opens.</summary>
        [Fact]
        public void ATooltipIsWatchedAsItOpens()
        {
            RunSta(() =>
            {
                var button = new Button { Content = "x", Width = 40, Height = 30 };
                var tip = new ToolTip { Content = Device, PlacementTarget = button };
                button.ToolTip = tip;
                Window window = Show(button, watch: true);
                try
                {
                    tip.IsOpen = true;
                    Drain(window);
                    TextBlock block = Find(tip);
                    Assert.NotNull(block);
                    Assert.Equal(FlowDirection.RightToLeft, tip.FlowDirection);
                    Assert.True(TextDirection.GetWatch(tip));
                    Assert.Equal(FlowDirection.LeftToRight, block.ReadLocalValue(FrameworkElement.FlowDirectionProperty));
                    tip.IsOpen = false;
                }
                finally { window.Close(); }
            });
        }

        // ─────────────────────────────────────────────
        //  The sidebar's slot pills (6.0.0 prep, settings-language-rtl)
        // ─────────────────────────────────────────────

        private const string LongName = "Nintendo Switch Pro Controller";

        /// <summary>The pill's instance label: "#1" keeps its sign before its
        /// number, as Unicode's rule for a number sign has it. Unwatched, WPF
        /// drew "1#" in the Arabic frame.</summary>
        [Fact]
        public void ANumberSignStaysBeforeItsNumber()
        {
            RunSta(() =>
            {
                var watched = new TextBlock { Text = "#1", Width = 22, FontSize = 12 };
                var plain = new TextBlock { Text = "#1", Width = 22, FontSize = 12 };
                Window window = Show(watched, watch: true), control = Show(plain, watch: false);
                try
                {
                    Assert.True(X(plain, control, 0) > X(plain, control, 1),
                        "unwatched, WPF draws the sign before the number now: the positive control is gone");
                    Assert.True(X(watched, window, 0) < X(watched, window, 1),
                        $"# at {X(watched, window, 0)}, 1 at {X(watched, window, 1)}");
                    // It keeps its place: the start side of its box, the right.
                    Assert.Equal(TextAlignment.Right, watched.TextAlignment);
                }
                finally { window.Close(); control.Close(); }
            });
        }

        /// <summary>A Latin name too long for its box is cut at its end with
        /// the ellipsis on the right, its first letter at the box's left edge.
        /// A right-to-left line put the ellipsis on the left, before the
        /// name's first letter ("…Nintendo Switch").</summary>
        [Fact]
        public void ATrimmedLatinNameIsCutAtItsEnd()
        {
            RunSta(() =>
            {
                TextBlock Cut() => new TextBlock
                {
                    Text = LongName, Width = 120, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                TextBlock watched = Cut(), plain = Cut();
                Window window = Show(watched, watch: true), control = Show(plain, watch: false);
                try
                {
                    Rect plainBox = plain.TransformToAncestor(control).TransformBounds(new Rect(plain.RenderSize));
                    double plainStart = X(plain, control, 0);
                    // Unwatched, the ellipsis stands before the name's first
                    // letter, on the left.
                    Assert.True(plainStart > plainBox.Left + 1,
                        $"unwatched, the name starts at {plainStart} in a box from {plainBox.Left}: the positive control is gone");
                    Rect box = watched.TransformToAncestor(window).TransformBounds(new Rect(watched.RenderSize));
                    Assert.InRange(X(watched, window, 0), box.Left - 0.5, box.Left + 0.5);
                }
                finally { window.Close(); control.Close(); }
            });
        }

        /// <summary>Text the class leaves as it is posts no dispatcher work: a
        /// trimmed line of right-to-left letters only, which Apply neither
        /// flips nor takes over, and any text once Watch is off again, as
        /// after a switch from Arabic to English. Latin text with a bracket
        /// and Arabic text with a bracketed stretch are still looked at, the
        /// positive controls.</summary>
        [Fact]
        public void TextThisClassLeavesAlone_PostsNoWork()
        {
            RunSta(() =>
            {
                var stateProperty = (DependencyProperty)typeof(TextDirection)
                    .GetField("StateProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                    .GetValue(null);
                bool Pending(TextBlock b)
                {
                    object s = b.GetValue(stateProperty);
                    return s != null && (bool)s.GetType().GetField("Pending").GetValue(s);
                }
                void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var latin = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                TextDirection.SetWatch(latin, true);
                latin.Text = Device;
                Assert.True(Pending(latin), "watched Latin text with a bracket is not looked at: the checks below prove nothing");

                var mixed = new TextBlock();
                TextDirection.SetWatch(mixed, true);
                mixed.Text = Before + "(2015)";
                Assert.True(Pending(mixed), "watched Arabic text with a bracketed stretch is not looked at");

                var arabic = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                TextDirection.SetWatch(arabic, true);
                arabic.Text = "وحدة تحكم طويلة الاسم";
                Assert.False(Pending(arabic), "a trimmed line of right-to-left letters posted work Apply does nothing with");
                Drain();

                TextDirection.SetWatch(latin, false);
                Drain();
                latin.Text = "Xbox One Controller (2016)";
                Assert.False(Pending(latin), "a block no longer watched posted work for a text change");
            });
        }

        /// <summary>The pill's Preset name scrolls under the pointer from its
        /// start: a Latin name's moving copy stands with its first letter at
        /// the label's left edge, and slides left from there.</summary>
        [Fact]
        public void AMarqueeLabelStartsALatinNameAtItsStart()
        {
            RunSta(() =>
            {
                var label = new PadForge.Controls.MarqueeLabel { Text = LongName, Width = 120, Height = 20 };
                Window window = Show(label, watch: true);
                try
                {
                    var track = (Canvas)VisualTreeHelper.GetChild(label, 1);
                    var run = (TextBlock)VisualTreeHelper.GetChild(track, 0);
                    Rect box = label.TransformToAncestor(window).TransformBounds(new Rect(label.RenderSize));
                    Assert.True(run.ActualWidth > box.Width, "the name fits: the checks below prove nothing");
                    Assert.InRange(X(run, window, 0), box.Left - 0.5, box.Left + 0.5);

                    // An Arabic name starts at the right edge.
                    label.Text = "وحدة تحكم طويلة الاسم جدا للاختبار هنا";
                    Drain(window);
                    Assert.True(run.ActualWidth > box.Width, "the Arabic name fits: the check below proves nothing");
                    Assert.InRange(X(run, window, 0), box.Right - 0.5, box.Right + 0.5);
                }
                finally { window.Close(); }
            });
        }

        /// <summary>Pictures the sidebar pills' text in a right-to-left
        /// window: each slot's number, its "#1" label as MainWindow builds it
        /// (UpdateControllerNavItemContent) and its Preset name in a
        /// MarqueeLabel, at rest and with the moving copy shown at its first
        /// frame. Does nothing unless PADFORGE_PAGE_RENDER_DIR names a folder.
        /// The pill's buttons, chip and outline are left out: this pictures
        /// the text, which is all TextDirection changes.</summary>
        [Fact]
        public void DrawTheSidebarPillText()
        {
            string dir = Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            RunSta(() =>
            {
                string[] names =
                {
                    "Xbox Series X|S Controller (Bluetooth)", "DualSense (PS5): Full Features", LongName,
                    "Custom", "لوحة مفاتيح + ماوس", "MIDI", "الواقع الافتراضي",
                };
                var face = new FontFamily("Segoe UI Variable Text");
                var column = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x18)), Width = 264 };
                var moving = new System.Collections.Generic.List<PadForge.Controls.MarqueeLabel>();
                for (int i = 0; i < names.Length * 2; i++)
                {
                    var row = new DockPanel { Margin = new Thickness(8, 6, 8, 6), Height = 26 };
                    var number = new TextBlock { Text = (i % names.Length + 1).ToString(), Width = 16, FontSize = 12, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    var chip = new Border { Width = 20, Height = 20, Margin = new Thickness(5, 0, 0, 0), Background = Brushes.DimGray, CornerRadius = new CornerRadius(4) };
                    var instance = new TextBlock { Text = "#1", FontSize = 12, Width = 22, Margin = new Thickness(6, 0, 0, 0), TextAlignment = TextAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
                    var preset = new PadForge.Controls.MarqueeLabel { Text = names[i % names.Length], Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    TextElement.SetFontSize(preset, 13.0);
                    if (i >= names.Length) moving.Add(preset);
                    foreach (var part in new UIElement[] { number, chip, instance, preset }) row.Children.Add(part);
                    column.Children.Add(row);
                }
                TextElement.SetFontFamily(column, face);
                TextElement.SetForeground(column, Brushes.White);
                // A right-to-left ground inside a left-to-right frame, so the
                // picture of the frame holds the ground's mirroring.
                column.FlowDirection = FlowDirection.RightToLeft;
                column.Language = XmlLanguage.GetLanguage("ar");
                var frame = new Border { Child = column, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                TextDirection.Start();
                var window = new Window
                {
                    Content = frame, Width = 400, Height = 900, ShowActivated = false, ShowInTaskbar = false,
                    Left = -10000, Top = -10000,
                };
                window.Show();
                window.FlowDirection = FlowDirection.LeftToRight;
                TextDirection.SetWatch(window, true);
                Drain(window);
                try
                {
                    // The lower seven: the moving copy at its first frame,
                    // as a hover shows it before it slides.
                    foreach (var label in moving)
                    {
                        VisualTreeHelper.GetChild(label, 0).SetValue(UIElement.VisibilityProperty, Visibility.Hidden);
                        VisualTreeHelper.GetChild(label, 1).SetValue(UIElement.VisibilityProperty, Visibility.Visible);
                    }
                    Drain(window);
                    frame.UpdateLayout();
                    const double scale = 1.5;
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)Math.Ceiling(frame.ActualWidth * scale), (int)Math.Ceiling(frame.ActualHeight * scale),
                        96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(frame);
                    var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    System.IO.Directory.CreateDirectory(dir);
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(dir, "sidebar-pill-text-rtl.png"));
                    png.Save(stream);
                }
                finally { window.Close(); }
            });
        }

        [Fact]
        public void TheLanguageWatchesOnlyARightToLeftWindow()
        {
            RunSta(() =>
            {
                var window = new Window();
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.RightToLeft);
                Assert.True(TextDirection.GetWatch(window));
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.LeftToRight);
                Assert.False(TextDirection.GetWatch(window));
                UiLanguage.KeepLeftToRight(window);
                UiLanguage.Declare(window, UiLanguage.Undeclared, FlowDirection.RightToLeft);
                Assert.False(TextDirection.GetWatch(window));
            });
        }
    }
}
