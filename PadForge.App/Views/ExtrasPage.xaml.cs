using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PadForge.Controls;

namespace PadForge.Views
{
    /// <summary>
    /// The Extras page (#574): a catalog of the optional features and a page
    /// for each. The catalog and the ten pages are panels of one scroll view,
    /// and this class shows one of them at a time.
    ///
    /// <para>An extra is named by its key (<see cref="Keys"/>). The key names
    /// its tile (key + "Tile"), its page (key + "Page") and, where the page
    /// has one, its More row (key + "More").</para>
    /// </summary>
    public partial class ExtrasPage : UserControl
    {
        /// <summary>The extras in catalog order: More Ways to Play, Send It
        /// Elsewhere, On Screen.</summary>
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            "WebController", "HeadTracking", "GameTelemetry", "TwitchChat",
            "RemoteLink", "MotionServer", "GameMods",
            "Overlays", "TouchpadOverlay", "ScreenOverlay",
        };

        /// <summary>Back to the catalog from an extra's page: Alt+Left and
        /// the keyboard's Back key, as in a browser and in Windows Settings.
        /// A command of its own and not NavigationCommands.BrowseBack, whose
        /// gestures include Backspace: pressed in one of the pages'
        /// read-only address boxes, which do not take it, Backspace would
        /// leave the page.</summary>
        private static readonly RoutedCommand BackCommand = new RoutedCommand("ExtrasBack", typeof(ExtrasPage),
            new InputGestureCollection { new KeyGesture(Key.Left, ModifierKeys.Alt), new KeyGesture(Key.BrowserBack) });

        public ExtrasPage()
        {
            InitializeComponent();
            CommandBindings.Add(new CommandBinding(BackCommand,
                (_, e) => { ShowCatalog(); e.Handled = true; },
                (_, e) => { e.CanExecute = CurrentExtra != null; e.Handled = true; }));
        }

        /// <summary>The key of the extra whose page is showing, or null while
        /// the catalog is.</summary>
        public string CurrentExtra { get; private set; }

        /// <summary>Shows the catalog. Coming back from an extra's page, the
        /// keyboard focus goes to that extra's tile, so the next Tab goes on
        /// from where the user left.</summary>
        public void ShowCatalog()
        {
            string from = CurrentExtra;
            Show(null);
            if (from != null && FindName(from + "Tile") is ExtraTile tile)
                FocusWhenShown(() => tile.FocusOpen());
        }

        /// <summary>Shows one extra's page, or the catalog for null. An
        /// unknown key changes nothing and returns false.</summary>
        public bool Show(string key)
        {
            if (key != null && !(FindName(key + "Page") is FrameworkElement)) return false;
            CatalogView.Visibility = key == null ? Visibility.Visible : Visibility.Collapsed;
            foreach (string k in Keys)
                if (FindName(k + "Page") is FrameworkElement page)
                    page.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
            CurrentExtra = key;
            PageScroll.ScrollToTop();
            return true;
        }

        /// <summary>Opens an extra's page with its More row open, for a link
        /// or a search result that points at a setting kept under More.</summary>
        public bool ShowMore(string key)
        {
            if (!Show(key)) return false;
            if (FindName(key + "More") is MoreRow more) more.IsExpanded = true;
            return true;
        }

        private void Tile_OpenRequested(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not ExtraTile tile || !Show(tile.Key)) return;
            e.Handled = true;
            // The tile that had the focus is gone from the screen. The way
            // back is the page's first control, so the focus goes there.
            if (FindName(tile.Key + "Page") is FrameworkElement page)
                FocusWhenShown(() => page.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowCatalog();

        /// <summary>The mouse's Back button goes back to the catalog.</summary>
        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (e.ChangedButton != MouseButton.XButton1 || CurrentExtra == null) return;
            ShowCatalog();
            e.Handled = true;
        }

        /// <summary>Runs after the panel just shown has been laid out, since
        /// a collapsed panel's controls cannot take the focus.</summary>
        private void FocusWhenShown(Func<bool> focus)
            => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => focus()));

        /// <summary>Opens the custom telemetry formats (#531) and keeps the
        /// list the user saved.</summary>
        private void GameTelemetryFormatsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.DashboardViewModel vm) return;
            var dlg = new CustomTelemetryFormatsDialog(vm.GameTelemetryFormats) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.Result != null)
                vm.GameTelemetryFormats = dlg.Result;
        }
    }
}
