using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PadForge.Common;
using PadForge.Resources.Strings;

namespace PadForge.Views
{
    /// <summary>
    /// The one icon picker (#471) for menu cells and shift layers. The Emoji
    /// tab is the catalog the shift layer dialog has always offered, and the
    /// Images tab draws every registered icon package's entries as pictures,
    /// so a cell or a layer picks from the same choices either way. Choosing
    /// raises <see cref="IconChosen"/> with the reference to store: the emoji
    /// itself, a <c>pficon://</c> package entry, or an empty string to clear.
    /// The icon the caller holds now is marked, and the picker opens on the
    /// tab and emoji category that hold it.
    /// </summary>
    public partial class IconPicker : UserControl
    {
        /// <summary>A reference was chosen. Empty clears the icon.</summary>
        public event Action<string> IconChosen;

        /// <summary>Browse Files was clicked. The host runs the file dialog,
        /// since only it knows the window to own it.</summary>
        public event Action BrowseRequested;

        /// <summary>Escape was pressed.</summary>
        public event Action CloseRequested;

        private string _current = "";

        public IconPicker()
        {
            InitializeComponent();
            EmojiCategoryBar.ItemsSource = ShiftActivatorDialog.EmojiCatalog;
        }

        /// <summary>One package's entries on the Images tab.</summary>
        public sealed record IconGroup(string Name, List<IconChoice> Icons);

        /// <summary>One entry: the reference to store, its file name, its
        /// thumbnail, and whether it is the icon held now.</summary>
        public sealed record IconChoice(string Reference, string Name, ImageSource Image, bool IsCurrent);

        /// <summary>One emoji on the Emoji tab.</summary>
        public sealed record EmojiChoice(string Glyph, bool IsCurrent);

        /// <summary>Fills the tabs and opens on the one that holds the current
        /// icon: Images for a picture, Emoji otherwise. A package named in
        /// <paramref name="showPackage"/> opens Images scrolled to it, which
        /// is where a browsed package with several entries lands.</summary>
        public void Prepare(string currentIcon, string clearLabel, string showPackage = null)
        {
            _current = currentIcon ?? "";
            ClearButton.Content = clearLabel;
            var groups = BuildGroups(_current);
            PackageGroups.ItemsSource = groups;
            // Only an empty registry is "no packages". A package whose images
            // all fail to decode shows nothing rather than that claim.
            NoPackagesText.Visibility = IconPackageManager.Packages.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            var catalog = ShiftActivatorDialog.EmojiCatalog;
            var category = catalog.FirstOrDefault(c => Array.IndexOf(c.Emojis, _current) >= 0) ?? catalog[0];
            ShowCategory(category);

            bool images = showPackage != null || MenuIconResolver.IsImageReference(_current);
            SetTab(images);
            if (showPackage != null)
                Dispatcher.BeginInvoke(new Action(() => ScrollToPackage(showPackage)), DispatcherPriority.Loaded);
        }

        /// <summary>Puts keyboard focus on the open tab. WPF moves no focus
        /// into a popup on its own, so without this the keyboard stays in the
        /// window behind.</summary>
        public void FocusOpenTab()
        {
            var tab = ImagesPanel.Visibility == Visibility.Visible ? ImagesTabButton : EmojiTabButton;
            tab.Focus();
        }

        // ── Thumbnails ────────────────────────────────────────────────────

        /// <summary>Decode width for a thumbnail: twice the 40-pixel button,
        /// so a high-DPI screen stays sharp.</summary>
        private const int ThumbnailPixelWidth = 80;

        /// <summary>Thumbnails by reference, misses kept as null. Apart from
        /// the resolver's full-size images, which a 40-pixel button does not
        /// need, and dropped whenever the package registry changes.</summary>
        private static readonly Dictionary<string, ImageSource> Thumbnails = new(StringComparer.OrdinalIgnoreCase);

        static IconPicker()
        {
            IconPackageManager.RegistryChanged += (_, __) =>
            {
                lock (Thumbnails) Thumbnails.Clear();
            };
        }

        internal static List<IconGroup> BuildGroups(string currentIcon)
        {
            var groups = new List<IconGroup>();
            foreach (var p in IconPackageManager.Packages)
            {
                var entries = IconPackageManager.ListIcons(p.Name);
                bool missing;
                lock (Thumbnails)
                    missing = entries.Any(e => !Thumbnails.ContainsKey(IconPackageManager.MakeRef(p.Name, e)));
                // One read of the archive decodes every thumbnail not yet made.
                if (missing)
                {
                    foreach (var (entry, bytes) in IconPackageManager.ReadIcons(p.Name))
                    {
                        string reference = IconPackageManager.MakeRef(p.Name, entry);
                        lock (Thumbnails)
                            if (Thumbnails.ContainsKey(reference)) continue;
                        var thumb = DecodeThumbnail(bytes);
                        lock (Thumbnails) Thumbnails[reference] = thumb;
                    }
                }

                var icons = new List<IconChoice>();
                foreach (var entry in entries)
                {
                    string reference = IconPackageManager.MakeRef(p.Name, entry);
                    ImageSource thumb;
                    lock (Thumbnails) Thumbnails.TryGetValue(reference, out thumb);
                    // An entry that does not decode offers nothing to click.
                    if (thumb != null)
                        icons.Add(new IconChoice(reference, Path.GetFileName(entry), thumb,
                            string.Equals(reference, currentIcon, StringComparison.OrdinalIgnoreCase)));
                }
                if (icons.Count > 0) groups.Add(new IconGroup(p.Name, icons));
            }
            return groups;
        }

        private static ImageSource DecodeThumbnail(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(bytes, writable: false);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = ThumbnailPixelWidth;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException
                or ArgumentException or InvalidOperationException or FileFormatException)
            {
                return null;
            }
        }

        // ── Tabs and choices ──────────────────────────────────────────────

        private void ScrollToPackage(string package)
        {
            if (PackageGroups.ItemsSource is not List<IconGroup> groups) return;
            int index = groups.FindIndex(g => string.Equals(g.Name, package, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            if (PackageGroups.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
                container.BringIntoView();
        }

        private void ShowCategory(ShiftActivatorDialog.EmojiCategory category)
        {
            EmojiGrid.ItemsSource = category.Emojis
                .Select(e => new EmojiChoice(e, string.Equals(e, _current, StringComparison.Ordinal)))
                .ToList();
        }

        private void SetTab(bool images)
        {
            EmojiPanel.Visibility = images ? Visibility.Collapsed : Visibility.Visible;
            ImagesPanel.Visibility = images ? Visibility.Visible : Visibility.Collapsed;
            MarkTab(EmojiTabButton, !images);
            MarkTab(ImagesTabButton, images);
        }

        /// <summary>The active tile wears the segment's ember gradient and
        /// light text, as the Dashboard's type segment does.</summary>
        private static void MarkTab(Button tab, bool active)
        {
            if (active)
            {
                tab.SetResourceReference(BackgroundProperty, "EmberSegGradient");
                tab.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xF3, 0xE8));
            }
            else
            {
                tab.ClearValue(BackgroundProperty);
                tab.ClearValue(ForegroundProperty);
            }
        }

        private void EmojiTab_Click(object sender, RoutedEventArgs e) => SetTab(false);

        private void ImagesTab_Click(object sender, RoutedEventArgs e) => SetTab(true);

        private void EmojiCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is ShiftActivatorDialog.EmojiCategory cat)
                ShowCategory(cat);
        }

        private void Emoji_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string s && !string.IsNullOrEmpty(s))
                IconChosen?.Invoke(s);
        }

        private void Image_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string s && !string.IsNullOrEmpty(s))
                IconChosen?.Invoke(s);
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => IconChosen?.Invoke("");

        private void Browse_Click(object sender, RoutedEventArgs e) => BrowseRequested?.Invoke();

        private void Picker_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CloseRequested?.Invoke();
        }
    }

    /// <summary>
    /// Opens the <see cref="IconPicker"/> in a popup under a button and hands
    /// the chosen reference back. Menu cells and the shift layer dialog share
    /// it, so the picker, the browse and a browsed package's follow-up behave
    /// the same in both places.
    /// </summary>
    internal static class IconPickerHost
    {
        public static void Open(UIElement anchor, string currentIcon, string clearLabel,
            Action<string> apply, string showPackage = null)
        {
            if (anchor == null || apply == null) return;
            var picker = new IconPicker();
            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                StaysOpen = false,
                Child = picker,
            };
            // A pick or Escape hands the keyboard back to the button that
            // opened the picker. A click elsewhere leaves focus where it
            // landed.
            void CloseToAnchor()
            {
                popup.IsOpen = false;
                if (anchor.Focusable) anchor.Focus();
            }
            picker.Prepare(currentIcon, clearLabel, showPackage);
            picker.IconChosen += reference =>
            {
                CloseToAnchor();
                apply(reference);
            };
            picker.CloseRequested += CloseToAnchor;
            picker.BrowseRequested += () =>
            {
                popup.IsOpen = false;
                string reference = BrowseFromDisk(Window.GetWindow(anchor), out string package);
                if (reference != null) apply(reference);
                // A package with several entries opens again on its pictures,
                // where the choice is made by eye.
                else if (package != null) Open(anchor, currentIcon, clearLabel, apply, package);
            };
            popup.IsOpen = true;
            picker.Dispatcher.BeginInvoke(new Action(picker.FocusOpenTab), DispatcherPriority.Input);
        }

        /// <summary>Filesystem browse for a loose image or a <c>.pficons</c>
        /// package. A package is registered, and one with a single entry is
        /// chosen at once. One with several returns null and names the
        /// package, which the picker then shows. A loose image stores
        /// exe-relative when it sits under the app directory (the portable-kit
        /// rule).</summary>
        internal static string BrowseFromDisk(Window owner, out string packageToShow)
        {
            packageToShow = null;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Strings.Instance.Menu_Icon_PickTitle,
                // Only what the resolver draws: an unsupported file stored as
                // an icon would draw nothing anywhere.
                Filter = "Images and icon packages|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.pficons"
                       + "|Icon packages (*.pficons)|*.pficons",
                CheckFileExists = true,
            };
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return null;

            if (dlg.FileName.EndsWith(IconPackageManager.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                string pkg = IconPackageManager.Register(dlg.FileName);
                if (pkg == null) return null;
                var icons = IconPackageManager.ListIcons(pkg);
                if (icons.Count == 1) return IconPackageManager.MakeRef(pkg, icons[0]);
                if (icons.Count > 1) packageToShow = pkg;
                return null;
            }

            return StoredImagePath(dlg.FileName);
        }

        /// <summary>The stored form of a loose image. A file directly in
        /// PadForge's folder would store as a bare name, which reads as a
        /// Steam icon name, so it keeps a leading <c>.\</c> and stays a
        /// path.</summary>
        internal static string StoredImagePath(string filePath)
        {
            string stored = IconPackageManager.MakeStoredPath(filePath);
            if (stored.IndexOfAny(new[] { '\\', '/', ':' }) < 0)
                stored = "." + Path.DirectorySeparatorChar + stored;
            return stored;
        }
    }
}
