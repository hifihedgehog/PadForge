using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using PadForge.Common.Input;
using PadForge.Engine.Tablets;
using PadForge.Services;

namespace PadForge.Tests;

public class InputServiceHidingLifecycleTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    private static InputManager Manager()
    {
        var manager = (InputManager)RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
        // Identity markers own no readers or native resources to finalize.
        GC.SuppressFinalize(manager);
        return manager;
    }

    private static InputService Service(Dispatcher dispatcher, bool stopped, InputManager manager, bool disposed = false)
    {
        var service = (InputService)RuntimeHelpers.GetUninitializedObject(typeof(InputService));
        GC.SuppressFinalize(service);
        Set(service, "_dispatcher", dispatcher);
        Set(service, "_stopped", stopped ? 1 : 0);
        Set(service, "_inputManager", manager);
        Set(service, "_disposed", disposed ? 1 : 0);
        return service;
    }

    private static void Set(InputService service, string field, object value)
        => typeof(InputService).GetField(field, Fields).SetValue(service, value);

    private static void Drain(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action<Dispatcher> body)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try { body(dispatcher); }
            catch (Exception error) { failure = error; }
            finally { dispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The dispatcher test did not finish.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    public void AutomaticHidingPreservesIdleControlsButRejectsRetiringOwners(
        bool disposed, bool stopped, bool hasManager, bool admitted)
    {
        RunSta(dispatcher =>
        {
            var service = Service(dispatcher, stopped, hasManager ? Manager() : null, disposed);
            int applies = 0;
            service.ApplyAutomaticDeviceHiding(() => applies++);
            Assert.Equal(admitted ? 1 : 0, applies);
        });
    }

    [Fact]
    public void QueuedAutomaticHidingChecksRetirementWhenDelivered()
    {
        RunSta(dispatcher =>
        {
            var service = Service(dispatcher, false, Manager());
            int applies = 0;
            var submit = new Thread(() => service.ApplyAutomaticDeviceHiding(() => applies++)) { IsBackground = true };
            submit.Start();
            Assert.True(submit.Join(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, applies);
            Set(service, "_stopped", 1);
            Drain(dispatcher);
            Assert.Equal(0, applies);

            // Once teardown clears the manager, ordinary stopped-state
            // reconciliation is still supported.
            Set(service, "_inputManager", null);
            service.ApplyAutomaticDeviceHiding(() => applies++);
            Assert.Equal(1, applies);
        });
    }

    [Fact]
    public void OldEngineNotificationsDoNotRefreshTheReplacement()
    {
        RunSta(dispatcher =>
        {
            var oldManager = Manager();
            var currentManager = Manager();
            var service = Service(dispatcher, false, oldManager);
            var handler = typeof(InputService).GetMethod("OnDevicesUpdated", Fields);
            var errors = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler capture = (_, e) =>
            {
                errors.Add(e.Exception);
                e.Handled = true;
            };
            dispatcher.UnhandledException += capture;
            try
            {
                handler.Invoke(service, new object[] { oldManager, EventArgs.Empty });
                Set(service, "_inputManager", currentManager);
                Drain(dispatcher);
                Assert.Empty(errors);

                // The deliberately absent view model is a trap at the first
                // refresh statement, before any driver access. A current
                // notification must reach it, proving delivery is active.
                handler.Invoke(service, new object[] { currentManager, EventArgs.Empty });
                Drain(dispatcher);
                Assert.IsType<NullReferenceException>(Assert.Single(errors));
            }
            finally { dispatcher.UnhandledException -= capture; }
        });
    }

    [Fact]
    public void UnhideRetiresOnlyTheStoppingEnginesWrappers()
    {
        RunSta(dispatcher =>
        {
            foreach (bool stopped in new[] { false, true })
            {
                using var fixture = new TabletReportStateTests.Fixture();
                using var device = new WindowsTabletDevice(fixture.Decoder, null,
                    () => new MemoryStream(), _ => { }, () => true);
                using var reader = new WindowsTabletReader();
                var devices = (Dictionary<string, WindowsTabletDevice>)typeof(WindowsTabletReader)
                    .GetField("devices", Fields).GetValue(reader);
                devices.Add(device.DevicePath, device);
                var manager = Manager();
                typeof(InputManager).GetField("_tabletReader", Fields).SetValue(manager, reader);
                var service = Service(dispatcher, stopped, manager);

                // Keeping cloaks avoids driver access while exercising the
                // real release entry point and its live-wrapper enumeration.
                service.RemoveDeviceHiding(keepCloaks: true);
                device.SetCapture(true);
                Assert.Equal(!stopped, device.CaptureWasRequested);
            }
        });
    }

    [Fact]
    public void AutomaticCallersUseTheGatewayAndManualSaveKeepsItsDirectApply()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "PadForge.App"))) root = root.Parent;
        Assert.NotNull(root);
        string service = File.ReadAllText(Path.Combine(root.FullName, "PadForge.App", "Services", "InputService.cs"));
        string window = File.ReadAllText(Path.Combine(root.FullName, "PadForge.App", "MainWindow.xaml.cs"));
        string tablets = File.ReadAllText(Path.Combine(root.FullName, "PadForge.App", "Services", "InputService.Tablets.cs"));

        static string Between(string text, string begin, string end)
        {
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            Assert.True(start >= 0, begin);
            int finish = text.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
            Assert.True(finish > start, end);
            return text.Substring(start, finish - start);
        }

        Assert.Contains("ApplyAutomaticDeviceHiding();", Between(service, "private void OnDevicesUpdated", "private void OnFrequencyUpdated"));
        Assert.Contains("ApplyAutomaticDeviceHiding();", Between(service, "private void OnHandheldRegistryChanged", "private void OnHandheldActivityChanged"));
        Assert.Contains("ApplyAutomaticDeviceHiding();", Between(service, "private void OnHandheldActivityChanged", "private void RefreshVoiceObjects"));
        Assert.Contains("ApplyAutomaticDeviceHiding();", Between(window, "_settingsService.AutoSaved +=", "_viewModel.Settings.ReloadRequested"));
        Assert.Contains("ApplyAutomaticDeviceHiding();", Between(window, "private IntPtr OnDeviceChangeMessage", "private void SetupNativeTooltip"));
        Assert.Contains("ApplyAutomaticDeviceHiding();", tablets);
        Assert.Contains("ApplyDeviceHiding();", Between(window, "_viewModel.Settings.SaveRequested +=", "_settingsService.AutoSaved +="));
        Assert.Contains("PrepareForUnhide(retireCapture: retiring)", service);
    }
}
