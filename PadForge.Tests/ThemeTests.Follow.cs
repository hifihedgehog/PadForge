using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Common.Themes;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.Models3D;
using PadForge.ViewModels;
using PadForge.Views;
using PadForge.Views.Controls;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// What a look change reaches, and what a theme's colors hold to: the
    /// views that draw in the look's tones follow a change while they show,
    /// the words on the accent's fills read on each fill, the colored text a
    /// theme moves reads where it stands, and a theme's picture lets the
    /// engine card's text read.
    /// </summary>
    public partial class ThemeTests
    {
        private const string NightRun = "Starter:sonic-night-run";

        private static T Field<T>(object owner, string name)
        {
            var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(field != null, owner.GetType().Name + " has no field " + name);
            return (T)field.GetValue(owner);
        }

        private static void RunFrame(object view)
        {
            var frame = view.GetType().GetMethod("OnRendering", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(frame);
            frame.Invoke(view, new object[] { null, EventArgs.Empty });
        }

        /// <summary>Lays an element out at a size and tells it it loaded, as a
        /// page that shows it does: no window.</summary>
        private static void Load(FrameworkElement element, double width, double height)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            element.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        }

        private static Window ShowOffScreen(UIElement content, double width = 900, double height = 500)
        {
            var window = new Window
            {
                Content = content, Width = width, Height = height, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000, WindowStyle = WindowStyle.None,
            };
            window.Show();
            return window;
        }

        private static Color ColorOf(Brush brush) => brush switch
        {
            SolidColorBrush solid => solid.Color,
            GradientBrush gradient => gradient.GradientStops[gradient.GradientStops.Count / 2].Color,
            _ => throw new InvalidOperationException("no color: " + brush),
        };

        /// <summary>Puts Night Run on: an accent of another hue than ember.</summary>
        private static Color ToNightRun()
        {
            ThemeManager.Apply(2, NightRun);
            Color accent = ThemeLook.Accent;
            Assert.NotEqual(EmberTheme.Accent, accent);
            return accent;
        }

        // ─────────────────────────────────────────────
        //  A view that draws in the look's tones follows a change
        // ─────────────────────────────────────────────

        [Fact]
        public void TheTriggerEffectGraphFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    var graph = new TriggerEffectGraph { Mode = AdaptiveTriggerMode.Feedback, Strength = 200, StartPosition = 40 };
                    Load(graph, 300, 90);
                    Rectangle Fill() => graph.GraphCanvas.Children.OfType<Rectangle>().Last(r => r.Fill is LinearGradientBrush);
                    Assert.Equal(EmberTheme.Accent, ColorOf(Fill().Fill));
                    Color accent = ToNightRun();
                    Assert.Equal(accent, ColorOf(Fill().Fill));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the trigger effect graph test timed out");
        }

        [Fact]
        public void TheTriggerTravelArcFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    var arc = new TriggerTravelArc();
                    Load(arc, 120, 120);
                    Assert.Equal(EmberTheme.Accent, ColorOf(Field<System.Windows.Shapes.Path>(arc, "_sweepArc").Stroke));
                    ToNightRun();
                    Color ember = ((SolidColorBrush)app.FindResource("EmberBrush")).Color;
                    Assert.Equal(ember, ColorOf(Field<System.Windows.Shapes.Path>(arc, "_sweepArc").Stroke));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the trigger arc test timed out");
        }

        [Fact]
        public void TheSteeringChartFollowsALookChangeAtRest()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    const int width = 320, height = 160;
                    RenderTargetBitmap Drawn(SteeringTorqueChart chart)
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(chart);
                        return bitmap;
                    }
                    var chart = new SteeringTorqueChart();
                    Load(chart, width, height);
                    Color before = Pixel(Drawn(chart), 60, 40);

                    ToNightRun();
                    // The page shows it again at the same size: an arrange, as a layout pass does.
                    chart.Measure(new Size(width, height));
                    chart.Arrange(new Rect(0, 0, width, height));
                    chart.UpdateLayout();
                    var fresh = new SteeringTorqueChart();
                    Load(fresh, width, height);
                    Color expected = Pixel(Drawn(fresh), 60, 40);
                    Assert.NotEqual(before, expected);
                    Assert.Equal(expected, Pixel(Drawn(chart), 60, 40));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the steering chart test timed out");
        }

        [Fact]
        public void TheKeyboardMousePreviewFollowsALookChangeAtRest()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    ThemeManager.Apply(2, null);
                    var vm = new PadViewModel(0) { OutputType = VirtualControllerType.KeyboardMouse };
                    var held = new KbmRawState();
                    held.SetMouseButton(0, true);
                    vm.KbmOutputSnapshot = held;
                    var view = new KBMPreviewView();
                    window = ShowOffScreen(view);
                    view.Bind(vm);
                    Pump(dispatcher);
                    RunFrame(view);
                    Assert.Equal(EmberTheme.Accent, ColorOf(Field<Shape>(view, "_lmbPath").Fill));

                    Color accent = ToNightRun();
                    RunFrame(view);
                    Assert.Equal(accent, ColorOf(Field<Shape>(view, "_lmbPath").Fill));
                    // The hover wash is made again too.
                    var parts = Field<object>(view, "_parts");
                    Color callout = ThemeLook.Callout;
                    Assert.Equal(Color.FromArgb(0x66, callout.R, callout.G, callout.B), ColorOf(Field<Shape>(parts, "LmbHover").Fill));
                    view.Unbind();
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                }
            }, Limit, "the keyboard and mouse preview test timed out");
        }

        [Fact]
        public void TheMidiPreviewFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    ThemeManager.Apply(2, null);
                    var vm = new PadViewModel(0) { OutputType = VirtualControllerType.Midi };
                    vm.MidiConfig.CcCount = 2;
                    var view = new MidiPreviewView();
                    window = ShowOffScreen(view);
                    view.Bind(vm);
                    Pump(dispatcher);
                    RunFrame(view);
                    Rectangle CcFill() => Field<Rectangle>(((IList)Field<object>(view, "_ccWidgets"))[0], "Fill");
                    Assert.Equal(EmberTheme.Accent, ColorOf(CcFill().Fill));

                    Color accent = ToNightRun();
                    RunFrame(view);
                    Assert.Equal(accent, ColorOf(CcFill().Fill));
                    view.Unbind();
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                }
            }, Limit, "the MIDI preview test timed out");
        }

        [Fact]
        public void TheMousePreviewFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    ThemeManager.Apply(2, null);
                    // The Devices page's mouse: its hover wash.
                    var mouse = new MousePreviewControl();
                    window = ShowOffScreen(mouse);
                    Pump(dispatcher);
                    Shape MouseHover() => (Shape)mouse.MouseCanvas.Children[6];
                    Color callout = ThemeLook.Callout;
                    Assert.Equal(Color.FromArgb(0x66, callout.R, callout.G, callout.B), ColorOf(MouseHover().Fill));

                    ToNightRun();
                    RunFrame(mouse);
                    callout = ThemeLook.Callout;
                    Assert.Equal(Color.FromArgb(0x66, callout.R, callout.G, callout.B), ColorOf(MouseHover().Fill));
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                }
            }, Limit, "the mouse preview test timed out");
        }

        [Fact]
        public void AHeldTouchCapsuleFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    var touchVm = new PadViewModel(0) { OutputType = VirtualControllerType.Extended };
                    typeof(PadViewModel).GetField("_profileId", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(touchVm, "steam-controller-2");
                    var strip = new TouchSensorStrip { DataContext = touchVm };
                    Load(strip, 600, 40);
                    touchVm.LeftStickTouch = true;
                    Border Capsule() => (Border)((StackPanel)strip.Content).Children[0];
                    Assert.Equal(EmberTheme.Accent, ColorOf(Capsule().Background));

                    Color accent = ToNightRun();
                    Assert.Equal(accent, ColorOf(Capsule().Background));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the touch capsule test timed out");
        }

        [Fact]
        public void AHeldSchematicButtonFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                Window window = null;
                try
                {
                    ThemeManager.Apply(2, null);
                    var schematic = new ControllerSchematicView();
                    window = ShowOffScreen(schematic, 900, 700);
                    Pump(dispatcher);
                    var rig = new PadViewModel(1) { OutputType = VirtualControllerType.Extended };
                    schematic.Bind(rig);
                    rig.UpdateFromRawHidState(new RawHidState { Axes = new short[8], Buttons = new uint[] { 1 }, Povs = new[] { -1 } });
                    RunFrame(schematic);
                    Shape Circle() => Field<Shape>(((IList)Field<object>(schematic, "_buttonWidgets"))[0], "Circle");
                    Assert.Equal(EmberTheme.Accent, ((DropShadowEffect)Circle().Effect).Color);

                    Color accent = ToNightRun();
                    RunFrame(schematic);
                    Assert.Equal(accent, ((DropShadowEffect)Circle().Effect).Color);
                    schematic.Unbind();
                }
                finally
                {
                    window?.Close();
                    BackToStock();
                }
            }, Limit, "the schematic test timed out");
        }
        [Fact]
        public void TheModelsGlowFollowsALookChange()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    var view = new ControllerModelView();
                    var model = ControllerModelBase.Create("Xbox360", null, false);
                    typeof(ControllerModelView).GetField("_currentModel", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(view, model);
                    Load(view, 400, 300);
                    Color Glow() => ((SolidColorBrush)((DiffuseMaterial)model.HighlightMaterials.Values.First()).Brush).Color;
                    Assert.Equal(EmberTheme.Accent, Glow());

                    ToNightRun();
                    Color accent = (Color)app.FindResource("SystemAccentColorPrimary");
                    Assert.NotEqual(EmberTheme.Accent, accent);
                    Assert.All(model.HighlightMaterials.Values.OfType<DiffuseMaterial>(), m => Assert.Equal(accent, ((SolidColorBrush)m.Brush).Color));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the model glow test timed out");
        }

        // ─────────────────────────────────────────────
        //  The web controller's finger dots
        // ─────────────────────────────────────────────

        [Fact]
        public void TheFingerDotsTakeTheThemesTones()
        {
            string sheet = WebFile("css", "padforge.css");
            var find = new Dictionary<string, object>
            {
                ["EmberBrush"] = new SolidColorBrush(Color.FromRgb(0x20, 0x60, 0xFF)),
                ["ColdBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)),
                ["SteelGroundBrush"] = new SolidColorBrush(Color.FromRgb(0x06, 0x0B, 0x26)),
            };
            var dressing = WebThemeSheet.Build(sheet, key => find.TryGetValue(key, out var v) ? v : null, dark: true);
            Assert.Contains("--ember-dot: rgba(32, 96, 255, 0.6);", dressing.Css);
            Assert.Contains("--cold-dot: rgba(255, 210, 63, 0.6);", dressing.Css);

            // No page keeps the stock tones of a dot as a literal: they are the sheet's.
            string assets = System.IO.Path.Combine(RepoRoot(), "PadForge.App", "WebAssets");
            var found = new List<string>();
            foreach (string path in Directory.GetFiles(assets, "*.*", SearchOption.AllDirectories))
            {
                if (path.EndsWith("padforge.css", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = System.IO.Path.GetExtension(path);
                if (ext is not (".css" or ".html" or ".js")) continue;
                string text = File.ReadAllText(path);
                foreach (string tone in new[] { "rgba(255, 107, 44", "rgba(124, 199, 234" })
                    if (text.Contains(tone, StringComparison.Ordinal)) found.Add(System.IO.Path.GetFileName(path) + " " + tone);
            }
            Assert.Empty(found);
        }

        // ─────────────────────────────────────────────
        //  Windows' high contrast while PadForge runs
        // ─────────────────────────────────────────────

        /// <summary>Raises Windows' notice of a changed system setting, as
        /// WPF raises it when Windows sends WM_SETTINGCHANGE.</summary>
        private static void RaiseSystemSetting(string name)
        {
            var field = typeof(SystemParameters).GetField(nameof(SystemParameters.StaticPropertyChanged), BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(field);
            ((PropertyChangedEventHandler)field.GetValue(null))?.Invoke(null, new PropertyChangedEventArgs(name));
        }

        [Fact]
        public void TurningHighContrastOnWhilePadForgeRunsTakesTheThemeOff()
        {
            var before = ThemeManager.HighContrastOn;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    bool on = false;
                    ThemeManager.HighContrastOn = () => on;
                    ThemeManager.Apply(2, NightRun);
                    Assert.NotNull(ThemeManager.Current);
                    on = true;
                    RaiseSystemSetting(nameof(SystemParameters.HighContrast));
                    Pump(dispatcher);
                    Assert.Null(ThemeManager.Current);
                    // Another system setting is no reason to put a look on again.
                    on = false;
                    RaiseSystemSetting(nameof(SystemParameters.MenuDropAlignment));
                    Pump(dispatcher);
                    Assert.Null(ThemeManager.Current);
                    RaiseSystemSetting(nameof(SystemParameters.HighContrast));
                    Pump(dispatcher);
                    Assert.NotNull(ThemeManager.Current);
                }
                finally
                {
                    ThemeManager.HighContrastOn = before;
                    BackToStock();
                }
            }, Limit, "the high contrast test timed out");
        }

        // ─────────────────────────────────────────────
        //  A picked file whose folder was gone at the start
        // ─────────────────────────────────────────────

        [Fact]
        public void APickedFileWhoseFolderComesBackIsReadAgain()
        {
            string root = TempFolder();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    string folder = System.IO.Path.Combine(root, "synced", "themes");
                    Directory.CreateDirectory(folder);
                    string path = System.IO.Path.Combine(folder, "away.pftheme");
                    var away = Custom(true, "#101418", "#22CC88", null, null);
                    away.Name = "Away";
                    ThemeFile.Write(path, away);
                    var listed = ThemeManager.Register(path);
                    var saved = ThemeManager.SaveRegistry();

                    // PadForge starts while the folder is gone: the list is read, the pick goes on, Dark stands in.
                    Directory.Delete(System.IO.Path.Combine(root, "synced"), recursive: true);
                    ThemeManager.LoadRegistry(saved);
                    ThemeManager.Apply(2, listed.Id);
                    Assert.Null(ThemeManager.Current);
                    Assert.Equal(ThemeFault.Missing, ThemeManager.Find(listed.Id).Fault);

                    // The folder comes back with the file.
                    Directory.CreateDirectory(folder);
                    ThemeFile.Write(path, away);
                    Until(dispatcher, () => ThemeManager.Current?.Name == "Away", "the file's return");
                }
                finally
                {
                    BackToStock();
                    Drop(root);
                }
            }, Limit, "the returning folder test timed out");
        }

        // ─────────────────────────────────────────────
        //  The words on the accent's fills
        // ─────────────────────────────────────────────

        private static readonly (string Ink, string Fill)[] InkPairs =
        {
            ("EmberInkBrush", "EmberBrush"), ("EmberInkBrush", "EmberLiftBrush"), ("EmberInkBrush", "EmberPressBrush"),
            ("AccentButtonForeground", "AccentButtonBackground"), ("AccentButtonForeground", "AccentButtonBackgroundPointerOver"),
            ("AccentButtonForegroundPointerOver", "AccentButtonBackgroundPressed"), ("AccentButtonForegroundPressed", "AccentButtonBackgroundPressed"),
        };

        private static Color Resolved(Application app, string key)
        {
            object found = app.TryFindResource(key);
            Assert.True(found != null, key + " is not found");
            return found switch
            {
                SolidColorBrush brush => Color.FromArgb((byte)Math.Round(brush.Color.A * brush.Opacity), brush.Color.R, brush.Color.G, brush.Color.B),
                Color color => color,
                _ => throw new InvalidOperationException(key + " is a " + found.GetType().Name),
            };
        }

        /// <summary>The page and a card under the look in force, as solid colors.</summary>
        private static (Color Page, Color Card) Grounds(Application app, bool dark)
        {
            Color page = ThemeContrastTests.Over(Resolved(app, "ApplicationBackgroundBrush"), dark ? Colors.Black : Colors.White);
            Color card = ThemeContrastTests.Over(Resolved(app, "CardBackgroundFillColorDefaultBrush"), page);
            return (page, card);
        }

        /// <summary>Every pair of words and fill a filled button draws, on a card and on the page, that reads under 4.5.</summary>
        private static IEnumerable<string> UnreadInk(Application app, string look, bool dark)
        {
            var (page, card) = Grounds(app, dark);
            foreach (var (inkKey, fillKey) in InkPairs)
            {
                foreach (var (ground, where) in new[] { (card, "a card"), (page, "the page") })
                {
                    Color fill = ThemeContrastTests.Over(Resolved(app, fillKey), ground);
                    Color ink = ThemeContrastTests.Over(Resolved(app, inkKey), fill);
                    double reads = ThemeColor.Contrast(ink, fill);
                    if (reads < ThemeEngine.TextContrast - 0.005)
                        yield return look + ": " + inkKey + " " + ink + " on " + fillKey + " " + fill + " on " + where + " reads " + reads.ToString("0.00");
                }
            }
        }

        [Fact]
        public void EveryStartersFilledButtonsReadInEveryState()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var starter in ThemeManager.Starters)
                    {
                        ThemeManager.Apply(2, starter.Id);
                        unread.AddRange(UnreadInk(app, starter.Id, starter.Dark));
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the starters' button test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        [Fact]
        public void PadForgesOwnFilledButtonsReadInEveryState()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.Apply(2, null);
                    unread.AddRange(UnreadInk(app, "Dark", true));
                    // The pressed fill is as designed: the words change, the fill stays.
                    Assert.Equal(Color.FromRgb(0xC4, 0x3D, 0x0C), Resolved(app, "AccentButtonBackgroundPressed"));
                    ThemeManager.Apply(1, null);
                    unread.AddRange(UnreadInk(app, "Light", false));
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the stock button test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        /// <summary>Under Windows' high contrast wpf-ui puts its contrast
        /// dictionary on, and the words of its filled buttons are that
        /// dictionary's: PadForge sets none of them over it. PadForge's own
        /// caution line takes the contrast theme's text color
        /// (UnderHighContrastEveryLookTakesTheContrastThemesColors).</summary>
        [Fact]
        public void HighContrastKeepsItsOwnButtonWords()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.HighContrast);
                    EmberTheme.ApplyAccent();
                    Assert.Equal(Wpf.Ui.Appearance.ApplicationTheme.HighContrast, Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());
                    var own = new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed" }
                        .Where(key => ThemeEngine.IsOwn(app.Resources, key)).ToList();
                    Assert.Empty(own);
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the high contrast words test timed out");
        }

        // The grounds text stands on and the text PadForge draws on them.
        private static readonly string[] ContrastGrounds =
        {
            "ApplicationBackgroundBrush", "SolidBackgroundFillColorBaseBrush", "NavigationViewContentBackground",
            "CardBackgroundFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush", EmberTheme.HomeEngineCardKey,
            "SteelGroundBrush", "SteelRaisedBrush", "SteelPressedBrush", "SteelPaneBrush", "SteelWindowBrush", "SteelFieldBrush",
            "SteelLiftBrush", "SteelFootBrush", "ShellDialogGroundBrush", "SteelSegmentBrush", "SteelBandBrush", "SteelTrayBrush",
            "SteelWellBrush", "SteelPaneHoverBrush", "SteelCardHoverBrush", "NavigationViewItemBackgroundSelected",
            "NavigationViewItemBackgroundPointerOver", "NavigationViewItemBackgroundPressed",
            "EmberCardPickBrush", "EmberWellBrush", "EmberPickBrush", "WaitWellBrush",
        };

        private static readonly string[] ContrastTexts =
        {
            "TextFillColorPrimaryBrush", "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "SubRowTextBrush",
            "SelectedTextBrush", "QuietGlyphBrush", "ColdBrush", "ColdDeepBrush", "ColdMutedBrush", "EmberTextBrush",
            "EmberHotBrush", "InputChipTextBrush", "DangerInkBrush", "LinkHoverBrush", "WaitBrush", "WaitTextBrush", "WaitMutedBrush",
            "ChannelRedBrush", "ChannelGreenBrush", "ChannelBlueBrush", "SystemFillColorCautionBrush",
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
        };

        /// <summary>Windows' contrast themes stand over every look: under a
        /// dark one and a white one, whatever look the user picked, the page,
        /// the cards and every ground text stands on take the contrast
        /// theme's window color, the text its window text, and the ember
        /// buttons its highlight pair. The System look under a dark contrast
        /// theme drew PadForge's light text ramp over the contrast ground.</summary>
        [Fact]
        public void UnderHighContrastEveryLookTakesTheContrastThemesColors()
        {
            var palettes = new (string Name, EmberTheme.ContrastColors Colors)[]
            {
                ("a dark contrast theme", new EmberTheme.ContrastColors(Hex("#000000"), Hex("#FFFFFF"), Hex("#3FF23F"), Hex("#1AEBFF"), Hex("#000000"), Hex("#FFFFFF"))),
                ("a white contrast theme", new EmberTheme.ContrastColors(Hex("#FFFFFF"), Hex("#000000"), Hex("#600000"), Hex("#37006E"), Hex("#FFFFFF"), Hex("#000000"))),
            };
            string gone = System.IO.Path.Combine(TempFolder(), "gone.pftheme");
            var looks = new (string Name, Action Put, Action Off)[]
            {
                ("System", () => ThemeManager.Apply(0, null), null),
                ("Light", () => ThemeManager.Apply(1, null), null),
                ("Dark", () => ThemeManager.Apply(2, null), null),
                ("a dark starter", () => ThemeManager.Apply(0, NightRun), null),
                ("a light starter", () => ThemeManager.Apply(1, "Starter:rings-map-of-middle-earth"), null),
                ("a file that is gone", () =>
                {
                    ThemeManager.LoadRegistry(new[] { ("Gone", gone) });
                    ThemeManager.Apply(0, ThemeManager.FilePrefix + gone);
                }, () => ThemeManager.LoadRegistry(null)),
                ("a game look", () =>
                {
                    ThemeManager.Apply(1, null);
                    ThemeManager.SetGameLook(Custom(true, "#101418", "#22CC88", null, null));
                }, () => ThemeManager.SetGameLook(null)),
                ("the builder's theme", () => ThemeManager.SetPreview(Custom(false, "#FFFFFF", "#2B7BFF", null, null)), () => ThemeManager.SetPreview(null)),
            };
            var wrong = new List<string>();
            var highContrast = ThemeManager.HighContrastOn;
            var contrast = EmberTheme.Contrast;
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    ThemeManager.HighContrastOn = () => true;
                    foreach (var (paletteName, colors) in palettes)
                    {
                        EmberTheme.Contrast = () => colors;
                        foreach (var (lookName, put, off) in looks)
                        {
                            put();
                            string where = lookName + " under " + paletteName + ": ";
                            if (Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != Wpf.Ui.Appearance.ApplicationTheme.HighContrast)
                                wrong.Add(where + "wpf-ui's theme is " + Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme());
                            if (ThemeManager.Current != null) wrong.Add(where + ThemeManager.Current.Name + " is on");
                            void Expect(string key, Color want)
                            {
                                Color got = Resolved(app, key);
                                if (got != want) wrong.Add(where + key + " is " + got + ", not " + want);
                            }
                            foreach (string key in ContrastGrounds) Expect(key, colors.Window);
                            foreach (string key in ContrastTexts) Expect(key, colors.WindowText);
                            Expect("TextFillColorDisabledBrush", colors.GrayText);
                            // The words of wpf-ui's controls that stand on no
                            // fill of their own, so on PadForge's ground, in the
                            // color the contrast dictionary gives them by role.
                            foreach (string key in new[] { "CheckBoxForeground", "RadioButtonForeground" }) Expect(key, colors.ButtonText);
                            foreach (string key in new[] { "ToggleSwitchContentForeground", "ListBoxItemForeground", "ListViewItemForeground" }) Expect(key, colors.WindowText);
                            foreach (string key in new[] { "RadioButtonForegroundDisabled", "ToggleSwitchContentForegroundDisabled", "LabelForeground" }) Expect(key, colors.GrayText);
                            Expect("ListViewItemBackgroundPointerOver", colors.Window);
                            foreach (string key in new[] { "EmberBrush", "EmberLiftBrush", "EmberPressBrush" }) Expect(key, colors.Highlight);
                            Expect("EmberInkBrush", colors.HighlightText);
                            foreach (string key in new[] { "EmberWashBrush", "WaitTintBrush", "EmberTintBrush", "ColdTintBrush" })
                                if (Resolved(app, key).A != 0) wrong.Add(where + key + " tints the ground");
                            off?.Invoke();
                        }
                    }

                    // High contrast goes off: PadForge's own look and the pick are back.
                    ThemeManager.HighContrastOn = () => false;
                    ThemeManager.Apply(2, NightRun);
                    Assert.Equal("Night Run", ThemeManager.Current?.Look);
                    ThemeManager.Apply(2, null);
                    Assert.DoesNotContain(new[] { "CheckBoxForeground", "RadioButtonForeground", "RadioButtonForegroundDisabled", "ToggleSwitchContentForeground",
                        "ToggleSwitchContentForegroundDisabled", "ListBoxItemForeground", "ListViewItemForeground", "ListViewItemBackgroundPointerOver", "LabelForeground" },
                        key => ThemeEngine.IsOwn(app.Resources, key));
                    Assert.Equal(Hex("#0B1018"), Resolved(app, "ApplicationBackgroundBrush"));
                    Assert.Equal(Hex("#EEF2F7"), Resolved(app, "TextFillColorPrimaryBrush"));
                    Assert.Equal(Hex("#FF6B2C"), Resolved(app, "EmberBrush"));
                    Assert.Equal(Color.FromArgb(0x1A, 0x7C, 0xC7, 0xEA), Resolved(app, "ColdTintBrush"));
                    ThemeManager.Apply(1, null);
                    Assert.Equal(Hex("#1A2433"), Resolved(app, "TextFillColorPrimaryBrush"));
                    Assert.NotEqual(Hex("#000000"), Resolved(app, "ApplicationBackgroundBrush"));
                }
                finally
                {
                    ThemeManager.HighContrastOn = highContrast;
                    EmberTheme.Contrast = contrast;
                    BackToStock();
                    Drop(System.IO.Path.GetDirectoryName(gone));
                }
            }, Limit, "the high contrast looks test timed out");
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }

        [Fact]
        public void ALightThemesPrimaryButtonReadsOnItsFill()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var (accent, background) in new[] { ("#FFC700", "#FFFFFF"), ("#FFC700", null), ("#00A000", "#FFFFFF"), ("#2B7BFF", "#F0F0F0") })
                    {
                        ThemeManager.SetPreview(Custom(false, background, accent, null, null));
                        unread.AddRange(UnreadInk(app, "light " + accent + " on " + (background ?? "stock"), false));
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the light primary button test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        // ─────────────────────────────────────────────
        //  Colored text a theme moves reads where it stands
        // ─────────────────────────────────────────────

        [Fact]
        public void TheCautionWordsReadOnALightThemesCard()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (string background in new[] { "#A0A0A0", "#808080", "#C0C0C0", "#E9DDC3" })
                    {
                        ThemeManager.SetPreview(Custom(false, background, null, null, null));
                        var (page, card) = Grounds(app, false);
                        foreach (var (ground, where) in new[] { (card, "a card"), (page, "the page") })
                        {
                            double reads = ThemeColor.Contrast(ThemeContrastTests.Over(Resolved(app, "SystemFillColorCautionBrush"), ground), ground);
                            if (reads < ThemeEngine.TextContrast - 0.005) unread.Add(background + ": caution on " + where + " reads " + reads.ToString("0.00"));
                        }
                    }
                    // PadForge's own looks keep wpf-ui's own caution tones.
                    ThemeManager.SetPreview(null);
                    foreach (var (stock, tone) in new[] { (1, Color.FromRgb(0x9D, 0x5D, 0x00)), (2, Color.FromRgb(0xFC, 0xE1, 0x00)) })
                    {
                        ThemeManager.Apply(stock, null);
                        Color drawn = Resolved(app, "SystemFillColorCautionBrush");
                        if (drawn != tone) unread.Add("stock " + stock + ": caution is " + drawn + ", not " + tone);
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the caution test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        [Fact]
        public void HomesEngineCardIsTheCardsOwnFillUnderALightTheme()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (bool dark in new[] { false, true })
                    {
                        ThemeManager.SetPreview(Custom(dark, dark ? "#101828" : "#D8E4F0", null, null, null));
                        Assert.Equal(((SolidColorBrush)app.TryFindResource("CardBackgroundFillColorDefaultBrush")).Color,
                            ((SolidColorBrush)app.Resources[EmberTheme.HomeEngineCardKey]).Color);
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the engine card test timed out");
        }

        [Fact]
        public void GoldWordsReadInTheirWellUnderEveryStarter()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var starter in ThemeManager.Starters)
                    {
                        ThemeManager.Apply(2, starter.Id);
                        var (page, card) = Grounds(app, starter.Dark);
                        Color well = ThemeContrastTests.Over(Resolved(app, "WaitWellBrush"), card);
                        Color tint = ThemeContrastTests.Over(Resolved(app, "WaitTintBrush"), card);
                        foreach (string text in new[] { "WaitBrush", "WaitTextBrush", "WaitMutedBrush" })
                            foreach (var (ground, where) in new[] { (well, "the wait well"), (tint, "the wait tint") })
                            {
                                double reads = ThemeColor.Contrast(ThemeContrastTests.Over(Resolved(app, text), ground), ground);
                                if (reads < ThemeEngine.TextContrast - 0.005)
                                    unread.Add(starter.Id + ": " + text + " on " + where + " reads " + reads.ToString("0.00"));
                            }
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the wait well test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        // ─────────────────────────────────────────────
        //  The engine card over a picture
        // ─────────────────────────────────────────────

        private static readonly (string Key, double Floor)[] EngineCardText =
        {
            ("TextFillColorPrimaryBrush", 7.0), ("TextFillColorSecondaryBrush", 5.5), ("TextFillColorTertiaryBrush", 4.5),
            ("ColdBrush", 4.5), ("QuietGlyphBrush", 3.0),
        };

        /// <summary>Every text of the engine card that reads under its floor
        /// somewhere over the picture: the card's fill over each pixel, the
        /// pixel over the page.</summary>
        private static List<string> UnreadOverPicture(Application app, BitmapSource picture, string look)
        {
            var unread = new List<string>();
            Color page = ThemeContrastTests.Over(Resolved(app, "ApplicationBackgroundBrush"), Colors.Black);
            Color card = Resolved(app, EmberTheme.HomeEngineCardKey);
            var pixels = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
            int width = pixels.PixelWidth, height = pixels.PixelHeight;
            var data = new byte[width * height * 4];
            pixels.CopyPixels(data, width * 4, 0);
            double darkest = double.MaxValue, lightest = double.MinValue;
            Color darkestGround = default, lightestGround = default;
            for (int i = 0; i < data.Length; i += 4)
            {
                Color under = ThemeContrastTests.Over(Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]), page);
                Color ground = ThemeContrastTests.Over(card, under);
                double light = ThemeColor.Luminance(ground);
                if (light < darkest) { darkest = light; darkestGround = ground; }
                if (light > lightest) { lightest = light; lightestGround = ground; }
            }
            foreach (var (key, floor) in EngineCardText)
            {
                foreach (Color ground in new[] { darkestGround, lightestGround })
                {
                    Color text = ThemeContrastTests.Over(Resolved(app, key), ground);
                    double reads = ThemeColor.Contrast(text, ground);
                    // A text between the darkest and the lightest ground meets a ground of its own lightness somewhere.
                    double own = ThemeColor.Luminance(text);
                    bool inside = own > darkest && own < lightest;
                    if (reads < floor - 0.005 || inside)
                        unread.Add(look + ": " + key + " " + text + " over " + ground + " reads " + reads.ToString("0.00") + ", under " + floor
                                   + (inside ? " (inside the grounds' range)" : ""));
                }
            }
            return unread;
        }

        [Fact]
        public void TheEngineCardsTextReadsOverEveryStartersPicture()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    foreach (var starter in ThemeManager.Starters.Where(s => s.Picture != null))
                    {
                        ThemeManager.Apply(2, starter.Id);
                        byte alpha = Resolved(app, EmberTheme.HomeEngineCardKey).A;
                        Assert.InRange(alpha, ThemeManager.PictureCardAlpha, (byte)255);
                        unread.AddRange(UnreadOverPicture(app, starter.Picture, starter.Id + " at " + alpha));
                    }

                    // A picture the card's text reads over at the approved opacity keeps it.
                    string folder = TempFolder();
                    try
                    {
                        string path = System.IO.Path.Combine(folder, "quiet.pftheme");
                        var quiet = Custom(true, "#0B1018", null, null, null);
                        quiet.Name = "Quiet";
                        quiet.PictureBytes = Png(Blocks(64, 16, (1, Color.FromRgb(0x0B, 0x10, 0x18))));
                        quiet.PictureName = "picture.png";
                        ThemeFile.Write(path, quiet);
                        ThemeManager.Apply(2, ThemeManager.Register(path).Id);
                        Assert.Equal(ThemeManager.PictureCardAlpha, Resolved(app, EmberTheme.HomeEngineCardKey).A);
                    }
                    finally
                    {
                        Drop(folder);
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the engine card picture test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));
        }

        // ─────────────────────────────────────────────
        //  Lighting's channel letters and the stick radar
        // ─────────────────────────────────────────────

        private static readonly string[] ChannelKeys = { "ChannelRedBrush", "ChannelGreenBrush", "ChannelBlueBrush" };

        [Fact]
        public void TheChannelLettersReadOnACardInEveryLook()
        {
            var unread = new List<string>();
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    var looks = new List<(string Name, Action Apply, bool Dark)>
                    {
                        ("Dark", () => ThemeManager.Apply(2, null), true),
                        ("Light", () => ThemeManager.Apply(1, null), false),
                    };
                    foreach (var starter in ThemeManager.Starters)
                        looks.Add((starter.Id, () => ThemeManager.Apply(2, starter.Id), starter.Dark));
                    looks.Add(("light gray", () => ThemeManager.SetPreview(Custom(false, "#A0A0A0", null, null, null)), false));
                    foreach (var (name, apply, dark) in looks)
                    {
                        apply();
                        var (page, card) = Grounds(app, dark);
                        foreach (string key in ChannelKeys)
                        {
                            double reads = ThemeColor.Contrast(ThemeContrastTests.Over(Resolved(app, key), card), card);
                            if (reads < ThemeEngine.TextContrast - 0.005) unread.Add(name + ": " + key + " reads " + reads.ToString("0.00"));
                        }
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the channel letter test timed out");
            Assert.True(unread.Count == 0, string.Join("\n", unread));

            // Every channel letter takes its token: no page writes the hues.
            var literal = new Regex("#(?:FF)?(?:D32F2F|388E3C|1976D2)\\b", RegexOptions.IgnoreCase);
            string views = System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Views");
            var found = Directory.GetFiles(views, "*.xaml").Where(p => literal.IsMatch(File.ReadAllText(p))).Select(System.IO.Path.GetFileName).ToList();
            Assert.Empty(found);
        }

        private static readonly (string Key, Color Stock)[] RadarTokens =
        {
            ("RadarRingBrush", Color.FromArgb(0xFF, 0x25, 0x30, 0x49)), ("RadarGridBrush", Color.FromArgb(0xFF, 0x1E, 0x27, 0x39)),
            ("DeadZoneFillBrush", Color.FromArgb(0x2E, 0x25, 0x30, 0x49)), ("DeadZoneFaintFillBrush", Color.FromArgb(0x18, 0x25, 0x30, 0x49)),
        };

        [Fact]
        public void TheStickRadarTakesTheLooksGround()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                try
                {
                    // PadForge's own looks draw what the radar drew before it had tokens.
                    foreach (int stock in new[] { 2, 1 })
                    {
                        ThemeManager.Apply(stock, null);
                        foreach (var (key, color) in RadarTokens) Assert.Equal(color, Resolved(app, key));
                    }
                    // A theme moves them with its ground.
                    foreach (string id in new[] { NightRun, "Starter:rings-map-of-middle-earth" })
                    {
                        var theme = ThemeManager.Find(id);
                        ThemeManager.Apply(2, id);
                        var engine = new ThemeEngine(theme.Seeds);
                        foreach (var (key, color) in RadarTokens)
                        {
                            Assert.Equal(engine.Map(key, color), Resolved(app, key));
                            Assert.NotEqual(color, Resolved(app, key));
                        }
                    }
                }
                finally
                {
                    BackToStock();
                }
            }, Limit, "the radar test timed out");

            string pad = File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));
            Assert.DoesNotMatch("#(?:[0-9A-Fa-f]{2})?(?:253049|1E2739)\\b", pad);
        }
    }
}
