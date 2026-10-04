using System;
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

        public MenuLayerGateRuntimeTests()
        {
            _savedDevices = SettingsManager.UserDevices;
            _savedSettings = SettingsManager.UserSettings;
            _savedSets = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
            _savedMenuProvider = PadForge.Engine.Common.Mapping.SourceCoercion.MenuItemFiredProvider;
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
            for (int i = 0; i < _savedSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = _savedSets[i];
            PadForge.Engine.Common.Mapping.SourceCoercion.MenuItemFiredProvider = _savedMenuProvider;
            InputManager.ClearAllShiftRuntime();
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
