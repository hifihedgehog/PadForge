using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace PadForge.Controls
{
    /// <summary>Has an element fade in when what it shows arrives (#574, the
    /// world-class pass): a Home card's picture, which is made a moment
    /// after the card. The element is bound to what it waits for, and when
    /// that goes from nothing to something the element rises from clear to
    /// its own opacity over a fifth of a second. The fade then lets go, so
    /// the element's opacity is its style's again.
    ///
    /// <para>Nothing fades while the element is in no window, when what it
    /// shows changes from one thing to another, or with Windows' animations
    /// off.</para></summary>
    public static class Appear
    {
        /// <summary>How long the fade takes.</summary>
        internal static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(200);

        /// <summary>Mirrors Windows' animation setting. A test sets it.</summary>
        internal static Func<bool> MotionEnabled = () => SystemParameters.ClientAreaAnimation;

        public static readonly DependencyProperty WhenProperty = DependencyProperty.RegisterAttached(
            "When", typeof(object), typeof(Appear), new PropertyMetadata(null, OnWhenChanged));

        public static object GetWhen(DependencyObject element) => element.GetValue(WhenProperty);

        public static void SetWhen(DependencyObject element, object value) => element.SetValue(WhenProperty, value);

        private static void OnWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || e.OldValue != null || e.NewValue == null) return;
            if (!element.IsLoaded || !MotionEnabled()) return;
            var fade = new DoubleAnimation
            {
                From = 0,
                Duration = FadeTime,
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            // A stopped fade stays on the element until it is taken off.
            fade.Completed += (_, _) => element.BeginAnimation(UIElement.OpacityProperty, null);
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
