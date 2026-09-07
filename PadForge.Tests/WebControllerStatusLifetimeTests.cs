using System;
using System.Reflection;
using PadForge.Services;
using PadForge.ViewModels;

namespace PadForge.Tests
{
    [Collection("SettingsManagerStatics")]
    public sealed class WebControllerStatusLifetimeTests
    {
        [Fact]
        public void ExplicitStopClearsDashboardStateWithoutAcceptingItsQueuedStatus()
        {
            var vm = new MainViewModel();
            var input = new InputService(vm);
            using var server = new WebControllerServer();
            var field = typeof(InputService).GetField("_webServer", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(input, server);
            var stale = new WebControllerServer.Status(server.Generation, "old running");
            vm.Dashboard.IsWebControllerRunning = true;
            vm.Dashboard.WebControllerClientCount = 4;
            typeof(InputService).GetMethod("StopWebServer", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(input, null);
            Assert.Null(field.GetValue(input));
            Assert.False(vm.Dashboard.IsWebControllerRunning);
            Assert.Equal(0, vm.Dashboard.WebControllerClientCount);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Common_Stopped, vm.Dashboard.WebControllerStatus);
            input.ApplyWebServerStatus(server, stale);
            Assert.False(vm.Dashboard.IsWebControllerRunning);
            Assert.Equal(PadForge.Resources.Strings.Strings.Instance.Common_Stopped, vm.Dashboard.WebControllerStatus);
        }

        [Fact]
        public void QueuedStatusCannotOverwriteAReplacementOrRestartedServer()
        {
            var vm = new MainViewModel();
            var input = new InputService(vm);
            using var old = new WebControllerServer();
            using var current = new WebControllerServer();
            typeof(InputService).GetField("_webServer", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(input, current);
            input.ApplyWebServerStatus(current, new WebControllerServer.Status(current.Generation, "current"));
            Assert.Equal("current", vm.Dashboard.WebControllerStatus);
            Assert.False(vm.Dashboard.IsWebControllerRunning);
            input.ApplyWebServerStatus(old, new WebControllerServer.Status(old.Generation, "old"));
            Assert.Equal("current", vm.Dashboard.WebControllerStatus);
            long generation = current.Generation;
            current.Stop();
            input.ApplyWebServerStatus(current, new WebControllerServer.Status(generation, "stale generation"));
            Assert.Equal("current", vm.Dashboard.WebControllerStatus);
            input.ApplyWebServerStatus(current, new WebControllerServer.Status(current.Generation, "stopped"));
            Assert.Equal("stopped", vm.Dashboard.WebControllerStatus);
        }
    }
}
