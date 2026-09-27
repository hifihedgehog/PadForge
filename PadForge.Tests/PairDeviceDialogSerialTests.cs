using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Resources.Strings;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>Runs alone. WPF allows one Application per process, and
    /// production code reads Application.Current to decide how to marshal,
    /// so nothing else may run while one exists.</summary>
    [CollectionDefinition("WpfApplicationSingleton", DisableParallelization = true)]
    public class WpfApplicationSingletonCollection { }

    /// <summary>
    /// The pairing dialog's Serial Controller family (hifihedgehog/SDL#33).
    /// The dialog's styles are application static resources, and a missing
    /// key would throw when a user opens it to pair a Wii Remote. The first
    /// test holds every key the dialog names to App.xaml. The second runs the
    /// dialog under a plain Application that holds stand-ins for those keys,
    /// since App.xaml names its merged dictionary by a path WPF resolves
    /// against the entry assembly, which in a test host is not PadForge.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class PairDeviceDialogSerialTests
    {
        private static string Repo(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "PadForge.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray()));
        }

        private static readonly string[] DialogKeys =
            { "BodyFontFamily", "DisplayFontFamily", "EmberIconButton", "EmberPrimaryButton", "EmberSelectListItem" };

        [Fact]
        public void EveryStaticResourceTheDialogNames_IsDefinedInApp()
        {
            string dialog = Repo("PadForge.App", "Views", "PairDeviceDialog.xaml");
            string app = Repo("PadForge.App", "App.xaml");
            var named = System.Text.RegularExpressions.Regex.Matches(dialog, @"\{StaticResource\s+([A-Za-z0-9_]+)\}")
                .Select(m => m.Groups[1].Value).Distinct().OrderBy(k => k).ToArray();
            Assert.Equal(DialogKeys.OrderBy(k => k).ToArray(), named);
            foreach (string key in named)
                Assert.Contains($"x:Key=\"{key}\"", app);
        }

        [Fact]
        public void TheSerialFamily_AddsAndRemovesAController_AndHandsBackThePairButton()
        {
            RunWithApp(() =>
            {
                var settings = new SettingsViewModel();
                int changes = 0;
                settings.SerialControllersChanged += (_, _) => changes++;
                var dialog = new PairDeviceDialog(settings) { ShowInTaskbar = false };
                try
                {
                    var family = (ComboBox)dialog.FindName("FamilyCombo");
                    var pickers = (FrameworkElement)dialog.FindName("SerialPickers");
                    var added = (FrameworkElement)dialog.FindName("SerialAddedPanel");
                    var ports = (ComboBox)dialog.FindName("PortCombo");
                    var controller = (ComboBox)dialog.FindName("ControllerCombo");
                    var pair = (ButtonBase)dialog.FindName("PairButton");
                    var temporary = (FrameworkElement)dialog.FindName("TemporaryCheck");

                    Assert.Equal(5, family.Items.Count);
                    Assert.Equal(Visibility.Collapsed, pickers.Visibility);
                    Assert.Equal(SerialControllers.Protocols.Length, controller.Items.Count);

                    family.SelectedIndex = 3;
                    Assert.Equal(Visibility.Visible, pickers.Visibility);
                    Assert.Equal(Visibility.Collapsed, temporary.Visibility);
                    Assert.Equal(Visibility.Collapsed, added.Visibility);
                    Assert.Equal(Strings.Instance.Common_Add, pair.Content);
                    Assert.Equal(ports.Items.Count > 0, pair.IsEnabled);

                    // A port as the list gives one, then Add.
                    var port = new ComPort("COM250", "USB Serial Port (COM250)", @"FTDIBUS\VID_0403+PID_6001+TEST\0000");
                    ports.ItemsSource = new List<ComPort> { port };
                    ports.SelectedIndex = 0;
                    controller.SelectedIndex = Array.FindIndex(SerialControllers.Protocols, p => p.Token == "stinger");
                    pair.IsEnabled = true;
                    pair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    var entry = Assert.Single(settings.SerialControllers);
                    Assert.Equal(port.InstanceId, entry.Port);
                    Assert.Equal("COM250", entry.PortName);
                    Assert.Equal("stinger", entry.Protocol);
                    Assert.Equal(1, changes);
                    Assert.Equal(Visibility.Visible, added.Visibility);

                    // The same port again replaces its controller.
                    controller.SelectedIndex = Array.FindIndex(SerialControllers.Protocols, p => p.Token == "magellan");
                    pair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal("magellan", Assert.Single(settings.SerialControllers).Protocol);
                    Assert.Equal(2, changes);

                    // Lay the list out, which builds each row from its
                    // template, then remove through the row's button.
                    var root = (FrameworkElement)dialog.Content;
                    root.Measure(new Size(460, 900));
                    root.Arrange(new Rect(0, 0, 460, 900));
                    var remove = Descendants(root).OfType<ButtonBase>()
                        .Single(b => b.Tag is SerialControllerEntry);
                    remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(settings.SerialControllers);
                    Assert.Equal(3, changes);
                    Assert.Equal(Visibility.Collapsed, added.Visibility);

                    // Back to the Wii: the pickers go and Pair comes back
                    // enabled, whatever the serial family left.
                    pair.IsEnabled = false;
                    family.SelectedIndex = 0;
                    Assert.Equal(Visibility.Collapsed, pickers.Visibility);
                    Assert.Equal(Visibility.Visible, temporary.Visibility);
                    Assert.True(pair.IsEnabled);
                    Assert.Equal(Strings.Instance.WiiPair_Pair, pair.Content);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        /// <summary>The DJI network family (hifihedgehog/SDL#33 Part 6): an
        /// address in the fork's form is added in its key form, an address
        /// the fork would refuse is not added, and the row's button removes
        /// it again.</summary>
        [Fact]
        public void TheDjiFamily_AddsAndRemovesARemoteByAddress()
        {
            RunWithApp(() =>
            {
                var settings = new SettingsViewModel();
                int changes = 0;
                settings.DjiRemoteHostsChanged += (_, _) => changes++;
                var dialog = new PairDeviceDialog(settings) { ShowInTaskbar = false };
                try
                {
                    var family = (ComboBox)dialog.FindName("FamilyCombo");
                    var pickers = (FrameworkElement)dialog.FindName("DjiPickers");
                    var serial = (FrameworkElement)dialog.FindName("SerialPickers");
                    var added = (FrameworkElement)dialog.FindName("DjiAddedPanel");
                    var address = (TextBox)dialog.FindName("DjiAddressBox");
                    var pair = (ButtonBase)dialog.FindName("PairButton");
                    var status = (TextBlock)dialog.FindName("StatusText");
                    var temporary = (FrameworkElement)dialog.FindName("TemporaryCheck");

                    Assert.Equal(Visibility.Collapsed, pickers.Visibility);
                    family.SelectedIndex = 4;
                    Assert.Equal(Visibility.Visible, pickers.Visibility);
                    Assert.Equal(Visibility.Collapsed, serial.Visibility);
                    Assert.Equal(Visibility.Collapsed, temporary.Visibility);
                    Assert.Equal(Visibility.Collapsed, added.Visibility);
                    Assert.Equal(Strings.Instance.Common_Add, pair.Content);
                    Assert.True(pair.IsEnabled);

                    address.Text = "192.168.01.20";
                    pair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(settings.DjiRemoteHosts);
                    Assert.Equal(0, changes);
                    Assert.Equal(Strings.Instance.DjiPair_Invalid, status.Text);

                    address.Text = " 192.168.7.251 ";
                    pair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Equal("192.168.7.251:40007", Assert.Single(settings.DjiRemoteHosts));
                    Assert.Equal(1, changes);
                    Assert.Equal(string.Empty, address.Text);
                    Assert.Equal(Visibility.Visible, added.Visibility);

                    // The same remote with its port named is the same entry.
                    address.Text = "192.168.7.251:40007";
                    pair.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Single(settings.DjiRemoteHosts);
                    Assert.Equal(1, changes);

                    var root = (FrameworkElement)dialog.Content;
                    root.Measure(new Size(460, 900));
                    root.Arrange(new Rect(0, 0, 460, 900));
                    var remove = Descendants(root).OfType<ButtonBase>()
                        .Single(b => b.Tag is string key && key == "192.168.7.251:40007");
                    remove.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.Empty(settings.DjiRemoteHosts);
                    Assert.Equal(2, changes);
                    Assert.Equal(Visibility.Collapsed, added.Visibility);

                    family.SelectedIndex = 0;
                    Assert.Equal(Visibility.Collapsed, pickers.Visibility);
                    Assert.Equal(Strings.Instance.WiiPair_Pair, pair.Content);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        /// <summary>Builds a plain Application holding stand-ins for the
        /// dialog's static resource keys, runs the body on its STA thread,
        /// and then puts the process back with no Application: WPF never
        /// clears its singleton on its own, and every other test runs without
        /// one.</summary>
        private static void RunWithApp(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try
                {
                    var app = new Application();
                    app.Resources["BodyFontFamily"] = new FontFamily("Segoe UI");
                    app.Resources["DisplayFontFamily"] = new FontFamily("Segoe UI");
                    app.Resources["EmberIconButton"] = new Style(typeof(Wpf.Ui.Controls.Button));
                    app.Resources["EmberPrimaryButton"] = new Style(typeof(Wpf.Ui.Controls.Button));
                    app.Resources["EmberSelectListItem"] = new Style(typeof(ListBoxItem));
                    body();
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { ReleaseApplication(); }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(30000), "The STA dialog test timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Null(Application.Current);
            AssertAWindowStillLoads();
        }

        /// <summary>WPF keeps the Application in statics: the instance, the
        /// once-per-process guard, and the shutting-down flag its dispatcher's
        /// shutdown raises. Left set, that flag stops every later Window from
        /// loading.</summary>
        private static void ReleaseApplication()
        {
            var type = typeof(Application);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var instance = type.GetField("_appInstance", flags);
            var created = type.GetField("_appCreatedInThisAppDomain", flags);
            var shutting = type.GetField("_isShuttingDown", flags);
            if (instance == null || created == null || shutting == null)
                throw new InvalidOperationException("WPF's Application statics moved. The test cannot put the process back.");
            instance.SetValue(null, null);
            created.SetValue(null, false);
            shutting.SetValue(null, false);
        }

        /// <summary>The positive control for the reset: a window on a fresh
        /// STA thread shows and loads, as the other WPF tests need.</summary>
        private static void AssertAWindowStillLoads()
        {
            Exception failure = null;
            bool loaded = false;
            var thread = new Thread(() =>
            {
                var window = new Window { Width = 200, Height = 100, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
                try
                {
                    window.Show();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    loaded = window.IsLoaded;
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    window.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(15000), "The window check timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(loaded, "A window no longer loads after the Application was released.");
        }
    }
}
