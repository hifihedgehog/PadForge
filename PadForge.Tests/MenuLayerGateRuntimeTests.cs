using System;
using PadForge.Common.Input;
using PadForge.Engine.Common;
using PadForge.Engine;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
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

        public MenuLayerGateRuntimeTests()
        {
            _savedDevices = SettingsManager.UserDevices;
            _savedSettings = SettingsManager.UserSettings;
            _savedSets = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
        }

        public void Dispose()
        {
            SettingsManager.UserDevices = _savedDevices;
            SettingsManager.UserSettings = _savedSettings;
            for (int i = 0; i < _savedSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = _savedSets[i];
            InputManager.ClearAllShiftRuntime();
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

        // ── Two pads on one slot (Astra round 4, finding 1) ──────────────

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

        // ── Finger-up click sampling (Astra round 4, finding 3) ──────────

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
    }
}
