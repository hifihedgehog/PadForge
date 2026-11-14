using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using PadForge.Resources.Strings;

namespace PadForge.Search
{
    /// <summary>Reads one binding once, with no element to show it (#574):
    /// a private element's one property is bound OneTime, read and cleared.
    /// Search reads the values of the rows it shows, and an item's gates,
    /// this way.</summary>
    internal static class BindingProbe
    {
        private sealed class Probe : DependencyObject
        {
            public static readonly DependencyProperty ValueProperty =
                DependencyProperty.Register("Value", typeof(object), typeof(Probe));
        }

        /// <summary>The value at <paramref name="path"/> on
        /// <paramref name="source"/>, through the converter when one is given,
        /// or null when the path reads nothing.</summary>
        internal static object Read(object source, string path, IValueConverter converter = null, object parameter = null, Type target = null)
        {
            if (source == null || string.IsNullOrEmpty(path)) return null;
            var probe = new Probe();
            BindingOperations.SetBinding(probe, Probe.ValueProperty, new Binding(path) { Source = source, Mode = BindingMode.OneTime });
            object value = probe.GetValue(Probe.ValueProperty);
            BindingOperations.ClearBinding(probe, Probe.ValueProperty);
            if (value == DependencyProperty.UnsetValue) return null;
            if (converter != null)
            {
                value = converter.Convert(value, target ?? typeof(object), parameter, CultureInfo.CurrentCulture);
                if (value == DependencyProperty.UnsetValue || value == Binding.DoNothing) return null;
            }
            return value;
        }

        /// <summary>A value as a binding's StringFormat writes it: a composite
        /// format when it holds {0}, else a format for the value alone.</summary>
        internal static string Format(object value, string stringFormat)
        {
            if (value == null) return null;
            if (string.IsNullOrEmpty(stringFormat)) return Convert.ToString(value, CultureInfo.CurrentCulture);
            string format = stringFormat.Contains("{0", StringComparison.Ordinal) ? stringFormat : "{0:" + stringFormat + "}";
            try { return string.Format(CultureInfo.CurrentCulture, format, value); }
            catch (FormatException) { return Convert.ToString(value, CultureInfo.CurrentCulture); }
        }

        /// <summary>Whether an item passes a visibility gate of its
        /// template.</summary>
        internal static bool Passes(object item, SearchGate gate, Func<string, object> resource)
        {
            var converter = gate.ConverterKey == null ? null : resource(gate.ConverterKey) as IValueConverter;
            object value = Read(item, gate.Path, converter, gate.ConverterParameter, typeof(Visibility));
            return value switch
            {
                Visibility v => v == Visibility.Visible,
                bool b => b,
                null => false,
                _ => true,
            };
        }
    }

    /// <summary>Finds what an entry stands for on its live page (#574): its
    /// label's element, the row or card around it, and whether it shows.
    /// The label is found by its words in the current language, the Nth of
    /// its kind in its scope when a scope shows the same words more than
    /// once. One walk of a scope serves every entry in it.</summary>
    internal sealed class SearchLocator
    {
        private readonly Func<SearchPage, FrameworkElement> _pageOf;
        private readonly Dictionary<(SearchPage, string), Dictionary<(SearchLabelSource, string), List<DependencyObject>>> _scopes = new();

        internal SearchLocator(Func<SearchPage, FrameworkElement> pageOf) => _pageOf = pageOf;

        /// <summary>Forgets every walk: the pages may have changed.</summary>
        internal void Reset() => _scopes.Clear();

        internal static string LabelText(SearchLabel label, object data)
        {
            if (label == null) return null;
            if (label.Key != null) return Strings.Get(label.Key);
            object value = BindingProbe.Read(data, label.Path);
            return value as string ?? value?.ToString();
        }

        internal static string EnglishText(SearchLabel label)
            => label?.Key == null ? null : Strings.ResourceManager.GetString(label.Key, CultureInfo.InvariantCulture);

        /// <summary>The element whose words are a label of this kind, or
        /// null for an element of another kind.</summary>
        internal static string TextOf(DependencyObject d, SearchLabelSource source) => source switch
        {
            SearchLabelSource.Text or SearchLabelSource.CardTitle when d is TextBlock tb => PadForge.Common.BidiText.GetText(tb) ?? tb.Text,
            SearchLabelSource.Toggle when d is ToggleButton t => t.Content as string,
            SearchLabelSource.ResetLabel when d is PadForge.Controls.SettingResetButton r => r.SettingLabel,
            SearchLabelSource.FoldHeader when d is HeaderedContentControl h => h.Header as string,
            SearchLabelSource.Button when d is ButtonBase b => b.Content as string,
            SearchLabelSource.Tile when d is PadForge.Controls.ExtraTile x => x.Title,
            _ => null,
        };

        internal static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject d) continue;
                yield return d;
                foreach (var below in LogicalDescendants(d)) yield return below;
            }
        }

        internal static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
        {
            int count = root is Visual || root is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
            for (int i = 0; i < count; i++)
            {
                var d = VisualTreeHelper.GetChild(root, i);
                yield return d;
                foreach (var below in VisualDescendants(d)) yield return below;
            }
        }

        internal DependencyObject ScopeOf(SearchEntry entry)
        {
            var page = _pageOf(entry.Page);
            if (page == null) return null;
            return entry.ScopeName != null && page.FindName(entry.ScopeName) is DependencyObject named ? named : page;
        }

        private Dictionary<(SearchLabelSource, string), List<DependencyObject>> Walk(SearchEntry entry)
        {
            var key = (entry.Page, entry.ScopeName ?? string.Empty);
            if (_scopes.TryGetValue(key, out var found)) return found;
            found = new Dictionary<(SearchLabelSource, string), List<DependencyObject>>();
            var scope = ScopeOf(entry);
            if (scope != null)
            {
                // The scope's own element comes first. An entry made from a
                // named fold card, button, tile or reset has that element as
                // its scope, and a walk of what lies under it never met it.
                foreach (var d in new[] { scope }.Concat(LogicalDescendants(scope)))
                {
                    foreach (SearchLabelSource source in Enum.GetValues(typeof(SearchLabelSource)))
                    {
                        if (source == SearchLabelSource.CardTitle) continue;
                        string text = TextOf(d, source);
                        if (string.IsNullOrEmpty(text)) continue;
                        var k = (source, text);
                        if (!found.TryGetValue(k, out var list)) found[k] = list = new List<DependencyObject>();
                        list.Add(d);
                    }
                }
            }
            _scopes[key] = found;
            return found;
        }

        /// <summary>The element that shows the label of an entry that is no
        /// item's and no row option's, or null when the page does not show
        /// it. <paramref name="index"/> gives the entries before it that read
        /// the same words in the same scope.</summary>
        internal DependencyObject Find(SearchEntry entry, IReadOnlyList<SearchEntry> index)
        {
            if (entry.ItemsPath != null || entry.RowOptions) return null;
            var page = _pageOf(entry.Page);
            if (page == null) return null;
            string text = LabelText(entry.Label, page.DataContext);
            if (string.IsNullOrEmpty(text)) return null;
            int n = 0;
            foreach (var other in index)
            {
                // Orders count within a page, so another page's are no guide.
                if (other.Page != entry.Page || other.Order >= entry.Order) continue;
                if (other.ScopeName == entry.ScopeName && other.ItemsPath == null && !other.RowOptions
                    && ListOf(other.LabelSource) == ListOf(entry.LabelSource) && LabelText(other.Label, page.DataContext) == text)
                    n++;
            }
            return Find(entry, n, text);
        }

        /// <summary>The list an entry's element is looked up in. A card's
        /// title is a text block, so it stands among the texts, and a title
        /// and a label that read the same words in one scope are counted
        /// together.</summary>
        private static SearchLabelSource ListOf(SearchLabelSource source)
            => source == SearchLabelSource.CardTitle ? SearchLabelSource.Text : source;

        /// <summary>The Nth element of its scope that shows an entry's words,
        /// <paramref name="text"/> in the current language.</summary>
        internal DependencyObject Find(SearchEntry entry, int n, string text)
        {
            if (entry.ItemsPath != null || entry.RowOptions || string.IsNullOrEmpty(text)) return null;
            var walk = Walk(entry);
            var source = ListOf(entry.LabelSource);
            walk.TryGetValue((source, text), out var list);
            if (list != null && n < list.Count) return list[n];
            // A card's title can run on past its words (a subtitle in a Run).
            // Those texts come after the ones that are the words exactly, in
            // the same count.
            if (entry.LabelSource == SearchLabelSource.CardTitle)
            {
                int at = n - (list?.Count ?? 0);
                foreach (var (k, all) in walk)
                {
                    if (k.Item1 != SearchLabelSource.Text || k.Item2.Length == text.Length
                        || !k.Item2.StartsWith(text, StringComparison.Ordinal))
                        continue;
                    if (at < all.Count) return all[at];
                    at -= all.Count;
                }
            }
            return null;
        }

        /// <summary>The ItemsControl of an item entry inside its scope.</summary>
        internal ItemsControl ListOf(SearchEntry entry)
        {
            var scope = ScopeOf(entry);
            if (scope == null) return null;
            return LogicalDescendants(scope).OfType<ItemsControl>().FirstOrDefault(ic =>
                BindingOperations.GetBinding(ic, ItemsControl.ItemsSourceProperty)?.Path?.Path == entry.ItemsPath);
        }

        /// <summary>The label's element of an item entry, inside the item's
        /// container (which shows once its section does), or the item's card
        /// for the card entry itself.</summary>
        internal DependencyObject FindInItem(SearchEntry entry, object item, IReadOnlyList<SearchEntry> index)
        {
            var list = ListOf(entry);
            if (list?.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container) return null;
            var parts = VisualDescendants(container).ToList();
            if (entry.Kind == SearchKind.Card)
                return parts.FirstOrDefault(p => p is PadForge.Controls.FoldCard || (p is Border b && b.Style != null && ReferenceEquals(b.Style, (list as FrameworkElement).TryFindResource("CardBorder"))))
                       ?? parts.OfType<Border>().FirstOrDefault();
            return FindAmong(parts, entry, item, index, e => e.ItemsPath == entry.ItemsPath && e.ScopeName == entry.ScopeName);
        }

        /// <summary>The label's element among the parts given, in their
        /// order: the Nth of the entries that match <paramref name="same"/>
        /// and read the same words.</summary>
        internal static DependencyObject FindAmong(IReadOnlyList<DependencyObject> parts, SearchEntry entry, object item,
            IReadOnlyList<SearchEntry> index, Func<SearchEntry, bool> same)
        {
            string text = LabelText(entry.Label, item);
            if (string.IsNullOrEmpty(text)) return null;
            int n = index.Count(o => o.Order < entry.Order && o.Page == entry.Page && same(o) && o.LabelSource == entry.LabelSource
                                     && LabelText(o.Label, item) == text);
            var source = entry.LabelSource == SearchLabelSource.CardTitle ? SearchLabelSource.Text : entry.LabelSource;
            foreach (var p in parts)
                if (TextOf(p, source) == text && n-- == 0)
                    return p;
            return null;
        }

        /// <summary>Whether an element sits under no collapsed ancestor, up to
        /// <paramref name="stop"/> (not counted).</summary>
        internal static bool Shows(DependencyObject d, DependencyObject stop)
        {
            for (var at = d; at != null && at != stop; at = Up(at))
                if (at is UIElement { Visibility: Visibility.Collapsed }) return false;
            return true;
        }

        internal static DependencyObject Up(DependencyObject d)
            => LogicalTreeHelper.GetParent(d) ?? (d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null);

        /// <summary>The row an entry's label belongs to, the card for a card,
        /// the button for an action: what a landing rings.</summary>
        internal static FrameworkElement TargetOf(DependencyObject label, SearchEntry entry)
        {
            if (entry.Kind == SearchKind.Card)
            {
                for (var at = label; at != null; at = Up(at))
                    if (at is PadForge.Controls.FoldCard || (at is Border { CornerRadius.TopLeft: >= 8 } border && border.BorderThickness.Top > 0 && at != label))
                        return (FrameworkElement)at;
                return label as FrameworkElement;
            }
            if (entry.Kind is SearchKind.Action or SearchKind.Extra) return label as FrameworkElement;
            return (Up(label) as FrameworkElement) ?? label as FrameworkElement;
        }
    }
}
