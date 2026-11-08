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
    /// Center Cell hides for a touch grid. What it adds: Layout, Opens With
    /// and Open When a Text Box Is Selected rows, Split's second steering
    /// input, the Keyboard Controls card with a row per control, and the
    /// Keyboard card. Every control writes through
    /// <see cref="KeyboardMenuDefaults.Set"/>, which keeps only what differs
    /// from the layout's default.
    /// </summary>
    public partial class MenuEditorItem
    {
        public bool IsKeyboard => Entry.Kind == MenuKind.Keyboard;

        /// <summary>The rows a radial ring or a touch grid uses: Cells,
        /// Engage Deadzone, Fire Mode, Show Labels and the cell bindings.</summary>
        public bool IsCellMenu => !IsKeyboard;

        /// <summary>Click Input: a keyboard types with its own controls.</summary>
        public bool ShowsClickRow => !IsKeyboard;

        /// <summary>Pad Half: a keyboard points with the whole touchpad.</summary>
        public bool ShowsHostHalfRow => HostIsTouchpad && !IsKeyboard;

        public bool IsSplitKeyboard => IsKeyboard && Entry.KeyboardLayout == KeyboardLayout.Split;

        /// <summary>The Custom Axes rows under the opener: a keyboard shows its
        /// own in the Keyboard Controls card.</summary>
        public bool ShowsCustomAxesRows => IsCustomHost && IsCellMenu;

        /// <summary>The Keyboard Controls card's count: the layout, and its
        /// steering rows and controls together.</summary>
        public string KeyboardControlsSummary => string.Format(Strings.Instance.Menu_Keyboard_Controls_Summary_Format,
            KeyboardLayoutOptions[Math.Clamp((int)Entry.KeyboardLayout, 0, 2)].Label,
            KeyboardControlRows.Count + (Entry.KeyboardLayout == KeyboardLayout.Split ? 2 : 1));

        /// <summary>Applies the keyboard's own defaults when a menu turns into
        /// one: the left stick steers, as on the Windows and Steam keyboards,
        /// and it sits at the bottom of the screen. Only a value still at the
        /// radial default changes.</summary>
        private void ApplyKeyboardDefaults()
        {
            if (string.Equals(Entry.HostDescriptor, "Gamepad RightStick", StringComparison.Ordinal))
                Entry.HostDescriptor = KeyboardMenuDefaults.Host;
            if (Entry.PosYPercent == 50) Entry.PosYPercent = KeyboardMenuDefaults.PosYPercent;
            Entry.HostHalf = 0;
        }

        private void RaiseKeyboardDependents()
        {
            OnPropertyChanged(nameof(IsKeyboard));
            OnPropertyChanged(nameof(IsCellMenu));
            OnPropertyChanged(nameof(ShowsClickRow));
            OnPropertyChanged(nameof(ShowsHostHalfRow));
            OnPropertyChanged(nameof(IsSplitKeyboard));
            OnPropertyChanged(nameof(IsCustomHost2));
            OnPropertyChanged(nameof(ShowsCustomAxesRows));
            OnPropertyChanged(nameof(KeyboardLayoutIndex));
            OnPropertyChanged(nameof(KeyboardLayoutDescription));
            OnPropertyChanged(nameof(HostInputLabel));
            OnPropertyChanged(nameof(HostInputTip));
            OnPropertyChanged(nameof(HostInputCaption));
            OnPropertyChanged(nameof(PosYPercent));
            RefreshHostOptions();
            RefreshHost2Options();
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
                OnPropertyChanged(nameof(IsSplitKeyboard));
                OnPropertyChanged(nameof(IsCustomHost2));
                OnPropertyChanged(nameof(HostInputLabel));
                OnPropertyChanged(nameof(HostInputTip));
                OnPropertyChanged(nameof(HostInputCaption));
                RebuildKeyboardControls();
                OnEdited();
            }
        }

        public string KeyboardLayoutDescription
            => KeyboardLayoutOptions[Math.Clamp((int)Entry.KeyboardLayout, 0, 2)].Description;

        public RelayCommand ResetKeyboardLayoutCommand => _resetKeyboardLayout ??= new RelayCommand(() => KeyboardLayoutIndex = 0);
        private RelayCommand _resetKeyboardLayout;

        /// <summary>The steering row's label in each layout: the selection in
        /// Grid, the left half in Split, the group in the wheel.</summary>
        private string KeyboardHostLabel => Entry.KeyboardLayout switch
        {
            KeyboardLayout.Split => Strings.Instance.Menu_Keyboard_SteerLeftHalf,
            KeyboardLayout.Wheel => Strings.Instance.Menu_Keyboard_PickGroup,
            _ => Strings.Instance.Menu_Host_SteerWith,
        };

        // ── Opening ──────────────────────────────────────────────

        public bool OpenOnTextFocus
        {
            get => Entry.OpenOnTextFocus;
            set { if (Entry.OpenOnTextFocus != value) { Entry.OpenOnTextFocus = value; OnPropertyChanged(); OnEdited(); } }
        }

        // ── Opens On: the macro trigger's press modes ────────────

        private static IReadOnlyList<MenuIntOption> OpenPressOptionsBacking = BuildOpenPressOptions();

        private static IReadOnlyList<MenuIntOption> BuildOpenPressOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Macro_OnPress },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Macro_HoldForMs, Description = Strings.Instance.Menu_Keyboard_OpenPress_Hold_Desc },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Macro_DoublePress, Description = Strings.Instance.Menu_Keyboard_OpenPress_Double_Desc },
        };

        public IReadOnlyList<MenuIntOption> OpenPressOptions => OpenPressOptionsBacking;

        public int OpenPressIndex
        {
            get => Math.Clamp((int)Entry.OpenPress, 0, 2);
            set
            {
                if (value < 0) return;
                var press = (KeyboardOpenPress)Math.Clamp(value, 0, 2);
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
                if (Entry.OpenPress == KeyboardOpenPress.Press) return;
                int v = Math.Clamp(value, 50, KeyboardMenuDefaults.MaxOpenPressMs);
                if (KeyboardMenuDefaults.OpenPressMs(Entry) == v && Entry.OpenPressMs != 0) return;
                Entry.OpenPressMs = v;
                OnPropertyChanged();
                OnEdited();
            }
        }

        public bool ShowsOpenPressMs => Entry.OpenPress != KeyboardOpenPress.Press;

        /// <summary>The time's name for the press: the macro trigger's Hold
        /// Time or Press Window.</summary>
        public string OpenPressMsLabel => Entry.OpenPress == KeyboardOpenPress.DoublePress
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

        // ── Keyboard card ────────────────────────────────────────

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
                if (!found)
                    list.Add(new MenuStringOption { Value = cur, Label = string.Format(s.Menu_Layer_Missing_Format, cur) });
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

        // ── Split's right half ───────────────────────────────────

        /// <summary>The right half's steering choices, the same list as the
        /// first one's.</summary>
        public IReadOnlyList<MenuHostOption> Host2Options => HostOptionsFor(Entry.Host2Descriptor);

        public MenuHostOption SelectedHost2
        {
            get
            {
                var options = Host2Options;
                foreach (var h in options)
                    if (h.Descriptor == Entry.Host2Descriptor) return h;
                return options[1];
            }
            set
            {
                if (value == null || Entry.Host2Descriptor == value.Descriptor) return;
                Entry.Host2Descriptor = value.Descriptor;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomHost2));
                RaiseCustomInput(MenuRecordTarget.Custom2X);
                RaiseCustomInput(MenuRecordTarget.Custom2Y);
                OnEdited();
            }
        }

        public bool IsCustomHost2 => IsSplitKeyboard
            && string.Equals(Entry.Host2Descriptor, "Custom", StringComparison.Ordinal);

        internal void RefreshHost2Options()
        {
            OnPropertyChanged(nameof(Host2Options));
            OnPropertyChanged(nameof(SelectedHost2));
            OnPropertyChanged(nameof(IsCustomHost2));
        }

        public RelayCommand ResetHost2Command => _resetHost2 ??= new RelayCommand(() =>
        {
            Entry.Host2Descriptor = KeyboardMenuDefaults.Host2;
            Entry.Custom2XDescriptor = "";
            Entry.Custom2YDescriptor = "";
            RefreshHost2Options();
            RaiseCustomInput(MenuRecordTarget.Custom2X);
            RaiseCustomInput(MenuRecordTarget.Custom2Y);
            OnEdited();
        });
        private RelayCommand _resetHost2;

        public IReadOnlyList<InputChoice> Custom2XChoices => BuildInputChoices(analog: true, Entry.Custom2XDescriptor);
        public IReadOnlyList<InputChoice> Custom2YChoices => BuildInputChoices(analog: true, Entry.Custom2YDescriptor);
        public System.ComponentModel.ICollectionView Custom2XChoicesView => BuildChoicesView(BuildInputChoices(analog: true, Entry.Custom2XDescriptor));
        public System.ComponentModel.ICollectionView Custom2YChoicesView => BuildChoicesView(BuildInputChoices(analog: true, Entry.Custom2YDescriptor));
        public string Custom2XSubtitle => SubtitleFor(Entry.Custom2XDescriptor, Strings.Instance.Menu_NotRecorded);
        public string Custom2YSubtitle => SubtitleFor(Entry.Custom2YDescriptor, Strings.Instance.Menu_NotRecorded);

        public string Custom2XSelected
        {
            get => Entry.Custom2XDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.Custom2X, value);
        }

        public string Custom2YSelected
        {
            get => Entry.Custom2YDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.Custom2Y, value);
        }

        public RelayCommand ResetCustom2XCommand => _resetCustom2X ??= new RelayCommand(() => SetCustomInput(MenuRecordTarget.Custom2X, ""));
        private RelayCommand _resetCustom2X;

        public RelayCommand ResetCustom2YCommand => _resetCustom2Y ??= new RelayCommand(() => SetCustomInput(MenuRecordTarget.Custom2Y, ""));
        private RelayCommand _resetCustom2Y;

        public string Host2RecordIcon => IconFor(MenuRecordTarget.Host2);
        public string Host2RecordText => TextFor(MenuRecordTarget.Host2);
        public string Custom2XRecordIcon => IconFor(MenuRecordTarget.Custom2X);
        public string Custom2YRecordIcon => IconFor(MenuRecordTarget.Custom2Y);
        public string Custom2XRecordText => TextFor(MenuRecordTarget.Custom2X);
        public string Custom2YRecordText => TextFor(MenuRecordTarget.Custom2Y);

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

        /// <summary>A control's name, the one its row and the issue's tables use.</summary>
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
                _ => a.ToString(),
            };
        }

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

        /// <summary>Applies a recorded press to a row. An input the keyboard
        /// cannot read while it is open is refused: a touchpad or mouse
        /// gesture and a menu cell come from state the keyboard holds at rest.</summary>
        public bool TryApplyKeyboardPress(KeyboardInputRow row, PadForge.Services.RecorderService.RecordedPress press)
        {
            if (row == null || string.IsNullOrWhiteSpace(press.Descriptor)) return false;
            if (IsSlotEvent(press.Descriptor) || IsSlotEvent(press.AndDescriptor)) return false;
            SetKeyboardInput(row, new KeyboardInput(press.Descriptor, press.DeviceGuid ?? "",
                press.AndDescriptor ?? "", press.AndDeviceGuid ?? ""));
            return true;
        }

        private static bool IsSlotEvent(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor)) return false;
            string clean = SourceCoercion.StripLegacyPrefix(descriptor.Trim(), out _, out _);
            return SourceCoercion.IsSlotEventDescriptor(clean);
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
        /// as the mapping picker shows them, less the families the keyboard
        /// cannot read while open (gestures, menu cells). The device-free
        /// "Gamepad ..." names sit under (Any Device). The row's own input,
        /// when no entry is it (an axis half, a combination), gets an entry
        /// of its own so the selection never lies.</summary>
        internal System.ComponentModel.ICollectionView KeyboardChoicesView(KeyboardInput current)
        {
            var list = new List<KeyboardChoice>();
            string curKey = KeyOf(current);
            bool found = curKey.Length == 0;
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
            {
                foreach (var c in provided)
                {
                    if (c == null || string.IsNullOrEmpty(c.Descriptor)) continue;
                    if (IsSlotEvent(c.Descriptor) || c.Descriptor.StartsWith("Menu ", StringComparison.Ordinal)) continue;
                    var input = new KeyboardInput(c.Descriptor, c.DeviceGuid ?? "");
                    string key = KeyOf(input);
                    if (key == curKey) found = true;
                    list.Add(new KeyboardChoice { Key = key, DisplayName = c.DisplayName, DeviceLabel = c.DeviceLabel, Input = input });
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

        private string DeviceLabelOf(string deviceGuid)
        {
            if (string.IsNullOrEmpty(deviceGuid)) return Strings.Instance.Mapping_AnyDevice;
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
                foreach (var c in provided)
                    if (c != null && string.Equals(c.DeviceGuid ?? "", deviceGuid, StringComparison.OrdinalIgnoreCase))
                        return c.DeviceLabel;
            return Strings.Instance.Mapping_AnyDevice;
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
    /// takes a press or a combination on any device of the slot, and its
    /// reset puts the layout's default back (Opens With's default is none).
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

        /// <summary>The row's id for UI Automation, stable across languages.</summary>
        public string AutomationId => "MenuKeyboard_" + (Action?.ToString() ?? "OpensWith");

        public KeyboardInput Input => Action is { } a
            ? KeyboardMenuDefaults.Effective(_owner.Entry, Layout, a)
            : KeyboardMenuDefaults.OpenInput(_owner.Entry);

        public KeyboardInput Default => Action is { } a ? KeyboardMenuDefaults.DefaultInput(Layout, a) : KeyboardInput.None;

        public System.ComponentModel.ICollectionView ChoicesView => _owner.KeyboardChoicesView(Input);

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
                            _owner.SetKeyboardInput(this, c.Input);
                            return;
                        }
            }
        }

        /// <summary>The device the input is read on, or, while the row records,
        /// what to do.</summary>
        public string Subtitle => IsRecording ? Strings.Instance.Menu_Keyboard_RecordingPrompt : _owner.KeyboardSubtitle(Input);

        /// <summary>The reset button's name: what the default is.</summary>
        public string ResetTip => string.Format(Strings.Instance.Menu_Keyboard_Reset_Format, Label,
            Default.IsEmpty ? Strings.Instance.Menu_NotRecorded : _owner.KeyboardInputText(Default));

        public bool IsRecording => ReferenceEquals(_owner.RecordingKeyboardRow, this);

        public string RecordIcon => IsRecording ? MenuEditorItem.StopGlyph : MenuEditorItem.RecordGlyph;

        public string RecordText => IsRecording ? Strings.Instance.Common_Recording : Strings.Instance.Common_Record;

        public RelayCommand ResetInputCommand => _reset ??= new RelayCommand(() => _owner.SetKeyboardInput(this, Default));
        private RelayCommand _reset;

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
        }
    }
}
