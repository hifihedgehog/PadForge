using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Engine.Menus;
using PadForge.Resources.Strings;

namespace PadForge.ViewModels
{
    /// <summary>One pickable menu opener (Left Stick / Right Stick /
    /// Touchpad N) on the PHYSICAL controller. Equality is by descriptor:
    /// the option lists rebuild on culture / capability changes, and the
    /// ComboBox's SelectedItem must keep matching across instance churn.</summary>
    public sealed class MenuHostOption
    {
        public string Descriptor { get; init; } = "";
        public string Label { get; init; } = "";
        public bool IsTouchpad { get; init; }
        public override string ToString() => Label;
        public override bool Equals(object obj)
            => obj is MenuHostOption o && string.Equals(o.Descriptor, Descriptor, StringComparison.Ordinal);
        public override int GetHashCode() => Descriptor.GetHashCode();
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

        // ── Culture-current option lists ─────────────────────────
        // NO ordering dependence on the CultureChanged event: a C# `static`
        // lambda compiles to an INSTANCE delegate on a compiler-generated
        // singleton, so it lands in the event's weak list and can run AFTER
        // instance handlers (Codex audit 2026-07-16: dropdowns stayed one
        // culture behind because the re-raise preceded the rebuild). The
        // backing lists are stamped with the LCID they were built for, and
        // every consumer path rebuilds them first when the culture moved.
        // Whoever runs first does the work; order stops mattering.
        private static int s_optionsLcid = System.Globalization.CultureInfo.CurrentUICulture.LCID;

        internal static void EnsureOptionsCultureCurrent()
        {
            int lcid = System.Globalization.CultureInfo.CurrentUICulture.LCID;
            if (lcid == s_optionsLcid) return;
            s_optionsLcid = lcid;
            KindOptionsBacking = BuildKindOptions();
            HostHalfOptionsBacking = BuildHostHalfOptions();
            FireOptionsBacking = BuildFireOptions();
        }

        /// <summary>Which input the freeform recorder is currently aimed
        /// at: the opener fold, one of the Custom steer axes, or the
        /// Click input.</summary>
        public enum MenuRecordTarget { Host, CustomX, CustomY, Click }

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

        private bool _supportsControllerButtons = true;

        /// <summary>Whether this slot's output can press controller
        /// buttons at all (Xbox / PlayStation / Extended). MIDI and
        /// Keyboard-Mouse slots CANNOT, and their cells must not offer a
        /// dead Controller Button choice: options are dynamic per slot
        /// type, never offered-then-warned-about.</summary>
        public bool SupportsControllerButtons
        {
            get => _supportsControllerButtons;
            set
            {
                if (_supportsControllerButtons == value) return;
                _supportsControllerButtons = value;
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
                // 0 is legal: an axis-only Extended layout has no buttons,
                // and clamping to 1 offered a "Btn 1" that the zero-word
                // raw state could never emit.
                value = Math.Max(0, value);
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
            // Rebuild-before-raise, with no reliance on handler order.
            EnsureOptionsCultureCurrent();
            OnPropertyChanged(nameof(KindOptions));
            OnPropertyChanged(nameof(HostOptions));
            OnPropertyChanged(nameof(HostHalfOptions));
            OnPropertyChanged(nameof(FireOptions));
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(SelectedFireDescription));
            RefreshInputChoices();
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

        // ── Opener surface (the PHYSICAL controller side) ────────

        /// <summary>Pickable openers: the full device-agnostic grammar,
        /// always. Mirrors the mapping picker's "(Any device)" convention
        /// (MappingDisplayResolver.BuildDeviceAgnosticChoices): two sticks,
        /// touchpads 0 and 1 (everything the Workshop translator emits for
        /// real hardware), and Custom Axes. Never gated on assignment or
        /// online state: imported profiles land on slots with nothing
        /// assigned yet, and the menu must be fully editable before any
        /// device exists. An authored descriptor outside the grammar
        /// (hand-edited XML, a typeless config's center_trackpad third
        /// pad) is still listed so the selection never silently lies.</summary>
        public IReadOnlyList<MenuHostOption> HostOptions
        {
            get
            {
                var s = Strings.Instance;
                var list = new List<MenuHostOption>(6)
                {
                    new MenuHostOption { Descriptor = "Gamepad LeftStick", Label = s.Menu_Host_LeftStick },
                    new MenuHostOption { Descriptor = "Gamepad RightStick", Label = s.Menu_Host_RightStick },
                };
                for (int n = 0; n < 2; n++)
                    list.Add(new MenuHostOption
                    {
                        Descriptor = $"Touchpad {n}",
                        Label = string.Format(s.Menu_Host_Touchpad_Format, n + 1),
                        IsTouchpad = true,
                    });
                // Custom Axes: exists precisely for devices the named
                // surfaces cannot describe (joysticks, wheels, anything
                // not detected as a gamepad).
                list.Add(new MenuHostOption { Descriptor = "Custom", Label = s.Menu_Host_Custom });
                bool found = false;
                foreach (var o in list)
                    if (string.Equals(o.Descriptor, Entry.HostDescriptor, StringComparison.Ordinal))
                    { found = true; break; }
                if (!found && !string.IsNullOrEmpty(Entry.HostDescriptor))
                {
                    bool touch = Entry.HostDescriptor.StartsWith("Touchpad ", StringComparison.Ordinal);
                    string label = Entry.HostDescriptor;
                    var parts = Entry.HostDescriptor.Split(' ');
                    if (touch && parts.Length == 2 && int.TryParse(parts[1], out int pad) && pad >= 0)
                        label = string.Format(s.Menu_Host_Touchpad_Format, pad + 1);
                    list.Add(new MenuHostOption
                    {
                        Descriptor = Entry.HostDescriptor,
                        Label = label,
                        IsTouchpad = touch,
                    });
                }
                return list;
            }
        }

        /// <summary>Re-raises the opener list and selection after a direct
        /// Entry.HostDescriptor mutation (record fold, reset) or a culture
        /// change rebuilds the labels.</summary>
        internal void RefreshHostOptions()
        {
            OnPropertyChanged(nameof(HostOptions));
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(HostIsTouchpad));
        }

        public MenuHostOption SelectedHost
        {
            get
            {
                var options = HostOptions;
                foreach (var h in options)
                    if (h.Descriptor == Entry.HostDescriptor) return h;
                return options[0];
            }
            set
            {
                if (value == null || Entry.HostDescriptor == value.Descriptor) return;
                Entry.HostDescriptor = value.Descriptor;
                if (!value.IsTouchpad) Entry.HostHalf = 0;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HostIsTouchpad));
                OnPropertyChanged(nameof(HostHalfIndex));
                OnPropertyChanged(nameof(IsCustomHost));
                RefreshInputChoices();
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
            else if (IsAnalogDescriptor(canonical))
            {
                // A flight stick, wheel, or throttle axis has no gamepad
                // stick to fold onto: it becomes a Custom Axes opener with
                // the recorded axis as Steer X.
                Entry.HostDescriptor = "Custom";
                Entry.CustomXDescriptor = canonical;
                RefreshHostOptions();
                OnPropertyChanged(nameof(IsCustomHost));
                RaiseCustomInput(MenuRecordTarget.CustomX);
                RaiseCustomInput(MenuRecordTarget.Click);
                OnEdited();
                return true;
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

        /// <summary>Applies a freeform-recorded descriptor to the given
        /// record target. Custom steer slots accept analog descriptors,
        /// the Click slot accepts button-family ones.</summary>
        public bool TryApplyRecorded(MenuRecordTarget target, string descriptor)
        {
            string d = (descriptor ?? "").Trim();
            if (d.Length == 0) return false;
            string canonical = PadForge.Engine.Common.Mapping.SourceCoercion.ResolveGamepadAlias(d) ?? d;

            switch (target)
            {
                case MenuRecordTarget.Host:
                    return TryApplyRecordedHost(descriptor);

                case MenuRecordTarget.CustomX:
                case MenuRecordTarget.CustomY:
                    if (!IsAnalogDescriptor(canonical)) return false;
                    if (target == MenuRecordTarget.CustomX) Entry.CustomXDescriptor = canonical;
                    else Entry.CustomYDescriptor = canonical;
                    RaiseCustomInput(target);
                    OnEdited();
                    return true;

                case MenuRecordTarget.Click:
                    if (IsAnalogDescriptor(canonical)) return false;
                    Entry.ClickDescriptor = canonical;
                    RaiseCustomInput(MenuRecordTarget.Click);
                    OnEdited();
                    return true;
            }
            return false;
        }

        private static bool IsAnalogDescriptor(string canonical)
            => canonical.StartsWith("Axis ", StringComparison.Ordinal)
            || canonical.StartsWith("Slider ", StringComparison.Ordinal)
            || canonical.StartsWith("IAxis ", StringComparison.Ordinal)
            || canonical.StartsWith("HAxis ", StringComparison.Ordinal)
            || canonical.StartsWith("IHAxis ", StringComparison.Ordinal);

        /// <summary>Friendly-name lookup for a recorded raw descriptor,
        /// supplied by PadViewModel from the slot's picker choices. Null
        /// (tests) shows the raw descriptor.</summary>
        internal Func<string, string> DescriptorDisplayProvider;

        private string DisplayFor(string descriptor)
            => string.IsNullOrEmpty(descriptor)
                ? Strings.Instance.Menu_NotRecorded
                : DescriptorDisplayProvider?.Invoke(descriptor) ?? descriptor;

        public bool IsCustomHost =>
            string.Equals(Entry.HostDescriptor, "Custom", StringComparison.Ordinal);

        /// <summary>The slot's pickable inputs (the mapping picker's
        /// cross-device list), supplied by PadViewModel. Null (tests)
        /// offers only the sentinel and the authored value.</summary>
        internal Func<IEnumerable<InputChoice>> InputChoicesProvider;

        /// <summary>Builds a Custom-steer / Click dropdown: the slot's
        /// cross-device picker list EXACTLY as the mapping table shows
        /// it (the "(Any device)" abstract group first, then every
        /// assigned device's own group of its mappable items), filtered
        /// by the SAME gate the record path applies: analog families
        /// steer (gamepad axis aliases resolve before the test, so the
        /// abstract stick/trigger entries stay pickable), everything
        /// else clicks. No deduplication and no relabeling: each entry
        /// keeps its device group and its device's naming, so a
        /// non-gamepad's Button 0..N, POV hats, and axes read raw while
        /// a gamepad's read by its object names. The authored value
        /// always gets an "(Any device)" entry when no group lists it,
        /// so the selection never silently lies.</summary>
        private List<InputChoice> BuildInputChoices(bool analog, string current)
        {
            var list = new List<InputChoice>();
            string cur = (current ?? "").Trim();
            bool found = false;
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
            {
                foreach (var c in provided)
                {
                    if (c == null || string.IsNullOrEmpty(c.Descriptor)) continue;
                    // A menu reading its own items back would feed itself.
                    if (c.Descriptor.StartsWith("Menu ", StringComparison.Ordinal)) continue;
                    string canonical = PadForge.Engine.Common.Mapping.SourceCoercion
                        .ResolveGamepadAlias(c.Descriptor) ?? c.Descriptor;
                    if (IsAnalogDescriptor(canonical) != analog) continue;
                    list.Add(c);
                    if (cur.Length > 0 && string.Equals(c.Descriptor, cur, StringComparison.Ordinal))
                        found = true;
                }
            }
            if (cur.Length > 0 && !found)
                list.Add(new InputChoice
                {
                    Descriptor = cur,
                    DisplayName = DisplayFor(cur),
                    DeviceGuid = "",
                    DeviceLabel = Strings.Instance.Mapping_AnyDevice,
                });
            return list;
        }

        /// <summary>Grouped view over a choice list, keyed on DeviceLabel
        /// like <c>PadViewModel.SlotAvailableInputsView</c> so the combo
        /// renders the mapping picker's device-name headers.</summary>
        private static System.ComponentModel.ICollectionView BuildChoicesView(List<InputChoice> items)
        {
            var view = new System.Windows.Data.ListCollectionView(items);
            view.GroupDescriptions.Add(
                new System.Windows.Data.PropertyGroupDescription(nameof(InputChoice.DeviceLabel)));
            return view;
        }

        public IReadOnlyList<InputChoice> CustomXChoices
            => BuildInputChoices(analog: true, Entry.CustomXDescriptor);

        public IReadOnlyList<InputChoice> CustomYChoices
            => BuildInputChoices(analog: true, Entry.CustomYDescriptor);

        public IReadOnlyList<InputChoice> ClickChoices
            => BuildInputChoices(analog: false, Entry.ClickDescriptor);

        public System.ComponentModel.ICollectionView CustomXChoicesView
            => BuildChoicesView(BuildInputChoices(analog: true, Entry.CustomXDescriptor));

        public System.ComponentModel.ICollectionView CustomYChoicesView
            => BuildChoicesView(BuildInputChoices(analog: true, Entry.CustomYDescriptor));

        public System.ComponentModel.ICollectionView ClickChoicesView
            => BuildChoicesView(BuildInputChoices(analog: false, Entry.ClickDescriptor));

        /// <summary>Subtitle under a steer / Click combo, mirroring the
        /// mapping and macro pickers: the SELECTED entry's owning-device
        /// label (the first provider match, which is also the instance
        /// the combo displays), or the empty-state text when nothing is
        /// assigned ("(not set)"; the Click row of a named opener reads
        /// "(host default)" because its default click still fires).</summary>
        private string SubtitleFor(string descriptor, string emptyLabel)
        {
            string d = (descriptor ?? "").Trim();
            if (d.Length == 0) return emptyLabel;
            var provided = InputChoicesProvider?.Invoke();
            if (provided != null)
                foreach (var c in provided)
                    if (c != null && string.Equals(c.Descriptor, d, StringComparison.Ordinal))
                        return c.DeviceLabel;
            return Strings.Instance.Mapping_AnyDevice;
        }

        public string CustomXSubtitle
            => SubtitleFor(Entry.CustomXDescriptor, Strings.Instance.Menu_NotRecorded);

        public string CustomYSubtitle
            => SubtitleFor(Entry.CustomYDescriptor, Strings.Instance.Menu_NotRecorded);

        public string ClickSubtitle
            => SubtitleFor(Entry.ClickDescriptor,
                IsCustomHost ? Strings.Instance.Menu_NotRecorded : Strings.Instance.Menu_ClickDefault);

        public string CustomXSelected
        {
            get => Entry.CustomXDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.CustomX, value);
        }

        public string CustomYSelected
        {
            get => Entry.CustomYDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.CustomY, value);
        }

        public string ClickSelected
        {
            get => Entry.ClickDescriptor ?? "";
            set => SetCustomInput(MenuRecordTarget.Click, value);
        }

        /// <summary>True while RaiseCustomInput swaps a combo's grouped
        /// ItemsSource view: the WPF Selector transiently clears its
        /// selection during the swap and the TwoWay SelectedValue binding
        /// writes that clear back through the setter, wiping the value
        /// that triggered the raise (a freshly recorded click vanished
        /// this way). Setter writes are ignored while the flag is up; the
        /// Selected re-raise that follows the swap re-asserts the stored
        /// value against the new view.</summary>
        private bool _raisingInputChoices;

        private void SetCustomInput(MenuRecordTarget target, string value)
        {
            if (_raisingInputChoices) return;
            string v = (value ?? "").Trim();
            switch (target)
            {
                case MenuRecordTarget.CustomX:
                    if ((Entry.CustomXDescriptor ?? "") == v) return;
                    Entry.CustomXDescriptor = v;
                    break;
                case MenuRecordTarget.CustomY:
                    if ((Entry.CustomYDescriptor ?? "") == v) return;
                    Entry.CustomYDescriptor = v;
                    break;
                default:
                    if ((Entry.ClickDescriptor ?? "") == v) return;
                    Entry.ClickDescriptor = v;
                    break;
            }
            RaiseCustomInput(target);
            OnEdited();
        }

        /// <summary>Re-raises one input row's selection, choices (the
        /// never-lie entry may appear or vanish), and display.</summary>
        private void RaiseCustomInput(MenuRecordTarget target)
        {
            _raisingInputChoices = true;
            try
            {
                // View before Selected: the Selected re-read must resolve
                // against the NEW ItemsSource, not the one being replaced.
                switch (target)
                {
                    case MenuRecordTarget.CustomX:
                        OnPropertyChanged(nameof(CustomXChoices));
                        OnPropertyChanged(nameof(CustomXChoicesView));
                        OnPropertyChanged(nameof(CustomXSelected));
                        OnPropertyChanged(nameof(CustomXSubtitle));
                        break;
                    case MenuRecordTarget.CustomY:
                        OnPropertyChanged(nameof(CustomYChoices));
                        OnPropertyChanged(nameof(CustomYChoicesView));
                        OnPropertyChanged(nameof(CustomYSelected));
                        OnPropertyChanged(nameof(CustomYSubtitle));
                        break;
                    default:
                        OnPropertyChanged(nameof(ClickChoices));
                        OnPropertyChanged(nameof(ClickChoicesView));
                        OnPropertyChanged(nameof(ClickSelected));
                        OnPropertyChanged(nameof(ClickSubtitle));
                        break;
                }
            }
            finally
            {
                _raisingInputChoices = false;
            }
        }

        /// <summary>Re-raises the three input dropdowns after the slot's
        /// picker list repopulates (device assignment changes) or the
        /// culture moves. Labels come from the live picker, so both
        /// events can relabel every entry.</summary>
        internal void RefreshInputChoices()
        {
            RaiseCustomInput(MenuRecordTarget.CustomX);
            RaiseCustomInput(MenuRecordTarget.CustomY);
            RaiseCustomInput(MenuRecordTarget.Click);
        }

        public RelayCommand ResetClickCommand => _resetClick ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.Click, ""));
        private RelayCommand _resetClick;

        public RelayCommand ResetCustomXCommand => _resetCustomX ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.CustomX, ""));
        private RelayCommand _resetCustomX;

        public RelayCommand ResetCustomYCommand => _resetCustomY ??= new RelayCommand(
            () => SetCustomInput(MenuRecordTarget.CustomY, ""));
        private RelayCommand _resetCustomY;

        /// <summary>Aims the NEXT record at the given target without
        /// arming the glyphs (the window's recorder handler arms).</summary>
        public void PrepareRecord(MenuRecordTarget target) => PendingRecordTarget = target;

        // ── Record plumbing (one freeform recorder, four targets) ──

        private MenuRecordTarget? _recordingTarget;

        /// <summary>The target the armed record button points at. The
        /// recorder callback routes the captured descriptor through
        /// <see cref="TryApplyRecorded"/> with this value.</summary>
        public MenuRecordTarget PendingRecordTarget { get; private set; } = MenuRecordTarget.Host;

        /// <summary>True while ANY record target is armed. The legacy
        /// setter shape stays for the recorder callback: writing false
        /// ends whatever recording was active.</summary>
        public bool HostRecording
        {
            get => _recordingTarget != null;
            set
            {
                if (!value) RecordingTarget = null;
                else if (_recordingTarget == null) RecordingTarget = MenuRecordTarget.Host;
            }
        }

        private MenuRecordTarget? RecordingTarget
        {
            get => _recordingTarget;
            set
            {
                if (_recordingTarget == value) return;
                _recordingTarget = value;
                OnPropertyChanged(nameof(HostRecording));
                OnPropertyChanged(nameof(HostRecordIcon));
                OnPropertyChanged(nameof(CustomXRecordIcon));
                OnPropertyChanged(nameof(CustomYRecordIcon));
                OnPropertyChanged(nameof(ClickRecordIcon));
            }
        }

        internal void BeginRecord(MenuRecordTarget target)
        {
            PendingRecordTarget = target;
            RecordingTarget = target;
        }

        private string IconFor(MenuRecordTarget t)
            => _recordingTarget == t ? "" : "";

        /// <summary>Segoe MDL2 glyphs: Stop (E71A) while that target is
        /// recording, Record (E7C8) while idle, mirroring the Aim Engage
        /// record button.</summary>
        public string HostRecordIcon => IconFor(MenuRecordTarget.Host);
        public string CustomXRecordIcon => IconFor(MenuRecordTarget.CustomX);
        public string CustomYRecordIcon => IconFor(MenuRecordTarget.CustomY);
        public string ClickRecordIcon => IconFor(MenuRecordTarget.Click);

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
            // Write the MODEL default directly, never by list index: a
            // never-lie entry for an out-of-grammar descriptor can extend
            // the list, so positions are not fixed.
            Entry.HostDescriptor = "Gamepad RightStick";
            Entry.HostHalf = 0;
            Entry.CustomXDescriptor = "";
            Entry.CustomYDescriptor = "";
            OnPropertyChanged(nameof(SelectedHost));
            OnPropertyChanged(nameof(HostOptions));
            OnPropertyChanged(nameof(HostIsTouchpad));
            OnPropertyChanged(nameof(HostHalfIndex));
            OnPropertyChanged(nameof(IsCustomHost));
            RefreshInputChoices();
            OnEdited();
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
            // tail, a removed center) ONLY when they carry no data. An
            // authored cell surviving out of reach is invisible to the
            // runtime (nothing hovers its index) but reappears intact when
            // the shape flips back; deleting it made a Radial/Grid style
            // toggle silently destroy the user's bindings (Codex audit
            // 2026-07-16: grid cell 0 died on Grid-to-Radial, ring cell N
            // and the center died the other way).
            if (Entry.Items != null)
            {
                Entry.Items.RemoveAll(it => it == null
                    || ((string.IsNullOrEmpty(it.Label)
                         && it.VirtualKey <= 0 && it.XboxButtons == 0 && it.ExtendedButton <= 0)
                        && (Entry.Kind == MenuKind.Radial
                            ? it.Index > Entry.CellCount || (it.Index == 0 && !Entry.HasCenter)
                            : it.Index >= Entry.CellCount || it.Index < 0)));
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

        /// <summary>Binding kinds, DYNAMIC per slot type: None and
        /// Keyboard Key everywhere, Controller Button only where the
        /// slot's output can actually press one. A stale button binding
        /// left by a slot-type switch stays visible, marked, so the
        /// selection never lies, but dead choices are never offered
        /// fresh. Built per read (tiny list, culture-safe).</summary>
        public IReadOnlyList<MenuIntOption> BindingKindOptions
        {
            get
            {
                var s = Strings.Instance;
                var list = new List<MenuIntOption>(3)
                {
                    new MenuIntOption { Value = 0, Label = s.Menu_Binding_None },
                    new MenuIntOption { Value = 1, Label = s.Menu_Binding_Key },
                };
                if (_owner.SupportsControllerButtons)
                    list.Add(new MenuIntOption { Value = 2, Label = s.Menu_Binding_Button });
                else if (_item != null && (_item.XboxButtons != 0 || _item.ExtendedButton > 0))
                    list.Add(new MenuIntOption
                    {
                        Value = 2,
                        Label = string.Format(s.Menu_Binding_Unsupported_Format, s.Menu_Binding_Button),
                    });
                return list;
            }
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

        private static SocdKeyOption[] _fullKeyOptionsCache;
        private static int _fullKeyOptionsCulture;

        /// <summary>Every key the macro editor's Key Press offers (the
        /// full VirtualKey vocabulary, same localized labels), as cell
        /// key bindings. The SOCD subset the first cut reused was
        /// missing the Windows keys, media keys, and the rest of the
        /// extended set (owner report 2026-07-17). VirtualKey.None is
        /// dropped: a cell with no key is an EMPTY cell, cleared by
        /// its reset button, never by a "None" pick.</summary>
        public System.Collections.Generic.IReadOnlyList<SocdKeyOption> KeyOptions
        {
            get
            {
                int culture = System.Globalization.CultureInfo.CurrentUICulture.LCID;
                var cached = _fullKeyOptionsCache;
                if (cached != null && _fullKeyOptionsCulture == culture)
                    return cached;
                // Rebuild-before-read: the macro table's own culture
                // rebuild has NO ordering guarantee against this getter
                // (the CultureChanged weak-list trap), so refresh it
                // here instead of trusting handler order.
                MacroAction.RefreshVirtualKeyValues();
                var source = MacroAction.VirtualKeyValues;
                var list = new System.Collections.Generic.List<SocdKeyOption>(source.Count);
                foreach (var k in source)
                    if ((int)k.Key > 0)
                        list.Add(new SocdKeyOption { Vk = (int)k.Key, Label = k.DisplayName });
                var arr = list.ToArray();
                _fullKeyOptionsCache = arr;
                _fullKeyOptionsCulture = culture;
                return arr;
            }
        }

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
                    // 0 buttons = empty picker (axis-only Extended layout).
                    int count = Math.Clamp(_owner.ExtendedButtonCount, 0, 128);
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
        /// slots, the 1-based raw button number on Extended slots. When the
        /// slot's TYPE changed after the cell was authored, the other value
        /// space's stored binding is presented through the shared
        /// mask-to-number equivalence (MacroButtonNames.NumberedMaskOrder)
        /// instead of showing a blank picker over live data.</summary>
        public int SelectedButtonFlag
        {
            get
            {
                if (_item == null) return 0;
                if (_owner.ButtonStyle == MacroButtonStyle.Numbered)
                    return _item.ExtendedButton > 0
                        ? _item.ExtendedButton
                        : MacroButtonNames.NumberFromMask(_item.XboxButtons);
                return _item.XboxButtons != 0
                    ? _item.XboxButtons
                    : MacroButtonNames.MaskFromNumber(_item.ExtendedButton);
            }
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
