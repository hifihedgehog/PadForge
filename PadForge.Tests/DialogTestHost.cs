using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// One host for a test that makes a dialog on the shared frame (#574).
    /// The frame's dictionary, Resources/Dialogs.xaml, builds on keys that
    /// live in App.xaml, which a test host cannot load. So the body runs on
    /// an STA thread beside a plain Application that holds a stand-in for
    /// each such key, and the process is put back with no Application after,
    /// as PairDeviceDialogSerialTests.RunWithApp does. A test that uses it
    /// sits in the WpfApplicationSingleton collection.
    /// </summary>
    internal static class DialogTestHost
    {
        internal static void Run(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try
                {
                    // The Application shuts down only when asked. Left to
                    // shut down when its last window closes, it does so
                    // under a test that closes one dialog and makes another,
                    // and takes WPF's resource package with it for the rest
                    // of the run (DialogFrameTests.TheHostOutlivesADialogItClosed).
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    StandIns(app.Resources);
                    body();
                }
                catch (Exception error) { failure = error; }
                finally
                {
                    try { PairDeviceDialogSerialTests.ReleaseApplication(); }
                    catch (Exception error) { failure ??= error; }
                    dispatcher.InvokeShutdown();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(30000), "The STA dialog test timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Null(Application.Current);
        }

        /// <summary>A stand-in for every key the dialogs' XAML names by
        /// StaticResource outside Dialogs.xaml and a dialog's own resources.</summary>
        internal static void StandIns(ResourceDictionary resources)
        {
            foreach (string key in new[] { "BodyFontFamily", "DisplayFontFamily", "TelemetryFontFamily" })
                resources[key] = new FontFamily("Segoe UI");
            foreach (string key in new[] { "QuietResetButton", "EmberIconButton", "EmberPrimaryButton", "RecordButton" })
                resources[key] = new Style(typeof(Wpf.Ui.Controls.Button));
            foreach (string key in new[] { "EmberAccentButton", "EmberDestructiveButton", "QuietPlainButton" })
                resources[key] = new Style(typeof(Button));
            resources["SegmentRadio"] = new Style(typeof(RadioButton));
            foreach (string key in new[] { "SegmentTrack", "PillChip", "InputChip" })
                resources[key] = new Style(typeof(Border));
            resources["SwitchCheckBox"] = new Style(typeof(CheckBox));
            resources["EmberSlider"] = new Style(typeof(Slider));
            resources["EmberSelectListItem"] = new Style(typeof(ListBoxItem));
            resources["PartList"] = new Style(typeof(ListBox));
            resources["PartListItem"] = new Style(typeof(ListBoxItem));
            foreach (string key in new[] { "CardTitle", "CardDescription", "EditorSubHead" })
                resources[key] = new Style(typeof(TextBlock));
            // Drawings of the app's dictionaries that a dialog names.
            foreach (string key in new[] { "FlameOuterGeometry", "SearchLineGeometry", "DownloadLineGeometry" })
                resources[key] = Geometry.Empty;
            resources["BoolToVisibilityConverter"] = new PadForge.Converters.BoolToVisibilityConverter();
            resources["RgbToBrushConverter"] = new PadForge.Converters.RgbToBrushConverter();
            foreach (var type in new[]
                     {
                         typeof(Button), typeof(ComboBox), typeof(PasswordBox),
                         typeof(Wpf.Ui.Controls.Button), typeof(Wpf.Ui.Controls.TextBox), typeof(Wpf.Ui.Controls.NumberBox),
                         typeof(Wpf.Ui.Controls.FluentWindow),
                     })
                resources[type] = new Style(type);
        }
    }
}
