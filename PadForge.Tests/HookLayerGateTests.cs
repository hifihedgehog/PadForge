using System;
using System.Collections.Generic;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The low-level hooks eat a macro's trigger key only while the macro's
    /// layer gate is open on its slot (MacroLayerGateOpen, the rule the
    /// engine's own consume follows). Before, a macro on the "Shift" layer
    /// with Consume Trigger Buttons on F1 ate F1 in Windows on every layer,
    /// so F1 typed nowhere on Base, where the macro cannot fire. A key's
    /// down and up are decided as a pair, so Windows never sees one without
    /// the other when the layer moves while the key is held.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class HookLayerGateTests : IDisposable
    {
        private const int Slot = 0;
        private static readonly Guid DevGuid = new("44444444-4444-4444-4444-444444444444");

        private const int VkA = 0x41, VkB = 0x42, VkF1 = 0x70, VkF2 = 0x71;
        private const uint ScanA = 0x1E, ScanB = 0x30, ScanF1 = 0x3B, ScanF2 = 0x3C;
        private const int X1 = 3;

        private readonly MappingSet[] _savedSets = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
        private readonly InputHookManager _hooks = new();
        private readonly MappingSet _set;
        private readonly MacroItem _onShift, _onBase;

        public HookLayerGateTests()
        {
            InputManager.ClearAllShiftRuntime();
            _set = new MappingSet();
            _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = DevGuid.ToString(),
                Descriptor = "Button 9",
                LayerMask = "Shift",
                LayerName = "Shift",
                Mode = "Hold",
                InheritUnmapped = false,
            });
            for (int i = 0; i < SettingsManager.SlotMappingSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = null;
            SettingsManager.SlotMappingSets[Slot] = _set;

            _onShift = new MacroItem { PadIndex = Slot, LayerMask = "Shift" };
            _onBase = new MacroItem { PadIndex = Slot, LayerMask = "Base" };

            // A row's mapped key and an any-layer macro's key: the static set.
            _hooks.SetSuppressedKeys(new HashSet<int> { VkA, VkB });
            InputManager.SetLayerGatedHookKeys(new[]
            {
                new InputManager.HookTriggerKey(_onShift, VkF1, false),
                new InputManager.HookTriggerKey(_onBase, VkF2, false),
                new InputManager.HookTriggerKey(_onShift, X1, true),
            });
        }

        public void Dispose()
        {
            InputManager.SetLayerGatedHookKeys(Array.Empty<InputManager.HookTriggerKey>());
            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            _hooks.SetSuppressedKeys(new HashSet<int>());
            _hooks.SetSuppressedMouseButtons(new HashSet<int>());
            InputManager.ClearAllShiftRuntime();
            for (int i = 0; i < _savedSets.Length; i++)
                SettingsManager.SlotMappingSets[i] = _savedSets[i];
        }

        private void HoldShift(bool held)
        {
            var st = new CustomInputState();
            st.Buttons[9] = held;
            InputManager.ResolveActiveLayerMask(Slot, _set, st, DevGuid.ToString());
            InputManager.PublishLayerGatedHookKeys(evaluating: true);
        }

        private static bool Key(int vk, uint scan, bool down)
            => InputHookManager.HandleKeyboardEvent(vk, scan, 0, IntPtr.Zero, down);

        /// <summary>A press and release, true when the hook ate both.</summary>
        private static bool Tap(int vk, uint scan)
        {
            bool down = Key(vk, scan, true);
            bool up = Key(vk, scan, false);
            Assert.Equal(down, up);
            return down;
        }

        private static bool Click(int button)
        {
            bool down = InputHookManager.HandleMouseButton(button, true, IntPtr.Zero, 0);
            bool up = InputHookManager.HandleMouseButton(button, false, IntPtr.Zero, 0);
            Assert.Equal(down, up);
            return down;
        }

        private static bool MergedKey(int vk, bool rawInputSaysDown)
        {
            var keys = new bool[256];
            keys[vk] = rawInputSaysDown;
            InputHookManager.MergeHookedKeyState(keys, keys.Length);
            return keys[vk];
        }

        private static bool MergedButton(int button, bool rawInputSaysDown)
        {
            var buttons = new bool[5];
            buttons[button] = rawInputSaysDown;
            InputHookManager.MergeHookedMouseState(buttons, buttons.Length);
            return buttons[button];
        }

        [Fact]
        public void ALayerMacrosKey_IsEatenOnlyWhileItsLayerIsEngaged()
        {
            HoldShift(false);
            Assert.False(Tap(VkF1, ScanF1));   // Shift's macro: F1 types on Base
            Assert.True(Tap(VkF2, ScanF2));    // Base's macro: eaten on Base
            Assert.True(Tap(VkA, ScanA));      // any-layer macro
            Assert.True(Tap(VkB, ScanB));      // a row's mapped key
            Assert.False(Click(X1));

            HoldShift(true);
            Assert.True(Tap(VkF1, ScanF1));
            Assert.False(Tap(VkF2, ScanF2));   // Shift replaces Base
            Assert.True(Tap(VkA, ScanA));
            Assert.True(Tap(VkB, ScanB));
            Assert.True(Click(X1));

            HoldShift(false);
            Assert.False(Tap(VkF1, ScanF1));
            Assert.True(Tap(VkF2, ScanF2));
            Assert.False(Click(X1));
        }

        /// <summary>With no slot evaluating (idle, a focus suspension, an
        /// engine stop) no layer macro can fire, so none of their keys is
        /// eaten, and no static key either (HookConsumePauseTests).</summary>
        [Fact]
        public void WithNoSlotEvaluating_NoLayerMacrosKeyIsEaten()
        {
            HoldShift(true);
            Assert.True(Tap(VkF1, ScanF1));
            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            Assert.False(Tap(VkF1, ScanF1));
            Assert.False(Tap(VkF2, ScanF2));
            Assert.False(Tap(VkA, ScanA));
            Assert.False(Tap(VkB, ScanB));
        }

        /// <summary>F1 went down to Windows on Base, Shift engaged while it
        /// was held: its up still goes to Windows, and PadForge reads it held
        /// from Raw Input until then.</summary>
        [Fact]
        public void AKeyDownThatPassed_KeepsItsUpPassed_WhenTheLayerEngages()
        {
            HoldShift(false);
            Assert.True(Tap(VkB, ScanB)); // the hook has tracked a key before
            Assert.False(Key(VkF1, ScanF1, true));
            HoldShift(true);
            Assert.True(MergedKey(VkF1, rawInputSaysDown: true));
            Assert.False(Key(VkF1, ScanF1, true)); // a repeat goes the down's way
            Assert.False(Key(VkF1, ScanF1, false));
            Assert.False(MergedKey(VkF1, rawInputSaysDown: false));
            // The next press is the layer's.
            Assert.True(Tap(VkF1, ScanF1));
        }

        /// <summary>F1 was eaten on Shift, Shift ended while it was held: its
        /// up is eaten too, and PadForge reads it held from the hook until
        /// then, since Raw Input never saw the down.</summary>
        [Fact]
        public void AKeyDownThatWasEaten_KeepsItsUpEaten_WhenTheLayerEnds()
        {
            HoldShift(true);
            Assert.True(Key(VkF1, ScanF1, true));
            HoldShift(false);
            Assert.True(MergedKey(VkF1, rawInputSaysDown: false));
            Assert.True(Key(VkF1, ScanF1, true)); // a repeat goes the down's way
            Assert.True(Key(VkF1, ScanF1, false));
            Assert.False(MergedKey(VkF1, rawInputSaysDown: false));
            Assert.False(Tap(VkF1, ScanF1));
        }

        /// <summary>The mouse hook pairs a button's down and up the same way.</summary>
        [Fact]
        public void AButtonsUp_GoesTheWayItsDownWent()
        {
            HoldShift(false);
            Assert.False(InputHookManager.HandleMouseButton(X1, true, IntPtr.Zero, 0));
            HoldShift(true);
            Assert.True(MergedButton(X1, rawInputSaysDown: true));
            Assert.False(InputHookManager.HandleMouseButton(X1, false, IntPtr.Zero, 0));

            Assert.True(InputHookManager.HandleMouseButton(X1, true, IntPtr.Zero, 0));
            HoldShift(false);
            Assert.True(MergedButton(X1, rawInputSaysDown: false));
            Assert.True(InputHookManager.HandleMouseButton(X1, false, IntPtr.Zero, 0));
            Assert.False(MergedButton(X1, rawInputSaysDown: false));
        }
    }

    /// <summary>
    /// While the engine evaluates nothing (idle, a focus suspension, the poll
    /// loop's exit) the hooks eat none of the consume sets, the static one
    /// included: no row or macro reads the key, and with background polling
    /// off a focus suspension means the user is typing in another program.
    /// Before, the static set stayed in the hook and every mapped key was
    /// dead there. A key's down and up still go the same way.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class HookConsumePauseTests : IDisposable
    {
        private const int VkF6 = 0x75;
        private const uint ScanF6 = 0x40;
        private const int X2 = 4;

        private readonly InputHookManager _hooks = new();

        public HookConsumePauseTests()
        {
            InputManager.SetLayerGatedHookKeys(Array.Empty<InputManager.HookTriggerKey>());
            InputManager.SetLayerGatedHookRows(Array.Empty<InputManager.HookRowKey>());
            _hooks.SetSuppressedKeys(new HashSet<int> { VkF6 });
            _hooks.SetSuppressedMouseButtons(new HashSet<int> { X2 });
        }

        public void Dispose()
        {
            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            _hooks.SetSuppressedKeys(new HashSet<int>());
            _hooks.SetSuppressedMouseButtons(new HashSet<int>());
        }

        private static bool Key(bool down)
            => InputHookManager.HandleKeyboardEvent(VkF6, ScanF6, 0, IntPtr.Zero, down);

        private static bool Tap()
        {
            bool down = Key(true);
            bool up = Key(false);
            Assert.Equal(down, up);
            return down;
        }

        private static bool Click()
        {
            bool down = InputHookManager.HandleMouseButton(X2, true, IntPtr.Zero, 0);
            bool up = InputHookManager.HandleMouseButton(X2, false, IntPtr.Zero, 0);
            Assert.Equal(down, up);
            return down;
        }

        [Fact]
        public void AStaticallyConsumedKey_PassesWhileNothingIsEvaluated()
        {
            InputManager.PublishLayerGatedHookKeys(evaluating: true);
            Assert.True(Tap());
            Assert.True(Click());

            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            Assert.False(Tap());
            Assert.False(Click());

            InputManager.PublishLayerGatedHookKeys(evaluating: true);
            Assert.True(Tap());
            Assert.True(Click());
        }

        /// <summary>A key eaten before the pause keeps its up eaten, and one
        /// that went to Windows during the pause keeps its up going there.</summary>
        [Fact]
        public void AKeyHeldAcrossThePause_KeepsItsPairing()
        {
            InputManager.PublishLayerGatedHookKeys(evaluating: true);
            Assert.True(Key(true));
            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            Assert.True(Key(true)); // a repeat goes the down's way
            Assert.True(Key(false));
            Assert.False(Tap());

            Assert.False(Key(true));
            Assert.False(InputHookManager.HandleMouseButton(X2, true, IntPtr.Zero, 0));
            InputManager.PublishLayerGatedHookKeys(evaluating: true);
            Assert.False(Key(true));
            Assert.False(Key(false));
            Assert.False(InputHookManager.HandleMouseButton(X2, false, IntPtr.Zero, 0));
            Assert.True(Tap());
        }
    }

    /// <summary>
    /// The low-level hooks eat a key or mouse button that mapping rows read
    /// only while one of those rows is the one the engine evaluates for its
    /// target: the row on the engaged layer, or a Base row the layer lets
    /// through (FindActiveRowForTarget, ApplyMappingSetToGamepad). Before,
    /// Consume Mapped Inputs collected every row of every layer into the
    /// static set, so a key read only by a row on "Shift" was dead on Base,
    /// where no row reads it.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class HookRowLayerGateTests : IDisposable
    {
        private const int Slot = 0;
        private static readonly Guid PadGuid = new("55555555-5555-5555-5555-555555555555");
        private static readonly Guid KeyboardGuid = new("66666666-6666-6666-6666-666666666666");
        private static readonly Guid MouseGuid = new("77777777-7777-7777-7777-777777777777");

        private const int VkF1 = 0x70, VkF2 = 0x71, VkF3 = 0x72, VkF4 = 0x73, VkF5 = 0x74;
        private const uint ScanF1 = 0x3B, ScanF2 = 0x3C, ScanF3 = 0x3D, ScanF4 = 0x3E, ScanF5 = 0x3F;
        private const int X1 = 3;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = SettingsManager.SlotMappingSets;
        private readonly bool[] _created = (bool[])SettingsManager.SlotCreated.Clone();
        private readonly InputHookManager _hooks = new();
        private readonly MappingSet _set = new();
        private readonly UserDevice _keyboard, _mouse;

        public HookRowLayerGateTests()
        {
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotMappingSets = new MappingSet[InputManager.MaxPads];
            Array.Clear(SettingsManager.SlotCreated);
            SettingsManager.SlotCreated[Slot] = true;
            InputManager.ClearAllShiftRuntime();
            SettingsManager.SlotMappingSets[Slot] = _set;
            _keyboard = Device(KeyboardGuid, InputDeviceType.Keyboard);
            _mouse = Device(MouseGuid, InputDeviceType.Mouse);
        }

        public void Dispose()
        {
            InputManager.SetLayerGatedHookRows(Array.Empty<InputManager.HookRowKey>());
            InputManager.PublishLayerGatedHookKeys(evaluating: false);
            _hooks.SetSuppressedKeys(new HashSet<int>());
            _hooks.SetSuppressedMouseButtons(new HashSet<int>());
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotMappingSets = _sets;
            Array.Copy(_created, SettingsManager.SlotCreated, _created.Length);
        }

        private static UserDevice Device(Guid id, int capType)
        {
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = "Device", InstanceName = "Device", CapType = capType,
            };
            SettingsManager.UserDevices.Items.Add(ud);
            var setting = new UserSetting { InstanceGuid = id, ProductGuid = id, MapTo = Slot };
            setting.SetPadSetting(new PadSetting());
            SettingsManager.UserSettings.Items.Add(setting);
            return ud;
        }

        private void Layer(bool inheritUnmapped)
            => _set.ShiftActivators.Add(new ShiftActivator
            {
                DeviceGuid = PadGuid.ToString(),
                Descriptor = "Button 9",
                LayerMask = "Shift",
                LayerName = "Shift",
                Mode = "Hold",
                InheritUnmapped = inheritUnmapped,
            });

        private MappingRow Row(string target, string layer, Guid device, string descriptor)
        {
            var row = new MappingRow
            {
                Target = target, LayerMask = layer,
                Sources = new List<MappingSource> { new() { Descriptor = descriptor, DeviceGuid = device.ToString() } },
            };
            _set.Rows.Add(row);
            return row;
        }

        /// <summary>The apply's collection for both devices, handed to the
        /// hooks and the poll thread the way ApplyInputHooks does.</summary>
        private (HashSet<int> Keys, List<InputManager.HookRowKey> Gated) Apply()
        {
            var keys = new HashSet<int>();
            var buttons = new HashSet<int>();
            var gated = new List<InputManager.HookRowKey>();
            InputService.CollectSuppressedInputs(_keyboard, keys, buttons, gated);
            InputService.CollectSuppressedInputs(_mouse, keys, buttons, gated);
            _hooks.SetSuppressedKeys(keys);
            _hooks.SetSuppressedMouseButtons(buttons);
            InputManager.SetLayerGatedHookRows(gated.ToArray());
            return (keys, gated);
        }

        private void HoldShift(bool held)
        {
            var st = new CustomInputState();
            st.Buttons[9] = held;
            InputManager.ResolveActiveLayerMask(Slot, _set, st, PadGuid.ToString());
            InputManager.PublishLayerGatedHookKeys(evaluating: true);
        }

        private static bool Key(int vk, uint scan, bool down)
            => InputHookManager.HandleKeyboardEvent(vk, scan, 0, IntPtr.Zero, down);

        private static bool Tap(int vk, uint scan)
        {
            bool down = Key(vk, scan, true);
            bool up = Key(vk, scan, false);
            Assert.Equal(down, up);
            return down;
        }

        private static bool Click(int button)
        {
            bool down = InputHookManager.HandleMouseButton(button, true, IntPtr.Zero, 0);
            bool up = InputHookManager.HandleMouseButton(button, false, IntPtr.Zero, 0);
            Assert.Equal(down, up);
            return down;
        }

        /// <summary>F1 and X1 are read only by rows on Shift, which replaces
        /// Base. They type on Base and are eaten on Shift. F2 is read by a
        /// Base row, eaten on Base and typed on Shift, since Shift replaces
        /// Base and no row reads F2 there.</summary>
        [Fact]
        public void AKeyOnlyALayerRowReads_IsEatenOnlyWhileThatLayerIsEngaged()
        {
            Layer(inheritUnmapped: false);
            Row("ButtonA", "Shift", KeyboardGuid, "Button " + VkF1);
            Row("ButtonB", "Shift", MouseGuid, "Button " + X1);
            Row("ButtonX", "Base", KeyboardGuid, "Button " + VkF2);
            var (keys, _) = Apply();
            Assert.DoesNotContain(VkF1, keys);

            HoldShift(false);
            Assert.False(Tap(VkF1, ScanF1));
            Assert.False(Click(X1));
            Assert.True(Tap(VkF2, ScanF2));

            HoldShift(true);
            Assert.True(Tap(VkF1, ScanF1));
            Assert.True(Click(X1));
            Assert.False(Tap(VkF2, ScanF2));

            HoldShift(false);
            Assert.False(Tap(VkF1, ScanF1));
            Assert.False(Click(X1));
            Assert.True(Tap(VkF2, ScanF2));
        }

        /// <summary>On a layer that inherits unmapped targets, a Base row
        /// stays live for every target the layer leaves alone, so its key
        /// is eaten on every layer and sits in the static set. A Base row
        /// whose target the layer maps is replaced there: its key types on
        /// the layer, and the layer row's key is eaten.</summary>
        [Fact]
        public void ABaseRowsKey_FollowsTheLayersInheritance()
        {
            Layer(inheritUnmapped: true);
            Row("ButtonX", "Base", KeyboardGuid, "Button " + VkF2);
            Row("ButtonY", "Base", KeyboardGuid, "Button " + VkF3);
            Row("ButtonY", "Shift", KeyboardGuid, "Button " + VkF4);
            var (keys, gated) = Apply();
            Assert.Contains(VkF2, keys);
            Assert.DoesNotContain(gated, g => g.Index == VkF2);

            HoldShift(false);
            Assert.True(Tap(VkF2, ScanF2));
            Assert.True(Tap(VkF3, ScanF3));
            Assert.False(Tap(VkF4, ScanF4));

            HoldShift(true);
            Assert.True(Tap(VkF2, ScanF2));
            Assert.False(Tap(VkF3, ScanF3));
            Assert.True(Tap(VkF4, ScanF4));
        }

        /// <summary>The Up key of an Incremental source and the modifier of
        /// an Invert on Hold source are read with their row, so they follow
        /// its layer too.</summary>
        [Fact]
        public void ALayerRowsUpKeyAndModifier_FollowTheRowsLayer()
        {
            Layer(inheritUnmapped: false);
            var row = Row("LeftThumbAxisX", "Shift", KeyboardGuid, "");
            row.Sources[0].Kind = "Incremental";
            row.Sources[0].ParamUp = "Button " + VkF1;
            row.Sources[0].ParamUpDeviceGuid = KeyboardGuid.ToString();
            row.Sources.Add(new MappingSource
            {
                Kind = "InvertOnHold", DeviceGuid = KeyboardGuid.ToString(),
                ParamModifier = "Button " + VkF5, ParamModifierDeviceGuid = KeyboardGuid.ToString(),
            });
            Apply();

            HoldShift(false);
            Assert.False(Tap(VkF1, ScanF1));
            Assert.False(Tap(VkF5, ScanF5));
            HoldShift(true);
            Assert.True(Tap(VkF1, ScanF1));
            Assert.True(Tap(VkF5, ScanF5));
        }

        /// <summary>F1 was eaten on Shift and Shift ended while it was held:
        /// its up is eaten too, and the next press types on Base.</summary>
        [Fact]
        public void AKeyHeldWhileItsLayerEnds_KeepsItsUpEaten()
        {
            Layer(inheritUnmapped: false);
            Row("ButtonA", "Shift", KeyboardGuid, "Button " + VkF1);
            Apply();

            HoldShift(true);
            Assert.True(Key(VkF1, ScanF1, true));
            HoldShift(false);
            Assert.True(Key(VkF1, ScanF1, true)); // a repeat goes the down's way
            Assert.True(Key(VkF1, ScanF1, false));
            Assert.False(Tap(VkF1, ScanF1));
        }

        /// <summary>A reWASD unlock layer hands the keyboard back to Windows.
        /// Imported, A, which Base maps to the pad's A, is eaten on Base and
        /// types on the unlock layer, where the layer's A row drops the key.
        /// B, which Base unmaps, is held back on Base and types on the unlock
        /// layer, which replaces Base and takes no copy of B's block. The
        /// mouse is not unlocked: X1, which Base unmaps, stays held back on
        /// the layer through its copy of the block, and the pad's own X
        /// mapping still runs there.</summary>
        [Fact]
        public void AnImportedUnlockLayer_HandsTheKeyboardBack_AndKeepsTheMouse()
        {
            const int VkA = 0x41, VkB = 0x42;
            const uint ScanA = 0x1E, ScanB = 0x30;
            const int MouseX1 = 3;
            var r = RewasdImportTests.Import(RewasdImportTests.File(
                shifts: """[{"id":1,"type":"unlockKeyboardOnly"}]""",
                masks: "[" + RewasdImportTests.Mask(1, 11, 30) + "," + RewasdImportTests.Mask(2, 1, 2) + ","
                    + RewasdImportTests.Mask(3, 1, 5) + "]",
                mappings: "[" + RewasdImportTests.Jump(3, 1) + ","
                    + RewasdImportTests.Map(1, "[{\"gamepad\":{\"id\":1,\"buttonId\":1}}]") + ","
                    + RewasdImportTests.Map(2, "[{\"gamepad\":{\"id\":1,\"buttonId\":3}}]") + ","
                    + """{"condition":{"hardware":{"deviceId":11,"buttonId":48,"description":"DIK_B"}},"hardware":{"unmap":true}},"""
                    + """{"condition":{"hardware":{"deviceId":23,"buttonId":4}},"hardware":{"unmap":true}}]"""));
            var set = r.Profile.SlotMappingSets[Slot];
            SettingsManager.SlotMappingSets[Slot] = set;
            var keyboard = Device(Guid.Parse(PadForge.Common.ConfigImport.ImportBuilder.AllKeyboardsGuid), InputDeviceType.Keyboard);
            var mouse = Device(Guid.Parse(PadForge.Common.ConfigImport.ImportBuilder.AllMiceGuid), InputDeviceType.Mouse);
            var macros = r.Profile.Macros
                .Select(md => SettingsService.LoadMacroFromData(md, VirtualControllerType.Xbox, null)).ToArray();
            Assert.Contains(set.Rows, row => row.Target == "ButtonX" && InputManager.RowReadsUnder(set, row, "Shift1"));

            var keys = new HashSet<int>();
            var buttons = new HashSet<int>();
            var rows = new List<InputManager.HookRowKey>();
            var gatedMacros = new List<InputManager.HookTriggerKey>();
            foreach (var device in new[] { keyboard, mouse })
            {
                InputService.CollectSuppressedInputs(device, keys, buttons, rows);
                InputService.CollectMacroTriggerKeys(device, slot => slot == Slot ? macros : null, keys, buttons, gatedMacros);
            }
            _hooks.SetSuppressedKeys(keys);
            _hooks.SetSuppressedMouseButtons(buttons);
            InputManager.SetLayerGatedHookRows(rows.ToArray());
            InputManager.SetLayerGatedHookKeys(gatedMacros.ToArray());
            try
            {
                InputManager.ApplyMacroLayerSwitch(Slot, "Base");
                InputManager.PublishLayerGatedHookKeys(evaluating: true);
                Assert.True(Tap(VkA, ScanA));
                Assert.True(Tap(VkB, ScanB));
                Assert.True(Click(MouseX1));

                InputManager.ApplyMacroLayerSwitch(Slot, "Shift1");
                Assert.Equal("Shift1", InputManager.GetEngagedLayerMask(Slot, set));
                InputManager.PublishLayerGatedHookKeys(evaluating: true);
                Assert.False(Tap(VkA, ScanA));
                Assert.False(Tap(VkB, ScanB));
                Assert.True(Click(MouseX1));
            }
            finally
            {
                InputManager.SetLayerGatedHookKeys(Array.Empty<InputManager.HookTriggerKey>());
            }
        }

        /// <summary>The legacy PadSetting fields project the Base rows, and
        /// the engine reads one only where the slot has no row for its
        /// target. A Base row's key that a legacy field repeats follows the
        /// row, so a replacing layer still passes it.</summary>
        [Fact]
        public void ALegacyFieldThatRepeatsABaseRow_FollowsTheRow()
        {
            Layer(inheritUnmapped: false);
            Row("ButtonX", "Base", KeyboardGuid, "Button " + VkF2);
            SettingsManager.FindSettingByInstanceGuidAndSlot(KeyboardGuid, Slot).GetPadSetting().ButtonX = "Button " + VkF2;
            Apply();

            HoldShift(false);
            Assert.True(Tap(VkF2, ScanF2));
            HoldShift(true);
            Assert.False(Tap(VkF2, ScanF2));
        }
    }
}
