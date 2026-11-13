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

        /// <summary>Runs the body on the dialog tests' one host: an STA
        /// thread beside a plain Application that holds a stand-in for every
        /// key a dialog names from the app's dictionaries
        /// (DialogTestHost).</summary>
        internal static void RunWithApp(Action body) => DialogTestHost.Run(body);
    }
}
