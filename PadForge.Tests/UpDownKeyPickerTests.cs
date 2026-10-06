using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Engine;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Data;
using PadForge.Services;
using PadForge.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PadForge.Tests
{
    /// <summary>
    /// The Up and Down pickers of an Incremental or Ramp source list only
    /// inputs their reader takes as a key. Both kinds read discrete inputs by
    /// design, and the Record button already refused a stick for them, but
    /// the two dropdowns showed the slot's whole input list. A stick, a
    /// trigger or another axis picked there read released forever.
    ///
    /// <para>The list is tested against the reader itself: every entry the
    /// pickers offer moves an Incremental value under some input state, and
    /// no entry they leave out moves it under any.</para>
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public sealed class UpDownKeyPickerTests : IDisposable
    {
        private const int Slot = 6;
        private const string AnyPad = "ad000000-0000-0000-0000-0000000000ad";

        private readonly ITestOutputHelper _output;
        private readonly Func<int, string, int, string, bool> _savedFired = SourceCoercion.TouchpadGestureFiredProvider;
        private readonly Func<int, string, int, string, float> _savedAxis = SourceCoercion.TouchpadGestureAxisProvider;
        private readonly Func<int, string, string, bool> _savedMouse = SourceCoercion.MouseGestureFiredProvider;
        private readonly Func<int, string, int, int, bool> _savedMenu = SourceCoercion.MenuItemFiredProvider;

        public UpDownKeyPickerTests(ITestOutputHelper output) => _output = output;

        public void Dispose()
        {
            SourceCoercion.TouchpadGestureFiredProvider = _savedFired;
            SourceCoercion.TouchpadGestureAxisProvider = _savedAxis;
            SourceCoercion.MouseGestureFiredProvider = _savedMouse;
            SourceCoercion.MenuItemFiredProvider = _savedMenu;
            // The reads below ask for the NFC, IR and Ring-Con data, and each
            // ask latches a demand a live engine would power a Joy-Con for.
            SourceCoercion.ResetMcuDemandLatches();
        }

        // ── The reader, driven with everything held ──

        /// <summary>Every gesture is fired, every gesture axis is pushed and
        /// every menu cell is open, on any slot and device.</summary>
        private static void FireEverySlotEvent()
        {
            SourceCoercion.TouchpadGestureFiredProvider = (slot, guid, pad, name) => true;
            SourceCoercion.TouchpadGestureAxisProvider = (slot, guid, pad, name) => 1f;
            SourceCoercion.MouseGestureFiredProvider = (slot, guid, name) => true;
            SourceCoercion.MenuItemFiredProvider = (slot, guid, menu, item) => true;
        }

        private static readonly float[] Grid = { 0.1f, 0.3f, 0.5f, 0.7f, 0.9f };
        private static readonly int[] HatAngles = { 0, 9000, 18000, 27000 };

        /// <summary>Twenty-five states with everything held. What a key can
        /// depend on changes from one to the next: the hat's direction, which
        /// way the Ring-Con flexes, the end each axis rests at, and where the
        /// fingers sit, over a grid that reaches every touchpad window.</summary>
        private static IEnumerable<CustomInputState> EverythingHeld(string descriptor)
        {
            int i = 0;
            foreach (float x in Grid)
            foreach (float y in Grid)
            {
                var s = new CustomInputState();
                Array.Fill(s.Buttons, true);
                Array.Fill(s.Axis, (i & 1) == 0 ? 65535 : 0);
                Array.Fill(s.Sliders, (i & 1) == 0 ? 65535 : 0);
                Array.Fill(s.Povs, HatAngles[i % HatAngles.Length]);
                for (int a = 0; a < 3; a++)
                {
                    s.Gyro[a] = s.GyroAux[a] = 20f;
                    s.Accel[a] = s.AccelAux[a] = 20f;
                }
                s.CapSense = new[] { true, true, true, true };
                s.NfcTag = Enumerable.Repeat(true, 256).ToArray();
                s.JoyConIrIntensity = 1f;
                s.RingConStrain = (i & 1) == 0 ? 1f : -1f;
                s.JoyCon2MouseDX = s.JoyCon2MouseDY = 50f;
                s.Ir = new WiiIrState { X = 1f, Y = 1f, Detected = (i & 1) == 0 };

                s.Touchpads = new TouchpadInputState[3];
                for (int p = 0; p < s.Touchpads.Length; p++)
                {
                    var pad = new TouchpadInputState(5) { Clicked = true };
                    for (int f = 0; f < pad.MaxFingers; f++)
                    {
                        pad.FingerDown[f] = true;
                        pad.FingerX[f] = x;
                        pad.FingerY[f] = y;
                        pad.FingerPressure[f] = 1f;
                        pad.FingerContactId[f] = f;
                    }
                    s.Touchpads[p] = pad;
                }

                s.Midi = new MidiInputState { PitchBend = 65535 };
                Array.Fill(s.Midi.Notes, true);
                Array.Fill(s.Midi.Cc, (byte)127);
                Array.Fill(s.Midi.CcUp, true);
                Array.Fill(s.Midi.CcDown, true);

                s.AnalogKeys = new AnalogKeyInputState();
                if (SourceCoercion.TryParseAnalogKey(descriptor, out int code))
                    s.AnalogKeys.Set(code, 1f);

                yield return s;
                i++;
            }
        }

        /// <summary>True when the descriptor, picked as the Up key of an
        /// Incremental source, moves the value under some state.</summary>
        private static bool CanMoveTheValue(string descriptor, string deviceGuid)
        {
            FireEverySlotEvent();
            foreach (var state in EverythingHeld(descriptor))
            {
                var rt = new SourceKindRuntime();
                var src = new MappingSource
                {
                    Kind = "Incremental", DeviceGuid = deviceGuid ?? "", ParamUp = descriptor, ParamDown = "",
                    ParamRate = 1.0, ParamMin = 0, ParamMax = 1, ParamSticky = true,
                };
                rt.FrameSeq++;
                string evaluated = string.IsNullOrEmpty(deviceGuid) ? AnyPad : deviceGuid;
                if (rt.TickIncremental(Slot, "LeftTrigger", 0, src, state, 0.05, evaluated) > 0)
                    return true;
            }
            return false;
        }

        // ── The check, family by family ──

        [Theory]
        // Buttons and hat directions, numbered and by their Gamepad names.
        [InlineData("Button 0", true)]
        [InlineData("Button 255", true)]
        [InlineData("Button 256", false)]
        [InlineData("Button -1", false)]
        [InlineData("Button x", false)]
        [InlineData("POV 0 Up", true)]
        [InlineData("POV 3 Left", true)]
        [InlineData("POV 4 Up", false)]
        [InlineData("POV 0 Sideways", false)]
        [InlineData("POV 0", false)]
        [InlineData("Gamepad ButtonA", true)]
        [InlineData("Gamepad DPadDown", true)]
        // Slot events.
        [InlineData("Touchpad 0 SwipeUp", true)]
        [InlineData("Touchpad 1 Custom_Circle", true)]
        [InlineData("Touchpad 0 PinchAxis", true)]
        [InlineData("Mouse Gesture Left", true)]
        [InlineData("Menu 3 Item 1", true)]
        // The hardware-bool families.
        [InlineData("Touchpad 0 Click", true)]
        [InlineData("Touchpad 2 Click", true)]
        [InlineData("Touchpad 0 Click Upper", true)]
        [InlineData("Touchpad 0 Finger 0 Down", true)]
        [InlineData("Touchpad 1 Finger 1 Down Left", true)]
        [InlineData("Touchpad 0 Finger 0 Down North Right", true)]
        [InlineData("Gamepad LeftStickTouch", true)]
        [InlineData("Gamepad RightGripTouch", true)]
        [InlineData("Any NFC Tag", true)]
        [InlineData("NFC Tag 3", true)]
        [InlineData("Any Voice Phrase", true)]
        [InlineData("Voice Phrase 2", true)]
        [InlineData("IR Brightness", true)]
        [InlineData("Ring-Con Squeeze", true)]
        [InlineData("Ring-Con Pull", true)]
        [InlineData("Analog Key 30", true)]
        // What a key never reads: the analog inputs.
        [InlineData("Axis 0", false)]
        [InlineData("Axis 5", false)]
        [InlineData("Slider 0", false)]
        [InlineData("Gamepad LeftTrigger", false)]
        [InlineData("Gamepad RightStickX", false)]
        [InlineData("Gamepad LeftStickRing", false)]
        [InlineData("Gyro Pitch", false)]
        [InlineData("Motion Lean", false)]
        [InlineData("Motion Shake", false)]
        [InlineData("Mouse Motion X", false)]
        [InlineData("Mouse Position Y", false)]
        [InlineData("IR Pointer X", false)]
        [InlineData("Balance Lean X", false)]
        [InlineData("Touchpad 0 Finger 0 X", false)]
        [InlineData("Touchpad 0 Finger 0 Pressure", false)]
        [InlineData("Touchpad 0 Finger 0 Ring", false)]
        [InlineData("Touchpad 0 Pointer X", false)]
        [InlineData("Flick Stick Touchpad 0", false)]
        [InlineData("Midi CC 7", false)]
        [InlineData("Midi Pitch Bend", false)]
        // Discrete inputs the reader has no branch for. They are left out
        // with the axes, since a key picked from them would never read.
        [InlineData("Midi Note 60", false)]
        [InlineData("Midi CC 7 Up", false)]
        [InlineData("IR Offscreen", false)]
        // Windows and forms outside the touchpad grammar.
        [InlineData("Touchpad 0 Click Sideways", false)]
        [InlineData("Touchpad 0 Click Left Right", false)]
        [InlineData("Touchpad 0 Finger 0 Down Upper Left", false)]
        [InlineData("Touchpad 0 Finger 0 Down Sideways", false)]
        [InlineData("Touchpad -1 Click", false)]
        [InlineData("Touchpad x Click", false)]
        [InlineData("Touchpad 0 Finger -1 Down", false)]
        [InlineData("Touchpad 0 Finger x Down", false)]
        // Nothing picked.
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void TheCheckAgreesWithTheReader(string descriptor, bool readsAsKey)
        {
            Assert.Equal(readsAsKey, SourceKindRuntime.ReadsAsKey(descriptor));
            Assert.Equal(readsAsKey, CanMoveTheValue(descriptor, AnyPad));
        }

        // ── The slot's list ──

        private static UserDevice Device(string name, int type, ushort vendor = 0, ushort product = 0,
            Action<UserDevice> more = null)
        {
            var id = Guid.NewGuid();
            var ud = new UserDevice
            {
                InstanceGuid = id, ProductGuid = id, IsOnline = true,
                ProductName = name, InstanceName = name,
                CapType = type, VendorId = vendor, ProdId = product,
                InputState = new CustomInputState(),
            };
            more?.Invoke(ud);
            return ud;
        }

        private static void Pad(UserDevice d, int buttons, int axes, int hats)
        {
            d.CapButtonCount = buttons;
            d.CapAxeCount = axes;
            d.CapPovCount = hats;
        }

        private static void Motion(UserDevice d)
            => d.HasGyro = d.HasAccel = d.HasGyroAux = d.HasAccelAux = true;

        /// <summary>One device for each input family the picker list can
        /// carry.</summary>
        private static UserDevice[] Devices() => new[]
        {
            Device("Two Touchpad Pad", InputDeviceType.Gamepad, more: d =>
            {
                Pad(d, 16, 6, 1); Motion(d);
                d.HasTouchpad = true; d.CapTouchpadCount = 2;
            }),
            // A DualSense: one touchpad, so the windowed forms, and voice phrases.
            Device("DualSense Wireless Controller", InputDeviceType.Gamepad, 0x054C, 0x0CE6, d =>
            {
                Pad(d, 16, 6, 1); Motion(d);
                d.HasTouchpad = true; d.CapTouchpadCount = 1;
            }),
            // A Joy-Con pair: the IR camera's brightness, the Ring-Con, NFC.
            Device("Nintendo Switch Joy-Con (L/R)", InputDeviceType.Gamepad, 0x057E, 0x2008, d =>
            {
                Pad(d, 16, 6, 1); Motion(d);
            }),
            Device("Nintendo Wii Remote", InputDeviceType.Gamepad, 0x057E, 0x0306, d => { Pad(d, 11, 0, 1); d.HasAccel = true; }),
            Device("Nintendo Wii Balance Board", InputDeviceType.Gamepad, 0x057E, 0x0306, d => Pad(d, 1, 0, 0)),
            Device("Nintendo Switch 2 Joy-Con (R)", InputDeviceType.Gamepad, 0x057E, 0x2066, d => { Pad(d, 16, 2, 0); Motion(d); }),
            Device("GunCon 2", InputDeviceType.Gamepad, 0x0B9A, 0x016A, d => Pad(d, 8, 0, 1)),
            Device("Flight Stick", InputDeviceType.Joystick, more: d => Pad(d, 32, 8, 2)),
            Device("Mouse", InputDeviceType.Mouse, more: d => Pad(d, 5, 3, 0)),
            Device("Keyboard", InputDeviceType.Keyboard, more: d => Pad(d, 256, 0, 0)),
            Device("MIDI Keys", InputDeviceType.Midi),
        };

        private sealed class Populated
        {
            public UserDevice Device;
            public PadViewModel Pad;
        }

        /// <summary>The slot's picker lists as the app builds them, once for
        /// each device.</summary>
        private static List<Populated> PopulateEachDevice()
        {
            var result = new List<Populated>();
            foreach (var ud in Devices())
            {
                SettingsManager.UserDevices = new DeviceCollection();
                SettingsManager.UserSettings = new SettingsCollection();
                SettingsManager.UserDevices.Items.Add(ud);
                var vm = new MainViewModel();
                var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
                var pad = vm.Pads[0];
                pad.OutputType = VirtualControllerType.PlayStation;
                typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(svc, new object[] { pad, ud });
                result.Add(new Populated { Device = ud, Pad = pad });
            }
            return result;
        }

        private static void WithCleanSettings(Action body)
        {
            var savedDevices = SettingsManager.UserDevices;
            var savedSettings = SettingsManager.UserSettings;
            try { body(); }
            finally
            {
                SettingsManager.UserDevices = savedDevices;
                SettingsManager.UserSettings = savedSettings;
            }
        }

        /// <summary>The contract, on the lists the app builds: an entry is in
        /// the Up and Down pickers exactly when the reader can read it.</summary>
        [Fact]
        public void EveryOfferedKeyCanMoveTheValueAndNothingLeftOutCan() => WithCleanSettings(() =>
        {
            var offeredButDead = new List<string>();
            var readableButMissing = new List<string>();
            var leftOut = new SortedSet<string>(StringComparer.Ordinal);
            int offered = 0;

            foreach (var p in PopulateEachDevice())
            {
                Assert.NotEmpty(p.Pad.SlotAvailableInputs);
                var keys = new HashSet<InputChoice>(p.Pad.SlotKeyInputs);
                foreach (var c in p.Pad.SlotAvailableInputs)
                {
                    bool inKeys = keys.Contains(c);
                    bool moves = CanMoveTheValue(c.Descriptor, c.DeviceGuid);
                    if (inKeys) offered++;
                    else leftOut.Add(Regex.Replace(c.Descriptor, @"\d+", "N"));
                    if (inKeys && !moves) offeredButDead.Add($"{p.Device.ProductName}: {c.Descriptor}");
                    if (!inKeys && moves) readableButMissing.Add($"{p.Device.ProductName}: {c.Descriptor}");
                }
            }

            _output.WriteLine("Left out of the Up and Down pickers: " + string.Join(", ", leftOut));
            Assert.True(offeredButDead.Count == 0,
                "Offered as a key and never read: " + string.Join(" | ", offeredButDead));
            Assert.True(readableButMissing.Count == 0,
                "Read as a key and not offered: " + string.Join(" | ", readableButMissing));
            Assert.True(offered > 500, $"Only {offered} keys were offered across the devices.");
        });

        /// <summary>What the report named: sticks, triggers and other axes.</summary>
        [Fact]
        public void TheUpAndDownPickersLeaveOutSticksTriggersAndAxes() => WithCleanSettings(() =>
        {
            foreach (var p in PopulateEachDevice())
            {
                var keys = p.Pad.SlotKeyInputs;
                Assert.DoesNotContain(keys, c => c.Descriptor.StartsWith("Axis ", StringComparison.Ordinal));
                Assert.DoesNotContain(keys, c => c.Descriptor.StartsWith("Slider ", StringComparison.Ordinal));
                Assert.DoesNotContain(keys, c => c.Descriptor.StartsWith("Gyro ", StringComparison.Ordinal));
                Assert.DoesNotContain(keys, c => SourceCoercion.IsGamepadAxisAlias(c.Descriptor));
                Assert.DoesNotContain(keys, c => c.Descriptor.EndsWith("StickRing", StringComparison.Ordinal));
                Assert.DoesNotContain(keys, c => c.Descriptor.StartsWith("Mouse Motion", StringComparison.Ordinal));
                Assert.DoesNotContain(keys, c => c.Descriptor.StartsWith("Mouse Position", StringComparison.Ordinal));
            }

            var pad = PopulateEachDevice()[0];
            string key = pad.Device.InstanceGuid.ToString().ToLowerInvariant();
            // The full list still carries them, for the modifier picker and
            // every other row.
            Assert.Contains(pad.Pad.SlotAvailableInputs, c => c.Descriptor == "Axis 0" && c.DeviceGuid == key);
            Assert.Contains(pad.Pad.SlotAvailableInputs, c => c.Descriptor == "Gyro Pitch" && c.DeviceGuid == key);
            // The keys a pad has are all there.
            foreach (string d in new[] { "Button 0", "Button 15", "POV 0 Up", "POV 0 Left", "Touchpad 0 Click", "Touchpad 1 Finger 0 Down" })
                Assert.Contains(pad.Pad.SlotKeyInputs, c => c.Descriptor == d && c.DeviceGuid == key);
            // So are the device-agnostic names for them.
            Assert.Contains(pad.Pad.SlotKeyInputs, c => c.Descriptor == "Gamepad ButtonA" && c.DeviceGuid == "");
            Assert.DoesNotContain(pad.Pad.SlotKeyInputs, c => c.Descriptor == "Gamepad LeftTrigger");
        });

        /// <summary>The key list holds the full list's own entries, in its
        /// order, so the item a row resolves from the full list is the item
        /// its picker holds.</summary>
        [Fact]
        public void TheKeyListHoldsTheFullListsOwnEntriesInOrder() => WithCleanSettings(() =>
        {
            foreach (var p in PopulateEachDevice())
            {
                var expected = p.Pad.SlotAvailableInputs.Where(InputService.IsUpDownKeyChoice).ToList();
                Assert.Equal(expected.Count, p.Pad.SlotKeyInputs.Count);
                for (int i = 0; i < expected.Count; i++)
                    Assert.Same(expected[i], p.Pad.SlotKeyInputs[i]);
            }
        });

        /// <summary>The lists are rebuilt whenever the slot's devices or
        /// gestures change. A second build leaves the key list as the
        /// first did, with nothing carried over.</summary>
        [Fact]
        public void RebuildingTheListsLeavesNoStaleKeys() => WithCleanSettings(() =>
        {
            var ud = Devices()[0];
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices.Items.Add(ud);
            var vm = new MainViewModel();
            var svc = new InputService(vm) { SettingsService = new SettingsService(vm) };
            var pad = vm.Pads[0];
            pad.OutputType = VirtualControllerType.PlayStation;
            var populate = typeof(InputService).GetMethod("PopulateAvailableInputs", BindingFlags.Instance | BindingFlags.NonPublic);

            populate.Invoke(svc, new object[] { pad, ud });
            var first = pad.SlotKeyInputs.Select(c => c.DeviceGuid + "|" + c.Descriptor).ToList();
            var firstItems = pad.SlotKeyInputs.ToList();
            populate.Invoke(svc, new object[] { pad, ud });

            Assert.Equal(first, pad.SlotKeyInputs.Select(c => c.DeviceGuid + "|" + c.Descriptor));
            Assert.All(pad.SlotKeyInputs, c => Assert.Contains(c, pad.SlotAvailableInputs));
            Assert.All(firstItems, c => Assert.DoesNotContain(c, pad.SlotKeyInputs));
        });

        /// <summary>Every row of the slot points at the one key list, the
        /// Motion rows included, and the modifier picker keeps the full
        /// list: an Invert on Hold modifier takes the full Direct read, so a
        /// stick or a trigger works there.</summary>
        [Fact]
        public void EveryRowSharesTheKeyListAndTheModifierKeepsTheFullOne() => WithCleanSettings(() =>
        {
            var pad = PopulateEachDevice()[0].Pad;
            Assert.NotEmpty(pad.Mappings);
            foreach (var row in pad.Mappings)
            {
                Assert.Same(pad.SlotKeyInputs, row.KeyInputs);
                Assert.Same(pad.SlotAvailableInputs, row.ParamInputs);
                Assert.Same(CollectionViewSource.GetDefaultView(pad.SlotKeyInputs), row.KeyInputsView);
            }
            var view = pad.Mappings[0].KeyInputsView;
            Assert.Single(view.GroupDescriptions);
            Assert.Equal(nameof(InputChoice.DeviceLabel),
                ((PropertyGroupDescription)view.GroupDescriptions[0]).PropertyName);
        });

        /// <summary>The search box and the device filter narrow the Up and
        /// Down pickers as they narrow every other picker.</summary>
        [Fact]
        public void TheDeviceFilterAndTheSearchReachTheKeyList() => WithCleanSettings(() =>
        {
            var p = PopulateEachDevice()[0];
            string key = p.Device.InstanceGuid.ToString().ToLowerInvariant();
            var view = CollectionViewSource.GetDefaultView(p.Pad.SlotKeyInputs);
            Assert.Contains(view.Cast<InputChoice>(), c => c.DeviceGuid == key);

            p.Pad.HiddenPickerDeviceKeys.Add(key);
            p.Pad.ApplyMappingPickerFilter();
            Assert.DoesNotContain(view.Cast<InputChoice>(), c => c.DeviceGuid == key);
            Assert.Contains(view.Cast<InputChoice>(), c => c.DeviceGuid == "");

            p.Pad.HiddenPickerDeviceKeys.Clear();
            p.Pad.MappingInputSearch = "no input is named this";
            Assert.Empty(view.Cast<InputChoice>());
            p.Pad.MappingInputSearch = "";
            Assert.Contains(view.Cast<InputChoice>(), c => c.DeviceGuid == key);
        });

        /// <summary>A stick saved as a key before the pickers were narrowed
        /// is not in the list, so its picker shows no selection. The saved
        /// value is kept: the picker writing its empty selection back does
        /// not clear it, and picking a key replaces it.</summary>
        [Fact]
        public void ASavedStickKeyShowsNoSelectionAndIsKeptUntilReplaced() => WithCleanSettings(() =>
        {
            var p = PopulateEachDevice()[0];
            string key = p.Device.InstanceGuid.ToString().ToLowerInvariant();
            var row = p.Pad.Mappings.First(m => m.TargetSettingName == "LeftTrigger");
            var src = new MappingSourceItem { Kind = "Incremental", DeviceGuid = key, ParamUp = "Axis 2", ParamDown = "Button 1" };
            row.ExtraSources.Add(src);

            Assert.NotNull(src.ParamUpInputChoice);
            Assert.DoesNotContain(src.ParamUpInputChoice, row.KeyInputs);
            Assert.Contains(src.ParamDownInputChoice, row.KeyInputs);

            src.ParamUpInputChoice = null;
            Assert.Equal("Axis 2", src.ParamUp);

            var button = row.KeyInputs.First(c => c.Descriptor == "Button 4" && c.DeviceGuid == key);
            src.ParamUpInputChoice = button;
            Assert.Equal("Button 4", src.ParamUp);
            Assert.Same(button, src.ParamUpInputChoice);
        });

        /// <summary>WPF elements need an STA thread. The helper other test
        /// classes use.</summary>
        private static void RunSta(Action body)
        {
            Exception failure = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception ex) { failure = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            Assert.True(t.Join(30000), "STA test body timed out");
            if (failure != null) throw failure;
        }

        /// <summary>The dropdown itself, bound as the page binds it. With a
        /// stick saved as the key it shows nothing selected, and the empty
        /// selection it writes back leaves the saved value alone. With a
        /// key saved it shows the key, and a pick from it replaces the
        /// stick.</summary>
        [Fact]
        public void TheDropdownShowsNothingSelectedForASavedStick() => WithCleanSettings(() => RunSta(() =>
        {
            var p = PopulateEachDevice()[0];
            string key = p.Device.InstanceGuid.ToString().ToLowerInvariant();
            var row = p.Pad.Mappings.First(m => m.TargetSettingName == "LeftTrigger");
            var src = new MappingSourceItem { Kind = "Incremental", DeviceGuid = key, ParamUp = "Axis 2", ParamDown = "Button 1" };
            row.ExtraSources.Add(src);

            ComboBox Bound(string field)
            {
                var combo = new ComboBox { DataContext = src, DisplayMemberPath = nameof(InputChoice.DisplayName) };
                combo.SetBinding(ItemsControl.ItemsSourceProperty,
                    new Binding(nameof(MappingItem.KeyInputsView)) { Source = row });
                combo.SetBinding(Selector.SelectedItemProperty,
                    new Binding(field) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                combo.GetBindingExpression(ItemsControl.ItemsSourceProperty).UpdateTarget();
                combo.GetBindingExpression(Selector.SelectedItemProperty).UpdateTarget();
                return combo;
            }

            var up = Bound(nameof(MappingSourceItem.ParamUpInputChoice));
            Assert.True(up.Items.Count > 0);
            Assert.Equal(-1, up.SelectedIndex);
            Assert.Null(up.SelectedItem);
            Assert.Equal("Axis 2", src.ParamUp);

            var down = Bound(nameof(MappingSourceItem.ParamDownInputChoice));
            Assert.Equal("Button 1", ((InputChoice)down.SelectedItem).Descriptor);

            // A rebuild of the slot's lists raises the choice again, as
            // every device change does. The stick is still kept.
            src.RefreshParamPickerChoices();
            Assert.Equal(-1, up.SelectedIndex);
            Assert.Equal("Axis 2", src.ParamUp);

            up.SelectedItem = row.KeyInputs.First(c => c.Descriptor == "Button 4" && c.DeviceGuid == key);
            Assert.Equal("Button 4", src.ParamUp);
            Assert.True(up.SelectedIndex >= 0);
        }));

        /// <summary>A row no slot has filled offers no keys.</summary>
        [Fact]
        public void ARowOutsideASlotOffersNoKeys()
        {
            var row = new MappingItem("Left Trigger", "LeftTrigger", MappingCategory.Triggers);
            Assert.Empty(row.KeyInputs);
            Assert.NotNull(row.KeyInputsView);

            var raised = new List<string>();
            row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var shared = new System.Collections.ObjectModel.ObservableCollection<InputChoice>();
            row.UseSharedKeyInputs(shared);
            Assert.Same(shared, row.KeyInputs);
            Assert.Same(CollectionViewSource.GetDefaultView(shared), row.KeyInputsView);
            Assert.Contains(nameof(MappingItem.KeyInputs), raised);
            Assert.Contains(nameof(MappingItem.KeyInputsView), raised);
        }

        // ── The dropdowns ──

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        /// <summary>The six Up and Down dropdowns bind the key list: the
        /// Incremental pair and the Ramp pair of an added source, and the
        /// pair in the row's own strip. The two modifier dropdowns keep the
        /// full list.</summary>
        [Fact]
        public void TheSixUpAndDownDropdownsBindTheKeyList()
        {
            string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Views", "PadPage.xaml"));
            var bindings = Regex.Matches(xaml,
                    @"ItemsSource=""\{Binding DataContext\.(\w+),\s*RelativeSource=\{RelativeSource AncestorType=\{x:Type DataGridRow\}\}\}""\s*SelectedItem=""\{Binding (Param\w+InputChoice),")
                .Select(m => (List: m.Groups[1].Value, Field: m.Groups[2].Value))
                .ToList();

            Assert.Equal(8, bindings.Count);
            Assert.Equal(3, bindings.Count(b => b.Field == "ParamUpInputChoice"));
            Assert.Equal(3, bindings.Count(b => b.Field == "ParamDownInputChoice"));
            Assert.Equal(2, bindings.Count(b => b.Field == "ParamModifierInputChoice"));
            foreach (var b in bindings)
                Assert.Equal(b.Field == "ParamModifierInputChoice"
                    ? nameof(MappingItem.ParamInputsView)
                    : nameof(MappingItem.KeyInputsView), b.List);
        }
    }
}
