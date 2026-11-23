using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PadForge.Common.Themes
{
    /// <summary>
    /// The look's colors for the places a by-name reference does not
    /// follow a change (#591). Markup reads nearly every color as a
    /// DynamicResource, which follows a theme by itself. Two places do
    /// not: a brush kept in a style's own resources takes a
    /// DynamicResource color once and keeps it, and code that draws with
    /// a color it looked up has no reference at all. The first binds to
    /// this object's indexer, which reports a change. The second reads
    /// <see cref="Of"/> and redraws on <see cref="Changed"/>.
    /// </summary>
    public sealed class ThemeTokens : INotifyPropertyChanged
    {
        public static ThemeTokens Instance { get; } = new ThemeTokens();

        private ThemeTokens() { }

        /// <summary>The color of the brush or color resource of that
        /// name, as the look in force draws it.</summary>
        public Color this[string key] => Of(key, Colors.Transparent);

        /// <summary>The color of the brush or color resource of that
        /// name, or the fallback when there is no application or no such
        /// resource (a unit test that builds one control).</summary>
        internal static Color Of(string key, Color fallback)
        {
            object found = Application.Current?.TryFindResource(key);
            return found switch
            {
                SolidColorBrush brush => brush.Color,
                Color color => color,
                _ => fallback,
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>Raised after a look is put on, stock or themed.</summary>
        internal static event EventHandler Changed;

        /// <summary>
        /// Has an element draw again each time a look is put on while it is
        /// loaded, and once when it loads: a view that builds its parts in
        /// code, in tones it read from the look. Loaded can come again with
        /// no Unloaded between, so the handler is taken off before it goes
        /// on. A look is put on by the application's own thread. An element
        /// of another thread belongs to a window that closed without
        /// unloading it, and lets go.
        /// </summary>
        internal static void Follow(FrameworkElement element, Action redraw)
        {
            EventHandler look = null;
            look = (_, _) =>
            {
                if (!element.CheckAccess())
                {
                    Changed -= look;
                    return;
                }
                redraw();
            };
            element.Loaded += (_, _) =>
            {
                Changed -= look;
                Changed += look;
                redraw();
            };
            element.Unloaded += (_, _) => Changed -= look;
            if (element.IsLoaded) Changed += look;
        }

        internal void Refresh()
        {
            ThemeLook.Invalidate();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
