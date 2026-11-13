using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Controls;
using PadForge.Engine;
using PadForge.ViewModels;
using PadForge.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Draws the shell's parts to pictures without a screen (#574), built as
    /// PageRenderDump draws the controller's page, to hold each against its board: Add a
    /// Controller, Home's first page, and Search with the title bar's box. Search's states run
    /// its real index and its real availability over an Xbox slot's page, and its results go to
    /// search-results.txt. Each state is drawn at the board's page area, 1031 by
    /// 772 at a 1280 by 820 window, and again at the page area of PadForge's smallest window,
    /// 651 by 552. Does nothing unless PADFORGE_PAGE_RENDER_DIR names a folder, and then
    /// writes one PNG per state into it, at the scale in PADFORGE_PAGE_RENDER_SCALE (1.5 when
    /// unset), the steps it reached into steps.txt, and for each state the bindings that looked
    /// up an ancestor or a named element and found none into lookups.txt.</summary>
    [Collection("WpfApplicationSingleton")]
    public class ShellRenderDump
    {
        // The board's page area at 1280 by 820 (x 249 to 1280, y 48 to 820),
        // and at PadForge's smallest window, 900 by 600.
        private const double Width = 1547 / 1.5, Height = 772, NarrowWidth = 651, NarrowHeight = 552;

        [Fact]
        public void DrawTheShellParts()
        {
            string dir = Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            double scale = double.TryParse(Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_SCALE"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double asked) ? asked : 1.5;
            Directory.CreateDirectory(dir);
            string log = Path.Combine(dir, "steps.txt");
            void Step(string what) => File.AppendAllText(log, DateTime.Now.ToString("HH:mm:ss.fff") + " " + what + Environment.NewLine);

            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try
                {
                    PageRenderDump.SetResourceAssembly(typeof(App).Assembly);
                    var app = PageRenderDump.MakeAppThatNeverStarts(dispatcher);
                    app.InitializeComponent();
                    PageRenderDump.ApplyTheme(PageRenderDump.PictureTheme());
                    PadForge.Common.EmberTheme.ApplyAccent();
                    Step("the app's resources and theme are loaded");

                    // Lays a part out at a page area's size, lets it settle,
                    // pictures it, and writes its lookups that found nothing.
                    void Area(string file, FrameworkElement view, double width = Width, double height = Height)
                    {
                        var ground = new Border { Background = (Brush)app.FindResource("SteelGroundBrush"), Child = view };
                        void Lay()
                        {
                            ground.Measure(new Size(width, height));
                            ground.Arrange(new Rect(0, 0, width, height));
                            ground.UpdateLayout();
                        }
                        Lay();
                        PageRenderDump.Settle(dispatcher);
                        Lay();
                        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale),
                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(ground);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = File.Create(Path.Combine(dir, file)))
                            png.Save(stream);
                        var lost = PageRenderDump.LostLookups(ground);
                        File.AppendAllLines(Path.Combine(dir, "lookups.txt"),
                            new[] { file + ": " + lost.Count + " lookups found nothing" }.Concat(lost.Select(l => "   " + l)));
                        ground.Child = null;
                        Step("drew " + file);
                    }

                    // ── Home's first page ──────────────────────────────
                    var row = new DeviceRowViewModel
                    {
                        DevicePath = @"\\?\HID#{00001124-0000-1000-8000-00805f9b34fb}_VID&0002054c_PID&0ce6#a&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
                        VendorId = 0x054C,
                        ProductId = 0x0CE6,
                        BatteryPercent = 82,
                    };
                    var home = new DashboardViewModel { EngineStateKey = "Idle" };
                    home.SetFirstRunDevice(Guid.NewGuid(), "DualSense Wireless Controller", row);
                    Area("home-first-run.png", new HomePage { DataContext = home });
                    Area("home-first-run-narrow.png", new HomePage { DataContext = home }, NarrowWidth, NarrowHeight);
                    home.ClearFirstRunDevice();
                    Area("home-first-run-none.png", new HomePage { DataContext = home });
                    home.EngineStateKey = "Stopped";
                    Area("home-first-run-stopped.png", new HomePage { DataContext = home });

                    // ── Add a Controller ───────────────────────────────
                    FrameworkElement Dialog(AddControllerViewModel vm)
                    {
                        var host = new ShellDialog { FrameMaxWidth = 760, Content = new AddControllerView { DataContext = vm } };
                        host.Open(null);
                        return host;
                    }
                    var counts = new Dictionary<VirtualControllerType, int>
                    {
                        [VirtualControllerType.Xbox] = 1, [VirtualControllerType.KeyboardMouse] = 1, [VirtualControllerType.Extended] = 1,
                    };
                    int CountOf(VirtualControllerType t) => counts.TryGetValue(t, out int n) ? n : 0;
                    var proPad = new[] { DrivenByChoice.For(Guid.NewGuid(), "Nintendo Switch Pro Controller", true, "64%", 0) };
                    Area("add-controller.png", Dialog(AddControllerViewModel.Build(proPad, CountOf, 3, true, true)));
                    Area("add-controller-narrow.png", Dialog(AddControllerViewModel.Build(proPad, CountOf, 3, true, true)), NarrowWidth, NarrowHeight);
                    Area("add-controller-no-device.png", Dialog(AddControllerViewModel.Build(null, CountOf, 3, true, true)));
                    Area("add-controller-limits.png", Dialog(AddControllerViewModel.Build(proPad,
                        t => t == VirtualControllerType.Vr ? 1 : CountOf(t), 4, false, true)));
                    Area("add-controller-full.png", Dialog(AddControllerViewModel.Build(null,
                        t => t == VirtualControllerType.Xbox ? 16 : 0, 16, true, true)));

                    // ── Search ──────────────────────────────────────────
                    // The title bar's box, read from the window's own markup,
                    // on the title bar's fill at the board's 48 tall.
                    string window = ExtrasPageTests.RepoText("PadForge.App", "MainWindow.xaml");
                    var boxMarkup = System.Text.RegularExpressions.Regex.Match(window,
                        "<controls:ClickBorder x:Name=\"TitleSearchBox\".*?</controls:ClickBorder>", System.Text.RegularExpressions.RegexOptions.Singleline).Value;
                    var box = (FrameworkElement)System.Windows.Markup.XamlReader.Parse(boxMarkup.Replace("<controls:ClickBorder x:Name=\"TitleSearchBox\"",
                        "<controls:ClickBorder xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
                        + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" "
                        + "xmlns:controls=\"clr-namespace:PadForge.Controls;assembly=PadForge\" "
                        + "xmlns:strings=\"clr-namespace:PadForge.Resources.Strings;assembly=PadForge\" x:Name=\"TitleSearchBox\""));
                    if (box.FindName("TitleSearchShortcut") is TextBlock shortcut) shortcut.Text = PadForge.Resources.Strings.Strings.Instance.Key_Control + " K";
                    Area("title-search-box.png", new Border { Background = (Brush)app.FindResource("SteelPaneBrush"), Child = box, Height = 48 }, 551, 48);

                    // The board's query on an Xbox slot's page.
                    bool made = PadForge.Common.Input.SettingsManager.SlotCreated[0];
                    PadForge.Common.Input.SettingsManager.SlotCreated[0] = true;
                    try
                    {
                        var padPage = new PadPage { DataContext = new PadViewModel(0) };
                        padPage.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                        var padGround = new Border { Child = padPage };
                        padGround.Measure(new Size(1473, 985));
                        padGround.Arrange(new Rect(0, 0, 1473, 985));
                        padGround.UpdateLayout();
                        PageRenderDump.Settle(dispatcher);
                        var main = new MainViewModel();
                        var service = new PadForge.Search.SearchService(main, p => p == PadForge.Search.SearchPage.Pad ? padPage : null);
                        service.SetIndex(PadForge.Search.SearchIndexBuilder.Build(PadForge.Search.SearchSources.Load()));
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        service.RefreshAvailability();
                        Step("Search read which entries show in " + clock.ElapsedMilliseconds + " ms");
                        var search = new SearchViewModel(service.Query) { Query = "deadzone" };
                        File.WriteAllLines(Path.Combine(dir, "search-results.txt"),
                            search.Results.Select(r => r.GroupLabel + " | " + r.Title + r.QualifierText + " | " + r.Where + " | " + r.Value));
                        var host = new ShellDialog { FrameMaxWidth = 660, ClosesOnGroundPress = true, Content = new SearchPanel { DataContext = search } };
                        host.Open(null);
                        Area("search.png", host);
                        search.Query = string.Empty;
                        Area("search-empty.png", host);
                        search.Query = "zzqx";
                        Area("search-no-match.png", host);
                        search.Query = "deadzone right";
                        Area("search-narrowed.png", host, NarrowWidth, NarrowHeight);
                    }
                    finally { PadForge.Common.Input.SettingsManager.SlotCreated[0] = made; }
                }
                catch (Exception error) { failure = error; Step("failed: " + error); }
                finally
                {
                    try
                    {
                        PairDeviceDialogSerialTests.ReleaseApplication();
                        PageRenderDump.SetResourceAssembly(null);
                    }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromMinutes(8)), "The shell render timed out. See steps.txt in the folder.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
