using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Common.Input.Telemetry;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The custom telemetry formats dialog (#531) run whole. Its styles are
    /// application static resources, so it runs under a plain Application
    /// that holds stand-ins for them, alone, the pairing dialog's rule
    /// (PairDeviceDialogSerialTests).
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class CustomTelemetryFormatsDialogTests
    {
        /// <summary>A NumberBox commits what was typed only when it loses
        /// focus (wpfui NumberBox.OnLostFocus) or on Enter's key up, and the
        /// default button and a screen reader's Invoke click Save with the
        /// box still focused. Save commits it first.</summary>
        [Fact]
        public void Save_CommitsTheNumberBoxThatStillHasFocus() => RunWithApp(() =>
        {
            var format = new CustomTelemetryFormat { Name = "Rig", Length = 40 };
            format.Fields.Add(new CustomTelemetryField { Target = CustomTelemetryTarget.Rpm, Offset = 4, Type = CustomTelemetryType.U16 });
            var dialog = new CustomTelemetryFormatsDialog(new[] { format }) { ShowInTaskbar = false };
            try
            {
                new WindowInteropHelper(dialog).EnsureHandle();
                var root = (FrameworkElement)dialog.Content;
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                var length = LogicalDescendants<Wpf.Ui.Controls.NumberBox>(dialog).Single(box =>
                    BindingOperations.GetBinding(box, Wpf.Ui.Controls.NumberBox.ValueProperty)?.Path.Path == "Length");
                FocusManager.SetFocusedElement(dialog, length);
                Assert.True(length.IsFocused);
                length.Text = "48";

                try
                {
                    typeof(CustomTelemetryFormatsDialog).GetMethod("SaveButton_Click", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(dialog, new[] { dialog.FindName("SaveButton"), new RoutedEventArgs() });
                }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
                {
                    // DialogResult is set only on a window shown with ShowDialog.
                }
                Assert.NotNull(dialog.Result);
                Assert.Equal(48, dialog.Result[0].Length);
            }
            finally
            {
                dialog.Close();
            }
        });

        private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is not DependencyObject node) continue;
                if (node is T match) yield return match;
                foreach (var deeper in LogicalDescendants<T>(node)) yield return deeper;
            }
        }

        /// <summary>Builds a plain Application holding stand-ins for the
        /// dialog's static resource keys, runs the body on its STA thread,
        /// and puts the process back with no Application, as
        /// PairDeviceDialogSerialTests.RunWithApp does.</summary>
        internal static void RunWithApp(Action body)
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
                    app.Resources["TelemetryFontFamily"] = new FontFamily("Segoe UI");
                    app.Resources["BoolToVisibilityConverter"] = new PadForge.Converters.BoolToVisibilityConverter();
                    app.Resources["CardTitle"] = new Style(typeof(TextBlock));
                    app.Resources["CardDescription"] = new Style(typeof(TextBlock));
                    app.Resources["EmberAccentButton"] = new Style(typeof(Button));
                    app.Resources["EmberDestructiveButton"] = new Style(typeof(Button));
                    app.Resources["EmberIconButton"] = new Style(typeof(Wpf.Ui.Controls.Button));
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
        }

        /// <summary>WPF keeps the Application in statics. Left set, the
        /// shutting-down flag stops every later Window from loading.</summary>
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
    }
}
