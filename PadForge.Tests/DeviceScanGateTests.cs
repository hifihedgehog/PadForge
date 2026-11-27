using System;
using System.IO;
using PadForge.Common.Input;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// A walk of the system's devices runs again only after Windows says a
    /// device changed, or after a fixed time all the same.
    ///
    /// <para>The DS3 threads walked every device of each class a DS3 can sit
    /// in twice a second with no pad present, the program's largest steady
    /// source of garbage for the collector (measured 2026-11-16). Each
    /// collection stops every thread, the poll loop's among them.</para>
    /// </summary>
    public class DeviceScanGateTests
    {
        [Fact]
        public void TheFirstAskWalks()
        {
            var gate = new DeviceScanGate(15000);
            Assert.True(gate.Due(1000, 0));
        }

        [Fact]
        public void NoChangeWaitsOutTheRescanTime()
        {
            var gate = new DeviceScanGate(15000);
            Assert.True(gate.Due(1000, 3));
            Assert.False(gate.Due(1001, 3));
            Assert.False(gate.Due(15999, 3));
            Assert.True(gate.Due(16000, 3));
            Assert.False(gate.Due(16001, 3));
        }

        [Fact]
        public void AChangeWalksAtOnceAndOnlyOnce()
        {
            var gate = new DeviceScanGate(15000);
            Assert.True(gate.Due(1000, 3));
            Assert.True(gate.Due(1500, 4));
            Assert.False(gate.Due(1600, 4));
            Assert.True(gate.Due(1700, 9));
        }

        [Fact]
        public void TheGateReadsTheCountWindowsKeeps()
        {
            var gate = new DeviceScanGate(long.MaxValue);
            long now = Environment.TickCount64;
            Assert.True(gate.Due(now));
            Assert.False(gate.Due(now));
            DeviceChanges.Changed();
            Assert.True(gate.Due(now));
        }

        /// <summary>Every WM_DEVICECHANGE the main window hears is counted,
        /// whatever its kind: a USB pad still on the inbox driver arrives
        /// with no interface the window registered for.</summary>
        [Fact]
        public void TheMainWindowCountsEveryDeviceChange()
        {
            string window = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
            int hook = window.IndexOf("private IntPtr OnDeviceChangeMessage(", StringComparison.Ordinal);
            Assert.True(hook > 0, "the device change hook moved");
            int arrival = window.IndexOf("(int)wParam == DBT_DEVICEARRIVAL", hook, StringComparison.Ordinal);
            Assert.True(arrival > hook);
            Assert.Contains("if (msg == WM_DEVICECHANGE) PadForge.Common.Input.DeviceChanges.Changed();",
                window.Substring(hook, arrival - hook));
        }

        /// <summary>The DS3 threads ask the gate before the walk that looks for
        /// a pad still needing WinUSB.</summary>
        [Fact]
        public void TheDs3ThreadsWalkOnlyWhenTheGateSays()
        {
            string ds3 = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "Ds3DirectService.cs")).Replace("\r\n", "\n");
            Assert.Contains("private readonly DeviceScanGate _usbScan = new(15000);", ds3);
            Assert.Contains("&& _usbScan.Due(now)\n                    && PadForge.Services.Ds3DriverInstaller.IsUsbPadNeedingWinUsb(", ds3);
        }

        /// <summary>The DS3 and Move watches look for a pad twice a second, and
        /// each look walked the interfaces the pad comes on. They walk only
        /// when the gate says, and a look between walks takes what the last
        /// walk found.</summary>
        [Theory]
        [InlineData("Ds3DirectService.cs", "string path = LookForWinUsbDs3();", "_winUsbLook.Due(Environment.TickCount64)) _winUsbFound = FindWinUsbDs3();")]
        [InlineData("Ds3DirectService.cs", "string path = LookForPdoPath();", "_pdoLook.Due(Environment.TickCount64)) _pdoFound = FindPdoPath();")]
        [InlineData("PsMoveDirectService.cs", "var found = LookForUsbHid();", "_usbLook.Due(Environment.TickCount64)) _usbFound = FindUsbHid();")]
        [InlineData("PsMoveDirectService.cs", "string path = LookForPdoPath();", "_pdoLook.Due(Environment.TickCount64)) _pdoFound = FindPdoPath();")]
        public void ThePadWatchesWalkOnlyWhenTheGateSays(string file, string look, string walk)
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", file)).Replace("\r\n", "\n");
            Assert.Contains(look, source);
            Assert.Contains(walk, source);
            int open = source.IndexOf("private bool OpenBluetooth()", StringComparison.Ordinal);
            int usb = source.IndexOf("private bool OpenUsb()", StringComparison.Ordinal);
            Assert.True(open > 0 && usb > 0);
            Assert.DoesNotContain("= FindPdoPath();", source.Substring(open, 600));
        }

        /// <summary>The vendor USB binder walked every present USB node every
        /// 3 s. It walks only when the gate says or while a bind waits for its
        /// retry, and its wait ends on Stop with no 100 ms slices.</summary>
        [Fact]
        public void TheVendorUsbBinderWalksOnlyWhenTheGateSaysOrABindWaits()
        {
            string source = File.ReadAllText(Path.Combine(RepoRoot(), "PadForge.App", "Common", "Input", "VendorUsbBindingService.cs")).Replace("\r\n", "\n");
            Assert.Contains("if (_retryWaits || _scan.Due(Environment.TickCount64))", source);
            Assert.Contains("if (stop.WaitOne(SweepMs)) break;", source);
            Assert.DoesNotContain("Thread.Sleep(100);", source);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }
    }
}
