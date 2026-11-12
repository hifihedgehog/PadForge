using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Common.Input.OnScreenKeyboard;
using PadForge.Engine.Common.Mapping;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>A labeled string choice (Types Through). Equal by value and
    /// label, as <see cref="MenuIntOption"/> is by value, label and description.</summary>
    public sealed class MenuStringOption
    {
        public string Value { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => Label;
        public override bool Equals(object obj)
            => obj is MenuStringOption o && string.Equals(o.Value, Value, StringComparison.Ordinal)
               && string.Equals(o.Label, Label, StringComparison.Ordinal);
        public override int GetHashCode() => Value.GetHashCode();
    }

    /// <summary>A Keyboard + Mouse slot a keyboard menu can type through.</summary>
    public readonly record struct KbmSlotChoice(int PadIndex, string Label);

    /// <summary>One entry of a keyboard control's input list: an input on a
    /// device, or the combination the control holds now.</summary>
    public sealed class KeyboardChoice
    {
        public string Key { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string DeviceLabel { get; init; } = "";
        public KeyboardInput Input { get; init; }
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The Keyboard style (#566): the On-Screen Keyboard as a menu. It has no
    /// cells, so the rows only a radial ring or a touch grid uses hide, as
    /// Center Cell hides for a touch grid, and it steers with its own
    /// controls, so the opener rows hide too. What it adds: Layout, Opens
    /// With and Open When a Text Box Is Selected rows, the Keyboard block's
    /// rows, and under them Keyboard Controls with a row per control
    /// (steering, keys and pointers alike). Every control writes through
    /// <see cref="KeyboardMenuDefaults.Set"/>, which keeps only what differs
    /// from the layout's default.
    /// </summary>
    public partial class MenuEditorItem
    {
        public bool IsKeyboard => Entry.Kind == MenuKind.Keyboard;

        /// <summary>The rows a radial ring or a touch grid uses: Cells, Fire
        /// Mode, Show Labels, the cell bindings and the opener rows.</summary>
        public bool IsCellMenu => !IsKeyboard;

        /// <summary>Click Input: a keyboard types with its own controls, and a
        /// hotbar fires with its Fire control.</summary>
        public bool ShowsClickRow => !IsKeyboard && !IsHotbar;

        /// <summary>Pad Half: a keyboard points with its own pointer rows.</summary>
        public bool ShowsHostHalfRow => HostIsTouchpad && !IsKeyboard;

        /// <summary>The Custom Axes rows under the opener, which a keyboard
        /// does not have: its steering rows take any axis on any device. A
        /// hotbar reads no axes of a Custom opener, only its four controls.</summary>
        public bool ShowsCustomAxesRows => IsCustomHost && IsCellMenu && !IsHotbar;

        /// <summary>The count beside the Keyboard Controls head: the layout and its rows.</summary>
        public string KeyboardControlsSummary => string.Format(Strings.Instance.Menu_Keyboard_Controls_Summary_Format,
            KeyboardLayoutOptions[Math.Clamp((int)Entry.KeyboardLayout, 0, 2)].Label,
            KeyboardControlRows.Count);

        /// <summary>Applies the keyboard's own defaults when a menu turns into
        /// one: it sits at the bottom of the screen, and steering waits for
        /// half a push. Only a value still at the radial default changes, and
        /// the opener the menu had stays for when it turns back.</summary>
        private void ApplyKeyboardDefaults()
        {
            if (Entry.PosYPercent == 50) Entry.PosYPercent = KeyboardMenuDefaults.PosYPercent;
            if (Entry.EngageDeadzonePercent == 25) Entry.EngageDeadzonePercent = KeyboardMenuDefaults.SteerDeadzonePercent;
        }

        /// <summary>The way back when a keyboard turns into a ring or a grid:
        /// a value still at the keyboard's default returns to the ring's, so
        /// the menu does not open at the bottom of the screen or wait for
        /// half a push. A value the user set stays.</summary>
        private void LeaveKeyboardDefaults()
        {
            if (Entry.PosYPercent == KeyboardMenuDefaults.PosYPercent) Entry.PosYPercent = 50;
            if (Entry.EngageDeadzonePercent == KeyboardMenuDefaults.SteerDeadzonePercent) Entry.EngageDeadzonePercent = 25;
        }

        private void RaiseKeyboardDependents()
        {
            OnPropertyChanged(nameof(IsKeyboard));
            OnPropertyChanged(nameof(IsCellMenu));
            OnPropertyChanged(nameof(ShowsClickRow));
            OnPropertyChanged(nameof(ShowsHostHalfRow));
            OnPropertyChanged(nameof(ShowsCustomAxesRows));
            OnPropertyChanged(nameof(EngageDeadzonePercent));
            OnPropertyChanged(nameof(KeyboardLayoutIndex));
            OnPropertyChanged(nameof(KeyboardLayoutDescription));
            OnPropertyChanged(nameof(PosYPercent));
            RefreshHostOptions();
            RebuildKeyboardControls();
        }

        // ── Layout ───────────────────────────────────────────────

        private static IReadOnlyList<MenuIntOption> KeyboardLayoutOptionsBacking = BuildKeyboardLayoutOptions();

        private static IReadOnlyList<MenuIntOption> BuildKeyboardLayoutOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Osk_Style_Grid, Description = Strings.Instance.Menu_KeyboardLayout_Grid_Desc },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Osk_Style_Split, Description = Strings.Instance.Menu_KeyboardLayout_Split_Desc },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Osk_Style_Wheel, Description = Strings.Instance.Menu_KeyboardLayout_Wheel_Desc },
        };

        public IReadOnlyList<MenuIntOption> KeyboardLayoutOptions => KeyboardLayoutOptionsBacking;

        public int KeyboardLayoutIndex
        {
            get => Math.Clamp((int)Entry.KeyboardLayout, 0, 2);
            set
            {
                if (value < 0) return;
                var layout = (KeyboardLayout)Math.Clamp(value, 0, 2);
                if (Entry.KeyboardLayout == layout) return;
                Entry.KeyboardLayout = layout;
                OnPropertyChanged();
                OnPropertyChanged(nameof(KeyboardLayoutDescription));
                RebuildKeyboardControls();
                OnEdited();
            }
        }

        public string KeyboardLayoutDescription
            => KeyboardLayoutOptions[Math.Clamp((int)Entry.KeyboardLayout, 0, 2)].Description;

        public RelayCommand ResetKeyboardLayoutCommand => _resetKeyboardLayout ??= new RelayCommand(() => KeyboardLayoutIndex = 0);
        private RelayCommand _resetKeyboardLayout;

        // ── Opening ──────────────────────────────────────────────

        public bool OpenOnTextFocus
        {
            get => Entry.OpenOnTextFocus;
            set { if (Entry.OpenOnTextFocus != value) { Entry.OpenOnTextFocus = value; OnPropertyChanged(); OnEdited(); } }
        }

        // ── Opens On: the macro trigger's press modes ────────────

        private static IReadOnlyList<MenuIntOption> OpenPressOptionsBacking = BuildOpenPressOptions();

        /// <summary>The macro trigger's press modes that open and close, by
        /// its own names, in the order a press runs through them.</summary>
        private static IReadOnlyList<MenuIntOption> BuildOpenPressOptions()
        {
            var s = Strings.Instance;
            return new[]
            {
                new MenuIntOption { Value = (int)KeyboardOpenPress.Press, Label = s.Macro_OnPress, Description = s.Menu_Keyboard_OpenPress_Press_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.Release, Label = s.Macro_OnRelease, Description = s.Menu_Keyboard_OpenPress_Release_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.WhileHeld, Label = s.Macro_WhileHeld, Description = s.Menu_Keyboard_OpenPress_WhileHeld_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.Hold, Label = s.Macro_HoldForMs, Description = s.Menu_Keyboard_OpenPress_Hold_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.ShortPress, Label = s.Macro_ShortPress, Description = s.Menu_Keyboard_OpenPress_Short_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.DoublePress, Label = s.Macro_DoublePress, Description = s.Menu_Keyboard_OpenPress_Double_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.TriplePress, Label = s.Macro_TriplePress, Description = s.Menu_Keyboard_OpenPress_Triple_Desc },
                new MenuIntOption { Value = (int)KeyboardOpenPress.SinglePress, Label = s.Macro_SinglePress, Description = s.Menu_Keyboard_OpenPress_Single_Desc },
            };
        }

        public IReadOnlyList<MenuIntOption> OpenPressOptions => OpenPressOptionsBacking;

        private const int LastOpenPress = (int)KeyboardOpenPress.SinglePress;

        public int OpenPressIndex
        {
            get => Math.Clamp((int)Entry.OpenPress, 0, LastOpenPress);
            set
            {
                if (value < 0) return;
                var press = (KeyboardOpenPress)Math.Clamp(value, 0, LastOpenPress);
                if (Entry.OpenPress == press) return;
                Entry.OpenPress = press;
                // A hold time is no press window: the new mode starts at its
                // own default, as a macro's does.
                Entry.OpenPressMs = 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpenPressMs));
                OnPropertyChanged(nameof(ShowsOpenPressMs));
                OnPropertyChanged(nameof(OpenPressMsLabel));
                OnEdited();
            }
        }

        /// <summary>The hold time or the press window, in milliseconds.</summary>
        public int OpenPressMs
        {
            get => KeyboardMenuDefaults.OpenPressMs(Entry);
            set
            {
                if (!ShowsOpenPressMs) return;
                int v = Math.Clamp(value, 50, KeyboardMenuDefaults.MaxOpenPressMs);
                if (KeyboardMenuDefaults.OpenPressMs(Entry) == v && Entry.OpenPressMs != 0) return;
                Entry.OpenPressMs = v;
                OnPropertyChanged();
                OnEdited();
            }
        }

        public bool ShowsOpenPressMs => KeyboardMenuDefaults.UsesHoldTime(Entry.OpenPress)
                                        || KeyboardMenuDefaults.UsesPressWindow(Entry.OpenPress);

        /// <summary>The time's name for the press: the macro trigger's Hold
        /// Time or Press Window.</summary>
        public string OpenPressMsLabel => KeyboardMenuDefaults.UsesPressWindow(Entry.OpenPress)
            ? Strings.Instance.Macro_TriggerDoublePressMs_Label
            : Strings.Instance.Macro_TriggerHoldMs_Label;
        public RelayCommand ResetOpenPressCommand => _resetOpenPress ??= new RelayCommand(() =>
        {
            if (Entry.OpenPress == KeyboardOpenPress.Press && Entry.OpenPressMs == 0) return;
            Entry.OpenPress = KeyboardOpenPress.Press;
            Entry.OpenPressMs = 0;
            OnPropertyChanged(nameof(OpenPressIndex));
            OnPropertyChanged(nameof(OpenPressMs));
            OnPropertyChanged(nameof(ShowsOpenPressMs));
            OnPropertyChanged(nameof(OpenPressMsLabel));
            OnEdited();
        });
        private RelayCommand _resetOpenPress;

        // ── Keyboard block ───────────────────────────────────────

        public bool FunctionRow
        {
            get => Entry.FunctionRow;
            set { if (Entry.FunctionRow != value) { Entry.FunctionRow = value; OnPropertyChanged(); OnEdited(); } }
        }

        public bool Suggestions
        {
            get => Entry.Suggestions;
            set { if (Entry.Suggestions != value) { Entry.Suggestions = value; OnPropertyChanged(); OnEdited(); } }
        }

        /// <summary>The symbols page's characters as the box shows them: the
        /// default set until the user writes another. Writing the default set
        /// back stores nothing, so the default keeps following PadForge's.</summary>
        public string Symbols
        {
            get => string.IsNullOrEmpty(Entry.Symbols) ? OskLayoutFrame.DefaultSymbols : Entry.Symbols;
            set
            {
                string v = value ?? "";
                if (string.Equals(v, OskLayoutFrame.DefaultSymbols, StringComparison.Ordinal)) v = "";
                if (string.Equals(Entry.Symbols ?? "", v, StringComparison.Ordinal)) return;
                Entry.Symbols = v;
                OnPropertyChanged();
                OnEdited();
            }
        }

        public RelayCommand ResetSymbolsCommand => _resetSymbols ??= new RelayCommand(() =>
        {
            if (string.IsNullOrEmpty(Entry.Symbols)) { OnPropertyChanged(nameof(Symbols)); return; }
            Entry.Symbols = "";
            OnPropertyChanged(nameof(Symbols));
            OnEdited();
        });
        private RelayCommand _resetSymbols;

        /// <summary>The Keyboard + Mouse slots this menu can type through,
        /// supplied by the owning PadViewModel. Null (tests) offers none.</summary>
        internal Func<IReadOnlyList<KbmSlotChoice>> KbmSlotsProvider;

        /// <summary>Types Through: Windows Input, the keyboard of each
        /// Keyboard + Mouse slot, and the HIDMaestro keyboards the Keyboard +
        /// Mouse slot's Keyboard picker lists, which the menu makes itself. A
        /// value no entry carries (a slot that is no longer Keyboard + Mouse,
        /// a profile not on this PC) gets a marked entry, so the selection
        /// never lies.</summary>
        public IReadOnlyList<MenuStringOption> TypesThroughOptions
        {
            get
            {
                var s = Strings.Instance;
                var list = new List<MenuStringOption>
                {
                    new() { Value = "", Label = s.Pad_Kbm_Device_WindowsInput },
                };
                var slots = KbmSlotsProvider?.Invoke();
                if (slots != null)
                    foreach (var c in slots)
                        list.Add(new MenuStringOption
                        {
                            Value = "slot:" + c.PadIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Label = string.Format(s.Menu_TypesThrough_Slot_Format, c.Label),
                        });
                foreach (var o in PadForge.Common.Input.KbmDeviceCatalog.KeyboardOptions(KbmSlotConfig.WindowsInput))
                {
                    if (string.IsNullOrEmpty(o.Value)) continue;
                    list.Add(new MenuStringOption { Value = o.Value, Label = o.Name });
                }
                string cur = Entry.TypesThrough ?? "";
                bool found = false;
                foreach (var o in list)
                    if (string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)) { found = true; break; }
                // The marked entry says what is gone in words: a slot that is
                // no longer Keyboard + Mouse, or a keyboard model this PC does
                // not have. The stored "slot:N" is never shown.
                if (!found)
                    list.Add(new MenuStringOption
                    {
                        Value = cur,
                        Label = cur.StartsWith("slot:", StringComparison.OrdinalIgnoreCase)
                            ? s.Menu_TypesThrough_MissingSlot
                            : string.Format(s.Menu_TypesThrough_MissingKeyboard_Format, cur),
                    });
                return list;
            }
        }

        public string TypesThrough
        {
            get
            {
                string cur = Entry.TypesThrough ?? "";
                // The value the list holds, in its own casing, so the picker
                // finds it.
                foreach (var o in TypesThroughOptions)
                    if (string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)) return o.Value;
                return cur;
            }
            set
            {
                if (value == null) return;
                if (string.Equals(Entry.TypesThrough ?? "", value, StringComparison.OrdinalIgnoreCase)) return;
                Entry.TypesThrough = value;
                OnPropertyChanged(nameof(TypesThroughOptions));
                OnPropertyChanged();
                OnEdited();
            }
        }

        public RelayCommand ResetTypesThroughCommand => _resetTypesThrough ??= new RelayCommand(() => TypesThrough = "");
        private RelayCommand _resetTypesThrough;

        /// <summary>Re-reads the Types Through list after a slot changes type.</summary>
        internal void RefreshTypesThrough()
        {
            OnPropertyChanged(nameof(TypesThroughOptions));
            OnPropertyChanged(nameof(TypesThrough));
        }

        // ── Opens With and the controls ──────────────────────────

        private KeyboardInputRow _openRow;

        /// <summary>Opens With, a row of the same shape as a control's.</summary>
        public KeyboardInputRow OpenRow => _openRow ??= new KeyboardInputRow(this, null, Entry.KeyboardLayout,
            Strings.Instance.Menu_Keyboard_OpensWith);

        /// <summary>The controls of the menu's layout, in the layout's order.</summary>
        public ObservableCollection<KeyboardInputRow> KeyboardControlRows { get; } = new();

        internal void RebuildKeyboardControls()
        {
            KeyboardControlRows.Clear();
            if (!IsKeyboard) return;
            var layout = Entry.KeyboardLayout;
            foreach (var a in KeyboardMenuDefaults.Actions(layout))
                KeyboardControlRows.Add(new KeyboardInputRow(this, a, layout, ControlName(layout, a)));
            OpenRow.Refresh();
            OnPropertyChanged(nameof(KeyboardControlsSummary));
        }

        /// <summary>A control's name, the one its row and the issue's tables use.
        /// Split's steering names the hand each set moves.</summary>
        internal static string ControlName(KeyboardLayout layout, KeyboardControlAction a)
        {
            var s = Strings.Instance;
            return a switch
            {
                KeyboardControlAction.TypeKey => s.Osk_Control_TypeKey,
                KeyboardControlAction.Backspace => s.Osk_Control_Backspace,
                KeyboardControlAction.Space => s.Osk_Control_Space,
                KeyboardControlAction.Enter => s.Osk_Control_Enter,
                KeyboardControlAction.Close => s.Osk_Control_Close,
                KeyboardControlAction.ShiftNextKey => layout == KeyboardLayout.Split ? s.Osk_Control_ShiftLeftHand : s.Osk_Control_ShiftNextKey,
                KeyboardControlAction.ShiftRightHand => s.Osk_Control_ShiftRightHand,
                KeyboardControlAction.SymbolsPage => s.Osk_Control_SymbolsPage,
                KeyboardControlAction.CursorLeft => s.Osk_Control_CursorLeft,
                KeyboardControlAction.CursorRight => s.Osk_Control_CursorRight,
                KeyboardControlAction.MoveUp => s.Osk_Control_MoveUp,
                KeyboardControlAction.MoveDown => s.Osk_Control_MoveDown,
                KeyboardControlAction.MoveLeft => s.Osk_Control_MoveLeft,
                KeyboardControlAction.MoveRight => s.Osk_Control_MoveRight,
                KeyboardControlAction.Suggestion1 => string.Format(s.Osk_Control_Suggestion_Format, 1),
                KeyboardControlAction.Suggestion2 => string.Format(s.Osk_Control_Suggestion_Format, 2),
                KeyboardControlAction.Suggestion3 => string.Format(s.Osk_Control_Suggestion_Format, 3),
                KeyboardControlAction.MoveKeyboard => s.Osk_Control_MoveKeyboard,
                KeyboardControlAction.TypeLeftHalf => s.Osk_Control_TypeLeftHalf,
                KeyboardControlAction.TypeRightHalf => s.Osk_Control_TypeRightHalf,
                KeyboardControlAction.ArrowUp => s.Osk_Control_ArrowUp,
                KeyboardControlAction.ArrowDown => s.Osk_Control_ArrowDown,
                KeyboardControlAction.ArrowLeft => s.Osk_Control_ArrowLeft,
                KeyboardControlAction.ArrowRight => s.Osk_Control_ArrowRight,
                KeyboardControlAction.PickWest => s.Osk_Control_PickWest,
                KeyboardControlAction.PickNorth => s.Osk_Control_PickNorth,
                KeyboardControlAction.PickEast => s.Osk_Control_PickEast,
                KeyboardControlAction.PickSouth => s.Osk_Control_PickSouth,
                KeyboardControlAction.Capitals => s.Osk_Control_Capitals,
                KeyboardControlAction.Numbers => s.Osk_Control_Numbers,
                KeyboardControlAction.Home => s.Osk_Control_Home,
                KeyboardControlAction.End => s.Osk_Control_End,
                KeyboardControlAction.SteerUp => Hand(layout, s.Osk_Control_SteerUp, left: true),
                KeyboardControlAction.SteerDown => Hand(layout, s.Osk_Control_SteerDown, left: true),
                KeyboardControlAction.SteerLeft => Hand(layout, s.Osk_Control_SteerLeft, left: true),
                KeyboardControlAction.SteerRight => Hand(layout, s.Osk_Control_SteerRight, left: true),
                KeyboardControlAction.Steer2Up => Hand(layout, s.Osk_Control_SteerUp, left: false),
                KeyboardControlAction.Steer2Down => Hand(layout, s.Osk_Control_SteerDown, left: false),
                KeyboardControlAction.Steer2Left => Hand(layout, s.Osk_Control_SteerLeft, left: false),
                KeyboardControlAction.Steer2Right => Hand(layout, s.Osk_Control_SteerRight, left: false),
                KeyboardControlAction.Pointer1X => string.Format(s.Osk_Control_PointerX_Format, 1),
                KeyboardControlAction.Pointer1Y => string.Format(s.Osk_Control_PointerY_Format, 1),
                KeyboardControlAction.Pointer1Touch => string.Format(s.Osk_Control_PointerTouch_Format, 1),
                KeyboardControlAction.Pointer1Click => string.Format(s.Osk_Control_PointerClick_Format, 1),
                KeyboardControlAction.Pointer2X => string.Format(s.Osk_Control_PointerX_Format, 2),
                KeyboardControlAction.Pointer2Y => string.Format(s.Osk_Control_PointerY_Format, 2),
                KeyboardControlAction.Pointer2Touch => string.Format(s.Osk_Control_PointerTouch_Format, 2),
                KeyboardControlAction.Pointer2Click => string.Format(s.Osk_Control_PointerClick_Format, 2),
                _ => a.ToString(),
            };
        }

        /// <summary>A steering name, with the hand on Split, whose left half
        /// and right half each steer on their own.</summary>
        private static string Hand(KeyboardLayout layout, string name, bool left)
            => layout != KeyboardLayout.Split ? name
             : string.Format(left ? Strings.Instance.Osk_Control_LeftHand_Format : Strings.Instance.Osk_Control_RightHand_Format, name);

        /// <summary>Reset All: every control back to its layout's default.</summary>
        public RelayCommand ResetKeyboardControlsCommand => _resetKeyboardControls ??= new RelayCommand(() =>
        {
            var layout = Entry.KeyboardLayout;
            var next = new List<KeyboardControlEntry>();
            if (Entry.KeyboardControls != null)
                foreach (var e in Entry.KeyboardControls)
                    if (e != null && e.Layout != layout) next.Add(e);
            Entry.KeyboardControls = next;
            foreach (var row in KeyboardControlRows) row.Refresh();
            OnEdited();
        });
        private RelayCommand _resetKeyboardControls;

        /// <summary>Writes a row's input: a control's through the defaults
        /// table, Opens With to its own fields.</summary>
        internal void SetKeyboardInput(KeyboardInputRow row, KeyboardInput input)
        {
            if (row.Action is { } a)
            {
                var before = KeyboardMenuDefaults.Effective(Entry, row.Layout, a);
                bool wasCustom = KeyboardMenuDefaults.IsCustomized(Entry, row.Layout, a);
                KeyboardMenuDefaults.Set(Entry, row.Layout, a, input);
                if (before.SameAs(input) && wasCustom == KeyboardMenuDefaults.IsCustomized(Entry, row.Layout, a)) return;
            }
            else
            {
                if (KeyboardMenuDefaults.OpenInput(Entry).SameAs(input)) return;
                KeyboardMenuDefaults.SetOpenInput(Entry, input);
            }
            row.Refresh();
            // A control that now shares an input with Move the Keyboard reads
            // differently, and the hint bar shows it on the next draw.
            OnEdited();
        }

        /// <summary>Applies a recorded press to a row. The row's press point
        /// stays, as a Mappings row's deadzone stays when its source changes.
        /// An input the row cannot read is refused (<see cref="KeyboardInputRow.Accepts"/>).</summary>
        public bool TryApplyKeyboardPress(KeyboardInputRow row, PadForge.Services.RecorderService.RecordedPress press)
        {
            if (row == null || string.IsNullOrWhiteSpace(press.Descriptor)) return false;
            if (!row.Accepts(press.Descriptor)) return false;
            if (!string.IsNullOrWhiteSpace(press.AndDescriptor) && (row.RecordsAnAxis || !row.Accepts(press.AndDescriptor))) return false;
            SetKeyboardInput(row, new KeyboardInput(press.Descriptor, press.DeviceGuid ?? "",
                press.AndDescriptor ?? "", press.AndDeviceGuid ?? "", row.Input.Threshold));
            return true;
        }

        /// <summary>Applies a recorded axis to a pointer's X or Y row: an
        /// input with a place across its range, on the device it moved on.</summary>
        public bool TryApplyKeyboardAxis(KeyboardInputRow row, string descriptor, string deviceGuid)
        {
            if (row == null || !row.RecordsAnAxis || string.IsNullOrWhiteSpace(descriptor)) return false;
            string d = descriptor.Trim();
            if (!IsPositionDescriptor(d)) return false;
            SetKeyboardInput(row, new KeyboardInput(d, deviceGuid ?? ""));
            return true;
        }

        /// <summary>A touchpad or mouse gesture or a menu cell: inputs the
        /// keyboard's own controls cannot read, since the keyboard holds the
        /// controller at rest while it is open and they come from that state.
        /// Opens With reads them, since the keyboard is closed when it opens.</summary>
        internal static bool IsSlotEvent(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return false;
            string clean = SourceCoercion.StripLegacyPrefix(descriptor.Trim(), out _, out _);
            return SourceCoercion.IsSlotEventDescriptor(clean);
        }

        /// <summary>An input with a place across a range, what a pointer's X
        /// and Y rows read (SourceCoercion.EvaluateForBipolarAxisTarget): an
        /// axis or a slider, the abstract Gamepad sticks among them, a
        /// touchpad finger's position, the absolute touchpad pointer, the
        /// mouse position and the IR pointer. Gyro and mouse motion measure
        /// speed, which has no place.</summary>
        internal static bool IsPositionDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return false;
            string clean = SourceCoercion.StripLegacyPrefix(descriptor.Trim(), out _, out _);
            return SourceCoercion.IsGenericSensitivityDescriptor(clean)
                || SourceCoercion.IsTouchpadPointerDescriptor(clean)
                || clean.StartsWith("Mouse Position ", StringComparison.Ordinal)
                || clean.StartsWith("IR Pointer ", StringComparison.Ordinal);
        }

        /// <summary>Whether an input has two halves to list: a plain axis,
        /// the abstract Gamepad sticks among them, with no direction of its
        /// own yet, on <paramref name="deviceGuid"/>. A stick rests at the
        /// middle of its range, so a press or a steering direction reads one
        /// half of it. An axis that rests at zero and moves one way (a
        /// gamepad's trigger, by its Gamepad name or on the controller, a VR
        /// controller's trigger or grip, a Bliss-Box pressure axis) is listed
        /// whole, by the rule the press recorder records it by
        /// (InputManager.AxisRestsAtZero): its lower half would read pressed
        /// at rest.</summary>
        internal static bool HasHalves(string descriptor, string deviceGuid = "")
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return false;
            string clean = SourceCoercion.StripLegacyPrefix(descriptor.Trim(), out bool invert, out bool half);
            if (invert || half) return false;
            if (clean.StartsWith("Gamepad ", StringComparison.Ordinal) && clean.Contains("Trigger", StringComparison.Ordinal)) return false;
            string canonical = SourceCoercion.ResolveGamepadAlias(clean) ?? clean;
            if (!canonical.StartsWith("Axis ", StringComparison.Ordinal)) return false;
            var device = Guid.TryParse(deviceGuid, out var g) ? PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(g) : null;
            return !PadForge.Common.Input.InputManager.AxisRestsAtZero(clean, device);
        }

        // ── A row's list and its text ────────────────────────────

        /// <summary>The key a list entry is found by: the device and the
        /// input, and the second input of a combination.</summary>
        internal static string KeyOf(KeyboardInput input)
        {
            if (input.IsEmpty) return "";
            string k = (input.DeviceGuid ?? "").ToLowerInvariant() + "|" + (input.Descriptor ?? "").Trim();
            if (input.HasAnd) k += "&" + (input.AndDeviceGuid ?? "").ToLowerInvariant() + "|" + input.AndDescriptor.Trim();
            return k;
        }

        /// <summary>A row's input list: the slot's inputs, grouped by device
        /// as the mapping picker shows them, less those the row cannot read
        /// (<see cref="KeyboardInputRow.Accepts"/>). An axis that rests at the
        /// middle is followed by its two halves, the directions a recorded
        /// push gives (<see cref="HasHalves"/>), on every row but a pointer's
        /// X and Y, which read the whole axis. The
        /// device-free "Gamepad ..." names sit under (Any Device). The row's
        /// own input, when no entry is it (a combination), gets an entry of
        /// its own so the selection never lies.</summary>
        internal System.ComponentModel.ICollectionView KeyboardChoicesView(KeyboardInputRow row)
        {
            var current = row.Input;
            var list = new List<KeyboardChoice>();
            string curKey = KeyOf(current);
            bool found = curKey.Length == 0;
            void Add(KeyboardInput input, string name, string device)
            {
                string key = KeyOf(input);
                if (key == curKey) found = true;
                list.Add(new KeyboardChoice { Key = key, DisplayName = name, DeviceLabel = device, Input = input });
            }
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
            {
                foreach (var c in provided)
                {
                    if (c == null || string.IsNullOrEmpty(c.Descriptor) || !row.Accepts(c.Descriptor)) continue;
                    string guid = c.DeviceGuid ?? "";
                    Add(new KeyboardInput(c.Descriptor, guid), c.DisplayName, c.DeviceLabel);
                    if (row.RecordsAnAxis || !HasHalves(c.Descriptor, guid)) continue;
                    foreach (string flags in new[] { "H", "IH" })
                    {
                        var half = new KeyboardInput(flags + c.Descriptor.Trim(), guid);
                        Add(half, KeyboardInputText(half), c.DeviceLabel);
                    }
                }
            }
            if (!found)
                list.Add(new KeyboardChoice
                {
                    Key = curKey,
                    DisplayName = KeyboardInputText(current),
                    DeviceLabel = DeviceLabelOf(current.DeviceGuid),
                    Input = current,
                });
            var view = new System.Windows.Data.ListCollectionView(list);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(KeyboardChoice.DeviceLabel)));
            return view;
        }

        /// <summary>An input's text: its name in the slot's lists, an axis
        /// half's direction, and a combination's two parts.</summary>
        internal string KeyboardInputText(KeyboardInput input)
        {
            if (input.IsEmpty) return Strings.Instance.Menu_NotRecorded;
            string text = LegText(input.Descriptor, input.DeviceGuid);
            if (input.HasAnd) text += " + " + LegText(input.AndDescriptor, input.AndDeviceGuid);
            return text;
        }

        private string LegText(string descriptor, string deviceGuid)
        {
            string d = (descriptor ?? "").Trim();
            string clean = SourceCoercion.StripLegacyPrefix(d, out bool invert, out bool half);
            string name = ChoiceName(d, deviceGuid) ?? ChoiceName(clean, deviceGuid) ?? clean;
            if (!half || ChoiceName(d, deviceGuid) != null) return name;
            bool stick = clean.StartsWith("Gamepad ", StringComparison.Ordinal) && (clean.EndsWith("StickX", StringComparison.Ordinal)
                || clean.EndsWith("StickY", StringComparison.Ordinal));
            string dir = stick
                ? (clean.EndsWith("X", StringComparison.Ordinal) ? (invert ? "←" : "→") : (invert ? "↑" : "↓"))
                : (invert ? "-" : "+");
            return name + " " + dir;
        }

        /// <summary>The name the slot's lists give an input on a device, the
        /// device's own entry first, then any entry of the input.</summary>
        private string ChoiceName(string descriptor, string deviceGuid)
        {
            var provided = InputChoicesProvider?.Invoke();
            if (provided == null || string.IsNullOrEmpty(descriptor)) return null;
            string any = null;
            foreach (var c in provided)
            {
                if (c == null || !string.Equals(c.Descriptor, descriptor, StringComparison.Ordinal)) continue;
                if (string.Equals(c.DeviceGuid ?? "", deviceGuid ?? "", StringComparison.OrdinalIgnoreCase)) return c.DisplayName;
                any ??= c.DisplayName;
            }
            return any;
        }

        /// <summary>The group an input on a device shows under: "(Any Device)"
        /// for one read on every controller, the device's label in the
        /// slot's lists, else the controller's name marked as not on the
        /// slot. An input stays on a controller that leaves the slot, and the
        /// runtime reads it on that controller alone, so it never shows as
        /// one any device presses.</summary>
        private string DeviceLabelOf(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid)) return Strings.Instance.Mapping_AnyDevice;
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
                foreach (var c in provided)
                    if (c != null && string.Equals(c.DeviceGuid ?? "", deviceGuid, StringComparison.OrdinalIgnoreCase))
                        return c.DeviceLabel;
            var device = Guid.TryParse(deviceGuid, out var g) ? PadForge.Common.Input.SettingsManager.FindDeviceByInstanceGuid(g) : null;
            string name = device != null ? PadForge.Services.InputService.LocalizedDeviceName(device) : null;
            if (string.IsNullOrEmpty(name)) name = deviceGuid.Length > 8 ? deviceGuid.Substring(0, 8) + "…" : deviceGuid;
            return string.Format(Strings.Instance.Menu_Keyboard_DeviceNotOnSlot_Format, name);
        }

        /// <summary>A row's subtitle: the device its input is read on, both
        /// devices for a combination across two, or that it has none.</summary>
        internal string KeyboardSubtitle(KeyboardInput input)
        {
            if (input.IsEmpty) return Strings.Instance.Menu_NotRecorded;
            string a = DeviceLabelOf(input.DeviceGuid);
            if (!input.HasAnd) return a;
            string b = DeviceLabelOf(input.AndDeviceGuid);
            return string.Equals(a, b, StringComparison.Ordinal) ? a : a + " + " + b;
        }

        /// <summary>True while a row's list is being replaced: the selector
        /// clears its selection during the swap, which is no edit
        /// (<see cref="_raisingInputChoices"/>'s reason).</summary>
        internal bool RaisingKeyboardChoices;

        // ── Recording ────────────────────────────────────────────

        private KeyboardInputRow _recordingRow;

        /// <summary>The row whose record button is armed, or null.</summary>
        public KeyboardInputRow RecordingKeyboardRow
        {
            get => _recordingRow;
            set
            {
                if (ReferenceEquals(_recordingRow, value)) return;
                var old = _recordingRow;
                _recordingRow = value;
                old?.RaiseRecording();
                value?.RaiseRecording();
                OnPropertyChanged();
            }
        }

        private bool _keyboardMapAll;

        /// <summary>True while Map All records the controls in order.</summary>
        public bool KeyboardMapAllActive
        {
            get => _keyboardMapAll;
            set
            {
                if (_keyboardMapAll == value) return;
                _keyboardMapAll = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MapAllText));
            }
        }

        public string MapAllText => _keyboardMapAll ? Strings.Instance.Common_Stop : Strings.Instance.Menu_Keyboard_MapAll;
    }

    /// <summary>
    /// One input row of a keyboard menu (#566): a control of the layout, or
    /// Opens With. Its list picks an input on one device, its record button
    /// takes a press or a combination on any device of the slot (an axis on
    /// a pointer's X and Y rows), its press point sets where an analog input
    /// presses, and its reset puts the layout's default back (Opens With's
    /// default is none).
    /// </summary>
    public sealed partial class KeyboardInputRow : ObservableObject
    {
        private readonly MenuEditorItem _owner;

        /// <summary>The control, or null for Opens With.</summary>
        public KeyboardControlAction? Action { get; }

        public KeyboardLayout Layout { get; }

        public string Label { get; }

        public KeyboardInputRow(MenuEditorItem owner, KeyboardControlAction? action, KeyboardLayout layout, string label)
        {
            _owner = owner;
            Action = action;
            Layout = layout;
            Label = label;
        }

        public bool IsOpensWith => Action == null;

        /// <summary>A pointer's X or Y row, which takes a place across a
        /// range and records an axis.</summary>
        public bool RecordsAnAxis => Action is { } a && KeyboardMenuDefaults.IsPointerAxis(a);

        /// <summary>Whether the row reads an input. A pointer's X and Y take
        /// an input with a place (<see cref="MenuEditorItem.IsPositionDescriptor"/>).
        /// A key, a pointer's touch and click, and Opens With press through
        /// the button read, which never presses on a touchpad finger's place
        /// (<see cref="MappingItem.ButtonReadPressesNothing"/>), so none of
        /// them takes one. A steering row reads its input's depth and keeps
        /// it. Opens With takes every other input, gestures and menu cells
        /// too. The controls take all but those two, which stop while the
        /// keyboard holds the controller at rest.</summary>
        internal bool Accepts(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return false;
            if (RecordsAnAxis) return MenuEditorItem.IsPositionDescriptor(descriptor);
            if (!(Action is { } a && KeyboardMenuDefaults.IsSteer(a))
                && MappingItem.ButtonReadPressesNothing(descriptor)) return false;
            if (IsOpensWith) return true;
            return !MenuEditorItem.IsSlotEvent(descriptor)
                   && !descriptor.TrimStart().StartsWith("Menu ", StringComparison.Ordinal);
        }

        /// <summary>The row's id for UI Automation, stable across languages.</summary>
        public string AutomationId => "MenuKeyboard_" + (Action?.ToString() ?? "OpensWith");

        public KeyboardInput Input => Action is { } a
            ? KeyboardMenuDefaults.Effective(_owner.Entry, Layout, a)
            : KeyboardMenuDefaults.OpenInput(_owner.Entry);

        public KeyboardInput Default => Action is { } a ? KeyboardMenuDefaults.DefaultInput(Layout, a) : KeyboardInput.None;

        public System.ComponentModel.ICollectionView ChoicesView => _owner.KeyboardChoicesView(this);

        public string SelectedKey
        {
            get => MenuEditorItem.KeyOf(Input);
            set
            {
                if (_owner.RaisingKeyboardChoices || value == null) return;
                if (value == MenuEditorItem.KeyOf(Input)) return;
                if (ChoicesView.SourceCollection is IEnumerable<KeyboardChoice> items)
                    foreach (var c in items)
                        if (c.Key == value)
                        {
                            // The press point stays with the row, as a
                            // Mappings row's deadzone stays with its source.
                            _owner.SetKeyboardInput(this, c.Input with { Threshold = Input.Threshold });
                            return;
                        }
            }
        }

        /// <summary>The device the input is read on, or, while the row records,
        /// what to do.</summary>
        public string Subtitle => IsRecording
            ? (RecordsAnAxis ? Strings.Instance.Menu_Keyboard_RecordingAxisPrompt : Strings.Instance.Menu_Keyboard_RecordingPrompt)
            : _owner.KeyboardSubtitle(Input);

        /// <summary>The reset button's name: what the default is.</summary>
        public string ResetTip => string.Format(Strings.Instance.Menu_Keyboard_Reset_Format, Label,
            Default.IsEmpty ? Strings.Instance.Menu_NotRecorded : _owner.KeyboardInputText(Default));

        public bool IsRecording => ReferenceEquals(_owner.RecordingKeyboardRow, this);

        public string RecordIcon => IsRecording ? MenuEditorItem.StopGlyph : MenuEditorItem.RecordGlyph;

        public string RecordText => IsRecording ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;

        public RelayCommand ResetInputCommand => _reset ??= new RelayCommand(() => _owner.SetKeyboardInput(this, Default));
        private RelayCommand _reset;

        // ── The press point ──────────────────────────────────────

        /// <summary>Whether the row's press point acts, so its Axis-to-Button
        /// Deadzone shows. A key, a pointer's touch or click and Opens With
        /// press at it where a Mappings row with a button target would, for
        /// either input of a combination
        /// (<see cref="MappingItem.ButtonReadThresholds"/>). A steering row
        /// reads its input's depth, so only a combination's second input,
        /// which holds the steering as a press, has one. A pointer's X and Y
        /// read a place, and have none.</summary>
        public bool ShowsThreshold
        {
            get
            {
                var input = Input;
                if (input.IsEmpty || RecordsAnAxis) return false;
                bool b = input.HasAnd && Thresholds(input.AndDescriptor);
                if (Action is { } a && KeyboardMenuDefaults.IsSteer(a)) return b;
                return b || Thresholds(input.Descriptor);
            }
        }

        private static bool Thresholds(string descriptor)
            => MappingItem.ButtonReadThresholds(SourceCoercion.StripLegacyPrefix((descriptor ?? "").Trim(), out _, out _));

        /// <summary>The press point, 1 to 99 percent of the input's travel,
        /// 50 unless set. A held press lets go at 70% of it.</summary>
        public int ThresholdPercent
        {
            get => Input.ThresholdPercent;
            set
            {
                int v = Math.Clamp(value, 1, 99);
                var input = Input;
                if (input.IsEmpty || input.ThresholdPercent == v) return;
                _owner.SetKeyboardInput(this, input with { Threshold = v });
            }
        }

        /// <summary>The press point's name for screen readers, with the row's.</summary>
        public string ThresholdName => string.Format(Strings.Instance.Menu_Keyboard_Threshold_Name_Format, Label);

        /// <summary>The press point's id for UI Automation.</summary>
        public string ThresholdAutomationId => AutomationId + "_Deadzone";

        public RelayCommand ResetThresholdCommand => _resetThreshold ??= new RelayCommand(
            () => ThresholdPercent = KeyboardInput.DefaultThreshold);
        private RelayCommand _resetThreshold;

        internal void RaiseRecording()
        {
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordIcon));
            OnPropertyChanged(nameof(RecordText));
            OnPropertyChanged(nameof(Subtitle));
        }

        /// <summary>Re-reads the row after its input changed or the slot's
        /// lists did: the list first, then the selection against it.</summary>
        internal void Refresh()
        {
            _owner.RaisingKeyboardChoices = true;
            try
            {
                OnPropertyChanged(nameof(ChoicesView));
                OnPropertyChanged(nameof(SelectedKey));
            }
            finally
            {
                _owner.RaisingKeyboardChoices = false;
            }
            OnPropertyChanged(nameof(Input));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(ResetTip));
            OnPropertyChanged(nameof(ShowsThreshold));
            OnPropertyChanged(nameof(ThresholdPercent));
        }
    }
}
