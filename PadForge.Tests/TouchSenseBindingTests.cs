using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PadForge.Common.Input;
using PadForge.Common.Input.KeyboardScreens;
using PadForge.Common.Input.Peripherals;
using PadForge.Services;
using Xunit;
using Xunit.Abstractions;
using Installer = PadForge.Services.VendorUsbDriverInstaller;
using Kind = PadForge.Services.VendorUsbDriverInstaller.OptInKind;
using Node = PadForge.Services.VendorUsbDriverInstaller.UsbNode;

namespace PadForge.Tests
{
    /// <summary>
    /// The opt-in binding that hands a TouchSense mouse to WinUSB (#494):
    /// which node is offered it, from which driver, what the package
    /// names, and that nothing binds such a mouse without the confirm
    /// that states the cost. No driver is installed, bound or removed
    /// here: the offers are computed from scripted nodes, and the tests
    /// that look at this PC only read it, past a scope that lifts the test
    /// host's refusal, so the class runs with no other beside it.
    /// </summary>
    [Collection("PeripheralOutputStatics")]
    public class TouchSenseBindingTests
    {
        private readonly ITestOutputHelper _output;
        public TouchSenseBindingTests(ITestOutputHelper output) => _output = output;

        private const string InterfaceGuid = "{fd826c66-3556-4845-9436-0fe7580ded02}";

        /// <summary>A TouchSense mouse's USB node: one device with one
        /// interface of HID class, boot subclass, mouse protocol, and the
        /// revision 1.01 (the iFeel Mouse's descriptors, tactile-cvs
        /// tactile/support/logs/ifeel.log,v:60-62 and 85-89).</summary>
        private static Node Mouse(ushort pid, string service, string port = "5&2E1A4C7B&0&2")
            => new($@"USB\VID_046D&PID_{pid:X4}\{port}", 0x046D, pid, -1, service,
                new[] { $@"USB\VID_046D&PID_{pid:X4}&REV_0101", $@"USB\VID_046D&PID_{pid:X4}" },
                new[] { @"USB\Class_03&SubClass_01&Prot_02", @"USB\Class_03&SubClass_01", @"USB\Class_03" });

        /// <summary>A shape seam for the offers in which no mouse collection
        /// may be read.</summary>
        private static bool Never(Node node) => throw new InvalidOperationException("the mouse collection of " + node.InstanceId + " was read");

        public static IEnumerable<object[]> Mice => TouchSenseProtocol.Mice.Select(m => new object[] { (int)m.ProductId });

        // ── The offer ──

        [Theory]
        [MemberData(nameof(Mice))]
        public void AMouseOnWindowsHidDriver_ThatDeclaresTheBuzzReport_IsOfferedTheBinding(int pid)
        {
            var node = Mouse((ushort)pid, "HidUsb");
            var asked = new List<string>();
            var offer = Installer.QueryOptIn(0x046D, (ushort)pid, new[] { node }, _ => false, n =>
            {
                asked.Add(n.InstanceId);
                return true;
            });

            Assert.NotNull(offer);
            Assert.Equal(Kind.TouchSenseMouse, offer.Kind);
            Assert.False(offer.Restore);
            // The whole device, by its own ID with no revision, on WinUSB.
            Assert.Equal($@"USB\VID_046D&PID_{pid:X4}", offer.Bind?.BindId);
            Assert.Equal("TouchSense mouse", offer.Bind?.Name);
            Assert.Equal(Installer.BindDriver.WinUsb, offer.Bind?.Driver);
            // Taken from Windows' HID driver and from nothing else, which
            // is also what the bind itself is told (MainWindow passes the
            // offer's list on).
            Assert.Equal(new[] { "HidUsb" }, offer.From);
            Assert.Equal(node.InstanceId, offer.NodeId);
            // The node on offer is the one whose collection was read, once.
            Assert.Equal(new[] { node.InstanceId }, asked);

            // Windows writes the service name in either case.
            Assert.NotNull(Installer.QueryOptIn(0x046D, (ushort)pid, new[] { Mouse((ushort)pid, "hidusb") }, _ => false, _ => true));
        }

        /// <summary>The IDs alone offer nothing. Only the iFeel Mouse's
        /// descriptor is on record, so a mouse that does not declare the
        /// seven-byte report is left as it is, and of two mice of one
        /// model the one that declares it is the one offered.</summary>
        [Theory]
        [MemberData(nameof(Mice))]
        public void AMouseThatDoesNotDeclareTheBuzzReport_IsOfferedNothing(int pid)
        {
            Assert.Null(Installer.QueryOptIn(0x046D, (ushort)pid, new[] { Mouse((ushort)pid, "HidUsb") }, _ => false, _ => false));

            var plain = Mouse((ushort)pid, "HidUsb", "5&2E1A4C7B&0&1");
            var buzz = Mouse((ushort)pid, "HidUsb", "5&2E1A4C7B&0&2");
            var offer = Installer.QueryOptIn(0x046D, (ushort)pid, new[] { plain, buzz }, _ => false,
                n => n.InstanceId == buzz.InstanceId);
            Assert.Equal(buzz.InstanceId, offer?.NodeId);
            Assert.NotNull(offer?.Bind);
        }

        /// <summary>A mouse on anything but Windows' HID driver is someone
        /// else's or has nothing to read a shape from, so it is offered
        /// nothing and its collection is never read.</summary>
        [Theory]
        [InlineData("")]          // no driver: no mouse collection to read
        [InlineData("usbccgp")]   // a composite device, which no TouchSense mouse on record is
        [InlineData("LHidUsb")]   // Logitech's own HID driver for the second iFeel Mouse (LH2KPLUS.INF:67 and 114)
        [InlineData("WUDFRd")]
        [InlineData("xusb22")]
        [InlineData("WINUSB")]    // WinUSB bound by someone else
        public void AMouseOnAnyOtherDriver_IsOfferedNothing_AndItsCollectionIsNeverRead(string service)
        {
            foreach (var mouse in TouchSenseProtocol.Mice)
                Assert.Null(Installer.QueryOptIn(mouse.VendorId, mouse.ProductId,
                    new[] { Mouse(mouse.ProductId, service) }, _ => false, Never));
        }

        [Theory]
        [MemberData(nameof(Mice))]
        public void ABoundMouse_IsOfferedTheRestore_AndOneOnAnotherWinUsbIsNot(int pid)
        {
            var bound = Mouse((ushort)pid, "WINUSB");
            var offer = Installer.QueryOptIn(0x046D, (ushort)pid, new[] { bound }, id => id == bound.InstanceId, Never);
            Assert.NotNull(offer);
            Assert.True(offer.Restore);
            Assert.Null(offer.Bind);
            Assert.Equal(Kind.TouchSenseMouse, offer.Kind);
            Assert.Equal(bound.InstanceId, offer.NodeId);

            // WinUSB without PadForge's interface is another program's binding.
            Assert.Null(Installer.QueryOptIn(0x046D, (ushort)pid, new[] { bound }, _ => false, Never));
        }

        [Fact]
        public void OnlyTheWholeDeviceOfAListedMouse_IsATarget()
        {
            // An interface child, which a composite sibling would have.
            var child = new Node(@"USB\VID_046D&PID_C030&MI_00\6&1A2B3C4D&0&0000", 0x046D, 0xC030, 0, "HidUsb",
                new[] { @"USB\VID_046D&PID_C030&REV_0101&MI_00", @"USB\VID_046D&PID_C030&MI_00" }, Array.Empty<string>());
            Assert.Null(Installer.QueryOptIn(0x046D, 0xC030, new[] { child }, _ => false, Never));

            // The second MouseMan ID, which the vendor's driver has no
            // entry for, the MouseMan Optical with no motor, and the same
            // product ID under another vendor.
            foreach (var (vid, pid) in new (ushort, ushort)[] { (0x046D, 0xC033), (0x046D, 0xC034), (0x045E, 0xC030) })
            {
                Assert.False(Installer.IsOptInRow(vid, pid));
                var node = new Node($@"USB\VID_{vid:X4}&PID_{pid:X4}\5&1&0&1", vid, pid, -1, "HidUsb",
                    new[] { $@"USB\VID_{vid:X4}&PID_{pid:X4}" }, Array.Empty<string>());
                Assert.Null(Installer.QueryOptIn(vid, pid, new[] { node }, _ => false, Never));
            }
            // A row of one model is never offered another model's node.
            Assert.Null(Installer.QueryOptIn(0x046D, 0xC030, new[] { Mouse(0xC032, "HidUsb") }, _ => false, Never));
        }

        // ── The table ──

        [Fact]
        public void TheRows_AreTheMiceTheProtocolNames_EachFromWindowsHidDriverAlone()
        {
            var rows = Installer.OptIns.Where(t => t.Kind == Kind.TouchSenseMouse).ToArray();
            Assert.Equal(TouchSenseProtocol.Mice.Select(m => (m.VendorId, m.ProductId)), rows.Select(r => (r.Vid, r.Pid)));
            Assert.All(rows, row =>
            {
                Assert.Equal(Installer.Match.Device, row.Match);
                Assert.Equal(-1, row.Interface);
                Assert.Null(row.Revision);
                Assert.Equal(new[] { "HidUsb" }, row.From);
                Assert.True(Installer.IsOptInRow(row.Vid, row.Pid));
                Assert.Equal(Kind.TouchSenseMouse, Installer.KindOfId($@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}"));
            });
        }

        /// <summary>The rows that were there before keep their offers
        /// exactly: each is still offered by its IDs, its interface and
        /// its revision, in every driver state, and none of them is ever
        /// asked for a descriptor shape.</summary>
        [Fact]
        public void EveryOtherOptIn_IsOfferedExactlyAsBefore_AndIsNeverAskedForAShape()
        {
            var others = Installer.OptIns.Where(t => t.Kind != Kind.TouchSenseMouse).ToArray();
            // The six rows the table held before the mice, unchanged.
            Assert.Equal(new[]
            {
                (0x045E, 0x028E, Installer.Match.Device, -1, "0110", Kind.Xbox360Pad, "xusb22"),
                (0x045E, 0x028E, Installer.Match.Device, -1, "0114", Kind.Xbox360Pad, "xusb22"),
                (0x045E, 0x0719, Installer.Match.Device, -1, "0100", Kind.Xbox360Receiver, "xusb22"),
                (0x8086, 0xC013, Installer.Match.Interface, 0, null, Kind.IntelBaseStation, "|HidUsb"),
                (0x8086, 0xC013, Installer.Match.Device, -1, null, Kind.IntelBaseStation, "|HidUsb"),
                (0x041E, 0x2801, Installer.Match.Interface, 1, null, Kind.Prodikeys, "|HidUsb"),
            }, others.Select(r => ((int)r.Vid, (int)r.Pid, r.Match, r.Interface, r.Revision, r.Kind, string.Join("|", r.From))));

            foreach (var row in others)
            {
                string revision = row.Revision ?? "0100";
                string id = row.Interface < 0
                    ? $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}"
                    : $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&MI_{row.Interface:X2}";
                Node On(string service) => new(id + @"\6&1A2B3C4D&0&1", row.Vid, row.Pid, row.Interface, service,
                    row.Interface < 0
                        ? new[] { $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&REV_{revision}", id }
                        : new[] { $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&REV_{revision}&MI_{row.Interface:X2}", id },
                    Array.Empty<string>());

                foreach (string from in row.From)
                {
                    var node = On(from);
                    var offer = Installer.QueryOptIn(row.Vid, row.Pid, new[] { node }, _ => false, Never);
                    Assert.NotNull(offer);
                    Assert.Equal(row.Kind, offer.Kind);
                    Assert.False(offer.Restore);
                    Assert.Equal(row.Revision != null ? $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&REV_{row.Revision}" : id,
                        offer.Bind?.BindId);
                    Assert.Equal(Installer.NameOf(row.Kind), offer.Bind?.Name);
                    Assert.Same(row.From, offer.From);
                    Assert.Equal(node.InstanceId, offer.NodeId);
                }

                var bound = On("WINUSB");
                var restore = Installer.QueryOptIn(row.Vid, row.Pid, new[] { bound }, _ => true, Never);
                Assert.True(restore?.Restore);
                Assert.Null(restore.Bind);
                Assert.Equal(row.Kind, restore.Kind);
                Assert.Null(Installer.QueryOptIn(row.Vid, row.Pid, new[] { bound }, _ => false, Never));
                Assert.Null(Installer.QueryOptIn(row.Vid, row.Pid, new[] { On("usbccgp") }, _ => false, Never));
            }
        }

        // ── The package ──

        [Theory]
        [MemberData(nameof(Mice))]
        public void ThePackage_NamesTheMousesOwnId(int pid)
        {
            var plan = Installer.QueryOptIn(0x046D, (ushort)pid, new[] { Mouse((ushort)pid, "HidUsb") }, _ => false, _ => true).Bind.Value;
            string inf = Installer.BuildInf(plan.BindId, plan.Name);
            Assert.Contains($"[Standard.NTamd64]\r\n%DeviceName% = USB_Install, USB\\VID_046D&PID_{pid:X4}\r\n", inf);
            Assert.Contains($"[Standard.NTarm64]\r\n%DeviceName% = USB_Install, USB\\VID_046D&PID_{pid:X4}\r\n", inf);
            Assert.Contains("DeviceName   = \"TouchSense mouse (WinUSB)\"", inf);
            Assert.Contains("Include = winusb.inf", inf);
            // The interface the bound mouse is found by afterward.
            Assert.Contains(InterfaceGuid.ToUpperInvariant(), inf);
            // One ID and no other: no revision, no interface, no sibling.
            Assert.Equal(2, inf.Split("VID_046D").Length - 1);
            Assert.DoesNotContain("&REV_", inf);
            Assert.DoesNotContain("&MI_", inf);
        }

        // ── Nothing binds a mouse on its own ──

        /// <summary>The automatic binder takes what
        /// <see cref="VendorUsbDriverInstaller.Plan"/> names, with no
        /// prompt. It never names a TouchSense mouse, in any driver
        /// state: a mouse that lost its pointer without being asked
        /// would be a mouse nobody can click the restore with.</summary>
        [Fact]
        public void TheAutomaticBinder_NeverPlansATouchSenseMouse()
        {
            foreach (var mouse in TouchSenseProtocol.Mice)
            {
                foreach (string service in new[] { "", "HidUsb", "hidusb", "usbccgp", "WINUSB", "LHidUsb" })
                    Assert.Null(Installer.Plan(Mouse(mouse.ProductId, service)));
                Assert.DoesNotContain(Installer.Targets, t => t.Vid == mouse.VendorId && t.Pid == mouse.ProductId);
                Assert.DoesNotContain(Installer.Xusb22Targets, t => t.Vid == mouse.VendorId && t.Pid == mouse.ProductId);
                Assert.DoesNotContain((mouse.VendorId, mouse.ProductId), Installer.XidIdentities);
            }
        }

        private static string Body(string source, string signature)
        {
            int start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, signature);
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced: " + signature);
        }

        private static string AppRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PadForge.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static IEnumerable<string> Sources(params string[] projects)
        {
            string root = AppRoot();
            foreach (string project in projects)
                foreach (string file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(root, file);
                    if (relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    yield return file;
                }
        }

        /// <summary>The opt-in bind is reached only through the confirm
        /// that states what the switch takes: the dialog shows the cost
        /// text of the offer's own kind, and a "no" leaves before
        /// anything is bound. The only other calls of the bind are the
        /// automatic binder's, which never plans a TouchSense mouse (the
        /// test above), and the keyboard screens' (#519), which binds the
        /// G19's screen interface alone. A keyboard's cost (#549) also
        /// names what leaves Windows with it.</summary>
        [Fact]
        public void TheBinding_IsMadeOnlyBehindTheConfirmThatStatesItsCost()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            string body = Body(window, "private async System.Threading.Tasks.Task SwitchDriverAsync(bool bind)");
            // One condition of the bind holds the refusal for other mice
            // of the model and, right after it, the confirm.
            const string condition = "if (bind && (TakesOthersAlong(offer, nodes, log)";
            int asked = body.IndexOf(condition, StringComparison.Ordinal);
            Assert.True(asked >= 0, "the condition every bind passes");
            int confirm = body.IndexOf("|| !Views.ConfirmDialog.Show(this, Strings.Instance.Devices_DriverConfirmTitle,", asked, StringComparison.Ordinal);
            Assert.True(confirm > asked, "the confirm");
            Assert.True(string.IsNullOrWhiteSpace(body.Substring(asked + condition.Length, confirm - asked - condition.Length)), "nothing between them");
            int cost = body.IndexOf("cost, Strings.Instance.Devices_DriverSwitch, keyboard ? name : null)))", confirm, StringComparison.Ordinal);
            Assert.True(cost > confirm, "the confirm shows the cost");
            // The cost is the offer's kind's, set once before the
            // condition. A keyboard's adds what leaves with it, read from
            // the collections on its node.
            string flat = string.Concat(body.Substring(0, asked).Where(c => !char.IsWhiteSpace(c)));
            Assert.Contains("boolkeyboard=offer.Kind==Services.VendorUsbDriverInstaller.OptInKind.Keyboard;", flat);
            Assert.Contains("stringcost=!bind?null:keyboard?KeyboardDriverCost(awaitSystem.Threading.Tasks.Task.Run(()=>"
                + "Services.WinUsbKeyboardBinding.WhatLeaves(offer.NodeId))):DriverCost(offer.Kind);", flat);
            Assert.Equal(1, body.Split(" cost =").Length - 1);
            Assert.Equal(1, body.Split(" keyboard =").Length - 1);
            int leave = body.IndexOf("return;", cost, StringComparison.Ordinal);
            int bind = body.IndexOf("Services.VendorUsbDriverInstaller.Bind(offer.Bind.Value, offer.NodeId, nodes, log, default, offer.From, offer.Kind)", StringComparison.Ordinal);
            Assert.True(leave > cost && bind > leave, "a refusal leaves before the bind");
            // Nothing between the confirm and its return but the condition itself.
            Assert.DoesNotContain("Bind(", body.Substring(0, leave));
            // The cost of this kind is the text that says the pointer goes.
            Assert.Contains("Services.VendorUsbDriverInstaller.OptInKind.TouchSenseMouse => Strings.Instance.Devices_DriverConfirm_TouchSenseMouse,",
                Body(window, "private static string DriverCost(Services.VendorUsbDriverInstaller.OptInKind kind) => kind switch"));
            // The request reaches the method from the page's button and from nowhere else.
            Assert.Equal(1, window.Split("SwitchDriverAsync(bind: true)").Length - 1);

            // Every call of the bind in the app: this one, the automatic
            // binder's and the G19 screen's.
            var callers = Sources("PadForge.App")
                .Select(file => (File: Path.GetFileName(file), Calls: File.ReadAllText(file).Split("VendorUsbDriverInstaller.Bind(").Length - 1))
                .Where(c => c.Calls > 0)
                .OrderBy(c => c.File, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(new[] { ("G19UsbScreen.cs", 1), ("MainWindow.xaml.cs", 1), ("VendorUsbBindingService.cs", 1) }, callers);

            // The G19 screen binds interface 0 of 046D:C229 alone, from the
            // bind's own default drivers (none and HidUsb), only while LGS
            // is away, and is no opt-in of the confirm's kinds.
            string g19 = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "KeyboardScreens", "G19UsbScreen.cs");
            Assert.Contains("internal const ushort Vid = KeyboardScreenDevices.LogitechVid;", g19);
            Assert.Contains("internal const ushort Pid = KeyboardScreenDevices.G19;", g19);
            // A screen is made only for a G19's interface 0, and each one
            // binds its own node.
            Assert.Contains("node.Vid != G19UsbScreen.Vid || node.Pid != G19UsbScreen.Pid || node.Interface != 0", g19);
            Assert.Contains("string.Equals(n.InstanceId, _instanceId, StringComparison.OrdinalIgnoreCase)", g19);
            Assert.Contains("if (!mayBind || now < _nextBind || _bindFailures >= MaxBindFailures) return;", g19);
            Assert.Contains("Bind = (plan, instanceId, nodes, log, ct) => VendorUsbDriverInstaller.Bind(plan, instanceId, nodes, log, ct),", g19);
            Assert.Contains("if (_io.Bind(plan, node.InstanceId, nodes, _log, _stopping)) _bindFailures = 0;", g19);
            Assert.DoesNotContain("OptIn", g19);
            Assert.DoesNotContain(TouchSenseProtocol.Mice, m => m.VendorId == KeyboardScreenDevices.LogitechVid
                && m.ProductId == KeyboardScreenDevices.G19);

            string binder = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "VendorUsbBindingService.cs");
            Assert.Contains("if (VendorUsbDriverInstaller.Plan(node) is { } plan)", binder);
            Assert.Contains("VendorUsbDriverInstaller.Bind(plan, node.InstanceId, nodes, _log, _cts.Token)", binder);
            Assert.DoesNotContain("OptIn", binder);
        }

        /// <summary>The driver switch lets go of a node before it asks
        /// Windows to change the node's driver, and keeps it let go until
        /// its wait for the new driver is over. The restore holds the
        /// mouse's node from before the package is removed to its return.
        /// The bind holds every node its ID names from before the forced
        /// update, which restarts them all, to the end of its wait, by
        /// whichever way it leaves. The device pass looks for bound mice
        /// every second, so a node let go for a moment would be open again
        /// in the middle of the switch.</summary>
        [Fact]
        public void TheDriverSwitch_LetsGoOfTheNodeBeforeItAsksWindows_AndUntilItsWaitIsOver()
        {
            string installer = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "VendorUsbDriverInstaller.cs");
            string restore = Body(installer, "internal static bool Restore(string instanceId, Action<string> log, CancellationToken ct)");
            // A using declaration: the hold ends where the method does.
            int letGo = restore.IndexOf("using var letGo = LetGoOf(instanceId);", StringComparison.Ordinal);
            int remove = restore.IndexOf("RemovePackage(inf, path);", StringComparison.Ordinal);
            int waits = restore.IndexOf("Thread.Sleep(250);", StringComparison.Ordinal);
            Assert.True(letGo > 0 && remove > letGo && waits > remove, $"{letGo} {remove} {waits}");
            Assert.DoesNotContain("letGo.Dispose()", restore);
            Assert.Equal(1, restore.Split("LetGoOf(").Length - 1);

            string bind = Body(installer, "internal static bool Bind(BindPlan plan, string instanceId, IReadOnlyList<UsbNode> nodes,");
            int list = bind.IndexOf("var letGo = new List<IDisposable>();", StringComparison.Ordinal);
            int guarded = bind.IndexOf("try", list, StringComparison.Ordinal);
            int each = bind.IndexOf("if (Names(n, plan.BindId)) letGo.Add(LetGoOf(n.InstanceId));", StringComparison.Ordinal);
            int force = bind.IndexOf("ForceDriver(plan.BindId, inf)", StringComparison.Ordinal);
            int wait = bind.IndexOf("Thread.Sleep(250);", force, StringComparison.Ordinal);
            int release = bind.IndexOf("foreach (var hold in letGo) hold.Dispose();", StringComparison.Ordinal);
            Assert.True(list > 0 && guarded > list && each > guarded && force > each && wait > force && release > wait,
                $"{list} {guarded} {each} {force} {wait} {release}");
            // Let go in the finally of that try, so a return from inside
            // the wait and a throw both end the holds, and nothing ends
            // them sooner.
            int last = bind.LastIndexOf("finally", release, StringComparison.Ordinal);
            Assert.True(last > wait, $"{last} {wait}");
            Assert.Equal("finally{", string.Concat(bind.Substring(last, release - last).Where(c => !char.IsWhiteSpace(c))));
            Assert.Equal(1, bind.Split("hold.Dispose()").Length - 1);
            Assert.Equal(1, bind.Split("LetGoOf(").Length - 1);

            Assert.Contains("=> PadForge.Common.Input.Peripherals.TouchSenseChannels.Shared.Hold(instanceId);", installer);
            Assert.DoesNotContain("CloseAt", installer);

            // The device pass asks the table before it opens a mouse, and
            // says so when the open is over, on every way out of it.
            string pass = AuditDelta20261002Tests.RepoText("PadForge.App", "Common", "Input", "InputManager.TouchSenseMice.cs");
            string sweep = Body(pass, "private void SweepTouchSenseMice()");
            int begin = sweep.IndexOf("if (!TouchSenseTable.TryBeginOpen(mouse.Path)) continue;", StringComparison.Ordinal);
            int open = sweep.IndexOf("TouchSenseOpen(mouse)", StringComparison.Ordinal);
            int end = sweep.IndexOf("TouchSenseTable.EndOpen(mouse.Path);", StringComparison.Ordinal);
            Assert.True(begin > 0 && open > begin && end > open, $"{begin} {open} {end}");
            int ends = sweep.LastIndexOf("finally", end, StringComparison.Ordinal);
            Assert.True(ends > open, $"{ends} {open}");
            Assert.Equal("finally{", string.Concat(sweep.Substring(ends, end - ends).Where(c => !char.IsWhiteSpace(c))));
        }

        /// <summary>A bind that would take another mouse of the model with
        /// the chosen one is refused before the confirm is shown, and once
        /// more after it against a fresh list of what is plugged in, since
        /// the confirm can stay open while a second mouse is plugged in.
        /// The status bar then says what to do, and stays. Each way out of
        /// the switch takes the busy mark off the page. A keyboard's Keep
        /// prompt and automatic undo (#549) run before the last of them,
        /// while the page is still busy.</summary>
        [Fact]
        public void ABindThatWouldTakeAnotherMouseAlong_IsRefusedBeforeTheConfirm_AndAgainAfterIt()
        {
            string window = AuditDelta20261002Tests.RepoText("PadForge.App", "MainWindow.xaml.cs");
            string body = Body(window, "private async System.Threading.Tasks.Task SwitchDriverAsync(bool bind)");
            const string asks = "TakesOthersAlong(offer, nodes, log)";
            const string lists = "await System.Threading.Tasks.Task.Run(Services.VendorUsbDriverInstaller.ListPresentUsbNodes);";
            int confirm = body.IndexOf("Views.ConfirmDialog.Show(", StringComparison.Ordinal);
            int before = body.IndexOf(asks, StringComparison.Ordinal);
            int listed = body.IndexOf("var nodes = " + lists, StringComparison.Ordinal);
            int again = body.IndexOf("nodes = " + lists, confirm, StringComparison.Ordinal);
            int after = body.IndexOf(asks, confirm, StringComparison.Ordinal);
            int bind = body.IndexOf("Services.VendorUsbDriverInstaller.Bind(", StringComparison.Ordinal);
            Assert.True(listed > 0 && before > listed && confirm > before && again > confirm && after > again && bind > after,
                $"{listed} {before} {confirm} {again} {after} {bind}");
            Assert.Equal(2, body.Split(asks).Length - 1);
            Assert.Equal(1, body.Split("devices.DriverBusy = true;").Length - 1);
            Assert.Equal(3, body.Split("devices.DriverBusy = false;").Length - 1);
            Assert.True(body.IndexOf("devices.DriverBusy = true;", StringComparison.Ordinal) < listed);

            string helper = Body(window, "private bool TakesOthersAlong(Services.VendorUsbDriverInstaller.OptInOffer offer,");
            Assert.Contains("Services.VendorUsbDriverInstaller.WhyNotBind(offer.Bind.Value, offer.NodeId, nodes, offer.From, out string why, offer.Kind)", helper);
            Assert.Contains("!= Services.VendorUsbDriverInstaller.BindRefusal.OthersPluggedIn)", helper);
            // A keyboard's refusal (#549) speaks of keyboards, and stays too.
            Assert.Contains("_viewModel.SetStatus(offer.Kind==Services.VendorUsbDriverInstaller.OptInKind.Keyboard"
                + "?Strings.Instance.Status_DriverBindOthersPluggedIn_Keyboard"
                + ":Strings.Instance.Status_DriverBindOthersPluggedIn,persist:true);",
                string.Concat(helper.Where(c => !char.IsWhiteSpace(c))));

            // The bind itself asks the same question before it stages anything.
            string installer = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "VendorUsbDriverInstaller.cs");
            string made = Body(installer, "internal static bool Bind(BindPlan plan, string instanceId, IReadOnlyList<UsbNode> nodes,");
            int refused = made.IndexOf("if (WhyNotBind(plan, instanceId, nodes, from, out string why, kind) != BindRefusal.None)", StringComparison.Ordinal);
            int staged = made.IndexOf("Directory.CreateDirectory(dir);", StringComparison.Ordinal);
            Assert.True(refused > 0 && staged > refused, $"{refused} {staged}");
        }

        private static Installer.BindPlan PlanOf(ushort pid)
            => Installer.QueryOptIn(0x046D, pid, new[] { Mouse(pid, "HidUsb") }, _ => false, _ => true).Bind.Value;

        /// <summary>The forced update moves every plugged-in node its ID
        /// names, since UpdateDriverForPlugAndPlayDevices takes a hardware
        /// ID and no node, and a bound mouse is no longer Windows'
        /// pointer. The confirm speaks of the one mouse the user chose, so
        /// the bind is refused while another mouse of the model would go
        /// with it.</summary>
        [Theory]
        [MemberData(nameof(Mice))]
        public void ABindOfAMouse_IsRefused_WhileAnotherMouseOfItsModelIsOnWindowsDriver(int pid)
        {
            ushort id = (ushort)pid;
            var chosen = Mouse(id, "HidUsb");
            var plan = PlanOf(id);
            string[] from = Installer.OptIns.Single(t => t.Kind == Kind.TouchSenseMouse && t.Pid == id).From;
            Assert.Equal(new[] { "HidUsb" }, from);

            // The one mouse of its model.
            Assert.Equal(Installer.BindRefusal.None, Installer.WhyNotBind(plan, chosen.InstanceId, new[] { chosen }, from, out string why));
            Assert.Null(why);

            // A second one on Windows' HID driver would be moved too,
            // wherever it stands in the list.
            var second = Mouse(id, "HidUsb", "5&2E1A4C7B&0&3");
            foreach (var nodes in new[] { new[] { chosen, second }, new[] { second, chosen } })
            {
                Assert.Equal(Installer.BindRefusal.OthersPluggedIn, Installer.WhyNotBind(plan, chosen.InstanceId, nodes, from, out why));
                Assert.Contains(second.InstanceId, why);
                Assert.Contains(chosen.InstanceId, why);
            }
            // The letter case of a service and of the chosen node's ID decides nothing.
            Assert.Equal(Installer.BindRefusal.OthersPluggedIn, Installer.WhyNotBind(plan, chosen.InstanceId.ToLowerInvariant(),
                new[] { chosen, Mouse(id, "hidusb", "5&2E1A4C7B&0&3") }, from, out _));
            Assert.Equal(Installer.BindRefusal.None, Installer.WhyNotBind(plan, chosen.InstanceId.ToLowerInvariant(), new[] { chosen }, from, out _));

            // One that is bound already is restarted on the driver it has, and loses nothing.
            Assert.Equal(Installer.BindRefusal.None, Installer.WhyNotBind(plan, chosen.InstanceId,
                new[] { chosen, Mouse(id, "WINUSB", "5&2E1A4C7B&0&3") }, from, out why));
            Assert.Null(why);

            // A mouse of another model is not named by the ID.
            ushort other = TouchSenseProtocol.Mice.First(m => m.ProductId != id).ProductId;
            Assert.Equal(Installer.BindRefusal.None, Installer.WhyNotBind(plan, chosen.InstanceId,
                new[] { chosen, Mouse(other, "HidUsb", "5&2E1A4C7B&0&3") }, from, out _));

            // A node on a driver the bind takes nothing from refuses the
            // ID, before the other refusal and whatever the order.
            var vendors = Mouse(id, "LHidUsb", "5&2E1A4C7B&0&4");
            foreach (var nodes in new[] { new[] { chosen, second, vendors }, new[] { vendors, second, chosen }, new[] { second, vendors, chosen } })
            {
                Assert.Equal(Installer.BindRefusal.AnotherDriver, Installer.WhyNotBind(plan, chosen.InstanceId, nodes, from, out why));
                Assert.Contains(vendors.InstanceId, why);
            }

            // A list that could not be read refuses nothing by itself.
            Assert.Equal(Installer.BindRefusal.None, Installer.WhyNotBind(plan, chosen.InstanceId, null, from, out why));
            Assert.Null(why);
        }

        /// <summary>The other kinds are read by PadForge on either driver
        /// and lose nothing the user was not told, so two of one model are
        /// bound together as before. A keyboard (#549) is bound alone, as a
        /// TouchSense mouse is, since another of its model would stop
        /// typing too.</summary>
        [Fact]
        public void EveryOtherKind_IsBoundWithItsSiblingsAsBefore()
        {
            var alone = new[] { Kind.TouchSenseMouse, Kind.Keyboard };
            foreach (Kind kind in Enum.GetValues<Kind>())
                Assert.True(alone.Contains(kind) == Installer.BindsOneDeviceAtATime(kind), kind.ToString());

            foreach (var row in Installer.OptIns.Where(t => !alone.Contains(t.Kind)))
            {
                string revision = row.Revision ?? "0100";
                string id = row.Interface < 0
                    ? $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}"
                    : $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&MI_{row.Interface:X2}";
                Node On(string service, string port) => new(id + @"\" + port, row.Vid, row.Pid, row.Interface, service,
                    row.Interface < 0
                        ? new[] { $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&REV_{revision}", id }
                        : new[] { $@"USB\VID_{row.Vid:X4}&PID_{row.Pid:X4}&REV_{revision}&MI_{row.Interface:X2}", id },
                    Array.Empty<string>());

                foreach (string service in row.From)
                {
                    var first = On(service, "6&1A2B3C4D&0&1");
                    var second = On(service, "6&1A2B3C4D&0&2");
                    var plan = Installer.QueryOptIn(row.Vid, row.Pid, new[] { first }, _ => false, Never).Bind.Value;
                    Assert.Equal(Installer.BindRefusal.None,
                        Installer.WhyNotBind(plan, first.InstanceId, new[] { first, second }, row.From, out string why));
                    Assert.Null(why);
                }
            }
        }

        /// <summary>The restore is offered for a node whose own interface
        /// PadForge's package registered, matched by the instance ID the
        /// path carries and the # that ends it, so a bound mouse on port
        /// 21 of a hub never offers the restore to another program's
        /// WinUSB node on port 2.</summary>
        [Fact]
        public void ANodesInterfacePath_IsMatchedByItsWholeInstanceId()
        {
            string[] paths = { @"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&21#" + InterfaceGuid };
            Assert.True(Installer.NamesNode(paths, @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&21"));
            Assert.False(Installer.NamesNode(paths, @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&2"));
            Assert.False(Installer.NamesNode(paths, @"USB\VID_046D&PID_C032\5&2E1A4C7B&0&21"));
            Assert.False(Installer.NamesNode(null, @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&21"));
            Assert.False(Installer.NamesNode(paths, null));
            Assert.False(Installer.NamesNode(paths, ""));
            Assert.False(Installer.NamesNode(new string[] { null }, @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&21"));
        }

        // ── Finding the bound mice ──

        [Theory]
        [InlineData(@"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid, 0x046D, 0xC030)]
        [InlineData(@"\\?\USB#VID_046D&PID_C032#5&2E1A4C7B&0&2#{FD826C66-3556-4845-9436-0FE7580DED02}", 0x046D, 0xC032)]
        [InlineData(@"\\?\usb#vid_14b7&pid_0982#6&1a2b3c4d&0&1#" + InterfaceGuid, 0x14B7, 0x0982)]
        public void ABoundNodesInterfacePath_GivesItsIds(string path, int vid, int pid)
        {
            Assert.True(TouchSenseBinding.TryParseBoundPath(path, out ushort v, out ushort p));
            Assert.Equal((vid, pid), (v, p));
        }

        [Theory]
        // The interface child of a composite device.
        [InlineData(@"\\?\usb#vid_046d&pid_c030&mi_00#6&2e1a4c7b&0&0000#" + InterfaceGuid)]
        // The mouse's HID collection, which it has only on Windows' HID driver.
        [InlineData(@"\\?\hid#vid_046d&pid_c030#7&1b2c3d4e&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
        [InlineData(@"\\?\usb#vid_046d&pid_c030")]
        [InlineData(@"usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid)]
        [InlineData(@"\\?\usb#root_hub30#4&1234&0&0#" + InterfaceGuid)]
        [InlineData("")]
        [InlineData(null)]
        public void APathThatIsNoWholeUsbDevice_GivesNoIds(string path)
        {
            Assert.False(TouchSenseBinding.TryParseBoundPath(path, out ushort v, out ushort p));
            Assert.Equal((0, 0), (v, p));
        }

        /// <summary>Every device PadForge binds registers the same
        /// interface, so the look keeps the TouchSense mice and passes
        /// over the rest, each with the path as Windows wrote it.</summary>
        [Fact]
        public void TheLook_KeepsTheTouchSenseMiceAmongPadForgesBoundDevices()
        {
            string iFeel = @"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid;
            string second = @"\\?\USB#VID_046D&PID_C031#5&2E1A4C7B&0&3#" + InterfaceGuid;
            var mice = TouchSenseBinding.BoundMice(new[]
            {
                iFeel,
                @"\\?\usb#vid_14b7&pid_0982#6&1a2b3c4d&0&1#" + InterfaceGuid,          // a Gametrak
                @"\\?\usb#vid_2ca3&pid_1023&mi_01#7&1a2b3c4d&0&0001#" + InterfaceGuid,  // a DJI RC's interface 1
                second,
                @"\\?\usb#vid_046d&pid_c033#5&2e1a4c7b&0&4#" + InterfaceGuid,          // not a listed mouse
                null,
                "",
            });
            Assert.Equal(new[] { new TouchSenseMouse(iFeel, 0x046D, 0xC030), new TouchSenseMouse(second, 0x046D, 0xC031) }, mice);
            Assert.Empty(TouchSenseBinding.BoundMice(null));
            Assert.Empty(TouchSenseBinding.BoundMice(Array.Empty<string>()));
        }

        // ── The shape read, from parts the test hands in ──

        private static readonly TouchSenseValueCap BuzzCap = new(0, 0xFF00, 0x0002, 8, 7);
        private const string Collection = @"\\?\hid#vid_046d&pid_c030#7&1b2c3d4e&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        private static HidCollectionInfo MouseCollection(string path, ushort pid = 0xC030, ushort usage = 0x0002, ushort output = 8)
            => new() { Path = path, VendorId = 0x046D, ProductId = pid, UsagePage = 0x0001, Usage = usage, InputReportLength = 5, OutputReportLength = output };

        private static TouchSenseBinding.OutputCapsReader Caps(params TouchSenseValueCap[] values)
            => (string path, out int buttons, out List<TouchSenseValueCap> read) =>
            {
                buttons = 0;
                read = values.ToList();
                return true;
            };

        /// <summary>A node is offered the binding by what its own mouse
        /// collection declares: the collection with the node's IDs whose
        /// parent is this node, read through the parts handed in. No
        /// collection of this PC is read.</summary>
        [Fact]
        public void TheShapeRead_AnswersForTheNodesOwnMouseCollection()
        {
            var node = Mouse(0xC030, "HidUsb");
            var other = Mouse(0xC030, "HidUsb", "5&2E1A4C7B&0&3");
            const string otherCollection = @"\\?\hid#vid_046d&pid_c030#7&9f8e7d6c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            var collections = new[] { MouseCollection(Collection), MouseCollection(otherCollection) };
            string ParentOf(string path) => path == Collection ? node.InstanceId : path == otherCollection ? other.InstanceId : null;

            Assert.True(TouchSenseBinding.NodeDeclaresBuzz(node, collections, ParentOf, Caps(BuzzCap)));

            // Two mice of one model on two ports: each answers for itself.
            TouchSenseBinding.OutputCapsReader onlyTheFirst = (string path, out int buttons, out List<TouchSenseValueCap> read) =>
            {
                buttons = 0;
                read = path == Collection ? new List<TouchSenseValueCap> { BuzzCap } : new List<TouchSenseValueCap>();
                return true;
            };
            Assert.True(TouchSenseBinding.NodeDeclaresBuzz(node, collections, ParentOf, onlyTheFirst));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(other, collections, ParentOf, onlyTheFirst));
            // The parent is compared as Windows compares instance IDs, without regard to case.
            Assert.True(TouchSenseBinding.NodeDeclaresBuzz(node, collections, path => ParentOf(path)?.ToLowerInvariant(), onlyTheFirst));

            // An output report of another shape, and another length.
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, collections, ParentOf, Caps(BuzzCap with { ReportCount = 8 })));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection, output: 9) }, ParentOf, Caps(BuzzCap)));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection, output: 0) }, ParentOf, Caps()));
        }

        /// <summary>A collection that is not the node's own mouse
        /// collection is never read, and one that cannot be read declares
        /// nothing.</summary>
        [Fact]
        public void TheShapeRead_ReadsNothingElse_AndACollectionThatCannotBeReadDeclaresNothing()
        {
            var node = Mouse(0xC030, "HidUsb");
            TouchSenseBinding.OutputCapsReader never = (string path, out int buttons, out List<TouchSenseValueCap> read)
                => throw new InvalidOperationException("the collection at " + path + " was read");
            string Own(string path) => node.InstanceId;

            // Another model, another vendor's device with the same product
            // ID, a collection of the mouse that is no mouse collection, and
            // a mouse collection under another node.
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection, pid: 0xC032) }, Own, never));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node,
                new[] { new HidCollectionInfo { Path = Collection, VendorId = 0x045E, ProductId = 0xC030, UsagePage = 1, Usage = 2, OutputReportLength = 8 } }, Own, never));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection, usage: 0x0006) }, Own, never));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, _ => @"USB\VID_046D&PID_C030\5&2E1A4C7B&0&9", never));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, _ => null, never));

            // A read that fails or throws, and a parent lookup that throws.
            TouchSenseBinding.OutputCapsReader fails = (string path, out int buttons, out List<TouchSenseValueCap> read) =>
            {
                buttons = 0;
                read = new List<TouchSenseValueCap> { BuzzCap };
                return false;
            };
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, Own, fails));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, Own, never));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) },
                _ => throw new InvalidOperationException("scripted"), Caps(BuzzCap)));
            // One collection that cannot be read costs the next one nothing.
            Assert.True(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { null, MouseCollection("unreadable"), MouseCollection(Collection) }, Own,
                (string path, out int buttons, out List<TouchSenseValueCap> read) =>
                {
                    if (path == "unreadable") throw new InvalidOperationException("scripted");
                    buttons = 0;
                    read = new List<TouchSenseValueCap> { BuzzCap };
                    return true;
                }));

            // Nothing to read from, and a node with no instance ID.
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, null, Own, Caps(BuzzCap)));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, Array.Empty<HidCollectionInfo>(), Own, Caps(BuzzCap)));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, null, Caps(BuzzCap)));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(node, new[] { MouseCollection(Collection) }, Own, null));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(default, new[] { MouseCollection(Collection) }, _ => null, Caps(BuzzCap)));
        }

        // ── A live PC, read only, and only when asked ──

        /// <summary>The two tests below read the PC they run on, and run
        /// only with PADFORGE_LIVE_TOUCHSENSE set to 1. They open no device
        /// for reading or writing and send nothing.</summary>
        private static bool Live => Environment.GetEnvironmentVariable("PADFORGE_LIVE_TOUCHSENSE") == "1";

        /// <summary>The look on this PC: the interfaces PadForge's
        /// packages registered that are active now, and the TouchSense
        /// mice among them. It reads SetupAPI and opens nothing.</summary>
        [Fact]
        public void Live_TheLookReadsThisPcsBoundInterfaces()
        {
            if (!Live) return;
            using var real = RealTouchSenseAccess.AllowForTest();
            var paths = Installer.ActiveInterfacePaths(Installer.InterfaceGuid);
            Assert.NotNull(paths);
            _output.WriteLine($"{paths.Count} active PadForge interfaces");
            foreach (string path in paths)
            {
                _output.WriteLine(path);
                Assert.Contains(InterfaceGuid, path, StringComparison.OrdinalIgnoreCase);
            }
            var mice = TouchSenseBinding.FindBound();
            Assert.NotNull(mice);
            Assert.All(mice, mouse =>
            {
                Assert.True(TouchSenseProtocol.IsMouse(mouse.VendorId, mouse.ProductId));
                Assert.Contains(mouse.Path, paths);
            });
            // An interface class nothing registers lists nothing, which is
            // not the failed look that null stands for.
            Assert.Empty(Installer.ActiveInterfacePaths(new Guid("0B5F7C1E-93D4-4B5A-9C27-5F1D6E2A8C41")));

            // Paths of the kind a bound mouse has: the interface Windows'
            // USB stack registers on every whole USB device, which differs
            // from PadForge's in its GUID alone. Each gives the look its
            // IDs and names a listed node, and the container lookup that
            // ties a bound mouse's row to its unit answers for it.
            var devicePaths = Installer.ActiveInterfacePaths(new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED"));
            Assert.NotNull(devicePaths);
            var nodes = Installer.ListPresentUsbNodes();
            int parsed = 0, tied = 0, contained = 0;
            foreach (string path in devicePaths)
            {
                if (!TouchSenseBinding.TryParseBoundPath(path, out ushort vid, out ushort pid)) continue;
                parsed++;
                if (nodes.Any(n => n.Interface < 0 && n.Vid == vid && n.Pid == pid
                                   && path.Contains(n.InstanceId.Replace('\\', '#'), StringComparison.OrdinalIgnoreCase)))
                    tied++;
                if (AudioPassthroughService.DevicePathContainerId(path) != Guid.Empty) contained++;
            }
            _output.WriteLine($"{devicePaths.Count} USB device interfaces, {parsed} parsed, {tied} tied to a listed node, {contained} with a container");
            if (parsed > 0)
            {
                Assert.True(tied > 0, "no USB device interface path named a listed node");
                Assert.True(contained > 0, "no USB device interface path gave a container");
            }

            // The same walk over an interface class this PC has devices
            // of, its HID collections: a collection that could be read is
            // among the active interfaces, and its node is found by the
            // instance ID in its path, the match the bind's proof makes.
            var hid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
            var active = Installer.ActiveInterfacePaths(hid);
            Assert.NotNull(active);
            var collections = HidCollectionRuntime.Enumerate();
            _output.WriteLine($"{active.Count} active HID interfaces, {collections?.Count ?? 0} collections read");
            if (collections == null || collections.Count == 0) return;
            var listed = new HashSet<string>(active, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(collections, c => listed.Contains(c.Path));
            // An interface path is its node's instance ID with # for each
            // backslash, between the prefix and the interface's GUID.
            static string InstanceOf(string path)
            {
                int end = path.LastIndexOf("#{", StringComparison.Ordinal);
                return path.StartsWith(@"\\?\", StringComparison.Ordinal) && end > 4
                    ? path.Substring(4, end - 4).Replace('#', '\\')
                    : null;
            }
            Assert.Contains(collections, c => InstanceOf(c.Path) is { } instance && Installer.HasActiveInterface(instance, hid));
            Assert.False(Installer.HasActiveInterface(@"HID\VID_0000&PID_0000\0&0&0", hid));
        }

        /// <summary>The shape read on this PC's own devices, with no access
        /// right and no report: each mouse collection's parent node and
        /// output capabilities are written out, and no node that is not a
        /// TouchSense mouse is ever taken for one that declares the buzz
        /// report.</summary>
        [Fact]
        public void Live_TheShapeReadTakesNoOtherDeviceOnThisPcForATouchSenseMouse()
        {
            if (!Live) return;
            using var real = RealHidAccess.AllowForTest();
            var nodes = Installer.ListPresentUsbNodes();
            var collections = HidCollectionRuntime.Enumerate();
            Assert.NotNull(collections);

            int mice = 0, onUsb = 0, tied = 0, read = 0;
            foreach (var collection in collections.Where(c => c.UsagePage == 0x0001 && c.Usage == 0x0002))
            {
                mice++;
                string parent = TouchSenseBinding.ParentOf(collection.Path);
                bool usb = parent != null && parent.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
                if (usb) onUsb++;
                if (usb && nodes.Any(n => string.Equals(n.InstanceId, parent, StringComparison.OrdinalIgnoreCase)
                                          && n.Vid == collection.VendorId && n.Pid == collection.ProductId))
                    tied++;
                bool ok = TouchSenseBinding.ReadOutputCaps(collection.Path, out int buttons, out var values);
                if (ok) read++;
                _output.WriteLine($"mouse {collection.VendorId:X4}:{collection.ProductId:X4} output {collection.OutputReportLength} "
                    + $"feature {collection.FeatureReportLength} caps {(ok ? $"{buttons} buttons, {values.Count} values" : "unread")} parent {parent ?? "(none)"}");
                if (ok && !TouchSenseProtocol.IsMouse(collection.VendorId, collection.ProductId))
                    Assert.False(TouchSenseProtocol.MouseCollectionDeclaresBuzz(collection.UsagePage, collection.Usage,
                        collection.OutputReportLength, collection.FeatureReportLength, buttons, values), collection.Path);
            }
            _output.WriteLine($"{mice} mouse collections, {onUsb} under a USB node, {tied} tied to a listed node with their IDs, {read} with caps read");

            // The read at work on real devices: where any collection with
            // an output report could be read, one of them shows an output
            // capability.
            int withOutput = 0, described = 0;
            foreach (var collection in collections.Where(c => c.OutputReportLength > 0))
            {
                if (!TouchSenseBinding.ReadOutputCaps(collection.Path, out int buttons, out var values)) continue;
                withOutput++;
                if (buttons + values.Count > 0) described++;
            }
            _output.WriteLine($"{withOutput} collections with an output report read, {described} with output capabilities");
            Assert.True(withOutput == 0 || described > 0, "no output capability was read from any collection");

            foreach (var node in nodes)
            {
                bool declares = TouchSenseBinding.NodeDeclaresBuzz(node);
                if (TouchSenseProtocol.IsMouse(node.Vid, node.Pid))
                    _output.WriteLine($"TouchSense mouse {node.InstanceId} on {node.Service}: declares the buzz report = {declares}");
                else
                    Assert.False(declares, node.InstanceId);
            }
            // A node that is not there declares nothing.
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(Mouse(0xC030, "HidUsb", "padforge-test-no-such-port")));
            Assert.False(TouchSenseBinding.NodeDeclaresBuzz(default));
            Assert.Null(TouchSenseBinding.ParentOf(@"\\?\hid#vid_046d&pid_c030#padforge-test-no-such-device#{4d1e55b2-f16f-11cf-88cb-001111000030}"));
            Assert.Null(TouchSenseBinding.ParentOf(null));
        }

        // ── One handle per mouse ──

        /// <summary>WinUSB lets one handle open a device, and the mouse's
        /// row holds it. SDL's libusb backend opens a device only after
        /// its routing let the interface through, and with the libusb
        /// whitelist on, which is its default where a platform backend
        /// exists, it lets through only devices that need libusb
        /// (SDL3-build SDL src/hidapi/libusb/hid.c:1164-1168 and
        /// 1249-1258, SDL_hidapi_vendorusb.c:285-290, SDL_hidapi.c:876-884
        /// and 1241-1242). No TouchSense mouse is on that list, so SDL
        /// never opens one as long as nothing here turns the whitelist
        /// off.</summary>
        [Fact]
        public void NothingTurnsSdlsLibusbWhitelistOff()
        {
            foreach (string file in Sources("PadForge.App", "PadForge.Engine"))
                Assert.DoesNotContain("LIBUSB_WHITELIST", File.ReadAllText(file));
        }

        // ── Remote Link ──

        /// <summary>A bound mouse shared over Remote Link is summed
        /// between two frames as a Raw Input mouse is, since its row
        /// publishes the same per-poll counts.</summary>
        [Fact]
        public void ASharedTouchSenseMouse_IsSummedAsAMouse()
        {
            var channel = new FakeTouchSenseChannel(@"\\?\usb#vid_046d&pid_c030#5&2e1a4c7b&0&2#" + InterfaceGuid);
            using var row = new TouchSenseMouseDevice(new TouchSenseMouse(channel.Path, 0x046D, 0xC030), channel, start: false);
            Assert.True(InputService.ReadsAsMouse(row));
            using var mouse = new PadForge.Engine.SdlMouseWrapper();
            Assert.True(InputService.ReadsAsMouse(mouse));
            Assert.False(InputService.ReadsAsMouse(null));

            string service = AuditDelta20261002Tests.RepoText("PadForge.App", "Services", "InputService.cs");
            Assert.Contains("e.acc.Accumulate(s, e.ud.InputStateSeq, ReadsAsMouse(e.source));", service);
            Assert.Contains("e.acc.DrainInto(s, ReadsAsMouse(e.source));", service);
            // No reader of the bridge keys on the wrapper's type alone any more.
            Assert.DoesNotContain("e.source is PadForge.Engine.SdlMouseWrapper", service);
        }
    }
}
