using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;
using PadForge.Common.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine;
using PadForge.Engine.Common;
using PadForge.Engine.Data;
using PadForge.Engine.Menus;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Keyboard menus on the real runtime (#566). Each poll here is Step 2's:
    /// a device's state goes through the keyboard's filter and
    /// UpdateMenuContexts, then the keyboard ticks. Opens With and its press
    /// modes, the layer, text focus, controls on any device, the controllers
    /// held at rest, and where the keys go. The keys land in a record or a
    /// fake device, never on the PC, and the menu clock is held so no test
    /// waits on the system clock.
    /// </summary>
    [Collection("SettingsManagerStatics")]
    public class OnScreenKeyboardMenuTests : IDisposable
    {
        private const int Slot = 2;
        private const string Guide = "Gamepad ButtonGuide";
        private const string Keyboard = "corsair-k55-rgb-pro-xt";
        private const int A = 0, B = 1, X = 2, Y = 3, Back = 6, Start = 7, GuideButton = 10, Paddle1 = 12;

        private readonly SettingsCollection _settings = SettingsManager.UserSettings;
        private readonly DeviceCollection _devices = SettingsManager.UserDevices;
        private readonly MappingSet[] _sets = (MappingSet[])SettingsManager.SlotMappingSets.Clone();
        private readonly bool[] _created = SettingsManager.SlotCreated;
        private readonly InputManager _im = new();
        private readonly OnScreenKeyboardEngineTests.Record _out = new();
        private readonly MenuDefinitionEntry _menu;
        private long _now = 10_000;

        public OnScreenKeyboardMenuTests()
        {
            MenuClock.Source = () => _now;
            InputManager.ClearAllShiftRuntime();
            SettingsManager.UserSettings = new SettingsCollection();
            SettingsManager.UserDevices = new DeviceCollection();
            SettingsManager.SlotCreated = new bool[InputManager.MaxPads];
            OwnHidOutput.ResetForTests();
            _im.OnScreenKeyboardOutputOverride = _out;
            _im.SetOnScreenKeyboardLayout(new OnScreenKeyboardEngineTests.UsLayout());
            _menu = AddMenu(Slot, Guide);
        }

        public void Dispose()
        {
            _im.ResetOnScreenKeyboardRuntime();
            SpinWait.SpinUntil(() => !_im.MenuKeyboardLifecyclePending, 5000);
            MenuClock.Source = null;
            SettingsManager.UserSettings = _settings;
            SettingsManager.UserDevices = _devices;
            SettingsManager.SlotCreated = _created;
            for (int i = 0; i < _sets.Length; i++) SettingsManager.SlotMappingSets[i] = _sets[i];
            InputManager.ClearAllShiftRuntime();
            OwnHidOutput.ResetForTests();
        }

        // ── Fixture ──────────────────────────────────────────────

        /// <summary>A keyboard menu with no word suggestions, which would ask
        /// Windows' text prediction, on a mapping set of its own that declares
        /// the layer L1.</summary>
        private static MenuDefinitionEntry AddMenu(int slot, string opensWith)
        {
            var set = new MappingSet();
            set.ShiftActivators.Add(new ShiftActivator
            {
                LayerMask = "L1", LayerName = "Typing", Descriptor = "Gamepad Paddle4", Mode = "Hold",
            });
            var def = KeyboardMenuDefaults.Create(1, "Keyboard");
            def.Suggestions = false;
            KeyboardMenuDefaults.SetOpenInput(def, new KeyboardInput(opensWith));
            set.Menus.Add(def);
            SettingsManager.SlotMappingSets[slot] = set;
            SettingsManager.SlotCreated[slot] = true;
            return def;
        }

        /// <summary>A device on a slot. Every device a test reads is added
        /// before its first poll: the runtime refreshes which slots a device
        /// feeds four times a second on the system clock.</summary>
        private static UserDevice AddPad(int slot = Slot, int capType = InputDeviceType.Gamepad)
        {
            var ud = new UserDevice
            {
                InstanceGuid = Guid.NewGuid(),
                CapType = capType,
                CapButtonCount = 16,
                IsOnline = true,
                InputState = Rest(),
            };
            lock (SettingsManager.UserDevices.SyncRoot)
                SettingsManager.UserDevices.Items.Add(ud);
            lock (SettingsManager.UserSettings.SyncRoot)
                SettingsManager.UserSettings.Items.Add(new UserSetting { InstanceGuid = ud.InstanceGuid, MapTo = slot });
            return ud;
        }

        /// <summary>Sticks centered, triggers let go, and the buttons named
        /// held, by SDL's gamepad index.</summary>
        private static CustomInputState Rest(params int[] buttons)
        {
            var s = new CustomInputState();
            s.Axis[0] = s.Axis[1] = s.Axis[3] = s.Axis[4] = 32768;
            foreach (int b in buttons) s.Buttons[b] = true;
            return s;
        }

        /// <summary>One poll: Step 2 reads each device, then the keyboard
        /// ticks. Returns the state each device's mappings see.</summary>
        private CustomInputState[] Poll(params (UserDevice Ud, CustomInputState Raw)[] reads)
            => PollOn(true, reads);

        private CustomInputState Poll(UserDevice ud, CustomInputState raw) => Poll((ud, raw))[0];

        /// <summary>A poll on the idle path, where Step 5 feeds no slot.</summary>
        private CustomInputState PollIdlePath(UserDevice ud, CustomInputState raw) => PollOn(false, (ud, raw))[0];

        private CustomInputState[] PollOn(bool step5Runs, params (UserDevice Ud, CustomInputState Raw)[] reads)
        {
            _now += 16;
            var seen = new CustomInputState[reads.Length];
            for (int i = 0; i < reads.Length; i++)
            {
                var (ud, raw) = reads[i];
                seen[i] = _im.OnScreenKeyboardFilter(ud, raw);
                ud.InputState = seen[i];
                _im.UpdateMenuContexts(ud, seen[i], raw);
            }
            _im.TickOnScreenKeyboard(step5Runs);
            return seen;
        }

        /// <summary>Ticks with no device read, as after they went offline.</summary>
        private void Idle(int ms)
        {
            for (int t = 0; t < ms; t += 16)
            {
                _now += 16;
                _im.TickOnScreenKeyboard();
            }
        }

        private bool IsOpen => _im.OnScreenKeyboardEngine?.IsOpen == true;

        /// <summary>A tap of Opens With on Guide, let go.</summary>
        private void OpenWithGuide(UserDevice ud)
        {
            Poll(ud, Rest());
            Poll(ud, Rest(GuideButton));
            Assert.True(IsOpen);
            Poll(ud, Rest());
        }

        private void Tap(UserDevice ud, params int[] buttons)
        {
            Poll(ud, Rest(buttons));
            Poll(ud, Rest());
        }

        // ── Opening, closing, holding at rest ────────────────────

        [Fact]
        public void OpensWith_OpensTheKeyboard_AndHoldsItsControllerAtRestForEverythingElse()
        {
            var ud = AddPad();
            var other = AddPad(Slot + 1);
            Poll((ud, Rest()), (other, Rest()));
            Assert.False(IsOpen);
            Poll((ud, Rest(GuideButton)), (other, Rest()));
            Assert.True(IsOpen);
            Assert.Equal((Slot, 1), _im.OnScreenKeyboardOwner);
            Assert.Equal(new[] { ud.InstanceGuid }, _im.OnScreenKeyboardOwners);
            Assert.True(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
            Assert.False(_im.OnScreenKeyboardMasks(other.InstanceGuid));

            var raw = Rest(A);
            raw.Axis[0] = 65535;
            raw.Axis[2] = 65535;
            raw.Povs[0] = 9000;
            raw.Gyro[1] = 3f;
            var otherRaw = Rest(A);
            var seen = Poll((ud, raw), (other, otherRaw));
            Assert.NotSame(raw, seen[0]);
            Assert.False(seen[0].Buttons[A]);
            Assert.Equal(32768, seen[0].Axis[0]);
            Assert.Equal(0, seen[0].Axis[2]);
            Assert.Equal(-1, seen[0].Povs[0]);
            Assert.Equal(0f, seen[0].Gyro[1]);
            // The real state is untouched: the keyboard reads it.
            Assert.True(raw.Buttons[A]);
            // A controller on another slot passes as it is.
            Assert.Same(otherRaw, seen[1]);
        }

        [Fact]
        public void TheKeyboardReadsTheRealState_AndTypes()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            Tap(ud, A);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void OpensWith_ClosesIt_AndThePressStaysSilentUntilLetGo()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            Poll(ud, Rest(GuideButton));
            Assert.False(IsOpen);
            Assert.Empty(_im.OnScreenKeyboardOwners);
            Assert.Empty(_out.Sent);

            // Guide is still down after the close, so the slot sees it up. A
            // button pressed after the close is live.
            var seen = Poll(ud, Rest(GuideButton, Y));
            Assert.False(seen.Buttons[GuideButton]);
            Assert.True(seen.Buttons[Y]);
            var released = Rest();
            Assert.Same(released, Poll(ud, released));
            Assert.False(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
            Assert.False(IsOpen);
        }

        [Fact]
        public void TheCloseControl_Closes_AndItsPressStaysSilentUntilLetGo()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            Poll(ud, Rest(B));
            Assert.False(IsOpen);
            Assert.True(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
            Assert.False(Poll(ud, Rest(B)).Buttons[B]);
            var released = Rest();
            Assert.Same(released, Poll(ud, released));
            Assert.False(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
        }

        /// <summary>The Desktop starter's case: Opens With on X, which is
        /// Backspace on the keyboard. The press is Backspace's.</summary>
        [Fact]
        public void OpensWithOnAControlsInput_GivesThePressToTheControl()
        {
            var ud = AddPad();
            KeyboardMenuDefaults.SetOpenInput(_menu, new KeyboardInput("Gamepad ButtonX"));
            Poll(ud, Rest());
            Poll(ud, Rest(X));
            Assert.True(IsOpen);
            Poll(ud, Rest());
            Assert.Empty(_out.Sent);
            Tap(ud, X);
            Assert.True(IsOpen);
            Assert.Equal(new[] { "tap 000E None" }, _out.Sent);
        }

        [Fact]
        public void AnotherControlPressedWithOpensWith_LeavesThePressToOpensWith()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            Poll(ud, Rest(A, GuideButton));
            Assert.False(IsOpen);
            Assert.Empty(_out.Sent);
        }

        /// <summary>Opens With on Back + Start, and Start is Enter: the
        /// combination is the more specific input, so it closes.</summary>
        [Fact]
        public void OpensWithOnACombination_ClosesThoughOneOfItsInputsIsAControl()
        {
            var ud = AddPad();
            KeyboardMenuDefaults.SetOpenInput(_menu, new KeyboardInput("Gamepad ButtonBack", "", "Gamepad ButtonStart"));
            Poll(ud, Rest());
            Poll(ud, Rest(Back));
            Assert.False(IsOpen);
            Poll(ud, Rest(Back, Start));
            Assert.True(IsOpen);
            Poll(ud, Rest());
            Poll(ud, Rest(Back));
            Poll(ud, Rest(Back, Start));
            Assert.False(IsOpen);
            Assert.Empty(_out.Sent);
        }

        /// <summary>Opens With on Start, and Enter on Paddle 1 + Start: the
        /// control's input holds Opens With's, so the press is Enter's.</summary>
        [Fact]
        public void AControlWhoseCombinationHoldsOpensWith_TakesThePress()
        {
            var ud = AddPad();
            KeyboardMenuDefaults.SetOpenInput(_menu, new KeyboardInput("Gamepad ButtonStart"));
            KeyboardMenuDefaults.Set(_menu, KeyboardLayout.Grid, KeyboardControlAction.Enter,
                new KeyboardInput("Gamepad Paddle1", "", "Gamepad ButtonStart"));
            Poll(ud, Rest());
            Poll(ud, Rest(Start));
            Assert.True(IsOpen);
            Poll(ud, Rest());
            Poll(ud, Rest(Paddle1));
            Poll(ud, Rest(Paddle1, Start));
            Poll(ud, Rest());
            Assert.True(IsOpen);
            Assert.Equal(new[] { "tap 001C None" }, _out.Sent);
            // Start alone is Opens With's own press.
            Poll(ud, Rest(Start));
            Assert.False(IsOpen);
        }

        [Fact]
        public void TheKeyboardCloses_WhenTheLastOfItsControllersGoesOffline()
        {
            var a = AddPad();
            var b = AddPad();
            Poll((a, Rest()), (b, Rest()));
            Poll((a, Rest(GuideButton)), (b, Rest()));
            Assert.True(IsOpen);
            Assert.Equal(new[] { a.InstanceGuid, b.InstanceGuid }, _im.OnScreenKeyboardOwners);

            // Step 2 stops reading b.
            for (int i = 0; i < 25; i++) Poll(a, Rest());
            Assert.True(IsOpen);
            Assert.Equal(new[] { a.InstanceGuid }, _im.OnScreenKeyboardOwners);
            Assert.False(_im.OnScreenKeyboardMasks(b.InstanceGuid));

            // And then a.
            Idle(400);
            Assert.False(IsOpen);
            Assert.Empty(_im.OnScreenKeyboardOwners);
            Assert.False(_im.OnScreenKeyboardMasks(a.InstanceGuid));
        }

        [Fact]
        public void AControllerThatGoesOffline_HoldingTheClosingButton_LosesItsMask()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            Poll(ud, Rest(B));
            Assert.False(IsOpen);
            Assert.True(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
            // It goes offline with B down, so Step 2 never sees it let go.
            Idle(400);
            Assert.False(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
        }

        [Fact]
        public void AControllerThatStartsDrivingTheMenuWhileOpen_IsHeldAtRestToo()
        {
            var a = AddPad();
            var b = AddPad();
            OpenWithGuide(a);
            Assert.Equal(new[] { a.InstanceGuid }, _im.OnScreenKeyboardOwners);
            Poll((a, Rest()), (b, Rest()));
            Assert.Equal(new[] { a.InstanceGuid, b.InstanceGuid }, _im.OnScreenKeyboardOwners);
            Assert.False(Poll((a, Rest()), (b, Rest(A)))[1].Buttons[A]);
        }

        [Fact]
        public void AnotherKeyboardMenusOpensWith_TakesTheKeyboardOver()
        {
            var a = AddPad();
            var b = AddPad(Slot + 1);
            AddMenu(Slot + 1, Guide);
            Poll((a, Rest()), (b, Rest()));
            Poll((a, Rest(GuideButton)), (b, Rest()));
            Assert.Equal((Slot, 1), _im.OnScreenKeyboardOwner);
            Poll((a, Rest()), (b, Rest()));
            Poll((a, Rest()), (b, Rest(GuideButton)));
            Assert.True(IsOpen);
            Assert.Equal((Slot + 1, 1), _im.OnScreenKeyboardOwner);
            Assert.Equal(new[] { b.InstanceGuid }, _im.OnScreenKeyboardOwners);
            Assert.False(_im.OnScreenKeyboardMasks(a.InstanceGuid));
        }

        /// <summary>The keyboard types on this PC, which a restricted Remote
        /// Link peer may not do: it cannot open on a slot such a peer feeds,
        /// and it closes on the poll a peer starts feeding its slot, as the
        /// macro pass checks each poll.</summary>
        [Fact]
        public void ARestrictedPeerOnTheSlot_KeepsTheKeyboardClosed_AndClosesAnOpenOne()
        {
            var ud = AddPad();
            var peer = AddPad();
            Poll((ud, Rest()), (peer, Rest()));
            _im.SetDeviceRestricted(peer.InstanceGuid, true);
            Poll((ud, Rest(GuideButton)), (peer, Rest()));
            Assert.False(IsOpen);

            _im.SetDeviceRestricted(peer.InstanceGuid, false);
            Poll((ud, Rest()), (peer, Rest()));
            Poll((ud, Rest(GuideButton)), (peer, Rest()));
            Assert.True(IsOpen);
            _im.SetDeviceRestricted(peer.InstanceGuid, true);
            Poll((ud, Rest(GuideButton)), (peer, Rest()));
            Assert.False(IsOpen);
            Assert.Empty(_im.OnScreenKeyboardOwners);
        }

        [Fact]
        public void TurningTheMenuOff_ClosesTheKeyboard()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            _menu.Enabled = false;
            Poll(ud, Rest());
            Assert.False(IsOpen);
            Assert.False(_im.OnScreenKeyboardMasks(ud.InstanceGuid));
        }

        [Fact]
        public void AMenuThatIsNoLongerAKeyboard_OrIsGone_ClosesTheKeyboard()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            _menu.Kind = MenuKind.Radial;
            Poll(ud, Rest());
            Assert.False(IsOpen);

            _menu.Kind = MenuKind.Keyboard;
            Poll(ud, Rest());
            Poll(ud, Rest(GuideButton));
            Assert.True(IsOpen);
            SettingsManager.SlotMappingSets[Slot].Menus.Clear();
            Poll(ud, Rest());
            Assert.False(IsOpen);
        }

        // ── Controls on any input, on any device ─────────────────

        [Fact]
        public void AControlRecordedOnAnotherDevice_ReadsThatDeviceOnly()
        {
            var pad = AddPad();
            var keys = AddPad(capType: InputDeviceType.HandheldButtons);
            KeyboardMenuDefaults.Set(_menu, KeyboardLayout.Grid, KeyboardControlAction.TypeKey,
                new KeyboardInput("Button 3", keys.InstanceGuidString));
            Poll((pad, Rest()), (keys, Rest()));
            Poll((pad, Rest(GuideButton)), (keys, Rest()));
            Assert.True(IsOpen);
            // It drives the keyboard, so it is held at rest too.
            Assert.True(_im.OnScreenKeyboardMasks(keys.InstanceGuid));
            Poll((pad, Rest()), (keys, Rest()));

            // A on the gamepad is no longer Type Key, and the device answers
            // only the control recorded on it.
            Poll((pad, Rest(A)), (keys, Rest(A)));
            Poll((pad, Rest()), (keys, Rest()));
            Assert.Empty(_out.Sent);
            Poll((pad, Rest()), (keys, Rest(Y)));
            Poll((pad, Rest()), (keys, Rest()));
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void AControlOnACombination_NeedsBothInputsHeld()
        {
            var ud = AddPad();
            KeyboardMenuDefaults.Set(_menu, KeyboardLayout.Grid, KeyboardControlAction.TypeKey,
                new KeyboardInput("Gamepad Paddle1", "", "Gamepad ButtonA"));
            OpenWithGuide(ud);
            Tap(ud, A);
            Assert.Empty(_out.Sent);
            Poll(ud, Rest(Paddle1));
            Poll(ud, Rest(Paddle1, A));
            Poll(ud, Rest());
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        /// <summary>The left stick's axes swapped: pushed right, the selection
        /// moves down, from q to a. The default steering would move it right,
        /// to w.</summary>
        [Fact]
        public void CustomSteering_ReadsTheAxesTheMenuNames()
        {
            var ud = AddPad();
            _menu.HostDescriptor = "Custom";
            _menu.CustomXDescriptor = "Gamepad LeftStickY";
            _menu.CustomYDescriptor = "Gamepad LeftStickX";
            OpenWithGuide(ud);
            var right = Rest();
            right.Axis[0] = 65535;
            Poll(ud, right);
            Poll(ud, Rest());
            Tap(ud, A);
            Assert.Equal(new[] { "tap 001E None" }, _out.Sent);
        }

        /// <summary>LT is Shift in Grid and types the left half in Split. Held
        /// through a click on the window's layout key, it types nothing until
        /// it is let go.</summary>
        [Fact]
        public void ALayoutKeyClickedInTheWindow_GuardsWhatTheNewLayoutReads()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            var lt = Rest();
            lt.Axis[2] = 65535;
            Poll(ud, lt);
            _im.PostOnScreenKeyboardCommand(new OskCommand(OskCommandKind.ClickKey, 0, 4, 5));
            Poll(ud, lt);
            Assert.Equal(OskStyle.Split, _im.OnScreenKeyboardEngine.Style);
            for (int i = 0; i < 5; i++) Poll(ud, lt);
            for (int i = 0; i < 10; i++) Poll(ud, Rest());
            Assert.Empty(_out.Sent);
            TypeTheLeftHalf(ud, lt);
        }

        /// <summary>A half of Split types once the window for pressing both
        /// halves together, a space, has passed.</summary>
        private void TypeTheLeftHalf(UserDevice ud, CustomInputState lt)
        {
            Poll(ud, lt);
            for (int i = 0; i < 10; i++) Poll(ud, Rest());
            Assert.Single(_out.Sent);
        }

        [Fact]
        public void ALayoutPickedOnTheMenusTabWhileOpen_GuardsWhatTheNewLayoutReads()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            var lt = Rest();
            lt.Axis[2] = 65535;
            Poll(ud, lt);
            _menu.KeyboardLayout = KeyboardLayout.Split;
            for (int i = 0; i < 5; i++) Poll(ud, lt);
            Assert.Equal(OskStyle.Split, _im.OnScreenKeyboardEngine.Style);
            for (int i = 0; i < 10; i++) Poll(ud, Rest());
            Assert.Empty(_out.Sent);
            TypeTheLeftHalf(ud, lt);
        }

        [Fact]
        public void TheView_CarriesTheOwnerMenusInputs()
        {
            var ud = AddPad();
            OpenWithGuide(ud);
            var v = _im.OnScreenKeyboardView;
            Assert.True(v.Open);
            Assert.Equal(Slot, v.Slot);
            Assert.Equal(1, v.MenuId);
            Assert.Equal(KeyboardMenuDefaults.Host, v.Host);
            Assert.Equal("Gamepad ButtonA", v.Bindings[KeyboardControlAction.TypeKey].Descriptor);
            Assert.Equal("Gamepad ButtonB", v.Bindings[KeyboardControlAction.Close].Descriptor);
            // A wheel control has no place on the grid.
            Assert.False(v.Bindings.ContainsKey(KeyboardControlAction.PickWest));
        }

        // ── Opens With's press modes and the layer ───────────────

        [Fact]
        public void OpensOnHold_ActsOnceHeldForTheHoldTime()
        {
            var ud = AddPad();
            _menu.OpenPress = KeyboardOpenPress.Hold;
            _menu.OpenPressMs = 400;
            Poll(ud, Rest());
            for (int i = 0; i < 20; i++) Poll(ud, Rest(GuideButton));
            Assert.False(IsOpen);
            for (int i = 0; i < 6; i++) Poll(ud, Rest(GuideButton));
            Assert.True(IsOpen);
            Poll(ud, Rest());

            // A short press does nothing. A long one closes it.
            Tap(ud, GuideButton);
            Assert.True(IsOpen);
            for (int i = 0; i < 26; i++) Poll(ud, Rest(GuideButton));
            Assert.False(IsOpen);
        }

        [Fact]
        public void OpensOnDoublePress_ActsOnASecondPressInsideTheWindow()
        {
            var ud = AddPad();
            _menu.OpenPress = KeyboardOpenPress.DoublePress;
            Poll(ud, Rest());
            Tap(ud, GuideButton);
            Assert.False(IsOpen);
            // Past the 442 ms window, the next press is a first press again.
            for (int i = 0; i < 30; i++) Poll(ud, Rest());
            Tap(ud, GuideButton);
            Assert.False(IsOpen);
            Poll(ud, Rest(GuideButton));
            Assert.True(IsOpen);
        }

        [Fact]
        public void OpensWith_OpensOnlyInTheMenusLayer()
        {
            var ud = AddPad();
            _menu.LayerMask = "L1";
            Poll(ud, Rest());
            Tap(ud, GuideButton);
            Assert.False(IsOpen);
            InputManager.ApplyMacroLayerSwitch(Slot, "L1");
            Poll(ud, Rest(GuideButton));
            Assert.True(IsOpen);
        }

        [Fact]
        public void ALayerThatHoldsTheMenuOpen_OpensItAndClosesItWhenLeft()
        {
            var ud = AddPad();
            _menu.LayerMask = "L1";
            _menu.LayerHoldsOpen = true;
            Poll(ud, Rest());
            InputManager.ApplyMacroLayerSwitch(Slot, "L1");
            Poll(ud, Rest());
            Assert.True(IsOpen);
            for (int i = 0; i < 10; i++) Poll(ud, Rest());
            InputManager.ApplyMacroLayerSwitch(Slot, "");
            Poll(ud, Rest());
            Assert.False(IsOpen);
        }

        /// <summary>The keyboard holds the layer's activator at rest, which
        /// ends the layer a poll after the open. The keyboard stays open.</summary>
        [Fact]
        public void ALayerThatEndsAsTheKeyboardOpens_LeavesItOpen()
        {
            var ud = AddPad();
            _menu.LayerMask = "L1";
            _menu.LayerHoldsOpen = true;
            Poll(ud, Rest());
            InputManager.ApplyMacroLayerSwitch(Slot, "L1");
            Poll(ud, Rest());
            Assert.True(IsOpen);
            InputManager.ApplyMacroLayerSwitch(Slot, "");
            Poll(ud, Rest());
            Assert.True(IsOpen);
            for (int i = 0; i < 10; i++) Poll(ud, Rest());
            Assert.True(IsOpen);
            Poll(ud, Rest(B));
            Assert.False(IsOpen);
        }

        // ── Text focus ───────────────────────────────────────────

        [Fact]
        public void TextFocus_OpensTheKeyboard_AndClosesTheOneItOpened()
        {
            var ud = AddPad();
            _menu.OpenOnTextFocus = true;
            Poll(ud, Rest());
            Assert.True(_im.OnScreenKeyboardWantsTextFocus);
            _im.NoteTextFocus(true);
            Poll(ud, Rest());
            Assert.True(IsOpen);
            Assert.Equal((Slot, 1), _im.OnScreenKeyboardOwner);
            _im.NoteTextFocus(false);
            Poll(ud, Rest());
            Assert.False(IsOpen);
        }

        [Fact]
        public void TextFocusLeaving_LeavesAKeyboardOpenedAnotherWayOpen()
        {
            var ud = AddPad();
            _menu.OpenOnTextFocus = true;
            OpenWithGuide(ud);
            _im.NoteTextFocus(true);
            Poll(ud, Rest());
            _im.NoteTextFocus(false);
            Poll(ud, Rest());
            Assert.True(IsOpen);
        }

        [Fact]
        public void TextFocus_DoesNothing_WhenNoMenuAsksForIt()
        {
            var ud = AddPad();
            Poll(ud, Rest());
            Assert.False(_im.OnScreenKeyboardWantsTextFocus);
            _im.NoteTextFocus(true);
            Poll(ud, Rest());
            Assert.False(IsOpen);
        }

        // ── Types Through ────────────────────────────────────────

        private IVirtualController[] Controllers()
            => (IVirtualController[])typeof(InputManager)
                .GetField("_virtualControllers", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(_im);

        /// <summary>Q's scan code is the virtual key Q, whatever layout the
        /// PC running the test has.</summary>
        private void QIsQ()
            => _im.OnScreenKeyboardVirtualKeyOverride = scan => scan == 0x10 ? (ushort)0x51 : (ushort)0;

        [Fact]
        public void TypesThroughASlot_SendsTheKeyInThatSlotsKeyboard_OneReportAPoll()
        {
            const int Kbm = 5;
            var ud = AddPad();
            _menu.TypesThrough = "slot:" + Kbm;
            _im.SlotControllerTypes[Kbm] = VirtualControllerType.KeyboardMouse;
            Controllers()[Kbm] = new KeyboardMouseVirtualController(Kbm);
            QIsQ();
            OpenWithGuide(ud);

            // Shift for the next key, then q: Shift goes down a report before
            // the key and up a report after it. Step 4 builds the slot's
            // state anew each poll, as the test does here.
            var lt = Rest();
            lt.Axis[2] = 65535;
            Poll(ud, lt);
            Poll(ud, Rest());
            var frames = new List<string>();
            string Frame() => (_im.CombinedKbmRawStates[Kbm].GetKey(0xA0) ? "Shift" : "")
                              + (_im.CombinedKbmRawStates[Kbm].GetKey(0x51) ? "Q" : "");
            Poll(ud, Rest(A));
            for (int i = 0; i < 4; i++)
            {
                frames.Add(Frame());
                _im.CombinedKbmRawStates[Kbm] = default;
                Poll(ud, Rest());
            }
            Assert.Equal(new[] { "Shift", "ShiftQ", "Shift", "" }, frames);
            Assert.Empty(_out.Sent);

            // On the idle path Step 5 feeds no slot, so Windows Input types it.
            PollIdlePath(ud, Rest(A));
            PollIdlePath(ud, Rest());
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
            Assert.False(_im.CombinedKbmRawStates[Kbm].GetKey(0x51));
        }

        [Fact]
        public void TypesThroughASlotThatIsNotKeyboardAndMouse_TypesThroughWindowsInput()
        {
            var ud = AddPad();
            _menu.TypesThrough = "slot:5";
            QIsQ();
            OpenWithGuide(ud);
            Tap(ud, A);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void TypesThroughItsOwnKeyboard_MakesIt_SendsTheKeyThere_AndRemovesItAfter()
        {
            var ud = AddPad();
            var sink = new Sink();
            string made = null;
            _im.OnScreenKeyboardDeviceFactory = (slot, menuId, profile) =>
                new KeyboardMouseVirtualController(slot, null, HMaestroProfileCatalog.GetProfileById(profile), null)
                {
                    KeyboardIdentityKey = InputManager.MenuKeyboardIdentityKey(slot, menuId),
                    CreateSinkSeam = (p, key) => { made = key; return sink; },
                    KeyResolverSeam = KbmHidKeyMap.Nominal,
                };
            QIsQ();
            _menu.TypesThrough = Keyboard;
            Poll(ud, Rest());
            Assert.True(SpinWait.SpinUntil(() => _im.OnScreenKeyboardDeviceOf(Slot, 1) != null, 5000));
            Assert.Equal("padforge:pad2:KeyboardMenu1:keyboard", made);

            OpenWithGuide(ud);
            Tap(ud, A);
            // q is usage 0x14, then the key let go.
            Assert.Equal(new[] { "K 00:14", "K 00:" }, sink.WaitFor(2));
            Assert.Empty(_out.Sent);

            // Back to Windows Input: the keyboard is removed.
            _menu.TypesThrough = "";
            for (int i = 0; i < 20; i++) Poll(ud, Rest());
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref sink.Disposes) == 1, 5000));
            Assert.Null(_im.OnScreenKeyboardDeviceOf(Slot, 1));
            Tap(ud, A);
            Assert.Equal(new[] { "tap 0010 None" }, _out.Sent);
        }

        [Fact]
        public void TheMenusKeyboardIdentity_NeverMeetsASlotsOwn()
        {
            Assert.Equal("padforge:pad3:KeyboardMenu7:keyboard", InputManager.MenuKeyboardIdentityKey(3, 7));
            Assert.NotEqual(KeyboardMouseVirtualController.IdentityKeyFor(3, keyboard: true),
                InputManager.MenuKeyboardIdentityKey(3, 7));
            Assert.True(InputManager.TryParseSlotTarget("slot:4", out int slot));
            Assert.Equal(4, slot);
            Assert.False(InputManager.TryParseSlotTarget("slot:-1", out _));
            Assert.False(InputManager.TryParseSlotTarget("slot:" + InputManager.MaxPads, out _));
            Assert.Null(InputManager.MenuKeyboardProfileOf(""));
            Assert.Null(InputManager.MenuKeyboardProfileOf("slot:4"));
            Assert.Equal(Keyboard, InputManager.MenuKeyboardProfileOf(Keyboard));
        }

        /// <summary>A menu keyboard's HID device, recording each report.</summary>
        private sealed class Sink : IKbmHidSink
        {
            private readonly object _gate = new();
            private readonly List<string> _log = new();
            public int Disposes;

            public void SubmitKeyboard(HMKeyModifiers modifiers, HMKey[] keys)
            {
                lock (_gate) _log.Add($"K {(byte)modifiers:X2}:" + string.Join(",", keys.Select(k => ((byte)k).ToString("X2"))));
            }

            public void SubmitConsumer(ushort[] usages)
            {
                lock (_gate) _log.Add("C " + string.Join(",", usages.Select(u => u.ToString("X"))));
            }

            public void SubmitMouse(in HMMouseState s)
            {
                lock (_gate) _log.Add("M");
            }

            public HMKeyboardLeds? KeyboardLeds => null;
#pragma warning disable CS0067 // never raised: nothing here sets the LEDs
            public event Action<HMKeyboardLeds> KeyboardLedsChanged;
#pragma warning restore CS0067
            public void Dispose() => Interlocked.Increment(ref Disposes);

            private string[] Log { get { lock (_gate) return _log.ToArray(); } }

            public string[] WaitFor(int count)
            {
                Assert.True(SpinWait.SpinUntil(() => Log.Length >= count, 5000),
                    $"expected {count} reports, got: {string.Join(" | ", Log)}");
                Thread.Sleep(50);   // nothing more arrives
                return Log;
            }
        }

        // ── Wiring ───────────────────────────────────────────────

        [Fact]
        public void Step2_PassesTheRealStateBesideTheOneAtRest_AndBothPathsTick()
        {
            string root = RepoRoot();
            string step2 = File.ReadAllText(Path.Combine(root, "PadForge.App", "Common", "Input", "InputManager.Step2.UpdateInputStates.cs"));
            Assert.Contains("UpdateMenuContexts(ud, newState, rawState);", step2);
            string im = File.ReadAllText(Path.Combine(root, "PadForge.App", "Common", "Input", "InputManager.cs"));
            Assert.Contains("TickOnScreenKeyboard(step5Runs: false);", im);
            Assert.Contains("TickOnScreenKeyboard(step5Runs: true);", im);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }
    }
}
