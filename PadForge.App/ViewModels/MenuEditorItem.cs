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
    /// key, button).</summary>
    public sealed class MenuIntOption
    {
        public int Value { get; init; }
        public string Label { get; init; } = "";
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

        public MenuEditorItem(MenuDefinitionEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            RebuildCells();
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
        // a static-only list binds silently empty.
        private static readonly IReadOnlyList<MenuIntOption> KindOptionsBacking = new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Style_Radial },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Style_Grid },
        };

        public IReadOnlyList<MenuIntOption> KindOptions => KindOptionsBacking;

        // ── Host surface ─────────────────────────────────────────

        private static readonly IReadOnlyList<MenuHostOption> HostOptionsBacking = new[]
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

        private static readonly IReadOnlyList<MenuIntOption> HostHalfOptionsBacking = new[]
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

        private static readonly IReadOnlyList<MenuIntOption> FireOptionsBacking = new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Fire_Click },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Fire_ClickRelease },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Fire_TouchRelease },
            new MenuIntOption { Value = 3, Label = Strings.Instance.Menu_Fire_Always },
        };

        public IReadOnlyList<MenuIntOption> FireOptions => FireOptionsBacking;

        public int FireTypeIndex
        {
            get => (int)Entry.FireType;
            set
            {
                var f = (MenuFireType)Math.Clamp(value, 0, 3);
                if (Entry.FireType == f) return;
                Entry.FireType = f;
                OnPropertyChanged();
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
            if (string.IsNullOrEmpty(item.Label) && item.VirtualKey <= 0 && item.XboxButtons == 0)
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

        private static readonly IReadOnlyList<MenuIntOption> BindingKindOptionsBacking = new[]
        {
            new MenuIntOption { Value = 0, Label = Strings.Instance.Menu_Binding_None },
            new MenuIntOption { Value = 1, Label = Strings.Instance.Menu_Binding_Key },
            new MenuIntOption { Value = 2, Label = Strings.Instance.Menu_Binding_Button },
        };

        /// <summary>Instance accessor (WPF instance bindings never see
        /// statics).</summary>
        public IReadOnlyList<MenuIntOption> BindingKindOptions => BindingKindOptionsBacking;

        /// <summary>0 = none, 1 = key, 2 = VC button.</summary>
        public int BindingKind
        {
            get => _item == null ? 0 : _item.VirtualKey > 0 ? 1 : _item.XboxButtons != 0 ? 2 : 0;
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
                        if (_item.VirtualKey <= 0) _item.VirtualKey = 0x20; // Space
                    }
                    else
                    {
                        _item.VirtualKey = 0;
                        if (_item.XboxButtons == 0)
                            _item.XboxButtons = PadForge.Engine.Gamepad.A;
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

        private static IReadOnlyList<MenuIntOption> _buttonOptions;

        /// <summary>Xbox-family button choices, labels mirrored from the
        /// macro editor's table (MacroButtonNames).</summary>
        public IReadOnlyList<MenuIntOption> ButtonOptions
        {
            get
            {
                if (_buttonOptions == null)
                {
                    var list = new List<MenuIntOption>();
                    foreach (var (label, flag) in MacroButtonNames.GetButtonDefs(MacroButtonStyle.Xbox360))
                        list.Add(new MenuIntOption { Value = flag, Label = label });
                    _buttonOptions = list;
                }
                return _buttonOptions;
            }
        }

        public int SelectedButtonFlag
        {
            get => _item?.XboxButtons ?? 0;
            set
            {
                if (value == 0 || (_item?.XboxButtons ?? 0) == value) return;
                _item ??= _owner.EnsureItem(Index);
                _item.XboxButtons = value;
                _item.VirtualKey = 0;
                OnPropertyChanged();
                _owner.RaiseChanged();
            }
        }

        public RelayCommand ResetCellCommand => _resetCell ??= new RelayCommand(() =>
        {
            Label = "";
            BindingKind = 0;
        });
        private RelayCommand _resetCell;
    }
}
