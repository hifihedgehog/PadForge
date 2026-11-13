using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PadForge.Controls
{
    /// <summary>
    /// One thing the Settings page's search shows or hides (#574): a
    /// setting's row with any line that belongs to it, a row of buttons, a
    /// driver's row, or a part that holds such things (a More row, a fold).
    /// Its Visibility keeps the page's own rule for it, such as a row that
    /// shows only while the switch above it is on. While the search leaves
    /// it out, IsFilteredOut holds it collapsed whatever that rule says.
    /// WPF's DataGridRow coerces its Visibility the same way.
    ///
    /// <para>With <see cref="Rule"/> set it draws a hairline over itself,
    /// its whole width: the line the Settings board has over every row of a
    /// card. The page sets it once for all its items, and an item that
    /// stands under a line of its holder's takes it off again.</para>
    /// </summary>
    public sealed class SettingItem : StackPanel
    {
        static SettingItem()
        {
            VisibilityProperty.OverrideMetadata(typeof(SettingItem),
                new FrameworkPropertyMetadata(null, CoerceVisibility));
        }

        public static readonly DependencyProperty IsFilteredOutProperty = DependencyProperty.Register(
            nameof(IsFilteredOut), typeof(bool), typeof(SettingItem),
            new PropertyMetadata(false, (d, _) => d.CoerceValue(VisibilityProperty)));

        /// <summary>Whether the search leaves the item out.</summary>
        public bool IsFilteredOut
        {
            get => (bool)GetValue(IsFilteredOutProperty);
            set => SetValue(IsFilteredOutProperty, value);
        }

        public static readonly DependencyProperty RuleProperty = DependencyProperty.Register(
            nameof(Rule), typeof(Brush), typeof(SettingItem),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The hairline over the item, or none.</summary>
        public Brush Rule
        {
            get => (Brush)GetValue(RuleProperty);
            set => SetValue(RuleProperty, value);
        }

        /// <summary>The hairline's thickness.</summary>
        internal const double RuleHeight = 1.0;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            Brush rule = Rule;
            if (rule == null || !(RenderSize.Width > 0)) return;
            // On whole device pixels, as a card's edge is.
            dc.PushGuidelineSet(new GuidelineSet(null, new[] { 0.0, RuleHeight }));
            dc.DrawRectangle(rule, null, new Rect(0, 0, RenderSize.Width, RuleHeight));
            dc.Pop();
        }

        private Visibility _own = Visibility.Visible;

        /// <summary>What the page's own rule says, whatever the search says.</summary>
        internal Visibility OwnVisibility => _own;

        private static object CoerceVisibility(DependencyObject d, object baseValue)
        {
            var item = (SettingItem)d;
            item._own = (Visibility)baseValue;
            return item.IsFilteredOut ? Visibility.Collapsed : baseValue;
        }
    }
}
