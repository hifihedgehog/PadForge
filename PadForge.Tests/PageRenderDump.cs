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
    /// <summary>Draws the controller's page, the Extras page and the Devices page to pictures without
    /// a screen (#574), to hold each against its boards. PADFORGE_PAGE_RENDER_PAGE names one of them
    /// (pad, header, home, stills, extras or devices) to draw alone. The controller's page is drawn to hold a section
    /// against its board: the page itself under the app's own resources and theme, showing macros
    /// made here. WPF draws to a bitmap whether or not a display is on, and the page shows states
    /// a real setup may not have: a trigger of every kind of input, a recording, an open step
    /// of every action type, and a menu of every style, each a second time in a tall picture with
    /// its More row open, and a ring's cell open on its fields, and each editor with nothing
    /// picked. Then come the first section of each kind of slot, and last the four stacked
    /// sections, Sticks and Triggers, Rumble and Haptics, Motion and Touch, and Lights and
    /// Sound, each drawn whole
    /// and again with its More rows and the lines the string round made open. The page
    /// stands in no window, so nothing it does as it loads
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
                    ApplyTheme(PictureTheme());
                    PadForge.Common.EmberTheme.ApplyAccent();
                    Step("the app's resources and theme are loaded");

                    string only = Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_PAGE");
                    bool Draws(string name) => string.IsNullOrEmpty(only) || string.Equals(only, name, StringComparison.OrdinalIgnoreCase);
                    if (Draws("home")) DrawHomePage(app, dispatcher, dir, scale, Step);
                    if (Draws("header")) DrawPadHeader(app, dispatcher, dir, scale, Step);
                    if (Draws("stills")) DrawStills(dir, Step);
                    if (Draws("extras")) DrawExtrasPages(app, dispatcher, dir, scale, Step);
                    if (Draws("devices")) DrawDevicesPage(app, dispatcher, dir, scale, Step);
                    if (Draws("sticks")) DrawSticksSection(app, dispatcher, dir, scale, Step);
                    if (!Draws("pad")) return;

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
                    page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
                    var ground = new Border { Background = (Brush)app.FindResource("ApplicationBackgroundBrush"), Child = page };
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

                    // Where the ring on the picked control went, in the
                    // ring layer's own coordinates.
                    void Ring(string file)
                    {
                        var ring = (Border)page.FindName("PickRing");
                        var hover = (Border)page.FindName("HoverRing");
                        var layer = (Canvas)page.FindName("PickRingLayer");
                        string at = ring.Visibility == Visibility.Visible
                            ? FormattableString.Invariant($"at {Canvas.GetLeft(ring):0.#},{Canvas.GetTop(ring):0.#} size {ring.Width:0.#}x{ring.Height:0.#} radius {ring.CornerRadius.TopLeft:0.#} in a layer {layer.ActualWidth:0.#}x{layer.ActualHeight:0.#}")
                            : "none";
                        if (hover.Visibility == Visibility.Visible)
                            at += FormattableString.Invariant($", hover at {Canvas.GetLeft(hover):0.#},{Canvas.GetTop(hover):0.#} size {hover.Width:0.#}x{hover.Height:0.#}");
                        File.AppendAllText(Path.Combine(dir, "rings.txt"), file + ": " + at + Environment.NewLine);
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
                    // The macro editor with no macro picked: its hint.
                    Picture("macros-none.png", () => vm.SelectedMacro = null);
                    Lookups("macros-none.png");

                    // A menu of every style, in the menus' part of the section.
                    foreach (PadForge.Engine.Menus.MenuKind kind in Enum.GetValues(typeof(PadForge.Engine.Menus.MenuKind)))
                    {
                        var menu = new MenuEditorItem(new PadForge.Engine.Menus.MenuDefinitionEntry { MenuId = 1 + (int)kind, Name = kind + " Menu", Kind = kind });
                        if (kind == PadForge.Engine.Menus.MenuKind.Radial)
                        {
                            // The Macros board's own two cells: a key, and a macro
                            // with no label of its own.
                            menu.MacroNamesProvider = () => new[] { "Quick Combo" };
                            menu.Cells[0].Label = "Reload";
                            menu.Cells[0].BindingKind = 1;
                            menu.Cells[0].SelectedKeyVk = 0x52;
                            menu.Cells[1].BindingKind = MenuCellItem.MacroKind;
                        }
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
                        if (kind == PadForge.Engine.Menus.MenuKind.Radial)
                        {
                            // A cell open on its fields.
                            string cell = file.Replace(".png", "-cell.png");
                            Picture(cell, () => menu.SelectedCell = menu.Cells[0]);
                            Lookups(cell);
                            menu.SelectedCell = null;
                        }
                    }

                    // The menu editor with no menu picked: its hint.
                    Picture("menus-none.png", () => vm.SelectedMenu = null);
                    Lookups("menus-none.png");

                    // The first section of each kind of slot, last, since a
                    // slot's kind changes what the whole page holds.
                    foreach (var type in new[]
                             {
                                 PadForge.Engine.VirtualControllerType.Xbox, PadForge.Engine.VirtualControllerType.KeyboardMouse,
                                 PadForge.Engine.VirtualControllerType.Midi, PadForge.Engine.VirtualControllerType.Extended,
                                 PadForge.Engine.VirtualControllerType.Vr,
                             })
                    {
                        string file = "slot-" + type.ToString().ToLowerInvariant() + ".png";
                        if (type == PadForge.Engine.VirtualControllerType.Vr)
                        {
                            // The picture of the two hands is built as its view
                            // loads, which a page in no window never does.
                            var hands = page.FindName("VRPreview");
                            hands.GetType().GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(hands, null);
                        }
                        Picture(file, () => { vm.OutputType = type; vm.SelectedConfigTab = 0; });
                        Lookups(file);
                        // With its first control picked: the bar under the
                        // picture, and what the slot has under that.
                        string picked = file.Replace(".png", "-picked.png");
                        Picture(picked, () => vm.PickedTarget = vm.Mappings.First().TargetSettingName, TallHeight / 2);
                        Lookups(picked);
                        Ring(picked);
                        // More picks of each kind of picture: a control of
                        // several rows, a stick, a trigger, a key, a note.
                        // The ring is on the whole control each time.
                        var more = new List<string>();
                        switch (type)
                        {
                            case PadForge.Engine.VirtualControllerType.Xbox:
                                more.AddRange(new[] { "DPadUp", "LeftThumbAxisX", "LeftTrigger", "LeftShoulder" });
                                break;
                            case PadForge.Engine.VirtualControllerType.KeyboardMouse:
                                more.AddRange(new[] { "KbmKey57", "KbmMouseX", "KbmScroll" });
                                break;
                            case PadForge.Engine.VirtualControllerType.Midi:
                                more.Add("MidiNote0");
                                break;
                            case PadForge.Engine.VirtualControllerType.Extended:
                                more.Add("RawBtn0");
                                more.AddRange(vm.Mappings.Where(m => m.Category == MappingCategory.Triggers).Take(1).Select(m => m.TargetSettingName));
                                more.AddRange(vm.Mappings.Where(m => m.Category == MappingCategory.DPad).Take(1).Select(m => m.TargetSettingName));
                                break;
                            case PadForge.Engine.VirtualControllerType.Vr:
                                more.AddRange(new[] { "VrRTrigger", "VrLStickX", "VrRGripClick" });
                                break;
                        }
                        foreach (string target in more)
                        {
                            string also = file.Replace(".png", "-picked-" + target.ToLowerInvariant() + ".png");
                            Picture(also, () => vm.PickedTarget = target);
                            Ring(also);
                        }
                        if (type is PadForge.Engine.VirtualControllerType.Xbox or PadForge.Engine.VirtualControllerType.KeyboardMouse)
                        {
                            // The list: a gamepad's with its groups, and a
                            // Keyboard + Mouse slot's, the longest table a slot
                            // has. steps.txt says how long each first open took.
                            string list = file.Replace(".png", "-list.png");
                            Picture(list, () => vm.SelectedConfigTab = PadViewModel.MappingsTabIndex);
                            Lookups(list);
                            vm.SelectedConfigTab = 0;
                        }
                        if (type == PadForge.Engine.VirtualControllerType.Xbox)
                        {
                            // A recording on the 3D model: the control flashes
                            // and the line under the picture says what it
                            // listens for. A stick's side flashes its quadrant.
                            foreach (string target in new[] { "ButtonA", "LeftThumbAxisXNeg" })
                            {
                                string listening = file.Replace(".png", "-recording-" + target.ToLowerInvariant() + ".png");
                                Picture(listening, () => vm.CurrentRecordingTarget = target);
                                vm.CurrentRecordingTarget = null;
                            }
                            // A recording that could not start says why there.
                            string refused = file.Replace(".png", "-refused.png");
                            Picture(refused, () =>
                            {
                                vm.PickedTarget = "ButtonY";
                                vm.ShowRecordRefusal(PadForge.Resources.Strings.Strings.Instance.Status_NoDeviceToRecord);
                            });
                            vm.PickedTarget = null;
                            // The 2D model. The page shows it by a setting of
                            // the window's, which a page in no window lacks,
                            // so it is shown by hand and put back after.
                            var model3D = (UIElement)page.FindName("ControllerModel3D");
                            var model2D = (ControllerModel2DView)page.FindName("ControllerModel2D");
                            model3D.Visibility = Visibility.Collapsed;
                            model2D.Visibility = Visibility.Visible;
                            model2D.Bind(vm);
                            foreach (string target in new[] { "ButtonA", "DPadUp", "LeftThumbAxisX", "LeftTrigger", "LeftShoulder" })
                            {
                                string flat = file.Replace(".png", "-2d-picked-" + target.ToLowerInvariant() + ".png");
                                Picture(flat, () => vm.PickedTarget = target);
                                Ring(flat);
                            }
                            // The pointer on a control beside the picked one:
                            // its own fainter line, as the board's hover. On
                            // the picked control it draws nothing more.
                            var hoverAt = typeof(PadPage).GetMethod("OnHoverTargetChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
                            vm.PickedTarget = "ButtonA";
                            foreach (string under in new[] { "ButtonB", "ButtonA" })
                            {
                                string hovered = file.Replace(".png", "-2d-hover-" + under.ToLowerInvariant() + ".png");
                                Picture(hovered, () => hoverAt.Invoke(page, new object[] { model2D, under }));
                                Ring(hovered);
                            }
                            hoverAt.Invoke(page, new object[] { model2D, null });
                            // A recording on the 2D model.
                            string flatListening = file.Replace(".png", "-2d-recording-buttona.png");
                            Picture(flatListening, () => vm.CurrentRecordingTarget = "ButtonA");
                            vm.CurrentRecordingTarget = null;
                            model2D.Unbind();
                            model2D.Visibility = Visibility.Collapsed;
                            model3D.Visibility = Visibility.Visible;

                            // The options of a picked control, open under the
                            // bar: each kind of source, a source added and
                            // each kind of that, the trim settings and the
                            // formula, and the list's open row.
                            vm.PickedTarget = "LeftTrigger";
                            vm.IsPickedOptionsOpen = true;
                            var row = vm.PickedRow;
                            var toOptions = (ScrollViewer)page.FindName("ButtonsScroll");
                            // The options stand under the picture and the
                            // bar, so the page is scrolled to its foot.
                            void Options(string name, Action show)
                            {
                                string shot = "opts-" + name + ".png";
                                Picture(shot, () =>
                                {
                                    show();
                                    Lay(ground, Height);
                                    toOptions.ScrollToEnd();
                                });
                                Lookups(shot);
                            }
                            Options("direct", () => { });
                            foreach (string kind in new[] { "Toggle", "RapidTrigger", "Incremental", "InvertOnHold", "Ramped" })
                                Options("kind-" + kind.ToLowerInvariant(), () => row.PrimaryKindSource.Kind = kind);
                            row.PrimaryKindSource.Kind = "Direct";
                            row.AddExtraSourceCommand.Execute(null);
                            var added = row.ExtraSources[row.ExtraSources.Count - 1];
                            Options("added", () => { });
                            foreach (string kind in new[] { "RapidTrigger", "Incremental", "InvertOnHold", "Ramped" })
                                Options("added-" + kind.ToLowerInvariant(), () => added.Kind = kind);
                            added.Kind = "Direct";
                            Options("trim", () => row.CombineMode = "StickTrim");
                            Options("formula", () => row.CombineMode = "Custom");
                            row.CombineMode = "";
                            Picture("opts-list-added.png", () => vm.SelectedConfigTab = PadViewModel.MappingsTabIndex, TallHeight / 2);
                            Lookups("opts-list-added.png");
                            vm.SelectedConfigTab = 0;
                            row.ExtraSources.Remove(added);
                            vm.IsPickedOptionsOpen = false;
                            toOptions.ScrollToHome();
                        }
                        vm.PickedTarget = null;
                        if (type == PadForge.Engine.VirtualControllerType.Midi)
                        {
                            // The slot's own card under the picture, with its
                            // More row open and closed again after.
                            var midiMore = (System.Windows.Controls.Expander)page.FindName("MidiMore");
                            string open = file.Replace(".png", "-more.png");
                            Picture(open, () => midiMore.IsExpanded = true, TallHeight / 2);
                            Lookups(open);
                            midiMore.IsExpanded = false;
                        }
                        if (type == PadForge.Engine.VirtualControllerType.Extended)
                        {
                            // The slot's own card with Customize on, the page
                            // scrolled to its foot, where what Customize shows
                            // stands. Off again and back at the top after.
                            var buttonsScroll = (ScrollViewer)page.FindName("ButtonsScroll");
                            string custom = file.Replace(".png", "-custom.png");
                            Picture(custom, () =>
                            {
                                vm.ExtendedConfig.Customize = true;
                                Lay(ground, Height);
                                buttonsScroll.ScrollToEnd();
                            });
                            Lookups(custom);
                            vm.ExtendedConfig.Customize = false;
                            buttonsScroll.ScrollToHome();
                        }
                    }

                    // A motion row's options, on a slot whose type has motion
                    // rows: by speed, and by angle.
                    vm.OutputType = PadForge.Engine.VirtualControllerType.PlayStation;
                    vm.SelectedConfigTab = 0;
                    vm.PickedTarget = PadForge.Engine.Data.MappingSetMigrator.MotionPitchTarget;
                    vm.IsPickedOptionsOpen = true;
                    var toMotion = (ScrollViewer)page.FindName("ButtonsScroll");
                    Picture("opts-motion.png", () => { Lay(ground, Height); toMotion.ScrollToEnd(); });
                    Lookups("opts-motion.png");
                    Picture("opts-motion-angle.png", () =>
                    {
                        vm.PickedRow.MotionResponse = "Angle";
                        Lay(ground, Height);
                        toMotion.ScrollToEnd();
                    });
                    Lookups("opts-motion-angle.png");
                    vm.PickedRow.MotionResponse = "";
                    vm.IsPickedOptionsOpen = false;
                    vm.PickedTarget = null;
                    toMotion.ScrollToHome();

                    // The four stacked sections, each in a tall picture with
                    // the groups and cards its board draws put up by hand: a
                    // page in no window has no device, and the page shows a
                    // group by the device's abilities. Then every group of the
                    // section with its More rows and the lines the string round
                    // made open, which close again after, since their state is
                    // the app's and outlives the page.
                    void Show(IEnumerable<string> names)
                    {
                        foreach (string name in names) ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
                    }
                    void Fold(bool open, IEnumerable<string> names)
                    {
                        foreach (string name in names) ((System.Windows.Controls.Expander)page.FindName(name)).IsExpanded = open;
                    }
                    // The audio chain's values as the Lights board draws them:
                    // the Jan Meier preset, and the EQ on. The Rumble board's
                    // right trigger plays Weapon, which shows its Range row.
                    vm.DeviceConfig.AudioCrossfeedLevel = 7;
                    vm.DeviceConfig.AudioEqEnabled = true;
                    vm.DeviceConfig.RightTriggerMode = AdaptiveTriggerMode.Weapon;
                    var sections = new (string Name, int Tab, string[] Board, string[] More, string[] Open)[]
                    {
                        ("sticks", 4, new[] { "TabSticks", "TabTriggers" }, new string[0], new string[0]),
                        ("rumble", 5, new[] { "TabForceFeedback", "TabAdaptiveTriggers", "TabBassShakers" },
                            new[] { "TabImpulseTriggers", "TabWheel", "WheelRangeRow", "WheelRpmRow", "FalconCard" },
                            new[] { "ForceFeedbackMore", "ImpulseTriggersMore", "BassShakersMore" }),
                        ("motion", 8, new[] { "TabGyro", "TabTouchpad", "GyroCalibrationCard", "GyroSensitivityCard", "GyroEngageCard",
                                "GyroResponseCard", "GyroSpaceCard", "GyroPassthroughCard", "GyroRatesLive", "GyroSensitivityMore" },
                            new[] { "GyroSimulationCard", "CompassYawCard", "TabPointer" },
                            new[] { "GyroSensitivityMore", "GyroGripCard", "GyroSpaceCard", "TouchpadMouseMore", "IrPointerMore" }),
                        ("lights", 7, new[] { "TabLighting", "LightbarModeCard", "IndicatorLedsCard", "PeripheralLightingCard", "TabAudio",
                                "CrossfeedFoldCard", "ParametricEqFoldCard", "LimiterFoldCard" },
                            new[] { "GuideLedCard" },
                            new[] { "SoundOutputMore", "CrossfeedFoldCard", "ParametricEqFoldCard", "LimiterFoldCard", "SoundMacrosFoldCard" }),
                    };
                    foreach (var (name, tab, board, more, open) in sections)
                    {
                        vm.SelectedConfigTab = tab;
                        Lay(ground, TallHeight);
                        Settle(dispatcher);
                        string file = "section-" + name + ".png";
                        Picture(file, () => Show(board), TallHeight);
                        Lookups(file);
                        string all = "section-" + name + "-open.png";
                        Picture(all, () => { Show(more); Fold(true, open); }, TallHeight);
                        Lookups(all);
                        Fold(false, open);
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

        /// <summary>A page on the ground the window gives a page (the
        /// shell's content ground, which follows the theme), with the text
        /// defaults the window gives every page (MainWindow.xaml: the body
        /// face at 13 in the primary color).</summary>
        private static Border Ground(App app, FrameworkElement page)
        {
            var ground = new Border { Background = (Brush)app.FindResource("ApplicationBackgroundBrush"), Child = page };
            System.Windows.Documents.TextElement.SetFontFamily(ground, (FontFamily)app.FindResource("BodyFontFamily"));
            System.Windows.Documents.TextElement.SetFontSize(ground, 13);
            System.Windows.Documents.TextElement.SetForeground(ground, (Brush)app.FindResource("TextFillColorPrimaryBrush"));
            return ground;
        }

        /// <summary>Shows a state, lets it settle, pictures it at the given
        /// size and writes its lookups that found nothing.</summary>
        private static void Shoot(FrameworkElement ground, Dispatcher dispatcher, string dir, double scale, string file,
            double width, double height, Action show, Action<string> step)
        {
            show?.Invoke();
            LayAt(ground, width, height);
            Settle(dispatcher);
            LayAt(ground, width, height);
            var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(ground);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(dir, file))) png.Save(stream);
            var lost = LostLookups(ground);
            File.AppendAllLines(Path.Combine(dir, "lookups.txt"),
                new[] { file + ": " + lost.Count + " lookups found nothing" }.Concat(lost.Select(l => "   " + l)));
            step("drew " + file);
        }

        private static void LayAt(FrameworkElement ground, double width, double height)
        {
            ground.Measure(new Size(width, height));
            ground.Arrange(new Rect(0, 0, width, height));
            ground.UpdateLayout();
        }

        /// <summary>Home (#574, the world-class pass) as its board draws it: the engine forging at
        /// 1000 Hz, a device on offer, a forging controller with three devices, a waiting one, a
        /// cold one, the Add Controller tile and two extras on. Then the same page with the engine
        /// stopped, and the forging page again at a narrow width, where the parts wrap. The width is
        /// the board's beside its sidebar (1176).</summary>
        private static void DrawHomePage(App app, Dispatcher dispatcher, string dir, double scale, Action<string> step)
        {
            const double PageWidth = 1176;
            var s = PadForge.Resources.Strings.Strings.Instance;
            var dash = new DashboardViewModel { RemoteLink = new SettingsViewModel() };

            PadViewModel.MappedDeviceInfo Device(string name, bool online, bool bluetooth, int battery, bool sending = false)
            {
                var device = new PadViewModel.MappedDeviceInfo
                {
                    Name = name, IsOnline = online, IsSending = sending, TransportGlyph = bluetooth ? "" : string.Empty,
                };
                device.SetBattery(online ? battery : -1, false, 15);
                return device;
            }
            // The finish the next card's still is drawn in.
            string stillFinish = null;
            SlotSummary Slot(int index, PadForge.Engine.VirtualControllerType type, string profile, string preset,
                params PadViewModel.MappedDeviceInfo[] devices)
            {
                var slot = new SlotSummary(index) { OutputType = type, PresetName = preset, TypeInstanceLabel = "1", SlotNumber = index + 1 };
                slot.MappedDevices = new System.Collections.ObjectModel.ObservableCollection<PadViewModel.MappedDeviceInfo>(devices);
                slot.MappedDeviceCount = devices.Length;
                slot.ConnectedDeviceCount = devices.Count(d => d.IsOnline);
                string art = PadForge.Common.StagePictures.PathFor(profile, type, _ => null);
                // A still of the 3D model, as the app shows by default,
                // or the 2D art for a type with no model.
                var (family, finish) = PadForge.Common.StagePictures.StillFor(profile, type, _ => stillFinish);
                if (family != null)
                {
                    PadForge.Common.StagePictures.MakeStill(family, finish);
                    PadForge.Common.StagePictures.TryGetStill(family, finish, out var color, out var gray);
                    slot.SetPicture(profile, type, string.Empty, true, color, gray);
                }
                else
                    slot.SetPicture(profile, type, string.Empty, false, PadForge.Common.StagePictures.Color(art), PadForge.Common.StagePictures.Gray(art));
                return slot;
            }

            stillFinish = "VelocityGreen";
            var xbox = Slot(0, PadForge.Engine.VirtualControllerType.Xbox, "xbox-series-xs-bluetooth", "Xbox Series X|S Controller (Bluetooth)",
                Device("DualSense Wireless Controller", true, true, 15, sending: true),
                Device("Xbox Series X Controller", true, true, 90),
                Device("Logitech G29 Driving Force Racing Wheel", false, false, -1));
            xbox.IsVirtualControllerConnected = true;
            xbox.StageLedger.Add(new SlotStageInfo("Sticks", string.Empty, isStickShape: true) { IsHot = true, Summary = "x" });
            xbox.StageLedger.Add(new SlotStageInfo("Triggers", string.Empty, isTriggerShape: true));
            xbox.StageLedger.Add(new SlotStageInfo("Gyro", ""));
            xbox.StageLedger.Add(new SlotStageInfo("Lighting", "") { IsHot = true, Summary = "x" });
            xbox.StageLedger.Add(new SlotStageInfo("Touchpad", ""));
            xbox.StageLedger.Add(new SlotStageInfo("Audio", ""));
            stillFinish = "DeepEarthVolcanic";
            var playStation = Slot(1, PadForge.Engine.VirtualControllerType.PlayStation, "dualsense-composite", "DualSense (PS5): Full",
                Device("Nintendo Switch Pro Controller", false, false, -1));
            playStation.StageLedger.Add(new SlotStageInfo("Sticks", string.Empty, isStickShape: true) { IsHot = true, Summary = "x" });
            playStation.StageLedger.Add(new SlotStageInfo("Triggers", string.Empty, isTriggerShape: true));
            playStation.StageLedger.Add(new SlotStageInfo("Gyro", ""));
            stillFinish = null;
            var nintendo = Slot(2, PadForge.Engine.VirtualControllerType.Nintendo, "switch-pro", "Nintendo Switch Pro Controller");
            var keys = Slot(3, PadForge.Engine.VirtualControllerType.KeyboardMouse, string.Empty, "Keyboard + Mouse",
                Device("Xbox Series X Controller", true, true, 90));
            keys.TypeInstanceLabel = "1";

            dash.EngineStateKey = "Running";
            dash.EngineStatus = s.Engine_Forging;
            dash.PollingFrequency = 1000;
            dash.OnlineDevices = 17;
            dash.SlotSummaries.Add(xbox);
            dash.SlotSummaries.Add(playStation);
            dash.SlotSummaries.Add(nintendo);
            dash.SetHomeOffer(Guid.NewGuid(), "Xbox Elite Wireless Controller Series 2");
            dash.OverlaysAnyEnabled = true;
            dash.EnableWebController = true;
            dash.WebControllerStatus = s.Common_Running;

            var page = new HomePage { DataContext = dash };
            var ground = Ground(app, page);
            step("Home is made");

            void Shot(string file, double width, double height, Action show) =>
                Shoot(ground, dispatcher, dir, scale, file, width, height, show, step);
            Shot("home.png", PageWidth, 1068, null);
            Shot("home-narrow.png", 640, 2000, null);
            // A fourth controller of a type with no picture, which takes the Add tile's place in the grid.
            Shot("home-four.png", PageWidth, 1300, () =>
            {
                keys.IsVirtualControllerConnected = true;
                dash.SlotSummaries.Add(keys);
            });
            dash.SlotSummaries.Remove(keys);
            Shot("home-stopped.png", PageWidth, 1068, () =>
            {
                dash.EngineStateKey = "Stopped";
                dash.EngineStatus = s.Common_Stopped;
                dash.PollingFrequency = 0;
            });
            Shot("home-idle.png", PageWidth, 1068, () =>
            {
                dash.EngineStateKey = "Idle";
                dash.EngineStatus = s.Common_Idle;
                dash.PollingFrequency = 1000;
                xbox.IsVirtualControllerConnected = false;
                xbox.ConnectedDeviceCount = 0;
                foreach (var device in xbox.MappedDevices) { device.IsOnline = false; device.IsSending = false; device.SetBattery(-1, false, 15); }
                dash.ClearHomeOffer();
            });
        }

        /// <summary>The still of every 3D model in its first finish, as a Home card's stage shows
        /// it, and how long each took to make.</summary>
        private static void DrawStills(string dir, Action<string> step)
        {
            foreach (string family in new[] { "XBOX360", "XboxSeries", "DS3", "DS4", "DualSense", "DualSenseEdge", "Switch2Pro", "SteamDeck", "SteamController", "SteamController2" })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                string finish = PadForge.Models3D.ControllerStill.FinishFor(family, null);
                PadForge.Common.StagePictures.MakeStill(family, finish);
                PadForge.Common.StagePictures.TryGetStill(family, finish, out var color, out var gray);
                long ms = clock.ElapsedMilliseconds;
                if (color is not BitmapSource picture)
                {
                    step("no still of " + family + " after " + ms + " ms");
                    continue;
                }
                foreach (var (name, source) in new[] { ("still-" + family + ".png", picture), ("still-" + family + "-gray.png", (BitmapSource)gray) })
                {
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(source));
                    using var stream = File.Create(Path.Combine(dir, name));
                    png.Save(stream);
                }
                step("still of " + family + " (" + (finish ?? "one finish") + "): " + picture.PixelWidth + " by " + picture.PixelHeight + " in " + ms + " ms");
            }
        }

        /// <summary>The controller page's header (#574, the world-class pass) as its board draws it:
        /// a forging Xbox controller driven by three devices, one sending input with a low battery,
        /// one resting and one not connected. Then the same header waiting, cold, and at a narrow
        /// width. Only the page's top is drawn.</summary>
        private static void DrawPadHeader(App app, Dispatcher dispatcher, string dir, double scale, Action<string> step)
        {
            const double PageWidth = 1176;
            var dash = new DashboardViewModel { RemoteLink = new SettingsViewModel() };
            var vm = new PadViewModel(0) { OutputType = PadForge.Engine.VirtualControllerType.Xbox };
            PadViewModel.MappedDeviceInfo Device(string name, bool online, bool bluetooth, int battery, bool sending = false)
            {
                var device = new PadViewModel.MappedDeviceInfo
                {
                    Name = name, IsOnline = online, IsSending = sending, TransportGlyph = bluetooth ? "" : string.Empty,
                };
                device.SetBattery(online ? battery : -1, false, 15);
                return device;
            }
            vm.MappedDevices.Add(Device("DualSense Wireless Controller", true, true, 15, sending: true));
            vm.MappedDevices.Add(Device("Xbox Series X Controller", true, true, 90));
            vm.MappedDevices.Add(Device("Logitech G29 Driving Force Racing Wheel", false, false, -1));
            vm.SelectedMappedDevice = vm.MappedDevices[0];

            var slot = new SlotSummary(0) { OutputType = vm.OutputType, MappedDeviceCount = 3, ConnectedDeviceCount = 2, IsVirtualControllerConnected = true };
            dash.EngineStateKey = "Running";
            dash.SlotSummaries.Add(slot);
            dash.SetSelectedPad(0);

            var page = new PadPage { DataContext = vm };
            page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
            page.BindSlotState(dash);
            var ground = Ground(app, page);
            step("the controller page is made");

            void Shot(string file, double width, double height, Action show) =>
                Shoot(ground, dispatcher, dir, scale, file, width, height, show, step);
            Shot("header.png", PageWidth, 420, null);
            Shot("header-second-picked.png", PageWidth, 420, () => vm.SelectedMappedDevice = vm.MappedDevices[1]);
            Shot("header-macros.png", PageWidth, 420, () => vm.SelectedConfigTab = PadViewModel.MacrosTabIndex);
            Shot("header-narrow.png", 640, 520, null);
            Shot("header-waiting.png", PageWidth, 420, () =>
            {
                slot.IsVirtualControllerConnected = false;
                slot.ConnectedDeviceCount = 0;
                foreach (var device in vm.MappedDevices) { device.IsOnline = false; device.IsSending = false; device.SetBattery(-1, false, 15); }
            });
            Shot("header-cold.png", PageWidth, 420, () =>
            {
                vm.MappedDevices.Clear();
                slot.MappedDeviceCount = 0;
            });
        }

        /// <summary>The Extras page (#574): the catalog and each extra's page in the states their
        /// boards draw, at the page's width beside the sidebar of a 1280-wide window (1031). A
        /// picture with More open is drawn tall and its card closed again after, since its state
        /// is the app's. The engine's switches the setters write are put back at the end.</summary>
        private static void DrawExtrasPages(App app, Dispatcher dispatcher, string dir, double scale, Action<string> step)
        {
            const double PageWidth = 1031;
            bool udp = PadForge.Common.Input.HeadTrackingRuntime.Enabled;
            bool free = PadForge.Common.Input.HeadTrackingRuntime.FreeTrackEnabled;
            bool xr = PadForge.Common.Input.HeadTrackingRuntime.OpenXrEnabled;
            bool telemetry = PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Enabled;
            bool chat = PadForge.Common.Input.Twitch.TwitchChatRuntime.Enabled;
            try
            {
                var s = PadForge.Resources.Strings.Strings.Instance;
                var settings = new SettingsViewModel();
                var dash = new DashboardViewModel
                {
                    RemoteLink = settings,
                    DiscoverOpenXrRuntimes = () => Array.Empty<PadForge.Engine.Common.OpenXr.OpenXrRuntimeEntry>(),
                };
                dash.HeadTrackingFreeTrack = false;
                dash.HeadTrackingOpenXr = false;
                var page = new ExtrasPage { DataContext = dash };
                var ground = Ground(app, page);
                step("the Extras page is made");

                void Shot(string file, double height, Action show) =>
                    Shoot(ground, dispatcher, dir, scale, file, PageWidth, height, show, step);
                void ShotMore(string key, string file, double height)
                {
                    Shot(file, height, () =>
                    {
                        page.Show(key);
                        ((System.Windows.Controls.Expander)page.FindName(key + "More")).IsExpanded = true;
                    });
                    ((System.Windows.Controls.Expander)page.FindName(key + "More")).IsExpanded = false;
                }

                // The catalog as its board draws it (the world-class pass): the engine forging, the Web
                // Controller on with a phone connected, and the overlays on. Its widths are a page's beside
                // the sidebar of 264: the board's 1440 frame, then 1280, the window's own 1100 and its least.
                dash.EngineStateKey = "Running";
                dash.EnableWebController = true;
                dash.WebControllerStatus = string.Format(s.Server_RunningClients_Format, 1);
                void Catalog(string file, double width, double height, Action show = null) =>
                    Shoot(ground, dispatcher, dir, scale, file, width, height, () => { page.Show(null); show?.Invoke(); }, step);
                Catalog("extras-catalog.png", 1176, 808);
                Catalog("extras-catalog-1280.png", 1016, 808);
                Catalog("extras-catalog-1100.png", 836, 1000);
                Catalog("extras-catalog-900.png", 636, 1250);
                // The engine stopped: the switches stay on, and nothing glows.
                Catalog("extras-catalog-stopped.png", 1176, 808, () =>
                {
                    dash.EngineStateKey = "Stopped";
                    dash.WebControllerStatus = s.Common_Stopped;
                });
                dash.EngineStateKey = "Running";
                // A long status, cut short at the tile's width.
                Catalog("extras-catalog-long.png", 836, 1000, () =>
                    dash.WebControllerStatus = string.Format(s.Server_RunningOn_Format, "https://192.168.100.120:8080 and http://192.168.100.120:8081"));
                dash.WebControllerStatus = s.Common_Running;
                dash.EnableDsuMotionServer = true;
                dash.DsuServerStatus = string.Format(s.Server_ListeningOn_Format, 26760);

                dash.HeadTrackingEnabled = true;
                dash.HeadTrackingStatus = string.Format(s.HeadTracker_StatusWaiting_Format, 4242);
                Shot("extras-headtracking.png", 900, () => page.Show("HeadTracking"));
                ShotMore("HeadTracking", "extras-headtracking-more.png", 1240);

                dash.EnableGameTelemetry = true;
                dash.GameTelemetryStatus = string.Format(s.Telemetry_Status_Waiting_Format, 20777);
                Shot("extras-gametelemetry.png", 760, () => page.Show("GameTelemetry"));
                ShotMore("GameTelemetry", "extras-gametelemetry-more.png", 900);

                dash.IsDsuServerRunning = true;
                Shot("extras-motionserver.png", 640, () => page.Show("MotionServer"));
                ShotMore("MotionServer", "extras-motionserver-more.png", 640);

                dash.EnableGameModServer = true;
                dash.IsGameModServerRunning = true;
                dash.GameModServerStatus = string.Format(s.Server_ListeningOn_Format, 6969);
                Shot("extras-gamemods.png", 680, () => page.Show("GameMods"));
                ShotMore("GameMods", "extras-gamemods-more.png", 680);

                Shot("extras-overlays.png", 700, () => page.Show("Overlays"));

                // The Web Controller serving, with its QR code.
                const string Url = "https://192.168.1.20:8080";
                dash.WebControllerUrl = Url;
                dash.WebControllerQr = PadForge.Services.WebControllerServer.RenderQr(Url);
                dash.HasWebControllerQr = true;
                dash.IsWebControllerRunning = true;
                dash.WebControllerAccessCode = "4821";
                Shot("extras-webcontroller.png", 820, () => page.Show("WebController"));
                const string Plain = "http://192.168.1.20:8081/?code=4821";
                dash.EnableWebControllerPlainHttp = true;
                dash.WebControllerPlainStatus = s.Common_Running;
                dash.WebControllerPlainUrl = Plain;
                dash.WebControllerPlainQr = PadForge.Services.WebControllerServer.RenderQr(Plain);
                dash.HasWebControllerPlainQr = true;
                ShotMore("WebController", "extras-webcontroller-more.png", 1200);
                dash.AddWebControllerPhone(new PadForge.Engine.WebControllerDevice("xbox360:phone-1", "Xbox 360 Web Controller: Sam"));
                dash.AddWebControllerPhone(new PadForge.Engine.WebControllerDevice("touchpad:tablet-1", "Web Touchpad 1", true, "touchpad"));
                Shot("extras-webcontroller-phones.png", 820, () => page.Show("WebController"));
                dash.WebControllerPhones.Clear();
                dash.HasWebControllerQr = false;
                dash.IsWebControllerRunning = false;
                dash.WebControllerStatus = "Port 8080 in use";
                Shot("extras-webcontroller-stopped.png", 820, () => page.Show("WebController"));

                // Remote Link running with one PC paired and online.
                dash.EnableRemoteLink = true;
                dash.IsRemoteLinkRunning = true;
                dash.RemoteLinkStatus = s.Common_Running;
                settings.TrustedPeers.Add(new RemoteLinkTrustedPeer("Living Room PC", "LIVINGROOM", "3F9A11C08B2E7D4455AA",
                    "", false, true, null, null, null));
                dash.RemoteLinkMyCode = "PF1-7XK2-9QW4";
                Shot("extras-remotelink.png", 900, () => page.Show("RemoteLink"));
                ShotMore("RemoteLink", "extras-remotelink-more.png", 1200);

                dash.EnableTwitchChat = true;
                dash.TwitchChatStatus = s.TwitchChat_Status_NoChannel;
                dash.TwitchChatDisplayUrl = "http://127.0.0.1:8081/display?code=4821";
                Shot("extras-twitchchat.png", 900, () => page.Show("TwitchChat"));

                Shot("extras-touchpadoverlay.png", 640, () => page.Show("TouchpadOverlay"));

                Shot("extras-screenoverlay.png", 800, () => page.Show("ScreenOverlay"));
                dash.AddScreenOverlay();
                Shot("extras-screenoverlay-two.png", 1100, () => page.Show("ScreenOverlay"));
            }
            finally
            {
                PadForge.Common.Input.HeadTrackingRuntime.Enabled = udp;
                PadForge.Common.Input.HeadTrackingRuntime.FreeTrackEnabled = free;
                PadForge.Common.Input.HeadTrackingRuntime.OpenXrEnabled = xr;
                PadForge.Common.Input.Telemetry.GameTelemetryRuntime.Enabled = telemetry;
                PadForge.Common.Input.Twitch.TwitchChatRuntime.Enabled = chat;
            }
        }

        /// <summary>The Sticks and Triggers section of the controller page (#574) alone, at the
        /// page's width beside the sidebar in a window of the default 1100, of 1280, of 1440 and of
        /// 1720, and of the least 900. A page in no window has no device, so the two groups are put
        /// up by hand.</summary>
        private static void DrawSticksSection(App app, Dispatcher dispatcher, string dir, double scale, Action<string> step)
        {
            var vm = new PadViewModel(0);
            var page = new PadPage { DataContext = vm };
            page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
            var ground = Ground(app, page);
            step("the controller page is made");
            vm.SelectedConfigTab = 4;
            void Shot(string file, double width, double height, Action show = null) =>
                Shoot(ground, dispatcher, dir, scale, file, width, height, show, step);
            Shot("sticks-1280.png", 1016, 1900, () =>
            {
                foreach (string name in new[] { "TabSticks", "TabTriggers" })
                    ((UIElement)page.FindName(name)).Visibility = Visibility.Visible;
            });
            Shot("sticks-1100.png", 836, 2300);
            Shot("sticks-1440.png", 1176, 1900);
            Shot("sticks-1720.png", 1456, 1900);
            Shot("sticks-900.png", 636, 3000);
        }

        /// <summary>The Devices page (#574) in the states its board draws, at the page's width beside
        /// the sidebar of a 1280-wide window (1016), of a 1440-wide one (1176) and of a 900-wide one
        /// (636, where the panel stands under the list). The view model holds the board's rows, made here, and the
        /// slots' chips are put in after each pick, since a pick builds them again from the slot
        /// statics, which nothing here touches. A picture with a fold open closes it again after,
        /// since its state is the app's.</summary>
        private static void DrawDevicesPage(App app, Dispatcher dispatcher, string dir, double scale, Action<string> step)
        {
            const double PageWidth = 1016, Narrow = 636;
            const string Bluetooth = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&1&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var vm = new DevicesViewModel();
            DeviceRowViewModel Add(string name, string typeKey, int capType, string path, bool online, ushort vid, ushort pid, int battery = -1)
            {
                var row = new DeviceRowViewModel
                {
                    InstanceGuid = Guid.NewGuid(), DeviceName = name, ProductName = name, DeviceTypeKey = typeKey,
                    DevicePath = path, VendorId = vid, ProductId = pid, IsOnline = online, BatteryPercent = battery,
                    KindGlyph = PadForge.Common.DeviceTypeGlyph.For(capType),
                };
                vm.Devices.Add(row);
                return row;
            }
            // The board's rows: three controllers, five keyboards and mice,
            // three other inputs and four devices that are not connected.
            var dualSense = Add("DualSense Wireless Controller", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad, Bluetooth, true, 0x054C, 0x0CE6, 15);
            dualSense.SlotBadges.Add(new SlotBadge { VcType = PadForge.Engine.VirtualControllerType.Xbox, TypeNumber = 1 });
            dualSense.IsSending = true;
            dualSense.IsHidHideAvailable = true;
            dualSense.HidHideEnabled = true;
            dualSense.ShowIdleDisconnect = true;
            dualSense.ShowQuickCharge = true;
            dualSense.HasRumble = dualSense.HasGyro = dualSense.HasAccel = dualSense.HasTouchpad = true;
            dualSense.AxisCount = 6;
            dualSense.ButtonCount = 13;
            dualSense.PovCount = 1;
            dualSense.HidHideInstancePath = @"HID\VID_054C&PID_0CE6";
            var xbox = Add("Xbox Series X Controller", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad, "XInput#0", true, 0x045E, 0x0B13, 90);
            xbox.SlotBadges.Add(new SlotBadge { VcType = PadForge.Engine.VirtualControllerType.Xbox, TypeNumber = 1 });
            var elite = Add("Xbox Elite Wireless Controller Series 2", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad,
                @"\\?\HID#VID_045E&PID_0B00&IG_00#8&2&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true, 0x045E, 0x0B00, 64);
            elite.BatteryCharging = true;
            Add("Logitech G502 HERO Gaming Mouse", "Mouse", PadForge.Engine.InputDeviceType.Mouse,
                @"\\?\HID#VID_046D&PID_C08B&MI_00#8&4&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", true, 0x046D, 0xC08B);
            var keyboard = Add("Standard PS/2 Keyboard", "Keyboard", PadForge.Engine.InputDeviceType.Keyboard,
                @"\\?\ACPI#PNP0303#4&1&0#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", true, 0, 0);
            keyboard.IsHidHideAvailable = true;
            Add("ITE Device(8258)", "Keyboard", PadForge.Engine.InputDeviceType.Keyboard,
                @"\\?\HID#VID_048D&PID_8258&MI_00#8&5&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", true, 0x048D, 0x8258);
            Add("USB Receiver", "Keyboard", PadForge.Engine.InputDeviceType.Keyboard,
                @"\\?\HID#VID_046D&PID_C547&MI_00#8&6&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}", true, 0x046D, 0xC547);
            Add("Jump Desktop Virtual Mouse Driver", "Mouse", PadForge.Engine.InputDeviceType.Mouse,
                @"\\?\HID#VID_FFFF&PID_0001#8&7&0&0000#{378de44c-56ef-11d1-bc8c-00a0c91405dd}", true, 0xFFFF, 0x0001);
            // A wheel a controller is to use, which is not here: its pill is quiet.
            var wheel = Add("Logitech G29 Driving Force Racing Wheel", "Wheel", PadForge.Engine.InputDeviceType.Driving,
                @"\\?\HID#VID_046D&PID_C24F#8&6&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x046D, 0xC24F);
            wheel.SlotBadges.Add(new SlotBadge { VcType = PadForge.Engine.VirtualControllerType.Xbox, TypeNumber = 1 });
            Add("DualShock 3", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad,
                @"\\?\HID#VID_054C&PID_0268#8&7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x054C, 0x0268);
            Add("Nintendo Wii Remote", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad,
                @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002057e_pid&0306#9&3&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x057E, 0x0306);
            Add("PlayStation Move Motion Controller", "Gamepad", PadForge.Engine.InputDeviceType.Gamepad,
                @"\\?\HID#VID_054C&PID_03D5#8&8&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", false, 0x054C, 0x03D5);
            Add("All Keyboards (Merged)", "Keyboard", PadForge.Engine.InputDeviceType.Keyboard, "aggregate://keyboards", true, 0, 0);
            Add("All Mice (Merged)", "Mouse", PadForge.Engine.InputDeviceType.Mouse, "aggregate://mice", true, 0, 0);
            Add("Headset Microphone", "Microphone", PadForge.Engine.InputDeviceType.Microphone, "mic://capture/0", true, 0, 0);
            vm.RefreshCounts();

            // A pick builds the slots' chips again from the slot statics, so
            // the board's are put in after it: Xbox #1, which the two
            // controllers in use drive, and Keyboard + Mouse #1 and Extended
            // #1, which nothing drives. The panel's picture is asked for as
            // the service asks: a still of the 3D model for a device a
            // preset with art of its own names, and the 2D art where the
            // model cannot be drawn.
            void Pick(DeviceRowViewModel row)
            {
                vm.SelectedDevice = row;
                vm.ActiveSlotItems.Clear();
                if (row == null) return;
                vm.ActiveSlotItems.Add(new SlotButtonItem { PadIndex = 0, SlotNumber = 1, VcType = PadForge.Engine.VirtualControllerType.Xbox, TypeNumber = 1, IsAssigned = row == dualSense || row == xbox });
                vm.ActiveSlotItems.Add(new SlotButtonItem { PadIndex = 1, SlotNumber = 2, VcType = PadForge.Engine.VirtualControllerType.KeyboardMouse, TypeNumber = 1 });
                vm.ActiveSlotItems.Add(new SlotButtonItem { PadIndex = 2, SlotNumber = 3, VcType = PadForge.Engine.VirtualControllerType.Extended, TypeNumber = 1 });
                var (folder, family) = PadForge.Common.StagePictures.ForDevice(row.VendorId, row.ProductId);
                System.Windows.Media.ImageSource color = null, gray = null;
                if (folder != null)
                {
                    string finish = PadForge.Common.StagePictures.FirstFinish(family);
                    PadForge.Common.StagePictures.MakeStill(family, finish);
                    PadForge.Common.StagePictures.TryGetStill(family, finish, out color, out gray);
                    if (color == null)
                    {
                        string art = PadForge.Common.StagePictures.FirstPath(folder);
                        color = PadForge.Common.StagePictures.Color(art);
                        gray = PadForge.Common.StagePictures.Gray(art);
                    }
                }
                vm.SetPicture(row.InstanceGuid, row.VendorId, row.ProductId, true, color, gray);
            }

            // The DualSense in SDL's gamepad layout: the left stick up and
            // right, the right stick at rest, the right trigger at 62% and A
            // held, as the board draws them.
            void PickDualSense()
            {
                Pick(dualSense);
                vm.RebuildRawStateCollections(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 13).ToArray(), 1, gamepadLayout: true);
                vm.HasRawData = true;
                vm.HasGyroData = vm.HasAccelData = vm.HasTouchpadData = true;
                var state = new PadForge.Engine.CustomInputState
                {
                    Axis = new int[PadForge.Engine.CustomInputState.MaxAxis],
                    Buttons = new bool[PadForge.Engine.CustomInputState.MaxButtons],
                };
                state.Axis[0] = 51118;
                state.Axis[1] = 16384;
                state.Axis[3] = 32767;
                state.Axis[4] = 32767;
                state.Axis[5] = 40632;
                state.Buttons[0] = true;
                state.Buttons[9] = true;
                vm.UpdateGamepadTest(state);
                foreach (var axis in vm.RawAxes)
                {
                    axis.RawValue = state.Axis[axis.Index];
                    axis.NormalizedValue = state.Axis[axis.Index] / 65535.0;
                }
                foreach (var button in vm.RawButtons)
                    button.IsPressed = state.Buttons[button.Index];
            }

            var page = new DevicesPage { DataContext = vm };
            var ground = Ground(app, page);
            step("the Devices page is made");
            void Shot(string file, double width, double height, Action show) =>
                Shoot(ground, dispatcher, dir, scale, file, width, height, show, step);
            void ShotOpen(string file, double height, params string[] folds)
            {
                Shot(file, PageWidth, height, () =>
                {
                    foreach (string fold in folds) ((System.Windows.Controls.Expander)page.FindName(fold)).IsExpanded = true;
                });
                foreach (string fold in folds) ((System.Windows.Controls.Expander)page.FindName(fold)).IsExpanded = false;
            }

            Shot("devices-1280.png", PageWidth, 1088, PickDualSense);
            ShotOpen("devices-1280-details.png", 1500, "DetailsFold");
            ShotOpen("devices-1280-more.png", 1800, "TestMore");
            ShotOpen("devices-1280-folds.png", 1300, "NotConnectedFold", "OtherInputsFold");
            Shot("devices-900.png", Narrow, 2300, null);
            // A wide window, where the table has all the room it wants.
            Shot("devices-1440.png", 1176, 1088, null);
            // A controller that charges and that nothing uses yet.
            Shot("devices-1280-elite.png", PageWidth, 1088, () =>
            {
                Pick(elite);
                vm.RebuildRawStateCollections(new[] { 0, 1, 2, 3, 4, 5 }, Enumerable.Range(0, 13).ToArray(), 1, gamepadLayout: true);
                vm.HasRawData = true;
            });
            // A device that is not connected: its picture in gray.
            Shot("devices-1280-away.png", PageWidth, 1088, () =>
            {
                vm.ClearRawState();
                Pick(vm.Devices.Single(d => d.DeviceName == "DualShock 3"));
                ((System.Windows.Controls.Expander)page.FindName("NotConnectedFold")).IsExpanded = true;
            });
            ((System.Windows.Controls.Expander)page.FindName("NotConnectedFold")).IsExpanded = false;
            Shot("devices-1280-keyboard.png", PageWidth, 1088, () =>
            {
                Pick(keyboard);
                vm.RebuildRawStateCollections(Array.Empty<int>(), Array.Empty<int>(), 0, isKeyboard: true);
                vm.HasRawData = true;
                vm.HasGyroData = vm.HasAccelData = vm.HasTouchpadData = false;
            });
            Shot("devices-1280-none.png", PageWidth, 1088, () =>
            {
                vm.ClearRawState();
                Pick(null);
            });
        }

        /// <summary>The app, for its resources alone. WPF queues an
        /// application's startup as the application is made, and the app's
        /// is the whole of PadForge starting: its single-instance check,
        /// its drivers, its window. Run in a test host beside a running
        /// PadForge it put up the already-running box and waited on it
        /// (2026-11-12). The queued startup is taken off the dispatcher
        /// here, before anything runs it, and the dump stops when that
        /// cannot be shown. ShellRenderDump makes its app the same way.</summary>
        internal static App MakeAppThatNeverStarts(Dispatcher dispatcher)
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
        internal static List<string> LostLookups(DependencyObject root)
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
                    // A line of a picker's item that shows only in the open
                    // list, or only in the closed box, looks for the list
                    // item around it. In the closed box there is none, and
                    // the binding says what the line does then. That is the
                    // app's own way of telling the two apart, and no lookup
                    // gone wrong.
                    if (ancestor && binding.RelativeSource.AncestorType == typeof(ComboBoxItem)
                        && binding.FallbackValue != DependencyProperty.UnsetValue)
                        continue;
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
        internal static void Settle(Dispatcher dispatcher)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Normal, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>The theme a run's pictures are drawn in: the dark one,
        /// or the light one with PADFORGE_PAGE_RENDER_THEME=light.</summary>
        internal static ApplicationTheme PictureTheme()
            => string.Equals(Environment.GetEnvironmentVariable("PADFORGE_PAGE_RENDER_THEME"), "light", StringComparison.OrdinalIgnoreCase)
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark;

        /// <summary>Applies a theme in a test host. wpf-ui tells every
        /// FluentWindow of the process about the change, and a window a
        /// finished test made on its own thread and never closed is still on
        /// that list. The windows of threads that have ended are taken off it
        /// first, since the change reads each window from the thread that
        /// applies it (TestHostResourceAssemblyTests).</summary>
        internal static void ApplyTheme(ApplicationTheme theme)
        {
            var changed = typeof(ApplicationThemeManager).GetEvent("Changed", BindingFlags.Public | BindingFlags.Static);
            var held = typeof(ApplicationThemeManager).GetField("Changed", BindingFlags.NonPublic | BindingFlags.Static);
            if (changed == null || held == null)
                throw new InvalidOperationException("wpf-ui's theme event moved. A theme cannot be applied in a test host.");
            if (held.GetValue(null) is Delegate all)
                foreach (var one in all.GetInvocationList())
                    if (one.Target is DispatcherObject owned && (!owned.Dispatcher.Thread.IsAlive || owned.Dispatcher.HasShutdownStarted))
                        changed.RemoveEventHandler(null, one);
            ApplicationThemeManager.Apply(theme);
        }

        /// <summary>The assembly WPF reads an application path from. WPF
        /// keeps it in two statics and takes the entry assembly when they
        /// are empty. It keeps the resources it opened for that assembly in
        /// a third, made at the first component any test loads, so that one
        /// is emptied here and WPF opens the named assembly's at its next
        /// read (TestHostResourceAssemblyTests).</summary>
        internal static void SetResourceAssembly(Assembly assembly)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var ofApp = typeof(Application).GetField("_resourceAssembly", flags);
            var ofUris = typeof(System.Windows.Navigation.BaseUriHelper).GetField("_resourceAssembly", flags);
            var container = typeof(Application).Assembly.GetType("MS.Internal.AppModel.ResourceContainer");
            var ofOpened = container?.GetField("s_applicationResourceManagerWrapper", flags)
                           ?? container?.GetField("_applicationResourceManagerWrapper", flags);
            if (ofApp == null || ofUris == null || ofOpened == null)
                throw new InvalidOperationException("WPF's resource assembly statics moved. The page cannot be drawn in a test host.");
            ofApp.SetValue(null, assembly);
            ofUris.SetValue(null, assembly);
            ofOpened.SetValue(null, null);
        }
    }

    /// <summary>The app's dictionaries load in a test host whatever test ran
    /// before. WPF opens the resources of the application's assembly once and
    /// keeps them, and in a test host that assembly is the host's until a test
    /// names the app's. A test that loaded any component first left WPF
    /// holding the host's resources, which have none of the app's
    /// dictionaries, and the app's own then failed to load with "Cannot
    /// locate resource 'resources/controllericons.xaml'" (2026-11-12, in a
    /// full run and never alone).</summary>
    [Collection("WpfApplicationSingleton")]
    public class TestHostResourceAssemblyTests
    {
        [Fact]
        public void TheAppsDictionariesLoad_AfterAnotherTestOpenedAResource()
        {
            // WPF also keeps each part it has looked up, found or not, so
            // each read here names a path of the app that nothing else reads
            // without its assembly's name.
            try
            {
                // What an earlier test leaves: a read while the application's
                // assembly is the host's, which has nothing of the app's. WPF
                // now holds the host's resources.
                PageRenderDump.SetResourceAssembly(null);
                Assert.Throws<IOException>(() => Application.GetResourceStream(new Uri("/app.xaml", UriKind.Relative)));

                PageRenderDump.SetResourceAssembly(typeof(App).Assembly);
                using (var own = Application.GetResourceStream(new Uri("/mainwindow.xaml", UriKind.Relative)).Stream)
                    Assert.True(own.Length > 0);
            }
            finally { PageRenderDump.SetResourceAssembly(null); }
            // The host's again, as the run started.
            Assert.Throws<IOException>(() => Application.GetResourceStream(new Uri("/views/homepage.xaml", UriKind.Relative)));
        }

        /// <summary>A theme is applied in a test host whatever window an
        /// earlier test left. wpf-ui tells every FluentWindow of the process
        /// about a theme change, and a window a finished test made and never
        /// closed is still on its list, owned by a thread that has ended. The
        /// change then failed on that window with "The calling thread cannot
        /// access this object" (2026-11-12, in a full run and never
        /// alone).</summary>
        [Fact]
        public void AThemeIsApplied_AfterAnotherTestLeftAWindow()
        {
            var made = new Thread(() =>
            {
                // Never shown and never closed, as a test that reads a
                // dialog's parts leaves it.
                _ = new Wpf.Ui.Controls.FluentWindow();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            });
            made.SetApartmentState(ApartmentState.STA);
            made.Start();
            Assert.True(made.Join(TimeSpan.FromSeconds(30)), "the window's thread did not end");

            PageRenderDumpPages.WithApp((app, dispatcher) => { }, TimeSpan.FromMinutes(1), "the theme run timed out");
        }
    }
}
