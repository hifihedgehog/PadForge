using System.Windows;

namespace PadForge.Controls
{
    /// <summary>Whether what an element shows belongs to something that is
    /// live (#574, the world-class pass). A card or a row of tiles sets it,
    /// and everything inside reads it, so a shared part can tell a forging
    /// controller's card from a cold one without knowing whose card it is
    /// in. Ember is the color of what is live: on a card that is not, a
    /// stage icon with something set is bright steel instead.
    ///
    /// <para>It is true unless a parent says otherwise, so a part outside
    /// any card looks as it always did.</para></summary>
    public static class Heat
    {
        public static readonly DependencyProperty IsLiveProperty = DependencyProperty.RegisterAttached(
            "IsLive", typeof(bool), typeof(Heat),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetIsLive(DependencyObject element) => (bool)element.GetValue(IsLiveProperty);

        public static void SetIsLive(DependencyObject element, bool value) => element.SetValue(IsLiveProperty, value);
    }
}
