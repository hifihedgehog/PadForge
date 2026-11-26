using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using HIDMaestro;
using PadForge.Resources.Strings;
using PadForge.Services;
using PadForge.ViewModels;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// Manage Profiles lists the devices connected now, which opens every
    /// HID interface on the PC (HIDMaestro's HidDeviceEnumerator), and checks
    /// an import in a HIDMaestro context of its own. Neither runs on the
    /// dispatcher: the window takes input while they run. The dialog is made
    /// and never shown.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ManageProfilesDialogThreadTests
    {
        [Fact]
        public void ReadingTheDevicesLeavesTheDispatcherFree() => DialogTestHost.Run(() =>
        {
            var dialog = new ManageProfilesDialog(new SettingsService(new MainViewModel())) { ShowInTaskbar = false };
            var release = new ManualResetEventSlim(false);
            try
            {
                dialog.ListDevicesSeam = () =>
                {
                    release.Wait(2000);
                    return Array.Empty<HMHidDeviceInfo>();
                };
                var clock = Stopwatch.StartNew();
                var reading = dialog.PopulateDevices(sayCount: true);
                long ranAt = -1;
                var frame = new DispatcherFrame();
                dialog.Dispatcher.BeginInvoke(new Action(() =>
                {
                    ranAt = clock.ElapsedMilliseconds;
                    frame.Continue = false;
                }));
                Dispatcher.PushFrame(frame);
                release.Set();
                while (!reading.IsCompleted && clock.ElapsedMilliseconds < 10000)
                {
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(5);
                }

                Assert.True(ranAt >= 0 && ranAt < 200, $"the dispatcher ran a posted item {ranAt} ms after the list began");
                Assert.True(reading.IsCompleted, "the list never finished");
                Assert.Equal(Strings.Counted(nameof(Strings.ImportFromDevice_StatusFound_Format), 0), dialog.StatusText.Text);
            }
            finally
            {
                release.Set();
                dialog.Close();
            }
        });
    }
}
