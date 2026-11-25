using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PadForge.Common;
using PadForge.Common.Input;
using PadForge.Engine.Data;
using PadForge.Search;
using PadForge.ViewModels;

namespace PadForge.Services.Catalog
{
    /// <summary>
    /// A mapping row's mouse settings as catalog entries (#600, item 40):
    /// the travel, centering and gate settings of each mouse source of every
    /// controller's rows, Mouse Speed's and Mouse Position's own, and "This
    /// axis moves relatively". An assistant reads and sets them as a person
    /// does on the row: each value is read from the row's view model and set
    /// through the same property the row's control sets, so the row's own
    /// save follows. Only what the row shows now is an entry, and a set of
    /// one the row no longer shows is refused. They join the catalog through
    /// W1's one seam for a live object's settings
    /// (<see cref="LiveCatalog.AddProvider"/>).
    /// <para>IDs follow no interface language:
    /// <c>row/c&lt;controller&gt;/&lt;row&gt;/mouse/&lt;key&gt;</c> for a row's
    /// first source and <c>row/c&lt;controller&gt;/&lt;row&gt;/s&lt;n&gt;/mouse/&lt;key&gt;</c>
    /// for its added source n, counted from 1. Every one is Free: the
    /// settings change how the mouse moves the stick, and none runs a
    /// program, opens a place or sends input to Windows.</para>
    /// <para>Each entry's element is its line where the controller page
    /// draws it, which the guided pointer rings (#600 item 7), and
    /// <see cref="SpotOf"/> reads an ID back into its controller, row and
    /// source for the walk to it.</para>
    /// </summary>
    internal static class MouseSteeringCatalog
    {
        private static WeakReference<MainViewModel> s_main;
        private static readonly Func<IReadOnlyList<LiveItem>> s_provider = Items;
        private static bool s_registered;

        /// <summary>The controller page the rows are drawn on, for each
        /// entry's element. The assistants' catalog gives it as it is made,
        /// from the window's own pages.</summary>
        internal static Func<FrameworkElement> DrawnOn { get; set; }

        /// <summary>Puts the rows of <paramref name="main"/> in the catalog.
        /// The window's view model calls it once it is made, and a later one
        /// takes the place of the first.</summary>
        internal static void Attach(MainViewModel main)
        {
            s_main = main == null ? null : new WeakReference<MainViewModel>(main);
            if (s_registered) return;
            s_registered = true;
            LiveCatalog.AddProvider(s_provider);
        }

        /// <summary>The entries now, read on the interface thread.</summary>
        internal static IReadOnlyList<LiveItem> Items()
        {
            var items = new List<LiveItem>();
            if (s_main == null || !s_main.TryGetTarget(out var main) || main?.Pads == null) return items;
            for (int i = 0; i < InputManager.MaxPads && i < main.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i]) continue;
                AddPad(items, main.Pads[i], NumberOf(i));
            }
            return items;
        }

        /// <summary>A controller's number in the IDs: the one the sidebar
        /// shows, or its place counted from 1 when it has none.</summary>
        private static int NumberOf(int padIndex)
        {
            int number = SettingsManager.SlotOrders.GetGlobalSlotNumber(padIndex);
            return number > 0 ? number : padIndex + 1;
        }

        /// <summary>The entries of one controller's rows, for a test and for
        /// <see cref="Items"/>.</summary>
        internal static void AddPad(List<LiveItem> items, PadViewModel pad, int number)
        {
            if (pad?.Mappings == null) return;
            string controller = SearchService.SlotName(pad);
            for (int r = 0; r < pad.Mappings.Count; r++)
            {
                var row = pad.Mappings[r];
                string id = "row/c" + number.ToString(CultureInfo.InvariantCulture) + "/" + r.ToString(CultureInfo.InvariantCulture);
                string where = controller + ", " + row.TargetLabel;
                AddLine(items, new RowLine(row), row, row, id + "/mouse/", where);
                for (int k = 0; k < row.ExtraSources.Count; k++)
                    AddLine(items, new SourceLine(row.ExtraSources[k]), row, row.ExtraSources[k],
                        id + "/s" + (k + 1).ToString(CultureInfo.InvariantCulture) + "/mouse/", where);
            }
        }

        /// <summary>The entries of one source's line. Each carries its
        /// element: the line where the controller page draws it now.</summary>
        private static void AddLine(List<LiveItem> items, ILine line, MappingItem row, object source, string prefix, string where)
        {
            var s = PadForge.Resources.Strings.Strings.Instance;
            var mine = new List<LiveItem>();
            if (line.AxisRelativeShown)
                mine.Add(Switch(prefix + "relative", s.Mapping_AxisRelative, where,
                    () => line.AxisRelativeShown, () => line.AxisRelative, v => line.AxisRelative = v));
            if (line.TravelShown)
            {
                mine.Add(Number(prefix + "lock", s.Mapping_MouseTravelCounts, where, () => line.TravelShown,
                    () => line.Counts, v => line.Counts = v, "0"));
                mine.Add(Choice(prefix + "centering", s.Mapping_MouseTravelCentering, where, () => line.TravelShown,
                    MouseTravelLine.CenteringChoices, () => line.Centering, v => line.Centering = v));
            }
            if (line.ReturnTimeShown)
                mine.Add(Number(prefix + "return", s.Mapping_MouseTravelReturnTime, where, () => line.ReturnTimeShown,
                    () => line.ReturnTime, v => line.ReturnTime = v, "0.00"));
            if (line.SpeedShown)
            {
                mine.Add(Number(prefix + "sensitivity", s.Mapping_MouseSpeedSensitivity, where, () => line.SpeedShown,
                    () => line.SpeedSensitivity, v => line.SpeedSensitivity = v, "0.00"));
                mine.Add(Number(prefix + "back", s.Mapping_MouseBackToCenter, where, () => line.SpeedShown,
                    () => line.BackToCenter, v => line.BackToCenter = v, "0.00"));
            }
            if (line.SteerShown)
            {
                mine.Add(Choice(prefix + "steer", s.Mapping_MouseTravelSteer, where, () => line.SteerShown,
                    MouseTravelLine.SteerChoices, () => line.Steer, v => line.Steer = v));
                mine.Add(Number(prefix + "smoothing", s.Mapping_MouseTravelSmoothing, where, () => line.SteerShown,
                    () => line.SmoothingMs, v => line.SmoothingMs = v, "0"));
            }
            if (line.SteerInputShown)
                mine.Add(new LiveItem(prefix + "steerinput", LiveKind.Setting, s.Mapping_MouseTravelSteerInput, PadForge.Common.ReachClass.Free)
                {
                    Where = where,
                    Read = () => line.SteerInput?.DisplayName ?? string.Empty,
                    Write = text =>
                    {
                        if (!line.SteerInputShown) throw new InvalidOperationException("This setting is not shown on the row now.");
                        line.SteerInput = FindInput(line.SteerInputs, text)
                            ?? throw new ArgumentException("No such input on the source's device: " + text);
                    },
                });
            if (line.AnchorShown)
                mine.Add(Switch(prefix + "anchor", s.Mapping_MousePositionAnchor, where,
                    () => line.AnchorShown, () => line.Anchor, v => line.Anchor = v));
            if (line.LockAtSpeedShown)
                mine.Add(Number(prefix + "lockatspeed", s.Mapping_MouseTravelLockAtSpeed, where, () => line.LockAtSpeedShown,
                    () => line.LockAtSpeed, v => line.LockAtSpeed = v, "0"));
            foreach (var item in mine)
            {
                string key = item.Id.Substring(prefix.Length);
                items.Add(item with { Element = () => LineElement(DrawnOn?.Invoke(), row, source, key) });
            }
        }

        /// <summary>Where an entry stands: its controller, its row, its
        /// source (the row itself for the row's first source, else the added
        /// one) and its key.</summary>
        internal sealed record Spot(PadViewModel Pad, MappingItem Row, object Source, string Key);

        /// <summary>An entry's ID read back into where it stands, with the
        /// numbering <see cref="Items"/> gives. Null for an ID of no row's
        /// mouse setting.</summary>
        internal static Spot SpotOf(MainViewModel main, string id)
        {
            if (main?.Pads == null || id == null || !id.StartsWith("row/c", StringComparison.Ordinal)) return null;
            // row/c<n>/<r>/mouse/<key>, or row/c<n>/<r>/s<k>/mouse/<key>.
            string[] parts = id.Split('/');
            if (parts.Length is not (5 or 6) || parts[^2] != "mouse") return null;
            if (!int.TryParse(parts[1].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int r))
                return null;
            int k = 0;
            if (parts.Length == 6
                && (!parts[3].StartsWith('s') || !int.TryParse(parts[3].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out k) || k < 1))
                return null;
            for (int i = 0; i < InputManager.MaxPads && i < main.Pads.Count; i++)
            {
                if (!SettingsManager.SlotCreated[i] || NumberOf(i) != number) continue;
                var pad = main.Pads[i];
                if (pad?.Mappings == null || r >= pad.Mappings.Count) return null;
                var row = pad.Mappings[r];
                if (k > row.ExtraSources.Count) return null;
                return new Spot(pad, row, k == 0 ? row : row.ExtraSources[k - 1], parts[^1]);
            }
            return null;
        }

        /// <summary>The property each key's line resets, as the line's reset
        /// button names it (PadPage.xaml's SourceDials).</summary>
        private static readonly Dictionary<string, string> ResetOf = new(StringComparer.Ordinal)
        {
            ["relative"] = nameof(MappingItem.ParamAxisRelative),
            ["lock"] = nameof(MappingItem.ParamTravelCounts),
            ["centering"] = nameof(MappingItem.ParamTravelCentering),
            ["return"] = nameof(MappingItem.ParamTravelReturnTime),
            ["sensitivity"] = nameof(MappingItem.ParamMouseSpeedSensitivity),
            ["back"] = nameof(MappingItem.ParamMouseBackToCenter),
            ["steer"] = nameof(MappingItem.ParamTravelSteer),
            ["smoothing"] = nameof(MappingItem.ParamTravelSmoothingMs),
            ["steerinput"] = nameof(MappingItem.TravelSteerInputChoice),
            ["anchor"] = nameof(MappingItem.ParamMousePositionAnchor),
            ["lockatspeed"] = nameof(MappingItem.ParamTravelLockAtSpeed),
        };

        /// <summary>
        /// A mouse setting's line where the controller page draws it now: in
        /// the list's open row, or under the picture's bar for the picked
        /// row. A line holds the setting's label, its control and its reset,
        /// and is found by the reset, which names the source it resets and
        /// the property. The line must show: the reset itself is not asked.
        /// Null while neither draws it, and off the page's thread.
        /// </summary>
        internal static FrameworkElement LineElement(FrameworkElement page, MappingItem row, object source, string key)
        {
            if (page == null || row == null || source == null || key == null || !page.Dispatcher.CheckAccess()
                || !ResetOf.TryGetValue(key, out string property)) return null;
            foreach (var drawn in DrawnRows(page, row))
                foreach (var reset in SearchLocator.VisualDescendants(drawn).OfType<PadForge.Controls.SettingResetButton>())
                    if (ReferenceEquals(reset.DataContext, source) && reset.CommandParameter as string == property
                        && VisualTreeHelper.GetParent(reset) is FrameworkElement { IsVisible: true } line)
                        return line;
            return null;
        }

        /// <summary>Where the page draws a row's lines: its row in the list,
        /// and the bar under the picture.</summary>
        private static IEnumerable<DependencyObject> DrawnRows(FrameworkElement page, MappingItem row)
        {
            if (page.FindName("MappingDataGrid") is DataGrid grid && grid.ItemContainerGenerator.ContainerFromItem(row) is DataGridRow listed)
                yield return listed;
            if (page.FindName("PickedLines") is DependencyObject bar) yield return bar;
        }

        private static LiveItem Number(string id, string words, string where, Func<bool> shown,
            Func<double> get, Action<double> set, string format)
            => new(id, LiveKind.Setting, words, PadForge.Common.ReachClass.Free)
            {
                Where = where,
                Read = () => get().ToString(format, CultureInfo.InvariantCulture),
                Write = text =>
                {
                    if (!shown()) throw new InvalidOperationException("This setting is not shown on the row now.");
                    if (!double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        || !double.IsFinite(v))
                        throw new ArgumentException("Not a number: " + text);
                    set(v);
                },
            };

        private static LiveItem Switch(string id, string words, string where, Func<bool> shown,
            Func<bool> get, Action<bool> set)
            => new(id, LiveKind.Setting, words, PadForge.Common.ReachClass.Free)
            {
                Where = where,
                Read = () => get() ? "true" : "false",
                Write = text =>
                {
                    if (!shown()) throw new InvalidOperationException("This setting is not shown on the row now.");
                    set((text ?? string.Empty).Trim().ToLowerInvariant() switch
                    {
                        "true" or "on" or "1" => true,
                        "false" or "off" or "0" => false,
                        _ => throw new ArgumentException("Not on or off: " + text),
                    });
                },
            };

        private static LiveItem Choice(string id, string words, string where, Func<bool> shown,
            Func<IReadOnlyList<MouseTravelChoice>> choices, Func<string> get, Action<string> set)
            => new(id, LiveKind.Setting, words, PadForge.Common.ReachClass.Free)
            {
                Where = where,
                Read = () =>
                {
                    string value = get();
                    foreach (var c in choices()) if (c.Value == value) return c.Display;
                    return value;
                },
                Write = text =>
                {
                    if (!shown()) throw new InvalidOperationException("This setting is not shown on the row now.");
                    string t = (text ?? string.Empty).Trim();
                    foreach (var c in choices())
                        if (string.Equals(c.Value, t, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(c.Display, t, StringComparison.CurrentCultureIgnoreCase))
                        {
                            set(c.Value);
                            return;
                        }
                    throw new ArgumentException("Not one of the choices: " + text);
                },
            };

        private static InputChoice FindInput(IReadOnlyList<InputChoice> inputs, string text)
        {
            string t = (text ?? string.Empty).Trim();
            if (inputs == null || t.Length == 0) return null;
            foreach (var c in inputs)
                if (string.Equals(c.Descriptor, t, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (var c in inputs)
                if (string.Equals(c.DisplayName, t, StringComparison.CurrentCultureIgnoreCase)) return c;
            return null;
        }

        /// <summary>A source's line as the catalog reads it: a row's first
        /// source or an added one, which name these alike.</summary>
        private interface ILine
        {
            bool AxisRelativeShown { get; }
            bool TravelShown { get; }
            bool ReturnTimeShown { get; }
            bool SpeedShown { get; }
            bool SteerShown { get; }
            bool SteerInputShown { get; }
            bool AnchorShown { get; }
            bool LockAtSpeedShown { get; }
            bool AxisRelative { get; set; }
            double Counts { get; set; }
            string Centering { get; set; }
            double ReturnTime { get; set; }
            double SpeedSensitivity { get; set; }
            double BackToCenter { get; set; }
            string Steer { get; set; }
            double SmoothingMs { get; set; }
            double LockAtSpeed { get; set; }
            bool Anchor { get; set; }
            IReadOnlyList<InputChoice> SteerInputs { get; }
            InputChoice SteerInput { get; set; }
        }

        private sealed class RowLine : ILine
        {
            private readonly MappingItem _m;
            internal RowLine(MappingItem m) => _m = m;
            public bool AxisRelativeShown => _m.IsAxisRelativeApplicable;
            public bool TravelShown => _m.IsMouseTravelSource;
            public bool ReturnTimeShown => _m.IsTravelReturnTimeShown;
            public bool SpeedShown => _m.IsMouseSpeedSource;
            public bool SteerShown => _m.IsMouseSteerSource;
            public bool SteerInputShown => _m.IsTravelSteerInputShown;
            public bool AnchorShown => _m.IsMousePositionAnchorShown;
            public bool LockAtSpeedShown => _m.IsTravelLockAtSpeedShown;
            public bool AxisRelative { get => _m.ParamAxisRelative; set => _m.ParamAxisRelative = value; }
            public double Counts { get => _m.ParamTravelCounts; set => _m.ParamTravelCounts = value; }
            public string Centering { get => _m.ParamTravelCentering; set => _m.ParamTravelCentering = value; }
            public double ReturnTime { get => _m.ParamTravelReturnTime; set => _m.ParamTravelReturnTime = value; }
            public double SpeedSensitivity { get => _m.ParamMouseSpeedSensitivity; set => _m.ParamMouseSpeedSensitivity = value; }
            public double BackToCenter { get => _m.ParamMouseBackToCenter; set => _m.ParamMouseBackToCenter = value; }
            public string Steer { get => _m.ParamTravelSteer; set => _m.ParamTravelSteer = value; }
            public double SmoothingMs { get => _m.ParamTravelSmoothingMs; set => _m.ParamTravelSmoothingMs = value; }
            public double LockAtSpeed { get => _m.ParamTravelLockAtSpeed; set => _m.ParamTravelLockAtSpeed = value; }
            public bool Anchor { get => _m.ParamMousePositionAnchor; set => _m.ParamMousePositionAnchor = value; }
            public IReadOnlyList<InputChoice> SteerInputs => _m.TravelSteerInputs;
            public InputChoice SteerInput { get => _m.TravelSteerInputChoice; set => _m.TravelSteerInputChoice = value; }
        }

        private sealed class SourceLine : ILine
        {
            private readonly MappingSourceItem _m;
            internal SourceLine(MappingSourceItem m) => _m = m;
            public bool AxisRelativeShown => _m.IsAxisRelativeApplicable;
            public bool TravelShown => _m.IsMouseTravelSource;
            public bool ReturnTimeShown => _m.IsTravelReturnTimeShown;
            public bool SpeedShown => _m.IsMouseSpeedSource;
            public bool SteerShown => _m.IsMouseSteerSource;
            public bool SteerInputShown => _m.IsTravelSteerInputShown;
            public bool AnchorShown => _m.IsMousePositionAnchorShown;
            public bool LockAtSpeedShown => _m.IsTravelLockAtSpeedShown;
            public bool AxisRelative { get => _m.ParamAxisRelative; set => _m.ParamAxisRelative = value; }
            public double Counts { get => _m.ParamTravelCounts; set => _m.ParamTravelCounts = value; }
            public string Centering { get => _m.ParamTravelCentering; set => _m.ParamTravelCentering = value; }
            public double ReturnTime { get => _m.ParamTravelReturnTime; set => _m.ParamTravelReturnTime = value; }
            public double SpeedSensitivity { get => _m.ParamMouseSpeedSensitivity; set => _m.ParamMouseSpeedSensitivity = value; }
            public double BackToCenter { get => _m.ParamMouseBackToCenter; set => _m.ParamMouseBackToCenter = value; }
            public string Steer { get => _m.ParamTravelSteer; set => _m.ParamTravelSteer = value; }
            public double SmoothingMs { get => _m.ParamTravelSmoothingMs; set => _m.ParamTravelSmoothingMs = value; }
            public double LockAtSpeed { get => _m.ParamTravelLockAtSpeed; set => _m.ParamTravelLockAtSpeed = value; }
            public bool Anchor { get => _m.ParamMousePositionAnchor; set => _m.ParamMousePositionAnchor = value; }
            public IReadOnlyList<InputChoice> SteerInputs => _m.TravelSteerInputs;
            public InputChoice SteerInput { get => _m.TravelSteerInputChoice; set => _m.TravelSteerInputChoice = value; }
        }
    }
}
