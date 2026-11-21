using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PadForge.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard and screen-reader work changes nothing a mouse user sees.
    /// Each element it put in place of another draws the same pixels as the
    /// element it replaced, rendered side by side here: a ClickBorder in
    /// place of a Border, a GlyphText in place of a TextBlock, the motor test
    /// area's ClickBorder around the Grid that held its attributes, the
    /// Paired PCs list inside a Border that holds its margin, empty or not,
    /// the three Button subclasses in place of Buttons, the decorative icons
    /// in place of wpf-ui's, a DecorativeImage in place of an Image and a
    /// ContentTabControl in place of a TabControl.
    /// </summary>
    public class AccessibilityLooksTheSameTests
    {
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

        private static byte[] Pixels(FrameworkElement element, int width, int height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        private static void SameDrawing(FrameworkElement before, FrameworkElement after, int width = 160, int height = 60)
        {
            byte[] a = Pixels(before, width, height);
            byte[] b = Pixels(after, width, height);
            Assert.True(a.Any(p => p != 0), "the reference drew nothing, so the comparison proves nothing");
            int differing = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3])
                    differing++;
            Assert.True(differing == 0, differing + " pixel(s) differ");
        }

        private static Style ChipStyle()
        {
            var style = new Style(typeof(Border));
            style.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x33))));
            style.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x58, 0xB6, 0xE4))));
            style.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(6)));
            style.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(8, 3, 8, 3)));
            return style;
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        [Fact]
        public void AClickBorder_DrawsAsTheBorderItReplaced() => RunSta(() =>
        {
            var border = new Border { Style = ChipStyle(), Child = Label("GAMEPAD 9"), Cursor = System.Windows.Input.Cursors.Hand };
            var chip = new ClickBorder { Style = ChipStyle(), Child = Label("GAMEPAD 9"), Cursor = System.Windows.Input.Cursors.Hand, IsSelected = false };
            SameDrawing(border, chip);

            var selected = new ClickBorder { Style = ChipStyle(), Child = Label("ALL 36"), IsSelected = true };
            SameDrawing(new Border { Style = ChipStyle(), Child = Label("ALL 36") }, selected);

            var preset = new ClickBorder { Style = ChipStyle(), Child = Label("Preset"), IsChecked = true, ClickMode = ClickMode.Press };
            SameDrawing(new Border { Style = ChipStyle(), Child = Label("Preset") }, preset);
        });

        [Fact]
        public void AGlyphText_DrawsAsTheTextBlockItReplaced() => RunSta(() =>
        {
            TextBlock Arrow(TextBlock tb)
            {
                tb.Text = "▾";
                tb.FontSize = 9;
                tb.Margin = new Thickness(5, 0, 0, 0);
                tb.Opacity = 0.7;
                tb.Foreground = Brushes.White;
                tb.VerticalAlignment = VerticalAlignment.Center;
                return tb;
            }
            SameDrawing(Arrow(new TextBlock()), Arrow(new GlyphText()), 40, 20);
        });

        [Fact]
        public void TheMotorTestArea_DrawsAsTheGridItWrapped() => RunSta(() =>
        {
            Grid Content(Grid grid)
            {
                grid.Children.Add(new Border { Height = 32, CornerRadius = new CornerRadius(16), Background = new SolidColorBrush(Color.FromRgb(0x25, 0x30, 0x49)) });
                grid.Children.Add(new TextBlock { Text = "Left Motor", FontSize = 13, Foreground = Brushes.Gainsboro, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                return grid;
            }
            var before = Content(new Grid { Margin = new Thickness(0, 0, 12, 0), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand });
            var after = new ClickBorder
            {
                Margin = new Thickness(0, 0, 12, 0),
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand,
                ClickMode = ClickMode.Press,
                Child = Content(new Grid()),
            };
            SameDrawing(before, after, 200, 40);
        });

        [Fact]
        public void ThePairedPcsList_KeepsItsPlace_EmptyOrNot() => RunSta(() =>
        {
            Style Collapsing()
            {
                var style = new Style(typeof(ItemsControl));
                style.Setters.Add(new Setter(UIElement.FocusableProperty, false));
                var empty = new Trigger { Property = ItemsControl.HasItemsProperty, Value = false };
                empty.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
                style.Triggers.Add(empty);
                return style;
            }
            StackPanel Card(bool wrapped, string[] peers)
            {
                var list = new ItemsControl { ItemsSource = peers };
                list.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(TextBlock)) };
                list.ItemTemplate.VisualTree.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
                list.ItemTemplate.VisualTree.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                var panel = new StackPanel { Background = Brushes.Black };
                panel.Children.Add(Label("Paired PCs"));
                if (wrapped)
                {
                    list.Style = Collapsing();
                    panel.Children.Add(new Border { Margin = new Thickness(0, 12, 0, 0), Child = list });
                }
                else
                {
                    list.Margin = new Thickness(0, 12, 0, 0);
                    panel.Children.Add(list);
                }
                panel.Children.Add(Label("Revoke All"));
                return panel;
            }
            SameDrawing(Card(false, Array.Empty<string>()), Card(true, Array.Empty<string>()), 200, 80);
            SameDrawing(Card(false, new[] { "OFFICE-PC", "DEN-LAPTOP" }), Card(true, new[] { "OFFICE-PC", "DEN-LAPTOP" }), 200, 100);
        });

        // An implicit style keyed by the base type, the way App.xaml keys its
        // Button style and wpf-ui keys its own, on a host the element sits in.
        // WPF finds an implicit style by the element's own type, so a subclass
        // gets this one only through BaseStyle.
        private static Border Host(Type key, Style implicitStyle, UIElement child)
        {
            var host = new Border { Background = Brushes.Black };
            host.Resources[key] = implicitStyle;
            host.Child = child;
            return host;
        }

        // Built the way XAML builds an element: its properties set between
        // BeginInit and EndInit, where Initialized fires.
        private static T Built<T>(T element, Action<T> set) where T : FrameworkElement
        {
            element.BeginInit();
            set(element);
            element.EndInit();
            return element;
        }

        private static Style TileStyle(Color fill)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(fill)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C))));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(2)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 4, 10, 4)));
            var chrome = new FrameworkElementFactory(typeof(Border));
            foreach (var (to, from) in new[]
            {
                (Border.BackgroundProperty, Control.BackgroundProperty),
                (Border.BorderBrushProperty, Control.BorderBrushProperty),
                (Border.BorderThicknessProperty, Control.BorderThicknessProperty),
                (Border.PaddingProperty, Control.PaddingProperty),
            })
                chrome.SetBinding(to, new System.Windows.Data.Binding { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent, Path = new PropertyPath(from) });
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Button)) { VisualTree = chrome }));
            return style;
        }

        [Fact]
        public void TheButtonSubclasses_TakeTheButtonStyle_WhenNothingElseStylesThem() => RunSta(() =>
        {
            // The sidebar's power flame and Add Controller entries set no
            // style of their own, as the plain Buttons before them did not.
            var buttonStyle = TileStyle(Color.FromRgb(0x1B, 0x23, 0x33));
            Button Tile(Button b) => Built(b, x => x.Content = Label("Nintendo"));
            Border Reference() => Host(typeof(Button), buttonStyle, Tile(new Button()));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new SegmentButton { IsSelected = true })));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new StateToggleButton { IsOn = true })));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new AvailabilityButton { IsAvailable = false })));
        });

        [Fact]
        public void TheButtonSubclasses_KeepTheStyleTheyAreGiven() => RunSta(() =>
        {
            var buttonStyle = TileStyle(Color.FromRgb(0x1B, 0x23, 0x33));
            var own = TileStyle(Color.FromRgb(0x2E, 0x6A, 0x8F));
            Button Tile(Button b) => Built(b, x => { x.Style = own; x.Content = Label("Xbox"); });
            Border Reference() => Host(typeof(Button), buttonStyle, Tile(new Button()));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new SegmentButton())));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new StateToggleButton())));
            SameDrawing(Reference(), Host(typeof(Button), buttonStyle, Tile(new AvailabilityButton())));
        });

        [Fact]
        public void TheButtonSubclasses_KeepTheStyleTheirTemplateSets() => RunSta(() =>
        {
            // A style the DataTemplate sets, as Home's slot card sets
            // its type tiles'. The first build of the change replaced it
            // with the Button style, and the tiles lost their look.
            var buttonStyle = TileStyle(Color.FromRgb(0x1B, 0x23, 0x33));
            ContentPresenter FromTemplate(string element)
            {
                string xaml =
                    "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' "
                    + "xmlns:c='clr-namespace:PadForge.Controls;assembly=" + typeof(SegmentButton).Assembly.GetName().Name + "'>"
                    + "<" + element + " Content='PS'><Button.Style><Style TargetType='Button'>"
                    + "<Setter Property='Background' Value='#2E6A8F'/><Setter Property='Foreground' Value='White'/>"
                    + "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>"
                    + "<Border Background='{TemplateBinding Background}' Padding='8,3'><ContentPresenter/></Border>"
                    + "</ControlTemplate></Setter.Value></Setter></Style></Button.Style></" + element + "></DataTemplate>";
                var template = (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
                return new ContentPresenter { Content = new object(), ContentTemplate = template };
            }
            SameDrawing(Host(typeof(Button), buttonStyle, FromTemplate("Button")),
                        Host(typeof(Button), buttonStyle, FromTemplate("c:SegmentButton")));
            SameDrawing(Host(typeof(Button), buttonStyle, FromTemplate("Button")),
                        Host(typeof(Button), buttonStyle, FromTemplate("c:StateToggleButton")));
        });

        [Fact]
        public void TheDecorativeIconsImageAndTabControl_TakeTheirBaseStyles() => RunSta(() =>
        {
            Style IconStyle(Type type)
            {
                var style = new Style(type);
                style.Setters.Add(new Setter(Wpf.Ui.Controls.FontIcon.FontSizeProperty, 20.0));
                style.Setters.Add(new Setter(Wpf.Ui.Controls.IconElement.ForegroundProperty, Brushes.White));
                return style;
            }
            Wpf.Ui.Controls.FontIcon Glyph(Wpf.Ui.Controls.FontIcon icon) =>
                Built(icon, x => { x.Glyph = ""; x.FontFamily = new FontFamily("Segoe MDL2 Assets"); });
            SameDrawing(Host(typeof(Wpf.Ui.Controls.FontIcon), IconStyle(typeof(Wpf.Ui.Controls.FontIcon)), Glyph(new Wpf.Ui.Controls.FontIcon())),
                        Host(typeof(Wpf.Ui.Controls.FontIcon), IconStyle(typeof(Wpf.Ui.Controls.FontIcon)), Glyph(new DecorativeFontIcon())), 40, 40);

            // SymbolIcon reads its font from the FluentSystemIcons resource
            // wpf-ui's dictionaries hold. Any font stands in for it here: the
            // two draw whatever it draws for the symbol.
            Border SymbolHost(Wpf.Ui.Controls.SymbolIcon icon)
            {
                var host = Host(typeof(Wpf.Ui.Controls.SymbolIcon), IconStyle(typeof(Wpf.Ui.Controls.SymbolIcon)),
                    Built(icon, x => x.Symbol = Wpf.Ui.Controls.SymbolRegular.Home24));
                host.Resources["FluentSystemIcons"] = new FontFamily("Segoe UI");
                return host;
            }
            SameDrawing(SymbolHost(new Wpf.Ui.Controls.SymbolIcon()), SymbolHost(new DecorativeSymbolIcon()), 40, 40);

            var imageStyle = new Style(typeof(Image));
            imageStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0.6));
            var picture = new DrawingImage(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2C)), null,
                new EllipseGeometry(new Point(12, 12), 10, 6)));
            Image Picture(Image image) => Built(image, x => { x.Source = picture; x.Width = 24; x.Height = 24; });
            SameDrawing(Host(typeof(Image), imageStyle, Picture(new Image())), Host(typeof(Image), imageStyle, Picture(new DecorativeImage())), 40, 40);

            var tabStyle = new Style(typeof(TabControl));
            tabStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x1B, 0x23, 0x33))));
            TabControl Tabs(TabControl tabs) => Built(tabs, x =>
            {
                x.Items.Add(new TabItem { Header = "Preview", Content = Label("Preview") });
                x.Items.Add(new TabItem { Header = "Mappings", Content = Label("Mappings") });
                x.SelectedIndex = 0;
            });
            SameDrawing(Host(typeof(TabControl), tabStyle, Tabs(new TabControl())), Host(typeof(TabControl), tabStyle, Tabs(new ContentTabControl())), 200, 80);
        });

        [Fact]
        public void ACurveEditorWithoutKeyboardFocus_MarksNoPoint() => RunSta(() =>
        {
            var editor = new CurveEditor { CurveString = "0,0;0.5,0.25;1,1", IsSigned = false, IsFullWidth = true, ChartHeight = 150, Width = 400 };
            var window = new Window
            {
                Width = 500, Height = 300, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, Content = editor,
            };
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Assert.False(editor.IsKeyboardFocused);
                var canvas = (Canvas)editor.FindName("ChartCanvas");
                // The handles are the 10-pixel ellipses. The 7-pixel live dot
                // is ember-hot by design.
                var handles = canvas.Children.OfType<Ellipse>().Where(e => e.Width == 10).ToList();
                Assert.Equal(3, handles.Count);
                var emberHot = Color.FromRgb(0xFF, 0xA2, 0x4D);
                Assert.DoesNotContain(handles, h => h.Fill is SolidColorBrush b && b.Color == emberHot);
                Assert.All(handles, h => Assert.Null(h.Effect));
                // The coordinate readout shows only during a drag, as before.
                Assert.DoesNotContain(canvas.Children.OfType<TextBlock>(), t => t.Visibility == Visibility.Visible);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
