using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.ViewModels;
using PadForge.Views;
using Wpf.Ui.Appearance;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Draws the controller's page to pictures without a screen (#574), to hold a section
    /// against its board: the page itself under the app's own resources and theme, showing macros
    /// made here. WPF draws to a bitmap whether or not a display is on, and the page shows states
    /// a real setup may not have. The page stands in no window, so nothing it does as it loads
    /// runs. Does nothing unless PADFORGE_PAGE_RENDER_DIR names a folder, and then writes one PNG
    /// per state into it, at the scale in PADFORGE_PAGE_RENDER_SCALE (1.5 when unset), and the
    /// steps it reached into steps.txt.</summary>
    [Collection("WpfApplicationSingleton")]
    public class PageRenderDump
    {
        private const double Width = 1473, Height = 985;

        private static MacroItem QuickCombo()
        {
            // The Macros board's own example: A and Left Trigger past 50%.
            var macro = new MacroItem { Name = "Quick Combo", TriggerButtons = 0x1000 };
            macro.TriggerAxisTargets = new[] { MacroAxisTarget.LeftTrigger };
            macro.TriggerAxisThreshold = 50;
            macro.Actions.Add(new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = 0x1000, DurationMs = 100 });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.Delay, DurationMs = 200 });
            macro.Actions.Add(new MacroAction { Type = MacroActionType.MouseButtonPress, MouseButton = MacroMouseButton.Left });
            return macro;
        }

        private static MacroItem AxisInputs()
        {
            // An axis of a device, a key's depth and a plain button: the
            // inputs that have settings of their own, and one that has none.
            var device = new Guid("11111111-2222-3333-4444-555555555555");
            var macro = new MacroItem { Name = "Axis Inputs" };
            macro.SetTriggerInputEntries(new List<MacroItem.TriggerInputEntry>
            {
                new MacroItem.TriggerInputEntry { DeviceGuid = device, AxisTarget = MacroAxisTarget.LeftStickX },
                new MacroItem.TriggerInputEntry
                {
                    DeviceGuid = device,
                    SourceDescriptor = PadForge.Engine.Common.Mapping.SourceCoercion.AnalogKeyDescriptor(PadForge.Engine.Common.AnalogKeyboard.AnalogKeyCodes.W),
                },
                new MacroItem.TriggerInputEntry { DeviceGuid = device, RawButton = 3 },
            });
            return macro;
        }

        [Fact]
        public void DrawThePage()
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
                    // The app's dictionaries name each other by paths WPF
                    // reads from the application's own assembly, which in a
                    // test host is the host's.
                    SetResourceAssembly(typeof(App).Assembly);
                    var app = MakeAppThatNeverStarts(dispatcher);
                    app.InitializeComponent();
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    PadForge.Common.EmberTheme.ApplyAccent();
                    Step("the app's resources and theme are loaded");

                    var vm = new PadViewModel(0);
                    var states = new (string File, MacroItem Macro)[]
                    {
                        ("macros-quick-combo.png", QuickCombo()),
                        ("macros-axis-inputs.png", AxisInputs()),
                        ("macros-no-inputs.png", new MacroItem { Name = "New Macro" }),
                        ("macros-recording.png", new MacroItem { Name = "Recording", TriggerButtons = 0x1000, IsRecordingTrigger = true }),
                    };
                    foreach (var (_, macro) in states) vm.Macros.Add(macro);
                    var page = new PadPage { DataContext = vm };
                    var ground = new Border { Background = (Brush)app.FindResource("SteelGroundBrush"), Child = page };
                    Step("the page is made");
                    vm.SelectedConfigTab = PadViewModel.MacrosTabIndex;

                    foreach (var (file, macro) in states)
                    {
                        vm.SelectedMacro = macro;
                        Lay(ground);
                        Settle(dispatcher);
                        Lay(ground);
                        var bitmap = new RenderTargetBitmap((int)Math.Round(Width * scale), (int)Math.Round(Height * scale),
                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(ground);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(dir, file));
                        png.Save(stream);
                        Step("drew " + file);
                    }
                }
                catch (Exception error) { failure = error; Step("failed: " + error); }
                finally
                {
                    try
                    {
                        PairDeviceDialogSerialTests.ReleaseApplication();
                        SetResourceAssembly(null);
                    }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "The page render timed out. See steps.txt in the folder.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        /// <summary>The app, for its resources alone. WPF queues an
        /// application's startup as the application is made, and the app's
        /// is the whole of PadForge starting: its single-instance check,
        /// its drivers, its window. Run in a test host beside a running
        /// PadForge it put up the already-running box and waited on it
        /// (2026-11-12). The queued startup is taken off the dispatcher
        /// here, before anything runs it, and the dump stops when that
        /// cannot be shown.</summary>
        private static App MakeAppThatNeverStarts(Dispatcher dispatcher)
        {
            var queued = new List<DispatcherOperation>();
            DispatcherHookEventHandler posted = (_, e) => queued.Add(e.Operation);
            dispatcher.Hooks.OperationPosted += posted;
            App app;
            try { app = new App(); }
            finally { dispatcher.Hooks.OperationPosted -= posted; }
            if (queued.Count != 1)
                throw new InvalidOperationException("Making the app queued " + queued.Count + " operations where its startup alone was expected.");
            queued[0].Abort();
            if (queued[0].Status != DispatcherOperationStatus.Aborted)
                throw new InvalidOperationException("The app's startup could not be taken off the dispatcher: " + queued[0].Status + ".");
            return app;
        }

        /// <summary>Lays the page out at the picture's size. With no window
        /// around it nothing else does.</summary>
        private static void Lay(FrameworkElement ground)
        {
            ground.Measure(new Size(Width, Height));
            ground.Arrange(new Rect(0, 0, Width, Height));
            ground.UpdateLayout();
        }

        /// <summary>Runs the dispatcher for a while, so the bindings the
        /// last change set off are done. A fixed time and no wait for the
        /// dispatcher to fall idle: a page with a live meter never is.</summary>
        private static void Settle(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Normal, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>The assembly WPF reads an application path from. WPF
        /// keeps it in two statics and takes the entry assembly when they
        /// are empty.</summary>
        private static void SetResourceAssembly(Assembly assembly)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var ofApp = typeof(Application).GetField("_resourceAssembly", flags);
            var ofUris = typeof(System.Windows.Navigation.BaseUriHelper).GetField("_resourceAssembly", flags);
            if (ofApp == null || ofUris == null)
                throw new InvalidOperationException("WPF's resource assembly statics moved. The page cannot be drawn in a test host.");
            ofApp.SetValue(null, assembly);
            ofUris.SetValue(null, assembly);
        }
    }
}
