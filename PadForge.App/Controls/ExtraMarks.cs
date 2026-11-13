using System.Windows;
using System.Windows.Controls;

namespace PadForge.Controls
{
    /// <summary>
    /// The square at the head of an extra's page (#574): the extra's glyph on
    /// a steel square while the extra is off, and in the ember text color on
    /// a warm square while it is on. It only decorates, since the page's title
    /// names the extra, so it takes no focus.
    /// </summary>
    public sealed class ExtraBadge : Control
    {
        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(ExtraBadge), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
            nameof(IsOn), typeof(bool), typeof(ExtraBadge), new PropertyMetadata(false));

        public ExtraBadge()
        {
            Focusable = false;
            IsTabStop = false;
        }

        /// <summary>The extra's glyph in Segoe MDL2 Assets, the one its catalog tile shows.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>Whether the extra is on. The square only reads it.</summary>
        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }
    }

    /// <summary>
    /// The flame of an extra's status card (#574): an outline while the extra
    /// is off, gold while it is on and waits (for a game, a tracker, the
    /// engine or a free port), and ember while it works. The status line
    /// beside it says the same in words, so the flame takes no focus.
    /// </summary>
    public sealed class StatusFlame : Control
    {
        public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
            nameof(IsOn), typeof(bool), typeof(StatusFlame), new PropertyMetadata(false));

        public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
            nameof(IsLive), typeof(bool), typeof(StatusFlame), new PropertyMetadata(false));

        public StatusFlame()
        {
            Focusable = false;
            IsTabStop = false;
        }

        /// <summary>Whether the extra is switched on.</summary>
        public bool IsOn
        {
            get => (bool)GetValue(IsOnProperty);
            set => SetValue(IsOnProperty, value);
        }

        /// <summary>Whether it works: serves, listens, or receives data.</summary>
        public bool IsLive
        {
            get => (bool)GetValue(IsLiveProperty);
            set => SetValue(IsLiveProperty, value);
        }
    }
}
