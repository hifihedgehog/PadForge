using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Common.Input.Twitch;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The three dialogs of this window of work with every binding evaluated:
    /// Map Key Lights, Chat Commands and the Keep prompt. Page navigation never
    /// realizes a dialog, so these give the check the diagnostics harvest reads
    /// as BINDERR, on WPF's own trace. Each dialog gets a handle and a layout
    /// and is never shown, so nothing reaches the screen, the pointer or the
    /// focus of whoever is using the machine.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class DialogBindingTests
    {
        private sealed class BindingErrors : TraceListener
        {
            private readonly List<string> _lines = new();

            public string[] Lines
            {
                get { lock (_lines) return _lines.ToArray(); }
            }

            public void Clear()
            {
                lock (_lines) _lines.Clear();
            }

            public override void Write(string message) { }

            public override void WriteLine(string message)
            {
                lock (_lines) _lines.Add(message);
            }
        }

        /// <summary>The dialog is made inside the listening window: a binding
        /// with a source of its own is evaluated in the constructor.</summary>
        private static void AssertBindsCleanly(Func<Window> make)
        {
            var errors = new BindingErrors();
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            var level = source.Switch.Level;
            source.Listeners.Add(errors);
            source.Switch.Level = SourceLevels.Error;
            Window dialog = null;
            try
            {
                // The positive control: a binding that names nothing is seen,
                // in the same window as the silence the dialog is held to.
                var control = new TextBlock { DataContext = new object() };
                control.SetBinding(TextBlock.TextProperty, "NoSuchProperty");
                Assert.NotEmpty(errors.Lines);
                errors.Clear();

                dialog = make();
                dialog.ShowInTaskbar = false;
                new WindowInteropHelper(dialog).EnsureHandle();
                var root = (FrameworkElement)dialog.Content;
                root.Measure(new Size(900, 700));
                root.Arrange(new Rect(0, 0, 900, 700));
                root.UpdateLayout();
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Empty(errors.Lines);
            }
            finally
            {
                source.Listeners.Remove(errors);
                source.Switch.Level = level;
                dialog?.Close();
            }
        }

        [Fact]
        public void MapKeyLights_BindsCleanly() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var path = new OutputPath(OutputFamily.HidLightingUnit, "dialog-binding-test");
            PeripheralLightMaps.ResetForTest();
            try
            {
                PeripheralLightMaps.Publish(path, new LightMap(new[]
                {
                    new LightInfo(0, LightKind.Key, 0x29, LightKeySource.Device),
                    new LightInfo(1, LightKind.Key, 0x04, LightKeySource.Device),
                }, "dialog-binding-test"));
                AssertBindsCleanly(() => new MapKeyLightsDialog(path, "Keyboard"));
            }
            finally
            {
                PeripheralLightMaps.ResetForTest();
            }
        });

        [Fact]
        public void ChatCommands_BindsCleanly_WithARowInItsList() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var before = ChatCommandRegistry.SaveRegistry();
            try
            {
                Assert.True(ChatCommandRegistry.Add("dialogbindingtest") > 0);
                AssertBindsCleanly(() =>
                {
                    var dialog = new ChatCommandsDialog();
                    Assert.NotEmpty(((ListBox)dialog.FindName("WordListBox")).Items);
                    return dialog;
                });
            }
            finally
            {
                ChatCommandRegistry.LoadRegistry(before);
            }
        });

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void TheKeepPrompt_BindsCleanly(bool mouse, bool touchpad) => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            var make = typeof(KeepDriverDialog).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(string), typeof(int), typeof(bool), typeof(bool) }, null);
            AssertBindsCleanly(() => (Window)make.Invoke(new object[] { "Keyboard", 15, mouse, touchpad }));
        });
    }
}
