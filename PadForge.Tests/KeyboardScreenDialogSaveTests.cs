using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The keyboard screens' dialog (#519, #560) saves what its boxes hold when
    /// Save runs with a box still focused, as Enter on the default button does.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class KeyboardScreenDialogSaveTests
    {
        private static string PictureFile(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(Path.GetTempPath(), "PadForge.Tests.KeyboardScreenDialog." + Guid.NewGuid().ToString("N") + ".png");
            using (var file = File.Create(path)) encoder.Save(file);
            return path;
        }

        private static KeyboardScreenDialog Open(KeyboardScreenData current)
        {
            var ctor = typeof(KeyboardScreenDialog).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];
            var dialog = (KeyboardScreenDialog)ctor.Invoke(new object[]
            {
                new KeyboardScreenService(new SettingsViewModel(), null), KeyboardScreenInfo.Overlay(1), current,
                "A Game", null, null, (Size?)new Size(100, 100),
            });
            dialog.ShowInTaskbar = false;
            new WindowInteropHelper(dialog).EnsureHandle();
            return dialog;
        }

        private static void Save(KeyboardScreenDialog dialog)
        {
            try
            {
                typeof(KeyboardScreenDialog).GetMethod("SaveButton_Click", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(dialog, new object[] { dialog.FindName("SaveButton"), new RoutedEventArgs() });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                // DialogResult is set only on a window shown with ShowDialog.
            }
        }

        private static int Field(KeyboardScreenDialog dialog, string name)
            => (int)typeof(KeyboardScreenDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);

        [Fact]
        public void Save_KeepsWhatEachBoxHolds() => CustomTelemetryFormatsDialogTests.RunWithApp(() =>
        {
            string path = PictureFile(400, 400);
            try
            {
                var current = new KeyboardScreenData { Mode = KeyboardScreenMode.Picture, Picture = path, Zoom = 400 };

                // A pan typed into its box, the box still focused.
                var dialog = Open(current);
                try
                {
                    ((TextBox)dialog.FindName("PanXBox")).Text = "30";
                    ((TextBox)dialog.FindName("PanYBox")).Text = "40";
                    Save(dialog);
                    Assert.Equal(30, Field(dialog, "_panX"));
                    Assert.Equal(40, Field(dialog, "_panY"));
                    Assert.Equal(400, Field(dialog, "_zoom"));
                }
                finally { dialog.Close(); }

                // A zoom typed into its box, and a pan beside it.
                dialog = Open(current);
                try
                {
                    ((TextBox)dialog.FindName("ZoomBox")).Text = "800";
                    ((TextBox)dialog.FindName("PanXBox")).Text = "20";
                    Save(dialog);
                    Assert.Equal(800, Field(dialog, "_zoom"));
                    Assert.Equal(20, Field(dialog, "_panX"));
                }
                finally { dialog.Close(); }
            }
            finally { File.Delete(path); }
        });
    }
}
