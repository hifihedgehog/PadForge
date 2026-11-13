using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PadForge.Engine.RemoteLink;
using PadForge.Views;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace PadForge.Tests
{
    /// <summary>The window of the devices shared with a paired PC. It is
    /// on the dialogs' frame (#574), so it is made beside the dialogs' test
    /// host, in the collection that keeps that host to itself.</summary>
    [Collection("WpfApplicationSingleton")]
    public class RemoteAssignmentsDialogChromeTests
    {
        [Fact]
        public void MicaDialogInitializesAnActualHiddenHwnd()
        {
            RunSta(() =>
            {
                var dialog = new RemoteAssignmentsDialog("Gaming PC", null,
                    () => Array.Empty<RemotePeerDeviceInfo>()) { ShowInTaskbar = false };
                FluentWindow invalid = null;
                try
                {
                    // EnsureHandle runs FluentWindow.OnSourceInitialized without showing a window.
                    IntPtr hwnd = new WindowInteropHelper(dialog).EnsureHandle();
                    Assert.NotEqual(IntPtr.Zero, hwnd);
                    Assert.True(IsWindow(hwnd));
                    Assert.False(IsWindowVisible(hwnd));
                    Assert.True(dialog.ExtendsContentIntoTitleBar);
                    Assert.Equal(WindowBackdropType.Mica, dialog.WindowBackdropType);

                    // High Contrast disables backdrops in the library. The property assertion above
                    // still checks the dialog's configuration when this negative control is inapplicable.
                    if (ApplicationThemeManager.GetAppTheme() != ApplicationTheme.HighContrast)
                    {
                        invalid = new FluentWindow
                        {
                            ShowInTaskbar = false,
                            WindowBackdropType = WindowBackdropType.Mica
                        };
                        var error = Assert.Throws<InvalidOperationException>(() =>
                            new WindowInteropHelper(invalid).EnsureHandle());
                        Assert.Contains(nameof(FluentWindow.ExtendsContentIntoTitleBar), error.Message);
                    }
                }
                finally
                {
                    invalid?.Close();
                    dialog.Close();
                }
            });
        }

        [Fact]
        public void HeaderShowsThePeerAndClosesWhileAnAsynchronousQueryIsPending()
        {
            RunSta(() =>
            {
                LinkAssignmentRequest sent = null;
                var channel = new LinkAssignmentChannel("peer", () => true, () => true, _ => null,
                    bytes =>
                    {
                        if (LinkAssignmentCodec.TryDecode(bytes, out var request, out _)) sent = request;
                        return true;
                    }, null);
                var source = new RemotePeerDeviceInfo { PeerLocalDeviceId = "pad", Name = "Browser Gamepad 1" };
                var owner = new Window { ShowInTaskbar = false };
                var dialog = new RemoteAssignmentsDialog("Gaming PC", channel, () => new[] { source })
                {
                    ShowInTaskbar = false
                };
                bool closed = false;
                dialog.Closed += (_, _) => closed = true;
                try
                {
                    IntPtr ownerHwnd = new WindowInteropHelper(owner).EnsureHandle();
                    dialog.Owner = owner;
                    IntPtr hwnd = new WindowInteropHelper(dialog).EnsureHandle();
                    Assert.True(IsWindow(hwnd));
                    Assert.False(IsWindowVisible(hwnd));

                    // The frame's head shows the title, which names the
                    // other PC, and so does the heading over its controllers.
                    dialog.ApplyTemplate();
                    var root = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                    root.Measure(new Size(560, 600));
                    root.Arrange(new Rect(0, 0, 560, 600));
                    root.UpdateLayout();
                    var title = dialog.TitlePart;
                    Assert.NotNull(title);
                    var close = Assert.IsAssignableFrom<ButtonBase>(dialog.FindName("CloseButton"));
                    Assert.Same(dialog, Window.GetWindow(title));
                    Assert.Equal(Visibility.Visible, title.Visibility);
                    Assert.True(dialog.HeadPart.ActualHeight > 0);
                    Assert.True(title.ActualWidth > 0);
                    Assert.Equal(dialog.Title, title.Text);
                    Assert.Contains("Gaming PC", title.Text);
                    Assert.Contains("Gaming PC", ((TextBlock)dialog.FindName("ControllersOnText")).Text);
                    dialog.Title = "Changed peer";
                    Assert.Equal("Changed peer", title.Text);
                    // Nothing has been asked yet: no controllers, no status.
                    var controllers = (FrameworkElement)dialog.FindName("ControllersBlock");
                    var status = (TextBlock)dialog.FindName("StatusText");
                    Assert.Equal(Visibility.Collapsed, controllers.Visibility);

                    var refreshButton = Assert.IsAssignableFrom<ButtonBase>(dialog.FindName("RefreshButton"));
                    var slots = Assert.IsType<ItemsControl>(dialog.FindName("SlotsList"));
                    Task refresh = Refresh(dialog, true);
                    Assert.NotNull(sent);
                    Assert.False(refresh.IsCompleted);
                    Assert.False(refreshButton.IsEnabled);
                    // While the other PC is asked, the line says so.
                    Assert.Equal(Visibility.Visible, status.Visibility);
                    Assert.Equal(PadForge.Resources.Strings.Strings.Instance.RemoteLink_AssignmentBusy, status.Text);
                    var first = sent;
                    channel.Receive(LinkAssignmentCodec.Encode(new LinkAssignmentReply(first.RequestId,
                        first.DeviceId, LinkAssignmentStatus.Ok, 7, "Profile A",
                        new[] { new LinkAssignmentSlot(0, "Controller 1", false, true) })));
                    PumpUntil(() => refresh.IsCompleted);
                    refresh.GetAwaiter().GetResult();
                    Assert.True(refreshButton.IsEnabled);
                    Assert.Single(slots.Items.Cast<object>());
                    // Its answer is the list: the controllers show under the
                    // profile it has loaded, and the line has nothing to say.
                    Assert.Equal(Visibility.Visible, controllers.Visibility);
                    Assert.Equal(Visibility.Collapsed, status.Visibility);
                    Assert.Equal(string.Format(PadForge.Resources.Strings.Strings.Instance.RemoteLink_AssignmentProfile_Format, "Profile A"),
                        ((TextBlock)dialog.FindName("ProfileText")).Text);
                    var devicesBox = Assert.IsType<ListBox>(dialog.FindName("DevicesBox"));
                    Assert.Same(source, devicesBox.SelectedItem);

                    Task pending = Refresh(dialog, false);
                    Assert.False(pending.IsCompleted);
                    Assert.False(refreshButton.IsEnabled);
                    Assert.True(close.IsEnabled);
                    close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Assert.True(closed);
                    Assert.False(IsWindow(hwnd));
                    Assert.True(IsWindow(ownerHwnd));
                    PumpUntil(() => pending.IsCompleted);
                    pending.GetAwaiter().GetResult();
                }
                finally
                {
                    if (!closed) dialog.Close();
                    channel.Close();
                    owner.Close();
                }
            });
        }

        private static Task Refresh(RemoteAssignmentsDialog dialog, bool reloadDevices)
        {
            var method = typeof(RemoteAssignmentsDialog).GetMethod("RefreshAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsAssignableFrom<Task>(method.Invoke(dialog, new object[] { reloadDevices }));
        }

        private static void PumpUntil(Func<bool> completed)
        {
            if (completed()) return;
            var clock = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };
            timer.Tick += (_, _) =>
            {
                if (completed() || clock.ElapsedMilliseconds >= 3000) frame.Continue = false;
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Assert.True(completed(), "The dialog operation did not finish within three seconds.");
        }

        // The dialog loads the dialogs' dictionary, whose styles build on
        // the app's own, so it is made beside the stand-ins the dialogs'
        // host holds.
        private static void RunSta(Action body) => DialogTestHost.Run(body);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
