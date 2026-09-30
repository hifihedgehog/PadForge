using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    /// <summary>
    /// Web Menus (#471): a phone on the web controller shows a slot's Touch
    /// Grid menus and fires a cell by holding its tile. The engine takes a
    /// press only where the page would show it, the snapshot offers the same
    /// set, every reader of a cell answers a tap, and the icon endpoint
    /// serves only what a snapshot names.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class WebMenusTests : IDisposable
    {
        private const int Slot = 3;
        private const int MenuId = 7;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly string _activeProfile = SettingsManager.ActiveProfileId;
        private readonly Func<int, string, int, int, bool> _menuProvider = SourceCoercion.MenuItemFiredProvider;
        private readonly MappingSet _set = new();

        public WebMenusTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            SettingsManager.SlotMappingSets[Slot] = _set;
            SettingsManager.ActiveProfileId = null;
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
        }

        public void Dispose()
        {
            InputManager.EndDeviceStateMemo();
            InputManager.ClearAllShiftRuntime();
            SourceCoercion.MenuItemFiredProvider = _menuProvider;
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            SettingsManager.ActiveProfileId = _activeProfile;
            WebMenusService.SetTokensForTest(new Dictionary<string, string>());
        }

        private static MenuDefinitionEntry GridMenu(bool marked = true)
        {
            var def = new MenuDefinitionEntry
            {
                MenuId = MenuId,
                Name = "Lights",
                Kind = MenuKind.Grid,
                CellCount = 4,
                ShowOnWebController = marked,
            };
            def.Items.Add(new MenuItemDefinition { Index = 2, Label = "Landing", VirtualKey = 0x4C });
            return def;
        }

        private (WebControllerDevice Web, UserDevice Ud) Phone(bool assigned = true)
        {
            var web = new WebControllerDevice("phone-" + Guid.NewGuid().ToString("N"), "Web Menus 1",
                isTouchpad: false, layoutKey: WebControllerDevice.MenusLayoutKey);
            web.SetConnected(true);
            var ud = new UserDevice();
            ud.LoadFromWebDevice(web);
            ud.Device = web;
            ud.IsOnline = true;
            ud.InputState = web.GetCurrentState();
            SettingsManager.UserDevices.Items.Add(ud);
            if (assigned) Assign(ud.InstanceGuid);
            return (web, ud);
        }

        /// <summary>A gamepad at rest on the slot, a device that answers
        /// "(Any Device)".</summary>
        private UserDevice Pad()
        {
            var id = Guid.NewGuid();
            var state = new CustomInputState();
            Array.Fill(state.Axis, 32768);
            state.Axis[2] = state.Axis[5] = 0;
            Array.Fill(state.Povs, -1);
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = "Pad", InstanceName = "Pad",
                CapType = InputDeviceType.Gamepad, InputState = state,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            Assign(id);
            return ud;
        }

        private static void Assign(Guid id)
        {
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
        }

        private static bool Fired(InputManager im, int cell) => im.IsMenuItemFired(Slot, null, MenuId, cell);

        private static (int, int, int)[] Cells(List<(int Slot, int MenuId, int Cell, long Seq)> presses)
            => presses.Select(p => (p.Slot, p.MenuId, p.Cell)).ToArray();

        /// <summary>A manager whose fired set the mapping readers see, the
        /// wiring InputService does at start.</summary>
        private static InputManager Wired()
        {
            var im = new InputManager();
            SourceCoercion.MenuItemFiredProvider = im.IsMenuItemFired;
            return im;
        }

        private static MappingSource Any(string descriptor) => new() { DeviceGuid = "", Descriptor = descriptor };

        private void AddRow(string target, params MappingSource[] sources)
            => _set.Rows.Add(new MappingRow { Target = target, LayerMask = "Base", Sources = sources.ToList() });

        private static readonly MethodInfo BeginFrame = typeof(InputManager)
            .GetMethod("BeginFrameMultiSourceTracking", BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo Apply = typeof(InputManager)
            .GetMethod("ApplyMappingSetToGamepad", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>One Step 3 pass for one device, as UpdateOutputStates runs
        /// it (AnyDeviceSourceEligibilityTests.Pass).</summary>
        private Gamepad Pass(UserDevice ud)
        {
            BeginFrame.Invoke(null, null);
            return PassInFrame(ud);
        }

        /// <summary>A pass inside the frame already begun, so a row read once
        /// a frame stays claimed by the pass that read it first.</summary>
        private Gamepad PassInFrame(UserDevice ud)
        {
            object[] args = { ud.InputState, _set, ud.InstanceGuidString, 50, Slot, new Gamepad() };
            Apply.Invoke(null, args);
            return (Gamepad)args[5];
        }

        /// <summary>A device on the slot that answers no "(Any Device)"
        /// source, an NFC reader.</summary>
        private UserDevice Reader()
        {
            var id = Guid.NewGuid();
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = "Reader", InstanceName = "Reader",
                CapType = InputDeviceType.Nfc, InputState = new CustomInputState(),
            };
            SettingsManager.UserDevices.Items.Add(ud);
            Assign(id);
            return ud;
        }

        // ── The engine takes a press only where the page shows it ─────────

        [Fact]
        public void AHeldTileFiresItsCellAndItsDirectKey()
        {
            _set.Menus.Add(GridMenu());
            var (web, ud) = Phone();
            var im = new InputManager();

            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.True(Fired(im, 2));
            Assert.False(Fired(im, 1));

            // The direct-output pass delivers the cell's key, through the
            // restriction-aware check the key lane uses.
            typeof(InputManager).GetMethod("CollectMenuDirectOutputs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(im, null);
            Assert.Contains((ushort)0x4C, im._desiredLatchedKeys);
        }

        [Fact]
        public void AReleasedTileStopsFiringOnceItsPulseEnds()
        {
            _set.Menus.Add(GridMenu());
            var (web, ud) = Phone();
            var im = new InputManager();

            web.SetMenuCell(Slot, MenuId, 2, true);
            web.SetMenuCell(Slot, MenuId, 2, false);
            // Released within the pulse: the tap still fires once.
            im.UpdateMenuDirectPresses(ud, web);
            Assert.True(Fired(im, 2));

            // Past the pulse the device forgets it, and the next tick clears it.
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            web.ReadMenuPresses(Environment.TickCount64 + MenuEvaluator.CommitPulseMs + 1000,
                MenuEvaluator.CommitPulseMs, list);
            Assert.Empty(list);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.False(Fired(im, 2));
        }

        [Fact]
        public void AQuickTapStaysPressedForTheCommitPulse()
        {
            var web = new WebControllerDevice("tap", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            web.SetMenuCell(Slot, MenuId, 1, true);
            web.SetMenuCell(Slot, MenuId, 1, false);
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Equal(new[] { (Slot, MenuId, 1) }, Cells(list));
        }

        [Theory]
        [InlineData("unmarked")]
        [InlineData("radial")]
        [InlineData("disabled")]
        [InlineData("layer")]
        [InlineData("otherdevice")]
        [InlineData("outside")]
        [InlineData("unassigned")]
        public void APressTheEngineMustNotFireDoesNothing(string refusal)
        {
            var def = GridMenu(marked: refusal != "unmarked");
            if (refusal == "radial") def.Kind = MenuKind.Radial;
            if (refusal == "disabled") def.Enabled = false;
            if (refusal == "layer") def.LayerMask = "L1";
            if (refusal == "otherdevice") def.DeviceGuid = Guid.NewGuid().ToString();
            _set.Menus.Add(def);
            var (web, ud) = Phone(assigned: refusal != "unassigned");
            var im = new InputManager();

            web.SetMenuCell(Slot, MenuId, refusal == "outside" ? 5 : 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.False(Fired(im, 2));
            Assert.False(Fired(im, 5));
        }

        [Fact]
        public void AMenuScopedToThePhoneFiresForThePhonesOwnSource()
        {
            var def = GridMenu();
            _set.Menus.Add(def);
            var (web, ud) = Phone();
            def.DeviceGuid = ud.InstanceGuidString;
            var im = new InputManager();

            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.True(im.IsMenuItemFired(Slot, ud.InstanceGuidString, MenuId, 2));
        }

        [Fact]
        public void EveryRuntimeClearDropsPhonePresses()
        {
            _set.Menus.Add(GridMenu());
            var (web, ud) = Phone();
            var im = new InputManager();
            void PressAgain()
            {
                web.SetMenuCell(Slot, MenuId, 2, false);
                web.SetMenuCell(Slot, MenuId, 2, true);
                im.UpdateMenuDirectPresses(ud, web);
                Assert.True(Fired(im, 2));
            }

            PressAgain();
            im.PurgeMenuContextsForDevice(ud.InstanceGuid);
            Assert.False(Fired(im, 2));

            PressAgain();
            im.ClearMenuRuntimeForSlot(Slot);
            Assert.False(Fired(im, 2));

            PressAgain();
            im.ClearMenuRuntimeForMenu(Slot, MenuId);
            Assert.False(Fired(im, 2));

            PressAgain();
            im.ResetMenuRuntime();
            Assert.False(Fired(im, 2));
        }

        [Fact]
        public void ATileHeldThroughAClearStaysUnfiredUntilPressedAgain()
        {
            _set.Menus.Add(GridMenu());
            var (web, ud) = Phone();
            var im = new InputManager();

            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.True(Fired(im, 2));

            // A profile switch clears the runtime while the finger stays down,
            // as a hover needs a fresh gesture after one.
            im.ResetMenuRuntime();
            im.UpdateMenuDirectPresses(ud, web);
            Assert.False(Fired(im, 2));

            // A repeated down on the held tile is the same press.
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.False(Fired(im, 2));

            web.SetMenuCell(Slot, MenuId, 2, false);
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);
            Assert.True(Fired(im, 2));
        }

        [Fact]
        public void ARestrictedPhoneFiresNoKey()
        {
            _set.Menus.Add(GridMenu());
            var (web, ud) = Phone();
            var im = new InputManager();
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(ud, web);

            var unrestricted = typeof(InputManager).GetMethod("IsMenuItemFiredByUnrestricted",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True((bool)unrestricted.Invoke(im, new object[] { Slot, MenuId, 2, Array.Empty<Guid>() }));
            Assert.False((bool)unrestricted.Invoke(im, new object[] { Slot, MenuId, 2, new[] { ud.InstanceGuid } }));
        }

        // ── Rows, layers, modifiers and macro triggers answer a tap ───────

        [Fact]
        public void AControllersPassReadsATapOnTheSlotsMenu()
        {
            _set.Menus.Add(GridMenu());
            AddRow("ButtonA", Any("Menu 7 Item 2"));
            var (web, phone) = Phone();
            var pad = Pad();
            var im = Wired();

            Assert.False(Pass(pad).IsButtonPressed(Gamepad.A));
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.True(Pass(pad).IsButtonPressed(Gamepad.A));
            // The phone's own pass leaves the cell to the controller, so the
            // slot reads it once, as it did before phones.
            Assert.False(Pass(phone).IsButtonPressed(Gamepad.A));
        }

        [Fact]
        public void AMenuScopedToThePhoneAnswersTheControllersPassToo()
        {
            var def = GridMenu();
            _set.Menus.Add(def);
            AddRow("ButtonA", Any("Menu 7 Item 2"));
            var (web, phone) = Phone();
            def.DeviceGuid = phone.InstanceGuidString;
            var pad = Pad();
            var im = Wired();

            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.True(Pass(pad).IsButtonPressed(Gamepad.A));

            // A menu scoped to the controller holds no phone press, so another
            // controller's pass still reads it as unfired.
            def.DeviceGuid = pad.InstanceGuidString;
            im.UpdateMenuDirectPresses(phone, web);
            Assert.False(im.IsMenuItemFired(Slot, Guid.NewGuid().ToString(), MenuId, 2));
        }

        [Fact]
        public void APhoneAloneOnItsSlotReadsRowsKeyedOnItsCells()
        {
            _set.Menus.Add(GridMenu());
            AddRow("ButtonA", Any("Menu 7 Item 2"));
            // Two cells on one row: the builders that read a row once a frame.
            AddRow("ButtonB", Any("Menu 7 Item 1"), Any("Menu 7 Item 2"));
            AddRow("RightTrigger", Any("Menu 7 Item 1"), Any("Menu 7 Item 2"));
            AddRow("LeftThumbAxisX", Any("Menu 7 Item 1"), Any("Menu 7 Item 2"));
            AddRow("LeftTrigger", Any("Menu 7 Item 2"));
            AddRow("RightThumbAxisY", Any("Menu 7 Item 2"));
            AddRow("TouchpadX1", Any("Menu 7 Item 2"));
            // A gamepad row the phone has no input for stays at rest.
            AddRow("ButtonX", Any("Button 0"));
            AddRow("RightThumbAxisX", Any("Axis 0"));
            var (web, phone) = Phone();
            var im = Wired();

            var idle = Pass(phone);
            Assert.False(idle.IsButtonPressed(Gamepad.A));
            Assert.False(idle.IsButtonPressed(Gamepad.B));
            Assert.Equal((ushort)0, idle.LeftTrigger);

            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);
            var held = Pass(phone);
            Assert.True(held.IsButtonPressed(Gamepad.A));
            Assert.True(held.IsButtonPressed(Gamepad.B));
            Assert.Equal((ushort)65535, held.LeftTrigger);
            Assert.Equal((ushort)65535, held.RightTrigger);
            Assert.NotEqual((short)0, held.ThumbLX);
            Assert.False(held.IsButtonPressed(Gamepad.X));
            Assert.Equal((short)0, held.ThumbRX);

            // The per-target readers the other output types use.
            string guid = phone.InstanceGuidString;
            Assert.True(InputManager.TryEvaluateMappingSetButton(phone.InputState, _set, guid, Slot, "ButtonA", 50, out bool a));
            Assert.True(a);
            Assert.True(InputManager.TryEvaluateMappingSetButton(phone.InputState, _set, guid, Slot, "ButtonX", 50, out bool x));
            Assert.False(x);
            Assert.True(InputManager.TryEvaluateMappingSetBipolarAxis(phone.InputState, _set, guid, Slot, "RightThumbAxisY", out short ry));
            Assert.NotEqual((short)0, ry);
            Assert.True(InputManager.TryEvaluateMappingSetRawTrigger(phone.InputState, _set, guid, Slot, "LeftTrigger", out short lt));
            Assert.True(lt > 0);
            Assert.True(InputManager.TryEvaluateMappingSetTouchpadAxis(phone.InputState, _set, guid, Slot, "TouchpadX1", 0, out short tp));
            Assert.NotEqual((short)0, tp);
        }

        [Fact]
        public void ARowReadOnceAFrameReadsThePhonesCellWhicheverPassComesFirst()
        {
            _set.Menus.Add(GridMenu());
            // A reader answers no "(Any Device)" source and comes ahead of the
            // phone, so its pass claims the row for the frame.
            var reader = Reader();
            AddRow("ButtonB", Any("Menu 7 Item 1"), Any("Menu 7 Item 2"));
            AddRow("RightTrigger", Any("Menu 7 Item 1"), Any("Menu 7 Item 2"));
            var (web, phone) = Phone();
            var im = Wired();
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);

            BeginFrame.Invoke(null, null);
            var first = PassInFrame(reader);
            PassInFrame(phone);
            Assert.True(first.IsButtonPressed(Gamepad.B));
            Assert.Equal((ushort)65535, first.RightTrigger);
        }

        [Fact]
        public void APhoneHostsNoMenu()
        {
            var def = GridMenu();
            _set.Menus.Add(def);
            var (_, phone) = Phone();
            // Scoped to the phone and hosted on a stick, which the phone's
            // resting axes would read as a full push.
            def.DeviceGuid = phone.InstanceGuidString;
            def.HostDescriptor = "Gamepad RightStick";
            var im = new InputManager();
            im.UpdateMenuContexts(phone, phone.InputState);
            Assert.Empty(im.MenuContexts);
            Assert.Null(im.ActiveMenuOverlay);
        }

        [Fact]
        public void ALonePhonesCellTriggerIsConsumedFromTheRows()
        {
            _set.Menus.Add(GridMenu());
            var (web, phone) = Phone();
            var im = Wired();
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);

            var scratch = (Array)typeof(InputManager)
                .GetField("_consumedScratchBySlot", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            scratch.SetValue(null, Slot);
            try
            {
                typeof(InputManager).GetField("_slotTriggerDeviceSlot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(im, -1);
                typeof(InputManager).GetMethod("AddConsumedDescriptor", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(im, new object[] { Slot, Guid.Empty, Any("Menu 7 Item 2") });
                var keys = (HashSet<(string Guid, string Desc)>)scratch.GetValue(Slot);
                Assert.NotNull(keys);
                Assert.Contains(keys, k => k.Guid == "");
                Assert.Contains(keys, k => string.Equals(k.Guid, phone.InstanceGuidString, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                scratch.SetValue(null, Slot);
            }
        }

        [Fact]
        public void APhoneAloneEngagesALayerAndFlipsARowFromItsCells()
        {
            _set.Menus.Add(GridMenu());
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = "", Descriptor = "Menu 7 Item 1", Mode = "Hold", LayerMask = "Tiles",
            });
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftThumbAxisX", LayerMask = "Base",
                Sources = new List<MappingSource>
                {
                    Any("Menu 7 Item 2"),
                    new() { DeviceGuid = "", Kind = "InvertOnHold", ParamModifier = "Menu 7 Item 3" },
                },
            });
            var (web, phone) = Phone();
            var im = Wired();

            Assert.Equal("Base", InputManager.ResolveActiveLayerMask(Slot, _set, phone.InputState, phone.InstanceGuidString));
            web.SetMenuCell(Slot, MenuId, 1, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.Equal("Tiles", InputManager.ResolveActiveLayerMask(Slot, _set, phone.InputState, phone.InstanceGuidString));

            web.ReleaseAllMenuCells();
            InputManager.ClearAllShiftRuntime();
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.True(Pass(phone).ThumbLX > 0);
            web.SetMenuCell(Slot, MenuId, 3, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.True(Pass(phone).ThumbLX < 0);
        }

        [Fact]
        public void APhoneAloneTrimsAStickFromItsTiles()
        {
            InputManager.ClearSourceKindRuntime();
            _set.Menus.Add(GridMenu());
            // Cell 1 holds the trim open, cell 2 is the trim axis.
            _set.Rows.Add(new MappingRow
            {
                Target = "LeftTrigger", LayerMask = "Base", CombineMode = "StickTrim",
                Sources = new List<MappingSource> { Any("Menu 7 Item 1"), Any("Menu 7 Item 2") },
            });
            var (web, phone) = Phone();
            var im = Wired();
            try
            {
                Assert.Equal((ushort)0, Pass(phone).LeftTrigger);
                web.SetMenuCell(Slot, MenuId, 1, true);
                im.UpdateMenuDirectPresses(phone, web);
                Assert.Equal((ushort)65535, Pass(phone).LeftTrigger);

                // Holding the axis tile too walks the level down.
                web.SetMenuCell(Slot, MenuId, 2, true);
                ushort level = 65535;
                for (int i = 0; i < 10 && level == 65535; i++)
                {
                    System.Threading.Thread.Sleep(20);
                    im.UpdateMenuDirectPresses(phone, web);
                    level = Pass(phone).LeftTrigger;
                }
                Assert.True(level < 65535);
            }
            finally
            {
                InputManager.ClearSourceKindRuntime();
            }
        }

        [Fact]
        public void APhoneAloneFiresADeviceFreeCellTriggerAndAControllerReadsItToo()
        {
            _set.Menus.Add(GridMenu());
            var (web, phone) = Phone();
            var im = Wired();
            var check = typeof(InputManager).GetMethod("AnySlotDeviceDescriptorActive", BindingFlags.Instance | BindingFlags.NonPublic);
            var refill = typeof(InputManager).GetField("_slotTriggerDeviceSlot", BindingFlags.Instance | BindingFlags.NonPublic);
            var src = Any("Menu 7 Item 2");
            bool Active()
            {
                refill.SetValue(im, -1); // each evaluator call starts a fresh fill
                return (bool)check.Invoke(im, new object[] { Slot, src });
            }

            Assert.False(Active());
            web.SetMenuCell(Slot, MenuId, 2, true);
            im.UpdateMenuDirectPresses(phone, web);
            Assert.True(Active());

            Pad();
            Assert.True(Active());
        }

        // ── The device ────────────────────────────────────────────────────

        [Fact]
        public void TheMenusDeviceHasNoInputsOfItsOwn()
        {
            var web = new WebControllerDevice("surface", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            Assert.True(web.IsMenuSurface);
            Assert.Equal(0, web.NumAxes);
            Assert.Equal(0, web.NumButtons);
            Assert.Equal(0, web.NumHats);
            Assert.Empty(web.SupportedButtonIndices);
            Assert.Empty(web.SupportedAxisIndices);
            Assert.Empty(web.GetDeviceObjects());
            Assert.Equal(InputDeviceType.WebMenus, web.GetInputDeviceType());
            Assert.False(web.HasRumble);
            Assert.False(InputDeviceType.AnswersAnyDeviceSources(InputDeviceType.WebMenus));
            var ud = new UserDevice();
            ud.LoadFromWebDevice(web);
            Assert.Equal(InputDeviceType.WebMenus, ud.CapType);

            var pad = new WebControllerDevice("pad", "Xbox 360 Web Controller 1", false, "xbox360");
            Assert.False(pad.IsMenuSurface);
            pad.SetMenuCell(Slot, MenuId, 1, true);
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            pad.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Empty(list);
        }

        [Fact]
        public void PressesAreBoundedAndADisconnectLetsGoOfThem()
        {
            var web = new WebControllerDevice("bounds", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            web.SetMenuCell(-1, MenuId, 1, true);
            web.SetMenuCell(64, MenuId, 1, true);
            web.SetMenuCell(Slot, MenuId, 64, true);
            for (int i = 0; i < WebControllerDevice.MaxHeldMenuCells + 5; i++)
                web.SetMenuCell(Slot, 100 + i, 0, true);
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Equal(WebControllerDevice.MaxHeldMenuCells, list.Count);

            web.SetConnected(false);
            list.Clear();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Empty(list);
        }

        [Fact]
        public void APhoneThatStopsAnsweringLetsGoOfItsTiles()
        {
            // The #402 ping covers a menus session, and its expiry
            // neutralizes the device, which lets go of the tiles.
            Assert.True(WebControllerServer.UsesInputDeadline(WebControllerDevice.MenusLayoutKey));
            Assert.True(WebControllerServer.UsesInputDeadline("gamepad"));
            Assert.False(WebControllerServer.UsesInputDeadline("xbox360"));

            var web = new WebControllerDevice("lapse", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            web.SetMenuCell(Slot, MenuId, 2, true);
            web.NeutralizeAll();
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Empty(list);
        }

        [Fact]
        public void TheSnapshotGoesOutOnlyWhenItChangesAndAgainOnReconnect()
        {
            var web = new WebControllerDevice("feed", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            var sent = new List<string>();
            web.MenusFeedChanged += sent.Add;
            web.SetMenusFeed("{\"a\":1}");
            web.SetMenusFeed("{\"a\":1}");
            web.SetMenusFeed("{\"a\":2}");
            // A resend forgets the last snapshot, so the one sender, the next
            // build, sends it whole.
            web.ResendMenusFeed();
            Assert.Equal(2, sent.Count);
            web.SetMenusFeed("{\"a\":2}");
            Assert.Equal(new[] { "{\"a\":1}", "{\"a\":2}", "{\"a\":2}" }, sent);
        }

        // ── The server's messages ─────────────────────────────────────────

        [Fact]
        public void TheServerAppliesCellAndProfileMessages()
        {
            var web = new WebControllerDevice("msg", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            string requested = null;
            web.ProfileRequested += id => requested = id;

            byte[] cell = Encoding.UTF8.GetBytes("{\"type\":\"cell\",\"slot\":3,\"menu\":7,\"cell\":2,\"down\":true}");
            WebControllerServer.ProcessMessage(web, cell, cell.Length);
            var list = new List<(int Slot, int MenuId, int Cell, long Seq)>();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Equal(new[] { (3, 7, 2) }, Cells(list));

            byte[] profile = Encoding.UTF8.GetBytes("{\"type\":\"profile\",\"id\":\"racing\"}");
            WebControllerServer.ProcessMessage(web, profile, profile.Length);
            Assert.Equal("racing", requested);
            requested = null;
            WebControllerServer.ProcessMessage(web, profile, profile.Length, acceptInput: false);
            Assert.Null(requested);

            // A menus device takes no pad input from a crafted client.
            byte[] input = Encoding.UTF8.GetBytes("{\"type\":\"input\",\"kind\":\"button\",\"code\":0,\"value\":1}");
            WebControllerServer.ProcessMessage(web, input, input.Length);
            Assert.False(web.GetCurrentState().Buttons[0]);
            byte[] touch = Encoding.UTF8.GetBytes("{\"type\":\"touchpad\",\"finger\":0,\"x\":0.5,\"y\":0.5,\"down\":true}");
            WebControllerServer.ProcessMessage(web, touch, touch.Length);
            Assert.False(web.HasTouchpad);

            // An expired session takes no press.
            web.ReleaseAllMenuCells();
            WebControllerServer.ProcessMessage(web, cell, cell.Length, acceptInput: false);
            list.Clear();
            web.ReadMenuPresses(Environment.TickCount64, MenuEvaluator.CommitPulseMs, list);
            Assert.Empty(list);
        }

        // ── The snapshot ──────────────────────────────────────────────────

        private static System.Text.Json.JsonElement Parse(string json)
            => System.Text.Json.JsonDocument.Parse(json).RootElement;

        [Fact]
        public void APhoneOnNoSlotSaysSo()
        {
            var (_, ud) = Phone(assigned: false);
            var json = new WebMenusService().BuildFeed(ud, new InputManager(),
                new List<WebMenusService.FeedProfile>(), new Dictionary<string, string>());
            Assert.Equal("unassigned", Parse(json).GetProperty("state").GetString());
            Assert.False(Parse(json).TryGetProperty("slots", out _));
        }

        [Fact]
        public void ASlotWithNoMarkedMenuSaysSoAndNamesTheSlot()
        {
            _set.Menus.Add(GridMenu(marked: false));
            var (_, ud) = Phone();
            var json = new WebMenusService().BuildFeed(ud, new InputManager(),
                new List<WebMenusService.FeedProfile>(), new Dictionary<string, string>());
            Assert.Equal("empty", Parse(json).GetProperty("state").GetString());
            var slot = Assert.Single(Parse(json).GetProperty("slots").EnumerateArray());
            Assert.Equal(Slot, slot.GetInt32());
        }

        [Fact]
        public void TheSnapshotOffersTheSameMenusTheEngineFires()
        {
            var shown = GridMenu();
            shown.Items[0].Icon = "\U0001F6EC";
            shown.Items[0].MacroName = "Landing Lights";
            _set.Menus.Add(shown);
            var hidden = GridMenu(marked: false);
            hidden.MenuId = MenuId + 1;
            _set.Menus.Add(hidden);
            var radial = GridMenu();
            radial.MenuId = MenuId + 2;
            radial.Kind = MenuKind.Radial;
            _set.Menus.Add(radial);
            var layered = GridMenu();
            layered.MenuId = MenuId + 3;
            layered.LayerMask = "L1";
            _set.Menus.Add(layered);

            var (_, ud) = Phone();
            var im = new InputManager();
            var macro = new MacroItem { Name = "Landing Lights", TriggerMode = MacroTriggerMode.Toggle };
            macro.ToggleTriggerLatched = true;
            im.MacroSnapshots[Slot] = new[] { macro };

            var json = new WebMenusService().BuildFeed(ud, im,
                new List<WebMenusService.FeedProfile>(), new Dictionary<string, string>());
            var root = Parse(json);
            Assert.Equal("ok", root.GetProperty("state").GetString());
            var pages = root.GetProperty("pages").EnumerateArray().ToList();
            var page = Assert.Single(pages);
            Assert.Equal(Slot, page.GetProperty("slot").GetInt32());
            Assert.Equal(MenuId, page.GetProperty("menu").GetInt32());
            Assert.Equal(4, page.GetProperty("count").GetInt32());
            var cell = Assert.Single(page.GetProperty("cells").EnumerateArray());
            Assert.Equal(2, cell.GetProperty("i").GetInt32());
            Assert.Equal("Landing", cell.GetProperty("label").GetString());
            Assert.Equal("\U0001F6EC", cell.GetProperty("glyph").GetString());
            Assert.True(cell.GetProperty("on").GetBoolean());

            macro.ToggleTriggerLatched = false;
            json = new WebMenusService().BuildFeed(ud, im,
                new List<WebMenusService.FeedProfile>(), new Dictionary<string, string>());
            cell = Parse(json).GetProperty("pages")[0].GetProperty("cells")[0];
            Assert.False(cell.GetProperty("on").GetBoolean());
        }

        [Fact]
        public void ACellWithNoToggleMacroCarriesNoState()
        {
            Assert.Null(WebMenusService.ToggleState(new InputManager(), Slot, "Nothing"));
            var im = new InputManager();
            im.MacroSnapshots[Slot] = new[] { new MacroItem { Name = "Tap", TriggerMode = MacroTriggerMode.OnPress } };
            Assert.Null(WebMenusService.ToggleState(im, Slot, "Tap"));
        }

        [Fact]
        public void TheActiveProfileIsNamedAndDefaultMapsToItsListEntry()
        {
            var (_, ud) = Phone(assigned: false);
            var profiles = new List<WebMenusService.FeedProfile>
            {
                new() { Id = ProfileListItem.DefaultProfileId, Name = "Default" },
                new() { Id = "racing", Name = "Racing" },
            };
            var json = new WebMenusService().BuildFeed(ud, new InputManager(), profiles, new Dictionary<string, string>());
            Assert.Equal(ProfileListItem.DefaultProfileId, Parse(json).GetProperty("profile").GetProperty("id").GetString());

            SettingsManager.ActiveProfileId = "racing";
            json = new WebMenusService().BuildFeed(ud, new InputManager(), profiles, new Dictionary<string, string>());
            Assert.Equal("Racing", Parse(json).GetProperty("profile").GetProperty("name").GetString());
            Assert.Equal(2, Parse(json).GetProperty("profiles").GetArrayLength());
        }

        [Fact]
        public void AnImageGoesByTokenAndTheEndpointServesOnlyWhatASnapshotNames()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pf471-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string png = Path.Combine(dir, "lamp.png");
                File.WriteAllBytes(png, TinyPng());
                var def = GridMenu();
                def.Items[0].Icon = png;
                _set.Menus.Add(def);
                var (_, ud) = Phone();

                var tokens = new Dictionary<string, string>();
                var json = new WebMenusService().BuildFeed(ud, new InputManager(),
                    new List<WebMenusService.FeedProfile>(), tokens);
                string img = Parse(json).GetProperty("pages")[0].GetProperty("cells")[0].GetProperty("img").GetString();
                string token = WebMenusService.Token(png);
                Assert.Equal("/api/menuicon?t=" + token, img);
                Assert.Equal(png, tokens[token]);
                // The page never learns where the picture lives.
                Assert.DoesNotContain("lamp.png", json);

                WebMenusService.SetTokensForTest(tokens);
                Assert.True(WebMenusService.TryGetIcon(token, out byte[] bytes, out string type));
                Assert.Equal("image/png", type);
                Assert.Equal(TinyPng(), bytes);
                Assert.False(WebMenusService.TryGetIcon("0123456789abcdef", out _, out _));
                Assert.False(WebMenusService.TryGetIcon(null, out _, out _));

                WebMenusService.SetTokensForTest(new Dictionary<string, string>());
                Assert.False(WebMenusService.TryGetIcon(token, out _, out _));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void ATokenStopsServingOnceNoSnapshotNamesIt()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pf471-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string png = Path.Combine(dir, "lamp.png");
                File.WriteAllBytes(png, TinyPng());
                var def = GridMenu();
                def.Items[0].Icon = png;
                _set.Menus.Add(def);
                Phone();
                string token = WebMenusService.Token(png);
                var none = new List<ProfileListItem>();

                new WebMenusService().Tick(new InputManager(), none);
                Assert.True(WebMenusService.TryGetIcon(token, out _, out _));

                // The icon leaves the cell, so the next snapshot names no
                // picture and the old token stops serving.
                def.Items[0].Icon = "";
                new WebMenusService().Tick(new InputManager(), none);
                Assert.False(WebMenusService.TryGetIcon(token, out _, out _));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Theory]
        [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D }, "image/png")]
        [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
        [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 }, "image/gif")]
        [InlineData(new byte[] { 0x42, 0x4D, 0x00, 0x00 }, "image/bmp")]
        [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 }, null)]
        public void OnlyTheIconFormatsAreServed(byte[] head, string type)
            => Assert.Equal(type, WebMenusService.SniffImageType(head));

        // 1x1 transparent PNG.
        private static byte[] TinyPng() => Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        // ── Icon forms ────────────────────────────────────────────────────

        [Theory]
        [InlineData("\U0001F468\u200D\U0001F4BB", true)]  // ZWJ sequence
        [InlineData("\U0001F1FA\U0001F1F8", true)]         // flag
        [InlineData("\u2620\uFE0F", true)]                  // emoji presentation selector
        [InlineData("\U0001F44D\U0001F3FD", true)]         // skin tone
        [InlineData("\u21E7", true)]
        [InlineData("A", true)]
        [InlineData("", false)]
        [InlineData(" ", false)]
        [InlineData("\U0001F600\U0001F600", false)]
        [InlineData("ghost_050_menu_0030.png", false)]
        [InlineData("pficon://Pack/a.png", false)]
        [InlineData("C:\\icons\\a.png", false)]
        public void AnEmojiIsOneTextElement(string reference, bool glyph)
            => Assert.Equal(glyph, MenuIconResolver.IsGlyph(reference));

        [Fact]
        public void OnlyAPackageEntryOrAnImagePathIsAPicture()
        {
            Assert.True(MenuIconResolver.IsImageReference("pficon://Pack/a.png"));
            Assert.True(MenuIconResolver.IsImageReference("C:\\icons\\a.png"));
            Assert.False(MenuIconResolver.IsImageReference("\u21E7"));
            Assert.False(MenuIconResolver.IsImageReference("ghost_050_menu_0030.png"));
            Assert.False(MenuIconResolver.IsImageReference(""));
            Assert.Null(MenuIconResolver.TryReadIconBytes("\U0001F600"));
        }

        [Fact]
        public void ALayerIconDrawsItsPictureOrAGlyphAndNeverAPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pf471-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string png = Path.Combine(dir, "layer.png");
                File.WriteAllBytes(png, TinyPng());
                Assert.NotNull(MenuIconResolver.ResolveLayerIcon(png, out string picture));
                Assert.Equal("⇧", picture);
                Assert.Null(MenuIconResolver.ResolveLayerIcon("\U0001F525", out string emoji));
                Assert.Equal("\U0001F525", emoji);
                Assert.Null(MenuIconResolver.ResolveLayerIcon("", out string empty));
                Assert.Equal("⇧", empty);
                Assert.Null(MenuIconResolver.ResolveLayerIcon(null, out string none));
                Assert.Equal("⇧", none);
                Assert.Null(MenuIconResolver.ResolveLayerIcon(Path.Combine(dir, "gone.png"), out string gone));
                Assert.Equal("⇧", gone);
                Assert.Null(MenuIconResolver.ResolveLayerIcon("pficon://Missing/a.png", out string missing));
                Assert.Equal("⇧", missing);
                Assert.Null(MenuIconResolver.ResolveLayerIcon(Path.Combine(dir, "layer.ico"), out string ico));
                Assert.Equal("⇧", ico);
                Assert.Null(MenuIconResolver.ResolveLayerIcon("badge.png", out string bare));
                Assert.Equal("⇧", bare);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void AnImageInPadForgesFolderStoresAsAPath()
        {
            string inApp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "badge.png");
            string stored = PadForge.Views.IconPickerHost.StoredImagePath(inApp);
            Assert.Equal("." + Path.DirectorySeparatorChar + "badge.png", stored);
            Assert.True(MenuIconResolver.IsImageReference(stored));
            string sub = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", "badge.png");
            Assert.Equal(Path.Combine("icons", "badge.png"), PadForge.Views.IconPickerHost.StoredImagePath(sub));
            string away = Path.Combine(Path.GetTempPath(), "badge.png");
            Assert.Equal(Path.GetFullPath(away), PadForge.Views.IconPickerHost.StoredImagePath(away));
        }

        [Fact]
        public void ThePickerMarksTheIconHeldNowFromOneReadOfEachPackage()
        {
            var saved = IconPackageManager.SaveRegistry();
            string dir = Path.Combine(Path.GetTempPath(), "pf471-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                IconPackageManager.LoadRegistry(null);
                string pack = Path.Combine(dir, "Deck.pficons");
                using (var zip = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var name in new[] { "a.png", "b.png" })
                        using (var s = zip.CreateEntry(name).Open())
                            s.Write(TinyPng());
                }
                string registered = IconPackageManager.Register(pack);
                Assert.NotNull(registered);
                string current = IconPackageManager.MakeRef(registered, "b.png");

                var groups = PadForge.Views.IconPicker.BuildGroups(current);
                var group = Assert.Single(groups);
                Assert.Equal(new[] { "a.png", "b.png" }, group.Icons.Select(c => c.Name).OrderBy(n => n).ToArray());
                Assert.True(group.Icons.Single(c => c.Name == "b.png").IsCurrent);
                Assert.False(group.Icons.Single(c => c.Name == "a.png").IsCurrent);
                Assert.All(group.Icons, c => Assert.NotNull(c.Image));
            }
            finally
            {
                IconPackageManager.LoadRegistry(saved);
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void EmojiCategoriesAreNamedInTheCurrentLanguage()
        {
            foreach (var category in PadForge.Views.ShiftActivatorDialog.EmojiCatalog)
                Assert.NotEqual("Emoji_Category_" + category.Name, category.DisplayName);
            var hands = PadForge.Views.ShiftActivatorDialog.EmojiCatalog.Single(c => c.Name == "Hands");
            var old = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("de");
                Assert.Equal("H\u00e4nde", hands.DisplayName);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = old;
            }
        }

        // ── Profile transfer ──────────────────────────────────────────────

        [Fact]
        public void LayerIconPackagesAreBundledAndRewritten()
        {
            var ms = new MappingSet { BaseIcon = "pficon://Base/b.png" };
            ms.ShiftActivators.Add(new ShiftActivator { LayerMask = "L1", Icon = "pficon://Layer/l.png" });
            ms.ShiftActivators.Add(new ShiftActivator { LayerMask = "L2", Icon = "\U0001F525" });
            var profile = new ProfileData { SlotMappingSets = new[] { ms } };

            var referenced = (IEnumerable<string>)typeof(ProfileTransfer)
                .GetMethod("ReferencedIconPackages", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { profile });
            Assert.Equal(new[] { "Base", "Layer" }, referenced.OrderBy(x => x).ToArray());

            // One guard across the whole import, compared by reference, as
            // ProfileTransfer.Import builds it. A chain of " (n)" renames must
            // move each icon once.
            var rewrite = typeof(ProfileTransfer).GetMethod("RewriteIconPackageRefs", BindingFlags.Static | BindingFlags.NonPublic);
            var guard = new HashSet<object>(ReferenceEqualityComparer.Instance);
            rewrite.Invoke(null, new object[] { profile, "Layer", "Layer (2)", guard });
            rewrite.Invoke(null, new object[] { profile, "Base", "Base (2)", guard });
            rewrite.Invoke(null, new object[] { profile, "Layer (2)", "Layer (3)", guard });
            rewrite.Invoke(null, new object[] { profile, "Base (2)", "Base (3)", guard });
            Assert.Equal("pficon://Layer (2)/l.png", ms.ShiftActivators[0].Icon);
            Assert.Equal("\U0001F525", ms.ShiftActivators[1].Icon);
            Assert.Equal("pficon://Base (2)/b.png", ms.BaseIcon);
        }

        // ── The model and the editor ─────────────────────────────────────

        [Fact]
        public void ShowOnWebControllerRidesCloneXmlAndJson()
        {
            var def = GridMenu();
            Assert.True(def.Clone().ShowOnWebController);

            var xml = new System.Xml.Serialization.XmlSerializer(typeof(MenuDefinitionEntry));
            var sw = new StringWriter();
            xml.Serialize(sw, def);
            var back = (MenuDefinitionEntry)xml.Deserialize(new StringReader(sw.ToString()));
            Assert.True(back.ShowOnWebController);

            string json = System.Text.Json.JsonSerializer.Serialize(def);
            Assert.True(System.Text.Json.JsonSerializer.Deserialize<MenuDefinitionEntry>(json).ShowOnWebController);

            // A file written before the field existed.
            var legacy = (MenuDefinitionEntry)xml.Deserialize(new StringReader(
                "<MenuDefinitionEntry MenuId=\"1\" Kind=\"Grid\" />"));
            Assert.False(legacy.ShowOnWebController);
        }

        [Fact]
        public void TheEditorsCheckboxIsForTouchGridAndResetsOff()
        {
            var vm = new MenuEditorItem(GridMenu(marked: false));
            Assert.True(vm.IsGrid);
            vm.ShowOnWebController = true;
            Assert.True(vm.Entry.ShowOnWebController);
            vm.ResetSettingCommand.Execute(nameof(MenuEditorItem.ShowOnWebController));
            Assert.False(vm.Entry.ShowOnWebController);

            vm.KindIndex = 0;
            Assert.False(vm.IsGrid);
            Assert.False(vm.ShowWebControllerRow);

            // A radial menu that kept the flag shows the row, so it can clear it.
            var radial = GridMenu();
            radial.Kind = MenuKind.Radial;
            var kept = new MenuEditorItem(radial);
            Assert.True(kept.ShowWebControllerRow);
            kept.ShowOnWebController = false;
            Assert.False(kept.ShowWebControllerRow);
        }

        // ── Remote Link ──────────────────────────────────────────────────

        [Fact]
        public void AMenusPhoneIsNotSharedOverRemoteLink()
        {
            var shareable = typeof(InputService).GetMethod("IsShareableDevice", BindingFlags.Static | BindingFlags.NonPublic);
            var phone = new WebControllerDevice("share", "Web Menus 1", false, WebControllerDevice.MenusLayoutKey);
            var pad = new WebControllerDevice("share2", "Xbox 360 Web Controller 1", false, "xbox360");
            Assert.False((bool)shareable.Invoke(null, new object[] { phone }));
            Assert.True((bool)shareable.Invoke(null, new object[] { pad }));
        }
    }
}
