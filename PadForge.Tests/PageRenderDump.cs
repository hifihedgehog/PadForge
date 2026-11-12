using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    /// a real setup may not have: a trigger of every kind of input, a recording, an open step
    /// of every action type, and a menu of every style, each a second time in a tall picture with
    /// its More row open. The page stands in no window, so nothing it does as it loads
    /// runs. Does nothing unless PADFORGE_PAGE_RENDER_DIR names a folder, and then writes one PNG
    /// per state into it, at the scale in PADFORGE_PAGE_RENDER_SCALE (1.5 when unset), and the
    /// steps it reached into steps.txt. It also writes lookups.txt: for each state, every binding
    /// of a part that shows which looks up an ancestor or a named element and found none. Such a
    /// binding leaves its part as it was made, so a part that should hide shows (the selected
    /// action's fields showed with no action selected, 2026-11-11).</summary>
    [Collection("WpfApplicationSingleton")]
    public class PageRenderDump
    {
        private const double Width = 1473, Height = 985, TallHeight = 3600;

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

        private static MacroItem PickedStep()
        {
            var macro = QuickCombo();
            macro.Name = "Picked Step";
            macro.SelectedAction = macro.Actions[1];
            return macro;
        }

        // A macro whose one step is of a type and is picked, so the step
        // stands open on that type's fields.
        private static MacroItem TypePicked(MacroActionType type, Action<MacroAction> seed = null)
        {
            var macro = new MacroItem { Name = type.ToString() };
            var action = new MacroAction { Type = type };
            seed?.Invoke(action);
            macro.Actions.Add(action);
            macro.SelectedAction = action;
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
                    var states = new List<(string File, MacroItem Macro)>
                    {
                        ("macros-quick-combo.png", QuickCombo()),
                        ("macros-step-picked.png", PickedStep()),
                        ("macros-axis-inputs.png", AxisInputs()),
                        ("macros-no-inputs.png", new MacroItem { Name = "New Macro" }),
                        ("macros-recording.png", new MacroItem { Name = "Recording", TriggerButtons = 0x1000, IsRecordingTrigger = true }),
                    };
                    // A step of every action type, open on that type's fields.
                    foreach (MacroActionType type in Enum.GetValues(typeof(MacroActionType)))
                    {
                        states.Add(("macros-type-" + type + ".png", TypePicked(type,
                            type == MacroActionType.LightbarColor ? a => a.AddLightbarPaletteColorCommand.Execute(null) : null)));
                    }
                    foreach (var (_, macro) in states) vm.Macros.Add(macro);
                    var page = new PadPage { DataContext = vm };
                    var ground = new Border { Background = (Brush)app.FindResource("SteelGroundBrush"), Child = page };
                    Step("the page is made");
                    vm.SelectedConfigTab = PadViewModel.MacrosTabIndex;

                    // Shows a state, lets it settle, and pictures it.
                    void Picture(string file, Action show, double height = Height)
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        show();
                        Lay(ground, height);
                        long shown = clock.ElapsedMilliseconds;
                        Settle(dispatcher);
                        Lay(ground, height);
                        var bitmap = new RenderTargetBitmap((int)Math.Round(Width * scale), (int)Math.Round(height * scale),
                            96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(ground);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(dir, file));
                        png.Save(stream);
                        Step("drew " + file + ", first laid out in " + shown + " ms, " + GroupsMade(ground));
                    }

                    void Lookups(string file)
                    {
                        var lost = LostLookups(ground);
                        File.AppendAllLines(Path.Combine(dir, "lookups.txt"),
                            new[] { file + ": " + lost.Count + " lookups found nothing" }.Concat(lost.Select(l => "   " + l)));
                    }

                    foreach (var (file, macro) in states)
                    {
                        Picture(file, () => vm.SelectedMacro = macro);
                        // How long picking another step takes: its fields are
                        // made anew each time.
                        if (macro.SelectedAction != null && macro.Actions.Count > 1)
                        {
                            var times = new List<long>();
                            var picked = macro.SelectedAction;
                            for (int round = 0; round < 3; round++)
                            {
                                foreach (var action in macro.Actions)
                                {
                                    var pick = System.Diagnostics.Stopwatch.StartNew();
                                    macro.SelectedAction = action;
                                    Lay(ground, Height);
                                    times.Add(pick.ElapsedMilliseconds);
                                }
                            }
                            macro.SelectedAction = picked;
                            Lay(ground, Height);
                            Step("picking a step lays out in " + string.Join(", ", times) + " ms");
                            Step("right after a pick: " + GroupsMade(ground));
                        }
                        Lookups(file);
                    }

                    // A menu of every style, in the menus' part of the section.
                    foreach (PadForge.Engine.Menus.MenuKind kind in Enum.GetValues(typeof(PadForge.Engine.Menus.MenuKind)))
                    {
                        var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1 + (int)kind, Name = kind + " Menu", Kind = kind });
                        vm.Menus.Add(menu);
                        string file = "menus-" + kind.ToString().ToLowerInvariant() + ".png";
                        Picture(file, () => vm.ListedMenu = menu);
                        Lookups(file);
                        // The whole editor: tall enough for every row, with More
                        // open. The fold is closed again after, since its state
                        // is the app's and outlives the page.
                        var more = (System.Windows.Controls.Expander)page.FindName("MenuMore");
                        string tall = file.Replace(".png", "-open.png");
                        Picture(tall, () => more.IsExpanded = true, TallHeight);
                        Lookups(tall);
                        more.IsExpanded = false;
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
            Assert.True(thread.Join(TimeSpan.FromMinutes(8)), "The page render timed out. See steps.txt in the folder.");
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

        // The properties a binding most often shows, hides, fills or wires a
        // part through.
        private static readonly DependencyProperty[] Looked =
        {
            UIElement.VisibilityProperty, UIElement.IsEnabledProperty, FrameworkElement.DataContextProperty,
            FrameworkElement.WidthProperty, FrameworkElement.MaxWidthProperty, FrameworkElement.TagProperty, FrameworkElement.ToolTipProperty,
            ButtonBase.CommandProperty, ButtonBase.CommandParameterProperty, ToggleButton.IsCheckedProperty,
            TextBlock.TextProperty, TextBox.TextProperty, ContentControl.ContentProperty,
            ItemsControl.ItemsSourceProperty, Selector.SelectedItemProperty, Selector.SelectedValueProperty, Selector.SelectedIndexProperty,
            RangeBase.ValueProperty, Control.ForegroundProperty, System.Windows.Shapes.Shape.StrokeProperty, System.Windows.Shapes.Shape.FillProperty,
        };

        /// <summary>The bindings under a part that look up an ancestor or a
        /// named element and are not bound.</summary>
        private static List<string> LostLookups(DependencyObject root)
        {
            var lost = new List<string>();
            var open = new Stack<DependencyObject>();
            open.Push(root);
            while (open.Count > 0)
            {
                var part = open.Pop();
                foreach (var property in Looked)
                {
                    var bound = BindingOperations.GetBindingExpression(part, property);
                    var binding = bound?.ParentBinding;
                    if (binding == null || bound.Status == BindingStatus.Active) continue;
                    bool ancestor = binding.RelativeSource != null && binding.RelativeSource.Mode == RelativeSourceMode.FindAncestor;
                    if (!ancestor && string.IsNullOrEmpty(binding.ElementName)) continue;
                    lost.Add(part.GetType().Name + " '" + (part as FrameworkElement)?.Name + "' " + property.Name + " <- " + binding.Path?.Path + " from "
                        + (ancestor ? "the nearest " + binding.RelativeSource.AncestorType?.Name : "the element " + binding.ElementName) + ": " + bound.Status);
                }
                for (int i = VisualTreeHelper.GetChildrenCount(part) - 1; i >= 0; i--) open.Push(VisualTreeHelper.GetChild(part, i));
            }
            return lost;
        }

        /// <summary>How many of the fields' groups are built: a group
        /// stands in a ShownPart, which holds nothing until it shows.</summary>
        private static string GroupsMade(DependencyObject root)
        {
            int groups = 0, made = 0, shown = 0, parts = 0;
            var open = new Stack<DependencyObject>();
            open.Push(root);
            while (open.Count > 0)
            {
                var part = open.Pop();
                parts++;
                if (part is PadForge.Controls.ShownPart group)
                {
                    groups++;
                    if (group.Child != null) made++;
                    if (group.Show) shown++;
                }
                for (int i = VisualTreeHelper.GetChildrenCount(part) - 1; i >= 0; i--) open.Push(VisualTreeHelper.GetChild(part, i));
            }
            return groups + " groups, " + made + " built, " + shown + " shown, " + parts + " parts on the page";
        }

        /// <summary>Lays the page out at the picture's size. With no window
        /// around it nothing else does.</summary>
        private static void Lay(FrameworkElement ground, double height)
        {
            ground.Measure(new Size(Width, height));
            ground.Arrange(new Rect(0, 0, Width, height));
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
