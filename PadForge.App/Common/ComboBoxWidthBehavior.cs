using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Resources.Strings;

namespace PadForge.Common
{
    /// <summary>
    /// Sizes a ComboBox to its WIDEST ITEM instead of its selected item.
    ///
    /// <para>Why: a MinWidth-only ComboBox resizes to whatever option is
    /// selected (neighbors fall out of column), and a fixed Width clips
    /// longer locales (Italian and German option labels run well past the
    /// English-tuned pixel widths, owner report 2026-07-16). Measuring every
    /// item gives a width that is stable across selection changes AND wide
    /// enough for the active language by construction.</para>
    ///
    /// <para>Remeasures when ItemsSource changes identity, which is exactly
    /// what a live language switch does (the option lists rebuild and the
    /// bindings hand the combo a new list instance). A combo whose options
    /// are written in the markup has no list to rebuild: its items keep
    /// their identity and take new words, so every loaded combo is measured
    /// again when the language changes. The hook subscribes on
    /// Loaded and detaches when unloaded or disabled, because
    /// DependencyPropertyDescriptor.AddValueChanged roots the element:
    /// without the symmetric remove, regenerated item-template combos (menu
    /// cell rows) would leak.</para>
    ///
    /// <para><see cref="WidthGroupProperty"/> keeps NEIGHBORS uniform: all
    /// live members of a group take the group's maximum measured width, so a
    /// stacked column of combos shares one edge.</para>
    /// </summary>
    public static class ComboBoxWidthBehavior
    {
        /// <summary>Closed-box chrome besides the item text: content margin,
        /// toggle arrow region, borders (WPF-UI ComboBox template).</summary>
        private const double ChromeWidth = 52;

        /// <summary>Safety cap, comfortably above any current locale's
        /// longest option. A pathological string clips at this width and
        /// shows in full in the dropdown popup, which auto-sizes.</summary>
        private const double MaxAutoWidth = 420;

        public static readonly DependencyProperty SizeToItemsProperty =
            DependencyProperty.RegisterAttached("SizeToItems", typeof(bool),
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(false, OnSizeToItemsChanged));

        public static void SetSizeToItems(DependencyObject d, bool value) => d.SetValue(SizeToItemsProperty, value);
        public static bool GetSizeToItems(DependencyObject d) => (bool)d.GetValue(SizeToItemsProperty);

        public static readonly DependencyProperty WidthGroupProperty =
            DependencyProperty.RegisterAttached("WidthGroup", typeof(string),
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(null, OnWidthGroupChanged));

        public static void SetWidthGroup(DependencyObject d, string value) => d.SetValue(WidthGroupProperty, value);
        public static string GetWidthGroup(DependencyObject d) => (string)d.GetValue(WidthGroupProperty);

        private static void OnWidthGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ComboBox cb || !cb.IsLoaded || !GetSizeToItems(cb)) return;
            Unregister(cb, e.OldValue as string);
            Register(cb);
            Resize(cb);
        }

        /// <summary>Explicit remeasure trigger (#413). The behavior watches
        /// ItemsSource IDENTITY, which is right for the option lists that
        /// rebuild on a culture switch. A collection reconciled IN PLACE, the
        /// way the layer pickers must be so a rename never drops the selected
        /// item, keeps its identity while its labels change, and nothing here
        /// would notice. Bind a counter the owner bumps after each reconcile
        /// and the combo remeasures on every change of it.</summary>
        public static readonly DependencyProperty ItemsVersionProperty =
            DependencyProperty.RegisterAttached("ItemsVersion", typeof(int),
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(0, OnItemsVersionChanged));

        public static void SetItemsVersion(DependencyObject d, int value) => d.SetValue(ItemsVersionProperty, value);
        public static int GetItemsVersion(DependencyObject d) => (int)d.GetValue(ItemsVersionProperty);

        private static void OnItemsVersionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ComboBox cb && cb.IsLoaded && GetSizeToItems(cb)) Resize(cb);
        }

        /// <summary>This combo's own measured width (before group max).</summary>
        private static readonly DependencyProperty MeasuredWidthProperty =
            DependencyProperty.RegisterAttached("MeasuredWidth", typeof(double),
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(0.0));

        private static readonly Dictionary<string, List<WeakReference<ComboBox>>> _groups = new();
        private static readonly Dictionary<(Type, string), PropertyInfo> _displayProps = new();

        // Every loaded combo the behavior sizes, held weakly, for the
        // measuring a language change asks for.
        private static readonly List<WeakReference<ComboBox>> _live = new();

        static ComboBoxWidthBehavior()
        {
            Strings.CultureChanged += RemeasureAll;
        }

        /// <summary>Measures every loaded combo again. Each is measured on
        /// its own dispatcher, after the bindings have given its options
        /// their new words.</summary>
        internal static void RemeasureAll()
        {
            var combos = new List<ComboBox>();
            lock (_live)
            {
                for (int i = _live.Count - 1; i >= 0; i--)
                {
                    if (_live[i].TryGetTarget(out var cb)) combos.Add(cb);
                    else _live.RemoveAt(i);
                }
            }
            foreach (var cb in combos)
                cb.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Resize(cb)));
        }

        private static void Track(ComboBox cb, bool live)
        {
            lock (_live)
            {
                _live.RemoveAll(wr => !wr.TryGetTarget(out var member) || ReferenceEquals(member, cb));
                if (live) _live.Add(new WeakReference<ComboBox>(cb));
            }
        }

        private static void OnSizeToItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ComboBox cb) return;
            if ((bool)e.NewValue)
            {
                cb.Loaded += OnLoaded;
                cb.Unloaded += OnUnloaded;
                if (cb.IsLoaded) OnLoaded(cb, null);
            }
            else
            {
                cb.Loaded -= OnLoaded;
                cb.Unloaded -= OnUnloaded;
                OnUnloaded(cb, null);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var cb = (ComboBox)sender;
            if (!GetSizeToItems(cb)) return;
            var descriptor = DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox));
            descriptor.RemoveValueChanged(cb, OnItemsSourceChanged);
            descriptor.AddValueChanged(cb, OnItemsSourceChanged);
            Track(cb, true);
            Register(cb);
            Resize(cb);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var cb = (ComboBox)sender;
            DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox))
                .RemoveValueChanged(cb, OnItemsSourceChanged);
            Track(cb, false);
            // A regenerated template's dead combo can linger alive (weak
            // ref, not yet collected) and would otherwise pin the group
            // maximum at its last measurement.
            cb.SetValue(MeasuredWidthProperty, 0.0);
            Unregister(cb, GetWidthGroup(cb));
        }

        private static void OnItemsSourceChanged(object sender, EventArgs e)
        {
            if (sender is ComboBox cb) Resize(cb);
        }

        private static void Register(ComboBox cb)
        {
            string group = GetWidthGroup(cb);
            if (string.IsNullOrEmpty(group)) return;
            if (!_groups.TryGetValue(group, out var list))
                _groups[group] = list = new List<WeakReference<ComboBox>>();
            foreach (var wr in list)
                if (wr.TryGetTarget(out var existing) && ReferenceEquals(existing, cb)) return;
            list.Add(new WeakReference<ComboBox>(cb));
        }

        private static void Resize(ComboBox cb)
        {
            if (!cb.IsLoaded || !GetSizeToItems(cb)) return;
            double measured = MeasureWidestItem(cb);
            if (measured <= 0) return;
            cb.SetValue(MeasuredWidthProperty, measured);

            string group = GetWidthGroup(cb);
            if (string.IsNullOrEmpty(group) || !_groups.TryGetValue(group, out var list))
            {
                cb.Width = measured;
                return;
            }

            ReapplyGroup(group, list);
        }

        private static void Unregister(ComboBox cb, string group)
        {
            if (string.IsNullOrEmpty(group) || !_groups.TryGetValue(group, out var list)) return;
            list.RemoveAll(wr => !wr.TryGetTarget(out var member) || ReferenceEquals(member, cb));
            ReapplyGroup(group, list);
        }

        private static void ReapplyGroup(string group, List<WeakReference<ComboBox>> list)
        {
            // Retired controls stop contributing immediately. Snapshot active
            // members before Width changes can trigger another lifecycle event.
            double groupMax = 0;
            var active = new List<ComboBox>();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out var member) || !member.IsLoaded
                    || !GetSizeToItems(member) || GetWidthGroup(member) != group)
                { list.RemoveAt(i); continue; }
                active.Add(member);
                double w = (double)member.GetValue(MeasuredWidthProperty);
                if (w > groupMax) groupMax = w;
            }
            if (list.Count == 0) _groups.Remove(group);
            if (groupMax <= 0) return;
            // A member with a MaxWidth of its own stops there, as it does
            // when it is sized alone.
            foreach (var member in active)
                if (member.IsLoaded && GetSizeToItems(member) && GetWidthGroup(member) == group)
                    member.Width = Math.Min(groupMax, member.MaxWidth);
        }

        private static double MeasureWidestItem(ComboBox cb)
        {
            // The options of a combo with no list are the items written
            // in its markup.
            System.Collections.IEnumerable items = cb.ItemsSource ?? cb.Items;

            var typeface = new Typeface(cb.FontFamily, cb.FontStyle, cb.FontWeight, cb.FontStretch);
            double ppd = VisualTreeHelper.GetDpi(cb).PixelsPerDip;
            string path = cb.DisplayMemberPath;

            double maxText = 0;
            foreach (var item in items)
            {
                if (item == null) continue;
                string text = ResolveDisplayText(item, path);
                if (string.IsNullOrEmpty(text)) continue;
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture,
                    cb.FlowDirection, typeface, cb.FontSize, Brushes.Black, ppd);
                if (ft.WidthIncludingTrailingWhitespace > maxText)
                    maxText = ft.WidthIncludingTrailingWhitespace;
            }
            if (maxText <= 0) return 0;

            double width = Math.Ceiling(maxText) + ChromeWidth;
            if (width < cb.MinWidth) width = cb.MinWidth;
            if (width > MaxAutoWidth) width = MaxAutoWidth;
            // A picker with a MaxWidth of its own is never sized past it. The
            // template draws its box at the Width it is given, so a Width over
            // the MaxWidth drew the end of the box outside the picker's room.
            if (width > cb.MaxWidth) width = cb.MaxWidth;
            return width;
        }

        private static string ResolveDisplayText(object item, string displayMemberPath)
        {
            if (item is string s) return s;
            // An item written in the markup: its words are its content.
            if (item is ContentControl written) return written.Content as string;
            if (string.IsNullOrEmpty(displayMemberPath)) return item.ToString();
            var key = (item.GetType(), displayMemberPath);
            if (!_displayProps.TryGetValue(key, out var prop))
                _displayProps[key] = prop = item.GetType().GetProperty(displayMemberPath);
            return prop?.GetValue(item)?.ToString() ?? item.ToString();
        }
    }
}
