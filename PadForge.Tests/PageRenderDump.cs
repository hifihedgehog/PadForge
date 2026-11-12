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
                    page.BindSteamVrState(new SettingsViewModel { IsSteamVrInstalled = true });
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
                                "GyroResponseCard", "GyroSpaceCard", "GyroPassthroughCard" },
                            new[] { "GyroSimulationCard", "CompassYawCard" },
                            new[] { "GyroCalibrationMore", "GyroSensitivityMore", "GyroGripCard", "GyroSpaceCard" }),
                        ("lights", 7, new[] { "TabLighting", "LightbarModeCard", "IndicatorLedsCard", "PeripheralLightingCard", "TabAudio", "AudioChainList" },
                            new string[0],
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
