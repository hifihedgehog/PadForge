using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace PadForge.Common
{
    // Ember identity (#175): the app accent is the forge orange from the
    // PadForge logo, never the Windows accent. Re-apply after every theme
    // apply, because ApplicationThemeManager.Apply and ApplySystemTheme
    // re-derive the accent from the system color by default.
    internal static class EmberTheme
    {
        public static readonly Color Accent = Color.FromRgb(0xFF, 0x6B, 0x2C);

        public static void ApplyAccent()
        {
            ApplicationAccentColorManager.Apply(Accent, ApplicationThemeManager.GetAppTheme());
        }
    }
}
