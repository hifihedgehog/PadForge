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
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Threading;
using PadForge.Engine.RemoteLink;
using PadForge.Views;
using Wpf.Ui.Appearance;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
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

                    var title = Assert.IsType<TextBlock>(dialog.FindName("DialogTitleText"));
                    var header = Assert.IsType<Border>(dialog.FindName("DialogHeader"));
                    var close = Assert.IsAssignableFrom<ButtonBase>(dialog.FindName("HeaderCloseButton"));
                    var root = Assert.IsType<Grid>(dialog.Content);
                    root.Measure(new Size(620, 500));
                    root.Arrange(new Rect(0, 0, 620, 500));
                    root.UpdateLayout();
                    BindingOperations.GetBindingExpression(title, TextBlock.TextProperty)?.UpdateTarget();
                    Assert.Same(dialog, Window.GetWindow(title));
                    Assert.Equal(Visibility.Visible, title.Visibility);
                    Assert.True(header.ActualHeight > 0);
                    Assert.True(title.ActualWidth > 0);
                    Assert.Equal(dialog.Title, title.Text);
                    Assert.Contains("Gaming PC", title.Text);
                    dialog.Title = "Changed peer";
                    BindingOperations.GetBindingExpression(title, TextBlock.TextProperty)?.UpdateTarget();
                    Assert.Equal("Changed peer", title.Text);

                    var refreshButton = Assert.IsAssignableFrom<ButtonBase>(dialog.FindName("RefreshButton"));
                    var slots = Assert.IsType<ItemsControl>(dialog.FindName("SlotsList"));
                    Task refresh = Refresh(dialog, true);
                    Assert.NotNull(sent);
                    Assert.False(refresh.IsCompleted);
                    Assert.False(refreshButton.IsEnabled);
                    var first = sent;
                    channel.Receive(LinkAssignmentCodec.Encode(new LinkAssignmentReply(first.RequestId,
                        first.DeviceId, LinkAssignmentStatus.Ok, 7, "Profile A",
                        new[] { new LinkAssignmentSlot(0, "Controller 1", false, true) })));
                    PumpUntil(() => refresh.IsCompleted);
                    refresh.GetAwaiter().GetResult();
                    Assert.True(refreshButton.IsEnabled);
                    Assert.Single(slots.Items.Cast<object>());

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

        private static void RunSta(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                try { body(); }
                catch (Exception error) { failure = error; }
                finally { dispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(15000), "The STA dialog test timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
