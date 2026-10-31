using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PadForge.Common.Input;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using Xunit;

namespace PadForge.Tests
{
    /// <summary>
    /// The test host changes no driver and reaches no TouchSense mouse:
    /// the installer's calls and the mouse's look, open and transfers each
    /// refuse, each asks before it reaches the PC, and nothing in the app
    /// turns a refusal on. Every path here names a device that is not
    /// there.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class TouchSenseOffLimitsTests
    {
        private const string NoMouse = @"\\?\usb#vid_046d&pid_c030#padforge-test#{fd826c66-3556-4845-9436-0fe7580ded02}";

        private static string Root()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "PadForge.sln")))
                d = d.Parent;
            Assert.NotNull(d);
            return d.FullName;
        }

        private static string Source(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { Root(), "PadForge.App" }.Concat(parts).ToArray()));

        private static IEnumerable<string> AppSources()
        {
            string root = Root();
            char slash = Path.DirectorySeparatorChar;
            return new[] { "PadForge.App", "PadForge.Engine" }
                .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
                .Where(path => !path.Contains(slash + "obj" + slash) && !path.Contains(slash + "bin" + slash));
        }

        // ── Drivers ──

        /// <summary>A bind and a restore asked of the installer in the test
        /// host change nothing: refused at the door, before a package is
        /// staged and before the node is even read, where a bind that ran
        /// and failed says why and leaves its staging folder.</summary>
        [Fact]
        public void TheTestHostChangesNoDriver()
        {
            Assert.True(VendorUsbDriverInstaller.NoDriverChangesForTest);
            // An ID no device has, so its staging folders are this test's alone.
            const string bindId = @"USB\VID_FFFE&PID_0494";
            string winUsb = VendorUsbDriverInstaller.StagingDirectory(bindId);
            string xusb = VendorUsbDriverInstaller.StagingDirectory(bindId + "_xusb22");
            var node = new VendorUsbDriverInstaller.UsbNode(bindId + @"\padforge-test-no-such-node", 0xFFFE, 0x0494, -1, "HidUsb",
                new[] { bindId }, Array.Empty<string>());
            var said = new List<string>();
            try
            {
                foreach (string staging in new[] { winUsb, xusb })
                    if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

                var plan = new VendorUsbDriverInstaller.BindPlan(bindId, "Test device");
                Assert.False(VendorUsbDriverInstaller.Bind(plan, node.InstanceId, new[] { node }, said.Add, CancellationToken.None));
                Assert.False(VendorUsbDriverInstaller.Bind(plan with { Driver = VendorUsbDriverInstaller.BindDriver.Xusb22 },
                    node.InstanceId, new[] { node }, said.Add, CancellationToken.None, new[] { "HidUsb" }));
                Assert.False(VendorUsbDriverInstaller.Restore(node.InstanceId, said.Add, CancellationToken.None));

                Assert.Empty(said);
                Assert.False(Directory.Exists(winUsb), "a package was staged");
                Assert.False(Directory.Exists(xusb), "a package was staged");
            }
            finally
            {
                foreach (string staging in new[] { winUsb, xusb })
                {
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
                }
            }
        }

        /// <summary>Signing a package, stocking it, forcing it onto a node
        /// and removing it are each made in one place in the installer,
        /// and that place asks first, so no path through the class reaches
        /// one of them in the test host.</summary>
        [Theory]
        [InlineData("private static bool SignPackage(string directory, string catalog, Action<string> log)", "Ds3DriverInstaller.SignDriverPackage(")]
        [InlineData("private static bool StockPackage(string inf, out bool reboot)", "Devcon.Install(")]
        [InlineData("private static bool ForceDriver(string bindId, string inf)", "UpdateDriverForPlugAndPlayDevices(IntPtr.Zero,")]
        [InlineData("private static void RemovePackage(string inf, string path)", "Devcon.DeleteDriver(")]
        public void EachCallThatChangesThePc_IsMadeInOnePlace_ThatAsksFirst(string helper, string call)
        {
            string source = Source("Services", "VendorUsbDriverInstaller.cs");
            int at = source.IndexOf(helper, StringComparison.Ordinal);
            Assert.True(at >= 0, helper);
            int made = source.IndexOf(call, StringComparison.Ordinal);
            Assert.True(made > at, call + " is not made in " + helper);
            Assert.Equal(-1, source.IndexOf(call, made + 1, StringComparison.Ordinal));
            string before = source.Substring(at + helper.Length, made - at - helper.Length);
            Assert.Contains("NoDriverChangesForTest", before);
            // The ask sits in this helper, not in one further down the file.
            Assert.DoesNotContain(" static ", before);
        }

        /// <summary>Bind and Restore ask before anything else: before a
        /// folder is made, a node is read or a helper is called.</summary>
        [Theory]
        [InlineData("internal static bool Bind(BindPlan plan, string instanceId, IReadOnlyList<UsbNode> nodes,")]
        [InlineData("internal static bool Restore(string instanceId, Action<string> log, CancellationToken ct)")]
        public void TheInstallersTwoDoors_AskBeforeAnythingElse(string door)
        {
            string source = Source("Services", "VendorUsbDriverInstaller.cs");
            int at = source.IndexOf(door, StringComparison.Ordinal);
            Assert.True(at >= 0, door);
            Assert.Equal(-1, source.IndexOf(door, at + 1, StringComparison.Ordinal));
            int asks = source.IndexOf("if (NoDriverChangesForTest) return false;", at, StringComparison.Ordinal);
            Assert.True(asks > at, door + " never asks");
            string before = source.Substring(at + door.Length, asks - at - door.Length);
            foreach (string early in new[] { "try", "foreach", "Directory.", "File.", "PnPDevice.", "Package(", "ForceDriver(", " static " })
                Assert.DoesNotContain(early, before);
        }

        // ── TouchSense mice ──

        [Fact]
        public void TheTestHostReachesNoTouchSenseMouse()
        {
            Assert.True(RealTouchSenseAccess.Refused);
            Assert.Null(WinUsbTouchSenseUsb.Open(NoMouse));
            Assert.Null(TouchSenseChannel.Open(NoMouse));
            // The look is refused, which reads as a look that failed.
            Assert.Null(TouchSenseBinding.FindBound());

            // The shape read of the offer reads no collection of this PC.
            Assert.True(RealHidAccess.Refused);
            var node = new VendorUsbDriverInstaller.UsbNode(@"USB\VID_046D&PID_C030\padforge-test-no-such-port", 0x046D, 0xC030, -1, "HidUsb",
                new[] { @"USB\VID_046D&PID_C030" }, Array.Empty<string>());
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node));
            Assert.False(TouchSenseBinding.ReadOutputCaps(
                @"\\?\hid#vid_046d&pid_c030#padforge-test#{4d1e55b2-f16f-11cf-88cb-001111000030}", out int buttons, out var values));
            Assert.Equal(0, buttons);
            Assert.Empty(values);
            // So an offer asked for with no seam of the test's is no offer.
            Assert.Null(VendorUsbDriverInstaller.QueryOptIn(0x046D, 0xC030, new[] { node }, _ => false));
        }

        [Fact]
        public void AnAllowScopeLiftsTheMiceRefusal_AndPutsItBack()
        {
            using (RealTouchSenseAccess.AllowForTest())
            {
                Assert.False(RealTouchSenseAccess.Refused);
                using (RealTouchSenseAccess.AllowForTest())
                    Assert.False(RealTouchSenseAccess.Refused);
                Assert.False(RealTouchSenseAccess.Refused);
                // The drivers stay off limits: nothing lifts that.
                Assert.True(VendorUsbDriverInstaller.NoDriverChangesForTest);
            }
            Assert.True(RealTouchSenseAccess.Refused);
        }

        /// <summary>The device pass on its real parts, as any test that
        /// runs an input manager has it: it looks at no real PC, and handed
        /// the path of a mouse it opens nothing.</summary>
        [Fact]
        public void TheDevicePassOnItsRealParts_FindsAndOpensNothing()
        {
            long now = 1_000_000;
            var manager = new InputManager
            {
                TouchSenseTable = new TouchSenseChannels(),
                TouchSenseTicks = () => now,
                TouchSenseRun = sweep =>
                {
                    sweep();
                    return Task.CompletedTask;
                },
            };
            try
            {
                Assert.False(manager.SyncTouchSenseMice());
                Assert.Empty(manager.TouchSenseMice);

                manager.TouchSenseFind = () => new[] { new TouchSenseMouse(NoMouse, 0x046D, 0xC030) };
                now += InputManager.TouchSenseSweepMs;
                Assert.False(manager.SyncTouchSenseMice());
                Assert.False(manager.SyncTouchSenseMice());
                Assert.Empty(manager.TouchSenseMice);
                Assert.Equal(0, manager.TouchSenseTable.Count);
            }
            finally
            {
                manager.RetireTouchSenseMice();
            }
        }

        /// <summary>The backend on its real look: refused, the look reads
        /// as one that failed, so the backend lists nothing and stays
        /// unscanned.</summary>
        [Fact]
        public void TheBackendOnItsRealLook_ListsNothing()
        {
            using var backend = new TouchSenseBackend(new TouchSenseChannels(), activeTickMs: 1, idleTickMs: 1);
            backend.Start();
            backend.Rescan(null);
            int seen = backend.PassCount;
            Assert.True(SpinWait.SpinUntil(() =>
            {
                backend.Nudge();
                return backend.PassCount >= seen + 2;
            }, 30000), "the worker made no pass");
            Assert.Same(DirectBackendSnapshot.NotScanned, backend.Snapshot);
        }

        /// <summary>The look, the open and each call on an open interface
        /// ask before the first call that reaches the PC, so a refusal
        /// enumerates nothing, opens no handle and sends no transfer. A
        /// bound keyboard's look, open and collection read (#549) ask the
        /// same way.</summary>
        [Theory]
        [InlineData("Services", "TouchSenseBinding.cs", "internal static List<TouchSenseMouse> FindBound()", "RealTouchSenseAccess.Refused")]
        [InlineData("Services", "WinUsbKeyboardBinding.cs", "internal static List<WinUsbKeyboard> FindBound()", "RealTouchSenseAccess.Refused")]
        [InlineData("Services", "WinUsbKeyboardBinding.cs", "internal static Takes WhatLeaves(string nodeId)", "RealHidAccess.Refused")]
        [InlineData("WinUsbKeyboards", "WinUsbKeyboardChannel.cs", "public static WinUsbKeyboardChannel Open(string path)", "RealTouchSenseAccess.Refused")]
        [InlineData("Services", "TouchSenseBinding.cs", "NodeDeclaresBuzz(VendorUsbDriverInstaller.UsbNode node)", "RealHidAccess.Refused")]
        [InlineData("Services", "TouchSenseBinding.cs", "internal static bool ReadOutputCaps(string path, out int buttons, out List<TouchSenseValueCap> values)", "RealHidAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseChannel.cs", "public static TouchSenseChannel Open(string path)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "internal static WinUsbTouchSenseUsb Open(string path)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public bool Describe(out byte interfaceClass, out byte interruptIn, out byte interruptOut)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public bool DescribeInterface(out byte number, out byte interfaceClass, out byte interruptIn, out ushort packetSize)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public int ReadConfiguration(byte[] buffer)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public bool ControlIn(byte[] setup, byte[] buffer, out int length)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public bool ControlOut(byte[] setup, byte[] data)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public bool WritePipe(byte pipe, byte[] data)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public int ReadPipe(byte pipe, byte[] buffer, out int error)", "RealTouchSenseAccess.Refused")]
        [InlineData("Peripherals", "TouchSenseWinUsb.cs", "public void SetTimeout(byte pipe, int milliseconds)", "RealTouchSenseAccess.Refused")]
        public void EachRealCallAsksBeforeItReachesThePc(string folder, string file, string signature, string ask)
        {
            string source = folder switch
            {
                "Services" => Source("Services", file),
                "WinUsbKeyboards" => Source("Common", "Input", "WinUsbKeyboards", file),
                _ => Source("Common", "Input", "Peripherals", file),
            };
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature);
            Assert.Equal(-1, source.IndexOf(signature, at + 1, StringComparison.Ordinal));
            int asks = source.IndexOf(ask, at, StringComparison.Ordinal);
            Assert.True(asks > at, signature + " never asks");
            string before = source.Substring(at + signature.Length, asks - at - signature.Length);
            foreach (string reach in new[] { "CreateFile", "WinUsb_", "WinUsbTouchSenseUsb.Open", "ActiveInterfacePaths", "Enumerate(", "ParentOf" })
                Assert.DoesNotContain(reach, before);
            // The ask sits in this method, not in one further down the file.
            Assert.DoesNotContain(" static ", before);
            Assert.DoesNotContain("public ", before);
            Assert.DoesNotContain("private ", before);
            Assert.DoesNotContain("internal ", before);
        }

        /// <summary>Every winusb.dll call the wrapper makes sits in a
        /// method the test above covers, so none is reached without the
        /// ask. The cancel, the free and the handle's close alone are left
        /// out: each acts on a handle the refused open never made, and
        /// lets go of a device rather than reach one.</summary>
        [Fact]
        public void NoWinUsbCallIsMadeOutsideAMethodThatAsks()
        {
            string source = Source("Common", "Input", "Peripherals", "TouchSenseWinUsb.cs");
            int declarations = source.IndexOf("[DllImport(\"winusb.dll\"", StringComparison.Ordinal);
            Assert.True(declarations > 0);
            string code = source.Substring(0, declarations);
            var called = Regex.Matches(code, @"\b(WinUsb_\w+|CreateFile|CancelIoEx|CloseHandle)\(").Select(m => m.Groups[1].Value).ToArray();
            Assert.Equal(new[]
            {
                "CreateFile", "WinUsb_Initialize", "CloseHandle",
                "WinUsb_QueryInterfaceSettings", "WinUsb_QueryPipe",
                "WinUsb_QueryInterfaceSettings", "WinUsb_QueryPipe",
                "WinUsb_GetDescriptor",
                "WinUsb_ControlTransfer", "WinUsb_ControlTransfer",
                "WinUsb_WritePipe",
                "WinUsb_ReadPipe",
                "WinUsb_SetPipePolicy",
                "CancelIoEx",
                "WinUsb_Free", "CloseHandle",
            }, called);
        }

        // ── Nothing in the app sets either ──

        [Fact]
        public void NothingInTheAppTurnsARefusalOn_OrLiftsOne()
        {
            var sources = AppSources().Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path))).ToList();
            Assert.Equal(new[] { "RealTouchSenseAccess.cs" },
                sources.Where(s => s.Text.Contains("KeepOutForTest", StringComparison.Ordinal)).Select(s => s.Name));
            Assert.Empty(sources.Where(s => s.Text.Contains("RealTouchSenseAccess.AllowForTest", StringComparison.Ordinal)
                                            && s.Name != "RealTouchSenseAccess.cs").Select(s => s.Name));
            // The installer's flag is read and never written.
            var assignment = new Regex(@"NoDriverChangesForTest\s*=(?!=)");
            Assert.Empty(sources.Where(s => assignment.IsMatch(s.Text)).Select(s => s.Name));
            Assert.Equal(new[] { "VendorUsbDriverInstaller.cs" },
                sources.Where(s => s.Text.Contains("NoDriverChangesForTest", StringComparison.Ordinal)).Select(s => s.Name));
        }
    }
}
