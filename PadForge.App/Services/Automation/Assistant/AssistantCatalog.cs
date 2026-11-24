using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using PadForge.Common.Input;
using PadForge.Search;
using PadForge.ViewModels;
using PadForge.Views;

namespace PadForge.Services.Automation
{
    /// <summary>
    /// The settings catalog (#597): every setting on PadForge's pages, built
    /// on Search's own index, so no list is written by hand and a setting
    /// added to a page later is covered with no change here. A setting's ID
    /// is Search's stable ID with what it is shown for (SearchIds).
    ///
    /// <para>What is shown, and on which controller and device, is read the
    /// way Search reads it when it opens (SearchService.RefreshAvailability).
    /// A value is read the way Search's results read it. A write goes
    /// through the control on the page: its text box with its binding updated,
    /// its switch or its picker, so the value is coerced, checked and saved
    /// exactly as a person's change is, and the page shows it. A setting of a
    /// card per item that is not drawn is written through the same binding
    /// path and converter its control uses. A controller the page is not
    /// bound to is bound for the write and bound back, as Search binds each
    /// controller in turn to read it.</para>
    ///
    /// <para>show goes through Search's navigator (SearchNavigator), which
    /// opens the page, unfolds More, scrolls and rings, and never raises or
    /// activates the window. Called on the interface's thread.</para>
    /// </summary>
    internal sealed class AssistantCatalog : IAssistantSettings
    {
        private readonly SearchService _service;
        private readonly SearchNavigator _navigator;
        private readonly Func<SearchPage, FrameworkElement> _pageOf;
        private readonly MainViewModel _vm;
        private readonly Action<PadViewModel> _bindPad;
        private readonly Func<AssistantWindowPlace> _windowPlace;

        /// <summary>The extras whose pages hold the user's own settings.</summary>
        internal static readonly string[] UsersOwnExtras = { "Automation", "Plugins" };

        /// <summary>The extras that listen on the network once they are
        /// on.</summary>
        internal static readonly string[] NetworkExtras = { "WebController", "RemoteLink", "MotionServer", "GameMods" };

        public AssistantCatalog(SearchService service, SearchNavigator navigator, Func<SearchPage, FrameworkElement> pageOf,
            MainViewModel vm, Action<PadViewModel> bindPad, Func<AssistantWindowPlace> windowPlace)
        {
            _service = service;
            _navigator = navigator;
            _pageOf = pageOf;
            _vm = vm;
            _bindPad = bindPad;
            _windowPlace = windowPlace ?? (() => AssistantWindowPlace.InFront);
        }

        // ─────────────────────────────────────────────
        //  Reading what shows
        // ─────────────────────────────────────────────

        /// <summary>Makes sure the index is there (it is built once the
        /// window is idle) and reads what shows now on every page and
        /// controller.</summary>
        private IReadOnlyList<SearchService.Shown> Read()
        {
            if (_service.Index.Count == 0) _service.SetIndex(SearchIndexBuilder.Build(SearchSources.Load()));
            _service.RefreshAvailability(otherControllers: true);
            return _service.AvailableNow().Where(s => s.Entry.Kind == SearchKind.Setting).ToList();
        }

        private int? NumberOf(PadViewModel pad)
        {
            if (pad == null) return null;
            int n = SettingsManager.SlotOrders.GetGlobalSlotNumber(pad.PadIndex);
            return n > 0 ? n : pad.PadIndex + 1;
        }

        private object RootOf(SearchService.Shown shown)
            => (object)shown.Pad ?? _pageOf(shown.Entry.Page)?.DataContext;

        private int? ItemIndexOf(SearchService.Shown shown)
        {
            if (shown.Item == null || shown.Entry.ItemsPath == null) return null;
            if (BindingProbe.Read(RootOf(shown), shown.Entry.ItemsPath) is IEnumerable list)
            {
                int i = 0;
                foreach (object item in list)
                {
                    if (ReferenceEquals(item, shown.Item)) return i;
                    i++;
                }
            }
            return null;
        }

        private string DeviceOf(SearchService.Shown shown)
            => shown.Entry.Page == SearchPage.Devices && _vm.Devices?.SelectedDevice is { } row
                ? AutomationDevices.IdOf(row.InstanceGuid) : null;

        private string IdOf(SearchService.Shown shown)
            => SearchIds.With(_service.IdOf(shown.Entry), shown.Entry.Page == SearchPage.Pad ? NumberOf(shown.Pad) : null,
                ItemIndexOf(shown), DeviceOf(shown));

        private SearchEntry EntryOf(string id)
            => _service.Index.FirstOrDefault(e => e.Kind == SearchKind.Setting && _service.IdOf(e) == id);

        // ─────────────────────────────────────────────
        //  IAssistantSettings
        // ─────────────────────────────────────────────

        public List<AssistantSetting> Find(string words, int most)
        {
            string q = (words ?? string.Empty).Trim();
            if (q.Length == 0) throw new AssistantRefusal("bad-argument", "Give words from the setting's name or place.");
            var shown = Read();
            return shown.Select(s => (Shown: s, Score: _service.ScoreFor(s, q)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Shown.Entry.Order)
                .Take(most)
                .Select(x => Describe(x.Shown))
                .ToList();
        }

        public List<AssistantSetting> All() => Read().Select(Describe).ToList();

        public AssistantSetting Get(string id)
        {
            var (shown, entry) = Resolve(id);
            if (shown != null) return Describe(shown.Value);
            // A setting the page does not show now: found by its ID all the
            // same, and named, with no value.
            var s = PadForge.Resources.Strings.Strings.Instance;
            return new AssistantSetting
            {
                Id = id,
                Name = SearchLocator.LabelText(entry.Label, null) ?? id,
                Where = PageName(entry.Page),
                Kind = KindOf(entry),
                UnderMore = entry.UnderMore,
                Available = false,
            };
        }

        /// <summary>The setting an ID names and, when the page shows it now,
        /// how it shows. Refuses an ID no setting has.</summary>
        private (SearchService.Shown? Shown, SearchEntry Entry) Resolve(string full)
        {
            if (!SearchIds.TryRead(full?.Trim(), out string id, out int? controller, out int? item, out string device))
                throw new AssistantRefusal("unknown-setting", "No setting has the ID " + full + ". find_settings gives IDs.");
            var entry = EntryOf(id) ?? throw new AssistantRefusal("unknown-setting", "No setting has the ID " + full + ". find_settings gives IDs.");
            if (device != null) SelectDevice(device);
            foreach (var s in Read())
            {
                if (!ReferenceEquals(s.Entry, entry)) continue;
                if (entry.Page == SearchPage.Pad && NumberOf(s.Pad) != controller) continue;
                if (item != null && ItemIndexOf(s) != item) continue;
                if (device != null && DeviceOf(s) != device) continue;
                return (s, entry);
            }
            return (null, entry);
        }

        /// <summary>Puts a device on the Devices page, as picking it in the
        /// list does: the page's settings are the device's it shows.</summary>
        private void SelectDevice(string device)
        {
            var row = _vm.Devices?.Devices?.FirstOrDefault(r => AutomationDevices.IdOf(r.InstanceGuid) == device)
                ?? throw new AssistantRefusal("unknown-device", "No device has the ID " + device + ". list_devices gives them.");
            if (!ReferenceEquals(_vm.Devices.SelectedDevice, row)) _vm.Devices.SelectedDevice = row;
        }

        private static string PageName(SearchPage page)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            return page switch
            {
                SearchPage.Pad => s.Home_Controllers,
                SearchPage.Settings => s.Settings_Title,
                SearchPage.Extras => s.Extras_Title,
                SearchPage.Devices => s.Devices_Title,
                _ => s.Profiles_Title,
            };
        }

        private static AssistantSettingKind KindOf(SearchEntry entry) => entry.Value?.Kind switch
        {
            SearchValueKind.Switch => AssistantSettingKind.Switch,
            SearchValueKind.Choice => AssistantSettingKind.Choice,
            _ => AssistantSettingKind.Number,
        };

        private AssistantSetting Describe(SearchService.Shown shown)
        {
            var entry = shown.Entry;
            object data = shown.Item ?? RootOf(shown);
            string title = SearchLocator.LabelText(entry.Label, data) ?? string.Empty;
            string qualifier = SearchLocator.LabelText(entry.Qualifier, data);
            var setting = new AssistantSetting
            {
                Id = IdOf(shown),
                Name = string.IsNullOrEmpty(qualifier) ? title : title + " " + qualifier,
                Where = _service.WhereFor(shown),
                Shown = _service.ValueFor(shown),
                Kind = KindOf(entry),
                UnderMore = entry.UnderMore,
                Available = true,
            };
            var bound = IsBound(shown);
            var row = bound && shown.Label != null ? SearchLocator.Up(shown.Label) as FrameworkElement : null;
            switch (setting.Kind)
            {
                case AssistantSettingKind.Number:
                {
                    var (min, max) = RangeOf(row, entry);
                    setting.Minimum = min;
                    setting.Maximum = max;
                    setting.Unit = entry.Value?.UnitKey != null ? PadForge.Resources.Strings.Strings.Get(entry.Value.UnitKey) : entry.Value?.UnitText;
                    if (TryNumber(ReadRaw(shown), out double number)) setting.Value = JsonSerializer.SerializeToElement(number);
                    break;
                }
                case AssistantSettingKind.Switch:
                    if (ReadRaw(shown) is bool on) setting.Value = JsonSerializer.SerializeToElement(on);
                    break;
                default:
                {
                    var combo = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ComboBox>().FirstOrDefault();
                    if (combo != null) setting.Choices = Choices(combo);
                    if (!string.IsNullOrEmpty(setting.Shown)) setting.Value = JsonSerializer.SerializeToElement(setting.Shown);
                    break;
                }
            }
            return setting;
        }

        /// <summary>Whether the page's own elements show this entry's
        /// controller or device now, so its row can be read and written in
        /// place.</summary>
        private bool IsBound(SearchService.Shown shown)
        {
            if (shown.Entry.Page != SearchPage.Pad) return true;
            return ReferenceEquals(_pageOf(SearchPage.Pad)?.DataContext, shown.Pad);
        }

        /// <summary>The value under the row's binding, through its
        /// converter, as Search reads it.</summary>
        private object ReadRaw(SearchService.Shown shown)
        {
            var value = shown.Entry.Value;
            if (value?.Path == null) return null;
            object data = DataOf(shown);
            var converter = value.ConverterKey == null ? null : _pageOf(shown.Entry.Page)?.TryFindResource(value.ConverterKey) as IValueConverter;
            return BindingProbe.Read(data, value.Path, value.Kind == SearchValueKind.Number ? converter : null);
        }

        private object DataOf(SearchService.Shown shown)
        {
            if (shown.Entry.ItemsPath != null) return shown.Item;
            if (shown.Entry.Page == SearchPage.Pad) return shown.Pad;
            var row = shown.Label == null ? null : SearchLocator.Up(shown.Label) as FrameworkElement;
            return row?.DataContext ?? _pageOf(shown.Entry.Page)?.DataContext;
        }

        private static bool TryNumber(object raw, out double number)
        {
            number = 0;
            switch (raw)
            {
                case null: return false;
                case string text:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number)
                        || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                case IConvertible convertible:
                    try { number = convertible.ToDouble(CultureInfo.InvariantCulture); return !double.IsNaN(number); }
                    catch (FormatException) { return false; }
                    catch (InvalidCastException) { return false; }
                default: return false;
            }
        }

        /// <summary>The range a row's number takes, from its slider or number
        /// box, whose bounds the markup sets, in the unit the row shows: a
        /// slider bound to the raw value while the row's box shows it through
        /// a converter (a share shown in percent) has its bounds put through
        /// the same converter.</summary>
        private (double? Min, double? Max) RangeOf(FrameworkElement row, SearchEntry entry)
        {
            if (row == null) return (null, null);
            foreach (var part in SearchLocator.LogicalDescendants(row))
            {
                if (part is Wpf.Ui.Controls.NumberBox box) return (box.Minimum, box.Maximum);
                if (part is not RangeBase range || part is ScrollBar) continue;
                var own = BindingOperations.GetBinding(range, RangeBase.ValueProperty);
                string converterKey = entry?.Value?.ConverterKey;
                if (converterKey == null || own == null || own.Converter != null || own.Path?.Path != entry.Value.Path)
                    return (range.Minimum, range.Maximum);
                var converter = _pageOf(entry.Page)?.TryFindResource(converterKey) as IValueConverter;
                if (converter == null) return (range.Minimum, range.Maximum);
                double? Shown(double raw)
                    => TryNumber(converter.Convert(raw, typeof(string), null, CultureInfo.CurrentCulture), out double v) ? v : null;
                double? a = Shown(range.Minimum), b = Shown(range.Maximum);
                if (a == null || b == null) return (null, null);
                return (Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
            }
            return (null, null);
        }

        private static List<string> Choices(ComboBox combo)
        {
            var list = new List<string>();
            foreach (object item in combo.Items) list.Add(ItemText(combo, item));
            return list;
        }

        private static string ItemText(ComboBox combo, object item)
        {
            if (item is ComboBoxItem cbi) return cbi.Content as string ?? cbi.Content?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(combo.DisplayMemberPath)) return BindingProbe.Read(item, combo.DisplayMemberPath)?.ToString() ?? string.Empty;
            return item?.ToString() ?? string.Empty;
        }

        // ─────────────────────────────────────────────
        //  Writing
        // ─────────────────────────────────────────────

        public AssistantAsk AskFor(string id, JsonElement value)
        {
            var (shown, entry) = Resolve(id);
            if (entry.Page == SearchPage.Extras && Array.IndexOf(UsersOwnExtras, entry.ExtraKey) >= 0)
                throw new AssistantRefusal("users-own", "Only the user can change the Automation and Plugins settings, on their pages in PadForge.");
            if (shown == null) return null;
            var setting = Describe(shown.Value);
            string valueText = value.ValueKind switch
            {
                JsonValueKind.True => PadForge.Resources.Strings.Strings.Instance.Common_On,
                JsonValueKind.False => PadForge.Resources.Strings.Strings.Instance.Common_Off,
                JsonValueKind.String => value.GetString(),
                _ => value.ToString() + (string.IsNullOrEmpty(setting.Unit) ? string.Empty : " " + setting.Unit),
            };
            string reason = null;
            string path = entry.Value?.Path ?? string.Empty;
            string labelKey = entry.Label?.Key ?? string.Empty;
            if (entry.Page == SearchPage.Extras && Array.IndexOf(NetworkExtras, entry.ExtraKey) >= 0 && value.ValueKind == JsonValueKind.True)
                reason = AssistantToolWords.ListensOnNetwork();
            else if (entry.Page == SearchPage.Settings && shown.Value.Label != null
                     && _pageOf(SearchPage.Settings) is SettingsPage settings && settings.GroupOf(shown.Value.Label) == SettingsGroup.Drivers)
                reason = AssistantToolWords.ChangesDrivers();
            else if (path.Contains("HidHide", StringComparison.OrdinalIgnoreCase) || labelKey.Contains("HidHide", StringComparison.OrdinalIgnoreCase))
                reason = AssistantToolWords.ChangesDrivers();
            else if (path.Contains("OnlyInPadForge", StringComparison.OrdinalIgnoreCase) || labelKey.Contains("OnlyInPadForge", StringComparison.OrdinalIgnoreCase)
                     || path.Contains("UseOnly", StringComparison.OrdinalIgnoreCase))
                reason = AssistantToolWords.ChangesUseOnly();
            if (reason == null) return null;
            return new AssistantAsk(AssistantToolWords.AskSetting(setting.Name),
                new[] { AssistantToolWords.SetsTo(setting.Name, valueText), reason });
        }

        public AssistantSetting Set(string id, JsonElement value)
        {
            var (shown, entry) = Resolve(id);
            if (entry.Page == SearchPage.Extras && Array.IndexOf(UsersOwnExtras, entry.ExtraKey) >= 0)
                throw new AssistantRefusal("users-own", "Only the user can change the Automation and Plugins settings, on their pages in PadForge.");
            if (shown == null)
                throw new AssistantRefusal("not-available", "The page does not show this setting now, for that controller or device, so it cannot be set. get_setting says so with available false.");
            if (entry.Value?.Path == null)
                throw new AssistantRefusal("not-available", "This setting has no value PadForge can read or write for a program. show takes the user to it.");

            var page = _pageOf(SearchPage.Pad);
            object bound = page?.DataContext;
            bool rebind = entry.Page == SearchPage.Pad && !ReferenceEquals(bound, shown.Value.Pad);
            if (rebind)
            {
                if (_bindPad != null) _bindPad(shown.Value.Pad);
                else page.DataContext = shown.Value.Pad;
            }
            try
            {
                _service.Locator.Reset();
                var label = entry.ItemsPath == null && !entry.RowOptions ? _service.Locator.Find(entry, _service.Index) : null;
                var row = label == null ? null : SearchLocator.Up(label) as FrameworkElement;
                var data = entry.ItemsPath != null ? shown.Value.Item : row?.DataContext ?? DataOf(shown.Value);
                switch (KindOf(entry))
                {
                    case AssistantSettingKind.Number: WriteNumber(entry, row, data, value); break;
                    case AssistantSettingKind.Switch: WriteSwitch(entry, row, data, value); break;
                    default: WriteChoice(entry, row, value); break;
                }
            }
            finally
            {
                if (rebind && page != null && !ReferenceEquals(page.DataContext, bound))
                {
                    if (_bindPad != null && bound is PadViewModel back) _bindPad(back);
                    else page.DataContext = bound;
                }
                _service.Locator.Reset();
            }
            return Get(id);
        }

        private void WriteNumber(SearchEntry entry, FrameworkElement row, object data, JsonElement value)
        {
            double number;
            if (value.ValueKind == JsonValueKind.Number) number = value.GetDouble();
            else if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) number = parsed;
            else throw new AssistantRefusal("bad-argument", "This setting takes a number.");
            var (min, max) = RangeOf(row, entry);
            if ((min != null && number < min.Value) || (max != null && number > max.Value))
                throw new AssistantRefusal("out-of-range", "This setting takes " + Num(min) + " to " + Num(max)
                    + (entry.Value.UnitKey != null ? " " + PadForge.Resources.Strings.Strings.Get(entry.Value.UnitKey) : entry.Value.UnitText != null ? " " + entry.Value.UnitText : string.Empty)
                    + ". " + Num(number) + " is outside it.");

            // The row's number box, which coerces as it does for a person.
            var numberBox = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<Wpf.Ui.Controls.NumberBox>()
                .FirstOrDefault(n => BindingOperations.GetBinding(n, Wpf.Ui.Controls.NumberBox.ValueProperty)?.Path?.Path == entry.Value.Path);
            if (numberBox != null)
            {
                numberBox.Value = number;
                numberBox.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateSource();
                return;
            }
            // The row's own text box, as a person types into it.
            var box = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<TextBox>()
                .FirstOrDefault(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path?.Path == entry.Value.Path);
            if (box != null)
            {
                var culture = box.Language?.GetSpecificCulture() ?? CultureInfo.CurrentCulture;
                box.Text = number.ToString("R", culture);
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                return;
            }
            var slider = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<RangeBase>()
                .FirstOrDefault(r => BindingOperations.GetBinding(r, RangeBase.ValueProperty)?.Path?.Path == entry.Value.Path);
            if (slider != null)
            {
                slider.Value = number;
                slider.GetBindingExpression(RangeBase.ValueProperty)?.UpdateSource();
                return;
            }
            Through(entry, data, number.ToString("R", CultureInfo.InvariantCulture));
        }

        private static string Num(double? n) => n == null ? "?" : n.Value.ToString("0.###", CultureInfo.InvariantCulture);

        private void WriteSwitch(SearchEntry entry, FrameworkElement row, object data, JsonElement value)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new AssistantRefusal("bad-argument", "This setting is a switch: it takes true or false.");
            bool on = value.ValueKind == JsonValueKind.True;
            var toggle = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ToggleButton>()
                .FirstOrDefault(t => BindingOperations.GetBinding(t, ToggleButton.IsCheckedProperty)?.Path?.Path == entry.Value.Path);
            if (toggle != null)
            {
                if (!toggle.IsEnabled)
                    throw new AssistantRefusal("not-available", "The page has this switch turned off for now. show takes the user to it, where the reason shows.");
                toggle.IsChecked = on;
                toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
                return;
            }
            Through(entry, data, on);
        }

        private void WriteChoice(SearchEntry entry, FrameworkElement row, JsonElement value)
        {
            var combo = row == null ? null : SearchLocator.LogicalDescendants(row).OfType<ComboBox>().FirstOrDefault()
                ?? throw new AssistantRefusal("not-available", "The page does not show this picker now, so it cannot be set.");
            if (combo == null) throw new AssistantRefusal("not-available", "The page does not show this picker now, so it cannot be set.");
            var choices = Choices(combo);
            int at = -1;
            if (value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString()?.Trim() ?? string.Empty;
                at = choices.FindIndex(c => string.Equals(c, text, StringComparison.CurrentCultureIgnoreCase));
                if (at < 0) at = choices.FindIndex(c => string.Equals(SearchMatcher.FoldQuotes(c), SearchMatcher.FoldQuotes(text), StringComparison.CurrentCultureIgnoreCase));
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) && index >= 0 && index < choices.Count)
                at = index;
            if (at < 0)
                throw new AssistantRefusal("out-of-range", "This setting takes one of: " + string.Join(", ", choices.Select(c => "\"" + c + "\"")) + ".");
            if (!combo.IsEnabled)
                throw new AssistantRefusal("not-available", "The page has this picker turned off for now. show takes the user to it.");
            combo.SelectedIndex = at;
            foreach (var property in new[] { Selector.SelectedItemProperty, Selector.SelectedValueProperty, Selector.SelectedIndexProperty })
                combo.GetBindingExpression(property)?.UpdateSource();
        }

        /// <summary>A write with no control in place to take it: through a
        /// binding with the same path and converter the control's has.</summary>
        private void Through(SearchEntry entry, object data, object value)
        {
            if (data == null) throw new AssistantRefusal("not-available", "The page does not show this setting now, so it cannot be set.");
            var converter = entry.Value.ConverterKey == null ? null : _pageOf(entry.Page)?.TryFindResource(entry.Value.ConverterKey) as IValueConverter;
            BindingProbe.Write(data, entry.Value.Path, value, converter);
        }

        // ─────────────────────────────────────────────
        //  show
        // ─────────────────────────────────────────────

        public AssistantShown Show(AssistantShowTarget target)
        {
            SearchResult result;
            string where;
            switch (target.Kind)
            {
                case AssistantShowKind.Setting:
                {
                    var (shown, entry) = Resolve(target.Id);
                    if (shown == null)
                        throw new AssistantRefusal("not-available", "The page does not show this setting now, for that controller or device.");
                    var s = shown.Value;
                    result = new SearchResult { Target = SearchTarget.Entry, Entry = entry, Payload = s.Item ?? s.Label, Pad = s.Pad };
                    where = _service.WhereFor(s);
                    break;
                }
                case AssistantShowKind.Device:
                {
                    var row = _vm.Devices?.Devices?.FirstOrDefault(r => AutomationDevices.IdOf(r.InstanceGuid) == target.Id?.Trim())
                        ?? throw new AssistantRefusal("unknown-device", "No device has the ID " + target.Id + ". list_devices gives them.");
                    result = new SearchResult { Target = SearchTarget.Device, Payload = row };
                    where = PadForge.Resources.Strings.Strings.Instance.Devices_Title;
                    break;
                }
                case AssistantShowKind.Controller:
                {
                    int number = int.Parse(target.Id, CultureInfo.InvariantCulture);
                    int index = SettingsManager.SlotOrders.PadIndexForGlobalSlotNumber(number);
                    if (index < 0) throw new AssistantRefusal("unknown-slot", "No virtual controller has that number. list_controllers gives them.");
                    result = new SearchResult { Target = SearchTarget.Controller, Payload = index };
                    where = SearchService.SlotName(index < _vm.Pads.Count ? _vm.Pads[index] : null);
                    break;
                }
                default:
                {
                    string want = target.Id?.Trim() ?? string.Empty;
                    var profile = _vm.Settings?.ProfileItems?.FirstOrDefault(p => p.Id == want)
                        ?? _vm.Settings?.ProfileItems?.FirstOrDefault(p => string.Equals(p.Name, want, StringComparison.CurrentCultureIgnoreCase))
                        ?? throw new AssistantRefusal("unknown-profile", "No profile has that name or ID. list_profiles gives them.");
                    result = new SearchResult { Target = SearchTarget.Profile, Payload = profile };
                    where = PadForge.Resources.Strings.Strings.Instance.Profiles_Title;
                    break;
                }
            }
            _navigator.Open(result, takeFocus: false);
            var place = _windowPlace();
            return new AssistantShown
            {
                Shown = true,
                Where = where,
                Window = place,
                Message = place switch
                {
                    AssistantWindowPlace.InFront => "PadForge's window shows it now, ringed for a moment.",
                    AssistantWindowPlace.Behind => "PadForge's window is behind another window. Ask the user to bring PadForge to the front, where the page shows it.",
                    AssistantWindowPlace.Minimized => "PadForge's window is minimized. Ask the user to open it from the taskbar, where the page shows it.",
                    _ => "PadForge is in the notification area. Ask the user to open it from its tray icon, where the page shows it.",
                },
            };
        }
    }
}
