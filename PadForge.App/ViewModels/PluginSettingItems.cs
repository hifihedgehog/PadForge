using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PadForge.Resources.Strings;
using PadForge.Services.Plugins;

namespace PadForge.ViewModels
{
    /// <summary>One entry of a declared choice, as its list shows it.</summary>
    public sealed record PluginChoiceItem(string Value, string Words)
    {
        public override string ToString() => Words;
    }

    /// <summary>A group's heading in a plugin's settings: the words the
    /// plugin named the group with.</summary>
    public sealed class PluginGroupHeading
    {
        internal PluginGroupHeading(string title) => Title = title;
        public string Title { get; }
    }

    /// <summary>Compact declarations of one group, drawn side by side in one
    /// row: pads with their names under them, then short sliders, then
    /// toggles.</summary>
    public sealed class PluginCompactRow : ObservableObject
    {
        private bool _isShown = true;

        /// <summary>False while every declaration in it is hidden by the
        /// switch it follows.</summary>
        public bool IsShown
        {
            get => _isShown;
            internal set => SetProperty(ref _isShown, value);
        }

        internal PluginCompactRow(IReadOnlyList<PluginSettingItem> items)
        {
            Pads = items.Where(i => i.Kind == PluginSettingKind.Pad).ToList();
            Sliders = items.Where(i => i.Kind == PluginSettingKind.Number).ToList();
            Toggles = items.Where(i => i.Kind == PluginSettingKind.Switch).ToList();
            Others = items.Where(i => i.Kind is not (PluginSettingKind.Pad or PluginSettingKind.Number or PluginSettingKind.Switch)).ToList();
            Items = items;
        }

        public IReadOnlyList<PluginSettingItem> Items { get; }
        public IReadOnlyList<PluginSettingItem> Pads { get; }
        public IReadOnlyList<PluginSettingItem> Sliders { get; }
        public IReadOnlyList<PluginSettingItem> Toggles { get; }
        public IReadOnlyList<PluginSettingItem> Others { get; }
        public bool HasSliders => Sliders.Count > 0;
        public bool HasToggles => Toggles.Count > 0;
    }

    /// <summary>
    /// One setting or control a plugin declared (#600 item 17), as its row
    /// draws it in PadForge's own styles. Its words are the plugin's,
    /// cleaned and capped when they were read. A change the user makes goes
    /// to the plugin's settings, which keep it and send it on. A NaN or an
    /// infinity from a box leaves the number or the pad as it was, as the
    /// plugin's settings refuse one a program sends (PluginValues.Normalize).
    /// </summary>
    public sealed class PluginSettingItem : FiniteObservableObject
    {
        private readonly PluginSettingsHost _host;
        private string _value;
        private bool _isShown = true;
        private IReadOnlyList<PluginChoiceItem> _choices;
        private bool _picking;

        internal PluginSettingItem(PluginSettingsHost host, PluginDeclaration declaration)
        {
            _host = host;
            Declaration = declaration;
            ResetCommand = new RelayCommand(() => _host.Set.Reset(_host.Device, Key));
            PressCommand = new RelayCommand(() => _host.Set.Press(_host.Device, Key));
            PickCommand = new RelayCommand(Pick);
            _choices = Convert(declaration.Choices);
        }

        internal PluginDeclaration Declaration { get; private set; }

        internal PluginSettingKind Kind => Declaration.Kind;

        /// <summary>The kind's word, which the row's template follows.</summary>
        public string KindWord => PluginDeclarations.Word(Declaration.Kind);

        public string Key => Declaration.Key;
        public string Name => Declaration.Name;
        public string Help => Declaration.Help;
        public bool HasHelp => Declaration.Help.Length > 0;

        /// <summary>A switch's and a button's help stand in the row, where
        /// they have room. Every other kind gives it on hover.</summary>
        public bool HelpInRow => HasHelp && Declaration.Kind is PluginSettingKind.Switch or PluginSettingKind.Button;

        public string HoverHelp => HasHelp && !HelpInRow ? Declaration.Help : null;
        public string Unit => Declaration.Unit;
        public double Min => Declaration.Min;
        public double Max => Declaration.Max;
        public double Step => Declaration.Step;
        public bool Compact => Declaration.Compact;
        public bool Stay => Declaration.Stay;

        /// <summary>The stable ID of its control: the plugin's, its device's
        /// and its key.</summary>
        public string AutomationId => _host.IdPrefix + "_" + Key;

        /// <summary>Whether the row shows a reset: the kinds that hold a
        /// value the user sets, at their default too, as every setting's
        /// reset in PadForge shows on its row.</summary>
        public bool CanReset => Declaration.HoldsValue;

        public IReadOnlyList<PluginChoiceItem> Choices
        {
            get => _choices;
            private set => SetProperty(ref _choices, value);
        }

        /// <summary>The value as PadForge keeps it.</summary>
        public string Value
        {
            get => _value;
            private set
            {
                if (!SetProperty(ref _value, value)) return;
                OnPropertyChanged(nameof(IsOn));
                OnPropertyChanged(nameof(Number));
                OnPropertyChanged(nameof(NumberText));
                OnPropertyChanged(nameof(Choice));
                OnPropertyChanged(nameof(Text));
                OnPropertyChanged(nameof(Pad));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(InputText));
            }
        }

        /// <summary>False while the switch it follows (<c>when</c>) is off.</summary>
        public bool IsShown
        {
            get => _isShown;
            internal set => SetProperty(ref _isShown, value);
        }

        public bool IsOn
        {
            get => _value == "true";
            set => Write(value ? "true" : "false");
        }

        public double Number
        {
            get => PluginDeclarations.TryNumber(_value, out double n) ? n : Declaration.Min;
            set
            {
                if (!double.IsFinite(value)) return;
                Write(PluginValues.FormatNumber(PluginValues.OnStep(Declaration, value)));
            }
        }

        /// <summary>The number with the decimals its step has and its
        /// unit: "4.0 s", "250 ms", "3%".</summary>
        public string NumberText
        {
            get
            {
                int decimals = Math.Min(PluginValues.Decimals(Declaration.Step), 4);
                string digits = Number.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);
                if (Unit.Length == 0) return digits;
                return Unit == "%" ? digits + "%" : digits + " " + Unit;
            }
        }

        public string Choice
        {
            get => _value;
            set { if (value != null) Write(value); }
        }

        public string Text
        {
            get => _value ?? string.Empty;
            set => Write(value ?? string.Empty);
        }

        /// <summary>A pad's position, X then Y, each -100 to 100.</summary>
        public System.Windows.Point Pad
        {
            get
            {
                var (x, y) = PluginValues.Pad(_value);
                return new System.Windows.Point(x, y);
            }
            set => SetPad(value.X, value.Y);
        }

        /// <summary>Sets both axes of a pad as one change.</summary>
        internal void SetPad(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y)) return;
            Write(PluginValues.FormatNumber(Math.Round(Math.Clamp(x, -100, 100), 1)) + ","
                     + PluginValues.FormatNumber(Math.Round(Math.Clamp(y, -100, 100), 1)));
        }

        public string StatusText => _value ?? string.Empty;

        /// <summary>An input's pick in words, or "Not picked" while it holds
        /// none, or the prompt while the user is asked to press.</summary>
        public string InputText => _picking ? Strings.Instance.Plugins_Input_Press
            : string.IsNullOrEmpty(_value) ? Strings.Instance.Plugins_Input_None : _value;

        public RelayCommand ResetCommand { get; }
        public RelayCommand PressCommand { get; }
        public RelayCommand PickCommand { get; }

        private void Write(string value)
        {
            if (_value == value) return;
            string error = _host.Set.Set(_host.Device, Key, value);
            // A refused value leaves the control where the kept one is.
            if (error != null) OnPropertyChanged(nameof(Value));
        }

        /// <summary>Takes the declaration and the value in force from the
        /// plugin's settings.</summary>
        internal void Read(PluginDeclaration declaration)
        {
            if (!ReferenceEquals(Declaration, declaration))
            {
                Declaration = declaration;
                Choices = Convert(declaration.Choices);
                OnPropertyChanged(string.Empty);
            }
            else if (!SameChoices(_choices, declaration.Choices)) Choices = Convert(declaration.Choices);
            Value = _host.Set.ValueOf(_host.Device, Key);
        }

        /// <summary>A choice's list opens: the plugin is asked for the list
        /// as it is now.</summary>
        internal void OpenChoices() => _host.Set.AskChoices(_host.Device, Key);

        private void Pick()
        {
            if (_picking) return;
            _picking = true;
            OnPropertyChanged(nameof(InputText));
            PluginInputPick.Start(_host.Device == null ? null : _host.DeviceGuid, (device, control) =>
            {
                _picking = false;
                if (device != null) Write(device + " " + control);
                OnPropertyChanged(nameof(InputText));
            });
        }

        private static IReadOnlyList<PluginChoiceItem> Convert(IReadOnlyList<PluginChoice> choices)
            => choices.Select(c => new PluginChoiceItem(c.Value, c.Words)).ToList();

        private static bool SameChoices(IReadOnlyList<PluginChoiceItem> a, IReadOnlyList<PluginChoice> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Value != b[i].Value || a[i].Words != b[i].Words) return false;
            return true;
        }
    }

    /// <summary>
    /// A plugin's declared settings, or one of its devices', as the page
    /// draws them (#600 item 17): rows in the order declared under the
    /// headings of their groups, a group's compact declarations in one row,
    /// Reset All, and the line that says why there are none. Follows the
    /// plugin's settings on the interface's thread: a new declaration
    /// rebuilds the rows, a new value updates the row in place, so a slider
    /// being dragged is not rebuilt under the pointer.
    /// </summary>
    public sealed class PluginSettingsHost : ObservableObject
    {
        private readonly Dispatcher _ui;
        private readonly Dictionary<string, PluginSettingItem> _items = new(StringComparer.Ordinal);
        private string _shape;
        private string _missing;
        private int _queued;

        internal PluginSettingsHost(PluginSettingSet set, string device, string idPrefix, Dispatcher ui, Guid deviceGuid = default)
        {
            Set = set;
            Device = device;
            IdPrefix = idPrefix;
            DeviceGuid = deviceGuid;
            _ui = ui ?? Dispatcher.CurrentDispatcher;
            ResetAllCommand = new RelayCommand(() => Set.ResetAll(Device));
            // Held weakly: the device panel makes a host each time it shows
            // a plugin's device, and one it let go stops listening.
            var weak = new WeakReference<PluginSettingsHost>(this);
            Action listen = null;
            listen = () =>
            {
                if (weak.TryGetTarget(out var host)) host.Queue();
                else set.Changed -= listen;
            };
            Set.Changed += listen;
            Refresh();
        }

        internal PluginSettingSet Set { get; }

        /// <summary>The device's key, or null for the plugin's own
        /// settings.</summary>
        internal string Device { get; }

        /// <summary>The device these settings are for, when they are a
        /// device's.</summary>
        internal Guid DeviceGuid { get; }

        internal string IdPrefix { get; }

        /// <summary>The rows: headings, settings and compact rows.</summary>
        public ObservableCollection<object> Rows { get; } = new();

        public bool HasRows => Rows.Count > 0;

        /// <summary>Why the plugin's controls are missing, or null while it
        /// shows them.</summary>
        public string MissingLine
        {
            get => _missing;
            internal set
            {
                if (SetProperty(ref _missing, value)) OnPropertyChanged(nameof(HasMissingLine));
            }
        }

        public bool HasMissingLine => !string.IsNullOrEmpty(_missing);

        public RelayCommand ResetAllCommand { get; }

        /// <summary>The settings it shows, by key.</summary>
        internal PluginSettingItem Item(string key) => _items.TryGetValue(key, out var item) ? item : null;

        internal IEnumerable<PluginSettingItem> Items => _items.Values;

        private void Queue()
        {
            if (_ui.CheckAccess())
            {
                Refresh();
                return;
            }
            if (System.Threading.Interlocked.Exchange(ref _queued, 1) != 0) return;
            _ui.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                System.Threading.Volatile.Write(ref _queued, 0);
                Refresh();
            }));
        }

        /// <summary>Reads the plugin's settings again. On the interface's
        /// thread.</summary>
        internal void Refresh()
        {
            var mine = Set.Declarations().Where(d => d.Device == Device).OrderBy(d => d.Order).ToList();
            string shape = string.Join("\n", mine.Select(d => d.Key + "|" + d.Kind + "|" + d.Group + "|" + d.Compact));
            if (shape != _shape)
            {
                _shape = shape;
                Rebuild(mine);
            }
            foreach (var d in mine)
                if (_items.TryGetValue(d.Key, out var item)) item.Read(d);
            foreach (var d in mine)
                if (d.When != null && _items.TryGetValue(d.Key, out var item))
                    item.IsShown = Set.ValueOf(Device, d.When) == "true";
            foreach (var row in Rows)
                if (row is PluginCompactRow compactRow) compactRow.IsShown = compactRow.Items.Any(i => i.IsShown);
        }

        private void Rebuild(List<PluginDeclaration> declarations)
        {
            _items.Clear();
            Rows.Clear();
            // Each group in the order its first declaration came, its
            // declarations in theirs.
            var groups = new List<(string Name, List<PluginDeclaration> List)>();
            foreach (var d in declarations)
            {
                var group = groups.FirstOrDefault(g => g.Name == d.Group);
                if (group.List == null)
                {
                    group = (d.Group, new List<PluginDeclaration>());
                    groups.Add(group);
                }
                group.List.Add(d);
            }
            foreach (var (name, list) in groups)
            {
                if (name.Length > 0) Rows.Add(new PluginGroupHeading(name));
                List<PluginSettingItem> compact = null;
                foreach (var d in list)
                {
                    var item = new PluginSettingItem(this, d);
                    _items[d.Key] = item;
                    if (d.Compact)
                    {
                        if (compact == null)
                        {
                            compact = new List<PluginSettingItem>();
                            Rows.Add(compact);
                        }
                        compact.Add(item);
                        continue;
                    }
                    compact = null;
                    Rows.Add(item);
                }
            }
            // A run of compact declarations is one row.
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i] is List<PluginSettingItem> run) Rows[i] = new PluginCompactRow(run);
            OnPropertyChanged(nameof(HasRows));
        }
    }
}
