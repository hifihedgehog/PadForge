using System;
using System.IO;
using System.Reflection;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.SteamWorkshop.Translation;
using PadForge.SteamWorkshop.Vdf;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// #413 stay-open menus driven through the real runtime: a device's
    /// state goes into <see cref="InputManager.UpdateMenuContexts"/> and the
    /// overlay snapshot and fired-item reads come out. The evaluator tests
    /// in MenuLayerGateAndIconSizeTests prove the firing rules with
    /// precomputed booleans; these prove the dispatch that computes them:
    /// the layer gate, the driver arbitration between two pads on one slot,
    /// the finger-up click sampling, and the two resets that keep a
    /// configuration operation from committing an in-flight interaction.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class MenuLayerGateRuntimeTests : IDisposable
    {
        private static readonly Guid DevA = new("aaaaaaaa-1111-1111-1111-111111111111");
        private static readonly Guid DevB = new("bbbbbbbb-2222-2222-2222-222222222222");

        private readonly DeviceCollection _savedDevices;
        private readonly SettingsCollection _savedSettings;
        private readonly MappingSet[] _savedSets;
        private readonly Func<int, string, int, int, bool> _savedMenuProvider;
        private readonly Func<string, string, int, bool> _savedKeyProvider;

        /// <summary>The menu runtime's clock (MenuClock), held for every
        /// test here. A commit lives 100 ms on it
        /// (MenuEvaluator.CommitPulseMs) and a menu context is believed for
        /// 250 ms, and the tests read both over several statements and
        /// frames. On the system clock a stall between two of them failed a
        /// test with nothing wrong in the product. Held, a commit is live
        /// until the test moves the clock past its pulse. It starts ten
        /// seconds after boot, long before any real tick count, so a read
        /// left on the system clock would see every held stamp as lapsed and
        /// a stamp left on it would never lapse here.</summary>
        private long _now = 10_000;

        public MenuLayerGateRuntimeTests()
        {
            _savedDevices = SettingsManager.UserDevices;
            _savedSettings = SettingsManager.UserSettings;
            _savedSets = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            _savedMenuProvider = PadForge.Engine.Common.Mapping.SourceCoercion.MenuItemFiredProvider;
            _savedKeyProvider = PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider;
            MenuClock.Source = () => _now;
        }

        public void Dispose()
        {
            // The system clock back first. ClearAllShiftRuntime below drops
            // the layer-exit commits made on the held one, which would read
            // as lapsed long ago to the next class anyway.
            MenuClock.Source = null;
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
            for (int i = 0; i < _savedSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = _savedSets[i];
            PadForge.Engine.Common.Mapping.SourceCoercion.MenuItemFiredProvider = _savedMenuProvider;
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = _savedKeyProvider;
            InputManager.ClearAllShiftRuntime();
            InputManager.ClearSourceKindRuntime();
        }

        /// <summary>The production wiring InputService installs: rows,
        /// activators and macro descriptor triggers read menu cells through
        /// this one hook.</summary>
        private static InputManager WiredManager()
        {
            var im = new InputManager();
            PadForge.Engine.Common.Mapping.SourceCoercion.MenuItemFiredProvider =
                (slot, guid, menuId, item) => im.IsMenuItemFired(slot, guid, menuId, item);
            return im;
        }

        // ── Fixture ──────────────────────────────────────────────────────

        private static MenuDefinitionEntry ArrangeSlot(MenuFireType fire, bool hasCenter = true,
            string host = "Gamepad RightStick", string click = "")
        {
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var set = new MappingSet();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L1", LayerName = "Radial", Descriptor = "Gamepad LeftShoulder", Mode = "Hold",
            });
            var def = new MenuDefinitionEntry
            {
                MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = hasCenter,
                HostDescriptor = host, ClickDescriptor = click,
                LayerMask = "L1", LayerHoldsOpen = true, FireType = fire, EngageDeadzonePercent = 25,
            };
            for (int i = 0; i <= 4; i++)
                def.Items.Add(new MenuItemDefinition { Index = i, VirtualKey = 0x41 + i });
            set.Menus.Add(def);
            SettingsManager.SlotMappingSets[0] = set;
            return def;
        }

        private static (UserDevice ud, CustomInputState st) AddPad(Guid guid)
        {
            var st = new CustomInputState();
            Center(st);
            var ud = new UserDevice
            {
                InstanceGuid = guid,
                CapType = InputDeviceType.Gamepad,
                CapButtonCount = 16,
                IsOnline = true,
                InputState = st,
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = guid, MapTo = 0 });
            return (ud, st);
        }

        // SourceKindRuntime.ReadNormAxis: center 32768, span 32767. Right
        // stick is Axis 3 / Axis 4; full +X is ring cell 2 on a 4-cell radial.
        private static void Center(CustomInputState st) { st.Axis[3] = 32768; st.Axis[4] = 32768; }
        private static void Deflect(CustomInputState st) { st.Axis[3] = 65535; st.Axis[4] = 32768; }

        private static void Layer(bool engaged) => InputManager.ApplyMacroLayerSwitch(0, engaged ? "L1" : "");

        private static bool Fired(InputManager im, int index) => im.IsMenuItemFired(0, null, 1, index);

        // ── The gate ─────────────────────────────────────────────────────

        [Fact]
        public void StayOpen_TheLayerAloneOpensTheMenu_AndItsEndingClosesIt()
        {
            ArrangeSlot(MenuFireType.Always);
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);

            im.UpdateMenuContexts(ud, st);                 // layer not engaged
            Assert.Null(im.ActiveMenuOverlay);

            Layer(true);
            im.UpdateMenuContexts(ud, st);                 // stick at rest
            var ov = im.ActiveMenuOverlay;
            Assert.NotNull(ov);
            Assert.Equal(0, ov.Slot);
            Assert.Equal(DevA, ov.Device);
            Assert.Equal(0, ov.HoveredIndex);              // the resting center
            Assert.True(Fired(im, 0), "Always asserts the resting center");

            Layer(false);
            im.UpdateMenuContexts(ud, st);
            Assert.Null(im.ActiveMenuOverlay);
            Assert.False(Fired(im, 0));
        }

        [Fact]
        public void StayOpen_AnUnconfiguredCustomOpener_OpensButHoversNothing()
        {
            // Custom host with no axes assigned: the layer opens the menu, but
            // reading zero axes must not manufacture a resting center.
            ArrangeSlot(MenuFireType.Always, host: "Custom");
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            Layer(true);
            im.UpdateMenuContexts(ud, st);
            var ov = im.ActiveMenuOverlay;
            Assert.NotNull(ov);
            Assert.Equal(-1, ov.HoveredIndex);
            Assert.False(Fired(im, 0));
        }

        // ── A hotbar through the runtime ──────────────

        /// <summary>A Hotbar style menu on a Custom opener, its four controls
        /// recorded buttons: the runtime reads each control as a press and
        /// steps the hotbar (MenuHotbar.Step), the bar's overlay shows only
        /// while the bar is up, and the fired cell is what the cell bindings
        /// read.</summary>
        [Fact]
        public void Hotbar_ShowsStepsAndFires_ThroughItsFourControls()
        {
            var def = ArrangeSlot(MenuFireType.Click, host: "Custom");
            def.Kind = MenuKind.Hotbar;
            def.HasCenter = false;
            def.LayerMask = "";
            def.LayerHoldsOpen = false;
            def.CellCount = 3;
            def.HotbarShowDescriptor = "Button 1";
            def.HotbarPreviousDescriptor = "Button 2";
            def.HotbarNextDescriptor = "Button 3";
            def.HotbarFireDescriptor = "Button 4";
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            void Tick(int? held = null)
            {
                for (int i = 1; i <= 4; i++) st.Buttons[i] = held == i;
                im.UpdateMenuContexts(ud, st);
                _now += 5;
            }

            Tick();
            Assert.Null(im.ActiveMenuOverlay);
            Tick(1);
            Tick();
            Assert.NotNull(im.ActiveMenuOverlay);
            Assert.Equal(1, im.ActiveMenuOverlay.HoveredIndex);
            Tick(3);
            Tick();
            Assert.Equal(2, im.ActiveMenuOverlay.HoveredIndex);
            Assert.False(Fired(im, 2));
            Tick(4);
            Assert.True(Fired(im, 2));
            // Hide After Firing took the bar down.
            Tick();
            Assert.Null(im.ActiveMenuOverlay);
            _now += 40;
            Tick();
            Assert.False(Fired(im, 2));
        }

        // ── Two pads on one slot ──────────────

        [Fact]
        public void StayOpen_TwoPadsOnOneSlot_TheMovingPadDrives_AndTheIdleOneStopsAssertingCenter()
        {
            ArrangeSlot(MenuFireType.Always);
            var im = new InputManager();
            var (udA, stA) = AddPad(DevA);
            var (udB, stB) = AddPad(DevB);
            Layer(true);

            im.UpdateMenuContexts(udA, stA);               // A polled first, idle: takes the record
            im.UpdateMenuContexts(udB, stB);
            Assert.Equal(DevA, im.ActiveMenuOverlay.Device);
            Assert.True(Fired(im, 0));

            Deflect(stB);                                  // B steers to cell 2
            im.UpdateMenuContexts(udB, stB);               // B takes the record; A still owns the snapshot
            im.UpdateMenuContexts(udA, stA);               // A is no longer the driver: releases, hovers nothing
            im.UpdateMenuContexts(udB, stB);               // B publishes
            var ov = im.ActiveMenuOverlay;
            Assert.NotNull(ov);
            Assert.Equal(DevB, ov.Device);
            Assert.Equal(2, ov.HoveredIndex);
            Assert.True(Fired(im, 2));
            Assert.False(Fired(im, 0), "the idle pad's resting center must not stay asserted beside cell 2");

            Center(stB);                                   // B rests: keeps the record, hovers the center
            im.UpdateMenuContexts(udB, stB);
            im.UpdateMenuContexts(udA, stA);
            Assert.Equal(DevB, im.ActiveMenuOverlay.Device);
            Assert.Equal(0, im.ActiveMenuOverlay.HoveredIndex);
        }

        // ── Resets that keep configuration from committing ───────────────

        [Fact]
        public void StayOpen_TheLayerEndingMidDeflection_Commits_PositiveControl()
        {
            // The documented Steam mode-shift-end commit. The two reset tests
            // below are only meaningful because this fires without them.
            ArrangeSlot(MenuFireType.TouchRelease);
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            Layer(true);
            im.UpdateMenuContexts(ud, st);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);
            Assert.Equal(2, im.ActiveMenuOverlay.HoveredIndex);
            Assert.False(Fired(im, 2));

            Layer(false);                                  // the layer ends while still deflected
            im.UpdateMenuContexts(ud, st);
            Assert.True(Fired(im, 2));
        }

        [Fact]
        public void StayOpen_ClearMenuRuntimeForSlot_ResetsInsteadOfCommitting()
        {
            // The layer editor's delete path: ClearShiftRuntime beside
            // ClearMenuRuntimeForSlot. The mask stays on the menu (marked
            // missing in the picker), so nothing in the authored signature
            // changes and only the explicit reset can stop the commit.
            ArrangeSlot(MenuFireType.TouchRelease);
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            Layer(true);
            im.UpdateMenuContexts(ud, st);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);

            im.ClearMenuRuntimeForSlot(0);
            Assert.Null(im.ActiveMenuOverlay);
            Layer(false);
            im.UpdateMenuContexts(ud, st);
            for (int i = 0; i <= 4; i++)
                Assert.False(Fired(im, i), $"cell {i} committed from a configuration operation");
        }

        [Fact]
        public void StayOpen_ClearMenuRuntimeForSlot_LeavesOtherSlotsAlone()
        {
            ArrangeSlot(MenuFireType.Always);
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            Layer(true);
            im.UpdateMenuContexts(ud, st);
            Assert.NotNull(im.ActiveMenuOverlay);
            im.ClearMenuRuntimeForSlot(3);
            Assert.NotNull(im.ActiveMenuOverlay);
            Assert.True(Fired(im, 0));
        }

        [Fact]
        public void StayOpen_AnAuthoredFlagEditMidDeflection_ResetsInsteadOfCommitting()
        {
            var def = ArrangeSlot(MenuFireType.TouchRelease);
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            Layer(true);
            im.UpdateMenuContexts(ud, st);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);

            def.LayerHoldsOpen = false;                    // the checkbox, mid-interaction
            im.UpdateMenuContexts(ud, st);
            Assert.False(Fired(im, 2), "an authored edit is not a release");
        }

        // ── Finger-up click sampling ──────────

        [Fact]
        public void StayOpen_Touchpad_AClickThatOutlivesTheTouch_CommitsTheCellItStartedOn()
        {
            ArrangeSlot(MenuFireType.ClickRelease, hasCenter: false,
                host: "Touchpad 0", click: "Gamepad ButtonA");
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            st.Touchpads = new[] { new TouchpadInputState(2) };
            var pad = st.Touchpads[0];
            Layer(true);

            im.UpdateMenuContexts(ud, st);                 // open, untouched
            Assert.Equal(-1, im.ActiveMenuOverlay.HoveredIndex);

            pad.FingerDown[0] = true; pad.FingerX[0] = 1f; pad.FingerY[0] = 0.5f;
            st.Buttons[0] = true;                          // click held on cell 2
            im.UpdateMenuContexts(ud, st);
            Assert.Equal(2, im.ActiveMenuOverlay.HoveredIndex);

            pad.FingerDown[0] = false;                     // lift, click still held
            im.UpdateMenuContexts(ud, st);
            Assert.False(Fired(im, 2));
            Assert.Equal(2, im.ActiveMenuOverlay.HoveredIndex);   // the pending selection stays visible

            st.Buttons[0] = false;                         // release one poll later
            im.UpdateMenuContexts(ud, st);
            Assert.True(Fired(im, 2));
        }

        [Fact]
        public void StayOpen_Touchpad_ALiftWithoutAClick_ManufacturesNoRelease()
        {
            ArrangeSlot(MenuFireType.ClickRelease, hasCenter: false,
                host: "Touchpad 0", click: "Gamepad ButtonA");
            var im = new InputManager();
            var (ud, st) = AddPad(DevA);
            st.Touchpads = new[] { new TouchpadInputState(2) };
            var pad = st.Touchpads[0];
            Layer(true);

            pad.FingerDown[0] = true; pad.FingerX[0] = 1f; pad.FingerY[0] = 0.5f;
            im.UpdateMenuContexts(ud, st);
            pad.FingerDown[0] = false;
            im.UpdateMenuContexts(ud, st);
            st.Buttons[0] = true;                          // click with the finger up
            im.UpdateMenuContexts(ud, st);
            st.Buttons[0] = false;
            im.UpdateMenuContexts(ud, st);
            for (int i = 0; i <= 4; i++)
                Assert.False(Fired(im, i));
        }

        // ── DC20: a commit made by leaving the layer runs on that layer ──
        //
        // menus.md: leaving the layer commits a selection still being steered,
        // an imported mode-shift menu commits its hovered cell when the layer
        // releases "matching Steam's mode-shift behavior", and a macro cell
        // keeps the macro's own Layer scope. The commit landed in a frame where
        // the layer had already ended, so the macro gate and the row lookup
        // read the NEW layer and the departed layer's macros and rows never
        // saw it. These run the real menu tick, the real macro pass and the
        // real gamepad row loop.

        private static readonly MethodInfo ApplyGamepad = typeof(InputManager).GetMethod(
            "ApplyMappingSetToGamepad", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo RunMacroPass = typeof(InputManager).GetMethod(
            "EvaluateMacros", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>A Steam-style mode-shift menu: surface engaged, gated to
        /// L1, Touch Release. Its cells carry no key bindings, because the
        /// real macro pass would send them as keystrokes.</summary>
        private static MappingSet ArrangeModeShiftMenu()
        {
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();

            var set = new MappingSet();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L1", LayerName = "Shift", Descriptor = "Gamepad LeftShoulder", Mode = "Hold",
            });
            var def = new MenuDefinitionEntry
            {
                MenuId = 1, Kind = MenuKind.Radial, CellCount = 4, HasCenter = false,
                HostDescriptor = "Gamepad RightStick",
                LayerMask = "L1", LayerHoldsOpen = false,
                FireType = MenuFireType.TouchRelease, EngageDeadzonePercent = 25,
            };
            for (int i = 1; i <= 4; i++)
                def.Items.Add(new MenuItemDefinition { Index = i });
            set.Menus.Add(def);
            SettingsManager.SlotMappingSets[0] = set;
            return set;
        }

        /// <summary>Hovers cell 2 with L1 engaged, then ends L1 while the
        /// stick is still deflected: Steam's mode-shift-end commit.</summary>
        private static void CommitByLeavingTheLayer(InputManager im, UserDevice ud, CustomInputState st)
        {
            Layer(true);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);
            Assert.False(Fired(im, 2));
            Layer(false);
            im.UpdateMenuContexts(ud, st);
            Assert.True(Fired(im, 2), "the layer ending did not commit the hovered cell");
        }

        /// <summary>The same cell committed while L1 is still engaged, by
        /// re-centering the stick.</summary>
        private static void CommitInsideTheLayer(InputManager im, UserDevice ud, CustomInputState st)
        {
            Layer(true);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);
            Center(st);
            im.UpdateMenuContexts(ud, st);
            Assert.True(Fired(im, 2), "re-centering inside the layer did not commit the hovered cell");
        }

        private static MacroItem PressMacro(string name, string mask, ushort button) => new()
        {
            Name = name, IsEnabled = true, PadIndex = 0, LayerMask = mask,
            TriggerMode = MacroTriggerMode.OnPress, RepeatMode = MacroRepeatMode.Once,
            ConsumeTriggerButtons = false,
            Actions = { new MacroAction { Type = MacroActionType.ButtonPress, ButtonFlags = button, DurationMs = 0 } },
        };

        /// <summary>One macro pass: the menu walk stamps cell macros, then
        /// the slot evaluator runs. Returns the slot's buttons.</summary>
        private static ushort MacroPass(InputManager im, ushort heldButtons = 0)
        {
            im.CombinedOutputStates[0] = new Gamepad { Buttons = heldButtons };
            RunMacroPass.Invoke(im, null);
            return im.CombinedOutputStates[0].Buttons;
        }

        /// <summary>One frame of the gamepad row loop. Returns the buttons.</summary>
        private static ushort RowPass(MappingSet set, CustomInputState st) => GamepadPass(set, st, DevA).Buttons;

        /// <summary>One frame of the gamepad row loop on one device's pass.</summary>
        private static Gamepad GamepadPass(MappingSet set, CustomInputState st, Guid device)
        {
            InputManager.GetSlotSourceKindRuntime(0).FrameSeq++;
            var args = new object[] { st, set, device.ToString(), 50, 0, new Gamepad() };
            ApplyGamepad.Invoke(null, args);
            return (Gamepad)args[5];
        }

        private static MappingRow CellRow(string target, string layer, params string[] descriptors)
        {
            var row = new MappingRow { Target = target, LayerMask = layer };
            foreach (var d in descriptors)
                row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = d });
            return row;
        }

        /// <summary>A macro cell naming a macro scoped to the departed layer
        /// runs on the layer-exit commit. It was gated by the new layer.</summary>
        [Fact]
        public void LayerExitCommit_RunsAMacroCellScopedToTheDepartedLayer()
        {
            var set = ArrangeModeShiftMenu();
            set.Menus[0].Items[1].MacroName = "Cell";
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            im.MacroSnapshots[0] = new[] { PressMacro("Cell", "L1", Gamepad.B) };

            CommitByLeavingTheLayer(im, ud, st);

            Assert.True((MacroPass(im) & Gamepad.B) != 0, "the departed layer's macro cell did not run");
        }

        /// <summary>An imported cell's macro rides the cell as its own
        /// trigger, scoped to the menu's layer. Same rule.</summary>
        [Fact]
        public void LayerExitCommit_RunsAMacroTriggeredByTheCell_ScopedToTheDepartedLayer()
        {
            ArrangeModeShiftMenu();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            var macro = PressMacro("Imported", "L1", Gamepad.B);
            macro.TriggerInputs = $"in:{Guid.Empty}:sd:Menu 1 Item 2";
            im.MacroSnapshots[0] = new[] { macro };

            CommitByLeavingTheLayer(im, ud, st);

            Assert.True((MacroPass(im) & Gamepad.B) != 0, "the departed layer's cell-triggered macro did not run");
        }

        /// <summary>Negative control: the departed layer's other macros stay
        /// inactive after the exit. A macro scoped to L1 on its own held
        /// button must not fire just because a commit came from L1.</summary>
        [Fact]
        public void LayerExitCommit_LeavesTheDepartedLayersOtherMacrosInactive()
        {
            var set = ArrangeModeShiftMenu();
            set.Menus[0].Items[1].MacroName = "Cell";
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            var own = PressMacro("Own", "L1", Gamepad.Y);
            own.TriggerButtons = Gamepad.X;
            im.MacroSnapshots[0] = new[] { PressMacro("Cell", "L1", Gamepad.B), own };

            CommitByLeavingTheLayer(im, ud, st);

            ushort buttons = MacroPass(im, heldButtons: Gamepad.X);
            Assert.True((buttons & Gamepad.B) != 0, "positive control: the commit's own macro ran");
            Assert.True((buttons & Gamepad.Y) == 0, "an L1 macro on its own trigger fired after L1 ended");
        }

        /// <summary>Positive control: the same macro cell runs on a commit
        /// made while the layer is still engaged.</summary>
        [Fact]
        public void InLayerCommit_RunsTheSameMacroCell_PositiveControl()
        {
            var set = ArrangeModeShiftMenu();
            set.Menus[0].Items[1].MacroName = "Cell";
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            im.MacroSnapshots[0] = new[] { PressMacro("Cell", "L1", Gamepad.B) };

            CommitInsideTheLayer(im, ud, st);

            Assert.True((MacroPass(im) & Gamepad.B) != 0);
        }

        /// <summary>A row on the departed layer that reads the committed cell
        /// sees the commit. A Base row reading the same cell is the positive
        /// control: it saw the commit before the fix too.</summary>
        [Fact]
        public void LayerExitCommit_ADepartedLayerRowReadingTheCell_SeesIt()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(CellRow("ButtonB", "L1", "Menu 1 Item 2"));
            set.Rows.Add(CellRow("ButtonA", "Base", "Menu 1 Item 2"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            ushort buttons = RowPass(set, st);
            Assert.True((buttons & Gamepad.A) != 0, "positive control: the Base row saw the commit");
            Assert.True((buttons & Gamepad.B) != 0, "the departed layer's row never saw its commit");
        }

        /// <summary>Negative control: the row's other source stays inactive.
        /// With AND, a held Button 5 read live would complete the row.</summary>
        [Fact]
        public void LayerExitCommit_AnotherSourceOnThatRow_StaysInactive()
        {
            var set = ArrangeModeShiftMenu();
            var row = CellRow("ButtonB", "L1", "Menu 1 Item 2", "Button 5");
            row.CombineMode = "AND";
            set.Rows.Add(row);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            st.Buttons[5] = true;

            Assert.True((RowPass(set, st) & Gamepad.B) == 0, "the departed row's other source was read live");
        }

        /// <summary>Negative control: a departed-layer row bound to a
        /// different input stays inactive after the exit.</summary>
        [Fact]
        public void LayerExitCommit_ADepartedLayerRowOnAnotherInput_StaysInactive()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(CellRow("ButtonB", "L1", "Menu 1 Item 2"));
            set.Rows.Add(CellRow("ButtonX", "L1", "Button 5"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            st.Buttons[5] = true;

            ushort buttons = RowPass(set, st);
            Assert.True((buttons & Gamepad.B) != 0, "positive control: the commit's row ran");
            Assert.True((buttons & Gamepad.X) == 0, "a departed-layer row on another input fired after the exit");
        }

        /// <summary>The per-target evaluators (Extended, MIDI, keyboard and
        /// mouse, VR) take the same rule through FindActiveRowForTarget's
        /// callers.</summary>
        [Fact]
        public void LayerExitCommit_APerTargetRowReadingTheCell_SeesIt()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(CellRow("KbmKey41", "L1", "Menu 1 Item 2"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            Assert.True(InputManager.TryEvaluateMappingSetButton(st, set, DevA.ToString(), 0, "KbmKey41", 50,
                out bool pressed));
            Assert.True(pressed, "the departed layer's key row never saw its commit");
        }

        /// <summary>Under an overlay layer, a target the layer does not map
        /// reads its Base row, so a commit made by leaving that layer presses
        /// the Base row, even when the exit lands in a layer that replaces
        /// Base. Under a replacing L1, the negative control, L1 never read
        /// the Base row and the commit leaves it alone.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LayerExitCommit_ABaseRowTheDepartedLayerReads_SeesIt_UnderAReplacingLayer(bool l1Overlays)
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators[0].InheritUnmapped = l1Overlays;
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L2", LayerName = "Other", Descriptor = "Gamepad RightShoulder", Mode = "Hold",
            });
            set.Rows.Add(CellRow("ButtonA", "Base", "Menu 1 Item 2"));
            set.Rows.Add(CellRow("ButtonB", "L1", "Menu 1 Item 2"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            Layer(true);
            Deflect(st);
            im.UpdateMenuContexts(ud, st);
            InputManager.ApplyMacroLayerSwitch(0, "L2");   // L1 ends straight into L2
            im.UpdateMenuContexts(ud, st);
            Assert.True(Fired(im, 2), "leaving L1 for L2 did not commit the hovered cell");

            ushort buttons = RowPass(set, st);
            Assert.True((buttons & Gamepad.B) != 0, "positive control: the departed layer's own row saw the commit");
            Assert.Equal(l1Overlays, (buttons & Gamepad.A) != 0);
        }

        /// <summary>An Invert on Hold modifier on the departed row is one of
        /// its other inputs, so it stays at rest after the exit: the commit
        /// pulls the trigger fully though the modifier's button is held.
        /// Inside the layer, the positive control, the held modifier inverts
        /// the committed pull to nothing.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LayerExitCommit_TheRowsInvertModifier_StaysAtRest(bool leaveTheLayer)
        {
            var set = ArrangeModeShiftMenu();
            var row = CellRow("RightTrigger", "L1", "Menu 1 Item 2");
            row.Sources.Add(new MappingSource { Kind = "InvertOnHold", ParamModifier = "Button 5" });
            set.Rows.Add(row);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            st.Buttons[5] = true;

            if (leaveTheLayer) CommitByLeavingTheLayer(im, ud, st);
            else CommitInsideTheLayer(im, ud, st);

            ushort pull = GamepadPass(set, st, DevA).RightTrigger;
            Assert.Equal(leaveTheLayer ? ushort.MaxValue : (ushort)0, pull);
        }

        /// <summary>A row with more than one input reads each input on its
        /// own device, so a cell pinned to the pad that drove the menu reads
        /// committed from every device's pass. This row is true while the
        /// cell is NOT committed: read only on its own pad's pass, the other
        /// pad's pass saw the cell at rest and pressed B.</summary>
        [Fact]
        public void LayerExitCommit_AMultiInputRow_ReadsThePinnedCellFromEveryPass()
        {
            var set = ArrangeModeShiftMenu();
            var row = new MappingRow
            {
                Target = "ButtonB", LayerMask = "L1", CombineMode = "Custom", CombineExpression = "!a || b",
            };
            row.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = DevA.ToString(), Descriptor = "Menu 1 Item 2" });
            row.Sources.Add(new MappingSource { Kind = "Direct", DeviceGuid = DevA.ToString(), Descriptor = "Button 5" });
            set.Rows.Add(row);
            var pinned = CellRow("ButtonA", "L1", "Menu 1 Item 2");
            pinned.Sources[0].DeviceGuid = DevA.ToString();
            set.Rows.Add(pinned);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            var (_, stB) = AddPad(DevB);

            CommitByLeavingTheLayer(im, ud, st);

            ushort onA = GamepadPass(set, st, DevA).Buttons;
            Assert.True((onA & Gamepad.A) != 0, "positive control: the pinned single-input row saw the commit");
            Assert.True((onA & Gamepad.B) == 0, "the pad's own pass pressed B");
            Assert.True((GamepadPass(set, stB, DevB).Buttons & Gamepad.B) == 0,
                "the other pad's pass read the pinned cell at rest and pressed B");
        }

        // ── DC20 beyond the rows' own inputs ──

        /// <summary>An activator hosted on the departed layer (Only While in
        /// Layer) whose input is the committed cell engages its layer. Its
        /// host gate read the layer engaged when the press rose, the new one,
        /// and stayed closed for the whole commit.</summary>
        [Fact]
        public void LayerExitCommit_EngagesAnActivatorHostedOnTheDepartedLayer()
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L2", LayerName = "Next", Descriptor = "Menu 1 Item 2", Mode = "Toggle", HostLayerMask = "L1",
            });
            set.Rows.Add(CellRow("ButtonX", "Base", "Button 9"));   // a set with rows ticks its activators
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            GamepadPass(set, st, DevA);                    // ticks the activators

            Assert.Equal("L2", InputManager.GetEngagedLayerMask(0, set));
        }

        /// <summary>The same for a Cycle's Previous button, which carries a
        /// host gate of its own.</summary>
        [Fact]
        public void LayerExitCommit_StepsACyclesPreviousHostedOnTheDepartedLayer()
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "C1", LayerName = "Cycle", Mode = "Cycle", Descriptor = "Button 6",
                CyclePrevDescriptor = "Menu 1 Item 2", CycleLayers = "L2", CycleIncludeBase = false, CycleWrap = true,
                HostLayerMask = "L1",
            });
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "L2", LayerName = "Two", Mode = "Passive" });
            set.Rows.Add(CellRow("ButtonX", "Base", "Button 9"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            GamepadPass(set, st, DevA);

            Assert.Equal("L2", InputManager.GetEngagedLayerMask(0, set));
        }

        /// <summary>Negative controls: an activator hosted on the departed
        /// layer stays off for a press of its own input after the layer ended,
        /// and one hosted on another layer stays off for the commit.</summary>
        [Fact]
        public void LayerExitCommit_LeavesOtherHostGatedActivatorsClosed()
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L2", LayerName = "Own", Descriptor = "Button 5", Mode = "Toggle", HostLayerMask = "L1",
            });
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L3", LayerName = "Elsewhere", Descriptor = "Menu 1 Item 2", Mode = "Toggle", HostLayerMask = "L4",
            });
            set.ShiftActivators.Add(new ShiftActivator { LayerMask = "L4", LayerName = "Four", Mode = "Passive" });
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L5", LayerName = "Unhosted", Descriptor = "Button 6", Mode = "Toggle",
            });
            set.Rows.Add(CellRow("ButtonX", "Base", "Button 9"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            st.Buttons[5] = true;
            GamepadPass(set, st, DevA);
            Assert.Equal("Base", InputManager.GetEngagedLayerMask(0, set));

            // Positive control: the pass ticks the activators.
            st.Buttons[6] = true;
            GamepadPass(set, st, DevA);
            Assert.Equal("L5", InputManager.GetEngagedLayerMask(0, set));
        }

        // ── DC20 on the menu clock ──

        /// <summary>A commit made by leaving a layer lives for its pulse on
        /// the menu runtime's clock and no longer. Its row reads it on every
        /// frame until the clock passes the pulse, however long the frames
        /// took, and on none after.</summary>
        [Fact]
        public void LayerExitCommit_LivesForItsPulseOnTheMenuClock()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(CellRow("ButtonB", "L1", "Menu 1 Item 2"));
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            for (int frame = 0; frame < 5; frame++)
                Assert.True((RowPass(set, st) & Gamepad.B) != 0, $"frame {frame} of the held pulse");

            _now += MenuEvaluator.CommitPulseMs - 1;       // the pulse's last millisecond
            Assert.True(Fired(im, 2));
            Assert.Equal("L1", InputManager.LayerExitCommitLayer(0, 1, 2, _now));
            Assert.True((RowPass(set, st) & Gamepad.B) != 0, "the commit lapsed inside its pulse");

            _now += 1;                                      // the pulse is over
            Assert.False(Fired(im, 2));
            Assert.Null(InputManager.LayerExitCommitLayer(0, 1, 2, _now));
            Assert.True((RowPass(set, st) & Gamepad.B) == 0, "the commit outlived its pulse");
        }

        private static readonly MethodInfo CollectDirectOutputs = typeof(InputManager).GetMethod(
            "CollectMenuDirectOutputs", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The direct-output walk reads a committed cell's key on
        /// the same clock, through the check the key lane uses: the key is
        /// wanted for the pulse and dropped after it. The walk fills the
        /// wanted set and sends nothing.</summary>
        [Fact]
        public void LayerExitCommit_TheDirectOutputWalkReadsItOnTheMenuClock()
        {
            var set = ArrangeModeShiftMenu();
            set.Menus[0].Items[1].VirtualKey = 0x42;       // cell 2
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            CollectDirectOutputs.Invoke(im, null);
            Assert.Contains((ushort)0x42, im._desiredLatchedKeys);

            _now += MenuEvaluator.CommitPulseMs;
            im._desiredLatchedKeys.Clear();                // the per-frame rebuild
            CollectDirectOutputs.Invoke(im, null);
            Assert.Empty(im._desiredLatchedKeys);
        }

        /// <summary>Every time the menu runtime stamps or weighs comes from
        /// MenuClock. The runtime's own file reads no other clock, and the
        /// overlay's stale gate on the UI timer weighs the snapshot's stamp
        /// on the clock that wrote it.</summary>
        [Fact]
        public void TheMenuRuntimeReadsNoClockButItsOwn()
        {
            string runtime = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Common", "Input", "InputManager.MenuRuntime.cs")));
            Assert.DoesNotContain("TickCount64", runtime);
            Assert.Contains("long nowMs = MenuClock.NowMs;", runtime);

            string service = File.ReadAllText(AuditDelta20260823Tests.FindRepoFile(
                Path.Combine("PadForge.App", "Services", "InputService.cs")));
            int overlay = service.IndexOf("private void UpdateMenuOverlayWindow()", StringComparison.Ordinal);
            Assert.True(overlay > 0, "UpdateMenuOverlayWindow not found");
            string body = service[overlay..service.IndexOf("_menuOverlay.UpdateFromSnapshot(snap);", overlay, StringComparison.Ordinal)];
            Assert.Contains("MenuClock.NowMs - snap.StampMs > 250", body);
            Assert.DoesNotContain("TickCount64", body);
        }

        private static readonly FieldInfo FrameDelta = typeof(InputManager)
            .GetField("_currentFrameDelta", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>A Ramp source on the departed layer whose Up key is the
        /// committed cell steps its row. The source's input is its keys, so
        /// the row looked unread and was never evaluated for the commit. Its
        /// Down key is another input of the departed layer and stays at rest,
        /// so holding it changes nothing.</summary>
        [Fact]
        public void LayerExitCommit_StepsARampKeyOnTheDepartedLayer()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "L1",
                Sources =
                {
                    new MappingSource
                    {
                        Kind = "Ramped", ParamUp = "Menu 1 Item 2", ParamDown = "Button 5",
                        ParamAttackTime = 0, ParamReleaseTime = 0, ParamAutocenter = true,
                    },
                },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            st.Buttons[5] = true;

            Assert.Equal(short.MaxValue, GamepadPass(set, st, DevA).ThumbLX);
        }

        /// <summary>Positive control: the same key steps the row when the
        /// commit is made inside the layer.</summary>
        [Fact]
        public void InLayerCommit_StepsTheSameRampKey_PositiveControl()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "L1",
                Sources =
                {
                    new MappingSource
                    {
                        Kind = "Ramped", ParamUp = "Menu 1 Item 2", ParamDown = "Button 5",
                        ParamAttackTime = 0, ParamReleaseTime = 0, ParamAutocenter = true,
                    },
                },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitInsideTheLayer(im, ud, st);

            Assert.Equal(short.MaxValue, GamepadPass(set, st, DevA).ThumbLX);
        }

        /// <summary>The Incremental twin on a trigger: each frame of the
        /// commit steps the departed row's value up.</summary>
        [Fact]
        public void LayerExitCommit_StepsAnIncrementalKeyOnTheDepartedLayer()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow
            {
                Target = "RightTrigger", LayerMask = "L1",
                Sources =
                {
                    new MappingSource
                    {
                        Kind = "Incremental", ParamUp = "Menu 1 Item 2", ParamDown = "Button 5",
                        ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
                    },
                },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);
            st.Buttons[5] = true;
            var delta = (double[])FrameDelta.GetValue(null);
            double savedDelta = delta[0];
            try
            {
                delta[0] = 0.25;
                GamepadPass(set, st, DevA);
                Assert.True(GamepadPass(set, st, DevA).RightTrigger > 30000,
                    "two quarter steps of the departed row's value");
            }
            finally { delta[0] = savedDelta; }
        }

        private static MappingSource KeySource(string kind, string up, string down) => new()
        {
            Kind = kind, ParamUp = up, ParamDown = down,
            ParamRate = 1, ParamMin = 0, ParamMax = 1, ParamSticky = true,
            ParamAttackTime = 0, ParamReleaseTime = 0, ParamAutocenter = true,
        };

        /// <summary>Two frames of the gamepad row loop, a quarter second
        /// each.</summary>
        private static Gamepad TwoQuarterSecondPasses(MappingSet set, CustomInputState st)
        {
            var delta = (double[])FrameDelta.GetValue(null);
            double savedDelta = delta[0];
            try
            {
                delta[0] = 0.25;
                GamepadPass(set, st, DevA);
                return GamepadPass(set, st, DevA);
            }
            finally { delta[0] = savedDelta; }
        }

        /// <summary>A key source on a target the engaged layer reads too.
        /// The two rows share one value, kept by the target and the source's
        /// place, and the engaged row ticks first in the frame. The commit's
        /// tick replayed that row's value for the frame, so the committed
        /// key never stepped the value while the Base row's own keys rested.
        /// The commit steps it: a trigger by a quarter each frame, a stick
        /// with no attack time to its end.</summary>
        [Theory]
        [InlineData("Incremental")]
        [InlineData("Ramped")]
        public void LayerExitCommit_StepsAKeyOnATargetTheEngagedLayerReadsToo(string kind)
        {
            bool ramp = kind == "Ramped";
            string target = ramp ? "LeftThumbAxisX" : "RightTrigger";
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = { KeySource(kind, "Button 6", "Button 7") } });
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "L1", Sources = { KeySource(kind, "Menu 1 Item 2", "Button 5") } });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            var gp = TwoQuarterSecondPasses(set, st);
            if (ramp) Assert.Equal(short.MaxValue, gp.ThumbLX);
            else Assert.True(gp.RightTrigger > 30000, "two quarter steps of the value the rows share");
        }

        /// <summary>The per-target evaluators (Extended, MIDI, keyboard and
        /// mouse, VR, the touchpad) read the engaged layer's row first and
        /// the commit after it, so each takes the same rule: three quarter
        /// steps press a button and pull a trigger past its middle, and a
        /// Ramp with no attack time puts an axis at its end.</summary>
        [Theory]
        [InlineData("button")]
        [InlineData("axis")]
        [InlineData("trigger")]
        [InlineData("touchpad")]
        public void LayerExitCommit_APerTargetRow_StepsAKeyOnATargetTheEngagedLayerReadsToo(string evaluator)
        {
            string kind = evaluator is "axis" or "touchpad" ? "Ramped" : "Incremental";
            string target = evaluator switch
            {
                "button" => "KbmKey41", "axis" => "LeftThumbAxisX", "trigger" => "RightTrigger", _ => "TouchpadX1",
            };
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = { KeySource(kind, "Button 6", "Button 7") } });
            set.Rows.Add(new MappingRow { Target = target, LayerMask = "L1", Sources = { KeySource(kind, "Menu 1 Item 2", "Button 5") } });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            string dev = DevA.ToString();
            bool pressed = false;
            short value = 0;
            var delta = (double[])FrameDelta.GetValue(null);
            double savedDelta = delta[0];
            try
            {
                delta[0] = 0.25;
                for (int frame = 0; frame < 3; frame++)
                {
                    InputManager.GetSlotSourceKindRuntime(0).FrameSeq++;
                    bool owned = evaluator switch
                    {
                        "button" => InputManager.TryEvaluateMappingSetButton(st, set, dev, 0, target, 50, out pressed),
                        "axis" => InputManager.TryEvaluateMappingSetBipolarAxis(st, set, dev, 0, target, out value),
                        "trigger" => InputManager.TryEvaluateMappingSetRawTrigger(st, set, dev, 0, target, out value),
                        _ => InputManager.TryEvaluateMappingSetTouchpadAxis(st, set, dev, 0, target, 0, out value),
                    };
                    Assert.True(owned);
                }
            }
            finally { delta[0] = savedDelta; }

            if (evaluator == "button") Assert.True(pressed, "three quarter steps never pressed the key");
            else if (evaluator == "trigger") Assert.True(value > 10000, "three quarter steps of the pull");
            else Assert.Equal(short.MaxValue, value);
        }

        /// <summary>One frame of the gamepad row loop at the frame time
        /// given.</summary>
        private static Gamepad PassAt(MappingSet set, CustomInputState st, double frameSeconds)
        {
            var delta = (double[])FrameDelta.GetValue(null);
            double savedDelta = delta[0];
            try
            {
                delta[0] = frameSeconds;
                return GamepadPass(set, st, DevA);
            }
            finally { delta[0] = savedDelta; }
        }

        /// <summary>The value's way to the pad and back through the row
        /// evaluators' short costs a count or two.</summary>
        private static void AssertNear(double expected, int actual)
            => Assert.InRange(actual, expected - 3, expected + 3);

        /// <summary>The Ramp pair at a new source's times, 0.30 s to attack
        /// and to release with autocenter on: the Base row on its own keys,
        /// at rest, and the departed layer's row on the committed cell. The
        /// two share one value and the Base row ticks first in the frame.
        /// Its rest tick took one release step off every frame and the
        /// commit put one attack step on, so the stick never got past one
        /// step for the whole commit. The value is stepped once a frame
        /// with the keys of both reads, so it climbs a step a frame while
        /// the commit lives, and the Base row releases it from there once
        /// the commit has lapsed.</summary>
        [Fact]
        public void LayerExitCommit_StepsARampKeyAtItsDefaultTimes_BesideAnEngagedRowAtRest()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base",
                Sources = { new MappingSource { Kind = "Ramped", ParamUp = "Button 6", ParamDown = "Button 7" } },
            });
            set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "L1",
                Sources = { new MappingSource { Kind = "Ramped", ParamUp = "Menu 1 Item 2", ParamDown = "Button 5" } },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            // 30 ms frames: a tenth of the travel a frame.
            for (int frame = 1; frame <= 3; frame++)
                AssertNear(frame * 0.1 * 32767, PassAt(set, st, 0.03).ThumbLX);

            _now += MenuEvaluator.CommitPulseMs;           // the commit has lapsed
            AssertNear(0.2 * 32767, PassAt(set, st, 0.03).ThumbLX);
        }

        /// <summary>An Incremental commit beside an engaged row that is not
        /// sticky. That row's rest tick put the shared value back on its
        /// floor every frame before the commit stepped it, so the trigger
        /// never got past one step. It climbs a step a frame while the
        /// commit lives, and the engaged row, not sticky, drops it once it
        /// reads alone.</summary>
        [Fact]
        public void LayerExitCommit_StepsAnIncrementalKey_BesideAnEngagedRowThatIsNotSticky()
        {
            var set = ArrangeModeShiftMenu();
            set.Rows.Add(new MappingRow
            {
                Target = "RightTrigger", LayerMask = "Base",
                Sources =
                {
                    new MappingSource
                    {
                        Kind = "Incremental", ParamUp = "Button 6", ParamDown = "Button 7",
                        ParamRate = 1, ParamSticky = false,
                    },
                },
            });
            set.Rows.Add(new MappingRow
            {
                Target = "RightTrigger", LayerMask = "L1",
                Sources =
                {
                    new MappingSource { Kind = "Incremental", ParamUp = "Menu 1 Item 2", ParamDown = "Button 5", ParamRate = 1 },
                },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            CommitByLeavingTheLayer(im, ud, st);

            // 100 ms frames at the full range a second: a tenth a frame.
            for (int frame = 1; frame <= 3; frame++)
                AssertNear(frame * 0.1 * 65535, PassAt(set, st, 0.1).RightTrigger);

            _now += MenuEvaluator.CommitPulseMs;           // the commit has lapsed
            Assert.Equal(0, PassAt(set, st, 0.1).RightTrigger);
        }

        /// <summary>A cell that the Base row and the departed layer's row
        /// both name as a key, a layer copied from Base. With Base engaged
        /// again its row reads the cell fired in the ordinary pass, and the
        /// commit reads the layer's row beside it. The two stepped the
        /// shared value twice a frame, so one commit raised it twice as far
        /// made by leaving the layer as made inside it. It is one step a
        /// frame either way.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LayerExitCommit_ACellBothRowsNameAsAKey_StepsTheValueOnce(bool leaveTheLayer)
        {
            var set = ArrangeModeShiftMenu();
            foreach (string layer in new[] { "Base", "L1" })
            {
                set.Rows.Add(new MappingRow
                {
                    Target = "RightTrigger", LayerMask = layer,
                    Sources = { new MappingSource { Kind = "Incremental", ParamUp = "Menu 1 Item 2", ParamRate = 1 } },
                });
            }
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            if (leaveTheLayer) CommitByLeavingTheLayer(im, ud, st);
            else CommitInsideTheLayer(im, ud, st);

            for (int frame = 1; frame <= 3; frame++)
                AssertNear(frame * 0.1 * 65535, PassAt(set, st, 0.1).RightTrigger);
        }

        /// <summary>Two layers that both let a Base row through end inside
        /// one commit pulse, and the row's Incremental source has a cell of
        /// each menu as a key: Down from the layer left first, Up from the
        /// second. Each commit reads the row with its own key alone, and
        /// the value is stepped once a frame with the keys of every read.
        /// So while both commits live the row reads both keys held, as it
        /// would read the two cells itself, and holds. Once the first has
        /// lapsed the second's key steps the value, and once both have,
        /// nothing reads the row.</summary>
        [Fact]
        public void LayerExitCommit_TwoDepartedLayers_TheirKeysOfOneRowReadTogether()
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators[0].InheritUnmapped = true;
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L2", LayerName = "Two", Descriptor = "Button 9", Mode = "Hold", InheritUnmapped = true,
            });
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L3", LayerName = "Three", Descriptor = "Button 10", Mode = "Hold",
            });
            var second = new MenuDefinitionEntry
            {
                MenuId = 2, Kind = MenuKind.Radial, CellCount = 4, HasCenter = false,
                HostDescriptor = "Gamepad RightStick",
                LayerMask = "L2", LayerHoldsOpen = false,
                FireType = MenuFireType.TouchRelease, EngageDeadzonePercent = 25,
            };
            for (int i = 1; i <= 4; i++)
                second.Items.Add(new MenuItemDefinition { Index = i });
            set.Menus.Add(second);
            set.Rows.Add(new MappingRow
            {
                Target = "RightTrigger", LayerMask = "Base",
                Sources = { KeySource("Incremental", "Menu 2 Item 2", "Menu 1 Item 2") },
            });
            PadForge.Engine.Common.Mapping.SourceCoercion.KeyHeldProvider = InputManager.KeyHeld;
            InputManager.ClearSourceKindRuntime();
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            Deflect(st);
            InputManager.ApplyMacroLayerSwitch(0, "L1");
            im.UpdateMenuContexts(ud, st);                 // menu 1 hovers cell 2
            InputManager.ApplyMacroLayerSwitch(0, "L2");
            im.UpdateMenuContexts(ud, st);                 // L1's menu commits, menu 2 hovers cell 2
            _now += 40;
            InputManager.ApplyMacroLayerSwitch(0, "L3");
            im.UpdateMenuContexts(ud, st);                 // L2's menu commits, 40 ms after L1's
            Assert.True(Fired(im, 2), "leaving L1 did not commit menu 1's cell");
            Assert.True(im.IsMenuItemFired(0, null, 2, 2), "leaving L2 did not commit menu 2's cell");

            // Both commits live: Down and Up together hold the value where
            // it is, on its floor. 100 ms frames at the full range a second.
            for (int frame = 0; frame < 2; frame++)
                Assert.Equal(0, PassAt(set, st, 0.1).RightTrigger);

            _now += 60;                                    // L1's commit has lapsed, L2's has 40 ms left
            Assert.Null(InputManager.LayerExitCommitLayer(0, 1, 2, _now));
            Assert.Equal("L2", InputManager.LayerExitCommitLayer(0, 2, 2, _now));
            AssertNear(0.1 * 65535, PassAt(set, st, 0.1).RightTrigger);
            AssertNear(0.2 * 65535, PassAt(set, st, 0.1).RightTrigger);

            _now += 40;                                    // both have lapsed, and L3 replaces Base
            Assert.Equal(0, PassAt(set, st, 0.1).RightTrigger);
        }

        /// <summary>Two layers that both let a Base row through end inside one
        /// commit pulse, each with a commit the row reads. The per-target pass
        /// evaluated the row once per departed layer, and the gamepad pass
        /// stopped at the first, so the second layer's cell never pressed the
        /// row there.</summary>
        [Fact]
        public void LayerExitCommit_TwoDepartedLayers_TheGamepadPassMatchesThePerTargetPass()
        {
            var set = ArrangeModeShiftMenu();
            set.ShiftActivators[0].InheritUnmapped = true;
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L2", LayerName = "Two", Descriptor = "Button 9", Mode = "Hold", InheritUnmapped = true,
            });
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L3", LayerName = "Three", Descriptor = "Button 10", Mode = "Hold",
            });
            var second = new MenuDefinitionEntry
            {
                MenuId = 2, Kind = MenuKind.Radial, CellCount = 4, HasCenter = false,
                HostDescriptor = "Gamepad RightStick",
                LayerMask = "L2", LayerHoldsOpen = false,
                FireType = MenuFireType.TouchRelease, EngageDeadzonePercent = 25,
            };
            for (int i = 1; i <= 4; i++)
                second.Items.Add(new MenuItemDefinition { Index = i });
            set.Menus.Add(second);
            var row = new MappingRow
            {
                Target = "ButtonA", LayerMask = "Base", CombineMode = "Custom", CombineExpression = "b",
            };
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Menu 1 Item 2" });
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Menu 2 Item 2" });
            set.Rows.Add(row);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);

            Deflect(st);
            InputManager.ApplyMacroLayerSwitch(0, "L1");
            im.UpdateMenuContexts(ud, st);                 // menu 1 hovers cell 2
            InputManager.ApplyMacroLayerSwitch(0, "L2");
            im.UpdateMenuContexts(ud, st);                 // L1's menu commits, menu 2 hovers cell 2
            InputManager.ApplyMacroLayerSwitch(0, "L3");
            im.UpdateMenuContexts(ud, st);                 // L2's menu commits
            Assert.True(Fired(im, 2), "leaving L1 did not commit menu 1's cell");
            Assert.True(im.IsMenuItemFired(0, null, 2, 2), "leaving L2 did not commit menu 2's cell");

            Assert.True(InputManager.TryEvaluateMappingSetButton(st, set, DevA.ToString(), 0, "ButtonA", 50,
                out bool perTarget));
            Assert.True(perTarget, "positive control: the per-target pass read L2's commit");
            Assert.True((RowPass(set, st) & Gamepad.A) != 0, "the gamepad pass read L1's commit alone");
        }

        /// <summary>A departed layer's touchpad passthrough row reading the
        /// committed cell takes the commit, as its sibling evaluators do. The
        /// finger on the left edge is another input of that row and stays at
        /// rest: read, it would tie the cell and win the combine.</summary>
        [Fact]
        public void LayerExitCommit_ReachesATouchpadPassthroughRow()
        {
            var set = ArrangeModeShiftMenu();
            var row = new MappingRow { Target = "TouchpadX1", LayerMask = "L1" };
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Touchpad 0 Finger 0 X" });
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Menu 1 Item 2" });
            set.Rows.Add(row);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            st.Touchpads = new[] { new TouchpadInputState(2) };
            st.Touchpads[0].FingerDown[0] = true;
            st.Touchpads[0].FingerX[0] = 0f;

            CommitByLeavingTheLayer(im, ud, st);

            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(st, set, DevA.ToString(), 0, "TouchpadX1", 0,
                out short x), "the departed layer's touchpad row never saw its commit");
            Assert.Equal(short.MaxValue, x);
        }

        /// <summary>Positive control: inside the layer the row reads the
        /// committed cell through the ordinary lookup.</summary>
        [Fact]
        public void InLayerCommit_ReachesTheSameTouchpadRow_PositiveControl()
        {
            var set = ArrangeModeShiftMenu();
            var row = new MappingRow { Target = "TouchpadX1", LayerMask = "L1" };
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Touchpad 0 Finger 0 X" });
            row.Sources.Add(new MappingSource { Kind = "Direct", Descriptor = "Menu 1 Item 2" });
            set.Rows.Add(row);
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            st.Touchpads = new[] { new TouchpadInputState(2) };

            CommitInsideTheLayer(im, ud, st);

            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(st, set, DevA.ToString(), 0, "TouchpadX1", 0,
                out short x));
            Assert.Equal(short.MaxValue, x);
        }

        /// <summary>A Steam config: button A holds a mode shift that turns
        /// the right trackpad into a two-cell touch menu on Touch Release,
        /// cell 0 bound to the gamepad's B and cell 1 to its X.</summary>
        private const string ModeShiftTouchMenuVdf =
            "\"controller_mappings\"\n{\n\t\"version\"\t\"3\"\n\t\"title\"\t\"Mode shift menu\"\n"
            + "\t\"group\"\n\t{\n\t\t\"id\"\t\"1\"\n\t\t\"mode\"\t\"four_buttons\"\n"
            + "\t\t\"inputs\"\n\t\t{\n"
            + "\t\t\t\"button_a\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\"binding\"\t\"mode_shift right_trackpad 2\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
            + "\t\t}\n\t}\n"
            + "\t\"group\"\n\t{\n\t\t\"id\"\t\"2\"\n\t\t\"mode\"\t\"touch_menu\"\n"
            + "\t\t\"inputs\"\n\t\t{\n"
            + "\t\t\t\"touch_menu_button_0\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button B\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
            + "\t\t\t\"touch_menu_button_1\"\n\t\t\t{\n\t\t\t\t\"activators\"\n\t\t\t\t{\n"
            + "\t\t\t\t\t\"Full_Press\"\n\t\t\t\t\t{\n\t\t\t\t\t\t\"bindings\"\n\t\t\t\t\t\t{\n"
            + "\t\t\t\t\t\t\t\"binding\"\t\"xinput_button X\"\n"
            + "\t\t\t\t\t\t}\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n"
            + "\t\t}\n"
            + "\t\t\"settings\"\n\t\t{\n\t\t\t\"touchmenu_button_fire_type\"\t\"2\"\n"
            + "\t\t\t\"touch_menu_button_count\"\t\"2\"\n\t\t}\n\t}\n"
            + "\t\"preset\"\n\t{\n\t\t\"id\"\t\"0\"\n\t\t\"name\"\t\"Default\"\n"
            + "\t\t\"group_source_bindings\"\n\t\t{\n"
            + "\t\t\t\"1\"\t\"button_diamond active\"\n"
            + "\t\t\t\"2\"\t\"right_trackpad active modeshift\"\n"
            + "\t\t}\n\t}\n}\n";

        /// <summary>End to end: the config above, translated and
        /// materialized the way an import is, runs its committed cell's
        /// binding when the mode shift releases with a finger still on the
        /// cell, as Steam does. Before the fix the cell committed and its
        /// row, which the translator puts on the mode shift's layer, never
        /// ran.</summary>
        [Fact]
        public void TranslatedModeShiftTouchMenu_RunsTheCommittedCellsBinding_WhenTheLayerReleases()
        {
            var p = new ConfigTranslator().Translate(
                PadForge.SteamWorkshop.Model.SteamInputConfig.FromVdf(VdfParser.Parse(ModeShiftTouchMenuVdf)),
                new TranslationOptions { FileId = 47 });
            var profile = PadForge.Services.WorkshopProfileMaterializer.Materialize(p);
            var set = profile.SlotMappingSets[0];
            Assert.NotNull(set);
            var menu = Assert.Single(set.Menus);
            string mask = menu.LayerMask;
            Assert.False(string.IsNullOrEmpty(mask), "the menu did not land on the mode shift's layer");
            Assert.Equal(MenuFireType.TouchRelease, menu.FireType);
            var cellRow = Assert.Single(set.Rows, r => r.Target == "ButtonB" && r.LayerMask == mask);
            Assert.Equal($"Menu {menu.MenuId} Item 0", Assert.Single(cellRow.Sources).Descriptor);

            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.SlotMappingSets[0] = set;
            var im = WiredManager();
            var (ud, st) = AddPad(DevA);
            Assert.StartsWith("Touchpad ", menu.HostDescriptor);
            int padIndex = int.Parse(menu.HostDescriptor.Substring("Touchpad ".Length));
            st.Touchpads = new TouchpadInputState[padIndex + 1];
            for (int i = 0; i <= padIndex; i++) st.Touchpads[i] = new TouchpadInputState(2);
            var surface = st.Touchpads[padIndex];

            InputManager.ApplyMacroLayerSwitch(0, mask);           // the mode shift engages
            surface.FingerDown[0] = true;                          // a finger on cell 0, the left half
            surface.FingerX[0] = 0.25f;
            surface.FingerY[0] = 0.5f;
            im.UpdateMenuContexts(ud, st);
            Assert.Equal(0, im.ActiveMenuOverlay.HoveredIndex);
            Assert.True((RowPass(set, st) & Gamepad.B) == 0, "a hover alone must not press the cell's binding");

            InputManager.ApplyMacroLayerSwitch(0, "");             // the mode shift releases, finger still down
            im.UpdateMenuContexts(ud, st);
            Assert.True(im.IsMenuItemFired(0, null, menu.MenuId, 0), "releasing the mode shift did not commit cell 0");
            Assert.True((RowPass(set, st) & Gamepad.B) != 0, "the committed cell's binding did not run");
            Assert.True((RowPass(set, st) & Gamepad.X) == 0, "the other cell's binding ran");
        }
    }
}
