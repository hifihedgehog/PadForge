using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using PadForge.Services;
using PadForge.Views;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The shift-layer flyout keeps a layer shown while its slide out runs
    /// (#600 item 42), the flyouts' fallback work area is in the monitor's
    /// pixels, and the copies older builds left in the user's temp folder
    /// are deleted as the user.
    /// </summary>
    [Collection("WpfApplicationSingleton")]
    public class ShiftLayerFlyoutSlideTests
    {
        private static void Pump(Dispatcher dispatcher, int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>A layer engaged while the flyout slides out after its
        /// two seconds keeps the window: the end of the old slide, which
        /// still arrives, hides nothing. The window is moved off every
        /// screen as soon as it is shown.</summary>
        [Fact]
        public void ALayerShownWhileTheFlyoutSlidesOut_KeepsTheWindow()
        {
            PageRenderDumpPages.WithApp((app, dispatcher) =>
            {
                var flyout = new ShiftLayerFlyout();
                try
                {
                    flyout.ShowLayer("Base", "#FF8800", null);
                    flyout.Left = -32000;
                    flyout.Top = -32000;
                    Pump(dispatcher, 400);
                    Assert.True(flyout.IsVisible);

                    flyout.HideFlyout();
                    Pump(dispatcher, 60);
                    flyout.ShowLayer("Layer 1", "#00AAFF", null);
                    flyout.Left = -32000;
                    flyout.Top = -32000;
                    Pump(dispatcher, 400);
                    Assert.Equal(Visibility.Visible, flyout.Visibility);
                    Assert.Equal("Layer 1", flyout.LayerNameText.Text);
                    // A layer set to stay is kept alive, with no second slide in.
                    Assert.True(flyout.KeepAlive());
                }
                finally
                {
                    flyout.Close();
                }
            }, TimeSpan.FromMinutes(2), "The shift-layer flyout test timed out.");
        }
    }

    [Collection("FlyoutPlacement")]
    public class FlyoutFallbackAreaTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        private const uint SPI_GETWORKAREA = 0x0030;

        /// <summary>A monitor Windows cannot describe falls back to the
        /// primary monitor's work area in its own pixels, with the system's
        /// scale: what SetWindowPos takes.</summary>
        [Fact]
        public void TheFallbackWorkArea_IsInThePrimarysPixels_AtTheSystemScale()
        {
            try
            {
                FlyoutPlacement.MonitorUnreadSeam = true;
                FlyoutPlacement.SystemDpiSeam = () => 144;
                var work = new RECT();
                Assert.True(SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0));
                var area = FlyoutPlacement.Of(IntPtr.Zero);
                Assert.Equal(new FlyoutPlacement.WorkArea(work.Left, work.Top, work.Right, work.Bottom, 1.5), area);
            }
            finally
            {
                FlyoutPlacement.MonitorUnreadSeam = false;
                FlyoutPlacement.SystemDpiSeam = null;
            }
        }
    }

    public class TempLeftoverTests
    {
        /// <summary>The voice model and the DSX stand-in older builds left in
        /// the user's temp folder are deleted once, through the run as the
        /// user, with the PadForge folder they leave empty. Nothing else
        /// there is touched, and with nothing left the run is not
        /// started.</summary>
        [Fact]
        public void TheCopiesOlderBuildsLeftInTemp_AreDeletedAsTheUser()
        {
            string root = Path.Combine(Path.GetTempPath(), "PadForge.Tests.Leftovers." + Guid.NewGuid().ToString("N"));
            try
            {
                string model = Path.Combine(root, "PadForge", "voice-models", "vosk-model-small-en-us-0.15", "am");
                Directory.CreateDirectory(model);
                File.WriteAllText(Path.Combine(model, "final.mdl"), "model");
                string dsx = Path.Combine(root, "PadForge_DSX");
                Directory.CreateDirectory(dsx);
                File.WriteAllText(Path.Combine(dsx, "DSX.exe"), "stand-in");
                string other = Path.Combine(root, "Another Program");
                Directory.CreateDirectory(other);

                int runs = 0;
                bool AsUser(Action work)
                {
                    runs++;
                    work();
                    return true;
                }
                Assert.Equal(2, TempLeftovers.Retire(root, AsUser));
                Assert.Equal(1, runs);
                Assert.False(Directory.Exists(Path.Combine(root, "PadForge")));
                Assert.False(Directory.Exists(dsx));
                Assert.True(Directory.Exists(other));

                // Nothing left: no run.
                Assert.Equal(0, TempLeftovers.Retire(root, AsUser));
                Assert.Equal(1, runs);

                // A PadForge folder that holds more than the model stays,
                // and a run that could not be made deletes nothing.
                Directory.CreateDirectory(model);
                Directory.CreateDirectory(Path.Combine(root, "PadForge", "kept"));
                Assert.Equal(0, TempLeftovers.Retire(root, _ => false));
                Assert.True(Directory.Exists(model));
                Assert.Equal(1, TempLeftovers.Retire(root, AsUser));
                Assert.True(Directory.Exists(Path.Combine(root, "PadForge", "kept")));
                Assert.False(Directory.Exists(Path.Combine(root, "PadForge", "voice-models")));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>With administrator rights, as PadForge runs, the run as
        /// the user carries the desktop shell's token, which has none.</summary>
        [ElevatedFact]
        public void TheRunAsTheUser_HasNoAdministratorRights()
        {
            // A session with no desktop shell, a remote shell's, has no user
            // to run as: nothing runs.
            using (var shell = PadForge.Services.Plugins.PluginLauncher.OpenShell())
            {
                if (shell == null)
                {
                    bool ran = false;
                    Assert.False(PadForge.Common.ShellUser.AsTheUser(() => ran = true));
                    Assert.False(ran);
                    return;
                }
            }
            bool? administrator = null;
            Assert.True(PadForge.Common.ShellUser.AsTheUser(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }));
            Assert.False(administrator);
            // Back on the process's own token after it.
            Assert.True(ElevatedProofTests.TestHostIsElevated());
        }
    }
}
