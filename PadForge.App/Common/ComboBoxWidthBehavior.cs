using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
    /// bindings hand the combo a new list instance). The hook subscribes on
    /// Loaded and unsubscribes on Unloaded, because
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
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(null));

        public static void SetWidthGroup(DependencyObject d, string value) => d.SetValue(WidthGroupProperty, value);
        public static string GetWidthGroup(DependencyObject d) => (string)d.GetValue(WidthGroupProperty);

        /// <summary>This combo's own measured width (before group max).</summary>
        private static readonly DependencyProperty MeasuredWidthProperty =
            DependencyProperty.RegisterAttached("MeasuredWidth", typeof(double),
                typeof(ComboBoxWidthBehavior), new PropertyMetadata(0.0));

        private static readonly Dictionary<string, List<WeakReference<ComboBox>>> _groups = new();
        private static readonly Dictionary<(Type, string), PropertyInfo> _displayProps = new();

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
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            var cb = (ComboBox)sender;
            DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox))
                .AddValueChanged(cb, OnItemsSourceChanged);
            Register(cb);
            Resize(cb);
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var cb = (ComboBox)sender;
            DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(ComboBox))
                .RemoveValueChanged(cb, OnItemsSourceChanged);
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
            double measured = MeasureWidestItem(cb);
            if (measured <= 0) return;
            cb.SetValue(MeasuredWidthProperty, measured);

            string group = GetWidthGroup(cb);
            if (string.IsNullOrEmpty(group) || !_groups.TryGetValue(group, out var list))
            {
                cb.Width = measured;
                return;
            }

            // Group max over live members, applied to every live member so
            // the whole column moves together (and dead entries prune here).
            double groupMax = measured;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out var member)) { list.RemoveAt(i); continue; }
                double w = (double)member.GetValue(MeasuredWidthProperty);
                if (w > groupMax) groupMax = w;
            }
            for (int i = 0; i < list.Count; i++)
                if (list[i].TryGetTarget(out var member))
                    member.Width = groupMax;
        }

        private static double MeasureWidestItem(ComboBox cb)
        {
            var items = cb.ItemsSource;
            if (items == null) return 0;

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
            return width;
        }

        private static string ResolveDisplayText(object item, string displayMemberPath)
        {
            if (item is string s) return s;
            if (string.IsNullOrEmpty(displayMemberPath)) return item.ToString();
            var key = (item.GetType(), displayMemberPath);
            if (!_displayProps.TryGetValue(key, out var prop))
                _displayProps[key] = prop = item.GetType().GetProperty(displayMemberPath);
            return prop?.GetValue(item)?.ToString() ?? item.ToString();
        }
    }
}
