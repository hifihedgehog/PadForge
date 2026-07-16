using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One pickable menu host surface (Left Stick / Right Stick /
    /// Touchpad N).</summary>
    public sealed class MenuHostOption
    {
        public string Descriptor { get; init; } = "";
        public string Label { get; init; } = "";
        public bool IsTouchpad { get; init; }
        public override string ToString() => Label;
    }

    /// <summary>Generic labeled int option (fire mode, half, binding kind,
    /// key, button). <see cref="Description"/> feeds the option's tooltip
    /// and, for fire modes, the persistent caption under the combo; empty
    /// for options that need no explanation.</summary>
    public sealed class MenuIntOption
    {
        public int Value { get; init; }
        public string Label { get; init; } = "";
        public string Description { get; init; } = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// Editor VM for one radial / touch menu (#9 B-17). Wraps the LIVE
    /// <see cref="MenuDefinitionEntry"/> stored on the slot's MappingSet
    /// (write-through, like the mapping grid edits its live rows); every
    /// mutation raises <see cref="Changed"/> so the owning PadViewModel
    /// can mark the settings dirty. Cells materialize lazily: the editor
    /// shows every cell position for the current shape, and a cell's
    /// entry exists in <see cref="MenuDefinitionEntry.Items"/> only once
    /// it carries a label or a binding.
    /// </summary>
    public class MenuEditorItem : ObservableObject
    {
        internal readonly MenuDefinitionEntry Entry;

        /// <summary>Raised after any persisted field changes.</summary>
        public event Action Changed;

        /// <summary>Static option lists rebuild once per language change
        /// (static handlers run before instance ones, so the per-instance
        /// refresh below always re-reads fresh lists). Same pattern as
        /// StickConfigItem's preset names.</summary>
        static MenuEditorItem()
        {
            Strings.CultureChanged += static () =>
            {
                KindOptionsBacking = BuildKindOptions();
                HostOptionsBacking = BuildHostOptions();
                HostHalfOptionsBacking = BuildHostHalfOptions();
                FireOptionsBacking = BuildFireOptions();
            };
        }

        public MenuEditorItem(MenuDefinitionEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            RebuildCells();
            // Weak event (Strings.CultureChanged): no unsubscribe needed.
            Strings.CultureChanged += OnCultureChanged;
        }

        private MacroButtonStyle _buttonStyle = MacroButtonStyle.Xbox360;

        /// <summary>The slot's button lettering, derived from its output
        /// type exactly like the macro editor
        /// (MacroButtonNames.DeriveStyle). The owning PadViewModel sets it
        /// at construction and re-syncs it when the slot type changes; the
        /// cells' pickers re-letter and re-value themselves on the flip.</summary>
        public MacroButtonStyle ButtonStyle
        {
            get => _buttonStyle;
            set
            {
                if (_buttonStyle == value) return;
                _buttonStyle = value;
                foreach (var cell in Cells)
                    cell.RefreshButtonStyle();
            }
        }

        private int _extendedButtonCount = 11;

        /// <summary>Raw button count of the Extended slot's custom layout
        /// (bounds the numbered picker), mirroring the macro editor's
        /// CustomButtonCount source.</summary>
        public int ExtendedButtonCount
        {
            get => _extendedButtonCount;
            set
            {
                value = Math.Max(1, value);
                if (_extendedButtonCount == value) return;
                _extendedButtonCount = value;
                if (_buttonStyle == MacroButtonStyle.Numbered)
                    foreach (var cell in Cells)
                        cell.RefreshButtonStyle();
            }
        }

        /// <summary>Re-raises every localized computed property after a
        /// language change. The lists themselves were already rebuilt by
        /// the static handler; SelectedHost must re-raise too because the
        /// rebuild replaced the option INSTANCES and the host combo binds
        /// SelectedItem by reference.</summary>
        private void OnCultureChanged()
        {
            OnPropertyChanged(nameof(KindOptions));
            OnPropertyChanged(nameof(HostOptions));
            OnPropertyChanged(nameof(HostHalfOptions));
            OnPropertyChanged(nameof(FireOptions));
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(SelectedFireDescription));
            foreach (var cell in Cells)
                cell.RefreshCulture();
        }

        private void OnEdited()
        {
            Changed?.Invoke();
        }

        public string Name
        {
            get => Entry.Name;
            set
            {
                if (Entry.Name == value) return;
                Entry.Name = value ?? "";
                OnPropertyChanged();
                OnEdited();
            }
        }

        public bool Enabled
        {
            get => Entry.Enabled;
            set { if (Entry.Enabled != value) { Entry.Enabled = value; OnPropertyChanged(); OnEdited(); } }
        }

        public bool IsRadial => Entry.Kind == MenuKind.Radial;

        /// <summary>0 = Radial, 1 = Grid (combo index = enum value).</summary>
        public int KindIndex
        {
            get => (int)Entry.Kind;
            set
            {
                var kind = value == 1 ? MenuKind.Grid : MenuKind.Radial;
                if (Entry.Kind == kind) return;
                Entry.Kind = kind;
                if (kind == MenuKind.Grid) Entry.HasCenter = false;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRadial));
                RebuildCells();
                OnEdited();
            }
        }

        // NOTE: the option lists below are instance accessors over static
        // backing fields on purpose. WPF's {Binding X} resolves against
        // the DataContext INSTANCE and never finds static properties, so
        // a static-only list binds silently empty. The backings are NOT
        // readonly: they capture localized labels, so the static
        // CultureChanged handler in the static ctor rebuilds them on every
        // language change (a readonly one-shot capture shipped stale
        // dropdowns after a live language switch, owner report 2026-07-16).
        private static IReadOnlyList<MenuIntOption> KindOptionsBacking = BuildKindOptions();

        private static IReadOnlyList<MenuIntOption> BuildKindOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Style_Radial },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Style_Grid },
        };

        public IReadOnlyList<MenuIntOption> KindOptions => KindOptionsBacking;

        // ── Host surface ─────────────────────────────────────────

        private static IReadOnlyList<MenuHostOption> HostOptionsBacking = BuildHostOptions();

        private static IReadOnlyList<MenuHostOption> BuildHostOptions() => new[]
        {
            new MenuHostOption { Descriptor = "Gamepad LeftStick", Label = Strings.Instance.Menu_Host_LeftStick },
            new MenuHostOption { Descriptor = "Gamepad RightStick", Label = Strings.Instance.Menu_Host_RightStick },
            new MenuHostOption { Descriptor = "Touchpad 0", Label = string.Format(Strings.Instance.Menu_Host_Touchpad_Format, 1), IsTouchpad = true },
            new MenuHostOption { Descriptor = "Touchpad 1", Label = string.Format(Strings.Instance.Menu_Host_Touchpad_Format, 2), IsTouchpad = true },
            new MenuHostOption { Descriptor = "Touchpad 2", Label = string.Format(Strings.Instance.Menu_Host_Touchpad_Format, 3), IsTouchpad = true },
        };

        public IReadOnlyList<MenuHostOption> HostOptions => HostOptionsBacking;

        public MenuHostOption SelectedHost
        {
            get
            {
                foreach (var h in HostOptions)
                    if (h.Descriptor == Entry.HostDescriptor) return h;
                return HostOptions[1]; // right stick default
            }
            set
            {
                if (value == null || Entry.HostDescriptor == value.Descriptor) return;
                Entry.HostDescriptor = value.Descriptor;
                if (!value.IsTouchpad) Entry.HostHalf = 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HostIsTouchpad));
                OnPropertyChanged(nameof(HostHalfIndex));
                OnEdited();
            }
        }

        public bool HostIsTouchpad => SelectedHost.IsTouchpad;

        private static IReadOnlyList<MenuIntOption> HostHalfOptionsBacking = BuildHostHalfOptions();

        private static IReadOnlyList<MenuIntOption> BuildHostHalfOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Half_Whole },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Half_Left },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Half_Right },
        };

        public IReadOnlyList<MenuIntOption> HostHalfOptions => HostHalfOptionsBacking;

        public int HostHalfIndex
        {
            get => Math.Clamp(Entry.HostHalf, 0, 2);
            set { if (Entry.HostHalf != value) { Entry.HostHalf = Math.Clamp(value, 0, 2); OnPropertyChanged(); OnEdited(); } }
        }

        /// <summary>Folds a freeform-recorded descriptor onto a host
        /// choice: stick axis / click reads pick the stick, touchpad
        /// family reads pick the pad. Returns false when the recorded
        /// input has no host surface (buttons, gyro, keys).</summary>
        public bool TryApplyRecordedHost(string descriptor)
        {
            string d = (descriptor ?? "").Trim();
            if (d.Length == 0) return false;
            string canonical = PadForge.Engine.Common.Mapping.SourceCoercion.ResolveGamepadAlias(d) ?? d;

            string host = null;
            if (d.Contains("LeftStick", StringComparison.OrdinalIgnoreCase)
                || canonical is "Axis 0" or "Axis 1")
                host = "Gamepad LeftStick";
            else if (d.Contains("RightStick", StringComparison.OrdinalIgnoreCase)
                || canonical is "Axis 3" or "Axis 4")
                host = "Gamepad RightStick";
            else if (d.StartsWith("Touchpad ", StringComparison.Ordinal))
            {
                var parts = d.Split(' ');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int pad) && pad >= 0 && pad <= 2)
                    host = $"Touchpad {pad}";
            }
            if (host == null) return false;

            foreach (var h in HostOptions)
            {
                if (h.Descriptor == host)
                {
                    SelectedHost = h;
                    return true;
                }
            }
            return false;
        }

        private bool _hostRecording;

        /// <summary>True while the freeform recorder is capturing the host
        /// input (drives the record button's glyph swap).</summary>
        public bool HostRecording
        {
            get => _hostRecording;
            set
            {
                if (SetProperty(ref _hostRecording, value))
                    OnPropertyChanged(nameof(HostRecordIcon));
            }
        }

        /// <summary>Segoe MDL2 glyph for the host record button: Stop
        /// (E71A) while recording, Record (E7C8) while idle, mirroring the
        /// Aim Engage record button.</summary>
        public string HostRecordIcon => _hostRecording ? "" : "";

        // ── Fire mode / shape ────────────────────────────────────

        private static IReadOnlyList<MenuIntOption> FireOptionsBacking = BuildFireOptions();

        private static IReadOnlyList<MenuIntOption> BuildFireOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Fire_Click,
                                Description = Strings.Instance.Menu_Fire_Click_Desc },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Fire_ClickRelease,
                                Description = Strings.Instance.Menu_Fire_ClickRelease_Desc },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Fire_TouchRelease,
                                Description = Strings.Instance.Menu_Fire_TouchRelease_Desc },
            new MenuIntOption { Value = 3, Label = Strings.Instance.Menu_Fire_Always,
                                Description = Strings.Instance.Menu_Fire_Always_Desc },
        };

        public IReadOnlyList<MenuIntOption> FireOptions => FireOptionsBacking;

        /// <summary>The selected fire mode's explanation, rendered as a
        /// persistent caption under the combo. A tooltip alone was not
        /// enough: "On Click" reads as self-explanatory while actually
        /// requiring a stick / trackpad CLICK, and the one user test we
        /// have (owner, 2026-07-16) walked straight past it.</summary>
        public string SelectedFireDescription =>
            FireOptionsBacking[Math.Clamp((int)Entry.FireType, 0, 3)].Description;

        public int FireTypeIndex
        {
            get => (int)Entry.FireType;
            set
            {
                var f = (MenuFireType)Math.Clamp(value, 0, 3);
                if (Entry.FireType == f) return;
                Entry.FireType = f;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFireDescription));
                OnEdited();
            }
        }

        public int CellCount
        {
            get => Entry.CellCount;
            set
            {
                int v = Math.Clamp(value, 1, 20);
                if (Entry.CellCount == v) return;
                Entry.CellCount = v;
                OnPropertyChanged();
                RebuildCells();
                OnEdited();
            }
        }

        public bool HasCenter
        {
            get => Entry.HasCenter;
            set
            {
                if (Entry.HasCenter == value) return;
                Entry.HasCenter = value;
                OnPropertyChanged();
                RebuildCells();
                OnEdited();
            }
        }

        public bool ShowLabels
        {
            get => Entry.ShowLabels;
            set { if (Entry.ShowLabels != value) { Entry.ShowLabels = value; OnPropertyChanged(); OnEdited(); } }
        }

        public int PosXPercent
        {
            get => Entry.PosXPercent;
            set { int v = Math.Clamp(value, 0, 100); if (Entry.PosXPercent != v) { Entry.PosXPercent = v; OnPropertyChanged(); OnEdited(); } }
        }

        public int PosYPercent
        {
            get => Entry.PosYPercent;
            set { int v = Math.Clamp(value, 0, 100); if (Entry.PosYPercent != v) { Entry.PosYPercent = v; OnPropertyChanged(); OnEdited(); } }
        }

        public int ScalePercent
        {
            get => Entry.ScalePercent;
            set { int v = Math.Clamp(value, 10, 400); if (Entry.ScalePercent != v) { Entry.ScalePercent = v; OnPropertyChanged(); OnEdited(); } }
        }

        public int OpacityPercent
        {
            get => Entry.OpacityPercent;
            set { int v = Math.Clamp(value, 5, 100); if (Entry.OpacityPercent != v) { Entry.OpacityPercent = v; OnPropertyChanged(); OnEdited(); } }
        }

        public int EngageDeadzonePercent
        {
            get => Entry.EngageDeadzonePercent;
            set { int v = Math.Clamp(value, 1, 95); if (Entry.EngageDeadzonePercent != v) { Entry.EngageDeadzonePercent = v; OnPropertyChanged(); OnEdited(); } }
        }

        // ── Per-row resets (canon: every setting row has one) ────

        public RelayCommand ResetHostCommand => _resetHost ??= new RelayCommand(() =>
        {
            SelectedHost = HostOptions[1];
            HostHalfIndex = 0;
        });
        private RelayCommand _resetHost;

        public RelayCommand ResetFireCommand => _resetFire ??= new RelayCommand(() => FireTypeIndex = 0);
        private RelayCommand _resetFire;

        public RelayCommand ResetStyleCommand => _resetStyle ??= new RelayCommand(() => KindIndex = 0);
        private RelayCommand _resetStyle;

        public RelayCommand ResetHostHalfCommand => _resetHostHalf ??= new RelayCommand(() => HostHalfIndex = 0);
        private RelayCommand _resetHostHalf;

        public RelayCommand ResetCellsCommand => _resetCells ??= new RelayCommand(() =>
        {
            CellCount = 4;
            HasCenter = false;
        });
        private RelayCommand _resetCells;

        public RelayCommand ResetGeometryCommand => _resetGeometry ??= new RelayCommand(() =>
        {
            PosXPercent = 50;
            PosYPercent = 50;
            ScalePercent = 100;
            OpacityPercent = 90;
            ShowLabels = true;
        });
        private RelayCommand _resetGeometry;

        public RelayCommand ResetDeadzoneCommand => _resetDeadzone ??= new RelayCommand(() => EngageDeadzonePercent = 25);
        private RelayCommand _resetDeadzone;

        // ── Cells ────────────────────────────────────────────────

        public ObservableCollection<MenuCellItem> Cells { get; } = new();

        /// <summary>Rebuilds the visible cell rows for the current shape:
        /// grid 0..N-1, radial (optional center 0 +) ring 1..N. Existing
        /// item entries keep their data; out-of-range entries are pruned
        /// from the definition so the serialized shape matches the editor.</summary>
        internal void RebuildCells()
        {
            Cells.Clear();

            var byIndex = new Dictionary<int, MenuItemDefinition>();
            if (Entry.Items != null)
            {
                foreach (var it in Entry.Items)
                    if (it != null) byIndex[it.Index] = it;
            }

            void AddCell(int index, bool isCenter)
            {
                byIndex.TryGetValue(index, out var item);
                Cells.Add(new MenuCellItem(this, index, isCenter, item));
            }

            if (Entry.Kind == MenuKind.Radial)
            {
                if (Entry.HasCenter) AddCell(0, isCenter: true);
                for (int k = 1; k <= Entry.CellCount; k++) AddCell(k, isCenter: false);
            }
            else
            {
                for (int k = 0; k < Entry.CellCount; k++) AddCell(k, isCenter: false);
            }

            // Prune entries the shape no longer reaches (a shrunken ring's
            // tail, a removed center) so exports and the overlay agree
            // with what the editor shows.
            if (Entry.Items != null)
            {
                Entry.Items.RemoveAll(it => it == null
                    || (Entry.Kind == MenuKind.Radial
                        ? it.Index > Entry.CellCount || (it.Index == 0 && !Entry.HasCenter)
                        : it.Index >= Entry.CellCount || it.Index < 0));
            }
        }

        /// <summary>Write-through for a cell edit: materializes the item
        /// entry on first use, drops it again when fully cleared.</summary>
        internal MenuItemDefinition EnsureItem(int index)
        {
            Entry.Items ??= new List<MenuItemDefinition>();
            foreach (var it in Entry.Items)
                if (it != null && it.Index == index) return it;
            var fresh = new MenuItemDefinition { Index = index };
            Entry.Items.Add(fresh);
            Entry.Items.Sort((a, b) => (a?.Index ?? 0).CompareTo(b?.Index ?? 0));
            return fresh;
        }

        internal void DropItemIfEmpty(MenuItemDefinition item)
        {
            if (item == null || Entry.Items == null) return;
            if (string.IsNullOrEmpty(item.Label) && item.VirtualKey <= 0
                && item.XboxButtons == 0 && item.ExtendedButton <= 0)
                Entry.Items.Remove(item);
        }

        internal void RaiseChanged() => OnEdited();
    }

    /// <summary>One cell row in the menu editor: label + one direct
    /// binding (none / keyboard key / virtual-controller button).</summary>
    public class MenuCellItem : ObservableObject
    {
        private readonly MenuEditorItem _owner;
        private MenuItemDefinition _item; // null until the cell has content

        public int Index { get; }

        public bool IsCenter { get; }

        public string Header => IsCenter
            ? Strings.Instance.Menu_CellCenter
            : string.Format(Strings.Instance.Menu_CellIndex_Format, Index);

        public MenuCellItem(MenuEditorItem owner, int index, bool isCenter, MenuItemDefinition item)
        {
            _owner = owner;
            Index = index;
            IsCenter = isCenter;
            _item = item;
        }

        public string Label
        {
            get => _item?.Label ?? "";
            set
            {
                string v = value ?? "";
                if ((_item?.Label ?? "") == v) return;
                if (v.Length == 0 && _item == null) return;
                _item ??= _owner.EnsureItem(Index);
                _item.Label = v;
                _owner.DropItemIfEmpty(_item);
                if (_owner.Entry.Items?.Contains(_item) != true) _item = null;
                OnPropertyChanged();
                _owner.RaiseChanged();
            }
        }

        // Rebuilt on language change by the static ctor's CultureChanged
        // handler; a readonly capture shipped stale labels after a live
        // language switch.
        private static IReadOnlyList<MenuIntOption> BindingKindOptionsBacking = BuildBindingKindOptions();

        private static IReadOnlyList<MenuIntOption> BuildBindingKindOptions() => new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Binding_None },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Binding_Key },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Binding_Button },
        };

        /// <summary>Instance accessor (WPF instance bindings never see
        /// statics).</summary>
        public IReadOnlyList<MenuIntOption> BindingKindOptions => BindingKindOptionsBacking;

        static MenuCellItem()
        {
            Strings.CultureChanged += static () =>
                BindingKindOptionsBacking = BuildBindingKindOptions();
        }

        /// <summary>Called by the owning editor's culture handler: re-raises
        /// every localized property on this cell row. The owner drives it
        /// (rather than each cell subscribing) so the raise order stays
        /// after the static list rebuilds.</summary>
        internal void RefreshCulture()
        {
            OnPropertyChanged(nameof(Header));
            OnPropertyChanged(nameof(BindingKindOptions));
            OnPropertyChanged(nameof(KeyOptions));
            OnPropertyChanged(nameof(ButtonOptions));
            OnPropertyChanged(nameof(SelectedKeyVk));
            OnPropertyChanged(nameof(SelectedButtonFlag));
        }

        /// <summary>0 = none, 1 = key, 2 = VC button (Xbox mask on
        /// Xbox / PlayStation slots, 1-based raw button number on
        /// Extended slots, per the owner's button style).</summary>
        public int BindingKind
        {
            get => _item == null ? 0
                : _item.VirtualKey > 0 ? 1
                : (_item.XboxButtons != 0 || _item.ExtendedButton > 0) ? 2 : 0;
            set
            {
                int cur = BindingKind;
                if (cur == value) return;
                if (value == 0)
                {
                    if (_item != null)
                    {
                        _item.VirtualKey = 0;
                        _item.XboxButtons = 0;
                        _item.ExtendedButton = 0;
                        _owner.DropItemIfEmpty(_item);
                        if (_owner.Entry.Items?.Contains(_item) != true) _item = null;
                    }
                }
                else
                {
                    _item ??= _owner.EnsureItem(Index);
                    if (value == 1)
                    {
                        _item.XboxButtons = 0;
                        _item.ExtendedButton = 0;
                        if (_item.VirtualKey <= 0) _item.VirtualKey = 0x20; // Space
                    }
                    else
                    {
                        _item.VirtualKey = 0;
                        if (_owner.ButtonStyle == MacroButtonStyle.Numbered)
                        {
                            _item.XboxButtons = 0;
                            if (_item.ExtendedButton <= 0) _item.ExtendedButton = 1;
                        }
                        else
                        {
                            _item.ExtendedButton = 0;
                            if (_item.XboxButtons == 0)
                                _item.XboxButtons = PadForge.Engine.Gamepad.A;
                        }
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowKeyPicker));
                OnPropertyChanged(nameof(ShowButtonPicker));
                OnPropertyChanged(nameof(SelectedKeyVk));
                OnPropertyChanged(nameof(SelectedButtonFlag));
                _owner.RaiseChanged();
            }
        }

        public bool ShowKeyPicker => BindingKind == 1;
        public bool ShowButtonPicker => BindingKind == 2;

        public System.Collections.Generic.IReadOnlyList<SocdKeyOption> KeyOptions
            => KbmSlotConfig.GetKeyOptions();

        public int SelectedKeyVk
        {
            get => _item?.VirtualKey ?? 0;
            set
            {
                if (value <= 0 || (_item?.VirtualKey ?? 0) == value) return;
                _item ??= _owner.EnsureItem(Index);
                _item.VirtualKey = value;
                _item.XboxButtons = 0;
                OnPropertyChanged();
                _owner.RaiseChanged();
            }
        }

        /// <summary>Button choices in the SLOT'S lettering, mirrored from
        /// the macro editor's tables (MacroButtonNames.DeriveStyle): Xbox
        /// letters on Xbox slots, DualShock symbols on PlayStation slots,
        /// numbered raw buttons (1..layout count) on Extended slots. Built
        /// fresh per read: the lists are tiny, and both a language switch
        /// and a slot-type switch re-raise this property.</summary>
        public IReadOnlyList<MenuIntOption> ButtonOptions
        {
            get
            {
                var list = new List<MenuIntOption>();
                if (_owner.ButtonStyle == MacroButtonStyle.Numbered)
                {
                    int count = Math.Clamp(_owner.ExtendedButtonCount, 1, 128);
                    for (int n = 1; n <= count; n++)
                        list.Add(new MenuIntOption
                        {
                            Value = n,
                            Label = string.Format(Strings.Instance.Extended_Button_Format, n),
                        });
                }
                else
                {
                    foreach (var (label, flag) in MacroButtonNames.GetButtonDefs(_owner.ButtonStyle))
                        list.Add(new MenuIntOption { Value = flag, Label = label });
                }
                return list;
            }
        }

        /// <summary>The picker's value: the Xbox mask on Xbox / PlayStation
        /// slots, the 1-based raw button number on Extended slots.</summary>
        public int SelectedButtonFlag
        {
            get => _owner.ButtonStyle == MacroButtonStyle.Numbered
                ? _item?.ExtendedButton ?? 0
                : _item?.XboxButtons ?? 0;
            set
            {
                if (value == 0 || SelectedButtonFlag == value) return;
                _item ??= _owner.EnsureItem(Index);
                if (_owner.ButtonStyle == MacroButtonStyle.Numbered)
                {
                    _item.ExtendedButton = value;
                    _item.XboxButtons = 0;
                }
                else
                {
                    _item.XboxButtons = value;
                    _item.ExtendedButton = 0;
                }
                _item.VirtualKey = 0;
                OnPropertyChanged();
                _owner.RaiseChanged();
            }
        }

        /// <summary>Called by the owner when the slot's output type changes:
        /// the lettering, the value space, and the option list all follow
        /// the new style.</summary>
        internal void RefreshButtonStyle()
        {
            OnPropertyChanged(nameof(ButtonOptions));
            OnPropertyChanged(nameof(SelectedButtonFlag));
            OnPropertyChanged(nameof(BindingKind));
            OnPropertyChanged(nameof(ShowKeyPicker));
            OnPropertyChanged(nameof(ShowButtonPicker));
        }

        public RelayCommand ResetCellCommand => _resetCell ??= new RelayCommand(() =>
        {
            Label = "";
            BindingKind = 0;
        });
        private RelayCommand _resetCell;
    }
}
